using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>複数の文字コードでの同時検索 (FIND-08) と、複数の語の同時検索 (FIND-26)。</summary>
public sealed class MultiPatternTests
{
    private static (string, Encoding) Enc(TextEncodingId id) => (id.ToString(), TextEncodings.Get(id));

    /// <summary>TD-RANDOM-16M を、読み込みを記録するデータソースで包む。</summary>
    private static FakeByteSource RecordingRandom16M()
    {
        byte[] data = RandomData16M;
        return new FakeByteSource(data.Length, (o, s) => data.AsSpan((int)o, s.Length).CopyTo(s), SourceCapabilities.CanResize);
    }

    /// <summary>読み込みの記録が「ファイルサイズ + 重なり幅 × チャンクの境界の数」以下で、同じ範囲を 2 回読んでいないことを確かめる。</summary>
    private static void AssertReadOnce(FakeByteSource source, int overlap, int chunkSize)
    {
        long total = source.Reads.Sum(r => (long)r.Length);
        long boundaries = (source.Length + chunkSize - 1) / chunkSize - 1;
        Assert.True(total <= source.Length + ((long)overlap * boundaries), $"読み込み {total:N0} バイト");

        // 同じ範囲を 2 回以上読んだ記録がない (重なり部分を除く): 各バイトを読んだ回数は、重なりの外では 1 回まで。
        var reads = source.Reads.OrderBy(r => r.Offset).ToList();
        for (int i = 1; i < reads.Count; i++)
        {
            long previousEnd = reads[i - 1].Offset + reads[i - 1].Length;
            Assert.True(reads[i].Offset >= previousEnd - overlap, $"0x{reads[i].Offset:X} を読み直した");
        }
    }

