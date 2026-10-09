using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>全画面表示 (UI-07)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FullScreenTests
{
    private static Task<JsonObject> ShellAsync(AppSession app) => app.SendAsync("shellState");

    [Fact]
    [Trait(UiTest.TC, "TC-UI-07-01")]
    public Task F11_toggles_full_screen_and_restores_the_bounds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("moveWindow", new JsonObject { ["x"] = 100, ["y"] = 100, ["width"] = 1200, ["height"] = 800 });
        await Task.Delay(300);
        await app.IdleAsync();
        JsonObject before = await ShellAsync(app);

        // 1〜2. Hex ビューで F11: 全画面 (モニター全体の大きさ) になり、タイトルバーとタブ列が隠れる。InfoBar が出る。
        Assert.Equal("menu:Command_FullScreen", (await app.KeyAsync("F11"))["handledBy"]!.GetValue<string>());
        await app.WaitUntilAsync(async () => (await ShellAsync(app))["presenter"]!.GetValue<string>() == "FullScreen", TimeSpan.FromSeconds(5), "full screen");
        await app.EventuallyAsync(async () =>
        {
            JsonObject full = await ShellAsync(app);
            Assert.Equal(full["monitorWidth"]!.GetValue<int>(), full["width"]!.GetValue<int>());
            Assert.Equal(full["monitorHeight"]!.GetValue<int>(), full["height"]!.GetValue<int>());
            Assert.False(full["titleBarVisible"]!.GetValue<bool>());
            Assert.False(full["tabStripVisible"]!.GetValue<bool>());
            Assert.Contains(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>().Contains("F11", StringComparison.Ordinal));
        });

        // 3〜4. もう一度 F11 で元の位置と大きさ。
        await app.KeyAsync("F11");
        await app.WaitUntilAsync(async () => (await ShellAsync(app))["presenter"]!.GetValue<string>() == "Overlapped", TimeSpan.FromSeconds(5), "back from full screen");
        await app.EventuallyAsync(async () =>
        {
            JsonObject after = await ShellAsync(app);
            foreach (string key in new[] { "x", "y", "width", "height" })
            {
                Assert.Equal(before[key]!.GetValue<int>(), after[key]!.GetValue<int>());
            }

            Assert.True(after["titleBarVisible"]!.GetValue<bool>());
            Assert.True(after["tabStripVisible"]!.GetValue<bool>());
        });
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-07-02")]
    public Task Pointer_at_the_top_edge_shows_the_tab_strip_over_the_editor() => UiTestContext.RunAsync(async ctx =>
    {
        // 実際のマウスは動かさない。ポインタが画面の上端から 2 px にあるときの処理をテスト用の命令で呼ぶ。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M"), ctx.TestData("TD-BYTES-256")] });
        await app.WaitForTabsAsync(2);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.fullScreen" });
        await app.WaitUntilAsync(async () => (await ShellAsync(app))["presenter"]!.GetValue<string>() == "FullScreen", TimeSpan.FromSeconds(5), "full screen");
        await Task.Delay(300);
        await app.IdleAsync();
        double editorTop = (await ShellAsync(app))["editorTop"]!.GetValue<double>();

        // 1〜2. 上端から 2 px に置いて 400 ms 待つと、タブ列がエディタの上に重なって出る (エディタは動かない)。
        await app.SendAsync("fullScreenPointer", new JsonObject { ["y"] = 2 });
        await Task.Delay(400);
        JsonObject shown = null!;
        await app.EventuallyAsync(async () => Assert.True((shown = await ShellAsync(app))["tabStripVisible"]!.GetValue<bool>(), "the tab strip is not shown"));
        Assert.Equal(editorTop, shown["editorTop"]!.GetValue<double>(), 1);
        Assert.True(shown["tabStripBottom"]!.GetValue<double>() > editorTop, "the tab strip does not overlap the editor");

        // 3. TD-SEQ-1M のタブを選ぶ (UI オートメーションの選択。マウスは使わない)。
        var tab = app.Window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem).And(cf.ByName("TD-SEQ-1M.bin")))
            ?? app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem)).First(t => AppSession.NameOf(t).Contains("TD-SEQ-1M", StringComparison.Ordinal));
        tab.Patterns.SelectionItem.Pattern.Select();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 0, TimeSpan.FromSeconds(5), "the TD-SEQ-1M tab");

        await app.SendAsync("execute", new JsonObject { ["id"] = "view.fullScreen" });
        await app.WaitUntilAsync(async () => (await ShellAsync(app))["presenter"]!.GetValue<string>() == "Overlapped", TimeSpan.FromSeconds(5), "back from full screen");
    });
}
