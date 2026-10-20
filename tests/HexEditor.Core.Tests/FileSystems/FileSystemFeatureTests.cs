using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-09 の書き出し、FOR-15 の対応付け、FOR-16 の強調と抽出、FOR-17 の削除項目。</summary>
public sealed class FileSystemFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-fs").FullName;

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

    // ---- FOR-09 ----

    [Fact]
    [Trait("TC", "TC-FOR-09-04")]
    public void Export_writes_files_streams_and_a_manifest_with_hashes_and_records_an_audit_event()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        FsEntry[] selected = [.. new[] { "readme.txt", "photo.png", "file.txt" }.Select(n => FsTestImages.Child(fs, fs.Root, n))];
        var events = new List<FileSystemAuditEvent>();
        FileSystemAudit.Sink = events.Add;
        try
        {
            string exports = Path.Combine(_dir, "exports");
            IReadOnlyList<FileExportRow> rows = FileExporter.Export(fs, selected, exports, new FileExportOptions());
            FileSystemAudit.Record(FileSystemAuditAction.ExportFiles, "TD-FOR-NTFS",
                new Dictionary<string, string> { ["manifest"] = Path.Combine(exports, FileExporter.ManifestName), ["count"] = rows.Count.ToString() });
            Assert.Equal(["file.txt", "file.txt_secret", "photo.png", "readme.txt"], Directory.GetFiles(exports).Select(Path.GetFileName)
                .Where(n => n != FileExporter.ManifestName).Order(StringComparer.Ordinal));
            Assert.Equal(FileSet.SecretStream, File.ReadAllBytes(Path.Combine(exports, "file.txt_secret")));
            string[] manifest = File.ReadAllLines(Path.Combine(exports, FileExporter.ManifestName));
            Assert.Equal("SourcePath,FileId,Size,CreatedUtc,ModifiedUtc,AccessedUtc,MD5,SHA256,Destination,Status,UnreadableRanges", manifest[0]);
            Assert.Equal(4, manifest.Length - 1);
            foreach (FileExportRow row in rows)
            {
                byte[] written = File.ReadAllBytes(row.Destination);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(written)), row.Sha256);
                Assert.Equal(Convert.ToHexStringLower(MD5.HashData(written)), row.Md5);
                Assert.Contains(manifest, l => l.Contains(row.Sha256, StringComparison.Ordinal) && l.Contains(row.Md5, StringComparison.Ordinal));
            }

            Assert.Contains(manifest, l => l.StartsWith("\\readme.txt,", StringComparison.Ordinal) && l.Contains("2026-01-15T01:30:00", StringComparison.Ordinal));
            Assert.Single(events, e => e.Action == FileSystemAuditAction.ExportFiles);
        }
        finally
        {
            FileSystemAudit.Sink = null;
        }
    }

    [Fact]
    public void Export_stops_before_writing_when_space_is_short()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT16");
        FsEntry data = FsTestImages.Child(fs, fs.Root, "data.bin");
        string target = Path.Combine(_dir, "small");
        Assert.Throws<ExportSpaceException>(() => FileExporter.Export(fs, [data], target, new FileExportOptions(), freeSpace: _ => 1000));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void Export_keeps_folder_structure_and_unsafe_names_stay_inside_the_folder()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        FsEntry dir1 = FsTestImages.Child(fs, fs.Root, "dir1");
        IReadOnlyList<FileExportRow> rows = FileExporter.Export(fs, [dir1], Path.Combine(_dir, "tree"), new FileExportOptions());
        Assert.Equal(40, rows.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "tree", "dir1", "f001.txt")));
        Assert.Equal("_", FileExporter.SafeName(".."));
        Assert.Equal("a_b", FileExporter.SafeName("a\\b"));
        Assert.Equal("_CON.txt", FileExporter.SafeName("CON.txt"));
        Assert.Equal("x", FileExporter.SafeName("x. "));
    }

    [Fact]
    public void Name_filter_supports_partial_match_and_wildcards()
    {
        Assert.True(new FsNameFilter("read").Matches("README.TXT"));
        Assert.True(new FsNameFilter("*.bin").Matches("data.bin"));
        Assert.False(new FsNameFilter("*.bin").Matches("readme.txt"));
        Assert.True(new FsNameFilter("f00?.txt").Matches("f001.txt"));
        Assert.True(new FsNameFilter(string.Empty).Matches("anything"));
    }

    [Fact]
    public void Lists_are_exported_as_csv_and_json()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT12");
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        string csv = FsListExport.ToCsv(root);
        Assert.Contains("readme.txt,txt,4096,", csv, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"readme.txt\"", FsListExport.ToJson(root), StringComparison.Ordinal);
    }

    // ---- FOR-15 ----

    [Fact]
    [Trait("TC", "TC-FOR-15-04")]
    public void The_reverse_map_is_built_once_and_reused_after_reopening()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        var identity = ClusterOwnerMap.Identity(128L << 20, 0, fs);
        int before = ClusterOwnerMap.BuildCount;
        using (ClusterOwnerMap first = ClusterOwnerMap.LoadOrBuild(_dir, "doc.clustermap.0", identity, fs, out bool built))
        {
            Assert.True(built);
        }

        // 別の位置の問い合わせ・開き直し (再起動の想定) では作らない。
        IFileSystem reopened = FsTestImages.Open("TD-FOR-NTFS");
        using (ClusterOwnerMap second = ClusterOwnerMap.LoadOrBuild(_dir, "doc.clustermap.0", ClusterOwnerMap.Identity(128L << 20, 0, reopened), reopened,
            out bool builtAgain))
        {
            Assert.False(builtAgain);
            Assert.NotEmpty(second.OwnersOf(FileSystemImageSet.Ntfs().Builder.MftLcn));
        }

        Assert.Equal(before + 1, ClusterOwnerMap.BuildCount);

        // データソースの大きさが変われば作り直す。
        using ClusterOwnerMap third = ClusterOwnerMap.LoadOrBuild(_dir, "doc.clustermap.0", ClusterOwnerMap.Identity(1, 0, reopened), reopened, out bool rebuilt);
        Assert.True(rebuilt);
    }

    [Fact]
    public void Clusters_map_to_their_files_including_deleted_ones()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        using ClusterOwnerMap map = ClusterOwnerMap.Build(fs, _dir, "owners");
        (long offset, _) = image.Fragments("\\frag.bin")[1];
        IReadOnlyList<ClusterOwner> owners = map.OwnersOf(offset / 4096 + 3);
        Assert.Equal("\\frag.bin", Assert.Single(owners).Path);
        long deleted = image.Find("\\deleted.txt").Data!.Runs[0].Lcn!.Value;
        ClusterOwner d = Assert.Single(map.OwnersOf(deleted));
        Assert.Equal(ClusterOwnerKind.Deleted, d.Kind);
        Assert.Equal("\\deleted.txt", d.Path);
        Assert.Contains(map.OwnersOf(image.Builder.MftLcn), o => o.Kind == ClusterOwnerKind.Metadata && o.Path == "\\$MFT");
        Assert.Empty(map.OwnersOf(image.Builder.ClusterCount - 10));
    }

    [Fact]
    public void Cluster_expression_and_go_to_cluster_use_the_session()
    {
        (SparseImage img, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        byte[] bytes = new byte[8 << 20];
        img.Read(0, bytes);
        var doc = new Document(new MemoryByteSource(bytes));
        IFileSystem fs = FileSystemRegistry.Open(new SparseImageReader(img));
        var session = new FileSystemSession(doc, fs, 0, img.Length);
        FileSystemSession.Attach(doc, session);
        var editor = new EditorState(doc);
        Assert.Equal(4096, new EditorExpressionContext(editor).ClusterSize);
        Assert.Equal(b.DataOffset, session.ClusterDocumentOffset(2));
        Assert.Equal((2L, 0x10L), session.ClusterAtDocument(b.DataOffset + 0x10));
        Assert.Null(session.ClusterDocumentOffset(fs.ClusterCount + 2));
        FileSystemSession.Attach(doc, null);
        Assert.Null(new EditorExpressionContext(editor).ClusterSize);
    }

    [Fact]
    public void Next_and_previous_fragment()
    {
        (long, long)[] f = [(100, 10), (500, 10), (300, 10)];
        Assert.Equal(300, FragmentNavigation.Next(f, 100));
        Assert.Equal(500, FragmentNavigation.Next(f, 300));
        Assert.Null(FragmentNavigation.Next(f, 500));
        Assert.Equal(300, FragmentNavigation.Previous(f, 500));
        Assert.Null(FragmentNavigation.Previous(f, 100));
    }

    // ---- FOR-16 ----

    [Fact]
    [Trait("TC", "TC-FOR-16-01")]
    public void Ram_slack_and_drive_slack_follow_the_end_of_a_small_file()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        var classifier = new FileSystemRegionClassifier(fs, 0, image.Image.Length)
        {
            Tails = FileTailTable.Build(fs),
        };
        long c = image.Fragments("\\small100.bin")[0].Offset;
        IReadOnlyList<FsRegion> regions = classifier.Classify(c, c + 4096);
        Assert.Equal(
            [new FsRegion(c, 100, FsRegionKind.FileData), new FsRegion(c + 100, 412, FsRegionKind.RamSlack), new FsRegion(c + 512, 3584, FsRegionKind.DriveSlack)],
            regions);

        // 末尾の一覧を作るまではスラックを出さない (仕様 4)。
        classifier.Tails = null;
        Assert.Equal([new FsRegion(c, 4096, FsRegionKind.FileData)], classifier.Classify(c, c + 4096));
    }

    [Fact]
    public void Metadata_unallocated_volume_slack_and_partition_gaps_are_classified()
    {
        IVolumeReader disk = FsTestImages.Reader("TD-FOR-NTFS-FAT32");
        Core.FileSystems.Partitions.PartitionAnalysis partitions = Core.FileSystems.Partitions.PartitionAnalyzer.Analyze(disk, 512);
        long start = FileSystemImageSet.NtfsPartitionStart * 512, length = FileSystemImageSet.NtfsPartitionSectors * 512;
        IFileSystem fs = FileSystemRegistry.Open(disk.Slice(start, length));
        var classifier = new FileSystemRegionClassifier(fs, start, length, partitions, disk.Length);
        Assert.Equal(FsRegionKind.PartitionGap, classifier.Classify(512, 1024).Single().Kind);
        Assert.Equal(FsRegionKind.Metadata, classifier.Classify(start, start + 512).Single().Kind);
        NtfsImage image = FileSystemImageSet.Ntfs(label: "NTFSPART");
        long free = start + (image.Builder.ClusterCount - 5) * 4096;
        Assert.Equal(FsRegionKind.Unallocated, classifier.Classify(free, free + 4096).Single().Kind);

        // 末尾の予備のブートセクタはメタデータ。
        Assert.Equal(FsRegionKind.Metadata, classifier.Classify(start + length - 512, start + length).Single().Kind);
    }

    [Fact]
    [Trait("TC", "TC-FOR-16-04")]
    public void Unallocated_export_size_and_map_point_back_to_the_disk()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        IVolumeReader volume = FsTestImages.Reader("TD-FOR-NTFS");
        IFileSystem fs = FileSystemRegistry.Open(volume);
        string output = Path.Combine(_dir, "unalloc.bin");
        long written = FileSystemExtraction.Write(volume, FileSystemExtraction.UnallocatedRuns(fs, 0), output, writeMap: true);
        Assert.Equal(image.Builder.FreeClusters * 4096, written);
        Assert.Equal(written, new FileInfo(output).Length);
        IReadOnlyList<ExtractMapRow> map = FileSystemExtraction.ReadMap(output + ".map.csv");
        var random = new Random(1604);
        using FileStream exported = File.OpenRead(output);
        for (int i = 0; i < 100; i++)
        {
            long at = (long)(random.NextDouble() * (written - 16));
            long disk = FileSystemExtraction.ToDiskOffset(map, at)!.Value;
            byte[] a = new byte[16];
            exported.Position = at;
            exported.ReadExactly(a);
            Assert.Equal(volume.ReadBytes(disk, 16), a);
        }

        // 仮想ドキュメントとして開く (「未割り当て領域を別タブで開く」)。
        var virtualSource = new ConcatenatedByteSource(volume, FileSystemExtraction.UnallocatedRuns(fs, 0), "unalloc");
        Assert.Equal(written, virtualSource.Length);
        Assert.Equal(map[0].DiskOffset, virtualSource.DiskOffsetOf(0));
    }

    [Fact]
    public void Slack_export_lists_the_file_paths()
    {
        IVolumeReader volume = FsTestImages.Reader("TD-FOR-NTFS");
        IFileSystem fs = FileSystemRegistry.Open(volume);
        FileTailTable tails = FileTailTable.Build(fs, keepPaths: true);
        string output = Path.Combine(_dir, "slack.bin");
        FileSystemExtraction.Write(volume, FileSystemExtraction.SlackRuns(fs, 0, tails), output, writeMap: true);
        IReadOnlyList<ExtractMapRow> map = FileSystemExtraction.ReadMap(output + ".map.csv");
        ExtractMapRow small = Assert.Single(map, r => r.Path == "\\small100.bin");
        Assert.Equal(4096 - 100, small.Length);
        byte[] slack = new byte[small.Length];
        using FileStream s = File.OpenRead(output);
        s.Position = small.ExportOffset;
        s.ReadExactly(slack);
        Assert.All(slack, b => Assert.Equal(0x41, b));
    }

    // ---- FOR-17 ----

    public static TheoryData<string> DeletedImages => new() { "TD-FOR-NTFS", "TD-FOR-FAT32", "TD-FOR-EXFAT" };

    [Theory]
    [MemberData(nameof(DeletedImages))]
    [Trait("TC", "TC-FOR-17-01")]
    public void A_just_deleted_file_is_good_and_exports_its_original_content(string id)
    {
        IFileSystem fs = FsTestImages.Open(id);
        FsEntry deleted = Assert.Single(fs.EnumerateDeleted(), e => e.Name == "deleted.txt" && e.DeletedSource != FsDeletedSource.IndexSlack);
        Assert.Equal(FsRecoveryStatus.Good, deleted.Recovery);
        Assert.Equal("\\deleted.txt", deleted.OriginalPath);
        string folder = Path.Combine(_dir, id);
        IReadOnlyList<FileExportRow> rows = FileExporter.Export(fs, [deleted], folder, new FileExportOptions { IncludeDeleted = true });
        Assert.Equal(FsTestImages.Sha256(FileSet.Deleted), Assert.Single(rows).Sha256);
        string manifest = File.ReadAllText(Path.Combine(folder, FileExporter.ManifestName));
        Assert.Contains(",Good,", manifest, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TC", "TC-FOR-17-02")]
    public void Overwritten_deleted_files_report_overwritten_and_partially_overwritten()
    {
        IFileSystem fs = FileSystemRegistry.Open(new SparseImageReader(FileSystemImageSet.NtfsOverwrite().Image));
        FsEntry[] deleted = [.. fs.EnumerateDeleted()];
        Assert.Equal(FsRecoveryStatus.Overwritten, Assert.Single(deleted, e => e.Name == "gone.bin").Recovery);
        Assert.Equal(FsRecoveryStatus.PartiallyOverwritten, Assert.Single(deleted, e => e.Name == "half.bin").Recovery);
    }

    [Fact]
    public void Fat_deleted_names_come_from_the_lfn_and_assume_contiguous_clusters()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT16");
        FsEntry deleted = Assert.Single(fs.EnumerateDeleted());
        Assert.Equal("deleted.txt", deleted.Name);
        Assert.True(deleted.Flags.HasFlag(FsEntryFlags.AssumedContiguous));
        Assert.Equal(FsDeletedSource.DirectoryEntry, deleted.DeletedSource);
    }

    [Fact]
    public void Resident_deleted_ntfs_files_report_resident()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-NTFS");
        FsEntry victim = Assert.Single(fs.EnumerateDeleted(), e => e.Name == "victim.txt" && e.DeletedSource == FsDeletedSource.MftRecord);
        Assert.Equal(FsRecoveryStatus.Resident, victim.Recovery);
        Assert.Equal("\\dir1\\victim.txt", victim.OriginalPath);
    }

    [Fact]
    public void Virtual_folders_list_metadata_deleted_and_unallocated()
    {
        (SparseImage img, _) = FileSystemImageSet.ExFat();
        var doc = new Document(new MemoryByteSource(new byte[16]));
        IFileSystem fs = FileSystemRegistry.Open(new SparseImageReader(img));
        using var session = new FileSystemSession(doc, fs, 0, img.Length);
        Assert.Contains(session.Children(session.Virtual[FsVirtualFolder.Metadata]), e => e.Name == "$FAT");
        Assert.Contains(session.Children(session.Virtual[FsVirtualFolder.Deleted]), e => e.Name == "deleted.txt");
        FsEntry[] free = [.. session.Children(session.Virtual[FsVirtualFolder.Unallocated])];
        Assert.Equal(fs.Allocation!.CountFree() * fs.ClusterSize, free.Sum(f => f.Size));
        Assert.Empty(session.Children(session.Virtual[FsVirtualFolder.VolumeSlack]));
    }
}
