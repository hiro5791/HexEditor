using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Sources;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// 各ファイルの末尾クラスタの一覧 (FOR-16 の仕様 4: ファイルスラックの判定に使う)。初回に全ファイルを走査して作る (長時間処理)。
/// 1 件はクラスタ番号とクラスタ内のデータの終わりの位置で、クラスタ番号の順に並べて二分探索する。
/// </summary>
public sealed class FileTailTable
{
    private readonly long[] _clusters;
    private readonly int[] _ends;
    private readonly string[]? _paths;

    private FileTailTable(long[] clusters, int[] ends, string[]? paths)
    {
        _clusters = clusters;
        _ends = ends;
        _paths = paths;
    }

    public int Count => _clusters.Length;

    /// <summary>作る。末尾がクラスタの途中で終わるファイル (非常駐) だけを記録する。<paramref name="keepPaths"/> ならスラックの書き出し用にパスも持つ。</summary>
    public static FileTailTable Build(IFileSystem fs, bool keepPaths = false, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = new List<(long Cluster, int End, string Path)>();
        long clusterSize = fs.ClusterSize;
        foreach (FsEntry e in fs.EnumerateAll(progress, cancellationToken))
        {
            if (e.IsDirectory || e.Flags.HasFlag(FsEntryFlags.Compressed))
            {
                continue;
            }

            IReadOnlyList<FsExtent> extents = fs.GetExtents(e);
            FsExtent? last = extents.LastOrDefault(x => !x.Resident && !x.IsSparse && x.ClusterCount > 0);
            if (last is null)
            {
                continue;
            }

            // 最後の断片の中で、データが終わるクラスタとその中の位置。
            long dataInLast = Math.Max(0, Math.Min(last.Length, e.Size - last.LogicalOffset));
            if (dataInLast <= 0 || dataInLast >= last.ClusterCount * clusterSize)
            {
                continue;
            }

            long clusterIndex = (dataInLast - 1) / clusterSize;
            int end = (int)(dataInLast - clusterIndex * clusterSize);
            if (end >= clusterSize)
            {
                continue;
            }

            list.Add((last.StartCluster + clusterIndex, end, keepPaths ? e.Path : string.Empty));
        }

        list.Sort((a, b) => a.Cluster.CompareTo(b.Cluster));
        return new FileTailTable([.. list.Select(x => x.Cluster)], [.. list.Select(x => x.End)], keepPaths ? [.. list.Select(x => x.Path)] : null);
    }

    /// <summary>クラスタがファイルの末尾クラスタなら、クラスタ内のデータの終わりの位置 (1〜クラスタサイズ - 1)。</summary>
    public int? TailEnd(long cluster)
    {
        int i = Array.BinarySearch(_clusters, cluster);
        return i >= 0 ? _ends[i] : null;
    }

    /// <summary>すべての末尾 (クラスタ番号、データの終わり、パス)。</summary>
    public IEnumerable<(long Cluster, int End, string Path)> Entries
    {
        get
        {
            for (int i = 0; i < _clusters.Length; i++)
            {
                yield return (_clusters[i], _ends[i], _paths?[i] ?? string.Empty);
            }
        }
    }
}

/// <summary>
/// Hex ビューの強調の領域の判定 (FOR-16 の仕様 1・4)。逆引き表は使わず、割り当て情報・メタデータの範囲・末尾クラスタの一覧から、表示中の範囲だけを判定する。
/// オフセットはドキュメント上の位置 (ファイルシステムの先頭が <see cref="VolumeOffset"/>)。
/// </summary>
public sealed class FileSystemRegionClassifier
{
    private readonly IFileSystem _fs;
    private readonly FsRegion[] _metadata;
    private readonly IAllocationMap? _allocation;

    public FileSystemRegionClassifier(IFileSystem fs, long volumeOffset, long volumeLength, PartitionAnalysis? partitions = null, long documentLength = -1)
    {
        _fs = fs;
        VolumeOffset = volumeOffset;
        VolumeLength = volumeLength;
        Partitions = partitions;
        DocumentLength = documentLength < 0 ? volumeOffset + volumeLength : documentLength;
        _metadata = [.. fs.MetadataRegions.Where(r => r.Length > 0).OrderBy(r => r.Offset)];
        _allocation = fs.Allocation;
    }

    public long VolumeOffset { get; }
    public long VolumeLength { get; }
    public long DocumentLength { get; }
    public PartitionAnalysis? Partitions { get; }

