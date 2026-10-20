using System.Globalization;
using HexEditor.Core.Devices;
using HexEditor.Core.Elevation;

namespace HexEditor.Core.Forensics.WriteBlock;

/// <summary>OS の USB 書き込み保護の値を読み書きできない (権限がない、グループポリシーで管理されているなど)。<see cref="ErrorCode"/> は Win32 のエラーコード。</summary>
public sealed class UsbWriteProtectException(int errorCode, string message) : IOException(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// レジストリの <c>HKLM\SYSTEM\CurrentControlSet\Control\StorageDevicePolicies</c> の <c>WriteProtect</c> (DWORD) の読み書き (12 の FOR-02)。
/// 実装は 3 つ: 実機のレジストリ (<see cref="RegistryUsbWriteProtectStore"/>。書けるのは管理者だけ)、補助プロセス経由
/// (<see cref="HelperUsbWriteProtectStore"/>)、テスト用の偽物 (<see cref="FakeUsbWriteProtectStore"/>)。
/// </summary>
public interface IUsbWriteProtectStore
{
    /// <summary>今の値。値 (またはキー) がなければ null。</summary>
    uint? Read();

    /// <summary>値を書く (キーがなければ作る)。</summary>
    void Write(uint value);

    /// <summary>値を削除する (もともと値がなかった状態に戻す)。値がなければ何もしない。</summary>
    void Delete();
}

/// <summary>有効にする前の状態 (無効にするときに戻す。FOR-02 の仕様 1)。</summary>
public readonly record struct UsbWriteProtectOriginal(uint? Value)
{
    private const string Absent = "absent";

    /// <summary>アプリの状態に保存する文字列。</summary>
    public string ToState() => Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : Absent;

    public static UsbWriteProtectOriginal? FromState(string? state) =>
        state == Absent ? new UsbWriteProtectOriginal(null)
        : uint.TryParse(state, NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? new UsbWriteProtectOriginal(v)
        : null;
}

/// <summary>OS の USB 書き込み保護の切り替え (FOR-02 の仕様 1)。</summary>
public static class UsbWriteProtect
{
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies";
    public const string ValueName = "WriteProtect";

    /// <summary>有効か (値が 0 以外)。</summary>
    public static bool IsOn(uint? value) => value is not null and not 0;

    /// <summary>有効にする (値を 1 にする)。戻す前の状態を返す (呼び出し側が保存する)。</summary>
    public static UsbWriteProtectOriginal Enable(IUsbWriteProtectStore store)
    {
        uint? before = store.Read();
        store.Write(1);
        return new UsbWriteProtectOriginal(before);
    }

    /// <summary>
    /// 無効にする。もともと値がなかった場合は値を削除し、それ以外は 0 にする。<paramref name="original"/> が分からなければ (状態を失った) 0 にする。
    /// </summary>
    public static void Disable(IUsbWriteProtectStore store, UsbWriteProtectOriginal? original)
    {
        if (original is { Value: null })
        {
            store.Delete();
        }
        else
        {
            store.Write(0);
        }
    }
}

/// <summary>
/// 実機のレジストリ。この PC (開発・テスト) では書き込まない: テストは <see cref="FakeUsbWriteProtectStore"/> を使い、実機の経路は CI の
/// 管理者のランナー (使い捨て) のテストだけで確かめる。
/// </summary>
public sealed class RegistryUsbWriteProtectStore : IUsbWriteProtectStore
{
    public static RegistryUsbWriteProtectStore Instance { get; } = new();

    public uint? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UsbWriteProtect.KeyPath, writable: false);
            return key?.GetValue(UsbWriteProtect.ValueName) switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw Translate(ex);
        }
    }

    public void Write(uint value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new UsbWriteProtectException(Win32Errors.NotSupported, "The registry is not available.");
        }

        // 書く値は 0 と 1 だけ (補助プロセスの許可リストと同じ。どの経路から呼ばれても他の値は OS の設定に書かない)。
        if (value is not (0 or 1))
        {
            throw new UsbWriteProtectException(Win32Errors.InvalidParameter, "Only 0 and 1 can be written.");
        }

        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UsbWriteProtect.KeyPath, writable: true);
            key.SetValue(UsbWriteProtect.ValueName, unchecked((int)value), Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw Translate(ex);
        }
    }

    public void Delete()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new UsbWriteProtectException(Win32Errors.NotSupported, "The registry is not available.");
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UsbWriteProtect.KeyPath, writable: true);
            key?.DeleteValue(UsbWriteProtect.ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw Translate(ex);
        }
    }

    private static UsbWriteProtectException Translate(Exception ex) => new(
        ex is System.Security.SecurityException or UnauthorizedAccessException ? Win32Errors.AccessDenied : (ex.HResult & 0xFFFF) is var code and > 0 ? code : Win32Errors.GenFailure,
        ex.Message);
}

