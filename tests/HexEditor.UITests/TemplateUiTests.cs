using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 構造体テンプレートの画面 (TPL-20〜TPL-30): 適用の選択画面、結果ツリー、値の編集、Hex ビューとの連動、右クリックメニュー、
/// エクスポート、自動適用。状態はテスト用の命令 "template" で読み、ダイアログは UI オートメーションで操作する。評価の中身は
/// Core のテスト (HexEditor.Core.Tests/Templates) が確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateUiTests
{
    internal static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    /// <summary>テンプレートのフィクスチャ (tests/fixtures/templates/data)。</summary>
    internal static string Fixture(string name) => Path.Combine(AppLocator.RepositoryRoot, "tests", "fixtures", "templates", "data", name);

    internal static Task<JsonObject> TemplateAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("template", request, TimeSpan.FromSeconds(120));
    }

    /// <summary>評価が終わり、結果ツリーに出るまで待って状態を返す。</summary>
    internal static async Task<JsonObject> WaitTemplatesAsync(AppSession app, int applications = 1)
    {
        JsonObject state = [];
        await app.WaitUntilAsync(async () =>
        {
            state = await TemplateAsync(app, "wait");
            JsonArray apps = state["applications"]!.AsArray();
            return apps.Count >= applications && apps.All(a => a!["completed"]?.GetValue<bool>() == true);
        }, Wait, "the template evaluation");
        return state;
    }

    internal static JsonObject Row(JsonObject state, string path) =>
        state["rows"]!.AsArray().Select(r => r!.AsObject()).FirstOrDefault(r => r["path"]?.GetValue<string>() == path)
        ?? throw new Xunit.Sdk.XunitException($"No row {path}: {string.Join(", ", state["rows"]!.AsArray().Select(r => r!["path"]))}");

    internal static async Task<JsonObject> RowAsync(AppSession app, string path)
    {
        // 再評価で木を作り直している間は行がないことがあるので、見つかるまで開き直す
        JsonObject? row = null;
        await app.WaitUntilAsync(async () =>
        {
            await TemplateAsync(app, "reveal", new JsonObject { ["path"] = path });
            JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
            row = state["rows"]!.AsArray().Select(r => r!.AsObject()).FirstOrDefault(r => r["path"]?.GetValue<string>() == path);
            return row?["loaded"]!.GetValue<bool>() == true;
        }, Wait, "the row " + path);
        return row!;
    }

    /// <summary>テンプレートのテキストをファイルにして適用する。</summary>
    internal static async Task<JsonObject> ApplyTextAsync(UiTestContext ctx, AppSession app, string template, string name = "test.hxt")
    {
        string file = ctx.WriteFile(name, Encoding.UTF8.GetBytes(template));
        await TemplateAsync(app, "applyText", new JsonObject { ["file"] = file });
        return await WaitTemplatesAsync(app);
    }

    /// <summary>適用の選択画面の一覧で項目を選ぶ (UI オートメーションの SelectionItem)。</summary>
    internal static async Task PickTemplateAsync(AppSession app, string name)
    {
        var list = await app.WaitForAsync("TplApply_List");
        FlaUI.Core.AutomationElements.AutomationElement? item = null;
        await app.WaitUntilAsync(() => Task.FromResult((item = list.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
            .FirstOrDefault(i => AppSession.NameOf(i) == name)) is not null), Wait, "the template " + name);
        item!.Patterns.SelectionItem.Pattern.Select();
        await app.IdleAsync();
    }

    internal static AppOptions NoAutoApply(params string[] files) => new() { Files = files, ExtraArgs = [] };

    private static async Task<byte[]> BytesAsync(AppSession app, long offset, int length) => await app.BytesAsync(offset, length);

    // ───────────── TPL-20 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-20-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Applying_png_from_the_dialog_shows_the_result_tree() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")] });
        await TemplateAsync(app, "run", new JsonObject { ["id"] = "templates.apply" });
        await app.WaitForDialogAsync("TemplateApplyDialog");
        await app.UiaSetValueAsync("TplApply_Search", "png");
        JsonArray items = (await TemplateAsync(app, "applyDialog"))["items"]!.AsArray();
        Assert.Contains(items, i => i!.GetValue<string>() == "PNG image");
        await PickTemplateAsync(app, "PNG image");
        await app.InvokeDialogButtonAsync("Apply");
        JsonObject state = await WaitTemplatesAsync(app);
        Assert.True(state["panelOpen"]!.GetValue<bool>());
        Assert.StartsWith("PNG image", Row(state, "@app")["name"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("0x0", (await RowAsync(app, "signature"))["offset"]!.GetValue<string>());
        await RowAsync(app, "chunks");

        // 先頭 8 バイトに色分けが付く
        JsonArray segments = (await TemplateAsync(app, "segments", new JsonObject { ["start"] = 0, ["length"] = 16 }))["segments"]!.AsArray();
        Assert.Contains(segments, s => s!["path"]!.GetValue<string>() == "signature" && s["start"]!.GetValue<long>() == 0);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-20-02")]
    public Task Applying_from_the_cursor_uses_the_cursor_as_the_start_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-PE-PNG")] });
        await TemplateAsync(app, "removeAll");
        await app.GoToAsync(0x2000);
        await TemplateAsync(app, "run", new JsonObject { ["id"] = "templates.applyHere" });
        await app.WaitForDialogAsync("TemplateApplyDialog");
        Assert.Equal("0x2000", (await app.WaitForAsync("TplApply_Offset")).Patterns.Value.Pattern.Value.Value);
        await app.UiaSetValueAsync("TplApply_Search", "PNG image");
        await PickTemplateAsync(app, "PNG image");
        await app.InvokeDialogButtonAsync("Apply");
        JsonObject state = await WaitTemplatesAsync(app);
        JsonObject app0 = state["applications"]!.AsArray().Last()!.AsObject();
        Assert.Equal(0x2000, app0["offset"]!.GetValue<long>());
        Assert.Equal(0, app0["errors"]!.GetValue<int>());
        Assert.Equal(0, app0["warnings"]!.GetValue<int>());
        Assert.Equal("0x2000", (await RowAsync(app, "signature"))["offset"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-20-03")]
    public Task Editing_reevaluates_after_500_ms_and_keeps_the_expanded_nodes() => UiTestContext.RunAsync(async ctx =>
    {
        string png = ctx.TestData("TD-PNG");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [png], ExtraArgs = ["--template", "PNG image"] });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "chunks[0].header.width" });
        Assert.Equal("16", (await RowAsync(app, "chunks[0].header.width"))["value"]!.GetValue<string>());

        await app.GoToAsync(0x10);
        await app.TypeAsync("00000020");
        await app.WaitUntilAsync(async () => (await RowAsync(app, "chunks[0].header.width"))["value"]!.GetValue<string>() == "32", Wait, "the new width");
        JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
        Assert.True(Row(state, "chunks")["expanded"]!.GetValue<bool>());
        Assert.True(Row(state, "chunks[0]")["expanded"]!.GetValue<bool>());
        Assert.True(Row(state, "chunks[0].header")["expanded"]!.GetValue<bool>());
        Assert.Equal("chunks[0].header.width", state["selectedPath"]?.GetValue<string>());

        // 500 ms の待ち (最後の編集から数える) は Core のテスト (時計を進める) で確かめる。ここでは再評価が 1 回にまとまることを見る。
        Assert.True(state["applications"]![0]!["evaluations"]!.GetValue<int>() <= 3);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-20-04")]
    public Task Reopening_the_document_restores_the_applications() => UiTestContext.RunAsync(async ctx =>
    {
        string file = Path.Combine(ctx.Root, "pe-png.bin");
        File.Copy(ctx.TestData("TD-ANA-PE-PNG"), file);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await TemplateAsync(app, "removeAll");
        await TemplateAsync(app, "apply", new JsonObject { ["template"] = "pe" });
        await TemplateAsync(app, "apply", new JsonObject { ["template"] = "png", ["offset"] = 0x2000, ["bigEndian"] = true });
        await WaitTemplatesAsync(app, 2);
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.close" });
        await app.WaitForTabsAsync(0);
        await app.OpenAsync(file);
        JsonObject state = await WaitTemplatesAsync(app, 2);
        JsonArray apps = state["applications"]!.AsArray();
        Assert.Equal(2, apps.Count);
        JsonObject png = apps.Single(a => a!["offset"]!.GetValue<long>() == 0x2000)!.AsObject();
        Assert.True(png["bigEndian"]!.GetValue<bool>());
        Assert.Equal("Fixed", png["kind"]!.GetValue<string>());
        Assert.DoesNotContain(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>().Contains("looks like", StringComparison.Ordinal));
    });

    // ───────────── TPL-21 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-21-01")]
    public Task Hundred_million_elements_are_grouped_and_the_ui_stays_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-MARKERS-1G")] });
        await TemplateAsync(app, "removeAll");
        await ApplyTextAsync(ctx, app, "struct P { u32 v; }; P a[100000000];");
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "a" });
        await TemplateAsync(app, "key", new JsonObject { ["key"] = "Right" });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1))
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            _ = app.Find("Tpl_Tree");
            TimeBudget.Limit(one.Elapsed < TimeSpan.FromMilliseconds(100), $"UI response {one.ElapsedMilliseconds} ms");
            await Task.Delay(50);
        }

        JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 200 });
        JsonObject group = Row(state, "a#0:1000000");
        Assert.Equal("[0 … 999999]", group["name"]!.GetValue<string>());
        Assert.Equal(100, state["rows"]!.AsArray().Count(r => r!["kind"]!.GetValue<string>() == "Group"));
        await TemplateAsync(app, "expand", new JsonObject { ["path"] = "a[1000]" });
        state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
        Assert.Equal("[0 … 999]", Row(state, "a#0:1000")["name"]!.GetValue<string>());
        Assert.Equal(3, Row(state, "a[1000]")["depth"]!.GetValue<int>() - Row(state, "a")["depth"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-21-03")]
    public Task Typing_a_path_moves_to_the_node() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", "PNG image"] });
        await WaitTemplatesAsync(app);
        JsonObject found = await TemplateAsync(app, "goToPath", new JsonObject { ["path"] = "chunks[0].header.width" });
        Assert.True(found["found"]!.GetValue<bool>());
        JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
        Assert.Equal("chunks[0].header.width", state["selectedPath"]!.GetValue<string>());
        Assert.True(Row(state, "chunks[0].header")["expanded"]!.GetValue<bool>());

        JsonObject missing = await TemplateAsync(app, "goToPath", new JsonObject { ["path"] = "chunks[0].nothing" });
        Assert.False(missing["found"]!.GetValue<bool>());
        Assert.Contains("chunks[0].nothing", missing["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("chunks[0].header.width", (await TemplateAsync(app, "state"))["selectedPath"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-21-04")]
    public Task Keyboard_moves_opens_and_goes_to_the_hex_view() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", "PNG image"] });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "focusTree");
        await TemplateAsync(app, "select", new JsonObject { ["path"] = "chunks" });
        await TemplateAsync(app, "key", new JsonObject { ["key"] = "Right" });
        JsonObject state = await TemplateAsync(app, "state", new JsonObject { ["rows"] = 2000 });
        Assert.True(Row(state, "chunks")["expanded"]!.GetValue<bool>());
        await TemplateAsync(app, "key", new JsonObject { ["key"] = "Right" });
        Assert.Equal("chunks[0]", (await TemplateAsync(app, "state"))["selectedPath"]!.GetValue<string>());
        await TemplateAsync(app, "key", new JsonObject { ["key"] = "Enter" });
        await ViewOps.AssertSelectionAsync(app, 8, 25);
        await TemplateAsync(app, "key", new JsonObject { ["key"] = "Left" });
        Assert.Equal("chunks", (await TemplateAsync(app, "state"))["selectedPath"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-21-06")]
    public Task Rows_expose_name_level_and_expand_state_to_ui_automation() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", Fixture("expect-fail.hxt")] });
        await WaitTemplatesAsync(app);
        await TemplateAsync(app, "reveal", new JsonObject { ["path"] = "length" });
        await RowAsync(app, "length");
        var tree = await app.WaitForAsync("Tpl_Tree");
        FlaUI.Core.AutomationElements.AutomationElement? warning = null;
        await app.WaitUntilAsync(() => Task.FromResult((warning = tree.FindFirstDescendant(cf => cf.ByAutomationId("Tpl_Row_length"))) is not null), Wait, "the row length");
        string name = AppSession.NameOf(warning!);
        Assert.Contains("length", name, StringComparison.Ordinal);
        Assert.Contains("Warning: ", name, StringComparison.Ordinal);
        Assert.Contains("offset 0x8", name, StringComparison.Ordinal);
        var signature = tree.FindFirstDescendant(cf => cf.ByAutomationId("Tpl_Row_signature"));
        Assert.NotNull(signature);
        Assert.Contains("size 0x8", AppSession.NameOf(signature), StringComparison.Ordinal);
        Assert.Equal(2, signature.Properties.Level.ValueOrDefault);
        var root = tree.FindFirstDescendant(cf => cf.ByAutomationId("Tpl_Row_@app"))!;
        Assert.Equal(FlaUI.Core.Definitions.ExpandCollapseState.Expanded, root.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value);
        // Axe.Windows は同梱していないため、その規則のうち「名前のない操作可能な要素がない」を確かめる (TC-UI-50-01 と同じ)
        Assert.Empty(ViewMiscTests.UnnamedInteractiveElements(app, "template panel"));
    });
}
