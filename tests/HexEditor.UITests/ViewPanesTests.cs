using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;
using static HexEditor.UITests.Infrastructure.ViewSettingsOps;

namespace HexEditor.UITests;

/// <summary>ミニマップ (VIEW-35)、画面分割 (VIEW-37)、同じドキュメントの複数ビュー (VIEW-38)、並べて表示と同期スクロール (VIEW-39)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewPanesTests
{
    private static async Task ExecuteAsync(AppSession app, string id, string? argument = null)
    {
        await app.SendAsync("execute", new JsonObject { ["id"] = id, ["argument"] = argument, ["fromPalette"] = true });
        await app.IdleAsync();
    }

    private static Task<JsonObject> ViewAsync(AppSession app, string view) => app.SendAsync("viewState", new JsonObject { ["view"] = view });

    private static Task<JsonObject> RenderViewAsync(AppSession app, string view) => app.SendAsync("renderView", new JsonObject { ["view"] = view });

    // ---- VIEW-35 ----

    private static async Task<JsonObject> MinimapAsync(AppSession app, bool complete = true)
    {
        JsonObject state = null!;
        await app.WaitUntilAsync(async () =>
        {
            state = await app.SendAsync("minimap");
            return state["visible"]!.GetValue<bool>() && (!complete || state["computed"]!.GetValue<int>() == state["total"]!.GetValue<int>());
        }, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the minimap");
        return state;
    }

    private static async Task<AppSession> MinimapAppAsync(UiTestContext ctx, string id)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData(id)] });
        await ExecuteAsync(app, "view.minimap");
        return app;
    }

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-35-01")]
    public Task Minimap_of_100_GB_is_ready_in_5_seconds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await ExecuteAsync(app, "view.minimap");
        for (int i = 0; i < 20; i++)
        {
            await app.KeyAsync("Down");
        }

        await MinimapAsync(app);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"概算の完了まで {watch.Elapsed.TotalMilliseconds:F0} ms");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-35-02")]
    public Task Entropy_is_shown_by_color_and_bar_length() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await MinimapAppAsync(ctx, "TD-VIEW-ENTROPY");
        JsonArray rows = (await MinimapAsync(app))["rows"]!.AsArray();
        long half = 32L * 1024 * 1024;
        JsonNode[] random = [.. rows.Where(r => r!["start"]!.GetValue<long>() + r["length"]!.GetValue<long>() <= half).Select(r => r!)];
        JsonNode[] zero = [.. rows.Where(r => r!["start"]!.GetValue<long>() >= half).Select(r => r!)];
        Assert.NotEmpty(random);
        Assert.NotEmpty(zero);
        Assert.All(random, r => Assert.True(r["value"]!.GetValue<double>() >= 7.9));
        Assert.All(random, r => Assert.True(r["bar"]!.GetValue<double>() >= 0.98));
        Assert.All(zero, r => Assert.Equal(0, r["value"]!.GetValue<double>()));
        Assert.All(zero, r => Assert.Equal(0, r["bar"]!.GetValue<double>()));
        string high = random[0]["color"]!.GetValue<string>();
        string low = zero[0]["color"]!.GetValue<string>();
        Assert.NotEqual(high, low);

        // 高い側は赤系、低い側は青系。
        Assert.True(Convert.ToInt32(high[1..3], 16) > Convert.ToInt32(high[5..7], 16), high);
        Assert.True(Convert.ToInt32(low[5..7], 16) > Convert.ToInt32(low[1..3], 16), low);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-35-03")]
    public Task Minimap_click_jumps_and_alt_left_returns() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await MinimapAppAsync(ctx, "TD-VIEW-ENTROPY");
        await GoToAsync(app, "0x100");
        await MinimapAsync(app);
        JsonObject state = await app.SendAsync("minimap", new JsonObject { ["action"] = "click", ["fraction"] = 0.75 });
        await app.IdleAsync();
        long cursor = await CursorAsync(app);
        JsonArray rows = state["rows"]!.AsArray();
        int pixelRow = (int)(rows.Count * 0.75);
        Assert.Equal(rows[pixelRow]!["start"]!.GetValue<long>(), cursor);
        Assert.InRange(cursor, 47L * 1024 * 1024, 49L * 1024 * 1024);
        JsonObject render = await app.RenderAsync();
        long cursorRow = cursor / 16 - render["topRow"]!.GetValue<long>();
        Assert.Equal(render["visibleRows"]!.GetValue<int>() / 3, cursorRow);
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Equal("Offset: 0x00000100", await StatusOffsetAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-35-04")]
    public Task Selection_search_and_bookmark_marks() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await MinimapAppAsync(ctx, "TD-MARKERS-1G");
        await app.GoToAsync(0x20000000);
        await app.KeyAsync("F2", ctrl: true);
        await SearchResultsTests.OpenFindAsync(app, 1, "@0000000030000000", encoding: "ASCII (7 bit)", incremental: false);
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => await CursorAsync(app) == 0x30000000, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the match");
        await app.SelectAsync(0x10000000, 0x1000000);
        await app.WaitUntilAsync(async () => (await app.SendAsync("minimap"))["marks"]!.AsArray().Any(m => m!["kind"]!.GetValue<string>() == "search"),
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the search marks");
        JsonObject state = await MinimapAsync(app, complete: false);
        double height = state["height"]!.GetValue<double>();
        JsonArray marks = state["marks"]!.AsArray();
        double TopOf(string kind) => marks.First(m => m!["kind"]!.GetValue<string>() == kind)!["top"]!.GetValue<double>() / height;
        Assert.InRange(TopOf("selection"), 0.24, 0.26);
        Assert.InRange(TopOf("bookmark"), 0.48, 0.51);
        Assert.InRange(TopOf("search"), 0.74, 0.76);
        Assert.InRange(TopOf("cursor"), 0.24, 0.27);
        Assert.Contains(marks, m => m!["kind"]!.GetValue<string>() == "viewport");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-35-05")]
    public Task Bars_show_the_difference_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await MinimapAppAsync(ctx, "TD-VIEW-ENTROPY");
        await ForceHighContrastAsync(app);
        string text = await SystemColorAsync(app, "SystemColorWindowTextColor");
        JsonArray rows = (await MinimapAsync(app))["rows"]!.AsArray();
        Assert.All(rows, r => Assert.Equal(text[^6..], r!["color"]!.GetValue<string>()[^6..]));
        long half = 32L * 1024 * 1024;
        Assert.All(rows.Where(r => r!["start"]!.GetValue<long>() + r["length"]!.GetValue<long>() <= half), r => Assert.True(r!["bar"]!.GetValue<double>() >= 0.98));
        Assert.All(rows.Where(r => r!["start"]!.GetValue<long>() >= half), r => Assert.Equal(0, r!["bar"]!.GetValue<double>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-35-06")]
    public Task Exact_calculation_is_cancellable() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await MinimapAppAsync(ctx, "TD-SPARSE-100G");
        await MinimapAsync(app, complete: false);
        await ExecuteAsync(app, "view.minimapExact");
        const string name = "Calculating the minimap";
        static int Running(JsonObject state) => state["operations"]!.AsArray()
            .Count(o => o!["name"]!.GetValue<string>() == name && o["state"]!.GetValue<string>() is "Running" or "Pending");
        await app.WaitUntilAsync(async () => Running(await app.StateAsync()) > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the minimap operation");
        await app.UiaInvokeAsync("Status_Operations");
        await app.UiaInvokeAsync("Operations_Cancel");
        await app.WaitUntilAsync(async () => Running(await app.StateAsync()) == 0, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the cancellation");
        await app.SendAsync("hideContextMenu");
        JsonObject state = await app.SendAsync("minimap");
        Assert.False(state["exact"]!.GetValue<bool>());
        await app.KeyAsync("Down");
        Assert.Equal(16, await CursorAsync(app));
    });

    // ---- VIEW-37 ----

    private static async Task<JsonObject> PanesAsync(AppSession app) => await app.SendAsync("panes");

    private static async Task SplitAsync(AppSession app)
    {
        await app.KeyAsync("220", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(2, (await PanesAsync(app))["views"]!.AsArray().Count);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-01")]
    public Task Ctrl_backslash_splits_and_unsplits() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SplitAsync(app);
        JsonArray views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.True(views[0]!["bottom"]!.GetValue<double>() < views[1]!["top"]!.GetValue<double>());
        await app.KeyAsync("220", ctrl: true);
        await app.IdleAsync();
        Assert.Single((await PanesAsync(app))["views"]!.AsArray());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-02")]
    public Task Panes_have_independent_cursors() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SplitAsync(app);
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await app.KeyAsync("Home", ctrl: true);
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await app.KeyAsync("End", ctrl: true);
        await app.KeyAsync("Left", count: 3);
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await app.KeyAsync("Right", count: 2);
        await app.IdleAsync();
        JsonArray views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.Equal(2, views[0]!["cursor"]!.GetValue<long>());
        Assert.Equal(0, views[0]!["topRow"]!.GetValue<long>());
        Assert.Equal(0xFFFFD, views[1]!["cursor"]!.GetValue<long>());
        Assert.True(views[1]!["topRow"]!.GetValue<long>() > 0xF000);
        Assert.Equal("Offset: 0x00000002", await StatusOffsetAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-03")]
    public Task Overwrite_in_one_pane_shows_in_the_other() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SplitAsync(app);
        foreach (int pane in new[] { 1, 0 })
        {
            await app.SendAsync("focusPane", new JsonObject { ["pane"] = pane });
            await app.SendAsync("goto", new JsonObject { ["offset"] = 0x100 });
        }

        await app.SendAsync("click", new JsonObject { ["offset"] = 0x105 });
        await app.TypeAsync("AA");
        JsonObject render = await RenderViewAsync(app, "pane2");
        JsonObject cell = CellOf(render, 0x105)!;
        Assert.Equal("AA", cell["hex"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-04")]
    public Task Insert_at_the_start_keeps_the_other_pane_still() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SplitAsync(app);
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x8000 });
        await ScrollToAsync(app, "pane2", 0x800);
        JsonObject before = await RenderViewAsync(app, "pane2");

        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await app.KeyAsync("Home", ctrl: true);
        await app.KeyAsync("Insert");
        await app.TypeAsync(string.Concat(Enumerable.Repeat("00", 16)));
        await app.IdleAsync();
        Assert.Equal(0x100010, (await ViewAsync(app, "pane2"))["length"]!.GetValue<long>());
        JsonObject after = await RenderViewAsync(app, "pane2");
        Assert.Equal(0x8010, (await ViewAsync(app, "pane2"))["topOffset"]!.GetValue<long>());
        string Lines(JsonObject r) => string.Join('\n', r["rows"]!.AsArray().Take(10).Select(row => RowHex(row!.AsObject())));
        Assert.Equal(Lines(before), Lines(after));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-05")]
    public Task Synchronized_pane_scrolling() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await SplitAsync(app);
        await app.SendAsync("viewKey", new JsonObject { ["view"] = "pane2", ["key"] = "Down", ["ctrl"] = true, ["count"] = 100 });
        await ExecuteAsync(app, "view.splitSync");
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await WheelAsync(app, -120, count: 2);
        await app.IdleAsync();
        JsonArray views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.Equal(6, views[0]!["topRow"]!.GetValue<long>());
        Assert.Equal(106, views[1]!["topRow"]!.GetValue<long>());
        await app.SendAsync("viewKey", new JsonObject { ["view"] = "pane2", ["key"] = "Up", ["ctrl"] = true, ["count"] = 4 });
        views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.Equal(2, views[0]!["topRow"]!.GetValue<long>());
        Assert.Equal(102, views[1]!["topRow"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-37-06")]
    public Task Split_from_the_menu_and_the_palette() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewSplitVertical");
        JsonArray views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.Equal(2, views.Count);
        Assert.True(views[0]!["right"]!.GetValue<double>() < views[1]!["left"]!.GetValue<double>());
        await MenuAsync(app, "Command_ViewSplitRemove");
        Assert.Single((await PanesAsync(app))["views"]!.AsArray());
        await ExecuteAsync(app, "view.splitHorizontal");
        views = (await PanesAsync(app))["views"]!.AsArray();
        Assert.Equal(2, views.Count);
        Assert.True(views[0]!["bottom"]!.GetValue<double>() < views[1]!["top"]!.GetValue<double>());
        await ExecuteAsync(app, "view.splitRemove");
        Assert.Single((await PanesAsync(app))["views"]!.AsArray());
    });

    /// <summary>VIEW-37 の仕様 10: 分割の状態 (向き・各ペインの位置) は付随データに保存し、開き直すと戻る。</summary>
    [Fact]
    public Task Split_state_is_restored_when_the_file_is_reopened() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await ExecuteAsync(app, "view.splitVertical");
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x4000 });
        await app.IdleAsync();
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);

        await app.OpenAsync(path);
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();
        JsonObject panes = await PanesAsync(app);
        Assert.True(panes["split"]!.GetValue<bool>());
        Assert.True(panes["sideBySide"]!.GetValue<bool>());
        JsonArray views = panes["views"]!.AsArray();
        Assert.Equal(2, views.Count);
        Assert.Equal(0x4000, views[1]!["cursor"]!.GetValue<long>());
    });

    /// <summary>VIEW-42 の仕様 3: 分割した 2 つ目のペインで変えた表示設定も、ドキュメントごとの設定として保存する (分割を解除した後も)。</summary>
    [Fact]
    public Task View_settings_changed_in_the_second_pane_are_saved() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await SplitAsync(app);
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await ExecuteAsync(app, "view.bytesPerRow32");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.OpenAsync(path);
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();
        Assert.Equal(32, (await ViewAsync(app, "pane2"))["bytesPerRow"]!.GetValue<int>());

        // 2 つ目のペインを残して分割を解除し、残ったペインで変える。
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await ExecuteAsync(app, "view.splitRemove");
        await ExecuteAsync(app, "view.bytesPerRow8");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.OpenAsync(path);
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();
        Assert.Equal(8, (await ViewAsync(app, "active"))["bytesPerRow"]!.GetValue<int>());
    });

    /// <summary>VIEW-37 の仕様 3: 付随データから戻した分割でも、「分割したペインの表示設定をそろえる」が効く。</summary>
    [Fact]
    public Task Restored_split_keeps_pane_settings_in_sync() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.split.syncSettings"] = true });
        string path = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [path] });
        await SplitAsync(app);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.OpenAsync(path);
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();
        Assert.True((await PanesAsync(app))["split"]!.GetValue<bool>());
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await ExecuteAsync(app, "view.bytesPerRow32");
        Assert.Equal(32, (await ViewAsync(app, "pane2"))["bytesPerRow"]!.GetValue<int>());
    });

    /// <summary>VIEW-37 の「エラー」: 2 ペインの最小の大きさを確保できない向きの分割だけを無効にする。</summary>
    [Fact]
    public Task Split_is_disabled_per_orientation_when_the_window_is_too_small() => UiTestContext.RunAsync(async ctx =>
    {
        // 大きな文字 (30 pt) と低いウィンドウで、上下の 2 ペイン (各 5 行分) が入らないようにする。左右は入る。
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.font.size"] = 30 });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("resize", new JsonObject { ["width"] = 1000, ["height"] = 420 });
        await app.IdleAsync();
        await app.SendAsync("refreshMenus");
        JsonArray commands = (await app.SendAsync("commands"))["items"]!.AsArray();
        bool Enabled(string id) => commands.Single(c => c!["id"]!.GetValue<string>() == id)!["enabled"]!.GetValue<bool>();
        Assert.False(Enabled("view.splitHorizontal"));
        Assert.True(Enabled("view.splitVertical"));
        await ExecuteAsync(app, "view.splitVertical");
        Assert.True((await PanesAsync(app))["split"]!.GetValue<bool>());
    });

    // ---- VIEW-38 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-38-01")]
    public Task New_view_shows_another_position() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewNewView");
        JsonArray tabs = (await app.SendAsync("tabs"))["tabs"]!.AsArray();
        Assert.Equal(2, tabs.Count);
        Assert.EndsWith(": 2", tabs[1]!["title"]!.GetValue<string>());
        await GoToAsync(app, "0x80000");
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        Assert.Equal("Offset: 0x00000000", await StatusOffsetAsync(app));
    });

    /// <summary>VIEW-38 の「呼び出し」: タブの右クリックメニュー「新しいビューで開く」。</summary>
    [Fact]
    public Task New_view_from_the_tab_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("tabMenu", new JsonObject { ["index"] = 0, ["item"] = "TabMenu_NewView" });
        await app.IdleAsync();
        JsonArray tabs = (await app.SendAsync("tabs"))["tabs"]!.AsArray();
        Assert.Equal(2, tabs.Count);
        Assert.EndsWith(": 2", tabs[1]!["title"]!.GetValue<string>());

        // 同じドキュメントのタブには「右に並べて表示」を使えない (別のドキュメントだけ)。
        JsonArray items = (await app.SendAsync("tabMenuItems", new JsonObject { ["index"] = 0 }))["items"]!.AsArray();
        Assert.False(items.Single(i => i!["id"]!.GetValue<string>() == "TabMenu_ShowToRight")!["enabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-38-02")]
    public Task Edits_are_shared_and_undo_moves_only_its_view() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewNewView");
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x10 });
        await app.TypeAsync("BB");
        long secondCursor = await CursorAsync(app);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        JsonObject cell = CellOf(await app.RenderAsync(), 0x10)!;
        Assert.Equal("BB", cell["hex"]!.GetValue<string>());
        Assert.Equal("solid", cell["underline"]!.GetValue<string>());
        await app.KeyAsync("Z", ctrl: true);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        Assert.Equal("10", CellOf(await app.RenderAsync(), 0x10)!["hex"]!.GetValue<string>());
        Assert.Equal(secondCursor, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-38-03")]
    public Task Only_the_last_view_asks_to_save() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await MenuAsync(app, "Command_ViewNewView");
        await app.SendAsync("click", new JsonObject { ["offset"] = 0 });
        await app.TypeAsync("CC");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(1);
        Assert.False(await app.IsShownAsync("CloseDialog"));
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForAsync("CloseDialog", UiTest.Scaled(TimeSpan.FromSeconds(10)));
        await app.InvokeDialogButtonAsync("Don't save");
        await app.WaitForTabsAsync(0);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-38-04")]
    public Task View_moved_to_another_window_shares_edits() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewNewView");
        await app.SendAsync("execute", new JsonObject { ["id"] = "tab.moveToNewWindow" });
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.IdleAsync();
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x20, ["window"] = 1 });
        await app.SendAsync("text", new JsonObject { ["text"] = "DD", ["window"] = 1 });
        await app.IdleAsync();
        Assert.Equal("DD", CellOf(await app.RenderAsync(), 0x20)!["hex"]!.GetValue<string>());
    });

    // ---- VIEW-39 ----

    private static async Task<AppSession> SideBySideAsync(UiTestContext ctx, string right = "TD-VIEW-SEQ-MOD")
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData(right)] });
        await app.WaitForTabsAsync(2);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await ExecuteAsync(app, "view.sideBySide", "1");
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-39-01")]
    public Task Side_by_side_follows_the_same_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        await app.KeyAsync("PageDown", count: 3);
        await app.IdleAsync();
        JsonObject left = await ViewAsync(app, "active");
        JsonObject state = await app.SendAsync("sideBySide");
        JsonObject right = state["views"]![0]!.AsObject();
        Assert.True(right["left"]!.GetValue<double>() > 0);
        Assert.Equal(left["topOffset"]!.GetValue<long>(), right["topOffset"]!.GetValue<long>());
        Assert.Equal(left["cursor"]!.GetValue<long>(), right["cursor"]!.GetValue<long>());
        Assert.Equal("Sync", state["status"]!.GetValue<string>());
        Assert.True(await app.IsShownAsync("Status_Sync"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-39-02")]
    public Task Keep_difference_sync() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        await MenuAsync(app, "Command_ViewSyncOff");
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x100 });
        await ScrollToAsync(app, "active", 0x10);
        await app.SendAsync("viewKey", new JsonObject { ["view"] = "side1", ["key"] = "Home", ["ctrl"] = true });
        await app.SendAsync("viewKey", new JsonObject { ["view"] = "side1", ["key"] = "Down", ["count"] = 0x30 });
        await ScrollToAsync(app, "side1", 0x30);
        await MenuAsync(app, "Command_ViewSyncKeepDifference");
        await app.KeyAsync("Down", count: 4);
        await WheelAsync(app, -120);
        await app.IdleAsync();
        JsonObject left = await ViewAsync(app, "active");
        JsonObject right = await ViewAsync(app, "side1");
        Assert.Equal(0x140, left["cursor"]!.GetValue<long>());
        Assert.Equal(0x340, right["cursor"]!.GetValue<long>());
        Assert.Equal(0x200, right["topOffset"]!.GetValue<long>() - left["topOffset"]!.GetValue<long>());
    });

    /// <summary>ビューの一番上の行を指定の行にする (Ctrl+↑↓ でスクロールする)。</summary>
    private static async Task ScrollToAsync(AppSession app, string view, long row)
    {
        long top = (await ViewAsync(app, view))["topRow"]!.GetValue<long>();
        if (top != row)
        {
            await app.SendAsync("viewKey", new JsonObject { ["view"] = view, ["key"] = row > top ? "Down" : "Up", ["ctrl"] = true, ["count"] = Math.Abs(row - top) });
        }

        Assert.Equal(row, (await ViewAsync(app, view))["topRow"]!.GetValue<long>());
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-39-03")]
    public Task Different_bytes_per_row_sync_by_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await MenuAsync(app, "Command_ViewBytesPerRow32");
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await GoToAsync(app, "0x12340");
        JsonObject left = await ViewAsync(app, "active");
        JsonObject right = await ViewAsync(app, "side1");
        Assert.Equal(0x12340, left["cursor"]!.GetValue<long>());
        Assert.Equal(0x12340, right["cursor"]!.GetValue<long>());
        Assert.Equal(32, right["bytesPerRow"]!.GetValue<int>());
        Assert.Equal(left["topOffset"]!.GetValue<long>() / 32 * 32, right["topOffset"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-39-04")]
    public Task Highlight_differences_in_the_shown_range() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        await MenuAsync(app, "Command_ViewSyncDifferences");
        foreach (string view in new[] { "active", "side1" })
        {
            JsonArray segments = (await app.SendAsync("highlightsView", new JsonObject { ["view"] = view }))["segments"]!.AsArray();
            long[] differing = [.. segments.Where(s => s!["layer"]!.GetValue<int>() == 11 && s["column"]!.GetValue<string>() == "hex")
                .SelectMany(s => Enumerable.Range(0, (int)(s!["last"]!.GetValue<long>() - s["first"]!.GetValue<long>() + 1)).Select(i => s["first"]!.GetValue<long>() + i))
                .Where(o => o < 0x50)];
            Assert.Equal([0x10, 0x25, 0x3F], differing);
        }

        Assert.False(await app.IsShownAsync("CompareResults"));
    });

    /// <summary>VIEW-39 の仕様 7: 並べたドキュメントと同期のモードは、セッションの復元で戻る。</summary>
    [Fact]
    public Task Side_by_side_is_restored_with_the_session() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string left = ctx.CopyTestData("TD-SEQ-1M");
        string right = ctx.CopyTestData("TD-VIEW-SEQ-MOD");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [left, right] });
        await app.WaitForTabsAsync(2);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await ExecuteAsync(app, "view.sideBySide", "1");
        await MenuAsync(app, "Command_ViewSyncKeepDifference");
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(UiTest.Scaled(TimeSpan.FromSeconds(20)));

        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        await again.WaitForTabsAsync(2);
        await again.IdleAsync();
        JsonObject state = await again.SendAsync("sideBySide");
        Assert.True(state["active"]!.GetValue<bool>());
        Assert.Equal("KeepDifference", state["mode"]!.GetValue<string>());
        Assert.Single(state["partners"]!.AsArray());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-39-05")]
    public Task Shorter_file_stops_at_its_end() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx, "TD-BYTES-256");
        await GoToAsync(app, "0x5000");
        Assert.Equal(0x100, (await ViewAsync(app, "side1"))["cursor"]!.GetValue<long>());
        await app.KeyAsync("Home", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(0, (await ViewAsync(app, "side1"))["cursor"]!.GetValue<long>());
    });

    /// <summary>VIEW-39 の「呼び出し」: タブの右クリックメニュー「右に並べて表示」は、操作中のタブの右にそのタブを並べる。</summary>
    [Fact]
    public Task Show_to_the_right_from_the_tab_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-VIEW-SEQ-MOD")] });
        await app.WaitForTabsAsync(2);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        JsonArray items = (await app.SendAsync("tabMenuItems", new JsonObject { ["index"] = 0 }))["items"]!.AsArray();
        Assert.False(items.Single(i => i!["id"]!.GetValue<string>() == "TabMenu_ShowToRight")!["enabled"]!.GetValue<bool>());
        await app.SendAsync("tabMenu", new JsonObject { ["index"] = 1, ["item"] = "TabMenu_ShowToRight" });
        await app.IdleAsync();
        JsonObject state = await app.SendAsync("sideBySide");
        Assert.True(state["active"]!.GetValue<bool>());
        Assert.Single(state["partners"]!.AsArray());
        Assert.Equal("SameOffset", state["mode"]!.GetValue<string>());
    });

    /// <summary>VIEW-39 の「画面」: 鎖のボタンは同期のモードを示し、押すとモードを選べる。</summary>
    [Fact]
    public Task Chain_button_shows_and_selects_the_sync_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        Assert.Equal("Same offset", await app.UiaNameAsync("SideBySide_SyncMode1"));
        (await app.WaitForAsync("SideBySide_Sync1")).Patterns.ExpandCollapse.Pattern.Expand();
        await app.WaitForAsync("SideBySide_Sync1_KeepDifference");
        FlaUI.Core.AutomationElements.AutomationElement item = await app.WaitForAsync("SideBySide_Sync1_KeepDifference");
        if (item.Patterns.Invoke.IsSupported)
        {
            item.Patterns.Invoke.Pattern.Invoke();
        }
        else if (item.Patterns.SelectionItem.IsSupported)
        {
            item.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            item.Patterns.Toggle.Pattern.Toggle();
        }

        await app.IdleAsync();
        Assert.Equal("KeepDifference", (await app.SendAsync("sideBySide"))["mode"]!.GetValue<string>());
        Assert.Equal("Keep the difference", await app.UiaNameAsync("SideBySide_SyncMode1"));
    });

    /// <summary>VIEW-39: 左のドキュメントを分割したら、同期は操作中のペインに従う。</summary>
    [Fact]
    public Task Sync_follows_the_active_pane_of_the_left_document() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await SideBySideAsync(ctx);
        await ExecuteAsync(app, "view.splitHorizontal");
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 1 });
        await app.IdleAsync();
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x5000 });
        await app.IdleAsync();
        Assert.Equal(0x5000, (await ViewAsync(app, "side1"))["cursor"]!.GetValue<long>());
        await app.SendAsync("focusPane", new JsonObject { ["pane"] = 0 });
        await app.IdleAsync();
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x300 });
        await app.IdleAsync();
        Assert.Equal(0x300, (await ViewAsync(app, "side1"))["cursor"]!.GetValue<long>());
    });
}
