using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.UITests.Infrastructure;

/// <summary>切れた文字列の数の比較の判定 (UI-47 の仕様 4)。増えても失敗にはせず警告にする。</summary>
public enum TrimVerdict
{
    Ok,
    Warning,
}

/// <summary>
/// 切れた文字列・重なった要素の一覧 (UI-46 の受け入れ基準 2、UI-47 の仕様 3・4)。アプリの textCheck の結果を、
/// リソースキー付きの行にする。キーは表示中の文字列と .resw の値 (プレースホルダーは任意の文字列) を突き合わせて求める。
/// </summary>
public static class TrimReport
{
    private static readonly Dictionary<string, IReadOnlyList<(string Key, Regex Pattern)>> Cache = [];

    /// <summary>qps-ploc (疑似翻訳) のキーと値の照合の一覧。</summary>
    public static IReadOnlyList<(string Key, Regex Pattern)> PseudoKeys() => Keys("qps-ploc");

    /// <summary>言語のキーと値の照合の一覧 (ビルドで作る疑似翻訳を含む)。</summary>
    public static IReadOnlyList<(string Key, Regex Pattern)> Keys(string language)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(language, out IReadOnlyList<(string, Regex)>? keys))
            {
                keys = [.. Load(Folder(language)).Select(kv => (kv.Key, ToPattern(kv.Value)))];
                Cache[language] = keys;
            }

            return keys;
        }
    }

    /// <summary>キーの値 (表示言語と英語) に一致する照合 (ボタンを表示名で探すため)。</summary>
    public static IReadOnlyList<Regex> ValuePatterns(string key, string culture) =>
        [.. new[] { Folder(culture), "en" }.Distinct().Select(Load).Where(d => d.ContainsKey(key)).Select(d => ToPattern(d[key]))];

    /// <summary>textCheck の結果を「画面 大きさ: 種類 "文字列" (キー) 場所」の行にする。</summary>
    public static IEnumerable<string> Describe(JsonObject check, string screen, string size, IReadOnlyList<(string Key, Regex Pattern)> keys)
    {
        foreach (string kind in new[] { "trimmed", "clipped" })
        {
            foreach (JsonNode? item in check[kind]?.AsArray() ?? [])
            {
                string text = item!["text"]!.GetValue<string>();
                yield return $"{screen} {size}: {kind} \"{text}\" ({KeyOf(text, keys)}) at {item["where"]} [{item["width"]?.GetValue<double>():F0}/{item["naturalWidth"]?.GetValue<double>():F0}]";
            }
        }

        foreach (JsonNode? item in check["overlaps"]?.AsArray() ?? [])
        {
            yield return $"{screen} {size}: overlap {item!["a"]} / {item["b"]} ({item["width"]}x{item["height"]})";
        }
    }

    /// <summary>前回の一覧と比べる。増えたら警告 (UI-47 の仕様 4。失敗にはしない)。</summary>
    public static TrimVerdict Compare(IReadOnlyCollection<string> previous, IReadOnlyCollection<string> current) =>
        current.Count > previous.Count || current.Except(previous).Any() ? TrimVerdict.Warning : TrimVerdict.Ok;

    /// <summary>表示中の文字列のリソースキー。見つからなければ "?"。</summary>
    public static string KeyOf(string text, IReadOnlyList<(string Key, Regex Pattern)> keys) =>
        keys.FirstOrDefault(k => k.Pattern.IsMatch(text)).Key ?? "?";

    private static string Folder(string language)
    {
        string strings = Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "Strings");
        if (Directory.Exists(Path.Combine(strings, language)))
        {
            return language;
        }

        string neutral = language.Split('-')[0];
        return Directory.Exists(Path.Combine(strings, neutral)) ? neutral : "en";
    }

    private static Dictionary<string, string> Load(string folder)
    {
        string path = Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.App", "Strings", folder, "Resources.resw");
        return File.Exists(path)
            ? System.Xml.Linq.XDocument.Load(path).Root!.Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value)
            : [];
    }

    /// <summary>.resw の値を、プレースホルダー ({0} など) を任意の文字列とみなす照合にする。</summary>
    private static Regex ToPattern(string value) =>
        new("^" + string.Join(".*", Regex.Split(value, @"\{[^{}]*\}").Select(Regex.Escape)) + "$", RegexOptions.Singleline);
}
