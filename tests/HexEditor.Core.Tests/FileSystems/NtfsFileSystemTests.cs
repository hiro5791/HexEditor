using System.Text;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-10 NTFS の解析。</summary>
public sealed class NtfsFileSystemTests
{
    private static NtfsFileSystem Open(string id = "TD-FOR-NTFS") => (NtfsFileSystem)FsTestImages.Open(id);

    [Fact]
    public void The_file_set_is_listed_with_names_sizes_times_and_contents()
    {
        NtfsFileSystem fs = Open();
        Assert.Equal(FileSystemKind.Ntfs, fs.Kind);
        Assert.Equal("NTFSTEST", fs.Summary.Label);
        Assert.Empty(fs.Issues);
        FsEntry[] root = [.. fs.ListDirectory(fs.Root)];
        foreach (FileSetItem item in FileSet.Common.Where(i => i.Name != "deleted.txt"))
        {
            FsEntry e = Assert.Single(root, r => r.Name == item.Name);
            Assert.Equal(item.Content.Length, e.Size);
            Assert.Equal(item.ModifiedUtc, e.Modified.Utc);
            Assert.False(e.Flags.HasFlag(FsEntryFlags.Corrupt));
            Assert.Equal(FsTestImages.Sha256(item.Content), FsTestImages.Sha256(FsTestImages.ReadAll(fs.OpenContent(e))));
        }

        // システムファイルはルートの一覧に出さず「$メタデータ」に出す (仕様 10)。
        Assert.DoesNotContain(root, r => r.Name.StartsWith('$'));
        string[] metadata = [.. fs.EnumerateMetadata().Select(m => m.Name)];
        Assert.Contains("$MFT", metadata);
        Assert.Contains("$Bitmap", metadata);
        Assert.Contains("$LogFile", metadata);
        Assert.Contains(root, r => r.Name == "dir1" && r.IsDirectory);
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-01")]
    public void Alternate_data_stream_is_listed_and_readable()
    {
        NtfsFileSystem fs = Open();
        FsEntry ads = FsTestImages.Child(fs, fs.Root, "file.txt:secret");
        Assert.True(ads.Flags.HasFlag(FsEntryFlags.Stream));
        Assert.Equal("secret", ads.StreamName);
        Assert.Equal(FileSet.SecretStream, FsTestImages.ReadAll(fs.OpenContent(ads)));
        Assert.Contains(fs.Describe(ads), d => d.Key == "StreamContent" && d.Value == "TOP SECRET DATA\r\n");
        FsEntry main = FsTestImages.Child(fs, fs.Root, "file.txt");
        Assert.Equal(FileSet.FileTxt, FsTestImages.ReadAll(fs.OpenContent(main)));
        Assert.Single(fs.GetStreams(main));

        // 設定で非表示にできる。
        var hidden = (NtfsFileSystem)FileSystemRegistry.Open(FsTestImages.Reader("TD-FOR-NTFS"), new FileSystemOpenOptions { ShowStreams = false });
        Assert.DoesNotContain(hidden.ListDirectory(hidden.Root), e => e.Name == "file.txt:secret");
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-02")]
    public void A_record_with_a_broken_fixup_is_corrupt_and_the_rest_is_read()
    {
        NtfsFileSystem fs = Open("TD-FOR-NTFS-BADFIXUP");
        FsEntry readme = FsTestImages.Child(fs, fs.Root, "readme.txt");
        Assert.True(readme.Flags.HasFlag(FsEntryFlags.Corrupt));
        Assert.Equal(FsCorruptionKind.FixupMismatch, readme.Corruption);
        Assert.Equal(FileSet.Photo, FsTestImages.ReadAll(fs.OpenContent(FsTestImages.Child(fs, fs.Root, "photo.png"))));
        Assert.Equal(FileSet.Data, FsTestImages.ReadAll(fs.OpenContent(FsTestImages.Child(fs, fs.Root, "data.bin"))));

        // 「MFT レコードを表示」: 適用前の生データを見られる (署名はあり、フィックスアップは不一致)。
        long number = long.Parse(readme.FileIdText);
        NtfsRecord record = fs.ReadRecord(number)!;
        Assert.True(record.HasSignature);
        Assert.False(record.FixupOk);
        Assert.Equal(record.Raw.AsSpan(510, 2).ToArray(), FsTestImages.Reader("TD-FOR-NTFS-BADFIXUP").ReadBytes(record.VolumeOffset + 510, 2));
        Assert.Equal(record.VolumeOffset, readme.MetadataOffset);
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-03")]
    public void Stomped_standard_information_times_are_flagged()
    {
        NtfsFileSystem fs = Open();
        FsEntry stomped = FsTestImages.Child(fs, fs.Root, "stomped.txt");
        Assert.True(stomped.Flags.HasFlag(FsEntryFlags.TimeMismatch));
        IReadOnlyList<FsDetail> details = fs.Describe(stomped);
        Assert.Contains(details, d => d.Key == "SiModified" && d.Value.StartsWith("2000-01-01T00:00:00", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Key == "FnModified" && d.Value.StartsWith("2026-01-15", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Key == "TimeMismatch");
        FsEntry readme = FsTestImages.Child(fs, fs.Root, "readme.txt");
        Assert.False(readme.Flags.HasFlag(FsEntryFlags.TimeMismatch));
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-04")]
    public void A_name_left_in_index_slack_is_a_deleted_entry()
    {
        NtfsFileSystem fs = Open();
        FsEntry[] deleted = [.. fs.EnumerateDeleted()];
        FsEntry victim = Assert.Single(deleted, d => d.DeletedSource == FsDeletedSource.IndexSlack);
        Assert.Equal("victim.txt", victim.Name);
        Assert.Equal("\\dir1\\victim.txt", victim.OriginalPath);
        FsEntry dir1 = FsTestImages.Child(fs, fs.Root, "dir1");
        FsEntry[] live = [.. fs.ListDirectory(dir1)];
        Assert.Equal(40, live.Length);
        Assert.DoesNotContain(live, e => e.Name == "victim.txt");
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-05")]
    public void Compressed_file_is_decompressed_and_its_fragments_show_the_compressed_positions()
    {
        NtfsFileSystem fs = Open();
        FsEntry compressed = FsTestImages.Child(fs, fs.Root, "compressed.bin");
        Assert.True(compressed.Flags.HasFlag(FsEntryFlags.Compressed));
        byte[] content = FsTestImages.ReadAll(fs.OpenContent(compressed));
        Assert.Equal(1 << 20, content.Length);
        Assert.Equal(FsTestImages.Sha256(FileSet.Compressed), FsTestImages.Sha256(content));
        IReadOnlyList<FsExtent> extents = fs.GetExtents(compressed);
        Assert.True(extents.Sum(e => e.DiskLength) < 1 << 20);
        Assert.All(extents, e => Assert.True(e.Compressed));
    }

    [Fact]
    public void Lznt1_decompression_matches_windows()
    {
        var random = new Random(5);
        foreach (int size in new[] { 1, 100, 4096, 4097, 65536 })
        {
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = random.Next(4) == 0 ? (byte)random.Next(256) : (byte)"abcabcabcd"[i % 10];
            }

            byte[] packed = Lznt1.Compress(data);
            byte[] output = new byte[size];
            int n = NtfsFormat.DecompressLznt1(packed, output);
            Assert.Equal(size, n);
            Assert.Equal(data, output);
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-10-06")]
    public void Log_file_restart_area_and_records_are_parsed()
    {
        NtfsFileSystem fs = Open();
        IVolumeReader log = NtfsLogFile.Open(fs)!;
        IReadOnlyList<NtfsRestartArea> restart = NtfsLogFile.ReadRestartAreas(log);
        Assert.Equal(2, restart.Count);
        Assert.All(restart, r => Assert.Equal(NtfsImageBuilder.CurrentLsn, r.CurrentLsn));
        Assert.Equal("NTFS", restart[0].Clients[0].Name);
        NtfsLogRecord[] records = [.. NtfsLogFile.ReadRecords(log, fs.MftRuns, fs.ClusterSize, fs.RecordSize)];
        Assert.Equal(NtfsImageBuilder.LogOperations.Length, records.Length);
        Assert.Equal(NtfsImageBuilder.FirstLsn, records[0].Lsn);
        Assert.Equal("InitializeFileRecordSegment", records[0].RedoName);
        Assert.Equal("Noop", records[0].UndoName);
        Assert.Equal("UpdateResidentValue", records[1].RedoName);
        Assert.All(records.Zip(NtfsImageBuilder.LogOperations), p => Assert.Equal(p.Second.Redo, p.First.RedoOp));
        Assert.NotNull(records[1].TargetRecord);
    }

    [Fact]
    public void Fragmented_file_extents_match_the_image()
    {
        NtfsImage image = FileSystemImageSet.Ntfs();
        NtfsFileSystem fs = Open();
        FsEntry frag = FsTestImages.Child(fs, fs.Root, "frag.bin");
        Assert.Equal(image.Fragments("\\frag.bin"), fs.GetExtents(frag).Select(e => (e.VolumeOffset!.Value, e.Length)));
        Assert.Equal(3, frag.FragmentCount);
    }

    [Fact]
    public void Mft_records_can_be_found_by_number_and_metadata_regions_include_the_mft()
    {
        NtfsFileSystem fs = Open();
        NtfsImage image = FileSystemImageSet.Ntfs();
        Assert.Equal(image.Builder.RecordOffset(0), fs.RecordOffset(0));
        Assert.Contains(fs.MetadataRegions, r => r.Offset == image.Builder.MftLcn * 4096);
        Assert.NotNull(fs.FindById("mft:0"));
        Assert.Equal("\\readme.txt", fs.FindById($"mft:{image.Find("\\readme.txt").Number}")!.Path);
        Assert.Equal(image.Builder.FreeClusters, fs.Allocation!.CountFree());
    }

    [Fact]
    public void Times_out_of_range_do_not_throw()
    {
        NtfsFileSystem fs = Open("TD-FOR-NTFS-BADTIME");
        FsEntry bad = FsTestImages.Child(fs, fs.Root, "badtime.txt");
        Assert.True(bad.Modified.Invalid);
        Assert.Null(bad.Modified.Utc);
        Assert.Equal(Encoding.ASCII.GetBytes("BADTIME!"), FsTestImages.ReadAll(fs.OpenContent(bad)));
    }
}
