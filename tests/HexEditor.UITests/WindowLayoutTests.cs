using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>メインウィンドウの構成 (UI-01) とタイトルバー (UI-02) のうちフェーズ 1 のもの。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class WindowLayoutTests
{
    private static async Task<(double Left, double Right, double Width)> BoundsAsync(AppSession app, string id)
    {
        JsonObject e = await app.ElementAsync(id);
        Assert.True(e["found"]!.GetValue<bool>(), $"{id} is missing");
        double left = e["left"]!.GetValue<double>(), right = e["right"]!.GetValue<double>();
        return (left, right, right - left);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-UI-01-02")]
    public Task Panels_collapse_to_header_strips_below_1024_px() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.bookmarks" });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.inspector" });
        await app.IdleAsync();
        Assert.True(await app.IsShownAsync("LeftPanel"));
        Assert.True(await app.IsShownAsync("RightPanel"));

        // 1〜2. 1000 × 700 にすると、左右のパネルが見出しだけの帯になる。
        await app.SendAsync("resize", new JsonObject { ["width"] = 1000, ["height"] = 700 });
        await app.WaitUntilAsync(async () => !await app.IsShownAsync("RightPanel"), TimeSpan.FromSeconds(5), "the right panel collapsed");
        await Task.Delay(300);
        await app.IdleAsync();
        Assert.False(await app.IsShownAsync("LeftPanel"));
        Assert.True(await app.IsShownAsync("PanelStrip_bookmarks"));
        Assert.True(await app.IsShownAsync("PanelStrip_inspector"));
        var strip = await BoundsAsync(app, "PanelStrip_inspector");
        JsonObject header = await app.ElementAsync("PanelStrip_inspector");
        Assert.True(header["bottom"]!.GetValue<double>() - header["top"]!.GetValue<double>() > strip.Width, "the header is not vertical");
        var editor = await BoundsAsync(app, "HexView");

        // 3. 右の帯の「データインスペクタ」を押すと、エディタの上に重ねて開く。エディタの幅は変わらない。
        await app.CommandAsync("PanelStrip_inspector");
        await app.WaitUntilAsync(() => app.IsShownAsync("RightPanel"), TimeSpan.FromSeconds(5), "the inspector over the editor");
        await Task.Delay(300);
        await app.IdleAsync();
        var panel = await BoundsAsync(app, "RightPanel");
        var after = await BoundsAsync(app, "HexView");
        Assert.Equal(editor.Width, after.Width, 1);
        Assert.True(panel.Left < editor.Right && panel.Right > editor.Left, $"the panel ({panel}) does not overlap the editor ({editor})");
        Assert.True(await app.IsShownAsync("Inspector_Rows") || await app.IsShownAsync("PanelTab_inspector"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-01-03")]
    public Task Offscreen_saved_position_is_centered_on_the_primary_monitor() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SESSION-OFFSCREEN: 外したモニターの座標 (どのモニターにも入らない位置) にウィンドウがあった session.json。
        string profile = ctx.NewProfile();
        await File.WriteAllTextAsync(Path.Combine(profile, "session.json"),
            """{"windows": [{"x": -32000, "y": -32000, "width": 1200, "height": 700, "activeTab": -1, "tabs": []}], "closedTabs": [], "lastActiveWindow": 0}""");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject s = await app.SendAsync("shellState");
        int x = s["x"]!.GetValue<int>(), y = s["y"]!.GetValue<int>(), w = s["width"]!.GetValue<int>(), h = s["height"]!.GetValue<int>();
        int wx = s["workX"]!.GetValue<int>(), wy = s["workY"]!.GetValue<int>(), ww = s["workWidth"]!.GetValue<int>(), wh = s["workHeight"]!.GetValue<int>();

        // ウィンドウ全体が主モニターの作業領域の中で、中央にあり、1280 × 800 (入らなければ作業領域の 90%)。
        Assert.True(x >= wx && y >= wy && x + w <= wx + ww && y + h <= wy + wh, $"{x},{y} {w}x{h} is outside the work area {wx},{wy} {ww}x{wh}");
        Assert.InRange(x - wx - (ww - w) / 2, -1, 1);
        Assert.InRange(y - wy - (wh - h) / 2, -1, 1);
        double scale = (await app.StateAsync())["scale"]!.GetValue<double>();
        bool fits = 1280 * scale <= ww && 800 * scale <= wh;
        Assert.Equal(fits ? (int)Math.Round(1280 * scale) : (int)(ww * 0.9), w);
        Assert.Equal(fits ? (int)Math.Round(800 * scale) : (int)(wh * 0.9), h);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-01-04")]
    public Task Splitter_moves_with_the_arrow_keys() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.inspector" });
        await app.WaitUntilAsync(() => app.IsShownAsync("RightPanel"), TimeSpan.FromSeconds(5), "the right panel");
        await app.IdleAsync();

        // 1. Tab でエディタと右パネルの間の分割バーへ (Hex ビューの中では Tab は列の切り替えなので、ステータスバーから
        //    Shift+Tab でたどる代わりに、Tab の移動と同じ扱いでフォーカスを移す)。
        Assert.EndsWith("RightSplitter", await app.FocusAsync("RightSplitter") ?? string.Empty);
        double before = (await BoundsAsync(app, "RightPanel")).Width;

        // 2〜3. ← を 3 回: 右パネルが 24 px 広がる。
        await app.SendAsync("splitterKey", new JsonObject { ["id"] = "RightSplitter", ["key"] = "Left", ["count"] = 3 });
        double scale = (await app.SendAsync("shellState"))["appliedUiZoom"]!.GetValue<double>();
        await app.EventuallyAsync(async () => Assert.Equal(before + 24 * scale, (await BoundsAsync(app, "RightPanel")).Width, 1));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-02-02")]
    public Task Title_ends_with_administrator_when_elevated() => UiTestContext.RunAsync(async ctx =>
    {
        // 昇格はしない (作業中の PC の UAC を出さない)。テスト用の設定で管理者として実行している扱いにし、タイトルの規則を確かめる。
        // 配布形態ごとの昇格した起動は配布のテスト (CI) の範囲。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["elevated"] = true }, Files = [ctx.TestData("TD-SEQ-1M")] });
        string title = (await app.StateAsync())["title"]!.GetValue<string>();
        Assert.EndsWith("(Administrator)", title, StringComparison.Ordinal);
        Assert.StartsWith("TD-SEQ-1M.bin - HexEditor", title, StringComparison.Ordinal);

        AppSession ja = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), UiLanguage = "ja", Hooks = new JsonObject { ["elevated"] = true } });
        Assert.EndsWith("(管理者)", (await ja.StateAsync())["title"]!.GetValue<string>(), StringComparison.Ordinal);
    });
}
