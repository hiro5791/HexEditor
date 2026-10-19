using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using Microsoft.Win32;

namespace HexEditor.UITests;

/// <summary>
/// 書き込み保護モード (FOR-01) の補助プロセス側の拒否と、OS の USB 書き込み保護 (FOR-02) の実機のレジストリの経路。CI の管理者のランナー
/// (使い捨て) だけで動く。開発者の PC ではスキップし、補助プロセスの起動・レジストリの書き込みをしない。
/// </summary>
public sealed partial class PrivilegedTests
{
    private const string UsbPolicyKey = @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies";

    [CiPrivilegedFact("Installer", "Portable")]
    [Trait(UiTest.TC, "TC-FOR-01-05")]
    public Task The_helper_refuses_write_requests_after_SetWriteBlock_and_the_virtual_disk_is_unchanged() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-VHDX-MBR を接続し、内容のハッシュを記録する。テスト用の命令が、管理者のアプリから本物の補助プロセスを起動して
        // Hello の後に SetWriteBlock を送り、要求を直接送る (UI のプロセスの代わりのクライアント)。
        string before = Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 1 << 20));
        AppSession app = await StartAsync(ctx, elevated: true);
        JsonObject r = await app.SendAsync("tcHelperProbe", new JsonObject
        {
            ["disk"] = CiPrivileged.VhdDiskPath,
            ["volume"] = $@"\\.\{CiPrivileged.VhdVolumeLetter.TrimEnd(':')}:",
        }, TimeSpan.FromMinutes(3));

        uint S(string key) => r[key]!.GetValue<uint>();
        Assert.Equal(0u, S("setWriteBlock"));

        // 1・5. 読み取りの OpenDevice と ReadSectors、GetUsbWriteProtect は成功する。
        Assert.Equal(0u, S("openRead"));
        Assert.Equal(0u, S("readSectors"));
        Assert.Equal(0u, S("getUsb"));

        // 2〜4. 読み書きの OpenDevice、WriteSectors、LockVolume・DismountVolume は拒否 (成功以外の状態)。
        Assert.NotEqual(0u, S("openWrite"));
        Assert.NotEqual(0u, S("writeSectors"));
        Assert.Equal(0u, S("openVolume"));
        Assert.NotEqual(0u, S("lock"));
        Assert.NotEqual(0u, S("dismount"));

        // 6. ディスクの内容が変わっていない (LBA 100 を含む先頭 1 MiB)。
        Assert.Equal(before, Hash(CiPrivileged.ReadDevice(CiPrivileged.VhdDiskPath, 1 << 20)));
    });

    /// <summary>テストの前後で StorageDevicePolicies のキーを退避・復元する (TC-FOR-02-02 の前提)。</summary>
    private sealed class UsbPolicyBackup : IDisposable
    {
        private readonly bool _keyExisted;
        private readonly object? _value;

        public UsbPolicyBackup()
        {
            CiPrivileged.EnsureEnabled();
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UsbPolicyKey);
            _keyExisted = key is not null;
            _value = key?.GetValue("WriteProtect");
        }

        public void Dispose()
        {
            if (!_keyExisted)
            {
                Registry.LocalMachine.DeleteSubKeyTree(UsbPolicyKey, throwOnMissingSubKey: false);
                return;
            }

            using RegistryKey key = Registry.LocalMachine.CreateSubKey(UsbPolicyKey, writable: true);
            if (_value is null)
            {
                key.DeleteValue("WriteProtect", throwOnMissingValue: false);
            }
            else
            {
                key.SetValue("WriteProtect", _value, RegistryValueKind.DWord);
            }
        }
    }

    private static int? UsbValue()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UsbPolicyKey);
        return key?.GetValue("WriteProtect") is int v ? v : null;
    }

    private static void SetUsbValue(int? value)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(UsbPolicyKey, writable: true);
        if (value is { } v)
        {
            key.SetValue("WriteProtect", v, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue("WriteProtect", throwOnMissingValue: false);
        }
    }

    [CiPrivilegedFact("Installer", "Portable")]
    [Trait(UiTest.TC, "TC-FOR-02-02")]
    public Task Turning_usb_write_protection_off_restores_the_registry_value() => UiTestContext.RunAsync(async ctx =>
    {
        using var backup = new UsbPolicyBackup();

        // 管理者として実行したアプリが同じプロセスでレジストリを書き換える (フック realUsbWriteProtect は CI の管理者のランナーでだけ効く)。
        AppSession app = await StartAsync(ctx, elevated: true, hooks: new JsonObject { ["realUsbWriteProtect"] = true });

        async Task ToggleAsync(int? expected)
        {
            await app.CommandAsync("Command_UsbWriteProtect");
            await app.WaitForDialogAsync("UsbProtectDialog");
            await app.InvokeDialogButtonAsync(R("UsbProtect_Change"));
            await app.WaitUntilAsync(() => Task.FromResult(UsbValue() == expected), TimeSpan.FromSeconds(20), $"WriteProtect = {expected?.ToString() ?? "absent"}");
        }

        // 1. 値を削除した状態で有効にする → 1。2. 無効にする → 値がない。
        SetUsbValue(null);
        await ToggleAsync(1);
        await ToggleAsync(null);

        // 3. 値が 0 の状態で有効にしてから無効にする → 0。
        SetUsbValue(0);
        await ToggleAsync(1);
        await ToggleAsync(0);

        // 4. キー自体がない状態で有効にしてから無効にする → 値がない。
        Registry.LocalMachine.DeleteSubKeyTree(UsbPolicyKey, throwOnMissingSubKey: false);
        await ToggleAsync(1);
        await ToggleAsync(null);
    });
}
