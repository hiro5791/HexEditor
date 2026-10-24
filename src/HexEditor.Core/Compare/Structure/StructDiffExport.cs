using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.Compare.Structure;

/// <summary>
/// 構造の差分の書き出し (ANA-32 の仕様 7): JSON・CSV・テキストのレポート。ストリームに順に書き、件数に比例するメモリを使わない。
/// 長時間処理として呼ぶ (一致した行の値を評価の結果から求めるため、UI スレッドでは呼ばない)。
/// </summary>
public static class StructDiffExport
{
    /// <summary>CSV の見出しの行。</summary>
    public const string CsvHeader = "kind,path,leftPath,rightPath,type,leftOffset,leftSize,leftValue,rightOffset,rightSize,rightValue,match";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>種類の、表示言語によらない英語の語 (JSON・CSV の <c>kind</c>)。</summary>
    public static string KindName(StructDiffKind kind) => kind switch
    {
        StructDiffKind.Changed => "changed",
        StructDiffKind.Added => "added",
        StructDiffKind.Removed => "removed",
        StructDiffKind.Moved => "moved",
        StructDiffKind.MovedChanged => "movedChanged",
        StructDiffKind.TypeChanged => "typeChanged",
        StructDiffKind.Unparsed => "unparsed",
        StructDiffKind.Same => "same",
        _ => "none",
    };

    /// <summary>要約の状態の語 (JSON)。</summary>
    public static string StateName(StructCompareState state) => state switch
    {
        StructCompareState.Completed => "completed",
        StructCompareState.Cancelled => "cancelled",
        StructCompareState.FieldLimit => "fieldLimit",
        StructCompareState.Running => "running",
        StructCompareState.NoTemplate => "noTemplate",
        StructCompareState.SyntaxError => "syntaxError",
        StructCompareState.TemplateMissing => "templateMissing",
        StructCompareState.Unsupported => "unsupported",
        _ => "failed",
    };

    /// <summary>行の <c>match</c> (配列の行と、配列の要素の行: 親の配列の対応付け)。</summary>
    public static string MatchOf(StructDiffNode node) =>
        node.Array?.MatchText ?? (node.Parent?.Array is { } parent && (node.LeftIndex >= 0 || node.RightIndex >= 0) ? parent.MatchText : string.Empty);

