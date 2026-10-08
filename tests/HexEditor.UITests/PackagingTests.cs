using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 配布形態に関わる機能の画面: 更新の確認と InfoBar (10 の PKG-17〜PKG-22)、ネットワークを使う機能の管理 (09 の UI-58)、
/// 翻訳の誤りの報告 (UI-41)、表示言語 (UI-43)、ジャンプリスト (UI-35)。
/// 更新の配布元は 127.0.0.1 の偽物 (<see cref="FakeUpdateFeed"/>)。ブラウザは開かず、開こうとした URL をログで確かめる
/// (テスト方針 7.2「外部の起動の記録」)。Windows の言語の設定は変えず、--test-hooks の windowsLanguages で差し替える。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PackagingTests
{
    internal static async Task<string> ProfileWithSettings(UiTestContext ctx, string json)
    {
        string profile = ctx.NewProfile();
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), json);
        return profile;
    }

    internal static async Task<JsonObject> MenuItemAsync(AppSession app, string id) =>
        await app.SendAsync("menuItem", new JsonObject { ["id"] = id });

    private static async Task<string> LaunchedUrlAsync(AppSession app) =>
        (await app.WaitForLogAsync(l => l.Contains("Test hooks: launch ", StringComparison.Ordinal), "the browser launch"))
            .Split("Test hooks: launch ", 2)[1].Trim();

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty);

    // ---- UI-58 ネットワークを使う機能の管理 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-58-01")]
    public Task Offline_mode_disables_check_for_updates_with_the_reason() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject online = await MenuItemAsync(app, "Command_CheckForUpdates");
        Assert.True(online["enabled"]!.GetValue<bool>());

        // 設定画面の「プライバシー」のスイッチと同じく、設定 network.offline を true にする (外部の編集は 1 秒以内に反映される)。
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"network.offline\": true}");
        await app.WaitUntilAsync(async () => !(await MenuItemAsync(app, "Command_CheckForUpdates"))["enabled"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(10), "the menu item to be disabled");
        JsonObject offline = await MenuItemAsync(app, "Command_CheckForUpdates");
        Assert.Equal("Offline mode is on", offline["reason"]!.GetValue<string>());
        Assert.Equal("Offline mode is on", offline["acceleratorText"]!.GetValue<string>());
    });

    [Fact]
    public Task Offline_mode_shows_the_url_before_opening_the_documentation() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = await ProfileWithSettings(ctx, "{\"$schemaVersion\": 1, \"network.offline\": true}") });
        await app.CommandAsync("Command_Documentation");
        AutomationElement dialog = await app.WaitForDialogAsync("OpenUrlDialog");
        Assert.NotNull(dialog);
        Assert.Contains("github.com/hiro5791/HexEditor", (await app.WaitForAsync("OpenUrlDialog_Url")).Patterns.Value.Pattern.Value.Value);
        await app.InvokeDialogButtonAsync("Cancel");
        await Task.Delay(500);
        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains("Test hooks: launch ", StringComparison.Ordinal));
    });

    // ---- 更新の確認と InfoBar (PKG-17、PKG-20、PKG-22) ----

    [Fact]
    public Task Manual_check_shows_up_to_date_or_the_new_version_with_its_buttons() => UiTestContext.RunAsync(async ctx =>
    {
        await using var feed = new FakeUpdateFeed();
        string profile = await ProfileWithSettings(ctx, $"{{\"$schemaVersion\": 1, \"test.update.source\": \"{feed.RepositoryUrl}\"}}");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 配布元にリリースがない (開発中の版 0.0.0-local より新しいものがない): 「最新の版です」、ボタンなし、8 秒で閉じる。
        JsonObject state = await app.SendAsync("updateCheck", new JsonObject { ["manual"] = true });
        Assert.Equal("Update_UpToDate", state["messageKey"]!.GetValue<string>());
        Assert.Equal("You're up to date (version 0.0.0-local).", state["message"]!.GetValue<string>());
        Assert.Empty(state["buttons"]!.AsArray());
        await app.WaitUntilAsync(async () => !(await app.SendAsync("updateState"))["barVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(12), "the bar to close");

        // 新しい版がある: 「版 0.9.1 があります」と、ダウンロードページ・リリースノート・この版をスキップ。
        feed.Versions.Add("0.9.1");
        state = await app.SendAsync("updateCheck", new JsonObject { ["manual"] = true });
        Assert.Equal("Version 0.9.1 is available.", state["message"]!.GetValue<string>());
        Assert.Equal(["OpenDownloadPage", "ReleaseNotes", "Skip"], state["buttons"]!.AsArray().Select(b => b!["kind"]!.GetValue<string>()));
        Assert.True(await app.IsShownAsync("UpdateBar"));
        await app.SendAsync("updateButton", new JsonObject { ["button"] = "OpenDownloadPage" });
        Assert.Equal($"{feed.RepositoryUrl}/releases/tag/v0.9.1", await LaunchedUrlAsync(app));

        // この版をスキップ: 設定に記録され、自動の確認では出ない。
        await app.SendAsync("updateButton", new JsonObject { ["button"] = "Skip" });
        await app.WaitUntilAsync(() => Task.FromResult(File.ReadAllText(Path.Combine(profile, "settings.json")).Contains("\"update.skippedVersion\": \"0.9.1\"")),
            TimeSpan.FromSeconds(5), "the skipped version in settings.json");
        state = await app.SendAsync("updateCheck", new JsonObject { ["manual"] = false });
        Assert.False(state["barVisible"]!.GetValue<bool>());
    });

    [Fact]
    public Task Automatic_check_runs_30_seconds_after_start_and_not_before() => UiTestContext.RunAsync(async ctx =>
    {
        await using var feed = new FakeUpdateFeed("0.9.1");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = await ProfileWithSettings(ctx, $"{{\"$schemaVersion\": 1, \"test.update.source\": \"{feed.RepositoryUrl}\"}}"),
        });
        var started = Stopwatch.StartNew();
        await app.WaitUntilAsync(async () => (await app.SendAsync("updateState"))["barVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(60), "the automatic check");
        Assert.True(started.Elapsed > TimeSpan.FromSeconds(24), $"checked after {started.Elapsed.TotalSeconds:0} s");
        Assert.Equal(1, feed.Requests);
    });

    // ---- UI-41 翻訳の誤りを報告 ----

    [Theory]
    [Trait(UiTest.TC, "TC-UI-41-01")]
    [Trait(UiTest.TC, "TC-UI-41-02")]
    [InlineData("de")]
    [InlineData("en")]
    public Task Report_a_translation_error_opens_the_form_with_the_string(string language) => UiTestContext.RunAsync(async ctx =>
    {
        Dictionary<string, string> resw = UiHelpers.LoadResw(language);
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = language });
        await app.CommandAsync("Command_ReportTranslation");
        await app.WaitForDialogAsync("TranslationReportDialog");
        await app.UiaSetValueAsync("TranslationReport_Search", "Menu_File");
        AutomationElement list = await app.WaitForAsync("TranslationReport_List");
        AutomationElement? item = null;
        await app.WaitUntilAsync(() => Task.FromResult((item = list.FindAllChildren().FirstOrDefault(i => AppSession.NameOf(i) == "Menu_File.Title")) is not null),
            TimeSpan.FromSeconds(10), "the Menu_File.Title item");
        item!.Patterns.SelectionItem.Pattern.Select();
        await app.WaitUntilAsync(() => Task.FromResult(app.Find("PrimaryButton")?.IsEnabled == true), TimeSpan.FromSeconds(5), "the Report button");
        (await app.WaitForAsync("PrimaryButton")).Patterns.Invoke.Pattern.Invoke();

        string url = await LaunchedUrlAsync(app);
        Assert.StartsWith("https://github.com/hiro5791/HexEditor/issues/new?", url);
        Dictionary<string, string> q = Query(url);
        Assert.Equal("translation.yml", q["template"]);
        Assert.Equal(language, q["language"]);
        Assert.Equal("Menu_File.Title", q["key"]);
        Assert.Equal(resw["Menu_File.Title"], q["current"]);
        Assert.False(string.IsNullOrEmpty(q["version"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-41-03")]
    public Task Showing_string_keys_puts_the_resource_key_in_the_menu_tooltips() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = await ProfileWithSettings(ctx, "{\"$schemaVersion\": 1, \"i18n.showStringKeys\": true}") });
        Assert.Equal("Menu_File_Open.Text", (await MenuItemAsync(app, "Command_Open"))["toolTip"]!.GetValue<string>());

        AppSession plain = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile() });
        Assert.Null((await MenuItemAsync(plain, "Command_Open"))["toolTip"]);
    });

    // ---- UI-43 表示言語の選択 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-43-01")]
    public Task Malay_windows_shows_indonesian() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = null,
            Hooks = new JsonObject { ["windowsLanguages"] = new JsonArray("ms-MY", "en-US") },
        });
        Assert.Equal(UiHelpers.LoadResw("id")["Menu_File.Title"], await FileMenuTitleAsync(app));
        JsonObject language = await app.SendAsync("displayLanguage");
        Assert.Equal("id", language["language"]!.GetValue<string>());
        Assert.Contains("Bahasa Indonesia", language["items"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("system", language["items"]![0]!["tag"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-43-02")]
    public Task Windows_language_outside_the_23_languages_shows_english() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = null, Hooks = new JsonObject { ["windowsLanguages"] = new JsonArray("sw-KE") } });
        Assert.Equal("File", await FileMenuTitleAsync(app));
    });

    [Theory]
    [Trait(UiTest.TC, "TC-UI-43-03")]
    [InlineData("en", "German")]
    [InlineData("ja", "ドイツ語")]
    public Task Language_list_names_each_language_in_itself(string uiLanguage, string germanLocalName) => UiTestContext.RunAsync(async ctx =>
    {
        string[] native =
        [
            "English", "简体中文", "繁體中文", "日本語", "한국어", "Bahasa Indonesia", "Tiếng Việt", "ไทย", "Deutsch", "Français",
            "Español", "Português", "Italiano", "Русский", "Українська", "Polski", "Čeština", "Magyar", "Română", "Ελληνικά",
            "العربية", "Türkçe", "فارسی",
        ];
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = uiLanguage });
        JsonArray items = (await app.SendAsync("displayLanguage"))["items"]!.AsArray();
        Assert.Equal(24, items.Count);
        for (int i = 0; i < native.Length; i++)
        {
            Assert.StartsWith(native[i] + " (", items[i + 1]!["text"]!.GetValue<string>());
        }

        // 各項目の横に、今の表示言語での言語名と「確認済み N%」がある。
        string german = items[9]!["text"]!.GetValue<string>();
        Assert.Contains(germanLocalName, german);
        Assert.Matches(uiLanguage == "ja" ? @"確認済み \d+%" : @"reviewed \d+%", german);

        // 仮の画面 (表示 > 表示言語) の一覧も同じ。
        await app.CommandAsync("Command_DisplayLanguage");
        await app.WaitForDialogAsync("LanguageDialog");
        await app.InvokeDialogButtonAsync(uiLanguage == "ja" ? "キャンセル" : "Cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-43-04")]
    public Task Restart_now_restarts_in_the_new_language_with_the_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        // 英語の Windows と同じ状態で起動する (この PC の言語の設定は変えない)。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = profile,
            UiLanguage = null,
            Files = [seq, bytes],
            Hooks = new JsonObject { ["windowsLanguages"] = new JsonArray("en-US") },
        });
        await app.WaitForTabsAsync(2);
        await app.SendAsync("setDisplayLanguage", new JsonObject { ["language"] = "ja" });
        DateTime since = DateTime.Now;
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Restart now" });
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        Process? restarted = null;
        try
        {
            // 古いプロセスは終わっているので、そのセッションの待ち方 (終了で失敗する) は使わない。
            var wait = Stopwatch.StartNew();
            while ((restarted = Process.GetProcessesByName("HexEditor")
                .FirstOrDefault(p => p.Id != app.Pid && StartedAfter(p, since) && SamePath(p, AppLocator.ExePath))) is null)
            {
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(30), "the app did not restart");
                await Task.Delay(200);
            }

            AppSession again = await ctx.AttachAsync(restarted!, profile);
            restarted = null;
            await again.WaitForTabsAsync(2);
            Assert.Equal([Path.GetFileName(seq), Path.GetFileName(bytes)], (await again.TabNamesAsync()).Select(Path.GetFileName));
            Assert.Equal(UiHelpers.LoadResw("ja")["Menu_File.Title"], await FileMenuTitleAsync(again));
        }
        finally
        {
            // つなぐ前に失敗したら、再起動したプロセスを残さない。
            restarted?.Kill();
        }
    });

    // ---- UI-35 ジャンプリスト ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-35-01")]
    public Task Recent_files_appear_in_the_jump_list_and_open_in_the_existing_window() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        DateTime testStart = DateTime.Now;
        // 2 つ目の起動を受け取れるよう、単一インスタンスとして起動する (--new-instance を付けない)。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, NewInstance = false });
        await app.OpenAsync(seq);
        await app.CommandAsync("Command_Close");
        await app.OpenAsync(bytes);
        await Task.Delay(1000);

        // ジャンプリストの「最近使ったもの」に TD-SEQ-1M がある (テスト用のビルドは Windows のジャンプリストを変えず、内容を記録する)。
        JsonArray items = (await app.SendAsync("jumpList"))["items"]!.AsArray();
        JsonNode recent = items.First(i => i!["category"]!.GetValue<string>() == "Recent" && i["path"]?.GetValue<string>() == seq)!;
        Assert.Equal(Path.GetFileName(seq), recent["title"]!.GetValue<string>());
        Assert.Contains(items, i => i!["category"]!.GetValue<string>() == "Tasks" && i["arguments"]!.GetValue<string>() == "--new-window");

        // ジャンプリストの項目を選んだときと同じ引数で起動する: 既存のプロセスに転送され、新しいタブで開く (UI-15)。
        int exit = await ctx.LaunchAndWaitAsync(new AppOptions { Profile = profile, NewInstance = false, ExtraArgs = [recent["arguments"]!.GetValue<string>().Trim('"')] },
            TimeSpan.FromSeconds(30));
        Assert.Equal(0, exit);
        await app.WaitForTabsAsync(2);
        Assert.Contains(Path.GetFileName(seq), (await app.TabNamesAsync()).Select(Path.GetFileName));
        Assert.Equal(app.Pid, Assert.Single(Process.GetProcessesByName("HexEditor"), p => SamePath(p, AppLocator.ExePath) && StartedAfter(p, testStart)).Id);
    });

    [Fact(Skip = "The jump list task --new-window needs multiple windows in one process (UI-14), which is not implemented yet.")]
    [Trait(UiTest.TC, "TC-UI-35-02")]
    public Task Jump_list_new_window_opens_a_second_window_in_the_same_process() => Task.CompletedTask;

    private static async Task<string> FileMenuTitleAsync(AppSession app) =>
        (await app.SendAsync("menuTexts"))["items"]!.AsArray().First(i => i!["menu"]?.GetValue<bool>() == true)!["text"]!.GetValue<string>();

    private static bool StartedAfter(Process p, DateTime since)
    {
        try
        {
            return p.StartTime >= since.AddSeconds(-1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SamePath(Process p, string exe)
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>プロセスの TCP の接続の相手 (IPv4・IPv6)。GetExtendedTcpTable (TCP_TABLE_OWNER_PID_ALL)。</summary>
    internal static class TcpConnections
    {
        public static IEnumerable<IPAddress> Remote(int pid) => Table(2, pid).Concat(Table(23, pid));

        private static List<IPAddress> Table(int family, int pid)
        {
            var result = new List<IPAddress>();
            int size = 0;
            _ = GetExtendedTcpTable(0, ref size, false, family, 5, 0);
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 5, 0) != 0)
                {
                    return result;
                }

                int count = Marshal.ReadInt32(buffer);
                int rowSize = family == 2 ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    nint row = buffer + 4 + (i * rowSize);
                    if (family == 2)
                    {
                        if (Marshal.ReadInt32(row + 20) == pid)
                        {
                            result.Add(new IPAddress((uint)Marshal.ReadInt32(row + 12)));
                        }
                    }
                    else if (Marshal.ReadInt32(row + 52) == pid)
                    {
                        byte[] address = new byte[16];
                        Marshal.Copy(row + 24, address, 0, 16);
                        result.Add(new IPAddress(address));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, int reserved);
    }
}

/// <summary>毎晩だけ実行する長いテスト (UI テストの通常の実行 Category=UI には入れない)。</summary>
[Trait(UiTest.Category, "Nightly")]
public sealed class PackagingNightlyTests
{
    /// <summary>
    /// TC-UI-58-02: オフラインモードで台本を繰り返しても、外部への通信がない。台本の長さは HEXEDITOR_OFFLINE_SCRIPT_MINUTES (既定 60 分)。
    /// 通信の確認は、アプリの通信の記録 (すべての通信の入口) と、プロセスの TCP の接続の一覧 (GetExtendedTcpTable、1 秒ごと) で行う。
    /// DNS の問い合わせは Windows の DNS クライアントのサービスが行うため、ETW を使える毎晩の CI の計測機でだけ確かめる (この試験では確かめない)。
    /// </summary>
    [Fact]
    [Trait(UiTest.TC, "TC-UI-58-02")]
    public Task An_hour_of_work_in_offline_mode_makes_no_external_connection() => UiTestContext.RunAsync(async ctx =>
    {
        double minutes = double.TryParse(Environment.GetEnvironmentVariable("HEXEDITOR_OFFLINE_SCRIPT_MINUTES"), out double m) ? m : 60;
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = await PackagingTests.ProfileWithSettings(ctx, "{\"$schemaVersion\": 1, \"network.offline\": true}") });
        var external = new List<string>();
        using var stop = new CancellationTokenSource();
        Task monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                external.AddRange(PackagingTests.TcpConnections.Remote(app.Pid).Where(a => !IPAddress.IsLoopback(a)).Select(a => a.ToString()));
                await Task.Delay(1000);
            }
        });

        var clock = Stopwatch.StartNew();
        int round = 0;
        while (clock.Elapsed < TimeSpan.FromMinutes(minutes))
        {
            round++;
            string copy = ctx.CopyTestData(round % 2 == 0 ? "TD-SEQ-1M" : "TD-RANDOM-16M", $"work-{round}.bin");
            await app.OpenAsync(copy);
            await app.IdleAsync();
            await app.SendAsync("click", new JsonObject { ["offset"] = 16, ["column"] = "Hex" });
            await app.TypeAsync("AB");
            await app.KeyAsync("S", ctrl: true);
            await app.IdleAsync();

            // ヘルプ > 更新の確認 は無効表示 (押しても通信しない)。ドキュメントは URL を表示して「キャンセル」。
            Assert.False((await PackagingTests.MenuItemAsync(app, "Command_CheckForUpdates"))["enabled"]!.GetValue<bool>());
            await app.CommandAsync("Command_Documentation");
            await app.WaitForDialogAsync("OpenUrlDialog");
            await app.InvokeDialogButtonAsync("Cancel");
            await app.SendAsync("advanceUpdateClock", new JsonObject { ["hours"] = 24.02 });
            await app.CommandAsync("Command_Close");
            await app.IdleAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        stop.Cancel();
        await monitor;
        Assert.Empty((await app.SendAsync("networkLog"))["requests"]!.AsArray());
        Assert.True(external.Count == 0, "external connections: " + string.Join(", ", external.Distinct()));
    });
}
