using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>
/// 検索の設定 (FIND-01 の仕様 2 のチャンクの大きさ、「エラー」の読み込みエラーの扱い)、表示の文字コードの一覧でのテキストの検索 (FIND-07 の
/// 仕様 1)、古い結果の判定 (FIND-03 の「エラー」)、範囲を選択の入力履歴 (EDIT-04 の仕様 10)。
/// </summary>
public sealed class SearchSettingsTests
{
    [Theory]
    [InlineData(4096, 4 * 1024 * 1024)]
    [InlineData(256, 256 * 1024)]
    [InlineData(65536, 64 * 1024 * 1024)]
    [InlineData(1, 256 * 1024)]
    [InlineData(1_000_000, 64 * 1024 * 1024)]
    public void ChunkSizeSettingIsClampedToTheSpecRange(int kib, int bytes) => Assert.Equal(bytes, SearchSettings.ChunkSizeBytes(kib));

    [Theory]
    [InlineData("ask", ReadErrorPolicy.Ask)]
    [InlineData("skip", ReadErrorPolicy.Skip)]
    [InlineData("abort", ReadErrorPolicy.Abort)]
    [InlineData("other", ReadErrorPolicy.Ask)]
    [InlineData(null, ReadErrorPolicy.Ask)]
    public void ReadErrorSettingValues(string? value, ReadErrorPolicy policy) => Assert.Equal(policy, SearchSettings.ReadErrorPolicyOf(value));

    private static FakeByteSource BadSource()
    {
        var source = new FakeByteSource(0x10000, (_, s) => s.Clear(), SourceCapabilities.HasGaps);
        source.BadRanges.Add(new UnreadableRange(0x1000, 0x100, UnreadableReason.IoError));
        source.BadRanges.Add(new UnreadableRange(0x8000, 0x100, UnreadableReason.IoError));
        return source;
    }

    [Fact]
    public void AskingOnceAppliesTheAnswerToTheRestOfTheSearch()
    {
        using var doc = new Document(BadSource(), Options());
        int asked = 0;
        var decider = new ReadErrorDecider(ReadErrorPolicy.Ask, _ =>
        {
            asked++;
            return UnreadableAction.Skip;
        });
        var options = new SearchOptions { ChunkSize = 0x800, IncludeOverlapping = true, OnUnreadable = decider.Decide };
        using SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("00 00"), options);

