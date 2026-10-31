using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ディスク・プロセスを開く UI の結合テスト (ENG-29、ENG-32、ENG-33)。偽のデバイス・プロセス (fakeDevices / fakeProcesses) を使い、
/// 実機のディスク・プロセスには触れない。昇格もしない (開発中のビルドの経路で、補助プロセスは偽のアクセスに置き換える)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DeviceTests
{
    internal static JsonObject Hooks(UiTestContext ctx)
    {
        var devices = new JsonObject
        {
            ["disks"] = new JsonArray(new JsonObject
            {
                ["number"] = 0,
                ["model"] = "Fake Disk",
                ["serial"] = "DISK0",
                ["size"] = 64L * 1024 * 1024,
                ["sectorSize"] = 512,
                ["seed"] = 1,
                ["requiresAdmin"] = true,
            }),
            ["volumes"] = new JsonArray(new JsonObject
            {
                ["path"] = @"\\.\E:",
                ["driveLetter"] = "E:",
                ["fileSystem"] = "FAT32",
                ["disk"] = 0,
                ["offset"] = 1024 * 1024,
                ["size"] = 16 * 1024 * 1024,
                ["removableUsb"] = true,
                ["requiresAdmin"] = false,
            }),
        };
        var processes = new JsonObject
        {
            ["elevated"] = false,
            ["processes"] = new JsonArray(new JsonObject
            {
                ["pid"] = 4321,
                ["name"] = "TestTarget.exe",
                ["user"] = "tester",
                ["addressLimit"] = 0x200000,
                ["access"] = "Direct",
                ["currentUser"] = true,
                ["regions"] = new JsonArray(new JsonObject
                {
                    ["base"] = 0x10000,
                    ["size"] = 0x1000,
                    ["state"] = "Commit",
                    ["protect"] = 4,
                    ["type"] = "Private",
                    ["data"] = "4D5A9000",
                }),
                ["modules"] = new JsonArray(new JsonObject
                {
                    ["name"] = "TestTarget.exe",
                    ["baseAddress"] = 0x10000,
                    ["size"] = 0x1000,
                    ["path"] = @"C:\TestTarget.exe",
                }),
            }),
        };
        string devicePath = ctx.WriteFile("fake-devices.json", Encoding.UTF8.GetBytes(devices.ToJsonString()));
        string processPath = ctx.WriteFile("fake-processes.json", Encoding.UTF8.GetBytes(processes.ToJsonString()));
        // 管理者でない扱いにする (CI のランナーは管理者として動くため、実際の権限によらず補助プロセスの経路を確かめる)。
        return new JsonObject { ["fakeDevices"] = devicePath, ["fakeProcesses"] = processPath, ["elevated"] = false };
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-01")]
    public Task Disk_list_marks_admin_only_items_and_usb_is_openable_without_a_helper() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        JsonObject list = await app.SendAsync("enumDevices");
        Assert.False(list["elevated"]!.GetValue<bool>());
        JsonObject disk = list["disks"]!.AsArray().Select(d => d!.AsObject()).Single(d => d["number"]!.GetValue<int>() == 0);
        Assert.True(disk["needsAdmin"]!.GetValue<bool>());
        JsonObject usb = list["volumes"]!.AsArray().Select(v => v!.AsObject()).Single(v => v["path"]!.GetValue<string>() == @"\\.\E:");
        Assert.False(usb["needsAdmin"]!.GetValue<bool>());

        // USB ストレージのボリュームは直接開ける (補助プロセスを使わない。ENG-29 の仕様 3 の 1)。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\E:" });
        Assert.Equal("Direct", opened["route"]!.GetValue<string>());
        Assert.False(opened["helper"]!.GetValue<bool>());
    });

    [Fact]
    [Trait("TC", "TC-ENG-28-02")]
    public Task Physical_disk_opens_through_the_helper_route_and_shows_the_shield() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // 物理ディスクは管理者権限が必要なため補助プロセスの経路で開く (開発中のビルドでは偽の昇格アクセスに置き換える)。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive0" });
        Assert.Equal("Helper", opened["route"]!.GetValue<string>());
        Assert.Equal(64L * 1024 * 1024, opened["length"]!.GetValue<long>());
        Assert.True(opened["helper"]!.GetValue<bool>());
    });

    [Fact]
    [Trait("TC", "TC-ENG-32-01")]
    public Task A_same_user_process_opens_without_a_helper() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        JsonObject opened = await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });
        Assert.Equal("Direct", opened["route"]!.GetValue<string>());
        Assert.True(opened["modules"]!.GetValue<int>() >= 1);
    });

    [Fact]
    [Trait("TC", "TC-ENG-18-03")]
    public Task Process_memory_auto_refresh_shows_new_values_within_a_second() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });
        await app.GoToAsync(0x10010);
        Assert.Equal(new byte[8], await app.BytesAsync(0x10010, 8));

        // 1. 自動更新を 1 秒にする。2. 変数を書き換える。
        await app.SendAsync("autoRefresh", new JsonObject { ["ms"] = 1000 });
        double written = (await app.SendAsync("processWrite", new JsonObject { ["pid"] = 4321, ["address"] = 0x10010, ["hex"] = "8877665544332211" }))["at"]!.GetValue<double>();

        // 3. 新しい値が表示される。読み直しは書き換えから 1 秒以内 (アプリの中の時刻で比べる)。
        byte[] expected = [0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11];
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0x10010, 8)).SequenceEqual(expected), UiTest.Scaled(TimeSpan.FromSeconds(5)), "the new value");
        double[] refreshes = [.. (await app.SendAsync("autoRefresh"))["refreshes"]!.AsArray().Select(t => t!.GetValue<double>())];
        double first = refreshes.First(t => t >= written);
        // 間隔 1 秒のタイマーの誤差 (数十 ms) は許す。
        Assert.True(first - written <= (1000 + 100) * UiTest.TimeoutScale, $"{first - written:F0} ms");
    });

    [Fact]
    [Trait("TC", "TC-ENG-14-02")]
    public Task A_disk_opens_read_only_and_allowing_writes_asks_with_the_model_and_size() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // 1・2. 既定 (「読み取り専用で開く」がオン) で物理ディスクを開くと読み取り専用。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive0" });
        Assert.True(opened["readOnly"]!.GetValue<bool>());
        Assert.False(opened["writable"]!.GetValue<bool>());
        byte[] before = await app.BytesAsync(0, 512);

        // 3・4. 「編集を許可する」(編集 > 読み取り専用) → 確認ダイアログ: 本文にモデル名とサイズ、ボタンは「書き込みを許可」「キャンセル」。
        await app.CommandAsync("Command_ReadOnly");
        await app.WaitForAsync("DeviceWriteConfirmDialog");
        string body = await app.UiaNameAsync("DeviceWriteConfirm_Body");
        Assert.Contains("Fake Disk", body, StringComparison.Ordinal);
        Assert.Contains("64", body, StringComparison.Ordinal);
        Assert.Contains("Nothing is written until you save", body, StringComparison.Ordinal);
        Assert.Equal("Allow writing", await app.UiaNameAsync("PrimaryButton"));
        Assert.Equal("Cancel", await app.UiaNameAsync("CloseButton"));

        // 5. 「書き込みを許可」: 読み取り専用が解除され、読み書きで開き直す。ディスクの内容は変わらない。
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () => (await app.DocumentAsync())["readOnly"]?.GetValue<bool>() == false, UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the document to become editable");
        Assert.Equal(before, await app.BytesAsync(0, 512));
    });

    [Fact]
    public Task A_disk_opened_with_read_only_off_is_editable_and_a_range_can_be_given_in_sectors() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // 「読み取り専用で開く」をオフにすると読み書きで開く (ENG-14 の仕様 1、ENG-29 の仕様 2)。範囲はセクタ数で指定できる。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject
        {
            ["path"] = @"\\.\PhysicalDrive0",
            ["readOnly"] = false,
            ["range"] = new JsonObject { ["start"] = "8", ["length"] = "0x10", ["sectors"] = true },
        });
        Assert.False(opened["readOnly"]!.GetValue<bool>());
        Assert.True(opened["writable"]!.GetValue<bool>());
        Assert.Equal(4096, opened["rangeStart"]!.GetValue<long>());
        Assert.Equal(16 * 512, opened["length"]!.GetValue<long>());
    });

    [Fact]
    public Task A_module_of_a_process_opens_on_its_own_with_the_module_in_the_name() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // モジュールを選んで開く (ENG-32 の仕様 2・6・8): オフセット 0 がベースアドレス、表示名「… - TestTarget.exe」。
        JsonObject opened = await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321, ["module"] = "TestTarget.exe" });
        Assert.Equal(0x10000, opened["baseAddress"]!.GetValue<long>());
        Assert.Equal(0x1000, opened["length"]!.GetValue<long>());
        Assert.EndsWith(" - TestTarget.exe", opened["name"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([0x4D, 0x5A], await app.BytesAsync(0, 2));
    });

    [Fact]
    [Trait("TC", "TC-ENG-34-03")]
    public Task Immediate_write_reaches_the_process_and_undo_writes_the_old_value_back() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        JsonObject opened = await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321, ["readOnly"] = false });
        Assert.False(opened["readOnly"]!.GetValue<bool>());
        await app.SendAsync("immediateWrite", new JsonObject { ["on"] = true });
        await app.IdleAsync();

        // 保存しなくても、入力した値が対象のプロセスに届く。
        await app.GoToAsync(0x10004);
        await app.TypeAsync("AB");
        await app.WaitUntilAsync(async () => (await app.SendAsync("processRead", new JsonObject { ["pid"] = 4321, ["address"] = 0x10004, ["length"] = 1 }))["hex"]!
            .GetValue<string>() == "AB", UiTest.Scaled(TimeSpan.FromSeconds(10)), "the immediate write");

        // Undo すると元の値 (00) が書き戻される。
        await app.KeyAsync("Z", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.SendAsync("processRead", new JsonObject { ["pid"] = 4321, ["address"] = 0x10004, ["length"] = 1 }))["hex"]!
            .GetValue<string>() == "00", UiTest.Scaled(TimeSpan.FromSeconds(10)), "the undo to be written");
    });

    [Fact]
    public Task Memory_map_rows_show_protection_and_the_context_menu_selects_or_opens_a_region() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });
        JsonObject panel = null!;
        await app.WaitUntilAsync(async () => (panel = await app.SendAsync("memoryMapPanel"))["rows"] is JsonArray { Count: > 0 },
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the memory map rows");

        // 行に開始・終了・状態・保護属性・種類・モジュール名 (ENG-33 の仕様 1)。「空き」は既定で隠す (仕様 2)。
        string row = panel["rows"]!.AsArray().Select(r => r!.GetValue<string>()).Single(r => r.StartsWith("000000010000-00000001", StringComparison.Ordinal));
        Assert.Contains("RW", row, StringComparison.Ordinal);
        Assert.Contains("TestTarget.exe", row, StringComparison.Ordinal);
        Assert.DoesNotContain(panel["rows"]!.AsArray(), r => r!.GetValue<string>().Contains("Free", StringComparison.Ordinal));

        // カーソルのある領域をステータスバーにも出す (仕様 4)。
        await app.GoToAsync(0x10010);
        await app.WaitUntilAsync(async () => (await app.SendAsync("memoryMapPanel"))["position"]?.GetValue<string>()?.Contains("TestTarget.exe+0x10", StringComparison.Ordinal) == true,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the region in the status bar");

        // 右クリックメニュー (仕様 3): 「領域を選択」「この領域を新しいタブで開く」。
        JsonObject selected = await app.SendAsync("memoryMapPanel", new JsonObject { ["select"] = 0x10000, ["action"] = "selectRegion" });
        Assert.Equal([0x10000L, 0x1000L], selected["selection"]!.AsArray().Select(v => v!.GetValue<long>()));
        int tabs = (await app.TabNamesAsync()).Count;
        await app.SendAsync("memoryMapPanel", new JsonObject { ["select"] = 0x10000, ["action"] = "openInNewTab" });
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == tabs + 1, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the region tab");
    });

    [Fact]
    public Task Opening_an_img_file_normally_suggests_opening_it_as_a_disk_image() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-31 の仕様 6: .img などを通常の「開く」で開くと、ディスクイメージとして開き直すかを InfoBar で提案する。
        var hooks = Hooks(ctx);
        hooks["suggestDiskImage"] = true;
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = hooks });
        string image = ctx.WriteFile("card.img", new byte[8192]);
        string other = ctx.WriteFile("notes.dat", new byte[8192]);
        await app.SendAsync("uiOpen", new JsonObject { ["path"] = other });
        await app.SendAsync("uiOpen", new JsonObject { ["path"] = image });
        await app.WaitUntilAsync(async () => (await app.StateAsync())["notifications"]!.AsArray()
            .Any(n => n!["message"]!.GetValue<string>().Contains("disk image", StringComparison.Ordinal)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the suggestion");
        Assert.Single((await app.StateAsync())["notifications"]!.AsArray(), n => n!["message"]!.GetValue<string>().Contains("disk image", StringComparison.Ordinal));
    });

    /// <summary>要素の外側に、祖先の ScrollViewer に切り取られずに描ける幅が、左右は <paramref name="horizontal"/>、上下は <paramref name="vertical"/> 以上ある。</summary>
    internal static async Task AssertFocusVisualFitsAsync(AppSession app, string id, double horizontal, double vertical)
    {
        JsonObject c = await app.SendAsync("focusClearance", new JsonObject { ["id"] = id });
        Assert.True(c["found"]!.GetValue<bool>(), $"{id} was not found");
        foreach ((string side, double min) in new[] { ("left", horizontal), ("right", horizontal), ("top", vertical), ("bottom", vertical) })
        {
            double value = c[side]!.GetValue<double>();
            Assert.True(value >= min, $"{id}: only {value:F1} epx outside the {side} edge before the scroll viewer clips it (the focus visual needs {min})");
        }
    }

    [Fact]
    public Task Open_disk_and_process_dialogs_keep_focus_visuals_and_list_icons_visible() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // 「ディスクを開く」: 並べ替えの ComboBox のキーボードフォーカスの枠 (外側に 4 px) と、チェックボックスの枠 (左右に 7 px) が
        // ダイアログの中身の ScrollViewer で切れない。
        await app.CommandAsync("Command_OpenDisk");
        await app.WaitForDialogAsync("OpenDiskDialog");
        JsonObject focus = await app.SendAsync("focus", new JsonObject { ["target"] = "OpenDisk_Sort" });
        Assert.Equal("Keyboard", focus["focusState"]?.GetValue<string>());
        await AssertFocusVisualFitsAsync(app, "OpenDisk_Sort", 4, 4);
        await AssertFocusVisualFitsAsync(app, "OpenDevice_ReadOnly", 7, 3);

        // 一覧: 管理者権限が要る物理ディスクには盾のアイコン (記号のフォント)、USB のボリュームにはなし。名前の左端はどの行も同じ。
        JsonArray rows = (await app.SendAsync("deviceListRows", new JsonObject { ["id"] = "OpenDisk_List" }))["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        JsonObject disk = rows.Select(r => r!.AsObject()).Single(r => r["text"]!.GetValue<string>().Contains("Fake Disk", StringComparison.Ordinal));
        JsonObject usb = rows.Select(r => r!.AsObject()).Single(r => r["text"]!.GetValue<string>().Contains("E:", StringComparison.Ordinal));
        Assert.Equal("\uEA18", disk["glyph"]!.GetValue<string>());
        Assert.True(disk["iconVisible"]!.GetValue<bool>());
        Assert.Contains("Segoe", disk["iconFont"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(usb["iconVisible"]!.GetValue<bool>());
        Assert.Equal(disk["textLeft"]!.GetValue<double>(), usb["textLeft"]!.GetValue<double>());
        Assert.Equal(disk["textLeft"]!.GetValue<double>(), disk["detailLeft"]!.GetValue<double>());
        Assert.StartsWith(disk["text"]!.GetValue<string>() + ", ", disk["name"]!.GetValue<string>(), StringComparison.Ordinal);

        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("element", new JsonObject { ["id"] = "OpenDisk_Sort" }))["found"]!.GetValue<bool>() == false,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the disk dialog to close");

        // 「プロセスを開く」: 絞り込みの入力欄・並べ替え・「自分のプロセスだけ表示」も同じ。
        await app.CommandAsync("Command_OpenProcess");
        await app.WaitForDialogAsync("OpenProcessDialog");
        await app.SendAsync("focus", new JsonObject { ["target"] = "OpenProcess_Sort" });
        await AssertFocusVisualFitsAsync(app, "OpenProcess_Sort", 4, 4);
        await AssertFocusVisualFitsAsync(app, "OpenProcess_Filter", 4, 4);
        await AssertFocusVisualFitsAsync(app, "OpenProcess_OwnOnly", 7, 3);
    });

    [Fact]
    [Trait("TC", "TC-ENG-33-01")]
    public Task Opening_a_process_shows_the_memory_map_with_modules() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });

        JsonObject map = await app.SendAsync("memoryMap");
        Assert.True(map["panelVisible"]!.GetValue<bool>());
        Assert.Contains(map["modules"]!.AsArray(), m => m!.GetValue<string>() == "TestTarget.exe");
        Assert.Contains(map["regions"]!.AsArray(), r => r!["access"]!.GetValue<string>() == "Readable");
    });

    // ---- ENG-28 の仕様 12 の 7: 補助プロセスがないときの「管理者として再起動」 (runas はテスト用の設定で差し替える。昇格しない) ----

    private static readonly Lazy<Dictionary<string, string>> En = new(() => UiHelpers.LoadResw("en"));

    /// <summary>補助プロセスがない扱いで、偽の物理ディスク 0 を「ディスクを開く」から開こうとし、案内ダイアログの「管理者として再起動」を押す。</summary>
    private static async Task RestartAsAdministratorFromGuidanceAsync(AppSession app)
    {
        await app.CommandAsync("Command_OpenDisk");
        FlaUI.Core.AutomationElements.AutomationElement dialog = await app.WaitForDialogAsync("OpenDiskDialog");
        string format = En.Value["OpenDisk_DiskName"];
        string name = format[..format.IndexOf("{1}", StringComparison.Ordinal)].Replace("{0}", "0", StringComparison.Ordinal);
        FlaUI.Core.AutomationElements.AutomationElement? item = null;
        await app.WaitUntilAsync(() => Task.FromResult((item = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
            .FirstOrDefault(i => AppSession.NameOf(i).Contains(name, StringComparison.Ordinal))) is not null), UiTest.Scaled(TimeSpan.FromSeconds(15)), $"the list item '{name}'");
        item!.Patterns.SelectionItem.Pattern.Select();
        await app.InvokeDialogButtonAsync(En.Value["Common_Open"], idle: false);

        // ダイアログが UI オートメーションに現れた直後は、ボタンがまだないことがある (CI のランナーで一覧が空だった)。
        // InvokeDialogButtonAsync がボタンが現れるまで待つ。「管理者として再起動」が出なければ、ここでタイムアウトして失敗する。
        await app.WaitForDialogAsync("AdminGuidanceDialog");
        await app.InvokeDialogButtonAsync(En.Value["AdminGuide_Restart"], idle: false);
    }

    private static JsonObject RestartHooks(UiTestContext ctx, string runAs)
    {
        JsonObject hooks = Hooks(ctx);
        hooks["helperMissing"] = true;
        hooks["runAsAdmin"] = runAs;
        return hooks;
    }

    [Fact]
    public Task Restart_as_administrator_saves_the_open_tabs_and_does_not_overwrite_them_while_exiting() => UiTestContext.RunAsync(async ctx =>
    {
        // TC-ENG-28-10 の不具合の回帰: 確認で文書を閉じた後にセッションを写し直し、タブが「閉じたタブ」に移った空のセッションで上書きしていた。
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = RestartHooks(ctx, "accept"), Files = [seq] });
        await app.WaitForTabsAsync(1);

        await RestartAsAdministratorFromGuidanceAsync(app);
        await app.WaitForExitAsync(UiTest.Scaled(TimeSpan.FromSeconds(30)));

        // 新しいプロセスが復元するセッション: TD-SEQ-1M のタブが開いたまま (閉じたタブではない)。
        JsonObject session = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(app.Profile, "session.json")))!.AsObject();
        JsonArray tabs = session["windows"]![0]!["tabs"]!.AsArray();
        Assert.Equal([Path.GetFullPath(seq)], tabs.Select(t => Path.GetFullPath(t!["path"]!.GetValue<string>())));
        Assert.DoesNotContain(session["closedTabs"]?.AsArray() ?? [], t => string.Equals(t!["path"]?.GetValue<string>(), seq, StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task Restart_as_administrator_denied_by_uac_keeps_the_app_and_its_tabs() => UiTestContext.RunAsync(async ctx =>
    {
        // ENG-28 の仕様 12 の 7: UAC で拒否されたら終了せず、元の状態のまま続ける (タブを閉じない)。
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = RestartHooks(ctx, "deny"), Files = [seq] });
        await app.WaitForTabsAsync(1);

        await RestartAsAdministratorFromGuidanceAsync(app);
        await app.WaitUntilAsync(() => Task.FromResult(app.Find("AdminGuidanceDialog") is null), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the guidance dialog to close");
        await app.IdleAsync();
        Assert.Equal([Path.GetFileName(seq)], await app.TabNamesAsync());
    });
}
