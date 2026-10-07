using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>すべて検索・件数の数え上げ (FIND-01、FIND-11、FIND-12 の Core 側)。</summary>
public sealed class FindAllTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;

    [Fact]
    [Trait(TC, "TC-FIND-01-01")]
    public void MatchesAcrossChunkBoundariesAreReportedOnce()
    {
        using Document doc = Doc(Chunk16M);
        SearchPattern p = SearchPattern.Literal(ChunkPattern);

        // 手順 1: すべて検索。重複はない。
        long[] all = Offsets(SearchEngine.FindAll(doc.Current, p));
        Assert.Equal(ChunkPositions, all);

        // 手順 2: 先頭から次を検索を繰り返す (折り返しなし)。
        var forward = new List<long>();
        long start = 0;
        while (SearchEngine.Find(doc.Current, p, start, forward: true, wrap: false) is { } hit)
        {
            forward.Add(hit.Offset);
            start = hit.Offset + 1;
        }

        Assert.Equal(ChunkPositions, forward);

        // 手順 3: 末尾から前を検索を繰り返す。
        var backward = new List<long>();
        long end = doc.Length;
        while (SearchEngine.Find(doc.Current, p, end, forward: false, wrap: false) is { } hit)
        {
            backward.Add(hit.Offset);
            end = hit.Offset;
        }

        Assert.Equal(ChunkPositions.Reverse(), backward);

        // 手順 4: 並列数を変えても同じ。
        foreach (int parallelism in new[] { 1, 2, 8 })
        {
            var options = new SearchOptions { MaxDegreeOfParallelism = parallelism };
            Assert.Equal(ChunkPositions, Offsets(SearchEngine.FindAll(doc.Current, p, options)));
        }
    }

    [Fact]
    [Trait(TC, "TC-FIND-01-02")]
    public void ResultsDoNotDependOnChunkSize()
    {
        int[] chunkSizes = [256 * KiB, 4 * MiB, 64 * MiB];

        // 手順 1。
        using (Document chunk = Doc(Chunk16M))
        {
            SearchPattern p = SearchPattern.Literal(ChunkPattern);
            foreach (int size in chunkSizes)
            {
                Assert.Equal(ChunkPositions, Offsets(SearchEngine.FindAll(chunk.Current, p, new SearchOptions { ChunkSize = size })));
            }
        }

        // 手順 2: Hex、ワイルドカード、大文字・小文字を区別しないテキスト。正規表現 (バイト列) は FIND-19 (フェーズ 1) で加える。
        using Document random = Doc(RandomData16M);
        SearchPattern[] patterns =
        [
            SearchPattern.FromHex("00 00"),
            SearchPattern.FromHex("?F ?? 0?"),
            SearchPattern.FromText("ab", Encoding.ASCII, new TextSearchOptions { CaseSensitive = false }),
        ];
        foreach (SearchPattern p in patterns)
        {
            foreach (bool overlapping in new[] { false, true })
            {
                List<SearchMatch>? expected = null;
                foreach (int size in chunkSizes)
                {
                    var options = new SearchOptions { ChunkSize = size, IncludeOverlapping = overlapping };
                    IReadOnlyList<SearchMatch> actual = SearchEngine.FindAll(random.Current, p, options).Matches;
                    expected ??= Naive(RandomData16M, p, overlapping);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    /// <summary>TC-FIND-01-02 の手順 3: 無作為なデータ・パターン・チャンクサイズで、基準の実装と同じ結果になる。</summary>
    [Property(MaxTest = 500)]
    [Trait(TC, "TC-FIND-01-02")]
    public Property RandomCasesMatchTheReferenceImplementation() => Prop.ForAll(Cases(), c =>
    {
        byte[] data = new byte[c.Length];
        new Random(c.Seed).NextBytes(data);

        // 一致が多く出るよう、値の種類を減らす。
        for (int i = 0; i < data.Length; i++)
        {
            data[i] &= c.ValueMask;
        }

        byte[] needle;
        var rng = new Random(c.Seed ^ 0x5A5A);
        if (c.FromData && data.Length >= c.PatternLength)
        {
            int at = rng.Next(data.Length - c.PatternLength + 1);
            needle = data.AsSpan(at, c.PatternLength).ToArray();
        }
        else
        {
            needle = new byte[c.PatternLength];
            rng.NextBytes(needle);
            for (int i = 0; i < needle.Length; i++)
            {
                needle[i] &= c.ValueMask;
            }
        }

        using Document doc = Doc(data);
        SearchPattern p = SearchPattern.Literal(needle);
        var options = new SearchOptions { ChunkSize = c.ChunkSize, IncludeOverlapping = true };
        Assert.Equal(Naive(data, p), SearchEngine.FindAll(doc.Current, p, options).Matches);
    });

    private sealed record RandomCase(int Length, int PatternLength, int ChunkSize, bool FromData, int Seed, byte ValueMask);

    private static Arbitrary<RandomCase> Cases() =>
        (from length in Gen.Frequency((1, Gen.Choose(0, 64)), (3, Gen.Choose(0, 2 * MiB)))
         from patternLength in Gen.Choose(1, 64)
         from chunk in Gen.Choose(256 * KiB, 2 * MiB)
         from fromData in Gen.Frequency((3, Gen.Constant(true)), (1, Gen.Constant(false)))
         from seed in Gen.Choose(0, int.MaxValue)
         from mask in Gen.Elements<byte>(0xFF, 0x03, 0x01)
         select new RandomCase(length, patternLength, chunk, fromData, seed, mask)).ToArbitrary();

    [Fact]
    public void SmallChunksGiveTheSameResultsForEveryKindOfPattern()
    {
        var rng = new Random(4401);
        byte[] data = new byte[200_000];
        rng.NextBytes(data);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(data[i] % 4 == 0 ? 'a' + data[i] % 3 : data[i] % 4 == 1 ? 'A' + data[i] % 3 : data[i]);
        }

        using Document doc = Doc(data);
        SearchPattern[] patterns =
        [
            SearchPattern.FromHex("61 62"),
            SearchPattern.FromHex("6? ?? 4?"),
            SearchPattern.FromHex("?? 41"),
            SearchPattern.FromText("abc", Encoding.ASCII, new TextSearchOptions { CaseSensitive = false }),
        ];
        foreach (SearchPattern p in patterns)
        {
            List<SearchMatch> expected = Naive(data, p);
            foreach (int chunk in new[] { 1, 3, 7, 4096 })
            {
                foreach (int parallelism in new[] { 1, 3 })
                {
                    var options = new SearchOptions { ChunkSize = chunk, MaxDegreeOfParallelism = parallelism, IncludeOverlapping = true };
                    Assert.Equal(expected, SearchEngine.FindAll(doc.Current, p, options).Matches);
                }
            }
        }
    }

    [Fact]
    public void NonOverlappingFindAllContinuesAfterTheMatchEvenAcrossChunks()
    {
        // FIND-20 の受け入れ基準 6: `AAAA` の中の `AA` は、重なる一致を含めると 3 件、含めないと 2 件。
        using Document doc = Doc(Aaaa());
        SearchPattern aa = SearchPattern.FromText("AA", Encoding.ASCII);
        Assert.Equal([0L, 1L, 2L, 0x20L], Offsets(SearchEngine.FindAll(doc.Current, aa, new SearchOptions { IncludeOverlapping = true })));
        Assert.Equal([0L, 2L, 0x20L], Offsets(SearchEngine.FindAll(doc.Current, aa)));

        // 一致がチャンクの境界をまたいで次のチャンクに続く場合も、並列数・チャンクサイズに関係なく同じ。
        byte[] runs = Encoding.ASCII.GetBytes(new string('A', 1001) + "xx" + new string('A', 37));
        using Document doc2 = Doc(runs);
        List<SearchMatch> expected = Naive(runs, aa, overlapping: false);
        foreach (int chunk in new[] { 1, 2, 3, 10, 999 })
        {
            foreach (int parallelism in new[] { 1, 4 })
            {
                Assert.Equal(expected, SearchEngine.FindAll(doc2.Current, aa, new SearchOptions { ChunkSize = chunk, MaxDegreeOfParallelism = parallelism }).Matches);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-FIND-11-02")]
    public void MatchesCrossingTheScopeEdgeAreNotReported()
    {
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        long[] Find(params SearchRange[] ranges) =>
            Offsets(SearchEngine.FindAll(doc.Current, p, new SearchOptions { Scope = SearchScope.Of(ranges) }));

        Assert.Empty(Find(SearchRange.FromInclusive(0x401, 0x7FF)));
        Assert.Equal([0x400L], Find(SearchRange.FromInclusive(0x400, 0x802)));
        Assert.Equal([0x400L, 0x800L], Find(SearchRange.FromInclusive(0x400, 0x803)));

        // マルチ選択: (0x3FE, 4) と (0x800, 4)。0x400 の一致は 1 つ目の要素の終わりをまたぐ。
        Assert.Equal([0x800L], Find(new SearchRange(0x3FE, 4), new SearchRange(0x800, 4)));
    }

    [Fact]
    public void ScopeOutsideTheDocumentIsClipped()
    {
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        var options = new SearchOptions { Scope = SearchScope.Of(0xF9BFE, 0x100000) };
        Assert.Equal([0xF9C00L], Offsets(SearchEngine.FindAll(doc.Current, p, options)));
        Assert.Equal(doc.Length - 0xF9BFE, options.Scope.TotalLength(doc.Length));
    }

    [Fact]
    public void MatchesAreStreamedInAscendingOrder()
    {
        using Document doc = Doc(Hits1000());
        var results = new SearchResults(doc.Current, SearchPattern.FromHex("12 34 56 78"),
            new SearchOptions { ChunkSize = 4096, MaxDegreeOfParallelism = 4 });
        var counts = new List<int>();
        results.MatchesAdded += (_, count) => counts.Add(count);
        SearchEngine.FindAll(results);

        Assert.Equal(SearchResultsState.Completed, results.State);
        Assert.Equal(Enumerable.Range(0, 1000).Select(k => k * 1024L), Offsets(results));
        Assert.True(counts.Count > 1, "結果は少しずつ追加される");
        Assert.Equal(counts.Order(), counts);
        Assert.Equal(1000, counts[^1]);
        Assert.Same(doc.Current, results.Snapshot);
    }

    [Fact]
    public void CountGivesTheMatchNumber()
    {
        // FIND-12 の仕様 1: 「n / N」の N と n。
        using Document doc = Doc(Hits1000());
        SearchResults count = SearchEngine.Count(doc.Current, SearchPattern.FromHex("12 34 56 78"));
        Assert.Equal(1000, count.Count);
        Assert.False(count.LimitReached);
        Assert.Equal(0, count.IndexOf(0));
        Assert.Equal(2, count.IndexOf(0x800));
        Assert.Equal(-1, count.IndexOf(0x801));
        Assert.Equal(3, count.CountBefore(0x801));
        Assert.Equal([new SearchMatch(0x400, 4), new SearchMatch(0x800, 4)], count.Overlapping(0x403, 0x401));
    }

    [Fact]
    public void CountIncludesOverlappingMatchesLikeFindNext()
    {
        using Document doc = Doc(Aaaa());
        Assert.Equal(4, SearchEngine.Count(doc.Current, SearchPattern.FromText("AA", Encoding.ASCII)).Count);
    }

    [Fact]
    public void CountStopsAboveOneMillionInOffsetOrder()
    {
        // FIND-12 の仕様 3、FIND-20 の仕様 6: 上限ちょうどの件数で、開始の小さい順 (並列数に関係なく同じ)。
        using Document doc = Doc(Hits1500K());
        SearchPattern p = SearchPattern.FromHex("AB CD");
        foreach (int parallelism in new[] { 1, 8 })
        {
            SearchResults count = SearchEngine.Count(doc.Current, p, new SearchOptions { MaxDegreeOfParallelism = parallelism, ChunkSize = 256 * KiB });
            Assert.True(count.LimitReached);
            Assert.Equal(SearchEngine.CountLimit, count.Count);
            Assert.Equal((SearchEngine.CountLimit - 1) * 4L, count[count.Count - 1].Offset);
        }
    }

    [Fact]
    public void ExactlyTheLimitIsNotReportedAsLimitReached()
    {
        using Document doc = Doc(Hits1000());
        SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("12 34 56 78"), new SearchOptions { MaxMatches = 1000 });
        Assert.Equal(SearchResultsState.Completed, results.State);
        SearchResults limited = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("12 34 56 78"), new SearchOptions { MaxMatches = 999 });
        Assert.Equal(SearchResultsState.LimitReached, limited.State);
        Assert.Equal(999, limited.Count);
    }

    [Theory]
    [InlineData(1L << 30, true)]
    [InlineData((1L << 30) + 1, false)]
    [InlineData(2L << 30, false)]
    public void CountIsAutomaticUpToOneGibibyte(long bytes, bool automatic) =>
        Assert.Equal(automatic, SearchEngine.CountsAutomatically(bytes));

    [Fact]
    public void SearchingASnapshotIgnoresLaterEdits()
    {
        // FIND-03 の仕様 1: 検索中の編集は結果に影響しない。
        using Document doc = Doc(Hits1000());
        DocumentSnapshot snapshot = doc.Current;
        doc.Insert(0, ReferenceBytes(100));
        Assert.Equal(1000, SearchEngine.FindAll(snapshot, SearchPattern.FromHex("12 34 56 78")).Count);
        Assert.Equal(1025, SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("12 34 56 78")).Count);

        static byte[] ReferenceBytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(0x12 + 0x22 * (i % 4))).ToArray();
    }

    [Fact]
    public void FindInViewReturnsOnlyMatchesOverlappingTheView()
    {
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        IReadOnlyList<SearchMatch> matches = [];
        bool complete = false;
        for (int i = 0; i < 1000 && !complete; i++)
        {
            matches = SearchEngine.FindInView(doc.Current, p, 0x3FE, 0x404, null, out complete);
            if (!complete)
            {
                Thread.Sleep(5);
            }
        }

        Assert.True(complete);
        Assert.Equal([new SearchMatch(0x400, 4), new SearchMatch(0x800, 4)], matches);

        // 一致の途中から始まる表示範囲と、範囲で絞る場合。
        Assert.Equal([new SearchMatch(0x400, 4)], SearchEngine.FindInView(doc.Current, p, 0x403, 1, null, out _));
        Assert.Empty(SearchEngine.FindInView(doc.Current, p, 0x3FE, 0x10, SearchScope.Of(0x401, 0x1000), out _));
        Assert.Equal([new SearchMatch(0x800, 4)], SearchEngine.FindInView(doc.Current, p, 0x3FE, 0x404, SearchScope.Of(0x401, 0x1000), out _));
    }
}
