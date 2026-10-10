using System.Text.Json;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Elevation;

/// <summary>
/// 補助プロセスが受け付ける操作の実行 (ENG-28 の仕様 4)。許可リストの判定もここで行う。補助プロセス (<see cref="HelperServer"/>) と、
/// アプリ全体を管理者として実行している場合の同じプロセスでの処理 (仕様 10) の両方がこのクラスを通す。
/// ハンドル番号はこの中の表の番号で、OS のハンドルそのものは外に出さない。
/// </summary>
public sealed class PrivilegedOperations : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, object> _handles = [];
    private readonly HashSet<uint> _locked = [];
    private readonly Dictionary<uint, IReadOnlyList<MemoryRegion>> _regionPages = [];
    private readonly IDeviceAccess _devices;
    private readonly IProcessAccess _processes;
    private readonly Action<string> _log;
    private uint _next = 1;
    private bool _writeBlock;
    private DateTime _idleSince = DateTime.UtcNow;

    public PrivilegedOperations(IDeviceAccess devices, IProcessAccess processes, Action<string>? log = null, string? appVersion = null)
    {
        _devices = devices;
        _processes = processes;
        _log = log ?? (_ => { });
        AppVersion = appVersion ?? string.Empty;
    }

    /// <summary>アプリの版 (<c>Hello</c> で返す)。</summary>
    public string AppVersion { get; }

    /// <summary>開いているハンドルの数。</summary>
    public int OpenHandles
    {
        get
        {
            lock (_lock)
            {
                return _handles.Count;
            }
        }
    }

    /// <summary>開いているハンドルが 0 になった時刻 (補助プロセスの終了の判定。ENG-28 の仕様 6)。開いていれば null。</summary>
    public DateTime? IdleSinceUtc
    {
        get
        {
            lock (_lock)
            {
                return _handles.Count == 0 ? _idleSince : null;
            }
        }
    }

    /// <summary>書き込み保護モード (FOR-01) の通知を受けている。</summary>
    public bool WriteBlock => _writeBlock;

    /// <summary>
    /// 1 つの要求を実行する。戻り値は (状態, 本体)。許可リストにない要求・不正なメッセージは <see cref="HelperProtocolException"/>
    /// (呼び出し側は切断して終了する)。
    /// </summary>
    public (uint Status, byte[] Body) Execute(ushort command, ushort flags, ReadOnlySpan<byte> body)
    {
        if (!HelperProtocol.IsKnown(command))
        {
            throw Reject($"unknown command 0x{command:X4}");
        }

        var cmd = (HelperCommand)command;
        var reader = new BodyReader(body);
        switch (cmd)
        {
            case HelperCommand.Hello:
            {
                uint version = reader.U32();
                string version2 = reader.Text();
                _log($"Hello protocol {version} app {version2}");
                return Ok(new BodyWriter().U32(HelperProtocol.Version).Text(AppVersion).ToArray());
            }

            case HelperCommand.Ping:
                return Ok([]);

            case HelperCommand.EnumDevices:
            {
                reader.End();
                DeviceCatalog catalog = _devices.Enumerate();
                _log($"EnumDevices -> {catalog.Disks.Count} disks, {catalog.Volumes.Count} volumes");
                return Ok(JsonSerializer.SerializeToUtf8Bytes(catalog, DeviceJson.Options));
            }

            case HelperCommand.OpenDevice:
            {
                bool writable = (flags & HelperProtocol.FlagWritable) != 0;
                string path = reader.Text();
                reader.End();
                if (!DevicePath.IsValid(path))
                {
                    throw Reject($"OpenDevice with a path that is not a device: {path}");
                }

                if (writable && _writeBlock)
                {
                    _log($"OpenDevice {path} rw refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                try
                {
                    IDeviceHandle handle = _devices.Open(path, writable);
                    uint id = Add(handle);
                    _log($"OpenDevice {path} {(writable ? "rw" : "r")} -> handle {id}");
                    return Ok(new BodyWriter().U32(id).ToArray());
                }
                catch (DeviceException ex)
                {
                    _log($"OpenDevice {path} {(writable ? "rw" : "r")} -> error {ex.ErrorCode}");
                    return Fail(ex.ErrorCode);
                }
            }

            case HelperCommand.GetGeometry:
            {
                uint id = reader.U32();
                reader.End();
                if (Get<IDeviceHandle>(id) is not { } handle)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                DeviceGeometry g = handle.Geometry;
                return Ok(new BodyWriter().I64(g.Length).U32((uint)g.LogicalSectorSize).U32((uint)g.PhysicalSectorSize).U32((uint)g.Alignment).ToArray());
            }

            case HelperCommand.ReadSectors:
            {
                uint id = reader.U32();
                long offset = reader.I64();
                uint length = reader.U32();
                reader.End();
                if (length > HelperProtocol.MaxTransfer)
                {
                    throw Reject($"ReadSectors longer than the limit ({length})");
                }

                if (Get<IDeviceHandle>(id) is not { } handle)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                byte[] data = new byte[length];
                int error = handle.ReadSectors(offset, data);
                if (error != 0)
                {
                    // 読み込みは数が多いため、失敗だけを記録する (ENG-28 の仕様 9)。
                    _log($"ReadSectors {handle.Path} 0x{offset:X} {length} -> {error}");
                }

                return error == 0 ? Ok(data) : Fail(error);
            }

            case HelperCommand.WriteSectors:
            {
                uint id = reader.U32();
                long offset = reader.I64();
                ReadOnlySpan<byte> data = reader.Rest();
                if (data.Length > HelperProtocol.MaxTransfer)
                {
                    throw Reject($"WriteSectors longer than the limit ({data.Length})");
                }

                if (_writeBlock)
                {
                    _log("WriteSectors refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                if (Get<IDeviceHandle>(id) is not { } handle)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                int error = handle.WriteSectors(offset, data);
                _log($"WriteSectors {handle.Path} 0x{offset:X} {data.Length} -> {error}");
                return error == 0 ? Ok([]) : Fail(error);
            }

            case HelperCommand.LockVolume:
            case HelperCommand.DismountVolume:
            case HelperCommand.UnlockVolume:
            case HelperCommand.Flush:
            {
                uint id = reader.U32();
                reader.End();
                if (_writeBlock && cmd is HelperCommand.LockVolume or HelperCommand.DismountVolume)
                {
                    _log($"{cmd} refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                if (Get<IDeviceHandle>(id) is not { } handle)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                int error = cmd switch
                {
                    HelperCommand.LockVolume => handle.LockVolume(),
                    HelperCommand.DismountVolume => handle.DismountVolume(),
                    HelperCommand.UnlockVolume => handle.UnlockVolume(),
                    _ => handle.Flush(),
                };
                lock (_lock)
                {
                    if (error == 0 && cmd is HelperCommand.LockVolume or HelperCommand.DismountVolume)
                    {
                        _locked.Add(id);
                    }
                    else if (error == 0 && cmd == HelperCommand.UnlockVolume)
                    {
                        _locked.Remove(id);
                    }
                }

                _log($"{cmd} {handle.Path} -> {error}");
                return error == 0 ? Ok([]) : Fail(error);
            }

            case HelperCommand.OpenProcess:
            {
                bool writable = (flags & HelperProtocol.FlagWritable) != 0;
                uint pid = reader.U32();
                reader.End();
                if (writable && _writeBlock)
                {
                    _log($"OpenProcess {pid} rw refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                try
                {
                    IProcessMemory memory = _processes.Open((int)pid, writable);
                    uint id = Add(memory);
                    _log($"OpenProcess {pid} {(writable ? "rw" : "r")} -> handle {id}");
                    return Ok(new BodyWriter().U32(id).I64(memory.AddressLimit).U16((ushort)memory.Architecture).Text(memory.Name).ToArray());
                }
                catch (ProcessAccessException ex)
                {
                    _log($"OpenProcess {pid} -> error {ex.ErrorCode} ({ex.Failure})");
                    return Fail(ex.Failure == ProcessOpenFailure.Protected ? ProtectedProcessStatus : ex.ErrorCode == 0 ? Win32Errors.AccessDenied : ex.ErrorCode);
                }
            }

            case HelperCommand.QueryRegions:
            {
                uint id = reader.U32();
                long start = reader.I64();
                uint max = reader.U32();
                reader.End();
                if (Get<IProcessMemory>(id) is not { } memory)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                IReadOnlyList<MemoryRegion> regions;
                lock (_lock)
                {
                    if (start == 0 || !_regionPages.TryGetValue(id, out regions!))
                    {
                        try
                        {
                            regions = memory.QueryRegions();
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException)
                        {
                            return Fail(memory.HasExited ? Win32Errors.ProcessAborted : Win32Errors.AccessDenied);
                        }

                        _regionPages[id] = regions;
                    }
                }

                var writer = new BodyWriter();
                int first = regions.Count;
                for (int i = 0; i < regions.Count; i++)
                {
                    if (regions[i].BaseAddress >= start)
                    {
                        first = i;
                        break;
                    }
                }

                int count = (int)Math.Min(Math.Min(max, 4096), regions.Count - first);
                writer.U32((uint)count);
                for (int i = first; i < first + count; i++)
                {
                    MemoryRegion r = regions[i];
                    writer.I64(r.BaseAddress).I64(r.Size).U8((byte)r.State).U32(r.Protect).U8((byte)r.Type).I64(r.AllocationBase).Text(r.MappedName);
                }

                writer.I64(first + count < regions.Count ? regions[first + count].BaseAddress : -1);
                return Ok(writer.ToArray());
            }

            case HelperCommand.EnumModules:
            {
                uint id = reader.U32();
                reader.End();
                if (Get<IProcessMemory>(id) is not { } memory)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                IReadOnlyList<ProcessModule> modules = memory.EnumModules();
                var writer = new BodyWriter().U32((uint)modules.Count);
                foreach (ProcessModule m in modules)
                {
                    writer.Text(m.Name).I64(m.BaseAddress).I64(m.Size).Text(m.Path);
                }

                return Ok(writer.ToArray());
            }

            case HelperCommand.ReadMemory:
            {
                uint id = reader.U32();
                long address = reader.I64();
                uint length = reader.U32();
                reader.End();
                if (length > HelperProtocol.MaxTransfer)
                {
                    throw Reject($"ReadMemory longer than the limit ({length})");
                }

                if (Get<IProcessMemory>(id) is not { } memory)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                byte[] data = new byte[length];
                int error = memory.Read(address, data, out int read);
                read = Math.Clamp(read, 0, (int)length);
                return ((uint)error, new BodyWriter().U32((uint)read).Bytes(data.AsSpan(0, read)).ToArray());
            }

            case HelperCommand.WriteMemory:
            {
                uint id = reader.U32();
                long address = reader.I64();
                ReadOnlySpan<byte> data = reader.Rest();
                if (data.Length > HelperProtocol.MaxTransfer)
                {
                    throw Reject($"WriteMemory longer than the limit ({data.Length})");
                }

                if (_writeBlock)
                {
                    _log("WriteMemory refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                if (Get<IProcessMemory>(id) is not { } memory)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                int error = memory.Write(address, data, out int written);
                _log($"WriteMemory PID {memory.Pid} 0x{address:X} {data.Length} -> {error}");
                return ((uint)error, new BodyWriter().U32((uint)Math.Max(0, written)).ToArray());
            }

            case HelperCommand.ProtectMemory:
            {
                uint id = reader.U32();
                long address = reader.I64();
                long size = reader.I64();
                uint protect = reader.U32();
                reader.End();
                if (_writeBlock)
                {
                    _log("ProtectMemory refused (write protection mode)");
                    return Fail(Win32Errors.WriteProtect);
                }

                if (Get<IProcessMemory>(id) is not { } memory)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                int error = memory.Protect(address, size, protect, out uint old);
                _log($"ProtectMemory PID {memory.Pid} 0x{address:X} {size} 0x{protect:X} -> {error}");
                return ((uint)error, new BodyWriter().U32(old).ToArray());
            }

            case HelperCommand.QueryDeviceInfo:
            {
                uint id = reader.U32();
                ushort kind = reader.U16();
                reader.End();
                if (!Enum.IsDefined((DeviceInfoKind)kind))
                {
                    throw Reject($"QueryDeviceInfo with a command that is not allowed (0x{kind:X4})");
                }

                if (Get<IDeviceHandle>(id) is null)
                {
                    return Fail(Win32Errors.InvalidHandle);
                }

                // 読み取り専用のデバイス情報の取得 (FOR-27、FOR-28) はフェーズ 5。許可リストの判定だけを先に置く。
                _log($"QueryDeviceInfo {(DeviceInfoKind)kind} -> not supported");
                return Fail(Win32Errors.NotSupported);
            }

            case HelperCommand.GetUsbWriteProtect:
                reader.End();

                // OS の USB 書き込み保護の取得・切り替え (FOR-02) はフェーズ 5。
                return Fail(Win32Errors.NotSupported);

            case HelperCommand.SetUsbWriteProtect:
            {
                uint value = reader.U32();
                reader.End();
                if (value is not (0 or 1 or HelperProtocol.UsbWriteProtectDelete))
                {
                    throw Reject($"SetUsbWriteProtect with a value that is not allowed ({value})");
                }

                if (_writeBlock)
                {
                    return Fail(Win32Errors.WriteProtect);
                }

                _log($"SetUsbWriteProtect {value} -> not supported");
                return Fail(Win32Errors.NotSupported);
            }

            case HelperCommand.SetWriteBlock:
            {
                byte on = reader.U8();
                reader.End();
                _writeBlock = on != 0;
                _log($"SetWriteBlock {_writeBlock}");
                return Ok([]);
            }

            case HelperCommand.Close:
            {
                uint id = reader.U32();
                reader.End();
                return Remove(id) ? Ok([]) : Fail(Win32Errors.InvalidHandle);
            }

            default:
                throw Reject($"unknown command 0x{command:X4}");
        }
    }

    /// <summary>保護されたプロセスを示す状態 (Win32 のエラーコードと重ならない独自の値)。</summary>
    public const int ProtectedProcessStatus = 0x20000001;

    private HelperProtocolException Reject(string reason)
    {
        _log("Rejected: " + reason);
        return new HelperProtocolException(reason);
    }

    private static (uint, byte[]) Ok(byte[] body) => (0, body);

    private static (uint, byte[]) Fail(int error) => ((uint)(error == 0 ? Win32Errors.InvalidParameter : error), []);

    private uint Add(object handle)
    {
        lock (_lock)
        {
            uint id = _next++;
            _handles[id] = handle;
            return id;
        }
    }

    private T? Get<T>(uint id)
        where T : class
    {
        lock (_lock)
        {
            return _handles.TryGetValue(id, out object? h) ? h as T : null;
        }
    }

    private bool Remove(uint id)
    {
        object? handle;
        bool wasLocked;
        lock (_lock)
        {
            if (!_handles.Remove(id, out handle))
            {
                return false;
            }

            wasLocked = _locked.Remove(id);
            _regionPages.Remove(id);
            if (_handles.Count == 0)
            {
                _idleSince = DateTime.UtcNow;
            }
        }

        if (wasLocked && handle is IDeviceHandle device)
        {
            device.UnlockVolume();
        }

        (handle as IDisposable)?.Dispose();
        _log($"Close handle {id}");
        return true;
    }

    /// <summary>
    /// 開いているハンドルをすべて閉じ、ロックしたボリュームのロックを解除する (UI のプロセスの終了・切断。ENG-28 の仕様 6)。
    /// </summary>
    public void Dispose()
    {
        uint[] ids;
        lock (_lock)
        {
            ids = [.. _handles.Keys];
        }

        foreach (uint id in ids)
        {
            Remove(id);
        }
    }
}

/// <summary>デバイスの一覧の JSON の設定。</summary>
public static class DeviceJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
