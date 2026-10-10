using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>一致しない箇所の検索 (FIND-25) と文字列の抽出 (FIND-32)。</summary>
public sealed class MismatchAndStringsTests
{
    private static SearchHit? Next(byte[] data, SearchPattern pattern, long start, bool forward = true)
    {
        using Document doc = Doc(data);
        return SearchEngine.Find(doc.Current, pattern, start, forward, wrap: false);
    }

    /// <summary>TD-FIND-GAPS: すべて 00 の中に、0x100000〜0x100003 に `12 34 56 78`、0x100100〜0x10011F に `AA` × 32 (0x100200 バイト)。</summary>
    internal static byte[] Gaps()
    {
        byte[] data = new byte[0x100200];
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(data, 0x100000);
        Array.Fill(data, (byte)0xAA, 0x100100, 32);
        return data;
    }

    [Fact]
    [Trait(TC, "TC-FIND-25-02")]
    public void RepetitionOriginAndMultiBytePatterns()
    {
        // 手順 1: `DE AD DE AD BE EF` で P = `DE AD`、開始 0 → オフセット 4。
        Assert.Equal(4, Next([0xDE, 0xAD, 0xDE, 0xAD, 0xBE, 0xEF], SearchPattern.Mismatch("DE AD", aligned: true), 0)!.Value.Offset);

        // 手順 2: `00 DE AD DE AD BE EF`、開始 1、起点を揃えない → 5 (起点 1 から `DE AD` を繰り返す)。
        byte[] step2 = [0x00, 0xDE, 0xAD, 0xDE, 0xAD, 0xBE, 0xEF];
        Assert.Equal(5, Next(step2, SearchPattern.Mismatch("DE AD", aligned: false), 1)!.Value.Offset);

        // 手順 3: 揃える (既定) → 1 (起点は 0 で、オフセット 1 は `AD` であるべき位置)。
        Assert.Equal(1, Next(step2, SearchPattern.Mismatch("DE AD", aligned: true), 1)!.Value.Offset);

        // 手順 4: `DE 00 DE 11 DE 22` で P = `DE ??` → 一致しない箇所はない。
        Assert.Null(Next([0xDE, 0x00, 0xDE, 0x11, 0xDE, 0x22], SearchPattern.Mismatch("DE ??", aligned: true), 0));

        // 手順 5: P が空ならエラー。
        Assert.Equal(PatternError.Empty, Assert.Throws<PatternException>(() => SearchPattern.Mismatch("  ", aligned: true)).Error);

        // 後方検索: 開始位置から先頭に向かって最初に違うバイト (仕様 3)。チャンクの境界をまたいでも見つかる。
        byte[] gaps = Gaps();
        SearchPattern zero = SearchPattern.Mismatch("00", aligned: true);
        Assert.Equal(0x100000, Next(gaps, zero, 0)!.Value.Offset);
        Assert.Equal(0x100001, Next(gaps, zero, 0x100001)!.Value.Offset);
        Assert.Equal(0x10011F, Next(gaps, zero, 0x100200, forward: false)!.Value.Offset);
        Assert.Equal(1, Next(gaps, zero, 0)!.Value.Length);
    }

