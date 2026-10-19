using HexEditor.Core.Sources;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ファイルシステムを開く処理 (FOR-09 の仕様 1)。形式ごとに 1 つ登録する。ext・HFS+・APFS・ISO 9660・UDF などは
/// <see cref="FileSystemRegistry.Register"/> で加える。
/// </summary>
public interface IFileSystemProvider
{
    /// <summary>この処理が開ける種類。</summary>
    IReadOnlyCollection<FileSystemKind> Kinds { get; }

    /// <summary>
    /// 開く。<paramref name="volume"/> のオフセット 0 がファイルシステムの先頭。開けない (署名が違う、BPB が矛盾して読めない) 場合は
    /// <see cref="FileSystemOpenException"/>。
    /// </summary>
    IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions options);
}

/// <summary>開くときの指定。</summary>
public sealed record FileSystemOpenOptions
{
    /// <summary>種類 (判定できなかったときに利用者が選んだもの。null は署名から判定)。</summary>
    public FileSystemKind? Kind { get; init; }

    /// <summary>ボリュームのセクタサイズ (分かれば。BPB の値が優先)。</summary>
    public int SectorSize { get; init; } = 512;

    /// <summary>FAT: 使う FAT の番号 (0 始まり。既定は 1 つ目。FOR-11 の仕様 3)。</summary>
    public int FatIndex { get; init; }

    /// <summary>FAT: BPB の値の上書き (クラスタあたりのセクタ数。FOR-11 の「エラー」の再解析)。</summary>
    public int? OverrideSectorsPerCluster { get; init; }

    /// <summary>NTFS: 代替データストリームを一覧に出す (設定。既定は表示。FOR-10 の仕様 5)。</summary>
    public bool ShowStreams { get; init; } = true;

    /// <summary>FAT のタイムゾーンを持たない日時を UTC にするときのオフセット (ケースのタイムゾーン。FOR-05)。</summary>
    public TimeSpan LocalOffset { get; init; } = TimeSpan.Zero;
}

/// <summary>ファイルシステムとして開けない (署名・BPB の誤り)。</summary>
public sealed class FileSystemOpenException(string message, FileSystemKind kind = FileSystemKind.Unknown) : Exception(message)
{
    public FileSystemKind Kind { get; } = kind;
}

/// <summary>形式ごとの開く処理の一覧。</summary>
public static class FileSystemRegistry
{
    private static readonly List<IFileSystemProvider> Providers =
    [
        new Fat.FatFileSystemProvider(), new Ntfs.NtfsFileSystemProvider(), new Ext.ExtFileSystemProvider(), new Iso.OpticalFileSystemProvider(),
    ];
    private static readonly object Gate = new();

    /// <summary>形式を加える (同じ種類の既存の処理より優先する)。</summary>
    public static void Register(IFileSystemProvider provider)
    {
        lock (Gate)
        {
            Providers.Insert(0, provider);
        }
    }

    /// <summary>種類を開ける処理があるか。</summary>
    public static bool CanOpen(FileSystemKind kind)
    {
        lock (Gate)
        {
            return Providers.Any(p => p.Kinds.Contains(kind));
        }
    }

    /// <summary>開ける種類の一覧 (判定できない場合に選ばせる。FOR-09 の仕様 1)。</summary>
    public static IReadOnlyList<FileSystemKind> OpenableKinds
    {
        get
        {
            lock (Gate)
            {
                return [.. Providers.SelectMany(p => p.Kinds).Distinct().Order()];
            }
        }
    }

    /// <summary>
    /// 署名から種類を判定して開く (<see cref="FileSystemOpenOptions.Kind"/> があればその種類で)。判定できない・開く処理がない場合は
    /// <see cref="FileSystemOpenException"/>。
    /// </summary>
    public static IFileSystem Open(IVolumeReader volume, FileSystemOpenOptions? options = null)
    {
        options ??= new FileSystemOpenOptions();
        FileSystemKind kind = options.Kind ?? FileSystemDetector.Detect(volume, 0, checkBackup: false)?.Kind ?? FileSystemKind.Unknown;
        if (kind == FileSystemKind.Unknown)
        {
            throw new FileSystemOpenException("The file system type could not be determined.");
        }

        IFileSystemProvider? provider;
        lock (Gate)
        {
            provider = Providers.FirstOrDefault(p => p.Kinds.Contains(kind));
        }

        return provider is null
            ? throw new FileSystemOpenException($"{FileSystemDetector.DisplayName(kind)} is not supported.", kind)
            : provider.Open(volume, options with { Kind = kind });
    }
}

