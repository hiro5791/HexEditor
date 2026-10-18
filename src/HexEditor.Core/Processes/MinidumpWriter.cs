using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Processes;

/// <summary>
/// プロセスメモリを Windows のミニダンプ形式 (全メモリを含む。<c>MiniDumpWithFullMemory</c> 相当) で保存する (ENG-35 の仕様 6)。
/// デバッガ (WinDbg、Visual Studio) で開くためのもの。<c>dbghelp</c> の <c>MiniDumpWriteDump</c> を使わず自前で書く (補助プロセス経由・
/// 偽のプロセスでも同じ経路で書け、進捗の表示・キャンセル・空き容量の確認をスナップショットと同じにするため)。
/// 書くストリーム: スレッドの一覧、モジュールの一覧、メモリの一覧 (64 bit)、メモリの情報の一覧、システムの情報、その他の情報 (PID)。
/// 読めなかったページはメモリの一覧から除く (範囲を分ける)。
/// </summary>
public static class MinidumpWriter
{
    /// <summary>ミニダンプの拡張子。</summary>
    public const string Extension = ".dmp";

    private const uint Signature = 0x504D444D; // "MDMP"
    private const uint Version = 0xA793; // MINIDUMP_VERSION
    private const ulong FlagsFullMemory = 0x2 | 0x800; // MiniDumpWithFullMemory | MiniDumpWithFullMemoryInfo

    private const uint ThreadListStream = 3;
    private const uint ModuleListStream = 4;
    private const uint SystemInfoStream = 7;
    private const uint Memory64ListStream = 9;
    private const uint MiscInfoStream = 15;
    private const uint MemoryInfoListStream = 16;

    private const int ModuleSize = 108;
    private const int MemoryInfoSize = 48;
    private const int DescriptorSize = 16;

    /// <summary>
    /// ミニダンプを保存する。同じフォルダの一時ファイルに書き、完了したら名前を変えて確定する (キャンセル・失敗・プロセスの終了では一時ファイルを
    /// 削除する)。<see cref="ProcessCaptureOptions.Scope"/> は使わず、読めるすべての領域を含める (「全メモリを含む」)。
    /// </summary>
    public static ProcessCaptureSummary Write(ProcessMemoryByteSource source, string path, ProcessCaptureOptions options,
        LongRunningOperation? operation = null)
    {
        string temp = ProcessCapture.TempPathFor(path);
        try
        {
            ProcessCaptureSummary summary = WriteCore(source, temp, options, operation);
            ProcessCapture.Commit(temp, path);
            return summary;
        }
        catch
        {
            ProcessCapture.TryDelete(temp);
            throw;
        }
    }

