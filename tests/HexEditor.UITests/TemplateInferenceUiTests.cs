using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.TemplateUiTests;

namespace HexEditor.UITests;

/// <summary>テンプレートの推定のパネル (07 の TPL-38〜TPL-40) の画面の試験。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TemplateInferenceUiTests
{
    private const long Rec48Length = 48_000;

    private static Task<JsonObject> InferAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("tplInfer", request, TimeSpan.FromSeconds(120));
    }

    private static Task<JsonObject> EditorAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("templateEditor", request, TimeSpan.FromSeconds(120));
    }

    /// <summary>推定が <paramref name="runs"/> 回終わるまで待つ。</summary>
    private static async Task<JsonObject> WaitInferenceAsync(AppSession app, int runs = 1)
    {
        JsonObject state = [];
        await app.WaitUntilAsync(async () =>
        {
            state = await InferAsync(app, "state");
            return !state["running"]!.GetValue<bool>() && state["runs"]!.GetValue<int>() >= runs;
        }, Wait, "the template inference");
        return state;
    }

    /// <summary>下書きが条件を満たすまで待つ (表を変えてから 300 ms 後に作り直す)。</summary>
    private static async Task<string> WaitDraftAsync(AppSession app, Func<string, bool> condition, string what)
    {
        string draft = string.Empty;
        await app.WaitUntilAsync(async () => condition(draft = (await InferAsync(app, "state"))["draft"]?.GetValue<string>() ?? string.Empty), Wait, what);
        return draft;
    }

    private static JsonObject RowAt(JsonObject state, int offset) =>
        state["rows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["offset"]!.GetValue<int>() == offset);

    /// <summary>範囲を選び、Hex ビューの選択範囲の右クリックメニュー「選択範囲からテンプレートを推定...」で開く。</summary>
    private static async Task<(AppSession App, JsonObject State)> OpenFromMenuAsync(UiTestContext ctx, string id, long length)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData(id)] });
        await app.SelectAsync(0, length);
        JsonObject render = await app.RenderAsync();
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(render, 0x10));
        await app.WaitForAsync("HexViewMenu_TemplateInfer");
        await app.CommandAsync("HexViewMenu_TemplateInfer");
        return (app, await WaitInferenceAsync(app));
    }

    private static Task<(AppSession App, JsonObject State)> OpenRec48Async(UiTestContext ctx) => OpenFromMenuAsync(ctx, "TD-TPL-INFER-REC48", Rec48Length);

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-38-03")]
    public Task Random_data_reports_that_no_fixed_length_records_were_found() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await OpenFromMenuAsync(ctx, "TD-TPL-INFER-RANDOM", 64 * 1024);
        Assert.True(state["notFound"]!.GetValue<bool>());
        string status = state["status"]!.GetValue<string>();
        Assert.Contains("No fixed-length records were found", status, StringComparison.Ordinal);
        Assert.Contains("Variable-length records", status, StringComparison.Ordinal);
        Assert.Empty(state["rows"]!.AsArray());
        await app.WaitUntilAsync(async () => (await InferAsync(app, "state"))["focused"]?.GetValue<string>() == "TplInfer_RecordLength",
            Wait, "the focus on the record length");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task The_context_menu_opens_the_panel_and_infers_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await OpenRec48Async(ctx);
        Assert.True(state["shown"]!.GetValue<bool>());
        Assert.Equal("0x0", state["start"]!.GetValue<string>());
        Assert.Equal(0, state["rangeStart"]!.GetValue<long>());
        Assert.Equal(Rec48Length, state["rangeLength"]!.GetValue<long>());
        Assert.Equal(10, state["rows"]!.AsArray().Count);
        await WaitDraftAsync(app, d => d.Contains("struct Record", StringComparison.Ordinal), "the draft preview");
        Assert.Null(state["internalError"]?.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-03")]
    public Task Renaming_and_retyping_reach_the_draft_and_the_result_tree() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await OpenRec48Async(ctx);
        await InferAsync(app, "apply");
        await WaitTemplatesAsync(app);

        JsonObject renamed = await InferAsync(app, "rename", new JsonObject { ["offset"] = 4, ["name"] = "index" });
        Assert.True(renamed["committed"]!.GetValue<bool>());
        await InferAsync(app, "setType", new JsonObject { ["offset"] = 4, ["type"] = "s32" });
        string draft = await WaitDraftAsync(app, d => System.Text.RegularExpressions.Regex.IsMatch(d, @"s32\s+index\s"), "s32 index in the draft");
        Assert.DoesNotContain("counter_04", draft, StringComparison.Ordinal);

        await InferAsync(app, "apply");
        JsonObject row = await RowAsync(app, "records[0].index");
        Assert.Equal("s32", row["type"]!.GetValue<string>());
        JsonObject templates = await WaitTemplatesAsync(app);
        Assert.Single(templates["applications"]!.AsArray());
        Assert.True(templates["applications"]![0]!["unsaved"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-04")]
    public Task Merge_and_split_change_the_fields_and_the_draft() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await OpenRec48Async(ctx);
        JsonObject merged = await InferAsync(app, "merge", new JsonObject { ["offset"] = 0x1A });
        Assert.Equal(4, RowAt(merged, 0x18)["size"]!.GetValue<int>());
        Assert.DoesNotContain(merged["rows"]!.AsArray(), r => r!["offset"]!.GetValue<int>() == 0x1A);
        string name = RowAt(merged, 0x18)["name"]!.GetValue<string>();
        await WaitDraftAsync(app, d => d.Contains(" " + name + " ", StringComparison.Ordinal) && !d.Contains("Flags_1A", StringComparison.Ordinal), "the merged field in the draft");

        JsonObject split = await InferAsync(app, "split", new JsonObject { ["offset"] = 0x18, ["at"] = 2 });
        Assert.True(split["committed"]!.GetValue<bool>());
        Assert.Equal(("Enum", 2), (RowAt(split, 0x18)["kind"]!.GetValue<string>(), RowAt(split, 0x18)["size"]!.GetValue<int>()));
        Assert.Equal(("Flags", 2), (RowAt(split, 0x1A)["kind"]!.GetValue<string>(), RowAt(split, 0x1A)["size"]!.GetValue<int>()));
        await WaitDraftAsync(app, d => d.Contains("enum Kind_18 : u16", StringComparison.Ordinal) && d.Contains("flags Flags_1A : u16", StringComparison.Ordinal),
            "the split fields in the draft");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-05")]
    public Task Manual_names_and_types_survive_inferring_again() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject before) = await OpenRec48Async(ctx);
        await InferAsync(app, "rename", new JsonObject { ["offset"] = 4, ["name"] = "index" });
        await InferAsync(app, "setType", new JsonObject { ["offset"] = 0x14, ["type"] = "u32" });
        await app.UiaInvokeAsync("TplInfer_Reinfer");
        JsonObject after = await WaitInferenceAsync(app, 2);
        Assert.Equal(("index", true), (RowAt(after, 4)["name"]!.GetValue<string>(), RowAt(after, 4)["manual"]!.GetValue<bool>()));
        Assert.Equal(("u32", true), (RowAt(after, 0x14)["typeId"]!.GetValue<string>(), RowAt(after, 0x14)["manual"]!.GetValue<bool>()));
        Assert.Contains("changed by hand", RowAt(after, 4)["automationName"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (JsonNode? row in after["rows"]!.AsArray().Where(r => r!["offset"]!.GetValue<int>() is not (4 or 0x14)))
        {
            JsonObject old = RowAt(before, row!["offset"]!.GetValue<int>());
            Assert.False(row["manual"]!.GetValue<bool>());
            Assert.Equal(old["name"]?.GetValue<string>(), row["name"]?.GetValue<string>());
            Assert.Equal(old["type"]!.GetValue<string>(), row["type"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-06")]
    public Task Open_in_editor_makes_an_unsaved_tab_that_saves_into_the_template_list() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await OpenRec48Async(ctx);
        string draft = await WaitDraftAsync(app, d => d.Length > 0, "the draft");
        await app.UiaInvokeAsync("TplInfer_OpenEditor");
        JsonObject editor = [];
        await app.WaitUntilAsync(async () => (editor = await EditorAsync(app, "state"))["open"]!.GetValue<bool>(), Wait, "the template editor");
        Assert.True(editor["dirty"]!.GetValue<bool>());
        Assert.Equal(draft, editor["text"]!.GetValue<string>());
        Assert.Contains("TD-TPL-INFER-REC48", editor["target"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("inferred-TD-TPL-INFER-REC48.hxt", Path.GetFileName(editor["path"]!.GetValue<string>()));

        await EditorAsync(app, "save");
        Assert.False((await EditorAsync(app, "state"))["dirty"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await InferAsync(app, "catalogFind", new JsonObject { ["name"] = "Inferred records (TD-TPL-INFER-REC48.bin)" }))["path"] is not null,
            Wait, "the saved draft in the template list");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-07")]
    public Task The_hex_view_preview_follows_the_table_and_disappears_on_close() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await OpenRec48Async(ctx);
        await app.GoToAsync(0);
        JsonArray segments = [];
        await app.WaitUntilAsync(async () =>
        {
            segments = (await app.SendAsync("highlights"))["segments"]!.AsArray();
            return segments.Any(s => s!["tag"]!.GetValue<string>().StartsWith("templateInference:", StringComparison.Ordinal));
        }, Wait, "the inference preview");
        JsonObject[] fields = [.. segments.Select(s => s!.AsObject())
            .Where(s => s["tag"]!.GetValue<string>().StartsWith("templateInference:", StringComparison.Ordinal) && s["layer"]!.GetValue<int>() == 9)];
        foreach (long at in new long[] { 0x00, 0x08, 0x2F, 0x30, 0x5F })
        {
            Assert.Contains(fields, s => s["first"]!.GetValue<long>() <= at && s["last"]!.GetValue<long>() >= at);
        }

        string? Background(long at) => fields.First(s => s["first"]!.GetValue<long>() <= at && s["last"]!.GetValue<long>() >= at)["background"]?.GetValue<string>();
        Assert.NotEqual(Background(0x00), Background(0x04));
        Assert.Contains(segments, s => s!["tag"]!.GetValue<string>() == "templateInferenceRecord" && s["first"]!.GetValue<long>() == 0x30);

        await InferAsync(app, "selectRow", new JsonObject { ["offset"] = 0x08 });
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((0x08L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        await app.GoToAsync(0x45);
        await app.WaitUntilAsync(async () =>
        {
            JsonObject state = await InferAsync(app, "state");
            int selected = state["selectedRow"]!.GetValue<int>();
            return selected >= 0 && state["rows"]![selected]!["offset"]!.GetValue<int>() == 0x14;
        }, Wait, "the row of 0x14");

        await InferAsync(app, "close");
        await app.WaitUntilAsync(async () => !(await app.SendAsync("highlights"))["segments"]!.AsArray()
            .Any(s => s!["tag"]!.GetValue<string>().StartsWith("templateInference", StringComparison.Ordinal)), Wait, "no preview");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-08")]
    public Task Table_changes_are_undone_with_ctrl_z_without_touching_the_document_history() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject before) = await OpenRec48Async(ctx);
        long? history = before["documentUndo"]?.GetValue<int>();
        await InferAsync(app, "rename", new JsonObject { ["offset"] = 4, ["name"] = "index" });
        await InferAsync(app, "focus");
        JsonObject undone = await InferAsync(app, "key", new JsonObject { ["key"] = "Z", ["ctrl"] = true });
        Assert.Equal("counter_04", RowAt(undone, 4)["name"]!.GetValue<string>());
        JsonObject redone = await InferAsync(app, "key", new JsonObject { ["key"] = "Y", ["ctrl"] = true });
        Assert.Equal("index", RowAt(redone, 4)["name"]!.GetValue<string>());
        Assert.Equal(history, redone["documentUndo"]?.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-40-09")]
    public Task Inference_rename_and_apply_work_from_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), Files = [ctx.TestData("TD-TPL-INFER-REC48")] });
        await app.SelectAsync(0, Rec48Length);

        // 1–2. コマンドパレットで「Templates: Infer from selection」を実行する
        await app.KeyAsync("P", ctrl: true, shift: true);
        await app.SendAsync("palette", new JsonObject { ["text"] = ">Infer from selection" });
        Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
        await WaitInferenceAsync(app);

        // 3. Tab で表に移り、↓ で 0x04 の行を選ぶ
        await TabToAsync(app, "TplInfer_Table");
        JsonObject state = await InferAsync(app, "state");
        for (int i = 0; i < 3 && (state["selectedRow"]!.GetValue<int>() < 0 || state["rows"]![state["selectedRow"]!.GetValue<int>()]!["offset"]!.GetValue<int>() != 4); i++)
        {
            state = await InferAsync(app, "key", new JsonObject { ["key"] = "Down" });
        }

        Assert.Equal(4, state["rows"]![state["selectedRow"]!.GetValue<int>()]!["offset"]!.GetValue<int>());

        // ナレーター (TPL-40 の仕様 8): 行の名前に「オフセット、名前、型、種類、確度」。パネルの操作できる部品にはすべて名前がある。
        await app.IdleAsync();
        string rowName = AppSession.NameOf(await app.WaitForAsync("TplInfer_Row_04"));
        Assert.Contains("0x04", rowName, StringComparison.Ordinal);
        Assert.Contains("counter_04", rowName, StringComparison.Ordinal);
        Assert.Contains("confidence", rowName, StringComparison.Ordinal);
        Assert.DoesNotContain(ViewMiscTests.UnnamedInteractiveElements(app, "template inference"), m => m.Contains("TplInfer", StringComparison.Ordinal));

        // 4. Shift+F10 で右クリックメニューを開き、「名前を変更」で index にして Enter
        Assert.True((await InferAsync(app, "key", new JsonObject { ["key"] = "F10", ["shift"] = true }))["rowMenuOpen"]!.GetValue<bool>());
        Assert.True((await InferAsync(app, "menuInvoke", new JsonObject { ["id"] = "TplInfer_Menu_Rename" }))["renameOpen"]!.GetValue<bool>());
        Assert.True((await InferAsync(app, "renameType", new JsonObject { ["text"] = "index" }))["committed"]!.GetValue<bool>());

        // 5. Tab で「適用」に移り Enter
        await WaitDraftAsync(app, d => d.Contains(" index ", StringComparison.Ordinal), "the renamed field in the draft");
        await TabToAsync(app, "TplInfer_Apply");
        Assert.Equal("button", (await InferAsync(app, "key", new JsonObject { ["key"] = "Enter" }))["handledBy"]!.GetValue<string>());
        await RowAsync(app, "records[0].index");
    });

    /// <summary>Tab でフォーカスを <paramref name="id"/> まで移す (最大 40 回)。</summary>
    private static async Task TabToAsync(AppSession app, string id)
    {
        for (int i = 0; i < 40; i++)
        {
            if ((await InferAsync(app, "state"))["focused"]?.GetValue<string>() == id)
            {
                return;
            }

            await InferAsync(app, "key", new JsonObject { ["key"] = "Tab" });
        }

        Assert.Fail("Tab did not reach " + id);
    }
}
