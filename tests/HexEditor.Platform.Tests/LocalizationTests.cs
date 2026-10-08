using HexEditor.Platform.Localization;
using HexEditor.Platform.Network;
using HexEditor.Platform.Shell;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>表示言語の選択 (09 の UI-43)、翻訳の誤りの報告の URL (UI-41)、ジャンプリスト (UI-35)、ネットワークの管理 (UI-58)。</summary>
public sealed class LocalizationTests
{
    [Fact]
    [Trait(TC, "TC-UI-43-01")]
    public void Malay_windows_uses_indonesian()
    {
        Assert.Equal("id", DisplayLanguages.Resolve(["ms-MY", "en-US"]));
        Assert.Equal("id", DisplayLanguages.Choose("system", ["ms-MY", "en-US"]));
    }

    [Fact]
    [Trait(TC, "TC-UI-43-02")]
    public void Windows_language_outside_the_23_languages_uses_english()
    {
        Assert.Equal("en", DisplayLanguages.Resolve(["sw-KE"]));
        Assert.Equal("en", DisplayLanguages.Resolve([]));
    }

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-Hant-MO", "zh-Hant")]
    [InlineData("be-BY", "ru")]
    [InlineData("kk-KZ", "ru")]
    [InlineData("sk-SK", "cs")]
    [InlineData("de-CH", "de")]
    [InlineData("pt-PT", "pt")]
    [InlineData("fa-IR", "fa")]
    [InlineData("tr", "tr")]
    public void Windows_languages_map_to_the_display_languages(string windows, string expected) =>
        Assert.Equal(expected, DisplayLanguages.Match(windows));

    [Fact]
    public void Explicit_setting_wins_and_unknown_values_follow_windows()
    {
        Assert.Equal("ja", DisplayLanguages.Choose("ja", ["de-DE"]));
        Assert.Equal("de", DisplayLanguages.Choose("klingon", ["de-DE"]));
        Assert.Equal("de", DisplayLanguages.Choose(null, ["xx", "de-DE"]));
    }

    [Fact]
    [Trait(TC, "TC-UI-43-03")]
    public void The_list_names_each_language_in_that_language_in_the_specified_order()
    {
        string[] expected =
        [
            "English", "简体中文", "繁體中文", "日本語", "한국어", "Bahasa Indonesia", "Tiếng Việt", "ไทย", "Deutsch", "Français",
            "Español", "Português", "Italiano", "Русский", "Українська", "Polski", "Čeština", "Magyar", "Română", "Ελληνικά",
            "العربية", "Türkçe", "فارسی",
        ];
        Assert.Equal(expected, DisplayLanguages.All.Select(l => l.NativeName));

        // フォルダ名はアプリの Strings と 00-overview 5.1 の表と同じ。
        string[] folders = [.. Directory.GetDirectories(RepoFile("src/HexEditor.App/Strings")).Select(Path.GetFileName)!];
        Assert.All(DisplayLanguages.All, l => Assert.Contains(l.Tag, folders));
    }

    [Fact]
    public void Language_setting_is_read_before_the_window_exists()
    {
        using var temp = new Support.TempFolder();
        Assert.Null(DisplayLanguages.ReadSetting(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), """{ "$schemaVersion": 1, "ui.language": "ja" }""");
        Assert.Equal("ja", DisplayLanguages.ReadSetting(temp.Path));
        File.WriteAllText(Path.Combine(temp.Path, "settings.json"), "{ broken");
        Assert.Null(DisplayLanguages.ReadSetting(temp.Path));
    }

    [Fact]
    public void Coverage_file_is_read_and_clamped()
    {
        IReadOnlyDictionary<string, LanguageCoverage> c = TranslationCoverage.Parse("""{ "languages": { "de": { "translated": 100, "reviewed": 87 }, "fr": { "translated": 120 } } }""");
        Assert.Equal(new LanguageCoverage("de", 100, 87), c["de"]);
        Assert.Equal(new LanguageCoverage("fr", 100, 0), c["fr"]);
    }

    // ---- UI-41 翻訳の誤りを報告 ----

    [Fact]
    public void Translation_report_url_fills_the_form_fields()
    {
        Uri url = TranslationReport.IssueUrl("de", "Menu_File.Title", "Datei & mehr", "1.2.0");
        Assert.StartsWith("https://github.com/hiro5791/HexEditor/issues/new?", url.AbsoluteUri);
        IReadOnlyDictionary<string, string> q = TranslationReport.ParseQuery(url);
        Assert.Equal("translation.yml", q["template"]);
        Assert.Equal("de", q["language"]);
        Assert.Equal("Menu_File.Title", q["key"]);
        Assert.Equal("Datei & mehr", q["current"]);
        Assert.Equal("1.2.0", q["version"]);

        // 「選ばずに報告」: 表示言語と版だけ。
        IReadOnlyDictionary<string, string> none = TranslationReport.ParseQuery(TranslationReport.IssueUrl("en", null, null, "1.2.0"));
        Assert.False(none.ContainsKey("key"));
        Assert.False(none.ContainsKey("current"));
        Assert.Equal("en", none["language"]);
    }

