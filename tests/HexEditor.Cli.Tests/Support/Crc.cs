namespace HexEditor.Cli.Tests.Support;

/// <summary>期待値用の CRC-32 (IEEE 802.3、テスト対象とは別に書いたビット単位の計算)。</summary>
internal static class Crc
{
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }
}
