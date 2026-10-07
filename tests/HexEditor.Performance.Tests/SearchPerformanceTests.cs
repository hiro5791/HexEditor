using System.Diagnostics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 検索の性能テスト (FIND-01 の「巨大ファイル・長時間処理」)。検索エンジンを直接呼び、検索バーの UI は通さない。
/// </summary>
[Trait("Category", "Performance")]
public sealed class SearchPerformanceTests
{
    private const string TC = "TC";
    private const long MiB = TestDataCatalog.MiB;
    private const long GiB = TestDataCatalog.GiB;

    private static DocumentOptions Options() => new()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditorTests", "recovery"),
    };

    /// <summary>
    /// TC-FIND-01-03: 100 GiB のファイルを、ファイルにない並びですべて検索し、続けて次を検索する。その間 100 ms ごとに
    /// プライベートバイトを記録し、最大値が 300 MiB 以下であることを確かめる (既定の設定: チャンク 4 MiB、既定の並列数)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-01-03")]
    public async Task SearchingHundredGigabytesKeepsMemoryBounded()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        SearchPattern pattern = SearchPattern.FromHex("DE AD BE EF CA FE BA BE");
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
            // 手順 1・2: すべて検索。
            SearchResults all = SearchEngine.FindAll(doc.Current, pattern);
            Assert.Equal(SearchResultsState.Completed, all.State);
            Assert.Equal(0, all.Count);

            // 手順 3: 次を検索。
            Assert.Null(SearchEngine.Find(doc.Current, pattern, 0, forward: true, wrap: true));
        }
        finally
        {
            stop.Cancel();
            await sampler;
        }

        Assert.True(max <= 300 * MiB, $"プライベートバイトの最大: {max / MiB} MiB");
    }

    /// <summary>
    /// 種類ごとの検索の速度の目安 (TC-FIND-01-05 はフェーズ 2 で、固定の計測機で行う)。ここではメモリ上の 1 GiB 相当のデータで、
    /// CPU の処理だけの速度が FIND-01 の表の値を下回らないことを確かめる (読み込みの速度は含まない)。
    /// </summary>
    [Fact]
    public void MatchingSpeedMeetsTheTargetsWithoutIo()
    {
        byte[] data = new byte[256 * MiB];
        new Random(4401).NextBytes(data);
        using var doc = new Document(new MemoryByteSource(data), Options());
        (SearchPattern Pattern, double MiBPerSecond)[] cases =
        [
            (SearchPattern.FromHex("48 45 58 45 4E 44 21 21"), 1024),
            (SearchPattern.FromHex("48 45 ?? 45 4E 44"), 500),
            (SearchPattern.FromText("hexend!!", Encoding.ASCII, new TextSearchOptions { CaseSensitive = false }), 500),
        ];
        foreach ((SearchPattern pattern, double target) in cases)
        {
            // 4 回 (256 MiB × 4 = 1 GiB) 探し、最も遅い 1 回で判定する。
            double slowest = double.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                var watch = Stopwatch.StartNew();
                SearchEngine.Find(doc.Current, pattern, 0, forward: true, wrap: false);
                slowest = Math.Min(slowest, data.Length / (double)MiB / watch.Elapsed.TotalSeconds);
            }

            Assert.True(slowest >= target, $"{pattern.Length} バイトのパターン: {slowest:F0} MiB/s (目標 {target} MiB/s)");
        }
    }
}
