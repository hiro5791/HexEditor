using System.Buffers.Binary;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ファイルシステムのメタデータのチェックサム (ext4 の metadata_csum の CRC32C、UDF の記述子の CRC-CCITT、APFS の Fletcher-64)。
/// 入力の長さに比例する時間だけを使い、例外を出さない。
/// </summary>
public static class FsChecksums
{
    private static readonly uint[] Crc32cTable = BuildCrc32c();
    private static readonly ushort[] CcittTable = BuildCcitt();

    private static uint[] BuildCrc32c()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    private static ushort[] BuildCcitt()
    {
        ushort[] t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1;
            }

            t[i] = (ushort)c;
        }

        return t;
    }

    /// <summary>
    /// CRC32C の途中の値 (Linux の <c>crc32c_le</c> と同じ。最初と最後の反転をしない)。ext4 の <c>ext4_chksum</c> はこれ
    /// (<paramref name="seed"/> は最初 0xFFFFFFFF)。
    /// </summary>
    public static uint Crc32cRaw(uint seed, ReadOnlySpan<byte> data)
    {
        uint c = seed;
        foreach (byte b in data)
        {
            c = Crc32cTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return c;
    }

    /// <summary>32 ビットの値 (リトルエンディアン) を続けて計算する。</summary>
    public static uint Crc32cRaw(uint seed, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return Crc32cRaw(seed, b);
    }

    /// <summary>CRC-CCITT (多項式 0x1021、初期値 0、反転なし。UDF の DescriptorCRC)。</summary>
    public static ushort CrcCcitt(ReadOnlySpan<byte> data)
    {
        int c = 0;
        foreach (byte b in data)
        {
            c = CcittTable[((c >> 8) ^ b) & 0xFF] ^ (c << 8);
        }

        return (ushort)c;
    }

    /// <summary>APFS のオブジェクトのチェックサム (先頭 8 バイトを除いた 32 ビット語の Fletcher-64)。</summary>
    public static ulong Fletcher64(ReadOnlySpan<byte> block)
    {
        const ulong Mod = 0xFFFFFFFF;
        ulong sum1 = 0, sum2 = 0;
        for (int i = 8; i + 4 <= block.Length; i += 4)
        {
            sum1 = (sum1 + BinaryPrimitives.ReadUInt32LittleEndian(block[i..])) % Mod;
            sum2 = (sum2 + sum1) % Mod;
        }

        ulong c1 = Mod - ((sum1 + sum2) % Mod);
        ulong c2 = Mod - ((sum1 + c1) % Mod);
        return (c2 << 32) | c1;
    }

    /// <summary>APFS のオブジェクトのチェックサムが正しいか (全部が 0 のブロックは正しくないとする)。</summary>
    public static bool ApfsObjectValid(ReadOnlySpan<byte> block) =>
        block.Length >= 32 && block.IndexOfAnyExcept((byte)0) >= 0 && Fletcher64(block) == BinaryPrimitives.ReadUInt64LittleEndian(block);
}
