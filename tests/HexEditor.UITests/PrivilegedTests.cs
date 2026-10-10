using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 管理者権限・仮想ディスク (TD-VHDX-MBR)・配布形態のインストール・本物の補助プロセスの昇格が要るテスト (ENG-14、ENG-28、ENG-32、
/// PKG-04、PKG-14、UI-34)。CI のジョブ ui-privileged だけで動く (<see cref="CiPrivilegedFactAttribute"/>。開発者の PC ではスキップし、
/// 昇格・仮想ディスクの作成・インストールをしない)。ジョブは配布形態ごとに 1 つずつ (Msix / Installer / Portable) 動き、
/// build/tests/Mount-TestVhd.ps1 で TD-VHDX-MBR を接続し、Install-TestBuild.ps1 でテスト用のビルドを入れる。ランナーのテストの
/// プロセスは管理者として動く (UAC は「確認なしで昇格」)。「一般ユーザー (CI)」のケースはアプリを昇格していないトークンで起動する。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PrivilegedTests
{
    private static readonly Lazy<Dictionary<string, string>> En = new(() => UiHelpers.LoadResw("en"));

    private static string R(string key) => En.Value[key];

    // ---- 共通 ----

    /// <summary>アプリを起動し、昇格の状態が期待どおりか確かめる (「管理者 (CI)」か「一般ユーザー (CI)」)。</summary>
    private static async Task<AppSession> StartAsync(UiTestContext ctx, bool elevated, JsonObject? hooks = null, IReadOnlyList<string>? files = null)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Restricted = !elevated, Hooks = hooks, Files = files ?? [] });
        JsonObject info = await app.SendAsync("helperInfo");
        Assert.True(info["elevated"]!.GetValue<bool>() == elevated, $"The app should run {(elevated ? "as administrator" : "without administrator rights")}.");
        Assert.Equal(elevated, CiPrivileged.IsElevated(app.Pid));
        return app;
    }

    /// <summary>「ディスクを開く」のダイアログを開く (ファイル > ディスクを開く…)。</summary>
    private static async Task<AutomationElement> OpenDiskDialogAsync(AppSession app)
    {
        await app.CommandAsync("Command_OpenDisk");
        return await app.WaitForDialogAsync("OpenDiskDialog");
    }

    /// <summary>ディスク・プロセスの一覧のダイアログで、文字に <paramref name="text"/> を含む行を選び、「開く」を押す。</summary>
    private static async Task ChooseAndOpenAsync(AppSession app, AutomationElement dialog, string text, bool idle = true)
    {
        AutomationElement? item = null;
        await app.WaitUntilAsync(() => Task.FromResult((item = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
            .FirstOrDefault(i => AppSession.NameOf(i).Contains(text, StringComparison.Ordinal))) is not null), TimeSpan.FromSeconds(15), $"the list item '{text}'");
        item!.Patterns.SelectionItem.Pattern.Select();
        await app.InvokeDialogButtonAsync(R("Common_Open"), idle);
    }

    /// <summary>ダイアログの「読み取り専用で開く」のチェックの状態。</summary>
    private static bool ReadOnlyChecked(AutomationElement dialog) =>
        AppSession.IsToggled(dialog.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox))
            ?? throw new InvalidOperationException("The read-only check box was not found."));

    private static async Task<JsonObject> DiskAsync(AppSession app, int number) =>
        (await app.SendAsync("enumDevices"))["disks"]!.AsArray().Select(d => d!.AsObject()).Single(d => d["number"]!.GetValue<int>() == number);

    private static bool ReadOnly(JsonObject doc) => doc["readOnly"]!.GetValue<bool>();

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>案内ダイアログ (ENG-28 の仕様 12) の文字とボタン。</summary>
    private static async Task<(string Text, IReadOnlyList<string> Buttons)> GuidanceAsync(AppSession app, params string[] expected)
    {
        AutomationElement dialog = await app.WaitForDialogAsync("AdminGuidanceDialog");
        string text = await app.WaitForDialogTextAsync(dialog, expected);
        return (text, UiHelpers.DialogButtons(dialog));
    }

    // ---- ENG-14: ディスクは既定で読み取り専用、解除時に確認 ----

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-ENG-14-02")]
    public Task Disk_opens_read_only_and_allowing_writes_asks_with_the_model_and_size() => UiTestContext.RunAsync(async ctx =>
    {
        string before = Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 1 << 20));
        AppSession app = await StartAsync(ctx, elevated: true);
        JsonObject disk = await DiskAsync(app, CiPrivileged.VhdDisk);

        // 1. 「読み取り専用で開く」の初期状態。2. そのまま TD-VHDX-MBR を開く。
        AutomationElement dialog = await OpenDiskDialogAsync(app);
        Assert.True(ReadOnlyChecked(dialog));
        await ChooseAndOpenAsync(app, dialog, CiPrivileged.VhdDiskPath);
        await app.WaitForTabsAsync(1);
        Assert.True(ReadOnly(await app.DocumentAsync()));

        // 3. 「編集を許可する」(編集 > 読み取り専用の解除)。4. 確認ダイアログの本文 (モデル名とサイズ) とボタン。
        await app.CommandAsync("Command_ReadOnly");
        AutomationElement confirm = await app.WaitForDialogAsync("ReadOnlyConfirmDialog");
        string displayName = disk["displayName"]!.GetValue<string>();
        await app.WaitForDialogTextAsync(confirm, R("ReadOnly_Confirm_Device"), displayName);
        Assert.Contains(disk["model"]!.GetValue<string>(), displayName, StringComparison.Ordinal);
        Assert.Equal(new[] { R("ReadOnly_AllowWriting"), R("Common_Cancel") }.Order(), UiHelpers.DialogButtons(confirm).Order());

        // 5. 「書き込みを許可」: 読み取り専用が解除され、ディスクの内容は変わっていない。
        await app.InvokeDialogButtonAsync(R("ReadOnly_AllowWriting"));
        await app.WaitUntilAsync(async () => !ReadOnly(await app.DocumentAsync()), TimeSpan.FromSeconds(10), "the read-only state to be released");
        Assert.Equal(before, Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 1 << 20)));
    });

    // ---- ENG-28 / PKG-14: 管理者として実行すると補助プロセスを使わない ----

    [CiPrivilegedFact("Installer", "Portable")]
    [Trait(UiTest.TC, "TC-ENG-28-03")]
    [Trait(UiTest.TC, "TC-PKG-14-03")]
    public Task Elevated_app_opens_the_disk_in_its_own_process() => UiTestContext.RunAsync(async ctx =>
    {
        // インストーラ版とポータブル版のジョブのそれぞれで動く (TC-PKG-14-03 の手順 4)。
        AppSession app = await StartAsync(ctx, elevated: true);
        await using var watch = new ProcessWatch();

        // 1. TD-VHDX-MBR の物理ディスクを開き、先頭セクタを読む (末尾が 55 AA)。
        AutomationElement dialog = await OpenDiskDialogAsync(app);
        await ChooseAndOpenAsync(app, dialog, CiPrivileged.VhdDiskPath);
        await app.WaitForTabsAsync(1);
        Assert.Equal(new byte[] { 0x55, 0xAA }, await app.BytesAsync(0x1FE, 2));

        // 2. HexEditor.Elevated.exe と consent.exe (UAC の確認) が作られていない。3. 盾のアイコンが出ない。
        JsonObject info = await app.SendAsync("helperInfo");
        Assert.False(info["running"]!.GetValue<bool>());
        Assert.Equal(0, info["launches"]!.GetValue<int>());
        Assert.False(info["shieldVisible"]!.GetValue<bool>());
        Assert.Empty(watch.Seen);
    });

    // ---- ENG-28 / PKG-04 / PKG-14: 管理者として実行していない Store 版の案内 ----

    /// <summary>Store 版の案内ダイアログ (ENG-28 の仕様 12、PKG-04 の仕様 2 の 3) を確かめて閉じる。</summary>
    private static async Task AssertStoreDiskGuidanceAsync(AppSession app, string diskPath)
    {
        // 「ディスクを開く…」は有効 (無効にしない)。
        JsonObject command = (await app.SendAsync("commands"))["items"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["id"]!.GetValue<string>() == "file.openDisk");
        Assert.True(command["enabled"]!.GetValue<bool>());

        AutomationElement dialog = await OpenDiskDialogAsync(app);
        await ChooseAndOpenAsync(app, dialog, diskPath, idle: false);

        // 理由、番号付きの手順、USB ストレージの代わりの方法、インストーラ版・ポータブル版の案内とダウンロードページへのリンク。
        (string text, IReadOnlyList<string> buttons) = await GuidanceAsync(app, R("AdminGuide_Disk"), R("AdminGuide_Steps"),
            R("AdminGuide_UsbAlternative"), R("AdminGuide_StoreDownload"));
        Assert.Contains("1.", text, StringComparison.Ordinal);
        Assert.NotNull(app.Find("AdminGuide_DownloadLink"));

        // ボタンは「閉じる」だけ (「管理者として再起動」がない)。
        Assert.Equal([R("Common_Close")], buttons);
        await app.InvokeDialogButtonAsync(R("Common_Close"));
        Assert.Equal(0, (await app.SendAsync("helperInfo"))["launches"]!.GetValue<int>());
    }

    [CiPrivilegedFact("Msix")]
    [Trait(UiTest.TC, "TC-ENG-28-04")]
    public Task Store_edition_without_admin_shows_the_guidance_for_the_vhd() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, elevated: false);
        await AssertStoreDiskGuidanceAsync(app, CiPrivileged.VhdDiskPath);
    });

    [CiPrivilegedFact("Msix")]
    [Trait(UiTest.TC, "TC-PKG-04-02")]
    public Task Store_edition_without_admin_shows_the_guidance_for_disk_0() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, elevated: false);
        await AssertStoreDiskGuidanceAsync(app, @"\\.\PhysicalDrive0");
    });

    [CiPrivilegedFact("Msix")]
    [Trait(UiTest.TC, "TC-PKG-14-04")]
    public Task Store_package_has_no_helper_and_shows_the_guidance() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, elevated: false);

        // 1. パッケージのインストール先に HexEditor.Elevated.exe がない。
        string installed = Path.GetDirectoryName(AppLocator.ImagePath)!;
        Assert.Contains(@"\WindowsApps\", installed, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(installed, "HexEditor.Elevated.exe", SearchOption.AllDirectories));

        // 2・3. ディスク 0 を開こうとすると案内ダイアログ。
        await AssertStoreDiskGuidanceAsync(app, @"\\.\PhysicalDrive0");
    });

    [CiPrivilegedFact("Msix")]
    [Trait(UiTest.TC, "TC-ENG-32-03")]
    public Task Store_edition_without_admin_shows_the_guidance_for_an_elevated_process() => UiTestContext.RunAsync(async ctx =>
    {
        // TestTarget はテストのプロセス (CI のランナーの管理者) から起動するため、管理者として動く。
        using TestTargetProcess target = await TestTargetProcess.StartAsync(ctx);
        Assert.True(CiPrivileged.IsElevated(target.Pid));
        AppSession app = await StartAsync(ctx, elevated: false);

        // 1. Ctrl+Shift+M で管理者の TestTarget を選んで「開く」。
        await app.KeyAsync("M", ctrl: true, shift: true);
        AutomationElement dialog = await app.WaitForDialogAsync("OpenProcessDialog");
        await ChooseAndOpenAsync(app, dialog, $"(PID {target.Pid})", idle: false);

        // 2. 「管理者権限で開く」の確認ではなく案内ダイアログ。理由と手順があり、「管理者として再起動」がない。
        (string text, IReadOnlyList<string> buttons) = await GuidanceAsync(app, R("AdminGuide_Process"), R("AdminGuide_Steps"));
        Assert.Contains("1.", text, StringComparison.Ordinal);
        Assert.Null(app.Find("OpenProcessElevatedDialog"));
        Assert.Equal([R("Common_Close")], buttons);
        await app.InvokeDialogButtonAsync(R("Common_Close"));
    });

    [CiPrivilegedFact("Msix")]
    [Trait(UiTest.TC, "TC-PKG-04-04")]
    public Task Store_edition_without_admin_opens_a_process_of_the_same_user() => UiTestContext.RunAsync(async ctx =>
    {
        // メモ帳の代わりに、同じ一般の権限で起動した TestTarget に `HEXEDITOR-PROC-TEST` (UTF-16 LE) を持たせる (テストケースの備考)。
        const string Text = "HEXEDITOR-PROC-TEST";
        using TestTargetProcess target = await TestTargetProcess.StartAsync(ctx, restricted: true, text: Text);
        Assert.False(CiPrivileged.IsElevated(target.Pid));
        AppSession app = await StartAsync(ctx, elevated: false);
        await using var watch = new ProcessWatch();

        // 1. Ctrl+Shift+M で TestTarget を選んで開く: 案内ダイアログ・UAC の確認が出ずにタブが開く。
        await app.KeyAsync("M", ctrl: true, shift: true);
        AutomationElement dialog = await app.WaitForDialogAsync("OpenProcessDialog");
        await ChooseAndOpenAsync(app, dialog, $"(PID {target.Pid})");
        await app.WaitForTabsAsync(1);
        Assert.Null(app.Find("AdminGuidanceDialog"));
        Assert.Null(app.Find("OpenProcessElevatedDialog"));
        Assert.Empty(watch.Seen);

        // 2. UTF-16 LE のテキストを検索して 1 件以上一致する (文字列を置いたページの手前から探す)。
        await app.GoToAsync(target.TextAddress!.Value - 0x100);
        await GoToAndFindTests.OpenFindAsync(app);
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Kind", ["index"] = 1 });
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Find_Encoding", ["text"] = "UTF-16 LE" });
        await app.UiaSetValueAsync("Find_Query", Text);
        await GoToAndFindTests.FindNextAsync(app);
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(Text.Length * 2, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(System.Text.Encoding.Unicode.GetBytes(Text), await app.BytesAsync(doc["selectionStart"]!.GetValue<long>(), Text.Length * 2));
    });

    // ---- ENG-32: 管理者として動いているプロセスを開く (補助プロセス経由) ----

    [CiPrivilegedFact("Installer", "Portable")]
    [Trait(UiTest.TC, "TC-ENG-32-02")]
    public Task Elevated_process_opens_through_the_helper_after_confirmation() => UiTestContext.RunAsync(async ctx =>
    {
        using TestTargetProcess target = await TestTargetProcess.StartAsync(ctx);
        Assert.True(CiPrivileged.IsElevated(target.Pid));
        AppSession app = await StartAsync(ctx, elevated: false);

        // 1. Ctrl+Shift+M で管理者の TestTarget を選んで「開く」。2. 確認ダイアログの本文とボタン。
        await app.KeyAsync("M", ctrl: true, shift: true);
        AutomationElement dialog = await app.WaitForDialogAsync("OpenProcessDialog");
        await ChooseAndOpenAsync(app, dialog, $"(PID {target.Pid})", idle: false);
        AutomationElement confirm = await app.WaitForDialogAsync("OpenProcessElevatedDialog");
        await app.WaitForDialogTextAsync(confirm, R("OpenProcess_NeedsAdmin"));
        Assert.Equal(new[] { R("OpenProcess_OpenElevated"), R("Common_Cancel") }.Order(), UiHelpers.DialogButtons(confirm).Order());

        // 3. 「管理者権限で開く」: 補助プロセスが起動してプロセスが開く (ランナーの UAC は確認なしで昇格する)。
        await app.InvokeDialogButtonAsync(R("OpenProcess_OpenElevated"));
        await app.WaitForTabsAsync(1);
        JsonObject info = await app.SendAsync("helperInfo");
        Assert.True(info["running"]!.GetValue<bool>());
        Assert.True(CiPrivileged.IsElevated(info["pid"]!.GetValue<int>()));

        // 4. 変数のアドレスの 8 バイト。
        await app.GoToAsync(target.ValueAddress);
        Assert.Equal(TestTargetProcess.Value, await app.BytesAsync(target.ValueAddress, 8));
    });

    // ---- ENG-28: 補助プロセスがないときの「管理者として再起動」 ----

    [CiPrivilegedFact("Installer")]
    [Trait(UiTest.TC, "TC-ENG-28-05")]
    public Task Without_the_helper_restart_as_administrator_restores_the_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        string helper = Path.Combine(Path.GetDirectoryName(AppLocator.ExePath)!, "HexEditor.Elevated.exe");
        string moved = helper + ".removed";
        File.Move(helper, moved, overwrite: true);
        try
        {
            string seq = ctx.CopyTestData("TD-SEQ-1M");
            AppSession app = await StartAsync(ctx, elevated: false, files: [seq]);
            await app.WaitForTabsAsync(1);

            // 1. TD-VHDX-MBR を開こうとする。2. 案内ダイアログに「管理者として再起動」と「閉じる」。
            AutomationElement dialog = await OpenDiskDialogAsync(app);
            await ChooseAndOpenAsync(app, dialog, CiPrivileged.VhdDiskPath, idle: false);
            (_, IReadOnlyList<string> buttons) = await GuidanceAsync(app, R("AdminGuide_Disk"));
            Assert.Equal(new[] { R("AdminGuide_Restart"), R("Common_Close") }.Order(), buttons.Order());

            // 3. 「管理者として再起動」: 元のプロセスが終わり、新しいプロセスが起動する。
            DateTime since = DateTime.Now.AddSeconds(-1);
            int oldPid = app.Pid;
            await app.InvokeDialogButtonAsync(R("AdminGuide_Restart"), idle: false);
            await app.WaitForExitAsync(UiTest.Scaled(TimeSpan.FromSeconds(30)));
            Process? restarted = null;
            await app.WaitUntilAsync(() => Task.FromResult((restarted = FindAppProcess(since, oldPid)) is not null), UiTest.Scaled(TimeSpan.FromSeconds(60)),
                "the restarted process");
            AppSession again = await ctx.AttachAsync(restarted!, app.Profile);

            // 4. 新しいプロセスは管理者として動き、TD-SEQ-1M のタブが復元されている。
            Assert.True(CiPrivileged.IsElevated(again.Pid));
            await again.WaitForTabsAsync(1);
            Assert.Equal([Path.GetFileName(seq)], await again.TabNamesAsync());
        }
        finally
        {
            File.Move(moved, helper, overwrite: true);
        }
    });

    /// <summary><paramref name="since"/> 以降に起動した、テスト対象と同じ実行ファイルのプロセス (<paramref name="except"/> を除く)。</summary>
    private static Process? FindAppProcess(DateTime since, int except)
    {
        foreach (Process p in Process.GetProcessesByName("HexEditor"))
        {
            try
            {
                if (p.Id != except && p.StartTime >= since && AppLocator.IsAppProcess(p))
                {
                    return p;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            p.Dispose();
        }

        return null;
    }

    // ---- ENG-28: UI のプロセスの強制終了で補助プロセスも終了し、ロックが解除される ----

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-ENG-28-08")]
    public Task Killing_the_app_ends_the_helper_and_releases_the_volume_lock() => UiTestContext.RunAsync(async ctx =>
    {
        string marker = Path.Combine(ctx.Root, "paused-after-lock.txt");
        AppSession app = await StartAsync(ctx, elevated: false, hooks: new JsonObject { ["pauseAfterVolumeLock"] = marker });
        JsonObject volume = (await app.SendAsync("enumDevices"))["volumes"]!.AsArray().Select(v => v!.AsObject())
            .First(v => string.Equals(v["letter"]?.GetValue<string>()?.TrimEnd(':'), CiPrivileged.VhdVolumeLetter, StringComparison.OrdinalIgnoreCase));
        long volumeOffset = volume["diskOffset"]!.GetValue<long>();

        // 物理ディスクを (補助プロセス経由で) 開き、読み取り専用を解除する。
        AutomationElement dialog = await OpenDiskDialogAsync(app);
        await ChooseAndOpenAsync(app, dialog, CiPrivileged.VhdDiskPath);
        await app.WaitForTabsAsync(1);
        await app.CommandAsync("Command_ReadOnly");
        await app.WaitForDialogAsync("ReadOnlyConfirmDialog");
        await app.InvokeDialogButtonAsync(R("ReadOnly_AllowWriting"));
        await app.WaitUntilAsync(async () => !ReadOnly(await app.DocumentAsync()), TimeSpan.FromSeconds(10), "the read-only state to be released");
        int helperPid = (await app.SendAsync("helperInfo"))["pid"]!.GetValue<int>();
        using Process helper = Process.GetProcessById(helperPid);

        // 1. ボリュームの範囲を書き換えて保存し、ロックの後・書き込みの前で止まる (テスト用のフック)。書き込みは行われない。
        await app.SendAsync("click", new JsonObject { ["offset"] = volumeOffset + 0x200000, ["column"] = "Hex" });
        await app.TypeAsync("AB");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitForDialogAsync("DiskWriteConfirmDialog");
        await app.InvokeDialogButtonAsync(R("DiskWrite_Write"), idle: false);
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(marker)), UiTest.Scaled(TimeSpan.FromSeconds(60)), "the pause after the volume lock");
        Assert.False(CiPrivileged.TryLockVolume(CiPrivileged.VhdVolumeLetter, out _), "the helper should hold the volume lock");

        // 2. UI のプロセスを強制終了する。3. 補助プロセスが 5 秒以内に終了する。
        var watch = Stopwatch.StartNew();
        app.Kill();
        Assert.True(helper.WaitForExit(TimeSpan.FromSeconds(5) + TimeSpan.FromMilliseconds(500)), "the helper did not exit within 5 s");
        watch.Stop();
        Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(5.5), $"{watch.Elapsed.TotalSeconds:F1} s");

        // 4. テストのコードがボリュームをロックできる (補助プロセスのロックが解除されている)。
        Assert.True(CiPrivileged.TryLockVolume(CiPrivileged.VhdVolumeLetter, out int error), $"FSCTL_LOCK_VOLUME failed: {error}");
    });

    // ---- PKG-14: 別のプロセスからのパイプへの接続の拒否 ----

    [CiPrivilegedFact("Installer")]
    [Trait(UiTest.TC, "TC-PKG-14-05")]
    public Task Other_processes_cannot_use_the_helper_pipe() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await StartAsync(ctx, elevated: false);
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = CiPrivileged.VhdDiskPath });
        Assert.Equal("Helper", opened["route"]!.GetValue<string>());
        // パイプ名は補助プロセスのコマンドラインの代わりに、アプリのテスト用の命令から取る (同じ値)。
        string pipe = (await app.SendAsync("helperInfo"))["pipe"]!.GetValue<string>();

        // ReadSectors の形の要求 (長さ 12、要求 ID 1、コマンド・フラグ、ハンドル 1・オフセット 0)。合言葉は知らない。
        const string Request = "0C000000010000000700000001000000" + "0000000000000000";

        // 1. テスト用のプロセス (PowerShell。HexEditor.exe ではない) から接続して要求を送る。
        string script = $"$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '{pipe}', [System.IO.Pipes.PipeDirection]::InOut); " +
            "try { $p.Connect(3000) } catch { 'NOT-CONNECTED'; exit 0 }; " +
            $"$b = [Convert]::FromHexString('{Request}'); try {{ $p.Write($b, 0, $b.Length); $p.Flush(); $buf = New-Object byte[] 4096; " +
            "$t = $p.ReadAsync($buf, 0, 4096); if ($t.Wait(3000)) { 'READ ' + $t.Result } else { 'NO-RESPONSE' } } catch { 'CLOSED' }";
        var info = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        using Process rogue = Process.Start(info)!;
        string rogueResult = (await rogue.StandardOutput.ReadToEndAsync()).Trim();
        await rogue.WaitForExitAsync();
        Assert.True(rogueResult is "NOT-CONNECTED" or "NO-RESPONSE" or "CLOSED" or "READ 0", $"the test process got: {rogueResult}");

        // 2. 同じ利用者の別の HexEditor.exe (別のフォルダに置いた同じ版) から接続して、同じ要求を送る。
        string copy = Path.Combine(ctx.Root, "other-copy");
        CopyFolder(Path.GetDirectoryName(AppLocator.ExePath)!, copy);
        Process otherProcess = CiPrivileged.StartRestricted(Path.Combine(copy, Path.GetFileName(AppLocator.ExePath)),
            ["--new-instance", "--test-profile", ctx.NewProfile(), "--test-hooks", ctx.WriteFile("hooks-other.json", "{}"u8.ToArray()), "--ui-lang", "en"], copy);
        using (TestChannelClient channel = await TestChannelClient.ConnectAsync(otherProcess.Id, () => otherProcess.HasExited, TimeSpan.FromSeconds(60))
            ?? throw new InvalidOperationException("The other HexEditor did not open the test channel."))
        {
            JsonObject probe = await channel.SendAsync("pipeProbe", new JsonObject { ["name"] = pipe, ["hex"] = Request, ["timeoutMs"] = 3000 });
            Assert.Equal(0, probe["response"]!.GetValue<int>());
            await channel.SendAsync("exit");
        }

        if (!otherProcess.WaitForExit(30_000))
        {
            otherProcess.Kill();
        }

        otherProcess.Dispose();

        // 3. 元のアプリからの読み込みは引き続きできる (まだ読んでいない範囲を補助プロセス経由で読む)。
        Assert.Equal(new byte[] { 0x55, 0xAA }, await app.BytesAsync(0x1FE, 2));
        byte[] later = await app.BytesAsync(32L << 20, 16);
        Assert.Equal(16, later.Length);
        Assert.True((await app.SendAsync("helperInfo"))["running"]!.GetValue<bool>());
        IReadOnlyList<string> log = await app.LogAsync();
        Assert.DoesNotContain(log, l => l.Contains("The elevated helper disconnected", StringComparison.Ordinal));
    });

    private static void CopyFolder(string source, string target)
    {
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }

        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    // ---- PKG-14: 差し替えた補助プロセスは起動しない ----

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-PKG-14-06")]
    public Task Replaced_helper_is_not_started() => UiTestContext.RunAsync(async ctx =>
    {
        string helper = Path.Combine(Path.GetDirectoryName(AppLocator.ExePath)!, "HexEditor.Elevated.exe");
        string original = helper + ".original";
        File.Copy(helper, original, overwrite: true);
        File.Copy(ctx.TestData("TD-PE-X64"), helper, overwrite: true);
        try
        {
            AppSession app = await StartAsync(ctx, elevated: false);
            await using var watch = new ProcessWatch();

            // 1. ディスク 0 を開こうとする。2. consent.exe と HexEditor.Elevated.exe が作られない。3. 改ざんの可能性の InfoBar。
            AutomationElement dialog = await OpenDiskDialogAsync(app);
            await ChooseAndOpenAsync(app, dialog, @"\\.\PhysicalDrive0", idle: false);
            await app.WaitForNotificationAsync(m => m.Contains(R("Helper_Tampered"), StringComparison.Ordinal), "the tampered helper InfoBar");
            Assert.Empty(watch.Seen);
            Assert.Equal(0, (await app.SendAsync("helperInfo"))["launches"]!.GetValue<int>());
            Assert.Empty(await app.TabNamesAsync());
        }
        finally
        {
            File.Copy(original, helper, overwrite: true);
            File.Delete(original);
        }
    });

    // ---- UI-34: 管理者として実行中のドロップの案内 ----

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-UI-34-06")]
    public Task Admin_drop_notice_is_shown_once_per_start() => UiTestContext.RunAsync(async ctx =>
    {
        string[] files = OpenAndDropTests.Files50(ctx);
        string notice = R("Drop_AdminLimited");
        var hooks = new JsonObject { ["adminDropNotice"] = true };
        AppSession app = await StartAsync(ctx, elevated: true, hooks: hooks);

        // 拒否されたドロップを検出できない実装 (UI-34 の仕様 7 の 2): 管理者として起動した時点で 1 回出る。
        // 一般の権限のエクスプローラーからのドラッグ & ドロップは Windows (UIPI) が届けないため、ドロップはテスト用の命令で行う。
        await app.WaitForNotificationAsync(m => m == notice, "the admin drop notice");
        Assert.Single(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>() == notice);

        // 1・2. file01.bin をドロップし、InfoBar を閉じる。
        await app.DropAsync([files[0]]);
        await app.WaitForTabsAsync(1);
        Assert.Equal(1, (await app.SendAsync("dismissNotice", new JsonObject { ["match"] = notice }))["dismissed"]!.GetValue<int>());

        // 3. もう一度ドロップしても同じ InfoBar は出ない。
        await app.DropAsync([files[1]]);
        await app.WaitForTabsAsync(2);
        await app.IdleAsync();
        Assert.DoesNotContain(await app.NotificationsAsync(), n => n["message"]!.GetValue<string>() == notice);

        // 4. 終了して、もう一度管理者として起動すると、また 1 回出る。
        await app.SendAsync("exit");
        await app.WaitForExitAsync(UiTest.Scaled(TimeSpan.FromSeconds(30)));
        AppSession again = await StartAsync(ctx, elevated: true, hooks: hooks);
        await again.WaitForNotificationAsync(m => m == notice, "the admin drop notice after the restart");
        Assert.Single(await again.NotificationsAsync(), n => n["message"]!.GetValue<string>() == notice);
    });
}
