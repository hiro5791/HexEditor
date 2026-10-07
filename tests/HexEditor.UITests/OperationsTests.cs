using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 長時間処理の進捗表示とキャンセル (ENG-09、FIND-02)、保存中の操作 (ENG-09 の仕様 7、ENG-20)。
/// ハッシュの計算 (F1-17) はフェーズ 1 のため、0.5 秒以上かかる読み取りのみの処理として検索を使う。遅い読み込みは
/// 異常を再現する仕組みの「遅いデータソース」(遅延は読み込み 1 回ごと。検索は 4 MiB ずつ読む) で作る。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed partial class OperationsTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-09-01")]
    public Task Short_operations_show_no_progress() => UiTestContext.RunAsync(async ctx =>
    {
        // 1. 0.5 秒未満で終わる処理 (1 MiB の検索と数え上げ) の間、進捗表示が一度も出ない。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await OpenFindAsync(app, "DE AD BE EF 01");
        var shown = new List<string>();
        var watch = Stopwatch.StartNew();
        await app.UiaInvokeAsync("Find_Next");
        while (watch.ElapsedMilliseconds < 1500)
        {
            JsonObject state = await app.StateAsync();
            if (state["statusOperationsVisible"]!.GetValue<bool>() || state["findBarProgress"]!.GetValue<bool>())
            {
                shown.Add($"{watch.ElapsedMilliseconds} ms: {state["statusOperationsText"]}");
            }

            await Task.Delay(10);
        }

        Assert.Empty(shown);
        Assert.StartsWith("Not found", await app.UiaNameAsync("Find_Status"), StringComparison.Ordinal);

        // 2. 遅いデータソース (読み込み 1 回ごとに 600 ms) の検索は、0.5 秒を過ぎてから進捗表示と処理名が出る。
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-16M", ["length"] = 16 * MiB, ["content"] = "zero", ["delayMs"] = 600 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() == 1, TimeSpan.FromSeconds(10), "the slow tab");
        await OpenFindAsync(app, "DE AD BE EF 01");
        watch.Restart();
        await app.UiaInvokeAsync("Find_Next");
        double? firstShown = null;
        while (watch.ElapsedMilliseconds < 5000 && firstShown is null)
        {
            JsonObject state = await app.StateAsync();
            if (state["statusOperationsVisible"]!.GetValue<bool>())
            {
                firstShown = watch.ElapsedMilliseconds;
                Assert.Equal("Find", state["statusOperationsText"]!.GetValue<string>());
                double elapsed = state["operations"]!.AsArray().First(o => o!["name"]!.GetValue<string>() == "Find")!["elapsedMs"]!.GetValue<double>();
                Assert.True(elapsed >= 500, $"the progress was shown {elapsed} ms after the start");
            }

            await Task.Delay(10);
        }

        Assert.NotNull(firstShown);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-09-03")]
    public Task Cancel_stops_within_200_ms() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-MARKERS-1G の代わりに 1 GiB の仮想のデータソース (読み込み 1 回ごとに 20 ms の遅延)。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-1G", ["length"] = GiB, ["content"] = "zero", ["delayMs"] = 20 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        // ステータスバーの進捗表示のフライアウト (処理センター) の行のキャンセルボタンで、2 回 (別々の処理) 確かめる。
        for (int round = 0; round < 2; round++)
        {
            await OpenFindAsync(app, round == 0 ? "DE AD BE EF" : "CA FE BA BE");
            await app.UiaInvokeAsync("Find_Next");
            await Task.Delay(2000);
            await app.WaitUntilAsync(async () => (await app.StateAsync())["statusOperationsVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the progress");
            await app.UiaInvokeAsync("Status_Operations");
            var cancel = await app.WaitForAsync("Operations_Cancel");
            int before = CancelledCount(await app.StateAsync());

            var watch = Stopwatch.StartNew();
            cancel.Patterns.Invoke.Pattern.Invoke();
            await app.WaitUntilAsync(async () => CancelledCount(await app.StateAsync()) > before, TimeSpan.FromSeconds(5), "the cancellation");
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds <= 200, $"the operation stopped {watch.ElapsedMilliseconds} ms after the cancel button");

            // 処理センターの状態が「キャンセル済み」になる (一覧は 250 ms ごとに更新)。
            await app.WaitUntilAsync(() => Task.FromResult(app.Find("Operations_Result") is { } r && AppSession.NameOf(r) == "Cancelled"),
                TimeSpan.FromSeconds(5), "Cancelled in the processing center");
            await app.CommandAsync("Find_Close");
            await app.IdleAsync();
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-09-05")]
    public Task Typing_while_saving_is_ignored() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-MARKERS-1G の代わりに 32 MiB の目印のファイル。保存の読み込み (4 MiB ずつ) を 300 ms ずつ遅らせる
        // (表示に使う先頭の 4 MiB は遅らせない)。
        string path = UiHelpers.WriteMarkerFile(ctx, "markers-32M.bin", 32 * MiB, MiB);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [path],
            Hooks = SlowFrom(4 * MiB, 300),
        });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("Insert");
        byte before = (await app.BytesAsync(0x10, 1))[0];

        // 1〜2. 保存の進捗表示が出たら Hex 列で 4 1 を入力する。
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["statusOperationsVisible"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the save progress");
        await app.GoToAsync(0x10);
        await app.TypeAsync("41");
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("can't be edited while", StringComparison.Ordinal), "the busy notice");
        Assert.Contains("Save: markers-32M.bin", notice["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // 3. 保存の完了を待つ。0x10 は入力前のままで、「変更あり」ではない。
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(60), "the save");
        Assert.Equal(before, (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(before, File.ReadAllBytes(path)[0x10]);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-20-03")]
    public Task Scrolling_and_searching_while_saving() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-MARKERS-1G の代わりに 64 MiB の目印のファイル (同じ規則)。保存の読み込みのうち 16 MiB より後を 400 ms ずつ遅らせる。
        string path = UiHelpers.WriteMarkerFile(ctx, "markers-64M.bin", 64 * MiB, MiB);
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = SlowFrom(16 * MiB, 400) });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("Home", ctrl: true);

        // 1. 保存を始める。
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the save");

        // 2. 保存中に PageDown を 20 回。表示の先頭が進む。
        long top0 = (await app.DocumentAsync())["topRow"]!.GetValue<long>();
        await app.KeyAsync("PageDown", count: 20);
        long top1 = (await app.DocumentAsync())["topRow"]!.GetValue<long>();
        int visible = (await app.DocumentAsync())["visibleRows"]!.GetValue<int>();
        Assert.True(top1 - top0 >= 19L * (visible - 1), $"the view moved from row {top0} to {top1}");

        // 3. 保存中に検索する。挿入で 1 バイトずれた 0x100000 の目印が 0x100001 で見つかる。
        // (IdleAsync は長時間処理の終わりを待つため使わない。)
        await app.KeyAsync("F", ctrl: true);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });
        (await app.WaitForAsync("Find_Query")).Patterns.Value.Pattern.SetValue("40 30 30 30 30 30 30 30 30 30 30 31");
        await app.WaitUntilAsync(async () => (await app.ElementAsync("Find_Status"))["text"]?.GetValue<string>().StartsWith("40 30", StringComparison.Ordinal) == true,
            TimeSpan.FromSeconds(5), "the query");
        await app.UiaInvokeAsync("Find_Next");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 12, TimeSpan.FromSeconds(10), "the match");
        Assert.Equal(0x100001, (await app.DocumentAsync())["selectionStart"]!.GetValue<long>());
        Assert.True((await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, "the save finished before the search");

        // 4. 保存が成功し、ファイルの内容が保存開始時のドキュメントと同じ。
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(60), "the save");
        Assert.Equal([0x00, .. original], File.ReadAllBytes(path));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-02-01")]
    public Task Search_shows_progress_after_half_a_second() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-EDIT-SPARSE-10G の代わりに 10 GiB の仮想のデータソース (読み込み 1 回 (4 MiB) ごとに 8 ms = 2 ms/MiB)。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-10G", ["length"] = 10 * GiB, ["content"] = "zero", ["delayMs"] = 8 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await OpenFindAsync(app, "DE AD BE EF");

        // 1〜2. 0.3 秒後には進捗バーがなく、0.7 秒後には進捗バーとキャンセルボタンがある。
        var watch = Stopwatch.StartNew();
        await app.UiaInvokeAsync("Find_Next");
        await DelayUntilAsync(watch, 300);
        Assert.False((await app.StateAsync())["findBarProgress"]!.GetValue<bool>(), "the progress bar is shown at 0.3 s");
        Assert.False(await app.IsShownAsync("Find_Cancel"));
        await DelayUntilAsync(watch, 700);
        Assert.True((await app.StateAsync())["findBarProgress"]!.GetValue<bool>(), "the progress bar is not shown at 0.7 s");
        Assert.True(await app.IsShownAsync("Find_Progress"));
        Assert.True(await app.IsShownAsync("Find_Cancel"));

        // 3. 処理センター: 進捗率・処理速度・残り時間・一致件数。表示の更新は 250 ms ごと。
        await app.UiaInvokeAsync("Status_Operations");
        await app.WaitForAsync("Operations_Details");
        var changes = new List<long>();
        string? last = null;
        string text = string.Empty;
        watch.Restart();
        while (watch.ElapsedMilliseconds < 3500)
        {
            text = app.Find("Operations_Details") is { } d ? AppSession.NameOf(d) : string.Empty;
            if (text != last)
            {
                changes.Add(watch.ElapsedMilliseconds);
                last = text;
            }

            await Task.Delay(100);
        }

        Assert.Matches(@"\d+%", text);
        Assert.Matches(@"[\d.,]+ [KMGT]B/s", text);
        Assert.Contains(" left", text, StringComparison.Ordinal);
        Assert.Contains("0 matches", text, StringComparison.Ordinal);
        var intervals = changes.Zip(changes.Skip(1), (a, b) => b - a).Order().ToList();
        Assert.True(intervals.Count >= 5, $"the details changed only {changes.Count} times");
        long median = intervals[intervals.Count / 2];
        Assert.InRange(median, 150, 450);
        await app.UiaInvokeAsync("Find_Cancel");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-02-02")]
    public Task Reads_stop_within_200_ms_of_cancelling() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-SPARSE-100G の代わりに 100 GiB の仮想のデータソース (読み込み 1 回 (4 MiB) ごとに 8 ms)。読み込みの記録は
        // データソースが持つ最後の読み込みの開始時刻。すべて検索 (F1-07) はフェーズ 1 のため、次を検索で確かめる。
        AppSession app = await ctx.StartAsync(new AppOptions { WaitForEditor = false });
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-100G", ["length"] = 100 * GiB, ["content"] = "zero", ["delayMs"] = 8 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");

        foreach (string method in new[] { "button", "processingCenter", "escape" })
        {
            await OpenFindAsync(app, "DE AD BE EF");
            await app.UiaInvokeAsync("Find_Next");
            await Task.Delay(2000);
            Assert.True((await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, $"{method}: the search is not running");

            var clock = Stopwatch.StartNew();
            switch (method)
            {
                case "button":
                    await app.UiaInvokeAsync("Find_Cancel");
                    break;
                case "processingCenter":
                    await app.UiaInvokeAsync("Status_Operations");
                    (await app.WaitForAsync("Operations_Cancel")).Patterns.Invoke.Pattern.Invoke();
                    break;
                default:
                    await app.SendAsync("findKey", new JsonObject { ["key"] = "Escape" });
                    break;
            }

            long cancelledAt = clock.ElapsedMilliseconds;
            await Task.Delay(1000);
            JsonObject stats = await app.SendAsync("sourceStats");
            double lastRead = clock.ElapsedMilliseconds - stats["msSinceLastRead"]!.GetValue<double>();
            Assert.True(lastRead - cancelledAt <= 200, $"{method}: the last read started {lastRead - cancelledAt:F0} ms after the cancel");
            Assert.Equal("Search cancelled.", await app.UiaNameAsync("Find_Status"));
            await app.CommandAsync("Find_Close");
            await app.IdleAsync();
        }
    });

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("ar-SA", "ar")]
    [Trait(UiTest.TC, "TC-ENG-09-06")]
    public Task Speed_and_remaining_time_use_the_regional_format(string culture, string language) => UiTestContext.RunAsync(async ctx =>
    {
        // 地域設定は OS を変えずに、異常を再現する仕組みの「地域設定の上書き」で指定する。時刻の固定は経過時間も止めて
        // 速度が出なくなるため使わず、速度・残り時間の数値の書式 (桁区切り・小数点) を地域設定の書式と比べる。
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            UiLanguage = language,
            WaitForEditor = false,
            Hooks = new JsonObject { ["culture"] = culture },
        });
        Assert.Equal(culture, (await app.StateAsync())["culture"]!.GetValue<string>());
        await app.SendAsync("openVirtual", new JsonObject { ["name"] = "slow-8G", ["length"] = 8 * GiB, ["content"] = "zero", ["delayMs"] = 8 });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the hex view");
        await OpenFindAsync(app, "DE AD BE EF");
        await app.UiaInvokeAsync("Find_Next");

        // 1〜2. 4 秒後 (残り時間が出る) の処理センターの文字列。
        await Task.Delay(4000);
        await app.UiaInvokeAsync("Status_Operations");
        await app.WaitForAsync("Operations_Details");
        await Task.Delay(500);
        string text = AppSession.NameOf(app.Find("Operations_Details")!);
        NumberFormatInfo format = new CultureInfo(culture).NumberFormat;

        // 処理速度: 小数 2 桁のサイズ (StatusFormat.ShortSize と同じ書式)。小数点・数字は地域設定のもの。
        string digits = string.Join(string.Empty, format.NativeDigits);
        Match speed = Regex.Match(text, $"([{Regex.Escape(digits)}0-9{Regex.Escape(format.NumberGroupSeparator)}]+){Regex.Escape(format.NumberDecimalSeparator)}([0-9{Regex.Escape(digits)}]{{2}}) [KMGT]B");
        Assert.True(speed.Success, $"no speed in the regional format ({culture}): {text}");
        double value = double.Parse(speed.Value[..^3], NumberStyles.Number, format);
        Assert.Equal(value.ToString("N2", format), speed.Value[..^3]);

        // 残り時間: 地域設定で書式化した数値 (整数) を含む。
        Match remaining = Regex.Match(text, @"\d+");
        Assert.True(remaining.Success);
        await app.UiaInvokeAsync("Find_Cancel");
    });

    // ---- 補助 ----

    private static int CancelledCount(JsonObject state) =>
        state["operations"]!.AsArray().Count(o => o!["state"]!.GetValue<string>() == "Cancelled");

    /// <summary>検索バーを開き、種類「Hex」で検索語を入れる。</summary>
    internal static async Task OpenFindAsync(AppSession app, string hex)
    {
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });
        await app.UiaSetValueAsync("Find_Query", hex);
    }

    /// <summary>開くファイル (*.bin) の読み込みのうち、<paramref name="from"/> より後を 1 回ごとに <paramref name="delayMs"/> 遅らせる。</summary>
    private static JsonObject SlowFrom(long from, int delayMs) => new()
    {
        ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "*.bin", ["delayMs"] = delayMs, ["delayFromOffset"] = from }),
    };

    private static async Task DelayUntilAsync(Stopwatch watch, int ms)
    {
        int rest = ms - (int)watch.ElapsedMilliseconds;
        if (rest > 0)
        {
            await Task.Delay(rest);
        }
    }
}
