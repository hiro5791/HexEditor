using HexEditor.Core.Devices;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Processes;

/// <summary>プロセスを開くときの情報 (ENG-32 の仕様 2・6・8)。</summary>
public sealed record ProcessOpenInfo
{
    /// <summary>タブに出す名前 (「notepad.exe (PID 1234)」、モジュールなら「… - kernel32.dll」)。</summary>
    public required string DisplayName { get; init; }

    /// <summary>オフセット 0 に対応する仮想アドレス (アドレス空間全体なら 0)。</summary>
    public long RangeStart { get; init; }

    /// <summary>長さ。null ならアドレス空間の上限まで。</summary>
    public long? RangeLength { get; init; }

    /// <summary>開いたモジュールの名前 (モジュールを選んで開いた場合)。</summary>
    public string? ModuleName { get; init; }

    public DeviceRoute Route { get; init; }
}

/// <summary>
/// プロセスメモリのデータソース (ENG-32)。オフセット + ベースアドレス = 仮想アドレス。長さ固定 (ENG-07)、
/// <see cref="SourceCapabilities.IsVolatile"/>、<see cref="SourceCapabilities.HasGaps"/>。読めない範囲 (空き・予約・アクセス不可・ガード) は
/// 領域マップ (ENG-33 の仕様 7) で示し、読み込まない。ガードページは読まない (ENG-32 の仕様 10)。
/// </summary>
public sealed class ProcessMemoryByteSource : ByteSourceBase, IRegionMapSource
{
    /// <summary>1 回の読み書きの最大 (補助プロセスの 1 要求と同じ)。</summary>
    public const int MaxTransfer = 1024 * 1024;

    /// <summary>ページの大きさ (読めなかった場合に分ける単位)。</summary>
    public const int PageSize = 4096;

    private readonly object _lock = new();
    private IProcessMemory _memory;
    private IProcessAccess _access;
    private volatile State _state = State.Empty;
    private volatile bool _exited;

    private sealed record State(IReadOnlyList<MemoryRegion> Raw, IReadOnlyList<SourceRegion> Map, IReadOnlyList<ProcessModule> Modules)
    {
        public static readonly State Empty = new([], [], []);
    }

    public ProcessMemoryByteSource(IProcessMemory memory, IProcessAccess access, ProcessOpenInfo info)
    {
        _memory = memory;
        _access = access;
        Info = info;
        long limit = memory.AddressLimit;
        if (info.RangeStart < 0 || info.RangeStart >= limit)
        {
            throw new ArgumentOutOfRangeException(nameof(info), "The start address is outside the address space.");
        }

        Length = Math.Min(info.RangeLength ?? limit - info.RangeStart, limit - info.RangeStart);
        memory.Exited += OnExited;
        RefreshRegions();
    }

    public ProcessOpenInfo Info { get; }

    public IProcessMemory Memory => _memory;

    /// <summary>開いた実装 (読み書きで開き直す)。</summary>
    public IProcessAccess Access => _access;

    public int Pid => _memory.Pid;

    public string ProcessName => _memory.Name;

    public override string DisplayName => Info.DisplayName;

    public override string Identity => $"process:{_memory.Pid}:{Info.RangeStart:X}:{Length:X}";

    public override long Length { get; }

    public override long BaseAddress => Info.RangeStart;

    public override SourceCapabilities Capabilities =>
        SourceCapabilities.IsVolatile | SourceCapabilities.HasGaps | (_memory.Writable && !_exited ? SourceCapabilities.CanWrite : 0);

    public bool HasExited => _exited;

    /// <summary>領域の一覧 (仮想アドレス。範囲を指定して開いた場合も全体)。</summary>
    public IReadOnlyList<MemoryRegion> MemoryRegions => _state.Raw;

    public IReadOnlyList<ProcessModule> Modules => _state.Modules;

    /// <summary>領域マップ (オフセット)。</summary>
    public IReadOnlyList<SourceRegion> Regions => _state.Map;

    public event EventHandler? RegionsChanged;

