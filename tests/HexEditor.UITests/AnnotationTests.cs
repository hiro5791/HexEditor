using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.InspectorTests;

namespace HexEditor.UITests;

/// <summary>
/// フェーズ 2 のインスペクタの型 (INSP-06〜INSP-19 の UI)、ブックマークのグループと変換 (INSP-27・INSP-28)、位置マネージャ (INSP-31)、
/// 注釈 (INSP-32)、色付けルールと凡例 (INSP-33・INSP-34)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class AnnotationTests
{
    /// <summary>インスペクタの行の構成 (表示する型) を設定ファイルに書いてから起動する。</summary>
    private static async Task<AppSession> StartWithRowsAsync(UiTestContext ctx, params string[] types)
    {
        string profile = ctx.NewProfile();
        var rows = new JsonArray([.. types.Select(t => (JsonNode?)new JsonObject { ["id"] = t, ["visible"] = true })]);
        ViewOps.WriteSettings(profile, new JsonObject { ["inspector.rows"] = new JsonObject { ["groups"] = new JsonArray(), ["rows"] = rows }.ToJsonString() });
        return await InspectorTests.StartAsync(ctx, profile: profile);
    }

    private static async Task<AppSession> StartFileAsync(UiTestContext ctx, string data = "TD-SEQ-1M", string? profile = null) =>
        await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData(data)], Profile = profile });

    private static async Task ShowPanelAsync(AppSession app, string command, string panel)
    {
        if (!await app.IsShownAsync(panel))
        {
            await app.CommandAsync(command);
        }

        await app.WaitForAsync(panel);
        await app.IdleAsync();
    }

    // ---- INSP-06・07・10・12・18・19 (インスペクタの UI) ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-06-02")]
    public Task Long_double_round_trips() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "float80");
        await GoAsync(app, 0x80);
        await WriteAsync(app, "float80", "3.14159265358979323846");
        Assert.Equal(Convert.FromHexString("35C26821A2DA0FC90040"), await app.BytesAsync(0x80, 10));
        Assert.Equal("3.1415926535897932385", await ValueAsync(app, "float80"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-07-02")]
    public Task Uleb128_is_written_with_the_same_length() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "uleb128", "sqlitevarint");
        await GoAsync(app, 0x30);
        await WriteAsync(app, "uleb128", "1");
        Assert.Equal(new byte[] { 0x81, 0x80, 0x00 }, await app.BytesAsync(0x30, 3));
        Assert.Equal("1 (3 bytes)", await ValueAsync(app, "uleb128"));

        await BeginEditAsync(app, "uleb128", "2097152");
        await app.WaitUntilAsync(async () => (await RowAsync(app, "uleb128"))["error"]?.GetValue<string>() is { Length: > 0 }, TimeSpan.FromSeconds(5), "the error");
        Assert.Contains("was 3 bytes, now 4 bytes", (await RowAsync(app, "uleb128"))["error"]!.GetValue<string>());
        await PanelKeyAsync(app, "Enter");
        Assert.True((await RowAsync(app, "uleb128"))["editing"]!.GetValue<bool>());
        await PanelKeyAsync(app, "Escape");

        await GoAsync(app, 0x34);
        await BeginEditAsync(app, "sqlitevarint", "1");
        await app.WaitUntilAsync(async () => (await RowAsync(app, "sqlitevarint"))["error"]?.GetValue<string>() is { Length: > 0 }, TimeSpan.FromSeconds(5), "the error");
        Assert.Contains("was 2 bytes, now 1 bytes", (await RowAsync(app, "sqlitevarint"))["error"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-10-02")]
    public Task Nul_terminated_string_is_rewritten() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "cstrAnsi");
        await GoAsync(app, 0x40);

        // 元の長さ (6 バイト) を超える文字列は書けない (手順 3。書き換えの前の文字列で確かめる)。
        await BeginEditAsync(app, "cstrAnsi", "HelloWorld");
        await app.WaitUntilAsync(async () => (await RowAsync(app, "cstrAnsi"))["error"]?.GetValue<string>() is { Length: > 0 }, TimeSpan.FromSeconds(5), "the error");
        Assert.Equal("Longer than the original length (6 bytes)", (await RowAsync(app, "cstrAnsi"))["error"]!.GetValue<string>());
        await PanelKeyAsync(app, "Escape");

        await WriteAsync(app, "cstrAnsi", "\"Hi\"");
        Assert.Equal(new byte[] { 0x48, 0x69, 0, 0, 0, 0 }, await app.BytesAsync(0x40, 6));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-12-02")]
    public Task Color_row_shows_a_swatch() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "rgba8");
        await GoAsync(app, 0x50);
        // 行の値の更新の後で、一覧の要素の色見本が描き直される。
        JsonObject row = null!;
        await app.WaitUntilAsync(async () => (row = await RowAsync(app, "rgba8"))["swatch"] is not null, TimeSpan.FromSeconds(5), "the swatch");
        Assert.Contains("#FF8000FF", row["value"]!.GetValue<string>());
        Assert.Equal("#FF8000", row["swatch"]!.GetValue<string>()[..7]);
        Assert.True(row["swatchBorder"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-12-03")]
    public Task Color_values_are_text_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "rgba8");
        await app.SendAsync("setHighContrast", new JsonObject { ["value"] = true });
        await GoAsync(app, 0x50);
        string value = (await RowAsync(app, "rgba8"))["value"]!.GetValue<string>();
        Assert.Contains("#FF8000FF", value);
        Assert.Contains("R 255, G 128, B 0, A 255", value);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-18-02")]
    public Task Uleb128_highlight_covers_the_used_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartWithRowsAsync(ctx, "uleb128");
        await GoAsync(app, 0x30);
        await app.SendAsync("inspectorHover", new JsonObject { ["id"] = "uleb128" });
        Assert.Equal(new HashSet<long> { 0x30, 0x31, 0x32 }, await HighlightedAsync(app, 3, "hex"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-19-03")]
    public Task Preset_all_shows_every_type() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await InspectorTests.StartAsync(ctx);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Preset");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_Preset", ["index"] = 1 });
        await app.SendAsync("panelKey", new JsonObject { ["key"] = "Escape" });
        await WaitForRowsAsync(app);
        List<string> rows = await RowIdsAsync(app);
        foreach (string id in new[]
        {
            "int8", "uint64", "int24", "uint128", "float", "double", "half", "bfloat16", "float80", "real48", "fixed_s32_16", "fixed_s16_8", "uleb128",
            "sleb128", "sqlitevarint", "binary8", "ansi", "utf8", "utf16", "cstrAnsi", "pstr32Utf8", "guid", "uuid", "rgba8", "bgra8", "rgb8", "rgb565",
            "unix32", "filetime", "dosdatetime", "unixms", "oledate", "hfsplus", "apfs", "cocoa", "sqldatetime", "sqlsmalldatetime", "dotnetdatetime",
            "digitsdatetime",
        })
        {
            Assert.Contains(id, rows);
        }

        await app.UiaInvokeAsync("Inspector_Rows");
        await app.UiaInvokeAsync("Inspector_ResetRows");
        await app.SendAsync("panelKey", new JsonObject { ["key"] = "Escape" });
        await WaitForRowsAsync(app);
        List<string> basic = [.. (await RowIdsAsync(app)).Where(id => !id.StartsWith("Inspector_Group_", StringComparison.Ordinal))];
        Assert.Equal(18, basic.Count);
        Assert.DoesNotContain("half", basic);
    });

    // ---- INSP-27 ----

    /// <summary>G に 0x100 と 0x200、グループなしの 0x300 (TC-INSP-27-01 の前提)。</summary>
    private static async Task<AppSession> StartGroupsAsync(UiTestContext ctx)
    {
        AppSession app = await StartFileAsync(ctx);
        await ShowPanelAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        foreach (long at in new long[] { 0x100, 0x200, 0x300 })
        {
            await app.GoToAsync(at);
            await app.KeyAsync("F2", ctrl: true);
        }

        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "create", ["name"] = "G" });
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "set", ["start"] = 0x100, ["group"] = "G" });
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "set", ["start"] = 0x200, ["group"] = "G" });
        await app.GoToAsync(0);
        await app.IdleAsync();
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-27-01")]
    public Task Hidden_group_is_not_shown_and_skipped() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartGroupsAsync(ctx);
        Assert.True((await app.SendAsync("bmGroup", new JsonObject { ["action"] = "select", ["path"] = "G" }))["selected"]!.GetValue<bool>());
        Assert.True((await app.SendAsync("bmGroup", new JsonObject { ["action"] = "key", ["key"] = "Space" }))["handled"]!.GetValue<bool>());
        foreach ((long at, bool shown) in new[] { (0x100L, false), (0x200L, false), (0x300L, true) })
        {
            await app.GoToAsync(at);
            await app.IdleAsync();
            Assert.Equal(shown, (await HighlightedAsync(app, 7, "hex", "bookmark:")).Contains(at));
        }

        await app.GoToAsync(0);
        await app.KeyAsync("F2");
        Assert.Equal(0x300, await ViewOps.CursorAsync(app));
        await app.KeyAsync("F2");
        Assert.Equal(0x300, await ViewOps.CursorAsync(app));

        JsonArray tree = (await app.SendAsync("bmTree"))["entries"]!.AsArray();
        Assert.All(tree.Where(e => e!["kind"]!.GetValue<string>() == "bookmark" && e["group"]?.GetValue<string>() == "G"), e => Assert.True(e!["dimmed"]!.GetValue<bool>()));
        Assert.False(tree.Single(e => e!["kind"]!.GetValue<string>() == "bookmark" && e["start"]!.GetValue<long>() == 0x300)!["dimmed"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-27-02")]
    public Task Group_color_is_inherited() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartGroupsAsync(ctx);
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "bookmarkColor", ["start"] = 0x200, ["index"] = 5 });
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "color", ["path"] = "G", ["index"] = 3 });
        await app.IdleAsync();
        JsonObject h = await app.SendAsync("highlights");
        string? Background(long at) => h["segments"]!.AsArray().First(s => s!["layer"]!.GetValue<int>() == 7 && s["first"]!.GetValue<long>() == at)!["background"]?.GetValue<string>();
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "color", ["path"] = "G", ["index"] = 0 });
        await app.IdleAsync();
        JsonObject plain = await app.SendAsync("highlights");
        string? PlainBackground(long at) => plain["segments"]!.AsArray().First(s => s!["layer"]!.GetValue<int>() == 7 && s["first"]!.GetValue<long>() == at)!["background"]?.GetValue<string>();
        Assert.NotEqual(PlainBackground(0x100), Background(0x100));
        Assert.Equal(PlainBackground(0x200), Background(0x200));

        JsonArray tree = (await app.SendAsync("bmTree"))["entries"]!.AsArray();
        Assert.Equal("5", tree.Single(e => e!["kind"]!.GetValue<string>() == "bookmark" && e["start"]!.GetValue<long>() == 0x200)!["color"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-27-03")]
    public Task Deleting_a_group_moves_bookmarks_to_the_parent() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await ShowPanelAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        foreach (long at in new long[] { 0x100, 0x200 })
        {
            await app.GoToAsync(at);
            await app.KeyAsync("F2", ctrl: true);
            await app.SendAsync("bmGroup", new JsonObject { ["action"] = "set", ["start"] = at, ["group"] = "P/C" });
        }

        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "showDelete", ["path"] = "P/C" });
        await app.UiaInvokeAsync("Bookmarks_DeleteGroupMoveToParent");
        await app.IdleAsync();
        List<string> Paths(JsonArray tree) => [.. tree.Select(e => e!["kind"]!.GetValue<string>() == "group" ? "G:" + e["path"]!.GetValue<string>() : "B:" + e["group"]?.GetValue<string>())];
        Assert.Equal(["G:P", "B:P", "B:P"], Paths((await app.SendAsync("bmTree"))["entries"]!.AsArray()));

        await app.SendAsync("noticeUndo");
        await app.IdleAsync();
        Assert.Equal(["G:P", "G:P/C", "B:P/C", "B:P/C"], Paths((await app.SendAsync("bmTree"))["entries"]!.AsArray()));
    });

    // ---- INSP-28 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-28-01")]
    public Task Multi_selection_becomes_bookmarks_in_a_group() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.CopyTestData("TD-SEQ-1M")],
            Hooks = new JsonObject { ["frozenTime"] = "2026-10-07T12:00:00Z" },
        });
        await ShowPanelAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        await app.SendAsync("multiSelection", new JsonObject { ["ranges"] = new JsonArray(new JsonArray(0x10, 16), new JsonArray(0x40, 16), new JsonArray(0x80, 16)) });
        await app.CommandAsync("Command_SelectionToBookmarks");
        await app.IdleAsync();
        JsonArray tree = (await app.SendAsync("bmTree"))["entries"]!.AsArray();
        JsonObject group = Assert.Single(tree, e => e!["kind"]!.GetValue<string>() == "group")!.AsObject();
        Assert.StartsWith("Selections ", group["path"]!.GetValue<string>());
        Assert.Contains("2026", group["path"]!.GetValue<string>());
        var bookmarks = tree.Where(e => e!["kind"]!.GetValue<string>() == "bookmark").Select(e => (e!["name"]!.GetValue<string>(), e["start"]!.GetValue<long>())).ToList();
        Assert.Equal([("Selection 1", 0x10L), ("Selection 2", 0x40L), ("Selection 3", 0x80L)], bookmarks);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-28-02")]
    public Task Bookmarks_become_a_multi_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await ShowPanelAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        foreach ((long start, long length) in new[] { (0x10L, 16L), (0x40L, 16L), (0x80L, 16L) })
        {
            await app.SelectAsync(start, length);
            await app.KeyAsync("F2", ctrl: true);
        }

        await app.SendAsync("bookmarksAdd", new JsonObject { ["step"] = 0x100, ["length"] = 0, ["count"] = 2 });
        await app.IdleAsync();
        await app.SendAsync("bookmarksSelect", new JsonObject { ["names"] = new JsonArray("Bookmark 1", "Bookmark 2", "Bookmark 3", "bm1") });
        await app.SendAsync("bmGroup", new JsonObject { ["action"] = "toSelection" });
        JsonArray ranges = (await app.SendAsync("multiSelection"))["ranges"]!.AsArray();
        Assert.Equal([(0x10L, 16L), (0x40L, 16L), (0x80L, 16L)], ranges.Select(r => (r![0]!.GetValue<long>(), r[1]!.GetValue<long>())));
    });

    // ---- INSP-31 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-31-01")]
    public Task Position_manager_edits_reach_the_bookmark_list() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await ShowPanelAsync(app, "Command_ToggleBookmarks", "BookmarksPanel");
        foreach (long at in new long[] { 0x10, 0x20, 0x100 })
        {
            await app.GoToAsync(at);
            await app.KeyAsync("F2", ctrl: true);
        }

        await app.GoToAsync(0x500);
        await ShowPanelAsync(app, "Command_TogglePositionManager", "PositionManagerPanel");
        await app.SendAsync("positionManager", new JsonObject { ["action"] = "select", ["index"] = 1 });
        await app.WaitUntilAsync(async () => await ViewOps.CursorAsync(app) == 0x20, TimeSpan.FromSeconds(5), "the cursor at the second bookmark");
        await app.SendAsync("positionManager", new JsonObject { ["action"] = "comment", ["text"] = "updated comment" });
        await app.IdleAsync();
        JsonObject b = (await app.SendAsync("bookmarks"))["bookmarks"]!.AsArray().Single(x => x!["start"]!.GetValue<long>() == 0x20)!.AsObject();
        Assert.Equal("updated comment", b["comment"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-31-02")]
    public Task Bookmark_tooltip_shows_rendered_markdown() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await app.SelectAsync(0x100, 16);
        await app.KeyAsync("F2", ctrl: true);
        await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = 0x100 });
        await app.WaitForAsync("Bookmark_Name");
        await app.UiaSetValueAsync("Bookmark_Name", "hdr");
        await app.UiaSetValueAsync("Bookmark_Comment", "# Title\n**bold** text");
        await app.KeyAsync("Escape");
        await app.GoToAsync(0x400);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        await ViewOps.PointerAsync(app, "move", ViewOps.CellPoint(render, 0x104));
        Assert.False((await app.RenderAsync())["toolTip"]!["open"]!.GetValue<bool>());
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["toolTip"]!["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the tooltip");
        JsonArray runs = (await app.RenderAsync())["toolTip"]!["rich"]!.AsArray();
        Assert.Contains(runs, r => r!["text"]!.GetValue<string>() == "hdr");
        Assert.Contains(runs, r => r!["text"]!.GetValue<string>().Contains("0x100") && r["text"]!.GetValue<string>().Contains("0x10F") && r["text"]!.GetValue<string>().Contains("16"));
        JsonObject title = runs.First(r => r!["text"]!.GetValue<string>() == "Title")!.AsObject();
        Assert.True(title["bold"]!.GetValue<bool>() && title["size"]!.GetValue<double>() > 14);
        Assert.True(runs.First(r => r!["text"]!.GetValue<string>() == "bold")!["bold"]!.GetValue<bool>());
        Assert.DoesNotContain(runs, r => r!["text"]!.GetValue<string>().Contains("**") || r["text"]!.GetValue<string>().StartsWith('#'));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-31-03")]
    public Task Description_at_cursor_is_readable_with_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await app.SelectAsync(0x100, 16);
        await app.KeyAsync("F2", ctrl: true);
        await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = 0x100 });
        await app.WaitForAsync("Bookmark_Name");
        string paragraph = string.Concat(Enumerable.Repeat("long description ", 180)).Trim();
        await app.UiaSetValueAsync("Bookmark_Name", "hdr");
        await app.UiaSetValueAsync("Bookmark_Comment", "# Title\n**bold** text\n\n" + paragraph);
        await app.KeyAsync("Escape");
        await app.GoToAsync(0x104);
        await app.CommandAsync("Command_ShowDescription");
        // フライアウトが開いた直後は Popup にフォーカスがあることがある (遅い環境)。説明の領域にフォーカスが入るまで待つ。
        await app.WaitUntilAsync(async () => (await app.SendAsync("descriptionFlyout"))["focused"]!.GetValue<bool>()
            && await app.FocusedAsync() == "ScrollViewer:Annotations_DescriptionFlyout", TimeSpan.FromSeconds(5), "the focus in the description flyout");
        JsonObject flyout = await app.SendAsync("descriptionFlyout");
        Assert.Contains(flyout["runs"]!.AsArray(), r => r!["text"]!.GetValue<string>() == paragraph);
        Assert.True(flyout["scrollableHeight"]!.GetValue<double>() > 0);
        await app.SendAsync("descriptionFlyout", new JsonObject { ["key"] = "PageDown" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("descriptionFlyout"))["verticalOffset"]!.GetValue<double>() > 0, TimeSpan.FromSeconds(5), "the scroll");
        await app.SendAsync("descriptionFlyout", new JsonObject { ["key"] = "Escape" });
        await app.WaitUntilAsync(async () => !(await app.SendAsync("descriptionFlyout"))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the flyout to close");
        await app.WaitUntilAsync(async () => (await app.FocusedAsync())?.StartsWith("HexView", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5), "the focus back on the hex view");
    });

    // ---- INSP-32 ----

    /// <summary>TD-INSP-SCRIPT-ANNOT3 と同じ 3 つの注釈 (出どころ「スクリプト」)。スクリプトはフェーズ 3 なので、テスト用の命令で付ける。</summary>
    private static Task AddThreeAsync(AppSession app) => app.SendAsync("annotationsAdd", new JsonObject
    {
        ["id"] = "script:annot3",
        ["origin"] = "Script",
        ["name"] = "annot3.js",
        ["items"] = new JsonArray(
            new JsonObject { ["start"] = 0x00, ["length"] = 0x40, ["label"] = "outer", ["description"] = "outer desc" },
            new JsonObject { ["start"] = 0x08, ["length"] = 0x18, ["label"] = "middle", ["description"] = "middle desc" },
            new JsonObject { ["start"] = 0x10, ["length"] = 1, ["label"] = "inner", ["description"] = "inner desc" }),
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-32-02")]
    public Task Annotation_column_shows_labels() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await AddThreeAsync(app);
        await app.CommandAsync("Command_AnnotationsColumn");
        await app.IdleAsync();
        JsonObject column = await app.SendAsync("annotationColumn");
        Assert.True(column["visible"]!.GetValue<bool>());
        Assert.True(column["x"]!.GetValue<double>() > column["textX"]!.GetValue<double>());
        JsonArray rows = column["rows"]!.AsArray();
        Assert.Equal("outer +1", rows.Single(r => r!["row"]!.GetValue<long>() == 0)!["text"]!.GetValue<string>());
        Assert.Equal("inner", rows.Single(r => r!["row"]!.GetValue<long>() == 1)!["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-32-04")]
    public Task Overlapping_annotations_in_the_tooltip_and_levels() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx);
        await AddThreeAsync(app);
        await app.GoToAsync(0x80);
        await app.IdleAsync();
        JsonObject render = await app.RenderAsync();
        await ViewOps.PointerAsync(app, "move", ViewOps.CellPoint(render, 0x10));
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["toolTip"]!["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the tooltip");
        string text = string.Join("\n", (await app.RenderAsync())["toolTip"]!["rich"]!.AsArray().Select(r => r!["text"]!.GetValue<string>()));
        foreach (string part in new[] { "outer", "middle", "inner", "outer desc", "middle desc", "inner desc", "Script" })
        {
            Assert.Contains(part, text);
        }

        JsonArray all = (await app.SendAsync("highlights"))["segments"]!.AsArray();
        var segments = all
            .Where(s => s!["layer"]!.GetValue<int>() == 8 && s["column"]!.GetValue<string>() == "hex" && s["first"]!.GetValue<long>() <= 0x10 && s["last"]!.GetValue<long>() >= 0x10)
            .Select(s => s!.AsObject()).ToList();
        Assert.True(segments.Count == 3, all.ToJsonString());
        JsonObject top = segments.MaxBy(s => s["order"]!.GetValue<int>())!;
        Assert.EndsWith(":inner", top["tag"]!.GetValue<string>());
        Assert.Equal(2, top["level"]!.GetValue<int>());
    });

    // ---- INSP-33・INSP-34 ----

    private static Task<JsonObject> ColoringAsync(AppSession app, JsonObject request) => app.SendAsync("coloring", request);

    private static Task AddRuleAsync(AppSession app, string scope, JsonObject rule) =>
        ColoringAsync(app, new JsonObject { ["scope"] = scope, ["action"] = "add", ["rule"] = rule });

    /// <summary>色付けルールの評価 (別のスレッド) が終わって、層 10 の強調が描かれるまで待つ。</summary>
    private static async Task<JsonObject> WaitColoringAsync(AppSession app, Func<JsonObject, bool> ready)
    {
        JsonObject? last = null;
        try
        {
            await app.WaitUntilAsync(async () =>
            {
                await app.IdleAsync();
                try
                {
                    last = await app.SendAsync("highlights");
                }
                catch (InvalidOperationException e) when (e.Message.Contains("No hex view", StringComparison.Ordinal))
                {
                    // タブを切り替えた直後は、そのタブの Hex ビューがまだできていないことがある。
                    return false;
                }

                return ready(last);
            }, TimeSpan.FromSeconds(10), "the coloring rules to be drawn");
        }
        catch (TimeoutException ex)
        {
            JsonObject state = await app.SendAsync("coloring", new JsonObject());
            throw new TimeoutException(ex.Message + " coloring: " + state.ToJsonString() + " highlights: "
                + (last is null ? string.Empty : string.Join(";", Layer10(last).Take(5).Select(s => s.ToJsonString()))), ex);
        }

        return last!;
    }

    private static IEnumerable<JsonObject> Layer10(JsonObject highlights, string column = "hex") =>
        highlights["segments"]!.AsArray().Select(s => s!.AsObject()).Where(s => s["layer"]!.GetValue<int>() == 10 && s["column"]!.GetValue<string>() == column);

    private static bool Covers(JsonObject s, long offset) => s["first"]!.GetValue<long>() <= offset && s["last"]!.GetValue<long>() >= offset;

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-33-01")]
    public Task Dim_zero_preset_dims_zero_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx, "TD-BYTES-256");
        await ShowPanelAsync(app, "Command_ToggleColoringRules", "ColoringRulesPanel");
        await ColoringAsync(app, new JsonObject { ["scope"] = "global", ["action"] = "enable", ["name"] = "Dim zeros", ["on"] = true });
        JsonObject h = await WaitColoringAsync(app, x => Layer10(x).Any(s => Covers(s, 0)));
        Assert.DoesNotContain(Layer10(h), s => Covers(s, 1));
        await app.WaitUntilAsync(async () => ViewOps.CellOf(await app.RenderAsync(), 0)!["foreground"]!.GetValue<string>().StartsWith("#FF9A9A9A", StringComparison.OrdinalIgnoreCase)
            || ViewOps.CellOf(await app.RenderAsync(), 0)!["foreground"]!.GetValue<string>().EndsWith("9A9A9A", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10), "the dim foreground");
        Assert.DoesNotContain("9A9A9A", ViewOps.CellOf(await app.RenderAsync(), 1)!["foreground"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-33-04")]
    public Task Global_and_document_rules_apply_to_their_documents() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq, bytes], Profile = profile });
        await AddRuleAsync(app, "global", new JsonObject { ["name"] = "A", ["pattern"] = "41", ["background"] = "#FF0000" });
        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "B", ["pattern"] = "42", ["background"] = "#0000FF" });

        async Task CheckAsync(AppSession session)
        {
            await session.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
            JsonObject h = await WaitColoringAsync(session, x => Layer10(x).Any(s => Covers(s, 0x41)) && Layer10(x).Any(s => Covers(s, 0x42)));
            Assert.Equal("#FFFF0000", Layer10(h).First(s => Covers(s, 0x41))["background"]!.GetValue<string>(), ignoreCase: true);
            Assert.Equal("#FF0000FF", Layer10(h).First(s => Covers(s, 0x42))["background"]!.GetValue<string>(), ignoreCase: true);
            await session.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
            h = await WaitColoringAsync(session, x => Layer10(x).Any(s => Covers(s, 0x41)));
            Assert.DoesNotContain(Layer10(h), s => Covers(s, 0x42));
        }

        await CheckAsync(app);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        AppSession again = await ctx.StartAsync(new AppOptions { Files = [seq, bytes], Profile = profile });
        await CheckAsync(again);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-33-06")]
    public Task Coloring_rules_do_not_change_the_file() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-ZIP");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file));
        DateTime written = File.GetLastWriteTimeUtc(file);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        foreach (string preset in new[] { "Dim zeros", "Printable ASCII", "Control characters", "FF", "High bit set" })
        {
            await ColoringAsync(app, new JsonObject { ["scope"] = "global", ["action"] = "enable", ["name"] = preset, ["on"] = true });
        }

        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "PK", ["kind"] = "HexPattern", ["pattern"] = "50 4B 03 04", ["background"] = "#FFD700" });
        await WaitColoringAsync(app, x => Layer10(x).Any());
        JsonObject doc = (await app.StateAsync())["document"]!.AsObject();
        Assert.False(doc["modified"]!.GetValue<bool>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(hash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-34-02")]
    public Task Selection_is_in_front_of_coloring_rules() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx, "TD-BYTES-256");
        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "ASCII", ["pattern"] = "20-7E", ["background"] = "#FFFF00", ["foreground"] = "#000080" });
        await WaitColoringAsync(app, x => Layer10(x).Any(s => Covers(s, 0x41)));
        await app.SelectAsync(0x40, 16);
        await app.IdleAsync();
        JsonObject cell = ViewOps.CellOf(await app.RenderAsync(), 0x41)!;
        Assert.Equal("selection", cell["hexLayer"]!.GetValue<string>());
        Assert.NotEqual("#FF000080", cell["foreground"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("#FFFFFF00", cell["hexBackground"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-34-03")]
    public Task Change_underline_is_drawn_over_coloring_rules() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx, "TD-BYTES-256");
        await AddRuleAsync(app, "document", new JsonObject
        {
            ["name"] = "All", ["pattern"] = "00-FF", ["foreground"] = "#FF0000", ["background"] = "#FFE0E0", ["border"] = "Solid",
        });
        await app.GoToAsync(0x30);
        await app.TypeAsync("AA");
        await app.GoToAsync(0x40);
        await app.KeyAsync("Insert");
        await app.TypeAsync("BB");
        await app.GoToAsync(0x80);
        JsonObject h = await WaitColoringAsync(app, x => Layer10(x).Any(s => Covers(s, 0x30)) && Layer10(x).Any(s => Covers(s, 0x40)));
        Assert.NotNull(Layer10(h).First(s => Covers(s, 0x30))["border"]);
        JsonObject render = await app.RenderAsync();
        JsonObject overwritten = ViewOps.CellOf(render, 0x30)!;
        JsonObject inserted = ViewOps.CellOf(render, 0x40)!;
        Assert.Equal("solid", overwritten["underline"]!.GetValue<string>());
        Assert.Equal("double", inserted["underline"]!.GetValue<string>());
        Assert.NotEqual("#FFFF0000", overwritten["foreground"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-34-04")]
    public Task Legend_next_moves_to_the_next_match() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-ZIP");
        byte[] data = File.ReadAllBytes(file);
        List<long> hits = [.. Enumerable.Range(0, data.Length - 3).Where(i => data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 3 && data[i + 3] == 4).Select(i => (long)i)];
        Assert.Equal(3, hits.Count);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "PK", ["kind"] = "HexPattern", ["pattern"] = "50 4B 03 04", ["background"] = "#FFD700" });
        await app.GoToAsync(0);
        await app.CommandAsync("Command_ToggleLegend");
        await app.WaitForAsync("LegendPanel");
        JsonObject render = await app.RenderAsync();
        long visibleEnd = (render["topRow"]!.GetValue<long>() + render["visibleRows"]!.GetValue<long>()) * render["bytesPerRow"]!.GetValue<int>();
        int visible = hits.Count(h => h < visibleEnd);
        JsonObject legend = await app.SendAsync("legend");
        Assert.Contains(visible.ToString(System.Globalization.CultureInfo.InvariantCulture), legend["items"]!.AsArray().Single(i => i!["name"]!.GetValue<string>() == "PK")!["count"]!.GetValue<string>());
        await app.SendAsync("legend", new JsonObject { ["action"] = "next", ["name"] = "PK" });
        Assert.Equal(hits[1], await ViewOps.CursorAsync(app));
        await app.SendAsync("legend", new JsonObject { ["action"] = "next", ["name"] = "PK" });
        Assert.Equal(hits[2], await ViewOps.CursorAsync(app));
        await app.SendAsync("legend", new JsonObject { ["action"] = "previous", ["name"] = "PK" });
        Assert.Equal(hits[1], await ViewOps.CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-34-05")]
    public Task High_contrast_uses_border_shapes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartFileAsync(ctx, "TD-BYTES-256");
        await app.SendAsync("setHighContrast", new JsonObject { ["value"] = true });
        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "A", ["pattern"] = "00-0F", ["background"] = "#FF0000" });
        await AddRuleAsync(app, "document", new JsonObject { ["name"] = "B", ["pattern"] = "10-1F", ["background"] = "#00FF00" });
        await app.CommandAsync("Command_ToggleLegend");
        JsonObject h = await WaitColoringAsync(app, x => Layer10(x).Any(s => Covers(s, 0x05)) && Layer10(x).Any(s => Covers(s, 0x15)));
        JsonObject a = Layer10(h).First(s => Covers(s, 0x05));
        JsonObject b = Layer10(h).First(s => Covers(s, 0x15));
        Assert.Null(a["background"]);
        Assert.Null(b["background"]);
        Assert.Null(a["dash"]);
        Assert.NotNull(b["dash"]);
        JsonArray items = (await app.SendAsync("legend"))["items"]!.AsArray();
        Assert.Equal("Solid border", items.Single(i => i!["name"]!.GetValue<string>() == "A")!["shape"]!.GetValue<string>());
        Assert.Equal("Dashed border", items.Single(i => i!["name"]!.GetValue<string>() == "B")!["shape"]!.GetValue<string>());

        await app.SendAsync("settingSet", new JsonObject { ["key"] = "coloring.highContrastColors", ["value"] = true });
        await app.SendAsync("coloring", new JsonObject());
        h = await WaitColoringAsync(app, x => Layer10(x).Any(s => Covers(s, 0x05) && s["background"] is not null));
        Assert.Equal("#FFFF0000", Layer10(h).First(s => Covers(s, 0x05))["background"]!.GetValue<string>(), ignoreCase: true);
    });
}
