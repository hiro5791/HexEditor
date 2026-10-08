using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ウィンドウの枠の機能の残り: 再起動ですべてのウィンドウを戻す (UI-43 の仕様 5、UI-13 の仕様 2)、閉じる確認のキャンセルと購読の解除
/// (UI-13、UI-14)、ツールバーの同期 (UI-14 の仕様 2)、パネルのドラッグ (UI-05 の仕様 3)、全画面の上端のバー (UI-07 の仕様 2)、
/// ポップアップのズーム (UI-08 の仕様 3)、未保存の印 (UI-09 の仕様 2)、ピン留めのタブを開き直す (UI-12 の仕様 2)、保存の完了を待つ
/// ダイアログ (UI-13)。実際のマウスは使わず、同じ処理をテスト用の命令で呼ぶ。新しいウィンドウもアクティブにしない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class WindowShellTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static Task<JsonObject> SendToAsync(AppSession app, int window, string cmd, JsonObject? args = null)
    {
        JsonObject request = args ?? new JsonObject();
        request["window"] = window;
        return app.SendAsync(cmd, request);
    }

    private static async Task NewWindowAsync(AppSession app, int expected)
    {
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WindowManagementTests.WaitForWindowsAsync(app, expected);
    }

    private static IReadOnlyList<string> Tabs(JsonNode window) => [.. window["tabs"]!.AsArray().Select(t => t!.GetValue<string>())];

    // ---- UI-43 の仕様 5、UI-13 の仕様 2: 再起動 ----

    [Fact]
    public Task Restart_now_asks_for_every_window_and_restores_every_window() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        byte[] bytesBefore = File.ReadAllBytes(bytes);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = profile,
            UiLanguage = null,
            Files = [seq],
            Hooks = new JsonObject { ["windowsLanguages"] = new JsonArray("en-US") },
        });
        await app.WaitForTabsAsync(1);
        await app.GoToAsync(0);
        await app.TypeAsync("FF");

        // ウィンドウ 2 に別のファイルを開いて編集する (未保存の文書が 2 つのウィンドウにある)。
        await NewWindowAsync(app, 2);
        await SendToAsync(app, 1, "uiOpen", new JsonObject { ["path"] = bytes });
        await app.WaitUntilAsync(async () => (await SendToAsync(app, 1, "state"))["hexViews"]!.GetValue<int>() > 0, Wait, "the hex view of window 2");
        await SendToAsync(app, 1, "goto", new JsonObject { ["offset"] = 0 });
        await SendToAsync(app, 1, "text", new JsonObject { ["text"] = "AA" });
        await app.SendAsync("activateWindow", new JsonObject { ["window"] = 0 });

        // 「今すぐ再起動」: 両方のウィンドウの未保存の文書が 1 つの一覧に出る。「保存せずに閉じる」で再起動する。
        await app.SendAsync("setDisplayLanguage", new JsonObject { ["language"] = "ja" });
        DateTime since = DateTime.Now;
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Restart now" });
        await app.WaitForAsync("Close_Item");
        Assert.Equal(2, app.Window.FindAllDescendants(cf => cf.ByAutomationId("Close_Item")).Length);
        await app.InvokeDialogButtonAsync("Close without saving", idle: false);
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        Process? restarted = null;
        try
        {
            var watch = Stopwatch.StartNew();
            while ((restarted = Process.GetProcessesByName("HexEditor")
                .FirstOrDefault(p => p.Id != app.Pid && StartedAfter(p, since) && AppLocator.IsAppProcess(p))) is null)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "the app did not restart");
                await Task.Delay(200);
            }

            AppSession again = await ctx.AttachAsync(restarted!, profile);
            restarted = null;

            // 2 つのウィンドウとそれぞれのタブが戻る (保存しなかった変更は戻らない)。
            await WindowManagementTests.WaitForWindowsAsync(again, 2);
            await again.WaitUntilAsync(async () => (await WindowManagementTests.WindowsAsync(again)).Sum(w => Tabs(w!).Count) == 2, Wait, "the tabs");
            JsonArray windows = await WindowManagementTests.WindowsAsync(again);
            Assert.Equal([Path.GetFileName(seq)], Tabs(windows[0]!));
            Assert.Equal([Path.GetFileName(bytes)], Tabs(windows[1]!));
            Assert.Equal(bytesBefore, File.ReadAllBytes(bytes));
        }
        finally
        {
            restarted?.Kill();
        }
    });

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


    // ---- UI-13、UI-14: 閉じる確認のキャンセルと、閉じたウィンドウの購読 ----

    private static async Task<JsonObject> SubscribersAsync(AppSession app, int window = 0) => await SendToAsync(app, window, "subscribers");

    private static readonly string[] AppWideEvents = ["settings", "bindings", "catalog", "panelRegistry", "panelDragging", "screenZoom", "operations", "recent"];

    private static void AssertSameSubscribers(JsonObject expected, JsonObject actual)
    {
        foreach (string key in AppWideEvents)
        {
            Assert.True(expected[key]!.GetValue<int>() == actual[key]!.GetValue<int>(), $"{key}: {expected[key]} -> {actual[key]}");
        }
    }

    [Fact]
    public Task Cancelled_close_keeps_the_window_working_and_closed_windows_leave_no_subscriptions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await app.WaitForTabsAsync(1);
        JsonObject one = await SubscribersAsync(app);

        // ウィンドウ 2 を開くと、アプリ全体のイベントの購読が増える。閉じると元の数に戻る。
        await NewWindowAsync(app, 2);
        JsonObject two = await SubscribersAsync(app);
        Assert.True(two["settings"]!.GetValue<int>() > one["settings"]!.GetValue<int>(), "the second window subscribes to the settings");
        await SendToAsync(app, 1, "closeWindow");
        await WindowManagementTests.WaitForWindowsAsync(app, 1);
        await app.IdleAsync();
        AssertSameSubscribers(one, await SubscribersAsync(app));

        // ウィンドウ 1 に未保存の文書と浮動パネル。ウィンドウ 2 を開いてから、ウィンドウ 1 を閉じて確認でキャンセルする。
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
        await app.SendAsync("panelMove", new JsonObject { ["id"] = "inspector", ["dock"] = "floating" });
        await NewWindowAsync(app, 2);
        await app.SendAsync("activateWindow", new JsonObject { ["window"] = 0 });
        JsonObject before = await SubscribersAsync(app);
        Assert.Equal(1, before["floatingPanels"]!.GetValue<int>());
        await SendToAsync(app, 0, "closeWindow");
        await app.WaitForAsync("CloseDialog");
        await app.InvokeDialogButtonAsync("Cancel");
        await app.IdleAsync();

        // ウィンドウ 1 はそのまま: 浮動パネルが残り、購読も外れていない。
        Assert.Equal(2, (await WindowManagementTests.WindowsAsync(app)).Count);
        JsonObject after = await SubscribersAsync(app);
        Assert.Equal(1, after["floatingPanels"]!.GetValue<int>());
        AssertSameSubscribers(before, after);
        Assert.Single((await SendToAsync(app, 0, "floatingPanels"))["panels"]!.AsArray());
    });

    // ---- UI-14 の仕様 2: ツールバー ----

    [Fact]
    public Task Toolbar_change_in_one_window_appears_in_the_other() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await NewWindowAsync(app, 2);
        Assert.Equal("Collapsed", (await SendToAsync(app, 1, "element", new JsonObject { ["id"] = "Toolbar" }))["visibility"]!.GetValue<string>());

        // ウィンドウ 1 でツールバーを表示すると、ウィンドウ 2 にも出る。
        await SendToAsync(app, 0, "execute", new JsonObject { ["id"] = "view.toolbar" });
        await app.WaitUntilAsync(async () => (await SendToAsync(app, 1, "element", new JsonObject { ["id"] = "Toolbar" }))["visibility"]!.GetValue<string>() == "Visible",
            Wait, "the toolbar in window 2");
        await app.WaitUntilAsync(async () => (await SendToAsync(app, 1, "element", new JsonObject { ["id"] = "ToolbarButton_file.open" }))["found"]!.GetValue<bool>(),
            Wait, "the toolbar buttons in window 2");

        // 高さ 40 px (UI-01 の仕様 1 の表)。
        await app.IdleAsync();
        Assert.Equal(40, (await SendToAsync(app, 1, "element", new JsonObject { ["id"] = "Toolbar" }))["height"]!.GetValue<double>(), 1);
        Assert.InRange((await SendToAsync(app, 1, "element", new JsonObject { ["id"] = "ToolbarButton_file.open" }))["height"]!.GetValue<double>(), 24, 40);

        // もう一度切り替えると、両方のウィンドウで隠れる。
        await SendToAsync(app, 1, "execute", new JsonObject { ["id"] = "view.toolbar" });
        await app.WaitUntilAsync(async () => (await SendToAsync(app, 0, "element", new JsonObject { ["id"] = "Toolbar" }))["visibility"]!.GetValue<string>() == "Collapsed",
            Wait, "the toolbar hidden in window 1");
    });

    // ---- UI-05 の仕様 3: パネルのドラッグ ----

    [Fact]
    public Task Panel_dropped_outside_the_window_floats_and_other_windows_reject_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        // ウィンドウの右に、画面の余白を残す (CI のランナーの画面は 1024 px 幅。画面にかからない位置の浮動パネルは
        // メインウィンドウの横に置き直される。PanelLayout.EnsureOnScreen)。
        JsonObject screen = await app.SendAsync("shellState");
        int monitorRight = screen["monitorX"]!.GetValue<int>() + screen["monitorWidth"]!.GetValue<int>();
        await app.SendAsync("moveWindow", new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = Math.Min(1100, monitorRight - 300), ["height"] = 800 });
        JsonObject placed = await app.SendAsync("shellState");
        int windowRight = placed["x"]!.GetValue<int>() + placed["width"]!.GetValue<int>();
        int dropX = Math.Min(windowRight + 200, monitorRight - 50);
        Assert.True(dropX > windowRight, $"no screen right of the window (window right {windowRight}, screen right {monitorRight})");
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
        Assert.Equal("right", (await app.SendAsync("panels"))["panels"]!["inspector"]!["location"]!.GetValue<string>());

        // ウィンドウの中で離した (取り消しと同じ) なら動かない。
        JsonObject inside = await app.SendAsync("panelDragOutside", new JsonObject { ["id"] = "inspector", ["x"] = 300, ["y"] = 300 });
        Assert.Equal("right", inside["panels"]!["inspector"]!["location"]!.GetValue<string>());

        // ウィンドウの外で離すと、その位置の浮動パネルになる。
        JsonObject outside = await app.SendAsync("panelDragOutside", new JsonObject { ["id"] = "inspector", ["x"] = dropX, ["y"] = 200 });
        Assert.Equal("floating", outside["panels"]!["inspector"]!["location"]!.GetValue<string>());
        JsonObject floating = (await app.SendAsync("floatingPanels"))["panels"]![0]!.AsObject();
        Assert.InRange(floating["x"]!.GetValue<int>(), windowRight - 100, dropX);

        // 元に戻して、別のウィンドウの下の場所には落とせない (同じウィンドウの下には落とせる)。
        await app.SendAsync("panelMove", new JsonObject { ["id"] = "inspector", ["dock"] = "right" });
        await NewWindowAsync(app, 2);
        JsonObject rejected = await SendToAsync(app, 0, "panelDragTo", new JsonObject { ["id"] = "inspector", ["toWindow"] = 1, ["dock"] = "bottom" });
        Assert.False(rejected["accepted"]!.GetValue<bool>());
        Assert.Equal("right", rejected["panels"]!["inspector"]!["location"]!.GetValue<string>());
        JsonObject accepted = await SendToAsync(app, 0, "panelDragTo", new JsonObject { ["id"] = "inspector", ["toWindow"] = 0, ["dock"] = "bottom" });
        Assert.True(accepted["accepted"]!.GetValue<bool>());
        Assert.Equal("bottom", accepted["panels"]!["inspector"]!["location"]!.GetValue<string>());
    });

    // ---- UI-07 の仕様 2: 全画面の上端 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-07-02")]
    public Task Pointer_at_the_top_edge_shows_the_menu_toolbar_and_tab_strip_over_the_editor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.toolbar" });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.fullScreen" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("shellState"))["presenter"]!.GetValue<string>() == "FullScreen", Wait, "full screen");
        await Task.Delay(300);
        await app.IdleAsync();
        JsonObject hidden = await app.SendAsync("shellState");
        Assert.False(hidden["titleBarVisible"]!.GetValue<bool>());
        Assert.False(hidden["toolbarVisible"]!.GetValue<bool>());
        double editorTop = hidden["editorTop"]!.GetValue<double>();

        // 上端に置いて 400 ms: タイトルバー (メニュー)、ツールバー、タブ列が上から順に重なって出る。エディタは動かない。
        await app.SendAsync("fullScreenPointer", new JsonObject { ["y"] = 2 });
        await Task.Delay(400);
        await app.IdleAsync();
        JsonObject shown = await app.SendAsync("shellState");
        Assert.True(shown["titleBarVisible"]!.GetValue<bool>(), "the title bar");
        Assert.True(shown["toolbarVisible"]!.GetValue<bool>(), "the toolbar");
        Assert.True(shown["tabStripVisible"]!.GetValue<bool>(), "the tab strip");
        Assert.Equal(editorTop, shown["editorTop"]!.GetValue<double>(), 1);
        Assert.Equal(0, shown["titleBarTop"]!.GetValue<double>(), 1);
        Assert.True(shown["menuBottom"]!.GetValue<double>() > shown["menuTop"]!.GetValue<double>(), "the menu has a height");
        Assert.Equal(shown["titleBarBottom"]!.GetValue<double>(), shown["toolbarTop"]!.GetValue<double>(), 1);
        Assert.Equal(shown["toolbarBottom"]!.GetValue<double>(), shown["tabStripTop"]!.GetValue<double>(), 1);

        // 下へ離れると、また隠れる。
        await app.SendAsync("fullScreenPointer", new JsonObject { ["y"] = 600 });
        await app.IdleAsync();
        JsonObject again = await app.SendAsync("shellState");
        Assert.False(again["titleBarVisible"]!.GetValue<bool>());
        Assert.False(again["toolbarVisible"]!.GetValue<bool>());
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.fullScreen" });
    });

    // ---- UI-08 の仕様 3: ポップアップ ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-04")]
    public Task Screen_zoom_scales_menus_flyouts_and_floating_panels() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)(1700 * scale), (int)(1000 * scale));
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "inspector" });
        await app.SendAsync("panelMove", new JsonObject { ["id"] = "inspector", ["dock"] = "floating" });
        JsonObject menu100 = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "menu" });
        Assert.True(menu100["opened"]!.GetValue<bool>(), "the menu did not open");
        double font = menu100["menuFontSizes"]!.AsArray()[0]!.GetValue<double>();
        JsonObject tip100 = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "tooltip" });
        double tipFont = tip100["toolTipFontSize"]!.GetValue<double>();

        for (int i = 0; i < 3; i++)
        {
            await app.CommandAsync("Command_UiZoomIn");
        }

        await app.IdleAsync();
        Assert.Equal(1.5, (await app.SendAsync("shellState"))["appliedUiZoom"]!.GetValue<double>(), 3);

        // 右クリックメニュー (ステータスバー): 項目の文字が 1.5 倍。フライアウト (通知の履歴): 中身が 1.5 倍。浮動パネル: 1.5 倍。
        JsonObject menu = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "menu" });
        Assert.All(menu["menuFontSizes"]!.AsArray(), f => Assert.Equal(font * 1.5, f!.GetValue<double>(), 1));
        JsonObject flyout = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "flyout" });
        Assert.True(flyout["opened"]!.GetValue<bool>(), "the flyout did not open");
        Assert.Equal(1.5, flyout["flyoutZoom"]!.GetValue<double>(), 3);
        JsonObject tip = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "tooltip" });
        Assert.True(tip["opened"]!.GetValue<bool>(), "the tooltip did not open");
        Assert.Equal(tipFont * 1.5, tip["toolTipFontSize"]!.GetValue<double>(), 1);
        await app.WaitUntilAsync(async () => Math.Abs((await app.SendAsync("floatingPanels"))["panels"]![0]!["appliedZoom"]!.GetValue<double>() - 1.5) < 0.01,
            Wait, "the floating panel at 150%");

        // 100% に戻すと元の大きさ。
        await app.CommandAsync("Command_UiZoomReset");
        await app.IdleAsync();
        JsonObject reset = await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "menu" });
        Assert.All(reset["menuFontSizes"]!.AsArray(), f => Assert.Equal(font, f!.GetValue<double>(), 1));
        Assert.Equal(tipFont, (await app.SendAsync("popupZoom", new JsonObject { ["kind"] = "tooltip" }))["toolTipFontSize"]!.GetValue<double>(), 1);
    });

    // ---- UI-09 の仕様 2: 未保存の印 ----

    [Fact]
    public Task Unsaved_mark_is_at_the_close_button_and_turns_into_close_on_hover() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();
        Assert.False((await app.SendAsync("tabCloseMark", new JsonObject { ["index"] = 0 }))["unsaved"]!.GetValue<bool>());

        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        await app.WaitUntilAsync(async () => (await app.SendAsync("tabCloseMark", new JsonObject { ["index"] = 0 }))["unsaved"]!.GetValue<bool>(), Wait, "the unsaved mark");

        // 読み上げの名前には印が残る (色や形だけに頼らない)。
        Assert.StartsWith("● ", (await TabTests.TabsAsync(app))["tabs"]![0]!["header"]!.GetValue<string>());

        // タブにポインタを置くと ×、離すと ● に戻る。
        await app.SendAsync("tabHover", new JsonObject { ["index"] = 0, ["over"] = true });
        Assert.False((await app.SendAsync("tabCloseMark", new JsonObject { ["index"] = 0 }))["unsaved"]!.GetValue<bool>());
        await app.SendAsync("tabHover", new JsonObject { ["index"] = 0, ["over"] = false });
        Assert.True((await app.SendAsync("tabCloseMark", new JsonObject { ["index"] = 0 }))["unsaved"]!.GetValue<bool>());

        // 元に戻すと印が消える。
        await app.KeyAsync("Z", ctrl: true);
        await app.WaitUntilAsync(async () => !(await app.SendAsync("tabCloseMark", new JsonObject { ["index"] = 0 }))["unsaved"]!.GetValue<bool>(), Wait, "no unsaved mark");
    });

    // ---- UI-12 の仕様 2: ピン留めのタブを開き直す ----

    [Fact]
    public Task Reopened_pinned_tab_returns_to_its_place_in_the_pinned_group() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = TabTests.Files(ctx, 4);
        AppSession app = await ctx.StartAsync();
        foreach (string f in files)
        {
            await app.UiOpenAsync(f);
        }

        for (int i = 0; i < 3; i++)
        {
            await app.SendAsync("tabMenu", new JsonObject { ["index"] = i, ["item"] = "TabMenu_Pin" });
        }

        Assert.Equal(["file01.bin", "file02.bin", "file03.bin", "file04.bin"], await TabTests.NamesAsync(app));

        // ピン留めの真ん中の file02 を閉じて開き直すと、ピン留めの並びの元の位置 (2 番目) に戻る。
        await app.SendAsync("tabCloseRequest", new JsonObject { ["index"] = 1 });
        await app.WaitForTabsAsync(3);
        await app.CommandAsync("Command_ReopenClosedTab");
        await app.WaitForTabsAsync(4);
        JsonArray tabs = (await TabTests.TabsAsync(app))["tabs"]!.AsArray();
        Assert.Equal(["file01.bin", "file02.bin", "file03.bin", "file04.bin"], tabs.Select(t => t!["name"]!.GetValue<string>()));
        Assert.True(tabs[1]!["pinned"]!.GetValue<bool>());
    });

    // ---- UI-13 の「巨大ファイル・長時間処理」: 保存の完了を待つ間の進捗 ----

    [Fact]
    public Task Waiting_for_the_save_before_closing_shows_progress() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-RANDOM-16M", "random.bin");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "random.bin", ["delayMs"] = 800, ["delayFromOffset"] = 65536 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, Wait, "the save");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForAsync("CloseDialog");
        await app.InvokeDialogButtonAsync("Close after saving");

        // 保存の進捗のダイアログが出て、保存が終わると自動で閉じ、タブも閉じる。
        await app.WaitUntilAsync(async () => (await app.SendAsync("closeWait"))["open"]!.GetValue<bool>(), Wait, "the waiting dialog");
        Assert.StartsWith("Waiting for", (await app.SendAsync("closeWait"))["text"]!.GetValue<string>());
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(60), "the tab to close");
        Assert.False((await app.SendAsync("closeWait"))["open"]!.GetValue<bool>());
        Assert.Equal(16_777_217, new FileInfo(path).Length);
    });
}