/// <summary>
/// 断片をつないだファイルの内容 (FOR-09 の仕様 6)。スパースの区間は 0。常駐データはバイト列から。圧縮単位は
/// <paramref name="decompress"/> で展開する (圧縮単位の論理オフセットとディスク上の断片を渡す)。
/// </summary>
public sealed class ExtentContentReader : IVolumeReader
{
    private readonly IVolumeReader _volume;
    private readonly IReadOnlyList<FsExtent> _extents;
    private readonly byte[]? _resident;
    private readonly Func<FsExtent, byte[]>? _decompress;
    private readonly object _gate = new();
    private (FsExtent Extent, byte[] Data)? _lastUnit;

    public ExtentContentReader(IVolumeReader volume, IReadOnlyList<FsExtent> extents, long length, byte[]? resident = null,
        Func<FsExtent, byte[]>? decompress = null)
    {
        _volume = volume;
        _extents = extents;
        Length = Math.Max(0, length);
        _resident = resident;
        _decompress = decompress;
    }

    public long Length { get; }

    public IReadOnlyList<FsExtent> Extents => _extents;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length)
        {
            return new ReadResult(0);
        }

        int total = (int)Math.Min(buffer.Length, Length - offset);
        Span<byte> dest = buffer[..total];
        dest.Clear();
        if (_resident is not null)
        {
            if (offset < _resident.Length)
            {
                _resident.AsSpan((int)offset, (int)Math.Min(total, _resident.Length - offset)).CopyTo(dest);
            }

            return new ReadResult(total);
        }

        List<UnreadableRange>? bad = null;
        int index = FindExtent(offset);
        for (int i = Math.Max(0, index); i < _extents.Count; i++)
        {
            FsExtent e = _extents[i];
            if (e.LogicalOffset >= offset + total)
            {
                break;
            }

            long from = Math.Max(offset, e.LogicalOffset);
            long to = Math.Min(offset + total, e.LogicalOffset + e.Length);
            if (to <= from || e.VolumeOffset is not long vol)
            {
                continue;
            }

            Span<byte> part = dest.Slice((int)(from - offset), (int)(to - from));
            if (e.Compressed && _decompress is not null)
            {
                byte[] unit = Unit(e);
                long within = from - e.LogicalOffset;
                if (within < unit.Length)
                {
                    unit.AsSpan((int)within, (int)Math.Min(part.Length, unit.Length - within)).CopyTo(part);
                }

                continue;
            }

            ReadResult r = _volume.Read(vol + (from - e.LogicalOffset), part);
            foreach (UnreadableRange u in r.Unreadable)
            {
                long s = u.Offset - vol + e.LogicalOffset;
                (bad ??= []).Add(u with { Offset = s });
            }
        }

        return new ReadResult(total, bad);
    }

    /// <summary>ファイルの論理オフセットに対応するボリューム上の位置 (スパース・圧縮・常駐なら null。FOR-09 の仕様 6 のステータスバー)。</summary>
    public long? VolumeOffsetOf(long logical)
    {
        int index = FindExtent(logical);
        if (index < 0)
        {
            return null;
        }

        FsExtent e = _extents[index];
        return e.VolumeOffset is long v && !e.Compressed && !e.Resident && logical < e.LogicalOffset + e.Length ? v + (logical - e.LogicalOffset) : null;
    }

    private byte[] Unit(FsExtent e)
    {
        lock (_gate)
        {
            if (_lastUnit is { } last && ReferenceEquals(last.Extent, e))
            {
                return last.Data;
            }
        }

        byte[] data = _decompress!(e);
        lock (_gate)
        {
            _lastUnit = (e, data);
        }

        return data;
    }

    /// <summary>論理オフセットを含む (またはその後ろの最初の) 断片の番号。</summary>
    private int FindExtent(long logical)
    {
        int lo = 0, hi = _extents.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_extents[mid].LogicalOffset <= logical)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}

