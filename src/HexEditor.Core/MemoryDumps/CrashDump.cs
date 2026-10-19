using System.Buffers.Binary;
using System.Numerics;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.MemoryDumps;

/// <summary>物理メモリの範囲 (ページ番号とページ数)。</summary>
public readonly record struct PhysicalRun(long BasePage, long PageCount)
{
    public long EndPage => BasePage + PageCount;
}

/// <summary>
/// Windows のクラッシュダンプのヘッダ (01 の ENG-36 の仕様 3)。64 bit (<c>PAGEDU64</c>、0x2000 バイト) と 32 bit (<c>PAGEDUMP</c>、0x1000 バイト)。
/// <code>
/// 64 bit: 0x88 物理メモリの範囲の記述子 (範囲の数 u32、ページ数 u64、範囲 [開始ページ u64、ページ数 u64]...)、0xF98 DumpType、0xFA8 取得日時 (FILETIME)
/// 32 bit: 0x64 記述子 (範囲の数 u32、ページ数 u32、範囲 [u32, u32]...)、0xF88 DumpType、0xFC0 取得日時
/// ビットマップの形式 (DumpType 2・5・6・7): ヘッダの直後 (0x2000) に "SDMP" / "FDMP" / "KDMP" "DUMP"、0x20 最初のページの位置、0x30 ビットの数、0x38 ビットマップ
/// </code>
/// 信頼できない入力として読む: 範囲の数・ページ数・位置を確かめ、違えば <see cref="MemoryDumpFormatException"/>。
/// </summary>
public sealed record CrashDumpHeader
{
    public const int PageSize = 4096;
    public const int HeaderSize64 = 0x2000;
    public const int HeaderSize32 = 0x1000;
    public const int DumpTypeOffset64 = 0xF98;
    public const int DumpTypeOffset32 = 0xF88;
    public const int RunsOffset64 = 0x88;
    public const int RunsOffset32 = 0x64;
    public const int SystemTimeOffset64 = 0xFA8;
    public const int SystemTimeOffset32 = 0xFC0;

    /// <summary>ヘッダの記述子に入る範囲の数の上限 (64 bit: (0x348 − 0x98) / 16)。</summary>
    public const int MaxRuns64 = 43;
    public const int MaxRuns32 = 86;

    /// <summary>範囲の一覧の形式の物理アドレス空間の大きさの単位 (1 GiB)。</summary>
    public const long PhysicalAlignment = 1L << 30;

    /// <summary>物理アドレス空間の上限 (4 PiB。壊れた値で極端な長さにしない)。</summary>
    public const long MaxPhysicalPages = 1L << 40;

    public bool Is64Bit { get; init; }

    public int DumpType { get; init; }

    public MemoryDumpKind Kind { get; init; }

    public DateTime? SystemTimeUtc { get; init; }

    public int MajorVersion { get; init; }

    public int MinorVersion { get; init; }

    /// <summary>範囲の一覧 (完全ダンプ)。ビットマップの形式では空。</summary>
    public IReadOnlyList<PhysicalRun> Runs { get; init; } = [];

    /// <summary>最初のページのファイル上の位置。</summary>
    public long DataOffset { get; init; }

    /// <summary>ビットマップの形式: ビットマップのファイル上の位置とビットの数 (= 物理ページの数)。</summary>
    public long BitmapOffset { get; init; }

    public long BitmapPages { get; init; }

    /// <summary>物理アドレス空間の大きさ (バイト)。</summary>
    public long PhysicalLength { get; init; }

