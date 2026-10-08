using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Platform.Localization;

/// <summary>表示言語 1 つ: リソースのフォルダ名と、その言語自身の表記 (09 の UI-43 の仕様 2)。</summary>
public sealed record DisplayLanguage(string Tag, string NativeName);

/// <summary>
/// 表示言語の選択 (00-overview 5.1・5.2、09 の UI-43)。設定 <c>ui.language</c> は <c>system</c> (既定) または 23 言語のフォルダ名。
/// <c>system</c> のときは Windows の表示言語の優先順位を上から見て、23 言語で最初に当たるものを選ぶ。
/// </summary>
public static class DisplayLanguages
{
    public const string SettingKey = "ui.language";
    public const string System = "system";

    /// <summary>23 言語 (00-overview 5.1 の順。一覧の 2 番目以降もこの順に並べる)。</summary>
    public static IReadOnlyList<DisplayLanguage> All { get; } =
    [
        new("en", "English"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("id", "Bahasa Indonesia"),
        new("vi", "Tiếng Việt"),
        new("th", "ไทย"),
        new("de", "Deutsch"),
        new("fr", "Français"),
        new("es", "Español"),
        new("pt", "Português"),
        new("it", "Italiano"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("pl", "Polski"),
        new("cs", "Čeština"),
        new("hu", "Magyar"),
        new("ro", "Română"),
        new("el", "Ελληνικά"),
        new("ar", "العربية"),
        new("tr", "Türkçe"),
        new("fa", "فارسی"),
    ];

    /// <summary>近い言語として使う言語 (00-overview 5.1 の「これを使う」)。</summary>
    private static readonly Dictionary<string, string> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms"] = "id",
        ["be"] = "ru",
        ["kk"] = "ru",
        ["sk"] = "cs",
    };

    public static DisplayLanguage? Find(string tag) => All.FirstOrDefault(l => l.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Windows の言語の 1 つ (BCP-47。例: <c>ms-MY</c>、<c>zh-TW</c>、<c>de-CH</c>) に当たる表示言語。なければ null。
    /// </summary>
    public static string? Match(string windowsLanguage)
    {
        string[] parts = windowsLanguage.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        string primary = parts[0].ToLowerInvariant();
        if (primary == "zh")
        {
            // zh-CN / zh-SG / zh-Hans → 簡体字、zh-TW / zh-HK / zh-MO / zh-Hant → 繁体字。地域だけの zh は簡体字。
            bool traditional = parts.Skip(1).Any(p => p.Equals("Hant", StringComparison.OrdinalIgnoreCase)
                || p.Equals("TW", StringComparison.OrdinalIgnoreCase) || p.Equals("HK", StringComparison.OrdinalIgnoreCase)
                || p.Equals("MO", StringComparison.OrdinalIgnoreCase));
            bool simplified = parts.Skip(1).Any(p => p.Equals("Hans", StringComparison.OrdinalIgnoreCase));
            return traditional && !simplified ? "zh-Hant" : "zh-Hans";
        }

        if (Substitutes.TryGetValue(primary, out string? substitute))
        {
            return substitute;
        }

        return All.FirstOrDefault(l => l.Tag.Equals(primary, StringComparison.OrdinalIgnoreCase))?.Tag;
    }

    /// <summary>Windows の言語の優先順位から選ぶ (UI-43 の仕様 3)。どれにも当たらなければ英語 (00-overview 5.1)。</summary>
    public static string Resolve(IEnumerable<string> windowsLanguages)
    {
        foreach (string language in windowsLanguages)
        {
            if (Match(language) is { } tag)
            {
                return tag;
            }
        }

        return "en";
    }

    /// <summary>設定の値と Windows の言語から、使う表示言語を決める。知らない値は <c>system</c> と同じ。</summary>
    public static string Choose(string? setting, IEnumerable<string> windowsLanguages) =>
        setting is { Length: > 0 } s && !s.Equals(System, StringComparison.OrdinalIgnoreCase) && Find(s) is { } chosen
            ? chosen.Tag
            : Resolve(windowsLanguages);

    /// <summary>
    /// 設定ファイルから <c>ui.language</c> だけを読む (起動処理で、最初のウィンドウを作る前に使う。UI-43 の仕様 4)。読めなければ null。
    /// </summary>
    public static string? ReadSetting(string settingsFolder)
    {
        try
        {
            string path = Path.Combine(settingsFolder, "settings.json");
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?[SettingKey] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
