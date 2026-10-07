using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;

namespace HexEditor.Core.Tests.Search;

/// <summary>次 / 前を検索の開始位置・方向・折り返し・範囲 (FIND-09、FIND-11 の Core 側)。</summary>
public sealed class FindNextTests
{
    private static readonly SearchPattern Aa = SearchPattern.FromText("AA", Encoding.ASCII);

    [Fact]
    public void NextStartsAfterTheSelectionOnlyWhenItIsThePreviousMatch()
    {
        // TC-FIND-09-01 の流れ (UI の部分を除く): カーソル 0 から次を検索を 3 回。
        using Document doc = Doc(Aaaa());
        var nav = new FindNavigator();
        long cursor = 0;
        long selStart = 0;
        long selLength = 0;
        var visited = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            SearchHit hit = nav.FindNext(doc.Current, Aa, forward: true, wrap: true, cursor, selStart, selLength)!.Value;
            visited.Add(hit.Offset);
            (cursor, selStart, selLength) = (hit.Offset, hit.Offset, hit.Length);
        }

        Assert.Equal([0L, 1L, 2L], visited);

        // オフセット 0x30 をクリック (選択なし) して前を検索: カーソルより前で最も近い一致。
        SearchHit back = nav.FindNext(doc.Current, Aa, forward: false, wrap: true, 0x30, 0x30, 0)!.Value;
        Assert.Equal(0x20, back.Offset);

