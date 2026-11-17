using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 文書以外のタブ (ページのタブ) の切り替えとフォーカス。不具合: Ctrl+Tab・Ctrl+PageDown が文書しか数えず、スクリプトのタブから戻れなかった。
/// コマンドパレットを閉じると、隠れた Hex ビューへフォーカスを渡そうとしていた。新しいテンプレートを保存した後の F12 で同じファイルのタブが増えていた
/// (いずれも手動テストの手順の実行で見つかった)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ToolTabSwitchTests
{
    private const int KeyTab = 0x09, KeyPageDown = 0x22, KeyPageUp = 0x21;

    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private static async Task<string?> StripSelectedAsync(AppSession app) => (await app.SendAsync("tabs"))["stripSelected"]?.GetValue<string>();

    private static Task WaitSelectedAsync(AppSession app, string? selected) =>
        app.WaitUntilAsync(async () => await StripSelectedAsync(app) == selected, Wait, "the tab " + selected);

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-08")]
    public Task Ctrl_Tab_and_Ctrl_PageDown_include_tool_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        string document = (await StripSelectedAsync(app))!;
        await app.SendAsync("execute", new JsonObject { ["id"] = "settings.open" });
        await WaitSelectedAsync(app, "tool:settings");
        await app.IdleAsync();

        // 1・2: 文書 1 つとページ 1 つでも、Ctrl+Tab で行き来できる。
        WindowKeys.Press(app, KeyTab, ctrl: true);
        await WaitSelectedAsync(app, document);
        Assert.Null((await app.SendAsync("tabs"))["activeToolTab"]?.GetValue<string>());
        WindowKeys.Press(app, KeyTab, ctrl: true);
        await WaitSelectedAsync(app, "tool:settings");
        Assert.Equal("settings", (await app.SendAsync("tabs"))["activeToolTab"]?.GetValue<string>());

        // 3・4: Ctrl+PageDown・Ctrl+PageUp は見出しの並び順で、ページのタブも通る。
        WindowKeys.Press(app, KeyPageDown, ctrl: true);
        await WaitSelectedAsync(app, document);
        WindowKeys.Press(app, KeyPageUp, ctrl: true);
        await WaitSelectedAsync(app, "tool:settings");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-09")]
    public Task Closing_the_palette_returns_focus_to_the_page_shown() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.SendAsync("templateEditor", new JsonObject { ["action"] = "new" });
        await app.SendAsync("templateEditor", new JsonObject { ["action"] = "focus" });
        await app.IdleAsync();
        string? before = await app.FocusedAsync();
        Assert.False(before?.StartsWith("HexView", StringComparison.Ordinal), before);

        await app.SendAsync("palette", new JsonObject { ["text"] = ">" });
        await app.SendAsync("paletteClose");
        await app.WaitUntilAsync(async () => await app.FocusedAsync() == before, Wait, "the focus to return to the page, it is on " + await app.FocusedAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-31-06")]
    public Task F12_in_a_new_template_stays_in_its_tab_before_and_after_saving() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });

        Task<JsonObject> EditorAsync(string action, JsonObject? args = null)
        {
            JsonObject request = args ?? [];
            request["action"] = action;
            return app.SendAsync("templateEditor", request, TimeSpan.FromSeconds(120));
        }

        await EditorAsync("new");
        await EditorAsync("setText", new JsonObject { ["text"] = "// comment\nstruct Header {\n  u8 magic;\n};\nHeader hdr;\n" });

        async Task F12GoesToTheStructAsync()
        {
            await EditorAsync("caret", new JsonObject { ["line"] = 5, ["column"] = 2 });
            JsonObject state = await EditorAsync("key", new JsonObject { ["key"] = "F12" });
            await app.WaitUntilAsync(async () => (await EditorAsync("state"))["caretLine"]!.GetValue<int>() == 2, Wait, "the caret on the struct, it is on line " + state["caretLine"]);
            Assert.Equal(1, (await EditorAsync("state"))["count"]!.GetValue<int>());
        }

        await F12GoesToTheStructAsync();
        await EditorAsync("save");
        Assert.False((await EditorAsync("state"))["dirty"]!.GetValue<bool>());
        await F12GoesToTheStructAsync();

        // 保存したタブをパスで開いても、2 つ目のタブにならない。
        string path = (await EditorAsync("state"))["path"]!.GetValue<string>();
        await EditorAsync("open", new JsonObject { ["path"] = path });
        Assert.Equal(1, (await EditorAsync("state"))["count"]!.GetValue<int>());
    });
}
