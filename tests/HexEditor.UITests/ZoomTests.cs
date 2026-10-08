using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>ズーム (UI-08) と、画面全体のズームでの Hex ビュー (VIEW-43 の受け入れ基準 4)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ZoomTests
{
    /// <summary>Ctrl+= (OemPlus)、Ctrl+- (OemMinus)、Ctrl+0。</summary>
    private const string Plus = "187", Minus = "189", Zero = "Number0";

    private static async Task<string?> HexZoomAsync(AppSession app)
    {
        await app.IdleAsync();
        JsonObject e = await app.ElementAsync("Status_HexZoom");
        return e["found"]!.GetValue<bool>() && e["effectivelyVisible"]!.GetValue<bool>() ? e["content"]?.GetValue<string>() : null;
    }

    /// <summary>画面全体のズームの項目。幅が足りずに「…」に入っている場合も、その文字列を返す (UI-06 の仕様 5)。</summary>
    private static async Task<string?> UiZoomAsync(AppSession app)
    {
        await app.IdleAsync();
        JsonObject e = await app.ElementAsync("Status_UiZoom");
        bool overflow = (await app.StateAsync())["statusOverflow"]!.AsArray().Any(i => i!.GetValue<string>() == "zoom");
        return e["found"]!.GetValue<bool>() && (e["effectivelyVisible"]!.GetValue<bool>() || overflow) ? e["content"]?.GetValue<string>() : null;
    }

    private static async Task<double> HeightAsync(AppSession app, string id)
    {
        await app.IdleAsync();
        JsonObject e = await app.ElementAsync(id);
        Assert.True(e["found"]!.GetValue<bool>(), $"{id} is missing");
        return e["bottom"]!.GetValue<double>() - e["top"]!.GetValue<double>();
    }

    private static async Task SelectTabAsync(AppSession app, int index)
    {
        await app.SendAsync("selectTab", new JsonObject { ["index"] = index });
        await app.IdleAsync();
        await Task.Delay(200);
        await app.IdleAsync();
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-01")]
    public Task Ctrl_plus_twice_is_125_and_ctrl_0_hides_the_status() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        Assert.Null(await HexZoomAsync(app));

        // 1〜2. Hex ビューで Ctrl+= を 2 回。
        await app.KeyAsync(Plus, ctrl: true, count: 2);
        Assert.Equal("Hex 125%", await HexZoomAsync(app));
        Assert.Equal(1.25, (await app.RenderAsync())["zoom"]!.GetValue<double>(), 3);

        // 3〜4. Ctrl+0 で 100% に戻り、項目が消える。
        await app.KeyAsync(Zero, ctrl: true);
        Assert.Null(await HexZoomAsync(app));
        Assert.Equal(1.0, (await app.RenderAsync())["zoom"]!.GetValue<double>(), 3);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-02")]
    public Task Zoom_is_shared_by_all_tabs_by_default() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);
        await SelectTabAsync(app, 0);
        await app.KeyAsync(Plus, ctrl: true);
        Assert.Equal("Hex 110%", await HexZoomAsync(app));

        await SelectTabAsync(app, 1);
        Assert.Equal("Hex 110%", await HexZoomAsync(app));
        Assert.Equal(1.1, (await app.RenderAsync())["zoom"]!.GetValue<double>(), 3);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-03")]
    public Task Zoom_per_tab_is_inherited_by_new_tabs_and_not_by_reopened_files() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SET-ZOOM-TAB: view.zoom.hexScope = tab。
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["view.zoom.hexScope"] = "tab" });
        string seq = ctx.TestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq, ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);
        await SelectTabAsync(app, 0);

        // 1〜2. タブ A で 125% にしても、タブ B は 100% のまま。
        await app.KeyAsync(Plus, ctrl: true, count: 2);
        Assert.Equal("Hex 125%", await HexZoomAsync(app));
        await SelectTabAsync(app, 1);
        Assert.Null(await HexZoomAsync(app));

        // 3〜4. タブ A を表示して開いたタブ C は、タブ A の倍率になる。
        await SelectTabAsync(app, 0);
        await app.OpenAsync(ctx.TestData("TD-ZERO-1M"));
        await app.WaitForTabsAsync(3);
        await app.IdleAsync();
        Assert.Equal("Hex 125%", await HexZoomAsync(app));

        // 5. タブ C を 100% に戻す。
        await app.KeyAsync(Zero, ctrl: true);
        Assert.Null(await HexZoomAsync(app));

        // 6〜7. タブ A を閉じて開き直すと 100% (閉じたタブの倍率を引き継がない)。
        await SelectTabAsync(app, 0);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(2);
        await app.OpenAsync(seq);
        await app.WaitForTabsAsync(3);
        await app.IdleAsync();
        Assert.Equal(Path.GetFileName(seq), (await app.DocumentAsync())["name"]!.GetValue<string>());
        Assert.Null(await HexZoomAsync(app));
    });

    /// <summary>適用範囲が「今のタブだけ」のとき、タブを別のウィンドウに移しても倍率はそのタブについていく (UI-08 の仕様 2、UI-11 の仕様 3)。</summary>
    [Fact]
    public Task Zoom_per_tab_moves_with_the_tab_to_a_new_window() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["view.zoom.hexScope"] = "tab" });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);
        await SelectTabAsync(app, 1);
        await app.KeyAsync(Plus, ctrl: true, count: 2);
        Assert.Equal("Hex 125%", await HexZoomAsync(app));

        await app.SendAsync("execute", new JsonObject { ["id"] = "tab.moveToNewWindow" });
        await WindowManagementTests.WaitForWindowsAsync(app, 2);

        // 新しいウィンドウ (操作の対象) は 125%、元のウィンドウのタブは 100% のまま。
        await app.WaitUntilAsync(async () => await HexZoomAsync(app) == "Hex 125%", TimeSpan.FromSeconds(10), "the moved tab keeps 125%");
        await app.SendAsync("activateWindow", new JsonObject { ["window"] = 0 });
        Assert.Null(await HexZoomAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-04")]
    public Task Screen_zoom_150_scales_menus_panels_and_dialogs() => UiTestContext.RunAsync(async ctx =>
    {
        // 150% でも左右のパネルを折りたたまない幅 (中身の幅 1024 px 以上。UI-01 の仕様 4) にする。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        await app.ResizeAsync((int)(1700 * scale), (int)(1000 * scale));
        await app.KeyAsync("I", ctrl: true, shift: true);
        await app.WaitUntilAsync(() => app.IsShownAsync("PanelTab_inspector"), TimeSpan.FromSeconds(10), "the inspector");

        // 1. メニューバー (「ファイル」の項目の高さ)、データインスペクタの見出し、バージョン情報の本文の高さ。
        async Task<double[]> MeasureAsync()
        {
            double menu = await HeightAsync(app, "MainMenu");
            double header = await HeightAsync(app, "PanelTab_inspector");
            await app.CommandAsync("Command_About");
            await app.WaitUntilAsync(() => app.IsShownAsync("About_Version"), TimeSpan.FromSeconds(10), "the about dialog");
            await Task.Delay(300);
            double about = await HeightAsync(app, "About_Version");
            // ダイアログの中の「閉じる」(パネルの閉じるボタンと同じ名前なので、ダイアログの中から探す)。
            var dialog = await app.WaitForDialogAsync("AboutDialog");
            dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
                .First(b => AppSession.NameOf(b) == "Close").Patterns.Invoke.Pattern.Invoke();
            await app.WaitUntilAsync(async () => !await app.IsShownAsync("About_Version"), TimeSpan.FromSeconds(10), "the dialog closed");
            return [menu, header, about];
        }

        double[] before = await MeasureAsync();

        // 2. 表示 > ズーム > 画面全体を拡大 を 3 回 (110 → 125 → 150)。
        for (int i = 0; i < 3; i++)
        {
            await app.CommandAsync("Command_UiZoomIn");
        }

        await Task.Delay(300);
        double[] after = await MeasureAsync();
        for (int i = 0; i < before.Length; i++)
        {
            Assert.True(Math.Abs(after[i] - before[i] * 1.5) <= 2, $"element {i}: {before[i]} -> {after[i]}");
        }

        // 4. ステータスバーに「画面 150%」(英語では Screen 150%)。
        Assert.Equal("Screen 150%", await UiZoomAsync(app));
        Assert.Null(await HexZoomAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-05")]
    public Task Screen_zoom_400_has_no_clipped_or_overlapping_text() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SET-ZOOM-UI400: view.zoom.ui = 400。中身は (ウィンドウ ÷ 4) と最小サイズ 640 × 400 の大きいほうで配置する
        // (UI-08 の仕様 3 の 4)。メイン画面と設定画面の全カテゴリで、文字の切れ・要素の重なりがないことを確かめる。
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["view.zoom.ui"] = 400 });
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.ResizeAsync(1920, 1080);
        Assert.Equal("Screen 400%", await UiZoomAsync(app));

        var problems = new List<string>();
        problems.AddRange(TrimReport.Describe(await app.TextCheckAsync(), "document", "400%", TrimReport.Keys("en")));
        SaveScreenshot(app, ctx, "zoom400-document");

        foreach (string category in SettingCategories)
        {
            await app.SendAsync("settingsPage", new JsonObject { ["open"] = true, ["category"] = category });
            await Task.Delay(300);
            await app.IdleAsync();
            problems.AddRange(TrimReport.Describe(await app.TextCheckAsync(), "settings-" + category, "400%", TrimReport.Keys("en")));
            SaveScreenshot(app, ctx, "zoom400-settings-" + category);
        }

        Assert.True(problems.Count == 0, "clipped or overlapping text at 400%:\n" + string.Join("\n", problems));
    });

    /// <summary>設定画面のカテゴリ (Core の SettingCategories.All)。</summary>
    private static readonly string[] SettingCategories =
        ["general", "appearance", "view", "editing", "search", "files", "keyboard", "language", "accessibility", "automation", "update", "privacy", "explorer", "advanced"];

    /// <summary>TC-UI-08-06 (人の目で文字の鮮明さを見る) のためのスクリーンショット。</summary>
    private static void SaveScreenshot(AppSession app, UiTestContext ctx, string name)
    {
        string folder = Path.Combine(UiTestContext.ArtifactsRoot, "zoom400");
        Directory.CreateDirectory(folder);
        app.Screenshot().SavePng(Path.Combine(folder, name + ".png"));
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-07")]
    public Task Cursor_stays_visible_while_zooming() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await ViewOps.GoToAsync(app, "0x80000");
        await app.IdleAsync();

        async Task AssertVisibleAsync(string when)
        {
            JsonObject render = await app.RenderAsync();
            Assert.Equal(0x80000, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
            Assert.True(ViewOps.RowOf(render, 0x80000) is not null, $"{when}: the row of 0x80000 is not visible");
        }

        // 1〜2. 5 回で 200%。
        await app.KeyAsync(Plus, ctrl: true, count: 5);
        Assert.Equal("Hex 200%", await HexZoomAsync(app));
        await AssertVisibleAsync("200%");

        // 3〜4. 6 回縮小して 90%。
        await app.KeyAsync(Minus, ctrl: true, count: 6);
        Assert.Equal("Hex 90%", await HexZoomAsync(app));
        await AssertVisibleAsync("90%");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-08-08")]
    public Task Menu_zoom_works_with_the_german_layout() => UiTestContext.RunAsync(async ctx =>
    {
        // 入力の配列の切り替え (OS の設定の変更) と実際の Alt のキー入力は行わない (TC-UI-03-02 と同じ)。ドイツ語配列では Ctrl+= が
        // 押せないため、メニューから拡大・縮小できることが要件 (UI-08 の仕様 6)。メニューの項目は配列に依存しないので、
        // 表示 > ズーム > 拡大 / 縮小 のアクセスキーの経路 (同じメニュー内で重ならない) と、項目を押したときの動作を確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        JsonArray items = (await app.SendAsync("menuTexts"))["items"]!.AsArray();
        JsonObject Item(string id) => items.Select(i => i!.AsObject()).Single(i => i["id"]!.GetValue<string>() == id);
        string zoomPath = Item("Command_Zoom")["path"]!.GetValue<string>();
        Assert.Equal("Z", Item("Command_Zoom")["accessKey"]!.GetValue<string>());
        Assert.Equal("I", Item("Command_ZoomIn")["accessKey"]!.GetValue<string>());
        Assert.Equal("O", Item("Command_ZoomOut")["accessKey"]!.GetValue<string>());
        Assert.StartsWith(zoomPath + "/", Item("Command_ZoomIn")["path"]!.GetValue<string>(), StringComparison.Ordinal);

        // 1〜2. 拡大で 110%。
        await app.CommandAsync("Command_ZoomIn");
        Assert.Equal("Hex 110%", await HexZoomAsync(app));

        // 3〜4. 縮小で 100% (項目が消える)。
        await app.CommandAsync("Command_ZoomOut");
        Assert.Null(await HexZoomAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-43-04")]
    public Task Hex_zoom_keeps_the_window_and_screen_zoom_scales_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        double status = await HeightAsync(app, "Status_Offset");
        double menu = await HeightAsync(app, "MainMenu");
        double row = (await app.RenderAsync())["rowHeight"]!.GetValue<double>();

        // 1〜2. Hex 表示のズーム 150% (Ctrl+= を 3 回: 110、125、150) では、ステータスバーとメニューバーの高さは変わらない。
        await app.KeyAsync(Plus, ctrl: true, count: 3);
        Assert.Equal("Hex 150%", await HexZoomAsync(app));
        Assert.Equal(status, await HeightAsync(app, "Status_Offset"), 1);
        Assert.Equal(menu, await HeightAsync(app, "MainMenu"), 1);

        // 3〜4. Hex を 100% に戻し、画面全体を 150% にすると、ステータスバー・メニューバー・Hex ビューの行が大きくなる。
        await app.KeyAsync(Zero, ctrl: true);
        for (int i = 0; i < 3; i++)
        {
            await app.CommandAsync("Command_UiZoomIn");
        }

        await Task.Delay(300);
        Assert.True(await HeightAsync(app, "Status_Offset") > status + 2, "the status bar did not grow");
        Assert.True(await HeightAsync(app, "MainMenu") > menu + 2, "the menu bar did not grow");
        JsonObject render = await app.RenderAsync();
        double screenRow = render["rowHeight"]!.GetValue<double>() * render["screenZoom"]!.GetValue<double>();
        Assert.True(screenRow > row * 1.4, $"the hex view rows did not grow ({row} -> {screenRow})");
    });
}
