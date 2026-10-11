using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.Compare;

/// <summary>
/// 差分の一覧の書き出し (ANA-06 の仕様 5・8)。CSV・JSON (共通の結果一覧のエクスポート) と、テキストのレポート (要約と全差分)。
/// どれも UTF-8 (BOM なし)。長時間処理として呼ぶ (件数に比例する時間。メモリは件数に比例しない)。
/// </summary>
public static class DiffExport
{
    /// <summary>左右の先頭から出すバイト数。</summary>
    public const int PreviewBytes = 16;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>差分の種類の機械向けの名前 (CSV・JSON)。</summary>
    public static string KindName(DiffKind kind) => kind switch
    {
        DiffKind.Changed => "changed",
        DiffKind.Inserted => "inserted",
        DiffKind.Deleted => "deleted",
        _ => "unreadable",
    };

    /// <summary>差分の片側の先頭のバイト (最大 16 バイト) の Hex (空白区切り、大文字)。読めないバイトは ??。</summary>
    public static string PreviewHex(CompareRange side, long offset, long length)
    {
        int n = (int)Math.Min(PreviewBytes, Math.Max(0, length));
        if (n == 0)
        {
            return string.Empty;
        }

        Span<byte> buffer = stackalloc byte[PreviewBytes];
        Sources.ReadResult r = side.Data.Read(offset, buffer[..n]);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            long at = offset + i;
            bool bad = i >= r.BytesReturned || r.Unreadable.Any(u => at >= u.Offset && at < u.End);
            sb.Append(bad ? "??" : buffer[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>CSV: 番号 (1 始まり)、種類、左オフセット、左の長さ、右オフセット、右の長さ、左のバイト、右のバイト。数値は 10 進。</summary>
    public static void WriteCsv(CompareResult result, IEnumerable<long> indices, Stream destination, CancellationToken cancellationToken = default,
        Action<long>? progress = null)
    {
        using var writer = new StreamWriter(destination, Utf8, 1 << 16, leaveOpen: true);
        writer.Write("Index,Kind,LeftOffset,LeftLength,RightOffset,RightLength,LeftBytes,RightBytes\r\n");
        long done = 0;
        foreach (long index in indices)
        {
            if ((++done & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done);
            }

            DiffRange d = result.Diffs[index];
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{index + 1},{KindName(d.Kind)},{d.LeftOffset},{d.LeftLength},{d.RightOffset},{d.RightLength},"));
            writer.Write(PreviewHex(result.Left, d.LeftOffset, d.LeftLength));
            writer.Write(',');
            writer.Write(PreviewHex(result.Right, d.RightOffset, d.RightLength));
            writer.Write("\r\n");
        }
    }

    /// <summary>JSON: 差分の配列 (各要素に index、kind、leftOffset、leftLength、rightOffset、rightLength、leftBytes、rightBytes)。</summary>
    public static void WriteJson(CompareResult result, IEnumerable<long> indices, Stream destination, CancellationToken cancellationToken = default,
        Action<long>? progress = null)
    {
        using var json = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        long done = 0;
        foreach (long index in indices)
        {
            if ((++done & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done);
                json.Flush();
            }

            DiffRange d = result.Diffs[index];
            json.WriteStartObject();
            json.WriteNumber("index", index + 1);
            json.WriteString("kind", KindName(d.Kind));
            json.WriteNumber("leftOffset", d.LeftOffset);
            json.WriteNumber("leftLength", d.LeftLength);
            json.WriteNumber("rightOffset", d.RightOffset);
            json.WriteNumber("rightLength", d.RightLength);
            json.WriteString("leftBytes", PreviewHex(result.Left, d.LeftOffset, d.LeftLength));
            json.WriteString("rightBytes", PreviewHex(result.Right, d.RightOffset, d.RightLength));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// テキストのレポート (仕様 8): 要約の行 (表示言語で呼び出し側が作る) と、全差分の一覧 (種類、左オフセット、右オフセット、長さ、
    /// 左右の先頭 16 バイトの Hex)。オフセットは 16 進 (地域設定に依存しない)。
    /// </summary>
    public static void WriteReport(CompareResult result, IEnumerable<string> summary, Func<DiffKind, string> kindLabel, Stream destination,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        using var writer = new StreamWriter(destination, Utf8, 1 << 16, leaveOpen: true);
        foreach (string line in summary)
        {
            writer.Write(line);
            writer.Write("\r\n");
        }

        writer.Write("\r\n");
        long count = result.Diffs.Count;
        for (long i = 0; i < count; i++)
        {
            if ((i & 0x3FFF) == 0x3FFF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(i);
            }

            DiffRange d = result.Diffs[i];
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1}\t{kindLabel(d.Kind)}\t0x{d.LeftOffset:X}\t0x{d.RightOffset:X}\t{d.LeftLength}\t{d.RightLength}\t"));
            writer.Write(PreviewHex(result.Left, d.LeftOffset, d.LeftLength));
            writer.Write('\t');
            writer.Write(PreviewHex(result.Right, d.RightOffset, d.RightLength));
            writer.Write("\r\n");
        }
    }
}
