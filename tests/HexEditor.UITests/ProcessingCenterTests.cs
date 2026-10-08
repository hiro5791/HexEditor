using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 処理センター (UI-37)。TD-UI-SPARSE-10G と TD-UI-HOOK-SLOW-200M の代わりに、遅い仮想のデータソース (読み込み 1 回 (4 MiB) ごとに
/// 8 ms = 約 500 MB/s) を使う。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ProcessingCenterTests
{
    private const long GiB = 1L << 30;

    private static async Task OpenSlowAsync(AppSession app, string name, long length)
    {
        int before = (await app.StateAsync())["documents"]!.AsArray().Count;
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = name, ["length"] = length, ["content"] = "zero", ["delayMs"] = 8 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["documents"]!.AsArray().Count > before, TimeSpan.FromSeconds(10), name);
        await app.IdleAsync();
    }

    /// <summary>SHA-256 だけを、ファイル全体について計算し始める (解析 > ハッシュ)。</summary>
    private static async Task StartHashAsync(AppSession app)
    {
        await app.SendAsync("hash", new JsonObject { ["action"] = "show" });
        await app.SendAsync("hash", new JsonObject { ["action"] = "algorithms", ["ids"] = new JsonArray("sha256") });
        await app.CommandAsync("Hash_Compute");
        await app.WaitUntilAsync(async () => Running(await app.StateAsync(), "Hash") > 0, TimeSpan.FromSeconds(10), "the hash calculation");
    }

    /// <summary>処理センター (ステータスバーのフライアウト) の文字列をすべてつなげたもの。</summary>
    private static string CenterText(AppSession app) =>
        string.Join('\n', app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)).Select(AppSession.NameOf));

    private static int Running(JsonObject state, string name) =>
        state["operations"]!.AsArray().Count(o => o!["name"]!.GetValue<string>() == name && o["state"]!.GetValue<string>() is "Running" or "Pending");

    [Fact]
    [Trait(UiTest.TC, "TC-UI-37-01")]
    public Task Hash_row_shows_speed_remaining_time_and_cancel() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await OpenSlowAsync(app, "slow-10G", 10 * GiB);

        // 1〜2. SHA-256 の計算を始め、しばらくしてからステータスバーの進捗表示を押して処理センターを開く。
        await StartHashAsync(app);
        await Task.Delay(2500);
        await app.UiaInvokeAsync("Status_Operations");
        await app.WaitForAsync("Operations_Details");
        await Task.Delay(600);

        // 3. 処理名・対象の文書名・進捗率・処理速度・残り時間・経過時間・キャンセルボタン。
        string all = CenterText(app);
        string details = AppSession.NameOf(app.Find("Operations_Details")!);
        Assert.Contains("Hash", all, StringComparison.Ordinal);
        Assert.Contains("slow-10G", all, StringComparison.Ordinal);
        Assert.Matches(@"\d+%", details);
        Assert.Matches(@"[\d.,]+ [KMGT]?B/s", details);
        Assert.Matches(@"About \d+ (min|s) left", details);
        Assert.Matches(@"Elapsed \d+:\d\d", details);
        Assert.NotNull(app.Find("Operations_Cancel"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-37-02")]
    public Task Cancel_stops_the_reads_within_500_ms() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await OpenSlowAsync(app, "slow-10G", 10 * GiB);
        await StartHashAsync(app);
        await Task.Delay(2500);
        await app.UiaInvokeAsync("Status_Operations");
        var cancel = await app.WaitForAsync("Operations_Cancel");

        // 1. キャンセルボタンを押してから読み込みが止まる (処理が終わる) までの時間。
        var watch = Stopwatch.StartNew();
        cancel.Patterns.Invoke.Pattern.Invoke();
        await app.WaitUntilAsync(async () => Running(await app.StateAsync(), "Hash") == 0, TimeSpan.FromSeconds(5), "the cancellation");
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds <= 500, $"the reads stopped {watch.ElapsedMilliseconds} ms after the cancel button");

        // 2. 行の結果が「キャンセル」。
        await app.WaitUntilAsync(() => Task.FromResult(app.Find("Operations_Result") is { } r && AppSession.NameOf(r) == "Cancelled"),
            TimeSpan.FromSeconds(5), "Cancelled in the processing center");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-37-03")]
    public Task Two_operations_run_at_the_same_time() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await OpenSlowAsync(app, "slow-10G", 10 * GiB);
        await OpenSlowAsync(app, "markers-32G", 32 * GiB);

        // 1〜2. 2 つ目のタブで DE AD BE EF をすべて検索し、1 つ目のタブで SHA-256 の計算を始める。ハッシュパネルは表示中のタブを
        //       対象にする (タブを切り替えると計算をやめる。ANA-18) ため、テストケースとは逆の順に始める。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        await SearchResultsTests.OpenFindAsync(app, 0, "DE AD BE EF");
        await SearchResultsTests.FindAllAsync(app);
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await StartHashAsync(app);

        // 3. 1 秒後に、ステータスバーの件数と処理センターの一覧。
        await Task.Delay(1000);
        var seen = new List<string>();
        try
        {
            await app.WaitUntilAsync(async () =>
            {
                JsonObject state = await app.StateAsync();
                string text = state["statusOperationsText"]!.GetValue<string>();
                seen.Add(text + " " + state["operations"]!.ToJsonString());
                return text == "Processing (2)";
            }, TimeSpan.FromSeconds(5), "two operations in the status bar");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\n" + string.Join("\n", seen.Take(3)), ex);
        }
        await app.UiaInvokeAsync("Status_Operations");
        await app.WaitForAsync("Operations_Details");
        await Task.Delay(500);
        Assert.Equal(2, app.Window.FindAllDescendants(cf => cf.ByAutomationId("Operations_Cancel")).Length);
        string all = CenterText(app);
        Assert.Contains("Hash", all, StringComparison.Ordinal);
        Assert.Contains("Find", all, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-37-04")]
    public Task Short_operations_are_not_listed() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-HOOK-SLOW-5M: 1 MiB の読み込みに約 0.2 秒。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-SEQ-1M")],
            Hooks = new JsonObject { ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "*.bin", ["delayMs"] = 200 }) },
        });

        // 1. DE AD BE EF をすべて検索し、開始から完了までの時間。テスト用のビルド (Debug) の初回の検索は JIT の分だけ遅く、遅い CI の
        //    ランナーでは 0.5 秒を超えることがある。0.5 秒未満で終わった検索を得るまで (最大 3 回) 繰り返す。
        await SearchResultsTests.OpenFindAsync(app, 0, "DE AD BE EF");
        var elapsed = new List<double>();
        for (int run = 0; run < 3 && (elapsed.Count == 0 || elapsed[^1] >= 500); run++)
        {
            await SearchResultsTests.FindAllAsync(app);
            await app.WaitUntilAsync(async () => CompletedSearches(await app.StateAsync()).Count > run, TimeSpan.FromSeconds(30), "the search");

            // 完了した処理の記録は新しい順。
            elapsed.Add(CompletedSearches(await app.StateAsync())[0]["elapsedMs"]!.GetValue<double>());
        }

        Assert.True(elapsed[^1] < 500, $"the search took {string.Join(", ", elapsed.Select(e => $"{e:F0}"))} ms");

        // 2. 処理センターの一覧に検索の行がない (実行中・完了のどちらにも)。ステータスバーの進捗表示は出ないので、一覧の中身を
        //    テスト用の命令で読む。0.5 秒を超えた (繰り返す前の) 検索は一覧に出てよいので、その数だけを許す。
        Assert.False((await app.StateAsync())["statusOperationsVisible"]!.GetValue<bool>());
        JsonObject center = await app.SendAsync("processingCenter");
        Assert.Empty(center["running"]!.AsArray());
        Assert.Equal(elapsed.Count(e => e > 500), center["completed"]!.AsArray().Count);
    });

    private static List<JsonObject> CompletedSearches(JsonObject state) =>
        [.. state["operations"]!.AsArray().Select(o => o!.AsObject())
            .Where(o => o["name"]!.GetValue<string>() == "Find all" && o["state"]!.GetValue<string>() == "Completed")];
}