        // 2 つの読めない範囲があっても尋ねるのは 1 回。どちらの範囲も飛ばして記録する。
        Assert.Equal(1, asked);
        Assert.Equal(2, results.SkippedRanges.Count);
        Assert.Equal(SearchResultsState.Completed, results.State);
    }

    [Fact]
    public void AbortAnswerStopsTheSearch()
    {
        using var doc = new Document(BadSource(), Options());
        var decider = new ReadErrorDecider(ReadErrorPolicy.Ask, _ => UnreadableAction.Abort);
        var options = new SearchOptions { OnUnreadable = decider.Decide };
        Assert.Throws<SearchAbortedException>(() => SearchEngine.Find(doc.Current, SearchPattern.FromHex("FF"), 0, true, false, options));
        Assert.Equal(1, decider.AskCount);
    }

    [Theory]
    [InlineData(ReadErrorPolicy.Skip, false)]
    [InlineData(ReadErrorPolicy.Abort, true)]
    public void FixedPoliciesNeverAsk(ReadErrorPolicy policy, bool aborts)
    {
        using var doc = new Document(BadSource(), Options());
        var decider = new ReadErrorDecider(policy, _ => throw new InvalidOperationException("must not ask"));
        var options = new SearchOptions { OnUnreadable = decider.Decide };
        if (aborts)
        {
            Assert.Throws<SearchAbortedException>(() => SearchEngine.Find(doc.Current, SearchPattern.FromHex("FF"), 0, true, false, options));
        }
        else
        {
            Assert.Null(SearchEngine.Find(doc.Current, SearchPattern.FromHex("FF"), 0, true, false, options));
        }

        Assert.Equal(0, decider.AskCount);
    }

    [Theory]
    [InlineData("ascii", 20127)]
    [InlineData("utf-8", 65001)]
    [InlineData("utf-16le", 1200)]
    [InlineData("utf-16be", 1201)]
    [InlineData("utf-32be", 12001)]
    [InlineData("cp932", 932)]
    [InlineData("cp37", 37)]
    [InlineData("cp1252", 1252)]
    [InlineData("cp51949", 51949)]
    public void CatalogEncodingsCanBeSearched(string id, int codePage)
    {
        Encoding encoding = TextEncodings.FromCatalogId(id)!;
        Assert.Equal(codePage, encoding.CodePage);

        // BOM を付けずに符号化する (FIND-07 の仕様 2)。
        SearchPattern pattern = SearchPattern.FromText("AB", encoding, new TextSearchOptions { CaseSensitive = true });
        Assert.Equal(encoding.GetBytes("AB"), pattern.Bytes);
    }

    [Fact]
    public void UnknownAndStatefulEncodingsAreNotOffered()
    {
        Assert.Null(TextEncodings.FromCatalogId("cp99999"));
        Assert.Null(TextEncodings.FromCatalogId("cp50220")); // ISO-2022-JP (状態を持つ)
        Assert.Null(TextEncodings.FromCatalogId(TextEncodings.DisplayEncodingId));
        Assert.True(TextEncodings.SupportsAlignment("utf-32le"));
        Assert.False(TextEncodings.SupportsAlignment("cp932"));
    }

    [Fact]
    public void HistoryWithTheOldEncodingNamesIsMigrated()
    {
        // 以前の検索履歴 (TextEncodingId の名前) は、一覧の名前に読み替える。
        var history = new SearchHistory();
        var old = new JsonObject
        {
            ["find"] = new JsonArray(
                new JsonObject { ["text"] = "a", ["kind"] = "Text", ["encoding"] = "ShiftJis" },
                new JsonObject { ["text"] = "b", ["kind"] = "Text", ["encoding"] = "cp1252" },
                new JsonObject { ["text"] = "c", ["kind"] = "Text" }),
        };
        history.Load(old);
        Assert.Equal(["cp932", "cp1252", TextEncodings.DisplayEncodingId], history.Find.Select(e => e.Conditions.Encoding));
    }

    [Fact]
    public void ResultsBecomeStaleWhenTheSourceIsReplaced()
    {
        using Document doc = SearchTestData.Doc([1, 2, 3, 1, 2, 3]);
        using SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("01 02"));
        Assert.False(results.IsStale(doc.Current));

        // 編集だけなら古くない (位置をずらして追える)。
        doc.Insert(0, [9]);
        Assert.False(results.IsStale(doc.Current));

        // 元データを読み直す (外部変更の検知の再読み込み。ENG-19) と古い結果になる。
        doc.ReplaceSource(new MemoryByteSource([1, 2, 3]));
        Assert.True(results.IsStale(doc.Current));
    }

    [Fact]
    public void InputHistoryKeepsTheLatestTwentyDistinctEntries()
    {
        IReadOnlyList<string> history = [];
        for (int i = 0; i < 25; i++)
        {
            history = InputHistory.Push(history, "0x" + i.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(InputHistory.Limit, history.Count);
        Assert.Equal("0x18", history[0]);

        // 同じ入力は先頭に移す。空白だけの入力は残さない。
        history = InputHistory.Push(history, " 0x10 ");
        history = InputHistory.Push(history, "   ");
        Assert.Equal("0x10", history[0]);
        Assert.Equal(1, history.Count(h => h == "0x10"));
        Assert.Equal(history, InputHistory.Parse(InputHistory.Serialize(history)));
        Assert.Empty(InputHistory.Parse("{broken"));
    }
}
