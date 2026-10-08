using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>
/// 結果一覧の 1 行 (FIND-20 の仕様 4)。<see cref="Offset"/> と <see cref="Length"/> は今の状態での位置 (FIND-03 の補正後)。
/// </summary>
public sealed record SearchResultRow(
    long Number,
    long Offset,
    long Length,
    long OriginalOffset,
    MatchStatus Status,
    string Hex,
    string Text,
    string Before,
    string After,
    string? Variant,
    string? Value);

/// <summary>結果一覧の行を作る (表示とエクスポートで共通)。</summary>
public sealed class SearchResultRowFactory
{
    /// <summary>一致したデータの Hex の列に出すバイト数 (FIND-20 の仕様 4)。</summary>
    public const int DataBytes = 32;

    /// <summary>テキストの列に出す文字数。</summary>
    public const int DataChars = 32;

    /// <summary>前後のデータの列のバイト数。</summary>
    public const int ContextBytes = 16;

    private readonly SearchResults _results;
    private readonly DocumentSnapshot _current;
    private readonly MatchTracker _tracker;
    private readonly Encoding _decoder;

    /// <param name="current">今の状態 (位置の補正と、データの読み込みに使う)。</param>
    /// <param name="encoding">テキストの列の文字コード (表示中の文字コード。テキストの検索では検索の文字コード)。</param>
    public SearchResultRowFactory(SearchResults results, DocumentSnapshot current, Encoding encoding)
    {
        _results = results;
        _current = current;
        _tracker = new MatchTracker(results.Snapshot, current);
        var decoder = (Encoding)encoding.Clone();
        decoder.DecoderFallback = new DecoderReplacementFallback("�");
        _decoder = decoder;
    }

    public SearchResults Results => _results;

    public DocumentSnapshot Current => _current;

    /// <summary>一致の位置を今の状態に補正する。</summary>
    public TrackedMatch Track(SearchMatch match) => _tracker.Track(match);

    /// <summary><paramref name="index"/> 番目 (0 から) の行。</summary>
    public SearchResultRow Row(long index) => Row(index, _results[index]);

    public SearchResultRow Row(long index, SearchMatch match) => Build(index, match, nonBlocking: false)!;

    /// <summary>
    /// 表示用の読み込み (ブロックしない) で行を作る。まだ読み込み中のバイトがあれば null (バックグラウンドで <see cref="Row(long, SearchMatch)"/> を呼ぶ)。
    /// </summary>
    public SearchResultRow? TryRowForDisplay(long index, SearchMatch match) => Build(index, match, nonBlocking: true);

    private SearchResultRow? Build(long index, SearchMatch match, bool nonBlocking)
    {
        TrackedMatch t = _tracker.Track(match);
        long length = t.Length;
        long dataLength = Math.Min(DataBytes, length);
        long beforeStart = Math.Max(0, t.Offset - ContextBytes);
        byte[]? data = Read(t.Offset, dataLength, nonBlocking);
        byte[]? before = Read(beforeStart, t.Offset - beforeStart, nonBlocking);
        byte[]? after = Read(t.Offset + length, ContextBytes, nonBlocking);
        if (data is null || before is null || after is null)
        {
            return null;
        }

        SearchPattern pattern = _results.Pattern;
        string? variant = pattern.Variants.Count > 1 && match.Variant < pattern.Variants.Count ? pattern.Variants[match.Variant] : null;
        string? value = null;
        if (pattern.Numeric is { } numeric && t.Status != MatchStatus.Deleted)
        {
            byte[]? valueBytes = data.Length >= numeric.ByteLength ? data : Read(t.Offset, numeric.ByteLength, nonBlocking);
            if (valueBytes is null)
            {
                return null;
            }

            value = numeric.FormatValue(valueBytes, match.Variant);
            variant ??= pattern.Variants.Count == 1 ? pattern.Variants[0] : null;
        }

        return new SearchResultRow(
            index + 1,
            t.Offset,
            length,
            match.Offset,
            t.Status,
            HexOf(data),
            TextOf(data),
            HexOf(before),
            HexOf(after),
            variant,
            value);
    }

    /// <summary>今の状態の範囲を読む。<paramref name="nonBlocking"/> なら表示用の読み込みで、読み込み中のバイトがあれば null。</summary>
    private byte[]? Read(long offset, long length, bool nonBlocking)
    {
        length = Math.Clamp(Math.Min(length, _current.Length - offset), 0, int.MaxValue);
        if (length == 0 || offset < 0)
        {
            return [];
        }

        byte[] buffer = new byte[length];
        if (nonBlocking)
        {
            var states = new ByteState[length];
            int n = _current.ReadForDisplay(offset, buffer, states);
            return states.AsSpan(0, n).Contains(ByteState.Loading) ? null : buffer[..n];
        }

        ReadResult r = _current.Read(offset, buffer);
        return r.BytesReturned == buffer.Length ? buffer : buffer[..r.BytesReturned];
    }

