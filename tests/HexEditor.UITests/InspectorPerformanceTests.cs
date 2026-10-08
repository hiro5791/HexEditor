using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>
/// データインスペクタとブックマークの性能テスト (INSP-01、INSP-23、INSP-26)。テストケースの環境が「性能テスト用の固定の環境
/// (テスト方針 6.6)」なので、その環境 (HEXEDITOR_PERF_MACHINE=1) でだけ実行する。
/// </summary>
[Trait(UiTest.Category, PerformanceTests.Performance)]
public sealed class InspectorPerformanceTests(ITestOutputHelper output)
{
    /// <summary>起点が変わってから全行の更新まで (INSP-01 の仕様 3)。</summary>
    private const double UpdateLimit = 50;

    /// <summary>→ を <paramref name="count"/> 回、200 ms 間隔で押し、インスペクタの更新までの時間を返す (表示値が直接の解釈と一致すること)。</summary>
    private static async Task<List<double>> MeasureAsync(AppSession app, int count = 100)
    {
        var times = new List<double>();
        for (int i = 0; i < count; i++)
        {
            await app.SendAsync("inspectorKeyLatency", new JsonObject { ["key"] = "Right" });
            JsonObject? result = null;
            await app.WaitUntilAsync(async () => (result = await app.SendAsync("inspectorLatencyResult"))["pending"] is null,
                TimeSpan.FromSeconds(5), "the inspector to update");
            Assert.True(result!["matches"]!.GetValue<bool>(), $"表示値が直接の解釈と違います (起点 {result["origin"]})");
            times.Add(result["ms"]!.GetValue<double>());
            await Task.Delay(200);
        }

        return times;
    }

    private static async Task ShowAllRowsAsync(AppSession app)
    {
        await InspectorTests.ShowAsync(app);
        await app.UiaInvokeAsync("Inspector_Rows");
        await app.WaitForAsync("Inspector_Preset");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Inspector_Preset", ["index"] = 1 });
        await InspectorTests.PanelKeyAsync(app, "Escape");
        await InspectorTests.WaitForRowsAsync(app);
    }

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-INSP-01-02")]
    public Task All_rows_update_within_fifty_milliseconds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        await ShowAllRowsAsync(app);
        List<double> times = await MeasureAsync(app);
        output.WriteLine($"最大 {times.Max():F1} ms、中央値 {FrameLog.Percentile(times, 0.5):F1} ms");
        Assert.All(times, t => Assert.True(t <= UpdateLimit, $"{t:F1} ms"));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-INSP-01-04")]
    public Task Updating_near_the_end_of_a_hundred_gigabyte_file_is_as_fast() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        await InspectorTests.ShowAsync(app);
        await ViewOps.GoToAsync(app, "end-0x1000");
        await InspectorTests.WaitForRowsAsync(app);
        List<double> nearEnd = await MeasureAsync(app);
        await app.GoToAsync(1L << 32);
        await InspectorTests.WaitForRowsAsync(app);
        List<double> at4G = await MeasureAsync(app);

        AppSession small = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")], Profile = ctx.NewProfile() });
        await InspectorTests.ShowAsync(small);
        List<double> baseline = await MeasureAsync(small);
        double median = FrameLog.Percentile(baseline, 0.5);
        double p95 = FrameLog.Percentile(baseline, 0.95);
        foreach ((string name, List<double> times) in new[] { ("末尾付近", nearEnd), ("2^32", at4G) })
        {
            output.WriteLine($"{name}: 中央値 {FrameLog.Percentile(times, 0.5):F1} ms、95% {FrameLog.Percentile(times, 0.95):F1} ms (基準 {median:F1} / {p95:F1})");
            Assert.True(FrameLog.Percentile(times, 0.5) <= Math.Max(median * 1.2, 1), name);
            Assert.True(FrameLog.Percentile(times, 0.95) <= Math.Max(p95 * 1.2, 1), name);
            Assert.All(times, t => Assert.True(t <= UpdateLimit, $"{name}: {t:F1} ms"));
        }
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-INSP-23-07")]
    public Task Scrolling_with_a_million_bookmarks_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-INSP-BM-1M の読み込み (INSP-30) はフェーズ 2 なので、同じ 100 万件をテスト用の命令の通り道で付ける。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-MARKERS-1G")] });
        await app.SendAsync("bookmarksAdd", new JsonObject { ["count"] = 1_000_000, ["step"] = 1024, ["length"] = 16 }, TimeSpan.FromMinutes(2));
        string log = Path.Combine(ctx.Root, "diagnostics.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        await Task.Delay(500);
        var watch = Stopwatch.StartNew();
        long next = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = "PageDown" });
            next += 33;
            int wait = (int)(next - watch.ElapsedMilliseconds);
            if (wait > 0)
            {
                await Task.Delay(wait);
            }
        }

        await ViewOps.WheelAsync(app, -120, count: 300);
        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        IReadOnlyList<double> intervals = [.. FrameLog.Read(log).Frames.Select(f => f.Interval)];
        output.WriteLine(FrameLog.Describe(intervals));
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= 16.7, FrameLog.Describe(intervals));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-INSP-26-05")]
    public Task Sorting_and_filtering_a_million_bookmarks_take_under_a_second() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-MARKERS-1G")] });
        await app.CommandAsync("Command_ToggleBookmarks");
        await app.SendAsync("bookmarksAdd", new JsonObject { ["count"] = 1_000_000, ["step"] = 1024, ["length"] = 16 }, TimeSpan.FromMinutes(2));
        await app.IdleAsync();

        async Task<double> TimeAsync(Func<Task> action)
        {
            var watch = Stopwatch.StartNew();
            await action();
            await app.IdleAsync();
            return watch.Elapsed.TotalMilliseconds;
        }

        double byName = await TimeAsync(() => app.CommandAsync("Bookmarks_SortName"));
        double byStart = await TimeAsync(() => app.CommandAsync("Bookmarks_SortStart"));
        JsonObject summary = await app.SendAsync("bookmarksSummary");
        Assert.Equal(0, summary["first"]!["start"]!.GetValue<long>());
        Assert.Equal(999_999L * 1024, summary["last"]!["start"]!.GetValue<long>());
        double filter = await TimeAsync(() => app.UiaSetValueAsync("Bookmarks_Filter", "bm99999"));
        summary = await app.SendAsync("bookmarksSummary");
        var names = summary["names"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        List<string> expected = [.. new[] { "bm99999" }.Concat(Enumerable.Range(999_990, 10).Select(k => "bm" + k)).Order(StringComparer.Ordinal)];
        Assert.Equal(expected, names);
        output.WriteLine($"名前順 {byName:F0} ms、開始順 {byStart:F0} ms、絞り込み {filter:F0} ms");
        Assert.All(new[] { byName, byStart, filter }, t => Assert.True(t <= 1000, $"{t:F0} ms"));
    });
}
