using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using Xunit.Abstractions;

namespace HexEditor.UITests;

/// <summary>
/// UI の性能テスト (00-overview 11.1、VIEW-03・VIEW-04・VIEW-25・VIEW-29・VIEW-40、PKG-11、ENG-11)。アプリの診断表示の記録
/// (フレームの時刻・描画・キー入力) を CSV に書き出して集計する。キー入力はテスト用の命令の通り道で注入し、
/// 実際のキーボード・マウスは使わない。性能が安定しない共有の環境では誤って失敗しうるため、PR の UI テスト (Category=UI) には
/// 含めない。テストケースの環境が「性能テスト用の固定の環境 (テスト方針 6.6)」のものは、その環境 (HEXEDITOR_PERF_MACHINE=1) で
/// だけ実行する (<see cref="PerfEnvironmentFactAttribute"/>)。計測にはテスト用の Release ビルド (-p:HexTestHooks=true) を使う。
/// </summary>
[Trait(UiTest.Category, Performance)]
public sealed class PerformanceTests(ITestOutputHelper output)
{
    public const string Performance = "Performance";

    /// <summary>「60 fps を保つ」(テスト方針 7.3)。</summary>
    private const double FrameP99 = 16.7;
    private const double FrameMax = 50;

    /// <summary>「UI が固まらない」: キー入力から画面反映まで (テスト方針 7.3、00-overview 11.1)。</summary>
    private const double InputLatency = 50;

