using System.Buffers.Binary;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 型付きブックマークと位置の式 (INSP-29)。編集のフライアウトの名前・位置の式は UI オートメーションの ValuePattern で入力する。
/// 再評価の待ち時間 (最後の編集から 200 ms) はアプリの中で測った記録で確かめる (テストからの往復の時間では測らない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class TypedBookmarkTests
{
    private static async Task<AppSession> StartAsync(UiTestContext ctx, string file)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        if (!(await app.SendAsync("bookmarks"))["visible"]!.GetValue<bool>())
        {
            await app.CommandAsync("Command_ToggleBookmarks");
            await app.IdleAsync();
        }

        return app;
    }

    private static async Task AddAsync(AppSession app, long offset, string name)
    {
        await app.GoToAsync(offset);
        await app.KeyAsync("F2", ctrl: true);
        await app.IdleAsync();
        await OpenEditorAsync(app, offset);
        await app.UiaSetValueAsync("Bookmark_Name", name);
        await app.IdleAsync();
    }

    private static async Task OpenEditorAsync(AppSession app, long start)
    {
        await app.SendAsync("bookmarkEdit", new JsonObject { ["start"] = start });
        await app.WaitForAsync("Bookmark_Expression");
    }

    private static async Task SetExpressionAsync(AppSession app, string expression)
    {
        await app.UiaSetValueAsync("Bookmark_Expression", expression);
        await app.IdleAsync();
    }

    private static async Task<JsonObject> StateAsync(AppSession app) => await app.SendAsync("bookmarksTyped");

    private static async Task<JsonObject> BookmarkAsync(AppSession app, string name) =>
        (await StateAsync(app))["bookmarks"]!.AsArray().Select(b => b!.AsObject()).FirstOrDefault(b => b["name"]!.GetValue<string>() == name)
            ?? throw new Xunit.Sdk.XunitException($"No bookmark {name}");

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-29-01")]
    public Task Position_expression_points_to_the_pe_header() => UiTestContext.RunAsync(async ctx =>
    {
        string pe = ctx.CopyTestData("TD-PE-X64");
        long lfanew = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(pe).AsSpan(0x3C));
        AppSession app = await StartAsync(ctx, pe);

        // 1: 0 に付けて、名前 pe、位置の式 u32le(0x3C)。
        await AddAsync(app, 0, "pe");
        await SetExpressionAsync(app, "u32le(0x3C)");

        // 2: 開始オフセットが e_lfanew で、「式」の印が付いている。
        await app.WaitUntilAsync(async () => (await BookmarkAsync(app, "pe"))["start"]!.GetValue<long>() == lfanew, TimeSpan.FromSeconds(10),
            "the bookmark to move to e_lfanew");
        JsonObject b = await BookmarkAsync(app, "pe");
        Assert.Equal("Expr", b["mark"]!.GetValue<string>());
        Assert.False(b["warning"]!.GetValue<bool>());

        // 3: Ctrl+G で bm.pe に移動し、カーソルから 4 バイトを読む。
        await app.KeyAsync("Escape");
        await ViewOps.GoToAsync(app, "bm.pe");
        Assert.Equal(lfanew, await ViewOps.CursorAsync(app));
        Assert.Equal([0x50, 0x45, 0x00, 0x00], await app.BytesAsync(lfanew, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-29-02")]
    public Task Editing_the_referenced_value_moves_the_bookmark_after_200_ms() => UiTestContext.RunAsync(async ctx =>
    {
        string pe = ctx.CopyTestData("TD-PE-X64");
        long lfanew = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(pe).AsSpan(0x3C));
        Assert.NotEqual(0x100, lfanew);
        AppSession app = await StartAsync(ctx, pe);
        await AddAsync(app, 0, "pe");
        await SetExpressionAsync(app, "u32le(0x3C)");
        await app.WaitUntilAsync(async () => (await BookmarkAsync(app, "pe"))["start"]!.GetValue<long>() == lfanew, TimeSpan.FromSeconds(10),
            "the bookmark to move to e_lfanew");
        await app.KeyAsync("Escape");

        // 1: 0x3C〜0x3F を 00 01 00 00 に上書きする。
        await app.GoToAsync(0x3C);
        await app.FocusAsync("editor");
        await app.TypeAsync("00010000");
        Assert.Equal([0x00, 0x01, 0x00, 0x00], await app.BytesAsync(0x3C, 4));

        // 2・3: 再評価は最後の編集から 200 ms 以上たってから行い、位置は 0x100 になる (時間はアプリの中で測る)。
        await app.WaitUntilAsync(async () => (await BookmarkAsync(app, "pe"))["start"]!.GetValue<long>() == 0x100, TimeSpan.FromSeconds(10),
            "the bookmark to move to 0x100");
        JsonObject state = await StateAsync(app);
        Assert.True(state["runAfterEdit"]!.GetValue<bool>());
        double waited = state["lastEditToRunMs"]!.GetValue<double>();
        Assert.True(waited >= 195, $"re-evaluated {waited:F0} ms after the last edit");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-29-03")]
    public Task Uint32_bookmark_shows_and_edits_its_value() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, ctx.CopyTestData("TD-INSP-VALUES"));
        await AddAsync(app, 0x10, "value");

        // 1・2: 型 uint32 → 長さ 4、値 67,305,985 (01 02 03 04 の LE)。値の列が表示される。
        await app.SendAsync("bookmarkEditorType", new JsonObject { ["type"] = "uint32" });
        await app.IdleAsync();
        JsonObject b = await BookmarkAsync(app, "value");
        Assert.Equal(4, b["length"]!.GetValue<long>());
        Assert.Equal("67,305,985", b["value"]!.GetValue<string>());
        Assert.Contains("Value", (await StateAsync(app))["header"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Contains("67,305,985", b["toolTip"]!.GetValue<string>());
        await app.KeyAsync("Escape");

        // 3・4: 一覧の値の列に 0x11223344 を入れて Enter → 44 33 22 11、値 287,454,020。
        JsonObject edit = await app.SendAsync("bookmarkValueEdit", new JsonObject { ["name"] = "value", ["text"] = "0x11223344" });
        Assert.True(edit["committed"]!.GetValue<bool>(), edit.ToJsonString());
        await app.IdleAsync();
        Assert.Equal([0x44, 0x33, 0x22, 0x11], await app.BytesAsync(0x10, 4));
        Assert.Equal("287,454,020", (await BookmarkAsync(app, "value"))["value"]!.GetValue<string>());

        // 書き換えは 1 回の Undo で戻る (INSP-17 の規則)。
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal([0x01, 0x02, 0x03, 0x04], await app.BytesAsync(0x10, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-29-04")]
    public Task Circular_references_are_errors_on_both_bookmarks() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, ctx.CopyTestData("TD-SEQ-1M"));
        await AddAsync(app, 0x100, "a");
        await app.KeyAsync("Escape");
        await AddAsync(app, 0x200, "b");
        await app.KeyAsync("Escape");

        // 1: a = bm.b + 4 → 0x204。
        await OpenEditorAsync(app, 0x100);
        await SetExpressionAsync(app, "bm.b + 4");
        await app.WaitUntilAsync(async () => (await BookmarkAsync(app, "a"))["start"]!.GetValue<long>() == 0x204, TimeSpan.FromSeconds(10), "a at 0x204");
        await app.KeyAsync("Escape");

        // 2: b = bm.a + 4 → 循環。
        await OpenEditorAsync(app, 0x200);
        await SetExpressionAsync(app, "bm.a + 4");

        // 3: 両方に警告の印と循環参照の理由。位置は最後に評価できた値のまま。
        await app.WaitUntilAsync(async () => (await BookmarkAsync(app, "b"))["warning"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the warning on b");
        foreach ((string name, long start) in new[] { ("a", 0x204L), ("b", 0x200L) })
        {
            JsonObject bm = await BookmarkAsync(app, name);
            Assert.True(bm["warning"]!.GetValue<bool>(), name);
            Assert.Equal("Cycle", bm["errorKind"]!.GetValue<string>());
            Assert.Contains("Circular", bm["reason"]!.GetValue<string>());
            Assert.Equal(start, bm["start"]!.GetValue<long>());
        }
    });
}