    /// <summary>ヘッダを読む。<paramref name="fileLength"/> は範囲の確認に使う。</summary>
    public static CrashDumpHeader Parse(ReadOnlySpan<byte> head, long fileLength, Func<long, int, byte[]?> readAt)
    {
        MemoryDumpDetection detection = MemoryDumpFormat.Detect(head);
        if (detection.Kind is MemoryDumpKind.Raw or MemoryDumpKind.Minidump || detection.IsUnsupported)
        {
            throw new MemoryDumpFormatException("This is not a supported Windows crash dump.");
        }

        bool is64 = detection.Is64Bit;
        int headerSize = is64 ? HeaderSize64 : HeaderSize32;
        if (head.Length < headerSize || fileLength < headerSize)
        {
            throw new MemoryDumpFormatException("The dump header is truncated.");
        }

        ulong fileTime = BinaryPrimitives.ReadUInt64LittleEndian(head[(is64 ? SystemTimeOffset64 : SystemTimeOffset32)..]);
        DateTime? time = fileTime is > 0 and < 0x7FFF_FFFF_FFFF_FFFF ? SafeFileTime(fileTime) : null;
        var header = new CrashDumpHeader
        {
            Is64Bit = is64,
            DumpType = detection.DumpType,
            Kind = detection.Kind,
            SystemTimeUtc = time,
            MajorVersion = BinaryPrimitives.ReadInt32LittleEndian(head[8..]),
            MinorVersion = BinaryPrimitives.ReadInt32LittleEndian(head[12..]),
        };

        return detection.Kind == MemoryDumpKind.CompleteDump
            ? ParseRuns(header, head, fileLength, headerSize)
            : ParseBitmap(header, fileLength, headerSize, readAt);
    }

