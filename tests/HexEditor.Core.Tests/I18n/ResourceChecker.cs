using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HexEditor.Core.Tests.I18n;

/// <summary>
/// 文字列リソースの検査 (UI-42 の仕様 6)。CI で実行し、誤りを 1 行ずつの文で返す。テストからは誤ったデータを渡して、
/// 検出できることも確かめる (TC-UI-42-01)。
/// </summary>
public static partial class ResourceChecker
{
    /// <summary>XAML に書いてよいリテラル (製品名・技術的な名前。翻訳しない)。</summary>
    public static readonly HashSet<string> AllowedLiterals =
    [
        "HexEditor", "ASCII", "UTF-8", "UTF-16 LE", "UTF-16 BE", "UTF-32", "Shift_JIS", "EUC-JP", "EBCDIC", "ANSI", "OEM",
    ];

    /// <summary>XAML で文字列を表すプロパティ。</summary>
    private static readonly string[] TextAttributes =
    [
        "Text", "Content", "Header", "Title", "PlaceholderText", "Label", "Description",
        "ToolTipService.ToolTip", "AutomationProperties.Name", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText",
    ];

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    /// <summary>コードの中のキーの参照: "Key" (文字列リテラル)。</summary>
    [GeneratedRegex(@"""([A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_.]*)""")]
    private static partial Regex CodeKey();

    /// <summary>キーの前方一致での参照: "GoTo_Error_" + 名前。</summary>
    [GeneratedRegex(@"""([A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]*)""\s*\+")]
    private static partial Regex CodeKeyPrefix();

    /// <summary>マニフェストからの参照: ms-resource:Key。</summary>
    [GeneratedRegex(@"ms-resource:([A-Za-z][A-Za-z0-9_]*)")]
    private static partial Regex ManifestKey();

    [GeneratedRegex(@"x:Uid=""([^""]+)""")]
    private static partial Regex XamlUid();

    public static Dictionary<string, string> LoadResw(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);

