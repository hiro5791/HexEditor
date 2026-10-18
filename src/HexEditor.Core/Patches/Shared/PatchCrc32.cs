namespace HexEditor.Core.Patches;

// このファイルは自己適用型パッチの適用プログラム (src/HexEditor.PatchApplier) でも使う。HexEditor.Core の他の型に依存しないこと。

/// <summary>UPS・BPS の CRC-32 (ISO-HDLC。多項式 0xEDB88320 の反転形、初期値・最終 XOR は 0xFFFFFFFF)。</summary>
public static class PatchCrc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>計算の途中の値 (最初は <see cref="Initial"/>)。</summary>
    public const uint Initial = 0xFFFFFFFFu;

    /// <summary>途中の値に <paramref name="data"/> を加える。</summary>
    public static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            state = Table[(state ^ b) & 0xFF] ^ (state >> 8);
        }

        return state;
    }

    /// <summary>途中の値から CRC-32 の値を得る。</summary>
    public static uint Finish(uint state) => state ^ 0xFFFFFFFFu;

    /// <summary>バイト列の CRC-32。</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Append(Initial, data));

    /// <summary>ストリームの現在位置から終わりまでの CRC-32。</summary>
    public static uint Compute(Stream stream, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[1 << 20];
        uint state = Initial;
        long done = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = Append(state, buffer.AsSpan(0, n));
            done += n;
            progress?.Invoke(done);
        }

        return Finish(state);
    }
}