    private static async Task<string> EnableDiagnosticsAsync(UiTestContext ctx, AppSession app)
    {
        string log = Path.Combine(ctx.Root, $"diagnostics-{Guid.NewGuid():N}.csv");
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = true, ["logPath"] = log });

        // 記録の始まりの不安定なフレームを避ける。
        await Task.Delay(500);
        return log;
    }

    private static async Task<FrameLog> StopDiagnosticsAsync(AppSession app, string log)
    {
        await Task.Delay(300);
        await app.SendAsync("diagnostics", new JsonObject { ["enabled"] = false });
        return FrameLog.Read(log);
    }

    private static Task KeyAsync(AppSession app, string key, bool ctrl = false, bool shift = false) =>
        app.SendAsync("keyMeasured", new JsonObject { ["key"] = key, ["ctrl"] = ctrl, ["shift"] = shift });

    /// <summary>キーを押し続ける (Windows の既定のキーの繰り返し: 約 30 回 / 秒)。</summary>
    private static async Task HoldKeyAsync(AppSession app, string key, TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        long next = 0;
        while (watch.Elapsed < duration)
        {
            await KeyAsync(app, key);
            next += 33;
            int wait = (int)(next - watch.ElapsedMilliseconds);
            if (wait > 0)
            {
                await Task.Delay(wait);
            }
        }
    }

    private void AssertSixtyFps(FrameLog log, string name)
    {
        IReadOnlyList<double> intervals = [.. log.Frames.Select(f => f.Interval)];
        output.WriteLine($"{name}: {FrameLog.Describe(intervals)}");

        // 遅かったフレームの前後 (原因を調べるため): 直前の描画の時間と、記録の開始からの時刻。
        foreach (FrameLog.Frame slow in log.Frames.OrderByDescending(f => f.Interval).Take(5))
        {
            FrameLog.Render? render = log.Renders.LastOrDefault(r => r.Time <= slow.Time);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {slow.Time:F0} ms: 間隔 {slow.Interval:F1} ms、直前の描画 {render?.Milliseconds:F2} ms (読み込み中 {render?.LoadingCells})"));
        }
        Assert.NotEmpty(intervals);
        Assert.True(FrameLog.Percentile(intervals, 0.99) <= FrameP99 && intervals.Max() <= FrameMax, $"{name}: {FrameLog.Describe(intervals)}");
    }

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-03-02")]
    public Task Scrolling_a_slow_source_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-VIEW-HOOK-SLOW2000 (先頭 64 KiB を除く読み込みに 2,000 ms の遅延) で TD-SEQ-1M を開く。
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "*", ["delayMs"] = 2000, ["delayFromOffset"] = 65536 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")], Hooks = hooks });
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 1: PageDown を 5 秒間押し続ける。
        await HoldKeyAsync(app, "PageDown", TimeSpan.FromSeconds(5));
        FrameLog log = await StopDiagnosticsAsync(app, path);

        // 手順 2: フレームの間隔と、描画が I/O を待った回数。読み込み中のセルは仮表示になる (待たない)。
        AssertSixtyFps(log, "遅いデータソースのスクロール");
        output.WriteLine($"描画: 最大 {log.Renders.Max(r => r.Milliseconds):F2} ms、仮表示のある描画 {log.Renders.Count(r => r.LoadingCells > 0)} 回");
        Assert.Contains(log.Renders, r => r.LoadingCells > 0);
        Assert.All(log.Renders, r => Assert.Equal(0, r.IoWaits));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-04-01")]
    public Task Holding_page_down_on_a_hundred_gigabyte_file_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: 計測で表示する範囲 (先頭から 64 MiB) を 1 回読んで OS のキャッシュに載せる。ウィンドウの最大化は前面に出るため行わない。
        string file = ctx.TestData("TD-SPARSE-100G");
        using (FileStream warm = File.OpenRead(file))
        {
            byte[] buffer = new byte[4 * 1024 * 1024];
            for (int i = 0; i < 16; i++)
            {
                warm.ReadExactly(buffer);
            }
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 1: PageDown を 10 秒間押し続ける。
        await HoldKeyAsync(app, "PageDown", TimeSpan.FromSeconds(10));
        FrameLog log = await StopDiagnosticsAsync(app, path);

        // 手順 2: 60 fps を保つ。前回より 20% 以上悪化したら警告。
        IReadOnlyList<double> intervals = [.. log.Frames.Select(f => f.Interval)];
        if (FrameLog.CompareWithPrevious("TC-VIEW-04-01-p99", FrameLog.Percentile(intervals, 0.99)) is { } warning)
        {
            output.WriteLine(warning);
        }

        AssertSixtyFps(log, "100 GiB で PageDown");
    });

    /// <summary>1 フレームの描画時間の上限と目標 (VIEW-04 の仕様 4、受け入れ基準 3)。</summary>
    private const double RenderLimit = 16.7;
    private const double RenderTarget = 8;

    /// <summary>
    /// TC-VIEW-04-03 の手順 1・2: ホイールを 16 ms の間隔で 300 回下へ送り、横スクロールで右端に移って繰り返す。描画時間 (診断表示の記録) を返す。
    /// ウィンドウの最大化は前面に出るため行わず、1920×1080 の大きさにする。
    /// </summary>
    private static async Task<List<double>> ScrollWideRowsAsync(UiTestContext ctx, AppSession app)
    {
        await app.ResizeAsync(1920, 1080);
        await ViewSettingsOps.SetBytesPerRowAsync(app, 4096);
        await app.IdleAsync();
        string path = await EnableDiagnosticsAsync(ctx, app);
        async Task WheelDownAsync()
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 300; i++)
            {
                await ViewOps.WheelAsync(app, -120);
                int wait = (int)((i + 1) * 16 - watch.ElapsedMilliseconds);
                if (wait > 0)
                {
                    await Task.Delay(wait);
                }
            }
        }

        await WheelDownAsync();
        await ViewOps.WheelAsync(app, -120, count: 4096, horizontal: true);
        await app.IdleAsync();
        await WheelDownAsync();
        FrameLog log = await StopDiagnosticsAsync(app, path);
        return [.. log.Renders.Select(r => r.Milliseconds)];
    }

    private void AssertRenderTimes(IReadOnlyList<double> renders, string name)
    {
        output.WriteLine($"{name}: 描画 {renders.Count} 回、中央値 {FrameLog.Percentile(renders, 0.5):F2} ms、最大 {renders.Max():F2} ms");
        Assert.NotEmpty(renders);
        Assert.All(renders, r => Assert.True(r <= RenderLimit, $"{name}: {r:F2} ms"));
        if (FrameLog.Percentile(renders, 0.5) > RenderTarget)
        {
            // 目標値 (8 ms) を超えた場合は警告だけにする (テストケースの期待結果)。
            output.WriteLine($"警告: {name} の描画時間の中央値が目標の {RenderTarget} ms を超えています。");
        }
    }

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-04-03")]
    public Task Scrolling_4096_bytes_per_row_keeps_frame_time() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M")] });
        AssertRenderTimes(await ScrollWideRowsAsync(ctx, app), "1 行 4,096 バイト");
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-04-04")]
    public Task A_million_bookmarks_keep_the_rendering_performance() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-INSP-BM-1M の読み込み (INSP-30) はフェーズ 2 なので、同じ 100 万件をテスト用の命令の通り道で付ける。
        string file = ctx.TestData("TD-SPARSE-100G");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        await app.SendAsync("bookmarksAdd", new JsonObject { ["count"] = 1_000_000, ["step"] = 1024, ["length"] = 16 }, TimeSpan.FromMinutes(2));
        await app.IdleAsync();

        // 手順 1: TC-VIEW-04-01 (PageDown を押し続けて 60 fps)。
        string path = await EnableDiagnosticsAsync(ctx, app);
        await HoldKeyAsync(app, "PageDown", TimeSpan.FromSeconds(10));
        AssertSixtyFps(await StopDiagnosticsAsync(app, path), "ブックマーク 100 万件で PageDown");

        // 手順 2: TC-VIEW-04-02 (矢印キーから画面まで 50 ms 以内)。
        await app.KeyAsync("Home", ctrl: true);
        path = await EnableDiagnosticsAsync(ctx, app);
        for (int i = 0; i < 100; i++)
        {
            await KeyAsync(app, i < 50 ? "Right" : "Down");
            await Task.Delay(200);
        }

        IReadOnlyList<double> latencies = (await StopDiagnosticsAsync(app, path)).KeyToFrame();
        output.WriteLine(Latencies("ブックマーク 100 万件で矢印キーから画面まで", latencies));
        Assert.All(latencies, l => Assert.True(l <= InputLatency, Latencies("矢印キーから画面まで", latencies)));

        // 手順 3: TC-VIEW-04-03 の手順 1 (1 行 4,096 バイトでホイール)。
        AssertRenderTimes(await ScrollWideRowsAsync(ctx, app), "ブックマーク 100 万件で 1 行 4,096 バイト");
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-04-02")]
    public Task Arrow_keys_reach_the_screen_within_fifty_milliseconds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 1: → を 100 回、↓ を 100 回、200 ms の間隔で押す。
        foreach (string key in new[] { "Right", "Down" })
        {
            for (int i = 0; i < 100; i++)
            {
                await KeyAsync(app, key);
                await Task.Delay(200);
            }
        }

        // 手順 2: キー入力から、新しいカーソル位置を含むフレームを出すまで。
        FrameLog log = await StopDiagnosticsAsync(app, path);
        IReadOnlyList<double> latencies = log.KeyToFrame();
        output.WriteLine(Latencies("矢印キーから画面まで", latencies));
        Assert.Equal(200, latencies.Count);
        Assert.All(latencies, l => Assert.True(l <= InputLatency, Latencies("矢印キーから画面まで", latencies)));
        Assert.Equal(100 + 100 * 16, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-25-06")]
    public Task Ctrl_end_on_a_hundred_gigabyte_file_reaches_the_screen_quickly() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提の「末尾を OS のキャッシュに載せない」は、キャッシュの消去に管理者権限が要るため行わない (TD-SPARSE-100G の末尾は
        // 割り当てのない範囲なので、読み込みにディスクを使わない)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 1・3: Ctrl+End と、Ctrl+Home・Ctrl+End を交互に 20 回。
        await KeyAsync(app, "End", ctrl: true);
        await Task.Delay(200);
        for (int i = 0; i < 20; i++)
        {
            await KeyAsync(app, "Home", ctrl: true);
            await Task.Delay(200);
            await KeyAsync(app, "End", ctrl: true);
            await Task.Delay(200);
        }

        // 手順 2: Ctrl+End の入力から末尾にカーソルを描いた最初のフレームまで (仮表示でもよい)。
        FrameLog log = await StopDiagnosticsAsync(app, path);
        IReadOnlyList<double> all = log.KeyToFrame();
        double[] ends = [.. all.Where((_, i) => log.Keys[i].Name == "End" && i % 2 == 0)];
        output.WriteLine(Latencies("Ctrl+End から画面まで", ends));
        Assert.Equal(21, ends.Length);
        Assert.All(ends, l => Assert.True(l <= InputLatency, Latencies("Ctrl+End から画面まで", ends)));
        Assert.Equal(100 * TestDataCatalog.GiB, (await app.DocumentAsync())["cursor"]!.GetValue<long>());
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-29-09")]
    public Task Jumping_near_the_end_of_a_hundred_gigabyte_file_is_fast() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: 末尾の 1 MiB を 1 回読んで OS のキャッシュに載せる。
        string file = ctx.TestData("TD-SPARSE-100G");
        long length = new FileInfo(file).Length;
        using (FileStream warm = File.OpenRead(file))
        {
            warm.Seek(length - TestDataCatalog.MiB, SeekOrigin.Begin);
            warm.ReadExactly(new byte[TestDataCatalog.MiB]);
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file] });
        string path = await EnableDiagnosticsAsync(ctx, app);
        for (int i = 0; i < 21; i++)
        {
            // 手順 1: Ctrl+G で end-0x1000 を入力して実行する (Enter と同じ「移動」ボタン)。
            await app.KeyAsync("G", ctrl: true);
            await app.UiaSetValueAsync("GoTo_Input", "end-0x1000");
            await app.SendAsync("mark");
            await app.CommandAsync("GoTo_Go");
            await Task.Delay(300);
            Assert.Equal(length - 0x1000, (await app.DocumentAsync())["cursor"]!.GetValue<long>());

            // 手順 3: Ctrl+Home。
            await app.KeyAsync("Home", ctrl: true);
            await Task.Delay(100);
        }

        // 手順 2: 実行から、移動先の値 (仮表示でない) を描いた最初のフレームまで。
        FrameLog log = await StopDiagnosticsAsync(app, path);
        IReadOnlyList<double> latencies = log.KeyToFrame(loaded: true);
        output.WriteLine(Latencies("末尾付近へのジャンプから画面まで", latencies));
        Assert.Equal(21, latencies.Count);
        Assert.All(latencies, l => Assert.True(l <= 100, Latencies("末尾付近へのジャンプから画面まで", latencies)));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-VIEW-40-01")]
    public Task Status_bar_follows_the_cursor_within_fifty_milliseconds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        AutomationElement offset = await app.WaitForAsync("Status_Offset");
        AutomationElement value = await app.WaitForAsync("Status_Value");

        // ステータスバーのカーソル位置と値の名前の変更 (UI オートメーションのイベント) を受けた時刻を記録する。
        var clock = Stopwatch.StartNew();
        var changes = new List<(double Time, string Name)>();
        void OnChanged(AutomationElement element, FlaUI.Core.Identifiers.PropertyId property, object name)
        {
            lock (changes)
            {
                changes.Add((clock.Elapsed.TotalMilliseconds, element.Properties.AutomationId.ValueOrDefault + "=" + name));
            }
        }

        using var offsetEvents = offset.RegisterPropertyChangedEvent(TreeScope.Element, OnChanged, AppSession.Automation.PropertyLibrary.Element.Name);
        using var valueEvents = value.RegisterPropertyChangedEvent(TreeScope.Element, OnChanged, AppSession.Automation.PropertyLibrary.Element.Name);
        await Task.Delay(300);

        // 手順 1・2: → を 100 回、200 ms の間隔で押し、キー入力から両方の表示が新しい値になるまでを計る。
        var latencies = new List<double>();
        for (int n = 1; n <= 100; n++)
        {
            double sent = clock.Elapsed.TotalMilliseconds;
            await KeyAsync(app, "Right");
            string offsetText = $"0x{n:X8}";
            string valueText = $"{n % 256:X2} ({n % 256})";
            var wait = Stopwatch.StartNew();
            double? done = null;
            while (done is null && wait.ElapsedMilliseconds < 1000)
            {
                lock (changes)
                {
                    double? o = changes.FirstOrDefault(c => c.Time >= sent && c.Name.StartsWith("Status_Offset=", StringComparison.Ordinal)
                        && c.Name.Contains(offsetText, StringComparison.OrdinalIgnoreCase)).Time;
                    double? v = changes.FirstOrDefault(c => c.Time >= sent && c.Name.StartsWith("Status_Value=", StringComparison.Ordinal)
                        && c.Name.Contains(valueText, StringComparison.Ordinal)).Time;
                    if (o > 0 && v > 0)
                    {
                        done = Math.Max(o.Value, v.Value);
                    }
                }

                await Task.Delay(1);
            }

            Assert.True(done is not null, $"カーソル位置 {n}: ステータスバーが {offsetText} / {valueText} になりません ({AppSession.NameOf(offset)} / {AppSession.NameOf(value)})");
            latencies.Add(done!.Value - sent);
            await Task.Delay(Math.Max(0, 200 - (int)wait.ElapsedMilliseconds));
        }

        output.WriteLine(Latencies("カーソル移動からステータスバーまで", latencies));
        Assert.All(latencies, l => Assert.True(l <= InputLatency, Latencies("カーソル移動からステータスバーまで", latencies)));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-FIND-02-03")]
    public Task Cursor_and_scrolling_stay_smooth_during_a_search() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-FIND-RANDOM-10G (10 GiB。スパースでない) を開く。性能テスト用の計測機だけで作る。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-FIND-RANDOM-10G")] });

        // 手順 1: 検索バーで Hex の ?? 45 58 ?? 4E 44 を次を検索する (一致は末尾の 1 か所だけなので、ファイル全体を読む)。
        await app.KeyAsync("F", ctrl: true);
        await app.IdleAsync();
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 0 });
        await app.UiaSetValueAsync("Find_Query", "?? 45 58 ?? 4E 44");
        string path = await EnableDiagnosticsAsync(ctx, app);
        await app.CommandAsync("Find_Next");

        // 手順 2: 検索中に ↓ を 30 ms 間隔で 200 回、PageDown を 30 ms 間隔で 100 回。
        foreach ((string key, int count) in new[] { ("Down", 200), ("PageDown", 100) })
        {
            for (int i = 0; i < count; i++)
            {
                await KeyAsync(app, key);
                await Task.Delay(30);
            }
        }

        // 手順 2 の間、検索は続いている。
        Assert.True((await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, "キー入力の間に検索が終わりました。");
        FrameLog log = await StopDiagnosticsAsync(app, path);

        // 手順 3: 60 fps を保ち、キー入力から画面の反映までが 50 ms 以下。
        AssertSixtyFps(log, "検索中の操作");
        IReadOnlyList<double> latencies = log.KeyToFrame();
        output.WriteLine(Latencies("検索中のキー入力から画面まで", latencies));
        Assert.All(latencies, l => Assert.True(l <= InputLatency, Latencies("検索中のキー入力から画面まで", latencies)));
    });

    [Fact(Skip = "1 行のバイト数の指定 (VIEW-08、フェーズ 1) が未実装。TC-EDIT-01-05 は 1 行 4,096 バイトで自動スクロールを末尾まで届かせる")]
    [Trait(UiTest.TC, "TC-EDIT-01-05")]
    public Task Drag_auto_scroll_through_a_hundred_gigabyte_file() => Task.CompletedTask;

    /// <summary>
    /// 60 fps の判定は共有の環境 (作業中の PC、Debug ビルド) では安定しない (p99 が 20 ms 前後になる) ため、他のフレームの間隔の
    /// テストと同じく性能テスト用の固定の環境で実行する。
    /// </summary>
    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-ENG-09-02")]
    public Task Scrolling_stays_smooth_while_hashing_ten_gigabytes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ENG-SPARSE-10G")] });

        // 手順 1: ハッシュパネルで SHA-256 だけを選び、全体の計算を始める。
        await app.SendAsync("hash", new JsonObject { ["action"] = "show" });
        await app.SendAsync("hash", new JsonObject { ["action"] = "algorithms", ["ids"] = new JsonArray("sha256") });
        await app.CommandAsync("Hash_Compute");
        await app.WaitUntilAsync(async () => (await app.SendAsync("hash"))["computing"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the calculation");
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 2: 計算の間、↓ を 16 ms 間隔で 600 回、続けてホイールのスクロールを 600 回。
        for (int i = 0; i < 600; i++)
        {
            await KeyAsync(app, "Down");
            await Task.Delay(16);
        }

        for (int i = 0; i < 600; i++)
        {
            await ViewOps.WheelAsync(app, -120);
            await Task.Delay(16);
        }

        bool stillComputing = (await app.SendAsync("hash"))["computing"]!.GetValue<bool>();
        FrameLog log = await StopDiagnosticsAsync(app, path);
        output.WriteLine(stillComputing ? "計算は続いていた" : "計算は手順 2 の途中で終わった");

        // 手順 3: 60 fps を保ち、キー入力から画面の反映までの最大が 50 ms 以下。
        AssertSixtyFps(log, "ハッシュの計算中の操作");
        IReadOnlyList<double> latencies = log.KeyToFrame();
        output.WriteLine(Latencies("ハッシュの計算中のキー入力から画面まで", latencies));
        Assert.All(latencies, l => Assert.True(l <= InputLatency, Latencies("ハッシュの計算中のキー入力から画面まで", latencies)));
    });

    /// <summary>
    /// TC-ENG-11-01 のアプリの部分: 起動済みのアプリで、コマンドラインの引数 (2 つ目の起動からの転送) と「開く」(選んだ後の処理。
    /// 標準のファイルのダイアログはフォーカスを奪うため使わない) で開き、先頭行の表示までの時間と、その間にアプリがファイルから読んだ量を計る。
    /// OS のファイルキャッシュの消去は管理者権限が要るため行わない (CI の性能テスト用のランナーで行う)。
    /// </summary>
    [Theory]
    [Trait(UiTest.TC, "TC-ENG-11-01")]
    [InlineData("TD-SPARSE-100G")]
    [InlineData("TD-EMPTY")]
    [InlineData("TD-SEQ-1M")]
    public Task Opening_from_the_command_line_and_the_open_command_is_fast(string id) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { NewInstance = false });
        string file = ctx.TestData(id);
        string name = Path.GetFileName(file);

        // コマンドラインの引数: 2 つ目の起動が既存のアプリに転送し、タブが開いて先頭行が描かれるまで。
        long readBefore = ReadBytes(app.Process);
        var watch = Stopwatch.StartNew();
        using (Process second = AppSession.Launch(new AppOptions { Files = [file], NewInstance = false }, ctx.DefaultProfile!,
            Path.Combine(ctx.Root, "hooks-second.json")))
        {
            await WaitForFirstRowAsync(app, name);
            TimeSpan commandLine = watch.Elapsed;
            long read = ReadBytes(app.Process) - readBefore;
            await second.WaitForExitAsync();
            output.WriteLine($"{id} (コマンドライン): {commandLine.TotalMilliseconds:F0} ms、読んだ量 {read:N0} バイト");
            Assert.True(commandLine <= TimeSpan.FromSeconds(1), $"{commandLine.TotalMilliseconds:F0} ms");
            Assert.True(read <= TestDataCatalog.MiB, $"{read:N0} バイト");
        }

        // 「開く」: タブを閉じてから、選んだファイルを開く処理 (ダイアログの後と同じ) から先頭行が描かれるまで。
        await app.KeyAsync("W", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(10), "the tab to close");
        readBefore = ReadBytes(app.Process);
        watch.Restart();
        await app.OpenAsync(file);
        await WaitForFirstRowAsync(app, name);
        TimeSpan open = watch.Elapsed;
        long readOpen = ReadBytes(app.Process) - readBefore;
        output.WriteLine($"{id} (開く): {open.TotalMilliseconds:F0} ms、読んだ量 {readOpen:N0} バイト");
        Assert.True(open <= TimeSpan.FromSeconds(1), $"{open.TotalMilliseconds:F0} ms");
        Assert.True(readOpen <= TestDataCatalog.MiB, $"{readOpen:N0} バイト");
    });

    /// <summary>選択中のタブが <paramref name="name"/> になり、先頭行が仮表示なしで描かれるまで待つ。</summary>
    private static async Task WaitForFirstRowAsync(AppSession app, string name)
    {
        string last = "(なし)";
        try
        {
            await WaitAsync();
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} 最後の状態: {last}", ex);
        }

        Task WaitAsync() => app.WaitUntilAsync(async () =>
        {
            JsonObject state = await app.StateAsync();
            if (state["document"]?["name"]?.GetValue<string>() != name || state["hexViews"]!.GetValue<int>() == 0)
            {
                last = $"タブ {state["documents"]!.ToJsonString()}、選択 {state["selectedIndex"]}、Hex ビュー {state["hexViews"]}";
                return false;
            }

            JsonArray rows;
            try
            {
                rows = (await app.RenderAsync())["rows"]!.AsArray();
            }
            catch (InvalidOperationException)
            {
                // 新しいタブの Hex ビューがまだできていない。
                return false;
            }

            last = rows.Count == 0 ? "行なし" : string.Join(" ", rows[0]!["cells"]!.AsArray().Select(c => c!["hex"]!.GetValue<string>()));
            return rows.Count > 0 && rows[0]!["cells"]!.AsArray().All(c => c!["hex"]!.GetValue<string>() != "··");
        }, TimeSpan.FromSeconds(10), $"the first row of {name}");
    }

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-PKG-11-01")]
    public Task Second_launch_is_forwarded_within_three_hundred_milliseconds() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { NewInstance = false });
        string file = ctx.TestData("TD-BYTES-256");
        var times = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            // 手順 1: HexEditor.exe <TD-BYTES-256> を起動し、プロセスの開始から終了までを計る。
            using Process second = AppSession.Launch(new AppOptions { Files = [file], NewInstance = false }, ctx.DefaultProfile!,
                Path.Combine(ctx.Root, $"hooks-second-{i}.json"));
            await second.WaitForExitAsync();
            times.Add((second.ExitTime - second.StartTime).TotalMilliseconds);

            // 手順 2: 既存のウィンドウにタブが開いたことを確かめ、閉じる。
            await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Contains("TD-BYTES-256.bin"), TimeSpan.FromSeconds(10), "the forwarded tab");
            await app.KeyAsync("W", ctrl: true);
            await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(10), "the tab to close");
        }

        output.WriteLine(Latencies("2 回目の起動の開始から終了まで", times));
        Assert.All(times, t => Assert.True(t <= 300, Latencies("2 回目の起動の開始から終了まで", times)));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-PKG-11-04")]
    public Task Startup_until_the_window_responds_is_within_two_seconds() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: OS のファイルキャッシュを温めるため 1 回起動・終了しておく。配布形態は HEXEDITOR_APP_EXE で選ぶ
        // (CI ではポータブル版・インストーラ版を展開した実行ファイルを指定して、このテストを配布形態ごとに実行する)。
        (await ctx.StartAsync(new AppOptions())).Kill();

        var times = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            // 手順 1: プロセスの開始から、メインウィンドウが表示されてキー入力に応答する (Ctrl+N で新しいタブが開く) まで。
            // Ctrl+O は標準のファイルのダイアログがフォーカスを奪うため、アプリの中で完結するキーで応答を確かめる。
            AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile() });
            await app.KeyAsync("N", ctrl: true);
            await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 1, TimeSpan.FromSeconds(10), "the new tab");
            times.Add((DateTime.Now - app.Process.StartTime).TotalMilliseconds);
            app.Kill();
        }

        output.WriteLine(Latencies($"起動から操作可能まで ({AppLocator.ExePath})", times));
        Assert.All(times, t => Assert.True(t <= 2000, Latencies("起動から操作可能まで", times)));
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-FIND-20-04")]
    public Task Scrolling_a_million_results_keeps_sixty_fps() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-FIND-HITS-1500K を開き、`AB CD` のすべて検索が上限 (1,000,000 件) で止まった状態。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.WriteFile("TD-FIND-HITS-1500K.bin", SearchResultsTests.Hits1500K())] });
        await SearchResultsTests.OpenFindAsync(app, 0, "AB CD");
        await SearchResultsTests.FindAllAsync(app);
        await SearchResultsTests.WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "LimitReached", "the limit", 120);
        string path = await EnableDiagnosticsAsync(ctx, app);

        // 手順 1: 結果一覧で PageDown を 30 ms 間隔で 300 回。
        for (int i = 0; i < 300; i++)
        {
            await app.SendAsync("searchResultsKey", new JsonObject { ["key"] = "PageDown" });
            await Task.Delay(30);
        }

        // 手順 2: スクロールバーのつまみを先頭から末尾まで 5 秒かけて動かす (つまみのドラッグと同じ、表示位置の連続した変更)。
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            await app.SendAsync("searchResultsScroll", new JsonObject { ["fraction"] = watch.ElapsedMilliseconds / 5000.0 });
            await Task.Delay(16);
        }

        FrameLog log = await StopDiagnosticsAsync(app, path);
        AssertSixtyFps(log, "100 万件の結果のスクロール");

        // 手順 3: Ctrl+End で最後の行 (1,000,000 行目) が 100 ms 以内に表示される。
        await app.SendAsync("searchResultsScroll", new JsonObject { ["fraction"] = 0 });
        watch.Restart();
        await app.SendAsync("searchResultsKey", new JsonObject { ["key"] = "End", ["ctrl"] = true });
        JsonObject state = await app.SendAsync("searchResults");
        double elapsed = watch.Elapsed.TotalMilliseconds;
        Assert.Equal(999_999, state["selected"]!.GetValue<long>());
        Assert.True(state["top"]!.GetValue<long>() + state["visibleRows"]!.GetValue<long>() >= 1_000_000);
        Assert.True(elapsed <= 100, $"Ctrl+End: {elapsed:F0} ms");
    });

    /// <summary>プロセスがファイル・デバイスから読んだバイト数 (GetProcessIoCounters の ReadTransferCount)。</summary>
    private static long ReadBytes(Process process)
    {
        Assert.True(GetProcessIoCounters(process.Handle, out IoCounters counters));
        return (long)counters.ReadTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    private static string Latencies(string name, IReadOnlyList<double> values) => values.Count == 0
        ? $"{name}: なし"
        : string.Create(CultureInfo.InvariantCulture,
            $"{name}: {values.Count} 回、最大 {values.Max():F1} ms、中央値 {FrameLog.Percentile(values, 0.5):F1} ms、p99 {FrameLog.Percentile(values, 0.99):F1} ms");
}
