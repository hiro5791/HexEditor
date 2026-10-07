using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>ENG-05 の仕様 3: バックグラウンドの処理は開始時のスナップショットを読み、処理中の編集の影響を受けない。</summary>
public sealed class SearchWhileEditingTests
{
    private const long MiB = TestDataCatalog.MiB;

    [Fact]
    [Trait(TC, "TC-ENG-05-03")]
    public async Task EditsDuringSearchDoNotChangeTheResults()
    {
        // 前提: 遅いデータソース (読み込み 1 MiB ごとに 5 ms。検索は 4 MiB のチャンクで読むので 1 回の読み込みに 20 ms) で TD-MARKERS-1G を開く。
        var slow = new FaultyByteSource(FileByteSource.Open(TestDataCatalog.Get("TD-MARKERS-1G")))
        {
            Delay = TimeSpan.FromMilliseconds(5 * SearchEngine.DefaultChunkSize / MiB),
        };
        using var doc = new Document(slow, Options());
        var center = new OperationCenter();

        // 手順 1: 40 30 30 30 のすべて検索を始める (アプリと同じく、長時間処理として開始時のスナップショットを読む)。
        LongRunningOperation? operation = null;
        DocumentSnapshot started = doc.Current;
        Task<SearchResults> search = center.RunAsync("検索", OperationKind.ReadOnly, doc, started.Length, op =>
        {
            operation = op;
            return Task.Run(() => SearchEngine.FindAll(started, SearchPattern.FromHex("40 30 30 30"), operation: op));
        });

        // 手順 2: 進捗が 10% を超えたら、オフセット 0 に 1 MiB を挿入し、元の 0x20000000 の目印 (挿入で 1 MiB 後ろにずれた) を 00 で上書きする。
        while (operation is null || (operation.Fraction ?? 0) <= 0.1)
        {
            Assert.False(search.IsCompleted, "検索が先に終わりました (遅いデータソースの遅延が足りません)。");
            await Task.Delay(1);
        }

        doc.Insert(0, new byte[MiB]);
        doc.Overwrite(0x20000000 + MiB, new byte[TestDataCatalog.MarkerLength]);
        Assert.False(search.IsCompleted, "編集の前に検索が終わりました。");

        // 手順 3: 検索の完了を待つ。
        SearchResults results = await search;
        Assert.Equal(SearchResultsState.Completed, results.State);

        // 期待結果: 1,025 件 (2^20 ごとの 1,024 個と末尾の 1 個)。編集前の位置で、0x20000000 の目印も含む。
        long[] expected = [.. Enumerable.Range(0, 1024).Select(i => i * MiB), TestDataCatalog.GiB - TestDataCatalog.MarkerLength];
        Assert.Equal(1025, results.Count);
        Assert.Equal(expected, results.Matches.Select(m => m.Offset));
        Assert.Contains(results.Matches, m => m.Offset == 0x20000000);

        // 編集はドキュメントには入っている (検索の結果だけが開始時の内容のまま)。
        Assert.Equal(TestDataCatalog.GiB + MiB, doc.Length);
        Assert.Equal(new byte[TestDataCatalog.MarkerLength], Read(doc.Current, 0x20000000 + MiB, TestDataCatalog.MarkerLength));
    }
}
