using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.SearchResultsTests;

namespace HexEditor.UITests;

/// <summary>
/// 検索と編集の残りのフェーズ 1 の項目: 読み込みエラーの確認 (FIND-01 の「エラー」)、ステータスバーの結果 (FIND-02 の仕様 5、FIND-09 の
/// 仕様 4)、古い結果の再検索 (FIND-03)、一致の強調の設定 (FIND-04 の仕様 9)、文字コードの一覧 (FIND-07 の仕様 1)、選択範囲の枠
/// (FIND-11 の仕様 3)、入力が止まったときの件数 (FIND-12 の仕様 1)、結果のタブと固定 (FIND-20 の仕様 3)、ブックマークへの変換
/// (FIND-21 の仕様 1・3)、検索のコマンド (FIND-21、FIND-23)、範囲を選択の入力履歴 (EDIT-04 の仕様 10)、形式ごとのコピー
/// (EDIT-25)、塗りつぶしの文字コード (EDIT-29)、編集の設定 (EDIT-10 の仕様 1)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SearchAndEditSettingsTests
{
    /// <summary>0x4000 バイトの 0。[0x1000, 0x1200) が読めない。</summary>
    private static JsonObject BadRange(string name) => new()
    {
        ["fileSources"] = new JsonArray(new JsonObject
        {
            ["match"] = name,
            ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x1000", ["length"] = 0x200 }),
        }),
    };

    private static Task<JsonObject> FindBarAsync(AppSession app) => app.SendAsync("findBar");

    private static async Task<string> FindStatusAsync(AppSession app) =>
        (await app.ElementAsync("Find_Status"))["text"]?.GetValue<string>() ?? string.Empty;

    private static Task SetSettingAsync(AppSession app, string key, JsonNode value) =>
        app.SendAsync("settingSet", new JsonObject { ["key"] = key, ["value"] = value });

    [Fact]
    public Task Unreadable_data_asks_and_can_stop_the_search() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.WriteFile("bad.bin", new byte[0x4000]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = BadRange("bad.bin") });

        // 見つからない語を探すと、読めない範囲で InfoBar が出る。「検索を中止する」で中止する。
        await OpenFindAsync(app, 0, "AB CD EF");
        _ = app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitForNotificationAsync(m => m.Contains("Can't read 512 bytes at", StringComparison.Ordinal), "the read error notice");
        await app.SendAsync("noticeAction", new JsonObject { ["label"] = "Stop searching" });
        await app.WaitUntilAsync(async () => (await FindStatusAsync(app)).Contains("Search stopped", StringComparison.Ordinal),
            TimeSpan.FromSeconds(15), "the search to stop");
    });

    [Fact]
    public Task Skipped_ranges_are_listed_with_the_results() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.WriteFile("bad.bin", new byte[0x4000]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = BadRange("bad.bin") });

        // 設定「飛ばす」では尋ねずに続け、読めなかった範囲を結果一覧に記録する。
        await SetSettingAsync(app, "search.readErrors", "skip");
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        JsonObject r = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        JsonArray skipped = r["skipped"]!.AsArray();
        Assert.Single(skipped);
        Assert.Equal((0x1000L, 0x200L), (skipped[0]!["offset"]!.GetValue<long>(), skipped[0]!["length"]!.GetValue<long>()));
        Assert.True(await app.IsShownAsync("SearchResults_Skipped"));
        Assert.DoesNotContain(await ViewOps.NoticesAsync(app), m => m.Contains("Can't read", StringComparison.Ordinal));
    });

    [Fact]
    public Task Results_are_shown_on_the_status_bar() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });

        // 末尾近くから次を検索: 折り返しは検索バーとステータスバーの両方に出す (FIND-09 の仕様 4)。
        await app.GoToAsync(0xFF0);
        await OpenFindAsync(app, 0, "AB CD");
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
        Assert.Contains("continued from the start", (await FindBarAsync(app))["statusMessage"]!.GetValue<string>(), StringComparison.Ordinal);

        // 検索バーを閉じて F3: 結果 (見つかった位置) をステータスバーに出す (FIND-02 の仕様 5)。
        await app.UiaInvokeAsync("Find_Close");
        await app.IdleAsync();
        await app.KeyAsync("F3");
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => (await FindBarAsync(app))["statusMessage"]!.GetValue<string>().Contains("0x20", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the result on the status bar");
    });

    [Fact]
    public Task Match_highlighting_can_be_turned_off() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });
        await OpenFindAsync(app, 0, "AB CD");
        await app.WaitUntilAsync(async () => MatchedAt(await app.RenderAsync(), 0x20), TimeSpan.FromSeconds(10), "the highlight");

        await SetSettingAsync(app, "search.highlightMatches", false);
        await app.WaitUntilAsync(async () => !MatchedAt(await app.RenderAsync(), 0x20), TimeSpan.FromSeconds(10), "the highlight to disappear");
        await SetSettingAsync(app, "search.highlightMatches", true);
        await app.WaitUntilAsync(async () => MatchedAt(await app.RenderAsync(), 0x20), TimeSpan.FromSeconds(10), "the highlight to return");
    });

    [Fact]
    public Task Find_bar_offers_every_display_encoding() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[0x100];
        System.Text.Encoding.Unicode.GetBytes("AB").CopyTo(data, 0x40);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("u16.bin", data)] });

        // 一覧の先頭は「表示中の文字コードに合わせる」、その後に表示の文字コードの一覧 (EBCDIC・Windows のコードページなど)。
        await OpenFindAsync(app, 1, string.Empty);
        string[] encodings = [.. (await FindBarAsync(app))["encodings"]!.AsArray().Select(n => n!.GetValue<string>())];
        Assert.Equal("display", encodings[0]);
        Assert.Contains("cp932", encodings);
        Assert.Contains("cp37", encodings);
        Assert.Contains("cp1252", encodings);
        Assert.Contains("utf-32be", encodings);
        Assert.DoesNotContain("cp50220", encodings); // 状態を持つ文字コードは出さない

        // 表示の文字コードを UTF-16 LE にすると、既定の「表示中の文字コードに合わせる」で UTF-16 LE として探す。
        // 検索語を入れた後で表示の文字コードを変えても、変えた文字コードで探す。
        await app.UiaSetValueAsync("Find_Query", "AB");
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.encoding.select", ["argument"] = "utf-16le" });
        await app.IdleAsync();
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((0x40L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));
    });

    [Fact]
    public Task Selection_scope_is_outlined() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });

        // 257 バイト以上を選んで Ctrl+F: 範囲が「選択範囲」になり、エディタにその範囲の枠が出る。
        await app.SelectAsync(0x10, 0x200);
        await OpenFindAsync(app, 0, "AB CD");
        JsonArray outline = (await FindBarAsync(app))["scopeOutline"]!.AsArray();
        Assert.Equal((0x10L, 0x200L), (outline[0]!["offset"]!.GetValue<long>(), outline[0]!["length"]!.GetValue<long>()));
        await app.WaitUntilAsync(async () => (await app.SendAsync("highlights"))["segments"]!.AsArray().Any(s => s!["tag"]!.GetValue<string>() == "searchScope"),
            TimeSpan.FromSeconds(10), "the scope outline");

        // 閉じると枠が消える。
        await app.UiaInvokeAsync("Find_Close");
        await app.WaitUntilAsync(async () => !(await app.SendAsync("highlights"))["segments"]!.AsArray().Any(s => s!["tag"]!.GetValue<string>() == "searchScope"),
            TimeSpan.FromSeconds(10), "the outline to disappear");
    });

    [Fact]
    public Task Incremental_input_counts_the_matches() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });

        // Enter を押さずに入力が止まるだけで「1 / 100」と件数が出る (FIND-12 の仕様 1)。
        await OpenFindAsync(app, 0, "AB CD");
        await app.WaitUntilAsync(async () => ((await app.ElementAsync("Find_Count"))["text"]?.GetValue<string>() ?? string.Empty).Contains("100", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the count");
    });

    [Fact]
    public Task Results_can_be_kept_in_pinned_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });
        static bool Done(JsonObject r) => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>();

        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, Done, "the first results");

        // 既定は置き換え。固定したタブは置き換えず、新しいタブを作る。
        JsonObject pinned = await app.SendAsync("searchResultsTab", new JsonObject { ["pin"] = true });
        Assert.True(pinned["pinned"]!.GetValue<bool>());
        await app.UiaSetValueAsync("Find_Query", "AB");
        await FindAllAsync(app);
        JsonObject second = await WaitForResultsAsync(app, r => Done(r) && r["tabs"]!.AsArray().Count == 2, "a second tab");
        Assert.Equal(1, second["activeTab"]!.GetValue<int>());
        Assert.Equal(100, second["count"]!.GetValue<long>());

        // 固定していないタブは置き換える (タブは 2 つのまま)。
        await app.UiaSetValueAsync("Find_Query", "CD");
        await FindAllAsync(app);
        JsonObject replaced = await WaitForResultsAsync(app, r => Done(r) && r["summary"]!.GetValue<string>().StartsWith("Hex: CD", StringComparison.Ordinal), "the replaced tab");
        Assert.Equal(2, replaced["tabs"]!.AsArray().Count);

        // 固定したタブに切り替えると、その結果が出る。閉じると残りのタブになる。
        JsonObject first = await app.SendAsync("searchResultsTab", new JsonObject { ["select"] = 0 });
        Assert.StartsWith("Hex: AB CD", first["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject closed = await app.SendAsync("searchResultsTab", new JsonObject { ["close"] = 0 });
        Assert.Single(closed["tabs"]!.AsArray());
        Assert.StartsWith("Hex: CD", closed["summary"]!.GetValue<string>(), StringComparison.Ordinal);

        // 設定「新しいタブに出す」では、毎回タブを作る。
        await SetSettingAsync(app, "search.results.newTab", true);
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => Done(r) && r["tabs"]!.AsArray().Count == 2, "a new tab");
    });

    [Fact]
    public Task Outdated_results_can_be_searched_again() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.WriteFile("hits.bin", Hits100());
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");

        // 外部で 1 件増やす (未編集のドキュメントは自動で再読み込みされる)。結果は古い結果になり、再検索ボタンが出る。
        byte[] changed = Hits100();
        changed[0xFF0] = 0xAB;
        changed[0xFF1] = 0xCD;
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, changed);
        File.Replace(temp, path, null);
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0xFF0, 1))[0] == 0xAB, TimeSpan.FromSeconds(20), "the reload");
        JsonObject stale = await WaitForResultsAsync(app, r => r["stale"]!.GetValue<bool>(), "the outdated results");
        Assert.Contains("outdated", stale["summary"]!.GetValue<string>(), StringComparison.Ordinal);

        // 再検索で、今の内容の結果 (101 件) になる。
        await app.UiaInvokeAsync("SearchResults_Research");
        JsonObject fresh = await WaitForResultsAsync(app, r => !r["stale"]!.GetValue<bool>() && r["state"]?.GetValue<string>() == "Completed"
            && !r["running"]!.GetValue<bool>(), "the new results");
        Assert.Equal(101, fresh["count"]!.GetValue<long>());
    });

    [Fact]
    public Task Many_results_ask_before_becoming_bookmarks() => UiTestContext.RunAsync(async ctx =>
    {
        // `AB` が 10,240 件。
        byte[] data = new byte[0x5000];
        Array.Fill(data, (byte)0xAB);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("many.bin", data)] });
        await OpenFindAsync(app, 0, "AB");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");

        // 10,000 件を超えるので確認ダイアログが出る。「やめる」ではブックマークを作らない。
        await app.SendAsync("searchResultsToBookmarks", new JsonObject { ["noWait"] = true });
        await app.WaitForAsync("BookmarksConfirm");

        // ダイアログのボタンは開いた直後にはまだないことがあるため、押せるまで試す。
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(10), "the dialog buttons");
        await app.IdleAsync();
        Assert.Equal(0, (await app.SendAsync("bookmarks"))["count"]!.GetValue<int>());

        // 対象を選んだ行 (2 行) にすると確認しない。
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 0 });
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 1, ["shift"] = true });
        await app.SendAsync("searchResultsToBookmarks", new JsonObject { ["target"] = "selected" });
        await app.IdleAsync();
        Assert.Equal(2, (await app.SendAsync("bookmarks"))["count"]!.GetValue<int>());
    });

    [Fact]
    public Task Search_commands_replace_and_export() => UiTestContext.RunAsync(async ctx =>
    {
        string export = Path.Combine(ctx.Root, "results.csv");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.WriteFile("hits.bin", Hits100())],
            Hooks = new JsonObject { ["savePicker"] = export },
        });

        // 「検索結果をエクスポート」: 保存のダイアログで選んだ形式 (拡張子) で書き出す。
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        Assert.True((await app.SendAsync("execute", new JsonObject { ["id"] = "search.results.export" }))["executed"]!.GetValue<bool>());
        await app.IdleAsync();
        Assert.Equal(101, File.ReadAllLines(export).Length);

        // 「すべて置換」: 置換欄の置換語ですべて置換する。
        await OpenFindAsync(app, 0, "AB CD", replace: true);
        await app.UiaSetValueAsync("Find_Replace", "11 22");
        await app.SendAsync("execute", new JsonObject { ["id"] = "search.replaceAll" });
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0x11, 0x22 }, await app.BytesAsync(0x20, 2));
    });

    [Fact]
    public Task Select_range_fields_remember_recent_entries() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });
        await EditCommandTests.SelectRangeAsync(app, "0x100", "0x20");
        await EditCommandTests.SelectRangeAsync(app, "0x200", "8");

        // 開き直すと、各欄に直近の入力が新しい順に候補として出る。計算で求めた欄 (終了) には残さない。
        await app.KeyAsync("E", ctrl: true);
        await app.WaitForAsync("SelectRangeDialog");
        Assert.Equal(["0x200", "0x100"], (await app.SendAsync("fieldHistory", new JsonObject { ["id"] = "SelectRange_Start" }))["items"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["8", "0x20"], (await app.SendAsync("fieldHistory", new JsonObject { ["id"] = "SelectRange_Length" }))["items"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty((await app.SendAsync("fieldHistory", new JsonObject { ["id"] = "SelectRange_End" }))["items"]!.AsArray());
        Assert.True((await app.ElementAsync("SelectRange_Start_History"))["found"]!.GetValue<bool>());
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
    });

    [Fact]
    public Task Copy_as_a_format_from_the_palette_and_the_context_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("hits.bin", Hits100())] });
        await app.SelectAsync(0, 2);

        // 形式ごとのコマンド (コマンドパレット「形式を選択してコピー: 配列 (C)」)。ダイアログを開かずにコピーし、前回の形式になる。
        JsonObject commands = await app.SendAsync("commands");
        Assert.Contains(commands["items"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "edit.copyAs.arrayC"
            && c["title"]!.GetValue<string>() == "Edit: Copy as: Array (C)");
        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.copyAs.arrayC" });
        await app.IdleAsync();
        Assert.Equal("unsigned char data[2] = { 0xAB, 0xCD };", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>().Trim());

        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.copyAs.base64" });
        await app.IdleAsync();
        Assert.Equal("q80=", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>().Trim());
        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.copyAsLast" });
        await app.IdleAsync();
        Assert.Equal("q80=", (await app.SendAsync("clipboard"))["text"]!.GetValue<string>().Trim());

        // 右クリックメニューに「形式を選択してコピー」のサブメニュー (よく使う形式) がある。
        JsonObject render = await app.RenderAsync();
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(render, 0));
        Assert.True((await app.WaitForAsync("HexViewMenu_CopyAsMenu")).IsEnabled);
        await app.SendAsync("hideContextMenu");
    });

    [Fact]
    public Task Fill_text_can_use_another_encoding() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("zero.bin", new byte[0x40])] });
        await app.SelectAsync(0, 8);
        await app.SendAsync("execute", new JsonObject { ["id"] = "edit.fill" });
        await app.WaitForAsync("FillDialog");
        await EditCommandTests.SelectItemAsync(app, "Fill_Kind", "Text");
        await EditCommandTests.SelectItemAsync(app, "Fill_TextEncoding", "UTF-16 LE");
        await app.UiaSetValueAsync("Fill_Text", "AB");
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app);
        Assert.Equal(new byte[] { 0x41, 0, 0x42, 0, 0x41, 0, 0x42, 0 }, await app.BytesAsync(0, 8));
    });

    [Fact]
    public Task New_documents_use_the_default_input_mode() => UiTestContext.RunAsync(async ctx =>
    {
        string second = ctx.WriteFile("b.bin", new byte[0x10]);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("a.bin", new byte[0x10])] });
        Assert.False((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());

        // 設定「新しく開いたファイルの入力モード」を挿入にすると、後から開いたファイルは挿入モードで開く (EDIT-10 の仕様 1)。
        await SetSettingAsync(app, "edit.defaultInputMode", "insert");
        await app.OpenAsync(second);
        await app.IdleAsync();
        Assert.True((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());
    });
}
