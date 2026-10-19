using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ディスクのクローン (12 の FOR-24) とセクタ範囲の保存と復元 (FOR-25) を、CI のランナーで接続した仮想ディスク (VHDX) で確かめる。
/// CI のジョブ ui-privileged だけで動く (<see cref="CiPrivilegedFactAttribute"/>)。build/tests/Mount-TestVhd.ps1 が TD-VHDX-MBR に加えて、
/// クローンの出力先の 512 MiB の空の VHDX (HEXEDITOR_TEST_CLONE_DISK) と TD-VHDX-GPT (HEXEDITOR_TEST_GPT_DISK) を接続する。
/// 開発者の PC ではスキップし、ディスクに触れない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ForensicImagePrivilegedTests
{
    private static readonly Lazy<Dictionary<string, string>> En = new(() => UiHelpers.LoadResw("en"));

    private static string R(string key) => En.Value[key];

    private static int CloneDisk => int.Parse(Environment.GetEnvironmentVariable("HEXEDITOR_TEST_CLONE_DISK")!, System.Globalization.CultureInfo.InvariantCulture);

    private static int GptDisk => int.Parse(Environment.GetEnvironmentVariable("HEXEDITOR_TEST_GPT_DISK")!, System.Globalization.CultureInfo.InvariantCulture);

    private static string DiskName(int number) => R("ImgClone_DiskName").Replace("{0}", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static async Task<AppSession> StartAsync(UiTestContext ctx, IReadOnlyList<string>? files = null)
    {
        CiPrivileged.EnsureEnabled();
        return await ctx.StartAsync(new AppOptions { RealElevation = true, Files = files ?? [] });
    }

    /// <summary>強い確認のダイアログ: 入力欄が空・違う名前の間は実行ボタンが無効、既定のフォーカスは「キャンセル」、正しい名前で実行する。</summary>
    private static async Task PassStrongConfirmAsync(AppSession app, string dialogId, string name, string execute)
    {
        AutomationElement dialog = await app.WaitForDialogAsync(dialogId);
        await app.WaitUntilAsync(() => Task.FromResult(AppSession.NameOf(AppSession.Automation.FocusedElement()) == R("Common_Cancel")),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the default focus on Cancel");
        Assert.False(app.Button(execute)!.IsEnabled);
        await app.UiaSetValueAsync(dialogId + "_Name", "Disk 999");
        Assert.False(app.Button(execute)!.IsEnabled);
        await app.UiaSetValueAsync(dialogId + "_Name", name);
        await app.WaitUntilAsync(() => Task.FromResult(app.Button(execute)!.IsEnabled), UiTest.Scaled(TimeSpan.FromSeconds(5)), "the execute button");
        _ = dialog;
        await app.InvokeDialogButtonAsync(execute, idle: false);
    }

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-24-01")]
    public Task Cloning_an_image_to_a_virtual_disk_verifies_as_match() => UiTestContext.RunAsync(async ctx =>
    {
        // 入力元: GPT のディスクイメージ (TD-FOR-RAID-SRC。TD-FOR-NTFS-FAT32 はファイルシステムの領域のテストデータのため、同じ GPT の構造の 128 MiB を使う)。
        string source = TestDataCatalog.Get("TD-FOR-RAID-SRC");
        AppSession app = await StartAsync(ctx, [source]);
        await app.SendAsync("imgCloneStart", new JsonObject { ["disk"] = CloneDisk });
        string name = DiskName(CloneDisk);
        await PassStrongConfirmAsync(app, "ImgCloneConfirmDialog", name, R("ImgClone_Execute").Replace("{0}", name, StringComparison.Ordinal));
        await app.WaitUntilAsync(async () => (await app.SendAsync("imgState"))["clone"] is not null, UiTest.Scaled(TimeSpan.FromSeconds(180)), "the clone");

        JsonObject clone = (await app.SendAsync("imgState"))["clone"]!.AsObject();
        Assert.Equal("Completed", clone["status"]!.GetValue<string>());
        Assert.Equal("Match", clone["verification"]!.GetValue<string>());
        Assert.Equal(Hash(File.ReadAllBytes(source)), Hash(CiPrivileged.ReadDevice($@"\\.\PhysicalDrive{CloneDisk}", (int)TestDataCatalog.RaidSourceLength)));
    });

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-24-02")]
    public Task A_volume_with_an_open_file_cannot_be_locked_and_nothing_is_written() => UiTestContext.RunAsync(async ctx =>
    {
        string before = Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 64 << 20));
        string source = TestDataCatalog.Get("TD-FOR-RAID0-M0");
        using var open = new FileStream($@"{CiPrivileged.VhdVolumeLetter}:\open.txt", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        open.Write("held"u8);
        open.Flush();
        AppSession app = await StartAsync(ctx, [source]);
        await app.SendAsync("imgCloneStart", new JsonObject { ["disk"] = CiPrivileged.VhdDisk, ["verify"] = false });
        string name = DiskName(CiPrivileged.VhdDisk);
        await PassStrongConfirmAsync(app, "ImgCloneConfirmDialog", name, R("ImgClone_Execute").Replace("{0}", name, StringComparison.Ordinal));

        // ロックできない: 強制的なディスマウントを尋ねられたら断る。実行しない旨のメッセージが出る。
        await app.WaitUntilAsync(async () =>
        {
            if (app.Find("DiskWriteDismountDialog") is not null)
            {
                await app.InvokeDialogButtonAsync(R("Common_Cancel"), idle: false);
            }

            return app.NotificationsText().Contains(R("ImgClone_LockFailed").Split('{')[0], StringComparison.Ordinal);
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the lock failure notice");
        open.Dispose();
        File.Delete($@"{CiPrivileged.VhdVolumeLetter}:\open.txt");
        Assert.Equal(before, Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 64 << 20)));
    });

    /// <summary>TD-VHDX-GPT を「ディスクを開く」で読み書きを許可して開く (偽ではない、本物の経路)。</summary>
    private static async Task OpenGptDiskAsync(AppSession app)
    {
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = $@"\\.\PhysicalDrive{GptDisk}", ["readOnly"] = true });
        Assert.Null(opened["error"]);
        await app.WaitForTabsAsync(1);
    }

    private static int PartitionCount(int disk)
    {
        using Process p = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"Update-Disk -Number {disk}; @(Get-Partition -DiskNumber {disk} -ErrorAction SilentlyContinue).Count\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(text.Trim().Split('\n')[^1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-25-01")]
    public Task Saving_and_restoring_the_gpt_brings_the_partitions_back() => UiTestContext.RunAsync(async ctx =>
    {
        string disk = $@"\\.\PhysicalDrive{GptDisk}";
        int partitions = PartitionCount(GptDisk);
        Assert.Equal(2, partitions);
        AppSession app = await StartAsync(ctx);
        await OpenGptDiskAsync(app);
        string bin = Path.Combine(ctx.Root, "gpt.bin");
        JsonObject saved = await app.SendAsync("imgSectorsSave", new JsonObject { ["startLba"] = 0, ["count"] = 34, ["path"] = bin });
        Assert.True(saved["saved"]!.GetValue<bool>());
        JsonObject json = JsonNode.Parse(File.ReadAllText(bin + ".json"))!.AsObject();
        foreach (string key in new[] { "model", "serialNumber", "capacity", "sectorSize", "startLba", "sectorCount", "savedAt", "sha256" })
        {
            Assert.True(json.ContainsKey(key), key);
        }

        Assert.Equal(34, json["sectorCount"]!.GetValue<long>());
        Assert.EndsWith("Z", json["savedAt"]!.GetValue<string>(), StringComparison.Ordinal);

        // LBA 1〜33 を 00 で上書きし、パーティションがなくなったことを確かめる。
        CiPrivileged.WriteDevice(disk, 512, new byte[33 * 512]);
        Assert.Equal(0, PartitionCount(GptDisk));
        byte[] broken = CiPrivileged.ReadDevice(disk, 34 * 512);

        await app.SendAsync("imgSectorsRestoreStart", new JsonObject { ["path"] = bin });
        await app.WaitForDialogAsync("ImgSectorsRestoreDialog");
        await app.InvokeDialogButtonAsync(R("ImgSectors_Next"), idle: false);
        await app.WaitForDialogAsync("ImgRestoreConfirmDialog");
        await app.InvokeDialogButtonAsync(R("ImgSectors_Write").Replace("{0}", "34", StringComparison.Ordinal), idle: false);
        await app.WaitUntilAsync(async () => (await app.SendAsync("imgState"))["restore"] is not null, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the restore");
        JsonObject restore = (await app.SendAsync("imgState"))["restore"]!.AsObject();
        Assert.True(restore["success"]!.GetValue<bool>());
        Assert.Equal(partitions, PartitionCount(GptDisk));
        Assert.Equal(broken, File.ReadAllBytes(restore["backup"]!.GetValue<string>()));
    });

    [CiPrivilegedFact("Portable")]
    [Trait(UiTest.TC, "TC-FOR-25-02")]
    public Task A_changed_data_file_is_refused_and_the_disk_is_unchanged() => UiTestContext.RunAsync(async ctx =>
    {
        string disk = $@"\\.\PhysicalDrive{GptDisk}";
        AppSession app = await StartAsync(ctx);
        await OpenGptDiskAsync(app);
        string bin = Path.Combine(ctx.Root, "gpt.bin");
        await app.SendAsync("imgSectorsSave", new JsonObject { ["startLba"] = 0, ["count"] = 34, ["path"] = bin });
        byte[] data = File.ReadAllBytes(bin);
        data[0x200] ^= 0x01;
        File.WriteAllBytes(bin, data);
        string before = Hash(CiPrivileged.ReadDevice(disk, 64 << 20));

        await app.SendAsync("imgSectorsRestoreStart", new JsonObject { ["path"] = bin });
        await app.WaitForDialogAsync("ImgSectorsRestoreDialog");
        await app.WaitUntilAsync(async () => (await app.SendAsync("imgState"))["restorePlan"]?.GetValue<string>()?.Contains(R("ImgSectors_HashMismatch"), StringComparison.Ordinal) == true,
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the hash mismatch message");
        Assert.False(app.Button(R("ImgSectors_Next"))!.IsEnabled);
        await app.InvokeDialogButtonAsync(R("Common_Cancel"));
        Assert.Equal(before, Hash(CiPrivileged.ReadDevice(disk, 64 << 20)));
    });
}