    /// <summary>JSON に書き出す (<paramref name="hexOffsets"/> なら、オフセットとサイズを 16 進の文字列にする。TPL-25 の仕様 4)。</summary>
    public static void WriteJson(StructCompareResult result, IEnumerable<StructDiffNode> rows, Stream destination, bool hexOffsets = false,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        using var w = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("template", result.TemplateName);
        w.WriteString("templatePath", result.Choice.TemplatePath);
        WriteSide(w, "left", result.Left, result.LeftResult, hexOffsets);
        WriteSide(w, "right", result.Right, result.RightResult, hexOffsets);
        w.WriteStartObject("summary");
        w.WriteNumber("leftFields", result.LeftFields);
        w.WriteNumber("rightFields", result.RightFields);
        w.WriteStartObject("counts");
        foreach (StructDiffKind k in DiffKinds)
        {
            w.WriteNumber(KindName(k), result.CountOf(k));
        }

        w.WriteEndObject();
        w.WriteStartObject("outside");
        w.WriteNumber("count", result.Outside?.Count ?? 0);
        w.WriteNumber("bytes", result.Outside?.Bytes ?? 0);
        w.WriteEndObject();
        w.WriteStartObject("errors");
        w.WriteNumber("left", result.LeftErrors);
        w.WriteNumber("right", result.RightErrors);
        w.WriteEndObject();
        w.WriteStartObject("warnings");
        w.WriteNumber("left", result.LeftWarnings);
        w.WriteNumber("right", result.RightWarnings);
        w.WriteEndObject();
        w.WriteNumber("elapsedSeconds", Math.Round(result.Elapsed.TotalSeconds, 3));
        w.WriteString("state", StateName(result.State));
        w.WriteEndObject();
        w.WriteStartArray("diffs");
        long done = 0;
        foreach (StructDiffNode n in rows)
        {
            if ((++done & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done);
                w.Flush();
            }

            w.WriteStartObject();
            w.WriteString("kind", KindName(n.Kind));
            w.WriteString("path", n.DisplayPath);
            WriteNullableString(w, "leftPath", n.HasLeft || n.Kind == StructDiffKind.Unparsed ? n.LeftPath : null);
            WriteNullableString(w, "rightPath", n.HasRight || n.Kind == StructDiffKind.Unparsed ? n.RightPath : null);
            w.WriteString("type", n.TypeName);
            WriteValueSide(w, "left", result, n, right: false, hexOffsets);
            WriteValueSide(w, "right", result, n, right: true, hexOffsets);
            string match = MatchOf(n);
            WriteNullableString(w, "match", match.Length == 0 ? null : match);
            w.WriteBoolean("padding", n.IsPadding);
            w.WriteBoolean("outsideRange", n.OutsideRange);
            if (n.Kind == StructDiffKind.Unparsed)
            {
                WriteNullableString(w, "reason", n.Reason switch
                {
                    StructUnparsedReason.Limit => "limit",
                    StructUnparsedReason.TooDeep => "tooDeep",
                    _ => n.Diagnostic?.Code,
                });
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static readonly StructDiffKind[] DiffKinds =
    [
        StructDiffKind.Changed, StructDiffKind.Added, StructDiffKind.Removed, StructDiffKind.Moved, StructDiffKind.MovedChanged,
        StructDiffKind.TypeChanged, StructDiffKind.Unparsed,
    ];

    private static void WriteSide(Utf8JsonWriter w, string name, StructCompareSideInfo side, Templates.TemplateResult? evaluation, bool hex)
    {
        w.WriteStartObject(name);
        w.WriteString("name", side.Name);
        WriteNullableString(w, "path", side.Path);
        WriteOffset(w, "start", side.Start, hex);
        WriteNullableString(w, "evaluation", evaluation?.State.ToString().ToLowerInvariant());
        w.WriteEndObject();
    }

    private static void WriteValueSide(Utf8JsonWriter w, string name, StructCompareResult result, StructDiffNode n, bool right, bool hex)
    {
        bool present = right ? n.HasRight : n.HasLeft;
        bool insertion = !present && n.Kind is StructDiffKind.Added or StructDiffKind.Removed;
        if (!present && !insertion)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        WriteOffset(w, "offset", right ? n.RightOffset : n.LeftOffset, hex);
        WriteOffset(w, "size", present ? (right ? n.RightSize : n.LeftSize) : 0, hex);
        if (present)
        {
            WriteValue(w, result.ValueOf(n, right));
            w.WriteString("display", result.DisplayOf(n, right));
        }
        else
        {
            w.WriteNull("value");
            w.WriteNull("display");
        }

        w.WriteEndObject();
    }

    private static void WriteOffset(Utf8JsonWriter w, string name, long value, bool hex)
    {
        if (hex)
        {
            w.WriteString(name, "0x" + value.ToString("X", CultureInfo.InvariantCulture));
        }
        else
        {
            w.WriteNumber(name, value);
        }
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    /// <summary>値: 数値は数値、64 bit を超える整数とバイト列は文字列 (TPL-25 の仕様 2)。</summary>
    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNull("value");
                break;
            case long l:
                w.WriteNumber("value", l);
                break;
            case ulong u:
                w.WriteNumber("value", u);
                break;
            case double d when double.IsFinite(d):
                w.WriteNumber("value", d);
                break;
            case double d:
                w.WriteString("value", double.IsNaN(d) ? "NaN" : d > 0 ? "Inf" : "-Inf");
                break;
            case bool b:
                w.WriteBoolean("value", b);
                break;
            case string s:
                w.WriteString("value", s);
                break;
            case byte[] bytes:
                w.WriteString("value", Convert.ToHexString(bytes));
                break;
            case DateTime dt:
                w.WriteString("value", dt.ToString("o", CultureInfo.InvariantCulture));
                break;
            default:
                w.WriteString("value", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>CSV に書き出す (BOM 付きの UTF-8、CRLF、RFC 4180 の引用。TPL-25 の仕様 3)。値の列は画面と同じ表示。</summary>
    public static void WriteCsv(StructCompareResult result, IEnumerable<StructDiffNode> rows, Stream destination, bool hexOffsets = false,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        using var writer = new StreamWriter(destination, Utf8Bom, 1 << 16, leaveOpen: true);
        writer.Write(CsvHeader);
        writer.Write("\r\n");
        long done = 0;
        foreach (StructDiffNode n in rows)
        {
            if ((++done & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done);
            }

            string Off(long v) => hexOffsets ? "0x" + v.ToString("X", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
            bool left = n.HasLeft || n.Kind is StructDiffKind.Added;
            bool right = n.HasRight || n.Kind is StructDiffKind.Removed;
            string[] fields =
            [
                KindName(n.Kind), n.DisplayPath, n.HasLeft ? n.LeftPath : string.Empty, n.HasRight ? n.RightPath : string.Empty, n.TypeName,
                left ? Off(n.LeftOffset) : string.Empty, left ? Off(n.HasLeft ? n.LeftSize : 0) : string.Empty, n.HasLeft ? result.DisplayOf(n, false) : string.Empty,
                right ? Off(n.RightOffset) : string.Empty, right ? Off(n.HasRight ? n.RightSize : 0) : string.Empty, n.HasRight ? result.DisplayOf(n, true) : string.Empty,
                MatchOf(n),
            ];
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                {
                    writer.Write(',');
                }

                writer.Write(Csv(fields[i]));
            }

            writer.Write("\r\n");
        }
    }

    /// <summary>RFC 4180 の引用: カンマ・引用符・改行・制御文字を含む値は二重引用符で囲み、中の引用符は 2 つにする。</summary>
    public static string Csv(string value)
    {
        bool quote = value.Length > 0 && (value[0] == ' ' || value[^1] == ' ');
        foreach (char c in value)
        {
            if (c is ',' or '"' or '\r' or '\n' || c < ' ')
            {
                quote = true;
                break;
            }
        }

        return quote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    /// <summary>
    /// テキストのレポート (UTF-8、BOM なし): 要約の行 (表示言語で呼び出し側が作る) と、1 行に 1 差分
    /// (例: <c>変更  nt.FileHeader.TimeDateStamp  0x5F5E1000 → 0x60000000  (左 0x88、右 0x88)</c>)。
    /// <paramref name="kindLabel"/> は表示言語の種類の語、<paramref name="offsetsFormat"/> は左右のオフセットの書式 (<c>({0}、{1})</c> の形)。
    /// </summary>
    public static void WriteReport(StructCompareResult result, IEnumerable<string> summary, IEnumerable<StructDiffNode> rows,
        Func<StructDiffKind, string> kindLabel, string offsetsFormat, Stream destination, CancellationToken cancellationToken = default,
        Action<long>? progress = null)
    {
        using var writer = new StreamWriter(destination, Utf8NoBom, 1 << 16, leaveOpen: true);
        foreach (string line in summary)
        {
            writer.Write(line);
            writer.Write("\r\n");
        }

        writer.Write("\r\n");
        long done = 0;
        foreach (StructDiffNode n in rows)
        {
            if ((++done & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(done);
            }

            string l = n.HasLeft ? result.DisplayOf(n, false) : string.Empty;
            string r = n.HasRight ? result.DisplayOf(n, true) : string.Empty;
            string values = n.Kind switch
            {
                StructDiffKind.Added => "→ " + r,
                StructDiffKind.Removed => l + " →",
                _ => l + " → " + r,
            };
            string offsets = string.Format(CultureInfo.InvariantCulture, offsetsFormat,
                "0x" + n.LeftOffset.ToString("X", CultureInfo.InvariantCulture), "0x" + n.RightOffset.ToString("X", CultureInfo.InvariantCulture));
            writer.Write(kindLabel(n.Kind));
            writer.Write("  ");
            writer.Write(n.DisplayPath);
            writer.Write("  ");
            writer.Write(values.Trim());
            writer.Write("  ");
            writer.Write(offsets);
            writer.Write("\r\n");
        }
    }
}
