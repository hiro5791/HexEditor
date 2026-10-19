using HexEditor.Core.Forensics.WriteBlock;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>
/// FOR-02 OS の USB ストレージ書き込み保護 (Core)。この PC のレジストリには書かない: 偽の保存先 (<see cref="FakeUsbWriteProtectStore"/>) で
/// 切り替えの規則を確かめる。実機のレジストリでの確認は CI の管理者のランナーだけで行う (UI テストの PrivilegedTests の TC-FOR-02-02)。
/// </summary>
public sealed class UsbWriteProtectTests
{
    [Fact]
    [Trait("TC", "TC-FOR-02-02")]
    public void Disabling_restores_the_value_to_its_previous_state()
    {
        // 1. 値がない状態で有効にする → 1。2. 無効にする → 値がない。
        var store = new FakeUsbWriteProtectStore();
        UsbWriteProtectOriginal original = UsbWriteProtect.Enable(store);
        Assert.Equal(1u, store.Value);
        Assert.True(UsbWriteProtect.IsOn(store.Read()));
        UsbWriteProtect.Disable(store, UsbWriteProtectOriginal.FromState(original.ToState()));
        Assert.Null(store.Value);

        // 3. 値が 0 の状態で有効にしてから無効にする → 0。
        store = new FakeUsbWriteProtectStore(0);
        original = UsbWriteProtect.Enable(store);
        UsbWriteProtect.Disable(store, original);
        Assert.Equal(0u, store.Value);

        // 4. キーがない状態 (値もない) で有効にしてから無効にする → 値がない。
        store = new FakeUsbWriteProtectStore();
        Assert.False(store.KeyExists);
        UsbWriteProtect.Disable(store, UsbWriteProtect.Enable(store));
        Assert.Null(store.Value);
        Assert.Equal(["read", "write 1", "delete"], store.Calls);
    }

    [Fact]
    public void Unknown_original_state_disables_with_zero_and_states_round_trip()
    {
        var store = new FakeUsbWriteProtectStore(1);
        UsbWriteProtect.Disable(store, null);
        Assert.Equal(0u, store.Value);
        Assert.Equal(new UsbWriteProtectOriginal(null), UsbWriteProtectOriginal.FromState("absent"));
        Assert.Equal(new UsbWriteProtectOriginal(0), UsbWriteProtectOriginal.FromState("0"));
        Assert.Null(UsbWriteProtectOriginal.FromState("garbage"));
        Assert.False(UsbWriteProtect.IsOn(null));
        Assert.False(UsbWriteProtect.IsOn(0));
    }

    [Fact]
    public void Group_policy_and_access_denied_failures_do_not_change_the_value()
    {
        // FOR-02 の「エラー」: グループポリシーで管理されている・権限がない場合は理由を示し、設定を変えない。
        var store = new FakeUsbWriteProtectStore(0) { ManagedByPolicy = true };
        UsbWriteProtectException ex = Assert.Throws<UsbWriteProtectException>(() => UsbWriteProtect.Enable(store));
        Assert.Contains("Group Policy", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0u, store.Value);
        store = new FakeUsbWriteProtectStore() { Denied = true };
        Assert.Equal(5, Assert.Throws<UsbWriteProtectException>(() => UsbWriteProtect.Enable(store)).ErrorCode);
        Assert.Null(store.Value);
    }
}