    [Fact]
    public void Issue_form_has_the_fields_that_the_app_fills()
    {
        string form = File.ReadAllText(RepoFile(".github/ISSUE_TEMPLATE/translation.yml"));
        foreach (string id in new[] { "language", "key", "current", "version", "correct" })
        {
            Assert.Contains($"id: {id}", form);
        }

        // .resw を直す Pull Request の手順へのリンク (UI-41 の仕様 4)。
        Assert.Contains("CONTRIBUTING.md", form);
        Assert.True(File.Exists(RepoFile("CONTRIBUTING.md")));
    }

    [Fact]
    public void Report_dialog_search_matches_key_source_or_translation_ignoring_case()
    {
        (string Key, string En, string Cur)[] items = [("Menu_File.Title", "File", "Datei"), ("Menu_Edit.Title", "Edit", "Bearbeiten")];
        Assert.Single(TranslationReport.Filter(items, "menu_file", i => i.Key, i => i.En, i => i.Cur));
        Assert.Single(TranslationReport.Filter(items, "DATEI", i => i.Key, i => i.En, i => i.Cur));
        Assert.Equal(2, TranslationReport.Filter(items, " ", i => i.Key, i => i.En, i => i.Cur).Count());
        Assert.Equal("Menu_File.Title", TranslationReport.KeyFromResourceName("Menu_File/Title"));
    }

    // ---- UI-35 ジャンプリスト ----

    [Fact]
    public void Jump_list_has_pinned_recent_up_to_10_and_tasks()
    {
        var source = new FakeRecent(
            pinned: [new RecentFileEntry(@"C:\p.bin")],
            recent: [.. Enumerable.Range(1, 15).Select(i => new RecentFileEntry($@"C:\f{i}.bin")), new RecentFileEntry(@"C:\P.BIN")]);
        IReadOnlyList<JumpListItem> items = JumpListPlan.Build(source, id => "T:" + id);
        Assert.Equal(@"C:\p.bin", Assert.Single(items, i => i.Category == JumpListCategory.Pinned).FilePath);
        List<JumpListItem> recent = [.. items.Where(i => i.Category == JumpListCategory.Recent)];
        Assert.Equal(10, recent.Count);
        Assert.Equal(@"C:\f1.bin", recent[0].FilePath);
        Assert.Equal("\"C:\\f1.bin\"", recent[0].Arguments);
        Assert.Equal(["--new-window", "--new-document"], items.Where(i => i.Category == JumpListCategory.Tasks).Select(i => i.Arguments));
        Assert.Equal("T:NewWindow", items.First(i => i.Category == JumpListCategory.Tasks).Title);
    }

    [Fact]
    public void Jump_list_arguments_parse_back_to_the_file_and_the_options()
    {
        Assert.Equal([@"C:\a b\x.bin"], CommandLine.ParseString(JumpListPlan.Quote(@"C:\a b\x.bin")).Files);
        Assert.True(CommandLine.ParseString("--new-window").NewWindow);
        Assert.True(CommandLine.ParseString("--new-document").NewDocument);
    }

    [Fact]
    public void Session_recent_files_keep_the_newest_first_without_duplicates()
    {
        var recent = new SessionRecentFiles();
        int changes = 0;
        recent.Changed += (_, _) => changes++;
        recent.Add(@"C:\a.bin");
        recent.Add(@"C:\b.bin");
        recent.Add(@"C:\A.bin");
        Assert.Equal([@"C:\A.bin", @"C:\b.bin"], recent.Recent.Select(r => r.Path));
        Assert.Equal(3, changes);
    }

    // ---- UI-58 ネットワークを使う機能の管理 ----

    [Fact]
    public void Offline_mode_disables_updates_and_downloads_but_only_asks_before_opening_pages()
    {
        var settings = new MemorySettings();
        var policy = new NetworkPolicy(settings);
        Assert.True(policy.Allows(NetworkFeature.Updates));
        Assert.False(policy.ConfirmBeforeOpening(translationReport: false));
        settings.SetBool(NetworkPolicy.OfflineKey, true);
        Assert.False(policy.Allows(NetworkFeature.Updates));
        Assert.False(policy.Allows(NetworkFeature.Downloads));
        Assert.False(policy.Allows(NetworkFeature.Components));
        Assert.True(policy.Allows(NetworkFeature.BrowserPages));
        Assert.True(policy.ConfirmBeforeOpening(translationReport: false));
        Assert.Throws<NetworkDisabledException>(() => new NetworkClient(policy, "1.0.0").EnsureAllowed(NetworkFeature.Updates));

        settings.SetBool(NetworkPolicy.OfflineKey, false);
        settings.SetBool(NetworkPolicy.TranslationReportEnabledKey, false);
        Assert.True(policy.ConfirmBeforeOpening(translationReport: true));
        Assert.False(policy.ConfirmBeforeOpening(translationReport: false));
        settings.SetBool(NetworkPolicy.DownloadsEnabledKey, false);
        Assert.False(policy.DownloadsAllowed);
        Assert.True(policy.UpdateCheckAllowed(manual: true));
    }

    private sealed class FakeRecent(IReadOnlyList<RecentFileEntry> pinned, IReadOnlyList<RecentFileEntry> recent) : IRecentFilesSource
    {
        public IReadOnlyList<RecentFileEntry> Pinned => pinned;

        public IReadOnlyList<RecentFileEntry> Recent => recent;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