    /// <summary>ファイルの末尾クラスタの一覧 (作るまでは null。スラックの強調を出さない)。</summary>
    public FileTailTable? Tails { get; set; }

    /// <summary>割り当て情報が読めるか (読めなければ強調を無効にする。仕様の「エラー」)。</summary>
    public bool CanClassify => _allocation is not null;

    /// <summary>[start, end) の領域を、同じ種類が続く区間にまとめて返す。</summary>
    public IReadOnlyList<FsRegion> Classify(long start, long end)
    {
        var result = new List<FsRegion>();
        if (end <= start)
        {
            return result;
        }

        void Add(long offset, long length, FsRegionKind kind)
        {
            if (length <= 0 || kind == FsRegionKind.None)
            {
                return;
            }

            if (result.Count > 0 && result[^1].Kind == kind && result[^1].End == offset)
            {
                result[^1] = result[^1] with { Length = result[^1].Length + length };
            }
            else
            {
                result.Add(new FsRegion(offset, length, kind));
            }
        }

        long volEnd = VolumeOffset + VolumeLength;
        long fsEnd = VolumeOffset + Math.Min(VolumeLength, _fs.FileSystemLength);
        long at = start;
        while (at < end)
        {
            if (at < VolumeOffset || at >= volEnd)
            {
                // ボリュームの外: ディスクのドキュメントならパーティションの隙間。
                long next = at < VolumeOffset ? Math.Min(end, VolumeOffset) : end;
                if (Partitions is { } p)
                {
                    long cursor = at;
                    while (cursor < next)
                    {
                        PartitionRow? row = p.PartitionAt(cursor);
                        long stop = row is not null ? Math.Min(next, row.StartOffset + row.Size) : NextPartitionStart(p, cursor, next);
                        Add(cursor, stop - cursor, row is null ? FsRegionKind.PartitionGap : FsRegionKind.None);
                        cursor = Math.Max(stop, cursor + 1);
                    }
                }

                at = next;
                continue;
            }

            if (at >= fsEnd)
            {
                long next = Math.Min(end, volEnd);
                Add(at, next - at, FsRegionKind.VolumeSlack);
                at = next;
                continue;
            }

            long v = at - VolumeOffset;
            if (MetadataAt(v) is { } meta)
            {
                long next = Math.Min(end, VolumeOffset + meta.End);
                Add(at, next - at, FsRegionKind.Metadata);
                at = next;
                continue;
            }

            long nextMeta = NextMetadataStart(v) is long nm ? VolumeOffset + nm : long.MaxValue;
            if (_fs.ClusterAt(v) is not long cluster || _fs.ClusterOffset(cluster) is not long clusterStart)
            {
                // クラスタの領域の外 (予約領域など)。
                long next = Math.Min(end, nextMeta);
                long firstCluster = _fs.ClusterOffset(_fs.FirstClusterNumber) is long fc && VolumeOffset + fc > at ? VolumeOffset + fc : fsEnd;
                next = Math.Min(next, Math.Max(at + 1, firstCluster));
                Add(at, next - at, FsRegionKind.Metadata);
                at = next;
                continue;
            }

            long cs = _fs.ClusterSize;
            long clusterEnd = Math.Min(Math.Min(end, VolumeOffset + clusterStart + cs), nextMeta);
            long baseOffset = VolumeOffset + clusterStart;
            if (_allocation is { } map && !map.IsAllocated(cluster))
            {
                Add(at, clusterEnd - at, FsRegionKind.Unallocated);
            }
            else if (Tails?.TailEnd(cluster) is int tail)
            {
                long dataEnd = baseOffset + tail;
                long sectorEnd = baseOffset + ((tail + _fs.SectorSize - 1) / _fs.SectorSize * _fs.SectorSize);
                Span(at, clusterEnd, baseOffset, dataEnd, FsRegionKind.FileData, Add);
                Span(at, clusterEnd, dataEnd, sectorEnd, FsRegionKind.RamSlack, Add);
                Span(at, clusterEnd, sectorEnd, baseOffset + cs, FsRegionKind.DriveSlack, Add);
            }
            else
            {
                Add(at, clusterEnd - at, FsRegionKind.FileData);
            }

            at = clusterEnd;
        }

        return result;
    }

    private static void Span(long from, long to, long regionStart, long regionEnd, FsRegionKind kind, Action<long, long, FsRegionKind> add)
    {
        long s = Math.Max(from, regionStart), e = Math.Min(to, regionEnd);
        if (e > s)
        {
            add(s, e - s, kind);
        }
    }

