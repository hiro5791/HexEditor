using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>読めない範囲の扱い (FIND-01 の「エラー」)、進捗とキャンセル (FIND-02 の Core 側)。</summary>
public sealed class UnreadableAndCancelTests
{
    private const long Length = 1024 * 1024;
    private static readonly SearchPattern Zeros = SearchPattern.FromHex("00 00");

    /// <summary>すべて 00 で、[0x1000, 0x3000) と [0x80000, 0x80010) が読めないデータソース。</summary>
    private static FakeByteSource BadSource(FailureMode mode = FailureMode.Precise)
    {
        var source = new FakeByteSource(Length, (_, s) => s.Clear(), SourceCapabilities.HasGaps) { FailureMode = mode };
        source.BadRanges.Add(new UnreadableRange(0x1000, 0x2000, UnreadableReason.IoError, 23));
        source.BadRanges.Add(new UnreadableRange(0x80000, 0x10, UnreadableReason.Unallocated));
        return source;
    }

    private static Document Open(IByteSource source) => new(source, Options());

    [Fact]
    public void UnreadableBytesNeverMatch()
    {
        // 読めない範囲は 0 で埋めて返るが、`00 00` の一致として報告しない。
        using Document doc = Open(BadSource());
        var seen = new List<UnreadableRange>();
        var options = new SearchOptions { ChunkSize = 0x800, IncludeOverlapping = true, OnUnreadable = r => { seen.Add(r); return UnreadableAction.Skip; } };
        SearchResults results = SearchEngine.FindAll(doc.Current, Zeros, options);

        Assert.All(results.Matches, m => Assert.False(m.Offset < 0x3000 && m.End > 0x1000 || m.Offset < 0x80010 && m.End > 0x80000));
        Assert.Equal(Length - 1 - (0x2000 + 1) - (0x10 + 1), results.Count);
        Assert.Equal(new[] { (0x1000L, 0x2000L, UnreadableReason.IoError), (0x80000L, 0x10L, UnreadableReason.Unallocated) },
            results.SkippedRanges.Select(r => (r.Offset, r.Length, r.Reason)));
        Assert.Equal(results.SkippedRanges.Sum(r => r.Length), seen.Sum(r => r.Length));
        Assert.Equal(23, results.SkippedRanges[0].ErrorCode);
    }

    [Fact]
    public void FindNextSkipsUnreadableBytesAndReportsEachRangeOnce()
    {
        using Document doc = Open(BadSource());
        var seen = new List<UnreadableRange>();
        var options = new SearchOptions { ChunkSize = 0x700, OnUnreadable = r => { seen.Add(r); return UnreadableAction.Skip; } };

        Assert.Equal(0x3000, SearchEngine.Find(doc.Current, Zeros, 0xFFF, true, false, options)!.Value.Offset);
        Assert.Equal(0xFFE, SearchEngine.Find(doc.Current, Zeros, 0x3000, false, false, options)!.Value.Offset);
        Assert.Equal(0x80010, SearchEngine.Find(doc.Current, Zeros, 0x7FFFF, true, false, options)!.Value.Offset);

        // 1 回の検索の中では、重なり部分を読み直しても同じバイトを 2 回知らせない。
        seen.Clear();
        Assert.Equal(0x3000, SearchEngine.Find(doc.Current, Zeros, 0xFFF, true, false, options)!.Value.Offset);
        Assert.Equal(0x2000, seen.Sum(r => r.Length));
        Assert.Equal(seen.Count, seen.Select(r => r.Offset).Distinct().Count());
    }

    [Fact]
    public void AbortStopsTheSearch()
    {
        using Document doc = Open(BadSource());
        var options = new SearchOptions { OnUnreadable = _ => UnreadableAction.Abort };

        // 読めない範囲より前に一致があれば、知らせる前に見つかる。
        Assert.Equal(0, SearchEngine.Find(doc.Current, Zeros, 0, true, false, options)!.Value.Offset);

        SearchAbortedException ex = Assert.Throws<SearchAbortedException>(() =>
            SearchEngine.Find(doc.Current, SearchPattern.FromHex("FF"), 0, true, false, options));
        Assert.Equal(0x1000, ex.Range.Offset);

        var results = new SearchResults(doc.Current, Zeros, options with { ChunkSize = 0x400, MaxDegreeOfParallelism = 4, IncludeOverlapping = true });
        Assert.Throws<SearchAbortedException>(() => SearchEngine.FindAll(results));
        Assert.Equal(SearchResultsState.Failed, results.State);
        Assert.Equal(0xFFF, results.Count); // 読めない範囲より前の一致は残る
    }

