using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Templates.Online;

/// <summary>テンプレートの種類 (索引の <c>kind</c>。TPL-36 の仕様 2)。</summary>
public enum OnlineTemplateKind
{
    Hxt,
    Ksy,
}

/// <summary>
/// 索引の 1 項目 (TPL-36 の仕様 2): ID、表示名 (複数言語)、説明、作者、版、ライセンス、対象の形式 (拡張子・MIME)、必要なアプリの版、
/// ファイルの SHA-256、種類、拡張関数を使うか。<see cref="File"/> は索引からの相対 URL。
/// </summary>
public sealed record OnlineTemplateEntry
{
    public required string Id { get; init; }

    /// <summary>表示名 (言語のタグ → 名前。<c>en</c> は必ずある)。</summary>
    public required IReadOnlyDictionary<string, string> Names { get; init; }

    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();

    public string Author { get; init; } = string.Empty;

    public required string Version { get; init; }

    public string License { get; init; } = string.Empty;

    /// <summary>分類 (画面の絞り込み。TPL-36 の仕様 4)。</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Extensions { get; init; } = [];

    public IReadOnlyList<string> Mime { get; init; } = [];

    /// <summary>必要なアプリの版 (なければ null)。</summary>
    public string? MinAppVersion { get; init; }

    public required string Sha256 { get; init; }

    public OnlineTemplateKind Kind { get; init; } = OnlineTemplateKind.Hxt;

    /// <summary>拡張関数 (TPL-19) を使う (インストール時に確認する。TPL-36 の仕様 9)。</summary>
    public bool UsesExtensions { get; init; }

    /// <summary>テンプレートのファイル (索引からの相対 URL)。</summary>
    public required string File { get; init; }

    /// <summary>ファイルの大きさ (バイト。索引にあれば照合する)。</summary>
    public long? Size { get; init; }

    public string FileExtension => Kind == OnlineTemplateKind.Ksy ? ".ksy" : ".hxt";

    /// <summary>表示名 (UI の言語、なければ英語、なければ ID)。</summary>
    public string DisplayName(CultureInfo culture) => Localized(Names, culture) ?? Id;

    public string Description(CultureInfo culture) => Localized(Descriptions, culture) ?? string.Empty;

    internal static string? Localized(IReadOnlyDictionary<string, string> values, CultureInfo culture)
    {
        for (CultureInfo c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (values.TryGetValue(c.Name, out string? v) || values.TryGetValue(c.Name.ToLowerInvariant(), out v))
            {
                return v;
            }
        }

        return values.TryGetValue("en", out string? en) ? en : values.Values.FirstOrDefault();
    }

    /// <summary>検索 (ID・表示名 (すべての言語)・説明・作者・拡張子に、大文字と小文字を区別せずに含む)。空の検索はすべてに合う。</summary>
    public bool Matches(string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return true;
        }

