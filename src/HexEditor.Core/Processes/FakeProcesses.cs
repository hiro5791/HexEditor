#if HEX_TEST_HOOKS
using System.Text.Json;
using HexEditor.Core.Devices;

namespace HexEditor.Core.Processes;

// テスト用のビルドだけに入れる、偽のプロセス (テスト方針 7.2)。本物のプロセスに触れずに、プロセスを開く・領域の一覧・書き込み・
// 保護属性の変更・終了の振る舞いを再現する。製品版には含めない。

/// <summary>偽の領域の定義。</summary>
public sealed record FakeRegionSpec
{
    public long Base { get; init; }

    public long Size { get; init; }

    public RegionState State { get; init; } = RegionState.Commit;

    public uint Protect { get; init; } = PageProtection.ReadWrite;

    public RegionType Type { get; init; } = RegionType.Private;

    public string? Name { get; init; }

    /// <summary>初期内容 (16 進文字列。先頭から)。足りない分は 0。</summary>
    public string? Data { get; init; }
}

/// <summary>偽のプロセスの定義。</summary>
public sealed record FakeProcessSpec
{
    public int Pid { get; init; }

    public string Name { get; init; } = "fake.exe";

    public ProcessArchitecture Architecture { get; init; } = ProcessArchitecture.X64;

    public string? User { get; init; } = "tester";

    public string? Title { get; init; }

    public ProcessAccessLevel Access { get; init; } = ProcessAccessLevel.Direct;

    /// <summary>開けるが、昇格していなければメモリを読めない (読み込みが権限不足になる。ANA-09 の「エラー」の再現)。</summary>
    public bool ReadNeedsElevation { get; init; }

    public bool CurrentUser { get; init; } = true;

    public long AddressLimit { get; init; } = 0x7FFF_FFFF_0000;

    public List<FakeRegionSpec> Regions { get; init; } = [];

    public List<ProcessModule> Modules { get; init; } = [];
}

/// <summary>偽のプロセスの一覧 (テスト用の設定ファイルの形)。</summary>
public sealed record FakeProcessListSpec
{
    public List<FakeProcessSpec> Processes { get; init; } = [];

