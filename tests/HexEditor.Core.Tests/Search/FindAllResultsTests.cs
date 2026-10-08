using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>すべて検索の結果 (FIND-20): 重なる一致、件数の上限と「続ける」、一時ファイルへの書き出し。</summary>
public sealed class FindAllResultsTests
{
    [Fact]
    [Trait(TC, "TC-FIND-20-06")]
    public void OverlappingMatchesAreOptional()
    {
        using Document doc = Doc(Aaaa());
        SearchPattern aa = SearchPattern.FromText("AA", Encoding.ASCII);
        SearchScope first4 = SearchScope.Of(0, 4);

        // 手順 1: オンで 0, 1, 2。
        Assert.Equal([0L, 1L, 2L], Offsets(SearchEngine.FindAll(doc.Current, aa, new SearchOptions { Scope = first4, IncludeOverlapping = true })));

        // 手順 2: オフで 0, 2。
        Assert.Equal([0L, 2L], Offsets(SearchEngine.FindAll(doc.Current, aa, new SearchOptions { Scope = first4 })));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ContinueAfterTheLimitHasNoDuplicatesOrGaps(int parallelism)
    {
        // TC-FIND-20-03 の Core の部分: 上限で止めた結果は昇順にちょうど上限の件数で、続けると残りが重ならずに続く。
        using Document doc = Doc(Hits1500K());
        SearchPattern p = SearchPattern.FromHex("AB CD");
        var options = new SearchOptions { MaxMatches = 1_000_000, MaxDegreeOfParallelism = parallelism, ChunkSize = 256 * 1024 };
        using var results = new SearchResults(doc.Current, p, options) { MemoryLimit = 300_000, SpillDirectory = SpillFolder() };
        SearchEngine.FindAll(results);
        Assert.Equal(SearchResultsState.LimitReached, results.State);
        Assert.Equal(1_000_000, results.Count);
        Assert.True(results.SpilledCount > 0);
        IReadOnlyList<SearchMatch> stopped = results.Matches;
        Assert.Equal(Enumerable.Range(0, 1_000_000).Select(k => 4L * k), stopped.Select(m => m.Offset));

        // 「続ける」: 上限は 2 倍。全体で 1,500,000 件、0, 4, …, 5,999,996。
        SearchEngine.ContinueFindAll(results);
        Assert.Equal(SearchResultsState.Completed, results.State);
        Assert.Equal(2_000_000, results.Limit);
        Assert.Equal(1_500_000, results.Count);
        Assert.Equal(Enumerable.Range(0, 1_500_000).Select(k => 4L * k), results.Matches.Select(m => m.Offset));
        Assert.Equal(1_000_000, results.IndexOf(4_000_000));
        Assert.Equal(1_499_999, results.CountBefore(5_999_996));
        Assert.Equal([new SearchMatch(4_000_000, 2)], results.Overlapping(4_000_000, 3));
    }

    [Fact]
    public void ContinueWithOverlappingMatchesResumesAfterTheLastStart()
    {
        byte[] data = new byte[100];
        Array.Fill(data, (byte)0x41);
        using Document doc = Doc(data);
        using var results = new SearchResults(doc.Current, SearchPattern.FromText("AA", Encoding.ASCII),
            new SearchOptions { IncludeOverlapping = true, MaxMatches = 10 });
        SearchEngine.FindAll(results);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), Offsets(results));
        SearchEngine.ContinueFindAll(results);
        SearchEngine.ContinueFindAll(results);
        SearchEngine.ContinueFindAll(results);
        Assert.Equal(Enumerable.Range(0, 80).Select(i => (long)i), Offsets(results));
        SearchEngine.ContinueFindAll(results);
        Assert.Equal(SearchResultsState.Completed, results.State);
        Assert.Equal(Enumerable.Range(0, 99).Select(i => (long)i), Offsets(results));
    }

    [Fact]
    public void ResultsBeyondTheMemoryLimitAreSpilledAndRemovedOnDispose()
    {
        using Document doc = Doc(Hits1000());
        string folder = SpillFolder();
        var results = new SearchResults(doc.Current, SearchPattern.FromHex("12 34 56 78"), SearchOptions.Default) { MemoryLimit = 100, SpillDirectory = folder };
        SearchEngine.FindAll(results);
        Assert.Equal(1000, results.Count);
        Assert.Equal(900, results.SpilledCount);
        Assert.Equal(Enumerable.Range(0, 1000).Select(k => k * 1024L), Offsets(results));
        Assert.Equal(new SearchMatch(999 * 1024, 4), results[999]);
        Assert.Single(Directory.GetFiles(folder));
        results.Dispose();
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void FailingToCreateTheSpillFileStopsAtTheMemoryLimit()
    {
        using Document doc = Doc(Hits1000());
        string blocked = Path.Combine(SpillFolder(), "file");
        File.WriteAllText(blocked, "x"); // フォルダの代わりにファイルがあるため、一時ファイルを作れない。
        using var results = new SearchResults(doc.Current, SearchPattern.FromHex("12 34 56 78"), SearchOptions.Default)
        {
            MemoryLimit = 100,
            SpillDirectory = Path.Combine(blocked, "sub"),
        };
        SearchEngine.FindAll(results);
        Assert.True(results.SpillFailed);
        Assert.Equal(SearchResultsState.LimitReached, results.State);
        Assert.Equal(100, results.Count);
        Assert.NotNull(results.SpillError);
    }

    [Fact]
    public void IncrementalWindowClipsTheScope()
    {
        SearchScope clipped = SearchScope.WholeDocument.Clip(100, 50, 1000, out bool truncated);
        Assert.True(truncated);
        Assert.Equal([new SearchRange(100, 50)], clipped.Resolve(1000));
        SearchScope rest = SearchScope.WholeDocument.Clip(990, 50, 1000, out truncated);
        Assert.False(truncated);
        Assert.Equal([new SearchRange(990, 10)], rest.Resolve(1000));
        SearchScope none = SearchScope.Of(0, 10).Clip(100, 50, 1000, out truncated);
        Assert.False(truncated);
        Assert.Empty(none.Resolve(1000));
    }

    private static string SpillFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "HexEditorTests", "search-spill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
