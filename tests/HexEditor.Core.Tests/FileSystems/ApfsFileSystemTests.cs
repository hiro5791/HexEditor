using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Apple;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-13 APFS の基本解析。イメージはテストデータの生成ツール (ApfsImageBuilder) がコードで書いたもの。</summary>
public sealed class ApfsFileSystemTests
{
    [Fact]
    [Trait("TC", "TC-FOR-13-02")]
    public void Unencrypted_volume_lists_files_matching_the_manifest()
    {
        var fs = (ApfsFileSystem)FsTestImages.Open("TD-FOR-APFS");
        Assert.Equal(FileSystemKind.Apfs, fs.Kind);
        Assert.Equal(2ul, fs.Xid);
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        Assert.Equal("Data", volume.Name);
        Assert.False(volume.Flags.HasFlag(FsEntryFlags.Encrypted));
        ApfsVolume v = Assert.Single(fs.Volumes);
        Assert.Equal("Data", v.RoleName);
        Assert.False(v.Encrypted);
        Assert.Contains(fs.Describe(volume), d => d.Key == "Encrypted" && d.Value == "no");

        FsEntry[] files = [.. fs.ListDirectory(volume).Where(e => !e.Flags.HasFlag(FsEntryFlags.Virtual))];
        IReadOnlyList<FileSetItem> manifest = [.. FileSet.Common.Where(i => i.Name != "deleted.txt")];
        Assert.Equal(manifest.Select(i => i.Name).Order(StringComparer.Ordinal), files.Select(f => f.Name).Order(StringComparer.Ordinal));
        foreach (FileSetItem item in manifest)
        {
            FsEntry e = files.Single(f => f.Name == item.Name);
            Assert.Equal("\\Data\\" + item.Name, e.Path);
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(item.ModifiedUtc, e.Modified.Utc);
            Assert.Equal(FsTestImages.Sha256(item.Content), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(e))));
            Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
        }

        Assert.Equal(3, fs.GetExtents(files.Single(f => f.Name == "frag.bin")).Count);
        Assert.Empty(fs.Issues);
    }

    [Fact]
    [Trait("TC", "TC-FOR-13-03")]
    public void Encrypted_volume_is_detected_and_not_listed()
    {
        var fs = (ApfsFileSystem)FsTestImages.Open("TD-FOR-APFS-ENC");
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        Assert.Equal("Secret", volume.Name);
        Assert.True(volume.Flags.HasFlag(FsEntryFlags.Encrypted));
        Assert.True(Assert.Single(fs.Volumes).Encrypted);
        Assert.Empty(fs.ListDirectory(volume));
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.Encrypted && i.Detail == "Secret");
        Assert.Contains(fs.Describe(volume), d => d.Key == "Encrypted" && d.Value == "yes");

        // 暗号化されたボリュームのツリーは読まない (乱数のブロックのチェックサムの警告が出ない)。
        Assert.DoesNotContain(fs.Issues, i => i.Kind == FsIssueKind.ApfsChecksum);
    }

    [Fact]
    public void Snapshots_are_listed_only()
    {
        var fs = (ApfsFileSystem)FsTestImages.Open("TD-FOR-APFS");
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        FsEntry folder = Assert.Single(fs.ListDirectory(volume), e => e.Flags.HasFlag(FsEntryFlags.Virtual));
        FsEntry snapshot = Assert.Single(fs.ListDirectory(folder));
        Assert.Equal("snap-2026-01-15", snapshot.Name);
        Assert.Equal(FileSet.ReadmeModifiedUtc.AddHours(1), snapshot.Created.Utc);
        Assert.Empty(fs.GetExtents(snapshot));
        Assert.Single(fs.Snapshots(fs.Volumes[0]));
    }

    [Fact]
    public void A_torn_newest_checkpoint_falls_back_to_the_previous_one()
    {
        (SparseImage image, _) = FileSystemImageSet.Apfs(torn: true);
        var fs = (ApfsFileSystem)FileSystemRegistry.Open(new SparseImageReader(image));
        Assert.Equal(2ul, fs.Xid);
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.ApfsOlderCheckpoint);
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        Assert.Contains(fs.ListDirectory(volume), e => e.Name == "photo.png");
    }

    [Fact]
    public void No_valid_checkpoint_is_reported()
    {
        (SparseImage image, _) = FileSystemImageSet.Apfs();
        var patches = new Dictionary<long, byte> { [100] = 0xEE, [2 * 4096 + 100] = 0xEE, [4 * 4096 + 100] = 0xEE };
        FileSystemOpenException ex = Assert.Throws<FileSystemOpenException>(() =>
            FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), patches)));
        Assert.Equal("Fs_ApfsNoCheckpoint", ex.MessageKey);
    }

    [Fact]
    public void Corrupt_tree_nodes_are_warned_and_skipped()
    {
        (SparseImage image, _) = FileSystemImageSet.Apfs();
        IFileSystem fs = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), new Dictionary<long, byte> { [44L * 4096 + 300] = 0x99 }));
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        _ = fs.ListDirectory(volume).ToList();
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.ApfsChecksum && i.Detail == "44");
    }

    [Fact]
    public void Space_manager_marks_used_blocks()
    {
        var fs = (ApfsFileSystem)FsTestImages.Open("TD-FOR-APFS");
        IAllocationMap map = fs.Allocation!;
        FsEntry volume = Assert.Single(fs.ListDirectory(fs.Root));
        FsEntry data = FsTestImages.Child(fs, volume, "data.bin");
        Assert.All(fs.GetExtents(data), e => Assert.True(map.IsAllocated(e.StartCluster)));
        Assert.True(map.IsAllocated(0));
        Assert.False(map.IsAllocated(fs.ClusterCount - 10));
        Assert.True(fs.UsesBlocks);
    }
}