/// <summary>ビットマップ (1 ビットが 1 クラスタ、1 が使用中) をボリュームから必要な部分だけ読む割り当て情報 (exFAT・NTFS)。</summary>
public sealed class BitmapAllocationMap : IAllocationMap
{
    private readonly IVolumeReader _bitmap;
    private readonly long _firstCluster;

    /// <param name="bitmap">ビットマップの内容 (ビット 0 がクラスタ <paramref name="firstCluster"/>)。</param>
    public BitmapAllocationMap(IVolumeReader bitmap, long clusterCount, long firstCluster)
    {
        _bitmap = bitmap is CachedVolumeReader ? bitmap : new CachedVolumeReader(bitmap, 64 * 1024, 64);
        ClusterCount = clusterCount;
        _firstCluster = firstCluster;
    }

    public long ClusterCount { get; }

    public bool IsAllocated(long cluster)
    {
        long bit = cluster - _firstCluster;
        if (bit < 0 || bit >= ClusterCount)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[1];
        _bitmap.Read(bit >> 3, b);
        return (b[0] & (1 << (int)(bit & 7))) != 0;
    }

    public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default)
    {
        long start = Math.Max(first, _firstCluster);
        long end = Math.Min(first + count, _firstCluster + ClusterCount);
        byte[] chunk = new byte[64 * 1024];
        long runStart = -1;
        for (long c = start; c < end;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long bit = c - _firstCluster;
            long byteIndex = bit >> 3;
            int n = (int)Math.Min(chunk.Length, ((end - _firstCluster + 7) >> 3) - byteIndex);
            if (n <= 0)
            {
                break;
            }

            _bitmap.Read(byteIndex, chunk.AsSpan(0, n));
            for (int i = 0; i < n && c < end; i++)
            {
                byte value = chunk[i];
                int firstBit = i == 0 ? (int)(bit & 7) : 0;
                if (firstBit == 0 && c + 8 <= end && value is 0 or 0xFF)
                {
                    if (value == 0)
                    {
                        if (runStart < 0)
                        {
                            runStart = c;
                        }
                    }
                    else if (runStart >= 0)
                    {
                        yield return (runStart, c - runStart);
                        runStart = -1;
                    }

                    c += 8;
                    continue;
                }

                for (int b = firstBit; b < 8 && c < end; b++, c++)
                {
                    bool used = (value & (1 << b)) != 0;
                    if (!used && runStart < 0)
                    {
                        runStart = c;
                    }
                    else if (used && runStart >= 0)
                    {
                        yield return (runStart, c - runStart);
                        runStart = -1;
                    }
                }
            }
        }

        if (runStart >= 0)
        {
            yield return (runStart, end - runStart);
        }
    }

    public long CountFree(CancellationToken cancellationToken = default)
    {
        long free = 0;
        foreach ((_, long n) in FreeRuns(_firstCluster, ClusterCount, cancellationToken))
        {
            free += n;
        }

        return free;
    }
}

/// <summary>ファイルシステムの実装の共通の部分。</summary>
public abstract class FileSystemBase : IFileSystem
{
    protected FileSystemBase(IVolumeReader volume)
    {
        Volume = volume;
    }

    public abstract FileSystemKind Kind { get; }
    public IVolumeReader Volume { get; }
    public abstract FileSystemSummary Summary { get; }
    public virtual IReadOnlyList<FsIssue> Issues => IssueList;
    protected List<FsIssue> IssueList { get; } = [];
    public abstract long ClusterSize { get; }
    public abstract long ClusterCount { get; }
    public virtual long FirstClusterNumber => 0;
    public virtual bool UsesBlocks => false;
    public abstract int SectorSize { get; }
    public abstract long FileSystemLength { get; }

    /// <summary>クラスタ番号 <see cref="FirstClusterNumber"/> の先頭のオフセット。</summary>
    protected abstract long ClusterAreaOffset { get; }

