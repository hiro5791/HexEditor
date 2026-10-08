using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// タブの切り離しとウィンドウ間の移動 (UI-11)、複数ウィンドウ (UI-14)、外部から開いたファイル (UI-15)、複数ウィンドウのセッション
/// (UI-31)。新しいウィンドウもアクティブにしない (作業中の利用者のフォーカスを奪わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class WindowManagementTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    internal static async Task<JsonArray> WindowsAsync(AppSession app) => (await app.SendAsync("windows"))["windows"]!.AsArray();

    internal static async Task WaitForWindowsAsync(AppSession app, int count) =>
        await app.WaitUntilAsync(async () => (await WindowsAsync(app)).Count == count, Wait, $"{count} window(s)");

    private static Task<JsonObject> SendToAsync(AppSession app, int window, string cmd, JsonObject? args = null)
    {
        JsonObject request = args ?? new JsonObject();
        request["window"] = window;
        return app.SendAsync(cmd, request);
    }

    private static IReadOnlyList<string> Tabs(JsonNode window) => [.. window["tabs"]!.AsArray().Select(t => t!.GetValue<string>())];

    /// <summary>このテストで起動した HexEditor のプロセスの数。</summary>
    private static int ProcessCount(DateTime since) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppLocator.ExePath))
        .Count(p =>
        {
            try
            {
                return !p.HasExited && string.Equals(p.MainModule?.FileName, AppLocator.ExePath, StringComparison.OrdinalIgnoreCase)
                    && p.StartTime >= since.AddSeconds(-1);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        });

    /// <summary>新しいウィンドウ (Ctrl+Shift+N) を開き、元のウィンドウを操作の対象に戻す。</summary>
    private static async Task NewWindowAsync(AppSession app, int expected)
    {
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WaitForWindowsAsync(app, expected);
    }

    // ---- UI-14 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-14-01")]
    public Task Ctrl_shift_n_opens_an_empty_window_in_the_same_process() => UiTestContext.RunAsync(async ctx =>
    {
        DateTime start = DateTime.Now;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1. Ctrl+Shift+N。
        Assert.Equal("menu:Command_NewWindow", (await app.KeyAsync("N", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());

        // 2. プロセスは 1 つで、新しいウィンドウはタブが 0 でスタートページを表示している。
        await WaitForWindowsAsync(app, 2);
        JsonArray windows = await WindowsAsync(app);
        Assert.Equal(["TD-SEQ-1M.bin"], Tabs(windows[0]!));
        Assert.Empty(Tabs(windows[1]!));
        Assert.True(windows[1]!["startPageVisible"]!.GetValue<bool>());
        Assert.Equal(1, ProcessCount(start));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-14-02")]
    public Task Theme_change_applies_to_both_windows() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "ui.theme": "light" }""") });
        await NewWindowAsync(app, 2);
        uint lightBackground = Background(await WindowsAsync(app), 1);

        // 1. ウィンドウ 1 の設定画面でテーマを「ダーク」にする。
        await SendToAsync(app, 0, "settingsPage", new JsonObject { ["category"] = "appearance" });
        Assert.True((await SendToAsync(app, 0, "settingSet", new JsonObject { ["key"] = "ui.theme", ["value"] = "dark" }))["valid"]!.GetValue<bool>());

        // 2. ウィンドウ 2 の背景がダークテーマの色 (ウィンドウ 1 と同じ) になる。
        await app.WaitUntilAsync(async () => (await WindowsAsync(app)).All(w => w!["theme"]!.GetValue<string>() == "Dark"), Wait, "the dark theme in both windows");
        await app.WaitUntilAsync(async () => Luminance(Background(await WindowsAsync(app), 1)) < 0.3, Wait, "the dark background of window 2");
        Assert.True(Luminance(lightBackground) > 0.7, $"light background {lightBackground:X8}");
    });

    /// <summary>ウィンドウのスタートページ (エディタ領域) の背景の色 (スクリーンショット。前面に出さずに取る)。</summary>
    private static uint Background(JsonArray windows, int index)
    {
        WindowImage image = WindowImage.Capture((nint)windows[index]!["hwnd"]!.GetValue<long>());
        return image.Pixel(image.Width / 2, image.Height * 3 / 4);
    }

    private static double Luminance(uint bgra) =>
        ((0.2126 * ((bgra >> 16) & 0xFF)) + (0.7152 * ((bgra >> 8) & 0xFF)) + (0.0722 * (bgra & 0xFF))) / 255.0;

    [Fact]
    [Trait(UiTest.TC, "TC-UI-14-03")]
    public Task Two_windows_are_restored_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = TabTests.Files(ctx, 3);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [files[0], files[1]] });
        await app.WaitForTabsAsync(2);
        await NewWindowAsync(app, 2);
        await SendToAsync(app, 1, "uiOpen", new JsonObject { ["path"] = files[2] });
        await SendToAsync(app, 0, "moveWindow", new JsonObject { ["x"] = 0, ["y"] = 0 });
        await SendToAsync(app, 1, "moveWindow", new JsonObject { ["x"] = 700, ["y"] = 100 });
        JsonArray before = await WindowsAsync(app);

        // 1〜2. ファイル > 終了 で終わり、同じ設定フォルダで起動する。
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        AppSession again = await ctx.StartAsync();

        // 3. ウィンドウが 2 つあり、位置とタブが同じ。
        await WaitForWindowsAsync(again, 2);
        JsonArray after = await WindowsAsync(again);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(Tabs(before[i]!), Tabs(after[i]!));
            Assert.Equal(before[i]!["x"]!.GetValue<int>(), after[i]!["x"]!.GetValue<int>());
            Assert.Equal(before[i]!["y"]!.GetValue<int>(), after[i]!["y"]!.GetValue<int>());
        }

        Assert.Equal(["file01.bin", "file02.bin"], Tabs(after[0]!));
        Assert.Equal(["file03.bin"], Tabs(after[1]!));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-14-04")]
    public Task Closing_the_last_window_ends_the_process() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await NewWindowAsync(app, 2);

        // 1. ウィンドウ 2 を閉じても、プロセスは動いている。
        await SendToAsync(app, 1, "closeWindow");
        await WaitForWindowsAsync(app, 1);
        Assert.False(app.Process.HasExited);

        // 2〜3. ウィンドウ 1 を閉じると、5 秒以内にプロセスが終わる。
        await SendToAsync(app, 0, "closeWindow");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(5));
    });

    // ---- UI-11 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-11-01")]
    public Task Detached_tab_keeps_unsaved_edits_and_undo() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);
        await EditingTests.SelectTabAsync(app, 0);
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        Assert.Equal(0xFF, (await app.BytesAsync(0, 1))[0]);

        // 1. タブ列から下へ 200 px の位置にドロップすると、新しいウィンドウにタブが移る。未保存の印がある。
        await SendToAsync(app, 0, "dragTabOutside", new JsonObject { ["index"] = 0, ["dx"] = 0, ["dy"] = 200 });
        await WaitForWindowsAsync(app, 2);
        JsonArray windows = await WindowsAsync(app);
        Assert.Equal(["TD-BYTES-256.bin"], Tabs(windows[0]!));
        Assert.Equal(["TD-SEQ-1M.bin"], Tabs(windows[1]!));
        JsonObject tabs = await TabTests.TabsAsync(app, 1);
        Assert.True(tabs["tabs"]![0]!["modified"]!.GetValue<bool>());
        Assert.StartsWith("● ", tabs["tabs"]![0]!["header"]!.GetValue<string>());

        // 元のウィンドウと同じ大きさ (UI-11 の仕様 6)。
        Assert.Equal(windows[0]!["width"]!.GetValue<int>(), windows[1]!["width"]!.GetValue<int>());
        Assert.Equal(windows[0]!["height"]!.GetValue<int>(), windows[1]!["height"]!.GetValue<int>());

        // 2〜3. 新しいウィンドウで Ctrl+Z を押すと、オフセット 0 は元の値 00。
        await app.WaitUntilAsync(async () => (await SendToAsync(app, 1, "state"))["hexViews"]!.GetValue<int>() > 0, Wait, "the hex view");
        await SendToAsync(app, 1, "key", new JsonObject { ["key"] = "Z", ["ctrl"] = true });
        Assert.Equal("00", (await SendToAsync(app, 1, "bytes", new JsonObject { ["offset"] = 0, ["length"] = 1 }))["hex"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-11-02")]
    public Task Find_all_continues_after_moving_the_tab() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-HOOK-SLOW-200M: TD-MARKERS-1G を遅いデータソースで開く (秒速 200 MB 程度)。
        string markers = TestDataCatalog.Generate("TD-MARKERS-1G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [markers], Hooks = SearchResultsTests.Slow(markers, 5) });
        await NewWindowAsync(app, 2);
        await app.SendAsync("activateWindow", new JsonObject { ["window"] = 0 });

        // 1. テキストの @00000000 をすべて検索する。
        await SearchResultsTests.OpenFindAsync(app, 1, "@00000000", encoding: "ASCII");
        await SearchResultsTests.FindAllAsync(app);
        await SearchResultsTests.WaitForResultsAsync(app, r => r["running"]!.GetValue<bool>(), "the search to start");

        // 2. 実行中に「別のウィンドウに移動」でウィンドウ 2 に移す。検索は止まらず、処理センターに実行中と出ている。
        await SendToAsync(app, 0, "tabMenu", new JsonObject { ["index"] = 0, ["item"] = "TabMenu_MoveToWindow_2" });
        await app.WaitUntilAsync(async () => (await WindowsAsync(app)).Count == 1, Wait, "the empty window to close");
        Assert.True((await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, "the search should still be running");

        // 3〜4. 検索の完了を待つと、移した先のウィンドウの一覧に 1,025 件。
        JsonObject results = await SearchResultsTests.WaitForResultsAsync(app,
            r => !r["running"]!.GetValue<bool>() && r["state"]?.GetValue<string>() == "Completed", "the search to finish", 300);
        Assert.Equal(1025, results["count"]!.GetValue<long>());
        Assert.True(results["visible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-11-03")]
    public Task Tab_is_dropped_between_tabs_of_another_window() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = TabTests.Files(ctx, 4);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [files[0], files[1]] });
        await app.WaitForTabsAsync(2);
        await NewWindowAsync(app, 2);
        await SendToAsync(app, 1, "uiOpen", new JsonObject { ["path"] = files[2] });
        await SendToAsync(app, 1, "uiOpen", new JsonObject { ["path"] = files[3] });
        await SendToAsync(app, 0, "moveWindow", new JsonObject { ["x"] = 0, ["y"] = 0 });
        await SendToAsync(app, 1, "moveWindow", new JsonObject { ["x"] = 1300, ["y"] = 0 });

        // 1. ウィンドウ 1 の file02 を、ウィンドウ 2 の file03 と file04 の間にドロップする。
        await SendToAsync(app, 0, "dropTab", new JsonObject { ["index"] = 1, ["toWindow"] = 1, ["toIndex"] = 1 });

        // 2. ウィンドウ 1 は file01 だけ、ウィンドウ 2 は file03、file02、file04。
        JsonArray windows = await WindowsAsync(app);
        Assert.Equal(["file01.bin"], Tabs(windows[0]!));
        Assert.Equal(["file03.bin", "file02.bin", "file04.bin"], Tabs(windows[1]!));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-11-04")]
    public Task Dragging_the_only_tab_moves_the_window() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("moveWindow", new JsonObject { ["x"] = 100, ["y"] = 100 });
        JsonObject before = (await WindowsAsync(app))[0]!.AsObject();

        // 2. タブ列から下へ 200 px、右へ 300 px の位置までドラッグしてドロップする。
        await app.SendAsync("dragTabOutside", new JsonObject { ["index"] = 0, ["dx"] = 300, ["dy"] = 200 });

        // 3. ウィンドウは 1 つのまま、右へ約 300 px、下へ約 200 px 動いている。
        JsonArray after = await WindowsAsync(app);
        Assert.Single(after);
        Assert.InRange(after[0]!["x"]!.GetValue<int>() - before["x"]!.GetValue<int>(), 290, 310);
        Assert.InRange(after[0]!["y"]!.GetValue<int>() - before["y"]!.GetValue<int>(), 190, 210);
    });

    // ---- UI-15 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-15-02")]
    public Task New_window_setting_opens_redirected_files_in_a_new_window() => UiTestContext.RunAsync(async ctx =>
    {
        DateTime start = DateTime.Now;
        string profile = CommandTests.Profile(ctx, """{ "$schemaVersion": 1, "window.openExternalIn": "newWindow" }""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")], NewInstance = false });

        // 1. TD-BYTES-256 を指定して起動する (既存のプロセスに転送される)。
        Assert.Equal(0, await ctx.LaunchAndWaitAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-BYTES-256")], NewInstance = false }, TimeSpan.FromSeconds(30)));

        // 2. プロセスは 1 つ、ウィンドウは 2 つで、新しいウィンドウに TD-BYTES-256 のタブだけ。
        await WaitForWindowsAsync(app, 2);
        await app.WaitUntilAsync(async () => Tabs((await WindowsAsync(app))[1]!).Count == 1, Wait, "the redirected file");
        JsonArray windows = await WindowsAsync(app);
        Assert.Equal(["TD-SEQ-1M.bin"], Tabs(windows[0]!));
        Assert.Equal(["TD-BYTES-256.bin"], Tabs(windows[1]!));
        Assert.Equal(1, ProcessCount(start));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-15-03")]
    public Task Five_selected_files_open_as_five_tabs_in_one_window() => UiTestContext.RunAsync(async ctx =>
    {
        // Explorer の代わりに、Explorer が複数選択で起動するときと同じく 5 つのパスを渡す。1 回の起動で 5 つを渡す場合と、
        // ファイルごとに起動して既存のプロセスに転送される場合 (UI-15 の仕様 7) の両方を確かめる。
        DateTime start = DateTime.Now;
        string[] files = TabTests.Files(ctx, 10);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files[..5], NewInstance = false });
        await app.WaitForTabsAsync(5);
        Assert.Single(await WindowsAsync(app));
        Assert.Equal(files[..5].Select(Path.GetFileName), await TabTests.NamesAsync(app));

        // ファイルごとの起動 (転送) は、同時に起動しても同じウィンドウのタブになる。
        await Task.WhenAll(files[5..].Select(f => ctx.LaunchAndWaitAsync(new AppOptions { Files = [f], NewInstance = false }, TimeSpan.FromSeconds(30))));
        await app.WaitForTabsAsync(10);
        Assert.Single(await WindowsAsync(app));
        Assert.Equal(1, ProcessCount(start));
    });

    // ---- UI-31 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-31-01")]
    public Task Two_windows_with_ten_tabs_are_restored() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: ウィンドウ 1 (0, 0)、1280 × 800 に file01〜06、ウィンドウ 2 (600, 200)、1000 × 700 に file07〜10。
        // 各タブのカーソルは 0x10 × タブの番号。
        string[] files = TabTests.Files(ctx, 10);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files[..6] });
        await app.WaitForTabsAsync(6);
        await NewWindowAsync(app, 2);
        foreach (string f in files[6..])
        {
            await SendToAsync(app, 1, "uiOpen", new JsonObject { ["path"] = f });
        }

        await SendToAsync(app, 0, "moveWindow", new JsonObject { ["x"] = 0, ["y"] = 0 });
        await SendToAsync(app, 0, "resizePhysical", new JsonObject { ["width"] = 1280, ["height"] = 800 });
        await SendToAsync(app, 1, "moveWindow", new JsonObject { ["x"] = 600, ["y"] = 200 });
        await SendToAsync(app, 1, "resizePhysical", new JsonObject { ["width"] = 1000, ["height"] = 700 });
        for (int n = 1; n <= 10; n++)
        {
            (int window, int index) = n <= 6 ? (0, n - 1) : (1, n - 7);
            await SendToAsync(app, window, "selectTab", new JsonObject { ["index"] = index });
            await SendToAsync(app, window, "goto", new JsonObject { ["offset"] = 0x10 * n });
        }

        await SendToAsync(app, 0, "selectTab", new JsonObject { ["index"] = 2 });
        await SendToAsync(app, 1, "selectTab", new JsonObject { ["index"] = 1 });
        JsonArray before = await WindowsAsync(app);

        // 1〜2. ファイル > 終了 で終わり、同じ設定フォルダで起動する。
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        AppSession again = await ctx.StartAsync();
        await WaitForWindowsAsync(again, 2);

        // 3. ウィンドウの位置・大きさ、タブの並び、アクティブなタブ、各タブのカーソル位置が同じ。
        JsonArray after = await WindowsAsync(again);
        foreach (string key in new[] { "x", "y", "width", "height", "selectedIndex" })
        {
            Assert.Equal(before.Select(w => w![key]!.GetValue<int>()), after.Select(w => w![key]!.GetValue<int>()));
        }

        Assert.Equal(before.Select(w => Tabs(w!)), after.Select(w => Tabs(w!)));
        for (int n = 1; n <= 10; n++)
        {
            (int window, int index) = n <= 6 ? (0, n - 1) : (1, n - 7);
            await SendToAsync(again, window, "selectTab", new JsonObject { ["index"] = index });
            JsonObject doc = (await SendToAsync(again, window, "state"))["document"]!.AsObject();
            Assert.Equal($"file{n:00}.bin", doc["name"]!.GetValue<string>());
            Assert.Equal(0x10 * n, doc["cursor"]!.GetValue<long>());
        }
    });
}