    [Fact]
    [Trait(TC, "TC-FIND-25-03")]
    public void FindAllListsRangesThatAreNotThePattern()
    {
        // TC-FIND-25-03 の Core の部分: 00 で埋まっていない範囲は 0x100000 から 4 バイトと、0x100100 から 32 バイト。
        using Document doc = Doc(Gaps());
        using SearchResults results = MismatchSearch.CreateResults(doc.Current, SearchPattern.Mismatch("00", aligned: true), new SearchOptions());
        SearchEngine.FindAll(results);
        Assert.Equal([new SearchMatch(0x100000, 4), new SearchMatch(0x100100, 32)], results.Matches);
        Assert.Equal(SearchResultsState.Completed, results.State);

        // 違うバイトの間に P が N 回 (16) 未満しか続かなければ、1 つの範囲にまとめる。
        byte[] data = new byte[200];
        data[10] = 1;
        data[20] = 2; // 間の 00 は 9 個
        data[60] = 3; // 間の 00 は 39 個 (16 以上)
        using Document small = Doc(data);
        using SearchResults r2 = MismatchSearch.CreateResults(small.Current, SearchPattern.Mismatch("00", aligned: true), new SearchOptions { ChunkSize = 4096 });
        SearchEngine.FindAll(r2);
        Assert.Equal([new SearchMatch(10, 11), new SearchMatch(60, 1)], r2.Matches);

        // 位置の条件 (FIND-17 の仕様 3): 範囲は条件を満たす位置の違うバイトから始める (次を検索と同じ)。mod 512 = 0 では 0 と 1024 から
        // (0x100 の 1 は条件を満たさないので範囲を始めない。1024 の範囲は途中の 1030 の違うバイトまで)。
        byte[] sectors = new byte[2048];
        sectors[0] = 1;
        sectors[0x100] = 1;
        sectors[1024] = 1;
        sectors[1030] = 1;
        using Document sectorDoc = Doc(sectors);
        SearchPattern bySector = SearchPattern.Mismatch("00", aligned: true).WithPosition(PositionCondition.Create(512, 0));
        using SearchResults r3 = MismatchSearch.CreateResults(sectorDoc.Current, bySector, new SearchOptions());
        SearchEngine.FindAll(r3);
        Assert.Equal([new SearchMatch(0, 1), new SearchMatch(1024, 7)], r3.Matches);
        Assert.Equal(1024, SearchEngine.Find(sectorDoc.Current, bySector, 1, forward: true, wrap: false)!.Value.Offset);

        // 件数の上限で止めて、続けると重複も取りこぼしもない (FIND-20 の仕様 6)。
        byte[] many = new byte[100_000];
        for (int i = 0; i < many.Length; i += 50)
        {
            many[i] = 0xFF;
        }

        using Document manyDoc = Doc(many);
        using SearchResults limited = MismatchSearch.CreateResults(manyDoc.Current, SearchPattern.Mismatch("00", aligned: true), new SearchOptions { MaxMatches = 1000 });
        SearchEngine.FindAll(limited);
        Assert.Equal(SearchResultsState.LimitReached, limited.State);
        Assert.Equal(1000, limited.Count);
        SearchEngine.ContinueFindAll(limited);
        Assert.Equal(2000, limited.Count);
        Assert.Equal(Enumerable.Range(0, 2000).Select(i => (long)i * 50), limited.Matches.Select(m => m.Offset));
    }

    /// <summary>TD-FIND-STRINGS (64 バイト)。</summary>
    private static byte[] Strings()
    {
        byte[] data = new byte[64];
        "Hello\0"u8.ToArray().CopyTo(data, 0x00);
        "Hi\0"u8.ToArray().CopyTo(data, 0x10);
        new byte[] { 0x57, 0x6F, 0x72, 0x6C, 0x64, 0xFF }.CopyTo(data, 0x20);
        new byte[] { 0xD5, 0x30, 0xA1, 0x30, 0xA4, 0x30, 0xEB, 0x30, 0x00, 0x00 }.CopyTo(data, 0x30);
        return data;
    }

    private static (long Offset, long Length, long Chars, string Encoding, string Text)[] Extract(byte[] data, StringExtractionOptions options)
    {
        using Document doc = Doc(data);
        using SearchResults results = StringExtractor.CreateResults(doc.Current, options, new SearchOptions());
        SearchEngine.FindAll(results);
        Assert.Equal(SearchResultsState.Completed, results.State);
        return [.. results.Matches.Select(m =>
        {
            byte[] bytes = data.AsSpan((int)m.Offset, (int)m.Length).ToArray();
            return (m.Offset, m.Length, (long)m.Extra, results.VariantNames![m.Variant], StringExtractor.Text(bytes, results.VariantEncodings![m.Variant], 4096));
        })];
    }

