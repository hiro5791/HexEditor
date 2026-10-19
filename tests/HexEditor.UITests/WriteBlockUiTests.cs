using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 書き込み保護モード (FOR-01)、OS の USB 書き込み保護 (FOR-02)、デバイス情報 (FOR-27・28)、暗号化の検出 (FOR-29)、メモリダンプ (ENG-36) の UI。
/// ディスクは偽のデバイス (fakeDevices)、OS の USB 書き込み保護は偽の保存先 (usbWriteProtect のファイル) を使い、実機のディスク・レジストリには
/// 触れない。昇格もしない。書き込みの関門そのもの (保存・デバイス・プロセス・ツールの拒否) は Core のテスト (WriteBlockTests) が確かめる。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class WriteBlockUiTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private static Task<JsonObject> WriteBlock(AppSession app, string action = "state", params (string Key, object? Value)[] values) =>
        app.SendAsync("tcWriteBlock", ToolsCalcTests.Args(action, values));

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>偽のディスク (SATA の HPA あり、仮想ディスク) と、USB 書き込み保護の偽の保存先のフック。</summary>
    private static JsonObject Hooks(UiTestContext ctx, string? usbFile = null, string bus = "Sata", long hpaSectors = 2048)
    {
        var devices = new JsonObject
        {
            ["disks"] = new JsonArray(new JsonObject
            {
                ["number"] = 2,
                ["model"] = "HexTest SSD",
                ["serial"] = "HXT-SERIAL-02",
                ["size"] = 64L * 1024 * 1024,
                ["sectorSize"] = 512,
                ["seed"] = 2,
                ["bus"] = bus,
                ["requiresAdmin"] = true,
                ["ata"] = bus == "Sata" ? new JsonObject { ["hpaSectors"] = hpaSectors, ["dcoSectors"] = 0 } : null,
            }),
        };
        string devicePath = ctx.WriteFile("fake-devices.json", Encoding.UTF8.GetBytes(devices.ToJsonString()));
        var hooks = new JsonObject { ["fakeDevices"] = devicePath, ["elevated"] = false, ["culture"] = "en-US", ["timeZone"] = "UTC" };
        if (usbFile is not null)
        {
            hooks["usbWriteProtect"] = usbFile;
        }

        return hooks;
    }

    private static async Task ExitAsync(AppSession app)
    {
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(ExitTimeout);
    }

    // ---- FOR-01 ----

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-01")]
    public Task Enabling_shows_the_status_bar_title_tab_locks_and_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string bytes = ctx.CopyTestData("TD-BYTES-256", "bytes.bin");
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq, bytes] });

        // 1. 「フォレンジック > 書き込み保護モード」。
        await app.CommandAsync("Command_WriteBlock");
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["enabled"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "write protection mode");

        // 2. ステータスバーの左端・タイトル・タブ。3. メニューのチェック。
        JsonObject state = await WriteBlock(app);
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]!.AsArray().All(t => t!["reason"]!.GetValue<string>() == "WriteProtectionMode"),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the tabs to become read-only");
        state = await WriteBlock(app);
        Assert.True(state["statusVisible"]!.GetValue<bool>());
        Assert.Equal("writeblock", state["firstVisibleStatus"]!.GetValue<string>());
        Assert.Equal(0, state["statusIndex"]!.GetValue<int>());
        Assert.Equal("Write protection", state["statusText"]!.GetValue<string>());
        Assert.Contains("Write protection", await app.UiaNameAsync("Status_WriteBlock"), StringComparison.Ordinal);
        Assert.Contains("[Write-protected]", state["title"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(state["tabs"]!.AsArray(), t => Assert.StartsWith("🔒", t!["visibleHeader"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Equal(2, state["tabs"]!.AsArray().Count);
        Assert.True(state["checked"]!.GetValue<bool>());

        // 4. 初回の InfoBar: OS や他のアプリの書き込みは防げないこと、ハードウェアの書き込み防止装置か FOR-02 の併用。
        string notice = state["notices"]!.AsArray().Select(n => n!.GetValue<string>()).Single(n => n.Contains("only stops this app", StringComparison.Ordinal));
        Assert.Contains("hardware write blocker", notice, StringComparison.Ordinal);
        Assert.Contains("OS USB write protection", notice, StringComparison.Ordinal);

        // 5. 再起動しても有効のまま表示が出る。
        await ExitAsync(app);
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });
        await again.WaitUntilAsync(async () => (await WriteBlock(again))["tabs"]!.AsArray().Any(t => t!["reason"]!.GetValue<string>() == "WriteProtectionMode"),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the restored mode");
        JsonObject after = await WriteBlock(again);
        Assert.True(after["enabled"]!.GetValue<bool>());
        Assert.True(after["statusVisible"]!.GetValue<bool>());
        Assert.Contains("[Write-protected]", after["title"]!.GetValue<string>(), StringComparison.Ordinal);

        // 初回の InfoBar は一度だけ。
        Assert.DoesNotContain(after["notices"]!.AsArray(), n => n!.GetValue<string>().Contains("only stops this app", StringComparison.Ordinal));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-03")]
    public Task Open_files_become_read_only_with_no_way_to_allow_editing() => UiTestContext.RunAsync(async ctx =>
    {
        string first = ctx.CopyTestData("TD-SEQ-1M", "first.bin");
        string second = ctx.CopyTestData("TD-SEQ-1M", "second.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [first] });

        // 1. 書き込み保護モードを有効にする。
        await WriteBlock(app, "enable");
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]![0]!["reason"]!.GetValue<string>() == "WriteProtectionMode",
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the read-only tab");

        // 2. 読み取り専用、「編集を許可する」が出ない (解除できない)、ハンドルは読み取りだけ。3. 「編集 > 読み取り専用」は解除できない。
        async Task CheckAsync(int index)
        {
            JsonObject tab = (await WriteBlock(app))["tabs"]![index]!.AsObject();
            Assert.True(tab["readOnly"]!.GetValue<bool>());
            Assert.Equal("NotAllowed", tab["release"]!.GetValue<string>());
            Assert.Equal("read", tab["access"]!.GetValue<string>());
            JsonObject readOnly = await WriteBlock(app, "commandState", ("id", "edit.readOnly"));
            Assert.False(readOnly["enabled"]!.GetValue<bool>());
            Assert.Contains("Write protection mode", readOnly["reason"]!.GetValue<string>(), StringComparison.Ordinal);
            JsonObject save = await WriteBlock(app, "commandState", ("id", "file.save"));
            Assert.False(save["enabled"]!.GetValue<bool>());

            // 4. Hex 列で F を入力してもデータは変わらない。
            byte before = (await app.BytesAsync(0, 1))[0];
            await app.TypeAsync("F");
            Assert.Equal(before, (await app.BytesAsync(0, 1))[0]);
        }

        await CheckAsync(0);

        // 5. モードのまま別のコピーを開いても同じ。
        await app.OpenAsync(second);
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]!.AsArray().Count == 2
            && (await WriteBlock(app))["tabs"]![1]!["reason"]!.GetValue<string>() == "WriteProtectionMode", UiTest.Scaled(TimeSpan.FromSeconds(10)), "the second tab");
        await CheckAsync(1);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-04")]
    [Trait(UiTest.Priority, UiTest.High)]
    public Task Saving_an_edited_file_is_refused_but_save_as_elsewhere_works() => UiTestContext.RunAsync(async ctx =>
    {
        string evidence = ctx.CopyTestData("TD-SEQ-1M", "evidence.bin");
        string hash = Sha256(evidence);
        DateTime stamp = File.GetLastWriteTimeUtc(evidence);
        string copyFolder = Directory.CreateDirectory(Path.Combine(ctx.Root, "other")).FullName;
        string copy = Path.Combine(copyFolder, "copy.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [evidence], Hooks = new JsonObject { ["savePicker"] = copy } });

        // 前提: オフセット 0 を AA に上書きする (保存しない)。1. 書き込み保護モードを有効にする。
        await app.TypeAsync("AA");
        await WriteBlock(app, "enable");
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]![0]!["reason"]!.GetValue<string>() == "WriteProtectionMode",
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the read-only tab");

        // 2. Ctrl+S (保存コマンドは無効で、ファイルへの書き込みは 0 回)。
        Assert.False((await WriteBlock(app, "commandState", ("id", "file.save")))["enabled"]!.GetValue<bool>());
        await app.KeyAsync("S", ctrl: true);
        await app.IdleAsync();

        // 3. SHA-256 と更新日時が同じ。オフセット 0 は AA で「変更あり」のまま。
        Assert.Equal(hash, Sha256(evidence));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(evidence));
        Assert.Equal(0xAA, (await app.BytesAsync(0, 1))[0]);
        Assert.True((await WriteBlock(app))["tabs"]![0]!["modified"]!.GetValue<bool>());

        // 4. 「名前を付けて保存」で別のフォルダの copy.bin に保存する。5. copy.bin のオフセット 0 は AA、evidence.bin は変わらない。
        Assert.True((await WriteBlock(app, "commandState", ("id", "file.saveAs")))["enabled"]!.GetValue<bool>());
        await app.CommandAsync("Command_SaveAs");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(copy) && new FileInfo(copy).Length == 1024 * 1024), UiTest.Scaled(TimeSpan.FromSeconds(30)), "copy.bin");
        await app.IdleAsync();
        Assert.Equal(0xAA, File.ReadAllBytes(copy)[0]);
        Assert.Equal(hash, Sha256(evidence));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-06")]
    public Task Destructive_commands_are_disabled_with_the_reason() => UiTestContext.RunAsync(async ctx =>
    {
        // 物理ディスク (偽) を開いて範囲を選び、モードを有効にする。消去・クローン・復元・墨塗りのコマンドは各担当の部品が
        // ForensicsService.RegisterDestructiveCommands で登録し、同じ関門で無効になる。パーティション表のエントリの作成はコマンドではなく
        // 失われたパーティションの結果の操作 (FOR-08) で、同じ書き込み保護の判定で断る (FileSystemUiTests が確かめる)。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive2" });
        await app.SelectAsync(0x100000, 0x200);
        await WriteBlock(app, "enable");

        // 1・2. メニューとコマンドパレットの同じコマンドが無効で、理由は「書き込み保護モードが有効です」。
        foreach (string id in new[]
        {
            "tools.files.shred", "forensics.wipe.selection", "forensics.wipe.disk", "forensics.wipe.freeSpace", "forensics.wipe.slack", "forensics.fs.redact",
            "forensics.disk.clone", "forensics.sectors.restore",
        })
        {
            JsonObject s = await WriteBlock(app, "commandState", ("id", id));
            Assert.False(s["enabled"]!.GetValue<bool>(), id);
            Assert.Equal("Write-protect mode is on.", s["reason"]!.GetValue<string>());
        }

        // ディスクの書き込み (保存) も無効。読み取りの操作 (選択範囲のエクスポート) は使える。
        Assert.False((await WriteBlock(app, "commandState", ("id", "file.save")))["enabled"]!.GetValue<bool>());
        Assert.True((await WriteBlock(app, "commandState", ("id", "forensics.device.hpaDco")))["enabled"]!.GetValue<bool>());

        // 3. 「セクタ範囲を保存」(読み取り) は使える。
        JsonObject save = await WriteBlock(app, "commandState", ("id", "forensics.sectors.save"));
        Assert.True(save["enabled"]!.GetValue<bool>(), save["reason"]?.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-07")]
    public Task Cancelling_the_confirmation_keeps_the_mode_on() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string bytes = ctx.CopyTestData("TD-BYTES-256", "bytes.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq, bytes], Hooks = ForensicsCaseTests.En });
        await ForensicsCaseTests.Case(app, "create", ("folder", folder), ("number", "C-0001"), ("name", "テストケース"), ("investigator", "tester"), ("timeZone", "UTC"));
        for (int i = 0; i < 2; i++)
        {
            await app.SendAsync("selectTab", new JsonObject { ["index"] = i });
            await ForensicsCaseTests.Case(app, "addEvidence", ("description", "E" + i), ("hashes", Array.Empty<string>()));
        }

        // ケースを開くと自動で有効になる (FOR-03 の仕様 4)。
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["enabled"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "write protection mode");

        // 1. 「フォレンジック > 書き込み保護モード」。2. 本文に証拠の数 (2) と名前。ボタンは「書き込み保護を解除する」と「キャンセル」。
        await app.CommandAsync("Command_WriteBlock");
        await app.WaitForAsync("WriteBlockDisableDialog");
        Assert.Contains("2", await app.UiaNameAsync("WriteBlock_DisableBody"), StringComparison.Ordinal);
        string names = await app.UiaNameAsync("WriteBlock_DisableEvidence");
        Assert.Contains("seq.bin", names, StringComparison.Ordinal);
        Assert.Contains("bytes.bin", names, StringComparison.Ordinal);
        Assert.Equal("Turn off write protection", await app.UiaNameAsync("PrimaryButton"));
        Assert.Equal("Cancel", await app.UiaNameAsync("CloseButton"));

        // 3. キャンセル。4. モードは有効のままで表示も残る。
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["count"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog to close");
        JsonObject state = await WriteBlock(app);
        Assert.True(state["enabled"]!.GetValue<bool>());
        Assert.True(state["statusVisible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-08")]
    public Task Evidence_disks_cannot_get_a_document_exception_but_a_memo_file_can() => UiTestContext.RunAsync(async ctx =>
    {
        string folder = Path.Combine(ctx.Root, "case");
        string memo = ctx.CopyTestData("TD-BYTES-256", "memo.bin");
        JsonObject hooks = Hooks(ctx);
        hooks["culture"] = "en-US";
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = hooks, WaitForEditor = false });
        await ForensicsCaseTests.Case(app, "create", ("folder", folder), ("number", "C-0001"), ("name", "テストケース"), ("investigator", "tester"), ("timeZone", "UTC"));

        // 前提: 物理ディスク (偽) を証拠に追加し、memo.bin を開く (証拠にはしない)。モードを有効にする。
        await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive2" });
        await ForensicsCaseTests.Case(app, "addEvidence", ("description", "disk"), ("hashes", Array.Empty<string>()));
        await app.OpenAsync(memo);
        await WriteBlock(app, "enable");
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]!.AsArray().All(t => t!["readOnly"]!.GetValue<bool>()),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the read-only tabs");

        // 1. ディスクのタブでは無効、2. memo.bin のタブでは有効。
        JsonArray tabs = (await WriteBlock(app))["tabs"]!.AsArray();
        Assert.True(tabs[0]!["evidence"]!.GetValue<bool>());
        Assert.False(tabs[0]!["allowEnabled"]!.GetValue<bool>());
        Assert.True(tabs[1]!["allowEnabled"]!.GetValue<bool>());

        // 3. memo.bin で「このドキュメントへの書き込みを許可」。強い確認: 入力するまで実行ボタンは無効。
        await app.SendAsync("tabMenu", new JsonObject { ["index"] = 1, ["item"] = "TabMenu_AllowWriting" });
        await app.WaitForAsync("WriteBlockAllowDialog");
        Assert.False((await app.WaitForAsync("PrimaryButton")).IsEnabled);
        await app.UiaSetValueAsync("WriteBlock_AllowName", "memo.bi");
        Assert.False((await app.WaitForAsync("PrimaryButton")).IsEnabled);
        await app.UiaSetValueAsync("WriteBlock_AllowName", "memo.bin");
        await app.WaitUntilAsync(async () => (await app.WaitForAsync("PrimaryButton")).IsEnabled, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the confirm button");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        // 4. タブのアイコンが開いた錠になり、読み取り専用が解除されている。
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]![1]!["exception"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the exception");
        JsonObject memoTab = (await WriteBlock(app))["tabs"]![1]!.AsObject();
        Assert.StartsWith("🔓", memoTab["visibleHeader"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(memoTab["readOnly"]!.GetValue<bool>());
        Assert.True((await WriteBlock(app))["tabs"]![0]!["readOnly"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-01-10")]
    [Trait(UiTest.Category, "Integration")]
    public Task Starting_with_write_block_enables_the_mode_before_the_first_document_opens() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");

        // 1. HexEditor.exe --write-block <TD-SEQ-1M> で起動する (前提: モードが無効の設定フォルダ)。
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = ctx.NewProfile(), ExtraArgs = ["--write-block"], Files = [seq] });

        // 2. ハンドルは GENERIC_READ だけで、読み取り専用。
        await app.WaitUntilAsync(async () => (await WriteBlock(app))["tabs"]!.AsArray().Count == 1
            && (await WriteBlock(app))["tabs"]![0]!["reason"]!.GetValue<string>() == "WriteProtectionMode", UiTest.Scaled(TimeSpan.FromSeconds(10)), "the tab");
        JsonObject tab = (await WriteBlock(app))["tabs"]![0]!.AsObject();
        Assert.Equal("read", tab["access"]!.GetValue<string>());
        Assert.True(tab["readOnly"]!.GetValue<bool>());

        // 3. ログで、モードの有効化がドキュメントを開く処理より前。
        IReadOnlyList<string> log = await app.LogAsync();
        int enabled = log.ToList().FindIndex(l => l.Contains("Write protection mode enabled (commandLine)", StringComparison.Ordinal));
        int opened = log.ToList().FindIndex(l => l.Contains("Document opened", StringComparison.Ordinal));
        Assert.True(enabled >= 0, string.Join(Environment.NewLine, log));
        Assert.True(opened > enabled, string.Join(Environment.NewLine, log));
        Assert.Contains("Document opened (write protection mode)", log[opened], StringComparison.Ordinal);
    });

    // ---- FOR-02 ----

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-02-03")]
    public Task Exiting_with_usb_write_protection_on_asks_whether_to_turn_it_off() => UiTestContext.RunAsync(async ctx =>
    {
        // 偽の保存先 (レジストリの代わり)。最初は値がない。
        string usb = ctx.WriteFile("usb.json", Encoding.UTF8.GetBytes("{\"value\":null}"));
        string profile = ctx.NewProfile();
        uint? Value() => JsonNode.Parse(File.ReadAllText(usb))!["value"] is JsonValue v ? v.GetValue<uint>() : null;

        async Task<AppSession> StartEnabledAsync()
        {
            AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Hooks = Hooks(ctx, usb), WaitForEditor = false });
            await app.CommandAsync("Command_UsbWriteProtect");
            await app.WaitForAsync("UsbProtectDialog");
            string body = await app.UiaNameAsync("UsbProtect_Body");
            Assert.Contains("already connected", body, StringComparison.Ordinal);
            Assert.Contains("SATA and NVMe", body, StringComparison.Ordinal);
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
            await app.WaitUntilAsync(() => Task.FromResult(Value() == 1), UiTest.Scaled(TimeSpan.FromSeconds(10)), "WriteProtect = 1");
            Assert.True((await app.SendAsync("tcUsbProtect"))["checked"]!.GetValue<bool>());
            return app;
        }

        // 1. 「ファイル > 終了」。2. 通知の文言とボタン。プロセスはまだ終了していない。
        AppSession first = await StartEnabledAsync();
        await first.CommandAsync("Command_Exit");
        await first.WaitForAsync("UsbProtectExitDialog");
        Assert.Contains("still on", await first.UiaNameAsync("UsbProtectExitDialog") + await first.UiaNameAsync("UsbProtect_ExitBody"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Turn off and exit", await first.UiaNameAsync("PrimaryButton"));
        Assert.Equal("Exit and keep it on", await first.UiaNameAsync("SecondaryButton"));
        Assert.False(first.Process.HasExited);

        // 3. 「無効にして終了」: 値が元の状態 (値なし) に戻る。
        await first.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await first.WaitForExitAsync(ExitTimeout);
        Assert.Null(Value());

        // 4. 有効にし直してから終了し、「有効のまま終了」: 値は 1 のまま。
        AppSession second = await StartEnabledAsync();
        await second.CommandAsync("Command_Exit");
        await second.WaitForAsync("UsbProtectExitDialog");
        await second.SendAsync("dialogButton", new JsonObject { ["name"] = "SecondaryButton" });
        await second.WaitForExitAsync(ExitTimeout);
        Assert.Equal(1u, Value());
    });

    // ---- FOR-27・FOR-28 ----

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-27-02")]
    public Task A_virtual_disk_that_does_not_answer_shows_cannot_check_without_exceptions() => UiTestContext.RunAsync(async ctx =>
    {
        // 仮想ディスクは ATA パススルーに応答しない (偽のディスクの接続を Virtual にする)。
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx, bus: "Virtual"), WaitForEditor = false });
        await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive2" });

        // 1. 「フォレンジック > デバイス > HPA / DCO の確認」。2. パネルの表示とログの例外。
        await app.CommandAsync("Command_DeviceHpaDco");
        await app.WaitUntilAsync(async () => (await app.SendAsync("tcDeviceInfo"))["hpaLines"] is JsonArray { Count: > 0 }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the panel");
        JsonObject state = await app.SendAsync("tcDeviceInfo");
        Assert.Equal("NotSupported", state["hpaAvailability"]!.GetValue<string>());
        Assert.Contains(state["hpaLines"]!.AsArray(), l => l!.GetValue<string>().Contains("cannot be checked on this device", StringComparison.Ordinal));
        Assert.DoesNotContain(await app.LogAsync(), l => l.Contains("Exception", StringComparison.Ordinal));

        // アプリは動作を続ける。
        Assert.Equal(64L * 1024 * 1024, (await app.DocumentAsync())["length"]!.GetValue<long>());
    });

    [Fact]
    public Task Opening_a_disk_with_an_hpa_shows_a_notice_and_the_panel_lists_smart_attributes() => UiTestContext.RunAsync(async ctx =>
    {
        // FOR-27 の仕様 2 (自動確認の InfoBar)、FOR-28 の仕様 1・3・4 (属性の列、デバイス情報、JSON の取得日時とシリアル番号)。
        string json = Path.Combine(ctx.Root, "smart.json");
        JsonObject hooks = Hooks(ctx, hpaSectors: 1_048_576);
        hooks["savePicker"] = json;
        hooks["frozenTime"] = "2026-10-07T03:15:42.1234567Z";
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = hooks, WaitForEditor = false });
        await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive2" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("tcDeviceInfo"))["hiddenArea"]?.GetValue<bool>() == true, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the automatic check");
        JsonObject state = await app.SendAsync("tcDeviceInfo");
        Assert.Contains(state["notices"]!.AsArray(), n => n!.GetValue<string>().Contains("hidden areas", StringComparison.Ordinal)
            && n.GetValue<string>().Contains("1,048,576", StringComparison.Ordinal));

        await app.CommandAsync("Command_DeviceSmart");
        await app.WaitUntilAsync(async () => (await app.SendAsync("tcDeviceInfo"))["smartRows"] is JsonArray { Count: > 0 }, UiTest.Scaled(TimeSpan.FromSeconds(10)), "SMART rows");
        state = await app.SendAsync("tcDeviceInfo");
        Assert.Equal(["ID", "Name", "Current", "Worst", "Threshold", "Raw (hex)", "Raw (decimal)", "Status"], state["smartColumns"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("Reallocated Sectors Count", state["smartRows"]![0]![1]!.GetValue<string>());
        Assert.Contains(state["identityLines"]!.AsArray(), l => l!.GetValue<string>() == "Serial number: HXT-SERIAL-02");
        Assert.Contains(state["hpaLines"]!.AsArray(), l => l!.GetValue<string>().StartsWith("HPA: present, 1,048,576 sectors", StringComparison.Ordinal));

        await app.UiaInvokeAsync("DeviceInfo_ExportJson");
        await app.WaitUntilAsync(() => Task.FromResult(File.Exists(json)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "smart.json");
        JsonNode exported = JsonNode.Parse(File.ReadAllText(json))!;
        Assert.EndsWith("Z", exported["capturedUtc"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("HXT-SERIAL-02", exported["identity"]!["serialNumber"]!.GetValue<string>());
    });

    // ---- FOR-29・ENG-36 の画面 ----

    [Fact]
    public Task Encryption_information_and_memory_dumps_open_from_the_ui() => UiTestContext.RunAsync(async ctx =>
    {
        string bitlocker = ctx.CopyTestData("TD-FOR-BITLOCKER-RAW", "bitlocker.raw");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [bitlocker] });
        JsonObject scan = await app.SendAsync("tcEncryption", new JsonObject { ["action"] = "scan" });
        JsonObject entry = scan["entries"]!.AsArray().Single()!.AsObject();
        Assert.Equal("BitLocker", entry["kind"]!.GetValue<string>());
        Assert.Equal("AES-XTS 128", entry["method"]!.GetValue<string>());
        Assert.Equal(["Password", "Recovery password"], entry["protectors"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.True(entry["blocksBrowser"]!.GetValue<bool>());
        Assert.Contains(entry["lines"]!.AsArray(), l => l!.GetValue<string>().Contains("does not open encrypted volumes", StringComparison.Ordinal));
        await app.CommandAsync("Command_EncryptionInfo");
        await app.WaitForAsync("EncryptionInfoDialog");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        // メモリダンプを開く: 完全メモリダンプは物理アドレス空間 (含まれないページは未割り当て)、読み取り専用。
        JsonObject dump = await app.SendAsync("tcMemDump", new JsonObject { ["action"] = "open", ["path"] = ctx.CopyTestData("TD-ENG-FULLDUMP", "full.dmp") });
        Assert.Equal("CompleteDump", dump["kind"]!.GetValue<string>());
        Assert.Contains("complete memory dump", dump["toolTip"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("NotAllowed", dump["release"]!.GetValue<string>());
        JsonObject page = await app.SendAsync("tcMemDump", new JsonObject { ["action"] = "read", ["offset"] = 0x100000, ["length"] = 20 });
        Assert.Equal(Convert.ToHexString(TestDataCatalog.PfnMarker(0x100)), page["hex"]!.GetValue<string>());
        JsonObject hole = await app.SendAsync("tcMemDump", new JsonObject { ["action"] = "read", ["offset"] = 0xA0000, ["length"] = 16 });
        Assert.Equal("Unallocated", hole["unreadable"]![0]!["reason"]!.GetValue<string>());

        // 壊れたダンプは「raw として開きますか」。
        byte[] broken = File.ReadAllBytes(TestDataCatalog.Get("TD-ENG-FULLDUMP"))[..0x3000];
        string brokenPath = ctx.WriteFile("broken.dmp", broken);
        Task<JsonObject> opening = app.SendAsync("tcMemDump", new JsonObject { ["action"] = "open", ["path"] = brokenPath }, TimeSpan.FromMinutes(2));
        // 命令の通り道は "tcMemDump" の完了 (ダイアログを閉じるまで) を待っているため、ここでは UI オートメーションだけで操作する。
        await app.WaitForAsync("MemoryDumpFormatDialog");
        Assert.Contains("broken.dmp", await app.UiaNameAsync("MemoryDump_FormatErrorText"), StringComparison.Ordinal);
        await app.InvokeDialogButtonAsync("Open as raw", idle: false);
        JsonObject raw = await opening;
        Assert.Equal("broken.dmp", raw["name"]!.GetValue<string>());
        Assert.Equal(0x3000, raw["length"]!.GetValue<long>());
    });
}