    private static DateTime? SafeFileTime(ulong fileTime)
    {
        try
        {
            return DateTime.FromFileTimeUtc((long)fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static CrashDumpHeader ParseRuns(CrashDumpHeader header, ReadOnlySpan<byte> head, long fileLength, int headerSize)
    {
        bool is64 = header.Is64Bit;
        int at = is64 ? RunsOffset64 : RunsOffset32;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[at..]);
        if (count == 0 || count > (is64 ? MaxRuns64 : MaxRuns32))
        {
            throw new MemoryDumpFormatException($"The number of physical memory runs is invalid ({count}).");
        }

        long totalPages = is64 ? BinaryPrimitives.ReadInt64LittleEndian(head[(at + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[(at + 4)..]);
        int first = is64 ? at + 16 : at + 8;
        int size = is64 ? 16 : 8;
        var runs = new List<PhysicalRun>((int)count);
        long sum = 0;
        long previousEnd = -1;
        for (int i = 0; i < count; i++)
        {
            int p = first + i * size;
            long basePage = is64 ? BinaryPrimitives.ReadInt64LittleEndian(head[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[p..]);
            long pages = is64 ? BinaryPrimitives.ReadInt64LittleEndian(head[(p + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(head[(p + 4)..]);
            if (basePage < 0 || pages <= 0 || basePage > MaxPhysicalPages - pages || basePage < previousEnd)
            {
                throw new MemoryDumpFormatException($"Physical memory run {i} is invalid.");
            }

            runs.Add(new PhysicalRun(basePage, pages));
            previousEnd = basePage + pages;
            sum += pages;
        }

        // 記述子のページ数と範囲の合計が合わない場合も、範囲の合計を正とする (書いたツールによって総数が違う)。ファイルに収まらなければ壊れている。
        if (sum > (fileLength - headerSize) / PageSize)
        {
            throw new MemoryDumpFormatException($"The dump file is shorter than its physical memory runs ({sum} pages, {totalPages} declared).");
        }

        // 物理アドレス空間の大きさは、最後の範囲の終わりを 1 GiB の境界に切り上げたもの (ダンプは機械の物理アドレス空間の大きさを持たないため。
        // 範囲の後ろの空きも「未割り当て」として見せる)。
        long end = previousEnd * PageSize;
        return header with { Runs = runs, DataOffset = headerSize, PhysicalLength = Math.Min((end + PhysicalAlignment - 1) / PhysicalAlignment * PhysicalAlignment, MaxPhysicalPages * PageSize) };
    }

    private static CrashDumpHeader ParseBitmap(CrashDumpHeader header, long fileLength, int headerSize, Func<long, int, byte[]?> readAt)
    {
        byte[]? bmp = readAt(headerSize, 0x38);
        if (bmp is null || !(bmp.AsSpan(0, 4).SequenceEqual("SDMP"u8) || bmp.AsSpan(0, 4).SequenceEqual("FDMP"u8) || bmp.AsSpan(0, 4).SequenceEqual("KDMP"u8))
            || !bmp.AsSpan(4, 4).SequenceEqual("DUMP"u8))
        {
            throw new MemoryDumpFormatException("The page bitmap header is missing.");
        }

        long firstPage = BinaryPrimitives.ReadInt64LittleEndian(bmp.AsSpan(0x20));
        long bits = BinaryPrimitives.ReadInt64LittleEndian(bmp.AsSpan(0x30));
        long bitmapOffset = headerSize + 0x38;
        if (bits <= 0 || bits > MaxPhysicalPages || bitmapOffset + (bits + 7) / 8 > fileLength || firstPage < bitmapOffset + (bits + 7) / 8 || firstPage > fileLength)
        {
            throw new MemoryDumpFormatException("The page bitmap is invalid.");
        }

        return header with { DataOffset = firstPage, BitmapOffset = bitmapOffset, BitmapPages = bits, PhysicalLength = bits * PageSize };
    }

    // ---- 作る (テストデータ TD-ENG-FULLDUMP・TD-ENG-DUMP-64G) ----

    /// <summary>64 bit の完全メモリダンプのヘッダ (0x2000 バイト) を作る。</summary>
    public static byte[] BuildFull64(IReadOnlyList<PhysicalRun> runs, DateTime systemTimeUtc)
    {
        byte[] h = new byte[HeaderSize64];
        "PAGEDU64"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 15);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 19041);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x30), 0x8664);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(RunsOffset64), (uint)runs.Count);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(RunsOffset64 + 8), runs.Sum(r => r.PageCount));
        for (int i = 0; i < runs.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(RunsOffset64 + 16 + i * 16), runs[i].BasePage);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(RunsOffset64 + 24 + i * 16), runs[i].PageCount);
        }

        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(DumpTypeOffset64), MemoryDumpFormat.DumpTypeFull);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(SystemTimeOffset64), systemTimeUtc.ToFileTimeUtc());
        return h;
    }

    /// <summary>ビットマップの形式の 64 bit のダンプの先頭 (ヘッダ + ビットマップの見出し + ビットマップ) を作る。ページは返した長さの直後に並べる。</summary>
    public static byte[] BuildBitmap64(int dumpType, ReadOnlySpan<byte> bitmap, long pages, DateTime systemTimeUtc)
    {
        int bitmapBytes = (int)((pages + 7) / 8);
        long first = AlignUp(HeaderSize64 + 0x38 + bitmapBytes, PageSize);
        byte[] h = new byte[first];
        "PAGEDU64"u8.CopyTo(h);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(DumpTypeOffset64), dumpType);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(SystemTimeOffset64), systemTimeUtc.ToFileTimeUtc());
        Span<byte> b = h.AsSpan(HeaderSize64);
        (dumpType == MemoryDumpFormat.DumpTypeBitmapFull ? "FDMP"u8 : dumpType == MemoryDumpFormat.DumpTypeBitmapKernel ? "KDMP"u8 : "SDMP"u8).CopyTo(b);
        "DUMP"u8.CopyTo(b[4..]);
        BinaryPrimitives.WriteInt64LittleEndian(b[0x20..], first);
        int present = 0;
        foreach (byte x in bitmap[..bitmapBytes])
        {
            present += BitOperations.PopCount(x);
        }

        BinaryPrimitives.WriteInt64LittleEndian(b[0x28..], present);
        BinaryPrimitives.WriteInt64LittleEndian(b[0x30..], pages);
        bitmap[..bitmapBytes].CopyTo(b[0x38..]);
        return h;
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}

/// <summary>
/// クラッシュダンプの物理アドレス空間のドキュメント (01 の ENG-36 の仕様 3)。オフセット = 物理アドレス。ダンプに含まれない物理ページは
/// 「未割り当て」として読む (<see cref="UnreadableReason.Unallocated"/>)。読み取り専用。開くときに読むのはヘッダ (と、ビットマップの形式では
/// ページのビットマップの 1 回の通し読み) だけで、ページの中身は表示する範囲だけを読む (「巨大ファイル・長時間処理」)。
/// </summary>
public sealed class CrashDumpByteSource : ByteSourceBase, IRegionMapSource
{
    /// <summary>ビットマップの累積の数を持つ区切り (ページ数)。</summary>
    private const int ChunkPages = 32768;

    private readonly SafeFileHandle _handle;
    private readonly long[] _runStartIndex;
    private readonly long[]? _chunkRank;
    private readonly object _bitmapLock = new();
    private readonly Dictionary<long, byte[]> _bitmapCache = [];
    private readonly List<SourceRegion> _regions;

