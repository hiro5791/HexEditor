using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;

namespace HexEditor.Core.Files;

/// <summary>
/// プロジェクトファイル (<c>.hexproj</c>、JSON。00-overview 10 章): ファイル本体へのパスと、ドキュメントに付随するデータ
/// (ブックマークとグループ (INSP-23〜INSP-27)、ドキュメントの色付けルール (INSP-33)、インスペクタのエンディアン (INSP-02))。
/// ブックマークの部分は <c>.hexbm.json</c> と同じ形 (INSP-30)。ファイル本体のパスは、プロジェクトファイルと同じフォルダからの相対パスで書く
/// (別のドライブなら絶対パス)。
/// <para>形式:</para>
/// <code>
/// { "version": 1, "format": "hexeditor-project", "file": "seq.bin",
///   "bookmarks": { "version": 1, "format": "hexeditor-bookmarks", "groups": [...], "bookmarks": [...] },
///   "coloringRules": [ ... ], "inspector": { "endian": "big" } }
/// </code>
/// </summary>
public sealed record HexProject(string? FilePath, BookmarkImportData Bookmarks, IReadOnlyList<ColoringRule> ColoringRules, string? InspectorEndian)
{
    public const int Version = 1;
    public const string Kind = "hexeditor-project";
    public const string Extension = ".hexproj";

    /// <summary>書き出す。<paramref name="projectPath"/> はプロジェクトファイルの場所 (ファイル本体の相対パスの基準)。</summary>
    public static byte[] Write(string projectPath, string? documentPath, BookmarkCollection bookmarks, IReadOnlyList<ColoringRule> coloringRules,
        string? inspectorEndian)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("format", Kind);
            if (documentPath is not null)
            {
                writer.WriteString("file", RelativePath(projectPath, documentPath));
            }

            writer.WriteStartObject("bookmarks");
            IReadOnlyList<Bookmark> all = bookmarks.Ordered;
            BookmarkExchange.WriteJsonBody(writer, bookmarks, all, bookmarks.GroupRecords());
            writer.WriteEndObject();
            writer.WritePropertyName("coloringRules");
            JsonNode.Parse(ColoringRule.Serialize(coloringRules))!.WriteTo(writer);
            if (inspectorEndian is not null)
            {
                writer.WriteStartObject("inspector");
                writer.WriteString("endian", inspectorEndian);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>読む。形式が不正なら <see cref="BookmarkFormatException"/>。<see cref="FilePath"/> は絶対パスにして返す。</summary>
    public static HexProject Read(string projectPath, byte[] content)
    {
        string text = new UTF8Encoding(false).GetString(content).TrimStart('﻿');
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string? file = root.TryGetProperty("file", out JsonElement f) ? f.GetString() : null;
            string? absolute = file is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty, file));
            BookmarkImportData bookmarks = root.TryGetProperty("bookmarks", out JsonElement b) && b.ValueKind == JsonValueKind.Object
                ? BookmarkExchange.ReadJsonBody(b)
                : new BookmarkImportData([], []);
            IReadOnlyList<ColoringRule> rules = root.TryGetProperty("coloringRules", out JsonElement r) ? ColoringRule.Parse(r.GetRawText()) ?? [] : [];
            string? endian = root.TryGetProperty("inspector", out JsonElement i) && i.TryGetProperty("endian", out JsonElement e) ? e.GetString() : null;
            return new HexProject(absolute, bookmarks, rules, endian);
        }
        catch (JsonException ex)
        {
            throw new BookmarkFormatException(ex.Message, ex.LineNumber is { } line ? (int)line + 1 : null,
                ex.BytePositionInLine is { } pos ? (int)pos + 1 : null);
        }
    }

    private static string RelativePath(string projectPath, string documentPath)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;
        string full = Path.GetFullPath(documentPath);
        return string.Equals(Path.GetPathRoot(baseDir), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(baseDir, full)
            : full;
    }
}
