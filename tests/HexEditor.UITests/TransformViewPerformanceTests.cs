using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>
/// 変換ビュー (EDIT-45) の表示の速さ: 100 GB のファイルで XOR の変換ビューを有効にしたままスクロールしても 60 fps を保つ (TC-EDIT-45-06)。
/// 計測の方法は <see cref="PerformanceTests"/> と同じ (診断表示の記録を集計する)。共有の環境では誤って失敗しうるため、PR の UI テストには含めない。
/// </summary>
[Trait(UiTest.Category, PerformanceTests.Performance)]
public sealed class TransformViewPerformanceTests(ITestOutputHelper output)
{
    private const double FrameP99 = 16.7;
    private const double FrameMax = 50;

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-45-06")]
    public Task Scrolling_a_hundred_gigabyte_file_in_a_xor_view_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });

        // 前提: 鍵 DE AD BE EF の XOR で表示する。
        await app.CommandAsync("Command_TransformViewXor");
        await app.WaitForAsync("TransformXor_Key");
        await app.UiaSetValueAsync("TransformXor_Key", "DE AD BE EF");
        await app.IdleAsync();
        await app.CommandAsync("TransformXor_Ok");
        await app.WaitUntilAsync(async () => (await app.SendAsync("transformView"))["active"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), "the transform view");
        await app.IdleAsync();

        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });
        await Task.Delay(500);

        // 手順 1: PageDown を 30 ms 間隔で 300 回送る。
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            await app.SendAsync("keyMeasured", new JsonObject { ["key"] = "PageDown" });
            int wait = (int)((i + 1) * 30 - watch.ElapsedMilliseconds);
            if (wait > 0)
            {
                await Task.Delay(wait);
            }
        }

        // 手順 2: 先頭から末尾まで 5 秒かけて移る (スクロールバーのつまみのドラッグと同じく、表示位置を連続して大きく変える)。
        const long Length = 100L * 1024 * 1024 * 1024;
        watch.Restart();
        for (int i = 0; i <= 300; i++)
        {
            await app.GoToAsync(Length / 300 * i);
            int wait = (int)((i + 1) * 16 - watch.ElapsedMilliseconds);
            if (wait > 0)
            {
                await Task.Delay(wait);
            }
        }

        // 手順 3: 記録を止めて集計する。60 fps を保つ。
        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        FrameLog frames = FrameLog.Read(log);
        IReadOnlyList<double> intervals = [.. frames.Frames.Select(f => f.Interval)];
        output.WriteLine($"XOR の変換ビューで 100 GiB をスクロール: {FrameLog.Describe(intervals)}");
        Assert.NotEmpty(intervals);
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= FrameP99 && intervals.Max() <= FrameMax, FrameLog.Describe(intervals));
    }, TimeSpan.FromMinutes(5));
}
