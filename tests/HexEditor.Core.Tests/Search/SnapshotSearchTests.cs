using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>編集中のスナップショットに対する検索 (FIND-03)。</summary>
public sealed class SnapshotSearchTests
{
    private static readonly byte[] Pattern = [0x12, 0x34, 0x56, 0x78];

    /// <summary>
    /// TC-FIND-03-01 の Core 側: すべて検索の結果一覧 (F1-07) はフェーズ 1 のため、すべて検索の処理 (SearchEngine.FindAll) に対して
    /// 確かめる。遅い読み込み (読み込み 1 回ごとに 2 ms。約 1 秒) で検索している間に、先頭に 100 バイトのパターンを挿入する。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-FIND-03-01")]
    public async Task Inserting_at_the_start_during_find_all_does_not_change_the_count()
    {
        var source = new FakeByteSource(Hits1000(), SourceCapabilities.CanResize | SourceCapabilities.CanWrite) { Delay = TimeSpan.FromMilliseconds(2) };
        using var doc = new Document(source, Options());
        SearchPattern pattern = SearchPattern.Literal(Pattern);

        // 1. すべて検索を始める (小さいチャンクで読み、途中で編集できるようにする)。
        var results = new SearchResults(doc.Current, pattern, new SearchOptions { ChunkSize = 4096, MaxDegreeOfParallelism = 1 });
        var firstMatch = new TaskCompletionSource();
        results.MatchesAdded += (_, _) => firstMatch.TrySetResult();
        Task search = Task.Run(() => SearchEngine.FindAll(results));

        // 2. 結果が 1 件以上出たら、先頭に 100 バイトの `12 34 56 78` のパターンを挿入する (25 件増える)。
        await firstMatch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SearchResultsState.Running, results.State);
        doc.InsertPattern(0, 100, Pattern);
        Assert.Equal(1024 * 1024 + 100, doc.Length);

        // 3. 検索の完了を待つ: 1,000 件 (完了)。挿入した 25 件は含まれない。
        await search.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SearchResultsState.Completed, results.State);
        Assert.Equal(1000, results.Count);
        Assert.Equal(Enumerable.Range(0, 1000).Select(k => (long)k * 1024), Offsets(results));

        // 4. 同じ条件でもう一度すべて検索する: 1,025 件。
        source.Delay = TimeSpan.Zero;
        Assert.Equal(1025, SearchEngine.FindAll(doc.Current, pattern).Count);
    }
}
