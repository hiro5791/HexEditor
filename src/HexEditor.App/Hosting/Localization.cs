using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Platform.Localization;

namespace HexEditor.App.Hosting;

/// <summary>表示言語 (UI-43、00-overview 5.1・5.2)。</summary>
public static class Localization
{
    /// <summary>対応する 23 言語。</summary>
    public static readonly string[] SupportedLanguages = [.. DisplayLanguages.All.Select(l => l.Tag)];

    /// <summary>疑似翻訳 (UI-46)。qps-plocm は右から左に表示する。</summary>
    public static readonly string[] PseudoLanguages = ["qps-ploc", "qps-plocm"];

    private static string? _override;

    /// <summary>表示言語が右から左に書く言語か (アラビア語・ペルシア語、疑似翻訳の qps-plocm。00-overview 5.4)。</summary>
    public static bool IsRightToLeft => _override == "qps-plocm" || CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    /// <summary>使っている表示言語 (リソースのフォルダ名、または疑似翻訳の名前)。翻訳の誤りの報告 (UI-41) に入れる。</summary>
    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// 表示言語を決めて設定する (UI-43 の仕様 3・4)。最初のウィンドウを作る前に呼ぶ。
    /// <paramref name="commandLine"/> (--ui-lang、--pseudo-locale) が対応言語なら、そのプロセスの間だけそれを使う。
    /// なければ設定 <c>ui.language</c>、<c>system</c> (既定) なら Windows の表示言語の優先順位から選ぶ (ms → id などの対応付けを含む)。
    /// <c>Microsoft.Windows.Globalization</c> 版の PrimaryLanguageOverride を使う (Windows.Globalization 版は非パッケージのアプリで落ちることがある)。
    /// </summary>
    public static void ApplyLanguage(string? commandLine, string settingsFolder)
    {
        string? explicitLanguage = commandLine is not null && SupportedLanguages.Concat(PseudoLanguages).Contains(commandLine, StringComparer.OrdinalIgnoreCase)
            ? commandLine
            : null;
        string language = explicitLanguage ?? DisplayLanguages.Choose(DisplayLanguages.ReadSetting(settingsFolder), WindowsLanguages());
        try
        {
            Set(language, explicitLanguage is not null);
        }
        catch (Exception ex)
        {
            // 表示言語を設定できなくても起動は続ける (英語またはリソースの既定で表示される)。
            AppLog.Warning($"Display language {language} could not be set: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// 表示言語を変える (従来の呼び出し。--ui-lang)。対応言語でなければ何もしない。
    /// </summary>
    public static void ApplyLanguageOverride(string? language)
    {
        if (language is not null && SupportedLanguages.Concat(PseudoLanguages).Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            Set(language, explicitChoice: true);
        }
    }

    /// <summary>Windows の表示言語の優先順位 (テスト用のビルドでは --test-hooks の windowsLanguages で差し替えられる)。</summary>
    public static IReadOnlyList<string> WindowsLanguages()
    {
        if (PackagingTestHooks.WindowsLanguages() is { } test)
        {
            return test;
        }

        try
        {
            return [.. Windows.System.UserProfile.GlobalizationPreferences.Languages];
        }
        catch (Exception)
        {
            return [CultureInfo.CurrentUICulture.Name];
        }
    }

    private static void Set(string language, bool explicitChoice)
    {
        CurrentLanguage = DisplayLanguages.Find(language)?.Tag ?? language.ToLowerInvariant();
        _override = language.ToLowerInvariant();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;

        // Windows の言語に従うときは、地域を含む今の UI のカルチャが同じ言語ならそのままにする (例: de-CH)。
        CultureInfo ui = CultureInfo.CurrentUICulture;
        bool sameLanguage = ui.Name.StartsWith(language, StringComparison.OrdinalIgnoreCase)
            || (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && ui.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                && DisplayLanguages.Match(ui.Name) == language);
        if (explicitChoice || !sameLanguage)
        {
            CultureInfo.CurrentUICulture = new CultureInfo(language);
        }
    }
}
