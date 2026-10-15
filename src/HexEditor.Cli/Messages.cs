using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HexEditor.Cli;

/// <summary>
/// コマンドラインのメッセージ (AUTO-36 の仕様 5: メッセージは表示言語に従って訳す。結果の書式・JSON のキーは訳さない)。
/// 文字列は <c>Strings/&lt;言語&gt;/Cli.resw</c> (キーは <c>Cli_</c> で始まる) を実行ファイルに埋め込み、実行時に読む。
/// 訳がない言語・キーは英語にする。
/// </summary>
public sealed partial class Messages
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> All = new(Load);

    private readonly IReadOnlyDictionary<string, string> _language;
    private readonly IReadOnlyDictionary<string, string> _english;

    public Messages(string languageTag)
    {
        Language = languageTag;
        _english = All.Value.TryGetValue("en", out IReadOnlyDictionary<string, string>? en) ? en : new Dictionary<string, string>();
        _language = All.Value.TryGetValue(languageTag, out IReadOnlyDictionary<string, string>? lang) ? lang : _english;
    }

    /// <summary>メッセージの言語 (リソースのフォルダ名)。</summary>
    public string Language { get; }

    /// <summary>リソースを持つ言語 (埋め込まれているもの)。</summary>
    public static IReadOnlyCollection<string> Languages => [.. All.Value.Keys];

    /// <summary>キーの文字列。なければキーそのもの。</summary>
    public string Get(string key) =>
        _language.TryGetValue(key, out string? s) ? s : _english.TryGetValue(key, out s) ? s : key;

    /// <summary>キーの文字列に引数を入れる (<c>{0}</c> の形式。地域設定に依存しない)。</summary>
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, Get(key), args);

    /// <summary>英語の文字列 (テストが期待値を作るため)。</summary>
    public static string English(string key) => All.Value.TryGetValue("en", out IReadOnlyDictionary<string, string>? en) && en.TryGetValue(key, out string? s) ? s : key;

    [GeneratedRegex(@"Strings[./\\]([^./\\]+(?:-[A-Za-z]+)*)[./\\]Cli\.resw$")]
    private static partial Regex ResourceName();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Assembly assembly = typeof(Messages).Assembly;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            Match m = ResourceName().Match(name);
            if (!m.Success)
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            result[m.Groups[1].Value] = Parse(stream);
        }

        return result;
    }

    /// <summary>resw (XML) の <c>data</c> 要素を読む。</summary>
    internal static IReadOnlyDictionary<string, string> Parse(Stream stream)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        XDocument doc = XDocument.Load(stream);
        foreach (XElement data in doc.Root!.Elements("data"))
        {
            if (data.Attribute("name")?.Value is { } key && data.Element("value")?.Value is { } value)
            {
                map[key] = value;
            }
        }

        return map;
    }
}
