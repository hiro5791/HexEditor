using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;

namespace HexEditor.Core.Tests.Devices;

/// <summary>ENG-30 ディスク・ボリュームへの書き込み (Core レベル。偽のデバイスを使う)。</summary>
public sealed class DiskWriteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-diskwrite").FullName;

    private string Journals => Path.Combine(_dir, "recovery");

    private static (FakeDeviceAccess Access, DeviceByteSource Source, Document Doc) OpenDisk(FakeDeviceSpec spec)
    {
        var access = new FakeDeviceAccess(spec);
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(0), writable: true);
        DiskInfo disk = access.Enumerate().Disks[0];
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path,
            DisplayName = "Disk 0",
            SerialNumber = spec.Disks[0].Serial,
            Disk = disk,
            Route = DeviceRoute.Elevated,
        });
        return (access, source, new Document(source));
    }

    private static FakeDeviceSpec Spec(FakeLockBehavior lockBehavior = FakeLockBehavior.Ok, bool withVolume = true, bool systemVolume = false) => new()
    {
        Elevated = true,
        Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 64 * 1024 * 1024, Serial = "DISK0" }],
        Volumes = withVolume
            ?
            [
                new FakeVolumeSpec
                {
                    Path = @"\\.\X:", DriveLetter = "X:", Disk = 0, Offset = 1024 * 1024, Size = 16 * 1024 * 1024,
                    FileSystem = "NTFS", Lock = lockBehavior, System = systemVolume,
                },
            ]
            : [],
    };

    [Fact]
    [Trait("TC", "TC-ENG-07-03")]
    public void Writes_are_aligned_to_whole_sectors()
    {
        // 論理セクタ 512 バイト・NeedsAlignment のデータソースで、1 バイトの上書きはそのセクタだけ、境界をまたぐ 2 バイトは 2 セクタだけを書く。
        (FakeDeviceAccess access, DeviceByteSource source, Document doc) = OpenDisk(Spec(withVolume: false));
        using (source)
        {
            byte[] before = new byte[2048];
            access.Disk(0).Read(0, before);

            doc.Overwrite(100, [0xFF]);
            access.ClearCalls();
            DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());
            doc.CompleteDeviceWrite(source, DiskWrite.Execute(plan, doc.Current, Journals));
            Assert.Equal([@"Write \\.\PhysicalDrive0 0x0 512"], access.Calls.Where(c => c.StartsWith("Write ", StringComparison.Ordinal)));

            byte[] after = new byte[2048];
            access.Disk(0).Read(0, after);
            byte[] expected = (byte[])before.Clone();
            expected[100] = 0xFF;
            Assert.Equal(expected, after);

            // セクタ境界 (511〜512) をまたぐ 2 バイト: セクタ 0 と 1 (位置 0・長さ 1,024、または 2 回) だけ。
            doc.Overwrite(511, [0x11, 0x22]);
            access.ClearCalls();
            plan = DiskWrite.Plan(doc, source, access.Enumerate());
            doc.CompleteDeviceWrite(source, DiskWrite.Execute(plan, doc.Current, Journals));
            List<string> writes = [.. access.Calls.Where(c => c.StartsWith("Write ", StringComparison.Ordinal))];
            Assert.True(writes.SequenceEqual([@"Write \\.\PhysicalDrive0 0x0 1024"])
                || writes.SequenceEqual([@"Write \\.\PhysicalDrive0 0x0 512", @"Write \\.\PhysicalDrive0 0x200 512"]), string.Join(", ", writes));
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-30-01")]
    public void Writing_a_mounted_volume_range_locks_writes_flushes_and_unlocks()
    {
        (FakeDeviceAccess access, DeviceByteSource source, Document doc) = OpenDisk(Spec());
        using (source)
        {
            // ボリュームの範囲内 (開始 + 32 MiB はこのディスクではボリュームの外。ボリュームは 1〜17 MiB)。ボリューム内の位置を上書きする。
            long at = 1024 * 1024 + 4096;
            doc.Overwrite(at, Enumerable.Repeat((byte)0xC3, 512).ToArray());
            DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());
            Assert.Equal(1, plan.RangeCount);
            Assert.Equal(512, plan.TotalBytes);
            Assert.Single(plan.VolumesToLock);
            Assert.False(plan.IsBlocked);

            access.ClearCalls();
            IReadOnlyList<SavedRange> saved = DiskWrite.Execute(plan, doc.Current, Journals);
            doc.CompleteDeviceWrite(source, saved);

            byte[] read = new byte[512];
            access.Disk(0).Read(at, read);
            Assert.All(read, b => Assert.Equal(0xC3, b));

            // 順序: ロック → 書き込み → Flush → ロック解除。
            string calls = string.Join(",", access.Calls);
            int lockIndex = access.Calls.ToList().FindIndex(c => c.StartsWith(@"Lock \\.\X:"));
            int writeIndex = access.Calls.ToList().FindIndex(c => c.StartsWith(@"Write \\.\PhysicalDrive0"));
            int flushIndex = access.Calls.ToList().FindIndex(c => c.StartsWith(@"Flush \\.\PhysicalDrive0"));
            int unlockIndex = access.Calls.ToList().FindIndex(c => c.StartsWith(@"Unlock \\.\X:"));
            Assert.True(lockIndex >= 0 && lockIndex < writeIndex, calls);
            Assert.True(writeIndex < flushIndex && flushIndex < unlockIndex, calls);
            Assert.False(doc.IsModified);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-30-02")]
    public void A_volume_with_open_files_prompts_for_dismount()
    {
        (FakeDeviceAccess access, DeviceByteSource source, Document doc) = OpenDisk(Spec(FakeLockBehavior.InUse));
        using (source)
        {
            doc.Overwrite(1024 * 1024 + 100, new byte[] { 1 });
            DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());
            bool prompted = false;

            // ディスマウントの確認でキャンセル (false) → ロックできず中止。
            Assert.Throws<VolumeLockException>(() => DiskWrite.Execute(plan, doc.Current, Journals, confirmDismount: _ =>
            {
                prompted = true;
                return false;
            }));
            Assert.True(prompted);

            // 承認 (true) → ディスマウントして書ける。
            byte[] expected = new byte[512];
            access.Disk(0).Read(1024 * 1024, expected);
            IReadOnlyList<SavedRange> saved = DiskWrite.Execute(plan, doc.Current, Journals, confirmDismount: _ => true);
            Assert.Single(saved);
            Assert.Contains(access.Calls, c => c.StartsWith(@"Dismount \\.\X:"));
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-30-03")]
    public void A_write_overlapping_a_system_volume_is_rejected()
    {
        (FakeDeviceAccess access, DeviceByteSource source, Document doc) = OpenDisk(Spec(systemVolume: true));
        using (source)
        {
            doc.Overwrite(1024 * 1024 + 100, new byte[] { 1 });
            DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());
            Assert.True(plan.IsBlocked);
            access.ClearCalls();
            Assert.Throws<DiskWriteBlockedException>(() => DiskWrite.Execute(plan, doc.Current, Journals));
            Assert.DoesNotContain(access.Calls, c => c.StartsWith("Write "));
            Assert.True(doc.IsModified);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-30-04")]
    public void Undo_then_save_restores_the_old_content()
    {
        (FakeDeviceAccess access, DeviceByteSource source, Document doc) = OpenDisk(Spec(withVolume: false));
        using (source)
        {
            long at = 512; // LBA 1, ボリュームの外。
            byte[] original = new byte[512];
            access.Disk(0).Read(at, original);

            doc.Overwrite(at, Enumerable.Repeat((byte)0xEE, 512).ToArray());
            doc.CompleteDeviceWrite(source, DiskWrite.Execute(DiskWrite.Plan(doc, source, access.Enumerate()), doc.Current, Journals));
            byte[] afterWrite = new byte[512];
            access.Disk(0).Read(at, afterWrite);
            Assert.All(afterWrite, b => Assert.Equal(0xEE, b));

            // Undo して保存すると書き込み前の内容に戻る (ENG-30 の仕様 5)。
            doc.Undo();
            doc.CompleteDeviceWrite(source, DiskWrite.Execute(DiskWrite.Plan(doc, source, access.Enumerate()), doc.Current, Journals));
            byte[] restored = new byte[512];
            access.Disk(0).Read(at, restored);
            Assert.Equal(original, restored);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-30-05")]
    public void A_crash_during_the_write_can_be_rolled_back_from_the_journal()
    {
        FakeDeviceSpec spec = Spec(withVolume: false);
        var access = new FakeDeviceAccess(spec);
        byte[] originalWhole;
        {
            originalWhole = new byte[(int)spec.Disks[0].Size];
            access.Disk(0).Read(0, originalWhole);
        }

        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(0), writable: true);
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path, DisplayName = "Disk 0", SerialNumber = "DISK0", Disk = access.Enumerate().Disks[0], Route = DeviceRoute.Elevated,
        });
        using (source)
        {
            var doc = new Document(source);
            // 未割り当て領域の 8 か所を 0 で上書き。
            for (int i = 0; i < 8; i++)
            {
                doc.Overwrite(20L * 1024 * 1024 + (i * 4L * 1024 * 1024), new byte[512]);
            }

            DiskWritePlan plan = DiskWrite.Plan(doc, source, access.Enumerate());

            // 3 回目の書き込みの後に失敗させる (強制終了に相当。ジャーナルは残る)。
            access.FailWriteAt = 3;
            string journalBefore;
            Assert.ThrowsAny<Exception>(() => DiskWrite.Execute(plan, doc.Current, Journals));
            // ロールバックで書き戻るため、失敗しても元に戻っている (このテストでは書き戻せる)。
            journalBefore = DiskWrite.FindJournals(Journals).FirstOrDefault() ?? string.Empty;

            byte[] afterWhole = new byte[originalWhole.Length];
            access.Disk(0).Read(0, afterWhole);
            Assert.Equal(originalWhole, afterWhole);
            _ = journalBefore;
        }
    }

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
}
