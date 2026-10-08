using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>セッションの復元の性能 (UI-31 の「巨大ファイル・長時間処理」、00-overview 11.1 の起動 2 秒)。</summary>
[Trait(UiTest.Category, PerformanceTests.Performance)]
public sealed class SessionPerformanceTests(ITestOutputHelper output)
{
    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-UI-31-02")]
    public Task Twenty_tabs_with_a_hundred_gigabyte_file_restore_within_two_seconds() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-SPARSE-100G (アクティブなタブ) と file01〜file19 の 20 タブを開いて終了した設定フォルダ。
        string folder = Path.Combine(ctx.Root, "files50");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(1, 19).Select(n =>
        {
            string path = Path.Combine(folder, $"file{n:00}.bin");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)n, 4096).ToArray());
            return path;
        }).ToList();
        files.Add(ctx.TestData("TD-SPARSE-100G"));
        AppSession first = await ctx.StartAsync(new AppOptions { Files = files });
        await first.WaitForTabsAsync(20);
        await first.CommandAsync("Command_Exit");
        await first.WaitForExitAsync(TimeSpan.FromSeconds(60));

        var times = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            // 1. プロセスの開始から、アクティブなタブの Hex ビューが描画され、キー入力を受け付けるまで。
            AppSession app = await ctx.StartAsync();
            await app.WaitUntilAsync(async () => (await app.StateAsync())["document"]?["name"]?.GetValue<string>() == "TD-SPARSE-100G.bin"
                && (await app.StateAsync())["hexViews"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(30), "the active tab");
            await app.RenderAsync();
            Assert.Equal("hexView", (await app.KeyAsync("Right"))["handledBy"]!.GetValue<string>());
            times.Add((DateTime.Now - app.Process.StartTime).TotalMilliseconds);

            // 20 タブの見出しが表示されている。
            Assert.Equal(20, (await app.TabNamesAsync()).Count);
            await app.CommandAsync("Command_Exit");
            await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        }

        output.WriteLine($"起動から操作可能まで: {string.Join(", ", times.Select(t => $"{t:F0} ms"))}");
        Assert.All(times, t => Assert.True(t <= 2000, $"{t:F0} ms"));
    });
}