    private static long NextPartitionStart(PartitionAnalysis p, long offset, long limit)
    {
        long next = limit;
        foreach (PartitionRow r in p.Partitions)
        {
            if (r.StartOffset > offset && r.StartOffset < next)
            {
                next = r.StartOffset;
            }
        }

        return next;
    }

    private FsRegion? MetadataAt(long volumeOffset)
    {
        int lo = 0, hi = _metadata.Length - 1;
        FsRegion? found = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_metadata[mid].Offset <= volumeOffset)
            {
                found = _metadata[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // 重なる範囲があるので、前の数件も見る。
        if (found is { } f && f.End > volumeOffset)
        {
            return f;
        }

        for (int i = Math.Max(0, lo - 8); i < Math.Min(_metadata.Length, lo + 1); i++)
        {
            if (_metadata[i].Offset <= volumeOffset && _metadata[i].End > volumeOffset)
            {
                return _metadata[i];
            }
        }

        return null;
    }

    private long? NextMetadataStart(long volumeOffset)
    {
        int lo = 0, hi = _metadata.Length - 1;
        long? next = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_metadata[mid].Offset > volumeOffset)
            {
                next = _metadata[mid].Offset;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return next;
    }
}

/// <summary>書き出しの対応表の 1 行 (FOR-16 の仕様 5・6)。</summary>
public sealed record ExtractMapRow(long ExportOffset, long DiskOffset, long Length, string Path = "");

/// <summary>
/// 未割り当て領域とスラックの書き出し、未割り当て領域の仮想ドキュメント (FOR-16 の仕様 5・6)。オフセットはドキュメント上の位置 (ボリュームの先頭が
/// <c>volumeOffset</c>)。
/// </summary>
public static class FileSystemExtraction
{
    /// <summary>未割り当てのクラスタの区間 (ドキュメント上の位置)。</summary>
    public static IEnumerable<ExtractMapRow> UnallocatedRuns(IFileSystem fs, long volumeOffset, CancellationToken cancellationToken = default)
    {
        if (fs.Allocation is not { } map)
        {
            yield break;
        }

        long export = 0;
        foreach ((long start, long count) in map.FreeRuns(fs.FirstClusterNumber, fs.ClusterCount, cancellationToken))
        {
            if (fs.ClusterOffset(start) is not long offset)
            {
                continue;
            }

            long length = count * fs.ClusterSize;
            yield return new ExtractMapRow(export, volumeOffset + offset, length);
            export += length;
        }
    }

    /// <summary>全ファイルのスラック (RAM スラックとドライブスラック。ファイルの末尾からクラスタの終わりまで)。</summary>
    public static IEnumerable<ExtractMapRow> SlackRuns(IFileSystem fs, long volumeOffset, FileTailTable tails)
    {
        long export = 0;
        foreach ((long cluster, int end, string path) in tails.Entries)
        {
            if (fs.ClusterOffset(cluster) is not long offset)
            {
                continue;
            }

            long length = fs.ClusterSize - end;
            yield return new ExtractMapRow(export, volumeOffset + offset + end, length, path);
            export += length;
        }
    }

    /// <summary>
    /// 区間を順につないで <paramref name="outputPath"/> に書き、対応表 <c>.map.csv</c> を書く (<paramref name="writeMap"/>)。書いたバイト数を返す。
    /// </summary>
    public static long Write(IVolumeReader document, IEnumerable<ExtractMapRow> rows, string outputPath, bool writeMap, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long written = 0;
        byte[] buffer = new byte[1 << 20];
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using StreamWriter? map = writeMap ? new StreamWriter(outputPath + ".map.csv", false, new UTF8Encoding(false)) : null;
        map?.Write("ExportOffset,DiskOffset,Length,Path\r\n");
        foreach (ExtractMapRow row in rows)
        {
            for (long done = 0; done < row.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, row.Length - done);
                Span<byte> span = buffer.AsSpan(0, n);
                span.Clear();
                document.Read(row.DiskOffset + done, span);
                output.Write(span);
                done += n;
                written += n;
                progress?.Invoke(written);
            }

            map?.Write(string.Create(CultureInfo.InvariantCulture, $"{row.ExportOffset},{row.DiskOffset},{row.Length},{Csv(row.Path)}\r\n"));
        }

        return written;
    }

