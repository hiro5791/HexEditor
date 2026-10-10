using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HexEditor.Core.Bookmarks;

/// <summary>ブックマークのファイルの形式 (INSP-30 の仕様 1)。</summary>
public enum BookmarkFileFormat
{
    /// <summary>HexEditor ブックマーク (<c>.hexbm.json</c>)。すべての情報を持つ。</summary>
    Json,

    /// <summary>CSV (UTF-8、BOM 付き)。</summary>
    Csv,

    /// <summary>wxHexEditor のタグ (XML)。</summary>
    WxHexEditor,

    /// <summary>プロジェクトファイル (<c>.hexproj</c>。00-overview 10 章)。</summary>
    Project,
}

/// <summary>インポートの選択肢 (INSP-30 の仕様 2)。</summary>
public enum BookmarkImportMode
{
    Append,
    Replace,
}

/// <summary>読み込んだブックマーク 1 件 (形式によらない)。</summary>
public sealed record BookmarkImportItem(long Start, long Length, string Name, BookmarkColor? Color, string? Group, int Number, string Comment)
{
    /// <summary>型と位置の式 (INSP-29。フェーズ 3)。JSON の値をそのまま残して書き出す。</summary>
    public string? TypeName { get; init; }

    public string? Expression { get; init; }
}

/// <summary>読み込んだ内容。</summary>
public sealed record BookmarkImportData(IReadOnlyList<BookmarkImportItem> Items, IReadOnlyList<BookmarkGroupRecord> Groups);

/// <summary>
/// 形式が不正なファイル (INSP-30 の「エラー」)。<see cref="Line"/> は行番号 (1 から)、<see cref="Position"/> は行の中の位置 (1 から)。
/// </summary>
public sealed class BookmarkFormatException(string reason, int? line, int? position) : FormatException(reason)
{
    public string Reason { get; } = reason;

    public int? Line { get; } = line;

    public int? Position { get; } = position;
}

/// <summary>インポートの結果 (InfoBar に出す件数。INSP-30 の仕様 3・4)。</summary>
public sealed record BookmarkImportReport(int Imported, int SkippedOutOfRange, int SkippedLimit, IReadOnlyList<int> NumbersMoved, int NumbersDropped);

/// <summary>
/// ブックマークとタグのインポート / エクスポート (INSP-30)。形式の細部:
/// <list type="bullet">
/// <item>JSON: 先頭に形式の版 (<c>"version": 1</c>)。グループ・番号・色・コメントなどすべての情報を持つ。</item>
/// <item>CSV: 見出し <c>start,length,name,color,group,comment</c>。開始は <c>0x</c> 付きの 16 進、色は <c>#RRGGBB</c>、グループは <c>/</c> 区切りのパス。
/// 値にカンマ・改行・<c>"</c> があれば <c>"</c> で囲む (Excel の「CSV UTF-8」と同じ)。BOM 付き UTF-8、改行は CRLF。</item>
/// <item>wxHexEditor: <c>&lt;wxHexEditor_XML_TAG&gt;&lt;filename path=".."&gt;&lt;TAG id=".."&gt;</c> の下に <c>start_offset</c>・
/// <c>end_offset</c> (このバイトを含む)・<c>tag_text</c>・<c>font_colour</c>・<c>note_colour</c>。対応しない情報は捨てる。</item>
/// </list>
/// </summary>
public static class BookmarkExchange
{
    public const int Version = 1;

    /// <summary>JSON の形式の名前。</summary>
    public const string JsonKind = "hexeditor-bookmarks";

    /// <summary>ファイル名の拡張子から形式を決める。分からなければ null。</summary>
    public static BookmarkFileFormat? FormatFromPath(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name switch
        {
            _ when name.EndsWith(".hexbm.json", StringComparison.Ordinal) || name.EndsWith(".json", StringComparison.Ordinal) => BookmarkFileFormat.Json,
            _ when name.EndsWith(".csv", StringComparison.Ordinal) => BookmarkFileFormat.Csv,
            _ when name.EndsWith(".tags", StringComparison.Ordinal) || name.EndsWith(".xml", StringComparison.Ordinal) => BookmarkFileFormat.WxHexEditor,
            _ when name.EndsWith(".hexproj", StringComparison.Ordinal) => BookmarkFileFormat.Project,
            _ => null,
        };
    }

    // ---- 書き出し ----

