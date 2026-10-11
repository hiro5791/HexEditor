using System.Diagnostics;
using System.Security.Cryptography;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Devices;

/// <summary>
/// ディスク・プロセスメモリのドキュメント (ENG-07、ENG-18、ENG-21、ENG-29、ENG-32)。仮想ディスク・TestTarget の代わりに偽のデバイス・プロセスを使う
/// (実際のディスク・他のプロセスには触れない)。
/// </summary>
public sealed class DeviceDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-devdoc").FullName;

    private static (FakeDeviceAccess Access, DeviceByteSource Source) Disk(long size = 64L * 1024 * 1024, bool writable = false)
    {
        var access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 1, SectorSize = 512, Size = size, Serial = "SER" }],
        });
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(1), writable);
        return (access, new DeviceByteSource(handle, access, new DeviceOpenInfo { Path = handle.Path, DisplayName = "Disk 1" }));
    }

    [Fact]
    [Trait("TC", "TC-ENG-07-01")]
    public void A_disk_document_cannot_change_its_length()
    {
        // ENG-07: 長さ固定のデータソース。挿入・削除は「長さを変えられない」で拒否し、上書きはできる。
        (FakeDeviceAccess access, DeviceByteSource source) = Disk(writable: true);
        using (access)
        using (var doc = new Document(source))
        {
            Assert.False(doc.CanResize);
            Assert.ThrowsAny<Exception>(() => doc.Insert(0x10, [1, 2]));
            Assert.ThrowsAny<Exception>(() => doc.Delete(0x10, 2));
            doc.Overwrite(0x10, [1, 2]);
            Assert.Equal(64L * 1024 * 1024, doc.Length);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-18-01")]
    public void Reloading_a_disk_keeps_the_changed_bytes_and_shows_the_new_content()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = Disk();
        using (access)
        using (var doc = new Document(source))
        {
            doc.Overwrite(0x1000, [0xDE, 0xAD, 0xBE, 0xEF]);

            // 1. 別のハンドル (テストコード) がセクタ 16 を 5A で埋める。2. 再読み込み。
            access.Disk(1).Write(0x2000, Enumerable.Repeat((byte)0x5A, 512).ToArray());
            doc.RefreshFromSource();

            byte[] changed = new byte[4];
            byte[] fresh = new byte[4];
            doc.Current.Read(0x1000, changed);
            doc.Current.Read(0x2000, fresh);
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, changed);
            Assert.Equal(new byte[] { 0x5A, 0x5A, 0x5A, 0x5A }, fresh);
            Assert.True(doc.IsModified);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-21-03")]
    public void Saving_a_disk_as_a_file_writes_the_image_and_leaves_the_disk_unchanged()
    {
        (FakeDeviceAccess access, DeviceByteSource source) = Disk(size: 4L * 1024 * 1024);
        using (access)
        using (var doc = new Document(source))
        {
            byte[] original = new byte[1];
            access.Disk(1).Read(0x10000, original);
            doc.Overwrite(0x10000, [0xAB]);

            string image = Path.Combine(_dir, "disk.img");
            DocumentSaver.Save(doc.Current, image);

            // 書き出したファイルはディスクの全体 (0x10000 だけ AB)。ディスクは元のまま。
            byte[] expected = new byte[source.Length];
            access.Disk(1).Read(0, expected);
            expected[0x10000] = 0xAB;
            Assert.Equal(source.Length, new FileInfo(image).Length);
            Assert.Equal(SHA256.HashData(expected), SHA256.HashData(File.ReadAllBytes(image)));
            byte[] after = new byte[1];
            access.Disk(1).Read(0x10000, after);
            Assert.Equal(original, after);
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-03")]
    public void A_two_terabyte_disk_opens_and_shows_the_first_sector_quickly()
    {
        var watch = Stopwatch.StartNew();
        (FakeDeviceAccess access, DeviceByteSource source) = Disk(size: 2L * 1024 * 1024 * 1024 * 1024);
        using (access)
        using (var doc = new Document(source))
        {
            byte[] first = new byte[512];
            Assert.True(doc.Current.Read(0, first).IsComplete);
            watch.Stop();
            Assert.Equal(2_199_023_255_552L, doc.Length);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed.TotalMilliseconds:F0} ms");
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-32-06")]
    public void Guard_pages_are_shown_as_no_access_without_touching_them()
    {
        const int pid = 300;
        var regions = new List<FakeRegionSpec>();
        for (int i = 0; i < 4; i++)
        {
            // スタック: ガードページ 1 つと、その上の確定したページ。
            long stack = 0x200000 + i * 0x100000L;
            regions.Add(new FakeRegionSpec { Base = stack, Size = 0x1000, Protect = PageProtection.ReadWrite | PageProtection.Guard });
            regions.Add(new FakeRegionSpec { Base = stack + 0x1000, Size = 0x10000, Protect = PageProtection.ReadWrite, Data = "11" });
        }

        for (int i = 0; i < 16; i++)
        {
            regions.Add(new FakeRegionSpec { Base = 0x1000000 + i * 0x3000L, Size = 0x1000, Protect = PageProtection.ReadWrite | PageProtection.Guard });
        }

        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes = [new FakeProcessSpec { Pid = pid, Name = "TestTarget.exe", AddressLimit = 0x2000000, Regions = regions }],
        });
        using var process = new ProcessMemoryByteSource(access.Open(pid, false), access, new ProcessOpenInfo { DisplayName = "TestTarget.exe" });
        using var doc = new Document(process);

        // 全体を読む (検索と同じく、読める範囲も読めない範囲もまとめて)。
        byte[] buffer = new byte[1 << 20];
        var states = new List<UnreadableRange>();
        for (long at = 0; at < doc.Length; at += buffer.Length)
        {
            ReadResult r = doc.Current.Read(at, buffer);
            states.AddRange(r.Unreadable);
        }

        Assert.False(access.Process(pid).Exited);
        Assert.Equal(0, access.Process(pid).GuardViolations);
        Assert.Contains(states, u => u.Offset <= 0x200000 && u.End >= 0x201000 && u.Reason == UnreadableReason.AccessDenied);
        Assert.Contains(states, u => u.Offset <= 0x1000000 && u.End >= 0x1001000 && u.Reason == UnreadableReason.AccessDenied);
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
