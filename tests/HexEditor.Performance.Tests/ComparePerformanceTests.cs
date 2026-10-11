using System.Diagnostics;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>比較の性能テスト (ANA-02・ANA-03・ANA-05 の「巨大ファイル・長時間処理」)。比較の処理を直接呼ぶ。</summary>
[Trait("Category", "Performance")]
public sealed class ComparePerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";

    /// <summary>
    /// TC-ANA-02-05: NVMe SSD 上の 10 GiB のファイル 2 つの単純比較が、2 つを並行に読む速度の 80% 以上で終わる。3 回の中央値で判定する。
    /// 性能テスト用の計測機でだけ実行する (10 GiB のテストデータを実際に書き、OS のファイルキャッシュを消すため)。
    /// </summary>
    [PerfMachineFact]
    [Trait(TC, "TC-ANA-02-05")]
    public void ComparingTwoTenGigabyteFilesKeepsUpWithTheDisk()
    {
        string a = TestDataCatalog.Get("TD-ANA-10G-A");
        string b = TestDataCatalog.Get("TD-ANA-10G-B");
        long length = new FileInfo(a).Length;

        // 前提: 2 ファイルを並行に読んだときの合計の速度。
        FileCache.Purge();
        var read = Stopwatch.StartNew();
        Parallel.Invoke(() => ReadAll(a), () => ReadAll(b));
        double readSpeed = 2.0 * length / MiB / read.Elapsed.TotalSeconds;

        var speeds = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            FileCache.Purge();
            using var left = FileByteSource.Open(a);
            using var right = FileByteSource.Open(b);
            using var result = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromSource(left)),
                CompareRange.Whole(CompareData.FromSource(right)));
            var watch = Stopwatch.StartNew();
            DataComparer.Run(new CompareOptions(), result);
            speeds.Add(2.0 * length / MiB / watch.Elapsed.TotalSeconds);
            Assert.Equal([new DiffRange(DiffKind.Changed, 0x140000000, 16, 0x140000000, 16)], result.Diffs.Enumerate());
        }

        double median = speeds.Order().ElementAt(1);
        output.WriteLine($"比較 {median:F0} MiB/s、読み込み {readSpeed:F0} MiB/s");
        TimeLimit(median >= readSpeed * 0.8, $"比較 {median:F0} MiB/s、読み込み {readSpeed:F0} MiB/s");

        static void ReadAll(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
            byte[] buffer = new byte[4 * MiB];
            while (stream.Read(buffer) > 0)
            {
            }
        }
    }

    /// <summary>
    /// TC-ANA-02-06: 差分が 1,000 万件出る比較 (TD-ANA-ALT20M-A と B) で、プライベートバイトの最大が 1 GB 以下 (100 万件を超えた分は
    /// 一時ファイルに書き出す)。最後の差分 (オフセット 19,999,999) も一覧から読める。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-02-06")]
    public async Task TenMillionDiffsKeepMemoryBelowOneGigabyte()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var left = FileByteSource.Open(TestDataCatalog.Get("TD-ANA-ALT20M-A"));
        using var right = FileByteSource.Open(TestDataCatalog.Get("TD-ANA-ALT20M-B"));
        using var result = new CompareResult(CompareMethod.Simple, CompareRange.Whole(CompareData.FromSource(left)),
            CompareRange.Whole(CompareData.FromSource(right)));
        long max = await SampleMemoryAsync(() => DataComparer.Run(new CompareOptions(), result));

        Assert.Equal(10_000_000, result.Diffs.Count);
        Assert.Equal(9_000_000, result.Diffs.SpilledCount);
        Assert.Equal(new DiffRange(DiffKind.Changed, 19_999_999, 1, 19_999_999, 1), result.Diffs[result.Diffs.Count - 1]);
        long leftover = Math.Max(0, StartupPrivateBytes);
        output.WriteLine($"プライベートバイトの最大: {max / MiB} MiB");
        Assert.True(max <= 1024 * MiB + leftover, $"プライベートバイトの最大: {max / MiB} MiB");
    }

    /// <summary>
    /// TC-ANA-03-05: W = 16 MB で全く異なる 1 GiB のデータ 2 つを挿入・削除を考慮して比較しても、プライベートバイトの最大が 1 GB 以下。
    /// データは仮想のデータソース (乱数を計算で作る) で、TD-ANA-RAND-1G と TD-ANA-RAND-1G-OTHER と同じ内容。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-03-05")]
    public async Task AWindowOfSixteenMegabytesKeepsMemoryBounded()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var left = new RandomData(GiB, 0xA0A0_1001);
        var right = new RandomData(GiB, 0xA0A0_3003);
        using var result = new CompareResult(CompareMethod.InsertDelete, CompareRange.Whole(left), CompareRange.Whole(right));
        var options = new CompareOptions { Method = CompareMethod.InsertDelete, Window = 16 * 1024 * 1024, MinMatch = 8 };
        var watch = Stopwatch.StartNew();
        long max = await SampleMemoryAsync(() => DataComparer.Run(options, result));

        Assert.Equal(CompareState.Completed, result.State);
        output.WriteLine($"プライベートバイトの最大: {max / MiB} MiB、{watch.Elapsed.TotalSeconds:F1} 秒、差分 {result.Diffs.Count} 件、" +
            $"打ち切ったウィンドウ {result.AbortedWindows} 個");
        Assert.True(max <= 1024 * MiB + StartupPrivateBytes, $"プライベートバイトの最大: {max / MiB} MiB");
    }

    /// <summary>
    /// TC-ANA-05-03: 1,000 万件の差分 (奇数のオフセットごと) で、先頭・中央・末尾近くから次 / 前の差分への移動がそれぞれ 100 ms 以内。
    /// 移動先はカーソルの次 (前) の奇数のオフセット。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-05-03")]
    public void MovingBetweenTenMillionDiffsIsFast()
    {
        using var store = new DiffStore();
        for (long i = 0; i < 10_000_000; i++)
        {
            store.Add(new DiffRange(DiffKind.Changed, 2 * i + 1, 1, 2 * i + 1, 1));
        }

        var times = new List<TimeSpan>();
        foreach (long cursor in (long[])[0, 10_000_000, 19_999_990])
        {
            long position = cursor;
            long current = -1;
            for (int k = 0; k < 20; k++)
            {
                DiffStep? step = null;
                times.Add(Time(() => step = DiffNavigation.Next(store, false, position, current)));
                long next = store[step!.Value.Index].LeftOffset;
                long expectedNext = position % 2 == 1 ? position + 2 : position + 1;
                Assert.Equal(expectedNext > 19_999_999 ? 1 : expectedNext, next);
                position = next;
                current = step.Value.Index;
            }

            position = cursor;
            for (int k = 0; k < 20; k++)
            {
                DiffStep? step = null;
                times.Add(Time(() => step = DiffNavigation.Previous(store, false, position)));
                long previous = store[step!.Value.Index].LeftOffset;
                long expected = position % 2 == 1 ? position - 2 : position - 1;
                Assert.Equal(expected < 1 ? 19_999_999 : expected, previous);
                position = previous;
            }
        }

        output.WriteLine(Summary(times));
        TimeLimit(MaxExceptFirst(times) <= TimeSpan.FromMilliseconds(100), Summary(times));
    }

    /// <summary>処理の間、100 ms ごとにプライベートバイトを記録し、最大値を返す。</summary>
    private static async Task<long> SampleMemoryAsync(Action work)
    {
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
            await Task.Run(work);
        }
        finally
        {
            stop.Cancel();
            await sampler;
        }

        process.Refresh();
        return Math.Max(max, process.PrivateMemorySize64);
    }

    /// <summary>乱数を計算で作るデータ (TestDataCatalog.Random と同じ値)。</summary>
    private sealed class RandomData(long length, ulong seed) : ICompareData
    {
        public long Length => length;

        public ReadResult Read(long offset, Span<byte> destination)
        {
            int n = (int)Math.Clamp(length - offset, 0, destination.Length);
            TestDataCatalog.Random(seed, offset, destination[..n]);
            return new ReadResult(n);
        }
    }
}