    [Fact]
    [Trait(TC, "TC-FIND-08-02")]
    public void EightEncodingsReadTheFileOnce()
    {
        FakeByteSource source = RecordingRandom16M();
        using var doc = new Document(source, Options());
        TextEncodingId[] ids =
        [
            TextEncodingId.Utf8, TextEncodingId.Utf16LE, TextEncodingId.Utf16BE, TextEncodingId.Utf32LE, TextEncodingId.Utf32BE,
            TextEncodingId.ShiftJis, TextEncodingId.EucJp, TextEncodingId.Gb18030,
        ];
        SearchPattern pattern = SearchPattern.FromTextEncodings("日本テスト", [.. ids.Select(Enc)], new TextSearchOptions(), out IReadOnlyList<string> excluded);
        Assert.Empty(excluded);
        Assert.Equal(8, pattern.Variants.Count);
        Assert.Equal(VariantColumn.Encoding, pattern.VariantColumn);
        using SearchResults results = SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { MaxDegreeOfParallelism = 4 });
        Assert.Equal(SearchResultsState.Completed, results.State);
        AssertReadOnce(source, pattern.MaxMatchLength - 1, SearchEngine.DefaultChunkSize);
    }

    [Fact]
    [Trait(TC, "TC-FIND-08-01")]
    public void EncodingsFindEachEncodingAndMergeIdenticalOnes()
    {
        // TC-FIND-08-01 の Core の部分: 3 種類の符号化の位置が 1 回の検索で見つかり、同じ符号化 (ASCII / UTF-8) は 1 つにまとまる。
        // 00 の中の UTF-16LE の並びは 1 バイト前から UTF-16BE としても読めるため、文字の境界に揃える (FIND-07 の仕様 5)。
        byte[] data = new byte[0x400];
        "Test"u8.ToArray().CopyTo(data, 0x100);
        Encoding.Unicode.GetBytes("Test").CopyTo(data, 0x200);
        Encoding.BigEndianUnicode.GetBytes("Test").CopyTo(data, 0x300);
        using Document doc = Doc(data);
        var aligned = new TextSearchOptions { AlignToCharacters = true };
        SearchPattern three = SearchPattern.FromTextEncodings("Test", [Enc(TextEncodingId.Ascii), Enc(TextEncodingId.Utf16LE), Enc(TextEncodingId.Utf16BE)],
            aligned, out _);
        SearchMatch[] matches = [.. SearchEngine.FindAll(doc.Current, three).Matches];
        Assert.Equal([0x100L, 0x200, 0x300], matches.Select(m => m.Offset));
        Assert.Equal(["Ascii", "Utf16LE", "Utf16BE"], matches.Select(m => three.Variants[m.Variant]));

        SearchPattern four = SearchPattern.FromTextEncodings("Test",
            [Enc(TextEncodingId.Ascii), Enc(TextEncodingId.Utf16LE), Enc(TextEncodingId.Utf16BE), Enc(TextEncodingId.Utf8)], aligned, out _);
        matches = [.. SearchEngine.FindAll(doc.Current, four).Matches];
        Assert.Equal(3, matches.Length);
        Assert.Equal("Ascii / Utf8", four.Variants[matches[0].Variant]);

        // 次を検索は、どの文字コードでも最も近い一致に移り、一致した文字コードを返す (仕様 4)。
        SearchHit hit = SearchEngine.Find(doc.Current, three, 0x101, forward: true, wrap: false)!.Value;
        Assert.Equal(0x200, hit.Offset);
        Assert.Equal("Utf16LE", three.Variants[hit.Variant]);

        // 符号化できない文字コードは除き、警告に名前を出す (仕様 6)。すべてで符号化できなければエラー。
        SearchPattern some = SearchPattern.FromTextEncodings("日本", [Enc(TextEncodingId.Ascii), Enc(TextEncodingId.Utf8)], new TextSearchOptions(), out IReadOnlyList<string> excluded);
        Assert.Equal(["Ascii"], excluded);
        Assert.Single(some.Variants);
        Assert.Throws<PatternException>(() => SearchPattern.FromTextEncodings("日本", [Enc(TextEncodingId.Ascii)], new TextSearchOptions(), out _));

        // 置換は一致した文字コードで符号化する。
        var template = ReplacementTemplate.PerVariant([Encoding.ASCII.GetBytes("Best"), Encoding.Unicode.GetBytes("Best"), Encoding.BigEndianUnicode.GetBytes("Best")]);
        Assert.True(Replacer.ReplaceAt(doc, three, 0x200, template, new ReplaceOptions()).Applied);
        byte[] after = new byte[8];
        doc.Current.Read(0x200, after);
        Assert.Equal(Encoding.Unicode.GetBytes("Best"), after);
    }

    /// <summary>TD-FIND-WORDS-1000: `word0000` から `word0999` までの 1,000 行 (LF)。</summary>
    private static string Words1000() => string.Concat(Enumerable.Range(0, 1000).Select(i => $"word{i:D4}\n"));

    [Fact]
    [Trait(TC, "TC-FIND-26-02")]
    public void ThousandTermsReadTheFileOnce()
    {
        string path = Path.Combine(Path.GetTempPath(), "HexEditor", "tests", $"words-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Words1000(), new UTF8Encoding(false));
        try
        {
            // 手順 1〜2: テキストとして読み込むと 1,000 語。
            IReadOnlyList<SearchTerm> terms = SearchTerms.FromFile(path, new SearchTerm { Kind = SearchKind.Text, Encoding = "ascii" });
            Assert.Equal(1000, terms.Count);
            Assert.Equal("word0999", terms[^1].Text);

            // 手順 3〜4: すべて検索。読み込みは 1 回だけ。
            SearchPattern pattern = SearchTerms.Build(terms, [.. terms.Select(t => t.Text)], new TextSearchOptions(), TextEncodings.FromCatalogId, out var errors)!;
            Assert.Empty(errors);
            FakeByteSource source = RecordingRandom16M();
            using var doc = new Document(source, Options());
            using SearchResults results = SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { MaxDegreeOfParallelism = 4 });
            Assert.Equal(SearchResultsState.Completed, results.State);
            AssertReadOnce(source, pattern.MaxMatchLength - 1, SearchEngine.DefaultChunkSize);

            // 語の一覧の保存と読み込み (仕様 8)。
            IReadOnlyList<SearchTerm> loaded = SearchTerms.FromJson(SearchTerms.ToJson(terms));
            Assert.Equal(terms, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait(TC, "TC-FIND-26-03")]
    public void SamePositionMatchesAreSeparateResults()
    {
        byte[] data = [0x00, 0x4D, 0x5A, 0x90, 0x00];
        SearchTerm[] terms =
        [
            new() { Kind = SearchKind.Hex, Text = "4D 5A" },
            new() { Kind = SearchKind.Text, Text = "MZ", Encoding = "ascii" },
            new() { Kind = SearchKind.Hex, Text = "4D" },
        ];
        SearchPattern pattern = SearchTerms.Build(terms, ["4D 5A", "MZ", "4D"], new TextSearchOptions(), TextEncodings.FromCatalogId, out _)!;
        using Document doc = Doc(data);
        SearchMatch[] matches = [.. SearchEngine.FindAll(doc.Current, pattern).Matches];
        Assert.Equal(3, matches.Length);
        Assert.All(matches, m => Assert.Equal(1, m.Offset));
        Assert.Equal([("4D 5A", 2L), ("MZ", 2L), ("4D", 1L)], matches.Select(m => (pattern.Variants[m.Variant], m.Length)));

        // 種類の違う語 (Hex、テキスト、整数、浮動小数点)、Aho-Corasick 法と候補の絞り込みの組み合わせを基準の実装と比べる。
        byte[] random = RandomData16M.AsSpan(0, 1 << 20).ToArray();
        SearchTerm[] mixed =
        [
            new() { Kind = SearchKind.Hex, Text = "00 01" },
            new() { Kind = SearchKind.Hex, Text = "A? 5?" },
            new() { Kind = SearchKind.Text, Text = "ab", Encoding = "ascii" },
            new() { Kind = SearchKind.Integer, Text = "0x1234", IntegerBits = 16, Endian = SearchEndian.Both },
            new() { Kind = SearchKind.Hex, Text = "00 01 02" },
        ];
        SearchPattern union = SearchTerms.Build(mixed, ["a", "b", "c", "d", "e"], new TextSearchOptions { CaseSensitive = false }, TextEncodings.FromCatalogId, out _)!;
        using Document big = Doc(random);
        var expected = new List<SearchMatch>();
        for (int v = 0; v < union.Parts.Count; v++)
        {
            expected.AddRange(Naive(random, union.Parts[v]).Select(m => m with { Variant = v }));
        }

        expected.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Variant.CompareTo(b.Variant));
        Assert.Equal(expected, SearchEngine.FindAll(big.Current, union, new SearchOptions { IncludeOverlapping = true, ChunkSize = 256 * 1024 }).Matches);

        // 不正な語がある間は検索できない (無効にした行は除く)。
        SearchTerm[] bad = [new() { Kind = SearchKind.Hex, Text = "4D 5" }, new() { Kind = SearchKind.Hex, Text = "ZZ", Enabled = false }];
        Assert.Null(SearchTerms.Build(bad, ["x", "y"], new TextSearchOptions(), TextEncodings.FromCatalogId, out var errors));
        Assert.Equal(0, Assert.Single(errors).Row);
    }

    [Fact]
    [Trait(TC, "TC-FIND-26-03")]
    public void AhoCorasickFindsEveryOccurrence()
    {
        // 語の接尾辞が別の語になる場合 (失敗の遷移の出力) も、すべての出現を見つける。
        byte[][] words = [[1, 2, 3], [2, 3], [3], [2, 3, 4], [9, 9]];
        var ac = new AhoCorasick(words);
        byte[] data = [0, 1, 2, 3, 4, 9, 9, 9];
        var hits = new List<(int End, int Id)>();
        ac.Search(data, 0, hits);
        Assert.Equal([(4, 0), (4, 1), (4, 2), (5, 3), (7, 4), (8, 4)], hits.OrderBy(h => h.End).ThenBy(h => h.Id));
    }
}