    /// <summary>このプロセスは管理者として動いている扱い。</summary>
    public bool Elevated { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static FakeProcessListSpec Parse(string json) => JsonSerializer.Deserialize<FakeProcessListSpec>(json, Options) ?? new FakeProcessListSpec();

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>偽のプロセス 1 つ (メモリの内容・保護属性を持つ)。</summary>
public sealed class FakeProcess
{
    private readonly object _lock = new();
    private readonly Dictionary<long, byte[]> _pages = [];
    private readonly List<MemoryRegion> _regions;

    public FakeProcess(FakeProcessSpec spec)
    {
        Spec = spec;
        _regions = [.. spec.Regions.Select(r => new MemoryRegion(r.Base, r.Size, r.State, r.State == RegionState.Commit ? r.Protect : 0, r.Type, r.Name, r.Base))];
        foreach (FakeRegionSpec r in spec.Regions.Where(r => r.Data is not null))
        {
            WriteRaw(r.Base, Convert.FromHexString(r.Data!));
        }
    }

    public FakeProcessSpec Spec { get; }

    public bool Exited { get; private set; }

    /// <summary>ガードページを読もうとした回数 (対象のプロセスで例外が起きる操作。0 でなければならない)。</summary>
    public int GuardViolations { get; private set; }

    /// <summary>保護属性の変更の記録 (アドレス, 新しい値)。</summary>
    public List<(long Address, uint Protect)> ProtectCalls { get; } = [];

    public event EventHandler? ExitedEvent;

    public IReadOnlyList<MemoryRegion> Regions
    {
        get
        {
            lock (_lock)
            {
                return [.. _regions.OrderBy(r => r.BaseAddress)];
            }
        }
    }

    public void Kill()
    {
        Exited = true;
        ExitedEvent?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>テストから領域を置き換える (解放・確保。ANA-09)。</summary>
    public void SetRegions(IEnumerable<MemoryRegion> regions)
    {
        lock (_lock)
        {
            _regions.Clear();
            _regions.AddRange(regions);
        }
    }

    /// <summary>テストから内容を書く (対象のプロセス自身の書き込み)。保護属性を見ない。</summary>
    public void WriteRaw(long address, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
            {
                long a = address + i;
                long page = a / 4096;
                if (!_pages.TryGetValue(page, out byte[]? bytes))
                {
                    bytes = new byte[4096];
                    _pages[page] = bytes;
                }

                bytes[a % 4096] = data[i];
            }
        }
    }

    public byte[] ReadRaw(long address, int length)
    {
        byte[] result = new byte[length];
        lock (_lock)
        {
            for (int i = 0; i < length; i++)
            {
                long a = address + i;
                if (_pages.TryGetValue(a / 4096, out byte[]? bytes))
                {
                    result[i] = bytes[a % 4096];
                }
            }
        }

        return result;
    }

    private MemoryRegion? RegionAt(long address) => _regions.FirstOrDefault(r => address >= r.BaseAddress && address < r.End);

    internal int Read(long address, Span<byte> buffer, out int read)
    {
        read = 0;
        if (Exited)
        {
            return Win32Errors.ProcessAborted;
        }

        lock (_lock)
        {
            while (read < buffer.Length)
            {
                long a = address + read;
                MemoryRegion? region = RegionAt(a);
                if (region is null || region.State != RegionState.Commit)
                {
                    return Win32Errors.PartialCopy;
                }

                if (region.IsGuard)
                {
                    GuardViolations++;
                    return Win32Errors.PartialCopy;
                }

                if (!region.IsReadable)
                {
                    return Win32Errors.PartialCopy;
                }

                int n = (int)Math.Min(buffer.Length - read, region.End - a);
                ReadRaw(a, n).CopyTo(buffer.Slice(read, n));
                read += n;
            }
        }

        return 0;
    }

    internal int Write(long address, ReadOnlySpan<byte> data, out int written)
    {
        written = 0;
        if (Exited)
        {
            return Win32Errors.ProcessAborted;
        }

        lock (_lock)
        {
            // すべて書ける場合だけ書く (WriteProcessMemory はページの途中で止まることがあるが、偽のプロセスでは単純にする)。
            for (long a = address; a < address + data.Length;)
            {
                MemoryRegion? region = RegionAt(a);
                if (region is null || !region.IsWritable)
                {
                    return Win32Errors.NoAccess;
                }

                a = region.End;
            }

            WriteRaw(address, data);
            written = data.Length;
        }

        return 0;
    }

    internal int Protect(long address, long size, uint protect, out uint old)
    {
        old = 0;
        if (Exited)
        {
            return Win32Errors.ProcessAborted;
        }

        lock (_lock)
        {
            MemoryRegion? region = RegionAt(address);
            if (region is null || region.State != RegionState.Commit)
            {
                return Win32Errors.InvalidAddress;
            }

            old = region.Protect;
            ProtectCalls.Add((address, protect));

            // 領域全体の保護属性を変える (ページ単位に分けない単純な再現)。
            int index = _regions.IndexOf(region);
            _regions[index] = region with { Protect = protect };
        }

        return 0;
    }
}

/// <summary>偽のプロセスの一覧と開く処理 (<see cref="IProcessAccess"/>)。</summary>
public sealed class FakeProcessAccess : IProcessAccess
{
    private readonly List<FakeProcess> _processes;

    public FakeProcessAccess(FakeProcessListSpec spec, bool? elevated = null)
    {
        _processes = [.. spec.Processes.Select(p => new FakeProcess(p))];
        Elevated = elevated ?? spec.Elevated;
    }

    public bool Elevated { get; set; }

    public IReadOnlyList<FakeProcess> Processes => _processes;

    public FakeProcess Process(int pid) => _processes.Single(p => p.Spec.Pid == pid);

    /// <summary>開いた回数 (経路の確認)。</summary>
    public int OpenCount { get; private set; }

    public IReadOnlyList<ProcessEntry> Enumerate() =>
    [
        .. _processes.Where(p => !p.Exited).Select(p => new ProcessEntry
        {
            Pid = p.Spec.Pid,
            Name = p.Spec.Name,
            Architecture = p.Spec.Architecture,
            User = p.Spec.User,
            WindowTitle = p.Spec.Title,
            CommitBytes = p.Spec.Regions.Where(r => r.State == RegionState.Commit).Sum(r => r.Size),
            Access = Elevated && p.Spec.Access == ProcessAccessLevel.NeedsElevation ? ProcessAccessLevel.Direct : p.Spec.Access,
            IsCurrentUser = p.Spec.CurrentUser,
        }),
    ];

    public IProcessMemory Open(int pid, bool writable)
    {
        FakeProcess? process = _processes.FirstOrDefault(p => p.Spec.Pid == pid && !p.Exited);
        if (process is null)
        {
            throw new ProcessAccessException(ProcessOpenFailure.NotFound, 87, "The process was not found.");
        }

        if (process.Spec.Access == ProcessAccessLevel.Protected)
        {
            throw new ProcessAccessException(ProcessOpenFailure.Protected, Win32Errors.AccessDenied, "The process is protected.");
        }

        if (process.Spec.Access == ProcessAccessLevel.NeedsElevation && !Elevated)
        {
            throw new ProcessAccessException(ProcessOpenFailure.AccessDenied, Win32Errors.AccessDenied, "Access denied.");
        }

        OpenCount++;
        return new Memory(process, writable, Elevated);
    }

    private sealed class Memory : IProcessMemory
    {
        private readonly FakeProcess _process;
        private readonly bool _elevated;

        public Memory(FakeProcess process, bool writable, bool elevated)
        {
            _process = process;
            _elevated = elevated;
            Writable = writable;
            process.ExitedEvent += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        }

        public int Pid => _process.Spec.Pid;

        public string Name => _process.Spec.Name;

        public ProcessArchitecture Architecture => _process.Spec.Architecture;

        public long AddressLimit => _process.Spec.AddressLimit;

        public bool Writable { get; }

        public bool HasExited => _process.Exited;

        public event EventHandler? Exited;

        public IReadOnlyList<MemoryRegion> QueryRegions() =>
            _process.Exited ? throw new InvalidOperationException("The process has exited.") : _process.Regions;

        public IReadOnlyList<ProcessModule> EnumModules() => _process.Spec.Modules;

        public int Read(long address, Span<byte> buffer, out int read)
        {
            if (_process.Spec.ReadNeedsElevation && !_elevated)
            {
                read = 0;
                return Win32Errors.AccessDenied;
            }

            return _process.Read(address, buffer, out read);
        }

        public int Write(long address, ReadOnlySpan<byte> data, out int written)
        {
            written = 0;
            return Writable ? _process.Write(address, data, out written) : Win32Errors.AccessDenied;
        }

        public int Protect(long address, long size, uint protect, out uint oldProtect)
        {
            oldProtect = 0;
            return Writable ? _process.Protect(address, size, protect, out oldProtect) : Win32Errors.AccessDenied;
        }

        public void Dispose()
        {
        }
    }
}
#endif
