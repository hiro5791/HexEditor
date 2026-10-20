using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Wiping;

/// <summary>FOR-30 範囲・ディスクの安全な消去 (偽のディスク・メモリ上のイメージだけを使う)。</summary>
public sealed class RangeWipeTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-wipe").FullName;

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

    /// <summary>TD-FOR-VHDX-PATTERN と同じ内容の偽のディスク (イメージのファイルは一時フォルダに作る)。</summary>
    private (FakeDeviceAccess Access, DeviceByteSource Source) OpenPatternDisk(long size = 16 * MiB, bool withVolume = true, bool systemVolume = false)
    {
        string image = Path.Combine(_dir, "pattern.img");
        WipeImages.WritePatternDisk(image, size);
        var spec = new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 1, SectorSize = 512, Size = size, Serial = "PATTERN1", Image = image }],
            Volumes = withVolume
                ? [new FakeVolumeSpec { Path = @"\\.\P:", DriveLetter = "P:", Disk = 1, Offset = 2 * MiB, Size = 4 * MiB, FileSystem = "NTFS", System = systemVolume }]
                : [],
        };
        var access = new FakeDeviceAccess(spec);
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(1), writable: true);
        DiskInfo disk = access.Enumerate().Disks[0];
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path, DisplayName = "Disk 1", SerialNumber = "PATTERN1", Disk = disk, Route = DeviceRoute.Elevated,
        });
        return (access, source);
    }

    private static bool HasPattern(FakeDisk disk, long offset, long length)
    {
        byte[] buffer = new byte[length];
        disk.Read(offset, buffer);
        return buffer.AsSpan().IndexOf("ORIGDATA"u8) >= 0;
    }

    private static bool AllPattern(FakeDisk disk, long offset, long length)
    {
        byte[] buffer = new byte[length];
        disk.Read(offset, buffer);
        byte[] expected = new byte[length];
        WipeImages.FillPattern(expected, offset);
        return buffer.AsSpan().SequenceEqual(expected);
    }

    private static WipeJob Job(DeviceByteSource source, WipeRange range, ShredMethod method, bool verify = true) => new()
    {
        Kind = WipeKind.Range,
        TargetName = source.DisplayName,
        Identity = source.Identity,
        Path = source.Path,
        Ranges = [range],
        TotalBytes = range.Length,
        Method = method,
        Verify = verify,
    };

    [Fact]
    [Trait("TC", "TC-FOR-30-01")]
    public void Random_once_removes_the_original_pattern_only_inside_the_range_and_records_an_audit_event()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = OpenPatternDisk();
        using (access)
        using (source)
        {
            var hooks = new RecordingWipeHooks();
            var range = new WipeRange(0x100000, 0x800000);
            IReadOnlyList<VolumeDeviceInfo> volumes = WipeVolumeLocks.Overlapping(source, access.Enumerate(), [(range.Offset, range.Length)], out var blocked);
            Assert.Empty(blocked);
            Assert.Equal(@"\\.\P:", Assert.Single(volumes).Path);

            access.ClearCalls();
            WipeResult result = WipeRunner.Run(Job(source, range, ShredMethod.RandomOnce), () => new DeviceWipeTarget(source),
                () => WipeVolumeLocks.Acquire(source, volumes), hooks: hooks);

            Assert.True(result.Succeeded);
            Assert.True(result.Verified);
            Assert.Equal(0x800000, result.BytesWritten);
            FakeDisk disk = access.Disk(1);
            Assert.False(HasPattern(disk, range.Offset, range.Length));
            Assert.True(AllPattern(disk, range.Offset - MiB, MiB));
            Assert.True(AllPattern(disk, range.End, MiB));

            // ボリュームをロックしてから書き、書いた後にロックを解除する。
            IReadOnlyList<string> calls = access.Calls;
            int lockAt = calls.ToList().FindIndex(c => c.StartsWith("lock", StringComparison.OrdinalIgnoreCase));
            int writeAt = calls.ToList().FindIndex(c => c.StartsWith("write", StringComparison.OrdinalIgnoreCase));
            int unlockAt = calls.ToList().FindLastIndex(c => c.StartsWith("unlock", StringComparison.OrdinalIgnoreCase));
            Assert.True(lockAt >= 0 && lockAt < writeAt && writeAt < unlockAt, string.Join(", ", calls));

            var record = Assert.Single(hooks.Records);
            Assert.Equal(WipeAuditOperations.Wipe, record.Operation);
            Assert.Equal("Disk 1", record.Target);
            Assert.Equal(WipeAuditOutcome.Succeeded, record.Outcome);
            Assert.Equal("random", record.Details["method"]);
            Assert.Equal("8388608", record.Details["bytes"]);
            Assert.Equal("ok", record.Details["verification"]);
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-30-02")]
    public void Dod_reports_three_passes_in_order_with_the_total_over_all_passes_and_verifies()
    {
        var image = new SparseImage(4 * MiB);
        byte[] fill = new byte[4 * MiB];
        WipeImages.FillPattern(fill, 0);
        image.Write(0, fill);
        var target = new SparseImageWipeTarget(image);
        var passes = new List<(int Pass, bool Verifying)>();
        var time = new ManualTimeProvider();
        var op = new LongRunningOperation("wipe", OperationKind.WritesExternal, null, null, time);
        WipeResult result = WipeEngine.Execute(target, [new WipeRange(0, 4 * MiB)], ShredMethod.Dod, verify: true, op,
            progress: p =>
            {
                if (passes.Count == 0 || passes[^1] != (p.Pass, p.Verifying))
                {
                    passes.Add((p.Pass, p.Verifying));
                }
            });

        Assert.Equal([(1, false), (2, false), (3, false), (3, true)], passes);
        Assert.Equal(4 * MiB * 4, op.TotalBytes);
        Assert.Equal(op.TotalBytes, op.ProcessedBytes);
        Assert.True(result.Verified);
        Assert.Empty(result.Mismatches);
        Assert.Equal(3, target.Flushes);
        Assert.Equal(3 * 4 * MiB, result.BytesWritten);
        Assert.False(WipeAssert.Contains(image, 0, 4 * MiB, "ORIGDATA"));
    }

    [Fact]
    public void Verification_reports_ranges_that_do_not_read_back_as_written()
    {
        var image = new SparseImage(2 * MiB);
        var target = new BrokenReadbackTarget(new SparseImageWipeTarget(image), badAt: MiB + 10);
        WipeResult result = WipeEngine.Execute(target, [new WipeRange(0, 2 * MiB)], ShredMethod.Custom([ShredPass.Fill(0xA5)])!, verify: true);
        Assert.False(result.Succeeded);
        Assert.Equal(new WipeRange(MiB + 10, 1), Assert.Single(result.Mismatches));

        WipeResult random = WipeEngine.Execute(target, [new WipeRange(0, 2 * MiB)], ShredMethod.RandomOnce, verify: true);
        Assert.Single(random.Mismatches);
    }

    [Fact]
    public void Write_errors_can_be_skipped_and_the_unwritten_ranges_are_reported()
    {
        var image = new SparseImage(4 * MiB);
        var target = new SparseImageWipeTarget(image) { FailAt = offset => offset == MiB };
        var errors = new List<WipeWriteError>();
        WipeResult result = WipeEngine.Execute(target, [new WipeRange(0, 4 * MiB)], ShredMethod.ZeroOnce, verify: true,
            onError: e => { errors.Add(e); return WipeErrorChoice.Continue; });
        Assert.Single(errors);
        Assert.Equal(new WipeRange(MiB, MiB), Assert.Single(result.Unwritten));
        Assert.Empty(result.Mismatches);
        Assert.False(result.Succeeded);
        Assert.False(result.Aborted);

        WipeResult aborted = WipeEngine.Execute(target, [new WipeRange(0, 4 * MiB)], ShredMethod.ZeroOnce, verify: false, onError: _ => WipeErrorChoice.Abort);
        Assert.True(aborted.Aborted);
        Assert.Equal(MiB, aborted.StoppedAt);
        Assert.Equal(1, aborted.StoppedPass);
    }

    [Fact]
    public void Cancelling_stops_at_a_chunk_boundary_and_reports_how_far_it_got()
    {
        var image = new SparseImage(8 * MiB);
        byte[] fill = new byte[8 * MiB];
        WipeImages.FillPattern(fill, 0);
        image.Write(0, fill);
        using var cts = new CancellationTokenSource();
        var target = new SparseImageWipeTarget(image);
        WipeResult result = WipeEngine.Execute(target, [new WipeRange(0, 8 * MiB)], ShredMethod.ZeroOnce, verify: true,
            progress: p =>
            {
                if (p.PassDone >= 3 * MiB)
                {
                    cts.Cancel();
                }
            }, cancellationToken: cts.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(3 * MiB, result.StoppedAt);
        Assert.False(WipeAssert.Contains(image, 0, 3 * MiB, "ORIGDATA"));
        Assert.True(WipeAssert.Contains(image, 3 * MiB, 5 * MiB, "ORIGDATA"));
    }

    [Fact]
    public void Device_writes_that_do_not_end_on_a_sector_are_read_merged_and_written()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = OpenPatternDisk(size: 2 * MiB, withVolume: false);
        using (access)
        using (source)
        {
            var target = new DeviceWipeTarget(source);
            WipeResult result = WipeEngine.Execute(target, [new WipeRange(1000, 100)], ShredMethod.ZeroOnce, verify: true);
            Assert.True(result.Succeeded);
            byte[] sector = new byte[1024];
            access.Disk(1).Read(512, sector);
            byte[] expected = new byte[1024];
            WipeImages.FillPattern(expected, 512);
            expected.AsSpan(1000 - 512, 100).Clear();
            Assert.Equal(expected, sector);
        }
    }

    [Fact]
    public void Ranges_on_disks_are_widened_to_sectors()
    {
        Assert.Equal(new WipeRange(512, 1024), RangeWipe.Widen(new WipeRange(600, 700), 512, 1 << 20));
        Assert.Equal(new WipeRange(600, 700), RangeWipe.Widen(new WipeRange(600, 700), 1, 1 << 20));
        Assert.Equal(new WipeRange(0, 1000), RangeWipe.Widen(new WipeRange(10, 990), 512, 1000));
    }

    [Fact]
    public void Write_block_mode_and_evidence_are_refused_before_anything_is_written_and_audited()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = OpenPatternDisk(withVolume: false);
        using (access)
        using (source)
        {
            var hooks = new RecordingWipeHooks { WriteBlock = true };
            var range = new WipeRange(0, MiB);
            access.ClearCalls();
            var ex = Assert.Throws<WipeRefusedException>(() => WipeRunner.Run(Job(source, range, ShredMethod.ZeroOnce), () => new DeviceWipeTarget(source), hooks: hooks));
            Assert.Equal(WipeRefusal.WriteBlock, ex.Reason);
            hooks.WriteBlock = false;
            hooks.Evidence.Add(source.Identity);
            Assert.Equal(WipeRefusal.Evidence,
                Assert.Throws<WipeRefusedException>(() => WipeRunner.Run(Job(source, range, ShredMethod.ZeroOnce), () => new DeviceWipeTarget(source), hooks: hooks)).Reason);
            Assert.Equal(0, access.WriteCount);
            Assert.All(hooks.Records, r => Assert.Equal(WipeAuditOutcome.Refused, r.Outcome));
            Assert.Equal(2, hooks.Records.Count);
            Assert.True(AllPattern(access.Disk(1), 0, MiB));
        }
    }

    [Fact]
    public void System_disks_are_recognized_and_volumes_in_use_by_windows_block_the_wipe()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = OpenPatternDisk(systemVolume: true);
        using (access)
        using (source)
        {
            DeviceCatalog catalog = access.Enumerate();
            Assert.True(RangeWipe.IsSystemDisk(source.Info.Disk, catalog));
            WipeVolumeLocks.Overlapping(source, catalog, [(2 * MiB, MiB)], out IReadOnlyList<VolumeDeviceInfo> blocked);
            Assert.Single(blocked);
        }
    }

    [Fact]
    public void A_volume_that_cannot_be_locked_stops_the_wipe_before_writing()
    {
        string image = Path.Combine(_dir, "lock.img");
        WipeImages.WritePatternDisk(image, 8 * MiB);
        var access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 1, Size = 8 * MiB, Image = image }],
            Volumes = [new FakeVolumeSpec { Path = @"\\.\Q:", DriveLetter = "Q:", Disk = 1, Offset = MiB, Size = 2 * MiB, Lock = FakeLockBehavior.InUse }],
        });
        using (access)
        {
            IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(1), writable: true);
            using var source = new DeviceByteSource(handle, access, new DeviceOpenInfo { Path = handle.Path, DisplayName = "Disk 1", Disk = access.Enumerate().Disks[0] });
            var hooks = new RecordingWipeHooks();
            IReadOnlyList<VolumeDeviceInfo> volumes = WipeVolumeLocks.Overlapping(source, access.Enumerate(), [(MiB, MiB)], out _);
            access.ClearCalls();
            Assert.Throws<VolumeLockException>(() => WipeRunner.Run(Job(source, new WipeRange(MiB, MiB), ShredMethod.ZeroOnce), () => new DeviceWipeTarget(source),
                () => WipeVolumeLocks.Acquire(source, volumes, _ => false), hooks: hooks));
            Assert.Equal(0, access.WriteCount);
            Assert.Equal(WipeAuditOutcome.Failed, Assert.Single(hooks.Records).Outcome);
        }
    }

    [Fact]
    public void Unsaved_edits_inside_the_range_are_detected()
    {
        var doc = new Document(new Sources.MemoryByteSource(new byte[4096]));
        Assert.False(WipeRunner.HasUnsavedEditsIn(doc, 0, 4096));
        doc.Overwrite(2000, [1, 2, 3]);
        Assert.True(WipeRunner.HasUnsavedEditsIn(doc, 1000, 1500));
        Assert.False(WipeRunner.HasUnsavedEditsIn(doc, 3000, 1000));
    }

    [Fact]
    public void A_file_target_writes_directly_without_the_data_engine()
    {
        string path = Path.Combine(_dir, "file.bin");
        File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)0x41, 8192)]);
        using (var target = new FileWipeTarget(path, baseOffset: 1024, length: 4096))
        {
            WipeResult result = WipeEngine.Execute(target, [new WipeRange(0, 4096)], ShredMethod.ZeroOnce, verify: true);
            Assert.True(result.Succeeded);
        }

        byte[] data = File.ReadAllBytes(path);
        Assert.All(data.AsSpan(0, 1024).ToArray(), b => Assert.Equal(0x41, b));
        Assert.All(data.AsSpan(1024, 4096).ToArray(), b => Assert.Equal(0, b));
        Assert.All(data.AsSpan(5120).ToArray(), b => Assert.Equal(0x41, b));
    }

    [Fact]
    public void Random_streams_regenerate_the_same_bytes_at_any_position_and_differ_per_key()
    {
        using var a = new WipeRandomStream();
        using var b = new WipeRandomStream();
        byte[] whole = new byte[100];
        a.Fill(whole, 1000);
        byte[] part = new byte[37];
        a.Fill(part, 1023);
        Assert.Equal(whole.AsSpan(23, 37).ToArray(), part);
        byte[] other = new byte[100];
        b.Fill(other, 1000);
        Assert.NotEqual(whole, other);
    }

    [Fact]
    public void Flash_media_and_estimates_are_described_for_the_confirmation()
    {
        Assert.True(RangeWipe.IsFlashMedia(new DiskInfo { Number = 0, Bus = DiskBusType.Nvme }));
        Assert.True(RangeWipe.IsFlashMedia(new DiskInfo { Number = 0, Bus = DiskBusType.Usb, Removable = true }));
        Assert.False(RangeWipe.IsFlashMedia(new DiskInfo { Number = 0, Bus = DiskBusType.Sata }));
        Assert.Equal(TimeSpan.FromSeconds(4), WipeEngine.Estimate(100 * MiB, 3, verify: true));
    }

    /// <summary>読み直したときに 1 バイトだけ違う内容を返す書き込み先 (検証の不一致)。</summary>
    private sealed class BrokenReadbackTarget(IWipeTarget inner, long badAt) : IWipeTarget
    {
        public string Name => inner.Name;
        public long Length => inner.Length;
        public int SectorSize => inner.SectorSize;
        public void Write(long offset, ReadOnlySpan<byte> data) => inner.Write(offset, data);

        public void Read(long offset, Span<byte> buffer)
        {
            inner.Read(offset, buffer);
            if (badAt >= offset && badAt < offset + buffer.Length)
            {
                buffer[(int)(badAt - offset)] ^= 0xFF;
            }
        }

        public void Flush() => inner.Flush();
        public void Dispose() => inner.Dispose();
    }
}