    /// <summary>領域とモジュールの一覧を取り直す (ENG-33 の仕様 6)。取得できない場合は例外 (パネルに理由を出す)。</summary>
    public void RefreshRegions()
    {
        IReadOnlyList<MemoryRegion> raw = _memory.QueryRegions();
        IReadOnlyList<ProcessModule> modules;
        try
        {
            modules = _memory.EnumModules();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            modules = [];
        }

        _state = new State(raw, BuildMap(raw, modules), modules);
        RegionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<SourceRegion> BuildMap(IReadOnlyList<MemoryRegion> raw, IReadOnlyList<ProcessModule> modules)
    {
        var map = new List<SourceRegion>();
        long start = Info.RangeStart, end = Info.RangeStart + Length;
        long cursor = start;
        foreach (MemoryRegion region in raw.OrderBy(r => r.BaseAddress))
        {
            long from = Math.Max(region.BaseAddress, cursor);
            long to = Math.Min(region.End, end);
            if (to <= from)
            {
                continue;
            }

            if (from > cursor)
            {
                map.Add(new SourceRegion(cursor - start, from - cursor, RegionAccess.Unallocated));
            }

            RegionAccess access = region.State != RegionState.Commit ? RegionAccess.Unallocated
                : region.IsReadable ? RegionAccess.Readable : RegionAccess.NoAccess;
            map.Add(new SourceRegion(from - start, to - from, access, LabelOf(region, modules)));
            cursor = to;
        }

        if (cursor < end)
        {
            map.Add(new SourceRegion(cursor - start, end - cursor, RegionAccess.Unallocated));
        }

        return map;
    }

    private static string? LabelOf(MemoryRegion region, IReadOnlyList<ProcessModule> modules) =>
        modules.FirstOrDefault(m => region.BaseAddress >= m.BaseAddress && region.BaseAddress < m.End)?.Name
            ?? (region.MappedName is { } mapped ? Path.GetFileName(mapped) : null);

    /// <summary>
    /// アドレスの説明 (ENG-33 の仕様 4): <c>kernel32.dll+0x1A2B0 (RX)</c>。モジュールの外は <c>private 0x…</c> / <c>mapped 0x…</c> + 領域内の位置。
    /// </summary>
    public string? DescribeAddress(long address)
    {
        State state = _state;
        MemoryRegion? region = FindRegion(state.Raw, address);
        ProcessModule? module = state.Modules.FirstOrDefault(m => address >= m.BaseAddress && address < m.End);
        string protect = region is null ? string.Empty : $" ({PageProtection.ShortText(region.Protect)})";
        if (module is not null)
        {
            return $"{module.Name}+0x{address - module.BaseAddress:X}{protect}";
        }

        if (region is null || region.State == RegionState.Free)
        {
            return null;
        }

        string kind = region.Type switch
        {
            RegionType.Mapped => region.MappedName is { } name ? Path.GetFileName(name) : "mapped",
            RegionType.Image => "image",
            _ => "private",
        };
        long baseAddress = region.AllocationBase != 0 ? region.AllocationBase : region.BaseAddress;
        return $"{kind} 0x{baseAddress:X}+0x{address - baseAddress:X}{protect}";
    }

    /// <summary>仮想アドレスを含む領域。</summary>
    public static MemoryRegion? FindRegion(IReadOnlyList<MemoryRegion> regions, long address)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            MemoryRegion r = regions[mid];
            if (address < r.BaseAddress)
            {
                hi = mid - 1;
            }
            else if (address >= r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return r;
            }
        }