    [Fact]
    public void WholeRequestFailureSkipsOnlyWhatTheSourceReports()
    {
        // 実際のデバイスのように、読めない範囲にかかる読み込み全体が失敗する場合 (チャンクごと飛ばす)。
        using Document doc = Open(BadSource(FailureMode.WholeRequest));
        var options = new SearchOptions { ChunkSize = 0x1000, MaxDegreeOfParallelism = 1 };
        SearchResults results = SearchEngine.FindAll(doc.Current, Zeros, options);
        Assert.All(results.Matches, m => Assert.False(m.Offset < 0x3000 && m.End > 0x1000));
        Assert.True(results.SkippedRanges.Sum(r => r.Length) >= 0x2010);
        Assert.Contains(results.Matches, m => m.Offset >= 0x3000 && m.Offset < 0x80000); // 次のチャンクからは読める
    }

    [Fact]
    public async Task ProgressReachesTheScopeLength()
    {
        using Document doc = Open(new MemoryByteSource(new byte[3 * 1024 * 1024 + 5]));
        var center = new OperationCenter();
        var scope = SearchScope.Of(100, 2 * 1024 * 1024);
        long total = scope.TotalLength(doc.Length);
        SearchResults results = await center.RunAsync("検索", OperationKind.ReadOnly, doc, total, op =>
            Task.FromResult(SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("FF"), new SearchOptions { Scope = scope, ChunkSize = 64 * 1024 }, op)));
        Assert.Equal(SearchResultsState.Completed, results.State);
        LongRunningOperation done = center.History[0];
        Assert.Equal(OperationState.Completed, done.State);
        Assert.Equal(total, done.ProcessedBytes);
    }

    [Fact]
    public async Task CancelledFindAllKeepsTheMatchesFoundSoFar()
    {
        // 1 回の読み込みに 5 ms かかるデータソースで、チャンクが 4 KiB、並列数 2 (全体で 0.6 秒以上)。
        var source = new FakeByteSource(1024 * 1024, (o, s) =>
        {
            s.Clear();
            for (long p = (o + 1023) / 1024 * 1024; p < o + s.Length; p += 1024)
            {
                s[(int)(p - o)] = 0xAB;
            }
        }, SourceCapabilities.None) { Delay = TimeSpan.FromMilliseconds(5) };
        using Document doc = Open(source);
        var center = new OperationCenter();
        var options = new SearchOptions { ChunkSize = 4096, MaxDegreeOfParallelism = 2 };
        var results = new SearchResults(doc.Current, SearchPattern.FromHex("AB"), options);
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        results.MatchesAdded += (_, _) => firstBatch.TrySetResult();
        LongRunningOperation? running = null;
        Task task = center.RunAsync("すべて検索", OperationKind.ReadOnly, doc, results.TotalBytes, op =>
        {
            running = op;
            SearchEngine.FindAll(results, op);
            return Task.CompletedTask;
        });

        await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        running!.Cancel();
        DateTime cancelledAt = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(DateTime.UtcNow - cancelledAt < TimeSpan.FromMilliseconds(500), "キャンセルの後すぐに止まる");
        int readsAfter = source.Reads.Count;
        await Task.Delay(50);
        Assert.Equal(readsAfter, source.Reads.Count);

        Assert.Equal(SearchResultsState.Cancelled, results.State);
        Assert.Equal(OperationState.Cancelled, running.State);
        Assert.InRange(results.Count, 1, 1023);
        Assert.Equal(Enumerable.Range(0, results.Count).Select(k => k * 1024L), results.Matches.Select(m => m.Offset));
    }

    [Fact]
    public void CancelledFindNextThrows()
    {
        using Document doc = Open(new MemoryByteSource(new byte[1024 * 1024]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SearchEngine.Find(doc.Current, SearchPattern.FromHex("FF"), 0, true, true, new SearchOptions(), null, cts.Token));
    }
}
