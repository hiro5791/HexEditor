using System.Security.Cryptography;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Tests.FileSystems;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Wiping;

/// <summary>
/// FOR-31 空き領域とスラックの消去。TD-FOR-VHDX-DELETED と同じ内容の生のディスクをメモリ上に作り、直接書き込みはそのイメージに、OS 経由は
/// イメージの空き領域にファイルを割り当てる偽のボリュームに書く (この PC の実際のボリュームには書かない)。
/// </summary>
public sealed class FileSystemWipeTests
{
    private const long VolumeOffset = WipeImages.DeletedPartitionStart * 512;

    private static IFileSystem OpenVolume(SparseImage disk) =>
        FileSystemRegistry.Open(new SparseImageReader(disk).Slice(VolumeOffset, WipeImages.DeletedVolumeBytes), new FileSystemOpenOptions());

    private static Dictionary<string, string> Hashes(IFileSystem fs) => fs.ListDirectory(fs.Root)
        .Where(e => !e.IsDirectory && !e.Name.StartsWith('$') && e.StreamName is null)
        .ToDictionary(e => e.Name, e => Convert.ToHexStringLower(SHA256.HashData(FsTestImages.ReadAll(fs.OpenContent(e)))));

    private static string DiskHash(SparseImage image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        for (long at = 0; at < image.Length; at += buffer.Length)
        {
            image.Read(at, buffer);
            sha.AppendData(buffer);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static WipeJob DirectJob(IEnumerable<WipeRange> ranges, WipeKind kind, IFileSystem fs) => new()
    {
        Kind = kind,
        TargetName = "Disk image",
        Identity = "test:deleted",
        Ranges = ranges,
        Method = ShredMethod.ZeroOnce,
        Verify = true,
        ChunkAlignment = (int)fs.ClusterSize,
        Details = new Dictionary<string, string> { ["fileSystem"] = fs.Kind.ToString() },
    };

    [Fact]
    [Trait("TC", "TC-FOR-31-01")]
    public void Free_space_wipe_removes_deleted_content_by_both_methods_and_keeps_existing_files()
    {
        foreach (bool direct in new[] { false, true })
        {
            SparseImage disk = WipeImages.DeletedDisk();
            Assert.True(WipeAssert.Contains(disk, 0, disk.Length, WipeImages.WipeMe));
            IFileSystem fs = OpenVolume(disk);
            Dictionary<string, string> before = Hashes(fs);
            Assert.Contains("keep.txt", before.Keys);
            var hooks = new RecordingWipeHooks();
            WipeRange[] free = [.. FileSystemWipe.FreeRanges(fs, VolumeOffset)];
            long freeBytes = free.Sum(r => r.Length);
            Assert.Equal(FileSystemWipe.FreeBytes(fs, VolumeOffset), freeBytes);
            if (direct)
            {
                FileSystemWipe.EnsureCanWriteDirect(fs, "Disk image", WipeKind.FreeSpaceDirect, hooks);
                WipeResult result = WipeRunner.Run(DirectJob(FileSystemWipe.FreeRanges(fs, VolumeOffset), WipeKind.FreeSpaceDirect, fs),
                    () => new SparseImageWipeTarget(disk), hooks: hooks);
                Assert.True(result.Succeeded);
                Assert.Equal(freeBytes, result.TotalBytes);
            }
            else
            {
                var volume = new FakeFreeSpaceVolume(disk, free.Select(r => (r.Offset, r.Length)));
                OsFreeSpaceWipeResult result = WipeRunner.RunOs(volume, @"X:\ (DELETED, NTFS)", "volume:X", ShredMethod.ZeroOnce, hooks: hooks);
                Assert.False(result.Cancelled);
                Assert.Null(result.Error);
                Assert.Empty(result.Leftovers);
                Assert.Equal(freeBytes + volume.SmallBytes, result.BytesOverwritten);
                Assert.Equal(100, result.SmallFiles);
                Assert.Empty(volume.Files);
                Assert.Empty(volume.Directories);
            }

            Assert.False(WipeAssert.Contains(disk, 0, disk.Length, WipeImages.WipeMe), direct ? "direct" : "os");
            Assert.Equal(before, Hashes(OpenVolume(disk)));
            var record = Assert.Single(hooks.Records);
            Assert.Equal(WipeAuditOutcome.Succeeded, record.Outcome);
            Assert.Equal(direct ? "FreeSpaceDirect" : "FreeSpaceOs", record.Details["kind"]);
            Assert.True(record.Details.ContainsKey("elapsedSeconds"));
            Assert.True(long.Parse(record.Details["bytes"], System.Globalization.CultureInfo.InvariantCulture) >= freeBytes);
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-31-02")]
    public void Slack_wipe_zeroes_the_slack_keeps_file_contents_and_counts_resident_files_as_skipped()
    {
        SparseImage disk = WipeImages.DeletedDisk();
        IFileSystem fs = OpenVolume(disk);
        Dictionary<string, string> before = Hashes(fs);
        FsEntry slack = FsTestImages.Child(fs, fs.Root, "slack.txt");
        long slackOffset = VolumeOffset + fs.GetExtents(slack)[0].VolumeOffset!.Value;
        Assert.True(WipeAssert.Contains(disk, slackOffset + 100, 4096 - 100, WipeImages.SlackRemnant));

        SlackWipePlan plan = FileSystemWipe.PlanSlack(fs, VolumeOffset);
        Assert.True(plan.Skipped.Resident >= 1, "tiny.txt is resident");
        Assert.Equal(plan.Skipped.Total, plan.Skipped.Compressed + plan.Skipped.Sparse + plan.Skipped.Resident + plan.Skipped.Inconsistent);
        Assert.Contains(new WipeRange(slackOffset + 100, 4096 - 100), plan.Ranges);

        FileSystemWipe.EnsureCanWriteDirect(fs, "Disk image", WipeKind.Slack);
        WipeResult result = WipeRunner.Run(DirectJob(plan.Ranges, WipeKind.Slack, fs) with { ChunkAlignment = 1 }, () => new SparseImageWipeTarget(disk));
        Assert.True(result.Succeeded);

        Assert.Equal(before, Hashes(OpenVolume(disk)));
        byte[] tail = new byte[4096 - 100];
        disk.Read(slackOffset + 100, tail);
        Assert.All(tail, b => Assert.Equal(0, b));
    }

    [Fact]
    [Trait("TC", "TC-FOR-31-03")]
    public void Cancelling_the_os_method_removes_every_temporary_file_and_returns_the_space()
    {
        SparseImage disk = WipeImages.DeletedDisk();
        IFileSystem fs = OpenVolume(disk);
        WipeRange[] free = [.. FileSystemWipe.FreeRanges(fs, VolumeOffset)];
        var volume = new FakeFreeSpaceVolume(disk, free.Select(r => (r.Offset, r.Length)));
        long freeBefore = volume.FreeBytes();
        using var cts = new CancellationTokenSource();
        var hooks = new RecordingWipeHooks();
        OsFreeSpaceWipeResult result = WipeRunner.RunOs(volume, "X:", "volume:X", ShredMethod.Dod,
            progress: p =>
            {
                if (p.PassDone > p.PassTotal * 3 / 10)
                {
                    cts.Cancel();
                }
            }, hooks: hooks, cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.Empty(volume.Files);
        Assert.Empty(volume.Directories);
        Assert.Empty(result.Leftovers);
        Assert.Equal(freeBefore, volume.FreeBytes());
        Assert.StartsWith(@"X:\" + OsFreeSpaceWipe.FolderPrefix, result.TemporaryFolder, StringComparison.Ordinal);
        Assert.Equal(WipeAuditOutcome.Cancelled, Assert.Single(hooks.Records).Outcome);
    }

    [Fact]
    [Trait("TC", "TC-FOR-31-04")]
    public void Direct_writing_is_refused_on_ntfs_with_the_dirty_flag()
    {
        SparseImage disk = WipeImages.DeletedDisk(dirty: true);
        string hash = DiskHash(disk);
        IFileSystem fs = OpenVolume(disk);
        Assert.True(FileSystemWipe.IsDirty(fs));
        Assert.Equal(FsInconsistency.DirtyFlag, FileSystemWipe.CheckConsistency(fs));
        var hooks = new RecordingWipeHooks();
        foreach (WipeKind kind in new[] { WipeKind.FreeSpaceDirect, WipeKind.Slack })
        {
            var ex = Assert.Throws<WipeRefusedException>(() => FileSystemWipe.EnsureCanWriteDirect(fs, "Disk image", kind, hooks));
            Assert.Equal(WipeRefusal.Inconsistent, ex.Reason);
            Assert.Equal(nameof(FsInconsistency.DirtyFlag), ex.Detail);
        }

        Assert.All(hooks.Records, r => Assert.Equal(WipeAuditOutcome.Refused, r.Outcome));
        Assert.Equal(hash, DiskHash(disk));

        // dirty フラグのないボリュームは不整合なし。
        Assert.Equal(FsInconsistency.None, FileSystemWipe.CheckConsistency(OpenVolume(WipeImages.DeletedDisk())));
    }

    [Theory]
    [InlineData("TD-FOR-FAT16")]
    [InlineData("TD-FOR-FAT32")]
    [InlineData("TD-FOR-EXFAT")]
    public void Free_space_direct_wipe_works_on_fat_and_exfat(string id)
    {
        // 共有のイメージ (ほかのテストも使う) は変えないので、写しに書く。
        SparseImage shared = id == "TD-FOR-EXFAT" ? FileSystemImageSet.ExFat().Image : FileSystemImageSet.Fat(id).Image;
        var image = new SparseImage(shared.Length);
        image.Place(0, shared);
        IFileSystem fs = FileSystemRegistry.Open(new SparseImageReader(image), new FileSystemOpenOptions());
        Assert.Equal(FsInconsistency.None, FileSystemWipe.CheckConsistency(fs));
        Dictionary<string, string> before = Hashes(fs);
        Assert.True(WipeAssert.Contains(image, 0, image.Length, "DELETED-FILE-MARKER"));
        WipeResult result = WipeRunner.Run(DirectJob(FileSystemWipe.FreeRanges(fs, 0), WipeKind.FreeSpaceDirect, fs), () => new SparseImageWipeTarget(image));
        Assert.True(result.Succeeded);
        Assert.False(WipeAssert.Contains(image, 0, image.Length, "DELETED-FILE-MARKER"));
        Assert.Equal(before, Hashes(FileSystemRegistry.Open(new SparseImageReader(image), new FileSystemOpenOptions())));

        SlackWipePlan slack = FileSystemWipe.PlanSlack(fs, 0);
        Assert.NotEmpty(slack.Ranges);
        Assert.True(WipeRunner.Run(DirectJob(slack.Ranges, WipeKind.Slack, fs) with { ChunkAlignment = 1 }, () => new SparseImageWipeTarget(image)).Succeeded);
        Assert.Equal(before, Hashes(FileSystemRegistry.Open(new SparseImageReader(image), new FileSystemOpenOptions())));
    }

    [Fact]
    public void Fat_dirty_bits_and_exfat_volume_flags_are_inconsistencies()
    {
        (SparseImage fat, _) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        var copy = new SparseImage(fat.Length);
        copy.Place(0, fat);
        copy.Write(0x41, [0x01]);
        Assert.True(FileSystemWipe.IsDirty(FileSystemRegistry.Open(new SparseImageReader(copy), new FileSystemOpenOptions())));

        (SparseImage exfat, _) = FileSystemImageSet.ExFat();
        var copy2 = new SparseImage(exfat.Length);
        copy2.Place(0, exfat);
        copy2.Write(106, [0x02, 0x00]);
        IFileSystem fs2 = FileSystemRegistry.Open(new SparseImageReader(copy2), new FileSystemOpenOptions());
        Assert.NotEqual(FsInconsistency.None, FileSystemWipe.CheckConsistency(fs2));
    }

    [Fact]
    public void The_fat_copies_must_agree_before_writing_directly()
    {
        IFileSystem fs = FsTestImages.Open("TD-FOR-FAT32-FATDIFF");
        Assert.Equal(FsInconsistency.FatCopiesDiffer, FileSystemWipe.CheckConsistency(fs));
    }
}
