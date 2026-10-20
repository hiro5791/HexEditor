namespace HexEditor.Core.Forensics.Images;

/// <summary>イメージ形式の検査値 (E01 の Adler-32、GPT の CRC-32、VHDX の CRC-32C)。</summary>
public static class ImageChecksums
{
    private static readonly uint[] Crc32Table = Table(0xEDB88320);
    private static readonly uint[] Crc32CTable = Table(0x82F63B78);

    /// <summary>Adler-32 (RFC 1950)。<paramref name="seed"/> は続きから計算するときの前回の値。</summary>
    public static uint Adler32(ReadOnlySpan<byte> data, uint seed = 1)
    {
        const uint Mod = 65521;
        uint a = seed & 0xFFFF;
        uint b = seed >> 16;
        while (data.Length > 0)
        {
            // 5552 バイトごとに剰余を取る (32 ビットで桁あふれしない最大の長さ)。
            int n = Math.Min(data.Length, 5552);
            foreach (byte value in data[..n])
            {
                a += value;
                b += a;
            }

            a %= Mod;
            b %= Mod;
            data = data[n..];
        }

        return (b << 16) | a;
    }

    /// <summary>CRC-32 (IEEE 802.3。GPT・zip と同じ)。</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0) => Compute(Crc32Table, data, crc);

    /// <summary>CRC-32C (Castagnoli。VHDX のヘッダー・領域表)。</summary>
    public static uint Crc32C(ReadOnlySpan<byte> data, uint crc = 0) => Compute(Crc32CTable, data, crc);

    private static uint Compute(uint[] table, ReadOnlySpan<byte> data, uint crc)
    {
        crc = ~crc;
        foreach (byte value in data)
        {
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Table(uint poly)
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