    public virtual long? ClusterOffset(long cluster)
    {
        long index = cluster - FirstClusterNumber;
        return index < 0 || index >= ClusterCount ? null : ClusterAreaOffset + index * ClusterSize;
    }

    public virtual long? ClusterAt(long offset)
    {
        if (offset < ClusterAreaOffset)
        {
            return null;
        }

        long index = (offset - ClusterAreaOffset) / ClusterSize;
        return index >= ClusterCount ? null : FirstClusterNumber + index;
    }

    public abstract FsEntry Root { get; }
    public abstract IEnumerable<FsEntry> ListDirectory(FsEntry directory, CancellationToken cancellationToken = default);
    public abstract IReadOnlyList<FsExtent> GetExtents(FsEntry entry);
    public abstract IVolumeReader OpenContent(FsEntry entry);
    public abstract IReadOnlyList<FsDetail> Describe(FsEntry entry);
    public abstract IEnumerable<FsEntry> EnumerateMetadata(CancellationToken cancellationToken = default);
    public abstract IEnumerable<FsEntry> EnumerateDeleted(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    public virtual IEnumerable<FsEntry> EnumerateOrphans(IProgress<double>? progress = null, CancellationToken cancellationToken = default) => [];
    public abstract IEnumerable<FsEntry> EnumerateAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    public abstract IAllocationMap? Allocation { get; }
    public abstract IReadOnlyList<FsRegion> MetadataRegions { get; }
    public virtual FsEntry? FindById(string id) => null;
    public virtual IEnumerable<FsEntry> GetStreams(FsEntry entry) => [];

    /// <summary>
    /// 削除された項目の状態 (FOR-17 の仕様 3): データのクラスタがすべて未割り当てなら良好、一部が割り当て済みなら一部上書き、すべてなら上書き済み。
    /// </summary>
    protected FsRecoveryStatus RecoveryOf(IReadOnlyList<FsExtent> extents)
    {
        if (extents.Count == 0)
        {
            return FsRecoveryStatus.Unknown;
        }

        if (extents.All(e => e.Resident))
        {
            return FsRecoveryStatus.Resident;
        }

        if (Allocation is not { } map)
        {
            return FsRecoveryStatus.Unknown;
        }

        long used = 0, total = 0;
        foreach (FsExtent e in extents)
        {
            if (e.Resident || e.IsSparse)
            {
                continue;
            }

            for (long c = 0; c < e.ClusterCount && total < 1_000_000; c++)
            {
                total++;
                if (map.IsAllocated(e.StartCluster + c))
                {
                    used++;
                }
            }
        }

        return total == 0 ? FsRecoveryStatus.Unknown : used == 0 ? FsRecoveryStatus.Good : used == total ? FsRecoveryStatus.Overwritten
            : FsRecoveryStatus.PartiallyOverwritten;
    }

    /// <summary>クラスタの連続区間を断片にする (論理オフセットはつなげた順)。</summary>
    protected List<FsExtent> ExtentsFromRuns(IEnumerable<(long Start, long Count)> runs, long size)
    {
        var list = new List<FsExtent>();
        long logical = 0;
        foreach ((long start, long count) in runs)
        {
            if (logical >= size && size >= 0)
            {
                break;
            }

            long bytes = count * ClusterSize;
            long length = Math.Min(bytes, Math.Max(0, size - logical));
            if (ClusterOffset(start) is long offset && length > 0)
            {
                list.Add(new FsExtent(logical, length, offset, start, count));
            }

            logical += bytes;
        }

        return list;
    }

    /// <summary>連続するクラスタ番号を区間にまとめる。</summary>
    protected static IEnumerable<(long Start, long Count)> Coalesce(IEnumerable<long> clusters)
    {
        long start = -1, count = 0;
        foreach (long c in clusters)
        {
            if (start >= 0 && c == start + count)
            {
                count++;
                continue;
            }

            if (start >= 0)
            {
                yield return (start, count);
            }

            start = c;
            count = 1;
        }

        if (start >= 0)
        {
            yield return (start, count);
        }
    }
}
