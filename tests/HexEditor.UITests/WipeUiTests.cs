using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData.FileSystemImages;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 安全な消去と墨塗りの UI (12 の FOR-30・FOR-32)。消去の対象は、テスト用の一時フォルダのイメージファイルを中身にした偽のディスク (fakeDevices) と、
/// 一時フォルダにコピーしたテスト用イメージだけ。実機のディスク・ボリュームには触れない。OS 経由の空き領域の消去は UI テストでは実行しない
/// (この PC のボリュームに書かないため。Core のテストと CI の管理者のテストで確かめる)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class WipeUiTests
{
    private const long MiB = 1024 * 1024;

    private const string DiskPath = @"\\.\PhysicalDrive1";

    /// <summary>TD-FOR-VHDX-PATTERN と同じ内容のイメージファイルを中身にした偽のディスク 1。</summary>
    private static JsonObject Hooks(UiTestContext ctx, string image, long size)
    {
        var devices = new JsonObject
        {
            ["disks"] = new JsonArray(new JsonObject
            {
                ["number"] = 1,
                ["model"] = "Pattern Disk",
                ["serial"] = "PATTERN1",
                ["size"] = size,
                ["sectorSize"] = 512,
                ["image"] = image,
                ["requiresAdmin"] = true,
            }),
        };
        string devicePath = ctx.WriteFile("fake-devices.json", Encoding.UTF8.GetBytes(devices.ToJsonString()));
        return new JsonObject { ["fakeDevices"] = devicePath, ["elevated"] = false, ["culture"] = "en-US", ["timeZone"] = "UTC" };
    }

    private static async Task<(AppSession App, string Image)> OpenPatternDiskAsync(UiTestContext ctx, long size)
    {
        string image = Path.Combine(ctx.Root, "pattern.img");
        WipeImages.WritePatternDisk(image, size);
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx, image, size) });
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = DiskPath, ["readOnly"] = false });
        Assert.True(opened["writable"]?.GetValue<bool>() == true, opened.ToJsonString());
        return (app, image);
    }

    private static byte[] ReadShared(string path, long offset, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string ShaShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool AllPattern(byte[] data, long offset)
    {
        byte[] expected = new byte[data.Length];
        WipeImages.FillPattern(expected, offset);
        return data.AsSpan().SequenceEqual(expected);
    }

    private static async Task<JsonObject> WaitConfirmAsync(AppSession app)
    {
        JsonObject confirm = null!;
        await app.WaitUntilAsync(async () => (confirm = await app.SendAsync("wipeConfirm"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)),
            "the confirmation dialog");
        return confirm;
    }

    private static async Task<JsonObject> WaitResultAsync(AppSession app, TimeSpan timeout)
    {
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("wipeState"))["result"] is not null
            && state["operation"]?["state"]?.GetValue<string>() is "Completed" or "Failed" or "Cancelled", timeout, "the wipe to finish");
        return state;
    }

    private static async Task SetCheckedAsync(AppSession app, string id)
    {
        await app.WaitForAsync(id);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = id, ["value"] = true });
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-30-01")]
    public Task Random_once_on_a_selected_range_confirms_the_range_and_removes_the_pattern() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, string image) = await OpenPatternDiskAsync(ctx, 16 * MiB);
        await app.SelectAsync(0x100000, 0x800000);

        await app.CommandAsync("Command_WipeSelection");
        await app.WaitForAsync("WipeDialog");
        await SetCheckedAsync(app, "Wipe_Random");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        // 手順 2: 本文 (対象・範囲・方式と回数・所要時間の目安・取り消せないこと) とボタン。
        JsonObject confirm = await WaitConfirmAsync(app);
        string text = confirm["text"]!.GetValue<string>();
        Assert.Contains("Disk 1", text, StringComparison.Ordinal);
        Assert.Contains("Pattern Disk", text, StringComparison.Ordinal);
        Assert.Contains("PATTERN1", text, StringComparison.Ordinal);
        Assert.Contains("start LBA 2,048, 16,384 sectors, 8,388,608 bytes", text, StringComparison.Ordinal);
        Assert.Contains("(1 pass)", text, StringComparison.Ordinal);
        Assert.Contains("Estimated time", text, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", text, StringComparison.Ordinal);
        Assert.Equal("Erase 8,388,608 bytes", confirm["primary"]!.GetValue<string>());
        Assert.Equal("Close", confirm["defaultButton"]!.GetValue<string>());
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        JsonObject state = await WaitResultAsync(app, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        Assert.True(state["result"]!["succeeded"]!.GetValue<bool>(), state.ToJsonString());
        Assert.True(state["result"]!["verified"]!.GetValue<bool>());

        // 手順 3・4: 範囲に ORIGDATA がなく、前後 1 MiB は元のまま。
        byte[] range = ReadShared(image, 0x100000, 0x800000);
        Assert.True(range.AsSpan().IndexOf("ORIGDATA"u8) < 0);
        Assert.True(AllPattern(ReadShared(image, 0, (int)MiB), 0));
        Assert.True(AllPattern(ReadShared(image, 0x900000, (int)MiB), 0x900000));

        // 消去の後、ドキュメントの表示も読み直されている。
        Assert.Equal(range.AsSpan(0, 64).ToArray(), await app.BytesAsync(0x100000, 64));

        // 監査ログ (このブランチではアプリのログ。統合で ForensicsService の監査ログに記録する)。
        string log = await app.WaitForLogAsync(l => l.Contains("Audit write.wipe Succeeded", StringComparison.Ordinal), "the audit record");
        Assert.Contains("method=random", log, StringComparison.Ordinal);
        Assert.Contains("bytes=8388608", log, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-30-02")]
    public Task Dod_shows_three_passes_in_order_with_the_remaining_time_and_verifies() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, string image) = await OpenPatternDiskAsync(ctx, 48 * MiB);
        await app.SendAsync("wipeDelay", new JsonObject { ["ms"] = 60 });
        await app.SelectAsync(0, 32 * MiB);
        await app.CommandAsync("Command_WipeSelection");
        await app.WaitForAsync("WipeDialog");
        await SetCheckedAsync(app, "Wipe_Dod");
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await WaitConfirmAsync(app);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });

        // 手順 2: 進捗の表示を記録する (何回目か、残り時間)。
        var details = new List<string>();
        bool remaining = false;
        JsonObject state = null!;
        await app.WaitUntilAsync(async () =>
        {
            state = await app.SendAsync("wipeState");
            if (state["operation"]?["detail"]?.GetValue<string>() is { } detail && (details.Count == 0 || details[^1] != detail))
            {
                details.Add(detail);
            }

            remaining |= state["operation"]?["remainingMs"] is not null && state["operation"]?["total"]?.GetValue<long>() == 32 * MiB * 4;
            return state["result"] is not null && state["operation"]?["state"]?.GetValue<string>() is "Completed" or "Failed";
        }, UiTest.Scaled(TimeSpan.FromSeconds(120)), "the wipe to finish");

        Assert.Equal(["Pass 1 of 3", "Pass 2 of 3", "Pass 3 of 3", "Verifying"], details.Where(d => d.StartsWith("Pass", StringComparison.Ordinal) || d == "Verifying"));
        Assert.True(remaining, "the remaining time is shown for all passes");
        Assert.True(state["result"]!["verified"]!.GetValue<bool>());
        Assert.Equal(0, state["result"]!["mismatches"]!.GetValue<int>());
        Assert.Equal(3, state["result"]!["passes"]!.GetValue<int>());
        Assert.True(ReadShared(image, 0, (int)(32 * MiB)).AsSpan().IndexOf("ORIGDATA"u8) < 0);
        Assert.True(AllPattern(ReadShared(image, 32 * MiB, (int)MiB), 32 * MiB));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-30-03")]
    public Task Erasing_a_whole_disk_needs_the_disk_name_typed_and_cancel_is_the_default() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, string image) = await OpenPatternDiskAsync(ctx, 16 * MiB);
        string sha = ShaShared(image);

        async Task OpenConfirmAsync()
        {
            await app.CommandAsync("Command_WipeDisk");
            await app.WaitForAsync("WipeDiskDialog");
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
            await WaitConfirmAsync(app);
        }

        // 手順 2: 入力欄が空なら実行ボタンは無効で、既定のフォーカスは「キャンセル」。
        await OpenConfirmAsync();
        JsonObject confirm = null!;
        // ダイアログを開くとフォーカスを「キャンセル」に置く (前面にないウィンドウではフォーカスの取得が空のことがあるため、置いた先と、取得できたら
        // その要素が「キャンセル」かを確かめる)。
        for (int tries = 0; tries < 50 && (confirm = await app.SendAsync("wipeConfirm"))["initialFocus"]?.GetValue<string>() != "CloseButton"; tries++)
        {
            await app.IdleAsync();
        }

        Assert.True(confirm["initialFocus"]?.GetValue<string>() == "CloseButton", confirm.ToJsonString());
        Assert.True(confirm["focused"]?.GetValue<string>() is null or "Button:CloseButton" or "ContentDialog:WipeConfirmDialog",
            confirm["focused"]?.GetValue<string>());
        Assert.False(confirm["primaryEnabled"]!.GetValue<bool>());
        Assert.Equal("Close", confirm["defaultButton"]!.GetValue<string>());
        Assert.Contains("type \u201cDisk 1\u201d", confirm["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // 手順 3: Enter (既定のボタン) では実行されない。
        JsonObject enter = await app.SendAsync("wipeConfirmEnter");
        Assert.Equal("Close", enter["pressed"]!.GetValue<string>());
        await app.WaitUntilAsync(async () => !(await app.SendAsync("wipeConfirm"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog to close");
        Assert.Null((await app.SendAsync("wipeState"))["result"]);

        // 手順 4: 「Disk 1」を入力すると「Erase Disk 1」が有効になる。「キャンセル」を押す。
        await OpenConfirmAsync();
        await app.UiaSetValueAsync("WipeConfirmDialog_Input", "Disk 1");
        await app.WaitUntilAsync(async () => (confirm = await app.SendAsync("wipeConfirm"))["primaryEnabled"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the erase button to be enabled");
        Assert.Equal("Erase Disk 1", confirm["primary"]!.GetValue<string>());
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
        await app.WaitUntilAsync(async () => !(await app.SendAsync("wipeConfirm"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog to close");

        // 手順 5: ディスクの内容は変わらない。
        Assert.Null((await app.SendAsync("wipeState"))["result"]);
        Assert.Equal(sha, ShaShared(image));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-30-04")]
    public Task Write_protect_mode_disables_every_wipe_menu() => UiTestContext.RunAsync(async ctx =>
    {
        string file = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [file], Hooks = new JsonObject { ["culture"] = "en-US" }, ExtraArgs = ["--write-block"] });
        await app.SelectAsync(0x1000, 0x1000);
        foreach (string id in new[] { "Command_WipeSelection", "Command_WipeDisk", "Command_WipeFreeSpace", "Command_WipeSlack" })
        {
            JsonObject item = await app.SendAsync("menuItem", new JsonObject { ["id"] = id });
            Assert.True(item["found"]!.GetValue<bool>(), id);
            Assert.False(item["enabled"]!.GetValue<bool>(), id);
            string reason = (item["reason"]?.GetValue<string>() ?? string.Empty) + (item["toolTip"]?.GetValue<string>() ?? string.Empty);
            Assert.Contains("Write-protect mode is on", reason, StringComparison.Ordinal);
        }

        JsonObject hexItem = await app.SendAsync("wipeHexMenu");
        Assert.True(hexItem["visible"]!.GetValue<bool>());
        Assert.False(hexItem["enabled"]!.GetValue<bool>());
        Assert.Contains("Write-protect mode is on", hexItem["toolTip"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject executed = await app.SendAsync("execute", new JsonObject { ["id"] = "forensics.wipe.selection", ["fromPalette"] = true });
        Assert.False(executed["executed"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-32-02")]
    public Task Undo_right_after_redaction_restores_the_image_without_writing_it() => UiTestContext.RunAsync(async ctx =>
    {
        string path = Path.Combine(ctx.Root, "ntfs.img");
        FileSystemImageSet.Ntfs().Image.Save(path);
        string fileSha = ShaShared(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["culture"] = "en-US" } });
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = path, ["sectorSize"] = 512, ["readOnly"] = false });
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.True(shown["opened"]!.GetValue<bool>(), shown.ToJsonString());
        JsonObject browser = await app.SendAsync("fsBrowser", new JsonObject { ["select"] = "photo.png", ["entries"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        Assert.Contains(browser["menu"]!.AsArray(), m => m!["id"]!.GetValue<string>() == "Fs_Menu_Redact");
        string before = (await app.SendAsync("wipeDocSha", null, UiTest.Scaled(TimeSpan.FromSeconds(60))))["sha256"]!.GetValue<string>();

        // 手順 1: 墨塗りする (ダイアログの既定: パターン 00、名前は置き換えない)。記録は一時フォルダに書く。
        await app.SendAsync("fsRedact", new JsonObject { ["dialog"] = true });
        await app.WaitForAsync("RedactDialog");
        await app.UiaSetValueAsync("Redact_Log", Path.Combine(ctx.Root, "redaction-log.csv"));
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("wipeState"))["result"]?["done"]?.GetValue<bool>() == true, UiTest.Scaled(TimeSpan.FromSeconds(30)),
            "the redaction");
        JsonObject redacted = await app.SendAsync("wipeDocSha", null, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        Assert.NotEqual(before, redacted["sha256"]!.GetValue<string>());
        Assert.True(redacted["modified"]!.GetValue<bool>());
        Assert.Equal("Redaction", redacted["undoName"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(ctx.Root, "redaction-log.csv")));

        // 手順 2・3: Ctrl+Z で元に戻すと、ドキュメント全体の SHA-256 が元と同じ。イメージには書き込まれていない。
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.WaitUntilAsync(async () => (await app.SendAsync("wipeDocSha", null, UiTest.Scaled(TimeSpan.FromSeconds(60))))["sha256"]!.GetValue<string>() == before,
            UiTest.Scaled(TimeSpan.FromSeconds(30)), "the undo");
        Assert.False((await app.SendAsync("wipeDocSha"))["modified"]!.GetValue<bool>());
        Assert.Equal(fileSha, ShaShared(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    });

    [Fact]
    public Task Redaction_is_disabled_for_a_read_only_image_with_the_reason() => UiTestContext.RunAsync(async ctx =>
    {
        string path = Path.Combine(ctx.Root, "ro.img");
        FileSystemImageSet.Ntfs().Image.Save(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = new JsonObject { ["culture"] = "en-US" } });
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = path, ["sectorSize"] = 512, ["readOnly"] = true });
        await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        if ((await app.SendAsync("wipeDocSha"))["readOnly"]!.GetValue<bool>() != true)
        {
            await app.CommandAsync("Command_ReadOnly");
        }

        Assert.True((await app.SendAsync("wipeDocSha"))["readOnly"]!.GetValue<bool>());
        await app.SendAsync("fsBrowser", new JsonObject { ["select"] = "photo.png", ["entries"] = false }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        JsonObject item = await app.SendAsync("menuItem", new JsonObject { ["id"] = "Command_FsRedact" });
        Assert.False(item["enabled"]!.GetValue<bool>());
        Assert.True(((item["reason"]?.GetValue<string>() ?? string.Empty) + item["toolTip"]).Contains("read-only", StringComparison.Ordinal), item.ToJsonString());
    });
}
