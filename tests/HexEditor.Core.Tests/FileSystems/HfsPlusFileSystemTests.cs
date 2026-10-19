using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Apple;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-13 HFS+ の解析。イメージはテストデータの生成ツール (HfsPlusImageBuilder) がコードで書いたもの。</summary>
public sealed class HfsPlusFileSystemTests
{
    private static HfsPlusFileSystem Open() => (HfsPlusFileSystem)FsTestImages.Open("TD-FOR-HFSPLUS");

    [Fact]
    [Trait("TC", "TC-FOR-13-01")]
    public void Data_and_resource_forks_are_listed_and_readable()
    {
        HfsPlusFileSystem fs = Open();
        Assert.Equal(FileSystemKind.HfsPlus, fs.Kind);
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        FsEntry data = Assert.Single(root, e => e.Name == "forked.txt");
        FsEntry rsrc = Assert.Single(root, e => e.Name == "forked.txt/..namedfork/rsrc");
        Assert.True(rsrc.Flags.HasFlag(FsEntryFlags.Stream));
        Assert.Equal("\\forked.txt/..namedfork/rsrc", rsrc.Path);
        Assert.Equal("DATA-FORK\n"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(data)));
        Assert.Equal("RSRC-FORK\n"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(rsrc)));
        Assert.Single(fs.GetStreams(data));
    }

    [Fact]
    public void Volume_header_alternate_and_summary()
    {
        HfsPlusFileSystem fs = Open();
        Assert.Equal("HFSTEST", fs.Summary.Label);
        Assert.Equal("HFS+", fs.Summary.TypeName);
        Assert.DoesNotContain(fs.Issues, i => i.Kind == FsIssueKind.HfsAlternateHeaderMismatch);
        Assert.True(fs.Journaled);
        Assert.NotNull(fs.Journal());
        Assert.Equal(0x4A4E4C78u, fs.Journal()!.Magic);
        Assert.Contains(fs.EnumerateMetadata(), m => m.Name == "$Journal");
        Assert.Contains(fs.Summary.Details, d => d.Key == "JournalSize");

        // 予備のボリュームヘッダーを変えると警告になる。
        (SparseImage image, _) = FileSystemImageSet.HfsPlus();
        long alt = image.Length - 1024;
        IFileSystem broken = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), new Dictionary<long, byte> { [alt + 47] = 0x01 }));
        Assert.Contains(broken.Issues, i => i.Kind == FsIssueKind.HfsAlternateHeaderMismatch && i.Detail.Contains("totalBlocks", StringComparison.Ordinal));
    }

    [Fact]
    public void Files_match_the_file_set_and_catalog_spans_several_leaves()
    {
        HfsPlusFileSystem fs = Open();
        (_, HfsPlusImageBuilder b) = FileSystemImageSet.HfsPlus();
        Assert.True(b.CatalogDepth >= 2);
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        foreach (FileSetItem item in FileSet.Common.Where(i => i.Name != "deleted.txt"))
        {
            FsEntry e = Assert.Single(root, r => r.Name == item.Name);
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(FileSystemImageSet.WholeSeconds(item.ModifiedUtc), e.Modified.Utc);
            Assert.Equal(FsTestImages.Sha256(item.Content), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(e))));
        }

        Assert.DoesNotContain(root, e => e.Name.Contains("HFS+ Private", StringComparison.Ordinal));
        FsEntry dir1 = FsTestImages.Child(fs, fs.Root, "dir1");
        Assert.Equal(40, fs.ListDirectory(dir1).Count());
        Assert.Equal("file-040\r\n"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(FsTestImages.Child(fs, dir1, "f040.txt"))));
        Assert.Equal(3, fs.GetExtents(FsTestImages.Child(fs, fs.Root, "frag.bin")).Count);
    }

    [Fact]
    public void Fragments_beyond_eight_come_from_the_extents_overflow_tree()
    {
        HfsPlusFileSystem fs = Open();
        FsEntry e = FsTestImages.Child(fs, fs.Root, "manyfrag.bin");
        Assert.Equal(10, fs.GetExtents(e).Count);
        Assert.Equal(FileSystemImageSet.HfsManyFrag, FsTestImages.ReadAll(fs.OpenContent(e)));
    }

    [Fact]
    public void Hard_links_resolve_to_the_inode_file()
    {
        HfsPlusFileSystem fs = Open();
        foreach (string name in new[] { "link1.txt", "link2.txt" })
        {
            FsEntry link = FsTestImages.Child(fs, fs.Root, name);
            Assert.Equal(FileSystemImageSet.HfsLinkTarget.Length, link.Size);
            Assert.Equal(FileSystemImageSet.HfsLinkTarget, FsTestImages.ReadAll(fs.OpenContent(link)));
            Assert.Contains(fs.Describe(link), d => d.Key == "HardLinkTarget" && d.Value.StartsWith("iNode", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Compressed_files_are_marked_and_left_raw()
    {
        HfsPlusFileSystem fs = Open();
        FsEntry e = FsTestImages.Child(fs, fs.Root, "compressed.txt");
        Assert.True(e.Flags.HasFlag(FsEntryFlags.Compressed));
        Assert.Contains(fs.Describe(e), d => d.Key == "Compression" && d.Value == "decmpfs");
        Assert.Equal(0, fs.OpenContent(e).Length);
    }

    [Fact]
    public void A_broken_catalog_node_marks_the_folder_and_continues()
    {
        (SparseImage image, HfsPlusImageBuilder b) = FileSystemImageSet.HfsPlus();
        long catalog = (long)b.CatalogStart * b.BlockSize;

        // 最初の葉のノード (ノード 1。ルートの中身がある) の記録の数を壊す。
        long node = catalog + HfsPlusImageBuilder.CatalogNodeSize;
        var patches = new Dictionary<long, byte> { [node + 10] = 0xFF, [node + 11] = 0xFF };
        IFileSystem fs = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), patches));
        _ = fs.ListDirectory(fs.Root).ToList();
        Assert.True(fs.Root.Flags.HasFlag(FsEntryFlags.Corrupt));
        Assert.Contains("catalog node 1", fs.Root.CorruptionDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocation_file_marks_used_blocks()
    {
        HfsPlusFileSystem fs = Open();
        IAllocationMap map = fs.Allocation!;
        Assert.True(map.IsAllocated(0));
        FsEntry data = FsTestImages.Child(fs, fs.Root, "data.bin");
        Assert.All(fs.GetExtents(data), x => Assert.True(map.IsAllocated(x.StartCluster)));
        Assert.False(map.IsAllocated(fs.ClusterCount - 100));
        Assert.True(map.IsAllocated(fs.ClusterCount - 1));
        Assert.Equal(fs.Summary.FreeClusters, map.CountFree());
        Assert.Equal("-rw-r--r--", data.Attributes);
    }
}
