using System.Buffers.Binary;
using System.Numerics;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>MD2 (RFC 1319)。遅く、安全でない。</summary>
public sealed class Md2Hasher() : BlockHasher(16)
{
    /// <summary>円周率の桁から作った置換 (RFC 1319 の PI_SUBST)。</summary>
    internal static ReadOnlySpan<byte> S =>
    [
        41, 46, 67, 201, 162, 216, 124, 1, 61, 54, 84, 161, 236, 240, 6, 19,
        98, 167, 5, 243, 192, 199, 115, 140, 152, 147, 43, 217, 188, 76, 130, 202,
        30, 155, 87, 60, 253, 212, 224, 22, 103, 66, 111, 24, 138, 23, 229, 18,
        190, 78, 196, 214, 218, 158, 222, 73, 160, 251, 245, 142, 187, 47, 238, 122,
        169, 104, 121, 145, 21, 178, 7, 63, 148, 194, 16, 137, 11, 34, 95, 33,
        128, 127, 93, 154, 90, 144, 50, 39, 53, 62, 204, 231, 191, 247, 151, 3,
        255, 25, 48, 179, 72, 165, 181, 209, 215, 94, 146, 42, 172, 86, 170, 198,
        79, 184, 56, 210, 150, 164, 125, 182, 118, 252, 107, 226, 156, 116, 4, 241,
        69, 157, 112, 89, 100, 113, 135, 32, 134, 91, 207, 101, 230, 45, 168, 2,
        27, 96, 37, 173, 174, 176, 185, 246, 28, 70, 97, 105, 52, 64, 126, 15,
        85, 71, 163, 35, 221, 81, 175, 58, 195, 92, 249, 206, 186, 197, 234, 38,
        44, 83, 13, 110, 133, 40, 132, 9, 211, 223, 205, 244, 65, 129, 77, 82,
        106, 220, 55, 200, 108, 193, 171, 250, 36, 225, 123, 8, 12, 189, 177, 74,
        120, 136, 149, 139, 227, 99, 232, 109, 233, 203, 213, 254, 59, 0, 29, 57,
        242, 239, 183, 14, 102, 88, 208, 228, 166, 119, 114, 248, 235, 117, 75, 10,
        49, 68, 80, 180, 143, 237, 31, 26, 219, 153, 141, 51, 159, 17, 131, 20,
    ];

    private readonly byte[] _x = new byte[48];
    private readonly byte[] _checksum = new byte[16];

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Compress(block);
        ReadOnlySpan<byte> s = S;
        byte l = _checksum[15];
        for (int j = 0; j < 16; j++)
        {
            _checksum[j] ^= s[block[j] ^ l];
            l = _checksum[j];
        }
    }

    private void Compress(ReadOnlySpan<byte> block)
    {
        ReadOnlySpan<byte> s = S;
        byte[] x = _x;
        for (int j = 0; j < 16; j++)
        {
            x[16 + j] = block[j];
            x[32 + j] = (byte)(x[16 + j] ^ x[j]);
        }

        int t = 0;
        for (int j = 0; j < 18; j++)
        {
            for (int k = 0; k < 48; k++)
            {
                t = x[k] ^= s[t];
            }

            t = (t + j) & 0xFF;
        }
    }

    protected override byte[] FinishCore()
    {
        int pad = 16 - Pending.Length;
        Span<byte> padding = stackalloc byte[16];
        padding[..pad].Fill((byte)pad);
        Append(padding[..pad]);
        Compress(_checksum);
        return _x[..16];
    }
}

/// <summary>MD4 (RFC 1320)。安全でない。ed2k の部品にも使う。</summary>
public sealed class Md4Hasher() : BlockHasher(64)
{
    private uint _a = 0x67452301, _b = 0xEFCDAB89, _c = 0x98BADCFE, _d = 0x10325476;

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);
        }

        uint a = _a, b = _b, c = _c, d = _d;

        // ラウンド 1
        for (int i = 0; i < 16; i += 4)
        {
            a = BitOperations.RotateLeft(a + ((b & c) | (~b & d)) + x[i], 3);
            d = BitOperations.RotateLeft(d + ((a & b) | (~a & c)) + x[i + 1], 7);
            c = BitOperations.RotateLeft(c + ((d & a) | (~d & b)) + x[i + 2], 11);
            b = BitOperations.RotateLeft(b + ((c & d) | (~c & a)) + x[i + 3], 19);
        }

        // ラウンド 2
        const uint k2 = 0x5A827999;
        for (int i = 0; i < 4; i++)
        {
            a = BitOperations.RotateLeft(a + ((b & c) | (b & d) | (c & d)) + x[i] + k2, 3);
            d = BitOperations.RotateLeft(d + ((a & b) | (a & c) | (b & c)) + x[i + 4] + k2, 5);
            c = BitOperations.RotateLeft(c + ((d & a) | (d & b) | (a & b)) + x[i + 8] + k2, 9);
            b = BitOperations.RotateLeft(b + ((c & d) | (c & a) | (d & a)) + x[i + 12] + k2, 13);
        }

        // ラウンド 3
        const uint k3 = 0x6ED9EBA1;
        ReadOnlySpan<int> order = [0, 2, 1, 3];
        foreach (int i in order)
        {
            a = BitOperations.RotateLeft(a + (b ^ c ^ d) + x[i] + k3, 3);
            d = BitOperations.RotateLeft(d + (a ^ b ^ c) + x[i + 8] + k3, 9);
            c = BitOperations.RotateLeft(c + (d ^ a ^ b) + x[i + 4] + k3, 11);
            b = BitOperations.RotateLeft(b + (c ^ d ^ a) + x[i + 12] + k3, 15);
        }

        _a += a;
        _b += b;
        _c += c;
        _d += d;
    }

    protected override byte[] FinishCore()
    {
        PadMd(0x80, 8, bigEndianLength: false);
        byte[] value = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(value, _a);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(4), _b);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(8), _c);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(12), _d);
        return value;
    }

    /// <summary>1 回だけの MD4 (ed2k の部品の値をまとめるときに使う)。</summary>
    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        var md4 = new Md4Hasher();
        md4.Append(data);
        return md4.Finish();
    }
}