    [Fact]
    [Trait(TC, "TC-FIND-32-01")]
    public void MinimumLengthEncodingsAndNulTermination()
    {
        byte[] data = Strings();
        (string, Encoding)[] defaults = [("ASCII", TextEncodings.Get(TextEncodingId.Ascii)), ("UTF-16LE", TextEncodings.Get(TextEncodingId.Utf16LE))];

        // 手順 1: Hello (ASCII、5)、World (ASCII、5)、ファイル (UTF-16LE、長さ 8、4 文字)。Hi は報告されない。
        Assert.Equal(
            [(0x00L, 5L, 5L, "ASCII", "Hello"), (0x20L, 5L, 5L, "ASCII", "World"), (0x30L, 8L, 4L, "UTF-16LE", "ファイル")],
            Extract(data, new StringExtractionOptions { Encodings = defaults }));

        // 手順 2: NUL で終わるものだけ → Hello とファイル (World の直後は FF)。
        Assert.Equal([0x00L, 0x30L], Extract(data, new StringExtractionOptions { Encodings = defaults, NulTerminatedOnly = true }).Select(r => r.Offset));

        // 手順 3: ASCII だけ、最小 2 → Hello、Hi (長さ 2)、World。
        Assert.Equal(
            [(0x00L, 5L, 5L, "ASCII", "Hello"), (0x10L, 2L, 2L, "ASCII", "Hi"), (0x20L, 5L, 5L, "ASCII", "World")],
            Extract(data, new StringExtractionOptions { Encodings = [defaults[0]], MinLength = 2 }));

        // 同じ範囲が複数の文字コードで文字列になる場合は長い方、同じ長さなら先に選んだ文字コード (仕様 2)。
        (string, Encoding)[] utf8First = [("UTF-8", TextEncodings.Get(TextEncodingId.Utf8)), ("ASCII", TextEncodings.Get(TextEncodingId.Ascii))];
        Assert.Equal(["UTF-8", "UTF-8"], Extract(data, new StringExtractionOptions { Encodings = utf8First }).Select(r => r.Encoding));
        byte[] mixed = Encoding.UTF8.GetBytes("abécd!!");
        var both = Extract(mixed, new StringExtractionOptions { Encodings = [defaults[0], utf8First[0]] });
        Assert.Equal(("UTF-8", 0L, (long)mixed.Length), (both.Single().Encoding, both.Single().Offset, both.Single().Length));

        // 一覧の絞り込み: 含まれる語 (大文字・小文字を区別しない) と、`/…/` の正規表現 (仕様 4)。
        using (Document stringsDoc = Doc(data))
        {
            using SearchResults found = StringExtractor.CreateResults(stringsDoc.Current, new StringExtractionOptions { Encodings = defaults }, new SearchOptions());
            SearchEngine.FindAll(found);
            var factory = new SearchResultRowFactory(found, stringsDoc.Current, Encoding.ASCII);
            Assert.Equal([1L], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "WORLD")!);
            Assert.Equal([0L, 1L], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "/^(hel|wor)/")!);
            Assert.Equal([2L], SearchResultsOrdering.Build([factory], SearchResultSortKey.Number, false, "/ファ/")!);
        }

        // 最大の長さを超える文字列も分割しない (長さは全体)。チャンクの境界をまたいでも 1 つ。
        byte[] longText = new byte[20_000];
        Array.Fill(longText, (byte)'A', 100, 10_000);
        using Document doc = Doc(longText);
        using SearchResults r = StringExtractor.CreateResults(doc.Current, new StringExtractionOptions { Encodings = [defaults[0]] }, new SearchOptions { ChunkSize = 4096 });
        SearchEngine.FindAll(r);
        Assert.Equal([new SearchMatch(100, 10_000, 0, 10_000)], r.Matches);
    }
}
