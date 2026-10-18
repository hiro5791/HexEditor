using System.Buffers.Binary;
using System.Diagnostics;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;
using ProcessModule = HexEditor.Core.Processes.ProcessModule;
using System.Globalization;
using System.Security.Cryptography;
using HexEditor.Core.Compare;
using HexEditor.Core.Operations;
using HexEditor.Core.Processes;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Processes;

/// <summary>
/// テスト自身が起動した TestTarget (tools/TestTarget) を読むテスト (テスト方針 7.1)。他のプロセスには触れない。書き込みはしない
/// (値の書き換えは TestTarget 自身が標準入力の命令で行う)。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TestTargetHost : IDisposable
{
    private readonly Process _process;
    private readonly Dictionary<string, string> _lines = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _poked = new(0);

    private TestTargetHost(Process process) => _process = process;

    public int Pid => _process.Id;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "testtarget", "TestTarget.exe");

    /// <summary>起動して READY まで待つ。</summary>
    public static TestTargetHost Start(params string[] args)
    {
        Assert.True(File.Exists(ExePath), ExePath);
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        var ready = new TaskCompletionSource();
        var process = Process.Start(info) ?? throw new InvalidOperationException("TestTarget を起動できません。");
        var host = new TestTargetHost(process);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line)
            {
                return;
            }

            if (line == "READY")
            {
                ready.TrySetResult();
            }
            else if (line == "POKED")
            {
                host._poked.Release();
            }
            else if (line.IndexOf(' ') is var space and > 0)
            {
                lock (host._lock)
                {
                    host._lines[line[..space]] = line[(space + 1)..];
                }
            }
        };
        process.BeginOutputReadLine();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(60)))
        {
            host.Dispose();
            throw new TimeoutException("TestTarget did not become ready.");
        }

        return host;
    }

    public string[] Line(string key)
    {
        lock (_lock)
        {
            return _lines[key].Split(' ');
        }
    }

    public static long Hex(string text) =>
        long.Parse(text.StartsWith("0x", StringComparison.Ordinal) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>TestTarget 自身に自分のメモリを書き換えさせる。</summary>
    public void Poke(long address, string hex)
    {
        _process.StandardInput.WriteLine($"poke 0x{address:X} {hex}");
        _process.StandardInput.Flush();
        Assert.True(_poked.Wait(TimeSpan.FromSeconds(30)), "POKED が来ない");
    }

    /// <summary>読み取り専用で開く (アドレス空間全体)。</summary>
    public ProcessMemoryByteSource Open(IProcessAccess? access = null, Func<IProcessMemory, IProcessMemory>? wrap = null)
    {
        access ??= new Win32ProcessAccess();
        IProcessMemory memory = access.Open(Pid, writable: false);
        return new ProcessMemoryByteSource(wrap?.Invoke(memory) ?? memory, access, new ProcessOpenInfo { DisplayName = $"TestTarget.exe (PID {Pid})" });
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        _process.Dispose();
        _poked.Dispose();
    }
}

/// <summary>1 MiB ごとに遅延を入れるプロセスメモリ (TC-ENG-35-04 の「遅いデータソース」)。</summary>
public sealed class SlowProcessMemory(IProcessMemory inner, TimeSpan delayPerMiB) : IProcessMemory
{
    public int Pid => inner.Pid;

    public string Name => inner.Name;

    public ProcessArchitecture Architecture => inner.Architecture;

    public long AddressLimit => inner.AddressLimit;

    public bool Writable => inner.Writable;

    public bool HasExited => inner.HasExited;

    public event EventHandler? Exited
    {
        add => inner.Exited += value;
        remove => inner.Exited -= value;
    }

    public IReadOnlyList<MemoryRegion> QueryRegions() => inner.QueryRegions();

    public IReadOnlyList<ProcessModule> EnumModules() => inner.EnumModules();

