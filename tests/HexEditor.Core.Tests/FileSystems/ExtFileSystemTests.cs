using System.Text;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ext;
using HexEditor.Core.Sources;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-12 ext2 / ext3 / ext4 の解析。イメージはテストデータの生成ツール (ExtImageBuilder) がコードで書いたもの。</summary>
public sealed class ExtFileSystemTests
{
    public static TheoryData<string, string> Images => new()
    {
        { "TD-FOR-EXT2", "ext2" },
        { "TD-FOR-EXT3", "ext3" },
        { "TD-FOR-EXT4", "ext4" },
    };

    /// <summary>ディレクトリの木をたどって、すべてのファイル (パス → 項目) を集める。</summary>
    internal static Dictionary<string, FsEntry> AllFiles(IFileSystem fs)
    {
        var files = new Dictionary<string, FsEntry>();
        var queue = new Queue<FsEntry>([fs.Root]);
        while (queue.Count > 0)
        {
            foreach (FsEntry e in fs.ListDirectory(queue.Dequeue()))
            {
                if (e.IsDirectory)
                {
                    queue.Enqueue(e);
                }
                else
                {
                    files[e.Path] = e;
                }
            }
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(Images))]
    [Trait("TC", "TC-FOR-12-01")]
    public void File_list_and_contents_match_the_manifest(string id, string variant)
    {
        IFileSystem fs = FsTestImages.Open(id);
        Assert.Equal(FileSystemKind.Ext, fs.Kind);
        Assert.Equal(variant, fs.Summary.TypeName);
        Assert.Empty(fs.Issues);
        Dictionary<string, FsEntry> files = AllFiles(fs);
        Assert.Equal(FileSystemImageSet.ExtManifest.Select(i => i.Path).Order(StringComparer.Ordinal), files.Keys.Order(StringComparer.Ordinal));
        foreach (FileSetItem item in FileSystemImageSet.ExtManifest)
        {
            FsEntry e = files[item.Path];
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(item.ModifiedUtc, e.Modified.Utc);
            Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
            Assert.Equal(FsTestImages.Sha256(item.Content), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(e))));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-12-01")]
    public void Ext4_shows_64bit_flex_bg_and_extent_features()
    {
        var fs = (ExtFileSystem)FsTestImages.Open("TD-FOR-EXT4");
        Assert.Contains("64bit", fs.FeatureNames);
        Assert.Contains("flex_bg", fs.FeatureNames);
        Assert.Contains("extent", fs.FeatureNames);
        Assert.Contains("metadata_csum", fs.FeatureNames);
        Assert.Equal(64, fs.Superblock.DescriptorSize);
        Assert.Equal(4, fs.Superblock.GroupCount);
        Assert.True(fs.Superblock.ChecksumValid);
        Assert.Contains(fs.Summary.Details, d => d.Key == "FeaturesIncompat" && d.Value.Contains("64bit", StringComparison.Ordinal)
            && d.Value.Contains("flex_bg", StringComparison.Ordinal) && d.Value.Contains("extent", StringComparison.Ordinal));
        Assert.Equal(new Guid("00000000-0000-0000-0000-0000000000E4").ToString("D"), fs.Summary.Serial);

        // flex_bg: すべてのグループの inode テーブルがグループ 0 にある。
        for (int g = 0; g < 4; g++)
        {
            Assert.True(fs.Group(g).InodeTable < 4096);
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-12-02")]
    public void Deleted_file_is_found_in_the_directory_entry_slack()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-EXT2");
        FsEntry[] deleted = [.. fs.EnumerateDeleted()];
        FsEntry e = Assert.Single(deleted, d => d.Name == "deleted.txt");
        Assert.Equal("\\deleted.txt", e.OriginalPath);
        Assert.Equal(FsDeletedSource.DirectorySlack, e.DeletedSource);
        Assert.Equal(FsEntryState.Deleted, e.State);

        // ext2 は削除しても位置情報が残るので、内容を読める (ブロックは空きに戻っている)。
        Assert.Equal(FileSet.Deleted, FsTestImages.ReadAll(fs.OpenContent(e)));
        Assert.Equal(FsRecoveryStatus.Good, e.Recovery);
        Assert.DoesNotContain(fs.ListDirectory(fs.Root), r => r.Name == "deleted.txt");
    }

    [Theory]
    [InlineData("TD-FOR-EXT3")]
    [InlineData("TD-FOR-EXT4")]
    public void Deleted_file_on_ext3_and_ext4_has_an_unknown_location(string id)
    {
        IFileSystem fs = FsTestImages.Open(id);
        FsEntry e = Assert.Single(fs.EnumerateDeleted(), d => d.Name == "deleted.txt");
        Assert.True(e.Flags.HasFlag(FsEntryFlags.LocationUnknown));
        Assert.Equal(FsRecoveryStatus.Unknown, e.Recovery);
        Assert.Equal(0, fs.OpenContent(e).Length);
    }

    [Fact]
    public void Htree_directory_lists_all_leaf_entries_without_false_deleted_names()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-EXT4");
        FsEntry dir1 = FsTestImages.Child(fs, fs.Root, "dir1");
        FsEntry[] list = [.. fs.ListDirectory(dir1)];
        Assert.Equal(FileSystemImageSet.ExtDir1Files, list.Length);
        Assert.Equal(Enumerable.Range(1, FileSystemImageSet.ExtDir1Files).Select(i => $"f{i:D3}.txt"), list.Select(e => e.Name));

        // dx_root の中身を削除された名前と誤らない。
        Assert.DoesNotContain(fs.EnumerateDeleted(), e => e.OriginalPath.StartsWith("\\dir1\\", StringComparison.Ordinal));
    }

