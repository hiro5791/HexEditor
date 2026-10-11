using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.Intrinsics.X86;

namespace HexEditor.Core.Hashing;

/// <summary>
/// CRC のパラメータ (Greg Cook の「Catalogue of parametrised CRC algorithms」の width, poly, init, refin, refout, xorout, check)。
/// 多項式は最上位ビットを省いた通常表記。
/// </summary>
public sealed record CrcParameters(int Width, ulong Poly, ulong Init, bool RefIn, bool RefOut, ulong XorOut, ulong Check)
{
    /// <summary>幅のマスク。</summary>
    public ulong Mask => Width == 64 ? ulong.MaxValue : (1UL << Width) - 1;
}

/// <summary>
/// パラメータで定めた CRC の計算 (ANA-19 の仕様 2、ANA-20 の仕様 6)。幅 8 以上はテーブル方式 (スライス 8)、幅 8 未満はビット単位。
/// CRC-32C は CPU の CRC32 命令 (SSE4.2) があれば使う。
/// </summary>
public sealed class CrcHasher : IHasher
{
    private static readonly ConcurrentDictionary<CrcParameters, ulong[][]> Tables = new();

    private readonly CrcParameters _p;
    private readonly ulong[][]? _tables;
    private readonly bool _hardwareCrc32C;

    /// <summary>
    /// 計算中のレジスタ。refin なら反転した値を幅のまま右詰めで、そうでなければ 64 bit の上位に左詰めで持つ (幅 8 以上)。
    /// 幅 8 未満は幅のまま右詰め (ビット単位の計算)。
    /// </summary>
    private ulong _crc;

    public CrcHasher(CrcParameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.Width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Width, 64);
        _p = parameters;
        _hardwareCrc32C = parameters.Width == 32 && parameters.Poly == 0x1EDC6F41 && parameters.RefIn && Sse42.X64.IsSupported;
        if (parameters.Width >= 8)
        {
            _tables = Tables.GetOrAdd(parameters, BuildTables);
            _crc = parameters.RefIn ? Reflect(parameters.Init & parameters.Mask, parameters.Width) : (parameters.Init & parameters.Mask) << (64 - parameters.Width);
        }
        else
        {
            _crc = parameters.Init & parameters.Mask;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_tables is null)
        {
            _crc = Bitwise(_p, _crc, data);
            return;
        }

        if (_hardwareCrc32C)
        {
            _crc = HardwareCrc32C((uint)_crc, data);
            return;
        }

