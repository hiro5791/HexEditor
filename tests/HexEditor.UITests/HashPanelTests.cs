using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ハッシュパネル (ANA-18、ANA-21、ANA-22)。パネルの状態はテスト用の命令 "hash" と UI オートメーションで読む。大きなファイルの代わりに
/// 仮想のデータソース (内容は種から計算する乱数) を使う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class HashPanelTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    private static Task<JsonObject> HashAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("hash", request, TimeSpan.FromSeconds(60));
    }

    private static Task SelectAlgorithmsAsync(AppSession app, params string[] ids) =>
        HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]) });

    private static async Task<JsonArray> WaitForRowsAsync(AppSession app, int count)
    {
        JsonArray rows = [];
        await app.WaitUntilAsync(async () =>
        {
            JsonObject state = await HashAsync(app);
            rows = state["rows"]!.AsArray();
            return !state["computing"]!.GetValue<bool>() && rows.Count == count;
        }, TimeSpan.FromSeconds(30), $"{count} hash results");
        return rows;
    }

    private static string ValueOf(JsonArray rows, string id) =>
        rows.First(r => r!["id"]!.GetValue<string>() == id)!["value"]!.GetValue<string>();

    private static int HashOperations(JsonObject state) =>
        state["operations"]!.AsArray().Count(o => o!["name"]!.GetValue<string>() == "Hash");

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-18-02")]
    public Task Check9_gives_the_catalogue_crc32_and_md5() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });

        // 1〜2. Ctrl+A ですべてを選択し、解析 > ハッシュ (セットは既定の「よく使う」)。選択範囲の右クリックメニューの
        // 「ハッシュを計算」は Hex ビューのメニューにまだ項目がないため、同じ処理のメニューの項目を使う。
        await app.KeyAsync("A", ctrl: true);
        await app.CommandAsync("Command_Hash");
        JsonArray rows = await WaitForRowsAsync(app, 4);

        // 3. CRC-32 は CRC カタログの CRC-32/ISO-HDLC の check 値、MD5 は RFC 1321 の値。値は UI オートメーションでも読める。
        Assert.Equal(["crc32", "md5", "sha1", "sha256"], rows.Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal("CBF43926", ValueOf(rows, "crc32"));
        Assert.Equal("25F9E794323B453885F5181F1B624D0B", ValueOf(rows, "md5"));
        Assert.Equal("CBF43926", await app.UiaNameAsync("Hash_Value_crc32"));
        Assert.Equal("25F9E794323B453885F5181F1B624D0B", await app.UiaNameAsync("Hash_Value_md5"));
        Assert.Equal("0x0–0x8 (9 bytes)", await app.UiaNameAsync("Hash_Range"));
    });

    [Fact]
    public Task Hash_panel_opens_in_the_right_panel_and_toggles_from_the_view_menu() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });

        // 解析 > ハッシュ: パネルの枠 (UI-05) の右の領域に開く (ANA-18 の「画面」: 既定は右)。
        await app.CommandAsync("Command_Hash");
        await app.WaitUntilAsync(async () => (await HashAsync(app))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the hash panel");
        Assert.Equal("right", (await HashAsync(app))["location"]?.GetValue<string>());
        await app.WaitForAsync("HashPanel");

        // 表示 > パネルの表示切り替え > ハッシュ (view.panel.hash) で閉じ、もう一度で開く。
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.hash" });
        await app.WaitUntilAsync(async () => !(await HashAsync(app))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the hash panel to close");
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.hash" });
        await app.WaitUntilAsync(async () => (await HashAsync(app))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the hash panel to reopen");
    });

    /// <summary>ANA-18 の「呼び出し」: 選択範囲の右クリックメニュー「ハッシュを計算」でハッシュパネルを開く (選択がないときは無効)。</summary>
    [Fact]
    public Task Selection_context_menu_opens_the_hash_panel() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        JsonObject render = await app.RenderAsync();
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(render, 0));
        Assert.False((await app.WaitForAsync("HexViewMenu_ComputeHash")).IsEnabled);
        await app.SendAsync("hideContextMenu");

        await app.SelectAsync(0, 9);
        await ViewOps.RightClickAsync(app, ViewOps.CellPoint(await app.RenderAsync(), 2));
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("HexViewMenu_ComputeHash")).IsEnabled, TimeSpan.FromSeconds(5), "the menu item");
        await app.UiaInvokeAsync("HexViewMenu_ComputeHash");
        await app.WaitUntilAsync(async () => (await HashAsync(app))["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the hash panel");
        await app.WaitForAsync("HashPanel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-18-05")]
    public Task Selections_over_64_MB_are_not_recalculated_automatically() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-ANA-RAND-1G の代わりに、1 GiB の乱数の仮想のデータソース。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "rand-1G", ["length"] = GiB, ["content"] = "random", ["seed"] = 0x1A });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await app.SelectAsync(0, 16);
        await HashAsync(app, "show");
        await WaitForRowsAsync(app, 4);

        // 1. 64 MiB ちょうど: 300 ms 後に計算が始まり、結果が表示される。
        int before = HashOperations(await app.StateAsync());
        await app.SelectAsync(0, 64 * MiB);
        await Task.Delay(1000);
        await app.WaitUntilAsync(async () => HashOperations(await app.StateAsync()) == before + 1, TimeSpan.FromSeconds(5), "the 64 MiB calculation");
        await WaitForRowsAsync(app, 4);
        Assert.False((await HashAsync(app))["highlighted"]!.GetValue<bool>());

        // 2. 64 MiB + 1: 計算は始まらず (処理センターに登録されない)、「計算」ボタンを強調表示する。
        await app.SelectAsync(0, 64 * MiB + 1);
        await Task.Delay(1000);
        JsonObject state = await app.StateAsync();
        Assert.Equal(before + 1, HashOperations(state));
        Assert.True((await HashAsync(app))["highlighted"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-18-06")]
    public Task Hashing_ten_gigabytes_keeps_the_editor_responsive_and_can_be_cancelled() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-ANA-10G-A の代わりに、10 GiB の乱数の仮想のデータソース (書き換えられるもの)。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject
        {
            ["name"] = "rand-10G", ["length"] = 10 * GiB, ["content"] = "random", ["seed"] = 0xA10,
        });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        // 1. SHA-256 だけを選び、ドキュメント全体の計算を始める。
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "sha256");
        await app.CommandAsync("Hash_Compute");
        await app.WaitUntilAsync(async () => (await HashAsync(app))["computing"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the calculation");

        // 2. 計算中に PageDown を 20 回、オフセット 0x100 で A B を入力する。キーから画面の更新までを診断の記録で計る。
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        await Task.Delay(300);
        for (int i = 0; i < 20; i++)
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = "PageDown" });
            await Task.Delay(30);
        }

        await app.GoToAsync(0x100);
        await app.TypeAsync("AB");
        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        Assert.True((await HashAsync(app))["computing"]!.GetValue<bool>(), "the calculation finished before the key presses");
        IReadOnlyList<double> latencies = FrameLog.Read(log).KeyToFrame();
        Assert.Equal(20, latencies.Count);
        Assert.All(latencies, l => Assert.True(l <= 50, $"key to frame: {string.Join(", ", latencies.Select(x => x.ToString("F1")))} ms"));
        Assert.Equal([0xAB], await app.BytesAsync(0x100, 1));

        // 3. 処理センターでキャンセルし、1 秒以内に止まる。結果の表に途中の値は残らない。
        await app.UiaInvokeAsync("Status_Operations");
        var cancel = await app.WaitForAsync("Operations_Cancel");
        var watch = Stopwatch.StartNew();
        cancel.Patterns.Invoke.Pattern.Invoke();
        await app.WaitUntilAsync(async () => !(await HashAsync(app))["computing"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the cancellation");
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds <= 1000, $"stopped {watch.ElapsedMilliseconds} ms after the cancel button");
        JsonObject state = await HashAsync(app);
        Assert.Empty(state["rows"]!.AsArray());
        Assert.Equal("The calculation was canceled.", state["status"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-21-01")]
    public Task Lowercase_and_colon_separated_sha256_match() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        await app.KeyAsync("A", ctrl: true);
        await app.CommandAsync("Command_Hash");
        await WaitForRowsAsync(app, 4);

        const string Lower = "15e2b0d3c33891ebb0f1ef609ec419420c20e320ce94c65fbc8c3312448eb225";
        string colons = string.Join(':', Enumerable.Range(0, 32).Select(i => Lower.Substring(i * 2, 2).ToUpperInvariant()));
        foreach (string expected in new[] { Lower, colons })
        {
            await app.UiaSetValueAsync("Hash_Expected", expected);
            await app.WaitUntilAsync(() => Task.FromResult(app.TextOrEmpty("Hash_MatchText_sha256") == "Match"), TimeSpan.FromSeconds(5), "Match for " + expected);
            Assert.Equal("Visible", (await app.SendAsync("element", new JsonObject { ["id"] = "Hash_MatchIcon_sha256" }))["visibility"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-21-05")]
    public Task Match_and_mismatch_are_shown_with_icons_and_text() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        await app.KeyAsync("A", ctrl: true);
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "crc32", "sha256");
        await HashAsync(app, "compute");
        await WaitForRowsAsync(app, 2);

        // 1〜2. 期待値 CBF43926: CRC-32 の行は一致のアイコンと「一致」、SHA-256 の行は別のアイコンと「不一致」。
        await app.UiaSetValueAsync("Hash_Expected", "CBF43926");
        await app.WaitUntilAsync(() => Task.FromResult(app.TextOrEmpty("Hash_MatchText_crc32") == "Match"), TimeSpan.FromSeconds(5), "the match");
        Assert.Equal("No match", app.TextOrEmpty("Hash_MatchText_sha256"));
        JsonObject icon = await app.SendAsync("element", new JsonObject { ["id"] = "Hash_MatchIcon_crc32" });
        Assert.Equal(("FontIcon", "Visible"), (icon["type"]!.GetValue<string>(), icon["visibility"]!.GetValue<string>()));
        JsonObject other = await app.SendAsync("element", new JsonObject { ["id"] = "Hash_MismatchIcon_sha256" });
        Assert.Equal(("FontIcon", "Visible"), (other["type"]!.GetValue<string>(), other["visibility"]!.GetValue<string>()));
        Assert.Equal("Collapsed", (await app.SendAsync("element", new JsonObject { ["id"] = "Hash_MismatchIcon_crc32" }))["visibility"]!.GetValue<string>());

        // 各行の UI オートメーションの名前は「CRC-32、一致」「SHA-256、不一致」の形 (英語の UI では「CRC-32, Match」)。
        var names = app.Find("Hash_Results")!.FindAllChildren().Select(AppSession.NameOf).ToList();
        Assert.Contains("CRC-32, Match", names);
        Assert.Contains("SHA-256, No match", names);

        // ハイコントラスト: 要素と文字はテーマに依存しない (色は ThemeResource のシステム色に変わるだけ)。OS のハイコントラストの
        // 切り替えは利用者の PC 全体の表示を変えるため、ここでは行わない (ViewTests の TC-VIEW の同じ理由のテストを参照)。
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-22-01")]
    public Task Copying_all_rows_as_json_includes_range_and_parameters() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 前提: 0x100 から 0x400 バイトを選択し、CRC-32、xxHash64 (シード 0x1234)、SHA-256 を計算してある。
        await app.SelectAsync(0x100, 0x400);
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "crc32", "xxh64", "sha256");
        await HashAsync(app, "setParameters", new JsonObject { ["id"] = "xxh64", ["seed"] = 0x1234 });
        await HashAsync(app, "compute");
        JsonArray rows = await WaitForRowsAsync(app, 3);
        Assert.Equal("xxHash64 (seed=0x1234)", rows[1]!["name"]!.GetValue<string>());

        // 1. 結果の表の「すべてコピー」の JSON。2. クリップボード (テストでは代わりのもの) の文字列を JSON として解析する。
        await app.CommandAsync("Hash_CopyAll");
        await app.IdleAsync();
        await app.CommandAsync("Hash_CopyAllJson");
        JsonArray json = JsonNode.Parse((await app.SendAsync("clipboard"))["text"]!.GetValue<string>())!.AsArray();
        Assert.Equal(3, json.Count);
        Assert.All(json, item =>
        {
            Assert.NotNull(item!["algorithm"]);
            Assert.NotNull(item["value"]);
            Assert.Equal(256, item["range"]!["start"]!.GetValue<long>());
            Assert.Equal(1024, item["range"]!["length"]!.GetValue<long>());
        });
        Assert.Equal(4660UL, json[1]!["parameters"]!["seed"]!.GetValue<ulong>());
    });
}
