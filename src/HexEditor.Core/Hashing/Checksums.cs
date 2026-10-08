using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;

namespace HexEditor.Core.Hashing;

/// <summary>値のバイト列の組み立て。</summary>
public static class HashBytes
{
    /// <summary>数値を <paramref name="bytes"/> バイトのビッグエンディアンのバイト列にする。</summary>
    public static byte[] FromNumber(ulong value, int bytes)
    {
        byte[] result = new byte[bytes];
        for (int i = bytes - 1; i >= 0; i--)
        {
            result[i] = (byte)value;
            value >>= 8;
        }

        return result;
    }

    /// <summary>ビッグエンディアンのバイト列を数値にする (8 バイト以下)。</summary>
    public static ulong ToNumber(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }
}

/// <summary>
/// 加算 8 / 16 / 32 bit (バイト単位) のチェックサム (ANA-19 の仕様 1)。各バイトを符号なしで足し、幅で切り捨てる。
/// </summary>
public sealed class ByteSumHasher(int bits, HashComplement complement) : IHasher
{
    private ulong _sum;

    public void Append(ReadOnlySpan<byte> data) => _sum = unchecked(_sum + Sum(data));

    public byte[] Finish()
    {
        ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        ulong value = complement switch
        {
            HashComplement.Ones => ~_sum,
            HashComplement.Twos => unchecked(0 - _sum),
            _ => _sum,
        };
        return HashBytes.FromNumber(value & mask, bits / 8);
    }

    /// <summary>バイトの合計 (2^64 で切り捨て)。SSE2 の SAD で 16 バイトずつ足す。</summary>
    internal static ulong Sum(ReadOnlySpan<byte> data)
    {
        ulong total = 0;
        int i = 0;
        if (Sse2.IsSupported && data.Length >= 16)
        {
            Vector128<ulong> acc = Vector128<ulong>.Zero;
            for (; i + 16 <= data.Length; i += 16)
            {
                Vector128<byte> v = Vector128.Create(data.Slice(i, 16));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(v, Vector128<byte>.Zero).AsUInt64());
            }

            total = acc.GetElement(0) + acc.GetElement(1);
        }

        for (; i < data.Length; i++)
        {
            total += data[i];
        }

        return total;
    }
}

/// <summary>XOR 8 bit (バイト単位) のチェックサム (ANA-19 の仕様 1)。</summary>
public sealed class ByteXorHasher : IHasher
{
    private byte _value;

    public void Append(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && data.Length >= Vector<byte>.Count)
        {
            Vector<byte> acc = Vector<byte>.Zero;
            for (; i + Vector<byte>.Count <= data.Length; i += Vector<byte>.Count)
            {
                acc ^= new Vector<byte>(data.Slice(i, Vector<byte>.Count));
            }

            for (int k = 0; k < Vector<byte>.Count; k++)
            {
                _value ^= acc[k];
            }
        }

        for (; i < data.Length; i++)
        {
            _value ^= data[i];
        }
    }

    public byte[] Finish() => [_value];
}

/// <summary>Adler-32 (RFC 1950)。</summary>
public sealed class Adler32Hasher : IHasher
{
    private const uint Mod = 65521;

    /// <summary>剰余を取らずに足せる最大のバイト数 (zlib の NMAX)。</summary>
    private const int Nmax = 5552;

    private uint _a = 1;
    private uint _b;

    public void Append(ReadOnlySpan<byte> data)
    {
        uint a = _a, b = _b;
        while (data.Length > 0)
        {
            int n = Math.Min(Nmax, data.Length);
            foreach (byte x in data[..n])
            {
                a += x;
                b += a;
            }

            a %= Mod;
            b %= Mod;
            data = data[n..];
        }

        _a = a;
        _b = b;
    }

    public byte[] Finish() => HashBytes.FromNumber(((ulong)_b << 16) | _a, 4);
}

/// <summary>.NET 標準 (System.Security.Cryptography) の暗号学的ハッシュ (ANA-19 の仕様 5)。</summary>
public sealed class IncrementalHasher(HashAlgorithmName name) : IHasher
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);

    public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

    public byte[] Finish()
    {
        byte[] value = _hash.GetHashAndReset();
        _hash.Dispose();
        return value;
    }
}

/// <summary>xxHash32 (xxHash の公式の定義)。</summary>
public sealed class XxHash32Hasher : IHasher
{
    private const uint P1 = 2654435761U, P2 = 2246822519U, P3 = 3266489917U, P4 = 668265263U, P5 = 374761393U;