    /// <summary>対応表を読む (書き出したファイルの位置から元の位置を求める。受け入れ基準 4)。</summary>
    public static IReadOnlyList<ExtractMapRow> ReadMap(string mapPath)
    {
        var rows = new List<ExtractMapRow>();
        foreach (string line in File.ReadLines(mapPath).Skip(1))
        {
            string[] p = line.Split(',', 4);
            if (p.Length >= 3 && long.TryParse(p[0], CultureInfo.InvariantCulture, out long e) && long.TryParse(p[1], CultureInfo.InvariantCulture, out long d)
                && long.TryParse(p[2], CultureInfo.InvariantCulture, out long l))
            {
                rows.Add(new ExtractMapRow(e, d, l, p.Length > 3 ? p[3].Trim('"') : string.Empty));
            }
        }

        return rows;
    }

    /// <summary>対応表で、書き出したファイルの位置を元の位置にする (範囲外なら null)。</summary>
    public static long? ToDiskOffset(IReadOnlyList<ExtractMapRow> map, long exportOffset)
    {
        int lo = 0, hi = map.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            ExtractMapRow r = map[mid];
            if (exportOffset < r.ExportOffset)
            {
                hi = mid - 1;
            }
            else if (exportOffset >= r.ExportOffset + r.Length)
            {
                lo = mid + 1;
            }
            else
            {
                return r.DiskOffset + (exportOffset - r.ExportOffset);
            }
        }

        return null;
    }

    internal static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>
/// 区間をつないだ仮想のデータソース (「未割り当て領域を別タブで開く」。FOR-16 の仕様 5)。元の位置は <see cref="DiskOffsetOf"/> で求め、ステータスバーに出す。
/// 読み取り専用。
/// </summary>
public sealed class ConcatenatedByteSource : ByteSourceBase
{
    private readonly IVolumeReader _document;
    private readonly ExtractMapRow[] _rows;

    public ConcatenatedByteSource(IVolumeReader document, IEnumerable<ExtractMapRow> rows, string displayName)
    {
        _document = document;
        _rows = [.. rows];
        DisplayName = displayName;
        Identity = "fs-extract:" + Guid.NewGuid().ToString("N");
        Length = _rows.Length == 0 ? 0 : _rows[^1].ExportOffset + _rows[^1].Length;
    }

    public override string DisplayName { get; }
    public override string Identity { get; }
    public override long Length { get; }
    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public IReadOnlyList<ExtractMapRow> Map => _rows;

    public long? DiskOffsetOf(long offset) => FileSystemExtraction.ToDiskOffset(_rows, offset);

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int total = ClampToLength(offset, buffer.Length);
        int done = 0;
        int i = Find(offset);
        while (done < total && i >= 0 && i < _rows.Length)
        {
            ExtractMapRow r = _rows[i];
            long within = offset + done - r.ExportOffset;
            int n = (int)Math.Min(total - done, r.Length - within);
            if (n <= 0)
            {
                i++;
                continue;
            }

            Span<byte> part = buffer.Slice(done, n);
            part.Clear();
            _document.Read(r.DiskOffset + within, part);
            done += n;
            i++;
        }

        return new ReadResult(total);
    }

    private int Find(long offset)
    {
        int lo = 0, hi = _rows.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_rows[mid].ExportOffset <= offset)
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

/// <summary>ファイルの内容の読み取り専用のデータソース (「ファイルを別タブで開く」。FOR-09 の仕様 6)。</summary>
public sealed class FileSystemContentSource(IVolumeReader content, string displayName, string identity) : ByteSourceBase
{
    public override string DisplayName { get; } = displayName;
    public override string Identity { get; } = identity;
    public override long Length => content.Length;
    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public IVolumeReader Content => content;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        if (offset >= Length)
        {
            return new ReadResult(0);
        }

        return content.Read(offset, buffer[..ClampToLength(offset, buffer.Length)]);
    }
}

/// <summary>書き出しの設定 (FOR-09 の仕様 6)。</summary>
public sealed record FileExportOptions
{
    public bool KeepStructure { get; init; } = true;
    public bool IncludeStreams { get; init; } = true;
    public bool IncludeSlack { get; init; }
    public bool IncludeDeleted { get; init; }
}