    private static string HexOf(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private string TextOf(byte[] bytes)
    {
        string text = _decoder.GetString(bytes);
        var sb = new StringBuilder(Math.Min(text.Length, DataChars));
        int chars = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            if (chars++ >= DataChars)
            {
                break;
            }

            sb.Append(Rune.IsControl(r) ? "." : r.ToString());
        }

        return sb.ToString();
    }
}

/// <summary>エクスポートの形式 (FIND-21 の仕様 4)。</summary>
public enum ExportFormat
{
    /// <summary>UTF-8 (BOM 付き)、区切りはカンマ。</summary>
    Csv,
    Json,
}

/// <summary>エクスポートの列の見出しと状態の文言 (表示言語の文字列を UI から渡す)。</summary>
public sealed record ExportLabels
{
    public string Number { get; init; } = "No.";

    public string Document { get; init; } = "Document";

    public string Offset { get; init; } = "Offset";

    public string OffsetHex { get; init; } = "Offset (hex)";

    public string Length { get; init; } = "Length";

    public string Hex { get; init; } = "Data (hex)";

    public string Text { get; init; } = "Data (text)";

    public string Before { get; init; } = "Before";

    public string After { get; init; } = "After";

    public string Status { get; init; } = "Status";

    public string Endian { get; init; } = "Endian";

    public string Value { get; init; } = "Value";

    public Func<MatchStatus, string> StatusText { get; init; } = s => s == MatchStatus.Unchanged ? string.Empty : s.ToString();
}

/// <summary>
/// 結果一覧のエクスポート (FIND-21 の仕様 4)。CSV (UTF-8、BOM 付き、カンマ区切り) と JSON。列は一覧の表示列で、オフセットは
/// 10 進と 16 進の両方を出す。「開いているすべてのドキュメント」の結果はドキュメントの列を加える。行は少しずつ作って書くため、
/// 件数に比例したメモリを使わない。長時間処理として進捗とキャンセルを扱う。
/// </summary>
public static class SearchResultsExporter
{
    /// <summary>
    /// <paramref name="rows"/> の行 (結果の番号 0 から) を書き出す。<paramref name="indices"/> が null ならすべての行。
    /// </summary>
    public static void Export(SearchResultRowFactory rows, Stream stream, ExportFormat format, ExportLabels? labels = null,
        IReadOnlyList<long>? indices = null, LongRunningOperation? operation = null, CancellationToken cancellationToken = default) =>
        Export([(null, rows)], stream, format, labels, indices, operation, cancellationToken);

    /// <summary>
    /// ドキュメントごとの結果を続けて書き出す。行の番号は一覧と同じ通し番号 (0 から)。<paramref name="indices"/> が null ならすべての行。
    /// ドキュメントの名前 (Document) がどれかにあれば「ドキュメント」の列を出す。
    /// </summary>
    public static void Export(IReadOnlyList<(string? Document, SearchResultRowFactory Rows)> groups, Stream stream, ExportFormat format,
        ExportLabels? labels = null, IReadOnlyList<long>? indices = null, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        labels ??= new ExportLabels();
        SearchPattern pattern = groups[0].Rows.Results.Pattern;
        bool numeric = pattern.Numeric is not null || pattern.Variants.Count > 1;
        bool documents = groups.Any(g => g.Document is not null);
        long total = indices?.Count ?? groups.Sum(g => g.Rows.Results.LongCount);
        operation?.SetTotal(total);

        (string? Document, SearchResultRowFactory Rows, long Local)? Locate(long index)
        {
            foreach ((string? document, SearchResultRowFactory rows) in groups)
            {
                long count = rows.Results.LongCount;
                if (index < count)
                {
                    return (document, rows, index);
                }

                index -= count;
            }

            return null;
        }

        IEnumerable<(string? Document, SearchResultRow Row)> Enumerate()
        {
            const int Batch = 4096;
            long written = 0;
            if (indices is null)
            {
                long number = 0;
                foreach ((string? document, SearchResultRowFactory rows) in groups)
                {
                    long count = rows.Results.LongCount;
                    for (long start = 0; start < count; start += Batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        operation?.CancellationToken.ThrowIfCancellationRequested();
                        IReadOnlyList<SearchMatch> matches = rows.Results.GetRange(start, (int)Math.Min(Batch, count - start));
                        for (int k = 0; k < matches.Count; k++)
                        {
                            yield return (document, rows.Row(start + k, matches[k]) with { Number = ++number });
                        }

                        written += matches.Count;
                        operation?.Report(written);
                    }
                }

                yield break;
            }

            foreach (long index in indices)
            {
                if ((written & (Batch - 1)) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    operation?.Report(written);
                }

                if (Locate(index) is { } found)
                {
                    yield return (found.Document, found.Rows.Row(found.Local) with { Number = index + 1 });
                }

                written++;
            }
        }

        if (format == ExportFormat.Csv)
        {
            WriteCsv(stream, Enumerate(), labels, numeric, documents);
        }
        else
        {
            WriteJson(stream, Enumerate(), numeric, documents);
        }
    }

