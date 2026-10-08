using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>すべて検索と結果一覧 (FIND-20、FIND-21)、編集の後の結果の位置 (FIND-03)、数値の検索の結果 (FIND-13)、検索範囲 (FIND-11)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SearchResultsTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-FIND-20-01")]
    public Task Alt_enter_lists_all_matches() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-100.bin", Hits100())] });

        // 1. 種類「Hex」に `AB CD` を入れて Alt+Enter。
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        JsonObject results = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");

        // 2. 見出しは「Hex: AB CD — 100 matches (completed)」。100 行、オフセットは 0x0、0x20、…、0xC60、長さ 2、データ `AB CD`。
        Assert.Equal("Hex: AB CD — 100 matches (completed)", results["summary"]!.GetValue<string>());
        JsonArray rows = (await ResultsAsync(app, 0, 100))["rows"]!.AsArray();
        Assert.Equal(100, rows.Count);
        for (int k = 0; k < 100; k++)
        {
            Assert.Equal(k + 1, rows[k]!["number"]!.GetValue<long>());
            Assert.Equal(k * 0x20, rows[k]!["offset"]!.GetValue<long>());
            Assert.Equal(2, rows[k]!["length"]!.GetValue<long>());
            Assert.Equal("AB CD", rows[k]!["hex"]!.GetValue<string>());
        }

        // 3. 見えている一致が強調表示されている (検索バーを閉じても一覧の強調が残る)。
        await app.UiaInvokeAsync("Find_Close");
        Assert.True(MatchedAt(await app.RenderAsync(), 0x20));

        // 4. 一覧を閉じると強調表示が消える。
        await app.UiaInvokeAsync("SearchResults_Close");
        await app.WaitUntilAsync(async () => !MatchedAt(await app.RenderAsync(), 0x20), TimeSpan.FromSeconds(10), "the highlights to disappear");
        Assert.False((await ResultsAsync(app))["visible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-20-02")]
    public Task Jump_to_a_result_while_searching_ten_gigabytes() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-EDIT-SPARSE-10G の代わりに、目印を 1 GiB ごとに置いた 10 GiB の TD-ENG-SPARSE-10G を遅いデータソースで開く。
        string path = ctx.TestData("TD-ENG-SPARSE-10G");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Slow(path, 20) });

        // 1. テキスト (ASCII) で `@00000` をすべて検索する。
        await OpenFindAsync(app, 1, "@00000", encoding: "ASCII");
        await FindAllAsync(app);

        // 2. 2 件以上出て、まだ検索中の間に 2 行目をクリックする。
        JsonObject running = await WaitForResultsAsync(app, r => r["count"]!.GetValue<long>() >= 2, "two results");
        Assert.True(running["running"]!.GetValue<bool>(), "the search should still be running");
        long second = (await ResultsAsync(app, 1, 1))["rows"]![0]!["offset"]!.GetValue<long>();
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 1 });

        // 3. その行のオフセットから 6 バイトが選択され、検索は続いている。
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(second, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(6, doc["selectionLength"]!.GetValue<long>());
        Assert.True((await ResultsAsync(app))["running"]!.GetValue<bool>());
        await app.UiaInvokeAsync("SearchResults_Cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-20-03")]
    public Task Stops_at_the_limit_and_continues() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1500K.bin", Hits1500K())] });

        // 1〜2. 上限 (1,000,000 件) で止まり、「続ける」が出る。
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        JsonObject stopped = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "LimitReached" && !r["running"]!.GetValue<bool>(), "the limit", 120);
        Assert.Contains("stopped at the limit of 1,000,000 matches", stopped["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1_000_000, stopped["count"]!.GetValue<long>());
        Assert.True(stopped["canContinue"]!.GetValue<bool>());

        // 3. 一覧の順に 0、4、…、3,999,996。
        Assert.True(await OffsetsAsync(app, 1_000_000) is var first && first.SequenceEqual(Enumerable.Range(0, 1_000_000).Select(k => 4L * k)));

        // 4〜5. 「続ける」で 1,500,000 件。0、4、…、5,999,996 で重複も抜けもない。
        await app.UiaInvokeAsync("SearchResults_Continue");
        JsonObject done = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "completion", 120);
        Assert.Equal(1_500_000, done["count"]!.GetValue<long>());
        Assert.True((await OffsetsAsync(app, 1_500_000)).SequenceEqual(Enumerable.Range(0, 1_500_000).Select(k => 4L * k)));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-20-05")]
    public Task Enter_on_a_row_moves_to_the_match() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-100.bin", Hits100())] });
        await OpenFindAsync(app, 0, "AB CD");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");
        await app.UiaInvokeAsync("Find_Close");
        await app.GoToAsync(0x800);

        // 1. 一覧の 1 行目を選び、↓ を 5 回押す (6 行目)。
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 0, ["shift"] = true });
        for (int i = 0; i < 5; i++)
        {
            await app.SendAsync("searchResultsKey", new JsonObject { ["key"] = "Down" });
        }

        // 2. エディタは 6 行目の一致 (0xA0) が見える位置にスクロールしている。カーソルは動かない (プレビュー)。
        JsonObject doc = await app.DocumentAsync();
        long top = doc["topRow"]!.GetValue<long>() * 16;
        Assert.InRange(0xA0, top, top + (doc["visibleRows"]!.GetValue<long>() * 16));
        Assert.Equal(0, doc["selectionLength"]!.GetValue<long>());

        // 3〜4. Enter: 0xA0〜0xA1 が選択され、カーソルは 0xA0。
        await app.SendAsync("searchResultsKey", new JsonObject { ["key"] = "Enter" });
        doc = await app.DocumentAsync();
        Assert.Equal((0xA0L, 2L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));
        Assert.Equal(0xA0, doc["cursor"]!.GetValue<long>());

        // 5. F3: 一覧の次の結果 (0xC0)。
        await app.KeyAsync("F3");
        await app.IdleAsync();
        doc = await app.DocumentAsync();
        Assert.Equal((0xC0L, 2L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-02-04")]
    public Task Cancelling_find_all_keeps_the_results() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.TestData("TD-MARKERS-1G");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = Slow(path, 100) });

        // 1. テキスト (ASCII) `@000000` で Alt+Enter。
        await OpenFindAsync(app, 1, "@000000", encoding: "ASCII");
        await FindAllAsync(app);

        // 2. 100 件以上になったら検索バーのキャンセルボタンを押す。
        await WaitForResultsAsync(app, r => r["count"]!.GetValue<long>() >= 100, "100 results", 60);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["findBarProgress"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the cancel button");
        await app.UiaInvokeAsync("Find_Cancel");

        // 3. 見出しに「中断」。件数は 100 以上 1,025 未満で、各行の位置に目印がある。
        JsonObject results = await WaitForResultsAsync(app, r => !r["running"]!.GetValue<bool>(), "the cancellation");
        Assert.Equal("Cancelled", results["state"]!.GetValue<string>());
        Assert.Contains("(interrupted)", results["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        long count = results["count"]!.GetValue<long>();
        Assert.InRange(count, 100, 1024);
        // 目印の位置 (1 MiB ごとと末尾) は分かっているので、各行のオフセットが目印の位置かを確かめ、先頭と最後の行は実際に読む
        // (遅いデータソースのため、すべての行は読まない)。
        long[] offsets = await OffsetsAsync(app, count);
        long gib = TestDataCatalog.GiB;
        Assert.All(offsets, o => Assert.True(o % TestDataCatalog.MiB == 0 || o == gib - TestDataCatalog.MarkerLength, $"0x{o:X}"));
        Assert.Equal(offsets.Order(), offsets);
        foreach (long offset in new[] { offsets[0], offsets[^1] })
        {
            Assert.Equal(TestDataCatalog.Marker(offset), await app.BytesAsync(offset, TestDataCatalog.MarkerLength));
        }

        // 4. 最後の行をクリックすると、その一致に移動して選択される。
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = count - 1 });
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(offsets[^1], doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(7, doc["selectionLength"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-03-02")]
    public Task Inserting_after_the_search_shifts_the_jump_target() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });

        // 1. `12 34 56 78` ですべて検索する。
        await OpenFindAsync(app, 0, "12 34 56 78");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");

        // 2. 位置 0 に 100 バイトを挿入する (編集 > 挿入… と同じ 1 回の編集)。
        await app.SendAsync("insertBytes", new JsonObject { ["offset"] = 0, ["length"] = 100 });

        // 3〜4. 3 行目 (元の 0x800) をクリック: 開始 0x864、長さ 4。
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 2 });
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((0x864L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 5. Ctrl+Z の後に同じ行: 開始 0x800、長さ 4。
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 2 });
        doc = await app.DocumentAsync();
        Assert.Equal((0x800L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-03-03")]
    public Task Editing_a_match_marks_it_and_undo_clears_the_mark() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });
        await OpenFindAsync(app, 0, "12 34 56 78");
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");
        await app.UiaInvokeAsync("Find_Close");

        // 1〜2. 0x401 に FF を入力する (上書きモード): 2 行目は「変更あり」(文字とアイコン)。
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x401 });
        await app.FocusAsync("editor", keyboard: false);
        await app.TypeAsync("FF");
        await app.WaitUntilAsync(async () => await StatusAsync(app, 1) == "Modified", TimeSpan.FromSeconds(10), "the modified mark");
        Assert.Equal(" Modified", (await ResultsAsync(app, 1, 1))["rows"]![0]!["status"]!.GetValue<string>());

        // 3〜4. 0x800 から 4 バイトを選択して Delete: 3 行目は「削除済み」。クリックすると削除された位置 0x800 に移動する。
        await app.SelectAsync(0x800, 4);
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Delete");
        await app.WaitUntilAsync(async () => await StatusAsync(app, 2) == "Deleted", TimeSpan.FromSeconds(10), "the deleted mark");
        await app.SendAsync("searchResultsClick", new JsonObject { ["index"] = 2 });
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x800, doc["cursor"]!.GetValue<long>());

        // 5〜6. Ctrl+Z を 2 回: どちらも印がない。
        await app.FocusAsync("editor", keyboard: false);
        await app.KeyAsync("Z", ctrl: true);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await app.WaitUntilAsync(async () => await StatusAsync(app, 1) == "Unchanged" && await StatusAsync(app, 2) == "Unchanged",
            TimeSpan.FromSeconds(10), "the marks to disappear");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-13-02")]
    public Task Both_endians_are_shown_in_the_results() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[1024 * 1024];
        data[0x100] = 0x34;
        data[0x101] = 0x12;
        data[0x200] = 0x12;
        data[0x201] = 0x34;
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("zero-1234.bin", data)] });

        // 1. 種類「整数」、16 bit、エンディアン「両方」で `0x1234`。
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 2 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_IntBits", ["text"] = "16 bit" });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Endian", ["index"] = 2 });
        await app.UiaSetValueAsync("Find_Query", "0x1234");

        // 2. 変換結果に両方のバイト列。
        string preview = await app.UiaNameAsync("Find_Status");
        Assert.Contains("34 12 (LE)", preview, StringComparison.Ordinal);
        Assert.Contains("12 34 (BE)", preview, StringComparison.Ordinal);

        // 3. 0x100 (LE) と 0x200 (BE) の 2 件。値はどちらも 4,660 (0x1234)。
        await FindAllAsync(app);
        await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");
        JsonArray rows = (await ResultsAsync(app, 0, 10))["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal((0x100L, "LE"), (rows[0]!["offset"]!.GetValue<long>(), rows[0]!["endian"]!.GetValue<string>()));
        Assert.Equal((0x200L, "BE"), (rows[1]!["offset"]!.GetValue<long>(), rows[1]!["endian"]!.GetValue<string>()));
        Assert.All(rows, r => Assert.Equal("4,660 (0x1234)", r!["value"]!.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-11-04")]
    public Task Offset_range_with_expressions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });

        // 1〜2. 範囲「オフセット範囲」、開始 `0x100`、終了 `end-0x10`: 256 (0x100) 〜 1,048,560 (0xFFFF0)。
        await OpenFindAsync(app, 0, "12 34 56 78");
        await app.CommandAsync("Find_Options");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Scope", ["index"] = 2 });
        await app.UiaSetValueAsync("Find_RangeStart", "0x100");
        await app.UiaSetValueAsync("Find_RangeEnd", "end-0x10");
        Assert.Equal("256 (0x100) to 1,048,560 (0xFFFF0)", await app.UiaNameAsync("Find_RangeInfo"));

        // 3. 999 件。最初は 0x400、最後は 0xF9C00。
        await FindAllAsync(app);
        JsonObject results = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed", "the results");
        Assert.Equal(999, results["count"]!.GetValue<long>());
        long[] offsets = await OffsetsAsync(app, 999);
        Assert.Equal((0x400L, 0xF9C00L), (offsets[0], offsets[^1]));

        // 4. 開始が終了より後: 赤枠で検索ボタンは無効。
        await app.UiaSetValueAsync("Find_RangeStart", "0x2000");
        await app.UiaSetValueAsync("Find_RangeEnd", "0x1000");
        Assert.True((await app.ElementAsync("Find_RangeStart"))["errorBorder"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("Find_Next")).IsEnabled);

        // 5. 開始が末尾の外 (`end+1`): 赤枠で検索ボタンは無効。
        await app.UiaSetValueAsync("Find_RangeStart", "end+1");
        Assert.True((await app.ElementAsync("Find_RangeStart"))["errorBorder"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("Find_Next")).IsEnabled);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-11-03")]
    public Task Searching_all_open_documents() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-ZERO-1M、TD-FIND-MARK-1M、TD-FF-1M をこの順にタブで開き、TD-ZERO-1M のタブがアクティブ。カーソルは 0。
        string mark = ctx.WriteFile("TD-FIND-MARK-1M.bin", Mark1M());
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ZERO-1M"), mark, ctx.TestData("TD-FF-1M")] });
        await app.WaitForTabsAsync(3);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await app.GoToAsync(0);

        // 1. Hex `CA FE BA BE`、範囲「開いているすべてのドキュメント」。
        await OpenFindAsync(app, 0, "CA FE BA BE");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Scope", ["index"] = 3 });

        // 2〜3. Enter: TD-FIND-MARK-1M のタブに切り替わり、選択は 0x80000〜0x80003。
        await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(10), "the second tab");
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal("TD-FIND-MARK-1M.bin", doc["name"]!.GetValue<string>());
        Assert.Equal((0x80000L, 4L), (doc["selectionStart"]!.GetValue<long>(), doc["selectionLength"]!.GetValue<long>()));

        // 4. Alt+Enter: TD-FIND-MARK-1M のまとまりに 1 件。ドキュメント列にファイル名がある。
        await FindAllAsync(app);
        JsonObject results = await WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
        Assert.Equal(1, results["count"]!.GetValue<long>());
        JsonObject row = (await ResultsAsync(app, 0, 1))["rows"]![0]!.AsObject();
        Assert.Equal("TD-FIND-MARK-1M.bin", row["document"]!.GetValue<string>());
        Assert.Equal(0x80000, row["offset"]!.GetValue<long>());
    });

    [Fact(Skip = "ブックマーク (INSP-23) は別の作業で実装中のため、この版にはブックマーク一覧がない。結果一覧の「変換 > ブックマークに」は "
        + "SearchResultsPanel.BookmarksRequested を出す (名前「検索: <検索語> #番号」、グループ「検索結果 <日時>」) ので、ブックマークの部品をつないだ後に有効にする")]
    [Trait(UiTest.TC, "TC-FIND-21-02")]
    public Task Results_become_bookmarks() => Task.CompletedTask;

    // ---- 補助 ----

    /// <summary>検索バーを開き、種類と検索語を入れる。</summary>
    internal static async Task OpenFindAsync(AppSession app, int kind, string query, bool replace = false, string? encoding = null)
    {
        await app.KeyAsync(replace ? "H" : "F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = kind });
        if (encoding is not null)
        {
            await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = encoding });
        }

        await app.UiaSetValueAsync("Find_Query", query);
    }

    /// <summary>検索欄で Alt+Enter (すべて検索)。</summary>
    internal static Task FindAllAsync(AppSession app) => app.SendAsync("findKey", new JsonObject { ["key"] = "Enter", ["alt"] = true });

    internal static Task<JsonObject> ResultsAsync(AppSession app, long? from = null, int? count = null)
    {
        var request = new JsonObject();
        if (from is { } f)
        {
            request["from"] = f;
        }

        if (count is { } c)
        {
            request["count"] = c;
        }

        return app.SendAsync("searchResults", request);
    }

    internal static async Task<JsonObject> WaitForResultsAsync(AppSession app, Func<JsonObject, bool> condition, string what, int seconds = 30)
    {
        JsonObject? last = null;
        await app.WaitUntilAsync(async () => condition(last = await ResultsAsync(app)), TimeSpan.FromSeconds(seconds), what);
        return last!;
    }

    /// <summary>結果の開始オフセットを一覧の順にすべて読む。</summary>
    private static async Task<long[]> OffsetsAsync(AppSession app, long total)
    {
        var all = new List<long>((int)total);
        const int Batch = 200_000;
        for (long from = 0; from < total; from += Batch)
        {
            JsonObject r = await app.SendAsync("searchResults", new JsonObject { ["from"] = from, ["count"] = Batch, ["offsetsOnly"] = true }, TimeSpan.FromSeconds(60));
            all.AddRange(r["rows"]!.AsArray().Select(n => n!.GetValue<long>()));
        }

        return [.. all];
    }

    private static async Task<string> StatusAsync(AppSession app, long index) =>
        (await ResultsAsync(app, index, 1))["rows"]![0]!["statusKind"]!.GetValue<string>();

    /// <summary>描画内容で、オフセット <paramref name="offset"/> のバイトが一致として強調されているか。</summary>
    internal static bool MatchedAt(JsonObject render, long offset)
    {
        long rowStart = offset / 16 * 16;
        JsonObject? row = render["rows"]!.AsArray().Select(r => r!.AsObject())
            .FirstOrDefault(r => r["offsetText"]!.GetValue<string>().EndsWith(rowStart.ToString("X8"), StringComparison.Ordinal));
        return row is not null && row["cells"]![(int)(offset - rowStart)]!["matched"]!.GetValue<bool>();
    }

    /// <summary>遅いデータソース: このファイルの読み込み 1 回ごとに <paramref name="delayMs"/> ms 待つ。</summary>
    internal static JsonObject Slow(string path, int delayMs) => new()
    {
        ["fileSources"] = new JsonArray(new JsonObject { ["match"] = Path.GetFileName(path), ["delayMs"] = delayMs }),
    };

    /// <summary>TD-FIND-HITS-100: k × 0x20 (k = 0〜99) に `AB CD` (4,096 バイト)。</summary>
    internal static byte[] Hits100()
    {
        byte[] data = new byte[4096];
        for (int k = 0; k < 100; k++)
        {
            data[k * 0x20] = 0xAB;
            data[(k * 0x20) + 1] = 0xCD;
        }

        return data;
    }

    /// <summary>TD-FIND-HITS-1000: k × 1,024 (k = 0〜999) に `12 34 56 78` (1 MiB)。</summary>
    internal static byte[] Hits1000()
    {
        byte[] data = new byte[1024 * 1024];
        for (int k = 0; k < 1000; k++)
        {
            new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(data, k * 1024);
        }

        return data;
    }

    /// <summary>TD-FIND-HITS-1500K: `AB CD 00 00` を 1,500,000 回。</summary>
    internal static byte[] Hits1500K()
    {
        byte[] data = new byte[6_000_000];
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i] = 0xAB;
            data[i + 1] = 0xCD;
        }

        return data;
    }

    /// <summary>TD-FIND-MARK-1M: 0x80000 に `CA FE BA BE` (1 MiB)。</summary>
    internal static byte[] Mark1M()
    {
        byte[] data = new byte[1024 * 1024];
        new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }.CopyTo(data, 0x80000);
        return data;
    }

    internal static string Ascii(byte[] bytes) => Encoding.ASCII.GetString(bytes);
}
