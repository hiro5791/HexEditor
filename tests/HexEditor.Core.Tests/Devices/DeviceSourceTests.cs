using HexEditor.Core.Devices;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Devices;

/// <summary>ENG-29 物理ディスク・論理ボリュームを開く (データソースの読み込み・切断。Core レベル)。</summary>
public sealed class DeviceSourceTests
{
    private static (FakeDeviceAccess Access, DeviceByteSource Source) Open(FakeDiskSpec disk, bool writable = false, bool record = false)
    {
        var access = new FakeDeviceAccess(new FakeDeviceSpec { Elevated = true, Disks = [disk] });
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(disk.Number), writable);
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo
        {
            Path = handle.Path,
            DisplayName = $"Disk {disk.Number}",
            SerialNumber = disk.Serial,
            Route = DeviceRoute.Elevated,
        }) { RecordReads = record };
        return (access, source);
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-04")]
    public void Reads_are_aligned_to_the_logical_sector_size()
    {
        (_, DeviceByteSource source) = Open(new FakeDiskSpec { Number = 0, SectorSize = 4096, Size = 64 * 1024 * 1024, Seed = 5 }, record: true);
        using (source)
        {
            Assert.Equal(4096, source.LogicalSectorSize);

            // セクタの途中から半端な長さを読んでも、デバイスへの要求はすべて 4,096 の倍数 (仕様 6)。
            byte[] buffer = new byte[100];
            source.Read(12288 + 10, buffer);
            Assert.All(source.ReadLog, r =>
            {
                Assert.Equal(0, r.Offset % 4096);
                Assert.Equal(0, r.Length % 4096);
            });
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-05")]
    public void The_last_partial_sector_is_readable()
    {
        // 末尾が端数のセクタを含むボリュームでも、末尾まで読める (仕様 4)。
        (FakeDeviceAccess access, DeviceByteSource source) = Open(new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1000 * 512, Seed = 7 });
        using (source)
        {
            byte[] expected = new byte[512];
            access.Disk(0).Read(source.Length - 512, expected);
            byte[] last = new byte[512];
            ReadResult result = source.Read(source.Length - 512, last);
            Assert.True(result.IsComplete);
            Assert.Equal(expected, last);
        }
    }

    [Fact]
    public void A_bad_sector_reads_as_an_io_error_but_the_rest_reads()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = Open(new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1024 * 1024 });
        using (source)
        {
            access.Disk(0).BadRanges.Add((512, 512));
            byte[] buffer = new byte[1536];
            ReadResult result = source.Read(0, buffer);
            // 1 回の要求 (1 MiB 以内) はまとめてデバイスに出すため、不良セクタを含む要求全体が読めない範囲になる。
            // セクタ単位に分けて読み直すのはブロックキャッシュ (ENG-06 の仕様 8) の役目。
            Assert.False(result.IsComplete);
            UnreadableRange bad = Assert.Single(result.Unreadable);
            Assert.Equal(UnreadableReason.IoError, bad.Reason);
            Assert.True(bad.Offset <= 512 && bad.End >= 1024);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-06")]
    public void Removing_the_device_marks_it_disconnected()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = Open(new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1024 * 1024 });
        using (source)
        {
            SourceChangeKind? change = null;
            source.Changed += (_, e) => change = e.Kind;
            access.Disk(0).Removed = true;

            byte[] buffer = new byte[512];
            ReadResult result = source.Read(0, buffer);
            Assert.False(result.IsComplete);
            Assert.Equal(UnreadableReason.Disconnected, Assert.Single(result.Unreadable).Reason);
            Assert.True(source.IsDisconnected);
            Assert.Equal(SourceChangeKind.Disconnected, change);
            Assert.False(source.Capabilities.HasFlag(SourceCapabilities.CanWrite));
        }
    }

    [Fact]
    public void Identity_uses_the_path_and_serial_so_the_same_device_is_not_opened_twice()
    {
        Assert.Equal(DeviceByteSource.IdentityOf(@"\\.\PhysicalDrive1", "S1"), DeviceByteSource.IdentityOf(@"\\.\physicaldrive1", "S1"));
        Assert.NotEqual(DeviceByteSource.IdentityOf(@"\\.\PhysicalDrive1", "S1"), DeviceByteSource.IdentityOf(@"\\.\PhysicalDrive1", "S2"));
    }

    [Fact]
    public void A_removable_usb_volume_is_opened_without_admin()
    {
        var access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = false,
            Disks = [new FakeDiskSpec { Number = 0, SectorSize = 512, Size = 1024 * 1024, RequiresAdmin = true }],
            Volumes = [new FakeVolumeSpec { Path = @"\\.\E:", DriveLetter = "E:", Disk = 0, Size = 1024 * 1024, RemovableUsb = true, RequiresAdmin = false }],
        });

        // USB ストレージのボリュームは管理者権限なしで開ける (ENG-29 の仕様 3 の 1)。
        using IDeviceHandle usb = access.Open(@"\\.\E:", writable: false);
        Assert.NotNull(usb);

        // 物理ディスクは管理者権限がないと開けない。
        Assert.Throws<DeviceException>(() => access.Open(@"\\.\PhysicalDrive0", writable: false));
    }
}
