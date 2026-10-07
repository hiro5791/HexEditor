using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>FIND-01、FIND-05〜FIND-09 の検索。</summary>
public sealed class SearchEngineTests
{
    private static Document Doc(byte[] data) => new(new MemoryByteSource(data), Options());

    [Fact]
    public void MatchesAcrossChunkBoundariesAreFound()
    {
        // チャンク 64 KiB の境界をまたぐ位置に目印を置く。
        const int chunk = 64 * 1024;
        byte[] data = new byte[chunk * 4];
        byte[] needle = "BOUNDARY-MARK"u8.ToArray();
        long[] positions = [chunk - 3, 2 * chunk - needle.Length + 1, 3 * chunk];
        foreach (long p in positions)
        {
            needle.CopyTo(data, p);
        }

        using Document doc = Doc(data);
        SearchPattern pattern = SearchPattern.Literal(needle);
        var found = new List<long>();
        long start = 0;
        while (SearchEngine.Find(doc.Current, pattern, start, forward: true, wrap: false, chunkSize: chunk) is { } hit)
        {
            found.Add(hit.Offset);
            start = hit.Offset + 1;
        }

        Assert.Equal(positions, found);

        // 後方検索でも同じ位置が逆順で見つかる。
        var back = new List<long>();
        long end = data.Length;
        while (SearchEngine.Find(doc.Current, pattern, end, forward: false, wrap: false, chunkSize: chunk) is { } hit)
        {
            back.Add(hit.Offset);
            end = hit.Offset;
        }

        Assert.Equal(positions.Reverse(), back);
    }

    [Fact]
    public void ResultsAreIndependentOfChunkSize()
    {
        var rng = new Random(91);
        byte[] data = new byte[300_000];
        rng.NextBytes(data);
        byte[] needle = data.AsSpan(123_456, 5).ToArray();
        using Document doc = Doc(data);
        var pattern = SearchPattern.Literal(needle);
        long? expected = SearchEngine.Find(doc.Current, pattern, 0, true, false)?.Offset;
        foreach (int chunk in new[] { 7, 64, 4096, 65536 })
        {
            Assert.Equal(expected, SearchEngine.Find(doc.Current, pattern, 0, true, false, chunkSize: chunk)?.Offset);
        }
    }

    [Fact]
    public void OverlappingMatchesAreFoundInTurn()
    {
        using Document doc = Doc("AAAA"u8.ToArray());
        var pattern = SearchPattern.FromText("AA", Encoding.ASCII);
        Assert.Equal(0, SearchEngine.Find(doc.Current, pattern, 0, true, false)!.Value.Offset);
        Assert.Equal(1, SearchEngine.Find(doc.Current, pattern, 1, true, false)!.Value.Offset);
        Assert.Equal(2, SearchEngine.Find(doc.Current, pattern, 2, true, false)!.Value.Offset);
        Assert.Null(SearchEngine.Find(doc.Current, pattern, 3, true, false));
    }

    [Fact]
    public void WrapAroundIsReported()
    {
        using Document doc = Doc("xxABxxxx"u8.ToArray());
        var pattern = SearchPattern.FromText("AB", Encoding.ASCII);
        SearchHit? hit = SearchEngine.Find(doc.Current, pattern, 5, forward: true, wrap: true);
        Assert.Equal(new SearchHit(2, 2, Wrapped: true), hit);
        Assert.Null(SearchEngine.Find(doc.Current, pattern, 5, forward: true, wrap: false));
        Assert.Equal(new SearchHit(2, 2, Wrapped: true), SearchEngine.Find(doc.Current, pattern, 1, forward: false, wrap: true));
    }

    [Fact]
    public void UnsavedEditsAreSearched()
    {
        using Document doc = Doc(new byte[1000]);
        doc.Overwrite(500, [0xDE, 0xAD]);
        Assert.Equal(500, SearchEngine.Find(doc.Current, SearchPattern.FromHex("DE AD"), 0, true, false)!.Value.Offset);
    }

    [Theory]
    [InlineData("DE ?? BE EF", 3)]
    [InlineData("D? AD", 3)]
    [InlineData("?? ?? BE", 3)]
    public void WildcardsMatch(string hex, long expected)
    {
        using Document doc = Doc([0, 0xDE, 0, 0xDE, 0xAD, 0xBE, 0xEF, 0]);
        Assert.Equal(expected, SearchEngine.Find(doc.Current, SearchPattern.FromHex(hex), 0, true, false)!.Value.Offset);
    }

    [Theory]
    [InlineData("DE AD BE EF")]
    [InlineData("0xDE,0xAD,0xBE,0xEF")]
    [InlineData("\\xDE\\xAD\\xBE\\xEF")]
    [InlineData("DEADBEEF")]
    public void HexNotationsAreEquivalent(string hex) =>
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, SearchPattern.FromHex(hex).Bytes);

    [Theory]
    [InlineData("DEA", PatternError.OddDigits)]
    [InlineData("", PatternError.Empty)]
    [InlineData("ZZ", PatternError.InvalidCharacter)]
    public void InvalidHexIsRejected(string hex, PatternError error) =>
        Assert.Equal(error, Assert.Throws<PatternException>(() => SearchPattern.FromHex(hex)).Error);

    [Fact]
    public void TextIsEncodedWithoutBom()
    {
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, SearchPattern.FromText("AB", Encoding.Unicode).Bytes);
        Assert.Equal(new byte[] { 0xE3, 0x81, 0x82 }, SearchPattern.FromText("あ", Encoding.UTF8).Bytes);
        PatternException ex = Assert.Throws<PatternException>(() => SearchPattern.FromText("あ", Encoding.ASCII));
        Assert.Equal(PatternError.NotEncodable, ex.Error);
        Assert.Equal("あ", ex.Detail);
    }

    [Fact]
    public void HundredGigabyteMarkerIsFoundNearEnd()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        SearchPattern pattern = SearchPattern.Literal(TestDataCatalog.Marker(doc.Length - TestDataCatalog.MarkerLength));
        SearchHit? hit = SearchEngine.Find(doc.Current, pattern, doc.Length - TestDataCatalog.MiB, true, false);
        Assert.Equal(doc.Length - TestDataCatalog.MarkerLength, hit!.Value.Offset);
    }
}
