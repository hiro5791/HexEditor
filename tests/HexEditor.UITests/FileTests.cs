using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>新規作成・開く・閉じる・保存 (ENG-10〜ENG-22) と単一インスタンス (UI-15)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FileTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-ENG-10-01")]
    public Task Ctrl_n_creates_numbered_untitled_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        // 新しい設定フォルダで起動すると、ドキュメントは 1 つも開いていない (スタートページ。UI-01 の仕様 2)。
        AppSession app = await ctx.StartAsync();
        Assert.Empty(await app.TabNamesAsync());

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("menu:Command_New", (await app.KeyAsync("N", ctrl: true))["handledBy"]!.GetValue<string>());
        }

        Assert.Equal(["Untitled 1", "Untitled 2", "Untitled 3"], await app.TabNamesAsync());
        Assert.Equal(0, (await app.DocumentAsync())["length"]!.GetValue<long>());

        for (int i = 2; i >= 0; i--)
        {
            // 閉じた後、タブの選択が移るのを待ってから次を閉じる。
            await app.WaitUntilAsync(async () => (await app.StateAsync())["selectedIndex"]!.GetValue<int>() >= 0,
                TimeSpan.FromSeconds(5), "a selected tab");
            await app.KeyAsync("W", ctrl: true);
            int expected = i;
            await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == expected, TimeSpan.FromSeconds(5), "the tab to close");
        }

        Assert.Empty(await app.TabNamesAsync());
        Assert.Null(app.Find("ContentDialog") ?? app.Window.FindFirstDescendant(cf => cf.ByClassName("ContentDialog")));
    });

    [Fact(Skip = "Windows の標準の保存ダイアログはフォーカスを奪うため自動テストで操作しない。ファイル選択の差し替えの仕組み (テスト用) がまだない")]
    [Trait(UiTest.TC, "TC-ENG-10-03")]
    public Task Saving_untitled_opens_save_as() => Task.CompletedTask;

    [Fact(Skip = "ENG-11 の仕様 (シンボリックリンク・ジャンクション経由のパスも同じファイルと判定する) が未実装: パスの文字列だけで比べるため新しいタブになる。同じパスを 2 回開く場合は合格")]
    [Trait(UiTest.TC, "TC-ENG-11-02")]
    public Task Opening_the_same_file_twice_does_not_add_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        string bytes = ctx.CopyTestData("TD-BYTES-256");
        string jdir = Path.Combine(ctx.Root, "jdir");
        RunCmd($"mklink /J \"{jdir}\" \"{ctx.Root}\"");
        string link = Path.Combine(ctx.Root, "link.bin");
        bool hasLink = TryCreateSymbolicLink(link, a);

        // ファイルを開くダイアログの代わりに、命令の通り道で開く (MainViewModel.Open と同じ処理)。
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [bytes] });
        await app.OpenAsync(a);
        foreach (string path in new[] { a, hasLink ? link : a, Path.Combine(jdir, "a.bin") })
        {
            await EditingTests.SelectTabAsync(app, 0);
            await app.OpenAsync(path);
            JsonObject state = await app.StateAsync();
            Assert.True(state["documents"]!.AsArray().Count == 2, $"a new tab was opened for {path}");
            Assert.Equal("a.bin", state["document"]!["name"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-17-01")]
    public Task Unmodified_tab_closes_without_dialog() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("W", ctrl: true);
        await app.IdleAsync();
        Assert.Empty(await app.TabNamesAsync());
        Assert.Null(app.Window.FindFirstDescendant(cf => cf.ByClassName("ContentDialog")));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-17-02")]
    public Task Closing_window_with_three_modified_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        string[] paths = ["a.bin", "b.bin", "c.bin"];
        paths = [.. paths.Select(n => ctx.CopyTestData("TD-BYTES-256", n))];
        string bHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[1])));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = paths });
        for (int i = 0; i < 3; i++)
        {
            await EditingTests.SelectTabAsync(app, i);
            await app.TypeAsync("AA");
        }

        // 1. ウィンドウを閉じる (ファイル > 終了 と同じ Close の処理。タイトルバーの閉じるボタンはマウスでしか押せない)。
        await app.CommandAsync("Command_Exit");

        // 2. ダイアログは 1 つで、3 件がすべてチェックされ、ボタンが 3 つ。
        await app.WaitForAsync("Close_Item");
        Assert.Single(app.Window.FindAllDescendants(cf => cf.ByAutomationId("CloseDialog")));
        var items = app.Window.FindAllDescendants(cf => cf.ByAutomationId("Close_Item"));
        Assert.Equal(3, items.Length);
        Assert.All(items, i => Assert.True(AppSession.IsToggled(i)));
        string text = AppSession.AllText(app.Window);
        foreach (string p in paths)
        {
            Assert.Contains(p, text);
        }

        var buttons = app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).Select(AppSession.NameOf).ToList();
        Assert.Contains("Save selected and close", buttons);
        Assert.Contains("Close without saving", buttons);
        Assert.Contains("Cancel", buttons);

        // 3. b.bin のチェックを外して「選択したものを保存して閉じる」。
        items.Single(i => AppSession.AllText(i).Contains("b.bin", StringComparison.Ordinal)).Patterns.Toggle.Pattern.Toggle();
        app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .Single(b => AppSession.NameOf(b) == "Save selected and close").Patterns.Invoke.Pattern.Invoke();

        // 4. プロセスが終わり、a と c だけが保存されている。
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0xAA, File.ReadAllBytes(paths[0])[0]);
        Assert.Equal(0x00, File.ReadAllBytes(paths[1])[0]);
        Assert.Equal(bHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths[1]))));
        Assert.Equal(0xAA, File.ReadAllBytes(paths[2])[0]);
    });

    [Fact(Skip = "名前を付けて保存は Windows の標準の保存ダイアログを使い、フォーカスを奪うため自動テストで操作しない。最近使ったファイルも未実装")]
    [Trait(UiTest.TC, "TC-ENG-21-01")]
    public Task Save_as_keeps_the_original_file() => Task.CompletedTask;

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-22-01")]
    public Task Forced_termination_while_saving_keeps_the_original() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-ENG-SPARSE-10G の代わりに TD-SEQ-1M を使う (保存処理の進捗の通知は 4 MiB ごとのため、止める位置は
        // 「書き出しの途中 (最初の通知)」「置き換えの直前」「直後」の 3 つ)。
        foreach (string point in new[] { "saveWrite", "saveBeforeReplace", "saveAfterReplace" })
        {
            string path = ctx.CopyTestData("TD-SEQ-1M", $"save-{point}.bin");
            byte[] original = File.ReadAllBytes(path);
            string before = Convert.ToHexString(SHA256.HashData(original));
            AppSession app = await ctx.StartAsync(new AppOptions
            {
                Profile = ctx.NewProfile(),
                Files = [path],
                Hooks = new JsonObject { ["killAt"] = point },
            });

            // 1. オフセット 0 に 00 を挿入し (安全な保存になる)、Ctrl+S で保存する。
            await app.KeyAsync("Insert");
            await app.TypeAsync("00");
            try
            {
                await app.KeyAsync("S", ctrl: true);
                await app.IdleAsync();
            }
            catch (IOException)
            {
                // 保存の途中でプロセスが終わり、命令の通り道が閉じた。
            }

            await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
            string after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (point == "saveAfterReplace")
            {
                Assert.Equal([0x00, .. original], File.ReadAllBytes(path));
            }
            else
            {
                Assert.Equal(before, after);
            }
        }
    });

    [Fact]
    public Task Write_error_while_saving_keeps_the_original() => UiTestContext.RunAsync(async ctx =>
    {
        // 異常を再現する仕組み「書き込みエラー」の確認 (ENG-22 の受け入れ基準 1 の補足)。
        string path = ctx.CopyTestData("TD-SEQ-1M", "fault.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [path],
            Hooks = new JsonObject { ["saveFault"] = new JsonObject { ["atByte"] = 0, ["kind"] = "diskFull" } },
        });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("S", ctrl: true);
        // 保存の失敗は文書の通知で知らせる (処理の失敗の通知と並ぶことがあるため、順序は問わない)。
        await app.WaitUntilAsync(
            async () => (await app.StateAsync())["notifications"]!.AsArray()
                .Any(n => n!["message"]!.GetValue<string>().Contains("original file has not been changed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30), "the save error");
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        Assert.Empty(Directory.GetFiles(ctx.Root, "*.tmp"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-15-01")]
    public Task Second_launch_is_redirected_to_the_existing_window() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        string bytes = ctx.TestData("TD-BYTES-256");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq], NewInstance = false });

        // 1〜2. Explorer と同じく、ファイルを指定して同じ設定フォルダで起動し、終了を待つ。
        int exitCode = await ctx.LaunchAndWaitAsync(new AppOptions { Files = [bytes], NewInstance = false }, TimeSpan.FromSeconds(20));
        Assert.Equal(0, exitCode);

        // 3. プロセスは 1 つで、既存のウィンドウに 2 つのタブがあり、TD-BYTES-256 がアクティブ。
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 2, TimeSpan.FromSeconds(10), "the redirected file");
        JsonObject state = await app.StateAsync();
        Assert.Equal(["TD-SEQ-1M.bin", "TD-BYTES-256.bin"], await app.TabNamesAsync());
        Assert.Equal(1, state["selectedIndex"]!.GetValue<int>());
        Assert.Single(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppLocator.ExePath)),
            p => SameExe(p) && CommandLineHasProfile(p, ctx.DefaultProfile!));
    });

    [Fact(Skip = "UI-15 の設定 window.openExternalIn (newWindow) と複数ウィンドウが未実装")]
    [Trait(UiTest.TC, "TC-UI-15-02")]
    public Task New_window_setting_opens_a_second_window() => Task.CompletedTask;

    [Fact(Skip = "既存のウィンドウを前面に出すことを確かめるテストで、作業中の PC のフォーカスを奪うため実行しない (CI 専用の仕組みが必要)")]
    [Trait(UiTest.TC, "TC-UI-15-04")]
    public Task Existing_window_is_brought_to_front() => Task.CompletedTask;

    [Fact(Skip = "UI-15 の仕様 (既存のプロセスが応答しないときに新しいインスタンスで開き InfoBar で知らせる) が未実装: 5 秒待って転送したものとして終わる")]
    [Trait(UiTest.TC, "TC-UI-15-05")]
    public Task Unresponsive_existing_process_opens_a_new_instance() => Task.CompletedTask;

    private static bool SameExe(Process p)
    {
        try
        {
            return string.Equals(p.MainModule?.FileName, AppLocator.ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>同じ実行ファイルの、同じ設定フォルダのプロセスだけを数える (別のテスト・普段使いのインスタンスを除く)。</summary>
    private static bool CommandLineHasProfile(Process p, string profile)
    {
        string root = Path.GetDirectoryName(profile)!;
        return !p.HasExited && Directory.Exists(root) && p.StartTime > Directory.GetCreationTime(root);
    }

    private static void RunCmd(string command)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit(10_000);
    }

    /// <summary>シンボリックリンクを作る (開発者モードか管理者でないと作れない。作れなければ false)。</summary>
    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
