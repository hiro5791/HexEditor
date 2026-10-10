using HexEditor.Core.Processes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Processes;

/// <summary>ENG-35 / ANA-09 スナップショットの作成・保存・再オープン (compare 領域が比較で使うデータソース)。</summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-snap").FullName;

    private static ProcessMemoryByteSource OpenProcess(int pid, params FakeRegionSpec[] regions)
    {
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes = [new FakeProcessSpec { Pid = pid, Name = "TestTarget.exe", AddressLimit = 0x400000, Regions = [.. regions] }],
        });
        return new ProcessMemoryByteSource(access.Open(pid, false), access, new ProcessOpenInfo { DisplayName = $"TestTarget.exe (PID {pid})" });
    }

    [Fact]
    [Trait("TC", "TC-ANA-09-04")]
    public void A_saved_snapshot_reopens_with_the_same_addresses_and_content()
    {
        long baseAddr = 0x100000;
        using ProcessMemoryByteSource process = OpenProcess(100,
            new FakeRegionSpec { Base = baseAddr, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.ReadWrite, Data = "DEADBEEF" });
        string path = Path.Combine(_dir, "s1" + HexSnapshot.Extension);
        HexSnapshot.Capture(process, path);

        using SnapshotByteSource snapshot = SnapshotByteSource.Open(path);
        Assert.Equal(process.Memory.AddressLimit, snapshot.Length);

        // 同じアドレスに同じ内容。
        byte[] buffer = new byte[4];
        ReadResult result = snapshot.Read(baseAddr, buffer);
        Assert.True(result.IsComplete);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, buffer);

        // 領域マップに読める領域があり、その外は未割り当て。
        Assert.Equal(RegionAccess.Readable, Core.Sources.RegionMaps.At(snapshot.Regions, baseAddr)!.Access);
        Assert.Equal(RegionAccess.Unallocated, Core.Sources.RegionMaps.At(snapshot.Regions, 0)!.Access);
    }

    [Fact]
    public void Writable_only_scope_captures_only_writable_regions()
    {
        using ProcessMemoryByteSource process = OpenProcess(101,
            new FakeRegionSpec { Base = 0x100000, Size = 0x1000, State = RegionState.Commit, Protect = PageProtection.ReadWrite, Data = "01" },
            new FakeRegionSpec { Base = 0x200000, Size = 0x1000, State = RegionState.Commit, Protect = PageProtection.ReadOnly, Data = "02" });
        string path = Path.Combine(_dir, "w" + HexSnapshot.Extension);
        SnapshotMetadata metadata = HexSnapshot.Capture(process, path, SnapshotScope.WritableOnly);
        SnapshotRegion region = Assert.Single(metadata.Regions);
        Assert.Equal(0x100000, region.BaseAddress);
    }

    [Fact]
    [Trait("TC", "TC-ANA-09-03")]
    public void Two_snapshots_record_added_and_removed_regions()
    {
        // compare 領域が差分を計算するためのデータ: 2 つのスナップショットの領域の一覧が違うことを確かめる。
        long r1 = 0x100000, r2 = 0x300000;
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes = [new FakeProcessSpec { Pid = 102, Name = "TestTarget.exe", AddressLimit = 0x400000,
                Regions = [new FakeRegionSpec { Base = r1, Size = 0x10000, State = RegionState.Commit, Protect = PageProtection.ReadWrite }] }],
        });
        using var process = new ProcessMemoryByteSource(access.Open(102, false), access, new ProcessOpenInfo { DisplayName = "t" });
        string s1 = Path.Combine(_dir, "a" + HexSnapshot.Extension);
        SnapshotMetadata m1 = HexSnapshot.Capture(process, s1);

        // R1 を解放し、R2 を確保する。
        access.Process(102).SetRegions([new MemoryRegion(r2, 0x20000, RegionState.Commit, PageProtection.ReadWrite, RegionType.Private, null, r2)]);
        process.RefreshRegions();
        string s2 = Path.Combine(_dir, "b" + HexSnapshot.Extension);
        SnapshotMetadata m2 = HexSnapshot.Capture(process, s2);

        Assert.Contains(m1.Regions, r => r.BaseAddress == r1);
        Assert.DoesNotContain(m2.Regions, r => r.BaseAddress == r1);
        Assert.Contains(m2.Regions, r => r.BaseAddress == r2);
    }

    [Fact]
    [Trait("TC", "TC-ANA-09-05")]
    public void Temporary_snapshots_are_deleted_on_dispose_but_kept_ones_remain()
    {
        using ProcessMemoryByteSource process = OpenProcess(103,
            new FakeRegionSpec { Base = 0x100000, Size = 0x1000, State = RegionState.Commit, Protect = PageProtection.ReadWrite, Data = "01" });
        var manager = new SnapshotManager(_dir);
        SnapshotByteSource first = manager.CreateTemporary(process);
        SnapshotByteSource second = manager.CreateTemporary(process);
        string firstPath = first.Path;
        string secondPath = second.Path;
        first.Dispose();
        second.Dispose();

        // 2 つ目を「名前を付けて保存」した扱いにして管理から外す。
        string kept = Path.Combine(_dir, "kept" + HexSnapshot.Extension);
        File.Copy(secondPath, kept);
        manager.Keep(secondPath);
        File.Delete(secondPath);

        manager.Dispose();
        Assert.False(File.Exists(firstPath));
        Assert.True(File.Exists(kept));
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
