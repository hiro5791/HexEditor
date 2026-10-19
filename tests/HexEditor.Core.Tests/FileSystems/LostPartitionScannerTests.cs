using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-08 失われたパーティションのスキャン。</summary>
public sealed class LostPartitionScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-lostpart").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-08-01")]
    public void Ntfs_and_fat32_are_found_with_high_confidence_and_open_as_partitions()
    {
        IVolumeReader disk = FsTestImages.Reader("TD-FOR-LOST-PART");
        PartitionAnalysis existing = PartitionAnalyzer.Analyze(disk, 512);
        Assert.True(existing.NoTable);
        var found = new List<LostPartitionCandidate>();
        LostPartitionScanResult result = LostPartitionScanner.Scan(disk, 512, new LostPartitionScanOptions(), existing, found: found.Add);
        Assert.False(result.Cancelled);
        Assert.Equal(result.Candidates, found);
        LostPartitionCandidate ntfs = Assert.Single(result.Candidates, c => c.Kind == FileSystemKind.Ntfs);
        LostPartitionCandidate fat = Assert.Single(result.Candidates, c => c.Kind == FileSystemKind.Fat32);
        Assert.Equal(FileSystemImageSet.NtfsPartitionStart, ntfs.StartLba);
        Assert.Equal(FileSystemImageSet.NtfsPartitionSectors, ntfs.SectorCount);
        Assert.Equal(FileSystemImageSet.FatPartitionStart, fat.StartLba);
        Assert.Equal(FileSystemImageSet.FatPartitionSectors, fat.SectorCount);
        Assert.Equal(ScanConfidence.High, ntfs.Confidence);
        Assert.Equal(ScanConfidence.High, fat.Confidence);
        Assert.Equal(2, result.Candidates.Count);

        // 「パーティションとして開く」→ ファイルシステムのブラウザ: TD-FOR-FILESET のファイルが見える。
        IFileSystem fs = FileSystemRegistry.Open(disk.Slice(ntfs.StartOffset, ntfs.Size!.Value));
        Assert.Contains(fs.ListDirectory(fs.Root), e => e.Name == "readme.txt");
        IFileSystem fat32 = FileSystemRegistry.Open(disk.Slice(fat.StartOffset, fat.Size!.Value));
        Assert.Contains(fat32.ListDirectory(fat32.Root), e => e.Name == "readme.txt");
    }

    [Fact]
    [Trait("TC", "TC-FOR-08-02")]
    public void Creating_table_entries_edits_the_document_but_not_the_file_until_saved()
    {
        string path = TestDataCatalog.Generate("TD-FOR-LOST-PART", _dir);
        string before = Hash(path);
        using (var doc = new Document(FileByteSource.Open(path)))
        {
            var reader = new SnapshotVolumeReader(doc.Current);
            LostPartitionScanResult result = LostPartitionScanner.Scan(reader, 512, new LostPartitionScanOptions(), null);
            var entries = result.Candidates.Where(c => c.Kind is FileSystemKind.Ntfs or FileSystemKind.Fat32)
                .Select(c => new PartitionTableEntry(c.StartLba, c.SectorCount!.Value, c.Kind)).ToList();
            (IReadOnlyList<PartitionTableWrite> writes, PartitionTableError error) = PartitionTableWriter.CreateMbr(reader, 512, entries);
            Assert.Equal(PartitionTableError.None, error);
            using (doc.BeginGroup("partition table"))
            {
                foreach (PartitionTableWrite w in writes)
                {
                    doc.Overwrite(w.Offset, w.Data);
                }
            }

            Assert.True(doc.IsModified);
            byte[] mbr = new byte[0x42];
            doc.Current.Read(0x1BE, mbr);
            Assert.Equal(0x07, mbr[4]);
            Assert.Equal(0x0C, mbr[16 + 4]);
            Assert.Equal(0x55, mbr[0x40]);
            Assert.Equal(0xAA, mbr[0x41]);

            // ドキュメント上ではパーティション表として読める。
            PartitionAnalysis analysis = PartitionAnalyzer.Analyze(new SnapshotVolumeReader(doc.Current), 512);
            Assert.Equal([FileSystemImageSet.NtfsPartitionStart, FileSystemImageSet.FatPartitionStart], analysis.Partitions.Select(p => p.StartLba));
        }

        // 保存せずに閉じると、ファイルは変わらない。
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void Gpt_entries_can_be_created_from_candidates()
    {
        var image = new SparseImage(64 * 1024 * 1024);
        var reader = new SparseImageReader(image);
        (IReadOnlyList<PartitionTableWrite> writes, PartitionTableError error) = PartitionTableWriter.CreateGpt(reader, 512,
            [new PartitionTableEntry(2048, 10000, FileSystemKind.Ntfs, "Recovered")], () => Guid.Parse("11111111-2222-3333-4444-555555555555"));
        Assert.Equal(PartitionTableError.None, error);
        foreach (PartitionTableWrite w in writes)
        {
            image.Write(w.Offset, w.Data);
        }

        PartitionAnalysis a = PartitionAnalyzer.Analyze(reader, 512);
        PartitionRow row = Assert.Single(a.Partitions);
        Assert.Equal("Recovered", row.Name);
        Assert.Equal(2048, row.StartLba);
        Assert.Empty(a.Issues);
    }

    [Fact]
    public async Task Scan_can_be_cancelled_and_keeps_the_results_so_far()
    {
        var reader = new SlowReader(FsTestImages.Reader("TD-FOR-LOST-PART"));
        using var cts = new CancellationTokenSource();
        var found = new List<LostPartitionCandidate>();
        Task<LostPartitionScanResult> scan = Task.Run(() => LostPartitionScanner.Scan(reader, 512,
            new LostPartitionScanOptions { Intervals = ScanIntervals.EverySector }, null, lba =>
            {
                if (lba > FileSystemImageSet.NtfsPartitionStart + 4096)
                {
                    cts.Cancel();
                }
            }, found.Add, cts.Token));
        LostPartitionScanResult result = await scan.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Cancelled);
        Assert.Contains(result.Candidates, c => c.Kind == FileSystemKind.Ntfs);
    }

    [Fact]
    public void Results_can_be_exported_as_csv_and_json()
    {
        LostPartitionScanResult result = LostPartitionScanner.Scan(FsTestImages.Reader("TD-FOR-LOST-PART"), 512, new LostPartitionScanOptions(), null);
        string csv = LostPartitionScanner.ToCsv(result.Candidates);
        Assert.StartsWith("StartLba,", csv, StringComparison.Ordinal);
        Assert.Contains("2048,262144", csv, StringComparison.Ordinal);
        string json = LostPartitionScanner.ToJson(result.Candidates);
        Assert.Contains("\"startLba\": 264192", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_sectors_are_skipped_and_reported()
    {
        var faulty = new FaultyReader(FsTestImages.Reader("TD-FOR-LOST-PART"), 4096 * 512L, 512);
        LostPartitionScanResult result = LostPartitionScanner.Scan(faulty, 512, new LostPartitionScanOptions(), null);
        Assert.Contains(result.Unreadable, u => u.Lba == 4096);
        Assert.Contains(result.Candidates, c => c.Kind == FileSystemKind.Fat32);
    }

    private static string Hash(string path)
    {
        using FileStream s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private sealed class SlowReader(IVolumeReader inner) : IVolumeReader
    {
        public long Length => inner.Length;

        public ReadResult Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);
    }

    private sealed class FaultyReader(IVolumeReader inner, long badOffset, int badLength) : IVolumeReader
    {
        public long Length => inner.Length;

        public ReadResult Read(long offset, Span<byte> buffer)
        {
            ReadResult r = inner.Read(offset, buffer);
            if (offset < badOffset + badLength && badOffset < offset + buffer.Length)
            {
                long from = Math.Max(offset, badOffset), to = Math.Min(offset + buffer.Length, badOffset + badLength);
                buffer.Slice((int)(from - offset), (int)(to - from)).Clear();
                return new ReadResult(r.BytesReturned, [new UnreadableRange(from, to - from, UnreadableReason.IoError)]);
            }

            return r;
        }
    }
}
