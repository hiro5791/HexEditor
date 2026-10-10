using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Selection;

/// <summary>
/// 名前を付けて保存した選択 (選択セット。EDIT-09 の仕様 1・2)。要素 (開始, 長さ) の一覧、または矩形 (行・列とその時点の 1 行のバイト数) で持つ。
/// </summary>
public sealed record SelectionSet(string Name, IReadOnlyList<ByteRange> Ranges, RectSelection? Rectangle, DateTimeOffset SavedAt)
{
    /// <summary>保存したときのドキュメントの長さ (矩形の要素数・合計の計算に使う)。</summary>
    public long DocumentLength { get; init; } = long.MaxValue;

    /// <summary>要素数。</summary>
    public long Count => Rectangle is { } r ? Snapshot().Count : Ranges.Count;

    /// <summary>合計バイト数。</summary>
    public long TotalLength => Rectangle is { } r ? r.ByteCount(DocumentLength) : Ranges.Sum(x => x.Length);

    private View.SelectionSnapshot Snapshot() =>
        new(View.SelectionKind.Rectangle, null, Rectangle, null, DocumentLength);
}

/// <summary>選択セットの保存の結果 (EDIT-09 の仕様 1・7)。</summary>
public enum SelectionSetSaveResult
{
    Saved,

    /// <summary>同じ名前がある (確認の上で <c>overwrite</c> を真にしてもう一度呼ぶ)。</summary>
    Exists,

    /// <summary>名前が 1〜100 文字でない。</summary>
    InvalidName,

    /// <summary>1 ドキュメントあたり 100 個を超える。</summary>
    TooMany,

    /// <summary>保存する選択がない。</summary>
    Empty,
}

/// <summary>選択セットの名前の変更の結果 (EDIT-09 の「画面」の名前変更)。</summary>
public enum SelectionSetRenameResult
{
    Renamed,

    /// <summary>変える選択セットがない。</summary>
    NotFound,

    /// <summary>新しい名前が 1〜100 文字でない。</summary>
    InvalidName,

    /// <summary>新しい名前の選択セットが既にある (名前はドキュメント内で一意。仕様 1)。</summary>
    Duplicate,
}

/// <summary>
/// 1 つのドキュメントの選択セット (EDIT-09)。保存先はドキュメントに付随するデータ (<see cref="DocumentDataStore"/> の
/// <see cref="Kind"/>)。編集でデータがずれても、保存した選択セットのオフセットは調整しない (仕様 5)。
/// </summary>
public sealed class SelectionSetCollection
{
    /// <summary>付随データの種類。</summary>
    public const string Kind = "selections";

    /// <summary>1 ドキュメントあたりの上限 (仕様 7)。</summary>
    public const int MaxSets = 100;

    /// <summary>名前の最大の長さ (仕様 1)。</summary>
    public const int MaxNameLength = 100;

    private readonly List<SelectionSet> _sets = [];

    /// <summary>選択セット (保存した順)。</summary>
    public IReadOnlyList<SelectionSet> Sets => _sets;

    /// <summary>一覧が変わった。</summary>
    public event EventHandler? Changed;

    public SelectionSet? Find(string name) => _sets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= MaxNameLength;

