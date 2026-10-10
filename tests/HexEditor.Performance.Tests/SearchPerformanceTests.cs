using System.Diagnostics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 検索の性能テスト (FIND-01 の「巨大ファイル・長時間処理」)。検索エンジンを直接呼び、検索バーの UI は通さない。
/// </summary>
[Trait("Category", "Performance")]
public sealed class SearchPerformanceTests(ITestOutputHelper output)
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
        // 同じプロセスで先に動いた性能テストが確保したままのメモリを OS に返してから計る (性能テストは 1 つずつ同じプロセスで動く)。
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        SearchPattern pattern = SearchPattern.FromHex("DE AD BE EF CA FE BA BE");
        using var process = Process.GetCurrentProcess();
        long baseline = process.PrivateMemorySize64;
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        int gen0Before = GC.CollectionCount(0);
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

        // 先に動いた性能テストが残したメモリ (GC の後も OS に返らない分) は、アプリのプロセスでは起きないため除く。
        long leftover = Math.Max(0, baseline - PerfSupport.StartupPrivateBytes);
        output.Report($"100 GiB の検索中のプライベートバイトの最大: {max / MiB} MiB (検索の前: {baseline / MiB} MiB、" +
            $"先に動いたテストの残り: {leftover / MiB} MiB)、割り当て {(GC.GetTotalAllocatedBytes() - allocatedBefore) / MiB} MiB、" +
            $"GC (第 0 世代) {GC.CollectionCount(0) - gen0Before} 回");
        Assert.True(max - leftover <= 300 * MiB, $"プライベートバイトの最大: {max / MiB} MiB (先に動いたテストの残り {leftover / MiB} MiB を除いて {(max - leftover) / MiB} MiB)");
    }

    /// <summary>
    /// TC-FIND-01-04: 10 GiB のファイルの末尾にあるリテラルを、OS のキャッシュにない状態から検索する (検索バーの Enter と同じ「次を検索」)。
    /// 検索の速度が、同じ条件で 4 MiB ずつ順に読む速度の 80% 以上で、10 秒以内に終わる。性能テスト用の計測機でだけ実行する。
    /// </summary>
    [PerfMachineFact]
    [Trait(TC, "TC-FIND-01-04")]
    public void FindingTheLiteralAtTheEndOfTenGigabytesKeepsUpWithTheDisk()
    {
        string path = TestDataCatalog.Get("TD-FIND-RANDOM-10G");
        long length = new FileInfo(path).Length;
        SearchPattern pattern = SearchPattern.FromHex("48 45 58 45 4E 44 21 21");
        double slowest = double.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            // 手順 1〜3: キャッシュを空にして開き、次を検索する。
            FileCache.Purge();
            using var doc = new Document(FileByteSource.Open(path), Options());
            var watch = Stopwatch.StartNew();
            SearchHit? hit = SearchEngine.Find(doc.Current, pattern, 0, forward: true, wrap: false);
            TimeSpan time = watch.Elapsed;
            Assert.Equal(10_737_418_232, hit?.Offset);
            Assert.Equal(8, hit?.Length);
            TimeLimit(time <= TimeSpan.FromSeconds(10), $"{i + 1} 回目: {time.TotalSeconds:F2} 秒");
            slowest = Math.Min(slowest, length / (double)MiB / time.TotalSeconds);
        }

        // 手順 4: 同じ条件 (キャッシュを空にした状態) で、先頭から 4 MiB ずつ順に読む速度。
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

        TimeLimit(slowest >= read * 0.8, $"検索 {slowest:F0} MiB/s、読み込み {read:F0} MiB/s");
    }

    /// <summary>
    /// TC-FIND-01-05: 種類ごとの検索 (大文字・小文字を区別しないテキスト、整数、許容誤差のある浮動小数点、後戻りしない正規表現) の速度を、
    /// OS のキャッシュにない TD-FIND-RANDOM-10G の次を検索で測る。それぞれ FIND-01 の表の値と読み込み速度の 80% の小さい方以上。
    /// 性能テスト用の計測機でだけ実行する。
    /// </summary>
    [PerfMachineFact]
    [Trait(TC, "TC-FIND-01-05")]
    public void EachKindOfSearchMeetsItsSpeedTarget()
    {
        string path = TestDataCatalog.Get("TD-FIND-RANDOM-10G");
        long length = new FileInfo(path).Length;

        // 手順 3: 読み込み速度 R (4 MiB ずつ順に読む)。
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

        (string Name, SearchPattern Pattern, double Target)[] kinds =
        [
            ("text (ignore case)", SearchPattern.FromText("hexend!!", Encoding.ASCII, new TextSearchOptions { CaseSensitive = false }), 500),
            ("int64", NumericSearch.Integer("0x2121444E45584548", new IntegerSearchOptions { Bits = 64, Endian = SearchEndian.Little }), 300),
            ("double ±1e-300", NumericSearch.Float("1.5e300", new FloatSearchOptions
            {
                Format = FloatFormat.Double,
                Tolerance = ToleranceKind.Absolute,
                ToleranceValue = 1e-300,
            }), 300),
            ("regex (bytes)", RegexSearch.Bytes("HEX[A-Z]{3}!!", new RegexSearchOptions { Singleline = true }), 100),
        ];
        foreach ((string name, SearchPattern pattern, double target) in kinds)
        {
            // 手順 1〜2: キャッシュを空にしてから、ファイル全体の次を検索。3 回測って最も遅い値で比べる。
            double slowest = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                FileCache.Purge();
                using var doc = new Document(FileByteSource.Open(path), Options());
                var watch = Stopwatch.StartNew();
                SearchEngine.Find(doc.Current, pattern, 0, forward: true, wrap: false);
                slowest = Math.Min(slowest, length / (double)MiB / watch.Elapsed.TotalSeconds);
            }

            double goal = Math.Min(target, read * 0.8);
            output.WriteLine($"{name}: {slowest:F0} MiB/s (目標 {goal:F0} MiB/s、読み込み {read:F0} MiB/s)");
            TimeLimit(slowest >= goal, $"{name}: {slowest:F0} MiB/s < {goal:F0} MiB/s");
        }
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

            TimeLimit(slowest >= target, $"{pattern.Length} バイトのパターン: {slowest:F0} MiB/s (目標 {target} MiB/s)");
        }
    }

    /// <summary>
    /// TC-FIND-06-03: ワイルドカードを含む検索語で 10 GiB のファイルの末尾の一致を、OS のキャッシュにない状態から探す。
    /// 速度が 500 MiB/s と読み込み速度の 80% のうち小さい方以上。性能テスト用の計測機でだけ実行する。
    /// </summary>
    [PerfMachineFact]
    [Trait(TC, "TC-FIND-06-03")]
    public void WildcardSearchOfTenGigabytesKeepsUpWithTheDisk()
    {
        string path = TestDataCatalog.Get("TD-FIND-RANDOM-10G");
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

        double target = Math.Min(500, read * 0.8);
        foreach (string hex in new[] { "48 45 ?? 45 4E 44", "4? 45 58 45 4E 4?" })
        {
            double slowest = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                FileCache.Purge();
                using var doc = new Document(FileByteSource.Open(path), Options());
                var watch = Stopwatch.StartNew();
                SearchHit? hit = SearchEngine.Find(doc.Current, SearchPattern.FromHex(hex), 0, forward: true, wrap: false);
                double speed = length / (double)MiB / watch.Elapsed.TotalSeconds;
                Assert.Equal(10_737_418_232, hit?.Offset);
                slowest = Math.Min(slowest, speed);
            }

            TimeLimit(slowest >= target, $"{hex}: {slowest:F0} MiB/s (目標 {target:F0} MiB/s、読み込み {read:F0} MiB/s)");
        }
    }

    /// <summary>TD-FIND-HITS-1000 / TD-FIND-HITS-1500K をメモリ上に作る。</summary>
    private static byte[] Hits(int count, int stride, byte[] pattern, int length)
    {
        byte[] data = new byte[length];
        for (int k = 0; k < count; k++)
        {
            pattern.CopyTo(data, k * stride);
        }

        return data;
    }

    private static long ReplaceAll(Document doc, SearchPattern pattern, string replacement)
    {
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, pattern, SearchOptions.Default);
        PreparedReplacement prepared = doc.PrepareReplacements(
            Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex(replacement), new ReplaceOptions(), doc.CanResize));
        doc.CommitReplacements(prepared, "すべて置換");
        return prepared.Count;
    }

    /// <summary>
    /// TC-FIND-23-01: 1 MiB のファイルの 1,000 件のすべて置換 (検索と適用) が 1 秒以内に終わり、Undo 1 回ですべて戻る。3 回行う。
    /// 検索バーの UI を通さず、すべて置換と同じ部品を直接呼ぶ (完了の InfoBar は UI のテスト TC-FIND-23-04 で確かめる)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-23-01")]
    public void ReplacingAThousandMatchesTakesLessThanASecond()
    {
        byte[] data = Hits(1000, 1024, [0x12, 0x34, 0x56, 0x78], (int)MiB);
        using var doc = new Document(new MemoryByteSource(data), Options());
        SearchPattern find = SearchPattern.FromHex("12 34 56 78");
        SearchPattern replaced = SearchPattern.FromHex("87 65 43 21");
        for (int i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            Assert.Equal(1000, ReplaceAll(doc, find, "87 65 43 21"));
            TimeSpan time = watch.Elapsed;
            TimeLimit(time <= TimeSpan.FromSeconds(1), $"{i + 1} 回目: {time.TotalMilliseconds:F0} ms");
            Assert.Equal(0, SearchEngine.Count(doc.Current, find).Count);
            Assert.Equal(1000, SearchEngine.Count(doc.Current, replaced).Count);
            doc.Undo();
            Assert.Equal(1000, SearchEngine.Count(doc.Current, find).Count);
            Assert.Equal(0, SearchEngine.Count(doc.Current, replaced).Count);
        }
    }

    /// <summary>
    /// TC-FIND-23-05: 1,500,000 件の同じ長さの置換の適用が 5 秒以内 (目標。超えた場合は警告として出力する) で、
    /// メモリ使用量の増加がメモリの上限 (既定 1 GB) を超えない。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-23-05")]
    public void ApplyingMillionsOfSameLengthReplacementsIsFastAndBounded()
    {
        byte[] data = Hits(1_500_000, 4, [0xAB, 0xCD], 6_000_000);
        using var doc = new Document(new MemoryByteSource(data), Options());
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromHex("AB CD"), SearchOptions.Default);
        Assert.Equal(1_500_000, found.Count);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        long before = process.PrivateMemorySize64;
        var watch = Stopwatch.StartNew();
        PreparedReplacement prepared = doc.PrepareReplacements(
            Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("12 34"), new ReplaceOptions(), doc.CanResize));
        doc.CommitReplacements(prepared, "すべて置換");
        TimeSpan time = watch.Elapsed;
        process.Refresh();
        long growth = process.PrivateMemorySize64 - before;
        Assert.Equal(1_500_000, prepared.Count);
        Assert.Equal(new byte[] { 0x12, 0x34 }, PerfSupport.Read(doc, 5_999_996, 2));
        Assert.True(growth <= GiB, $"メモリ使用量の増加: {growth / MiB} MiB");
        if (time > TimeSpan.FromSeconds(5))
        {
            Console.WriteLine($"警告: 適用に {time.TotalSeconds:F1} 秒かかりました (目標 5 秒)。");
        }

        TimeLimit(time <= TimeSpan.FromSeconds(30), $"適用の時間: {time.TotalSeconds:F1} 秒");
    }

    /// <summary>
    /// TC-FIND-24-04: TD-SPARSE-100G で `@000000` (7 バイト) を `#0000000` (8 バイト) に「長さを変える」ですべて置換する。適用は 1 秒以内、
    /// メモリ使用量の増加は 50 MB 以下、長さは 107,374,182,400 + 置換の件数。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-24-04")]
    public void ChangingLengthReplaceAllOnHundredGigabytes()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromText("@000000", Encoding.ASCII), SearchOptions.Default);
        Assert.InRange(found.Count, 100, 1000);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        long before = process.PrivateMemorySize64;
        var watch = Stopwatch.StartNew();
        PreparedReplacement prepared = doc.PrepareReplacements(Replacer.PlanAll(doc.Current, found,
            ReplacementTemplate.FromText("#0000000", Encoding.ASCII, false), new ReplaceOptions(), doc.CanResize));
        doc.CommitReplacements(prepared, "すべて置換");
        TimeSpan time = watch.Elapsed;
        process.Refresh();
        long growth = process.PrivateMemorySize64 - before;
        output.Report($"100 GiB の長さを変えるすべて置換 ({found.Count} 件) の適用: {time.TotalMilliseconds:F0} ms、メモリの増加 {growth / MiB} MiB");
        TimeLimit(time <= TimeSpan.FromSeconds(1), $"適用の時間: {time.TotalMilliseconds:F0} ms");
        Assert.True(growth <= 50 * MiB, $"メモリ使用量の増加: {growth / MiB} MiB");
        Assert.Equal((100 * GiB) + found.Count, doc.Length);
    }

    /// <summary>
    /// TC-FIND-27-03: 100 GiB のファイルで、ファイルにない `DE` をインクリメンタルサーチと同じ範囲 (起点から前方 256 MB) で探す。
    /// 範囲を読み終えたら止まり (「Enter で続きを検索」)、ファイル全体を読まない。検索は検索バーと同じくバックグラウンドで行い、
    /// 入力の取り消しは UI のテスト (TC-FIND-27-02) で確かめる。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-27-03")]
    public void IncrementalSearchStopsAfterTheWindow()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        SearchScope window = SearchScope.WholeDocument.Clip(0, SearchScope.IncrementalWindow, doc.Length, out bool truncated);
        Assert.True(truncated);
        var watch = Stopwatch.StartNew();
        SearchHit? hit = SearchEngine.Find(doc.Current, SearchPattern.FromHex("DE"), 0, forward: true, wrap: false, new SearchOptions { Scope = window });
        TimeSpan time = watch.Elapsed;
        Assert.Null(hit);
        TimeLimit(time <= TimeSpan.FromSeconds(10), $"{time.TotalSeconds:F1} 秒");
    }
}
