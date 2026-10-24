using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Compare.Structure;

/// <summary>配列ごとの対応付けの方法 (ANA-31 の仕様 5 の 3)。テンプレートと配列のパス (添字を除いた形) ごとに記録する。</summary>
public sealed record StructArrayRule(string Template, string Path, StructMatchMethod Method, string? Key);

/// <summary>
/// 配列ごとの対応付けの方法の記録 (設定 <c>compare.structure.arrayRules</c>)。設定の値は
/// <c>[{"template": "…", "path": "chunks", "method": "key", "key": "type"}, …]</c> の形の JSON の配列。
/// </summary>
public sealed partial class StructArrayRules
{
    /// <summary>設定のキー。</summary>
    public const string SettingKey = "compare.structure.arrayRules";

    private readonly List<StructArrayRule> _rules = [];

    public IReadOnlyList<StructArrayRule> Items => _rules;

    /// <summary>テンプレート <paramref name="template"/> の配列 <paramref name="path"/> の設定 (なければ null)。</summary>
    public StructArrayRule? Find(string template, string path) =>
        _rules.FirstOrDefault(r => string.Equals(r.Template, template, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Path, path, StringComparison.Ordinal));

    /// <summary>設定を加える (同じテンプレート・パスの設定は置き換える)。<see cref="StructMatchMethod.Auto"/> は設定を消す。</summary>
    public void Set(string template, string path, StructMatchMethod method, string? key)
    {
        _rules.RemoveAll(r => string.Equals(r.Template, template, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Path, path, StringComparison.Ordinal));
        if (method != StructMatchMethod.Auto)
        {
            _rules.Add(new StructArrayRule(template, path, method, method == StructMatchMethod.Key ? key : null));
        }
    }

    /// <summary>パスから添字とキーの表示を除いた形 (<c>chunks[3].palette</c> → <c>chunks.palette</c>)。</summary>
    public static string RulePathOf(string path) => IndexPattern().Replace(path, string.Empty);

    public JsonArray ToJson()
    {
        var array = new JsonArray();
        foreach (StructArrayRule r in _rules)
        {
            var o = new JsonObject
            {
                ["template"] = r.Template,
                ["path"] = r.Path,
                ["method"] = r.Method switch { StructMatchMethod.Key => "key", StructMatchMethod.Content => "content", _ => "index" },
            };
            if (r.Key is not null)
            {
                o["key"] = r.Key;
            }

            array.Add(o);
        }

        return array;
    }

    /// <summary>設定の値から読む。形の違う要素は無視する。</summary>
    public static StructArrayRules FromJson(JsonNode? node)
    {
        var rules = new StructArrayRules();
        if (node is not JsonArray array)
        {
            return rules;
        }

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject o || o["template"]?.GetValueKind() != System.Text.Json.JsonValueKind.String
                || o["path"]?.GetValueKind() != System.Text.Json.JsonValueKind.String)
            {
                continue;
            }

            StructMatchMethod? method = (o["method"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)o["method"] : null) switch
            {
                "key" => StructMatchMethod.Key,
                "content" => StructMatchMethod.Content,
                "index" => StructMatchMethod.Index,
                _ => null,
            };
            if (method is null)
            {
                continue;
            }

            string? key = o["key"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?)o["key"] : null;
            if (method == StructMatchMethod.Key && string.IsNullOrEmpty(key))
            {
                continue;
            }

            rules.Set((string)o["template"]!, (string)o["path"]!, method.Value, key);
        }

        return rules;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\s\([^)]*\)")]
    private static partial Regex IndexPattern();
}
