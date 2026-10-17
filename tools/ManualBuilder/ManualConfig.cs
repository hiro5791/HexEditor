using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.ManualBuilder;

/// <summary>
/// マニュアルの構成 (<c>docs/manual/manual.json</c>): 言語の一覧、ページの順序、画面の部品の文言。
/// </summary>
internal sealed class ManualConfig
{
    /// <summary>既定の言語。すべてのページがこの言語で書かれている必要がある。ほかの言語にないページはこの言語の内容で出す。</summary>
    public string DefaultLanguage { get; init; } = "en";

    public List<ManualLanguage> Languages { get; init; } = [];

    /// <summary>ページの ID (ファイル名から .md を除いたもの) の順序。サイドバーと「前へ」「次へ」の順。</summary>
    public List<string> Pages { get; init; } = [];

    /// <summary>言語ごとの画面の部品の文言。ない言語・キーは既定の言語の文言を使う。</summary>
    public Dictionary<string, Dictionary<string, string>> Strings { get; init; } = [];

    public static readonly string[] StringKeys =
    [
        "siteTitle", "skipToContent", "contents", "onThisPage", "language", "previous", "next", "notTranslated", "chooseLanguage",
    ];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ManualConfig Load(string path) =>
        JsonSerializer.Deserialize<ManualConfig>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>文言。言語にないキーは既定の言語、それもなければキーそのもの。</summary>
    public string Text(string language, string key) =>
        Strings.GetValueOrDefault(language)?.GetValueOrDefault(key)
        ?? Strings.GetValueOrDefault(DefaultLanguage)?.GetValueOrDefault(key)
        ?? key;
}

internal sealed class ManualLanguage
{
    /// <summary>言語のコード (アプリの表示言語のフォルダ名と同じ。例: <c>en</c>、<c>ja</c>、<c>zh-Hans</c>)。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>言語の切り替えに出す、その言語での名前 (例: 日本語)。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>右から左に書く言語なら <c>rtl</c>。</summary>
    [JsonPropertyName("dir")]
    public string Direction { get; init; } = "ltr";
}
