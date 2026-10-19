using HexEditor.Core.Devices;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Elevation;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Forensics.WriteBlock;

/// <summary>
/// 書き込み保護モード (FOR-01) をデバイスの経路そのものに掛ける。アプリが使うデバイスのアクセス手段 (直接・同じプロセス・補助プロセス・偽物) は
/// すべてこれで包む。モードが有効な間は、読み書きでのオープン・書き込み・ロック・ディスマウントを <see cref="Win32Errors.WriteProtect"/> で拒否する
/// (補助プロセスの拒否と同じ結果。開いた後でモードを有効にした場合も、その時点から拒否する)。読み取りとデバイス情報の取得は通す。
/// </summary>
public sealed class WriteBlockedDeviceAccess(IDeviceAccess inner, Func<WriteBlockPolicy>? policy = null) : IDeviceAccess
{
    private readonly Func<WriteBlockPolicy> _policy = policy ?? (() => WriteBlockGate.Current);

    public IDeviceAccess Inner { get; } = inner;

    public DeviceCatalog Enumerate() => Inner.Enumerate();

    public IDeviceHandle Open(string path, bool writable)
    {
        if (writable && _policy().IsEnabled)
        {
            throw new DeviceException(Win32Errors.WriteProtect, "Write protection mode is on; the device cannot be opened for writing.");
        }

        return new Handle(Inner.Open(path, writable), _policy);
    }

    /// <summary>包んだアクセス手段の中身 (包んでいなければそのまま)。</summary>
    public static IDeviceAccess Unwrap(IDeviceAccess access) => access is WriteBlockedDeviceAccess w ? w.Inner : access;

    private sealed class Handle(IDeviceHandle inner, Func<WriteBlockPolicy> policy) : IDeviceHandle, IDeviceInfoSource
    {
        public string Path => inner.Path;

        public bool Writable => inner.Writable;

        public DeviceGeometry Geometry => inner.Geometry;

        private bool Blocked => policy().IsEnabled;

        public int ReadSectors(long offset, Span<byte> buffer) => inner.ReadSectors(offset, buffer);

        public int WriteSectors(long offset, ReadOnlySpan<byte> data) => Blocked ? Win32Errors.WriteProtect : inner.WriteSectors(offset, data);

        public int LockVolume() => Blocked ? Win32Errors.WriteProtect : inner.LockVolume();

        public int DismountVolume() => Blocked ? Win32Errors.WriteProtect : inner.DismountVolume();

        public int UnlockVolume() => inner.UnlockVolume();

        public int Flush() => inner.Flush();

        public int QueryDeviceInfo(DeviceInfoKind kind, out byte[] data)
        {
            if (inner is IDeviceInfoSource info)
            {
                return info.QueryDeviceInfo(kind, out data);
            }

            data = [];
            return Win32Errors.NotSupported;
        }

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>
/// プロセスメモリの経路の書き込み保護 (FOR-01)。読み書きでのオープンだけを拒否する (開いた後の書き込みは <see cref="ProcessMemoryByteSource.TryWrite"/> と
/// <see cref="ProcessWrite"/> が拒否する。メモリの付加機能 (一時停止・スレッド) を保つため、メモリそのものは包まない)。
/// </summary>
public sealed class WriteBlockedProcessAccess(IProcessAccess inner, Func<WriteBlockPolicy>? policy = null) : IProcessAccess
{
    private readonly Func<WriteBlockPolicy> _policy = policy ?? (() => WriteBlockGate.Current);

    public IProcessAccess Inner { get; } = inner;

    public IReadOnlyList<ProcessEntry> Enumerate() => Inner.Enumerate();

    public IProcessMemory Open(int pid, bool writable)
    {
        if (writable && _policy().IsEnabled)
        {
            throw new ProcessAccessException(ProcessOpenFailure.AccessDenied, Win32Errors.WriteProtect,
                "Write protection mode is on; the process cannot be opened for writing.");
        }

        return Inner.Open(pid, writable);
    }
}
