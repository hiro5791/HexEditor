using HexEditor.Core.Compare;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Tests.Processes;

/// <summary>ANA-09 プロセスメモリのスナップショットの比較 (領域ごとの比較。偽のプロセスで TestTarget の手順を再現する)。</summary>
public sealed class SnapshotCompareTests : IDisposable
{
    private const int Pid = 200;
    private const long PrivateBase = 0x100000;
    private const long ModuleBase = 0x400000;
    private const long DataSection = 0x402000;

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-snapcmp").FullName;

    private static (FakeProcessAccess Access, ProcessMemoryByteSource Source) Process()
    {
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes =
            [
                new FakeProcessSpec
                {
                    Pid = Pid, Name = "TestTarget.exe", AddressLimit = 0x800000,
                    Regions =
                    [
                        new FakeRegionSpec { Base = PrivateBase, Size = 0x10000, Protect = PageProtection.ReadWrite, Data = "00" },
                        new FakeRegionSpec { Base = ModuleBase, Size = 0x1000, Protect = PageProtection.ReadOnly, Type = RegionType.Image, Data = "4D5A" },
                        new FakeRegionSpec { Base = DataSection, Size = 0x1000, Protect = PageProtection.ReadWrite, Type = RegionType.Image, Data = "00" },
                    ],
                    Modules = [new ProcessModule("TestTarget.exe", ModuleBase, 0x3000, @"C:\t\TestTarget.exe")],
                },
            ],
        });
        return (access, new ProcessMemoryByteSource(access.Open(Pid, false), access, new ProcessOpenInfo { DisplayName = "TestTarget.exe (PID 200)" }));
    }

    private SnapshotByteSource Snapshot(ProcessMemoryByteSource process, string name)
    {
        string path = Path.Combine(_dir, name + HexSnapshot.Extension);
        HexSnapshot.Capture(process, path);
        return SnapshotByteSource.Open(path, name);
    }

    private static CompareResult Compare(Sources.IByteSource left, Sources.IByteSource right)
    {
        var result = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromSource(left)), CompareRange.Whole(CompareData.FromSource(right)));
        DataComparer.Run(new CompareOptions(), result);
        return result;
    }

    [Fact]
    [Trait("TC", "TC-ANA-09-01")]
    public void Two_snapshots_list_the_changed_addresses_with_region_names()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Process();
        _ = access;
        using (process)
        {
            using SnapshotByteSource s1 = Snapshot(process, "S1");
            access.Process(Pid).WriteRaw(PrivateBase + 0x20, [1, 2, 3, 4]);
            access.Process(Pid).WriteRaw(DataSection + 0x10, [9, 9, 9, 9]);
            using SnapshotByteSource s2 = Snapshot(process, "S2");

            using CompareResult result = Compare(s1, s2);
            Assert.True(result.ByRegion);
            Assert.Equal(2, result.DiffCount);
            DiffRange first = result.Diffs[0];
            DiffRange second = result.Diffs[1];
            Assert.Equal((PrivateBase + 0x20, 4L), (first.LeftOffset, first.LeftLength));
            Assert.Equal((DataSection + 0x10, 4L), (second.LeftOffset, second.LeftLength));

            // 領域名: private の領域は「private 0x…」、モジュールの中は「TestTarget.exe+0x…」(モジュールの先頭からの位置)。
            Assert.Equal($"private 0x{PrivateBase:X}", RegionComparer.RegionName(s1.Regions, first.LeftOffset, s1.Modules));
            Assert.Equal($"TestTarget.exe+0x{DataSection + 0x10 - ModuleBase:X}", RegionComparer.RegionName(s1.Regions, second.LeftOffset, s1.Modules));

            // アドレス空間全体ではなく、領域の合計だけを比べている。
            Assert.Equal(0x12000, result.ComparedBytes);
        }
    }

    [Fact]
    [Trait("TC", "TC-ANA-09-02")]
    public void A_snapshot_is_compared_with_the_current_memory()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Process();
        _ = access;
        using (process)
        {
            using SnapshotByteSource s1 = Snapshot(process, "S1");
            access.Process(Pid).WriteRaw(PrivateBase, [1, 2, 3, 4, 5, 6, 7, 8]);

            using CompareResult result = Compare(s1, process);
            DiffRange diff = Assert.Single(result.Diffs.Enumerate().Take(10));
            Assert.Equal((DiffKind.Changed, PrivateBase, 8L), (diff.Kind, diff.LeftOffset, diff.LeftLength));
        }
    }

    [Fact]
    public void Freed_and_allocated_regions_are_region_removals_and_additions()
    {
        // ANA-09 の仕様 5 (TC-ANA-09-03 の比較の部分): 片方にしかない領域は「領域の削除」「領域の追加」。
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Process();
        _ = access;
        using (process)
        {
            using SnapshotByteSource s1 = Snapshot(process, "S1");
            long r2 = 0x300000;
            FakeProcess target = access.Process(Pid);
            target.SetRegions([.. target.Regions.Where(r => r.BaseAddress != PrivateBase),
                new MemoryRegion(r2, 0x20000, RegionState.Commit, PageProtection.ReadWrite, RegionType.Private, null, r2)]);
            process.RefreshRegions();
            using SnapshotByteSource s2 = Snapshot(process, "S2");

            using CompareResult result = Compare(s1, s2);
            var changes = result.Diffs.Enumerate().Take(10).Select(d => (d, RegionComparer.ChangeOf(result, d))).ToList();
            Assert.Contains(changes, c => c.Item2 == RegionChange.Removed && c.d.LeftOffset == PrivateBase && c.d.LeftLength == 0x10000);
            Assert.Contains(changes, c => c.Item2 == RegionChange.Added && c.d.RightOffset == r2 && c.d.RightLength == 0x20000);
        }
    }

    [Fact]
    public void Pages_that_could_not_be_read_are_unreadable_and_not_counted()
    {
        // ANA-09 の仕様 8: スナップショットで読めなかったページは「読み込み不可」で、差分に数えない。
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Process();
        using (process)
        {
            string path = Path.Combine(_dir, "gap" + HexSnapshot.Extension);
            SnapshotMetadata metadata = HexSnapshot.Capture(process, path);
            metadata = metadata with { UnreadablePages = [new SnapshotGap(PrivateBase + 0x1000, 0x1000)] };
            using var withGap = new SnapshotByteSource(path, metadata, "gap");
            byte[] buffer = new byte[0x3000];
            Sources.ReadResult read = withGap.Read(PrivateBase, buffer);
            Assert.Contains(read.Unreadable, u => u.Offset == PrivateBase + 0x1000 && u.Length == 0x1000);

            access.Process(Pid).WriteRaw(PrivateBase + 0x20, [1]);
            using SnapshotByteSource later = Snapshot(process, "later");
            using CompareResult result = Compare(withGap, later);
            Assert.Equal(1, result.CountOf(DiffKind.Unreadable));
            Assert.Equal(1, result.CountedDiffs);
            Assert.Equal(0, result.DifferentBytes - 1);
        }
    }

    [Fact]
    public void Snapshot_files_are_recognised_by_their_header()
    {
        // ANA-09 の仕様 3: 「ファイルを選択...」で選んだ .hexsnap も、スナップショットとして領域ごとに比べる。
        (_, ProcessMemoryByteSource process) = Process();
        using (process)
        {
            using SnapshotByteSource s1 = Snapshot(process, "S1");
            Assert.True(HexSnapshot.IsSnapshotFile(s1.Path));
            string other = Path.Combine(_dir, "plain.bin");
            File.WriteAllBytes(other, new byte[64]);
            Assert.False(HexSnapshot.IsSnapshotFile(other));
            Assert.False(HexSnapshot.IsSnapshotFile(Path.Combine(_dir, "missing.bin")));
        }
    }

    [Fact]
    public void Capturing_without_read_rights_reports_access_denied_and_leaves_no_file()
    {
        // ANA-09 の「エラー」: 権限不足でプロセスを読めない。呼び出し側が昇格した補助プロセスでの再試行を提案する (偽のプロセスで再現)。
        var spec = new FakeProcessListSpec
        {
            Processes =
            [
                new FakeProcessSpec
                {
                    Pid = Pid, Name = "Locked.exe", AddressLimit = 0x800000, ReadNeedsElevation = true,
                    Regions = [new FakeRegionSpec { Base = PrivateBase, Size = 0x2000, Protect = PageProtection.ReadWrite, Data = "11" }],
                },
            ],
        };
        var direct = new FakeProcessAccess(spec, elevated: false);
        string path = Path.Combine(_dir, "denied" + HexSnapshot.Extension);
        using (var source = new ProcessMemoryByteSource(direct.Open(Pid, false), direct, new ProcessOpenInfo { DisplayName = "Locked.exe" }))
        {
            ProcessAccessException ex = Assert.Throws<ProcessAccessException>(() => HexSnapshot.Capture(source, path));
            Assert.Equal(ProcessOpenFailure.AccessDenied, ex.Failure);
        }

        Assert.False(File.Exists(path));

        var elevated = new FakeProcessAccess(spec, elevated: true);
        using var allowed = new ProcessMemoryByteSource(elevated.Open(Pid, false), elevated, new ProcessOpenInfo { DisplayName = "Locked.exe" });
        SnapshotMetadata metadata = HexSnapshot.Capture(allowed, path);
        Assert.Single(metadata.Regions);
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
