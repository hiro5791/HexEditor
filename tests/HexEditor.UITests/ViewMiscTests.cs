using System.Diagnostics;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;
using static HexEditor.UITests.Infrastructure.ViewSettingsOps;

namespace HexEditor.UITests;

/// <summary>
/// ツールチップ (VIEW-07)、マウスの戻る・進む (VIEW-31)、表示設定の保存 (VIEW-42)、ズーム (VIEW-43)、配色 (UI-28)、フォント (UI-29)、
/// UI オートメーション (UI-50)、読み上げ (UI-51)。設定画面 (UI-22) とズームの操作 (UI-08) は別の機能のため、同じ処理をテスト用の命令で行う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ViewMiscTests
{
    // ---- VIEW-07 ----

    private static async Task<JsonObject> HoverAsync(AppSession app, (double X, double Y) point, int waitMs)
    {
        await PointerAsync(app, "move", point);
        await Task.Delay(waitMs);
        return (await app.RenderAsync())["toolTip"]!.AsObject();
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-07-01")]
    public Task Tool_tip_shows_offset_and_values() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x1F00");
        JsonObject render = await app.RenderAsync();
        (double X, double Y) point = CellPoint(render, 0x1F4F);

        // 2. 300 ms ではまだ出ない。
        Assert.False((await HoverAsync(app, point, 300))["open"]!.GetValue<bool>());

        // 3. 合計 700 ms で出る。
        await Task.Delay(400);
        JsonObject tip = (await app.RenderAsync())["toolTip"]!.AsObject();
        Assert.True(tip["open"]!.GetValue<bool>());
        string text = tip["text"]!.GetValue<string>();
        foreach (string part in new[] { "0x1F4F (8,015)", "4F", "79", "117", "01001111" })
        {
            Assert.Contains(part, text);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-07-02")]
    public Task Tool_tip_shows_the_bookmark_name() => UiTestContext.RunAsync(async ctx =>
    {
        // ブックマーク (INSP-23) の代わりに、同じ入口 (HexView.AnnotationNames) に名前を渡す。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("annotate", new JsonObject { ["offset"] = 0x20, ["length"] = 4, ["name"] = "header" });
        JsonObject render = await app.RenderAsync();
        Assert.Contains("header", (await HoverAsync(app, CellPoint(render, 0x21), 700))["text"]!.GetValue<string>());
        JsonObject other = await HoverAsync(app, CellPoint(render, 0x30), 700);
        Assert.DoesNotContain("header", other["text"]?.GetValue<string>() ?? string.Empty);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-07-03")]
    public Task Tool_tips_can_be_turned_off() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.tooltips"] = false });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject render = await app.RenderAsync();
        foreach ((double X, double Y) point in new[] { CellPoint(render, 0x10), CellPoint(render, 0x10, text: true), OffsetColumnPoint(render, 1) })
        {
            Assert.False((await HoverAsync(app, point, 1000))["open"]!.GetValue<bool>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-07-04")]
    public Task Tool_tip_uses_german_digit_grouping() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")], Hooks = new JsonObject { ["culture"] = "de-DE" } });
        await GoToAsync(app, "0x1F00");
        string text = (await HoverAsync(app, CellPoint(await app.RenderAsync(), 0x1F00), 700))["text"]!.GetValue<string>();
        Assert.Contains("0x1F00 (7.936)", text);
    });

    // ---- VIEW-31 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-31-05")]
    public Task Mouse_back_and_forward_buttons() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x1000");
        await GoToAsync(app, "0x2000");
        await app.SendAsync("mouseHistory", new JsonObject { ["back"] = true });
        Assert.Contains("0x00001000", await StatusOffsetAsync(app));
        await app.SendAsync("mouseHistory", new JsonObject { ["back"] = false });
        Assert.Contains("0x00002000", await StatusOffsetAsync(app));
    });

    [Fact]
    public Task Diagnostics_overlay_is_enabled_from_the_settings() => UiTestContext.RunAsync(async ctx =>
    {
        // VIEW-04 の仕様 8: 診断表示は既定オフ。設定 (詳細 > 診断) で有効にする。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.False(await app.IsShownAsync("HexViewDiagnostics"));

        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["diagnostics.hexView.overlay"] = true });
        AppSession other = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await other.IdleAsync();
        await other.WaitUntilAsync(() => other.IsShownAsync("HexViewDiagnostics"), TimeSpan.FromSeconds(10), "diagnostics overlay");
    });

    [Fact]
    public Task Scroll_bar_marks_for_selection_and_bookmarks_follow_the_settings() => UiTestContext.RunAsync(async ctx =>
    {
        // VIEW-02 の仕様 9: 既定はカーソル位置 (と検索結果) だけ。設定で選択範囲とブックマークの印も出せる。
        static async Task<List<string>> KindsAsync(AppSession app) =>
            [.. (await app.RenderAsync())["scrollMarkers"]!.AsArray().Select(m => m!["kind"]!.GetValue<string>())];

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("bookmarksAdd", new JsonObject { ["count"] = 3, ["step"] = 0x40000, ["length"] = 4 });
        await app.SendAsync("select", new JsonObject { ["start"] = 0x80000, ["length"] = 0x10000 });
        await app.IdleAsync();
        Assert.Equal(["cursor"], await KindsAsync(app));

        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["view.scrollBar.selectionMark"] = true, ["view.scrollBar.bookmarkMarks"] = true });
        AppSession other = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await other.SendAsync("bookmarksAdd", new JsonObject { ["count"] = 3, ["step"] = 0x40000, ["length"] = 4 });
        await other.SendAsync("select", new JsonObject { ["start"] = 0x80000, ["length"] = 0x10000 });
        await other.IdleAsync();
        JsonArray marks = (await other.RenderAsync())["scrollMarkers"]!.AsArray();
        Assert.Equal(3, marks.Count(m => m!["kind"]!.GetValue<string>() == "bookmark"));
        JsonNode selection = Assert.Single(marks, m => m!["kind"]!.GetValue<string>() == "selection")!;
        JsonNode cursor = Assert.Single(marks, m => m!["kind"]!.GetValue<string>() == "cursor")!;

        // 選択範囲 (ファイルの 1/2 から 1/16) の印は、スクロールバーの中央付近にある。
        double barTop = selection["top"]!.GetValue<double>();
        Assert.True(barTop > 0 && selection["height"]!.GetValue<double>() >= 2);
        Assert.True(cursor["top"]!.GetValue<double>() >= barTop - 3);
    });

    [Fact]
    public Task History_list_is_available_in_the_command_palette() => UiTestContext.RunAsync(async ctx =>
    {
        // VIEW-31 の仕様 8 と「呼び出し」: コマンドパレット「移動: 履歴の一覧」で最新の履歴を選んで移れる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        foreach (string target in new[] { "0x1000", "0x2000", "0x3000" })
        {
            await GoToAsync(app, target);
        }

        await app.SendAsync("execute", new JsonObject { ["id"] = "go.history" });
        JsonObject state = await app.SendAsync("palette");
        Assert.True(state["open"]!.GetValue<bool>());
        JsonArray entries = state["entries"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.StartsWith("history:", e!["key"]!.GetValue<string>()));
        Assert.Contains("2000", entries[0]!["title"]!.GetValue<string>());

        await app.SendAsync("paletteEnter");
        await app.IdleAsync();
        Assert.Equal(0x2000, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-31-01")]
    public Task History_menu_items_follow_back_and_forward() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        foreach (string target in new[] { "0x1000", "0x2000", "0x3000" })
        {
            await GoToAsync(app, target);
        }

        foreach (long expected in new long[] { 0x2000, 0x1000, 0 })
        {
            await app.KeyAsync("Left", alt: true);
            await app.IdleAsync();
            Assert.Equal(expected, await CursorAsync(app));
        }

        foreach (long expected in new long[] { 0x1000, 0x2000, 0x3000 })
        {
            await app.KeyAsync("Right", alt: true);
            await app.IdleAsync();
            Assert.Equal(expected, await CursorAsync(app));
        }

        Assert.True((await MenuItemAsync(app, "Command_GoBack"))["enabled"]!.GetValue<bool>());
        Assert.False((await MenuItemAsync(app, "Command_GoForward"))["enabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-31-03")]
    public Task History_list_has_one_item_after_arrow_keys() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x1000");
        await app.KeyAsync("Down", count: 50);
        await app.KeyAsync("Right", count: 20);
        await app.KeyAsync("PageDown", count: 5);
        await WheelAsync(app, -120, count: 10);
        await app.IdleAsync();
        JsonObject history = await MenuItemAsync(app, "Command_GoHistory");
        Assert.Single(history["items"]!.AsArray());
        Assert.StartsWith("0x00000000", history["items"]![0]!.GetValue<string>());
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Contains("0x00000000", await StatusOffsetAsync(app));
    });

    // ---- VIEW-42 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-42-01")]
    public Task Per_document_settings_are_restored() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewBytesPerRow32");
        await app.CommandAsync("Command_Close");
        await ExitAsync(app);

        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.Equal(32, (await again.RenderAsync())["bytesPerRow"]!.GetValue<int>());
        await again.OpenAsync(ctx.TestData("TD-RANDOM-16M"));
        await again.IdleAsync();

        // 新しいタブの Hex ビューが作られるまで待つ。
        int bytesPerRow = 0;
        await again.WaitUntilAsync(async () =>
        {
            try
            {
                bytesPerRow = (await again.RenderAsync())["bytesPerRow"]!.GetValue<int>();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10), "the new tab's hex view");
        Assert.Equal(16, bytesPerRow);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-42-02")]
    public Task Save_as_default_excludes_document_specific_items() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await MenuAsync(app, "Command_ViewBytesPerRow32");
        await MenuAsync(app, "Command_ViewGroup4");
        await MenuAsync(app, "Command_ViewRadixDecimal");
        await SetBaseAddressAsync(app, "0x1000");
        await MenuAsync(app, "Command_ViewSaveDefault");

        static async Task AssertDefaultsAsync(AppSession app)
        {
            JsonObject view = (await ViewSettingsAsync(app))["view"]!.AsObject();
            Assert.Equal(32, view["bytesPerRow"]!.GetValue<int>());
            Assert.Equal(4, view["groupSize"]!.GetValue<int>());
            Assert.Equal("decimal", view["radix"]!.GetValue<string>());
            Assert.Equal(0UL, view["baseAddress"]!.GetValue<ulong>());
        }

        // 3. 新しく開いたファイルに既定値が使われる (ベースアドレスは含まない)。
        await app.OpenAsync(ctx.TestData("TD-RANDOM-16M"));
        await app.IdleAsync();
        await AssertDefaultsAsync(app);

        // 4. 「既定に戻す」で全体の既定値に戻る。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await MenuAsync(app, "Command_ViewResetDefault");
        JsonObject reset = (await ViewSettingsAsync(app))["view"]!.AsObject();
        Assert.Equal(32, reset["bytesPerRow"]!.GetValue<int>());
        Assert.Equal(0UL, reset["baseAddress"]!.GetValue<ulong>());

        // 5. 再起動後も使われる (設定ファイルに書かれている)。
        string settingsFile = Path.Combine(profile, "settings.json");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(settingsFile) && File.ReadAllText(settingsFile).Contains("view.defaults")),
            TimeSpan.FromSeconds(5), "the defaults in settings.json");
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-BYTES-256")] });
        await AssertDefaultsAsync(again);
    });

    // ---- VIEW-43 ----

    private static async Task<(double Font, double Cell, double Row, double Ruler)> SizesAsync(AppSession app)
    {
        JsonObject render = await app.RenderAsync();
        return (render["fontSize"]!.GetValue<double>(), render["cellWidth"]!.GetValue<double>(), render["rowHeight"]!.GetValue<double>(),
            render["ruler"]!["fontSize"]!.GetValue<double>());
    }

    private static async Task ZoomAsync(AppSession app, double zoom, double? pointerY = null)
    {
        var args = new JsonObject { ["zoom"] = zoom };
        if (pointerY is { } y)
        {
            args["pointerY"] = y;
        }

        await app.SendAsync("zoom", args);
        await app.IdleAsync();
    }

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-43-01")]
    public Task Zoom_scales_font_cell_and_row() => UiTestContext.RunAsync(async ctx =>
    {
        // Ctrl+= / Ctrl+0 (UI-08) の代わりに、同じ倍率 (110% を 2 回 = 121%、100%) をテスト用の命令で設定する。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        var before = await SizesAsync(app);
        await ZoomAsync(app, 1.21);
        var zoomed = await SizesAsync(app);
        Assert.True(zoomed.Font > before.Font && zoomed.Cell > before.Cell && zoomed.Row > before.Row && zoomed.Ruler > before.Ruler, $"{before} -> {zoomed}");
        await ZoomAsync(app, 1.0);
        Assert.Equal(before, await SizesAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-43-02")]
    public Task Wheel_zoom_keeps_the_row_under_the_pointer() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await GoToAsync(app, "0x8000");
        JsonObject render = await app.RenderAsync();
        double height = render["rowHeight"]!.GetValue<double>();
        int visible = render["visibleRows"]!.GetValue<int>();
        double y = (visible * 3 / 4 + 0.5) * height;
        long rowStart = render["rows"]![visible * 3 / 4]!["rowStart"]!.GetValue<long>();

        await ZoomAsync(app, 1.2, y);
        render = await app.RenderAsync();
        JsonObject row = render["rows"]!.AsArray().First(r => r!["rowStart"]!.GetValue<long>() == rowStart)!.AsObject();
        double top = row["index"]!.GetValue<int>() * render["rowHeight"]!.GetValue<double>() - render["subRowOffset"]!.GetValue<double>();
        Assert.InRange(y, top, top + render["rowHeight"]!.GetValue<double>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-43-03")]
    public Task Key_zoom_keeps_the_cursor_row_height() => UiTestContext.RunAsync(async ctx =>
    {
        // ファイルの先頭では縮小したときに一番上の行を 0 より上にできないため、途中の位置で確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10000);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        int half = render["visibleRows"]!.GetValue<int>() / 2;
        long cursorRow = 0x10000 / 16 - render["topRow"]!.GetValue<long>();
        await app.KeyAsync(cursorRow < half ? "Down" : "Up", count: (int)Math.Abs(half - cursorRow));
        await app.IdleAsync();
        double Center(JsonObject r) => r["caret"]!["top"]!.GetValue<double>() + r["caret"]!["height"]!.GetValue<double>() / 2;
        double center = Center(await app.RenderAsync());
        foreach (double zoom in new[] { 1.1, 1.21, 1.331, 1.21, 1.1, 1.0, 0.9, 0.81 })
        {
            await ZoomAsync(app, zoom);
            render = await app.RenderAsync();
            Assert.True(Math.Abs(Center(render) - center) < render["rowHeight"]!.GetValue<double>(), $"zoom {zoom}: {Center(render)} vs {center}");
        }
    });

    // TC-VIEW-43-04 (Hex 表示のズームと画面全体のズームの違い) は ZoomTests (UI-08) にある。

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-43-05")]
    public Task Zoom_reduces_auto_bytes_per_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.ResizeAsync(1920, 1000);
        await MenuAsync(app, "Command_ViewBytesPerRowAuto");
        await Task.Delay(300);
        await app.IdleAsync();
        int before = (await app.RenderAsync())["bytesPerRow"]!.GetValue<int>();
        await ZoomAsync(app, 1.331);
        await app.EventuallyAsync(async () => Assert.True((await app.RenderAsync())["bytesPerRow"]!.GetValue<int>() < before));
        await ZoomAsync(app, 1.0);
        await app.EventuallyAsync(async () => Assert.Equal(before, (await app.RenderAsync())["bytesPerRow"]!.GetValue<int>()));
    });

    // ---- UI-28 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-28-01")]
    public Task Color_scheme_applies_immediately() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: ウィンドウ 1 に TD-SEQ-1M、ウィンドウ 2 に TD-BYTES-256。
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["ui.theme"] = "light" });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        int pid = app.Pid;
        await app.KeyAsync("N", ctrl: true, shift: true);
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.SendAsync("uiOpen", new JsonObject { ["window"] = 1, ["path"] = ctx.TestData("TD-BYTES-256") });

        // 1. ウィンドウ 1 で「ソラライズド」を選ぶ。
        await app.SendAsync("invoke", new JsonObject { ["window"] = 0, ["id"] = "Command_ViewColorScheme_solarized" });

        // 2. 両方のウィンドウの Hex ビューの背景がソラライズドの背景色になる (再起動なし)。
        foreach (int window in new[] { 0, 1 })
        {
            await app.WaitUntilAsync(async () =>
            {
                try
                {
                    return (await app.SendAsync("render", new JsonObject { ["window"] = window }))["background"]?.GetValue<string>() == "#FFFDF6E3";
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(10), $"the solarized background in window {window + 1}");
        }

        Assert.Equal(pid, (await app.StateAsync())["pid"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-28-02")]
    public Task Exported_scheme_looks_the_same_elsewhere() => UiTestContext.RunAsync(async ctx =>
    {
        string exported = Path.Combine(ctx.Root, "test-scheme.json");
        string profileA = ctx.NewProfile();
        WriteSettings(profileA, new JsonObject { ["ui.theme"] = "light" });
        AppSession a = await ctx.StartAsync(new AppOptions { Profile = profileA, Files = [ctx.TestData("TD-BYTES-256")] });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "duplicate", ["from"] = "default", ["name"] = "test-scheme" });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "set", ["element"] = "Background", ["color"] = "#102030" });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "set", ["element"] = "HexText", ["color"] = "#F0E0D0" });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "save" });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "select", ["name"] = "test-scheme" });
        await a.SendAsync("colorScheme", new JsonObject { ["action"] = "export", ["name"] = "test-scheme", ["path"] = exported });
        JsonObject renderA = await a.RenderAsync();
        await ExitAsync(a);

        string profileB = ctx.NewProfile();
        WriteSettings(profileB, new JsonObject { ["ui.theme"] = "light" });
        AppSession b = await ctx.StartAsync(new AppOptions { Profile = profileB, Files = [ctx.TestData("TD-BYTES-256")] });
        await b.SendAsync("colorScheme", new JsonObject { ["action"] = "import", ["path"] = exported });
        await b.SendAsync("colorScheme", new JsonObject { ["action"] = "select", ["name"] = "test-scheme" });
        JsonObject renderB = await b.RenderAsync();
        Assert.Equal("#FF102030", renderB["background"]!.GetValue<string>());
        Assert.Equal(renderA["background"]!.GetValue<string>(), renderB["background"]!.GetValue<string>());
        Assert.Equal(renderA["hexTextColor"]!.GetValue<string>(), renderB["hexTextColor"]!.GetValue<string>());
        Assert.Equal(CellOf(renderA, 0x41)!["foreground"]!.GetValue<string>(), CellOf(renderB, 0x41)!["foreground"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-28-03")]
    public Task Low_contrast_is_warned_and_saved() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.SendAsync("colorScheme", new JsonObject { ["action"] = "duplicate", ["from"] = "default", ["name"] = "low" });
        await app.SendAsync("colorScheme", new JsonObject { ["action"] = "set", ["element"] = "Background", ["color"] = "#FFFFFF", ["dark"] = false });
        JsonObject result = await app.SendAsync("colorScheme", new JsonObject { ["action"] = "set", ["element"] = "HexText", ["color"] = "#949494", ["dark"] = false });
        Assert.Contains(result["warnings"]!.AsArray(), w => w!["foreground"]!.GetValue<string>() == "HexText" && w["ratio"]!.GetValue<double>() < 4.5);
        string path = (await app.SendAsync("colorScheme", new JsonObject { ["action"] = "save" }))["path"]!.GetValue<string>();
        Assert.Equal(Path.Combine(app.Profile, "themes", "low.json"), path);
        Assert.True(File.Exists(path));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-28-04")]
    public Task Modified_byte_is_underlined_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-ZERO-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await GoToAsync(app, "0x10");
        await app.TypeAsync("FF");
        await ForceHighContrastAsync(app);
        await GoToAsync(app, "0x100");
        JsonObject render = await app.RenderAsync();
        Assert.Equal("solid", CellOf(render, 0x10)!["underline"]!.GetValue<string>());
        Assert.Equal("none", CellOf(render, 0x11)!["underline"]!.GetValue<string>());
        Assert.Equal(await SystemColorAsync(app, "SystemColorWindowTextColor"), CellOf(render, 0x10)!["foreground"]!.GetValue<string>());
    });

    // ---- VIEW-41 ----

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-41-01")]
    public Task High_contrast_distinguishes_by_shape() => UiTestContext.RunAsync(async ctx =>
    {
        // OS のハイコントラストは切り替えず、同じ描き方をテスト用の模擬 (ForcedHighContrast) で行う。
        string file = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await ForceHighContrastAsync(app);

        // 1. 0x20 を上書き、0x40〜0x47 にブックマーク、0x60〜0x64 を選択してカーソルを 0x65 (選択範囲の直後) に置く。
        await app.GoToAsync(0x20);
        await app.TypeAsync("AA");
        await app.SelectAsync(0x40, 8);
        await app.KeyAsync("F2", ctrl: true);
        await app.SelectAsync(0x60, 5);
        await app.IdleAsync();
        Assert.Equal(0x65, await CursorAsync(app));

        JsonObject render = await app.RenderAsync();
        JsonObject highlights = await app.SendAsync("highlights");
        Assert.True(render["highContrast"]!.GetValue<bool>());

        string Shape(long offset)
        {
            JsonObject cell = CellOf(render, offset)!;
            bool framed = highlights["segments"]!.AsArray().Any(s => s!["column"]!.GetValue<string>() == "hex"
                && s["first"]!.GetValue<long>() <= offset && offset <= s["last"]!.GetValue<long>() && s["border"] is not null);
            bool caret = render["caret"]!["visible"]!.GetValue<bool>()
                && Math.Abs(render["caret"]!["left"]!.GetValue<double>() - cell["hexLeft"]!.GetValue<double>()) < render["cellWidth"]!.GetValue<double>()
                && RowOf(render, offset)!["index"]!.GetValue<int>() == render["caret"]!["row"]!.GetValue<int>();
            return $"underline={cell["underline"]!.GetValue<string>()} frame={framed} selected={cell["selected"]!.GetValue<bool>()} caret={caret}";
        }

        string[] shapes = [.. new long[] { 0x20, 0x40, 0x60, 0x65, 0x80 }.Select(Shape)];
        Assert.Equal(shapes.Length, shapes.Distinct().Count());
        Assert.Equal("underline=solid frame=False selected=False caret=False", shapes[0]);
        Assert.Equal("underline=none frame=True selected=False caret=False", shapes[1]);
        Assert.Equal("underline=none frame=False selected=True caret=False", shapes[2]);
        Assert.Equal("underline=none frame=False selected=False caret=True", shapes[3]);
        Assert.Equal("underline=none frame=False selected=False caret=False", shapes[4]);

        // 2. 使われている色はすべてシステム色 (WindowText、Window、Highlight、HighlightText、GrayText、HotTrack)。
        var system = new HashSet<string>();
        foreach (string key in new[] { "SystemColorWindowTextColor", "SystemColorWindowColor", "SystemColorHighlightColor",
            "SystemColorHighlightTextColor", "SystemColorGrayTextColor", "SystemColorHotlightColor" })
        {
            system.Add(await SystemColorAsync(app, key));
        }

        Assert.Equal(await SystemColorAsync(app, "SystemColorHighlightColor"), CellOf(render, 0x60)!["background"]!.GetValue<string>());
        var used = new List<string>();
        foreach (long offset in new long[] { 0x20, 0x40, 0x60, 0x65, 0x80 })
        {
            JsonObject cell = CellOf(render, offset)!;
            used.AddRange(new[] { "foreground", "textForeground", "hexBackground", "textBackground" }
                .Select(k => cell[k]?.GetValue<string>()).OfType<string>());
        }

        used.AddRange(highlights["segments"]!.AsArray().SelectMany(s => new[] { s!["background"], s["border"] }).Select(c => c?.GetValue<string>()).OfType<string>());
        Assert.All(used, c => Assert.Contains(c, system));
    });

    // ---- UI-29 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-29-01")]
    public Task Font_list_shows_monospaced_fonts_by_default() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonObject list = await app.SendAsync("fonts", new JsonObject { ["all"] = false });
        string[] items = [.. list["items"]!.AsArray().Select(i => i!.GetValue<string>())];
        string[] mono = [.. list["monospaced"]!.AsArray().Select(i => i!.GetValue<string>())];
        Assert.All(items, i => Assert.Contains(i, mono));
        Assert.Contains("Consolas", items);
        Assert.DoesNotContain("Segoe UI", items);
        string[] all = [.. (await app.SendAsync("fonts", new JsonObject { ["all"] = true }))["items"]!.AsArray().Select(i => i!.GetValue<string>())];
        Assert.True(all.Length > items.Length);
        Assert.Contains("Segoe UI", all);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-29-02")]
    public Task Consolas_is_used_without_cascadia_mono() => UiTestContext.RunAsync(async ctx =>
    {
        // Cascadia Mono が入っている PC では、入っていないことにする (フォントは削除しない)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("hideFonts", new JsonObject { ["names"] = new JsonArray("Cascadia Mono") });
        await app.IdleAsync();
        string line = await app.WaitForLogAsync(l => l.Contains("HexView font: Consolas", StringComparison.Ordinal), "the font log");
        Assert.Contains("Consolas", line);
        Assert.Equal("Consolas", (await app.RenderAsync())["fontFamily"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-29-03")]
    public Task Windows_text_size_enlarges_the_hex_view() => UiTestContext.RunAsync(async ctx =>
    {
        // Windows の文字の大きさは変えず、同じ倍率の通知をテスト用の命令で模擬する (UI 全体の拡大は Windows が行う)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        double row = (await app.RenderAsync())["rowHeight"]!.GetValue<double>();
        await app.SendAsync("setSystem", new JsonObject { ["textScaleFactor"] = 1.5 });
        await app.IdleAsync();
        Assert.True((await app.RenderAsync())["rowHeight"]!.GetValue<double>() >= row * 1.3);
    });

    // ---- UI-50 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-50-01")]
    public Task Every_interactive_element_has_a_name() => UiTestContext.RunAsync(async ctx =>
    {
        // Axe.Windows は同梱していないため、その規則のうち「名前のない操作可能な要素がない」を UI オートメーションの木で確かめる。
        // スタートページ (文書がない状態)、文書とデータインスペクタ・ブックマークのパネルを開いた状態、設定画面の各カテゴリ (手順 2)。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.IdleAsync();
        var missing = new List<string>();
        missing.AddRange(UnnamedInteractiveElements(app, "start page"));

        await app.OpenAsync(ctx.TestData("TD-SEQ-1M"));
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        foreach (string panel in new[] { "inspector", "bookmarks" })
        {
            await app.SendAsync("panelShow", new JsonObject { ["id"] = panel });
        }

        await app.IdleAsync();
        missing.AddRange(UnnamedInteractiveElements(app, "document"));

        foreach (string category in SettingsCategories)
        {
            await app.SendAsync("settingsPage", new JsonObject { ["category"] = category });
            await app.IdleAsync();
            missing.AddRange(UnnamedInteractiveElements(app, "settings/" + category));
        }

        Assert.True(missing.Count == 0, "unnamed interactive elements:\n" + string.Join("\n", missing));
    });

    /// <summary>設定画面のカテゴリ (Core の SettingCategories.All と同じ順)。</summary>
    private static readonly string[] SettingsCategories =
        ["general", "appearance", "view", "editing", "search", "files", "keyboard", "language", "accessibility", "automation", "update", "privacy", "explorer", "advanced"];

    /// <summary>表示中の操作できる要素のうち、名前 (AutomationProperties.Name) のないもの。</summary>
    private static IEnumerable<string> UnnamedInteractiveElements(AppSession app, string where)
    {
        ControlType[] interactive =
        [
            ControlType.Button, ControlType.Edit, ControlType.ComboBox, ControlType.CheckBox, ControlType.RadioButton, ControlType.MenuItem,
            ControlType.TabItem, ControlType.Hyperlink, ControlType.Slider, ControlType.SplitButton, ControlType.ScrollBar, ControlType.Document,
        ];
        var missing = new List<string>();
        foreach (AutomationElement element in app.Window.FindAllDescendants())
        {
            ControlType type = element.Properties.ControlType.ValueOrDefault;
            if (!interactive.Contains(type) || element.Properties.IsOffscreen.ValueOrDefault)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(element.Properties.Name.ValueOrDefault))
            {
                missing.Add($"{where}: {type} {element.Properties.AutomationId.ValueOrDefault}");
            }
        }

        return missing;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-50-02")]
    public Task Hex_view_value_reports_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        AutomationElement view = await app.WaitForAsync("HexView");
        Assert.Equal(ControlType.Edit, view.Properties.ControlType.Value);
        Assert.Equal(Path.GetFileName(ctx.TestData("TD-BYTES-256")), view.Properties.Name.Value);
        await GoToAsync(app, "0x4A");
        string value = view.Patterns.Value.Pattern.Value.Value;
        Assert.Contains("0x0000004A", value);
        Assert.Contains("4A", value);
        Assert.Contains("J", value);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-50-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Document_range_is_fast_on_100_GB() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await app.GoToAsync(0x1900000000 - 1024 * 1024);
        await app.IdleAsync();
        AutomationElement view = await app.WaitForAsync("HexView");
        var text = view.Patterns.Text.Pattern;
        int visible = (await app.RenderAsync())["rows"]!.AsArray().Count;
        for (int i = 0; i < 50; i++)
        {
            var watch = Stopwatch.StartNew();
            string all = text.DocumentRange.GetText(-1);
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds <= 50, $"try {i}: {watch.ElapsedMilliseconds} ms");
            Assert.Equal(visible, all.Split('\n').Length);
        }
    });

    // ---- UI-51 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-51-01")]
    public Task Announcements_carry_the_cursor_state() => UiTestContext.RunAsync(async ctx =>
    {
        // ブックマーク (INSP-23) の代わりに、同じ入口 (HexView.AnnotationNames) に名前を渡す。
        string file = ctx.CopyTestData("TD-ZERO-1M");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject
            {
                ["match"] = Path.GetFileName(file),
                ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x200", ["length"] = 512 }),
            }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file], Hooks = hooks });
        await app.SendAsync("annotate", new JsonObject { ["offset"] = 0x20, ["length"] = 1, ["name"] = "header" });
        await app.FocusAsync("HexView");
        await GoToAsync(app, "0x10");
        await app.TypeAsync("4A");
        await GoToAsync(app, "0x0F");
        await app.SendAsync("announcements", new JsonObject { ["clear"] = true });

        // 1. → 1 回で、オフセット 0x10・値 4A・文字 J・Hex 列・変更あり。
        await app.KeyAsync("Right");
        // 16 進の数字は 1 文字ずつ読ませるため桁の間に空白が入る (UI-51 の仕様 2)。空白を除いて比べる。
        string first = (await LastCursorAnnouncementAsync(app)).Replace(" ", string.Empty);
        foreach (string part in new[] { "0x00000010", "4A", "J", "Hexcolumn", "modified" })
        {
            Assert.Contains(part, first);
        }

        // 2. 30 回押す間は読み上げず、最後の入力の後に 1 件だけ。
        await app.SendAsync("announcements", new JsonObject { ["clear"] = true });
        // 30 回の入力を 1 つの命令で送る (1 回ずつ送ると、混んだ CI のランナーでは命令の往復が読み上げの待ち時間 150 ms を超える)。
        await app.KeyAsync("Right", count: 30);

        await Task.Delay(400);
        JsonArray items = (await app.SendAsync("announcements", new JsonObject { ["clear"] = true }))["items"]!.AsArray();
        Assert.Single(items, i => i!["id"]!.GetValue<string>() == "HexViewCursor");

        // 3. Insert で挿入モードの通知。
        await app.KeyAsync("Insert");
        await Task.Delay(100);
        items = (await app.SendAsync("announcements", new JsonObject { ["clear"] = true }))["items"]!.AsArray();
        Assert.Contains(items, i => i!["id"]!.GetValue<string>() == "HexViewMode");
        await app.KeyAsync("Insert");

        // 4〜5. ブックマークの名前と、読み取れないバイト。
        await GoToAsync(app, "0x20");
        Assert.Contains("bookmark header", await LastCursorAnnouncementAsync(app));
        await GoToAsync(app, "0x200");
        await app.WaitUntilAsync(async () => (await LastCursorAnnouncementAsync(app, clear: false)).Contains("cannot be read"), TimeSpan.FromSeconds(10), "unreadable");

        static async Task<string> LastCursorAnnouncementAsync(AppSession app, bool clear = true)
        {
            string? found = null;
            await app.WaitUntilAsync(async () =>
            {
                JsonArray items = (await app.SendAsync("announcements", new JsonObject { ["clear"] = false }))["items"]!.AsArray();
                found = items.LastOrDefault(i => i!["id"]!.GetValue<string>() == "HexViewCursor")?["text"]?.GetValue<string>();
                return found is not null;
            }, TimeSpan.FromSeconds(5), "the cursor announcement");
            if (clear)
            {
                await app.SendAsync("announcements", new JsonObject { ["clear"] = true });
            }

            return found!;
        }
    });
}
