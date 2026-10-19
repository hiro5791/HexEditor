using System.Security.Cryptography;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Scope;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>
/// FOR-24 ディスクのクローンと FOR-25 セクタ範囲の保存と復元 (Core)。出力先は偽のディスク (FakeDevices) だけで、内容はテスト用の一時フォルダの
/// ファイルに置く。実機のディスクには触れない (実際の仮想ディスクでの確認は CI の特権のジョブの UI テスト)。
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class DiskCloneTests : IDisposable
{
    private readonly ImageTempDir _dir = new("hexeditor-clone");

    public void Dispose()
    {
        ImageForensics.Hooks = NullImageForensicHooks.Instance;
        _dir.Dispose();
    }

    private (FakeDeviceAccess Access, DeviceByteSource Disk) FakeDisk(long size, FakeLockBehavior lockBehavior = FakeLockBehavior.Ok, string? image = null,
        bool systemVolume = false)
    {
        var spec = new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 3, Size = size, SectorSize = 512, Serial = "TARGET3", Model = "Fake Target", Image = image ?? _dir.File("disk3.img") }],
            Volumes =
            [
                new FakeVolumeSpec
                {
                    Path = @"\\.\X:", DriveLetter = "X:", Label = "OLD", Disk = 3, Offset = 1024 * 1024, Size = 32 * 1024 * 1024, Lock = lockBehavior,
                    System = systemVolume,
                },
            ],
        };
        var access = new FakeDeviceAccess(spec);
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(3), writable: true);
        DiskInfo info = access.Enumerate().Disks[0];
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path, DisplayName = "Disk 3", SerialNumber = "TARGET3", Disk = info, Route = DeviceRoute.Elevated,
        });
        return (access, source);
    }

    private static AcquisitionSourceInfo SourceInfo(IByteSource source, string path) => new()
    {
        Path = path, Identity = source.Identity, Capacity = source.Length, SectorSize = 512,
    };

    [Fact]
    [Trait("TC", "TC-FOR-24-01")]
    public async Task Cloning_an_image_to_a_fake_disk_verifies_as_match()
    {
        using IImageSource image = ImageOpener.Open(TestDataCatalog.Get("TD-FOR-RAID-SRC"), ImageFormat.Raw);
        (FakeDeviceAccess access, DeviceByteSource target) = FakeDisk(256 * MiB);
        using (access)
        using (target)
        {
            ClonePlan plan = DiskClone.Plan(image, SourceInfo(image, "src.img"), 0, image.Length, target, 0, zeroFillRest: false, access.Enumerate());
            Assert.Equal(CloneProblem.None, plan.Problem);
            Assert.False(plan.Truncated);
            Assert.Equal(@"\\.\X:", Assert.Single(plan.TargetVolumes).Path);

            CloneResult result = await DiskClone.RunAsync(plan, new CloneOptions { LogPath = _dir.File(Path.Combine("reports", "clone.txt")) });
            Assert.Equal(AcquisitionStatus.Completed, result.Status);
            Assert.Equal(VerificationVerdict.Match, result.Verification);
            Assert.Contains(access.Calls, c => c.StartsWith("Lock", StringComparison.Ordinal));
            Assert.Contains(access.Calls, c => c.StartsWith("Unlock", StringComparison.Ordinal));

            // ディスクの先頭 (入力元の大きさ) の SHA-256 が入力元と一致する。
            byte[] expected = Hash(image, HashAlgorithmName.SHA256);
            using var head = new SubRangeSource(target, 0, image.Length);
            Assert.Equal(expected, Hash(head, HashAlgorithmName.SHA256));
            Assert.Contains("match", File.ReadAllText(_dir.File(Path.Combine("reports", "clone.txt"))));
        }
    }

    [Fact]
    public async Task A_smaller_target_takes_only_the_head_and_zero_fill_clears_the_rest()
    {
        using var source = new VirtualByteSource(8 * MiB, VirtualContent.Offset64);
        (FakeDeviceAccess access, DeviceByteSource target) = FakeDisk(6 * MiB);
        using (access)
        using (target)
        {
            ClonePlan plan = DiskClone.Plan(source, SourceInfo(source, "v"), 0, source.Length, target, 1024, zeroFillRest: true, access.Enumerate());
            Assert.True(plan.Truncated);
            Assert.Equal(6 * MiB - 1024 * 512, plan.BytesToWrite);
            Assert.Equal(0, plan.ZeroFillBytes);
            CloneResult r = await DiskClone.RunAsync(plan, new CloneOptions());
            Assert.Equal(VerificationVerdict.Match, r.Verification);
            Assert.Equal(ReadAll(source, 0, 4096), ReadAll(target, 1024 * 512, 4096));

            ClonePlan fill = DiskClone.Plan(new VirtualByteSource(MiB, VirtualContent.Fill, fill: 0x11), SourceInfo(source, "w"), 0, MiB, target, 0, true, access.Enumerate());
            Assert.Equal(5 * MiB, fill.ZeroFillBytes);
            await DiskClone.RunAsync(fill, new CloneOptions());
            Assert.All(ReadAll(target, 2 * MiB, 4096), b => Assert.Equal(0, b));
            Assert.All(ReadAll(target, 0, 4096), b => Assert.Equal(0x11, b));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-24-02")]
    public async Task A_volume_that_cannot_be_locked_stops_the_clone_and_nothing_is_written()
    {
        using var source = new VirtualByteSource(4 * MiB, VirtualContent.Random, seed: 1);
        (FakeDeviceAccess access, DeviceByteSource target) = FakeDisk(64 * MiB, FakeLockBehavior.Refuse);
        using (access)
        using (target)
        {
            byte[] before = Hash(target, HashAlgorithmName.SHA256);
            ClonePlan plan = DiskClone.Plan(source, SourceInfo(source, "v"), 0, source.Length, target, 0, false, access.Enumerate());
            VolumeLockException ex = await Assert.ThrowsAsync<VolumeLockException>(() => DiskClone.RunAsync(plan, new CloneOptions(), confirmDismount: _ => false));
            Assert.Equal("X:", ex.Volume);
            Assert.Equal(0, access.WriteCount);
            Assert.Equal(before, Hash(target, HashAlgorithmName.SHA256));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-24-03")]
    public void Write_block_mode_and_evidence_disks_refuse_the_clone()
    {
        using var source = new VirtualByteSource(MiB);
        (FakeDeviceAccess access, DeviceByteSource target) = FakeDisk(64 * MiB);
        using (access)
        using (target)
        {
            var hooks = new RecordingHooks { IsWriteBlocked = true };
            ImageForensics.Hooks = hooks;
            Assert.Equal(CloneProblem.WriteBlocked, DiskClone.Plan(source, SourceInfo(source, "v"), 0, MiB, target, 0, false, access.Enumerate()).Problem);
            hooks.IsWriteBlocked = false;
            hooks.EvidenceDisks.Add(3);
            Assert.Equal(CloneProblem.TargetIsEvidence, DiskClone.Plan(source, SourceInfo(source, "v"), 0, MiB, target, 0, false, access.Enumerate()).Problem);
            hooks.EvidenceDisks.Clear();
            Assert.Equal(CloneProblem.SectorSizeMismatch,
                DiskClone.Plan(source, SourceInfo(source, "v") with { SectorSize = 4096 }, 0, MiB, target, 0, false, access.Enumerate()).Problem);
            Assert.Equal(CloneProblem.SameDevice,
                DiskClone.Plan(source, SourceInfo(source, "v") with { DiskNumber = 3 }, 0, MiB, target, 0, false, access.Enumerate()).Problem);
        }

        (FakeDeviceAccess a2, DeviceByteSource t2) = FakeDisk(64 * MiB, systemVolume: true, image: _dir.File("disk3b.img"));
        using (a2)
        using (t2)
        {
            Assert.Equal(CloneProblem.TargetInUseByWindows, DiskClone.Plan(source, SourceInfo(source, "v"), 0, MiB, t2, 0, false, a2.Enumerate()).Problem);
        }
    }

    // ---- FOR-25 ----

    private (FakeDeviceAccess Access, DeviceByteSource Disk) GptDisk()
    {
        string image = _dir.File("gpt-disk.img");
        File.Copy(TestDataCatalog.Get("TD-FOR-RAID-SRC"), image, overwrite: true);
        return FakeDisk(TestDataCatalog.RaidSourceLength, image: image);
    }

    private static SectorTargetInfo TargetInfo(DeviceByteSource d) => new(d.Path, d.Info.Disk?.Model, d.Info.SerialNumber, d.Length, 512, d.Identity);

    [Fact]
    [Trait("TC", "TC-FOR-25-01")]
    public void Saving_and_restoring_the_gpt_brings_the_partitions_back()
    {
        (FakeDeviceAccess access, DeviceByteSource disk) = GptDisk();
        using (access)
        using (disk)
        {
            ImagePartitionTable original = ImagePartitions.Read(disk, 512);
            Assert.Equal("GPT", original.Scheme);
            string bin = _dir.File("gpt.bin");
            var info = new AcquisitionSourceInfo { Path = disk.Path, Model = "Fake Target", SerialNumber = "TARGET3", Capacity = disk.Length, SectorSize = 512 };
            var time = new ManualTimeProvider();
            time.Advance(new DateTimeOffset(2026, 10, 7, 3, 15, 42, TimeSpan.Zero) - time.GetUtcNow());
            SectorRangeArchive.Save(disk, info, 0, 34, bin, time: time);

            SectorRangeSidecar sidecar = SectorRangeArchive.ReadSidecar(bin)!;
            Assert.Equal("Fake Target", sidecar.Model);
            Assert.Equal("TARGET3", sidecar.SerialNumber);
            Assert.Equal(disk.Length, sidecar.Capacity);
            Assert.Equal(512, sidecar.SectorSize);
            Assert.Equal(0, sidecar.StartLba);
            Assert.Equal(34, sidecar.SectorCount);
            Assert.Equal("2026-10-07T03:15:42.0000000Z", sidecar.SavedAt);
            Assert.Equal(SectorRangeArchive.Sha256Of(bin), sidecar.Sha256);
            Assert.Contains("\"sha256\"", File.ReadAllText(bin + ".json"));

            // LBA 1〜33 を 00 で上書きして GPT を壊す。
            disk.Write(512, new byte[33 * 512]);
            Assert.Empty(ImagePartitions.Read(disk, 512).Partitions);
            byte[] broken = ReadAll(disk, 0, 34 * 512);

            SectorRestorePlan plan = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk), catalog: access.Enumerate(), diskNumber: 3);
            Assert.True(plan.CanRun);
            Assert.False(plan.IdentityMismatch);
            Assert.Equal(34, plan.SectorCount);
            SectorRestoreResult result = SectorRangeArchive.Restore(plan, bin, disk, backup: true, disk, time: time);
            Assert.True(result.Success);
            Assert.True(result.Verified);
            Assert.Equal(original.Partitions, ImagePartitions.Read(disk, 512).Partitions);

            // 退避ファイルの内容が、書き込む前の範囲と一致する。
            Assert.Equal(_dir.File("gpt.before-20261007T031542Z.bin"), result.BackupPath);
            Assert.Equal(broken, File.ReadAllBytes(result.BackupPath!));
            Assert.True(File.Exists(result.BackupPath + ".json"));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-25-02")]
    public void A_changed_data_file_is_refused_by_the_hash_check()
    {
        (FakeDeviceAccess access, DeviceByteSource disk) = GptDisk();
        using (access)
        using (disk)
        {
            string bin = _dir.File("gpt.bin");
            var info = new AcquisitionSourceInfo { Path = disk.Path, SerialNumber = "TARGET3", Capacity = disk.Length, SectorSize = 512 };
            SectorRangeArchive.Save(disk, info, 0, 34, bin);
            byte[] data = File.ReadAllBytes(bin);
            data[0x200] ^= 0x01;
            File.WriteAllBytes(bin, data);
            byte[] before = Hash(disk, HashAlgorithmName.SHA256);
            access.ClearCalls();

            SectorRestorePlan plan = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk));
            Assert.True(plan.HashMismatch);
            Assert.False(plan.CanRun);
            Assert.Throws<InvalidOperationException>(() => SectorRangeArchive.Restore(plan, bin, disk, backup: true));
            Assert.Equal(0, access.WriteCount);
            Assert.Equal(before, Hash(disk, HashAlgorithmName.SHA256));
        }
    }

    [Fact]
    public void Restore_warns_about_another_disk_needs_a_start_without_sidecar_and_is_refused_in_write_block_mode()
    {
        (FakeDeviceAccess access, DeviceByteSource disk) = GptDisk();
        using (access)
        using (disk)
        {
            string bin = _dir.File("mbr.bin");
            SectorRangeArchive.Save(disk, new AcquisitionSourceInfo { Path = "other", SerialNumber = "OTHER", Capacity = 1, SectorSize = 512 }, 0, 1, bin);
            Assert.True(SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk)).IdentityMismatch);

            File.Delete(bin + ".json");
            SectorRestorePlan noSidecar = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk));
            Assert.True(noSidecar.NeedsStartLba);
            Assert.False(noSidecar.CanRun);
            SectorRestorePlan withStart = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk), startLba: 100);
            Assert.True(withStart.CanRun);
            Assert.Equal(100, withStart.StartLba);
            Assert.True(SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk), startLba: disk.Length / 512).OutOfRange);

            ImageForensics.Hooks = new RecordingHooks { IsWriteBlocked = true };
            SectorRestorePlan blocked = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk), startLba: 100);
            Assert.True(blocked.WriteBlocked);
            Assert.False(blocked.CanRun);

            // 保存は書き込み保護モードでもできる。
            SectorRangeArchive.Save(disk, new AcquisitionSourceInfo { Path = disk.Path, SectorSize = 512, Capacity = disk.Length }, 0, 1, _dir.File("again.bin"));
        }
    }

    [Fact]
    public void A_range_covering_a_whole_partition_needs_the_strong_confirmation_and_write_errors_report_the_lba()
    {
        (FakeDeviceAccess access, DeviceByteSource disk) = GptDisk();
        using (access)
        using (disk)
        {
            ImagePartition p = ImagePartitions.Read(disk, 512).Partitions[0];
            string bin = _dir.File("part.bin");
            SectorRangeArchive.Save(disk, new AcquisitionSourceInfo { Path = disk.Path, SerialNumber = "TARGET3", Capacity = disk.Length, SectorSize = 512 },
                p.Offset / 512, p.Length / 512, bin);
            SectorRestorePlan plan = SectorRangeArchive.PlanRestore(bin, disk, TargetInfo(disk), catalog: access.Enumerate(), diskNumber: 3);
            Assert.True(plan.WholeDiskOrPartition);
            Assert.Equal(@"\\.\X:", Assert.Single(plan.VolumesToLock).Path);

            // 2 回目の書き込みから先はディスクが取り外された状態にする (再試行しても書けない)。
            access.AfterWrite = n =>
            {
                if (n == 1)
                {
                    access.Disk(3).Removed = true;
                }
            };
            SectorRestoreResult result = SectorRangeArchive.Restore(plan, bin, disk, backup: false, disk);
            Assert.False(result.Success);
            Assert.NotNull(result.FailedLba);
            Assert.True(result.FailedLba >= p.Offset / 512);
        }
    }
}
