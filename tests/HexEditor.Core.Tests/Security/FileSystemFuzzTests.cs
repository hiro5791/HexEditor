using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// パーティション表とファイルシステムの解析 (FOR-06〜FOR-17) のファジング。テスト用イメージのメタデータの近く (ブートセクタ、FAT、
/// ディレクトリ、MFT レコード、INDX、パーティション表) のバイトを変えたものを解析し、例外 (開けない場合の <see cref="FileSystemOpenException"/> 以外)・
/// 無限ループ・確保量の上限超えがないことを確かめる。
/// </summary>
public sealed class FileSystemFuzzTests
{
    /// <summary>元のイメージに変更を重ねた読み込み。</summary>
    private sealed class PatchedReader(IVolumeReader inner, IReadOnlyDictionary<long, byte> patches) : IVolumeReader
    {
        public long Length => inner.Length;

        public ReadResult Read(long offset, Span<byte> buffer)
        {
            ReadResult r = inner.Read(offset, buffer);
            foreach ((long at, byte value) in patches)
            {
                if (at >= offset && at < offset + r.BytesReturned)
                {
                    buffer[(int)(at - offset)] = value;
                }
            }

            return r;
        }
    }

    private sealed record Case(string Name, Dictionary<long, byte> Patches)
    {
        public override string ToString() => $"{Name}: {string.Join(' ', Patches.Take(8).Select(p => $"{p.Key:X}={p.Value:X2}"))}";
    }

    private static readonly byte[] Special = [0x00, 0xFF, 0x7F, 0x80, 0x01, 0xFE];