        _crc = _p.RefIn ? UpdateReflected(_tables, _crc, data) : UpdateNormal(_tables, _crc, data);
    }

    public byte[] Finish()
    {
        ulong value;
        if (_tables is null)
        {
            value = _p.RefOut ? Reflect(_crc, _p.Width) : _crc;
        }
        else if (_p.RefIn)
        {
            // レジスタは反転した値。refout でなければ戻す。
            value = _p.RefOut ? _crc : Reflect(_crc, _p.Width);
        }
        else
        {
            ulong normal = _crc >> (64 - _p.Width);
            value = _p.RefOut ? Reflect(normal, _p.Width) : normal;
        }

        return HashBytes.FromNumber((value ^ _p.XorOut) & _p.Mask, (_p.Width + 7) / 8);
    }

    /// <summary>カタログの定義どおりのビット単位の計算 (基準の実装。幅 8 未満の CRC もこれで求める)。</summary>
    /// <param name="register">refin に関係なく、通常の (反転しない) 向きのレジスタ。</param>
    internal static ulong Bitwise(CrcParameters p, ulong register, ReadOnlySpan<byte> data)
    {
        ulong top = 1UL << (p.Width - 1);
        foreach (byte value in data)
        {
            byte b = p.RefIn ? (byte)Reflect(value, 8) : value;
            for (int bit = 7; bit >= 0; bit--)
            {
                bool feedback = ((register & top) != 0) ^ (((b >> bit) & 1) != 0);
                register = (register << 1) & p.Mask;
                if (feedback)
                {
                    register ^= p.Poly;
                }
            }
        }

        return register & p.Mask;
    }

    /// <summary>カタログの定義どおりに、データ全体の CRC を求める (テストの基準の実装)。</summary>
    public static ulong Reference(CrcParameters p, ReadOnlySpan<byte> data)
    {
        ulong register = Bitwise(p, p.Init & p.Mask, data);
        if (p.RefOut)
        {
            register = Reflect(register, p.Width);
        }

        return (register ^ p.XorOut) & p.Mask;
    }

    /// <summary>カタログの check (ASCII の <c>123456789</c> の CRC) をパラメータから求める。</summary>
    public static ulong ComputeCheck(CrcParameters p) => Reference(p, "123456789"u8);

    /// <summary>
    /// カタログの residue (誤りのない符号語を読んだ後のレジスタ。最終 XOR はかけない) をパラメータから求める。カタログの定義の
    /// 「レジスタを xorout で初期化し、refout なら反転し、幅と同じ数の 0 のビットを読み、refin なら反転した値」で計算する。
    /// </summary>
    public static ulong ComputeResidue(CrcParameters p)
    {
        ulong register = p.XorOut & p.Mask;
        if (p.RefOut)
        {
            register = Reflect(register, p.Width);
        }

        ulong top = 1UL << (p.Width - 1);
        for (int i = 0; i < p.Width; i++)
        {
            bool feedback = (register & top) != 0;
            register = (register << 1) & p.Mask;
            if (feedback)
            {
                register ^= p.Poly & p.Mask;
            }
        }

        return p.RefIn ? Reflect(register, p.Width) : register;
    }

    /// <summary>下位 <paramref name="width"/> ビットの順序を反転する。</summary>
    public static ulong Reflect(ulong value, int width)
    {
        ulong r = 0;
        for (int i = 0; i < width; i++)
        {
            r = (r << 1) | ((value >> i) & 1);
        }

        return r;
    }

    private static ulong[][] BuildTables(CrcParameters p)
    {
        var t = new ulong[8][];
        for (int k = 0; k < 8; k++)
        {
            t[k] = new ulong[256];
        }

        if (p.RefIn)
        {
            ulong poly = Reflect(p.Poly, p.Width);
            for (int i = 0; i < 256; i++)
            {
                ulong c = (ulong)i;
                for (int bit = 0; bit < 8; bit++)
                {
                    c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
                }

                t[0][i] = c;
            }

            for (int k = 1; k < 8; k++)
            {
                for (int i = 0; i < 256; i++)
                {
                    ulong prev = t[k - 1][i];
                    t[k][i] = (prev >> 8) ^ t[0][prev & 0xFF];
                }
            }
        }
        else
        {
            ulong poly = p.Poly << (64 - p.Width);
            for (int i = 0; i < 256; i++)
            {
                ulong c = (ulong)i << 56;
                for (int bit = 0; bit < 8; bit++)
                {
                    c = (c & (1UL << 63)) != 0 ? (c << 1) ^ poly : c << 1;
                }

                t[0][i] = c;
            }

            for (int k = 1; k < 8; k++)
            {
                for (int i = 0; i < 256; i++)
                {
                    ulong prev = t[k - 1][i];
                    t[k][i] = (prev << 8) ^ t[0][prev >> 56];
                }
            }
        }

        return t;
    }

    private static ulong UpdateReflected(ulong[][] t, ulong crc, ReadOnlySpan<byte> data)
    {
        ulong[] t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3], t4 = t[4], t5 = t[5], t6 = t[6], t7 = t[7];
        while (data.Length >= 8)
        {
            crc ^= BinaryPrimitives.ReadUInt64LittleEndian(data);
            crc = t7[crc & 0xFF] ^ t6[(crc >> 8) & 0xFF] ^ t5[(crc >> 16) & 0xFF] ^ t4[(crc >> 24) & 0xFF]
                ^ t3[(crc >> 32) & 0xFF] ^ t2[(crc >> 40) & 0xFF] ^ t1[(crc >> 48) & 0xFF] ^ t0[crc >> 56];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ t0[(crc ^ b) & 0xFF];
        }

        return crc;
    }

    private static ulong UpdateNormal(ulong[][] t, ulong crc, ReadOnlySpan<byte> data)
    {
        ulong[] t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3], t4 = t[4], t5 = t[5], t6 = t[6], t7 = t[7];
        while (data.Length >= 8)
        {
            crc ^= BinaryPrimitives.ReadUInt64BigEndian(data);
            crc = t7[crc >> 56] ^ t6[(crc >> 48) & 0xFF] ^ t5[(crc >> 40) & 0xFF] ^ t4[(crc >> 32) & 0xFF]
                ^ t3[(crc >> 24) & 0xFF] ^ t2[(crc >> 16) & 0xFF] ^ t1[(crc >> 8) & 0xFF] ^ t0[crc & 0xFF];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = (crc << 8) ^ t0[(crc >> 56) ^ b];
        }

        return crc;
    }

    private static ulong HardwareCrc32C(uint crc, ReadOnlySpan<byte> data)
    {
        ulong c = crc;
        while (data.Length >= 8)
        {
            c = Sse42.X64.Crc32(c, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        uint c32 = (uint)c;
        foreach (byte b in data)
        {
            c32 = Sse42.Crc32(c32, b);
        }

        return c32;
    }
}

/// <summary>
/// POSIX の <c>cksum</c> と同じ計算 (ANA-19 の仕様 2 の「cksum (長さ付き)」)。CRC-32/CKSUM で、データの後にデータ長を
/// 下位バイトから順に、0 でない上位バイトがなくなるまで加えて計算する (空のデータは長さのバイトを加えない)。
/// </summary>
public sealed class CksumHasher : IHasher
{
    /// <summary>CRC-32/CKSUM のパラメータ。</summary>
    public static readonly CrcParameters Crc32Cksum = new(32, 0x04C11DB7, 0, false, false, 0xFFFFFFFF, 0x765E7680);

    private readonly CrcHasher _crc = new(Crc32Cksum);
    private ulong _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        _crc.Append(data);
        _length += (ulong)data.Length;
    }

    public byte[] Finish()
    {
        Span<byte> lengthBytes = stackalloc byte[8];
        int n = 0;
        for (ulong l = _length; l != 0; l >>= 8)
        {
            lengthBytes[n++] = (byte)l;
        }

        _crc.Append(lengthBytes[..n]);
        return _crc.Finish();
    }
}
