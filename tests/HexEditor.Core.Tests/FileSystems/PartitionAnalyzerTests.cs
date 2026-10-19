using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-06 パーティション解析と表示。</summary>
public sealed class PartitionAnalyzerTests
{
    private static PartitionAnalysis Analyze(string id, int sectorSize = 512, bool small = false) =>
        PartitionAnalyzer.Analyze(FsTestImages.Reader(id), sectorSize, new PartitionAnalyzerOptions { ShowSmallGaps = small });

    [Fact]
    [Trait("TC", "TC-FOR-06-01")]
    public void Mbr_primary_and_logical_partitions_have_the_defined_start_lbas()
    {
        PartitionAnalysis a = Analyze("TD-FOR-MBR-EXT");
        PartitionRow[] mbr = [.. a.Rows.Where(r => r.Scheme == PartitionScheme.Mbr)];
        PartitionRow[] logical = [.. a.Rows.Where(r => r.Scheme == PartitionScheme.Logical)];
        Assert.Equal(4, mbr.Length);
        Assert.Equal(5, logical.Length);
        for (int i = 0; i < 4; i++)
        {
            (byte type, long start, long count) = PartitionImages.MbrExtPrimary[i];
            PartitionRow row = mbr.Single(r => r.Number == i + 1);
            Assert.Equal(type.ToString("X2"), row.TypeCode);
            Assert.Equal(start, row.StartLba);
            Assert.Equal(count, row.SectorCount);
            Assert.Equal(start + count - 1, row.EndLba);
        }

        Assert.True(mbr.Single(r => r.Number == 4).IsContainer);
        Assert.Equal(PartitionImages.MbrExtLogicalStarts, logical.Select(r => r.StartLba));
        Assert.All(logical, r => Assert.Equal(32768, r.SectorCount));
        Assert.All(logical, r => Assert.Equal("07", r.TypeCode));
        Assert.Equal(0x12345678u, a.DiskSignature);
        Assert.Empty(a.Issues);
        Assert.All(a.Rows.Where(r => !r.IsUnallocated), r => Assert.False(r.Warning));
    }

    [Fact]
    [Trait("TC", "TC-FOR-06-02")]
    public async Task A_looping_ebr_chain_stops_with_a_warning()
    {
        PartitionAnalysis a = await Task.Run(() => Analyze("TD-FOR-MBR-EBRLOOP")).WaitAsync(TimeSpan.FromSeconds(5));
        PartitionRow[] logical = [.. a.Rows.Where(r => r.Scheme == PartitionScheme.Logical)];
        Assert.Equal(3, logical.Length);
        PartitionIssue loop = Assert.Single(a.Issues, i => i.Kind == PartitionIssueKind.EbrLoop);
        Assert.NotNull(loop.Row);
        Assert.Contains(logical, r => r.Warning && r.Number == loop.Row);
    }

    [Fact]
    [Trait("TC", "TC-FOR-06-03")]
    public void Gpt_entries_are_named_and_a_changed_entry_array_breaks_the_crc()
    {
        PartitionAnalysis good = Analyze("TD-FOR-GPT");
        PartitionRow[] parts = [.. good.Partitions.Where(r => r.Scheme == PartitionScheme.Gpt)];
        Assert.Equal(3, parts.Length);
        Assert.Equal(["EfiSystem", "WindowsBasicData", "LinuxFileSystem"], parts.Select(p => p.TypeKey));
        Assert.Equal(["EFI", "Data", "Linux"], parts.Select(p => p.Name));
        Assert.Equal([2048L, 4096, 36864], parts.Select(p => p.StartLba));
        Assert.Equal([4095L, 36863, 69631], parts.Select(p => p.EndLba));
        Assert.Equal(PartitionImages.Numbered(2), parts[1].UniqueGuid);
        Assert.All(parts, p => Assert.False(p.Warning));
        Assert.Empty(good.Issues);
        Assert.Equal(PartitionImages.GptDisk, good.DiskGuid);

        PartitionAnalysis bad = Analyze("TD-FOR-GPT-BADCRC");
        Assert.Contains(bad.Issues, i => i.Kind == PartitionIssueKind.GptEntriesCrc);
    }

    [Fact]
    [Trait("TC", "TC-FOR-06-04")]
    public void Only_the_backup_header_is_broken_so_the_list_comes_from_the_primary_header()
    {
        PartitionAnalysis a = Analyze("TD-FOR-GPT-BADBACKUP");
        PartitionAnalysis good = Analyze("TD-FOR-GPT");
        Assert.Equal(good.Partitions.Select(p => (p.StartLba, p.EndLba, p.Name)), a.Partitions.Select(p => (p.StartLba, p.EndLba, p.Name)));
        Assert.Contains(a.Issues, i => i.Kind == PartitionIssueKind.GptBackupMismatch);
        Assert.DoesNotContain(a.Issues, i => i.Kind is PartitionIssueKind.GptEntriesCrc or PartitionIssueKind.GptHeaderCrc);
    }

