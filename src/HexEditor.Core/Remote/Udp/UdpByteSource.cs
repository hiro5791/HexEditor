using HexEditor.Core.Sources;

namespace HexEditor.Core.Remote.Udp;

/// <summary>
/// UDP の受信のドキュメントのデータソース (ENG-42 の仕様 2〜4)。読み取り専用で、長さは受信に応じて増える。オフセット 0 は保持している
/// 先頭 (= 捨てたバイト数の位置)、<see cref="BaseAddress"/> は捨てたバイト数で、表示上のアドレスは受信開始からの通算の位置になる。
/// 長さとベースアドレスは <see cref="Publish"/> で UI のスレッドから進める (ドキュメントの長さと食い違わないように)。
/// </summary>
public sealed class UdpByteSource : ByteSourceBase
{
    private readonly string _identity = $"udp:{Guid.NewGuid():N}";
    private long _length;
    private long _base;

    public UdpByteSource(UdpCapture capture, string displayName)
    {
        Capture = capture;
        DisplayName = displayName;
        Publish();
    }

    public UdpCapture Capture { get; }

    public override string DisplayName { get; }

    public override string Identity => _identity;

    public override long Length => Interlocked.Read(ref _length);

    public override long BaseAddress => Interlocked.Read(ref _base);

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    /// <summary>
    /// 受信した分を反映する。戻り値: 先頭を捨てた (ベースアドレスが変わった) なら true (ドキュメントは元データを差し替える)。
    /// 伸びただけなら false (ドキュメントは末尾を加える)。
    /// </summary>
    public bool Publish()
    {
        AppendStore store = Capture.Store;
        long start = store.Start;
        long end = store.End;
        bool shifted = start != Interlocked.Read(ref _base);
        Interlocked.Exchange(ref _base, start);
        Interlocked.Exchange(ref _length, end - start);
        return shifted;
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        // 反映した後に先頭が捨てられた範囲は読めない (次の反映で差し替わる)。
        int got = Capture.Store.Read(BaseAddress + offset, buffer[..length], out int skipped);
        if (skipped > 0 || got < length - skipped)
        {
            var bad = new List<UnreadableRange>();
            if (skipped > 0)
            {
                bad.Add(new UnreadableRange(offset, skipped, UnreadableReason.Unallocated));
            }

            if (got < length - skipped)
            {
                bad.Add(new UnreadableRange(offset + skipped + got, length - skipped - got, UnreadableReason.Unallocated));
            }

            return new ReadResult(length, bad);
        }

        return new ReadResult(length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Capture.Dispose();
        }
    }
}

/// <summary>パケットの一覧のエクスポート (ENG-42 の仕様 6。結果一覧のエクスポートと同じ CSV / JSON)。</summary>
public static class UdpPacketExport
{
    /// <summary>CSV (見出し: 番号, 位置, 長さ, 受信時刻 (UTC, ISO 8601), 送信元)。</summary>
    public static string ToCsv(IEnumerable<UdpPacket> packets)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("number,position,length,received,source\r\n");
        foreach (UdpPacket p in packets)
        {
            sb.Append(p.Number).Append(',')
                .Append("0x").Append(p.Position.ToString("X", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Length).Append(',')
                .Append(p.ReceivedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Source).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>JSON (オブジェクトの配列)。</summary>
    public static string ToJson(IEnumerable<UdpPacket> packets) =>
        System.Text.Json.JsonSerializer.Serialize(packets.Select(p => new
        {
            number = p.Number,
            position = p.Position,
            length = p.Length,
            received = p.ReceivedAt.UtcDateTime,
            source = p.Source.ToString(),
        }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
