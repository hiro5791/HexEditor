using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ファイル比較 (ANA-01〜ANA-08)。比較タブ・差分の一覧・「ファイルを比較」ダイアログはテスト用の命令 (compare*) で操作し、状態を読む。
/// テストケースの前提の「--compare A B で起動」(コマンドラインの --compare は F3-08) は、同じ処理を呼ぶテスト用の命令 compareOpen で行う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CompareTests
{
    private static Task<JsonObject> StateAsync(AppSession app) => app.SendAsync("compareState");

    private static Task<JsonObject> WaitAsync(AppSession app) => app.SendAsync("compareWait", null, UiTest.Scaled(TimeSpan.FromSeconds(90)));

    private static async Task<JsonObject> OpenCompareAsync(AppSession app, string left, string right, string method = "simple", JsonObject? more = null)
    {
        JsonObject request = more ?? [];
        request["left"] = left;
        request["right"] = right;
        request["method"] = method;
        await app.SendAsync("compareOpen", request, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        return await WaitAsync(app);
    }

    private static Task<JsonObject> DialogAsync(AppSession app, string action, JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("compareDialog", request);
    }

    private static List<JsonObject> Diffs(JsonObject state, string key = "diffs") => [.. state[key]!.AsArray().Select(d => d!.AsObject())];

    private static long L(JsonNode? node) => node!.GetValue<long>();

    private static async Task<(AppSession App, string A, string B)> Diff3Async(UiTestContext ctx, string method = "insertDelete")
    {
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b, method);
        return (app, a, b);
    }

    // ---- ANA-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-01-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Two_open_tabs_are_compared_in_a_new_tab() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-DIFF3-A"), ctx.TestData("TD-ANA-DIFF3-B")] });

        // 1〜3. 解析 > 比較 > ファイルを比較...、左に A、右に B、既定の方式で「比較」。
        JsonObject dialog = await DialogAsync(app, "open");
        Assert.Equal("TD-ANA-DIFF3-A.bin", dialog["left"]!["targetLabel"]!.GetValue<string>());
        await DialogAsync(app, "set", new JsonObject { ["left"] = new JsonObject { ["target"] = 0 }, ["right"] = new JsonObject { ["target"] = 1 } });
        await DialogAsync(app, "compare");
        JsonObject state = await WaitAsync(app);

        // 新しいタブ「A ↔ B」に Hex ビューが 2 つ並び、差分の一覧パネルに 1 件以上の差分がある。
        Assert.Equal("TD-ANA-DIFF3-A.bin ↔ TD-ANA-DIFF3-B.bin", state["title"]!.GetValue<string>());
        Assert.Contains("TD-ANA-DIFF3-A.bin ↔ TD-ANA-DIFF3-B.bin", state["toolTabs"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("TD-ANA-DIFF3-A.bin", state["left"]!["name"]!.GetValue<string>());
        Assert.Equal("TD-ANA-DIFF3-B.bin", state["right"]!["name"]!.GetValue<string>());
        await app.WaitForAsync("Compare_LeftView");
        await app.WaitForAsync("Compare_RightView");
        Assert.True(state["panelShown"]!.GetValue<bool>());
        Assert.True(L(state["listCount"]) >= 1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-01-02")]
    public Task Different_start_offsets_are_shown_side_by_side() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-SHIFT200-A");
        string b = ctx.TestData("TD-ANA-SHIFT200-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });

        // 1〜3. 左の開始 0x200、右の開始 0 で単純比較。
        await DialogAsync(app, "open");
        await DialogAsync(app, "set", new JsonObject
        {
            ["left"] = new JsonObject { ["target"] = 0, ["start"] = "0x200" },
            ["right"] = new JsonObject { ["target"] = 1, ["start"] = "0" },
            ["method"] = "simple",
        });
        await DialogAsync(app, "compare");
        JsonObject state = await WaitAsync(app);

        // 4. 左の先頭の行が 0x200、右が 0x0 で同じ高さ。差分 0 件、一致率 100.00%。
        Assert.Equal(0x200, L(state["left"]!["topOffset"]));
        Assert.Equal(0x0, L(state["right"]!["topOffset"]));
        Assert.Equal(0, L(state["diffCount"]));
        Assert.Equal(100.00, state["matchPercent"]!.GetValue<double>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-01-04")]
    public Task Swap_exchanges_targets_and_ranges() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-DIFF3-A"), ctx.TestData("TD-ANA-DIFF3-B")] });
        await DialogAsync(app, "open");
        await DialogAsync(app, "set", new JsonObject
        {
            ["left"] = new JsonObject { ["target"] = 0, ["start"] = "0x10", ["length"] = "0x100" },
            ["right"] = new JsonObject { ["target"] = 1, ["start"] = "0x20", ["length"] = "0x200" },
        });

        JsonObject swapped = await DialogAsync(app, "swap");

        JsonNode left = swapped["left"]!;
        JsonNode right = swapped["right"]!;
        Assert.Equal("TD-ANA-DIFF3-B.bin", left["targetLabel"]!.GetValue<string>());
        Assert.Equal("0x20", left["start"]!.GetValue<string>());
        Assert.Equal("0x200", left["length"]!.GetValue<string>());
        Assert.Equal("TD-ANA-DIFF3-A.bin", right["targetLabel"]!.GetValue<string>());
        Assert.Equal("0x10", right["start"]!.GetValue<string>());
        Assert.Equal("0x100", right["length"]!.GetValue<string>());
        Assert.Contains("(0x20)", left["startText"]!.GetValue<string>());
        Assert.Contains("(0x200)", left["lengthText"]!.GetValue<string>());
        Assert.Contains("(0x10)", right["startText"]!.GetValue<string>());
        await DialogAsync(app, "cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-01-05")]
    public Task Last_method_and_options_become_the_defaults() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });

        // 1. 挿入・削除を考慮した比較、再同期ウィンドウ 1M、最小一致長 16 で比較する。
        await DialogAsync(app, "open");
        await DialogAsync(app, "set", new JsonObject
        {
            ["left"] = new JsonObject { ["target"] = 0 },
            ["right"] = new JsonObject { ["target"] = 1 },
            ["method"] = "insertDelete",
            ["resyncWindow"] = "1M",
            ["minMatch"] = "16",
        });
        await DialogAsync(app, "compare");
        await WaitAsync(app);

        static void AssertRemembered(JsonObject dialog)
        {
            Assert.Equal("insertDelete", dialog["method"]!.GetValue<string>());
            Assert.Equal("1048576", dialog["window"]!.GetValue<string>());
            Assert.Equal("16", dialog["minMatch"]!.GetValue<string>());
        }

        // 2. もう一度開く。
        AssertRemembered(await DialogAsync(app, "open"));
        await DialogAsync(app, "cancel");

        // 3. 終了して同じ設定フォルダで起動し直す。
        await Task.Delay(1000);
        try
        {
            await app.SendAsync("exit");
        }
        catch (IOException)
        {
        }

        await app.WaitForExitAsync(TimeSpan.FromSeconds(20));
        AppSession again = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        AssertRemembered(await DialogAsync(again, "open"));
        await DialogAsync(again, "cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-01-06")]
    public Task A_missing_path_disables_compare() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-DIFF3-A")] });
        JsonObject dialog = await DialogAsync(app, "open");

        // 1〜2. 左に A、右に「ファイルを選択...」で存在しないパス。
        int file = dialog["candidates"]!.AsArray().Count - 1;
        JsonObject state = await DialogAsync(app, "set", new JsonObject
        {
            ["left"] = new JsonObject { ["target"] = 0 },
            ["right"] = new JsonObject { ["target"] = file, ["path"] = @"C:\hexed-test\no-such-file.bin" },
        });

        Assert.Equal("The file does not exist.", state["right"]!["error"]!.GetValue<string>());
        Assert.False(state["valid"]!.GetValue<bool>());
        await DialogAsync(app, "cancel");
    });

    [Fact]
    public Task Shift_dropping_two_files_opens_the_compare_dialog_with_both() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-01 の仕様 10: Explorer から 2 つのファイルを Shift を押しながらドロップすると、その 2 つで比較ダイアログを開く。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        JsonObject dialog = await app.SendAsync("compareDrop", new JsonObject
        {
            ["paths"] = new JsonArray(ctx.TestData("TD-ANA-DIFF3-A"), ctx.TestData("TD-ANA-DIFF3-B")),
        });
        Assert.Equal("TD-ANA-DIFF3-A.bin", dialog["left"]!["targetLabel"]!.GetValue<string>());
        Assert.Equal("TD-ANA-DIFF3-B.bin", dialog["right"]!["targetLabel"]!.GetValue<string>());
        await DialogAsync(app, "cancel");
    });

    // ---- ANA-03 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-03-06")]
    public Task Summary_says_approximate_only_for_insert_delete() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        JsonObject state = await StateAsync(app);
        string method = state["summary"]!["method"]!.GetValue<string>();
        Assert.Contains("insertions and deletions", method);
        Assert.Contains("(approximate)", method);

        // 3. ツールバーの方式を単純比較にして再比較する。
        await app.SendAsync("compareMethodBox", new JsonObject { ["index"] = 0 });
        state = await WaitAsync(app);
        Assert.Equal("Simple", state["method"]!.GetValue<string>());
        Assert.DoesNotContain("(approximate)", state["summary"]!["method"]!.GetValue<string>());
    });

    // ---- ANA-04 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-02")]
    public Task Each_kind_of_difference_has_its_own_mark() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);

        async Task<Dictionary<string, JsonObject>> KindsAsync(bool right)
        {
            JsonObject h = await app.SendAsync("compareHighlights", new JsonObject { ["right"] = right });
            return h["segments"]!.AsArray().Select(s => s!.AsObject()).Where(s => s["layer"]!.GetValue<int>() == 11 && s["column"]!.GetValue<string>() == "hex")
                .GroupBy(s => s["tag"]!.GetValue<string>().Split(':')[0]).ToDictionary(g => g.Key, g => g.First());
        }

        async Task CheckAsync(bool highContrast)
        {
            // 変更 (0x100)・挿入 (右の 0x800)・削除 (左の 0xC00) と、揃えるための空白 (左の 0x800、右の 0xC10)。
            await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });
            var left = new Dictionary<string, JsonObject>();
            foreach (long at in new long[] { 0x100, 0x800, 0xC00 })
            {
                await app.GoToAsync(at);
                foreach ((string k, JsonObject v) in await KindsAsync(false))
                {
                    left[k] = v;
                }
            }

            await app.SendAsync("compareFocus", new JsonObject { ["right"] = true });
            await app.GoToAsync(0x800);
            Dictionary<string, JsonObject> right = await KindsAsync(true);

            JsonObject changed = left["diff-changed"];
            JsonObject deleted = left["diff-deleted"];
            JsonObject padding = left["diff-padding"];
            JsonObject inserted = right["diff-inserted"];
            Assert.Equal("1,2", changed["dash"]!.GetValue<string>());
            Assert.NotNull(changed["border"]);
            Assert.Equal("LeftBar", inserted["mark"]!.GetValue<string>());
            Assert.Equal("Strike", deleted["mark"]!.GetValue<string>());
            Assert.Equal("Hatch", padding["mark"]!.GetValue<string>());

            // 背景のリソース名はすべて異なる (下線は差分の印に使わない: 下線の層 6 の印はない)。
            string[] keys = [.. new[] { changed, inserted, deleted, padding }.Select(s => s["tag"]!.GetValue<string>().Split(':')[1])];
            Assert.Equal(4, keys.Distinct().Count());
            if (!highContrast)
            {
                Assert.Equal(4, new[] { changed, inserted, deleted, padding }.Select(s => s["background"]?.GetValue<string>()).Distinct().Count());
            }
        }

        await CheckAsync(highContrast: false);
        await ViewSettingsOps.ForceHighContrastAsync(app);
        await CheckAsync(highContrast: true);
        JsonObject hc = await app.SendAsync("compareHighlights", new JsonObject { ["right"] = true });
        Assert.True(hc["highContrast"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-03")]
    public Task Synchronized_scrolling_follows_the_focused_view() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-SEQ-1M");
        string b = ctx.TestData("TD-ANA-SEQ-1M-MOD");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1〜2. 左で PageDown を 10 回: 右の先頭の行も同じオフセット。
        await app.KeyAsync("PageDown", count: 10);
        JsonObject state = await StateAsync(app);
        long top = L(state["left"]!["topOffset"]);
        Assert.True(top > 0);
        Assert.Equal(top, L(state["right"]!["topOffset"]));

        // 3. 同期をオフにして PageDown を 10 回: 右は動かない。
        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.syncScroll" });
        await app.KeyAsync("PageDown", count: 10);
        state = await StateAsync(app);
        Assert.False(state["sync"]!.GetValue<bool>());
        Assert.Equal(top, L(state["right"]!["topOffset"]));
        Assert.NotEqual(top, L(state["left"]!["topOffset"]));

        // 4. オンに戻すと、フォーカスのある左に合わせる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.syncScroll" });
        state = await StateAsync(app);
        Assert.Equal(L(state["left"]!["topOffset"]), L(state["right"]!["topOffset"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-04")]
    public Task Matching_content_after_an_insertion_is_at_the_same_height() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1. 左のカーソルを 0x810 に置く。
        await app.GoToAsync(0x810);
        JsonObject state = await StateAsync(app);

        // 右のカーソルは 0x820 で、カーソルの行は同じ高さ (画面の行 = カーソルの行 − 一番上の行)。
        Assert.Equal(0x820, L(state["right"]!["cursor"]));
        long leftRow = L(state["left"]!["cursor"]) / 16 - L(state["left"]!["top"]);
        long rightRow = L(state["right"]!["cursor"]) / 16 - L(state["right"]!["top"]);
        Assert.Equal(leftRow, rightRow);

        // 左の 0x800 の位置に、挿入の分の揃えるための空白の印 (斜線の模様)。
        JsonObject h = await app.SendAsync("compareHighlights", new JsonObject { ["right"] = false });
        Assert.Contains(h["segments"]!.AsArray(), s => s!["tag"]!.GetValue<string>().StartsWith("diff-padding", StringComparison.Ordinal)
            && L(s["first"]) == 0x800 && s["mark"]?.GetValue<string>() == "Hatch");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-05")]
    public Task Clicking_the_difference_map_moves_there() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-SEQ-1M");
        string b = ctx.TestData("TD-ANA-SEQ-1M-MOD");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b);

        // 1. 左の差分マップの高さの 75% の位置をクリックする。
        JsonObject state = await app.SendAsync("compareMapClick", new JsonObject { ["right"] = false, ["fraction"] = 0.75 });

        // 2. 左の表示範囲に 0xC0000 付近 (マップの 1 ピクセルの範囲内) が入り、右も同じ位置を表示する。
        long top = L(state["left"]!["topOffset"]);
        long end = top + L(state["left"]!["visibleRows"]) * 16;
        Assert.InRange(0xC0000, top - 0x1000, end + 0x1000);
        Assert.Equal(top, L(state["right"]!["topOffset"]));
        await app.WaitUntilAsync(async () => (await app.SendAsync("compareHighlights", new JsonObject { ["right"] = false }))["mapLines"]!.AsArray().Count == 4,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the difference map");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-06")]
    public Task Editing_in_the_compare_tab_shows_recompare_and_undo_works() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-ANA-DIFF3-A");
        string b = ctx.CopyTestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b, "insertDelete");
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1. 左のカーソルを 0x0 に置き、Hex 列で FF を入力する。
        await app.GoToAsync(0);
        await app.TypeAsync("FF");

        // 2. 比較結果が古くなった InfoBar と「再比較」。
        await app.WaitUntilAsync(async () => (await StateAsync(app))["stale"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the stale bar");
        await app.WaitUntilAsync(() => app.IsShownAsync("Compare_StaleBar"), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the stale bar to be shown");
        await app.WaitForAsync("Compare_StaleRecompare");

        // 3. Ctrl+Z: 左の 0x0 が 00 に戻る。
        await app.KeyAsync("Z", ctrl: true);
        JsonObject state = await StateAsync(app);
        Assert.False(state["left"]!["modified"]!.GetValue<bool>());

        // 4. A のタブでも 0x0 は 00 で、変更済みの印がない。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        Assert.Equal(0x00, (await app.BytesAsync(0, 1))[0]);
        Assert.DoesNotContain("●", (await app.DocumentAsync())["header"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-04-08")]
    public Task Automation_text_includes_the_kind_and_the_other_value() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx, "simple");
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1. 左のカーソルを 0x100、0x0 の順に置く。
        await app.GoToAsync(0x100);
        string atChange = (await StateAsync(app))["left"]!["summary"]!.GetValue<string>();
        Assert.Contains("00000100", atChange);
        Assert.Contains("difference: Changed", atChange);
        Assert.Contains("other side FF", atChange);

        await app.GoToAsync(0);
        string atSame = (await StateAsync(app))["left"]!["summary"]!.GetValue<string>();
        Assert.Contains("difference: Same", atSame);

        // 2. 名前のない操作可能な要素がない (Axe.Windows の規則の 1 つ。Axe.Windows は同梱していない)。
        Assert.Empty(ViewMiscTests.UnnamedInteractiveElements(app, "compare tab"));
    });

    // ---- ANA-05 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-05-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Alt_F5_moves_between_differences_and_selects_both_sides() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });
        await app.GoToAsync(0);

        // 1〜2. Alt+F5: 左右とも 0x100〜0x103 が選択され、「差分 1 / 3」。
        await app.KeyAsync("F5", alt: true);
        JsonObject state = await StateAsync(app);
        Assert.Equal(0x100, L(state["left"]!["selectionStart"]));
        Assert.Equal(4, L(state["left"]!["selectionLength"]));
        Assert.Equal(0x100, L(state["right"]!["selectionStart"]));
        Assert.Equal(4, L(state["right"]!["selectionLength"]));
        Assert.StartsWith("Difference 1 / 3", state["statusCompare"]!.GetValue<string>());

        // 3. Alt+F5: 2 件目 (挿入)。右の 0x800〜0x80F。
        await app.KeyAsync("F5", alt: true);
        state = await StateAsync(app);
        Assert.Equal(0x800, L(state["right"]!["selectionStart"]));
        Assert.Equal(16, L(state["right"]!["selectionLength"]));

        // 4. Shift+Alt+F5: 1 件目に戻る。
        await app.KeyAsync("F5", alt: true, shift: true);
        state = await StateAsync(app);
        Assert.Equal(0, L(state["currentIndex"]));
        Assert.Equal(0x100, L(state["left"]!["selectionStart"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-05-02")]
    public Task Next_after_the_last_difference_returns_to_the_first() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });
        await app.GoToAsync(0);
        await app.KeyAsync("F5", alt: true, count: 3);
        Assert.Equal(2, L((await StateAsync(app))["currentIndex"]));

        // 1〜2. 移動 > 次の差分: 先頭の差分 (0x100) に戻り、その旨が出る。
        await app.CommandAsync("Command_NextDiff");
        JsonObject state = await StateAsync(app);
        Assert.Equal(0x100, L(state["left"]!["selectionStart"]));
        Assert.Equal("Returned to the first difference.", state["statusMessage"]!.GetValue<string>());

        // 3. 3 秒後には消えている。
        await app.WaitUntilAsync(async () => (await StateAsync(app))["statusMessage"]!.GetValue<string>().Length == 0,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the message to disappear");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-05-04")]
    public Task Alt_Left_returns_to_where_the_move_started() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });
        await app.GoToAsync(0x40);

        await app.KeyAsync("F5", alt: true);
        Assert.Equal(0x100, L((await StateAsync(app))["left"]!["cursor"]));
        await app.KeyAsync("Left", alt: true);
        Assert.Equal(0x40, L((await StateAsync(app))["left"]!["cursor"]));
    });

    // ---- ANA-06 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-01")]
    public Task Selecting_a_row_moves_the_compare_view() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);

        // 1. 一覧の 3 行目 (削除) を選ぶ。
        JsonObject state = await app.SendAsync("compareListClick", new JsonObject { ["row"] = 2 });

        Assert.Equal(0xC00, L(state["left"]!["cursor"]));
        Assert.Equal(0xC00, L(state["left"]!["selectionStart"]));
        Assert.Equal(32, L(state["left"]!["selectionLength"]));
        JsonObject row = Diffs(state, "list")[2];
        Assert.Equal("deleted", row["kind"]!.GetValue<string>());
        Assert.Equal(0xC00, L(row["leftOffset"]));
        Assert.Equal(32, L(row["leftLength"]));
        Assert.Equal(0, L(row["rightLength"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-02")]
    public Task Filtering_by_kind_shows_only_that_kind() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        JsonObject state = await app.SendAsync("compareListFilter", new JsonObject { ["kinds"] = new JsonArray("Inserted") });
        Assert.Equal(1, L(state["listCount"]));
        JsonObject row = Diffs(state, "list")[0];
        Assert.Equal("inserted", row["kind"]!.GetValue<string>());
        Assert.Equal(0x800, L(row["rightOffset"]));
        Assert.Equal(16, L(row["rightLength"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-03")]
    public Task Selected_rows_become_a_multiple_selection_of_the_left_document() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _, _) = await Diff3Async(ctx);
        string? before = (await StateAsync(app))["tabs"]![1]!["selectionLength"]!.ToJsonString();

        // 1. 1 行目と 3 行目を Ctrl+クリックで選ぶ。2. 「マルチ選択に変換」の「左」。
        await app.SendAsync("compareListClick", new JsonObject { ["row"] = 0, ["ctrl"] = true });
        await app.SendAsync("compareListClick", new JsonObject { ["row"] = 2, ["ctrl"] = true });
        JsonObject state = await app.SendAsync("compareConvert", new JsonObject { ["to"] = "multi", ["right"] = false });

        // 3. A のマルチ選択が 2 つの範囲 (0x100 長さ 4、0xC00 長さ 32)。マルチ選択の部品 (F2-12) に渡した範囲で確かめる。
        JsonObject multi = state["multiSelection"]!.AsObject();
        Assert.Equal("TD-ANA-DIFF3-A.bin", multi["document"]!.GetValue<string>());
        Assert.Equal([(0x100L, 4L), (0xC00L, 32L)], multi["ranges"]!.AsArray().Select(r => (L(r!["offset"]), L(r["length"]))));

        // B の選択範囲は変わらない。
        Assert.Equal(before, (await StateAsync(app))["tabs"]![1]!["selectionLength"]!.ToJsonString());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-04")]
    public Task Clicking_a_bar_moves_to_the_first_difference_of_the_interval() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-SEQ-1M");
        string b = ctx.TestData("TD-ANA-SEQ-1M-MOD");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        JsonObject opened = await OpenCompareAsync(app, a, b);
        Assert.Equal(512, L(opened["distribution"]));

        JsonObject state = await app.SendAsync("compareGraph", new JsonObject { ["bucket"] = 256 });
        Assert.True(state["moved"]!.GetValue<bool>());
        Assert.Equal(0x80000, L(state["left"]!["cursor"]));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-05")]
    public Task The_graph_can_be_shown_as_a_table() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-SEQ-1M");
        string b = ctx.TestData("TD-ANA-SEQ-1M-MOD");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b);

        JsonObject state = await app.SendAsync("compareGraph", new JsonObject { ["table"] = true });
        string[] rows = [.. state["tableRows"]!.AsArray().Select(r => r!.GetValue<string>())];
        Assert.Equal(512, rows.Length);
        long[] modified = [0x10000, 0x40000, 0x80000, 0xC0000];
        for (int i = 0; i < rows.Length; i++)
        {
            long start = i * 2048L;
            Assert.StartsWith("0x" + start.ToString("X"), rows[i]);
            Assert.EndsWith(modified.Contains(start) ? "0.78%" : "0.00%", rows[i]);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-06-06")]
    public Task A_million_differences_are_exported_to_csv_while_the_ui_keeps_working() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.TestData("TD-ANA-ALT2M-A");
        string b = ctx.TestData("TD-ANA-ALT2M-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        JsonObject opened = await OpenCompareAsync(app, a, b);
        Assert.Equal(1_000_000, L(opened["diffCount"]));
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = false });

        // 1. CSV へのエクスポートを始め、2. 出力中に左のビューで ↓ を押す (キーの処理は止まらない)。
        string csv = Path.Combine(ctx.Root, "diffs.csv");
        await app.SendAsync("compareExport", new JsonObject { ["format"] = "csv", ["path"] = csv, ["noWait"] = true });
        long before = L((await StateAsync(app))["left"]!["cursor"]);
        await app.KeyAsync("Down", count: 50);
        Assert.Equal(before + 50 * 16, L((await StateAsync(app))["left"]!["cursor"]));

        // 3. 終了を待ち、行数と内容を確かめる。
        await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray()
            .Any(o => o!["name"]!.GetValue<string>() == "Export differences" && o["state"]!.GetValue<string>() == "Completed"),
            UiTest.Scaled(TimeSpan.FromSeconds(120)), "the export");
        string[] lines = File.ReadAllLines(csv);
        Assert.Equal(1_000_001, lines.Length);
        Assert.StartsWith("1,changed,1,1,1,1,", lines[1]);
        Assert.StartsWith("1000000,changed,1999999,1,1999999,1,", lines[^1]);
    }, TimeSpan.FromMinutes(15));

    // ---- ANA-07 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-07-03")]
    public Task Copying_all_differences_is_undone_with_one_ctrl_z() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-ANA-DIFF3-A");
        string b = ctx.CopyTestData("TD-ANA-DIFF3-B");
        byte[] original = File.ReadAllBytes(b);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenCompareAsync(app, a, b, "insertDelete");

        // 1. 「比較: すべての差分を右へコピー」: 一覧が空になる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.copyAllRight" });
        await app.WaitUntilAsync(async () => L((await StateAsync(app))["listCount"]) == 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the list to empty");

        // 2. 右にフォーカスを移して Ctrl+Z を 1 回。3. 右の内容が元の B と同じ。
        await app.SendAsync("compareFocus", new JsonObject { ["right"] = true });
        await app.KeyAsync("Z", ctrl: true);
        JsonObject state = await StateAsync(app);
        long length = L(state["right"]!["length"]);
        Assert.Equal(original.Length, length);
        Assert.Equal(SHA256.HashData(original), SHA256.HashData(await app.BytesAsync(0, length)));
    });

    // ---- ANA-08 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-08-02")]
    public Task An_externally_changed_file_is_compared_detecting_insertions() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x100);
        await app.TypeAsync("FF");

        // 1. 外部で 0x8000 に EE を 8 バイト挿入した内容に置き換える (他のエディタの安全な保存と同じ)。
        byte[] changed = [.. original[..0x8000], .. Enumerable.Repeat((byte)0xEE, 8), .. original[0x8000..]];
        string temp = Path.Combine(ctx.Root, "seq.tmp");
        File.WriteAllBytes(temp, changed);
        File.Replace(temp, path, null);

        // 2. 外部変更の通知の「比較」を押す。
        await app.WaitUntilAsync(async () => (await app.NotificationsAsync()).Any(n => n["message"]!.GetValue<string>().Contains("changed by another app", StringComparison.Ordinal)),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), "the external change notice");
        await ExternalChangeTests.PressNoticeButtonAsync(app, "Compare");
        JsonObject state = await WaitAsync(app);

        // 3. 比較方式は挿入・削除を考慮した比較。0x100 の変更と、右から見た 0x8000 の削除 (8 バイト、左 = ディスク上のみ)。
        Assert.Equal("InsertDelete", state["method"]!.GetValue<string>());
        List<JsonObject> diffs = Diffs(state);
        Assert.Contains(diffs, d => d["kind"]!.GetValue<string>() == "changed" && L(d["rightOffset"]) == 0x100);
        Assert.Contains(diffs, d => d["kind"]!.GetValue<string>() == "deleted" && L(d["rightOffset"]) == 0x8000 && L(d["leftLength"]) == 8);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-08-03")]
    public Task Without_edits_no_compare_tab_is_opened() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.CommandAsync("Command_CompareSaved");
        JsonObject state = await StateAsync(app);
        Assert.Equal("Same as the saved content.", state["statusMessage"]!.GetValue<string>());
        Assert.Equal(0, L(state["count"]));
    });

    [Fact]
    public Task Saved_content_comparison_uses_the_pieces_and_names_the_left_side() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x10);
        await app.TypeAsync("AA");
        await app.CommandAsync("Command_CompareSaved");
        JsonObject state = await WaitAsync(app);
        Assert.Equal("seq.bin (saved)", state["left"]!["name"]!.GetValue<string>());
        Assert.Equal([new JsonObject { ["kind"] = "changed", ["leftOffset"] = 0x10, ["leftLength"] = 1, ["rightOffset"] = 0x10, ["rightLength"] = 1 }.ToJsonString()],
            Diffs(state).Select(d => d.ToJsonString()));
    });
}