        return Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Names.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))
            || Descriptions.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase))
            || Author.Contains(q, StringComparison.OrdinalIgnoreCase)
            || Extensions.Any(e => e.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>索引の形式の誤り。</summary>
public sealed class TemplateIndexFormatException(string message) : Exception(message);

/// <summary>
/// リポジトリの索引 (<c>index.json</c>。TPL-36 の仕様 1・2)。形式:
/// <code>
/// { "format": 1, "name": "…", "templates": [ { "id": "hxtest", "name": { "en": "HXTest format" }, "description": { "en": "…" },
///   "author": "…", "version": "1.0.0", "license": "MIT", "categories": ["test"], "extensions": ["hxts"], "mime": [],
///   "minAppVersion": "0.1.0", "sha256": "…", "size": 123, "kind": "hxt", "usesExtensions": false, "file": "hxtest-1.0.0.hxt" } ] }
/// </code>
/// 署名は別のファイル <c>index.json.sig</c> (Ed25519、索引のバイト列そのものに対する 64 バイト。Base64 の文字列でもよい)。
/// </summary>
public sealed class TemplateRepositoryIndex
{
    /// <summary>索引の大きさの上限 (TPL-36 の「巨大ファイル・長時間処理」)。</summary>
    public const long MaxIndexBytes = 16L * 1024 * 1024;

    /// <summary>1 つのテンプレートの大きさの上限 (TPL-01)。</summary>
    public const long MaxTemplateBytes = 4L * 1024 * 1024;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<OnlineTemplateEntry> Templates { get; init; } = [];

    public static TemplateRepositoryIndex Parse(ReadOnlySpan<byte> json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new TemplateIndexFormatException(ex.Message);
        }

        if (root is not JsonObject o || o["templates"] is not JsonArray items)
        {
            throw new TemplateIndexFormatException("The index has no \"templates\" array.");
        }

        var list = new List<OnlineTemplateEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject t)
            {
                continue;
            }

            string id = Str(t, "id") ?? throw new TemplateIndexFormatException("A template has no id.");
            if (!IsSafeId(id))
            {
                throw new TemplateIndexFormatException($"The template id is not allowed: {id}");
            }

            string version = Str(t, "version") ?? throw new TemplateIndexFormatException($"{id}: no version.");
            string sha = (Str(t, "sha256") ?? throw new TemplateIndexFormatException($"{id}: no sha256.")).ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            {
                throw new TemplateIndexFormatException($"{id}: sha256 is not a SHA-256 value.");
            }

            string file = Str(t, "file") ?? throw new TemplateIndexFormatException($"{id}: no file.");
            if (!ids.Add(id + "@" + version))
            {
                continue;
            }

            list.Add(new OnlineTemplateEntry
            {
                Id = id,
                Names = Texts(t["name"]) is { Count: > 0 } names ? names : new Dictionary<string, string> { ["en"] = id },
                Descriptions = Texts(t["description"]),
                Author = Str(t, "author") ?? string.Empty,
                Version = version,
                License = Str(t, "license") ?? string.Empty,
                Categories = Strings(t["categories"]),
                Extensions = Strings(t["extensions"]),
                Mime = Strings(t["mime"]),
                MinAppVersion = Str(t, "minAppVersion"),
                Sha256 = sha,
                Size = t["size"] is JsonValue sv && sv.TryGetValue(out long size) ? size : null,
                Kind = string.Equals(Str(t, "kind"), "ksy", StringComparison.OrdinalIgnoreCase) ? OnlineTemplateKind.Ksy : OnlineTemplateKind.Hxt,
                UsesExtensions = t["usesExtensions"] is JsonValue uv && uv.TryGetValue(out bool uses) && uses,
                File = file,
            });
        }

        return new TemplateRepositoryIndex { Name = Str(o, "name") ?? string.Empty, Templates = list };
    }

    /// <summary>ID に使える文字 (ファイル名にするため: 英数字、<c>-</c>、<c>_</c>、<c>.</c>。先頭は英数字)。</summary>
    public static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.Contains("..", StringComparison.Ordinal);

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static IReadOnlyDictionary<string, string> Texts(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonValue v && v.TryGetValue(out string? single))
        {
            result["en"] = single;
        }
        else if (node is JsonObject o)
        {
            foreach ((string key, JsonNode? value) in o)
            {
                if (value is JsonValue jv && jv.TryGetValue(out string? s))
                {
                    result[key] = s;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray a ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>()] : [];
}

/// <summary>版の比較 (<c>1.2.3</c>、<c>1.2.3-beta.1</c>。数字の部分を数値で比べ、プレリリースは正式版より前)。</summary>
public static class TemplateVersion
{
    public static int Compare(string a, string b)
    {
        (string coreA, string? preA) = Split(a);
        (string coreB, string? preB) = Split(b);
        string[] pa = coreA.Split('.');
        string[] pb = coreB.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            long x = i < pa.Length && long.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out long vx) ? vx : 0;
            long y = i < pb.Length && long.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out long vy) ? vy : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return (preA, preB) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(preA, preB),
        };
    }

    private static (string Core, string? Pre) Split(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            v = v[..plus];
        }

        int dash = v.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
    }
}
