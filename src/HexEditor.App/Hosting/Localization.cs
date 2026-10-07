using System.Globalization;

namespace HexEditor.App.Hosting;

/// <summary>表示言語 (UI-43、00-overview 5.1・5.2)。</summary>
public static class Localization
{
    /// <summary>対応する 23 言語。</summary>
    public static readonly string[] SupportedLanguages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    /// <summary>表示言語が右から左に書く言語か (アラビア語・ペルシア語。00-overview 5.4)。</summary>
    public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    /// <summary>
    /// 表示言語を決める。<paramref name="language"/> (--ui-lang) が対応言語ならそのプロセスの間だけ使う。
    /// 指定がなければ Windows の表示言語に従う (リソースの読み込みが自動で選ぶ)。
    /// </summary>
    public static void ApplyLanguageOverride(string? language)
    {
        if (language is null || !SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        CultureInfo.CurrentUICulture = new CultureInfo(language);
    }
}
