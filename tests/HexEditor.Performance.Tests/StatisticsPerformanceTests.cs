using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>統計 (ANA-10、ANA-14、ANA-16) とファイル形式の判定 (ANA-17) の速さとメモリ使用量。</summary>
public sealed class StatisticsPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    /// <summary>内容を種から計算する読み取り専用のデータソース (巨大な長さでもメモリを使わない)。</summary>
    private sealed class RandomSource(long length, ulong seed) : ByteSourceBase
    {
        public override string DisplayName => "random";

        public override string Identity { get; } = "random:" + Guid.NewGuid().ToString("N");

        public override long Length => length;

        public override SourceCapabilities Capabilities => SourceCapabilities.None;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int n = (int)Math.Clamp(length - offset, 0, buffer.Length);
            TestDataCatalog.Random(seed, offset, buffer[..n]);
            return new ReadResult(n);
        }
    }

    /// <summary>TC-ANA-10-04: 10 GB のファイル全体のバイトヒストグラムが、読み込み速度の 80% 以上で求まる (3 回の中央値)。</summary>
    [PerfMachineFact]
    [Trait(TC, "TC-ANA-10-04")]
    public void ByteHistogramOfTenGigabytesKeepsUpWithTheDisk()
    {
        string path = TestDataCatalog.Get("TD-ANA-10G-A");
        long length = new FileInfo(path).Length;
        FileCache.Purge();
        double read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[4 * MiB];
            var watch = Stopwatch.StartNew();
            while (stream.Read(buffer) > 0)
            {
            }

            read = length / (double)MiB / watch.Elapsed.TotalSeconds;
        }

        var speeds = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            FileCache.Purge();
            using var doc = new Document(FileByteSource.Open(path), Options());
            var watch = Stopwatch.StartNew();
            StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
            Assert.Equal(length, r.Histogram.InBins);
            speeds.Add(length / (double)MiB / watch.Elapsed.TotalSeconds);
        }

        double median = speeds.Order().ElementAt(1);
        output.WriteLine($"読み込み {read:F0} MiB/s、ヒストグラム {median:F0} MiB/s");
        TimeLimit(median >= read * 0.8, $"{median:F0} MiB/s (読み込み {read:F0} MiB/s の 80% 未満)");
    }

    /// <summary>計算中のプライベートバイトの最大値。</summary>
    private static long PeakDuring(Action work)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        long max = 0;
        using var stop = new CancellationTokenSource();
        Task sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                process.Refresh();
                max = Math.Max(max, process.PrivateMemorySize64);
                await Task.Delay(100);
            }
        });
        try
        {
            work();
        }
        finally
        {
            stop.Cancel();
            sampler.GetAwaiter().GetResult();
        }

        process.Refresh();
        return Math.Max(max, process.PrivateMemorySize64);
    }

    /// <summary>
    /// TC-ANA-14-04: ダイグラムと位置ごとのバイト分布のメモリ使用量が対象の大きさに依存しない (16 MiB と 10 GiB の差が 32 MB 以内)。
    /// TD-ANA-10G-A の代わりに、同じ作り方の内容を計算するデータソースを使う (標準の環境で 10 GiB を書かないため)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-14-04")]
    public void DigramMemoryDoesNotGrowWithTheSize()
    {
        static void Run(long length)
        {
            using var doc = new Document(new RandomSource(length, TestDataCatalog.Ana10GSeed), Options());
            StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
            Assert.Equal(length - 1, r.Digram!.Total);
        }

        long small = PeakDuring(() => Run(16 * MiB));
        long large = PeakDuring(() => Run(10 * GiB));
        output.WriteLine($"16 MiB: {small / MiB} MiB、10 GiB: {large / MiB} MiB");
        Assert.True(large - small <= 32 * MiB, $"差 {(large - small) / MiB} MiB");
    }

    /// <summary>TC-ANA-16-05: 100 GB のファイルをブロック 4 KB で分類しても、メモリ使用量が 1 GB を超えない。</summary>
    [Fact]
    [Trait(TC, "TC-ANA-16-05")]
    public void ClassifyingHundredGigabytesStaysUnderOneGigabyte()
    {
        long peak = PeakDuring(() =>
        {
            using var doc = Open("TD-SPARSE-100G");
            ClassificationResult r = DataClassifier.Classify(doc.Current, new ClassifyRequest());
            Assert.True(r.Completed);
        });
        output.WriteLine($"最大のプライベートバイト: {peak / MiB} MiB");
        Assert.True(peak <= GiB, $"{peak / MiB} MiB");
    }

    /// <summary>
    /// TC-ANA-17-03 の Core の部分: 100 GB のファイルを開いたときの自動判定は最大 128 KiB しか読まず、すぐに終わる
    /// (判定はバックグラウンドで行い、先頭の表示を待たせない。表示の順序は UI の処理で、ここでは読む量と時間を計る)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-17-03")]
    public void DetectionOnOpenReadsAtMost128Kilobytes()
    {
        using var doc = Open("TD-SPARSE-100G");
        var watch = Stopwatch.StartNew();
        var data = new HeadTailMagicData(doc.Current);
        FileTypeReport report = new FileTypeDetector(FileTypeDatabase.BuiltIn).Detect(data, ".bin");
        TimeSpan time = watch.Elapsed;
        Assert.True(data.BytesRead <= 128 * 1024, $"{data.BytesRead} bytes");
        output.WriteLine($"判定 {time.TotalMilliseconds:F0} ms、候補 {report.Candidates.Count} 件");
        TimeLimit(time <= TimeSpan.FromSeconds(1), $"{time.TotalMilliseconds:F0} ms");
    }
}
