using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 最近使ったファイル (ENG-16、UI-32)、閉じたタブ (UI-12)、閉じるときの確認 (UI-13)、起動時の動作とセッション (UI-30、UI-31)、
/// スタートページと初回起動 (UI-38)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class SessionAndRecentTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>TD-UI-FILES-50 の一部: fileNN.bin (4 KiB、すべてのバイトが NN)。</summary>
    private static string[] Files(UiTestContext ctx, int count, int first = 1)
    {
        string folder = Path.Combine(ctx.Root, "files50");
        Directory.CreateDirectory(folder);
        return [.. Enumerable.Range(first, count).Select(n =>
        {
            string path = Path.Combine(folder, $"file{n:00}.bin");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)n, 4096).ToArray());
            return path;
        })];
    }

    private static Task<JsonObject> FilesStateAsync(AppSession app) => app.SendAsync("files");

    private static async Task<IReadOnlyList<(string Name, bool Pinned)>> RecentAsync(AppSession app) =>
        (await FilesStateAsync(app))["recent"]!.AsArray().Select(r => (r!["name"]!.GetValue<string>(), r["pinned"]!.GetValue<bool>())).ToList();

    private static async Task OpenAndCloseAsync(AppSession app, string path)
    {
        int before = (await app.TabNamesAsync()).Count;
        await app.UiOpenAsync(path);
        await app.WaitForTabsAsync(before + 1);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(before);
    }

    private static async Task ExitAsync(AppSession app)
    {
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(ExitTimeout);
    }

    private static async Task SelectByNameAsync(AppSession app, string name)
    {
        int index = (await app.TabNamesAsync()).ToList().IndexOf(name);
        Assert.True(index >= 0, $"{name} is not open");
        await EditingTests.SelectTabAsync(app, index);
    }

    private static async Task<string> ActiveNameAsync(AppSession app) => (await app.DocumentAsync())["name"]!.GetValue<string>();

    private static async Task<long> CursorAsync(AppSession app) => (await app.DocumentAsync())["cursor"]!.GetValue<long>();

    private static void WriteSettings(string profile, string json) =>
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, " + json + "}");

    // ---- ENG-16、UI-32 最近使ったファイル ----

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-16-02")]
    public Task Reopening_from_recent_files_restores_the_cursor() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 1〜2. 0x12345 へ移動して閉じる。
        await app.GoToAsync(0x12345);
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);

        // 3〜4. 最近使ったファイルの先頭を選ぶと、カーソルが 0x12345。
        await app.SendAsync("openRecentMenuAt", new JsonObject { ["index"] = 0 });
        await app.WaitForTabsAsync(1);
        Assert.Equal(0x12345, await CursorAsync(app));

        // 5. 長さ 0x10000 に切り詰めてから開き直すと、カーソルは末尾。
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(0x10000);
        }

        await app.SendAsync("openRecentMenuAt", new JsonObject { ["index"] = 0 });
        await app.WaitForTabsAsync(1);
        Assert.Equal(0x10000, await CursorAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-32-01")]
    public Task Pinned_file_stays_after_thirty_other_files() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 31);
        AppSession app = await ctx.StartAsync();

        // 前提: file01 を開いて閉じ、ピン留めする (「すべて表示…」の右クリックメニューの「ピン留め」と同じ処理)。
        await OpenAndCloseAsync(app, files[0]);
        Assert.True((await app.SendAsync("recentPin", new JsonObject { ["path"] = files[0] }))["done"]!.GetValue<bool>());

        // 1. file02〜file31 の 30 個を順に開いて閉じる。
        foreach (string file in files.Skip(1))
        {
            await OpenAndCloseAsync(app, file);
        }

        // 2. サブメニューの先頭と一覧にピン留めした file01 があり、ピン留めしていない項目は file31〜file07 の 25 件。
        JsonObject state = await FilesStateAsync(app);
        Assert.StartsWith("file01.bin", state["recentMenu"]![0]!["text"]!.GetValue<string>());
        IReadOnlyList<(string Name, bool Pinned)> recent = await RecentAsync(app);
        Assert.Equal(("file01.bin", true), recent[0]);
        Assert.Equal(Enumerable.Range(7, 25).Reverse().Select(n => $"file{n:00}.bin"), recent.Skip(1).Select(r => r.Name));
        Assert.All(recent.Skip(1), r => Assert.False(r.Pinned));

        // サブメニューはピン留め + 最近の 10 件。
        Assert.Equal(11, state["recentMenu"]!.AsArray().Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-32-02")]
    public Task Clearing_the_list_keeps_pinned_and_undo_restores_everything() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 4);
        AppSession app = await ctx.StartAsync();
        foreach (string file in files)
        {
            await OpenAndCloseAsync(app, file);
        }

        await app.SendAsync("recentPin", new JsonObject { ["path"] = files[0] });
        IReadOnlyList<(string Name, bool Pinned)> before = await RecentAsync(app);
        Assert.Equal(["file01.bin", "file04.bin", "file03.bin", "file02.bin"], before.Select(r => r.Name));

        // 1〜2. 「一覧を消去」で、一覧は file01 (ピン留め) だけ。
        await app.CommandAsync("Command_ClearRecent");
        Assert.Equal([("file01.bin", true)], await RecentAsync(app));

        // 3〜4. InfoBar の「元に戻す」で、4 件が元の順に戻る。
        await app.WaitForNotificationAsync(m => m.Contains("was cleared", StringComparison.Ordinal), "the cleared notice");
        AutomationElement? undo = null;
        await app.WaitUntilAsync(() => Task.FromResult((undo = app.Button("Undo")) is not null), TimeSpan.FromSeconds(10), "the undo button");
        undo!.Patterns.Invoke.Pattern.Invoke();
        await app.IdleAsync();
        Assert.Equal(before, await RecentAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-32-03")]
    public Task Unresponsive_network_path_does_not_slow_the_menu() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-RECENT-NETWORK: 応答しないアドレスのパスと TD-SEQ-1M の 2 件の recent.json。
        string seq = ctx.TestData("TD-SEQ-1M");
        string profile = ctx.NewProfile();
        var recent = new JsonObject
        {
            ["version"] = 1,
            ["items"] = new JsonArray(
                new JsonObject { ["kind"] = "file", ["path"] = @"\\192.0.2.1\share\remote.bin", ["displayName"] = "remote.bin", ["lastOpenedUtc"] = "2026-01-01T00:00:00Z" },
                new JsonObject { ["kind"] = "file", ["path"] = seq, ["displayName"] = Path.GetFileName(seq), ["lastOpenedUtc"] = "2025-12-01T00:00:00Z" }),
        };
        await File.WriteAllTextAsync(Path.Combine(profile, "recent.json"), recent.ToJsonString());
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 1. サブメニューの項目がすぐに用意できる (存在の確認を待たない)。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        JsonObject state = await FilesStateAsync(app);
        Assert.True(watch.ElapsedMilliseconds <= 500, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(2, state["recentMenu"]!.AsArray().Count);

        // 2. 「すべて表示…」の一覧が 500 ms 以内に表示される。
        watch.Restart();
        await app.CommandAsync("Command_ShowAllRecent");
        await app.WaitUntilAsync(async () => await app.IsShownAsync("RecentAll_List"), TimeSpan.FromSeconds(5), "the list");
        Assert.True(watch.ElapsedMilliseconds <= 500, $"{watch.ElapsedMilliseconds} ms");

        // 3. remote.bin は薄く表示されず、「見つかりません」も付かない (ネットワークのパスは確かめない)。
        await Task.Delay(1000);
        AutomationElement list = await app.WaitForAsync("RecentAll_List");
        string text = AppSession.AllText(list);
        Assert.Contains("remote.bin", text);
        Assert.DoesNotContain("Not found", text);
        state = await FilesStateAsync(app);
        Assert.Equal(1.0, state["recentMenu"]![0]!["opacity"]!.GetValue<double>());
        Assert.DoesNotContain("not found", state["recentMenu"]![0]!["text"]!.GetValue<string>());
        await app.InvokeDialogButtonAsync("Close");
    });

    [Fact(Skip = "VHDX の作成・接続とドライブ文字の変更は OS の状態を変えるため、この PC では行わない (CI の配布テストで実行する)。相対パスの記録は Core の RecentFilesTests.PortablePathsAreRelativeToTheExeFolder で確認している")]
    [Trait(UiTest.TC, "TC-UI-32-04")]
    public Task Portable_version_opens_recent_files_after_the_drive_letter_changes() => Task.CompletedTask;

    [Fact(Skip = "ジャンプリスト (UI-35) は配布の担当が作る。recent.maxItems = 0 で一覧 (ピン留めを含む) を消す処理は Core の RecentFilesTests.ZeroMaxItemsClearsEverythingAndStopsRecording で確認している")]
    [Trait(UiTest.TC, "TC-UI-32-05")]
    public Task Zero_max_items_clears_the_jump_list() => Task.CompletedTask;

    // ---- UI-12 閉じたタブを開き直す ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-12-01")]
    public Task Closed_tabs_reopen_in_reverse_order_at_their_places() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 5);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(5);
        foreach ((string name, long cursor) in new[] { ("file02.bin", 0x100L), ("file03.bin", 0x200L), ("file04.bin", 0x300L) })
        {
            await SelectByNameAsync(app, name);
            await app.GoToAsync(cursor);
        }

        // 1. file02、file04、file03 の順に Ctrl+W で閉じる。
        foreach (string name in new[] { "file02.bin", "file04.bin", "file03.bin" })
        {
            await SelectByNameAsync(app, name);
            int count = (await app.TabNamesAsync()).Count;
            await app.KeyAsync("W", ctrl: true);
            await app.WaitForTabsAsync(count - 1);
        }

        // 2〜3. 開き直すたびに、閉じた逆順に元の位置とカーソルで戻る。
        foreach ((string name, long cursor) in new[] { ("file03.bin", 0x200L), ("file04.bin", 0x300L), ("file02.bin", 0x100L) })
        {
            int count = (await app.TabNamesAsync()).Count;
            await app.CommandAsync("Command_ReopenClosedTab");
            await app.WaitForTabsAsync(count + 1);
            Assert.Equal(name, await ActiveNameAsync(app));
            Assert.Equal(cursor, await CursorAsync(app));
        }

        Assert.Equal(files.Select(Path.GetFileName), await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-12-02")]
    public Task Closed_tab_can_be_reopened_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 2);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(2);

        // 1〜2. file02 を閉じて終了し、同じ設定フォルダで起動する。
        await SelectByNameAsync(app, "file02.bin");
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(1);
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync();

        // 3〜4. 閉じたタブを開き直すと file02 のタブが開く。
        await again.WaitForTabsAsync(1);
        await again.CommandAsync("Command_ReopenClosedTab");
        await again.WaitForTabsAsync(2);
        Assert.Contains("file02.bin", await again.TabNamesAsync());
    });

    // ---- UI-13 閉じるときの確認 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-13-01")]
    public Task Closing_a_modified_tab_asks_to_save() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.TypeAsync("FF");

        // 1〜2. Ctrl+W で、本文に「seq.bin への変更を保存しますか?」と変更量 (1 か所) があり、ボタンは「保存」「保存しない」「キャンセル」。
        await app.KeyAsync("W", ctrl: true);
        AutomationElement dialog = await app.WaitForAsync("CloseDialog");
        string text = AppSession.AllText(dialog);
        Assert.Contains("Save changes to seq.bin?", text);
        Assert.Contains("1 places", text);
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains("Save", buttons);
        Assert.Contains("Don't save", buttons);
        Assert.Contains("Cancel", buttons);
        Assert.DoesNotContain("Yes", buttons);
        Assert.DoesNotContain("No", buttons);

        // 3〜4. 「キャンセル」でタブが残り、未保存の印が付いている。
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.Equal(["seq.bin"], await app.TabNamesAsync());
        Assert.StartsWith("● ", (await app.DocumentAsync())["header"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-13-02")]
    public Task Exiting_with_three_modified_documents_lists_them() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 4);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(4);
        for (int i = 0; i < 3; i++)
        {
            await EditingTests.SelectTabAsync(app, i);
            await app.TypeAsync("FF");
        }

        // 1〜2. ファイル > 終了 で、file01〜file03 の 3 件がチェックボックス付きで並び、file04 はない。
        await app.CommandAsync("Command_Exit");
        await app.WaitForAsync("Close_Item");
        AutomationElement[] items = app.Window.FindAllDescendants(cf => cf.ByAutomationId("Close_Item"));
        Assert.Equal(3, items.Length);
        string text = AppSession.AllText(app.Window);
        foreach (string name in new[] { "file01.bin", "file02.bin", "file03.bin" })
        {
            Assert.Contains(name, text);
        }

        Assert.DoesNotContain(items, i => AppSession.AllText(i).Contains("file04.bin", StringComparison.Ordinal));
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(app.Window);
        Assert.Contains("Save selected and close", buttons);
        Assert.Contains("Close without saving", buttons);
        Assert.Contains("Cancel", buttons);
        Assert.DoesNotContain("Yes", buttons);
        Assert.DoesNotContain("No", buttons);

        // 3. 「キャンセル」の後もアプリは終了していない。
        await app.InvokeDialogButtonAsync("Cancel");
        await Task.Delay(500);
        Assert.False(app.Process.HasExited);
        Assert.Equal(4, (await app.TabNamesAsync()).Count);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-13-03")]
    public Task Closing_while_saving_can_wait_for_the_save() => UiTestContext.RunAsync(async ctx =>
    {
        // TD-UI-HOOK-SLOW-5M の代わりに、読み込み 1 回ごとに遅延を入れる (保存は 4 MiB ずつ読むため、数秒かかる)。
        string path = ctx.CopyTestData("TD-RANDOM-16M", "random.bin");
        var hooks = new JsonObject
        {
            ["fileSources"] = new JsonArray(new JsonObject { ["match"] = "random.bin", ["delayMs"] = 800, ["delayFromOffset"] = 65536 }),
        };
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = hooks });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");

        // 1〜3. 保存中に Ctrl+W を押すと、保存を実行中であることと「保存が終わったら閉じる」「処理を中止して閉じる」「キャンセル」が出る。
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() > 0, TimeSpan.FromSeconds(10), "the save");
        await app.KeyAsync("W", ctrl: true);
        AutomationElement dialog = await app.WaitForAsync("CloseDialog");
        Assert.Contains("is running", AppSession.AllText(dialog));
        IReadOnlyList<string> buttons = UiHelpers.DialogButtons(dialog);
        Assert.Contains("Close after saving", buttons);
        Assert.Contains("Stop and close", buttons);
        Assert.Contains("Cancel", buttons);

        // 4〜5. 「保存が終わったら閉じる」で、保存の完了後にタブが閉じ、ファイルの長さは 16,777,217 バイト。
        app.Button("Close after saving")!.Patterns.Invoke.Pattern.Invoke();
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 0, TimeSpan.FromSeconds(60), "the tab to close");
        Assert.Equal(16_777_217, new FileInfo(path).Length);
    });

    // ---- UI-30 起動時の動作、UI-31 セッション ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-30-01")]
    public Task Never_restore_shows_only_the_start_page() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, "\"session.restoreOnStartup\": \"never\"");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [ctx.CopyTestData("TD-SEQ-1M", "seq.bin")] });
        await ExitAsync(app);

        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        await again.IdleAsync();
        Assert.Empty(await again.TabNamesAsync());
        Assert.True((await FilesStateAsync(again))["startPageVisible"]!.GetValue<bool>());
        Assert.False((await FilesStateAsync(again))["canRestoreSession"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-30-02")]
    public Task Ask_offers_to_restore_the_previous_session() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, "\"session.restoreOnStartup\": \"ask\"");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = profile,
            Files = [ctx.CopyTestData("TD-SEQ-1M", "seq.bin"), ctx.CopyTestData("TD-BYTES-256", "bytes.bin")],
        });
        await app.WaitForTabsAsync(2);
        await ExitAsync(app);

        // 2. スタートページに「前回のセッションを復元」がある。
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile });
        await again.WaitUntilAsync(async () => await again.IsShownAsync("Start_RestoreSession"), TimeSpan.FromSeconds(10), "the restore button");
        Assert.Empty(await again.TabNamesAsync());

        // 3〜4. 押すと 2 つのタブが戻る。
        await again.UiaInvokeAsync("Start_RestoreSession");
        await again.WaitForTabsAsync(2);
        Assert.Equal(["seq.bin", "bytes.bin"], await again.TabNamesAsync());
    });

    /// <summary>
    /// 1 つのウィンドウのタブの並び・アクティブなタブ・カーソル・ウィンドウの位置と大きさ。2 つのウィンドウの TC-UI-31-01 は
    /// WindowManagementTests。
    /// </summary>
    [Fact]
    public Task Session_restores_tabs_cursors_and_bounds() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 6);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(6);
        for (int i = 0; i < 6; i++)
        {
            await EditingTests.SelectTabAsync(app, i);
            await app.GoToAsync(0x10 * (i + 1));
        }

        await EditingTests.SelectTabAsync(app, 2);
        await app.ResizeAsync(1100, 760);
        string bounds = (await FilesStateAsync(app))["windowBounds"]!.GetValue<string>();
        await ExitAsync(app);

        AppSession again = await ctx.StartAsync();
        await again.WaitForTabsAsync(6);
        Assert.Equal(files.Select(Path.GetFileName), await again.TabNamesAsync());
        Assert.Equal("file03.bin", await ActiveNameAsync(again));
        Assert.Equal(bounds, (await FilesStateAsync(again))["windowBounds"]!.GetValue<string>());
        for (int i = 0; i < 6; i++)
        {
            await EditingTests.SelectTabAsync(again, i);
            Assert.Equal(0x10 * (i + 1), await CursorAsync(again));
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-31-03")]
    public Task Deleted_file_keeps_its_tab_with_a_message() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 2);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = files });
        await app.WaitForTabsAsync(2);
        await ExitAsync(app);
        File.Delete(files[0]);

        AppSession again = await ctx.StartAsync();
        await again.WaitForTabsAsync(2);
        await EditingTests.SelectTabAsync(again, 0);
        await again.WaitUntilAsync(async () => await again.IsShownAsync("Session_MissingText"), TimeSpan.FromSeconds(10), "the missing text");
        Assert.Equal($"File not found: {files[0]}", (await again.ElementAsync("Session_MissingText"))["text"]!.GetValue<string>());
        Assert.True(await again.IsShownAsync("Session_Close"));
        Assert.True(await again.IsShownAsync("Session_Locate"));
        Assert.Equal("Close", (await again.ElementAsync("Session_Close"))["content"]!.GetValue<string>());
        Assert.Equal("Locate...", (await again.ElementAsync("Session_Locate"))["content"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-31-04")]
    public Task Session_survives_forced_termination() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = Files(ctx, 3);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [files[0]] });

        // 1〜3. file02、file03 を開き、35 秒待って強制終了する。
        await app.UiOpenAsync(files[1]);
        await app.UiOpenAsync(files[2]);
        await app.WaitForTabsAsync(3);
        await Task.Delay(TimeSpan.FromSeconds(35));
        app.Kill();

        // 4. 同じ設定フォルダで起動すると 3 つのタブが戻る。
        AppSession again = await ctx.StartAsync();
        await again.WaitForTabsAsync(3);
        Assert.Equal(files.Select(Path.GetFileName), await again.TabNamesAsync());
    });

    // ---- UI-38 スタートページと初回起動 ----

    [Fact]
    [Trait(UiTest.TC, "TC-UI-38-01")]
    public Task Welcome_is_shown_only_on_the_first_start() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1. 「はじめに」があり、3 つの項目に既定値が選ばれている。ネットワークを使う機能の 1 行がある。
        await app.WaitUntilAsync(async () => await app.IsShownAsync("Start_Welcome"), TimeSpan.FromSeconds(10), "the welcome panel");
        foreach (string id in new[] { "Start_Language", "Start_Theme", "Start_Preset" })
        {
            Assert.Equal(0, (await app.ElementAsync(id))["selectedIndex"]!.GetValue<int>());
        }

        Assert.Contains("internet", (await app.ElementAsync("Start_NetworkInfo"))["text"]!.GetValue<string>());

        // 2. 「閉じる」で消える。
        await app.UiaInvokeAsync("Start_WelcomeClose");
        await app.WaitUntilAsync(async () => !await app.IsShownAsync("Start_Welcome"), TimeSpan.FromSeconds(10), "the panel to close");

        // 3〜4. 終了して起動し直しても出ない。
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync();
        await again.WaitForAsync("Start_Open");
        Assert.False(await again.IsShownAsync("Start_Welcome"));
        Assert.False((await FilesStateAsync(again))["welcome"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-38-02")]
    public Task Defaults_work_without_touching_the_welcome_panel() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        Assert.True((await FilesStateAsync(app))["welcome"]!.GetValue<bool>());

        // 1〜2. 何も選ばずに開くと「はじめに」が消え、表示言語は英語、テーマは system、プリセットは default。
        await app.UiOpenAsync(ctx.TestData("TD-SEQ-1M"));
        await app.WaitForTabsAsync(1);
        JsonObject state = await FilesStateAsync(app);
        Assert.False(state["welcome"]!.GetValue<bool>());
        Assert.Equal("system", state["language"]!.GetValue<string>());
        Assert.Equal("en", (await app.StateAsync())["uiCulture"]!.GetValue<string>()[..2]);
        Assert.Equal("system", state["theme"]!.GetValue<string>());
        Assert.Equal("default", state["preset"]!.GetValue<string>());

        // 3. Ctrl+G で移動バーが開く (既定のショートカットで使える)。
        await app.KeyAsync("G", ctrl: true);
        Assert.True((await app.StateAsync())["goToBarVisible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-UI-38-03")]
    public Task Recent_file_opens_from_the_start_page() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq] });
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(0);

        JsonObject state = await FilesStateAsync(app);
        Assert.True(state["startPageVisible"]!.GetValue<bool>());
        Assert.Equal(Path.GetFileName(seq), state["startRecent"]![0]!["name"]!.GetValue<string>());

        // 1〜2. スタートページの一覧から選ぶ (Enter と同じ処理) と、タブが開く。
        await app.SendAsync("openRecentAt", new JsonObject { ["index"] = 0 });
        await app.WaitForTabsAsync(1);
        Assert.Equal([Path.GetFileName(seq)], await app.TabNamesAsync());
    });
}