    private static void WriteCsv(Stream stream, IEnumerable<(string? Document, SearchResultRow Row)> rows, ExportLabels labels, bool numeric, bool documents)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 65536, leaveOpen: true);
        var header = new List<string> { labels.Number };
        if (documents)
        {
            header.Add(labels.Document);
        }

        header.AddRange([labels.Offset, labels.OffsetHex, labels.Length, labels.Hex, labels.Text, labels.Before, labels.After, labels.Status]);
        if (numeric)
        {
            header.Add(labels.Endian);
            header.Add(labels.Value);
        }

        writer.Write(string.Join(',', header.Select(Csv)));
        writer.Write("\r\n");
        foreach ((string? document, SearchResultRow r) in rows)
        {
            var fields = new List<string> { r.Number.ToString(CultureInfo.InvariantCulture) };
            if (documents)
            {
                fields.Add(document ?? string.Empty);
            }

            fields.AddRange(
            [
                r.Offset.ToString(CultureInfo.InvariantCulture),
                "0x" + r.Offset.ToString("X", CultureInfo.InvariantCulture),
                r.Length.ToString(CultureInfo.InvariantCulture),
                r.Hex,
                r.Text,
                r.Before,
                r.After,
                labels.StatusText(r.Status),
            ]);
            if (numeric)
            {
                fields.Add(r.Variant ?? string.Empty);
                fields.Add(r.Value ?? string.Empty);
            }

            writer.Write(string.Join(',', fields.Select(Csv)));
            writer.Write("\r\n");
        }
    }

    private static void WriteJson(Stream stream, IEnumerable<(string? Document, SearchResultRow Row)> rows, bool numeric, bool documents)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteStartArray("results");
        foreach ((string? document, SearchResultRow r) in rows)
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", r.Number);
            if (documents)
            {
                writer.WriteString("document", document ?? string.Empty);
            }

            writer.WriteNumber("offset", r.Offset);
            writer.WriteString("offsetHex", "0x" + r.Offset.ToString("X", CultureInfo.InvariantCulture));
            writer.WriteNumber("length", r.Length);
            writer.WriteString("hex", r.Hex);
            writer.WriteString("text", r.Text);
            writer.WriteString("before", r.Before);
            writer.WriteString("after", r.After);
            writer.WriteString("status", r.Status.ToString());
            if (numeric)
            {
                writer.WriteString("endian", r.Variant ?? string.Empty);
                writer.WriteString("value", r.Value ?? string.Empty);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>CSV の 1 項目。カンマ・引用符・改行を含む場合は引用符で囲む。</summary>
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>結果をブックマークに変換するときの名前 (FIND-21 の仕様 3)。ブックマークの作成は INSP-23 の部品が行う。</summary>
public static class SearchResultsConversion
{
    /// <summary>10,000 件を超える場合は確認を求める (FIND-21 の仕様 3)。</summary>
    public const int BookmarkConfirmThreshold = 10_000;

    /// <summary>マルチ選択に変換する範囲の上限 (FIND-21 の仕様 2)。</summary>
    public const int MaxSelectionRanges = 1_000_000;

    /// <summary>ブックマークの名前の形「<paramref name="prefix"/>: <paramref name="summary"/> #番号」(prefix は「検索」の訳)。</summary>
    public static string BookmarkName(string prefix, string summary, long number) =>
        string.Create(CultureInfo.CurrentCulture, $"{prefix}: {summary} #{number}");

    /// <summary>グループの名前「<paramref name="prefix"/> 日時」(prefix は「検索結果」の訳、日時は地域設定の書式)。</summary>
    public static string BookmarkGroup(string prefix, DateTimeOffset time) =>
        string.Create(CultureInfo.CurrentCulture, $"{prefix} {time.LocalDateTime:g}");
}
