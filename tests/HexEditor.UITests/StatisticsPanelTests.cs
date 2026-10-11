using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 統計パネル (ANA-10〜ANA-16) と「ファイル形式」パネル (ANA-17)。パネルの状態はテスト用の命令 "stats"・"fileType" と UI オートメーションで読む。
/// グラフのクリック・ドラッグ・ホバーは、グラフの要素の範囲から求めた位置で、ポインタの処理と同じ処理を呼ぶ。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class StatisticsPanelTests
{
    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private static Task<JsonObject> StatsAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("stats", request, TimeSpan.FromSeconds(120));
    }

    /// <summary>統計パネルを開き、計算が終わるまで待つ。</summary>
    private static async Task<JsonObject> ShowAndWaitAsync(AppSession app, string tab = "Histogram")
    {
        await StatsAsync(app, "show", new JsonObject { ["tab"] = tab });
        await app.WaitForAsync("StatisticsPanel");
        JsonObject state = [];
        await app.WaitUntilAsync(async () =>
        {
            state = await StatsAsync(app);
            return state["completed"]?.GetValue<bool>() == true && !state["computing"]!.GetValue<bool>();
        }, Wait, "the statistics");
        return state;
    }

    private static async Task<long> CursorAsync(AppSession app) => (await app.DocumentAsync())["cursor"]!.GetValue<long>();

    [Fact]
    public Task Statistics_panel_opens_at_the_bottom_from_the_analysis_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.CommandAsync("Command_Statistics");
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["open"]!.GetValue<bool>(), Wait, "the statistics panel");
        Assert.Equal("bottom", (await StatsAsync(app))["location"]?.GetValue<string>());
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.statistics" });
        await app.WaitUntilAsync(async () => !(await StatsAsync(app))["open"]!.GetValue<bool>(), Wait, "the panel to close");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-10-03")]
    public Task Double_clicking_a_bar_moves_to_the_next_occurrence() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-BYTES-256")] });
        await app.GoToAsync(0);
        JsonObject state = await ShowAndWaitAsync(app);
        Assert.Equal(256, state["bins"]!.GetValue<int>());

        // 0x7F の棒は UI オートメーションの要素 (Invoke はダブルクリックと同じ)。
        await app.UiaInvokeAsync("Stats_Bar_7F");
        await app.WaitUntilAsync(async () => await CursorAsync(app) == 0x7F, Wait, "the cursor at 0x7F");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-10-05")]
    public Task Histogram_as_a_table_matches_the_json_export() => UiTestContext.RunAsync(async ctx =>
    {
        string export = Path.Combine(ctx.Root, "histogram.json");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-TEXT-ASCII")], Hooks = new JsonObject { ["savePicker"] = export } });
        await ShowAndWaitAsync(app);
        await StatsAsync(app, "set", new JsonObject { ["histogramAsTable"] = true });
        await app.WaitForAsync("Stats_HistogramTable");
        await StatsAsync(app, "export", new JsonObject { ["json"] = true });
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(export)), Wait, "the JSON export");

        JsonArray rows = (await StatsAsync(app))["histogramRows"]!.AsArray();
        JsonArray bins = JsonNode.Parse(File.ReadAllText(export))!["bins"]!.AsArray();
        Assert.Equal(256, rows.Count);
        for (int i = 0; i < 256; i++)
        {
            Assert.Equal(bins[i]!["count"]!.GetValue<long>(), rows[i]!["count"]!.GetValue<long>());
            Assert.Equal(bins[i]!["percent"]!.GetValue<double>(), rows[i]!["percent"]!.GetValue<double>(), 9);
        }

        Assert.Equal("A", rows[0x41]!["char"]!.GetValue<string>());
    });

    /// <summary>TD-RANDOM-16M をブロックの大きさ 64 KB で計算した状態 (256 ブロック) にする。</summary>
    private static async Task<AppSession> Random16MWith64KBlocksAsync(UiTestContext ctx)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        await ShowAndWaitAsync(app, "Entropy");
        await StatsAsync(app, "set", new JsonObject { ["blockSize"] = 65536L });
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["blocks"]?.GetValue<int>() == 256, Wait, "256 blocks");
        await app.WaitForAsync("Stats_EntropyChart");
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-13-02")]
    public Task Clicking_the_graph_moves_and_dragging_selects() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await Random16MWith64KBlocksAsync(ctx);
        await StatsAsync(app, "graphClick", new JsonObject { ["fraction"] = 0.5 });
        await app.WaitUntilAsync(async () => await CursorAsync(app) == 0x800000, Wait, "the cursor at 0x800000");

        await StatsAsync(app, "graphDrag", new JsonObject { ["from"] = 0.25, ["to"] = 0.5 });
        await ViewOps.AssertSelectionAsync(app, 0x400000, 0x400000);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-13-03")]
    public Task Zooming_in_recalculates_the_visible_range_with_smaller_blocks() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        JsonObject state = await ShowAndWaitAsync(app, "Entropy");
        Assert.Equal(16 * 1024, state["blockSize"]!.GetValue<long>());

        // 1 ブロックが 4 ピクセル以上になるまで拡大する。パネルの幅は画面・ランナーで変わる (狭いと 8 倍では 1 ブロックが 4 px に満たない) ため、
        // 幅 128 px まで足りる 32 倍にする。
        await StatsAsync(app, "zoomIn", new JsonObject { ["times"] = 5 });
        await StatsAsync(app, "flushDetail");
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["detailLog"]!.AsArray().Count > 0, Wait, "the detail blocks");
        JsonObject log = (await StatsAsync(app))["detailLog"]!.AsArray()[0]!.AsObject();
        long size = log["blockSize"]!.GetValue<long>();
        Assert.InRange(size, 256, 16 * 1024 - 1);

        // 表示範囲 (全体の 8 分の 1) の外は計算し直していない。
        Assert.True(log["to"]!.GetValue<long>() - log["from"]!.GetValue<long>() <= (16 << 20) / 8 + (16 * 1024));

        await StatsAsync(app, "set", new JsonObject { ["graphAsTable"] = true });
        JsonArray rows = (await StatsAsync(app))["blockRows"]!.AsArray();
        Assert.Contains(rows, r => r!["blockSize"]!.GetValue<string>() != rows[^1]!["blockSize"]!.GetValue<string>()
            || rows.Count > 1024);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-13-05")]
    public Task Keyboard_moves_between_blocks_and_jumps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await Random16MWith64KBlocksAsync(ctx);
        await app.GoToAsync(0);
        // F6 と Tab でフォーカスを移した状態の代わりに、グラフにフォーカスを移してキーを送る。
        await StatsAsync(app, "graphKey", new JsonObject { ["key"] = "Right", ["count"] = 3 });
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["selectedBlock"]!.GetValue<int>() == 3, Wait, "the fourth block");

        // UI オートメーションの選択項目で確かめる。
        var chart = await app.WaitForAsync("Stats_EntropyChart");
        var selected = chart.Patterns.Selection.Pattern.Selection.Value;
        Assert.Single(selected);
        Assert.Contains("0x30000", selected[0].Name, StringComparison.Ordinal);

        await StatsAsync(app, "graphKey", new JsonObject { ["key"] = "Enter" });
        await app.WaitUntilAsync(async () => await CursorAsync(app) == 0x30000, Wait, "the cursor at 0x30000");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-14-02")]
    public Task Digram_cells_show_the_value_and_count() => UiTestContext.RunAsync(async ctx =>
    {
        string pe = ctx.TestData("TD-PE-X64");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [pe] });
        await ShowAndWaitAsync(app, "Digram");
        string text = (await StatsAsync(app, "digramHover", new JsonObject { ["x"] = 0x4D, ["y"] = 0x5A }))["text"]!.GetValue<string>();
        byte[] data = File.ReadAllBytes(pe);
        int count = Enumerable.Range(0, data.Length - 1).Count(i => data[i] == 0x4D && data[i + 1] == 0x5A);
        Assert.Contains("0x4D 0x5A", text, StringComparison.Ordinal);
        Assert.Contains(count.ToString("N0"), text, StringComparison.Ordinal);
        Assert.Contains("%", text, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-14-03")]
    public Task Double_clicking_a_digram_cell_searches_the_two_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-PE-X64")] });
        await app.GoToAsync(0);
        await ShowAndWaitAsync(app, "Digram");
        await StatsAsync(app, "digramInvoke", new JsonObject { ["x"] = 0x50, ["y"] = 0x45 });
        await app.WaitUntilAsync(async () => await CursorAsync(app) == 0x80, Wait, "the PE signature");
        Assert.Equal("50 45", (await app.WaitForAsync("Find_Query")).Patterns.Value.Pattern.Value.Value);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-15-04")]
    public Task Period_results_can_request_the_record_view() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-REC64")] });
        await ShowAndWaitAsync(app, "Pattern");

        await StatsAsync(app, "patterns");
        await StatsAsync(app, "set", new JsonObject { ["patternView"] = 1 });
        JsonObject state = await StatsAsync(app);
        Assert.Equal(64, state["periods"]!.AsArray()[0]!.GetValue<int>());
        JsonObject menu = await StatsAsync(app, "periodMenu", new JsonObject { ["row"] = 0 });
        Assert.True(menu["enabled"]!.GetValue<bool>());
        // 「この長さでレコード表示」で、レコード表示 (VIEW-18) がレコード長 64 でオンになる。
        JsonObject view = (await app.SendAsync("viewSettings"))["view"]!.AsObject();
        Assert.True(view["recordView"]!.GetValue<bool>());
        Assert.Equal(64, view["recordLength"]!.GetValue<int>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-16-02")]
    public Task Embedded_zlib_stream_is_listed_once_with_decompress_in_the_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-ZLIB-EMB")] });
        await app.CommandAsync("Command_Classify");
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["classRows"]!.AsArray().Count > 0, Wait, "the classification");
        JsonArray rows = (await StatsAsync(app))["classRows"]!.AsArray();
        JsonNode zlib = Assert.Single(rows, r => r!["signature"]?.GetValue<string>() == "zlib")!;
        Assert.Equal(0x10000, zlib["offset"]!.GetValue<long>());

        // 「ここから展開...」は 03 の展開機能 (F4-04、フェーズ 4) ができるまで無効 (一覧に出ることだけを確かめる)。
        JsonArray items = (await StatsAsync(app, "classMenu", new JsonObject { ["format"] = "zlib" }))["items"]!.AsArray();
        Assert.Contains(items, i => i!["id"]!.GetValue<string>() == "Stats_Menu_Decompress");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-16-04")]
    public Task Legend_and_band_regions_carry_names_and_patterns() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CLASSES")] });
        await app.CommandAsync("Command_Classify");
        await app.WaitUntilAsync(async () => (await StatsAsync(app))["legend"]!.AsArray().Count > 0, Wait, "the classification");
        JsonArray legend = (await StatsAsync(app))["legend"]!.AsArray();
        string[] patterns = [.. legend.Select(l => l!["pattern"]!.GetValue<string>())];
        Assert.Equal(patterns.Length, patterns.Distinct().Count());
        Assert.All(legend, l => Assert.False(string.IsNullOrEmpty(l!["name"]!.GetValue<string>())));

        // 帯の区間の UI オートメーションの名前: 分類名・開始オフセット・長さ。ItemStatus は模様のリソース名。
        IReadOnlyList<long> starts = TestDataCatalog.ClassesStarts;
        string name = await app.UiaNameAsync("Stats_Region_1");
        Assert.Contains("0x" + starts[1].ToString("X"), name, StringComparison.Ordinal);
        Assert.Contains(legend.First(l => l!["pattern"]!.GetValue<string>() == "Pattern_Dots")!["name"]!.GetValue<string>(), name, StringComparison.Ordinal);
        Assert.Equal("pattern=Pattern_Dots", (await app.WaitForAsync("Stats_Region_1")).Properties.ItemStatus.Value);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-17-02")]
    public Task Status_bar_file_type_opens_the_panel_with_the_mismatch() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-PNG-AS-JPG")] });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fileType"))["status"]!.GetValue<string>().Contains("PNG"), Wait, "the file type");
        await app.UiaInvokeAsync("Status_FileType");
        await app.WaitUntilAsync(async () => (await app.SendAsync("fileType"))["open"]!.GetValue<bool>(), Wait, "the file type panel");
        JsonObject state = await app.SendAsync("fileType");
        Assert.Contains("PNG", state["candidates"]!.AsArray()[0]!["name"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(state["mismatch"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(await app.UiaNameAsync("FileType_Mismatch")));
    });
}
