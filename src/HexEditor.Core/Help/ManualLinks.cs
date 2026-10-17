using System.Text.Json;

namespace HexEditor.Core.Help;

/// <summary>
/// マニュアルのページと見出し (09 の UI-59)。<see cref="Page"/> は原稿のファイル名 (docs/manual/&lt;言語&gt;/&lt;Page&gt;.md)、
/// <see cref="Anchor"/> は見出しの ID (言語によらない <c>{#id}</c>。省略ならページの先頭)。
/// </summary>
public sealed record ManualTopic(string Page, string? Anchor = null)
{
    /// <summary>マニュアルの入口 (ヘルプ > ドキュメント。UI-40 の仕様 1)。</summary>
    public static ManualTopic Home { get; } = new("index");

    /// <summary>C# スクリプトのデバッグ (08 の AUTO-22 の受け入れ基準 3)。</summary>
    public static ManualTopic CSharpScriptDebugging { get; } = new("scripting", "debugging-csharp-scripts");

    /// <summary>テンプレートの標準ライブラリのリファレンス (07 の TPL-18 の仕様 4)。</summary>
    public static ManualTopic TemplateLibrary { get; } = new("template-library");

    /// <summary>テンプレートのエラーのコードの説明 (07 の TPL-27 の仕様 7)。見出しはコード (例: HXT1004 → hxt1004)。</summary>
    public static ManualTopic TemplateError(string code) => new("template-error-codes", code.ToLowerInvariant());

    /// <summary>アプリから開く見出し (マニュアルのすべての言語にあることを Core のテストで確かめる)。</summary>
    public static IReadOnlyList<ManualTopic> Linked { get; } = [Home, CSharpScriptDebugging, TemplateLibrary];
}

/// <summary>開くマニュアルのページ。<see cref="IsLocal"/> なら同梱の HTML (file:)、そうでなければオンラインのページ。</summary>
public sealed record ManualLink(Uri Uri, bool IsLocal, string Language);

/// <summary>
/// マニュアルを開く URL の決め方 (09 の UI-59)。オンライン (<see cref="OnlineBase"/>) と同梱 (アプリのフォルダの
/// <c>manual\</c>) は同じ HTML で、構成は <c>&lt;言語&gt;/&lt;ページ&gt;.html#&lt;見出し&gt;</c>。
/// <list type="number">
/// <item>表示言語のマニュアルがあればその言語、なければ英語 (<see cref="DefaultLanguage"/>)。</item>
/// <item>オンライン (オフラインモードでなく、ネットワークにつながっている) ならオンラインのページ、そうでなければ同梱のページ。</item>
/// <item>同梱のページがない (開発中のビルドなど) ときは、オフラインでもオンラインのページ (開く前に URL を確かめる。UI-58 の仕様 3)。</item>
/// </list>
/// </summary>
public static class ManualLinks
{
    /// <summary>GitHub Pages で公開するマニュアル (.github/workflows/pages.yml)。</summary>
    public const string OnlineBase = "https://hiro5791.github.io/HexEditor/";

    public const string DefaultLanguage = "en";

    /// <summary>アプリのフォルダの中の、同梱のマニュアルのフォルダ。</summary>
    public const string BundledFolder = "manual";

    /// <summary>マニュアルの言語とページの一覧 (ManualBuilder が書く)。</summary>
    public const string IndexFile = "manual-index.json";

    /// <summary>同梱のマニュアルの言語の一覧。一覧がなければ英語だけ。</summary>
    public static IReadOnlyList<string> AvailableLanguages(string bundledRoot)
    {
        string path = Path.Combine(bundledRoot, IndexFile);
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("languages", out JsonElement languages) && languages.ValueKind == JsonValueKind.Array)
                {
                    string[] codes = [.. languages.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!)];
                    if (codes.Length > 0)
                    {
                        return codes;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return [DefaultLanguage];
    }

    /// <summary>
    /// 表示言語 <paramref name="uiLanguage"/> (例: <c>ja</c>、<c>zh-Hans</c>、<c>pt-BR</c>) に使うマニュアルの言語。同じものがなければ
    /// 地域を除いた言語 (<c>pt</c>)、それもなければ英語。
    /// </summary>
    public static string ResolveLanguage(string uiLanguage, IReadOnlyCollection<string> available)
    {
        string? exact = available.FirstOrDefault(l => string.Equals(l, uiLanguage, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        int dash = uiLanguage.IndexOf('-');
        if (dash > 0)
        {
            string neutral = uiLanguage[..dash];
            string? match = available.FirstOrDefault(l => string.Equals(l, neutral, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return DefaultLanguage;
    }

    /// <summary>ページの相対パス (<c>ja/scripting.html#debugging-csharp-scripts</c>)。</summary>
    public static string RelativePath(ManualTopic topic, string language) =>
        $"{language}/{topic.Page}.html" + (string.IsNullOrEmpty(topic.Anchor) ? string.Empty : "#" + Uri.EscapeDataString(topic.Anchor));

    public static Uri Online(ManualTopic topic, string language) => new(OnlineBase + RelativePath(topic, language));

    /// <summary>同梱のページのファイル (見出しを除く)。</summary>
    public static string LocalFile(string bundledRoot, ManualTopic topic, string language) =>
        Path.Combine(bundledRoot, language, topic.Page + ".html");

    public static Uri Local(string bundledRoot, ManualTopic topic, string language)
    {
        var uri = new UriBuilder(new Uri(LocalFile(bundledRoot, topic, language))) { Fragment = topic.Anchor ?? string.Empty };
        return uri.Uri;
    }

    /// <summary>
    /// 開くページを決める。<paramref name="online"/> はオフラインモードでなく、ネットワークにつながっていること。
    /// <paramref name="fileExists"/> は同梱のページがあるかの確かめ方 (テストで差し替える)。
    /// </summary>
    public static ManualLink Choose(ManualTopic topic, string uiLanguage, bool online, string bundledRoot, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        string language = ResolveLanguage(uiLanguage, AvailableLanguages(bundledRoot));
        if (!online && fileExists(LocalFile(bundledRoot, topic, language)))
        {
            return new(Local(bundledRoot, topic, language), IsLocal: true, language);
        }

        return new(Online(topic, language), IsLocal: false, language);
    }
}
