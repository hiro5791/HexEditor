using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.MemoryDumps;
using HexEditor.Core.Operations;
using HexEditor.Core.Processes;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Processes;
using HexEditor.TestData;
using ProcessModule = HexEditor.Core.Processes.ProcessModule;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// ENG-36 物理メモリ (RAM) とメモリダンプ。物理メモリそのものは読まない (ドライバを使わない方針。仕様 1)。他のツールで取ったダンプの形式を、
/// 生成ツールが組み立てたファイルと、テスト自身が起動した TestTarget のミニダンプで確かめる。
/// </summary>
public sealed class MemoryDumpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-memdump").FullName;

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

    private static (byte[] Data, ReadResult Result) Read(IByteSource source, long offset, int length)
    {
        byte[] data = new byte[length];
        return (data, source.Read(offset, data));
    }

    [Fact]
    [Trait("TC", "TC-ENG-36-01")]
    [Trait("Category", "Integration")]
    public void A_complete_dump_reads_only_its_physical_ranges_and_marks_the_rest_unallocated()
    {
        // 1. TD-ENG-FULLDUMP を「メモリダンプを開く」で開く。
        OpenedMemoryDump opened = MemoryDumpOpener.Open(TestDataCatalog.Get("TD-ENG-FULLDUMP"));
        using IByteSource source = opened.Source;
        using var doc = new Document(source);

        // 2. 各範囲の位置の 16 バイトがページの目印と一致する。
        foreach (long address in new long[] { 0x1000, 0x9F000, 0x100000, 0x7FF000, 0x1000000, 0x10FF000 })
        {
            (byte[] data, ReadResult r) = Read(source, address, 16);
            Assert.True(r.IsComplete, $"0x{address:X}");
            Assert.Equal(Encoding.ASCII.GetString(TestDataCatalog.PfnMarker(address / 4096))[..16], Encoding.ASCII.GetString(data));
        }

        // 3. ダンプに含まれない物理アドレスは「未割り当て」。
        foreach (long address in new long[] { 0x0, 0xA0000, 0x800000, 0x1100000 })
        {
            (byte[] data, ReadResult r) = Read(source, address, 16);
            Assert.Equal(UnreadableReason.Unallocated, Assert.Single(r.Unreadable).Reason);
            Assert.All(data, b => Assert.Equal(0, b));
        }

        Assert.True(source.Capabilities.HasFlag(SourceCapabilities.HasGaps));
        Assert.False(source.Capabilities.HasFlag(SourceCapabilities.CanWrite));
        Assert.Equal(1L << 30, source.Length);

        // 4. ダンプの種類 (完全メモリダンプ) と取得日時。ドキュメントは読み取り専用 (書き戻す先がない)。
        Assert.Equal(MemoryDumpKind.CompleteDump, opened.Info.Kind);
        Assert.Equal(TestDataCatalog.FullDumpTime, opened.Info.CapturedUtc);
        Assert.Throws<NotSupportedException>(() => source.Write(0x1000, [1]));
        var regions = ((IRegionMapSource)source).Regions;
        Assert.Equal([RegionAccess.Unallocated, RegionAccess.Readable, RegionAccess.Unallocated, RegionAccess.Readable, RegionAccess.Unallocated, RegionAccess.Readable, RegionAccess.Unallocated],
            regions.Select(r => r.Access));
    }

    [Fact]
    [Trait("TC", "TC-ENG-36-02")]
    [Trait("Category", "Integration")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_minidump_saved_by_ENG_35_opens_with_the_same_addresses_content_and_modules()
    {
        // 前提: TC-ENG-35-01 と同じ状態の TestTarget (このテストが起動した、読むだけのプロセス) のミニダンプを保存する。
        using TestTargetHost target = TestTargetHost.Start("--random", "1048576", "--seed", "53");
        string[] random = target.Line("RANDOM");
        long randomAddress = TestTargetHost.Hex(random[0]);
        string path = Path.Combine(_dir, "t" + MinidumpWriter.Extension);
        using (ProcessMemoryByteSource process = target.Open())
        {
            MinidumpWriter.Write(process, path, new ProcessCaptureOptions(), new LongRunningOperation("dump", OperationKind.ReadOnly, null, null, TimeProvider.System));
        }

        // 1. t.dmp を開く。
        Assert.Equal(MemoryDumpKind.Minidump, MemoryDumpFormat.Detect(path).Kind);
        OpenedMemoryDump opened = MemoryDumpOpener.Open(path);
        using var snapshot = (SnapshotByteSource)opened.Source;

        // 2. 乱数の領域の SHA-256 と、TestTarget.exe のベースアドレスの 2 バイト。
        byte[] data = new byte[1048576];
        Assert.True(snapshot.Read(randomAddress, data).IsComplete);
        Assert.Equal(random[2], Convert.ToHexString(SHA256.HashData(data)));
        ProcessModule exe = Assert.Single(snapshot.Modules, m => m.Name.Equals("TestTarget.exe", StringComparison.OrdinalIgnoreCase));
        byte[] mz = new byte[2];
        snapshot.Read(exe.BaseAddress, mz);
        Assert.Equal("MZ"u8.ToArray(), mz);

        // 3. メモリマップのパネルのモジュールの一覧 (TestTarget.exe と ntdll.dll)。読み取り専用。
        Assert.Contains(snapshot.Modules, m => m.Name.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Regions, r => r.Access == RegionAccess.Readable && r.Label is { } l && l.Equals("TestTarget.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(snapshot.Capabilities.HasFlag(SourceCapabilities.CanWrite));
        Assert.Equal(target.Pid, snapshot.Metadata.Pid);
        Assert.Equal(MemoryDumpKind.Minidump, opened.Info.Kind);
    }

    [Fact]
    public void Bitmap_kernel_and_32_bit_dumps_map_present_pages()
    {
        // ビットマップの形式 (カーネルメモリダンプ): ページ 0、3、70000 だけが入っている。
        long pages = 80000;
        byte[] bitmap = new byte[(pages + 7) / 8];
        foreach (long p in new long[] { 0, 3, 70000 })
        {
            bitmap[p / 8] |= (byte)(1 << (int)(p % 8));
        }

        byte[] head = CrashDumpHeader.BuildBitmap64(MemoryDumpFormat.DumpTypeBitmapKernel, bitmap, pages, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        string path = Path.Combine(_dir, "kernel.dmp");
        using (FileStream f = File.Create(path))
        {
            f.Write(head);
            foreach (long p in new long[] { 0, 3, 70000 })
            {
                byte[] page = new byte[4096];
                TestDataCatalog.PfnMarker(p).CopyTo(page, 0);
                f.Write(page);
            }
        }

        OpenedMemoryDump opened = MemoryDumpOpener.Open(path);
        using IByteSource source = opened.Source;
        Assert.Equal(MemoryDumpKind.KernelDump, opened.Info.Kind);
        Assert.Equal(pages * 4096, source.Length);
        foreach (long p in new long[] { 0, 3, 70000 })
        {
            (byte[] d, ReadResult r) = Read(source, p * 4096, 20);
            Assert.True(r.IsComplete);
            Assert.Equal(TestDataCatalog.PfnMarker(p), d);
        }

        (_, ReadResult missing) = Read(source, 4 * 4096, 8192);
        Assert.Equal(UnreadableReason.Unallocated, Assert.Single(missing.Unreadable).Reason);

        // 32 bit の完全ダンプ (PAGEDUMP、ヘッダ 0x1000)。
        byte[] h32 = new byte[0x1000];
        "PAGEDUMP"u8.CopyTo(h32);
        BinaryPrimitives.WriteUInt32LittleEndian(h32.AsSpan(CrashDumpHeader.RunsOffset32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h32.AsSpan(CrashDumpHeader.RunsOffset32 + 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h32.AsSpan(CrashDumpHeader.RunsOffset32 + 8), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(h32.AsSpan(CrashDumpHeader.RunsOffset32 + 12), 2);
        BinaryPrimitives.WriteInt32LittleEndian(h32.AsSpan(CrashDumpHeader.DumpTypeOffset32), 1);
        string path32 = Path.Combine(_dir, "x86.dmp");
        using (FileStream f = File.Create(path32))
        {
            f.Write(h32);
            f.Write(Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        }

        OpenedMemoryDump x86 = MemoryDumpOpener.Open(path32);
        using IByteSource s32 = x86.Source;
        Assert.Equal(1L << 30, s32.Length);
        Assert.Equal(0x5A, Read(s32, 0x10 * 4096 + 5, 1).Data[0]);
        Assert.Equal(UnreadableReason.Unallocated, Read(s32, 0, 1).Result.Unreadable[0].Reason);
    }

    [Fact]
    public void Raw_images_and_unknown_signatures_open_as_physical_address_space()
    {
        string raw = Path.Combine(_dir, "memory.raw");
        File.WriteAllBytes(raw, Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray());
        OpenedMemoryDump opened = MemoryDumpOpener.Open(raw);
        using IByteSource source = opened.Source;
        Assert.Equal(MemoryDumpKind.Raw, opened.Info.Kind);
        Assert.Equal(8192, source.Length);
        Assert.Equal(0x34, Read(source, 0x1234, 1).Data[0]);
        Assert.Equal(0, source.BaseAddress);
    }

    [Fact]
    public void Broken_and_unsupported_dumps_raise_a_format_error_and_can_be_opened_as_raw()
    {
        // 範囲の数が 0 の完全ダンプ。
        byte[] header = CrashDumpHeader.BuildFull64([new PhysicalRun(0, 1)], DateTime.UtcNow);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(CrashDumpHeader.RunsOffset64), 0);
        string broken = Path.Combine(_dir, "broken.dmp");
        File.WriteAllBytes(broken, [.. header, .. new byte[4096]]);
        Assert.Throws<MemoryDumpFormatException>(() => MemoryDumpOpener.Open(broken));
        using (MemoryDumpOpener.Open(broken, forceRaw: true).Source)
        {
        }

        // 範囲がファイルより長い。
        header = CrashDumpHeader.BuildFull64([new PhysicalRun(0, 100)], DateTime.UtcNow);
        File.WriteAllBytes(broken, [.. header, .. new byte[4096]]);
        Assert.Throws<MemoryDumpFormatException>(() => MemoryDumpOpener.Open(broken));

        // 休止状態のファイル (圧縮されている) とトリアージダンプは、この版では読めない (仕様 6)。
        string hiber = Path.Combine(_dir, "hiberfil.sys");
        File.WriteAllBytes(hiber, [.. "HIBR"u8, .. new byte[8192]]);
        Assert.Equal(UnsupportedDumpKind.Hibernation, MemoryDumpFormat.Detect(hiber).Unsupported);
        Assert.Throws<MemoryDumpFormatException>(() => MemoryDumpOpener.Open(hiber));
        header = CrashDumpHeader.BuildFull64([new PhysicalRun(0, 1)], DateTime.UtcNow);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(CrashDumpHeader.DumpTypeOffset64), MemoryDumpFormat.DumpTypeTriage);
        File.WriteAllBytes(broken, [.. header, .. new byte[4096]]);
        Assert.Equal(UnsupportedDumpKind.UnknownDumpType, MemoryDumpFormat.Detect(broken).Unsupported);

        // 壊れたミニダンプ。
        string mini = Path.Combine(_dir, "broken-mini.dmp");
        File.WriteAllBytes(mini, [.. "MDMP"u8, 0x93, 0xA7, 0, 0, 0xFF, 0xFF, 0, 0, 0x20, 0, 0, 0, .. new byte[64]]);
        Assert.Throws<MemoryDumpFormatException>(() => MemoryDumpOpener.Open(mini));
    }

    [Fact]
    public void Live_physical_memory_is_not_available_without_a_driver()
    {
        // 仕様 1: 物理メモリを直接読む機能は実装しない。経路の抽象だけを置き、実機では常に「使えない」。
        IPhysicalMemoryAccess live = UnavailablePhysicalMemoryAccess.Instance;
        Assert.False(live.IsAvailable);
        Assert.Contains("driver", live.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => live.Open());
        IPhysicalMemoryAccess fake = new FakePhysicalMemoryAccess([1, 2, 3]);
        using IByteSource source = fake.Open();
        Assert.Equal(3, source.Length);
    }
}