        // もう一度前を検索: 選択範囲が直前の一致なので、その開始 − 1 から後方へ。
        SearchHit back2 = nav.FindNext(doc.Current, Aa, forward: false, wrap: true, back.Offset, back.Offset, back.Length)!.Value;
        Assert.Equal(2, back2.Offset);
    }

    [Fact]
    public void SelectionThatIsNotThePreviousMatchStartsAtTheCursor()
    {
        var nav = new FindNavigator();
        nav.Remember(new SearchHit(0x10, 4, false));

        // 選択範囲が直前の一致と同じ: 前方は開始 + 1、後方は開始 (開始 − 1 から後方に探す)。
        Assert.Equal(0x11, nav.StartFor(forward: true, cursor: 0x10, selectionStart: 0x10, selectionLength: 4));
        Assert.Equal(0x10, nav.StartFor(forward: false, cursor: 0x10, selectionStart: 0x10, selectionLength: 4));

        // 長さが違う・別の選択・選択なし: カーソル位置。
        Assert.Equal(0x10, nav.StartFor(true, 0x10, 0x10, 3));
        Assert.Equal(0x14, nav.StartFor(true, 0x14, 0x12, 2));
        Assert.Equal(0x30, nav.StartFor(false, 0x30, 0x30, 0));

        nav.Reset();
        Assert.Equal(0x10, nav.StartFor(true, 0x10, 0x10, 4));
    }

    [Fact]
    public void NotFoundKeepsThePreviousMatch()
    {
        using Document doc = Doc(Aaaa());
        var nav = new FindNavigator();
        SearchHit last = nav.FindNext(doc.Current, Aa, true, false, 0x20, 0x20, 0)!.Value;
        Assert.Equal(0x20, last.Offset);
        Assert.Null(nav.FindNext(doc.Current, Aa, true, false, last.Offset, last.Offset, last.Length));
        Assert.Null(nav.FindNext(doc.Current, Aa, true, false, last.Offset, last.Offset, last.Length));
    }

    [Fact]
    public void WrapAroundIsReported()
    {
        // TC-FIND-09-02 の流れ (UI の部分を除く)。
        using Document doc = Doc(Aaaa());
        Assert.Equal(new SearchHit(0, 2, Wrapped: true), SearchEngine.Find(doc.Current, Aa, 0x30, forward: true, wrap: true));
        Assert.Null(SearchEngine.Find(doc.Current, Aa, 0x30, forward: true, wrap: false));
        Assert.Null(SearchEngine.Find(doc.Current, SearchPattern.FromText("ZZ", Encoding.ASCII), 0x30, true, true));
        Assert.Equal(new SearchHit(0x20, 2, Wrapped: true), SearchEngine.Find(doc.Current, Aa, 0, forward: false, wrap: true));
    }

    [Fact]
    public void WrapStaysInsideTheScope()
    {
        // TC-FIND-11-01 の手順 5 の流れ: 範囲 0x300〜0x8FF の中で 0x400、0x800、0x400 の順。
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        var options = new SearchOptions { Scope = SearchScope.Of(0x300, 0x600) };
        var nav = new FindNavigator();
        long cursor = 0x10;
        long selStart = 0x10;
        long selLength = 0;
        var visited = new List<(long, bool)>();
        for (int i = 0; i < 3; i++)
        {
            SearchHit hit = nav.FindNext(doc.Current, p, true, true, cursor, selStart, selLength, options)!.Value;
            visited.Add((hit.Offset, hit.Wrapped));
            (cursor, selStart, selLength) = (hit.Offset, hit.Offset, hit.Length);
        }

        Assert.Equal([(0x400L, false), (0x800L, false), (0x400L, true)], visited);

        // 後方も範囲の中で折り返す。
        Assert.Equal(new SearchHit(0x800, 4, true), SearchEngine.Find(doc.Current, p, 0x400, false, true, options));
        Assert.Equal(new SearchHit(0x400, 4, false), SearchEngine.Find(doc.Current, p, 0x800, false, true, options));
    }

    [Fact]
    public void FindNextInMultipleRangesSkipsMatchesCrossingTheEdges()
    {
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        var options = new SearchOptions { Scope = SearchScope.Of([new SearchRange(0x3FE, 4), new SearchRange(0x800, 4), new SearchRange(0xC00, 4)]) };
        Assert.Equal(0x800, SearchEngine.Find(doc.Current, p, 0, true, false, options)!.Value.Offset);
        Assert.Equal(0xC00, SearchEngine.Find(doc.Current, p, 0x801, true, false, options)!.Value.Offset);
        Assert.Equal(new SearchHit(0x800, 4, true), SearchEngine.Find(doc.Current, p, 0xC01, true, true, options));
        Assert.Equal(0x800, SearchEngine.Find(doc.Current, p, 0xC00, false, false, options)!.Value.Offset);
        Assert.Null(SearchEngine.Find(doc.Current, p, 0x800, false, false, options));
    }

    [Fact]
    public void BackwardSearchFindsTheNearestMatchBeforeStart()
    {
        var rng = new Random(909);
        byte[] data = new byte[100_000];
        rng.NextBytes(data);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] &= 0x03;
        }

        using Document doc = Doc(data);
        SearchPattern[] patterns =
        [
            SearchPattern.FromHex("01 02 03"),
            SearchPattern.FromHex("0? 02 ?? 03"),
            SearchPattern.FromHex("?? ?? 01"),
        ];
        foreach (SearchPattern p in patterns)
        {
            List<SearchMatch> all = Naive(data, p);
            foreach (int chunk in new[] { 5, 4096, SearchEngine.DefaultChunkSize })
            {
                var options = new SearchOptions { ChunkSize = chunk };
                for (int t = 0; t < 50; t++)
                {
                    long start = rng.Next(data.Length + 1);
                    SearchMatch? expectedBack = all.LastOrDefault(m => m.Offset < start) is { Length: > 0 } b ? b : null;
                    SearchMatch? expectedFwd = all.FirstOrDefault(m => m.Offset >= start) is { Length: > 0 } f ? f : null;
                    Assert.Equal(expectedBack?.Offset, SearchEngine.Find(doc.Current, p, start, false, false, options)?.Offset);
                    Assert.Equal(expectedFwd?.Offset, SearchEngine.Find(doc.Current, p, start, true, false, options)?.Offset);
                }
            }
        }
    }

    [Fact]
    public void CaseInsensitiveFindNextAcrossChunkBoundaries()
    {
        byte[] data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("xxxxHeLLo", 300)));
        using Document doc = Doc(data);
        SearchPattern p = SearchPattern.FromText("hello", Encoding.ASCII, new TextSearchOptions { CaseSensitive = false });
        foreach (int chunk in new[] { 1, 2, 7 })
        {
            var options = new SearchOptions { ChunkSize = chunk };
            var found = new List<long>();
            long start = 0;
            while (SearchEngine.Find(doc.Current, p, start, true, false, options) is { } hit)
            {
                found.Add(hit.Offset);
                start = hit.Offset + 1;
            }

            Assert.Equal(Enumerable.Range(0, 300).Select(i => i * 9L + 4), found);
            Assert.Equal(299 * 9L + 4, SearchEngine.Find(doc.Current, p, data.Length, false, false, options)!.Value.Offset);
        }
    }
}
