using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 構造を理解した比較 (ANA-31・ANA-32)。比較タブはテスト用の命令 compareOpen で開き、構造の差分パネルはテスト用の命令 (struct*) で操作し、
/// 状態を読む (実際のマウス・キーボードは使わない)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class StructCompareTests
{
    private static TimeSpan Wait => UiTest.Scaled(TimeSpan.FromSeconds(90));

    private static async Task<JsonObject> OpenAsync(AppSession app, string left, string right, string? template = null)
    {
        await app.SendAsync("compareOpen", new JsonObject { ["left"] = left, ["right"] = right, ["method"] = "simple" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        await app.SendAsync("compareWait", null, Wait);
        if (template is not null)
        {
            return await app.SendAsync("structTemplate", new JsonObject { ["path"] = template }, Wait);
        }

        return await app.SendAsync("structWait", null, Wait);
    }

    private static async Task<(AppSession App, JsonObject State)> PngAsync(UiTestContext ctx)
    {
        string a = ctx.TestData("TD-ANA-STRUCT-PNG-A");
        string b = ctx.TestData("TD-ANA-STRUCT-PNG-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        JsonObject state = await OpenAsync(app, a, b);
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "structDiffs" });
        return (app, await app.SendAsync("structState"));
    }

    private static List<JsonObject> Rows(JsonObject state) => [.. state["rows"]!.AsArray().Select(r => r!.AsObject())];

    private static List<JsonObject> Leaves(JsonObject state) => [.. Rows(state).Where(r => !r["hasChildren"]!.GetValue<bool>() && r["kind"]!.GetValue<string>() != "outside")];

    private static string S(JsonNode? n) => n?.GetValue<string>() ?? string.Empty;

    private static long L(JsonNode? n) => n!.GetValue<long>();

    // ---- ANA-31 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-31-06")]
    public Task Chosen_key_is_used_for_the_next_compare() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-STRUCT-ARR-A");
        string b = ctx.TestData("TD-ANA-STRUCT-ARR-B");
        string items = ctx.TestData("TD-ANA-STRUCT-ARR-HXT");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        JsonObject state = await OpenAsync(app, a, b, items);
        Assert.Equal(3, L(state["diffCount"]));
        Assert.Equal("Content", S(Rows(state).Single(r => S(r["leftPath"]) == "items")["match"]));

        // 1〜2. items の行で「対応付けの方法...」を開き、キー id にして確定する。
        await app.SendAsync("structRow", new JsonObject { ["path"] = "items" });
        Task<JsonObject> menu = app.SendAsync("structMenu", new JsonObject { ["item"] = "Matching" });
        JsonObject dialog = await app.SendAsync("structRuleDialog", new JsonObject());
        await menu;
        Assert.Equal(["id", "value"], dialog["keys"]!.AsArray().Select(k => S(k)));
        await app.SendAsync("structRuleDialog", new JsonObject { ["method"] = 1, ["key"] = "id", ["ok"] = true });
        await app.WaitUntilAsync(async () => S(Rows(await app.SendAsync("structWait", null, Wait)).FirstOrDefault(r => S(r["leftPath"]) == "items")?["match"]) == "Key: id",
            Wait, "the key matching");
        state = await app.SendAsync("structState");
        Assert.Equal(3, L(state["diffCount"]));

        // 3. 比較タブを閉じ、同じ 2 つで同じテンプレートの比較をもう一度行う。
        JsonObject compare = await app.SendAsync("compareState");
        await app.SendAsync("closeToolPage", new JsonObject { ["id"] = S(compare["id"]) });
        state = await OpenAsync(app, a, b, items);
        Assert.Equal("Key: id", S(Rows(state).Single(r => S(r["leftPath"]) == "items")["match"]));
        Assert.Equal(3, L(state["diffCount"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-31-13")]
    public Task Without_a_template_only_bytes_are_compared() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });

        // 1. 解析 > 比較 > ファイルを比較... で左に A、右に B を選び「比較」(構造でも比較するは既定でオン)。
        JsonObject dialog = await app.SendAsync("compareDialog", new JsonObject { ["action"] = "open" });
        Assert.True(dialog["structure"]!.GetValue<bool>());
        await app.SendAsync("compareDialog", new JsonObject
        {
            ["action"] = "set", ["left"] = new JsonObject { ["target"] = 0 }, ["right"] = new JsonObject { ["target"] = 1 },
        });
        await app.SendAsync("compareDialog", new JsonObject { ["action"] = "compare" });
        JsonObject bytes = await app.SendAsync("compareWait", null, Wait);
        Assert.True(L(bytes["listCount"]) >= 1);

        // 2. 下のパネルの「構造の差分」を開く。
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "structDiffs" });
        JsonObject state = await app.SendAsync("structWait", null, Wait);
        Assert.Equal("noTemplate", S(state["state"]));
        Assert.Contains("No template was chosen", S(state["reason"]));
        Assert.True(state["chooseVisible"]!.GetValue<bool>());
        Assert.Equal(0, (await app.SendAsync("openDialogs"))["count"]!.GetValue<int>());
        Assert.DoesNotContain("Error", S((await app.SendAsync("compareState"))["statusMessage"]));
    });

    // ---- ANA-32 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-01")]
    public Task Only_differing_leaves_and_their_ancestors_are_shown() => UiTestContext.RunAsync(async ctx =>
    {
        (_, JsonObject state) = await PngAsync(ctx);
        List<JsonObject> rows = Rows(state);
        JsonObject chunks = rows.First();
        Assert.Equal("chunks", S(chunks["name"]));
        Assert.Equal("Changed 5, Added 1, Removed 1", S(chunks["kindText"]));
        Assert.DoesNotContain(rows, r => S(r["name"]) == "signature");
        Assert.DoesNotContain(rows, r => S(r["path"]).Contains("IDAT", StringComparison.Ordinal) || S(r["path"]).Contains("IEND", StringComparison.Ordinal));
        Assert.Equal(7, Leaves(state).Count);
        Assert.All(rows.Where(r => r["hasChildren"]!.GetValue<bool>()), r => Assert.True(r["expanded"]!.GetValue<bool>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-02")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Selecting_a_row_selects_the_field_on_both_sides() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await PngAsync(ctx);
        JsonObject state = await app.SendAsync("structSelect", new JsonObject { ["path"] = "chunks[0].header.width" });
        Assert.Equal((0x10L, 4L), (L(state["left"]!["selectionStart"]), L(state["left"]!["selectionLength"])));
        Assert.Equal((0x10L, 4L), (L(state["right"]!["selectionStart"]), L(state["right"]!["selectionLength"])));

        JsonObject added = Leaves(state).Single(r => S(r["kind"]) == "added");
        state = await app.SendAsync("structSelect", new JsonObject { ["row"] = L(added["index"]) });
        Assert.Equal((L(added["rightOffset"]), L(added["rightSize"])), (L(state["right"]!["selectionStart"]), L(state["right"]!["selectionLength"])));
        Assert.Equal(L(added["leftOffset"]), L(state["left"]!["cursor"]));
        Assert.Equal(0, L(state["left"]!["selectionLength"]));
        Assert.Equal(0x39, L(added["leftOffset"])); // 左の tEXt (Author) のチャンクの終わり
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-03")]
    public Task Alt_F5_moves_between_structure_differences_and_wraps() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await PngAsync(ctx);
        List<JsonObject> leaves = Leaves(state);
        await app.SendAsync("structSelect", new JsonObject { ["row"] = L(leaves[0]["index"]) });
        await app.SendAsync("structFocus");

        // 1. Alt+F5 を 6 回: 7 番目 (最後) の葉の行。
        for (int i = 0; i < 6; i++)
        {
            await app.KeyAsync("F5", alt: true);
        }

        state = await app.SendAsync("structState");
        Assert.Equal(S(leaves[6]["path"]), S(state["current"]));

        // 2. もう 1 回: 最初の葉の行に戻り、ステータスバーに知らせる。
        await app.KeyAsync("F5", alt: true);
        state = await app.SendAsync("structState");
        Assert.Equal(S(leaves[0]["path"]), S(state["current"]));
        Assert.Contains("Returned to the first structure difference", S(state["status"]));
        long before = L(state["left"]!["cursor"]);

        // 3. Shift+Alt+F5: 最後の葉の行。
        await app.KeyAsync("F5", shift: true, alt: true);
        state = await app.SendAsync("structState");
        Assert.Equal(S(leaves[6]["path"]), S(state["current"]));

        // 4. Hex ビューにフォーカスを移して Alt+←: 左のカーソルが手順 3 の前の位置に戻る。
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });
        await app.KeyAsync("Left", alt: true);
        state = await app.SendAsync("structState");
        Assert.Equal(before, L(state["left"]!["cursor"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-04")]
    public Task Filtering_by_kind_and_text() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await PngAsync(ctx);
        JsonObject state = await app.SendAsync("structFilter", new JsonObject { ["kinds"] = new JsonArray("Added") });
        JsonObject added = Assert.Single(Leaves(state));
        Assert.Equal("added", S(added["kind"]));
        Assert.Contains("tEXt", S(added["path"]));

        state = await app.SendAsync("structFilter", new JsonObject { ["text"] = "tEXt" });
        List<JsonObject> leaves = Leaves(state);
        Assert.Equal(4, leaves.Count);
        Assert.All(leaves, r => Assert.Contains("tEXt", S(r["path"])));
        Assert.Equal(3, leaves.Count(r => S(r["kind"]) == "changed"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-07")]
    public Task Show_in_left_result_tree_selects_the_node() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await PngAsync(ctx);
        await app.SendAsync("structRow", new JsonObject { ["path"] = "chunks[0].header.width" });
        await app.SendAsync("structMenu", new JsonObject { ["item"] = "TreeLeft" });

        JsonObject tree = [];
        await app.WaitUntilAsync(async () => S((tree = await app.SendAsync("template", new JsonObject { ["action"] = "state" }))["selectedPath"]) == "chunks[0].header.width",
            Wait, "the node in the result tree");
        JsonObject application = Assert.Single(tree["applications"]!.AsArray())!.AsObject();
        Assert.EndsWith("png.hxt", S(application["template"]));
        Assert.Equal(0, L(application["offset"]));
        Assert.Equal("Fixed", S(application["kind"]));
        Assert.EndsWith("TD-ANA-STRUCT-PNG-A.png", S((await app.DocumentAsync())["path"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-08")]
    public Task Keyboard_alone_operates_the_panel() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await PngAsync(ctx);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1. F6 で構造の差分パネルにフォーカスが移るまで繰り返す (下のパネルの見出し → 中身のツリー表。代わりの操作は使わない)。
        JsonObject state = await app.SendAsync("structState");
        var visited = new List<string>();
        for (int i = 0; i < 24 && !state["treeRowFocused"]!.GetValue<bool>(); i++)
        {
            JsonObject f6 = await app.KeyAsync("F6");
            await app.IdleAsync();
            state = await app.SendAsync("structState");
            visited.Add($"{f6["handledBy"]}:{state["focusedId"]}");
        }

        Assert.True(state["treeRowFocused"]!.GetValue<bool>(), "F6 did not reach the structure differences tree: " + string.Join(", ", visited));

        // 2. ↓ を 2 回、← で chunks を閉じ、→ で開く。
        await app.SendAsync("structRow", new JsonObject { ["row"] = 0 });
        await app.SendAsync("structKey", new JsonObject { ["key"] = "Down" });
        await app.SendAsync("structKey", new JsonObject { ["key"] = "Down" });
        await app.SendAsync("structRow", new JsonObject { ["row"] = 0 });
        state = await app.SendAsync("structKey", new JsonObject { ["key"] = "Left" });
        Assert.False(Rows(state)[0]["expanded"]!.GetValue<bool>());
        Assert.Single(Rows(state));
        state = await app.SendAsync("structKey", new JsonObject { ["key"] = "Right" });
        Assert.True(Rows(state)[0]["expanded"]!.GetValue<bool>());

        // 3. ↓ で葉の行を選び、Enter。
        JsonObject width = Rows(state).First(r => S(r["leftPath"]) == "chunks[0].header.width");
        await app.SendAsync("structRow", new JsonObject { ["row"] = L(width["index"]) - 1 });
        await app.SendAsync("structKey", new JsonObject { ["key"] = "Down" });
        state = await app.SendAsync("structKey", new JsonObject { ["key"] = "Enter" });
        Assert.Equal((0x10L, 4L), (L(state["left"]!["selectionStart"]), L(state["left"]!["selectionLength"])));
        Assert.Equal((0x10L, 4L), (L(state["right"]!["selectionStart"]), L(state["right"]!["selectionLength"])));

        // 4. Shift+F10 で右クリックメニューを開き、Esc で閉じる。
        JsonObject key = await app.SendAsync("structKey", new JsonObject { ["key"] = "F10", ["shift"] = true });
        Assert.True(key["handled"]!.GetValue<bool>());
        await app.IdleAsync();
        JsonObject menu = await app.SendAsync("structMenuClose");
        Assert.True(menu["menuOpen"]!.GetValue<bool>());
        Assert.Contains("StructDiffMenu_CopyPath", menu["menuItems"]!.AsArray().Select(i => S(i)));

        // 5. ツールバーの「エクスポート...」に Tab (Shift+Tab) で移り、画面が開く (メニューを閉じたときのフォーカスの戻りを待ってから)。
        await app.IdleAsync();
        state = await app.SendAsync("structState");
        var tabbed = new List<string>();
        for (int i = 0; i < 30 && S(state["focusedId"]) != "StructDiff_Export"; i++)
        {
            state = await app.SendAsync("structTab", new JsonObject { ["shift"] = true });
            await app.IdleAsync();
            state = await app.SendAsync("structState");
            tabbed.Add(S(state["focusedId"]));
        }

        Assert.True(S(state["focusedId"]) == "StructDiff_Export", "Shift+Tab did not reach Export: " + string.Join(", ", tabbed));
        Assert.True((await app.SendAsync("structExportDialog"))["open"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-10")]
    public Task Rows_have_automation_names_with_kind_path_and_values() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, JsonObject state) = await PngAsync(ctx);
        JsonObject width = Rows(state).Single(r => S(r["leftPath"]) == "chunks[0].header.width");
        string name = S(width["accessibleName"]);
        Assert.Contains("Changed", name);
        Assert.Contains("16", name);
        Assert.Contains("32", name);
        Assert.Contains("chunks[0]", name);

        await app.SendAsync("structFocus");
        await app.IdleAsync();
        var element = await app.WaitForAsync(S(width["automationId"]));
        Assert.Contains("Changed", AppSession.NameOf(element));
        Assert.Empty(ViewMiscTests.UnnamedInteractiveElements(app, "structure differences panel"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-32-12")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Hundred_thousand_diffs_keep_the_ui_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-STRUCT-ARR100K-A");
        string b = ctx.TestData("TD-ANA-STRUCT-ARR100K-B");
        string items = ctx.TestData("TD-ANA-STRUCT-ARR-HXT");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await app.SendAsync("compareOpen", new JsonObject { ["left"] = a, ["right"] = b, ["method"] = "simple" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        await app.SendAsync("compareWait", null, Wait);
        JsonObject state = await app.SendAsync("structTemplate", new JsonObject { ["path"] = items, ["rows"] = false, ["timeoutSeconds"] = 600 }, UiTest.Scaled(TimeSpan.FromMinutes(10)));
        Assert.Equal(100_000, L(state["diffCount"]));
        await app.SendAsync("panelShow", new JsonObject { ["id"] = "structDiffs" });
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 手順 4 の計測: キー (← / →) から Hex ビューの画面の更新までを診断の記録で計る。
        await app.SendAsync("keyMeasured", new JsonObject { ["key"] = "Right" });
        await app.IdleAsync();
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        int keys = 0;
        async Task KeyAsync()
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = keys++ % 2 == 0 ? "Right" : "Left" });
            await Task.Delay(50);
        }

        // 1. 一覧を PageDown 100 回分スクロールする (1 回 = 25 行)。
        for (int i = 1; i <= 100; i++)
        {
            await app.SendAsync("structScroll", new JsonObject { ["row"] = i * 25 });
            if (i % 10 == 0)
            {
                await KeyAsync();
            }
        }

        // 2. 文字列 items[9 で絞り込む (500 ms 以内)。
        Task<JsonObject> filtering = app.SendAsync("structFilter", new JsonObject { ["text"] = "items[9", ["rows"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        while (!filtering.IsCompleted)
        {
            await KeyAsync();
        }

        JsonObject filtered = await filtering;
        TimeBudget.Limit(filtered["filterMs"]!.GetValue<double>() <= 500, $"filter: {filtered["filterMs"]} ms (limit 500 ms)");

        // 3. 絞り込みを消し、すべての差分を CSV に書き出す (100,001 行)。
        await app.SendAsync("structFilter", new JsonObject { ["text"] = string.Empty, ["rows"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        string csv = Path.Combine(ctx.Root, "all.csv");
        Task<JsonObject> export = app.SendAsync("structExport", new JsonObject { ["path"] = csv, ["format"] = "csv", ["all"] = true }, UiTest.Scaled(TimeSpan.FromMinutes(5)));
        while (!export.IsCompleted)
        {
            await KeyAsync();
        }

        await export;
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        Assert.Equal(100_001, File.ReadLines(csv).Count());

        IReadOnlyList<double> latencies = FrameLog.Read(log).KeyToFrame();
        Assert.NotEmpty(latencies);
        string shown = string.Join(", ", latencies.Select(x => x.ToString("F1")));
        TimeBudget.Limit(latencies.All(l => l <= 50), $"key to frame: {shown} ms (limit 50 ms)");
        Assert.All(latencies, l => Assert.True(l <= 500, $"key to frame: {shown} ms"));
    }, UiTest.Scaled(TimeSpan.FromMinutes(20)));
}
