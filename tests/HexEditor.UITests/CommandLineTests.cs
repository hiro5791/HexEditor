using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ファイルを開くコマンド (AUTO-37)。コマンドライン <c>hexed open</c> が <c>HexEditor.exe</c> を起動し、既存のインスタンスに転送する流れを
/// 実際のプロセスで確かめる。<c>hexed</c> は HEXED_GUI_PATH でテスト用のビルドの HexEditor.exe を指す。ウィンドウは前面に出さない
/// (開発用の印 dev-no-activate と --test-hooks)。実際のディスク・ボリューム・プロセスは開かない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CommandLineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>テスト用のビルドの hexed.exe (環境変数 HEXED_PATH、なければ src/HexEditor.Cli/bin の下で最も新しいもの)。</summary>
    internal static string HexedPath()
    {
        if (Environment.GetEnvironmentVariable("HEXED_PATH") is { Length: > 0 } configured)
        {
            return configured;
        }

        string bin = Path.Combine(AppLocator.RepositoryRoot, "src", "HexEditor.Cli", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "hexed.exe", SearchOption.AllDirectories).Where(p => p.Contains("Debug", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).First()
            : throw new FileNotFoundException($"hexed.exe was not found. Build src/HexEditor.Cli first ({bin}).");
    }

    /// <summary>hexed を実行して終了を待つ。GUI は起動するだけで待たない (--wait を付けた場合は閉じるまで)。</summary>
    internal static async Task<(int ExitCode, string Out, string Err)> RunHexedAsync(string workingDirectory, IEnumerable<string> args)
    {
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "HexEditor"));
        string marker = Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }

        using var process = Process.Start(StartInfo(workingDirectory, args))!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(Wait);
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await output, await error);
    }

    private static ProcessStartInfo StartInfo(string workingDirectory, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(HexedPath())
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["HEXED_GUI_PATH"] = AppLocator.ExePath;

        // CI の実行環境は管理者として動くため、hexed を管理者でない扱いに固定する (AUTO-34 の仕様 8。テスト用のビルドだけが読む)。
        info.Environment["HEXED_TEST_ELEVATED"] = "0";
        return info;
    }

    /// <summary>テスト用の起動の引数 (設定フォルダ、異常の再現の設定、英語の表示)。</summary>
    private static string[] TestArgs(UiTestContext ctx, string profile) =>
    [
        "--test-profile", profile,
        "--test-hooks", WriteHooks(ctx),
        "--ui-lang", "en",
    ];

    private static string WriteHooks(UiTestContext ctx)
    {
        string path = Path.Combine(ctx.Root, $"hooks-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        return path;
    }

    /// <summary>hexed が起動した HexEditor のプロセス (since より後に始まったもの)。</summary>
    private static async Task<Process> FindAppProcessAsync(DateTime since, IReadOnlyCollection<int>? except = null)
    {
        DateTime deadline = DateTime.UtcNow + UiTest.Scaled(Wait);
        while (DateTime.UtcNow < deadline)
        {
            Process? found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppLocator.ExePath)).FirstOrDefault(p =>
            {
                try
                {
                    return !p.HasExited && AppLocator.IsAppProcess(p) && p.StartTime >= since.AddSeconds(-1) && except?.Contains(p.Id) != true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return false;
                }
            });
            if (found is not null)
            {
                return found;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The HexEditor process started by hexed was not found.");
    }

    private static int ProcessCount(DateTime since) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppLocator.ExePath))
        .Count(p =>
        {
            try
            {
                return !p.HasExited && AppLocator.IsAppProcess(p) && p.StartTime >= since.AddSeconds(-1);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Offset_and_select_put_the_cursor_and_the_selection_in_place() => UiTestContext.RunAsync(async ctx =>
    {
        string work = Path.Combine(ctx.Root, "work");
        Directory.CreateDirectory(work);
        File.Copy(ctx.TestData("TD-SEQ-1M"), Path.Combine(work, "a.bin"));
        string profile = ctx.NewProfile();

        DateTime start = DateTime.Now;
        (int exit, _, string err) = await RunHexedAsync(work, ["open", "a.bin", "--offset", "0x1000", "--select", "0x1000:0x10", "--new-instance", .. TestArgs(ctx, profile)]);
        Assert.True(exit == 0, err);

        AppSession app = await ctx.AttachAsync(await FindAppProcessAsync(start), profile);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).SequenceEqual(["a.bin"]), Wait, "the a.bin tab");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 0x10, Wait, "the selection");
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x1000, doc["cursor"]!.GetValue<long>());
        Assert.Equal(0x1000, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(0x10, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(Path.Combine(work, "a.bin"), doc["path"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Readonly_encoding_and_an_end_relative_offset_are_applied() => UiTestContext.RunAsync(async ctx =>
    {
        string work = Path.Combine(ctx.Root, "work");
        Directory.CreateDirectory(work);
        File.Copy(ctx.TestData("TD-SEQ-1M"), Path.Combine(work, "a.bin"));
        string profile = ctx.NewProfile();

        DateTime start = DateTime.Now;
        (int exit, _, string err) = await RunHexedAsync(work, ["open", "a.bin", "-g", "end-16", "--select", "end-16..end", "-r", "--new-instance", .. TestArgs(ctx, profile)]);
        Assert.True(exit == 0, err);

        AppSession app = await ctx.AttachAsync(await FindAppProcessAsync(start), profile);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).SequenceEqual(["a.bin"]), Wait, "the a.bin tab");
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["selectionLength"]!.GetValue<long>() == 16, Wait, "the selection");
        JsonObject doc = await app.DocumentAsync();
        long length = 1L << 20;
        Assert.Equal(length - 16, doc["cursor"]!.GetValue<long>());
        Assert.Equal(length - 16, doc["selectionStart"]!.GetValue<long>());
        Assert.True(doc["readOnly"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-02")]
    public Task A_file_from_another_folder_opens_as_a_tab_of_the_running_app() => UiTestContext.RunAsync(async ctx =>
    {
        string work1 = Path.Combine(ctx.Root, "work1");
        string work2 = Path.Combine(ctx.Root, "work2");
        Directory.CreateDirectory(work1);
        Directory.CreateDirectory(work2);
        File.Copy(ctx.TestData("TD-SEQ-1M"), Path.Combine(work1, "a.bin"));
        File.Copy(ctx.TestData("TD-ZERO-1M"), Path.Combine(work2, "b.bin"));
        string profile = ctx.NewProfile();

        DateTime start = DateTime.Now;
        (int firstExit, _, string firstErr) = await RunHexedAsync(work1, ["open", "a.bin", .. TestArgs(ctx, profile)]);
        Assert.True(firstExit == 0, firstErr);
        AppSession app = await ctx.AttachAsync(await FindAppProcessAsync(start), profile);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).SequenceEqual(["a.bin"]), Wait, "the a.bin tab");

        // 別のフォルダから hexed b.bin (サブコマンドの省略) を実行する。同じ設定フォルダ = 同じインスタンス。
        (int exit, _, string err) = await RunHexedAsync(work2, ["b.bin", .. TestArgs(ctx, profile)]);
        Assert.True(exit == 0, err);
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).SequenceEqual(["a.bin", "b.bin"]), Wait, "the b.bin tab");

        JsonObject doc = await app.DocumentAsync();
        Assert.Equal("b.bin", doc["name"]!.GetValue<string>());
        Assert.Equal(Path.Combine(work2, "b.bin"), doc["path"]!.GetValue<string>());
        JsonArray windows = (await app.SendAsync("windows"))["windows"]!.AsArray();
        Assert.Single(windows);
        Assert.Equal(1, ProcessCount(start));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-03")]
    public Task New_instance_starts_a_separate_process() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        string work = ctx.Root;
        string profile1 = ctx.NewProfile();
        string profile2 = ctx.NewProfile();

        DateTime start = DateTime.Now;
        (int firstExit, _, string firstErr) = await RunHexedAsync(work, ["open", seq, .. TestArgs(ctx, profile1)]);
        Assert.True(firstExit == 0, firstErr);
        Process first = await FindAppProcessAsync(start);
        await ctx.AttachAsync(first, profile1);

        (int exit, _, string err) = await RunHexedAsync(work, ["open", seq, "--new-instance", .. TestArgs(ctx, profile2)]);
        Assert.True(exit == 0, err);
        Process second = await FindAppProcessAsync(start, [first.Id]);
        await ctx.AttachAsync(second, profile2);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, ProcessCount(start));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-04")]
    public Task Wait_does_not_return_until_the_tab_is_closed() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-BYTES-256")], NewInstance = false });
        await app.WaitForTabsAsync(1);

        using var hexed = Process.Start(StartInfo(ctx.Root, ["open", ctx.TestData("TD-SEQ-1M"), "--wait", .. TestArgs(ctx, profile)]))!;
        _ = hexed.StandardOutput.ReadToEndAsync();
        _ = hexed.StandardError.ReadToEndAsync();
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).SequenceEqual(["TD-BYTES-256.bin", "TD-SEQ-1M.bin"]), Wait, "the tab opened by hexed");

        // 3 秒後もまだ終わっていない。
        Assert.False(hexed.WaitForExit(3000));

        // タブを閉じる (Ctrl+W) と、1 秒以内に終わる。もう 1 つのタブは開いたまま。
        await app.KeyAsync("W", ctrl: true);
        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(Wait);
        await hexed.WaitForExitAsync(cts.Token);
        watch.Stop();
        Assert.Equal(0, hexed.ExitCode);
        TimeBudget.Limit(watch.Elapsed < TimeSpan.FromSeconds(1), $"hexed --wait returned {watch.ElapsedMilliseconds} ms after the tab was closed");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(["TD-BYTES-256.bin"], await app.TabNamesAsync());
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-AUTO-37-05")]
    public Task Opening_a_100_gib_file_at_end_minus_16_shows_the_last_row_within_a_second() => UiTestContext.RunAsync(async ctx =>
    {
        // 起動時間を含めないよう、アプリを先に起動しておく (性能テスト用の環境では OS のファイルキャッシュを消した状態で行う)。
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.TestData("TD-BYTES-256")], NewInstance = false });
        await app.WaitForTabsAsync(1);
        string sparse = ctx.TestData("TD-SPARSE-100G");
        long length = 100L * (1L << 30);

        var times = new List<long>();
        for (int run = 0; run < 5; run++)
        {
            long before = (await app.RenderAsync())["now"]?.GetValue<long>() ?? 0;
            var watch = Stopwatch.StartNew();
            (int exit, _, string err) = await RunHexedAsync(ctx.Root, ["open", sparse, "--offset", "end-16", .. TestArgs(ctx, profile)]);
            Assert.True(exit == 0, err);
            await app.WaitUntilAsync(async () => (await app.DocumentAsync())["cursor"]!.GetValue<long>() == length - 16, Wait, "the cursor at the end");
            await app.WaitUntilAsync(async () => (await app.RenderAsync())["firstFrameTime"]!.GetValue<long>() != 0, Wait, "the first frame");
            watch.Stop();
            times.Add(watch.ElapsedMilliseconds);
            _ = before;
            await app.KeyAsync("W", ctrl: true);
        }

        long median = times.Order().ElementAt(times.Count / 2);
        TimeBudget.Limit(median <= 1000, $"opening the 100 GiB file at end-16 took {median} ms (median of {times.Count})");
        Assert.True(median < 30_000);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-07")]
    public Task Test_profile_keeps_settings_and_state_in_the_given_folder() => UiTestContext.RunAsync(async ctx =>
    {
        // 普段のデータフォルダ (開発中の実行は %LocalAppData%\HexEditorData-dev) のファイルの一覧とハッシュを記録する。
        string usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HexEditorData-dev");
        Dictionary<string, string> before = Snapshot(usual);

        string profile = ctx.NewProfile();
        DateTime start = DateTime.Now;
        string seq = ctx.TestData("TD-SEQ-1M");
        (int exit, _, string err) = await RunHexedAsync(ctx.Root, ["open", seq, "--new-instance", .. TestArgs(ctx, profile)]);
        Assert.True(exit == 0, err);
        AppSession app = await ctx.AttachAsync(await FindAppProcessAsync(start), profile);
        await app.WaitForTabsAsync(1);
        await ViewSettingsOps.SetBytesPerRowAsync(app, 32);
        await ViewSettingsOps.ExitAsync(app);

        string[] files = [.. Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(profile, f))];
        Assert.Contains("settings.json", files);
        Assert.Contains(files, f => f.StartsWith("documents", StringComparison.OrdinalIgnoreCase) || f.EndsWith("session.json", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith("state.json", StringComparison.OrdinalIgnoreCase));

        // 普段のデータフォルダは 1 つも変わっていない。
        Assert.Equal(before, Snapshot(usual));

        // 既存のインスタンス (オプションなしで起動したもの) があっても、--test-profile 付きの起動は渡さず新しいプロセスで起動する。
        string otherProfile = ctx.NewProfile();
        AppSession plain = await ctx.StartAsync(new AppOptions { Profile = otherProfile, Files = [seq], NewInstance = false });
        DateTime second = DateTime.Now;
        (int exit2, _, string err2) = await RunHexedAsync(ctx.Root, ["open", seq, .. TestArgs(ctx, profile)]);
        Assert.True(exit2 == 0, err2);
        Process another = await FindAppProcessAsync(second, [plain.Pid]);
        await ctx.AttachAsync(another, profile);
        Assert.NotEqual(plain.Pid, another.Id);
    });

    private static Dictionary<string, string> Snapshot(string folder)
    {
        var result = new Dictionary<string, string>();
        if (!Directory.Exists(folder))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                result[Path.GetRelativePath(folder, file)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            }
            catch (IOException)
            {
                result[Path.GetRelativePath(folder, file)] = "locked";
            }
        }

        return result;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-37-08")]
    public Task Ui_lang_ar_shows_arabic_without_changing_the_settings() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string seq = ctx.TestData("TD-SEQ-1M");
        DateTime start = DateTime.Now;
        string[] common = ["--test-profile", profile, "--test-hooks", WriteHooks(ctx), "--new-instance"];
        (int exit, _, string err) = await RunHexedAsync(ctx.Root, ["open", seq, .. common, "--ui-lang", "ar"]);
        Assert.True(exit == 0, err);
        AppSession ar = await ctx.AttachAsync(await FindAppProcessAsync(start), profile);
        await ar.WaitForTabsAsync(1);

        // メニューの項目名がアラビア語、ウィンドウは右から左、Hex ビューは左から右。
        Dictionary<string, string> arabic = UiHelpers.LoadResw("ar"), english = UiHelpers.LoadResw("en");
        var menus = ar.Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu"))!.FindAllChildren()
            .Where(m => AppSession.NameOf(m) != "Test").ToList();
        Assert.Equal(arabic.GetValueOrDefault("Menu_File.Title") ?? english["Menu_File.Title"], AppSession.NameOf(menus[0]));
        Assert.True(menus[1].BoundingRectangle.Right <= menus[0].BoundingRectangle.Left + 1, "the menus are not right to left");
        Assert.Equal("LeftToRight", (await ar.RenderAsync())["flowDirection"]!.GetValue<string>());

        // 設定 ui.language は既定のまま。
        string settings = File.Exists(Path.Combine(profile, "settings.json")) ? File.ReadAllText(Path.Combine(profile, "settings.json")) : string.Empty;
        Assert.DoesNotContain("\"ui.language\": \"ar\"", settings);
        await ViewSettingsOps.ExitAsync(ar);

        // --ui-lang なしで起動し直すと英語のメニュー。
        DateTime again = DateTime.Now;
        (int exit2, _, string err2) = await RunHexedAsync(ctx.Root, ["open", seq, .. common, "--ui-lang", "en"]);
        Assert.True(exit2 == 0, err2);
        AppSession en = await ctx.AttachAsync(await FindAppProcessAsync(again), profile);
        await en.WaitForTabsAsync(1);
        var englishMenus = en.Window.FindFirstDescendant(cf => cf.ByAutomationId("MainMenu"))!.FindAllChildren()
            .Where(m => AppSession.NameOf(m) != "Test").ToList();
        Assert.Equal(english["Menu_File.Title"], AppSession.NameOf(englishMenus[0]));

        // 23 言語にない言語タグは hexed が終了コード 2 で断る。
        (int bad, _, _) = await RunHexedAsync(ctx.Root, ["open", "--ui-lang", "xx"]);
        Assert.Equal(2, bad);
    });
}
