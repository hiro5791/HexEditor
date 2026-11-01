using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// Ctrl+W (file.close) は選択中のタブを閉じる (09 の UI-09 の仕様 11)。不具合: 文書以外のタブ (設定画面など) を選んでいても、裏の文書を閉じていた
/// (手動テスト TC-UI-26-03 で見つかった)。キーは実際のキー入力と同じ道筋 (<see cref="WindowKeys"/>) で届ける。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TabCloseTests
{
    private const int KeyW = 'W';

    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private static async Task<int> DocumentCountAsync(AppSession app) => (await app.SendAsync("tabs"))["tabs"]!.AsArray().Count;

    private static async Task<bool> CloseEnabledAsync(AppSession app) =>
        (await app.SendAsync("tcWriteBlock", new JsonObject { ["action"] = "commandState", ["id"] = "file.close" }))["enabled"]!.GetValue<bool>();

    /// <summary>タブの見出しがあるか (UI オートメーションの木で調べる。閉じたタブの XAML の要素はしばらく残るので、要素の検索では分からない)。</summary>
    private static bool HasTab(AppSession app, string tabId) => app.Window.FindFirstDescendant(cf => cf.ByAutomationId(tabId)) is not null;

    private static Task WaitClosedAsync(AppSession app, string tabId) =>
        app.WaitUntilAsync(() => Task.FromResult(!HasTab(app, tabId)), Wait, tabId + " to close");

    private static Task WaitOpenAsync(AppSession app, string tabId) =>
        app.WaitUntilAsync(() => Task.FromResult(HasTab(app, tabId)), Wait, tabId);

    /// <summary>コマンドで開くページ: 文書があってもなくても、Ctrl+W でそのページだけが閉じる。</summary>
    [Theory]
    [Trait(UiTest.TC, "TC-UI-09-06")]
    [Trait(UiTest.Priority, UiTest.High)]
    [InlineData("settings.open", "ToolPageTab_settings", true)]
    [InlineData("settings.open", "ToolPageTab_settings", false)]
    [InlineData("help.shortcuts", "ToolPageTab_shortcuts", true)]
    [InlineData("analysis.demangle", "ToolPageTab_demangle", true)]
    public Task Ctrl_W_closes_the_selected_tool_page_and_keeps_the_document(string command, string tabId, bool withDocument) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = withDocument ? [ctx.TestData("TD-BYTES-256")] : [], WaitForEditor = withDocument });
        int documents = await DocumentCountAsync(app);
        await app.SendAsync("execute", new JsonObject { ["id"] = command });
        await WaitOpenAsync(app, tabId);
        Assert.True(await CloseEnabledAsync(app), "file.close is disabled on a tool page");

        WindowKeys.Press(app, KeyW, ctrl: true);
        await WaitClosedAsync(app, tabId);
        Assert.Equal(documents, await DocumentCountAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-06")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_W_closes_the_selected_script_editor_and_asks_when_it_has_changes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        string path = ctx.WriteFile("close.js", "// script\n"u8.ToArray());
        await app.SendAsync("scriptUi", new JsonObject { ["action"] = "openEditor", ["path"] = path });
        JsonObject wait = await app.SendAsync("codeEditor", new JsonObject { ["action"] = "wait" }, TimeSpan.FromSeconds(120));
        Assert.True(wait["ready"]!.GetValue<bool>(), "Monaco did not start");
        await app.SendAsync("codeEditor", new JsonObject { ["action"] = "type", ["text"] = "x" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("scriptUi", new JsonObject { ["action"] = "activeEditor" }))["dirty"]?.GetValue<bool>() == true, Wait, "the editor to be dirty");

        // Monaco の中のキーとして (ページからアプリに渡される)
        JsonObject key = new() { ["action"] = "key", ["key"] = "w", ["keyCode"] = KeyW, ["ctrl"] = true };
        await app.SendAsync("codeEditor", key, TimeSpan.FromSeconds(60));
        await app.WaitForDialogAsync("ScriptEditorUnsavedDialog");
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.Equal(1, await DocumentCountAsync(app));
        Assert.True((await app.SendAsync("codeEditor", new JsonObject { ["action"] = "state" }))["open"]!.GetValue<bool>(), "cancel must keep the editor open");

        await app.SendAsync("codeEditor", key.DeepClone().AsObject(), TimeSpan.FromSeconds(60));
        await app.InvokeDialogButtonAsync("Don't save");
        await app.WaitUntilAsync(async () => !(await app.SendAsync("codeEditor", new JsonObject { ["action"] = "state" }))["open"]!.GetValue<bool>(), Wait, "the script editor to close");
        Assert.Equal(1, await DocumentCountAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-06")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_W_closes_the_template_editor_tab_without_a_document() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("templateEditor", new JsonObject { ["action"] = "new" });
        JsonObject state = await app.SendAsync("codeEditor", new JsonObject { ["action"] = "wait" }, TimeSpan.FromSeconds(120));
        Assert.True(state["ready"]!.GetValue<bool>(), "Monaco did not start");
        Assert.True(await CloseEnabledAsync(app), "file.close is disabled on the template editor");

        // Monaco の中のキーとして (ページからアプリに渡される)
        await app.SendAsync("codeEditor", new JsonObject { ["action"] = "key", ["key"] = "w", ["keyCode"] = KeyW, ["ctrl"] = true }, TimeSpan.FromSeconds(60));
        await app.WaitUntilAsync(async () => (await app.SendAsync("codeEditor", new JsonObject { ["action"] = "state" }))["open"]?.GetValue<bool>() != true, Wait, "the template editor to close");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-06")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Ctrl_W_on_a_document_tab_still_closes_the_document_with_a_tool_page_open_behind_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256"), ctx.TestData("TD-SEQ-1M")] });
        await app.SendAsync("execute", new JsonObject { ["id"] = "settings.open" });
        await WaitOpenAsync(app, "ToolPageTab_settings");
        app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.TabItem)).First(t => AppSession.NameOf(t).StartsWith("TD-SEQ-1M", StringComparison.Ordinal))
            .Patterns.SelectionItem.Pattern.Select();
        await app.WaitUntilAsync(async () => !await app.IsShownAsync("ToolPageHost"), Wait, "the document tab");

        WindowKeys.Press(app, KeyW, ctrl: true);
        await app.WaitUntilAsync(async () => await DocumentCountAsync(app) == 1, Wait, "the document to close");
        Assert.True(HasTab(app, "ToolPageTab_settings"));
    });

    /// <summary>文書がないときのタブ列の下: ページがタブの見出しに重ならない (不具合: タブが増えてタブ列が高くなっても位置を直さず、18 px 重なっていた)。</summary>
    [Fact]
    [Trait(UiTest.TC, "TC-UI-09-07")]
    public Task Tool_page_starts_below_the_tab_strip_when_no_document_is_open() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        string path = ctx.WriteFile("place.js", "// script\n"u8.ToArray());
        await app.SendAsync("scriptUi", new JsonObject { ["action"] = "openEditor", ["path"] = path });
        string tabId = "ToolPageTab_script_" + Path.GetFullPath(path).ToUpperInvariant().Replace(':', '_');
        await app.WaitUntilAsync(async () =>
        {
            JsonObject tab = await app.ElementAsync(tabId), page = await app.ElementAsync("ToolPageHost");
            return tab["bottom"] is { } bottom && page["top"] is { } top && top.GetValue<double>() >= bottom.GetValue<double>() - 1;
        }, TimeSpan.FromSeconds(10), "the page below the tab header");
    });
}