    /// <summary>
    /// 選択を名前を付けて保存する。同じ名前があれば <paramref name="overwrite"/> が偽なら <see cref="SelectionSetSaveResult.Exists"/>
    /// (UI は確認してから上書きする)。
    /// </summary>
    public SelectionSetSaveResult Save(string name, View.SelectionSnapshot selection, bool overwrite = false, DateTimeOffset? now = null)
    {
        if (!IsValidName(name))
        {
            return SelectionSetSaveResult.InvalidName;
        }

        if (selection.IsEmpty)
        {
            return SelectionSetSaveResult.Empty;
        }

        name = name.Trim();
        int index = _sets.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (index >= 0 && !overwrite)
        {
            return SelectionSetSaveResult.Exists;
        }

        if (index < 0 && _sets.Count >= MaxSets)
        {
            return SelectionSetSaveResult.TooMany;
        }

        IReadOnlyList<ByteRange> ranges = selection.Rectangle is null ? selection.Ranges.ToList() : [];
        var set = new SelectionSet(name, ranges, selection.Rectangle, now ?? DateTimeOffset.Now) { DocumentLength = selection.DocumentLength };
        if (index >= 0)
        {
            _sets[index] = set;
        }
        else
        {
            _sets.Add(set);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return SelectionSetSaveResult.Saved;
    }

    public bool Delete(string name)
    {
        bool removed = _sets.RemoveAll(s => string.Equals(s.Name, name, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>名前を変える。新しい名前が正しくない・既にある場合は変えずに理由を返す (UI はそれを示す)。</summary>
    public SelectionSetRenameResult Rename(string name, string newName)
    {
        int index = _sets.FindIndex(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (index < 0)
        {
            return SelectionSetRenameResult.NotFound;
        }

        if (!IsValidName(newName))
        {
            return SelectionSetRenameResult.InvalidName;
        }

        if (newName.Trim() != name && Find(newName.Trim()) is not null)
        {
            return SelectionSetRenameResult.Duplicate;
        }

        _sets[index] = _sets[index] with { Name = newName.Trim() };
        Changed?.Invoke(this, EventArgs.Empty);
        return SelectionSetRenameResult.Renamed;
    }

    /// <summary>
    /// 読み込むときの要素 (仕様 4): ドキュメントの長さより後ろを指す要素は切り詰めるか除外する。矩形は今の 1 行のバイト数が同じなら矩形のまま、
    /// 違えば行ごとの要素にする。切り詰めた・除外した要素の数を返す (UI は InfoBar で知らせる)。
    /// </summary>
    public static (IReadOnlyList<ByteRange> Ranges, RectSelection? Rectangle, int Truncated, int Removed) Resolve(SelectionSet set, long documentLength,
        int bytesPerRow, int rowShift)
    {
        if (set.Rectangle is { } rect)
        {
            if (rect.BytesPerRow == bytesPerRow && rect.RowShift == rowShift)
            {
                return ([], rect, 0, 0);
            }

            var rows = new RangeSet(rect.Ranges(Math.Min(documentLength, set.DocumentLength)));
            return ([.. rows], null, 0, 0);
        }

        var ranges = new RangeSet(set.Ranges);
        (int truncated, int removed) = ranges.ClipTo(documentLength);
        return ([.. ranges], null, truncated, removed);
    }

    // ---- 保存と読み込み (付随データ) ----

    /// <summary>付随データに書く (100 万要素でも書けるよう、要素は配列で書き流す)。選択セットがなければ消す。</summary>
    public void Save(DocumentDataStore store, string documentPath, FileStamp? stamp)
    {
        if (_sets.Count == 0)
        {
            store.Delete(documentPath, Kind);
            return;
        }

        store.Write(documentPath, Kind, stamp, writer =>
        {
            writer.WriteStartArray("sets");
            foreach (SelectionSet set in _sets)
            {
                writer.WriteStartObject();
                writer.WriteString("name", set.Name);
                writer.WriteString("savedAt", set.SavedAt);
                writer.WriteNumber("documentLength", set.DocumentLength);
                if (set.Rectangle is { } r)
                {
                    writer.WriteStartObject("rectangle");
                    writer.WriteNumber("firstRow", r.FirstRow);
                    writer.WriteNumber("lastRow", r.LastRow);
                    writer.WriteNumber("firstColumn", r.FirstColumn);
                    writer.WriteNumber("lastColumn", r.LastColumn);
                    writer.WriteNumber("bytesPerRow", r.BytesPerRow);
                    writer.WriteNumber("rowShift", r.RowShift);
                    writer.WriteEndObject();
                }

                // 要素は [開始, 長さ, 開始, 長さ, …] の数値の並び (小さく、速く読み書きできる)。
                writer.WriteStartArray("ranges");
                foreach (ByteRange range in set.Ranges)
                {
                    writer.WriteNumberValue(range.Start);
                    writer.WriteNumberValue(range.Length);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>付随データから読む。なければ (読めなければ) 空。</summary>
    public static SelectionSetCollection Load(DocumentDataStore store, string documentPath)
    {
        var collection = new SelectionSetCollection();
        try
        {
            if (store.Read(documentPath, Kind) is not { } read)
            {
                return collection;
            }

            using JsonDocument doc = read.Body;
            if (!doc.RootElement.TryGetProperty("sets", out JsonElement sets) || sets.ValueKind != JsonValueKind.Array)
            {
                return collection;
            }

            foreach (JsonElement e in sets.EnumerateArray())
            {
                string name = e.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                if (!IsValidName(name))
                {
                    continue;
                }

                DateTimeOffset savedAt = e.TryGetProperty("savedAt", out JsonElement t) && t.TryGetDateTimeOffset(out DateTimeOffset time) ? time : default;
                long length = e.TryGetProperty("documentLength", out JsonElement l) ? l.GetInt64() : long.MaxValue;
                RectSelection? rect = null;
                if (e.TryGetProperty("rectangle", out JsonElement r))
                {
                    rect = new RectSelection(r.GetProperty("firstRow").GetInt64(), r.GetProperty("lastRow").GetInt64(),
                        r.GetProperty("firstColumn").GetInt32(), r.GetProperty("lastColumn").GetInt32(),
                        r.GetProperty("bytesPerRow").GetInt32(), r.GetProperty("rowShift").GetInt32());
                }

                var ranges = new List<ByteRange>();
                if (e.TryGetProperty("ranges", out JsonElement values))
                {
                    long start = -1;
                    foreach (JsonElement v in values.EnumerateArray())
                    {
                        if (start < 0)
                        {
                            start = v.GetInt64();
                        }
                        else
                        {
                            ranges.Add(new ByteRange(start, v.GetInt64()));
                            start = -1;
                        }
                    }
                }

                if (collection._sets.Count < MaxSets)
                {
                    collection._sets.Add(new SelectionSet(name, ranges, rect, savedAt) { DocumentLength = length });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            // 読めない付随データは無視する (00-overview.md 10 章)。
        }

        return collection;
    }
}

/// <summary>選択範囲のインポートの書式エラーの理由 (EDIT-09 の「エラー」)。</summary>
public enum SelectionImportError
{
    /// <summary>開始が数値ではない。</summary>
    StartNotNumber,

    /// <summary>長さが数値ではない。</summary>
    LengthNotNumber,

    /// <summary>終了が数値ではない。</summary>
    EndNotNumber,

    /// <summary>終了が開始より前、または長さが 0 以下。</summary>
    EmptyRange,

    /// <summary>行の形が読めない (値の数が違うなど)。</summary>
    BadLine,

    /// <summary>JSON として読めない、または形が違う。</summary>
    BadJson,
}

/// <summary>インポートの書式エラー (行番号は見出しの行を 1 行目として数える)。</summary>
public sealed class SelectionImportException(int line, SelectionImportError error)
    : FormatException($"{line} 行目: {error}")
{
    public int Line { get; } = line;

    public SelectionImportError Error { get; } = error;
}

/// <summary>選択範囲のエクスポート・インポートの形式 (EDIT-09 の仕様 6)。</summary>
public enum SelectionFileFormat
{
    Csv,
    Json,

    /// <summary>1 行に「開始 長さ」または「開始-終了」(インポートのみ)。</summary>
    Text,
}

/// <summary>選択範囲のエクスポート・インポート (EDIT-09 の仕様 6)。</summary>
public static class SelectionFile
{
    /// <summary>CSV: 1 行目は見出し <c>start,length</c>、値は常に <c>0x</c> 付きの 16 進 (大文字、先頭の 0 は詰めない)。</summary>
    public static void WriteCsv(TextWriter writer, IEnumerable<ByteRange> ranges)
    {
        writer.Write("start,length\r\n");
        foreach (ByteRange r in ranges)
        {
            writer.Write("0x");
            writer.Write(r.Start.ToString("X", CultureInfo.InvariantCulture));
            writer.Write(",0x");
            writer.Write(r.Length.ToString("X", CultureInfo.InvariantCulture));
            writer.Write("\r\n");
        }
    }

    /// <summary>JSON: <c>{"name": "...", "ranges": [{"start": 256, "length": 16}]}</c>。</summary>
    public static void WriteJson(Stream stream, string name, IEnumerable<ByteRange> ranges)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteStartArray("ranges");
        foreach (ByteRange r in ranges)
        {
            writer.WriteStartObject();
            writer.WriteNumber("start", r.Start);
            writer.WriteNumber("length", r.Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static string ToCsv(IEnumerable<ByteRange> ranges)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(writer, ranges);
        return writer.ToString();
    }

    public static string ToJson(string name, IEnumerable<ByteRange> ranges)
    {
        using var stream = new MemoryStream();
        WriteJson(stream, name, ranges);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// インポートする。形式は内容から判別する (<c>{</c> で始まれば JSON、1 行目が <c>start,length</c> なら CSV、それ以外はテキスト)。
    /// CSV とテキストの値は <c>0x</c> 付きの 16 進と 10 進 (<c>0x</c> のない値) を受け付ける。誤りがあれば
    /// <see cref="SelectionImportException"/> (何も読み込まない)。
    /// </summary>
    public static List<ByteRange> Parse(string content, out string? name)
    {
        name = null;
        string trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{'))
        {
            return ParseJson(trimmed, out name);
        }

        var result = new List<ByteRange>();
        string[] lines = content.TrimStart('﻿').Split('\n');
        bool csv = lines.Length > 0 && lines[0].Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Equals("start,length", StringComparison.OrdinalIgnoreCase);
        for (int i = csv ? 1 : 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (csv)
            {
                string[] cells = line.Split(',');
                if (cells.Length != 2)
                {
                    throw new SelectionImportException(lineNumber, SelectionImportError.BadLine);
                }

                long start = Number(cells[0], lineNumber, SelectionImportError.StartNotNumber);
                long length = Number(cells[1], lineNumber, SelectionImportError.LengthNotNumber);
                result.Add(Checked(start, length, lineNumber));
                continue;
            }

            // テキスト: 「開始 長さ」または「開始-終了」(終了はそのバイトを含む)。
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                long start = Number(parts[0], lineNumber, SelectionImportError.StartNotNumber);
                long length = Number(parts[1], lineNumber, SelectionImportError.LengthNotNumber);
                result.Add(Checked(start, length, lineNumber));
            }
            else if (parts.Length == 1 && parts[0].IndexOf('-', 1) is int dash and > 0)
            {
                long start = Number(parts[0][..dash], lineNumber, SelectionImportError.StartNotNumber);
                long end = Number(parts[0][(dash + 1)..], lineNumber, SelectionImportError.EndNotNumber);
                result.Add(Checked(start, end - start + 1, lineNumber));
            }
            else
            {
                throw new SelectionImportException(lineNumber, SelectionImportError.BadLine);
            }
        }

        return result;
    }

    private static List<ByteRange> ParseJson(string content, out string? name)
    {
        name = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;
            name = root.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var result = new List<ByteRange>();
            int index = 0;
            foreach (JsonElement r in root.GetProperty("ranges").EnumerateArray())
            {
                index++;
                if (!r.TryGetProperty("start", out JsonElement s) || !s.TryGetInt64(out long start) || start < 0)
                {
                    throw new SelectionImportException(index, SelectionImportError.StartNotNumber);
                }

                if (!r.TryGetProperty("length", out JsonElement l) || !l.TryGetInt64(out long length))
                {
                    throw new SelectionImportException(index, SelectionImportError.LengthNotNumber);
                }

                result.Add(Checked(start, length, index));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            int line = ex is JsonException { LineNumber: { } l } ? (int)l + 1 : 1;
            throw new SelectionImportException(line, SelectionImportError.BadJson);
        }
    }

    private static ByteRange Checked(long start, long length, int line) =>
        length > 0 ? new ByteRange(start, length) : throw new SelectionImportException(line, SelectionImportError.EmptyRange);

    private static long Number(string text, int line, SelectionImportError error)
    {
        text = text.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;
        bool ok = hex
            ? long.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long value)
            : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return ok && value >= 0 ? value : throw new SelectionImportException(line, error);
    }
}
