using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ブックマーク (INSP-23〜INSP-26)。キーはメニューのショートカット (Ctrl+F2・F2・Ctrl+Shift+N など) と同じ処理に渡す。
/// 一覧の中のキーは <c>panelKey</c> で一覧に渡す。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class BookmarkTests
{
    private static async Task<AppSession> StartAsync(UiTestContext ctx, bool showList = true, string? file = null, string? profile = null)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file ?? ctx.CopyTestData("TD-SEQ-1M")], Profile = profile });
        if (showList && !(await ListAsync(app))["visible"]!.GetValue<bool>())
        {
            await app.CommandAsync("Command_ToggleBookmarks");
            await app.IdleAsync();
        }

        return app;
    }

    private static Task<JsonObject> ListAsync(AppSession app) => app.SendAsync("bookmarks");

    private static async Task<List<JsonObject>> BookmarksAsync(AppSession app) =>
        [.. (await ListAsync(app))["bookmarks"]!.AsArray().Select(b => b!.AsObject())];

    private static async Task<List<string>> RowsAsync(AppSession app) =>
        [.. (await ListAsync(app))["rows"]!.AsArray().Select(r => r!.GetValue<string>())];

    private static async Task ToggleAsync(AppSession app, long offset)
    {
        await app.GoToAsync(offset);
        Assert.Equal("menu:Command_ToggleBookmark", (await app.KeyAsync("F2", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
    }

    private static async Task RangeAsync(AppSession app, long start, long length)
    {
        await app.SelectAsync(start, length);
        await app.KeyAsync("F2", ctrl: true);
        await app.IdleAsync();
    }

    /// <summary>編集のフライアウトでブックマークの名前・コメントを変える。</summary>
    private static async Task EditAsync(AppSession app, long start, string? name = null, string? comment = null)
    {
        await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = start });
        await app.WaitForAsync("Bookmark_Name");
        if (name is not null)
        {
            await app.UiaSetValueAsync("Bookmark_Name", name);
        }

        if (comment is not null)
        {
            await app.UiaSetValueAsync("Bookmark_Comment", comment);
        }
    }

    private static async Task<long> CursorAsync(AppSession app) => await ViewOps.CursorAsync(app);

    // ---- INSP-23 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-23-01")]
    public Task Ctrl_f2_sets_and_removes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);
        JsonObject b = Assert.Single(await BookmarksAsync(app));
        Assert.Equal(("Bookmark 1", 0x100L, 1L), (b["name"]!.GetValue<string>(), b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>()));
        Assert.Equal(["Bookmark 1"], await RowsAsync(app));
        Assert.Contains(0x100L, await InspectorTests.HighlightedAsync(app, 7, "hex", "bookmark:"));
        JsonObject h = await app.SendAsync("highlights");
        JsonObject mark = Assert.Single(h["marks"]!.AsArray().Select(m => m!.AsObject()));
        double rowHeight = h["rowHeight"]!.GetValue<double>();
        long topRow = await ViewOps.TopRowAsync(app);
        Assert.Equal((0x100 / 16 - topRow) * rowHeight, mark["top"]!.GetValue<double>() - 1, 1.0);

        await app.KeyAsync("F2", ctrl: true);
        await app.IdleAsync();
        Assert.Empty(await RowsAsync(app));
        Assert.DoesNotContain(await ViewOps.NoticesAsync(app), n => n.Contains("deleted", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-23-02")]
    public Task Selection_becomes_the_range() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await RangeAsync(app, 0x200, 4);
        JsonObject b = Assert.Single(await BookmarksAsync(app));
        Assert.Equal((0x200L, 4L), (b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-23-03")]
    public Task Inserting_before_moves_and_undo_restores() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await RangeAsync(app, 0x100, 4);
        await app.GoToAsync(0x10);
        await app.KeyAsync("Insert");
        await app.TypeAsync(new string('0', 20));
        await app.IdleAsync();
        JsonObject b = Assert.Single(await BookmarksAsync(app));
        Assert.Equal((0x10AL, 4L), (b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>()));

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        b = Assert.Single(await BookmarksAsync(app));
        Assert.Equal((0x100L, 4L), (b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-23-05")]
    public Task Bookmarks_do_not_mark_the_document_modified() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);
        JsonObject state = await app.StateAsync();
        Assert.DoesNotContain("●", state["title"]!.GetValue<string>());
        JsonObject doc = state["document"]!.AsObject();
        Assert.False(doc["modified"]!.GetValue<bool>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.Equal(string.Empty, app.TextOrEmpty("Status_Modified"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-23-06")]
    public Task Bookmarks_survive_closing_and_reopening() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-SEQ-1M", "copy.bin");
        byte[] hash = SHA256.HashData(File.ReadAllBytes(file));
        AppSession app = await StartAsync(ctx, file: file);
        await ToggleAsync(app, 0x100);
        await RangeAsync(app, 0x200, 4);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));

        AppSession again = await StartAsync(ctx, file: file, profile: ctx.DefaultProfile);
        List<JsonObject> list = await BookmarksAsync(again);
        Assert.Equal([(0x100L, 1L), (0x200L, 4L)], list.Select(b => (b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>())));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(file)));
    });

    // ---- INSP-24 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-24-01")]
    public Task Names_work_in_expressions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);

        // 1: Hex ビューの右クリックメニュー「ブックマークを編集」で名前を header にする。
        JsonObject render = await app.RenderAsync();
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(render, 0x100));
        await app.WaitForAsync("HexViewMenu_EditBookmark");
        await app.CommandAsync("HexViewMenu_EditBookmark");
        await app.WaitForAsync("Bookmark_Name");
        await app.UiaSetValueAsync("Bookmark_Name", "header");
        Assert.Equal("header", Assert.Single(await BookmarksAsync(app))["name"]!.GetValue<string>());

        // 2・3: Ctrl+G で bm.header+8。
        await ViewOps.OpenGoToAsync(app);
        await app.UiaSetValueAsync("GoTo_Input", "bm.header+8");
        Assert.StartsWith("= 0x108", await app.UiaNameAsync("GoTo_Interpretation"));
        await app.UiaInvokeAsync("GoTo_Go");
        await app.IdleAsync();
        Assert.Equal(0x108, await CursorAsync(app));

        // 4: 入力式で使えない名前は、エラーではなく案内を出す (名前は保存する)。
        await ToggleAsync(app, 0x300);
        await EditAsync(app, 0x300, name: "2nd header");
        Assert.Equal("This name can't", (await app.UiaNameAsync("Bookmark_NameNote"))[..15]);
        Assert.Contains(await BookmarksAsync(app), b => b["name"]!.GetValue<string>() == "2nd header");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-24-02")]
    public Task Comment_markdown_preview() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);
        await EditAsync(app, 0x100, comment: "**太字**\n<b>html</b>");
        JsonObject editor = await app.SendAsync("bookmarkEditor", new JsonObject { ["preview"] = true });
        var runs = editor["runs"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Contains(runs, r => r["text"]!.GetValue<string>() == "太字" && r["bold"]!.GetValue<bool>());
        string all = string.Concat(runs.Select(r => r["text"]!.GetValue<string>()));
        Assert.DoesNotContain("**", all);
        JsonObject html = runs.Single(r => r["text"]!.GetValue<string>().Contains("<b>html</b>", StringComparison.Ordinal));
        Assert.False(html["bold"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-24-03")]
    public Task Document_links_in_comments() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);
        await app.GoToAsync(0x500);
        await EditAsync(app, 0x100, comment: "[ヘッダ](#0x100)\n[外部](file:///C:/Windows/notepad.exe)");
        await app.SendAsync("bookmarkEditor", new JsonObject { ["preview"] = true });

        Assert.True((await app.SendAsync("bookmarkLink", new JsonObject { ["text"] = "ヘッダ" }))["clicked"]!.GetValue<bool>());
        await app.IdleAsync();
        Assert.Equal(0x100, await CursorAsync(app));

        // 3: http / https 以外は開かない (プロセスを起動しない。ログに残す)。ドキュメント内のリンクでフライアウトは閉じている。
        await app.WaitUntilAsync(async () => !(await app.SendAsync("bookmarkEditor"))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the flyout to close");
        await EditAsync(app, 0x100);
        await app.SendAsync("bookmarkEditor", new JsonObject { ["preview"] = true });
        int before = System.Diagnostics.Process.GetProcessesByName("notepad").Length;
        Assert.True((await app.SendAsync("bookmarkLink", new JsonObject { ["text"] = "外部" }))["clicked"]!.GetValue<bool>());
        await app.WaitForLogAsync(l => l.Contains("Comment link not opened: file:///C:/Windows/notepad.exe", StringComparison.Ordinal), "the link to be refused");
        Assert.Equal(before, System.Diagnostics.Process.GetProcessesByName("notepad").Length);
        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains("Test hooks: launch file:", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-24-04")]
    public Task Eight_colors_are_distinct_in_high_contrast() => UiTestContext.RunAsync(async ctx =>
    {
        // OS のハイコントラストを変えずに、Hex ビューのハイコントラストの判定だけを差し替える (利用者の PC の設定は変えない)。
        AppSession app = await StartAsync(ctx);
        await app.SendAsync("setHighContrast", new JsonObject { ["value"] = true });
        for (int i = 0; i < 8; i++)
        {
            await RangeAsync(app, i * 0x10, 4);
        }

        // 色の一覧の 1〜8 番目 (ブックマーク i に色 i + 1)。
        for (int i = 0; i < 8; i++)
        {
            await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = i * 0x10 });
            (await app.WaitForAsync("Bookmark_Color" + (i + 1))).Patterns.SelectionItem.Pattern.Select();
            await app.IdleAsync();
        }

        await app.GoToAsync(0);
        JsonObject h = await app.SendAsync("highlights");
        var segments = h["segments"]!.AsArray().Select(s => s!.AsObject()).Where(s => s["layer"]!.GetValue<int>() == 7 && s["column"]!.GetValue<string>() == "hex").ToList();
        Assert.Equal(8, segments.Count);
        Assert.All(segments, s => Assert.Null(s["background"]));
        Assert.Equal(8, segments.Select(s => (s["border"]?.GetValue<string>(), s["dash"]?.GetValue<string>())).Distinct().Count());
        Assert.True(h["highContrast"]!.GetValue<bool>());
        await app.SendAsync("setHighContrast", new JsonObject { ["value"] = null });
    });

    // ---- INSP-25 ----

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-25-01")]
    public Task Ctrl_shift_3_sets_and_ctrl_3_goes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.GoToAsync(0x300);
        Assert.Equal("menu:Command_SetNumberedBookmark3", (await app.KeyAsync("Number3", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        JsonObject b = Assert.Single(await BookmarksAsync(app));
        Assert.Equal(("[3]", 3, 0x300L, 1L), (b["name"]!.GetValue<string>(), b["number"]!.GetValue<int>(), b["start"]!.GetValue<long>(), b["length"]!.GetValue<long>()));
        Assert.Equal("3", Assert.Single((await app.SendAsync("highlights"))["marks"]!.AsArray())!["text"]!.GetValue<string>());

        await ViewOps.GoToAsync(app, "0x8000");
        await app.KeyAsync("Number3", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(0x300, await CursorAsync(app));
        await app.KeyAsync("Left", alt: true);
        await app.IdleAsync();
        Assert.Equal(0x8000, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-25-02")]
    public Task Moving_a_number_to_another_place() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.GoToAsync(0x300);
        await app.KeyAsync("Number3", ctrl: true, shift: true);
        await ToggleAsync(app, 0x500);
        await EditAsync(app, 0x500, name: "keep");

        await app.GoToAsync(0x400);
        await app.KeyAsync("Number3", ctrl: true, shift: true);
        await app.IdleAsync();
        List<JsonObject> list = await BookmarksAsync(app);
        Assert.DoesNotContain(list, b => b["start"]!.GetValue<long>() == 0x300);
        Assert.Contains(list, b => b["start"]!.GetValue<long>() == 0x400 && b["name"]!.GetValue<string>() == "[3]" && b["number"]!.GetValue<int>() == 3);

        await app.GoToAsync(0x500);
        await app.KeyAsync("Number3", ctrl: true, shift: true);
        await app.IdleAsync();
        list = await BookmarksAsync(app);
        Assert.DoesNotContain(list, b => b["start"]!.GetValue<long>() == 0x400);
        Assert.Equal(3, list.Single(b => b["name"]!.GetValue<string>() == "keep")["number"]!.GetValue<int>());

        await app.KeyAsync("Number3", ctrl: true, shift: true);
        await app.IdleAsync();
        list = await BookmarksAsync(app);
        Assert.Equal(0, list.Single(b => b["name"]!.GetValue<string>() == "keep")["number"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-25-03")]
    public Task Going_to_an_unset_number() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.GoToAsync(0x123);
        await app.KeyAsync("Number5", ctrl: true);
        await app.WaitForNotificationAsync(m => m.Contains("Numbered bookmark 5", StringComparison.Ordinal), "the message");
        Assert.Equal(0x123, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-25-04")]
    public Task Numeric_keypad_works_too() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx);
        await app.GoToAsync(0x700);
        Assert.Equal("menu:Command_SetNumberedBookmark7", (await app.KeyAsync("NumberPad7", ctrl: true, shift: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        Assert.Contains(await BookmarksAsync(app), b => b["start"]!.GetValue<long>() == 0x700 && b["number"]!.GetValue<int>() == 7 && b["name"]!.GetValue<string>() == "[7]");

        await app.GoToAsync(0);
        await app.KeyAsync("NumberPad7", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(0x700, await CursorAsync(app));

        // 4: Ctrl+テンキーの 0 は番号付きブックマークに使わない (ズームを 100% に戻すキー。UI-08 の担当)。
        string handledBy = (await app.KeyAsync("NumberPad0", ctrl: true))["handledBy"]!.GetValue<string>();
        Assert.DoesNotContain("NumberedBookmark", handledBy);
        Assert.Single(await BookmarksAsync(app));
    });

    // ---- INSP-26 ----

    private static async Task<AppSession> ThreeAsync(UiTestContext ctx)
    {
        AppSession app = await StartAsync(ctx);
        await ToggleAsync(app, 0x100);
        await RangeAsync(app, 0x200, 4);
        await ToggleAsync(app, 0x300);
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-26-01")]
    public Task F2_goes_to_the_next_and_wraps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ThreeAsync(ctx);
        await app.GoToAsync(0);
        foreach ((long start, long length) in new[] { (0x100L, 1L), (0x200L, 4L), (0x300L, 1L) })
        {
            Assert.Equal("menu:Command_NextBookmark", (await app.KeyAsync("F2"))["handledBy"]!.GetValue<string>());
            await app.IdleAsync();
            Assert.Equal(start, await CursorAsync(app));
            await ViewOps.AssertSelectionAsync(app, start, length);
        }

        await app.KeyAsync("F2");
        await app.IdleAsync();
        Assert.Equal(0x100, await CursorAsync(app));
        await app.WaitForNotificationAsync(m => m.Contains("first bookmark", StringComparison.Ordinal), "the wrap message");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-26-02")]
    public Task Shift_f2_goes_to_the_previous() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ThreeAsync(ctx);
        await app.GoToAsync(0x250);
        foreach (long start in new long[] { 0x200, 0x100, 0x300 })
        {
            await app.KeyAsync("F2", shift: true);
            await app.IdleAsync();
            Assert.Equal(start, await CursorAsync(app));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-26-03")]
    public Task Delete_in_the_list_and_undo() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ThreeAsync(ctx);
        await EditAsync(app, 0x200, name: "header");

        // 1・2: header の行を選んで Delete。
        await app.SendAsync("bookmarksSelect", new JsonObject { ["names"] = new JsonArray("header") });
        Assert.Equal("bookmarks", (await InspectorTests.PanelKeyAsync(app, "Delete"))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
        Assert.Equal(2, (await RowsAsync(app)).Count);
        await app.WaitForNotificationAsync(m => m.Contains("Bookmark \"header\" was deleted.", StringComparison.Ordinal), "the deleted notice");

        // 3: InfoBar の「元に戻す」。
        await app.InvokeDialogButtonAsync("Undo");
        Assert.Equal(3, (await RowsAsync(app)).Count);
        JsonObject header = (await BookmarksAsync(app)).Single(b => b["name"]!.GetValue<string>() == "header");
        Assert.Equal((0x200L, 4L), (header["start"]!.GetValue<long>(), header["length"]!.GetValue<long>()));

        // 4: Ctrl+A と Delete で 0 行、「元に戻す」で 3 行。
        await app.SendAsync("bookmarksSelect", new JsonObject { ["names"] = new JsonArray("header") });
        await InspectorTests.PanelKeyAsync(app, "A", ctrl: true);
        await InspectorTests.PanelKeyAsync(app, "Delete");
        await app.IdleAsync();
        Assert.Empty(await RowsAsync(app));
        await app.WaitForNotificationAsync(m => m.Contains("3 bookmarks were deleted.", StringComparison.Ordinal), "the deleted notice");
        await app.InvokeDialogButtonAsync("Undo");
        Assert.Equal(3, (await RowsAsync(app)).Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-26-04")]
    public Task Filter_by_name_or_comment() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ThreeAsync(ctx);
        await EditAsync(app, 0x100, name: "Header");
        await EditAsync(app, 0x200, name: "body", comment: "see header");
        await EditAsync(app, 0x300, name: "footer", comment: "end");
        await app.UiaSetValueAsync("Bookmarks_Filter", "HEADER");
        Assert.Equal(["Header", "body"], await RowsAsync(app));
        await app.UiaSetValueAsync("Bookmarks_Filter", string.Empty);
        Assert.Equal(3, (await RowsAsync(app)).Count);
    });
    /// <summary>INSP-26 の仕様 1: 列の表示・順序を変えられ、再起動後も保たれる。</summary>
    [Fact]
    public Task Columns_can_be_shown_hidden_and_reordered() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await StartAsync(ctx, profile: profile);
        static string[] Header(JsonObject r) => [.. r["header"]!.AsArray().Select(h => h!.GetValue<string>())];
        Assert.Equal(["No.", "●", "Name", "Start", "Length"], Header(await app.SendAsync("bookmarkColumns")));

        Assert.Equal(["No.", "●", "Name", "Start", "Length", "Group"],
            Header(await app.SendAsync("bookmarkColumns", new JsonObject { ["action"] = "toggle", ["column"] = "Group" })));
        Assert.Equal(["No.", "●", "Name", "Start", "Group", "Length"],
            Header(await app.SendAsync("bookmarkColumns", new JsonObject { ["action"] = "move", ["column"] = "Group", ["delta"] = -1 })));
        Assert.Equal(["●", "Name", "Start", "Group", "Length"],
            Header(await app.SendAsync("bookmarkColumns", new JsonObject { ["action"] = "toggle", ["column"] = "Number" })));

        // 名前の列は非表示にできない。
        Assert.Contains("Name", Header(await app.SendAsync("bookmarkColumns", new JsonObject { ["action"] = "toggle", ["column"] = "Name" })));

        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        AppSession again = await StartAsync(ctx, profile: profile);
        Assert.Equal(["●", "Name", "Start", "Group", "Length"], Header(await again.SendAsync("bookmarkColumns")));
    });

    /// <summary>INSP-26 の仕様 8: 「すべてのドキュメント」で、開いているすべてのドキュメントのブックマークをドキュメントごとにまとめて出す。</summary>
    [Fact]
    public Task All_documents_option_lists_bookmarks_of_every_open_document() => UiTestContext.RunAsync(async ctx =>
    {
        string first = ctx.CopyTestData("TD-SEQ-1M");
        string second = ctx.CopyTestData("TD-BYTES-256");
        AppSession app = await StartAsync(ctx, file: first);
        await ToggleAsync(app, 0x100);
        await app.OpenAsync(second);
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(10), "the second tab");
        await ToggleAsync(app, 0x10);
        await ToggleAsync(app, 0x20);
        Assert.Equal(2, (await RowsAsync(app)).Count);

        await app.CommandAsync("Bookmarks_AllDocuments");
        await app.IdleAsync();
        JsonObject list = await ListAsync(app);
        Assert.Equal(3, list["rows"]!.AsArray().Count);
        Assert.Equal([Path.GetFileName(first), Path.GetFileName(second), Path.GetFileName(second)],
            list["rowDocuments"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal("Document", list["header"]![0]!.GetValue<string>());

        // 別のドキュメントのブックマークを選んで Enter: そのタブに切り替えて移動する。
        await app.SendAsync("bookmarksSelect", new JsonObject { ["indices"] = new JsonArray(0) });
        await InspectorTests.PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        Assert.Equal(0, (await app.StateAsync())["selectedIndex"]!.GetValue<int>());
        Assert.Equal(0x100, await CursorAsync(app));

        // オフに戻すと、今のドキュメントのものだけ。
        await app.CommandAsync("Bookmarks_AllDocuments");
        await app.IdleAsync();
        Assert.Single(await RowsAsync(app));
    });

    /// <summary>
    /// 複数ウィンドウ (UI-14): タブを別のウィンドウに移すとブックマークもついていき、「すべてのドキュメント」はすべてのウィンドウの
    /// ドキュメントを出す。別のウィンドウのブックマークを選ぶと、そのウィンドウのタブに移動する (INSP-26 の仕様 8)。
    /// </summary>
    [Fact]
    public Task All_documents_lists_every_window_and_bookmarks_move_with_the_tab() => UiTestContext.RunAsync(async ctx =>
    {
        string first = ctx.CopyTestData("TD-SEQ-1M");
        string second = ctx.CopyTestData("TD-BYTES-256");
        AppSession app = await StartAsync(ctx, file: first);
        await ToggleAsync(app, 0x100);
        await app.OpenAsync(second);
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(10), "the second tab");
        await ToggleAsync(app, 0x10);
        await ToggleAsync(app, 0x20);

        // 2 つ目のタブを新しいウィンドウに移す。ブックマークはそのまま。
        await app.SendAsync("execute", new JsonObject { ["id"] = "tab.moveToNewWindow" });
        await WindowManagementTests.WaitForWindowsAsync(app, 2);
        await app.IdleAsync();
        if (!(await ListAsync(app))["visible"]!.GetValue<bool>())
        {
            await app.CommandAsync("Command_ToggleBookmarks");
            await app.IdleAsync();
        }

        await app.WaitUntilAsync(async () => (await RowsAsync(app)).Count == 2, TimeSpan.FromSeconds(10), "the moved bookmarks");

        // 「すべてのドキュメント」: 両方のウィンドウのドキュメント。
        await app.CommandAsync("Bookmarks_AllDocuments");
        await app.IdleAsync();
        JsonObject list = await ListAsync(app);
        Assert.Equal([Path.GetFileName(first), Path.GetFileName(second), Path.GetFileName(second)],
            list["rowDocuments"]!.AsArray().Select(d => d!.GetValue<string>()));

        // 元のウィンドウのブックマークを選んで Enter: 元のウィンドウが操作の対象になり、そのタブで移動する。
        await app.SendAsync("bookmarksSelect", new JsonObject { ["indices"] = new JsonArray(0) });
        await InspectorTests.PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        Assert.Equal(0, (await app.SendAsync("windows"))["current"]!.GetValue<int>());
        Assert.Equal(0x100, await CursorAsync(app));
    });
}