    /// <summary>言語のキーとプレースホルダーを英語と比べる (キーの不足・古いキーの残り・プレースホルダーの不一致・空の文字列)。</summary>
    public static IEnumerable<string> CompareWithEnglish(string language, IReadOnlyDictionary<string, string> english,
        IReadOnlyDictionary<string, string> translated)
    {
        foreach (string missing in english.Keys.Except(translated.Keys).Order())
        {
            yield return $"{language}: キー {missing} がありません。";
        }

        foreach (string extra in translated.Keys.Except(english.Keys).Order())
        {
            yield return $"{language}: キー {extra} は英語にありません (古いキーの残り)。";
        }

        foreach ((string key, string source) in english)
        {
            if (!translated.TryGetValue(key, out string? value))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"{language}: {key} が空です。";
            }

            var expected = Placeholder().Matches(source).Select(m => m.Value).Order();
            var actual = Placeholder().Matches(value).Select(m => m.Value).Order();
            if (!expected.SequenceEqual(actual))
            {
                yield return $"{language}: {key} のプレースホルダーが英語と違います。";
            }
        }
    }

    /// <summary>コードと XAML から参照されないキー (仕様 6 の 4)。</summary>
    public static IEnumerable<string> UnusedKeys(IEnumerable<string> keys, IEnumerable<string> codeFiles, IEnumerable<string> xamlFiles)
    {
        var exact = new HashSet<string>();
        var prefixes = new List<string>();
        foreach (string code in codeFiles)
        {
            exact.UnionWith(CodeKey().Matches(code).Select(m => m.Groups[1].Value));
            exact.UnionWith(ManifestKey().Matches(code).Select(m => m.Groups[1].Value));
            prefixes.AddRange(CodeKeyPrefix().Matches(code).Select(m => m.Groups[1].Value));
        }

        var uids = xamlFiles.SelectMany(x => XamlUid().Matches(x).Select(m => m.Groups[1].Value)).ToHashSet();
        foreach (string key in keys)
        {
            string uid = key.Split('.')[0];
            bool used = exact.Contains(key) || uids.Contains(uid) || prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));
            if (!used)
            {
                yield return $"キー {key} はどこからも参照されていません。";
            }
        }
    }

    /// <summary>XAML のリテラルの文字列 (x:Uid でリソース化されていない表示文字列。仕様 2)。</summary>
    public static IEnumerable<string> XamlLiterals(string fileName, string xaml)
    {
        XDocument doc = XDocument.Parse(xaml);
        foreach (XElement element in doc.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                string name = attribute.Name.LocalName;
                if (!TextAttributes.Contains(name) || attribute.Name.Namespace != XNamespace.None && name != "Name")
                {
                    continue;
                }

                string value = attribute.Value;
                if (value.StartsWith('{') || AllowedLiterals.Contains(value) || !value.Any(char.IsLetter))
                {
                    continue;
                }

                yield return $"{fileName}: <{element.Name.LocalName} {name}=\"{value}\"> はリソース化されていません (x:Uid を使ってください)。";
            }
        }
    }

    /// <summary>
    /// メニューのアクセスキーの重複 (UI-03 の仕様 5)。<paramref name="menus"/> はメニューごとの項目の x:Uid の一覧
    /// (メニューバー自体も 1 つのメニューとして渡す)。
    /// </summary>
    public static IEnumerable<string> DuplicateAccessKeys(string language, IReadOnlyDictionary<string, string> strings,
        IEnumerable<(string Menu, IReadOnlyList<string> ItemUids)> menus)
    {
        foreach ((string menu, IReadOnlyList<string> items) in menus)
        {
            var keys = items
                .Select(uid => (uid, key: strings.TryGetValue(uid + ".AccessKey", out string? k) ? k.ToUpperInvariant() : null))
                .Where(p => p.key is not null)
                .GroupBy(p => p.key);
            foreach (var group in keys.Where(g => g.Count() > 1))
            {
                yield return $"{language}: {menu} の中でアクセスキー {group.Key} が重複しています: {string.Join(", ", group.Select(p => p.uid))}";
            }
        }
    }

    /// <summary>MainWindow.xaml のメニュー構成 (メニューバーと各メニューの項目の x:Uid)。</summary>
    public static IEnumerable<(string Menu, IReadOnlyList<string> ItemUids)> Menus(string xaml)
    {
        XDocument doc = XDocument.Parse(xaml);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string? Uid(XElement e) => e.Attribute(x + "Uid")?.Value;
        var bars = doc.Descendants().Where(e => e.Name.LocalName == "MenuBar").ToList();
        foreach (XElement bar in bars)
        {
            var top = bar.Elements().Where(e => e.Name.LocalName == "MenuBarItem").ToList();
            yield return ("MenuBar", top.Select(Uid).OfType<string>().ToList());
            foreach (XElement menu in top.Concat(bar.Descendants().Where(e => e.Name.LocalName == "MenuFlyoutSubItem")))
            {
                var items = menu.Elements().Where(e => Uid(e) is not null && e.Name.LocalName != "MenuFlyoutSeparator")
                    .Select(e => Uid(e)!).ToList();
                yield return (Uid(menu) ?? "?", items);
            }
        }
    }

    /// <summary>色の直書き (UI-26 の仕様 4): XAML の #RRGGBB と、コードの Colors.* / Color.FromArgb。</summary>
    public static IEnumerable<string> HardCodedColors(string fileName, string text, bool isXaml)
    {
        Regex pattern = isXaml ? XamlColor() : CodeColor();
        foreach (Match match in pattern.Matches(text))
        {
            yield return $"{fileName}: 色の直書き {match.Value} (ThemeResource を使ってください)。";
        }
    }

    [GeneratedRegex(@"=""#[0-9A-Fa-f]{6,8}""")]
    private static partial Regex XamlColor();

    [GeneratedRegex(@"\bColors\.[A-Z]\w*|\bColor\.FromArgb\(")]
    private static partial Regex CodeColor();
}