/// <summary>補助プロセス経由 (ENG-28 の <c>GetUsbWriteProtect</c> / <c>SetUsbWriteProtect</c>)。</summary>
public sealed class HelperUsbWriteProtectStore(HelperClient client) : IUsbWriteProtectStore
{
    public uint? Read()
    {
        (uint status, byte[] body) = Send(HelperCommand.GetUsbWriteProtect, []);
        Check(status);
        var reader = new BodyReader(body);
        uint value = reader.U32();
        return value == HelperProtocol.UsbWriteProtectDelete ? null : value;
    }

    public void Write(uint value) => Check(Send(HelperCommand.SetUsbWriteProtect, new BodyWriter().U32(value).ToArray()).Status);

    public void Delete() => Check(Send(HelperCommand.SetUsbWriteProtect, new BodyWriter().U32(HelperProtocol.UsbWriteProtectDelete).ToArray()).Status);

    private (uint Status, byte[] Body) Send(HelperCommand command, byte[] body)
    {
        try
        {
            return client.Send(command, 0, body);
        }
        catch (Exception ex) when (ex is HelperTimeoutException or HelperDisconnectedException)
        {
            throw new UsbWriteProtectException(Win32Errors.BrokenPipe, ex.Message);
        }
    }

    private static void Check(uint status)
    {
        if (status != 0)
        {
            throw new UsbWriteProtectException((int)status, $"The elevated helper could not change the setting (error {status}).");
        }
    }
}

/// <summary>テスト用の偽物 (メモリ上)。<see cref="ManagedByPolicy"/> でグループポリシーによる拒否を、<see cref="Denied"/> で権限の不足を再現する。</summary>
public sealed class FakeUsbWriteProtectStore : IUsbWriteProtectStore
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private uint? _value;

    public FakeUsbWriteProtectStore(uint? initial = null) => _value = initial;

    public bool KeyExists { get; private set; }

    public bool ManagedByPolicy { get; set; }

    public bool Denied { get; set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public uint? Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }

        set
        {
            lock (_gate)
            {
                _value = value;
                KeyExists |= value is not null;
            }
        }
    }

    public uint? Read()
    {
        lock (_gate)
        {
            _calls.Add("read");
            return _value;
        }
    }

    public void Write(uint value)
    {
        lock (_gate)
        {
            _calls.Add("write " + value.ToString(CultureInfo.InvariantCulture));
            CheckWritable();
            KeyExists = true;
            _value = value;
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            _calls.Add("delete");
            CheckWritable();
            _value = null;
        }
    }

    private void CheckWritable()
    {
        if (Denied)
        {
            throw new UsbWriteProtectException(Win32Errors.AccessDenied, "Access is denied.");
        }

        if (ManagedByPolicy)
        {
            throw new UsbWriteProtectException(Win32Errors.AccessDenied, "The value is managed by Group Policy.");
        }
    }
}