    private static ProcessCaptureSummary WriteCore(ProcessMemoryByteSource source, string path, ProcessCaptureOptions options,
        LongRunningOperation? operation)
    {
        using ProcessCapture.PauseScope pause = ProcessCapture.Pause(source.Memory, options.PauseTarget);
        if (options.RefreshRegions)
        {
            try
            {
                source.RefreshRegions();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        IProcessMemory memory = source.Memory;
        if (source.HasExited || memory.HasExited)
        {
            throw new ProcessExitedDuringCaptureException();
        }

        IReadOnlyList<MemoryRegion> all = [.. source.MemoryRegions.OrderBy(r => r.BaseAddress)];
        IReadOnlyList<MemoryRegion> regions = ProcessCapture.SelectRegions(all, options with { Scope = SnapshotScope.AllReadable });
        IReadOnlyList<ProcessModule> modules = source.Modules;
        long total = regions.Sum(r => r.Size);

        // 読めなかったページで範囲を分けるため、記述子の数には余裕を持たせる (足りなくなったら分けずに 0 で埋める)。
        int descriptorCapacity = (regions.Count * 2) + 1024;
        byte[] head = BuildHead(source, memory, all, modules, descriptorCapacity, out int memoryListOffset);
        long dataStart = AlignUp(head.Length, 16);
        ProcessCapture.CheckSpace(path, dataStart + total, options.Volumes);
        operation?.SetTotal(total);

        var descriptors = new List<(long Start, long Size)>();
        var gaps = new List<SnapshotGap>();
        byte[] buffer = new byte[ProcessCapture.ChunkSize];
        long done = 0;
        bool denied = false;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(head);
            stream.SetLength(dataStart);
            stream.Position = dataStart;
            foreach (MemoryRegion region in regions)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                for (long pos = 0; pos < region.Size; pos += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, region.Size - pos);
                    long address = region.BaseAddress + pos;
                    var chunkGaps = new List<SnapshotGap>();
                    ProcessCapture.ReadChunk(memory, address, buffer.AsSpan(0, n), chunkGaps, ref denied);
                    WriteReadableParts(stream, buffer.AsSpan(0, n), address, chunkGaps, descriptors, descriptorCapacity);
                    foreach (SnapshotGap g in chunkGaps)
                    {
                        if (gaps.Count > 0 && gaps[^1].BaseAddress + gaps[^1].Size == g.BaseAddress)
                        {
                            gaps[^1] = gaps[^1] with { Size = gaps[^1].Size + g.Size };
                        }
                        else
                        {
                            gaps.Add(g);
                        }
                    }

                    done += n;
                    operation?.Report(done);
                }
            }

            if (descriptors.Count == 0 && denied)
            {
                throw new ProcessAccessException(ProcessOpenFailure.AccessDenied, Devices.Win32Errors.AccessDenied, "Access denied.");
            }

            // メモリの一覧 (64 bit) の見出しと記述子を、空けておいた位置に書く。
            byte[] list = new byte[16 + (descriptors.Count * DescriptorSize)];
            BinaryPrimitives.WriteUInt64LittleEndian(list, (ulong)descriptors.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(list.AsSpan(8), (ulong)dataStart);
            for (int i = 0; i < descriptors.Count; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(list.AsSpan(16 + (i * DescriptorSize)), (ulong)descriptors[i].Start);
                BinaryPrimitives.WriteUInt64LittleEndian(list.AsSpan(24 + (i * DescriptorSize)), (ulong)descriptors[i].Size);
            }

            stream.Position = memoryListOffset;
            stream.Write(list);

            // ディレクトリのメモリの一覧の大きさを、実際の記述子の数に合わせる (位置は 3 番目のエントリ)。
            stream.Position = 32 + (2 * 12) + 4;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)list.Length);
            stream.Write(size);
        }

        (int unreadableRegions, long unreadableBytes) = ProcessCapture.Summarize(regions, gaps);
        return new ProcessCaptureSummary
        {
            PauseFailed = pause.Failed,
            UnreadableRegions = unreadableRegions,
            UnreadableBytes = unreadableBytes,
            Regions = regions.Count,
            CapturedBytes = total - unreadableBytes,
        };
    }

    /// <summary>読めた部分だけをデータに書き、記述子を加える (直前の記述子と続く場合は伸ばす)。</summary>
    private static void WriteReadableParts(Stream stream, ReadOnlySpan<byte> chunk, long address, List<SnapshotGap> gaps,
        List<(long Start, long Size)> descriptors, int capacity)
    {
        long cursor = address;
        long end = address + chunk.Length;
        foreach ((long gapStart, long gapSize) in gaps.OrderBy(g => g.BaseAddress))
        {
            // 記述子が足りなくなったら、読めなかったページも 0 として含める (範囲を分けない)。
            if (descriptors.Count >= capacity - 1)
            {
                break;
            }

            Emit(stream, chunk, address, cursor, gapStart, descriptors);
            cursor = gapStart + gapSize;
        }

        Emit(stream, chunk, address, cursor, end, descriptors);
    }

    private static void Emit(Stream stream, ReadOnlySpan<byte> chunk, long address, long from, long to, List<(long Start, long Size)> descriptors)
    {
        if (to <= from)
        {
            return;
        }

        stream.Write(chunk.Slice((int)(from - address), (int)(to - from)));
        if (descriptors.Count > 0 && descriptors[^1].Start + descriptors[^1].Size == from)
        {
            descriptors[^1] = (descriptors[^1].Start, descriptors[^1].Size + (to - from));
        }
        else
        {
            descriptors.Add((from, to - from));
        }
    }

    /// <summary>見出し・ディレクトリ・小さいストリーム (メモリの一覧のデータ以外) を組み立てる。</summary>
    private static byte[] BuildHead(ProcessMemoryByteSource source, IProcessMemory memory, IReadOnlyList<MemoryRegion> all,
        IReadOnlyList<ProcessModule> modules, int descriptorCapacity, out int memoryListOffset)
    {
        var w = new BlobWriter();
        const int streamCount = 6;

        // MINIDUMP_HEADER
        w.U32(Signature);
        w.U32(Version);
        w.U32(streamCount);
        w.U32(32); // StreamDirectoryRva
        w.U32(0); // CheckSum
        w.U32((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        w.U64(FlagsFullMemory);

        int directory = w.Position;
        w.Skip(streamCount * 12);

        // スレッドの一覧 (MINIDUMP_THREAD、48 バイト)。スレッドの状態を取れない経路では、デバッガが開けるよう空のレジスタのスレッドを 1 つ置く。
        IReadOnlyList<ProcessThreadState> threadStates = memory is IProcessThreads source2 ? source2.CaptureThreads() : [];
        if (threadStates.Count == 0)
        {
            threadStates = [EmptyThread(memory.Architecture)];
        }

        int threads = w.Position;
        w.U32((uint)threadStates.Count);
        int threadEntries = w.Position;
        w.Skip(threadStates.Count * 48);
        int threadsSize = w.Position - threads;
        for (int i = 0; i < threadStates.Count; i++)
        {
            ProcessThreadState t = threadStates[i];
            w.Align(16);
            int contextRva = w.Position;
            w.Bytes(t.Context);
            int at = threadEntries + (i * 48);
            w.PutU32(at, (uint)t.ThreadId);
            w.PutU64(at + 16, (ulong)t.Teb);
            w.PutU64(at + 24, (ulong)t.StackPointer); // スタックの内容はメモリの一覧 (64 bit) にある
            w.PutU32(at + 40, (uint)t.Context.Length);
            w.PutU32(at + 44, (uint)contextRva);
        }

        ProcessArchitecture dumpArchitecture = threadStates[0].Architecture;

        // モジュールの一覧
        w.Align(4);
        int moduleList = w.Position;
        w.U32((uint)modules.Count);
        int moduleEntries = w.Position;
        w.Skip(modules.Count * ModuleSize);
        int moduleListSize = w.Position - moduleList;
        for (int i = 0; i < modules.Count; i++)
        {
            ProcessModule module = modules[i];
            w.Align(4);
            int name = w.String(string.IsNullOrEmpty(module.Path) ? module.Name : module.Path);
            (uint timeStamp, uint checkSum) = PeStamp(memory, module.BaseAddress);
            int at = moduleEntries + (i * ModuleSize);
            w.PutU64(at, (ulong)module.BaseAddress);
            w.PutU32(at + 8, (uint)Math.Min(module.Size, uint.MaxValue));
            w.PutU32(at + 12, checkSum);
            w.PutU32(at + 16, timeStamp);
            w.PutU32(at + 20, (uint)name);
            // VS_FIXEDFILEINFO (52 バイト) の署名だけを書く (版の情報は持たない)
            w.PutU32(at + 24, 0xFEEF04BD);
            w.PutU32(at + 28, 0x00010000);
        }

        // メモリの情報の一覧 (すべての領域。!address で使う)
        w.Align(8);
        int infoList = w.Position;
        w.U32(16); // SizeOfHeader
        w.U32(MemoryInfoSize);
        w.U64((ulong)all.Count);
        foreach (MemoryRegion region in all)
        {
            w.U64((ulong)region.BaseAddress);
            w.U64((ulong)(region.AllocationBase != 0 ? region.AllocationBase : region.BaseAddress));
            w.U32(region.Protect);
            w.U32(0);
            w.U64((ulong)region.Size);
            w.U32(region.State switch { RegionState.Commit => 0x1000u, RegionState.Reserve => 0x2000u, _ => 0x10000u });
            w.U32(region.State == RegionState.Commit ? region.Protect : 0);
            w.U32(region.State == RegionState.Free ? 0 : region.Type switch
            {
                RegionType.Image => 0x1000000u,
                RegionType.Mapped => 0x40000u,
                RegionType.Private => 0x20000u,
                _ => 0u,
            });
            w.U32(0);
        }

        int infoListSize = w.Position - infoList;

        // システムの情報 (MINIDUMP_SYSTEM_INFO、56 バイト) と CSD の文字列 (空)
        w.Align(4);
        int system = w.Position;
        Version os = Environment.OSVersion.Version;
        w.U16(ArchitectureCode(dumpArchitecture));
        w.U16(6); // ProcessorLevel
        w.U16(0); // ProcessorRevision
        w.U8((byte)Math.Min(Environment.ProcessorCount, 255));
        w.U8(1); // VER_NT_WORKSTATION
        w.U32((uint)os.Major);
        w.U32((uint)os.Minor);
        w.U32((uint)os.Build);
        w.U32(2); // VER_PLATFORM_WIN32_NT
        int csdRva = w.Position;
        w.U32(0); // CSDVersionRva (後で埋める)
        w.U16(0x100); // SuiteMask (VER_SUITE_SINGLEUSERTS)
        w.U16(0);
        w.Skip(24); // CPU_INFORMATION
        int systemSize = w.Position - system;
        w.Align(4);
        w.PutU32(csdRva, (uint)w.String(string.Empty));

        // その他の情報 (MINIDUMP_MISC_INFO、PID だけ)
        w.Align(4);
        int misc = w.Position;
        w.U32(24);
        w.U32(1); // MINIDUMP_MISC1_PROCESS_ID
        w.U32((uint)source.Pid);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        int miscSize = w.Position - misc;

        // メモリの一覧 (64 bit)。記述子の数は書き終わってから決まるため、上限分を空けておく (データはこの後ろ)。
        w.Align(8);
        memoryListOffset = w.Position;
        w.Skip(16 + (descriptorCapacity * DescriptorSize));

        (uint Type, int Rva, int Size)[] entries =
        [
            (ThreadListStream, threads, threadsSize),
            (ModuleListStream, moduleList, moduleListSize),
            (Memory64ListStream, memoryListOffset, 16), // 大きさは書き終わってから直す
            (MemoryInfoListStream, infoList, infoListSize),
            (SystemInfoStream, system, systemSize),
            (MiscInfoStream, misc, miscSize),
        ];
        for (int i = 0; i < entries.Length; i++)
        {
            w.PutU32(directory + (i * 12), entries[i].Type);
            w.PutU32(directory + (i * 12) + 4, (uint)entries[i].Size);
            w.PutU32(directory + (i * 12) + 8, (uint)entries[i].Rva);
        }

        return w.ToArray();
    }

    /// <summary>レジスタが 0 のスレッド (アーキテクチャの CONTEXT の大きさと ContextFlags だけを持つ)。</summary>
    private static ProcessThreadState EmptyThread(ProcessArchitecture architecture)
    {
        bool arm64 = architecture is ProcessArchitecture.Arm64 or ProcessArchitecture.Arm64EC;
        byte[] context = new byte[arm64 ? 0x390 : 1232];
        BinaryPrimitives.WriteUInt32LittleEndian(context.AsSpan(arm64 ? 0 : 0x30), arm64 ? 0x400003u : 0x100003u);
        return new ProcessThreadState(1, 0, 0, context, arm64 ? ProcessArchitecture.Arm64 : ProcessArchitecture.X64);
    }

    /// <summary>PE のヘッダーの TimeDateStamp と CheckSum (デバッガがシンボルを探すのに使う)。読めなければ 0。</summary>
    private static (uint TimeStamp, uint CheckSum) PeStamp(IProcessMemory memory, long baseAddress)
    {
        Span<byte> header = stackalloc byte[1024];
        if (memory.Read(baseAddress, header, out int read) != 0 && read < 0x200)
        {
            return (0, 0);
        }

        if (read < 0x40 || header[0] != 'M' || header[1] != 'Z')
        {
            return (0, 0);
        }

        int pe = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (pe < 0 || pe + 24 + 68 > read || BinaryPrimitives.ReadUInt32LittleEndian(header[pe..]) != 0x00004550)
        {
            return (0, 0);
        }

        uint timeStamp = BinaryPrimitives.ReadUInt32LittleEndian(header[(pe + 8)..]);
        uint checkSum = BinaryPrimitives.ReadUInt32LittleEndian(header[(pe + 24 + 64)..]);
        return (timeStamp, checkSum);
    }

    private static ushort ArchitectureCode(ProcessArchitecture architecture) => architecture switch
    {
        ProcessArchitecture.X86 => 0, // PROCESSOR_ARCHITECTURE_INTEL
        ProcessArchitecture.Arm => 5,
        ProcessArchitecture.Arm64 or ProcessArchitecture.Arm64EC => 12,
        _ => 9, // PROCESSOR_ARCHITECTURE_AMD64
    };

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>リトルエンディアンで書く小さな書き手。</summary>
    private sealed class BlobWriter
    {
        private byte[] _data = new byte[4096];

        public int Position { get; private set; }

        private void Ensure(int more)
        {
            if (Position + more > _data.Length)
            {
                Array.Resize(ref _data, Math.Max(_data.Length * 2, Position + more));
            }
        }

        public void Skip(int count)
        {
            Ensure(count);
            Position += count;
        }

        public void Align(int alignment) => Skip((int)(AlignUp(Position, alignment) - Position));

        public void U8(byte value)
        {
            Ensure(1);
            _data[Position++] = value;
        }

        public void U16(ushort value)
        {
            Ensure(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(Position), value);
            Position += 2;
        }

        public void U32(uint value)
        {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(Position), value);
            Position += 4;
        }

        public void U64(ulong value)
        {
            Ensure(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(Position), value);
            Position += 8;
        }

        public void Bytes(ReadOnlySpan<byte> data)
        {
            Ensure(data.Length);
            data.CopyTo(_data.AsSpan(Position));
            Position += data.Length;
        }

        public void PutU32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(at), value);

        public void PutU64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(_data.AsSpan(at), value);

        /// <summary>MINIDUMP_STRING (長さ (バイト、終端を含まない) + UTF-16 + 終端)。位置を返す。</summary>
        public int String(string text)
        {
            int at = Position;
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            U32((uint)bytes.Length);
            Ensure(bytes.Length + 2);
            bytes.CopyTo(_data, Position);
            Position += bytes.Length;
            U16(0);
            return at;
        }

        public byte[] ToArray() => _data[..Position];
    }
}
