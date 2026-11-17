using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// 一覧 (ListView) と木 (TreeView) の行にフォーカスがあるとき、実際のキー入力 (WindowKeys) の Enter と Space が各パネルの操作に届くこと。
/// ListViewItem は Enter と Space を処理済みにするので、一覧の KeyDown では受けられず PreviewKeyDown で受ける。
/// テスト用の命令の key / panelKey は XAML のキーの道筋を通らないので、この不具合を見つけられない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ItemKeyTests
{
    private const int Enter = 0x0D, Space = 0x20;

    private static async Task TypeAtAsync(AppSession app, long offset, string text)
    {
        await app.GoToAsync(offset);
        await app.TypeAsync(text);
        await app.IdleAsync();
    }

    private static async Task ShowAsync(AppSession app, string command, string panel)
    {
        if (!await app.IsShownAsync(panel))
        {
            await app.CommandAsync(command);
        }

        await app.WaitForAsync(panel);
        await app.IdleAsync();
    }

    private static async Task FocusRowAsync(AppSession app, string list, int index, string expectedKind = "ListViewItem")
    {
        string? focused = await app.FocusItemAsync(list, index);
        Assert.StartsWith(expectedKind, focused ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-20-03")]
    public Task History_tree_enter() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await TypeAtAsync(app, 0x10, "A1");
        await TypeAtAsync(app, 0x20, "A2");
        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.history"));
        await app.WaitForAsync("History_List");
        await app.SendAsync("historyTreeView", new JsonObject { ["on"] = true });
        await app.WaitForAsync("History_Tree");
        await app.IdleAsync();
        int before = (await app.SendAsync("historyPanel"))["current"]!.GetValue<int>();
        Assert.Equal(2, before);
        int row = (await app.SendAsync("historyPanel"))["treeRows"]!.GetValue<int>() - 1;
        for (int i = 0; i <= row; i++)
        {
            // どの行が最初の状態かは並びによるので、現在の状態でない行を探して Enter を押す。
            await FocusRowAsync(app, "History_Tree", i);
            WindowKeys.Press(app, Enter);
            await app.IdleAsync();
            if ((await app.SendAsync("historyPanel"))["current"]!.GetValue<int>() != before)
            {
                return;
            }
        }

        Assert.Fail("Enter on a tree row did not go to that state.");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-28-04")]
    public Task Clipboard_slot_and_history_enter_paste() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SelectAsync(0x10, 4);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.copy3"));
        await app.SelectAsync(0x80, 2);
        await app.KeyAsync("C", ctrl: true);
        await app.IdleAsync();
        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.clipboard"));
        await app.WaitForAsync("ClipboardPanel_Slots");

        // 3 番のスロット: 0x200 に貼る。
        await app.GoToAsync(0x200);
        await FocusRowAsync(app, "ClipboardPanel_Slots", 2);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0x200, 4)).SequenceEqual(new byte[] { 0x10, 0x11, 0x12, 0x13 }), TimeSpan.FromSeconds(5), "Enter to paste the slot");

        // 履歴の先頭 (0x80 の 2 バイト): 0x400 に貼る。
        await app.GoToAsync(0x400);
        await FocusRowAsync(app, "ClipboardPanel_History", 0);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0x400, 2)).SequenceEqual(new byte[] { 0x80, 0x81 }), TimeSpan.FromSeconds(5), "Enter to paste the history item");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-26-06")]
    public Task Bookmark_row_enter_and_group_space() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await ShowAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        foreach (long at in new long[] { 0x100, 0x200, 0x300 })
        {
            await app.GoToAsync(at);
            await app.KeyAsync("F2", ctrl: true);
        }

        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "create", ["name"] = "G" });
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "set", ["start"] = 0x100, ["group"] = "G" });
        await app.GoToAsync(0);
        await app.IdleAsync();

        // グループの行 (先頭) で Space: グループが非表示になる。
        string? focused = await app.FocusItemAsync("Bookmarks_List", 0);
        Assert.Equal("ListViewItem:Bookmarks_Group_G", focused);
        WindowKeys.Press(app, Space);
        await app.WaitUntilAsync(async () =>
        {
            await app.GoToAsync(0x100);
            await app.IdleAsync();
            return !(await AnnotationHighlightAsync(app, 0x100));
        }, TimeSpan.FromSeconds(5), "Space to hide the group");

        // ブックマークの行で Enter: そのブックマークへ移る。
        await app.GoToAsync(0);
        string? row = null;
        for (int i = 1; i < 4 && row != "ListViewItem:"; i++)
        {
            row = await app.FocusItemAsync("Bookmarks_List", i);
        }

        Assert.Equal("ListViewItem:", row);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) != 0, TimeSpan.FromSeconds(5), "Enter to go to the bookmark");
    });

    private static async Task<bool> AnnotationHighlightAsync(AppSession app, long at)
    {
        JsonObject h = await app.SendAsync("highlights");
        return h["segments"]!.AsArray().Any(s => s!["layer"]!.GetValue<int>() == 7 && s["first"]!.GetValue<long>() <= at && s["last"]!.GetValue<long>() >= at);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-33-07")]
    public Task Coloring_rule_space_and_enter() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-BYTES-256")] });
        await ShowAsync(app, "Command_ToggleColoringRules", "ColoringRulesPanel");
        await app.SendAsync("coloring", new JsonObject
        {
            ["scope"] = "global", ["action"] = "add", ["rule"] = new JsonObject { ["name"] = "A", ["pattern"] = "41", ["background"] = "#FF0000" },
        });
        async Task<bool> EnabledAsync() => (await app.SendAsync("coloring", new JsonObject { ["scope"] = "global" }))["items"]!.AsArray()
            .First(i => i!["name"]!.GetValue<string>() == "A")!["enabled"]!.GetValue<bool>();

        bool initial = await EnabledAsync();
        int index = (await app.SendAsync("coloring", new JsonObject { ["scope"] = "global" }))["items"]!.AsArray()
            .Select(i => i!["name"]!.GetValue<string>()).ToList().IndexOf("A");
        await FocusRowAsync(app, "Coloring_List", index);
        WindowKeys.Press(app, Space);
        await app.WaitUntilAsync(async () => await EnabledAsync() != initial, TimeSpan.FromSeconds(5), "Space to toggle the rule");

        // Enter: 編集欄 (名前) へフォーカスが移る。
        await FocusRowAsync(app, "Coloring_List", index);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.FocusedAsync())?.StartsWith("TextBox", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5), "Enter to focus the editor");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-17-06")]
    public Task File_type_embedded_list_enter() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-PE-PNG")] });
        JsonObject state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embedded" }, TimeSpan.FromSeconds(120));
        JsonArray found = state["embedded"]!.AsArray();
        int index = found.Select((e, i) => (e, i)).First(x => x.e!["offset"]!.GetValue<long>() == 0x2000).i;
        await app.WaitForAsync("FileType_Embedded");
        await app.GoToAsync(0);
        await FocusRowAsync(app, "FileType_Embedded", index);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) == 0x2000, TimeSpan.FromSeconds(5), "Enter to go to the embedded format");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-19-05")]
    public Task Inspector_row_settings_space() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx);
        List<string> before = await InspectorTests.RowIdsAsync(app);
        Assert.Contains("int16", before);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Setting_int16");
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("Inspector_SettingsList", 0, "Inspector_SettingItem_int16") ?? string.Empty);
        WindowKeys.Press(app, Space);
        await app.WaitUntilAsync(async () => !(await InspectorTests.RowIdsAsync(app)).Contains("int16"), TimeSpan.FromSeconds(5), "Space to hide the int16 row");
    });

    private static string MultiFileFolder(UiTestContext ctx)
    {
        string dir = Path.Combine(ctx.Root, "multi");
        Directory.CreateDirectory(dir);
        byte[] data = new byte[64];
        data[0x10] = 0xCA;
        data[0x11] = 0xFE;
        data[0x12] = 0xBA;
        data[0x13] = 0xBE;
        File.WriteAllBytes(Path.Combine(dir, "a.bin"), data);
        return dir;
    }

    private static async Task<JsonObject> MultiFileSearchAsync(AppSession app, string folder, string mode)
    {
        await app.SendAsync("multiFile", new JsonObject
        {
            ["show"] = true, ["mode"] = mode, ["kind"] = "Hex", ["query"] = "CA FE BA BE", ["replace"] = "11 22 33 44", ["folders"] = new JsonArray(folder),
        });
        await app.SendAsync("multiFile", new JsonObject { ["search"] = true });
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("multiFile"))["state"]?.GetValue<string>() is "Completed" && !state["running"]!.GetValue<bool>(),
            UiTest.Scaled(TimeSpan.FromSeconds(60)), "the search");
        return state;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-30-08")]
    public Task Multi_file_result_enter_opens_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await MultiFileSearchAsync(app, MultiFileFolder(ctx), "search");
        await app.WaitForAsync("MultiFile_Results");
        await FocusRowAsync(app, "MultiFile_Results", 1);
        WindowKeys.Press(app, Enter);
        await app.WaitForTabsAsync(2);
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) == 0x10, TimeSpan.FromSeconds(10), "the cursor at the match");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-31-06")]
    public Task Multi_file_result_space_toggles_the_check_in_replace_mode() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("dummy.bin", new byte[16])] });
        await MultiFileSearchAsync(app, MultiFileFolder(ctx), "replace");
        await app.WaitForAsync("MultiFile_Results");
        async Task<bool?> CheckedAsync() => (await app.SendAsync("multiFile"))["rows"]!.AsArray()[1]!["checked"]?.GetValue<bool>();
        bool? initial = await CheckedAsync();
        await FocusRowAsync(app, "MultiFile_Results", 1);
        WindowKeys.Press(app, Space);
        await app.WaitUntilAsync(async () => await CheckedAsync() != initial, TimeSpan.FromSeconds(5), "Space to toggle the check");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-19-05")]
    public Task Script_list_enter_runs_the_script() => UiTestContext.RunAsync(async ctx =>
    {
        const string Hello = "// ==HexEditorScript==\n// @name hello\n// ==/HexEditorScript==\nconsole.log('hello from js');\n";
        string profile = ctx.NewProfile();
        string dir = Path.Combine(profile, "scripts");
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "hello.js");
        File.WriteAllText(script, Hello);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.WriteFile("data.bin", new byte[256])] });
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Always allow");
        async Task<bool> OutputAsync() => (await app.SendAsync("scriptUi", new JsonObject { ["action"] = "outputRows" }))["rows"]!.AsArray()
            .Any(r => r!["text"]!.GetValue<string>() == "hello from js");
        await app.WaitUntilAsync(OutputAsync, UiTest.Scaled(TimeSpan.FromSeconds(20)), "the first run");
        await app.SendAsync("scriptUi", new JsonObject { ["action"] = "outputClear" });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.scripts" });
        await app.WaitForAsync("ScriptList_Tree");
        string? focused = await app.FocusItemAsync("ScriptList_Tree", 0, nodeText: "hello");
        Assert.StartsWith("TreeViewItem", focused ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(OutputAsync, UiTest.Scaled(TimeSpan.FromSeconds(20)), "Enter to run the script");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-13")]
    public Task Struct_diff_row_enter_selects_the_range() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-STRUCT-PNG-A");
        string b = ctx.TestData("TD-ANA-STRUCT-PNG-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await app.SendAsync("compareOpen", new JsonObject { ["left"] = a, ["right"] = b, ["method"] = "simple" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        await app.SendAsync("compareWait", null, UiTest.Scaled(TimeSpan.FromSeconds(90)));
        await app.SendAsync("structWait", null, UiTest.Scaled(TimeSpan.FromSeconds(90)));
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "structDiffs" });
        await app.SendAsync("structRow", new JsonObject { ["row"] = 0 });
        JsonObject state = await app.SendAsync("structKey", new JsonObject { ["key"] = "Right" });
        JsonObject width = state["rows"]!.AsArray().Select(r => r!.AsObject()).First(r => r["leftPath"]?.GetValue<string>() == "chunks[0].header.width");
        await app.SendAsync("structRow", new JsonObject { ["row"] = width["index"]!.GetValue<long>() });
        await app.WaitForAsync("StructDiff_Tree");
        Assert.Contains("Row", await app.FocusItemAsync("StructDiff_Tree", 0, keep: true) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.SendAsync("structState"))["left"]!["selectionStart"]!.GetValue<long>() == 0x10, TimeSpan.FromSeconds(5), "Enter to select the range");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-21-07")]
    public Task Template_tree_enter_goes_to_the_data() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PNG")], ExtraArgs = ["--template", "PNG image"] });
        await TemplateUiTests.WaitTemplatesAsync(app);
        await TemplateUiTests.TemplateAsync(app, "select", new JsonObject { ["path"] = "chunks" });
        await TemplateUiTests.TemplateAsync(app, "key", new JsonObject { ["key"] = "Right" });
        await TemplateUiTests.TemplateAsync(app, "key", new JsonObject { ["key"] = "Right" });
        Assert.Equal("chunks[0]", (await TemplateUiTests.TemplateAsync(app, "state"))["selectedPath"]!.GetValue<string>());
        await app.GoToAsync(0x40);
        Assert.StartsWith("TemplateTreeRow", await app.FocusItemAsync("Tpl_Tree", 0, keep: true) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["primaryStart"]?.GetValue<long>() == 8, TimeSpan.FromSeconds(5), "Enter to select chunks[0]");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-27-06")]
    public Task Template_output_enter_opens_the_row_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")], ExtraArgs = ["--template", TemplateUiTests.Fixture("runtime-err.hxt")] });
        await TemplateUiTests.WaitTemplatesAsync(app);
        JsonArray rows = [];
        await app.WaitUntilAsync(async () => (rows = (await TemplateUiTests.TemplateAsync(app, "output"))["rows"]!.AsArray()).Any(r => r!["severity"]!.GetValue<string>() == "Error"),
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the error row");
        int index = rows.Select((r, i) => (r, i)).First(x => x.r!["severity"]!.GetValue<string>() == "Error").i;
        await app.WaitForAsync("TplOut_List");
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("TplOut_List", index) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitForAsync("TplOut_ToData");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-21-06")]
    public Task Encoding_list_enter_chooses_the_encoding() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-VIEW-PATTERNS")] });
        await app.SendAsync("encodingList", new JsonObject { ["open"] = true });
        await app.WaitForAsync("EncodingList_Filter");
        await app.UiaSetValueAsync("EncodingList_Filter", "1252");
        await app.IdleAsync();
        JsonArray items = (await app.SendAsync("encodingList"))["items"]!.AsArray();
        int index = items.Select((x, i) => (x, i)).First(x => x.x!["id"]!.GetValue<string>() == "cp1252").i;
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("EncodingList_Items", index) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.SendAsync("encodingList"))["encoding"]!.GetValue<string>() == "cp1252", TimeSpan.FromSeconds(5), "Enter to choose the encoding");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TOOL-20-03")]
    public Task Tool_result_table_enter_opens_the_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false, Hooks = ToolsCalcTests.EnUs });
        await app.SendAsync("tcFolder", ToolsCalcTests.Args("compare", ("left", HexEditor.TestData.TestDataCatalog.FolderOf("TD-TOOL-FOLDER-L")), ("right", HexEditor.TestData.TestDataCatalog.FolderOf("TD-TOOL-FOLDER-R"))));
        JsonObject state = await app.SendAsync("tcFolder", ToolsCalcTests.Args("state"));
        int index = state["rows"]!.AsArray().Select((r, i) => (r, i)).First(x => x.r!.AsArray()[1]!["text"]!.GetValue<string>() == "sub/diff.bin").i;
        await app.WaitForAsync("FolderCmp_List");
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("FolderCmp_List", index) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.SendAsync("compareState"))["count"]!.GetValue<int>() == 1, UiTest.Scaled(TimeSpan.FromSeconds(60)), "Enter to open the file comparison");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-09-06")]
    public Task File_system_list_enter_jumps_to_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData("TD-FOR-NTFS"), ["sectorSize"] = 512, ["readOnly"] = true });
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.True(shown["opened"]!.GetValue<bool>(), shown.ToJsonString());
        IReadOnlyList<(long Offset, long Length)> fragments = HexEditor.TestData.FileSystemImages.FileSystemImageSet.Ntfs().Fragments("\\frag.bin");
        await app.SendAsync("fsBrowser", new JsonObject { ["select"] = "frag.bin" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        await app.WaitForAsync("Fs_List");
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("Fs_List", 0, keep: true) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) == fragments[0].Offset, TimeSpan.FromSeconds(10), "Enter to jump to the file");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-06-08")]
    public Task Partition_list_enter_goes_to_the_start() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData("TD-FOR-GPT"), ["sectorSize"] = 512, ["readOnly"] = true });
        JsonObject result = [];
        await app.WaitUntilAsync(async () => (result = await app.SendAsync("fsPartitions"))["done"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)), "the partition analysis");
        JsonArray rows = result["rows"]!.AsArray();
        int index = rows.Select((r, i) => (r, i)).First(x => x.r!["number"]!.GetValue<int>() == 2).i;
        long start = rows[index]!["startLba"]!.GetValue<long>() * 512;
        await app.WaitForAsync("Partitions_List");
        await app.GoToAsync(0);
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("Partitions_List", index) ?? string.Empty);
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) == start, TimeSpan.FromSeconds(5), "Enter to go to the partition start");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-20-02")]
    public Task History_list_enter()=> UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await TypeAtAsync(app, 0x10, "A1");
        await TypeAtAsync(app, 0x20, "A2");
        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.history"));
        await app.WaitForAsync("History_List");
        Assert.StartsWith("ListViewItem", await app.FocusItemAsync("History_List", 1) ?? "");
        WindowKeys.Press(app, Enter);
        await app.WaitUntilAsync(async () => (await app.SendAsync("historyPanel"))["rows"]!.AsArray()[1]!["current"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "Enter");
    });
}
