using System.Buffers.Binary;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// テスト用イメージの生成ツールが使うバイト列の書き込みとチェックサム (ext4 の CRC32C、UDF の CRC-CCITT、APFS の Fletcher-64)。
/// アプリの解析の実装とは別に書いたもの (生成ツールはアプリのコードを使わない)。
/// </summary>
public static class ImageBytes
{
    private static readonly uint[] Crc32cTable = Table32(0x82F63B78u);
    private static readonly uint[] Crc32Table = Table32(0xEDB88320u);

    private static uint[] Table32(uint poly)
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    /// <summary>反転しない CRC32C (Linux の crc32c_le と同じ)。</summary>
    public static uint Crc32c(uint seed, ReadOnlySpan<byte> data)
    {
        uint c = seed;
        for (int i = 0; i < data.Length; i++)
        {
            c = Crc32cTable[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    public static uint Crc32c(uint seed, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return Crc32c(seed, b);
    }

    /// <summary>反転しない CRC32 (LVM2 の calc_crc。初期値は 0xF597A6CF)。</summary>
    public static uint Crc32Raw(uint seed, ReadOnlySpan<byte> data)
    {
        uint c = seed;
        for (int i = 0; i < data.Length; i++)
        {
            c = Crc32Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    /// <summary>CRC-CCITT (多項式 0x1021、初期値 0)。ビット単位で計算する。</summary>
    public static ushort CrcCcitt(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
                crc &= 0xFFFF;
            }
        }

        return (ushort)crc;
    }

    /// <summary>APFS のオブジェクトのチェックサムを先頭の 8 バイトに書く。</summary>
    public static void SetFletcher64(Span<byte> block)
    {
        ulong sum1 = 0, sum2 = 0;
        const ulong M = 0xFFFFFFFF;
        for (int i = 8; i < block.Length; i += 4)
        {
            sum1 = (sum1 + BinaryPrimitives.ReadUInt32LittleEndian(block[i..])) % M;
            sum2 = (sum2 + sum1) % M;
        }

        ulong c1 = M - ((sum1 + sum2) % M);
        ulong c2 = M - ((sum1 + c1) % M);
        BinaryPrimitives.WriteUInt64LittleEndian(block, (c2 << 32) | c1);
    }

    public static void U16(Span<byte> b, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b[at..], (ushort)v);
    public static void U32(Span<byte> b, int at, long v) => BinaryPrimitives.WriteUInt32LittleEndian(b[at..], (uint)v);
    public static void U64(Span<byte> b, int at, long v) => BinaryPrimitives.WriteUInt64LittleEndian(b[at..], (ulong)v);
    public static void B16(Span<byte> b, int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(b[at..], (ushort)v);
    public static void B32(Span<byte> b, int at, long v) => BinaryPrimitives.WriteUInt32BigEndian(b[at..], (uint)v);
    public static void B64(Span<byte> b, int at, long v) => BinaryPrimitives.WriteUInt64BigEndian(b[at..], (ulong)v);

    /// <summary>ISO 9660 の両エンディアン (リトル + ビッグ)。</summary>
    public static void Both16(Span<byte> b, int at, int v)
    {
        U16(b, at, v);
        B16(b, at + 2, v);
    }

    public static void Both32(Span<byte> b, int at, long v)
    {
        U32(b, at, v);
        B32(b, at + 4, v);
    }

    public static void Ascii(Span<byte> b, int at, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            b[at + i] = (byte)s[i];
        }
    }

    public static long Unix(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;
}