    private readonly uint _seed;
    private readonly byte[] _buffer = new byte[16];
    private uint _v1, _v2, _v3, _v4;
    private int _buffered;
    private ulong _total;

    public XxHash32Hasher(ulong seed)
    {
        _seed = (uint)seed;
        _v1 = unchecked(_seed + P1 + P2);
        _v2 = unchecked(_seed + P2);
        _v3 = _seed;
        _v4 = unchecked(_seed - P1);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_buffered > 0)
        {
            int n = Math.Min(16 - _buffered, data.Length);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
            if (_buffered < 16)
            {
                return;
            }

            Stripe(_buffer);
            _buffered = 0;
        }

        while (data.Length >= 16)
        {
            Stripe(data);
            data = data[16..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    public byte[] Finish()
    {
        uint h = _total >= 16
            ? BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7) + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18)
            : unchecked(_seed + P5);
        h = unchecked(h + (uint)_total);
        ReadOnlySpan<byte> rest = _buffer.AsSpan(0, _buffered);
        while (rest.Length >= 4)
        {
            h = unchecked(BitOperations.RotateLeft(h + BinaryPrimitives.ReadUInt32LittleEndian(rest) * P3, 17) * P4);
            rest = rest[4..];
        }

        foreach (byte b in rest)
        {
            h = unchecked(BitOperations.RotateLeft(h + b * P5, 11) * P1);
        }

        h ^= h >> 15;
        h = unchecked(h * P2);
        h ^= h >> 13;
        h = unchecked(h * P3);
        h ^= h >> 16;
        return HashBytes.FromNumber(h, 4);
    }

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt32LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt32LittleEndian(s[4..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt32LittleEndian(s[8..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt32LittleEndian(s[12..]));
    }

    private static uint Round(uint acc, uint input) => unchecked(BitOperations.RotateLeft(acc + input * P2, 13) * P1);
}

/// <summary>xxHash64 (xxHash の公式の定義)。</summary>
public sealed class XxHash64Hasher : IHasher
{
    private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
        P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    private readonly ulong _seed;
    private readonly byte[] _buffer = new byte[32];
    private ulong _v1, _v2, _v3, _v4;
    private int _buffered;
    private ulong _total;

    public XxHash64Hasher(ulong seed)
    {
        _seed = seed;
        _v1 = unchecked(seed + P1 + P2);
        _v2 = unchecked(seed + P2);
        _v3 = seed;
        _v4 = unchecked(seed - P1);
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_buffered > 0)
        {
            int n = Math.Min(32 - _buffered, data.Length);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
            if (_buffered < 32)
            {
                return;
            }

            Stripe(_buffer);
            _buffered = 0;
        }

        while (data.Length >= 32)
        {
            Stripe(data);
            data = data[32..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    public byte[] Finish()
    {
        ulong h;
        if (_total >= 32)
        {
            h = unchecked(BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7) + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18));
            h = Merge(h, _v1);
            h = Merge(h, _v2);
            h = Merge(h, _v3);
            h = Merge(h, _v4);
        }
        else
        {
            h = unchecked(_seed + P5);
        }

        h = unchecked(h + _total);
        ReadOnlySpan<byte> rest = _buffer.AsSpan(0, _buffered);
        while (rest.Length >= 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rest));
            h = unchecked(BitOperations.RotateLeft(h, 27) * P1 + P4);
            rest = rest[8..];
        }

        if (rest.Length >= 4)
        {
            h ^= unchecked(BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1);
            h = unchecked(BitOperations.RotateLeft(h, 23) * P2 + P3);
            rest = rest[4..];
        }

        foreach (byte b in rest)
        {
            h ^= unchecked(b * P5);
            h = unchecked(BitOperations.RotateLeft(h, 11) * P1);
        }

        h ^= h >> 33;
        h = unchecked(h * P2);
        h ^= h >> 29;
        h = unchecked(h * P3);
        h ^= h >> 32;
        return HashBytes.FromNumber(h, 8);
    }

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }

    private static ulong Round(ulong acc, ulong input) => unchecked(BitOperations.RotateLeft(acc + input * P2, 31) * P1);

    private static ulong Merge(ulong h, ulong v) => unchecked((h ^ Round(0, v)) * P1 + P4);
}
