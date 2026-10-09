using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>設定画面 (UI-22)、設定の保存 (UI-23)、リセット (UI-24)、インポート / エクスポート (UI-25)、パネル (UI-05)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SettingsUiTests
{
    private static async Task<JsonObject> CardAsync(AppSession app, string key, JsonObject? request = null) =>
        (await app.SendAsync("settingsPage", request))["cards"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["key"]!.GetValue<string>() == key);

    // ---- UI-22 設定画面 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-22-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Settings_open_only_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1. Ctrl+, (VK_OEM_COMMA = 188)。
        Assert.Equal("menu:Command_Settings", (await app.KeyAsync("188", ctrl: true))["handledBy"]!.GetValue<string>());
        JsonObject state = await app.SendAsync("settingsPage");
        Assert.Equal(["settings"], state["pages"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("settings", state["active"]!.GetValue<string>());

        // 2. 設定画面はタブ列のタブとして開いている (モーダルダイアログではない)。
        JsonObject tabs = await TabTests.TabsAsync(app);
        Assert.Equal(["settings"], tabs["toolTabs"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("tool:settings", tabs["stripSelected"]!.GetValue<string>());

        // 3. 文書のタブに切り替え、もう一度 Ctrl+,。
        Assert.Null((await app.SendAsync("showDocument"))["active"]?.GetValue<string>());
        Assert.Equal("TD-SEQ-1M.bin", (await TabTests.TabsAsync(app))["stripSelected"]!.GetValue<string>());
        await app.KeyAsync("188", ctrl: true);
        state = await app.SendAsync("settingsPage");
        Assert.Equal(["settings"], state["pages"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("settings", state["active"]!.GetValue<string>());

        // 4. 設定のタブは 1 つだけで、アクティブ。
        tabs = await TabTests.TabsAsync(app);
        Assert.Equal(["settings"], tabs["toolTabs"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal("tool:settings", tabs["stripSelected"]!.GetValue<string>());

        // 別のウィンドウで Ctrl+, を押しても、2 つ目は開かない (アプリ全体で 1 つ)。
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.SendAsync("key", new JsonObject { ["window"] = 1, ["key"] = "188", ["ctrl"] = true });
        Assert.Empty((await TabTests.TabsAsync(app, 1))["toolTabs"]!.AsArray());
        Assert.Equal(["settings"], (await TabTests.TabsAsync(app, 0))["toolTabs"]!.AsArray().Select(p => p!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-22-02")]
    public Task Theme_change_applies_while_settings_stay_open() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: 2 つのウィンドウを開き、ウィンドウ 1 で設定画面を開いた。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "ui.theme": "light" }""") });
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.SendAsync("settingsPage", new JsonObject { ["window"] = 0, ["category"] = "appearance" });

        // 1〜2. テーマを「ダーク」にすると、1 秒以内に両方のウィンドウがダークテーマになる。時間はアプリの中で測る (変更を始めた時刻から
        // 各ウィンドウのテーマが変わった時刻まで)。命令の往復の時間は含めず、状態の確認は倍率を掛けた上限まで待つ。
        JsonObject set = await app.SendAsync("settingSet", new JsonObject { ["window"] = 0, ["key"] = "ui.theme", ["value"] = "dark" });
        Assert.True(set["valid"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await WindowManagementTests.WindowsAsync(app)).All(w => w!["theme"]!.GetValue<string>() == "Dark"),
            UiTest.Scaled(TimeSpan.FromSeconds(5)), "the dark theme in both windows");
        double at = set["atMs"]!.GetValue<double>();
        double[] took = [.. (await WindowManagementTests.WindowsAsync(app)).Select(w => w!["themeChangedAtMs"]!.GetValue<double>() - at)];
        Assert.True(took.All(t => t < 1000), $"{string.Join(", ", took.Select(t => $"{t:F0} ms"))}");
        Assert.Equal("settings", (await app.SendAsync("settingsPage", new JsonObject { ["window"] = 0 }))["active"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-22-03")]
    public Task Search_and_modified_mark() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ja" });

        // 1〜2. 英語の名前でも見つかる。ズームの項目 (UI-08) は後半で登録するので、代わりにテーマ (theme) で確かめる。
        JsonArray cards = (await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["search"] = "theme" }))["cards"]!.AsArray();
        Assert.Contains(cards, c => c!["key"]!.GetValue<string>() == "ui.theme" && c["name"]!.GetValue<string>() == "テーマ");

        // 3〜4. 検索を消して「表示」の項目を変える。
        await app.SendAsync("settingsPage", new JsonObject { ["search"] = string.Empty, ["category"] = "view" });
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "view.jump.position", ["value"] = "center" });
        await app.IdleAsync();
        JsonObject changed = await CardAsync(app, "view.jump.position");
        Assert.True(changed["modified"]!.GetValue<bool>());
        Assert.True(changed["resetVisible"]!.GetValue<bool>());
        JsonObject other = await CardAsync(app, "view.scroll.cursorMargin");
        Assert.False(other["modified"]!.GetValue<bool>());
        Assert.False(other["resetVisible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-22-04")]
    public Task Language_change_offers_restart() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "language" });
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "ui.language", ["value"] = "ja" });
        await app.IdleAsync();
        JsonObject state = await app.SendAsync("settingsPage");
        Assert.True(state["restartBar"]!.GetValue<bool>());
        Assert.True((await CardAsync(app, "ui.language"))["restartNote"]!.GetValue<bool>());
        Assert.True(await app.IsShownAsync("Settings_RestartNow"));
    });

    // ---- UI-23 保存形式 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-23-01")]
    public Task First_run_writes_only_schema_and_version() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await CommandTests.ExitAsync(app);
        JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "settings.json")))!.AsObject();
        Assert.Equal(["$schema", "$schemaVersion"], json.Select(p => p.Key));
        Assert.Equal(1, json["$schemaVersion"]!.GetValue<int>());
        var schema = new Uri(json["$schema"]!.GetValue<string>());
        Assert.True(schema.IsFile && File.Exists(schema.LocalPath), schema.ToString());
        Assert.EndsWith("settings.schema.json", schema.LocalPath, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-23-02")]
    public Task External_edit_applies_within_a_second() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "ui.theme": "light" }""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        Assert.Equal("Light", (await app.StateAsync())["actualTheme"]!.GetValue<string>());
        // 書き込んだ時刻からテーマが変わった時刻 (アプリの中で記録) まで。どちらも Stopwatch (QPC) の時刻で、プロセスをまたいで比べられる。
        double written = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), """{ "$schemaVersion": 1, "ui.theme": "dark" }""");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["actualTheme"]!.GetValue<string>() == "Dark",
            UiTest.Scaled(TimeSpan.FromSeconds(5)), "the dark theme");
        double took = (await WindowManagementTests.WindowsAsync(app))[0]!["themeChangedAtMs"]!.GetValue<double>() - written;
        Assert.True(took < 1000, $"{took:F0} ms");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-23-03")]
    public Task Broken_settings_start_with_defaults_and_keep_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SET-BROKEN。
        const string broken = """{"$schemaVersion": 1, "ui.theme":""";
        string profile = CommandTests.Profile(ctx, broken);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("default settings", StringComparison.OrdinalIgnoreCase), "the broken settings notice");
        Assert.NotNull(notice);
        string file = Assert.Single(Directory.GetFiles(profile, "settings.json.broken-*"));
        Assert.Equal(broken, await File.ReadAllTextAsync(file));

        // 「ファイルを開く」で、名前を変えて残したファイルを開く (UI-23 の「エラー」)。
        Assert.Contains("Open file", notice["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Open file" });
        await app.WaitForLogAsync(l => l.Contains("Test hooks: launch file:", StringComparison.Ordinal) && l.Contains("settings.json.broken-", StringComparison.Ordinal), "the broken file is opened");
        Assert.False((await CardAsync(app, "ui.theme", new JsonObject { ["category"] = "appearance" }))["modified"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-23-05")]
    public Task Killed_while_writing_leaves_readable_settings() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "view.font.size": 11 }""");
        foreach ((string point, int expected) in new[] { ("settingsTemp", 11), ("settingsBeforeReplace", 11), ("settingsAfterReplace", 12) })
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = new JsonObject { ["killAt"] = point } });

            // 2. view.font.size を 12 に (500 ms 後の書き込みでフックが強制終了する)。フォント (UI-29) の項目は別の機能が登録するので、
            // 値はテスト用の命令で直接書く。
            try
            {
                await app.SendAsync("settingRaw", new JsonObject { ["key"] = "view.font.size", ["value"] = 12 });
            }
            catch (IOException)
            {
            }

            await app.WaitForExitAsync(TimeSpan.FromSeconds(20));
            JsonObject json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "settings.json")))!.AsObject();
            Assert.Equal(1, json["$schemaVersion"]!.GetValue<int>());
            Assert.Equal(expected, json["view.font.size"]!.GetValue<int>());
        }
    });

    // ---- UI-24 リセット ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-24-01")]
    public Task Item_reset_can_be_undone() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "ui.theme": "dark" }""") });
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "appearance" });
        await app.IdleAsync();

        // 1. テーマの「既定に戻す」。確認なしで戻り、「元に戻す」付きの通知が出る。
        await app.UiaInvokeAsync("SettingReset_ui.theme");
        await app.IdleAsync();
        Assert.False((await CardAsync(app, "ui.theme"))["modified"]!.GetValue<bool>());
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("Theme", StringComparison.Ordinal), "the reset notice");
        Assert.NotNull(notice);

        // 3. 「元に戻す」。
        await app.SendAsync("noticeUndo");
        await app.IdleAsync();
        Assert.True((await CardAsync(app, "ui.theme"))["modified"]!.GetValue<bool>());
        Assert.Equal("Dark", (await app.StateAsync())["actualTheme"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-24-02")]
    public Task Full_reset_backs_up_and_keeps_document_data() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "ui.theme": "dark" }""");

        // ブックマーク (INSP) は別の機能なので、ドキュメントに付随するデータのファイルを置いて代わりにする。
        Directory.CreateDirectory(Path.Combine(profile, "documents"));
        string docData = Path.Combine(profile, "documents", "seq.json");
        await File.WriteAllTextAsync(docData, """{ "bookmarks": [ { "name": "mark1", "offset": 256 } ] }""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 1〜2. 「すべての設定を既定に戻す」のダイアログの初期状態を確かめ、そのまま「既定に戻す」。
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "advanced" });
        await app.IdleAsync();
        await app.UiaInvokeAsync("SettingsAction_settings.resetAll");
        await app.WaitForDialogAsync("ResetAllDialog");
        Assert.True(AppSession.IsToggled(await app.WaitForAsync("ResetAll_Settings")));
        Assert.True(AppSession.IsToggled(await app.WaitForAsync("ResetAll_KeyBindings")));
        Assert.False(AppSession.IsToggled(await app.WaitForAsync("ResetAll_Themes")));
        Assert.False(AppSession.IsToggled(await app.WaitForAsync("ResetAll_RecentAndState")));
        Assert.False(AppSession.IsToggled(await app.WaitForAsync("ResetAll_Documents")));
        await app.InvokeDialogButtonAsync("Reset to default");
        await app.WaitUntilAsync(() => Task.FromResult(Directory.GetDirectories(profile, "backup-*").Length > 0), TimeSpan.FromSeconds(10), "the backup folder");
        await app.SendAsync("flushSettings");

        // 3. バックアップがあり、settings.json は ui.theme を含まない。
        string backup = Assert.Single(Directory.GetDirectories(profile, "backup-*"));
        Assert.Contains("\"dark\"", await File.ReadAllTextAsync(Path.Combine(backup, "settings.json")));
        Assert.DoesNotContain("ui.theme", await File.ReadAllTextAsync(Path.Combine(profile, "settings.json")));

        // 4. 付随データは残る。
        Assert.Contains("mark1", await File.ReadAllTextAsync(docData));
    });

    // ---- UI-25 インポート / エクスポート ----

    [Fact(Skip = "インストーラ版とポータブル版を入れたランナーで行う配布のテスト (テスト方針 6.4)。処理は Core の SettingsFeatureTests と TC-UI-25-02 / 03 で確かめる。")]
    [Trait(UiTest.TC, "TC-UI-25-01")]
    public void Settings_move_from_installer_to_portable()
    {
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-25-02")]
    public Task Default_export_contains_no_paths() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M"), bytes = ctx.TestData("TD-BYTES-256");
        string profile = CommandTests.Profile(ctx);

        // 最近使ったファイル (ENG-16 / UI-32) の記録の代わりに、2 件の recent.json を置く。
        await File.WriteAllTextAsync(Path.Combine(profile, "recent.json"), new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject { ["path"] = seq }, new JsonObject { ["path"] = bytes }),
        }.ToJsonString());
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "ui.theme", ["value"] = "dark" });

        // 1. 「設定をエクスポート」の初期状態 (最近使ったファイルはオフ)。
        await app.SendAsync("settingsPage", new JsonObject { ["category"] = "advanced" });
        await app.IdleAsync();
        await app.UiaInvokeAsync("SettingsAction_settings.export");
        await app.WaitForDialogAsync("ExportSettingsDialog");
        Assert.False(AppSession.IsToggled(await app.WaitForAsync("Export_RecentAndState")));
        Assert.True(AppSession.IsToggled(await app.WaitForAsync("Export_Settings")));
        await app.InvokeDialogButtonAsync("Cancel");

        string exported = Path.Combine(ctx.Root, "export.json");
        await app.SendAsync("exportSettings", new JsonObject { ["path"] = exported });
        string text = await File.ReadAllTextAsync(exported);
        Assert.Contains("\"dark\"", text);
        foreach (string s in new[] { seq, bytes, Path.GetFileName(seq), Path.GetFileName(bytes) })
        {
            Assert.DoesNotContain(s.Replace("\\", "\\\\"), text);
            Assert.DoesNotContain(s, text);
        }

        Assert.DoesNotMatch(new Regex(@"[A-Za-z]:\\"), text);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-25-03")]
    public Task Invalid_values_are_skipped_on_import() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SETEXP-BADVALUE。view.font.size (UI-29) と view.zoom.hex (UI-08) の項目は別の機能が登録するまで知らない設定として
        // そのまま読み込む (範囲の検査は Core の SettingsFeatureTests で確かめる)。不正なテーマは飛ばす。
        string file = Path.Combine(ctx.Root, "bad.json");
        await File.WriteAllTextAsync(file, """{"$schemaVersion": 1, "app": "HexEditor", "version": "1.0.0", "settings": {"view.font.size": 200, "ui.theme": "purple", "view.zoom.hex": 150}}""");
        string profile = CommandTests.Profile(ctx);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject result = await app.SendAsync("importSettings", new JsonObject { ["path"] = file, ["modes"] = new JsonObject { ["Settings"] = "Merge" } });
        Assert.Contains(result["skipped"]!.AsArray(), s => s!["key"]!.GetValue<string>() == "ui.theme" && s["value"]!.GetValue<string>() == "\"purple\"");
        await app.WaitForNotificationAsync(m => m.Contains("ui.theme", StringComparison.Ordinal), "the skipped list");
        await app.SendAsync("flushSettings");
        JsonObject settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "settings.json")))!.AsObject();
        Assert.Equal(150, settings["view.zoom.hex"]!.GetValue<int>());
        Assert.Null(settings["ui.theme"]);
        Assert.Single(Directory.GetDirectories(profile, "backup-*"));
    });

    // ---- UI-05 パネル ----

    private static async Task<JsonObject> PanelsAsync(AppSession app) => await app.SendAsync("panels");

    private static async Task<string?> LocationAsync(AppSession app, string id) =>
        (await PanelsAsync(app))["panels"]![id]!["location"]?.GetValue<string>();

    [Fact]
    [Trait(UiTest.TC, "TC-UI-05-01")]
    public Task Moved_panel_stays_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx);
        string seq = ctx.TestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });
        await app.KeyAsync("I", ctrl: true, shift: true);
        Assert.Equal("right", await LocationAsync(app, "inspector"));

        // 1〜2. 見出しを下パネルへ移す (マウスのドラッグは作業中の PC の入力を奪うので、ドロップと同じ処理を呼ぶ)。
        await app.SendAsync("panelMove", new JsonObject { ["id"] = "inspector", ["dock"] = "bottom" });
        Assert.Equal("bottom", await LocationAsync(app, "inspector"));
        await app.WaitUntilAsync(async () => await app.IsShownAsync("BottomPanel") && !await app.IsShownAsync("RightPanel"),
            TimeSpan.FromSeconds(5), "the inspector in the bottom panel");
        await CommandTests.ExitAsync(app);

        // 3〜4. 起動し直す。
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });
        Assert.Equal("bottom", await LocationAsync(again, "inspector"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-05-02")]
    public Task Keyboard_only_float_and_redock() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("I", ctrl: true, shift: true);
        await app.IdleAsync();

        // 1. F6 で右パネルの見出しへ。
        for (int i = 0; i < 6 && !((await app.FocusedAsync()) ?? string.Empty).EndsWith("PanelTab_inspector", StringComparison.Ordinal); i++)
        {
            await app.SendAsync("region", new JsonObject { ["forward"] = true });
            await app.IdleAsync();
        }

        Assert.EndsWith("PanelTab_inspector", await app.FocusedAsync() ?? string.Empty);

        // 2〜3. Shift+F10 (見出しの右クリックメニュー) >「移動 > 浮動」。
        await app.SendAsync("panelMenu", new JsonObject { ["id"] = "inspector", ["dock"] = "floating" });
        await app.IdleAsync();

        // 4. 浮動パネルはメインウィンドウの子ウィンドウ。
        JsonObject state = await PanelsAsync(app);
        JsonObject floating = Assert.Single(state["floating"]!.AsArray())!.AsObject();
        Assert.Equal("inspector", floating["id"]!.GetValue<string>());
        Assert.True(floating["ownedByMain"]!.GetValue<bool>());
        Assert.Equal("floating", await LocationAsync(app, "inspector"));

        // 5. 浮動パネルの見出しで「移動 > 右」。
        await app.SendAsync("panelMenu", new JsonObject { ["id"] = "inspector", ["dock"] = "right" });
        state = await PanelsAsync(app);
        Assert.Empty(state["floating"]!.AsArray());
        Assert.Equal("right", await LocationAsync(app, "inspector"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-05-03")]
    public Task Panels_in_one_place_share_tabs_and_layout_resets() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "bookmarks" });
        Assert.Equal("left", await LocationAsync(app, "bookmarks"));

        // 1. ブックマークの見出しのメニューで「移動 > 右」。
        await app.SendAsync("panelMenu", new JsonObject { ["id"] = "bookmarks", ["dock"] = "right" });
        await app.IdleAsync();

        // 2. 右パネルの上部に 2 つのタブ。
        JsonObject right = (await PanelsAsync(app))["areas"]!["right"]!.AsObject();
        Assert.Equal(["inspector", "bookmarks"], right["tabs"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(await app.IsShownAsync("PanelTab_inspector"));
        Assert.True(await app.IsShownAsync("PanelTab_bookmarks"));

        // 3〜4. 「パネルの配置を元に戻す」。
        await app.CommandAsync("Command_ResetPanelLayout");
        await app.IdleAsync();
        Assert.Equal("right", await LocationAsync(app, "inspector"));
        Assert.Equal("left", await LocationAsync(app, "bookmarks"));
    });

    // ---- UI-24 / UI-25 最近使ったファイルのリセットとインポート ----

    [Fact]
    public Task Resetting_and_importing_recent_files_update_the_shared_list() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = CommandTests.Profile(ctx);
        string first = ctx.TestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [first] });
        async Task<List<string>> RecentAsync() =>
            [.. (await app.SendAsync("files"))["recent"]!.AsArray().Select(r => Path.GetFileName(r!["path"]!.GetValue<string>()))];
        await app.WaitUntilAsync(async () => (await RecentAsync()).Count == 1, TimeSpan.FromSeconds(10), "the recent file");
        string exported = Path.Combine(ctx.Root, "with-recent.json");
        await app.SendAsync("exportSettings", new JsonObject { ["parts"] = new JsonArray("RecentAndState"), ["path"] = exported });

        // リセットはメモリの一覧も空にする (残っていると、次に開いたときに recent.json に書き戻る)。
        Assert.True((await app.SendAsync("resetAll", new JsonObject { ["parts"] = new JsonArray("RecentAndState") }))["done"]!.GetValue<bool>());
        Assert.Empty(await RecentAsync());
        await app.OpenAsync(ctx.TestData("TD-BYTES-256"));
        await app.WaitUntilAsync(async () => (await RecentAsync()).Count == 1, TimeSpan.FromSeconds(10), "the new recent file");
        Assert.DoesNotContain("TD-SEQ-1M", await File.ReadAllTextAsync(Path.Combine(profile, "recent.json")));

        // マージで読み込むと、今の項目と合わせる。
        await app.SendAsync("importSettings", new JsonObject { ["path"] = exported, ["modes"] = new JsonObject { ["RecentAndState"] = "merge" } });
        List<string> merged = await RecentAsync();
        Assert.Contains("TD-SEQ-1M.bin", merged);
        Assert.Contains("TD-BYTES-256.bin", merged);
        Assert.Contains("TD-SEQ-1M", await File.ReadAllTextAsync(Path.Combine(profile, "recent.json")));
    });
}
