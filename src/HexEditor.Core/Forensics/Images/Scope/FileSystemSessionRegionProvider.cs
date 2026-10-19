using HexEditor.Core.FileSystems;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>
/// スパース・スケルトンイメージ (FOR-23) の範囲を、ファイルシステムの解析 (FOR-09〜14 の <see cref="FileSystemRegistry"/>) の割り当ての情報と
/// メタデータの範囲から求める。NTFS・FAT12/16/32・exFAT のほか、ext・HFS+・APFS・ISO 9660・UDF も同じ解析を使う。
/// 割り当て済みの領域は「ボリューム全体から空きクラスタを除いたもの」(ファイルシステムの外のボリュームの末尾や予約領域は含める)。
/// </summary>
public sealed class FileSystemSessionRegionProvider : IFileSystemRegionProvider
{
    public string Name => "file systems";

    /// <summary>判定は <see cref="Analyze"/> で行う (FileSystemDetector は 4 KiB より先も読む)。</summary>
    public bool Probe(ReadOnlySpan<byte> head) => true;

    public FileSystemRegions? Analyze(IByteSource volume, CancellationToken cancellationToken)
    {
        var reader = new ByteSourceVolumeReader(volume);
        FileSystemKind kind = FileSystemDetector.Detect(reader, 0, checkBackup: false)?.Kind ?? FileSystemKind.Unknown;
        if (kind is FileSystemKind.Unknown or FileSystemKind.BitLocker or FileSystemKind.Ldm or FileSystemKind.Lvm2 || !FileSystemRegistry.CanOpen(kind))
        {
            return null;
        }

        IFileSystem fs;
        try
        {
            fs = FileSystemRegistry.Open(reader, new FileSystemOpenOptions { Kind = kind });
        }
        catch (FileSystemOpenException)
        {
            return null;
        }

        try
        {
            if (fs.Allocation is not { } map || fs.ClusterSize <= 0)
            {
                return null;
            }

            // 空きクラスタの区間 (ボリュームの先頭からのバイトの範囲)。
            var free = new List<(long Offset, long Length)>();
            foreach ((long start, long count) in map.FreeRuns(fs.FirstClusterNumber, fs.ClusterCount, cancellationToken))
            {
                if (fs.ClusterOffset(start) is long offset)
                {
                    free.Add((offset, count * fs.ClusterSize));
                }
            }

            List<(long Offset, long Length)> allocated = Complement(FileSystemRegionProviders.Merge(free), volume.Length);
            // スケルトンのメタデータ: ファイルシステムのメタデータの領域、メタデータファイル (NTFS の $MFT など)、ディレクトリの内容
            // (FAT のディレクトリはデータ領域にあり、これがないとファイル一覧が作れない)。
            var meta = new List<(long Offset, long Length)>(fs.MetadataRegions.Where(r => r.Kind == FsRegionKind.Metadata && r.Length > 0).Select(r => (r.Offset, r.Length)));
            foreach (FsEntry entry in fs.EnumerateMetadata(cancellationToken))
            {
                AddExtents(fs, entry, meta);
            }

            AddDirectories(fs, meta, cancellationToken);
            List<(long Offset, long Length)> metadata = FileSystemRegionProviders.Merge(meta);
            return new FileSystemRegions(FileSystemDetector.DisplayName(fs.Kind), allocated, metadata);
        }
        finally
        {
            (fs as IDisposable)?.Dispose();
        }
    }

    /// <summary>ディレクトリの数の上限 (壊れたディレクトリの循環・大量の項目でも止まる)。</summary>
    internal const int MaxDirectories = 1_000_000;

    private static void AddExtents(IFileSystem fs, FsEntry entry, List<(long Offset, long Length)> ranges)
    {
        foreach (FsExtent e in fs.GetExtents(entry))
        {
            if (e.VolumeOffset is long offset && !e.Resident && e.DiskLength > 0)
            {
                ranges.Add((offset, e.DiskLength));
            }
        }
    }

    /// <summary>すべてのディレクトリの内容の範囲 (幅優先。訪れたディレクトリの ID で循環を止める)。</summary>
    private static void AddDirectories(IFileSystem fs, List<(long Offset, long Length)> ranges, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<FsEntry>();
        queue.Enqueue(fs.Root);
        while (queue.Count > 0 && seen.Count < MaxDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FsEntry dir = queue.Dequeue();
            if (!seen.Add(dir.Id))
            {
                continue;
            }

            AddExtents(fs, dir, ranges);
            foreach (FsEntry child in fs.ListDirectory(dir, cancellationToken))
            {
                if (child.IsDirectory && !child.Flags.HasFlag(FsEntryFlags.Virtual) && child.Name is not "." and not "..")
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>[0, length) から、昇順で重ならない <paramref name="ranges"/> を除いた範囲。</summary>
    internal static List<(long Offset, long Length)> Complement(IReadOnlyList<(long Offset, long Length)> ranges, long length)
    {
        var result = new List<(long Offset, long Length)>();
        long at = 0;
        foreach ((long o, long l) in ranges)
        {
            long start = Math.Clamp(o, 0, length);
            if (start > at)
            {
                result.Add((at, start - at));
            }

            at = Math.Max(at, Math.Clamp(o + l, 0, length));
        }

        if (at < length)
        {
            result.Add((at, length - at));
        }

        return result;
    }
}
