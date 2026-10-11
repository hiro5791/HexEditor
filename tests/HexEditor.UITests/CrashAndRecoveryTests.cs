using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>異常終了の後の復旧 (ENG-27) とクラッシュ情報 (PKG-30)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CrashAndRecoveryTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    [Trait(UiTest.TC, "TC-PKG-30-01")]
    public Task Crash_report_is_written_and_shown_on_next_start() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1. 開発用のコマンド (テスト用のメニュー) で、UI スレッドで未処理の例外を起こす。
        await app.CommandAsync("TestMenu_ThrowUi");
        int exitCode = await app.WaitForExitAsync(ExitTimeout);
        Assert.NotEqual(0, exitCode);

        // 2. crash\<日時>.txt が 1 つでき、版・配布形態・アーキテクチャ・OS の版・例外の型とスタックトレースがある。
        string[] reports = CrashReports(app.CrashFolder);
        string report = File.ReadAllText(Assert.Single(reports));
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}\.txt$", Path.GetFileName(reports[0]));
        Assert.Contains("Version: ", report);
        Assert.Contains("Distribution: ", report);
        Assert.Contains("Architecture: ", report);
        Assert.Contains("OS: ", report);
        Assert.Contains("TestHookException", report);
        Assert.Contains("   at ", report);

        // 3. 同じ設定フォルダで起動すると、アプリ全体の InfoBar で知らせる。
        AppSession again = await ctx.StartAsync();
        await again.WaitUntilAsync(async () => (await again.StateAsync())["crashNotice"]!["open"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(15), "the crash notice");
        Assert.Contains("closed unexpectedly", (await again.StateAsync())["crashNotice"]!["message"]!.GetValue<string>());
        // 通知 (UI-36) の中に「クラッシュ情報を開く」「問題を報告」のボタンがある。
        await again.WaitForAsync("Notification");
        string text = again.NotificationsText();
        Assert.Contains("closed unexpectedly", text);
        Assert.Contains("Open crash info", text);
        Assert.Contains("Report a problem", text);
    });

    /// <summary>PKG-30 の仕様 4: ミニダンプは既定では作らず、設定 diagnostics.writeMiniDump が true のときだけ crash\ に書く。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Mini_dump_is_written_only_when_enabled(bool enabled) => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        if (enabled)
        {
            await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"diagnostics.writeMiniDump\": true}");
        }

        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.CommandAsync("TestMenu_ThrowUi");
        Assert.NotEqual(0, await app.WaitForExitAsync(ExitTimeout));

        Assert.Single(CrashReports(app.CrashFolder));
        string[] dumps = Directory.Exists(app.CrashFolder) ? Directory.GetFiles(app.CrashFolder, "*.dmp") : [];
        if (enabled)
        {
            Assert.True(new FileInfo(Assert.Single(dumps)).Length > 0, "the mini dump is empty");
        }
        else
        {
            Assert.Empty(dumps);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-PKG-30-03")]
    public Task Crash_report_does_not_contain_file_contents() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-SECRET: ファイル名 secret-content.bin。目印を先頭から繰り返して 4,096 バイトで打ち切ったもの。
        const string secret = "HEXEDITOR-SECRET-7F3A";
        byte[] content = Enumerable.Range(0, 4096).Select(i => (byte)secret[i % secret.Length]).ToArray();
        string path = ctx.WriteFile("secret-content.bin", content);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.SelectAsync(0, 0x40);

        await app.CommandAsync("TestMenu_ThrowUi");
        await app.WaitForExitAsync(ExitTimeout);

        string report = File.ReadAllText(Assert.Single(CrashReports(app.CrashFolder)));
        byte[] raw = File.ReadAllBytes(CrashReports(app.CrashFolder)[0]);
        Assert.DoesNotContain(secret, report);
        Assert.Equal(-1, raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)));
        string hex = Convert.ToHexString(Encoding.ASCII.GetBytes(secret));
        Assert.DoesNotContain(hex, report.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-content.bin", report, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-27-01")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Recovers_after_forced_termination() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        byte[] original = File.ReadAllBytes(path);

        // 保存間隔は 1 分の代わりに 2 秒にする (待ち時間を短くするため。定期の書き出しの仕組みは同じ)。
        var hooks = new JsonObject { ["recoveryIntervalSeconds"] = 2 };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });

        // 1. 0x10 に DE AD BE EF を挿入し、0x2000 を 77 に上書きして、カーソルを 0x2000 に置く。
        await EditAsync(app);
        byte[] expected = [.. original[..0x10], 0xDE, 0xAD, 0xBE, 0xEF, .. original[0x10..]];
        expected[0x2000] = 0x77;
        Assert.Equal(expected, await app.BytesAsync(0, expected.Length));

        // 2. 定期の書き出しを待って、強制終了する。
        await WaitForRecoveryDataAsync(app);
        app.Kill();

        // 3. 同じ設定フォルダで起動すると、復旧の画面に表示名・パス・保存日時・変更の量と「復旧する」「破棄」「あとで」が出る。
        AppSession again = await ctx.StartAsync(new AppOptions { Hooks = hooks });
        AutomationElement dialog = await WaitForRecoveryDialogAsync(again);
        string text = await again.WaitForDialogTextAsync(dialog, "seq.bin", path, "bytes changed");
        Assert.Contains("seq.bin", text);
        Assert.Contains(path, text);
        Assert.Matches(@"Saved .+ · [\d,]+ bytes changed", text);
        Assert.Single(dialog.FindAllDescendants(cf => cf.ByAutomationId("Recovery_Restore")));
        Assert.Single(dialog.FindAllDescendants(cf => cf.ByAutomationId("Recovery_Discard")));
        Assert.Contains(dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)), b => AppSession.NameOf(b) == "Later");

        // 4. 「復旧する」で、内容・「変更あり」・カーソル位置が戻り、Undo の件数は 0。
        (await again.WaitForAsync("Recovery_Restore")).Patterns.Invoke.Pattern.Invoke();
        await again.WaitUntilAsync(async () => (await again.StateAsync())["document"]?["name"]?.GetValue<string>() == "seq.bin",
            TimeSpan.FromSeconds(10), "the recovered document");
        JsonObject doc = await again.DocumentAsync();
        Assert.True(doc["modified"]!.GetValue<bool>());
        Assert.Equal(0x2000, doc["cursor"]!.GetValue<long>());
        Assert.Equal(0, doc["undoCount"]!.GetValue<int>());
        Assert.Equal(expected, await again.BytesAsync(0, expected.Length));
        Assert.Equal("● Modified", await again.UiaNameAsync("Status_Modified"));

        // 5. Ctrl+S で保存すると、そのドキュメントの復旧用データが消える。
        await again.KeyAsync("S", ctrl: true);
        await again.IdleAsync();
        Assert.Equal(expected, File.ReadAllBytes(path));
        await again.WaitUntilAsync(() => Task.FromResult(!RecoveryStates(again.RecoveryFolder).Any(s => File.ReadAllText(s).Contains("seq.bin"))),
            TimeSpan.FromSeconds(10), "the recovery data to be removed");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-27-03")]
    public Task Recovery_of_a_changed_file_opens_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        var hooks = new JsonObject { ["recoveryIntervalSeconds"] = 2 };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });
        await EditAsync(app);
        await WaitForRecoveryDataAsync(app);
        app.Kill();

        // 1. 起動する前に、元のファイルのオフセット 0x80000 を書き換える (更新日時が変わる)。
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 0x80000;
            stream.WriteByte(0xEE);
        }

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        // 2. 起動して「復旧する」を押す。
        AppSession again = await ctx.StartAsync(new AppOptions { Hooks = hooks });
        AutomationElement dialog = await WaitForRecoveryDialogAsync(again);
        (await again.WaitForAsync("Recovery_Restore")).Patterns.Invoke.Pattern.Invoke();

        // 3. 警告が出て、読み取り専用で開く。
        await again.WaitUntilAsync(async () => (await again.StateAsync())["notice"]!["open"]!.GetValue<bool>(),
            TimeSpan.FromSeconds(10), "the warning");
        JsonObject state = await again.StateAsync();
        Assert.Contains("original file has changed", state["notice"]!["message"]!.GetValue<string>());
        Assert.True(state["document"]!["readOnly"]!.GetValue<bool>());
        await again.WaitForAsync("Notification");
        Assert.Contains("original file has changed", again.NotificationsText());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-27-04")]
    public Task Recovers_after_unhandled_exception() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");

        // 保存間隔を 60 分にして、定期の書き出しが起きないようにする。
        var hooks = new JsonObject { ["recoveryIntervalSeconds"] = 3600 };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });

        // 1. ファイルのオフセット 0 を AB に上書きし、Ctrl+N の無題のドキュメントに 01 02 03 を入力する。
        await app.TypeAsync("AB");
        await app.KeyAsync("N", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["hexViews"]!.GetValue<int>() >= 2
            || (await app.DocumentAsync())["name"]!.GetValue<string>().StartsWith("Untitled", StringComparison.Ordinal),
            TimeSpan.FromSeconds(10), "the untitled document");
        await app.IdleAsync();
        await app.TypeAsync("010203");
        Assert.Equal(new byte[] { 1, 2, 3 }, await app.BytesAsync(0, 3));
        Assert.Empty(RecoveryStates(app.RecoveryFolder));

        // 2. テスト用のメニューから、UI スレッドで未処理の例外を起こす。
        await app.CommandAsync("TestMenu_ThrowUi");
        await app.WaitForExitAsync(ExitTimeout);

        // 3. 同じ設定フォルダで起動すると、復旧の画面に 2 件出る。
        AppSession again = await ctx.StartAsync(new AppOptions { Hooks = hooks });
        AutomationElement dialog = await WaitForRecoveryDialogAsync(again);
        Assert.Equal(2, dialog.FindAllDescendants(cf => cf.ByAutomationId("Recovery_Restore")).Length);

        // 4. 2 つとも「復旧する」を押す。
        for (int i = 0; i < 2; i++)
        {
            AutomationElement restore = await again.WaitForAsync("Recovery_Restore");
            restore.Patterns.Invoke.Pattern.Invoke();
            await again.IdleAsync();
        }

        JsonArray docs = (await again.StateAsync())["documents"]!.AsArray();
        Assert.Equal(2, docs.Count);
        int fileIndex = docs.Select(d => d!["name"]!.GetValue<string>()).ToList().IndexOf("seq.bin");
        Assert.True(fileIndex >= 0);
        await again.SendAsync("selectTab", new JsonObject { ["index"] = fileIndex });
        Assert.Equal(0xAB, (await again.BytesAsync(0, 1))[0]);
        Assert.Equal(1, (await again.BytesAsync(1, 1))[0]);

        await again.SendAsync("selectTab", new JsonObject { ["index"] = 1 - fileIndex });
        JsonObject untitled = await again.DocumentAsync();
        Assert.Null(untitled["path"]);
        Assert.Equal(3, untitled["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 1, 2, 3 }, await again.BytesAsync(0, 3));
    });

    /// <summary>0x10 に DE AD BE EF を挿入し、0x2000 を 77 に上書きして、カーソルを 0x2000 に置く。</summary>
    private static async Task EditAsync(AppSession app)
    {
        await app.GoToAsync(0x10);
        await app.KeyAsync("Insert");
        await app.TypeAsync("DEADBEEF");
        await app.KeyAsync("Insert");
        await app.GoToAsync(0x2000);
        await app.TypeAsync("77");
        await app.GoToAsync(0x2000);
        Assert.False((await app.DocumentAsync())["insertMode"]!.GetValue<bool>());
    }

    /// <summary>
    /// <see cref="EditAsync"/> の 2 つの編集 (4 バイトの挿入と 1 バイトの上書き) をすべて含む復旧用データが書かれるまで待つ。
    /// 書き出しは 2 秒ごとなので、編集の途中 (挿入だけ) の書き出しがあっても、それでは待ち終えない (混んだランナーでは編集に数秒かかる)。
    /// </summary>
    private static Task WaitForRecoveryDataAsync(AppSession app) =>
        app.WaitUntilAsync(() => Task.FromResult(RecoveryStates(app.RecoveryFolder).Any(s => ChangedBytes(s) >= 5)), TimeSpan.FromSeconds(20), "the recovery data");

    /// <summary>state.json の変更の量 (ChangedBytes。読めなければ -1。書き換えの途中のことがある)。</summary>
    private static long ChangedBytes(string statePath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(statePath)) is JsonObject state
                && state.FirstOrDefault(p => string.Equals(p.Key, "ChangedBytes", StringComparison.OrdinalIgnoreCase)).Value is { } value)
            {
                return value.GetValue<long>();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }

        return -1;
    }

    private static async Task<AutomationElement> WaitForRecoveryDialogAsync(AppSession app)
    {
        await app.WaitForAsync("Recovery_Restore", TimeSpan.FromSeconds(20));
        return app.Find("RecoveryDialog") ?? app.Window;
    }

    private static IEnumerable<string> RecoveryStates(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "state.json", SearchOption.AllDirectories) : [];

    private static string[] CrashReports(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.txt").Where(f => !Path.GetFileName(f).Equals("seen.txt", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
}
