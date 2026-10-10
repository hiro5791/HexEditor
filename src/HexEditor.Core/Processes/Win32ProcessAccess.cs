using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HexEditor.Core.Devices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Processes;

/// <summary>
/// Windows のプロセスを直接開く実装 (ENG-32)。UI のプロセス (同じユーザーの昇格していないプロセス)、補助プロセス、管理者として実行中の
/// アプリ (<see cref="EnableDebugPrivilege"/>) から使う。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class Win32ProcessAccess : IProcessAccess
{
    public static Win32ProcessAccess Instance { get; } = new();

    private static readonly Lazy<bool> CurrentElevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>このプロセスが管理者として動いている。</summary>
    public static bool IsCurrentProcessElevated => CurrentElevated.Value;

    // ---- 一覧 ----

    public IReadOnlyList<ProcessEntry> Enumerate()
    {
        string? currentSid = WindowsIdentity.GetCurrent().User?.Value;
        var users = new Dictionary<string, string?>();
        var list = new List<ProcessEntry>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4)
                {
                    // Idle と System はメモリを開けない (保護されたものとして示す)。
                    list.Add(new ProcessEntry { Pid = process.Id, Name = process.Id == 0 ? "Idle" : "System", Access = ProcessAccessLevel.Protected });
                    continue;
                }

                list.Add(Describe(process, currentSid, users));
            }
        }

        return list;
    }

    private static ProcessEntry Describe(Process process, string? currentSid, Dictionary<string, string?> users)
    {
        string name = process.ProcessName + ".exe";
        ProcessArchitecture arch = ProcessArchitecture.Unknown;
        string? user = null;
        string? sid = null;
        long? commit = null;
        string? image = null;
        bool isProtected = false;
        using (SafeProcessHandle query = OpenProcess(ProcessQueryLimitedInformation, false, process.Id))
        {
            if (!query.IsInvalid)
            {
                arch = ArchitectureOf(query);
                isProtected = IsProtected(query);
                (sid, user) = UserOf(query, users);
                commit = CommitOf(query);
                image = ImagePathOf(query);
                if (image is not null)
                {
                    name = Path.GetFileName(image);
                }
            }
        }

        ProcessAccessLevel access;
        if (isProtected)
        {
            access = ProcessAccessLevel.Protected;
        }
        else if (IsCurrentProcessElevated)
        {
            access = ProcessAccessLevel.Direct;
        }
        else
        {
            using SafeProcessHandle probe = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
            access = probe.IsInvalid ? ProcessAccessLevel.NeedsElevation : ProcessAccessLevel.Direct;
        }

        string? title = null;
        try
        {
            title = process.MainWindowTitle is { Length: > 0 } t ? t : null;
        }
        catch (InvalidOperationException)
        {
        }

        return new ProcessEntry
        {
            Pid = process.Id,
            Name = name,
            Architecture = arch,
            User = user,
            WindowTitle = title,
            CommitBytes = commit,
            Access = access,
            IsCurrentUser = sid is not null && sid == currentSid,
            ImagePath = image,
        };
    }

    private static ProcessArchitecture ArchitectureOf(SafeProcessHandle handle)
    {
        if (IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
        {
            ushort machine = processMachine == 0 ? nativeMachine : processMachine;
            ProcessArchitecture arch = machine switch
            {
                0x014C => ProcessArchitecture.X86,
                0x8664 => ProcessArchitecture.X64,
                0xAA64 => ProcessArchitecture.Arm64,
                0x01C4 => ProcessArchitecture.Arm,
                _ => ProcessArchitecture.Unknown,
            };

            // ARM64 の OS の x64 プロセス (エミュレーション) は IsWow64Process2 では区別できない。ARM64EC の判定は GetProcessInformation が要るため省く。
            return arch;
        }

        return ProcessArchitecture.Unknown;
    }

    private static bool IsProtected(SafeProcessHandle handle)
    {
        // PROCESS_PROTECTION_LEVEL_INFORMATION (ProcessProtectionLevelInfo = 7)。PROTECTION_LEVEL_NONE = 0xFFFFFFFE。
        uint level = 0xFFFFFFFE;
        return GetProcessInformation(handle, 7, &level, sizeof(uint)) && level != 0xFFFFFFFE;
    }

    private static (string? Sid, string? Name) UserOf(SafeProcessHandle handle, Dictionary<string, string?> cache)
    {
        if (!OpenProcessToken(handle, TokenQuery, out SafeAccessTokenHandle token))
        {
            return (null, null);
        }

        using (token)
        {
            byte* buffer = stackalloc byte[256];
            if (!GetTokenInformation(token, 1 /* TokenUser */, buffer, 256, out _))
            {
                return (null, null);
            }

            var sid = new SecurityIdentifier(*(IntPtr*)buffer);
            string value = sid.Value;
            if (!cache.TryGetValue(value, out string? name))
            {
                try
                {
                    name = sid.Translate(typeof(NTAccount)).Value;
                }
                catch (IdentityNotMappedException)
                {
                    name = value;
                }
                catch (SystemException)
                {
                    name = value;
                }

                cache[value] = name;
            }

            return (value, name);
        }
    }

    private static long? CommitOf(SafeProcessHandle handle)
    {
        // PROCESS_MEMORY_COUNTERS: cb, PageFaultCount, Peak/WorkingSetSize, QuotaPeak/PagedPool..., PagefileUsage (= コミットサイズ)
        long* counters = stackalloc long[10];
        int size = 4 + 4 + (8 * 8);
        *(int*)counters = size;
        if (!GetProcessMemoryInfo(handle, counters, size))
        {
            return null;
        }

        return *(long*)((byte*)counters + 8 + (6 * 8));
    }

    private static string? ImagePathOf(SafeProcessHandle handle)
    {
        char* buffer = stackalloc char[1024];
        int length = 1024;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
    }

    // ---- 開く ----

    public IProcessMemory Open(int pid, bool writable)
    {
        uint access = ProcessVmRead | ProcessQueryInformation | Synchronize | (writable ? ProcessVmWrite | ProcessVmOperation : 0);
        SafeProcessHandle handle = OpenProcess(access, false, pid);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            ProcessOpenFailure failure = error switch
            {
                Win32Errors.InvalidParameter => ProcessOpenFailure.NotFound,
                Win32Errors.AccessDenied => ProtectedById(pid) ? ProcessOpenFailure.Protected : ProcessOpenFailure.AccessDenied,
                _ => ProcessOpenFailure.Other,
            };
            throw new ProcessAccessException(failure, error, new System.ComponentModel.Win32Exception(error).Message);
        }

        return new Memory(pid, handle, writable);
    }

    private static bool ProtectedById(int pid)
    {
        using SafeProcessHandle query = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        return !query.IsInvalid && IsProtected(query);
    }

    /// <summary>管理者として実行中に <c>SeDebugPrivilege</c> を有効にする (ENG-32 の仕様 4 の 2、ENG-28 の概要の表)。</summary>
    public static bool EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out long luid))
            {
                return false;
            }

            // TOKEN_PRIVILEGES { DWORD Count; LUID_AND_ATTRIBUTES { LUID; DWORD Attributes } }
            byte* privileges = stackalloc byte[16];
            *(int*)privileges = 1;
            *(long*)(privileges + 4) = luid;
            *(int*)(privileges + 12) = 2; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, privileges, 16, null, null) && Marshal.GetLastPInvokeError() == 0;
        }
    }

    private sealed class Memory : IProcessMemory
    {
        private readonly SafeProcessHandle _handle;
        private readonly RegisteredWaitHandle? _wait;
        private readonly ManualResetEvent _exitEvent;
        private volatile bool _exited;

        public Memory(int pid, SafeProcessHandle handle, bool writable)
        {
            Pid = pid;
            _handle = handle;
            Writable = writable;
            Name = ImagePathOf(handle) is { } image ? Path.GetFileName(image) : $"PID {pid}";
            Architecture = ArchitectureOf(handle);
            AddressLimit = ComputeAddressLimit();
            _exitEvent = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false) };
            _wait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) =>
            {
                _exited = true;
                Exited?.Invoke(this, EventArgs.Empty);
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public int Pid { get; }

        public string Name { get; }

        public ProcessArchitecture Architecture { get; }

        public long AddressLimit { get; }

        public bool Writable { get; }

        public bool HasExited => _exited;

        public event EventHandler? Exited;

        private long ComputeAddressLimit()
        {
            bool wow64 = IsWow64Process2(_handle, out ushort processMachine, out _) && processMachine != 0;
            if (!wow64)
            {
                SystemInfo info;
                GetNativeSystemInfo(&info);
                return (long)info.MaximumApplicationAddress + 1;
            }

            return LargeAddressAware() ? 4L * 1024 * 1024 * 1024 : 2L * 1024 * 1024 * 1024;
        }

        /// <summary>32 bit のプロセスの実行ファイルが大きいアドレスに対応しているか (PE の <c>IMAGE_FILE_LARGE_ADDRESS_AWARE</c>)。</summary>
        private bool LargeAddressAware()
        {
            IReadOnlyList<ProcessModule> modules = EnumModules();
            if (modules.Count == 0)
            {
                return true;
            }

            Span<byte> header = stackalloc byte[512];
            if (Read(modules[0].BaseAddress, header, out int read) != 0 || read < 0x40)
            {
                return true;
            }

            int pe = BitConverter.ToInt32(header[0x3C..]);
            if (pe < 0 || pe + 0x18 > read)
            {
                return true;
            }

            ushort characteristics = BitConverter.ToUInt16(header[(pe + 0x16)..]);
            return (characteristics & 0x20) != 0;
        }

        public IReadOnlyList<MemoryRegion> QueryRegions()
        {
            var list = new List<MemoryRegion>();
            long address = 0;
            MemoryBasicInformation info;
            char* name = stackalloc char[512];
            while (address < AddressLimit)
            {
                if (VirtualQueryEx(_handle, (IntPtr)address, &info, sizeof(MemoryBasicInformation)) == 0)
                {
                    if (list.Count == 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        throw new IOException(new System.ComponentModel.Win32Exception(error).Message);
                    }

                    break;
                }

                long size = (long)info.RegionSize;
                if (size <= 0)
                {
                    break;
                }

                RegionState state = info.State switch
                {
                    0x1000 => RegionState.Commit,
                    0x2000 => RegionState.Reserve,
                    _ => RegionState.Free,
                };
                RegionType type = info.Type switch
                {
                    0x1000000 => RegionType.Image,
                    0x40000 => RegionType.Mapped,
                    0x20000 => RegionType.Private,
                    _ => RegionType.None,
                };
                string? mapped = null;
                if (type is RegionType.Mapped or RegionType.Image)
                {
                    int n = GetMappedFileNameW(_handle, (IntPtr)address, name, 512);
                    mapped = n > 0 ? new string(name, 0, n) : null;
                }

                list.Add(new MemoryRegion((long)info.BaseAddress, size, state, state == RegionState.Commit ? info.Protect : 0, type, mapped,
                    (long)info.AllocationBase));
                address = (long)info.BaseAddress + size;
            }

            return list;
        }

        public IReadOnlyList<ProcessModule> EnumModules()
        {
            IntPtr* modules = stackalloc IntPtr[1024];
            if (!EnumProcessModulesEx(_handle, modules, 1024 * IntPtr.Size, out int needed, 3 /* LIST_MODULES_ALL */))
            {
                return [];
            }

            int count = Math.Min(1024, needed / IntPtr.Size);
            var list = new List<ProcessModule>(count);
            char* text = stackalloc char[1024];
            IntPtr* info = stackalloc IntPtr[3];
            for (int i = 0; i < count; i++)
            {
                if (!GetModuleInformation(_handle, modules[i], info, 3 * IntPtr.Size))
                {
                    continue;
                }

                int n = GetModuleFileNameExW(_handle, modules[i], text, 1024);
                string path = n > 0 ? new string(text, 0, n) : string.Empty;
                list.Add(new ProcessModule(Path.GetFileName(path), (long)info[0], (uint)(long)info[1], path));
            }

            return list;
        }

        public int Read(long address, Span<byte> buffer, out int read)
        {
            fixed (byte* p = buffer)
            {
                bool ok = ReadProcessMemory(_handle, (IntPtr)address, p, (IntPtr)buffer.Length, out IntPtr done);
                read = (int)done;
                return ok ? 0 : Error();
            }
        }

        public int Write(long address, ReadOnlySpan<byte> data, out int written)
        {
            fixed (byte* p = data)
            {
                bool ok = WriteProcessMemory(_handle, (IntPtr)address, p, (IntPtr)data.Length, out IntPtr done);
                written = (int)done;
                return ok ? 0 : Error();
            }
        }

        public int Protect(long address, long size, uint protect, out uint oldProtect) =>
            VirtualProtectEx(_handle, (IntPtr)address, (IntPtr)size, protect, out oldProtect) ? 0 : Error();

        private int Error()
        {
            int error = Marshal.GetLastPInvokeError();
            return _exited ? Win32Errors.ProcessAborted : error == 0 ? Win32Errors.PartialCopy : error;
        }

        public void Dispose()
        {
            _wait?.Unregister(null);
            _exitEvent.Dispose();
            _handle.Dispose();
        }
    }

    // ---- Win32 ----

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInfo
    {
        public ushort ProcessorArchitecture;
        public ushort Reserved;
        public uint PageSize;
        public IntPtr MinimumApplicationAddress;
        public IntPtr MaximumApplicationAddress;
        public IntPtr ActiveProcessorMask;
        public uint NumberOfProcessors;
        public uint ProcessorType;
        public uint AllocationGranularity;
        public ushort ProcessorLevel;
        public ushort ProcessorRevision;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, void* information, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, char* name, ref int size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, void* counters, int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, void* information, int length, out int returned);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, void* newState, int length, void* previous, int* returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(SafeProcessHandle process, IntPtr address, MemoryBasicInformation* info, int length);

    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(SystemInfo* info);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMappedFileNameW(SafeProcessHandle process, IntPtr address, char* name, int size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(SafeProcessHandle process, IntPtr* modules, int size, out int needed, int filter);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetModuleInformation(SafeProcessHandle process, IntPtr module, void* info, int size);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileNameExW(SafeProcessHandle process, IntPtr module, char* name, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte* buffer, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(SafeProcessHandle process, IntPtr address, byte* buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(SafeProcessHandle process, IntPtr address, IntPtr size, uint protect, out uint oldProtect);
}
