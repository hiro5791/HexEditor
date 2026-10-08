using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>異常を再現する仕組み (遅いデータソース・読み込みエラー。テスト方針 7.2) を使う表示のテスト (VIEW-03、ENG-06)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DataSourceHookTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-03-01")]
    public Task Slow_source_shows_placeholders_then_data() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-VIEW-HOOK-SLOW2000: 読み込み要求ごとに 2,000 ms の遅延 (先頭 64 KiB を除く)。
        string path = ctx.TestData("TD-SEQ-1M");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-SEQ-1M.bin", ["delayMs"] = 2000, ["delayFromOffset"] = 65536 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });
        Assert.Equal("FaultyByteSource", (await app.DocumentAsync())["sourceType"]!.GetValue<string>());

        // 1〜2. 0x80000 に移動して 200 ms 後: 読み込み中の仮表示。
        JsonArray normal = (await app.RenderAsync())["rows"]![0]!["cells"]!.AsArray();
        await app.GoToAsync(0x80000);
        await Task.Delay(200);
        JsonObject row = await RowAsync(app, "00080000");
        foreach (JsonNode? cell in row["cells"]!.AsArray())
        {
            Assert.Equal("··", cell!["hex"]!.GetValue<string>());
            Assert.Equal(" ", cell["text"]!.GetValue<string>());
            // オフセット 0 の 00 は薄い色 (VIEW-13) なので、通常の文字色はオフセット 1 のセルで比べる。
            Assert.NotEqual(normal[1]!["foreground"]!.GetValue<string>(), cell["foreground"]!.GetValue<string>());
        }

        // 3. 読み込み完了の 500 ms 後: 本当の値に置き換わる。
        await Task.Delay(2300);
        await app.IdleAsync();
        row = await RowAsync(app, "00080000");
        JsonArray cells = row["cells"]!.AsArray();
        for (int c = 0; c < 16; c++)
        {
            Assert.Equal(((0x80000 + c) % 256).ToString("X2"), cells[c]!["hex"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-VIEW-03-04")]
    public Task Unreadable_range_is_drawn_as_question_marks() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-VIEW-HOOK-READERR: 0x1000〜0x1FFF の読み込みを ERROR_IO_DEVICE にする。
        // 手順 3〜5 (ツールチップ・ステータスバーの表示・InfoBar を出さないこと) は対象外: 描画 (手順 1〜2) だけを確かめる。
        // 現在の版は「読み取れない範囲があります」をステータスバーではなくタブの通知 (InfoBar) で出す (VIEW-03 と違う)。
        string path = ctx.TestData("TD-SEQ-1M");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject
            {
                ["match"] = "TD-SEQ-1M.bin",
                ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x1000", ["length"] = "0x1000" }),
            }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });
        await app.GoToAsync(0xFF0);
        await app.IdleAsync();

        JsonArray good = (await RowAsync(app, "00000FF0"))["cells"]!.AsArray();
        for (int c = 0; c < 16; c++)
        {
            Assert.Equal(((0xFF0 + c) % 256).ToString("X2"), good[c]!["hex"]!.GetValue<string>());
        }

        foreach (JsonNode? cell in (await RowAsync(app, "00001000"))["cells"]!.AsArray())
        {
            Assert.Equal("??", cell!["hex"]!.GetValue<string>());
            Assert.Equal(" ", cell["text"]!.GetValue<string>());

            // 背景に斜線の模様を重ねる (色だけに頼らない。VIEW-03 の仕様 5)。
            Assert.True(cell["hatched"]!.GetValue<bool>());
        }

    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-06-02")]
    public Task Slow_source_does_not_block_the_ui() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.TestData("TD-RANDOM-16M");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "TD-RANDOM-16M.bin", ["delayMs"] = 2000 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });

        // UI スレッドの応答を、2 本目の命令の通り道で 10 ms ごとに確かめる。
        using TestChannelClient monitor = (await TestChannelClient.ConnectAsync(app.Pid, () => app.Process.HasExited, TimeSpan.FromSeconds(10)))!;
        using var stop = new CancellationTokenSource();
        double worst = 0;
        var total = Stopwatch.StartNew();
        var slow = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await monitor.SendAsync("ping");
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                if (sw.Elapsed.TotalMilliseconds > 50)
                {
                    slow.Enqueue($"{total.ElapsedMilliseconds - sw.ElapsedMilliseconds}ms: {sw.ElapsedMilliseconds}ms");
                }
                await Task.Delay(10);
            }
        });

        slow.Enqueue($"keys start {total.ElapsedMilliseconds}");
        for (int i = 0; i < 100; i++)
        {
            await app.KeyAsync("PageDown");
            await Task.Delay(10);
        }

        slow.Enqueue($"keys end {total.ElapsedMilliseconds}");
        await app.KeyAsync("End", ctrl: true);
        await app.KeyAsync("Home", ctrl: true);

        // まだ読み込んでいない位置は、読み込み中の状態で描かれる。
        // 仮表示は 100 ms を超えた読み込みにだけ出す (VIEW-03 の仕様) ので、200 ms 待ってから読む。
        await app.GoToAsync(8L * 1024 * 1024);
        await Task.Delay(200);
        bool loading = (await app.RenderAsync())["rows"]!.AsArray().Any(r => r!["cells"]!.AsArray().Any(c => c!["hex"]!.GetValue<string>() == "··"));
        await app.KeyAsync("Home", ctrl: true);
        await Task.Delay(5000);
        await stop.CancelAsync();
        await watch;

        // 命令の往復を含むため、判定の 50 ms に通り道の往復の余裕を足す。
        Assert.True(worst < 100, $"UI thread blocked for {worst:F0} ms: {string.Join(", ", slow)}");
        Assert.True(loading, "Rows that have not been read yet should be drawn as loading.");
        await app.IdleAsync();
        byte[] expected = new byte[16];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, expected);
        JsonArray cells = (await app.RenderAsync())["rows"]![0]!["cells"]!.AsArray();
        Assert.Equal(Convert.ToHexString(expected), string.Concat(cells.Select(c => c!["hex"]!.GetValue<string>())));
    });

    private static async Task<JsonObject> RowAsync(AppSession app, string offsetText) =>
        (await app.RenderAsync())["rows"]!.AsArray().Single(r => r!["offsetText"]!.GetValue<string>() == offsetText)!.AsObject();
}