    [Fact]
    public void Fragmented_file_has_three_extents_and_free_gaps_between_them()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-EXT4");
        (_, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        FsEntry frag = FsTestImages.Child(fs, fs.Root, "frag.bin");
        IReadOnlyList<FsExtent> extents = fs.GetExtents(frag);
        Assert.Equal(3, extents.Count);
        Assert.Equal(b.Nodes.First(n => n.Name == "frag.bin").Runs.Select(r => r.Physical), extents.Select(e => e.StartCluster));
        IAllocationMap map = fs.Allocation!;
        Assert.True(map.IsAllocated(extents[0].StartCluster));
        Assert.False(map.IsAllocated(extents[0].StartCluster + extents[0].ClusterCount));
        Assert.Equal(extents[0].StartCluster, frag.FirstCluster);
    }

    [Theory]
    [InlineData(ExtVariant.Ext2)]
    [InlineData(ExtVariant.Ext4)]
    public void Indirect_blocks_and_extent_trees_with_index_nodes_are_read(ExtVariant variant)
    {
        // ext2: 5 MiB のファイルは二重間接ブロックを使う。ext4: 6 個の断片は深さ 1 のエクステントツリーになる。
        var b = new ExtImageBuilder(variant, 32 * FileSystemImageSet.MiB, Guid.NewGuid(), "BIG", blocksPerGroup: 8192);
        byte[] big = new byte[5 * 1024 * 1024];
        new Random(5).NextBytes(big);
        b.AddFile(b.Root, "big.bin", big, FileSet.TimeOf(1), fragments: variant == ExtVariant.Ext4 ? 6 : 1, gapBlocks: 3);
        var fs = (ExtFileSystem)FileSystemRegistry.Open(new SparseImageReader(b.Build()));
        FsEntry e = FsTestImages.Child(fs, fs.Root, "big.bin");
        Assert.Equal(FsEntryFlags.None, e.Flags & FsEntryFlags.Corrupt);
        Assert.Equal(big, FsTestImages.ReadAll(fs.OpenContent(e)));
        Assert.Contains(fs.Describe(e), d => d.Key == "DataMapping" && d.Value == (variant == ExtVariant.Ext4 ? "extents (depth 1)" : "indirect"));
        Assert.Equal(variant == ExtVariant.Ext4 ? 6 : 1, fs.GetExtents(e).Count);
    }

    [Fact]
    public void Inline_data_and_fast_symlinks_are_resident()
    {
        var b = new ExtImageBuilder(ExtVariant.Ext4, 16 * FileSystemImageSet.MiB, Guid.NewGuid(), "INLINE", inlineData: true);
        b.AddFile(b.Root, "tiny.txt", Encoding.ASCII.GetBytes("inline!"), FileSet.TimeOf(1), inline: true);
        b.AddSymlink(b.Root, "link", "tiny.txt", FileSet.TimeOf(2));
        var fs = (ExtFileSystem)FileSystemRegistry.Open(new SparseImageReader(b.Build()));
        FsEntry tiny = FsTestImages.Child(fs, fs.Root, "tiny.txt");
        Assert.True(tiny.Flags.HasFlag(FsEntryFlags.Resident));
        Assert.Equal("inline!"u8.ToArray(), FsTestImages.ReadAll(fs.OpenContent(tiny)));
        FsExtent extent = Assert.Single(fs.GetExtents(tiny));
        Assert.True(extent.Resident);
        Assert.Equal(fs.InodeOffset(b.Nodes.First(n => n.Name == "tiny.txt").Inode) + 0x28, extent.VolumeOffset);

        FsEntry link = FsTestImages.Child(fs, fs.Root, "link");
        Assert.StartsWith("l", link.Attributes, StringComparison.Ordinal);
        Assert.Contains(fs.Describe(link), d => d.Key == "SymlinkTarget" && d.Value == "tiny.txt");
    }

    [Fact]
    public void Journal_blocks_are_listed_without_replay()
    {
        var fs = (ExtFileSystem)FsTestImages.Open("TD-FOR-EXT3");
        ExtJournal journal = ExtJournal.Read(fs)!;
        Assert.Equal(ExtJournalBlockType.SuperblockV2, journal.Type);
        Assert.Equal(4096, journal.BlockSize);
        Assert.Equal(1024u, journal.MaxLength);
        ExtJournalBlock[] blocks = [.. journal.Blocks()];
        Assert.Equal(
            [ExtJournalBlockType.SuperblockV2, ExtJournalBlockType.Descriptor, ExtJournalBlockType.Commit, ExtJournalBlockType.Revoke, ExtJournalBlockType.Commit],
            blocks.Select(x => x.Type));
        Assert.Equal([0u, 1u, 1u, 2u, 2u], blocks.Select(x => x.Sequence));
        Assert.All(blocks, x => Assert.NotNull(x.VolumeOffset));
        Assert.Contains(fs.EnumerateMetadata(), m => m.Name == "$Journal");
    }