    /// <summary>書き出すファイルの中身 (CSV は BOM 付き UTF-8、ほかは BOM なしの UTF-8)。</summary>
    public static byte[] Export(BookmarkFileFormat format, BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items,
        IReadOnlyList<BookmarkGroupRecord>? groups = null, string? fileName = null)
    {
        groups ??= GroupsOf(bookmarks, items);
        return format switch
        {
            BookmarkFileFormat.Csv => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(ExportCsv(bookmarks, items))],
            BookmarkFileFormat.WxHexEditor => new UTF8Encoding(false).GetBytes(ExportWx(bookmarks, items, fileName)),
            _ => new UTF8Encoding(false).GetBytes(ExportJson(bookmarks, items, groups)),
        };
    }

    /// <summary>書き出すブックマークが使うグループ (祖先を含む)。</summary>
    public static IReadOnlyList<BookmarkGroupRecord> GroupsOf(BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (Bookmark b in items)
        {
            if (b.Group is { } g)
            {
                paths.UnionWith(BookmarkGroups.SelfAndAncestors(g));
            }
        }

        return [.. bookmarks.GroupRecords().Where(r => paths.Contains(r.Path))];
    }

    public static string ExportJson(BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items, IReadOnlyList<BookmarkGroupRecord> groups)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            WriteJsonBody(writer, bookmarks, items, groups);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>JSON の中身 (先頭に形式の版)。プロジェクトファイルの中にも同じ形で書く。</summary>
    internal static void WriteJsonBody(Utf8JsonWriter writer, BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items, IReadOnlyList<BookmarkGroupRecord> groups)
    {
        writer.WriteNumber("version", Version);
        writer.WriteString("format", JsonKind);
        BookmarkStore.WriteGroups(writer, groups);
        writer.WriteStartArray("bookmarks");
        foreach (Bookmark b in items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("start", b.Start);
            writer.WriteNumber("length", b.Length);
            writer.WriteString("name", b.Name);
            writer.WriteString("color", b.Color.ToString());

            // 色を個別に設定したか (グループの色を使うか。INSP-27 の仕様 2)。書いていないファイルは個別の色とみなす。
            writer.WriteBoolean("colorSet", b.ColorSet);

            if (b.Group is not null)
            {
                writer.WriteString("group", b.Group);
            }

            if (b.Number != 0)
            {
                writer.WriteNumber("number", b.Number);
            }

            if (b.Comment.Length > 0)
            {
                writer.WriteString("comment", b.Comment);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static string ExportCsv(BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items)
    {
        var text = new StringBuilder("start,length,name,color,group,comment\r\n");
        foreach (Bookmark b in items)
        {
            text.Append("0x").Append(b.Start.ToString("X", CultureInfo.InvariantCulture)).Append(',')
                .Append(b.Length.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(CsvField(b.Name)).Append(',')
                .Append(bookmarks.EffectiveColor(b).HexText).Append(',')
                .Append(CsvField(b.Group ?? string.Empty)).Append(',')
                .Append(CsvField(b.Comment)).Append("\r\n");
        }

        return text.ToString();
    }

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    public static string ExportWx(BookmarkCollection bookmarks, IReadOnlyList<Bookmark> items, string? fileName)
    {
        var file = new XElement("filename", new XAttribute("path", fileName ?? string.Empty));
        int id = 0;
        foreach (Bookmark b in items)
        {
            BookmarkColor color = bookmarks.EffectiveColor(b);
            file.Add(new XElement("TAG", new XAttribute("id", id++),
                new XElement("start_offset", b.Start.ToString(CultureInfo.InvariantCulture)),
                new XElement("end_offset", (b.Start + Math.Max(1, b.Length) - 1).ToString(CultureInfo.InvariantCulture)),
                new XElement("tag_text", b.Comment.Length > 0 ? b.Name + "\n" + b.Comment : b.Name),
                new XElement("font_colour", "#000000"),
                new XElement("note_colour", color.HexText)));
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("wxHexEditor_XML_TAG", file));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }

    // ---- 読み込み ----

    /// <summary>ファイルの中身を読む。形式が不正なら <see cref="BookmarkFormatException"/> (何も読み込まない)。</summary>
    public static BookmarkImportData Parse(BookmarkFileFormat format, byte[] content)
    {
        string text = DecodeText(content);
        return format switch
        {
            BookmarkFileFormat.Csv => ParseCsv(text),
            BookmarkFileFormat.WxHexEditor => ParseWx(text),
            BookmarkFileFormat.Project => ParseProject(text),
            _ => ParseJson(text),
        };
    }

    /// <summary>UTF-8 (BOM の有無を問わない) か、BOM 付きの UTF-16 の文字列にする。</summary>
    private static string DecodeText(byte[] content)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        int skip = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false).GetString(content, skip, content.Length - skip);
    }

    public static BookmarkImportData ParseJson(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Skip });
            return ReadJsonBody(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new BookmarkFormatException(ex.Message, ex.LineNumber is { } line ? (int)line + 1 : null,
                ex.BytePositionInLine is { } pos ? (int)pos + 1 : null);
        }
    }

    internal static BookmarkImportData ReadJsonBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("bookmarks", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new BookmarkFormatException("\"bookmarks\" array is missing", null, null);
        }

        if (root.TryGetProperty("version", out JsonElement version) && (version.ValueKind != JsonValueKind.Number || version.GetInt32() > Version))
        {
            throw new BookmarkFormatException($"Unsupported version {version}", null, null);
        }

        var items = new List<BookmarkImportItem>();
        int index = 0;
        foreach (JsonElement e in list.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("start", out JsonElement start) || start.ValueKind != JsonValueKind.Number)
            {
                throw new BookmarkFormatException($"Bookmark {index}: \"start\" is missing", null, null);
            }

            long length = e.TryGetProperty("length", out JsonElement l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 1;
            BookmarkColor? color = e.TryGetProperty("color", out JsonElement c) ? BookmarkColor.ParseHex(c.ToString()) ?? BookmarkColor.Parse(c.ToString()) : null;
            bool colorSet = !e.TryGetProperty("colorSet", out JsonElement set) || set.GetBoolean();
            items.Add(new BookmarkImportItem(
                start.GetInt64(),
                Math.Max(0, length),
                e.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                colorSet ? color : null,
                e.TryGetProperty("group", out JsonElement group) ? group.GetString() : null,
                e.TryGetProperty("number", out JsonElement number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : 0,
                e.TryGetProperty("comment", out JsonElement comment) ? comment.GetString() ?? string.Empty : string.Empty)
            {
                TypeName = e.TryGetProperty("type", out JsonElement type) ? type.GetString() : null,
                Expression = e.TryGetProperty("expression", out JsonElement expr) ? expr.GetString() : null,
            });
        }

        return new BookmarkImportData(items, BookmarkStore.ReadGroups(root));
    }

    public static BookmarkImportData ParseCsv(string text)
    {
        List<(List<string> Fields, int Line)> rows = SplitCsv(text);
        if (rows.Count == 0)
        {
            return new BookmarkImportData([], []);
        }

        // 見出しの行 (列の名前で列を決める。見出しがなければ既定の順)。
        string[] defaultOrder = ["start", "length", "name", "color", "group", "comment"];
        List<string> header = [.. rows[0].Fields.Select(f => f.Trim().ToLowerInvariant())];
        bool hasHeader = header.Contains("start");
        List<string> columns = hasHeader ? header : [.. defaultOrder];
        int Col(string name) => columns.IndexOf(name);
        var items = new List<BookmarkImportItem>();
        foreach ((List<string> fields, int line) in rows.Skip(hasHeader ? 1 : 0))
        {
            if (fields.All(f => f.Length == 0))
            {
                continue;
            }

            string Field(string name) => Col(name) is int i and >= 0 && i < fields.Count ? fields[i] : string.Empty;
            if (!TryParseNumber(Field("start"), out long start) || start < 0)
            {
                throw new BookmarkFormatException($"Invalid start offset \"{Field("start")}\"", line, Col("start") + 1);
            }

            string lengthText = Field("length");
            long length = 1;
            if (lengthText.Length > 0 && (!TryParseNumber(lengthText, out length) || length < 0))
            {
                throw new BookmarkFormatException($"Invalid length \"{lengthText}\"", line, Col("length") + 1);
            }

            string colorText = Field("color").Trim();
            BookmarkColor? color = null;
            if (colorText.Length > 0 && (color = BookmarkColor.ParseHex(colorText)) is null)
            {
                throw new BookmarkFormatException($"Invalid color \"{colorText}\"", line, Col("color") + 1);
            }

            string group = Field("group").Trim();
            items.Add(new BookmarkImportItem(start, length, Field("name"), color, group.Length > 0 ? group : null, 0,
                Field("comment").Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return new BookmarkImportData(items, []);
    }

    /// <summary>10 進か <c>0x</c> 付きの 16 進 (<c>h</c> で終わる 16 進も受け付ける)。</summary>
    private static bool TryParseNumber(string text, out long value)
    {
        text = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (text.EndsWith('h') || text.EndsWith('H'))
        {
            return long.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>CSV を行と列に分ける (<c>"</c> で囲んだ値の中のカンマ・改行・<c>""</c> を扱う)。行番号は物理的な行 (1 から)。</summary>
    private static List<(List<string> Fields, int Line)> SplitCsv(string text)
    {
        var rows = new List<(List<string>, int)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        int line = 1;
        int rowLine = 1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add((fields, rowLine));
                    fields = [];
                    line++;
                    rowLine = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (quoted)
        {
            throw new BookmarkFormatException("Unterminated quoted value", rowLine, null);
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add((fields, rowLine));
        }

        return rows;
    }

    public static BookmarkImportData ParseWx(string text)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new BookmarkFormatException(ex.Message, ex.LineNumber, ex.LinePosition);
        }

        var items = new List<BookmarkImportItem>();
        foreach (XElement tag in doc.Descendants("TAG"))
        {
            var info = (IXmlLineInfo)tag;
            if (!long.TryParse(tag.Element("start_offset")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long start)
                || !long.TryParse(tag.Element("end_offset")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long end) || end < start)
            {
                throw new BookmarkFormatException("Invalid start_offset or end_offset", info.LineNumber, info.LinePosition);
            }

            string tagText = tag.Element("tag_text")?.Value ?? string.Empty;
            string[] parts = tagText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', 2);
            BookmarkColor? color = BookmarkColor.ParseHex(tag.Element("note_colour")?.Value);
            items.Add(new BookmarkImportItem(start, end - start + 1, parts[0], color, null, 0, parts.Length > 1 ? parts[1] : string.Empty));
        }

        return new BookmarkImportData(items, []);
    }

    /// <summary>プロジェクトファイルのブックマークの部分 (<c>"bookmarks"</c> の中に JSON と同じ形)。</summary>
    public static BookmarkImportData ParseProject(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("bookmarks", out JsonElement body) || body.ValueKind != JsonValueKind.Object)
            {
                return new BookmarkImportData([], []);
            }

            return ReadJsonBody(body);
        }
        catch (JsonException ex)
        {
            throw new BookmarkFormatException(ex.Message, ex.LineNumber is { } line ? (int)line + 1 : null,
                ex.BytePositionInLine is { } pos ? (int)pos + 1 : null);
        }
    }

    // ---- 適用 ----

    /// <summary>
    /// 読み込んだブックマークを加える (INSP-30 の仕様 2〜4)。<paramref name="shift"/> はオフセットのずれ。ドキュメントの範囲外になるものは
    /// 読み込まない。番号が衝突したらインポートする方を優先し、既存のものから外す。1〜9 以外の番号は外す。
    /// </summary>
    public static BookmarkImportReport Apply(BookmarkCollection bookmarks, BookmarkImportData data, BookmarkImportMode mode, long shift, long documentLength)
    {
        if (mode == BookmarkImportMode.Replace)
        {
            bookmarks.Clear();
        }

        foreach (BookmarkGroupRecord g in data.Groups)
        {
            bookmarks.RestoreGroup(g);
        }

        int outOfRange = 0;
        int limit = 0;
        int dropped = 0;
        var moved = new List<int>();
        var added = new List<Bookmark>();
        foreach (BookmarkImportItem item in data.Items)
        {
            long start = item.Start + shift;
            if (start < 0 || start > documentLength || start + item.Length > documentLength)
            {
                outOfRange++;
                continue;
            }

            if (bookmarks.Count >= BookmarkCollection.MaxCount)
            {
                limit++;
                continue;
            }

            string? group = BookmarkGroups.Normalize(item.Group);
            if (group is not null && BookmarkGroups.DepthOf(group) > BookmarkGroups.MaxDepth)
            {
                group = string.Join(BookmarkGroups.Separator, group.Split(BookmarkGroups.Separator).Take(BookmarkGroups.MaxDepth));
            }

            Bookmark b = bookmarks.AddQuiet(start, item.Length, item.Name, item.Color ?? BookmarkColor.Default, group, colorSet: item.Color is not null);
            if (item.Comment.Length > 0)
            {
                b.Comment = item.Comment.Length <= BookmarkCollection.MaxCommentLength ? item.Comment : item.Comment[..BookmarkCollection.MaxCommentLength];
                b.IsCustomized = true;
            }

            if (item.Number is >= 1 and <= 9)
            {
                if (bookmarks.WithNumber(item.Number) is { } other && other != b)
                {
                    moved.Add(item.Number);
                }

                bookmarks.SetNumberQuiet(b, item.Number);
            }
            else if (item.Number != 0)
            {
                dropped++;
            }

            added.Add(b);
        }

        if (mode == BookmarkImportMode.Replace)
        {
            bookmarks.RaiseReset();
        }
        else
        {
            bookmarks.RaiseAdded(added);
        }

        return new BookmarkImportReport(added.Count, outOfRange, limit, moved, dropped);
    }
}
