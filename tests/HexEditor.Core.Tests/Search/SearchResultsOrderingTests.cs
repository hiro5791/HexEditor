using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;

namespace HexEditor.Core.Tests.Search;

/// <summary>結果一覧の並べ替えと絞り込み (00-overview 9 章の「結果一覧」、FIND-20 の仕様 9)。</summary>
public sealed class SearchResultsOrderingTests
{
    /// <summary>`41 xx` (xx = 3, 1, 2, 1) が 0x10 ごとにあるデータ。</summary>
    private static Document Sample()
    {
        byte[] data = new byte[0x80];
        byte[] seconds = [3, 1, 2, 1];
        for (int k = 0; k < seconds.Length; k++)
        {
            data[k * 0x10] = 0x41;
            data[(k * 0x10) + 1] = seconds[k];
        }

        return Doc(data);
    }

    private static (SearchResults Results, SearchResultRowFactory Factory) Find(Document doc)
    {
        SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("41 ??"));
        return (results, new SearchResultRowFactory(results, doc.Current, Encoding.ASCII));
    }

    [Fact]
    public void Sorting_by_data_is_stable_and_can_be_reversed()
    {
        using Document doc = Sample();
        (SearchResults results, SearchResultRowFactory factory) = Find(doc);
        using (results)
        {
            Assert.Equal([0L, 1, 2, 3], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, null)!);
            Assert.Equal([1L, 3, 2, 0], SearchResultsOrdering.Build([factory], SearchResultSortKey.Hex, false, null)!);
            Assert.Equal([0L, 2, 3, 1], SearchResultsOrdering.Build([factory], SearchResultSortKey.Hex, true, null)!);
            Assert.Equal([3L, 2, 1, 0], SearchResultsOrdering.Build([factory], SearchResultSortKey.Offset, true, null)!);
        }
    }

    [Fact]
    public void Filtering_matches_hex_without_spaces_and_text_ignoring_case()
    {
        using Document doc = Sample();
        (SearchResults results, SearchResultRowFactory factory) = Find(doc);
        using (results)
        {
            Assert.Equal([1L, 3], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "4101")!);
            Assert.Equal([1L, 3], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "41 01")!);
            Assert.Equal([0L, 1, 2, 3], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "a")!);
            Assert.Empty(SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "zz")!);
        }
    }
}