    [Fact]
    public void Metadata_checksum_mismatches_are_warned_and_marked()
    {
        (SparseImage image, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        int readme = b.Nodes.First(n => n.Name == "readme.txt").Inode;
        var patches = new Dictionary<long, byte>
        {
            [1024 + 0x30] = 0x5A, // スーパーブロックの s_wtime
            [b.InodeOffset(readme) + 0x08] = 0x77, // readme.txt の atime
        };
        IFileSystem fs = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), patches));
        Assert.Contains(fs.Issues, i => i.Kind == FsIssueKind.ExtSuperblockChecksum);
        FsEntry e = FsTestImages.Child(fs, fs.Root, "readme.txt");
        Assert.Equal(FsCorruptionKind.ChecksumMismatch, e.Corruption);
        Assert.True(e.Flags.HasFlag(FsEntryFlags.Corrupt));

        // 内容は読める (印を付けて続ける)。
        Assert.Equal(FileSet.Readme, FsTestImages.ReadAll(fs.OpenContent(e)));
    }

    [Fact]
    public void Bad_extent_magic_marks_the_file_corrupt_and_continues()
    {
        (SparseImage image, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        int data = b.Nodes.First(n => n.Name == "data.bin").Inode;
        IFileSystem fs = FileSystemRegistry.Open(new PatchedImageReader(new SparseImageReader(image), new Dictionary<long, byte> { [b.InodeOffset(data) + 0x28] = 0 }));
        FsEntry e = FsTestImages.Child(fs, fs.Root, "data.bin");
        Assert.True(e.Flags.HasFlag(FsEntryFlags.Corrupt));
        Assert.Empty(fs.GetExtents(e));
        Assert.Equal(FileSet.Readme, FsTestImages.ReadAll(fs.OpenContent(FsTestImages.Child(fs, fs.Root, "readme.txt"))));
    }

    [Fact]
    public void Block_bitmap_marks_free_space_and_uninitialized_groups()
    {
        var fs = (ExtFileSystem)FsTestImages.Open("TD-FOR-EXT4");
        IAllocationMap map = fs.Allocation!;
        FsEntry contig = FsTestImages.Child(fs, fs.Root, "contig.bin");
        Assert.All(fs.GetExtents(contig), e => Assert.True(map.IsAllocated(e.StartCluster)));

        // グループ 1 は BLOCK_UNINIT: 予備のスーパーブロックだけが使用中。
        Assert.Equal(ExtGroupDescriptor.BlockUninit, fs.Group(1).Flags & ExtGroupDescriptor.BlockUninit);
        Assert.True(map.IsAllocated(4096));
        Assert.False(map.IsAllocated(4096 + 100));
        long free = map.CountFree();
        Assert.Equal(fs.Superblock.FreeBlocks, free);
    }

    [Fact]
    public void Inode_offset_and_lookup_by_number()
    {
        var fs = (ExtFileSystem)FsTestImages.Open("TD-FOR-EXT4");
        (_, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        int readme = b.Nodes.First(n => n.Name == "readme.txt").Inode;
        Assert.Equal(b.InodeOffset(readme), fs.InodeOffset(readme));
        FsEntry e = fs.EntryForInode(readme)!;
        Assert.Equal(FileSet.Readme.Length, e.Size);
        Assert.Null(fs.InodeOffset(0));
        Assert.Null(fs.InodeOffset(fs.Superblock.InodesCount + 1));
        Assert.Contains(fs.Describe(e), d => d.Key == "InodeChecksum" && d.Value == "OK");
        Assert.Empty(fs.EnumerateOrphans());
    }

    [Fact]
    public void Times_have_nanoseconds_and_detection_uses_the_superblock()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-EXT4");
        FsEntry photo = FsTestImages.Child(fs, fs.Root, "photo.png");
        Assert.Equal(FileSet.TimeOf(1), photo.Modified.Utc);
        Assert.Equal(FileSet.TimeOf(1), photo.Created.Utc);
        Assert.Equal(FileSystemKind.Ext, FileSystemDetector.Detect(FsTestImages.Reader("TD-FOR-EXT4"))!.Kind);
        Assert.Equal(64 * FileSystemImageSet.MiB, FileSystemDetector.Detect(FsTestImages.Reader("TD-FOR-EXT4"))!.EstimatedLength);
    }
}

/// <summary>元のイメージの一部のバイトを変えて読む (壊したイメージのテスト)。</summary>
public sealed class PatchedImageReader(IVolumeReader inner, IReadOnlyDictionary<long, byte> patches) : IVolumeReader
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