    public int Read(long address, Span<byte> buffer, out int read)
    {
        Thread.Sleep(delayPerMiB * Math.Max(1.0, buffer.Length / (1024.0 * 1024)));
        return inner.Read(address, buffer, out read);
    }

    public int Write(long address, ReadOnlySpan<byte> data, out int written) => inner.Write(address, data, out written);

    public int Protect(long address, long size, uint protect, out uint oldProtect) => inner.Protect(address, size, protect, out oldProtect);

    public void Dispose() => inner.Dispose();
}

/// <summary>ENG-35 プロセスのスナップショットとダンプ保存。</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ProcessCaptureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-eng35").FullName;

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

    private static LongRunningOperation NewOperation() => new("capture", OperationKind.ReadOnly, null, null, TimeProvider.System);

    // ---- TestTarget (結合) ----

    [Fact]
    [Trait("TC", "TC-ENG-35-01")]
    [Trait("Category", "Integration")]
    public void A_saved_snapshot_of_TestTarget_reopens_with_the_same_addresses_content_and_regions()
    {
        using TestTargetHost target = TestTargetHost.Start("--random", "1048576", "--seed", "53");
        string[] random = target.Line("RANDOM");
        long randomAddress = TestTargetHost.Hex(random[0]);
        using ProcessMemoryByteSource process = target.Open();

        // 1. 読めるすべての領域を t1.hexsnap に保存する。
        string path = Path.Combine(_dir, "t1" + HexSnapshot.Extension);
        SnapshotCaptureResult result = HexSnapshot.Write(process, path, new ProcessCaptureOptions());

        // 2. 先頭 8 バイトが HXSNAP01。
        byte[] head = new byte[8];
        using (FileStream f = File.OpenRead(path))
        {
            f.ReadExactly(head);
        }

        Assert.Equal("HXSNAP01"u8.ToArray(), head);

        // 3. 開いて、乱数の領域の SHA-256 と、実行ファイルのベースアドレスの 2 バイトを取る。
        using SnapshotByteSource snapshot = SnapshotByteSource.Open(path);
        byte[] data = new byte[1048576];
        Assert.True(snapshot.Read(randomAddress, data).IsComplete);
        Assert.Equal(random[2], Convert.ToHexString(SHA256.HashData(data)));
        ProcessModule exe = Assert.Single(snapshot.Modules, m => m.Name.Equals("TestTarget.exe", StringComparison.OrdinalIgnoreCase));
        byte[] mz = new byte[2];
        snapshot.Read(exe.BaseAddress, mz);
        Assert.Equal("MZ"u8.ToArray(), mz);
        Assert.False(snapshot.Capabilities.HasFlag(SourceCapabilities.CanWrite));

        // 4. 読める領域の開始アドレス・サイズ・保護属性が、取得したときのライブの一覧と一致する。
        var live = process.MemoryRegions.Where(r => r.State == RegionState.Commit && r.IsReadable)
            .ToDictionary(r => r.BaseAddress);
        Assert.NotEmpty(result.Metadata.Regions);
        foreach (SnapshotRegion region in result.Metadata.Regions)
        {
            Assert.True(live.TryGetValue(region.BaseAddress, out MemoryRegion? match), $"0x{region.BaseAddress:X}");
            Assert.Equal((match!.Size, match.Protect), (region.Size, region.Protect));
        }

    }

    [Fact]
    [Trait("TC", "TC-ENG-35-02")]
    [Trait("Category", "Integration")]
    public void Snapshots_before_and_after_a_change_differ_only_at_the_changed_address()
    {
        using TestTargetHost target = TestTargetHost.Start("--random", "1048576", "--seed", "53");
        long randomAddress = TestTargetHost.Hex(target.Line("RANDOM")[0]);
        using ProcessMemoryByteSource process = target.Open();

        string p1 = Path.Combine(_dir, "t1" + HexSnapshot.Extension);
        HexSnapshot.Write(process, p1, new ProcessCaptureOptions());
        target.Poke(randomAddress + 0x100, "00000000");
        string p2 = Path.Combine(_dir, "t2" + HexSnapshot.Extension);
        HexSnapshot.Write(process, p2, new ProcessCaptureOptions());

        using SnapshotByteSource s1 = SnapshotByteSource.Open(p1);
        using SnapshotByteSource s2 = SnapshotByteSource.Open(p2);
        var compared = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromSource(s1)), CompareRange.Whole(CompareData.FromSource(s2)));
        using (compared)
        {
            DataComparer.Run(new CompareOptions(), compared);
            long end = randomAddress + 1048576;
            List<DiffRange> inRandom = [.. compared.Diffs.Enumerate().Where(d => d.LeftOffset < end && d.LeftOffset + d.LeftLength > randomAddress)];
            DiffRange diff = Assert.Single(inRandom);
            Assert.True(diff.LeftOffset >= randomAddress + 0x100 && diff.LeftOffset + diff.LeftLength <= randomAddress + 0x104,
                $"0x{diff.LeftOffset:X} +{diff.LeftLength}");
        }
    }

    [CdbFact]
    [Trait("TC", "TC-ENG-35-03")]
    [Trait("Category", "Integration")]
    public void A_minidump_of_TestTarget_opens_in_cdb_with_modules_and_memory()
    {
        using TestTargetHost target = TestTargetHost.Start("--random", "1048576", "--seed", "53");
        long randomAddress = TestTargetHost.Hex(target.Line("RANDOM")[0]);
        using ProcessMemoryByteSource process = target.Open();
        string dump = Path.Combine(_dir, "t" + MinidumpWriter.Extension);
        MinidumpWriter.Write(process, dump, new ProcessCaptureOptions());
        byte[] expected = new byte[16];
        Assert.Equal(0, process.Memory.Read(randomAddress, expected, out _));
        process.Dispose();
        target.Dispose();

        string output = CdbFactAttribute.Run(dump, $"lm; db 0x{randomAddress:X} L10; q", _dir);
        Assert.Contains("TestTarget", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ntdll", output, StringComparison.OrdinalIgnoreCase);
        string bytes = string.Join(' ', expected.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        Assert.Contains(bytes[..23] + "-" + bytes[24..], output, StringComparison.Ordinal);
    }

    [CdbFact]
    [Trait("Category", "Integration")]
    public void A_minidump_without_thread_states_still_opens_in_cdb()
    {
        // スレッドの状態を取れない経路 (補助プロセス・偽のプロセス) では、空のレジスタのスレッドで代用する。
        (_, ProcessMemoryByteSource process) = Fake();
        string dump = Path.Combine(_dir, "fake" + MinidumpWriter.Extension);
        using (process)
        {
            MinidumpWriter.Write(process, dump, new ProcessCaptureOptions());
        }

        string output = CdbFactAttribute.Run(dump, "db 0x100000 L4; q", _dir);
        Assert.Contains("11 22 33 44", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TC", "TC-ENG-35-04")]
    [Trait("Category", "Integration")]
    public async Task Cancelling_during_the_capture_leaves_no_file()
    {
        using TestTargetHost target = TestTargetHost.Start("--commit", "512");
        long commit = TestTargetHost.Hex(target.Line("COMMIT")[0]);
        using ProcessMemoryByteSource process = target.Open(wrap: m => new SlowProcessMemory(m, TimeSpan.FromMilliseconds(10)));
        string folder = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        string path = Path.Combine(folder, "t" + HexSnapshot.Extension);

        // 確保した 512 MiB の領域だけを対象にして、進捗が 20% を超えたらキャンセルする。
        LongRunningOperation op = NewOperation();
        var options = new ProcessCaptureOptions { Scope = SnapshotScope.Selected, SelectedRanges = [(commit, 512L * 1024 * 1024)] };
        Task capture = Task.Run(() => HexSnapshot.Write(process, path, options, op));
        await WaitUntilAsync(() => op.Fraction > 0.2 || capture.IsCompleted);
        op.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);

        Assert.Empty(Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), "timeout");
            await Task.Delay(20);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Pausing_TestTarget_suspends_and_resumes_its_threads()
    {
        using TestTargetHost target = TestTargetHost.Start("--random", "4096");
        using ProcessMemoryByteSource process = target.Open();
        var suspend = Assert.IsAssignableFrom<IProcessSuspend>(process.Memory);
        using (ProcessCapture.PauseScope pause = ProcessCapture.Pause(process.Memory, pause: true))
        {
            Assert.True(pause.IsPaused);
            Assert.False(pause.Failed);
        }

        // 再開した後は標準入力の命令に応える (停止したままではない)。
        target.Poke(TestTargetHost.Hex(target.Line("RANDOM")[0]), "01");
        _ = suspend;
    }

    // ---- 偽のプロセス ----

    private static (FakeProcessAccess Access, ProcessMemoryByteSource Source) Fake(bool cannotSuspend = false)
    {
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes =
            [
                new FakeProcessSpec
                {
                    Pid = 300, Name = "TestTarget.exe", AddressLimit = 0x800000, CannotSuspend = cannotSuspend,
                    Regions =
                    [
                        new FakeRegionSpec { Base = 0x100000, Size = 0x4000, Protect = PageProtection.ReadWrite, Data = "11223344" },
                        new FakeRegionSpec { Base = 0x200000, Size = 0x2000, Protect = PageProtection.ReadOnly, Data = "AABB" },
                        new FakeRegionSpec { Base = 0x400000, Size = 0x1000, Protect = PageProtection.ReadOnly, Type = RegionType.Image, Data = "4D5A" },
                        new FakeRegionSpec { Base = 0x500000, Size = 0x1000, State = RegionState.Reserve },
                    ],
                    Modules = [new ProcessModule("TestTarget.exe", 0x400000, 0x1000, @"C:\t\TestTarget.exe")],
                },
            ],
        });
        return (access, new ProcessMemoryByteSource(access.Open(300, false), access, new ProcessOpenInfo { DisplayName = "TestTarget.exe (PID 300)" }));
    }

    [Fact]
    public void Pausing_the_target_reads_every_region_while_it_is_suspended()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            FakeProcess fake = access.Process(300);
            SnapshotCaptureResult result = HexSnapshot.Write(process, Path.Combine(_dir, "p.hexsnap"), new ProcessCaptureOptions { PauseTarget = true });
            Assert.False(result.Summary.PauseFailed);
            Assert.Equal(0x7000, fake.BytesReadWhileSuspended);
            Assert.Equal(0, fake.SuspendCount); // 再開した
        }
    }

    [Fact]
    public void A_target_that_cannot_be_paused_is_captured_with_a_warning()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Fake(cannotSuspend: true);
        using (process)
        {
            SnapshotCaptureResult result = HexSnapshot.Write(process, Path.Combine(_dir, "p.hexsnap"), new ProcessCaptureOptions { PauseTarget = true });
            Assert.True(result.Summary.PauseFailed);
            Assert.Equal(3, result.Metadata.Regions.Count);
            Assert.Equal(0, access.Process(300).BytesReadWhileSuspended);
        }
    }

    [Fact]
    public void The_selected_scope_captures_regions_overlapping_the_selection()
    {
        (_, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            // メモリマップのパネルでモジュールを選んだ (モジュールの範囲に重なる領域を含める)。
            var options = new ProcessCaptureOptions { Scope = SnapshotScope.Selected, SelectedRanges = [(0x400000, 0x1000)] };
            SnapshotCaptureResult result = HexSnapshot.Write(process, Path.Combine(_dir, "s.hexsnap"), options);
            SnapshotRegion region = Assert.Single(result.Metadata.Regions);
            Assert.Equal((0x400000L, RegionType.Image), (region.BaseAddress, region.Type));
        }
    }

    [Fact]
    public void Unreadable_pages_are_reported_by_region_count_and_bytes()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            // 取得の直前に 2 ページ目を読めなくする (領域の一覧は古いまま)。
            access.Process(300).SetRegions(
            [
                new MemoryRegion(0x100000, 0x1000, RegionState.Commit, PageProtection.ReadWrite, RegionType.Private),
                new MemoryRegion(0x101000, 0x1000, RegionState.Commit, PageProtection.NoAccess, RegionType.Private),
                new MemoryRegion(0x102000, 0x2000, RegionState.Commit, PageProtection.ReadWrite, RegionType.Private),
                new MemoryRegion(0x200000, 0x2000, RegionState.Commit, PageProtection.ReadOnly, RegionType.Private),
                new MemoryRegion(0x400000, 0x1000, RegionState.Commit, PageProtection.ReadOnly, RegionType.Image),
            ]);
            var options = new ProcessCaptureOptions { RefreshRegions = false };
            string path = Path.Combine(_dir, "u.hexsnap");
            SnapshotCaptureResult result = HexSnapshot.Write(process, path, options);
            Assert.Equal((1, 0x1000L), (result.Summary.UnreadableRegions, result.Summary.UnreadableBytes));
            SnapshotGap gap = Assert.Single(result.Metadata.UnreadablePages);
            Assert.Equal((0x101000L, 0x1000L), (gap.BaseAddress, gap.Size));

            // 開いたスナップショットでは、読めなかったページが読めない範囲になる。
            using SnapshotByteSource snapshot = SnapshotByteSource.Open(path);
            byte[] page = new byte[0x1000];
            Assert.False(snapshot.Read(0x101000, page).IsComplete);
            Assert.True(snapshot.Read(0x100000, page).IsComplete);
            Assert.Equal(0x11, page[0]);
        }
    }

    [Fact]
    public void A_target_that_exits_during_the_capture_aborts_and_deletes_the_file()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            FakeProcess fake = access.Process(300);
            fake.BeforeRead = (address, _) =>
            {
                if (address >= 0x200000)
                {
                    fake.Kill();
                }
            };
            string path = Path.Combine(_dir, "x.hexsnap");
            Assert.Throws<ProcessExitedDuringCaptureException>(() => HexSnapshot.Write(process, path, new ProcessCaptureOptions { RefreshRegions = false }));
            Assert.Empty(Directory.EnumerateFiles(_dir));
        }
    }

    [Fact]
    public void Not_enough_free_space_is_reported_before_writing()
    {
        (_, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            var options = new ProcessCaptureOptions { Volumes = new SmallVolume(1000) };
            string path = Path.Combine(_dir, "n.hexsnap");
            InsufficientSpaceException ex = Assert.Throws<InsufficientSpaceException>(() => HexSnapshot.Write(process, path, options));
            Assert.True(ex.Required > 0x7000);
            Assert.Empty(Directory.EnumerateFiles(_dir));
            Assert.Throws<InsufficientSpaceException>(() => MinidumpWriter.Write(process, Path.Combine(_dir, "n.dmp"), options));
        }
    }

    private sealed class SmallVolume(long free) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("T:", "NTFS", free);
    }

    [Fact]
    public void Saving_over_an_existing_snapshot_keeps_it_when_the_capture_fails()
    {
        (FakeProcessAccess access, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            string path = Path.Combine(_dir, "keep.hexsnap");
            File.WriteAllText(path, "old");
            access.Process(300).Kill();
            Assert.Throws<ProcessExitedDuringCaptureException>(() => HexSnapshot.Write(process, path, new ProcessCaptureOptions { RefreshRegions = false }));
            Assert.Equal("old", File.ReadAllText(path));
        }
    }

    [Fact]
    public void A_minidump_has_the_streams_and_memory_of_the_process()
    {
        (_, ProcessMemoryByteSource process) = Fake();
        using (process)
        {
            string path = Path.Combine(_dir, "f.dmp");
            ProcessCaptureSummary summary = MinidumpWriter.Write(process, path, new ProcessCaptureOptions());
            Assert.Equal(3, summary.Regions);
            byte[] dump = File.ReadAllBytes(path);
            Assert.Equal(0x504D444Du, BinaryPrimitives.ReadUInt32LittleEndian(dump));
            int count = BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(8));
            int dir = BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(12));
            var streams = Enumerable.Range(0, count).ToDictionary(
                i => BinaryPrimitives.ReadUInt32LittleEndian(dump.AsSpan(dir + (i * 12))),
                i => (Size: BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(dir + (i * 12) + 4)), Rva: BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(dir + (i * 12) + 8))));
            Assert.Contains(4u, streams.Keys); // ModuleList
            Assert.Contains(7u, streams.Keys); // SystemInfo
            Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(dump.AsSpan(streams[7u].Rva))); // AMD64

            // モジュールの一覧: TestTarget.exe がベースアドレス 0x400000。
            int modules = streams[4u].Rva;
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(modules)));
            Assert.Equal(0x400000L, BinaryPrimitives.ReadInt64LittleEndian(dump.AsSpan(modules + 4)));
            int nameRva = BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(modules + 4 + 20));
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(dump.AsSpan(nameRva));
            Assert.Equal(@"C:\t\TestTarget.exe", System.Text.Encoding.Unicode.GetString(dump, nameRva + 4, nameLength));

            // メモリの一覧 (64 bit): 3 つの領域が順に並び、内容が一致する。
            (int size, int rva) = streams[9u];
            long ranges = BinaryPrimitives.ReadInt64LittleEndian(dump.AsSpan(rva));
            Assert.Equal(16 + (ranges * 16), size);
            long data = BinaryPrimitives.ReadInt64LittleEndian(dump.AsSpan(rva + 8));
            var expected = new (long Start, long Size, byte First)[] { (0x100000, 0x4000, 0x11), (0x200000, 0x2000, 0xAA), (0x400000, 0x1000, 0x4D) };
            Assert.Equal(expected.Length, ranges);
            long at = data;
            for (int i = 0; i < ranges; i++)
            {
                long start = BinaryPrimitives.ReadInt64LittleEndian(dump.AsSpan(rva + 16 + (i * 16)));
                long length = BinaryPrimitives.ReadInt64LittleEndian(dump.AsSpan(rva + 24 + (i * 16)));
                Assert.Equal((expected[i].Start, expected[i].Size), (start, length));
                Assert.Equal(expected[i].First, dump[at]);
                at += length;
            }

            Assert.Equal(dump.Length, at);
        }
    }
}