    /// <summary>4096 バイトセクタの GPT を 512 で解析すると、4096 を提案する (UI の InfoBar は TC-FOR-06-05 の UI テスト)。</summary>
    [Fact]
    public void A_4k_gpt_read_with_512_byte_sectors_suggests_4096()
    {
        PartitionAnalysis wrong = Analyze("TD-FOR-GPT-4K", 512);
        Assert.Equal(4096, wrong.SuggestedSectorSize);
        PartitionAnalysis right = Analyze("TD-FOR-GPT-4K", 4096);
        Assert.Null(right.SuggestedSectorSize);
        Assert.Equal(PartitionImages.Gpt4KParts.Select(p => (p.First, p.Last)), right.Partitions.Select(p => (p.StartLba, p.EndLba)));
        Assert.Equal(256L * 4096, right.Partitions.First().StartOffset);
    }

    [Fact]
    [Trait("TC", "TC-FOR-06-06")]
    public void Apple_partition_map_and_bsd_disklabel_partitions_are_listed()
    {
        PartitionAnalysis apm = Analyze("TD-FOR-APM");
        PartitionRow[] rows = [.. apm.Rows.Where(r => r.Scheme == PartitionScheme.Apm)];
        Assert.Equal(3, rows.Length);
        for (int i = 0; i < 3; i++)
        {
            (string name, string type, long start, long count) = PartitionImages.ApmEntries[i];
            Assert.Equal(name, rows[i].Name);
            Assert.Equal(type, rows[i].TypeText);
            Assert.Equal(start, rows[i].BlockStart);
            Assert.Equal(count, rows[i].BlockCount);
        }

        PartitionAnalysis bsd = Analyze("TD-FOR-BSD");
        PartitionRow[] labels = [.. bsd.Rows.Where(r => r.Scheme == PartitionScheme.Bsd)];
        Assert.Equal(4, labels.Length);
        Assert.Equal(PartitionImages.BsdPartitions.Select(p => (p.Offset, p.Size)), labels.Select(r => (r.StartLba, r.SectorCount)));
        Assert.Equal(["4.2BSD", "swap", "4.2BSD", "4.2BSD"], labels.Select(r => r.TypeText));
        Assert.Equal(["a", "b", "d", "e"], labels.Select(r => r.Name));
    }

    [Fact]
    public void Gaps_of_at_least_one_mib_are_listed_as_unallocated()
    {
        PartitionAnalysis a = Analyze("TD-FOR-GPT");
        PartitionRow[] gaps = [.. a.Rows.Where(r => r.IsUnallocated)];

        // 34〜2047 (1 MiB 未満) は既定で出さず、最後のパーティションの後ろ (69632〜) は出す。
        Assert.DoesNotContain(gaps, g => g.StartLba < 2048);
        Assert.Contains(gaps, g => g.StartLba == 69632);
        PartitionAnalysis small = Analyze("TD-FOR-GPT", small: true);
        Assert.Contains(small.Rows, g => g.IsUnallocated && g.StartLba == 34);
    }

    [Fact]
    public void Partition_file_systems_are_guessed_from_the_first_sector()
    {
        PartitionAnalysis a = Analyze("TD-FOR-NTFS-FAT32");
        PartitionRow[] parts = [.. a.Partitions];
        Assert.Equal(2, parts.Length);
        Assert.Equal(FileSystemKind.Ntfs, parts[0].FileSystem);
        Assert.Equal(FileSystemKind.Fat32, parts[1].FileSystem);
        Assert.Equal(parts[1], a.PartitionAt(parts[1].StartOffset + 10));
    }

    [Fact]
    public void A_disk_without_a_table_reports_no_table()
    {
        PartitionAnalysis a = PartitionAnalyzer.Analyze(new ArrayVolumeReader(new byte[1 << 20]), 512);
        Assert.True(a.NoTable);
        PartitionAnalysis volume = Analyze("TD-FOR-NTFS");
        Assert.True(volume.NoTable);
    }

    [Fact]
    public void Overlapping_entries_are_warned()
    {
        var image = new SparseImage(64 * 1024 * 1024);
        PartitionImages.WriteMbr(image, 1, PartitionImages.MbrEntry(0x07, 2048, 10000), PartitionImages.MbrEntry(0x07, 5000, 10000),
            PartitionImages.MbrEntry(0x07, 100000, 1_000_000));
        PartitionAnalysis a = PartitionAnalyzer.Analyze(new SparseImageReader(image), 512);
        Assert.Contains(a.Issues, i => i.Kind == PartitionIssueKind.Overlap && i.Row == 1 && i.OtherRow == 2);
        Assert.Contains(a.Issues, i => i.Kind == PartitionIssueKind.OutOfRange && i.Row == 3);
        Assert.True(a.Rows.Single(r => r.Number == 3).Warning);
    }
}