        return null;
    }

    public MemoryRegion? RegionAtOffset(long offset) => FindRegion(_state.Raw, Info.RangeStart + offset);

    /// <summary>[offset, offset + length) がすべてコミット済みの領域にある (編集できる。ENG-34 の仕様 7)。</summary>
    public bool IsAllocated(long offset, long length)
    {
        IReadOnlyList<SourceRegion> map = _state.Map;
        long end = offset + Math.Max(length, 1);
        for (long at = offset; at < end;)
        {
            SourceRegion? r = RegionMaps.At(map, at);
            if (r is null || r.Access == RegionAccess.Unallocated)
            {
                return false;
            }

            at = r.End;
        }

        return true;
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        Span<byte> target = buffer[..length];
        if (_exited)
        {
            target.Clear();
            return new ReadResult(length, [new UnreadableRange(offset, length, UnreadableReason.Disconnected)]);
        }

        IReadOnlyList<SourceRegion> map = _state.Map;
        List<UnreadableRange>? bad = null;
        long end = offset + length;
        for (long at = offset; at < end;)
        {
            SourceRegion? region = RegionMaps.At(map, at);
            long regionEnd = region is null ? end : Math.Min(region.End, end);
            int n = (int)(regionEnd - at);
            Span<byte> dest = target.Slice((int)(at - offset), n);
            if (region is null || region.Access != RegionAccess.Readable)
            {
                dest.Clear();
                Add(ref bad, new UnreadableRange(at, n,
                    region?.Access == RegionAccess.NoAccess ? UnreadableReason.AccessDenied : UnreadableReason.Unallocated));
            }
            else
            {
                ReadReadable(at, dest, ref bad);
            }

            at = regionEnd;
        }

        return new ReadResult(length, bad);
    }

    private void ReadReadable(long offset, Span<byte> dest, ref List<UnreadableRange>? bad)
    {
        long address = Info.RangeStart + offset;
        for (int pos = 0; pos < dest.Length;)
        {
            int n = Math.Min(MaxTransfer, dest.Length - pos);
            int error = _memory.Read(address + pos, dest.Slice(pos, n), out int read);
            if (error == 0 && read == n)
            {
                pos += n;
                continue;
            }

            read = Math.Clamp(read, 0, n);
            pos += read;

            // 読めなかったページを読めない範囲にし、次のページから続ける (ページが解放されたなど。ENG-32 の「エラー」)。
            long failAt = address + pos;
            int skip = (int)Math.Min(dest.Length - pos, PageSize - (failAt % PageSize));
            dest.Slice(pos, skip).Clear();
            if (Win32Errors.IsDisconnect(error) || _memory.HasExited)
            {
                MarkExited();
                dest[pos..].Clear();
                Add(ref bad, new UnreadableRange(offset + pos, dest.Length - pos, UnreadableReason.Disconnected, error));
                return;
            }

            Add(ref bad, new UnreadableRange(offset + pos, skip, UnreadableReason.IoError, error == 0 ? Win32Errors.PartialCopy : error));
            pos += skip;
        }
    }

    private static void Add(ref List<UnreadableRange>? list, UnreadableRange range)
    {
        list ??= [];
        if (list.Count > 0 && list[^1].End == range.Offset && list[^1].Reason == range.Reason && list[^1].ErrorCode == range.ErrorCode)
        {
            list[^1] = list[^1] with { Length = list[^1].Length + range.Length };
        }
        else
        {
            list.Add(range);
        }
    }

    /// <summary>
    /// 書く (保存 ENG-34 だけが呼ぶ。即時書き込みモードを含む)。<paramref name="written"/> は書けたバイト数。失敗は Win32 のエラーコード。
    /// </summary>
    public int TryWrite(long offset, ReadOnlySpan<byte> data, out int written)
    {
        written = 0;
        if (_exited)
        {
            return Win32Errors.ProcessAborted;
        }

        long address = Info.RangeStart + offset;
        for (int pos = 0; pos < data.Length;)
        {
            int n = Math.Min(MaxTransfer, data.Length - pos);
            int error = _memory.Write(address + pos, data.Slice(pos, n), out int done);
            written += Math.Max(0, done);
            if (error != 0 || done != n)
            {
                if (_memory.HasExited)
                {
                    MarkExited();
                    return Win32Errors.ProcessAborted;
                }

                return error == 0 ? Win32Errors.PartialCopy : error;
            }

            pos += n;
        }

        return 0;
    }

    public override void Write(long offset, ReadOnlySpan<byte> data)
    {
        int error = TryWrite(offset, data, out _);
        if (error != 0)
        {
            throw DeviceException.FromError(error, $"0x{Info.RangeStart + offset:X}");
        }
    }

    private void OnExited(object? sender, EventArgs e) => MarkExited();

    /// <summary>対象のプロセスが終了した: 「切断」にする (ENG-32 の仕様 9)。</summary>
    public void MarkExited()
    {
        lock (_lock)
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
        }

        OnChanged(SourceChangeKind.Disconnected);
    }

    /// <summary>読み書きのアクセス権で開き直したもの・補助プロセス経由で開き直したものに付け替える (ENG-14 の仕様 3)。</summary>
    public void ReplaceMemory(IProcessMemory memory, IProcessAccess access)
    {
        if (memory.Pid != _memory.Pid)
        {
            memory.Dispose();
            throw new ArgumentException("A different process.", nameof(memory));
        }

        IProcessMemory old;
        lock (_lock)
        {
            old = _memory;
            old.Exited -= OnExited;
            _memory = memory;
            _access = access;
            memory.Exited += OnExited;
        }

        if (!ReferenceEquals(old, memory))
        {
            old.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memory.Exited -= OnExited;
            _memory.Dispose();
        }
    }
}
