using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 比較 (ANA-01・ANA-04・ANA-05・ANA-07・ANA-09) と解析のパネル (06 の 0.1・0.2、ANA-16・ANA-17) の、監査で足した振る舞い。
/// プロセスは偽のプロセス (fakeProcesses) を使い、昇格もしない (補助プロセスの経路は偽の昇格アクセスに置き換える)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CompareAuditTests
{
    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private const int Pid = 4321;

    private static Task<JsonObject> StateAsync(AppSession app) => app.SendAsync("compareState");

    private static Task<JsonObject> WaitAsync(AppSession app) => app.SendAsync("compareWait", null, UiTest.Scaled(TimeSpan.FromSeconds(90)));

    private static long L(JsonNode? node) => node!.GetValue<long>();

    private static async Task<JsonObject> OpenAsync(AppSession app, JsonObject request)
    {
        request["method"] ??= "simple";
        await app.SendAsync("compareOpen", request, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        return await WaitAsync(app);
    }

    /// <summary>偽のプロセス 1 つ (領域 2 つ、1 つはモジュール)。<paramref name="readNeedsElevation"/> なら昇格しないと読めない。</summary>
    private static JsonObject ProcessHooks(UiTestContext ctx, bool readNeedsElevation = false)
    {
        var processes = new JsonObject
        {
            ["elevated"] = false,
            ["processes"] = new JsonArray(new JsonObject
            {
                ["pid"] = Pid,
                ["name"] = "TestTarget.exe",
                ["user"] = "tester",
                ["addressLimit"] = 0x200000,
                ["access"] = "Direct",
                ["currentUser"] = true,
                ["readNeedsElevation"] = readNeedsElevation,
                ["regions"] = new JsonArray(
                    new JsonObject { ["base"] = 0x10000, ["size"] = 0x1000, ["state"] = "Commit", ["protect"] = 2, ["type"] = "Image", ["data"] = "4D5A9000" },
                    new JsonObject { ["base"] = 0x40000, ["size"] = 0x2000, ["state"] = "Commit", ["protect"] = 4, ["type"] = "Private", ["data"] = "00" }),
                ["modules"] = new JsonArray(new JsonObject
                {
                    ["name"] = "TestTarget.exe", ["baseAddress"] = 0x10000, ["size"] = 0x1000, ["path"] = @"C:\TestTarget.exe",
                }),
            }),
        };
        return new JsonObject { ["fakeProcesses"] = ctx.WriteFile("fake-processes.json", Encoding.UTF8.GetBytes(processes.ToJsonString())) };
    }

    /// <summary>スナップショットを作り (名前を決めた後の処理)、できたスナップショットのパスを返す。</summary>
    private static async Task<string> SnapshotAsync(AppSession app, string name)
    {
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.SendAsync("snapshotCreate", new JsonObject { ["name"] = name });
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("snapshotState"))["last"]?.GetValue<string>() == name, Wait, "the snapshot " + name);
        return state["lastPath"]!.GetValue<string>();
    }

    // ---- ANA-01・ANA-09: スナップショットを比較の対象にする ----

    [Fact]
    public Task Snapshots_are_compared_by_region_from_the_dialog_and_as_chosen_files() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = ProcessHooks(ctx) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = Pid });
        string first = await SnapshotAsync(app, "S1");
        await app.SendAsync("processWrite", new JsonObject { ["pid"] = Pid, ["address"] = 0x40010, ["hex"] = "01020304" });
        string second = await SnapshotAsync(app, "S2");

        // 1. ダイアログの対象に「スナップショット...」があり、.hexsnap を選ぶと領域ごとに比べる (ANA-01 の「画面」、ANA-09 の仕様 3・5)。
        JsonObject dialog = await app.SendAsync("compareDialog", new JsonObject { ["action"] = "open" });
        string[] candidates = [.. dialog["candidates"]!.AsArray().Select(c => c!.GetValue<string>())];
        int snapshot = Array.IndexOf(candidates, "Snapshot...");
        Assert.True(snapshot >= 0, string.Join(", ", candidates));
        dialog = await app.SendAsync("compareDialog", new JsonObject
        {
            ["action"] = "set",
            ["left"] = new JsonObject { ["target"] = snapshot, ["path"] = first },
            ["right"] = new JsonObject { ["target"] = snapshot, ["path"] = second },
            ["method"] = "simple",
        });
        Assert.True(dialog["valid"]!.GetValue<bool>(), dialog.ToJsonString());

        // スナップショットでないファイルは選べない。
        string plain = ctx.WriteFile("plain.bin", new byte[64]);
        JsonObject bad = await app.SendAsync("compareDialog", new JsonObject { ["action"] = "set", ["right"] = new JsonObject { ["path"] = plain } });
        Assert.False(bad["valid"]!.GetValue<bool>());
        Assert.Equal("The file is not a process snapshot (.hexsnap).", bad["right"]!["error"]!.GetValue<string>());
        await app.SendAsync("compareDialog", new JsonObject { ["action"] = "set", ["right"] = new JsonObject { ["path"] = second } });
        await app.SendAsync("compareDialog", new JsonObject { ["action"] = "compare" });
        JsonObject state = await WaitAsync(app);
        Assert.True(state["byRegion"]!.GetValue<bool>());
        JsonObject diff = Assert.Single(state["diffs"]!.AsArray())!.AsObject();
        Assert.Equal(0x40010, L(diff["leftOffset"]));
        Assert.Equal(4, L(diff["leftLength"]));

        // 2. 「ファイルを選択...」で .hexsnap を選んでも、生のバイト列ではなく領域ごとに比べる。
        state = await OpenAsync(app, new JsonObject { ["leftFile"] = first, ["rightFile"] = second, ["unit"] = 2 });
        Assert.True(state["byRegion"]!.GetValue<bool>());
        Assert.Equal(1, L(state["countedDiffs"]));

        // 3. 左右の Hex ビューの上部に領域のコンボボックス (ANA-09 の「画面」)。選ぶとその領域の先頭へ移動する。
        JsonObject regions = await app.SendAsync("compareRegions", new JsonObject { ["right"] = false });
        string[] items = [.. regions["regions"]!["items"]!.AsArray().Select(i => i!.GetValue<string>())];
        Assert.Equal(["TestTarget.exe+0x0", "private 0x40000"], items);
        Assert.True(regions["otherRegions"]!["shown"]!.GetValue<bool>());
        regions = await app.SendAsync("compareRegions", new JsonObject { ["right"] = false, ["select"] = 1 });
        Assert.Equal(0x40000, L(regions["left"]!["cursor"]));
        Assert.Equal(1, L(regions["regions"]!["selected"]));
    });

    [Fact]
    public Task Access_denied_snapshot_offers_the_elevated_helper_and_retries() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-09 の「エラー」: 権限不足でプロセスを読めない → 昇格した補助プロセスでの再試行 (偽の昇格アクセス。UAC は出ない)。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = ProcessHooks(ctx, readNeedsElevation: true) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = Pid });
        await app.SendAsync("snapshotCreate", new JsonObject { ["name"] = "Elevated" });
        await app.WaitForAsync("SnapshotElevatedDialog");
        await app.WaitUntilAsync(async () =>
        {
            try
            {
                await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, Wait, "the confirm button");
        JsonObject state = [];
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("snapshotState"))["last"]?.GetValue<string>() == "Elevated", Wait, "the snapshot");
        Assert.True(state["helper"]!.GetValue<bool>());
        Assert.True(File.Exists(state["lastPath"]!.GetValue<string>()));
    });

    // ---- ANA-04 ----

    [Fact]
    public Task External_changes_mark_the_comparison_out_of_date_and_recompare_reads_the_new_content() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-ANA-DIFF3-A");
        string b = ctx.CopyTestData("TD-ANA-DIFF3-A", "b.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a] });
        JsonObject state = await OpenAsync(app, new JsonObject { ["left"] = a, ["rightFile"] = b });
        Assert.Equal(0, L(state["diffCount"]));

        // 1. 比較タブが自分で開いた右のファイルを外部で書き換える: 「再比較」の InfoBar が外部変更の文で出る (ANA-04 の「エラー」)。
        byte[] changed = File.ReadAllBytes(b);
        changed[0x20] ^= 0xFF;
        string temp = Path.Combine(ctx.Root, "b.tmp");
        File.WriteAllBytes(temp, changed);
        File.Replace(temp, b, null);
        await app.WaitUntilAsync(async () => (await StateAsync(app))["staleExternal"]!.GetValue<bool>(), Wait, "the external change");
        await app.WaitUntilAsync(() => app.IsShownAsync("Compare_StaleBar"), Wait, "the stale bar");

        // 2. 再比較で開き直し、新しい内容と比べる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.recompare" });
        state = await WaitAsync(app);
        Assert.False(state["stale"]!.GetValue<bool>());
        JsonObject diff = Assert.Single(state["diffs"]!.AsArray())!.AsObject();
        Assert.Equal(0x20, L(diff["rightOffset"]));

        // 3. 左 (開いているタブ) のファイルを外部で変更しても、比較タブに「再比較」が出る。
        byte[] left = File.ReadAllBytes(a);
        left[0x40] ^= 0xFF;
        File.WriteAllBytes(temp, left);
        File.Replace(temp, a, null);
        await app.WaitUntilAsync(async () => (await StateAsync(app))["staleExternal"]!.GetValue<bool>(), Wait, "the external change of the tab");
    });

    [Fact]
    public Task View_settings_stay_the_same_on_both_sides_after_the_comparison_opens() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-04 の仕様 2: 1 行のバイト数・グループ化・文字コードは左右で共通 (作った後に変えても)。
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenAsync(app, new JsonObject { ["left"] = a, ["right"] = b });

        JsonObject state = await app.SendAsync("compareSideView", new JsonObject { ["right"] = true, ["bytesPerRow"] = 8, ["groupSize"] = 4, ["encoding"] = "utf-8" });
        Assert.Equal(8, L(state["left"]!["bytesPerRow"]));
        Assert.Equal(4, L(state["left"]!["groupSize"]));
        Assert.Equal("utf-8", state["left"]!["encoding"]!.GetValue<string>(), ignoreCase: true);

        state = await app.SendAsync("compareSideView", new JsonObject { ["right"] = false, ["bytesPerRow"] = 32 });
        Assert.Equal(32, L(state["right"]!["bytesPerRow"]));
    });

    [Fact]
    public Task Toolbar_options_change_the_unit_and_merge_gap_and_compare_again() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-04 の「画面」の「オプション」: 比較の単位・近い差分をまとめる・W・M を変えて再比較する。値は次回の既定にもなる。
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenAsync(app, new JsonObject { ["left"] = a, ["right"] = b, ["method"] = "insertDelete" });

        JsonObject rejected = await app.SendAsync("compareOptions", new JsonObject { ["resyncWindow"] = "16", ["apply"] = true });
        Assert.False(rejected["applied"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(rejected["inputs"]!["error"]!.GetValue<string>()));
        Assert.True(rejected["inputs"]!["insertDeleteVisible"]!.GetValue<bool>());

        JsonObject applied = await app.SendAsync("compareOptions", new JsonObject
        {
            ["unit"] = 4, ["mergeGap"] = "16", ["resyncWindow"] = "4096", ["minMatch"] = "4", ["apply"] = true,
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        Assert.True(applied["applied"]!.GetValue<bool>());
        JsonObject state = await WaitAsync(app);
        JsonNode options = state["options"]!;
        Assert.Equal((4L, 16L, 4096L, 4L), (L(options["unit"]), L(options["mergeGap"]), L(options["window"]), L(options["minMatch"])));
        Assert.Equal("insertDelete", options["method"]!.GetValue<string>());
        Assert.Equal(16, L(applied["remembered"]!["mergeGap"]));
    });

    // ---- ANA-05・ANA-07 ----

    [Fact]
    public Task Moving_from_a_normal_tab_updates_the_current_difference() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-05 の仕様 6: 通常のタブから移動しても、比較タブの「差分 n / N」と次の移動の基準がその差分になる。
        string a = ctx.TestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a, b] });
        await OpenAsync(app, new JsonObject { ["left"] = a, ["right"] = b, ["method"] = "insertDelete" });
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.GoToAsync(0);

        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.nextDiff" });
        JsonObject state = await StateAsync(app);
        Assert.Equal(0, L(state["currentIndex"]));
        Assert.StartsWith("Difference 1 / 3", state["statusText"]!.GetValue<string>(), StringComparison.Ordinal);

        await app.SendAsync("execute", new JsonObject { ["id"] = "compare.nextDiff" });
        Assert.Equal(1, L((await StateAsync(app))["currentIndex"]));
    });

    [Fact]
    public Task Copy_items_in_the_difference_list_are_disabled_when_the_target_is_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-07 の「エラー」: 書き込み先が読み取り専用ならメニュー項目を無効にし、ツールチップに「右側は読み取り専用です」。
        string a = ctx.CopyTestData("TD-ANA-DIFF3-A");
        string b = ctx.TestData("TD-ANA-DIFF3-B");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a] });
        await OpenAsync(app, new JsonObject { ["left"] = a, ["rightFile"] = b, ["method"] = "insertDelete" });
        await app.SendAsync("compareListClick", new JsonObject { ["row"] = 0 });

        JsonObject menu = (await app.SendAsync("compareListMenu"))["menu"]!.AsObject();
        Assert.False(menu["DiffListMenu_CopyRight"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("The right side is read-only.", menu["DiffListMenu_CopyRight"]!["toolTip"]!.GetValue<string>());
        Assert.True(menu["DiffListMenu_CopyLeft"]!["enabled"]!.GetValue<bool>());
        Assert.True(menu["DiffListMenu_ExportCsv"]!["enabled"]!.GetValue<bool>());
    });

    // ---- 06 の 0.1・0.2、ANA-16・ANA-17 ----

    [Fact]
    public Task Pattern_and_classification_tabs_say_when_the_results_are_out_of_date() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-ANA-DIFF3-A");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.SendAsync("stats", new JsonObject { ["action"] = "show", ["tab"] = "Pattern" });
        await app.SendAsync("stats", new JsonObject { ["action"] = "patterns" }, TimeSpan.FromSeconds(120));
        await app.SendAsync("stats", new JsonObject { ["action"] = "classify" }, TimeSpan.FromSeconds(120));
        JsonObject state = await app.SendAsync("stats");
        Assert.False(state["patternStale"]!.GetValue<bool>());
        Assert.False(state["classifyStale"]!.GetValue<bool>());

        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.GoToAsync(0);
        await app.TypeAsync("FF");
        await app.WaitUntilAsync(async () =>
        {
            JsonObject s = await app.SendAsync("stats");
            return s["patternStale"]!.GetValue<bool>() && s["classifyStale"]!.GetValue<bool>();
        }, Wait, "the stale notices");
    });

    [Fact]
    public Task Classification_uses_the_file_type_and_is_drawn_on_the_minimap() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-17 の仕様 7: 判定したファイル形式を分類に渡す。ANA-16 の仕様 7: 分類をミニマップに「分類」レイヤとして重ねる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-PNG-AS-JPG")] });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fileType"))["status"]!.GetValue<string>().Contains("PNG", StringComparison.Ordinal), Wait,
            "the file type");
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.minimap" });
        await app.SendAsync("stats", new JsonObject { ["action"] = "show", ["tab"] = "Classify" });
        await app.SendAsync("stats", new JsonObject { ["action"] = "classify" }, TimeSpan.FromSeconds(120));
        JsonObject stats = await app.SendAsync("stats");
        Assert.Contains("PNG", stats["classifyFileType"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject minimap = [];
        try
        {
            await app.WaitUntilAsync(async () => (minimap = await app.SendAsync("minimap"))["classes"]!.AsArray().Count > 0, Wait, "the classification layer");
        }
        catch (TimeoutException)
        {
            minimap.Remove("rows");
            throw new TimeoutException(minimap.ToJsonString());
        }
        JsonArray classes = (await app.SendAsync("minimap"))["classes"]!.AsArray();
        Assert.All(classes, c => Assert.NotEqual("None", c!["class"]!.GetValue<string>()));
    });

    [Fact]
    public Task Embedded_formats_use_the_common_target_selector() => UiTestContext.RunAsync(async ctx =>
    {
        // ANA-17 の仕様 6 と 06 の 0.1: 「埋め込まれた形式を探す」は共通の対象範囲で探し、実際の範囲を表示する。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-PE-PNG")] });
        JsonObject state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embeddedTarget" });
        Assert.Equal(2, L(state["embeddedTarget"]));

        state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embeddedTarget", ["target"] = 3, ["start"] = "0x1000", ["length"] = "0x1FFF", ["usesEnd"] = true });
        Assert.Equal("0x1000–0x1FFF (4,096 bytes)", state["embeddedRange"]!.GetValue<string>());
        state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embedded" }, TimeSpan.FromSeconds(120));
        Assert.DoesNotContain(state["embedded"]!.AsArray(), e => e!["name"]!.GetValue<string>().Contains("PNG", StringComparison.Ordinal));

        state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embeddedTarget", ["target"] = 2 });
        state = await app.SendAsync("fileType", new JsonObject { ["action"] = "embedded" }, TimeSpan.FromSeconds(120));
        Assert.Contains(state["embedded"]!.AsArray(), e => L(e!["offset"]) == 0x2000 && e["name"]!.GetValue<string>().Contains("PNG", StringComparison.Ordinal));
    });
}