/// <summary>Windows 用デバッグツールの cdb.exe がないときはスキップする Fact (TC-ENG-35-03 の「環境」)。</summary>
public sealed class CdbFactAttribute : FactAttribute
{
    public CdbFactAttribute()
    {
        if (Find() is null)
        {
            Skip = "cdb.exe がない (Windows 用デバッグツールを入れたランナーだけで動く)。";
        }
    }

    public static string? Find()
    {
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        foreach (string? root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
        {
            if (root is not null && Path.Combine(root, "Windows Kits", "10", "Debuggers", arch, "cdb.exe") is var path && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>ダンプを開いてコマンドを実行し、出力を返す。シンボルはネットワークから取らない (空のローカルのフォルダを指す)。</summary>
    public static string Run(string dump, string commands, string workDir)
    {
        string symbols = Directory.CreateDirectory(Path.Combine(workDir, "nosymbols")).FullName;
        var info = new ProcessStartInfo(Find()!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        info.Environment["_NT_SYMBOL_PATH"] = symbols;
        info.Environment.Remove("_NT_ALT_SYMBOL_PATH");
        foreach (string a in new[] { "-y", symbols, "-z", dump, "-c", commands })
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "cdb did not exit");
        Assert.True(process.ExitCode == 0, $"cdb exit {process.ExitCode}: {output}{error.Result}");
        return output;
    }
}