    private CrashDumpByteSource(string path, SafeFileHandle handle, CrashDumpHeader header, long[] runStart, long[]? chunkRank, long bytesRead)
    {
        Path = path;
        _handle = handle;
        Header = header;
        _runStartIndex = runStart;
        _chunkRank = chunkRank;
        OpenBytesRead = bytesRead;
        _regions = header.Runs.Count > 0 ? BuildRegions(header) : [new SourceRegion(0, header.PhysicalLength, RegionAccess.Readable)];
        if (header.Runs.Count > 0 && _regions[^1].End < header.PhysicalLength)
        {
            _regions.Add(new SourceRegion(_regions[^1].End, header.PhysicalLength - _regions[^1].End, RegionAccess.Unallocated));
        }
    }

    public string Path { get; }

    public CrashDumpHeader Header { get; }

    /// <summary>開くときにファイルから読んだ量 (TC-ENG-36-03 の「読み込み量」)。</summary>
    public long OpenBytesRead { get; }

    public override string DisplayName => System.IO.Path.GetFileName(Path);

    public override string Identity => "memdump:" + System.IO.Path.GetFullPath(Path).ToUpperInvariant();

    public override long Length => Header.PhysicalLength;

    public override SourceCapabilities Capabilities => SourceCapabilities.HasGaps;

    public IReadOnlyList<SourceRegion> Regions => _regions;

    public event EventHandler? RegionsChanged
    {
        add { }
        remove { }
    }

    /// <summary>ダンプを開く。形式が壊れていれば <see cref="MemoryDumpFormatException"/>。</summary>
    public static CrashDumpByteSource Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            long fileLength = RandomAccess.GetLength(handle);
            long bytesRead = 0;
            byte[]? ReadAt(long offset, int count)
            {
                if (offset < 0 || offset > fileLength - count)
                {
                    return null;
                }

                byte[] buffer = new byte[count];
                int n = RandomAccess.Read(handle, buffer, offset);
                bytesRead += n;
                return n == count ? buffer : null;
            }

            byte[] head = ReadAt(0, (int)Math.Min(CrashDumpHeader.HeaderSize64, fileLength)) ?? [];
            CrashDumpHeader header = CrashDumpHeader.Parse(head, fileLength, ReadAt);
            long[] runStart = new long[header.Runs.Count];
            long index = 0;
            for (int i = 0; i < header.Runs.Count; i++)
            {
                runStart[i] = index;
                index += header.Runs[i].PageCount;
            }

            long[]? chunkRank = null;
            if (header.BitmapPages > 0)
            {
                // ページの順位 (ファイル上の位置) を求める区切りごとの累積の数。ビットマップを 1 回だけ通して読む (物理メモリ 64 GiB で 2 MiB)。
                long chunks = (header.BitmapPages + ChunkPages - 1) / ChunkPages;
                chunkRank = new long[chunks + 1];
                byte[] buffer = new byte[ChunkPages / 8];
                long rank = 0;
                for (long c = 0; c < chunks; c++)
                {
                    long bitsHere = Math.Min(ChunkPages, header.BitmapPages - c * ChunkPages);
                    int bytes = (int)((bitsHere + 7) / 8);
                    int n = RandomAccess.Read(handle, buffer.AsSpan(0, bytes), header.BitmapOffset + c * (ChunkPages / 8));
                    bytesRead += n;
                    if (n < bytes)
                    {
                        throw new MemoryDumpFormatException("The page bitmap is truncated.");
                    }

                    chunkRank[c] = rank;
                    rank += PopCount(buffer.AsSpan(0, bytes), bitsHere);
                }

                chunkRank[chunks] = rank;
                if (header.DataOffset + rank * CrashDumpHeader.PageSize > fileLength)
                {
                    throw new MemoryDumpFormatException("The dump file is shorter than its page bitmap.");
                }
            }

            return new CrashDumpByteSource(path, handle, header, runStart, chunkRank, bytesRead);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static long PopCount(ReadOnlySpan<byte> bytes, long bits)
    {
        long count = 0;
        int full = (int)(bits / 8);
        for (int i = 0; i < full; i++)
        {
            count += BitOperations.PopCount(bytes[i]);
        }

        int rest = (int)(bits % 8);
        if (rest > 0)
        {
            count += BitOperations.PopCount((uint)(bytes[full] & ((1 << rest) - 1)));
        }

        return count;
    }

