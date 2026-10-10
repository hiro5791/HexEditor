using System.Diagnostics;
using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>インポート (TOOL-04、TOOL-05) と範囲の切り出しと保存 (TOOL-16) の性能テスト。</summary>
[Trait("Category", "Performance")]
public sealed class FormatPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const string TC = "TC";
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>処理の間、200 ms ごとにプライベートバイトを記録し、最大値を返す。</summary>
    private static long PeakPrivateBytes(Action action)
    {
        long peak = 0;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            using Process process = Process.GetCurrentProcess();
            do
            {
                process.Refresh();
                peak = Math.Max(peak, process.PrivateMemorySize64);
            }
            while (!done.Wait(200));
        }) { IsBackground = true };
        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            done.Set();
            sampler.Join();
        }

        return peak;
    }

    [Fact]
    [Trait(TC, "TC-TOOL-04-02")]
    public void Importing_1_gb_of_intel_hex_stays_under_the_memory_limit()
    {
        string path = TestDataCatalog.Get("TD-TOOL-IHEX-1G");
        long baseline = PrivateBytesAfterGc();
        ImportResult? result = null;
        long peak = PeakPrivateBytes(() => result = Importer.DecodeFile(path, new ImportOptions { Format = FormatIds.IntelHex },
            Path.Combine(_folder.Path, "temp")));
        using (result)
        {
            output.Report($"peak {peak / MiB} MiB (baseline {baseline / MiB} MiB)");
            Assert.True(peak - baseline < GiB, $"{(peak - baseline) / MiB} MiB");
            Assert.Equal(TestDataCatalog.IhexLargeData, result!.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] a = new byte[MiB], b = new byte[MiB];
            for (long offset = 0; offset < result.Length; offset += a.Length)
            {
                result.Image!.Read(offset, a);
                TestDataCatalog.Random(TestDataCatalog.IhexLargeSeed, offset, b);
                hash.AppendData(a);
                expected.AppendData(b);
            }

            Assert.Equal(expected.GetHashAndReset(), hash.GetHashAndReset());
        }
    }

    [Fact]
    [Trait(TC, "TC-TOOL-05-02")]
    public void Sparse_i32hex_imports_quickly_with_little_memory()
    {
        string path = TestDataCatalog.Get("TD-TOOL-IHEX-SPARSE");
        string temp = Path.Combine(_folder.Path, "temp");
        long baseline = PrivateBytesAfterGc();
        ImportResult? result = null;
        long peak = 0;
        TimeSpan time = Time(() => peak = PeakPrivateBytes(() => result = Importer.DecodeFile(path,
            new ImportOptions { Format = FormatIds.IntelHex, Placement = AddressPlacement.Absolute }, temp)));
        using (result)
        {
            TimeLimit(time < TimeSpan.FromSeconds(1), Ms(time));
            Assert.True(peak - baseline < 50 * MiB, $"{(peak - baseline) / MiB} MiB");
            Assert.True(Directory.GetFiles(temp).Sum(f => new FileInfo(f).Length) < MiB);
            SparseImage image = result!.Image!;
            Assert.Equal(0xFFFF0010, image.Length);
            byte[] b = new byte[16];
            image.Read(0, b);
            Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), b);
            image.Read(0xFFFF0000, b);
            Assert.Equal(Enumerable.Range(0xF0, 16).Select(i => (byte)i), b);
            byte[] one = new byte[1];
            image.Read(0x7FFFFFFF, one);
            Assert.Equal(0xFF, one[0]);
        }
    }

    [PerfMachineFact]
    [Trait(TC, "TC-TOOL-16-02")]
    public void Saving_a_50_gb_selection_does_not_grow_memory()
    {
        // 前提: TD-SPARSE-100G を開き、0 から長さ 50 GiB を選択している。保存は「選択範囲をファイルに保存」と同じ処理 (形式「バイナリ」のエクスポート)。
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        DocumentSnapshot snapshot = doc.Current;
        var source = new ExportSource { Read = (o, d) => snapshot.Read(o, d), Length = snapshot.Length };
        string target = Path.Combine(_folder.Path, "part.bin");
        long baseline = PrivateBytesAfterGc();
        long peak = PeakPrivateBytes(() => Exporter.WriteFile(target, stream =>
            Exporter.Write(source, [(0, 50 * GiB)], new ExportOptions { Format = FormatIds.Binary }, stream)));
        output.WriteLine($"Private bytes: baseline {baseline / MiB} MiB, peak {peak / MiB} MiB");

        // 期待結果: 差が 100 MB 未満、出力の長さが 53,687,091,200、2^31 の位置に TD-SPARSE-100G と同じ目印。
        Assert.True(peak - baseline < 100_000_000, $"{(peak - baseline) / MiB} MiB");
        Assert.Equal(53_687_091_200, new FileInfo(target).Length);
        Assert.Equal(TestDataCatalog.Marker(1L << 31), ReadFile(target, 1L << 31, TestDataCatalog.MarkerLength));
    }
}
