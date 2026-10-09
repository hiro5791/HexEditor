using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FlaUI.Core.Definitions;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>検索バー (FIND-04)、検索語の長さ (FIND-05)、件数 (FIND-12)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FindBarTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-FIND-04-02")]
    public Task Selection_is_copied_into_the_query() => UiTestContext.RunAsync(async ctx =>
    {
        // 範囲を選択 (Ctrl+E。F1-11) はフェーズ 1 のため、同じ選択を命令の通り道で作る。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });

        // 1〜2. 0x41 から 4 バイト: Hex の文字列が入る。
        await SelectAndOpenAsync(app, 0x41, 4);
        Assert.Equal("41 42 43 44", await QueryAsync(app));
        Assert.Equal(0, (await app.ElementAsync("Find_Kind"))["selectedIndex"]!.GetValue<int>());

        // 3. 256 バイト: 256 バイトの Hex。範囲は「ドキュメント全体」。
        await app.UiaInvokeAsync("Find_Close");
        await SelectAndOpenAsync(app, 0, 256);
        string all = string.Join(' ', Enumerable.Range(0, 256).Select(i => i.ToString("X2")));
        Assert.Equal(all, await QueryAsync(app));
        Assert.Equal(0, (await app.ElementAsync("Find_Scope"))["selectedIndex"]!.GetValue<int>());

        // 4. 257 バイト: 検索欄は変わらず、範囲は「選択範囲」。
        await app.UiaInvokeAsync("Find_Close");
        await SelectAndOpenAsync(app, 0, 257);
        Assert.Equal(all, await QueryAsync(app));
        Assert.Equal(1, (await app.ElementAsync("Find_Scope"))["selectedIndex"]!.GetValue<int>());

        // 5. 種類を「テキスト」(ASCII) にしてから 0x41 から 4 バイト: ABCD。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "ASCII (7 bit)" });
        await app.UiaInvokeAsync("Find_Close");
        await SelectAndOpenAsync(app, 0x41, 4);
        Assert.Equal("ABCD", await QueryAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-04-04")]
    public Task Editing_while_the_find_bar_is_open() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });
        await OperationsTests.OpenFindAsync(app, "20 21");

        // 1. Hex ビューの 0x80 をクリックする (クリックと同じ処理と、クリックで移るフォーカス)。
        await app.SendAsync("click", new JsonObject { ["offset"] = 0x80 });
        await app.FocusAsync("editor", keyboard: false);

        // 2〜3. FF を入力する。0x80 は FF、検索バーは開いたまま、フォーカスは Hex ビュー。
        await app.TypeAsync("FF");
        Assert.Equal(0xFF, (await app.BytesAsync(0x80, 1))[0]);
        JsonObject state = await app.StateAsync();
        Assert.True(state["findBarVisible"]!.GetValue<bool>());
        Assert.StartsWith("HexView", state["focused"]!.GetValue<string>(), StringComparison.Ordinal);

        // 4〜5. 「次を検索」: 0x80 より後の一致 (0x120、長さ 2)。
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 2, TimeSpan.FromSeconds(10), "the match");
        Assert.Equal(0x120, (await app.DocumentAsync())["selectionStart"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-04-05")]
    public Task Search_results_are_announced_to_screen_readers() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        var received = new ConcurrentQueue<string>();
        using var handler = app.Window.RegisterNotificationEvent(TreeScope.Subtree,
            (_, _, _, text, activityId) => received.Enqueue($"{activityId}|{text}"));

        // 1〜2. 見つかった位置 (0x10) を含む通知が 1 回。
        await OperationsTests.OpenFindAsync(app, "10 11 12");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(() => Task.FromResult(received.Any(r => r.StartsWith("FindResult|", StringComparison.Ordinal))), TimeSpan.FromSeconds(10), "the notification");
        await Task.Delay(300);
        string found = Assert.Single(received, r => r.StartsWith("FindResult|", StringComparison.Ordinal));
        Assert.Contains("0x10", found, StringComparison.Ordinal);

        // 3〜4. 見つからなかったことを伝える通知が 1 回。検索欄の下の表示と警告色の枠。ダイアログは出ない。
        received.Clear();
        await app.UiaSetValueAsync("Find_Query", "10 12 14");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(() => Task.FromResult(received.Any(r => r.StartsWith("FindResult|", StringComparison.Ordinal))), TimeSpan.FromSeconds(10), "the notification");
        await Task.Delay(300);
        string notFound = Assert.Single(received, r => r.StartsWith("FindResult|", StringComparison.Ordinal));
        Assert.Contains("Not found", notFound, StringComparison.Ordinal);
        Assert.StartsWith("Not found", await app.UiaNameAsync("Find_Status"), StringComparison.Ordinal);
        Assert.True((await app.ElementAsync("Find_Query"))["cautionBorder"]!.GetValue<bool>());
        Assert.Null(app.Find("SaveDialog"));
        Assert.DoesNotContain(app.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.Window)), w => AppSession.AllText(w).Contains("Not found", StringComparison.Ordinal));
    });

    [Fact(Skip = "IME の変換中の文字列は、日本語 IME へのシステムのキー入力 (n・i・h・o・n・Space) でしか作れない。作業中の PC のキーボードを使わないため自動では実行しない (CI の専用の環境が必要)。「入力しながら検索」(FIND-27) は TextBox の TextCompositionStarted〜Ended の間は検索を始めない (FindBar.Incremental.cs)")]
    [Trait(UiTest.TC, "TC-FIND-04-07")]
    public Task Ime_composition_does_not_start_a_search() => Task.CompletedTask;

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-05-03")]
    public Task One_mebibyte_query() => UiTestContext.RunAsync(async ctx =>
    {
        // クリップボードは使わない。貼り付けの代わりに、同じ文字列を UI オートメーションの ValuePattern で入れる。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        byte[] data = new byte[0x100001];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0x100000, data);
        string exact = Convert.ToHexString(data, 0, 0x100000);
        Assert.Equal(2_097_152, exact.Length);

        // 2〜3. 1 MiB ちょうど: エラーはなく、0x100000 から 1,048,576 バイトが選択される。
        await OperationsTests.OpenFindAsync(app, exact);
        Assert.False((await app.ElementAsync("Find_Query"))["errorBorder"]!.GetValue<bool>());
        Assert.True((await app.WaitForAsync("Find_Next")).IsEnabled);
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 0x100000, TimeSpan.FromSeconds(60), "the match");
        Assert.Equal(0x100000, (await app.DocumentAsync())["selectionStart"]!.GetValue<long>());

        // 4. 1,048,577 バイト: 1 MiB までのエラーで、検索ボタンは無効。
        await app.UiaSetValueAsync("Find_Query", Convert.ToHexString(data));
        Assert.True((await app.ElementAsync("Find_Query"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("at most 1 MiB", await app.UiaNameAsync("Find_Status"), StringComparison.Ordinal);
        Assert.False((await app.WaitForAsync("Find_Next")).IsEnabled);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-12-01")]
    public Task Count_and_current_match_number() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1000.bin", Hits1000())] });

        // 1〜2. 件数の数え上げが終わると「1 / 1,000」(件数は地域設定の桁区切りで書く)。
        await OperationsTests.OpenFindAsync(app, "12 34 56 78");
        await app.UiaInvokeAsync("Find_Next");
        await WaitForCountAsync(app, "1 / 1,000");

        // 3. 次を検索のたびに番号が進む。
        await app.UiaInvokeAsync("Find_Next");
        await WaitForCountAsync(app, "2 / 1,000");
        await app.UiaInvokeAsync("Find_Next");
        await WaitForCountAsync(app, "3 / 1,000");

        // 4. 画面に見えている一致 (0x0、0x400 など) が強調表示されている (描画内容の読み出し)。
        await app.GoToAsync(0);
        JsonObject render = await app.RenderAsync();
        JsonObject row0 = render["rows"]!.AsArray().Select(r => r!.AsObject()).Single(r => r["offsetText"]!.GetValue<string>().EndsWith("00000000", StringComparison.Ordinal));
        Assert.All(row0["cells"]!.AsArray().Take(4), c => Assert.True(c!["matched"]!.GetValue<bool>()));
        Assert.False(row0["cells"]![4]!["matched"]!.GetValue<bool>());

        // 色だけで伝えない (UI-28 の仕様 7): 一致は Hex 列とテキスト列の両方で枠線でも囲む。
        string[] borders = [.. row0["lines"]!.AsArray().Select(l => l!["kind"]!.GetValue<string>())];
        Assert.Contains("matchBorder", borders);
        Assert.Contains("matchBorderText", borders);
        JsonObject border = row0["lines"]!.AsArray().Select(l => l!.AsObject()).First(l => l["kind"]!.GetValue<string>() == "matchBorder");
        Assert.Equal(row0["cells"]![0]!["hexLeft"]!.GetValue<double>(), border["x1"]!.GetValue<double>(), 2);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-12-02")]
    public Task Ranges_over_1_GiB_are_not_counted_automatically() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-FIND-SPARSE-2G")] });

        // 1〜2. 2 GiB: 件数は数えず、「件数を数える」ボタンがある。
        await OpenTextFindAsync(app, "@000");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => await app.IsShownAsync("Find_CountButton"), TimeSpan.FromSeconds(10), "the count button");
        Assert.False(await app.IsShownAsync("Find_Count") && (await app.ElementAsync("Find_Count"))["text"]!.GetValue<string>().Contains("2,049", StringComparison.Ordinal));

        // 3〜4. ボタンを押すと「1 / counting...」、終わると「1 / 2,049」。
        await app.UiaInvokeAsync("Find_CountButton");
        await app.WaitUntilAsync(async () => (await CountTextAsync(app)) is "1 / counting..." or "1 / 2,049", TimeSpan.FromSeconds(10), "counting");
        await WaitForCountAsync(app, "1 / 2,049");

        // 5. ちょうど 1 GiB の TD-MARKERS-1G は自動で数える: 「1 / 1,025」。
        await app.UiaInvokeAsync("Find_Close");
        await app.OpenAsync(ctx.TestData("TD-MARKERS-1G"));
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(10), "the second tab");
        await OpenTextFindAsync(app, "@000");
        await app.UiaInvokeAsync("Find_Next");
        await WaitForCountAsync(app, "1 / 1,025");
        Assert.False(await app.IsShownAsync("Find_CountButton"));
    });

    // ---- 補助 ----

    private static async Task SelectAndOpenAsync(AppSession app, long start, long length)
    {
        await app.SelectAsync(start, length);
        Assert.Equal("menu:Command_Find", (await app.KeyAsync("F", ctrl: true))["handledBy"]!.GetValue<string>());
        await app.IdleAsync();
    }

    private static async Task<string> QueryAsync(AppSession app) => (await app.ElementAsync("Find_Query"))["text"]!.GetValue<string>();

    private static async Task<string> CountTextAsync(AppSession app) => (await app.ElementAsync("Find_Count"))["text"]?.GetValue<string>() ?? string.Empty;

    private static Task WaitForCountAsync(AppSession app, string expected) =>
        app.WaitUntilAsync(async () => await CountTextAsync(app) == expected, TimeSpan.FromSeconds(60), $"the count '{expected}'");

    /// <summary>検索バーを開き、種類「テキスト」(ASCII) で検索語を入れる。</summary>
    private static async Task OpenTextFindAsync(AppSession app, string text)
    {
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "ASCII (7 bit)" });
        await SearchResultsTests.DisableIncrementalAsync(app);
        await app.UiaSetValueAsync("Find_Query", text);
    }

    /// <summary>TD-FIND-HITS-1000: すべて 00 の中に、k × 1,024 (k = 0〜999) から `12 34 56 78`。</summary>
    private static byte[] Hits1000()
    {
        byte[] data = new byte[1024 * 1024];
        for (int k = 0; k < 1000; k++)
        {
            new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(data, k * 1024);
        }

        return data;
    }
}
