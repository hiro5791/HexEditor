using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Fat;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-11 FAT12 / FAT16 / FAT32 / exFAT の解析。</summary>
public sealed class FatFileSystemTests
{
    public static TheoryData<string, FileSystemKind> Images => new()
    {
        { "TD-FOR-FAT12", FileSystemKind.Fat12 },
        { "TD-FOR-FAT16", FileSystemKind.Fat16 },
        { "TD-FOR-FAT32", FileSystemKind.Fat32 },
        { "TD-FOR-EXFAT", FileSystemKind.ExFat },
    };

    [Theory]
    [MemberData(nameof(Images))]
    [Trait("TC", "TC-FOR-11-01")]
    public void Types_are_detected_and_long_names_are_listed(string id, FileSystemKind kind)
    {
        IFileSystem fs = FsTestImages.Open(id);
        Assert.Equal(kind, fs.Kind);
        Assert.Equal(kind, FileSystemDetector.Detect(FsTestImages.Reader(id))!.Kind);
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        foreach (FileSetItem item in FileSet.Common.Where(i => i.Name != "deleted.txt"))
        {
            FsEntry e = Assert.Single(root, r => r.Name == item.Name);
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
            Assert.Equal(item.Content, FsTestImages.ReadAll(fs.OpenContent(e)));
            Assert.Equal("\\" + item.Name, e.Path);
        }

        Assert.Contains(root, r => r.Name == FileSet.LongName);
        Assert.DoesNotContain(root, r => r.Name.Contains("deleted", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Images))]
    public void Modified_times_are_recorded_without_a_zone(string id, FileSystemKind kind)
    {
        _ = kind;
        IFileSystem fs = FileSystemRegistry.Open(FsTestImages.Reader(id), new FileSystemOpenOptions { LocalOffset = FileSet.FatZone });
        FsEntry readme = FsTestImages.Child(fs, fs.Root, "readme.txt");
        Assert.True(readme.Modified.ZoneUnknown);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0), readme.Modified.Recorded);
        Assert.Equal(FileSet.ReadmeModifiedUtc, readme.Modified.Utc);
    }

    [Fact]
    public void Fragmented_files_have_three_extents_at_the_built_positions()
    {
        (SparseImage _, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT32");
        FsEntry frag = FsTestImages.Child(fs, fs.Root, "frag.bin");
        IReadOnlyList<FsExtent> extents = fs.GetExtents(frag);
        Assert.Equal(3, extents.Count);
        Assert.Equal(b.Files["frag.bin"].Fragments, extents.Select(e => (e.VolumeOffset!.Value, e.Length)));
        Assert.Equal(3, frag.FragmentCount);
        Assert.Equal(b.Files["frag.bin"].FirstCluster, frag.FirstCluster);
    }

    [Fact]
    [Trait("TC", "TC-FOR-11-02")]
    public void Differing_fats_are_counted_and_the_fat_can_be_switched()
    {
        var fs = (FatFileSystem)FsTestImages.Open("TD-FOR-FAT32-FATDIFF");
        Assert.Equal(10, fs.CountFatDifferences());
        FsEntry data = FsTestImages.Child(fs, fs.Root, "data.bin");
        Assert.Equal(256, fs.ChainOf(data).Clusters.Count);
        fs.SelectFat(1);
        FatTable.Chain second = fs.ChainOf(data);
        Assert.Equal(2, second.Clusters.Count);
        fs.SelectFat(0);
        Assert.Equal(256, fs.ChainOf(data).Clusters.Count);
    }

    [Fact]
    [Trait("TC", "TC-FOR-11-03")]
    public async Task A_looping_chain_is_marked_corrupt_without_hanging()
    {
        (FsEntry frag, IFileSystem fs) = await Task.Run(() =>
        {
            IFileSystem f = FsTestImages.Open("TD-FOR-FAT32-LOOP");
            return (FsTestImages.Child(f, f.Root, "frag.bin"), f);
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(frag.Flags.HasFlag(FsEntryFlags.Corrupt));
        Assert.Equal(FsCorruptionKind.ChainLoop, frag.Corruption);
        IVolumeReader content = fs.OpenContent(frag);
        Assert.True(content.Length <= 100 * fs.ClusterSize, $"length {content.Length}");
    }

    [Fact]
    [Trait("TC", "TC-FOR-11-04")]
    public void Exfat_no_fat_chain_file_is_read_as_contiguous_clusters()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-EXFAT");
        FsEntry contig = FsTestImages.Child(fs, fs.Root, "contig.bin");
        Assert.True(contig.Flags.HasFlag(FsEntryFlags.NoFatChain));
        Assert.Contains(fs.Describe(contig), d => d.Key == "NoFatChain" && d.Value == "1");
        Assert.Equal(FsTestImages.Sha256(FileSet.Contig), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(contig))));
    }

    [Fact]
    public void Exfat_boot_checksums_and_special_entries_are_read()
    {
        var fs = (ExFatFileSystem)FsTestImages.Open("TD-FOR-EXFAT");
        Assert.True(fs.MainChecksumOk);
        Assert.True(fs.BackupChecksumOk);
        Assert.Empty(fs.Issues);
        Assert.Equal("EXFAT", fs.Summary.Label);
        Assert.NotNull(fs.Allocation);
        Assert.Contains(fs.EnumerateMetadata(), m => m.Name == "$AllocationBitmap");
    }

    [Fact]
    public void Fat32_summary_has_the_bpb_and_fsinfo()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT32");
        FileSystemSummary s = fs.Summary;
        Assert.Equal("FAT32", s.Label);
        Assert.Equal(4096, s.ClusterSize);
        Assert.Contains(s.Details, d => d.Key == "FsInfoFreeClusters");
        Assert.Empty(fs.Issues);
        Assert.Equal(2, fs.FirstClusterNumber);
        (SparseImage _, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        Assert.Equal(b.DataOffset, fs.ClusterOffset(2));
        Assert.Equal(2, fs.ClusterAt(b.DataOffset + 5));
        Assert.Null(fs.ClusterOffset(fs.ClusterCount + 2));
    }

    [Fact]
    public void Fat_metadata_files_are_listed()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT16");
        string[] names = [.. fs.EnumerateMetadata().Select(m => m.Name)];
        Assert.Equal(["$BootSector", "$FAT1", "$FAT2", "$RootDirectory"], names);
    }
}
