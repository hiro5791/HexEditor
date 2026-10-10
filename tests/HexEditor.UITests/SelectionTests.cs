using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// 選択範囲をずらす (EDIT-05)、矩形選択 (EDIT-06)、マルチ選択 (EDIT-07)、マルチカーソル (EDIT-08)、選択範囲の保存と読み込み (EDIT-09)、
/// 矩形範囲の編集 (EDIT-17)、選択範囲のドラッグ &amp; ドロップ (EDIT-18)。ポインタは実際のマウスを使わず、テスト用の命令の通り道で
/// Hex ビューに渡す (修飾キーも命令で指定する)。クリップボードはテスト用のビルドのアプリ内の代わりのものを読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SelectionTests
{
    internal static Task Pointer(AppSession app, string action, (double X, double Y) point, bool ctrl = false, bool alt = false, bool shift = false) =>
        app.SendAsync("pointer", new JsonObject
        {
            ["action"] = action, ["x"] = point.X, ["y"] = point.Y, ["ctrl"] = ctrl, ["alt"] = alt, ["shift"] = shift,
        });

    /// <summary>オフセット <paramref name="from"/> から <paramref name="to"/> までドラッグする (修飾キー付き)。</summary>
    internal static async Task DragAsync(AppSession app, long from, long to, bool ctrl = false, bool alt = false)
    {
        JsonObject render = await app.RenderAsync();
        await Pointer(app, "down", CellPoint(render, from), ctrl, alt);
        (double x, double y) end = CellPoint(render, to);
        await Pointer(app, "move", (end.x - 2, end.y), ctrl, alt);
        await Pointer(app, "move", end, ctrl, alt);
        await Pointer(app, "up", end, ctrl, alt);
        await app.IdleAsync();
    }

    internal static async Task ClickAtAsync(AppSession app, long offset, bool ctrl = false, bool alt = false)
    {
        JsonObject render = await app.RenderAsync();
        await app.SendAsync("pointer", new JsonObject
        {
            ["action"] = "click", ["x"] = CellPoint(render, offset).X, ["y"] = CellPoint(render, offset).Y, ["ctrl"] = ctrl, ["alt"] = alt,
        });
        await app.IdleAsync();
    }

    internal static List<(long Start, long Length)> Ranges(JsonObject doc) =>
        [.. doc["ranges"]!.AsArray().Select(r => (r![0]!.GetValue<long>(), r[1]!.GetValue<long>()))];

    internal static Task<bool> ExecuteAsync(AppSession app, string id) => ExecuteCoreAsync(app, id);

    private static async Task<bool> ExecuteCoreAsync(AppSession app, string id)
    {
        bool executed = (await app.SendAsync("execute", new JsonObject { ["id"] = id }))["executed"]!.GetValue<bool>();
        await app.IdleAsync();
        return executed;
    }

    private static async Task<string> StatusSelectionAsync(AppSession app) => await app.UiaNameAsync("Status_Selection");

    // ---- EDIT-03 / EDIT-07 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-03-03")]
    public Task Escape_leaves_only_the_primary_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x10, 0x13);
        await DragAsync(app, 0x20, 0x23, ctrl: true);
        await DragAsync(app, 0x30, 0x33, ctrl: true);
        Assert.Equal(3, (await app.DocumentAsync())["selectionCount"]!.GetValue<long>());

        // 4〜5. Esc: 選択はなく、カーソルは主要素 (0x30〜0x33) の末尾 0x34 に 1 つ。
        await app.KeyAsync("Escape");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal("None", doc["selectionKind"]!.GetValue<string>());
        Assert.Equal(1, doc["caretCount"]!.GetValue<int>());
        Assert.Equal(0x34, doc["cursor"]!.GetValue<long>());

        // 6〜7. Alt+クリックで 0x50 と 0x60 にカーソルを追加して Esc: 主カーソル (0x60) だけが残る。
        await ClickAtAsync(app, 0x50, alt: true);
        await ClickAtAsync(app, 0x60, alt: true);
        Assert.Equal(3, (await app.DocumentAsync())["caretCount"]!.GetValue<int>());
        Assert.Contains("3", await StatusSelectionAsync(app));
        await app.KeyAsync("Escape");
        doc = await app.DocumentAsync();
        Assert.Equal(1, doc["caretCount"]!.GetValue<int>());
        Assert.Equal(0x60, doc["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-07-01")]
    public Task Deleting_three_ranges_is_undone_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x10, 0x13);
        await DragAsync(app, 0x20, 0x23, ctrl: true);
        await DragAsync(app, 0x30, 0x33, ctrl: true);

        // 4. 「3 個の範囲、計 12 バイト」。
        string status = await StatusSelectionAsync(app);
        Assert.Contains("3 ranges", status);
        Assert.Contains("12 bytes", status);

        // 5〜6. Delete で 3 か所が抜ける。
        await app.KeyAsync("Delete");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(1_048_564, doc["length"]!.GetValue<long>());
        byte[] expected = [0x0E, 0x0F, .. Enumerable.Range(0x14, 12).Select(i => (byte)i), .. Enumerable.Range(0x24, 7).Select(i => (byte)i)];
        Assert.Equal(expected, await app.BytesAsync(0x0E, expected.Length));

        // 7〜8. Ctrl+Z 1 回で元に戻り、履歴は 0 件。
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        doc = await app.DocumentAsync();
        Assert.Equal(1_048_576, doc["length"]!.GetValue<long>());
        Assert.Equal(Enumerable.Range(0, 0x40).Select(i => (byte)i), await app.BytesAsync(0, 0x40));
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-07-02")]
    public Task Overlapping_and_adjacent_ranges_are_merged() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x10, 0x1F);
        await DragAsync(app, 0x18, 0x27, ctrl: true);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((1L, 24L), (doc["selectionCount"]!.GetValue<long>(), doc["selectedBytes"]!.GetValue<long>()));
        Assert.Equal([(0x10L, 0x18L)], Ranges(doc));

        await DragAsync(app, 0x28, 0x2F, ctrl: true);
        doc = await app.DocumentAsync();
        Assert.Equal((1L, 32L), (doc["selectionCount"]!.GetValue<long>(), doc["selectedBytes"]!.GetValue<long>()));

        await DragAsync(app, 0x40, 0x4F, ctrl: true);
        doc = await app.DocumentAsync();
        Assert.Equal((2L, 48L), (doc["selectionCount"]!.GetValue<long>(), doc["selectedBytes"]!.GetValue<long>()));
        string status = await StatusSelectionAsync(app);
        Assert.Contains("2 ranges", status);
        Assert.Contains("48 bytes", status);
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-EDIT-07-04")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Converting_100000_results_and_filling_keeps_the_ui_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-EDIT-REC100K")] });

        // 1〜2. Hex で CA FE をすべて検索して 100,000 件。
        await SearchResultsTests.OpenFindAsync(app, 0, "CA FE", incremental: false);
        await SearchResultsTests.FindAllAsync(app);
        await SearchResultsTests.WaitForResultsAsync(app, r => r["count"]?.GetValue<long>() == 100_000 && r["running"]?.GetValue<bool>() == false,
            "100,000 results", 120);

        // 3〜4. 選択範囲に変換: 「100000 個の範囲、計 200000 バイト」。
        Assert.True(await ExecuteAsync(app, "search.results.toSelection"));
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((100_000L, 200_000L), (doc["selectionCount"]!.GetValue<long>(), doc["selectedBytes"]!.GetValue<long>()));

        // 5〜7. 00 で塗りつぶす。実行中も UI オートメーションの応答は 100 ms 以下で、処理センターに塗りつぶしが出る。
        await app.CommandAsync("Command_Fill");
        await app.WaitForAsync("FillDialog");
        await app.IdleAsync();
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        var slowest = TimeSpan.Zero;
        await app.WaitUntilAsync(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await app.UiaNameAsync("Status_Selection");
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
            JsonObject state = await app.StateAsync();
            return state["operations"]!.AsArray().Any(o => o!["name"]!.GetValue<string>() == "Fill" && o["state"]!.GetValue<string>() == "Completed");
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the fill to finish");
        Assert.True(slowest <= TimeSpan.FromMilliseconds(100), $"UI automation response during the fill: {slowest.TotalMilliseconds} ms");

        // 8. CA FE は 0 件で、各レコードの先頭 2 バイトが 00 00、その後ろは 11 のまま。
        Assert.Equal(new byte[] { 0, 0, 0x11, 0x11 }, await app.BytesAsync(16 * 54_321, 4));
        Assert.Equal(new byte[] { 0, 0, 0x11 }, await app.BytesAsync(16 * 99_999, 3));
    }, TimeSpan.FromMinutes(5));

    // ---- EDIT-05 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-05-01")]
    public Task Shifting_the_selection_moves_it_by_its_length() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await EditCommandTests.SelectRangeAsync(app, "0", "0x20");
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await ExecuteAsync(app, "edit.selection.shiftNext"));
        }

        await AssertSelectionAsync(app, 0x60, 0x20);

        // 4〜6. 末尾の 32 バイトはずらせず、InfoBar が出る。
        await EditCommandTests.SelectRangeAsync(app, "end-0x20", "0x20");
        await ExecuteAsync(app, "edit.selection.shiftNext");
        await AssertSelectionAsync(app, 0xFFFE0, 0x20);
        Assert.Contains(await NoticesAsync(app), n => n.Contains("can't be shifted", StringComparison.Ordinal));

        // 7〜8. 先頭より前にも出ない。
        await EditCommandTests.SelectRangeAsync(app, "0x10", "0x20");
        await ExecuteAsync(app, "edit.selection.shiftPrevious");
        await AssertSelectionAsync(app, 0x10, 0x20);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-05-02")]
    public Task Swapping_anchor_and_cursor_keeps_the_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x100);
        await app.KeyAsync("Down", shift: true, count: 64);
        await AssertSelectionAsync(app, 0x100, 0x400);
        Assert.Equal(0x500, await CursorAsync(app));

        await ExecuteAsync(app, "edit.selection.swapEnds");
        await AssertSelectionAsync(app, 0x100, 0x400);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x100, doc["cursor"]!.GetValue<long>());
        long top = doc["topRow"]!.GetValue<long>();
        Assert.InRange(0x100 / 16, top, top + doc["visibleRows"]!.GetValue<int>() - 1);

        await app.KeyAsync("Right", shift: true);
        await AssertSelectionAsync(app, 0x101, 0x3FF);
    });

    // ---- EDIT-06 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-06-01")]
    public Task Alt_drag_selects_a_rectangle_and_copies_rows_in_order() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x04, 0x37, alt: true);

        // 2. 「矩形 4 行 × 4 バイト (計 16 バイト)」。
        Assert.Equal("Rectangle 4 rows × 4 bytes (16 bytes total)", await StatusSelectionAsync(app));

        // 3. 選択されているバイトは各行の列 4〜7 だけ。
        JsonObject render = await app.RenderAsync();
        var selected = new List<long>();
        for (long o = 0; o < 0x40; o++)
        {
            if (CellOf(render, o)?["selected"]?.GetValue<bool>() == true)
            {
                selected.Add(o);
            }
        }

        Assert.Equal(new long[] { 4, 5, 6, 7, 0x14, 0x15, 0x16, 0x17, 0x24, 0x25, 0x26, 0x27, 0x34, 0x35, 0x36, 0x37 }, selected);

        // 4〜5. Ctrl+C: 行順に連結した 16 バイト。
        await app.KeyAsync("C", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.SendAsync("clipboard"))["binary"]?.GetValue<string>() == "04050607141516172425262734353637",
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the rectangle in the clipboard");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-06-02")]
    public Task Changing_bytes_per_row_turns_the_rectangle_into_a_multi_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x04, 0x37, alt: true);

        // 2. 1 行のバイト数を 8 にする。
        await ViewSettingsOps.SetBytesPerRowAsync(app, 8);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal("Multiple", doc["selectionKind"]!.GetValue<string>());
        Assert.Contains("4 ranges", await StatusSelectionAsync(app));
        Assert.Contains("16 bytes", await StatusSelectionAsync(app));
        Assert.Equal([(4L, 4L), (0x14L, 4L), (0x24L, 4L), (0x34L, 4L)], Ranges(doc));

        // 5. 「次の要素へ」を 4 回: 主要素が 4 つの要素をたどる。
        var starts = new HashSet<long>();
        for (int i = 0; i < 4; i++)
        {
            await ExecuteAsync(app, "edit.selection.nextElement");
            starts.Add((await app.DocumentAsync())["primaryStart"]!.GetValue<long>());
        }

        Assert.Equal(new HashSet<long> { 4, 0x14, 0x24, 0x34 }, starts);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-06-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task A_rectangle_over_100_GB_does_not_use_memory() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await ViewSettingsOps.SetBytesPerRowAsync(app, 4096);
        long before = (await app.StateAsync())["privateBytes"]!.GetValue<long>();

        // 1〜3. 矩形は範囲を選択 (Ctrl+E) の「矩形として選択」で作る (受け入れ基準 4 の注記)。列 0〜3、最終行まで。
        await SelectRectangleAsync(app, "0", "end-0xFFD");
        JsonObject doc = await app.DocumentAsync();
        long rows = doc["selectionCount"]!.GetValue<long>();
        Assert.True(rows >= 26_214_000, $"{rows} rows");
        // ステータスバーの表示は UI オートメーションで読まない (1 行 4,096 バイトでは Hex ビューのセルの要素が多く、木をたどるのに時間がかかる)。
        JsonObject rect = doc["rectangle"]!.AsObject();
        Assert.Equal((0, 3), (rect["firstColumn"]!.GetValue<int>(), rect["lastColumn"]!.GetValue<int>()));
        Assert.Equal(rows * 4, doc["selectedBytes"]!.GetValue<long>());
        long after = (await app.StateAsync())["privateBytes"]!.GetValue<long>();
        Assert.True(after - before <= 50L * 1024 * 1024, $"memory grew by {(after - before) / 1024 / 1024} MB");

        // 5. 表示を末尾までスクロールする (Ctrl+End はカーソルの移動なので選択を解除する。スクロールバーのつまみを末尾へ動かす)。
        await app.SendAsync("scrollBar", new JsonObject { ["type"] = "ThumbTrack", ["value"] = 1_000_000 });
        await app.SendAsync("scrollBar", new JsonObject { ["type"] = "EndScroll", ["value"] = 1_000_000 });
        long lastRow = (100L << 30) - 4096;
        JsonObject render = await WaitForContentAsync(app, lastRow);
        string docText = (await app.DocumentAsync()).ToJsonString();
        Assert.All(Enumerable.Range(0, 4), c => Assert.True(CellOf(render, lastRow + c)?["selected"]?.GetValue<bool>() == true,
            $"{CellOf(render, lastRow + c)?.ToJsonString()} doc={docText}"));
    });

    /// <summary>範囲を選択 (Ctrl+E) の「矩形として選択」: 開始のバイトと終了のバイトを対角とする矩形。</summary>
    internal static async Task SelectRectangleAsync(AppSession app, string start, string end)
    {
        await app.KeyAsync("E", ctrl: true);
        await app.WaitForAsync("SelectRangeDialog");
        await app.IdleAsync();
        await app.UiaSetValueAsync("SelectRange_Start", start);
        await app.UiaSetValueAsync("SelectRange_End", end);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "SelectRange_Rectangle", ["value"] = true });
        if (!(await app.WaitForAsync("PrimaryButton")).IsEnabled)
        {
            string Describe(JsonObject e) => e.ToJsonString();
            Assert.Fail("Select range: " + string.Join(" / ", await Task.WhenAll(new[] { "SelectRange_StartResult", "SelectRange_EndResult", "SelectRange_LengthResult" }
                .Select(async id => id + "=" + Describe(await ElementAsync(app, id))))));
        }

        await EditCommandTests.PressAsync(app);
    }

    // ---- EDIT-08 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-08-01")]
    public Task Typing_into_four_vertical_cursors_is_undone_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await app.KeyAsync("Down", ctrl: true, alt: true, count: 3);
        Assert.Equal("4 cursors", await StatusSelectionAsync(app));

        await app.TypeAsync("FF");
        await app.IdleAsync();
        foreach (long o in new long[] { 0x10, 0x20, 0x30, 0x40 })
        {
            Assert.Equal(0xFF, (await app.BytesAsync(o, 1))[0]);
            Assert.Equal((byte)(o + 1), (await app.BytesAsync(o + 1, 1))[0]);
        }

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        foreach (long o in new long[] { 0x10, 0x20, 0x30, 0x40 })
        {
            Assert.Equal((byte)o, (await app.BytesAsync(o, 1))[0]);
        }

        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-08-02")]
    public Task Insert_mode_typing_shifts_the_second_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.GoToAsync(0x10);
        await ClickAtAsync(app, 0x20, alt: true);
        await app.KeyAsync("Insert");
        await app.TypeAsync("AB");
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(1_048_578, doc["length"]!.GetValue<long>());
        byte[] expected = [0x0F, 0xAB, .. Enumerable.Range(0x10, 16).Select(i => (byte)i), 0xAB, 0x20, 0x21, 0x22];
        Assert.Equal(expected, await app.BytesAsync(0x0F, expected.Length));
        Assert.Equal([0x11L, 0x22L], doc["carets"]!.AsArray().Select(c => c!.GetValue<long>()));
    });

    // ---- EDIT-09 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-09-01")]
    public Task Saved_selection_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-SEQ-1M");
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file], Profile = profile });
        await DragAsync(app, 0x10, 0x13);
        await DragAsync(app, 0x20, 0x27, ctrl: true);
        await DragAsync(app, 0x30, 0x3F, ctrl: true);

        // 2. 選択範囲を保存… で名前 set1。
        await app.CommandAsync("Command_Selection_Save");
        await app.WaitForAsync("SaveSelectionDialog");
        await app.IdleAsync();
        await app.UiaSetValueAsync("SaveSelection_Name", "set1");
        await EditCommandTests.PressAsync(app);

        // 3〜5. 解除してから読み込む: 「3 個の範囲、計 28 バイト」。
        await app.KeyAsync("Escape");
        await LoadSetAsync(app);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal([(0x10L, 4L), (0x20L, 8L), (0x30L, 0x10L)], Ranges(doc));
        Assert.Contains("3 ranges", await StatusSelectionAsync(app));
        Assert.Contains("28 bytes", await StatusSelectionAsync(app));

        // 6〜8. 同じ設定フォルダで起動し直しても一覧に set1 (3 個、28 バイト) があり、読み込める。
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        app = await ctx.StartAsync(new AppOptions { Files = [file], Profile = profile });
        JsonObject set = (await app.DocumentAsync())["selectionSets"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("set1", 3L, 28L), (set["name"]!.GetValue<string>(), set["count"]!.GetValue<long>(), set["total"]!.GetValue<long>()));
        await LoadSetAsync(app);
        Assert.Equal([(0x10L, 4L), (0x20L, 8L), (0x30L, 0x10L)], Ranges(await app.DocumentAsync()));
    });

    private static async Task LoadSetAsync(AppSession app)
    {
        await app.CommandAsync("Command_Selection_Load");
        await app.WaitForAsync("LoadSelectionDialog");
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app);
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-09-02")]
    public Task Csv_export_and_import_give_the_same_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x10, 0x13);
        await DragAsync(app, 0x20, 0x27, ctrl: true);
        await DragAsync(app, 0x30, 0x3F, ctrl: true);

        // 2〜3. CSV に書き出す: 見出しと 3 行。
        string csv = Path.Combine(ctx.Root, "sel.csv");
        await app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["savePicker"] = csv } });
        await app.CommandAsync("Command_Selection_Export");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(csv)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "sel.csv");
        await app.IdleAsync();
        Assert.Equal(["start,length", "0x10,0x4", "0x20,0x8", "0x30,0x10"], File.ReadAllLines(csv));

        // 4〜6. 解除してインポートすると同じ 3 要素。
        await app.KeyAsync("Escape");
        await app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray(csv) } });
        await app.CommandAsync("Command_Selection_Import");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionCount"]!.GetValue<long>() == 3,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the imported selection");
        Assert.Equal([(0x10L, 4L), (0x20L, 8L), (0x30L, 0x10L)], Ranges(await app.DocumentAsync()));

        // 7〜8. 2 行目の開始が abc のファイルは、行番号付きのエラーで読み込まない。
        string bad = Path.Combine(ctx.Root, "bad.csv");
        File.WriteAllLines(bad, ["start,length", "abc,0x4", "0x20,0x8"]);
        await app.SendAsync("setHooks", new JsonObject { ["settings"] = new JsonObject { ["openPicker"] = new JsonArray(bad) } });
        await app.CommandAsync("Command_Selection_Import");
        await app.WaitUntilAsync(async () => (await NoticesAsync(app)).Any(n => n.StartsWith("Line 2:", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the import error");
        Assert.Contains(await NoticesAsync(app), n => n.Contains("start isn't a number", StringComparison.Ordinal));
        Assert.Equal(3, (await app.DocumentAsync())["selectionCount"]!.GetValue<long>());
    });

    // ---- EDIT-17 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-17-01")]
    public Task Deleting_a_rectangle_is_undone_at_once() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x04, 0x35, alt: true);
        await app.KeyAsync("Delete");
        Assert.Equal(1_048_568, (await app.DocumentAsync())["length"]!.GetValue<long>());
        byte[] expected = [0, 1, 2, 3, .. Enumerable.Range(6, 14).Select(i => (byte)i), .. Enumerable.Range(0x16, 14).Select(i => (byte)i),
            .. Enumerable.Range(0x26, 14).Select(i => (byte)i), 0x36, 0x37];
        Assert.Equal(expected, await app.BytesAsync(0, expected.Length));

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(Enumerable.Range(0, 0x40).Select(i => (byte)i), await app.BytesAsync(0, 0x40));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-17-02")]
    public Task A_copied_rectangle_is_overwrite_pasted_row_by_row() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await DragAsync(app, 0x04, 0x35, alt: true);
        await app.KeyAsync("C", ctrl: true);
        await app.IdleAsync();

        // 2. テキスト形式は行ごとに改行した Hex。
        // コピーは非同期に読むので、クリップボードに入るまで待つ。
        await app.WaitUntilAsync(async () => (await app.SendAsync("clipboard"))["text"]?.GetValue<string>() == "04 05\r\n14 15\r\n24 25\r\n34 35",
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the rectangle in the clipboard");

        // 3〜4. 0x48 で Ctrl+B: 4 行の列 8〜9 に上書きされ、長さは変わらない。
        await ClickAtAsync(app, 0x48);
        await app.KeyAsync("B", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x04, 0x05, 0x4A }, await app.BytesAsync(0x48, 3));
        Assert.Equal(new byte[] { 0x14, 0x15 }, await app.BytesAsync(0x58, 2));
        Assert.Equal(new byte[] { 0x24, 0x25 }, await app.BytesAsync(0x68, 2));
        Assert.Equal(new byte[] { 0x34, 0x35, 0x7A }, await app.BytesAsync(0x78, 3));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-17-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Deleting_a_two_million_row_rectangle_is_refused() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await ViewSettingsOps.SetBytesPerRowAsync(app, 4096);

        // 1〜2. 2,000,000 行 × 列 0〜1 の矩形 (範囲を選択の「矩形として選択」で作る)。
        await SelectRectangleAsync(app, "0", "0x" + (1_999_999L * 4096 + 1).ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2_000_000, (await app.DocumentAsync())["selectionCount"]!.GetValue<long>());

        // 3〜4. Delete は行われず、上限のメッセージが出る。
        await app.KeyAsync("Delete");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(100L << 30, doc["length"]!.GetValue<long>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.Contains(await NoticesAsync(app), n => n.Contains("more than the limit of 1,000,000 rows", StringComparison.Ordinal));

        // 5〜6. 上書きの操作 (FF で塗りつぶし) は行える。
        await app.CommandAsync("Command_Fill");
        await app.WaitForAsync("FillDialog");
        await app.IdleAsync();
        await app.UiaSetValueAsync("Fill_Value", "0xFF");
        await EditCommandTests.PressAsync(app);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, await app.BytesAsync(0, 2));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, await app.BytesAsync(1_999_999L * 4096, 2));
    }, TimeSpan.FromMinutes(5));

    // ---- EDIT-18 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-18-01")]
    public Task Dragging_the_selection_moves_it_and_undo_restores_it() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x00, 0x0F);
        await DragAsync(app, 0x05, 0x40);
        Assert.Equal(1_048_576, (await app.DocumentAsync())["length"]!.GetValue<long>());
        byte[] expected = [.. Enumerable.Range(0x10, 0x30).Select(i => (byte)i), .. Enumerable.Range(0, 0x10).Select(i => (byte)i), 0x40];
        Assert.Equal(expected, await app.BytesAsync(0, expected.Length));

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(Enumerable.Range(0, 0x50).Select(i => (byte)i), await app.BytesAsync(0, 0x50));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-18-02")]
    public Task Holding_ctrl_when_dropping_copies() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x00, 0x0F);
        JsonObject render = await app.RenderAsync();
        await Pointer(app, "down", CellPoint(render, 0x05));
        (double x, double y) target = CellPoint(render, 0x40);
        await Pointer(app, "move", (target.x - 2, target.y));
        await Pointer(app, "move", target);

        // 3. Ctrl を押すと「コピー」の表示になる。
        await Pointer(app, "move", target, ctrl: true);
        Assert.Equal("copy", (await app.DocumentAsync())["dropEffect"]!.GetValue<string>());
        await Pointer(app, "up", target, ctrl: true);
        await app.IdleAsync();

        Assert.Equal(1_048_592, (await app.DocumentAsync())["length"]!.GetValue<long>());
        Assert.Equal(Enumerable.Range(0, 0x10).Select(i => (byte)i), await app.BytesAsync(0, 0x10));
        Assert.Equal([.. Enumerable.Range(0, 0x10).Select(i => (byte)i), 0x40], await app.BytesAsync(0x40, 0x11));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-18-04")]
    public Task Dropping_inside_the_selection_is_not_allowed() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await DragAsync(app, 0x00, 0x1F);
        JsonObject render = await app.RenderAsync();
        await Pointer(app, "down", CellPoint(render, 0x05));
        (double x, double y) target = CellPoint(render, 0x10);
        await Pointer(app, "move", (target.x - 2, target.y));
        await Pointer(app, "move", target);

        // 3. ポインタは禁止の形 (実際のポインタの代わりに、ビューが示すドロップの効果を読む)。
        Assert.Equal("none", (await app.DocumentAsync())["dropEffect"]!.GetValue<string>());
        await Pointer(app, "up", target);
        await app.IdleAsync();
        Assert.Equal(Enumerable.Range(0, 0x40).Select(i => (byte)i), await app.BytesAsync(0, 0x40));
        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());
    });

    // ---- FIND-21 ----

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-21-01")]
    public Task Converting_100_results_gives_100_ranges() => UiTestContext.RunAsync(async ctx =>
    {
        byte[] data = new byte[4096];
        for (int k = 0; k < 100; k++)
        {
            data[k * 0x20] = 0xAB;
            data[k * 0x20 + 1] = 0xCD;
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-100.bin", data)] });
        await SearchResultsTests.OpenFindAsync(app, 0, "AB CD", incremental: false);
        await SearchResultsTests.FindAllAsync(app);
        await SearchResultsTests.WaitForResultsAsync(app, r => r["count"]?.GetValue<long>() == 100 && r["running"]?.GetValue<bool>() == false, "100 results");

        // 1〜2. 変換 > 選択範囲に: 「100 個の範囲、計 200 バイト」。
        // 変換のドロップダウンの項目と同じ処理のコマンド (メニューは開かない)。選んだ行が 2 行未満なので、すべての行が対象。
        Assert.True(await ExecuteAsync(app, "search.results.toSelection"));
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal((100L, 200L), (doc["selectionCount"]!.GetValue<long>(), doc["selectedBytes"]!.GetValue<long>()));
        Assert.Contains("100 ranges", await StatusSelectionAsync(app));

        // 3. Ctrl+C: AB CD を 100 回連結した 200 バイト。
        await app.SendAsync("focus", new JsonObject { ["target"] = "editor" });
        await app.KeyAsync("C", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.SendAsync("clipboard"))["binary"]?.GetValue<string>() == string.Concat(Enumerable.Repeat("ABCD", 100)),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the ranges in the clipboard");
    });
}