    private static IEnumerable<Case> Mutations(string name, int seed, int count, IReadOnlyList<(long Start, long Length)> regions)
    {
        var random = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var patches = new Dictionary<long, byte>();
            int edits = random.Next(1, 9);
            for (int e = 0; e < edits; e++)
            {
                (long start, long length) = regions[random.Next(regions.Count)];
                long at = start + (long)(random.NextDouble() * length);
                patches[at] = random.Next(3) == 0 ? Special[random.Next(Special.Length)] : (byte)random.Next(256);
            }

            yield return new Case(name, patches);
        }
    }

    private static bool Allowed(Exception ex) => ex is FileSystemOpenException or OperationCanceledException;

    /// <summary>ファイルシステムを開き、ブラウザが行う操作を一通り行う (件数・深さに上限)。</summary>
    private static void Exercise(IVolumeReader reader)
    {
        IFileSystem fs = FileSystemRegistry.Open(reader);
        _ = fs.Summary;
        _ = fs.Issues;
        _ = fs.MetadataRegions;
        var queue = new Queue<(FsEntry, int)>();
        queue.Enqueue((fs.Root, 0));
        int seen = 0;
        while (queue.Count > 0 && seen < 400)
        {
            (FsEntry dir, int depth) = queue.Dequeue();
            foreach (FsEntry e in fs.ListDirectory(dir).Take(200))
            {
                seen++;
                IReadOnlyList<FsExtent> extents = fs.GetExtents(e);
                _ = fs.Describe(e);
                IVolumeReader content = fs.OpenContent(e);
                byte[] head = new byte[(int)Math.Min(content.Length, 8192)];
                content.Read(0, head);
                if (content.Length > 8192)
                {
                    content.Read(content.Length - 1, new byte[1]);
                }

                _ = fs.GetStreams(e).Take(4).ToList();
                if (e.IsDirectory && depth < 4)
                {
                    queue.Enqueue((e, depth + 1));
                }

                _ = extents.Count;
            }
        }

        _ = fs.EnumerateMetadata().Take(64).ToList();
        _ = fs.EnumerateDeleted().Take(200).ToList();
        if (fs.Allocation is { } map)
        {
            _ = map.FreeRuns(fs.FirstClusterNumber, Math.Min(fs.ClusterCount, 1 << 16)).Take(1000).ToList();
        }

        if (fs is NtfsFileSystem ntfs && NtfsLogFile.Open(ntfs) is { } log)
        {
            _ = NtfsLogFile.ReadRestartAreas(log);
            _ = NtfsLogFile.ReadRecords(log, ntfs.MftRuns, ntfs.ClusterSize, ntfs.RecordSize, limit: 500).ToList();
        }
    }

    [Fact]
    public void Fat_parsers_survive_mutated_boot_sectors_fats_and_directories()
    {
        foreach (string id in new[] { "TD-FOR-FAT12", "TD-FOR-FAT32" })
        {
            (SparseImage image, FatImageBuilder b) = FileSystemImageSet.Fat(id);
            var inner = new SparseImageReader(image);
            long fat = b.ReservedSectors * 512L;
            (long, long)[] regions =
            [
                (0, 512), (512, 512 * 7), (fat, Math.Min(4096, b.FatSectors * 512)), (fat + b.FatSectors * 512, 2048),
                (b.Files["readme.txt"].EntryOffset - 64, 512),
            ];
            Fuzz.Run(Mutations(id, 1101, 250, regions), c => Exercise(new PatchedReader(inner, c.Patches)), Allowed);
        }
    }

    [Fact]
    public void Exfat_parser_survives_mutated_metadata()
    {
        (SparseImage image, ExFatImageBuilder b) = FileSystemImageSet.ExFat();
        var inner = new SparseImageReader(image);
        (long, long)[] regions = [(0, 512 * 24), (128 * 512, 2048), (b.ClusterOffset(b.RootCluster), 1024), (b.ClusterOffset(b.BitmapCluster), 512)];
        Fuzz.Run(Mutations("exfat", 1107, 300, regions), c => Exercise(new PatchedReader(inner, c.Patches)), Allowed);
    }

    [Fact]
    public void Ntfs_parser_survives_mutated_boot_sector_records_and_indexes()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        var inner = new SparseImageReader(image.Image);
        long mft = image.Builder.MftLcn * 4096;
        long rootIndex = image.Find("\\readme.txt").Number;
        (long, long)[] regions =
        [
            (0, 512), (mft, 6 * 1024), (mft + 24 * 1024, 16 * 1024), (image.Builder.RecordOffset(rootIndex), 1024),
            (image.Builder.LogLcn * 4096, 0x4000),
        ];

        // INDX ブロック (ルートと dir1) の位置: $INDEX_ALLOCATION のラン。
        var fs = (NtfsFileSystem)FileSystemRegistry.Open(inner);
        var indexRegions = new List<(long, long)>(regions);
        foreach (FsEntry dir in new[] { fs.Root, FsTestImages.Child(fs, fs.Root, "dir1") })
        {
            foreach (FsExtent e in fs.GetExtents(dir))
            {
                indexRegions.Add((e.VolumeOffset!.Value, Math.Min(e.Length, 4096)));
            }
        }

        Fuzz.Run(Mutations("ntfs", 1110, 400, indexRegions), c => Exercise(new PatchedReader(inner, c.Patches)), Allowed);
    }

    [Fact]
    public void Partition_analysis_and_scanning_survive_mutated_tables()
    {
        foreach (string id in new[] { "TD-FOR-MBR-EXT", "TD-FOR-GPT", "TD-FOR-APM", "TD-FOR-BSD" })
        {
            var inner = FsTestImages.Reader(id);
            (long, long)[] regions = id == "TD-FOR-MBR-EXT"
                ? [(0, 512), (100352L * 512 + 0x1B0, 0x50), (166400L * 512 + 0x1B0, 0x50), (231424L * 512 + 0x1B0, 0x50)]
                : [(0, 512 * 34), (inner.Length - 512 * 33, 512 * 33)];
            Fuzz.Run(Mutations(id, 1106, 300, regions), c =>
            {
                var reader = new PatchedReader(inner, c.Patches);
                foreach (int sector in new[] { 512, 4096 })
                {
                    PartitionAnalysis a = PartitionAnalyzer.Analyze(reader, sector, new PartitionAnalyzerOptions { ShowSmallGaps = true });
                    Assert.True(a.Rows.Count(r => r.Scheme == PartitionScheme.Logical) <= PartitionAnalyzer.MaxLogical);
                }
            }, Allowed);
        }

        // 乱数のディスク (先頭 64 KiB) のスキャン。
        Fuzz.Run(Fuzz.Cases(1108, [], 0, randoms: 200, maxRandomLength: 64 * 1024), data =>
        {
            byte[] disk = new byte[64 * 1024];
            data.CopyTo(disk, 0);
            disk[510] = 0x55;
            disk[511] = 0xAA;
            var reader = new ArrayVolumeReader(disk);
            _ = PartitionAnalyzer.Analyze(reader, 512);
            _ = LostPartitionScanner.Scan(reader, 512, new LostPartitionScanOptions { Intervals = ScanIntervals.EverySector }, null);
            _ = FileSystemDetector.Detect(reader);
        });
    }

    [Fact]
    public void Ntfs_structure_readers_survive_random_input()
    {
        Fuzz.Run(Fuzz.Cases(1109, [], 0, randoms: 2000, maxRandomLength: 4096), data =>
        {
            _ = NtfsFormat.DecodeRuns(data, 0);
            byte[] output = new byte[65536];
            _ = NtfsFormat.DecompressLznt1(data, output);
            _ = NtfsFormat.ParseRecord(0, 0, data.Length >= 1024 ? data[..1024] : [.. data, .. new byte[1024 - data.Length]], 512);
            _ = NtfsFormat.ParseAttribute(data, 0, 0);
            _ = NtfsFileName.Parse(data);
            var log = new ArrayVolumeReader([.. data, .. new byte[0x6000]]);
            _ = NtfsLogFile.ReadRestartAreas(log);
            _ = NtfsLogFile.ReadRecords(log, limit: 100).ToList();
        });

        // 正しいレコードに "FILE" の署名を付けた乱数。
        Fuzz.Run(Fuzz.Cases(1111, [], 0, randoms: 2000, maxRandomLength: 1024), data =>
        {
            byte[] record = new byte[1024];
            data.CopyTo(record, 0);
            "FILE"u8.CopyTo(record);
            record[20] = 0x38;
            _ = NtfsFormat.ParseRecord(1, 0, record, 512);
        });
    }
}