    private static List<SourceRegion> BuildRegions(CrashDumpHeader header)
    {
        var regions = new List<SourceRegion>();
        long cursor = 0;
        foreach (PhysicalRun run in header.Runs)
        {
            long start = run.BasePage * CrashDumpHeader.PageSize;
            if (start > cursor)
            {
                regions.Add(new SourceRegion(cursor, start - cursor, RegionAccess.Unallocated));
            }

            regions.Add(new SourceRegion(start, run.PageCount * CrashDumpHeader.PageSize, RegionAccess.Readable));
            cursor = start + run.PageCount * CrashDumpHeader.PageSize;
        }

        return regions;
    }

    /// <summary>物理ページのファイル上の位置。ダンプに含まれなければ -1。</summary>
    public long FileOffsetOfPage(long page)
    {
        if (page < 0 || page >= Length / CrashDumpHeader.PageSize)
        {
            return -1;
        }

        if (_chunkRank is null)
        {
            IReadOnlyList<PhysicalRun> runs = Header.Runs;
            int lo = 0, hi = runs.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                PhysicalRun run = runs[mid];
                if (page < run.BasePage)
                {
                    hi = mid - 1;
                }
                else if (page >= run.EndPage)
                {
                    lo = mid + 1;
                }
                else
                {
                    return Header.DataOffset + (_runStartIndex[mid] + page - run.BasePage) * CrashDumpHeader.PageSize;
                }
            }

            return -1;
        }

        long chunk = page / ChunkPages;
        byte[] bits = BitmapChunk(chunk);
        int inChunk = (int)(page % ChunkPages);
        if ((bits[inChunk / 8] & (1 << (inChunk % 8))) == 0)
        {
            return -1;
        }

        long rank = _chunkRank[chunk] + PopCount(bits, inChunk);
        return Header.DataOffset + rank * CrashDumpHeader.PageSize;
    }

    private byte[] BitmapChunk(long chunk)
    {
        lock (_bitmapLock)
        {
            if (_bitmapCache.TryGetValue(chunk, out byte[]? cached))
            {
                return cached;
            }

            if (_bitmapCache.Count >= 64)
            {
                _bitmapCache.Clear();
            }

            byte[] buffer = new byte[ChunkPages / 8];
            long bitsHere = Math.Min(ChunkPages, Header.BitmapPages - chunk * ChunkPages);
            int bytes = (int)((bitsHere + 7) / 8);
            RandomAccess.Read(_handle, buffer.AsSpan(0, bytes), Header.BitmapOffset + chunk * (ChunkPages / 8));
            _bitmapCache[chunk] = buffer;
            return buffer;
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int length = ClampToLength(offset, buffer.Length);
        if (length == 0)
        {
            return new ReadResult(0);
        }

        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < length)
        {
            long address = offset + done;
            long page = address / CrashDumpHeader.PageSize;
            int inPage = (int)(address % CrashDumpHeader.PageSize);
            int n = Math.Min(length - done, CrashDumpHeader.PageSize - inPage);
            Span<byte> dest = buffer.Slice(done, n);
            long fileAt = FileOffsetOfPage(page);
            if (fileAt < 0)
            {
                dest.Clear();
                Add(ref bad, address, n, UnreadableReason.Unallocated);
            }
            else
            {
                int read = RandomAccess.Read(_handle, dest, fileAt + inPage);
                if (read < n)
                {
                    dest[Math.Max(0, read)..].Clear();
                    Add(ref bad, address + read, n - read, UnreadableReason.IoError);
                }
            }

            done += n;
        }

        return new ReadResult(length, bad);
    }

    private static void Add(ref List<UnreadableRange>? list, long offset, long length, UnreadableReason reason)
    {
        list ??= [];
        if (list.Count > 0 && list[^1] is var last && last.End == offset && last.Reason == reason)
        {
            list[^1] = last with { Length = last.Length + length };
        }
        else
        {
            list.Add(new UnreadableRange(offset, length, reason));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }
}