/// <summary>マニフェストの 1 行 (FOR-09 の仕様 6、FOR-17 の仕様 5 の「状態」)。</summary>
public sealed record FileExportRow(string SourcePath, string FileId, long Size, FsTimestamp Created, FsTimestamp Modified, FsTimestamp Accessed,
    string Md5, string Sha256, string Destination, FsRecoveryStatus Status, IReadOnlyList<(long Offset, long Length)> Unreadable);

/// <summary>書き出し先の空き容量が足りない (書き出し前に検出して止める)。</summary>
public sealed class ExportSpaceException(long required, long available) : IOException($"Not enough free space: {required} bytes needed, {available} available.")
{
    public long Required { get; } = required;
    public long Available { get; } = available;
}

/// <summary>
/// ファイルの書き出し (FOR-09 の仕様 6、FOR-17 の仕様 5)。書き出したファイルごとに MD5 と SHA-256 を計算し、<c>export-manifest.csv</c> を作る。
/// 読めなかった範囲は 0 で埋めて、マニフェストに範囲を記録する。名前は書き出し先のフォルダの外に出ないように直す。
/// </summary>
public static class FileExporter
{
    public const string ManifestName = "export-manifest.csv";
    public const int MaxDepth = 64;

    /// <summary>書き出す。<paramref name="entries"/> のフォルダは中身ごと書き出す。<paramref name="freeSpace"/> は書き出し先の空き容量 (テスト用に差し替える)。</summary>
    public static IReadOnlyList<FileExportRow> Export(IFileSystem fs, IReadOnlyList<FsEntry> entries, string outputFolder, FileExportOptions options,
        Action<long>? progress = null, Func<string, long>? freeSpace = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        var files = new List<(FsEntry Entry, string Relative)>();
        foreach (FsEntry e in entries)
        {
            Collect(fs, e, string.Empty, options, files, 0, cancellationToken);
        }

        long required = files.Sum(f => f.Entry.Size + (options.IncludeSlack ? fs.ClusterSize : 0));
        long available = (freeSpace ?? DefaultFreeSpace)(outputFolder);
        if (available >= 0 && required > available)
        {
            throw new ExportSpaceException(required, available);
        }

        var rows = new List<FileExportRow>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long done = 0;
        foreach ((FsEntry entry, string relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Unique(Path.Combine(outputFolder, relative), used);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            IVolumeReader content = fs.OpenContent(entry);
            (string md5, string sha, List<(long, long)> bad) = Copy(content, Math.Min(entry.Size, content.Length), destination, n => progress?.Invoke(done + n),
                cancellationToken);
            done += entry.Size;
            rows.Add(new FileExportRow(entry.Path.Length > 0 ? entry.Path : entry.OriginalPath, entry.FileIdText, entry.Size, entry.Created, entry.Modified,
                entry.Accessed, md5, sha, destination, entry.State == FsEntryState.Deleted ? entry.Recovery : FsRecoveryStatus.None, bad));

            if (options.IncludeSlack && SlackOf(fs, entry) is { } slack)
            {
                string slackPath = Unique(destination + ".slack", used);
                (string m, string s, List<(long, long)> b) = Copy(slack.Reader, slack.Reader.Length, slackPath, null, cancellationToken);
                rows.Add(new FileExportRow((entry.Path.Length > 0 ? entry.Path : entry.OriginalPath) + " (slack)", entry.FileIdText, slack.Reader.Length,
                    default, default, default, m, s, slackPath, FsRecoveryStatus.None, b));
            }
        }

        WriteManifest(Path.Combine(outputFolder, ManifestName), rows);
        return rows;
    }

    private static long DefaultFreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static void Collect(IFileSystem fs, FsEntry entry, string prefix, FileExportOptions options, List<(FsEntry, string)> files, int depth,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entry.State == FsEntryState.Deleted && !options.IncludeDeleted && depth > 0)
        {
            return;
        }

        string name = SafeName(entry.StreamName is not null ? entry.Name.Replace(':', '_') : entry.Name);
        if (entry.IsDirectory)
        {
            if (depth >= MaxDepth)
            {
                return;
            }

            string sub = options.KeepStructure ? Path.Combine(prefix, name.Length == 0 ? "root" : name) : prefix;
            foreach (FsEntry child in fs.ListDirectory(entry, token).ToList())
            {
                if (child.Flags.HasFlag(FsEntryFlags.Stream))
                {
                    if (options.IncludeStreams)
                    {
                        files.Add((child, Path.Combine(sub, SafeName(child.Name.Replace(':', '_')))));
                    }

                    continue;
                }

                Collect(fs, child, sub, options, files, depth + 1, token);
            }

            return;
        }

        files.Add((entry, Path.Combine(prefix, name)));
        if (options.IncludeStreams && !entry.Flags.HasFlag(FsEntryFlags.Stream))
        {
            foreach (FsEntry stream in fs.GetStreams(entry))
            {
                // 代替データストリームは「名前_ストリーム名」で保存する。
                files.Add((stream, Path.Combine(prefix, SafeName($"{entry.Name}_{stream.StreamName}"))));
            }
        }
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>ファイルシステムの名前を、書き出し先のフォルダの外に出ない安全な名前にする (区切り・予約名・末尾の点を直す)。</summary>
    public static string SafeName(string name)
    {
        var s = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            s.Append(c < 0x20 || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }

        string result = s.ToString().TrimEnd(' ', '.');
        if (result.Length == 0 || result is "." or "..")
        {
            result = "_";
        }

        string stem = result.Split('.')[0];
        if (Reserved.Contains(stem))
        {
            result = "_" + result;
        }

        return result.Length > 200 ? result[..200] : result;
    }

    private static string Unique(string path, HashSet<string> used)
    {
        string candidate = path;
        for (int n = 2; used.Contains(candidate) || File.Exists(candidate); n++)
        {
            candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
        }

        used.Add(candidate);
        return candidate;
    }

    private static (string Md5, string Sha256, List<(long, long)> Unreadable) Copy(IVolumeReader content, long length, string destination,
        Action<long>? progress, CancellationToken token)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bad = new List<(long, long)>();
        byte[] buffer = new byte[1 << 20];
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            for (long at = 0; at < length;)
            {
                token.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - at);
                Span<byte> span = buffer.AsSpan(0, n);
                span.Clear();
                ReadResult r = content.Read(at, span);
                foreach (UnreadableRange u in r.Unreadable)
                {
                    span.Slice((int)Math.Max(0, u.Offset - at), (int)Math.Min(u.Length, n - Math.Max(0, u.Offset - at))).Clear();
                    bad.Add((u.Offset, u.Length));
                }

                output.Write(span);
                md5.AppendData(span);
                sha.AppendData(span);
                at += n;
                progress?.Invoke(at);
            }
        }

        return (Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha.GetHashAndReset()), bad);
    }

    /// <summary>ファイルのスラック (データの終わりからクラスタの終わりまで)。なければ null。</summary>
    public static (IVolumeReader Reader, long VolumeOffset)? SlackOf(IFileSystem fs, FsEntry entry)
    {
        IReadOnlyList<FsExtent> extents = fs.GetExtents(entry);
        FsExtent? last = extents.LastOrDefault(x => !x.Resident && !x.IsSparse && !x.Compressed && x.ClusterCount > 0);
        if (last is null || last.VolumeOffset is not long offset)
        {
            return null;
        }

        long dataInLast = Math.Max(0, Math.Min(last.Length, entry.Size - last.LogicalOffset));
        long allocated = last.ClusterCount * fs.ClusterSize;
        if (dataInLast >= allocated)
        {
            return null;
        }

        return (fs.Volume.Slice(offset + dataInLast, allocated - dataInLast), offset + dataInLast);
    }

    private static string Time(FsTimestamp t) => t.Utc?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture) ?? (t.Invalid ? "invalid" : string.Empty);

    public static void WriteManifest(string path, IReadOnlyList<FileExportRow> rows)
    {
        var s = new StringBuilder("SourcePath,FileId,Size,CreatedUtc,ModifiedUtc,AccessedUtc,MD5,SHA256,Destination,Status,UnreadableRanges\r\n");
        foreach (FileExportRow r in rows)
        {
            s.Append(FileSystemExtraction.Csv(r.SourcePath)).Append(',').Append(FileSystemExtraction.Csv(r.FileId)).Append(',')
                .Append(r.Size.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Time(r.Created)).Append(',').Append(Time(r.Modified)).Append(',')
                .Append(Time(r.Accessed)).Append(',').Append(r.Md5).Append(',').Append(r.Sha256).Append(',').Append(FileSystemExtraction.Csv(r.Destination))
                .Append(',').Append(r.Status == FsRecoveryStatus.None ? string.Empty : r.Status.ToString()).Append(',')
                .Append(FileSystemExtraction.Csv(string.Join(' ', r.Unreadable.Select(u => $"{u.Offset}+{u.Length}")))).Append("\r\n");
        }

        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
