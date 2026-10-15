using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>
/// スクリプトの実行中の UI の応答 (AUTO-05 の受け入れ基準 3)。テストケースの環境が「性能テスト用の固定の環境」なので、
/// その環境 (HEXEDITOR_PERF_MACHINE=1) でだけ実行する。
/// </summary>
[Trait(UiTest.Category, PerformanceTests.Performance)]
public sealed class ScriptPerformanceTests(ITestOutputHelper output)
{
    private const double InputLatency = 50;

    /// <summary>CPU を使い続ける計算と、アクティブなドキュメントへの transform を 30 秒間繰り返す (TD-AUTO-JS-BUSY)。</summary>
    private const string Busy = """
        const end = Date.now() + 30000;
        let x = 0;
        while (Date.now() < end) {
            for (let i = 0; i < 1e6; i++) { x = (x * 31 + i) | 0; }
            await doc.transform('all', 'xor', '5A');
        }
        console.log('busy done ' + x);
        """;

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-AUTO-05-03")]
    public Task Ui_responds_within_fifty_milliseconds_while_a_script_runs() => UiTestContext.RunAsync(async ctx =>
    {
        string random = ctx.CopyTestData("TD-RANDOM-16M", "random.bin");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string script = ctx.WriteFile("busy.js", System.Text.Encoding.UTF8.GetBytes(Busy));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq, random] });
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();

        // 1. スクリプトを実行する (信頼の確認は「1 回だけ実行する」)。
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Run once");
        await app.WaitUntilAsync(async () => (await app.SendAsync("script", new JsonObject { ["action"] = "running" }))["count"]!.GetValue<int>() == 1,
            TimeSpan.FromSeconds(10), "the script to start");

        // 2. TD-RANDOM-16M のタブで PageDown を 100 回。
        List<double> pageDown = await MeasureAsync(ctx, app, "PageDown");

        // 3. TD-SEQ-1M のタブで Hex の入力を 100 回。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        List<double> typing = await MeasureAsync(ctx, app, "A");
        Assert.Equal(1, (await app.SendAsync("script", new JsonObject { ["action"] = "running" }))["count"]!.GetValue<int>());
        await app.SendAsync("script", new JsonObject { ["action"] = "stopAll" });

        foreach ((string name, List<double> times) in new[] { ("PageDown", pageDown), ("Hex の入力", typing) })
        {
            double p95 = FrameLog.Percentile(times, 0.95);
            output.WriteLine($"{name}: {times.Count} 回、95% {p95:F1} ms、最大 {times.Max():F1} ms");
            Assert.True(p95 <= InputLatency, $"{name}: 95% {p95:F1} ms");
        }
    });

    /// <summary>キーを 100 回、1 回ずつ送り、キー入力から描画完了までの時間を返す。</summary>
    private static async Task<List<double>> MeasureAsync(UiTestContext ctx, AppSession app, string key)
    {
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        await Task.Delay(500);
        for (int i = 0; i < 100; i++)
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = key });
            await Task.Delay(50);
        }

        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        return [.. FrameLog.Read(log).KeyToFrame()];
    }
}
