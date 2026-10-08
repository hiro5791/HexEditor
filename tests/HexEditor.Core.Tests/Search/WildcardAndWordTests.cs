using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>Hex のワイルドカード (FIND-06) と単語単位の検索 (FIND-10 の仕様 4)。</summary>
public sealed class WildcardAndWordTests
{
    private static readonly Encoding Ascii = TextEncodings.Get(TextEncodingId.Ascii);
    private static readonly Encoding Utf8 = TextEncodings.Get(TextEncodingId.Utf8);

    private static SearchMatch[] FindAll(byte[] data, SearchPattern pattern, SearchOptions? options = null)
    {
        using Document doc = Doc(data);
        return [.. SearchEngine.FindAll(doc.Current, pattern, options ?? new SearchOptions { IncludeOverlapping = true }).Matches];
    }

    /// <summary>TD-FIND-WILDGAP: 0x0000 から `4D 5A 50 45`、0x4000 から `4D 5A` + 00 × 4,096 + `50 45`、0x8000 から 00 × 4,097 の間。</summary>
    private static byte[] WildGap()
    {
        byte[] data = new byte[65536];
        void Put(int at, int gap)
        {
            data[at] = 0x4D;
            data[at + 1] = 0x5A;
            data[at + 2 + gap] = 0x50;
            data[at + 3 + gap] = 0x45;
        }

        Put(0x0000, 0);
        Put(0x4000, 4096);
        Put(0x8000, 4097);
        return data;
    }

    [Fact]
    [Trait(TC, "TC-FIND-06-01")]
    public void ByteAndNibbleWildcards()
    {
        // 手順 1: `4D 5A ?? ?? 50 45`。オフセット 7 の並びは `50 46` で一致しない。
        byte[] step1 = [0x00, 0x4D, 0x5A, 0x90, 0x00, 0x50, 0x45, 0x4D, 0x5A, 0x90, 0x00, 0x50, 0x46];
        Assert.Equal([new SearchMatch(1, 6)], FindAll(step1, SearchPattern.FromHex("4D 5A ?? ?? 50 45")));

        // 手順 2: TD-BYTES-256 で `A?` (0xA0〜0xAF) と `?5` (0x05, 0x15, …, 0xF5)。
        byte[] bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        Assert.Equal(Enumerable.Range(0xA0, 16).Select(i => (long)i), FindAll(bytes256, SearchPattern.FromHex("A?")).Select(m => m.Offset));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (long)(i * 16 + 5)), FindAll(bytes256, SearchPattern.FromHex("?5")).Select(m => m.Offset));

        // 手順 3: `???` と `A? B` は桁数のエラー (位置付き)。`?? ??` と `* ??` はワイルドカードだけのエラー。
        PatternException odd = Assert.Throws<PatternException>(() => SearchPattern.FromHex("???"));
        Assert.Equal(PatternError.OddDigits, odd.Error);
        Assert.Equal(3, odd.Position);
        PatternException odd2 = Assert.Throws<PatternException>(() => SearchPattern.FromHex("A? B"));
        Assert.Equal(PatternError.OddDigits, odd2.Error);
        Assert.Equal(4, odd2.Position);
        Assert.Equal(PatternError.WildcardsOnly, Assert.Throws<PatternException>(() => SearchPattern.FromHex("?? ??")).Error);
        Assert.Equal(PatternError.WildcardsOnly, Assert.Throws<PatternException>(() => SearchPattern.FromHex("* ??")).Error);

        // 手順 4: `?? 4D 5A ??`: 警告が返り、前後の `??` を一致の範囲に含める。
        SearchPattern edge = SearchPattern.FromHex("?? 4D 5A ??");
        Assert.Equal(PatternWarnings.EdgeWildcardsIgnored, edge.Warnings);
        Assert.Equal([new SearchMatch(0, 4), new SearchMatch(6, 4)], FindAll(step1, edge));

        // 警告のない検索語。
        Assert.Equal(PatternWarnings.None, SearchPattern.FromHex("4D ?? 5A").Warnings);
    }

    [Fact]
    [Trait(TC, "TC-FIND-06-02")]
    public void VariableWildcardHonoursTheMaximumMatchLength()
    {
        byte[] data = WildGap();

        // 手順 1: 0x0000 (長さ 4) と 0x4000 (長さ 4,100)。0x8000 (間 4,097) は一致しない。
        Assert.Equal([new SearchMatch(0, 4), new SearchMatch(0x4000, 4100)], FindAll(data, SearchPattern.FromHex("4D 5A * 50 45")));

        // 手順 2: `*{0,16}` は 0x0000 だけ。
        Assert.Equal([new SearchMatch(0, 4)], FindAll(data, SearchPattern.FromHex("4D 5A *{0,16} 50 45")));

        // 手順 3: 一致の最大長 8,192 なら 0x8000 (長さ 4,101) も。
        SearchPattern wide = SearchPattern.FromHex("4D 5A * 50 45", new HexSearchOptions { MaxWildcardLength = 8192 });
        Assert.Equal([new SearchMatch(0, 4), new SearchMatch(0x4000, 4100), new SearchMatch(0x8000, 4101)], FindAll(data, wide));

        // 手順 4: チャンクサイズ 256 KiB でも同じ。さらに小さいチャンク (境界をまたぐ) でも同じ。
        foreach (int chunk in new[] { 256 * 1024, 0x1000, 0x2003 })
        {
            Assert.Equal([new SearchMatch(0, 4), new SearchMatch(0x4000, 4100)],
                FindAll(data, SearchPattern.FromHex("4D 5A * 50 45"), new SearchOptions { ChunkSize = chunk, IncludeOverlapping = true }));
        }
    }

    [Fact]
    public void VariableWildcardIsShortestAndWorksBackwards()
    {
        // 最短一致: 4D 5A 50 45 50 45 では最初の 50 45 で終わる。
        byte[] data = [0x00, 0x4D, 0x5A, 0x50, 0x45, 0x50, 0x45, 0x00];
        SearchPattern p = SearchPattern.FromHex("4D 5A * 50 45");
        Assert.Equal([new SearchMatch(1, 4)], FindAll(data, p));
        using Document doc = Doc(data);
        Assert.Equal(new SearchHit(1, 4, false), SearchEngine.Find(doc.Current, p, data.Length, forward: false, wrap: false));
        Assert.Equal("4D 5A * 50 45", p.Preview());
        Assert.Equal("4D 5A *{0,16} 50 45", SearchPattern.FromHex("4D 5A *{0,16} 50 45").Preview());

        // 範囲の誤り。
        Assert.Equal(PatternError.InvalidWildcardRange, Assert.Throws<PatternException>(() => SearchPattern.FromHex("4D *{5,2} 45")).Error);
        Assert.Equal(PatternError.InvalidWildcardRange, Assert.Throws<PatternException>(() => SearchPattern.FromHex("4D *{0,5000} 45")).Error);

        // 先頭と末尾の `*` は取り除いて警告。
        SearchPattern edge = SearchPattern.FromHex("* 4D 5A *");
        Assert.Equal(PatternWarnings.EdgeWildcardsIgnored, edge.Warnings);
        Assert.False(edge.HasVariableGap);
        Assert.Equal([new SearchMatch(1, 2)], FindAll(data, edge));
    }

    [Fact]
    [Trait(TC, "TC-FIND-10-02")]
    public void WholeWordSearch()
    {
        var word = new TextSearchOptions { WholeWord = true };

        // 手順 1: 2 (`a cat.`) と 25 (`(cat)`)。
        Assert.Equal([2L, 25L], FindAll(Encoding.ASCII.GetBytes("a cat. concat cats cat_ (cat)"), SearchPattern.FromText("cat", Ascii, word)).Select(m => m.Offset));

        // 手順 2: ドキュメント全体が `cat`: 範囲の端は境界。
        Assert.Equal([0L], FindAll(Encoding.ASCII.GetBytes("cat"), SearchPattern.FromText("cat", Ascii, word)).Select(m => m.Offset));

        // 手順 3: CJK の文字は単語を作る文字。
        Assert.Empty(FindAll(Encoding.UTF8.GetBytes("日本cat日本 catの"), SearchPattern.FromText("cat", Utf8, word)));

        // 手順 4: 復号できないバイトは境界。
        Assert.Equal([1L], FindAll([0xFF, 0x63, 0x61, 0x74, 0xFF], SearchPattern.FromText("cat", Ascii, word)).Select(m => m.Offset));

        // 次を検索・前を検索・大文字小文字を区別しない場合も同じ境界。
        byte[] text = Encoding.ASCII.GetBytes("concat Cat cats");
        using Document doc = Doc(text);
        SearchPattern p = SearchPattern.FromText("cat", Ascii, word with { CaseSensitive = false });
        Assert.Equal(7, SearchEngine.Find(doc.Current, p, 0, forward: true, wrap: false)!.Value.Offset);
        Assert.Equal(7, SearchEngine.Find(doc.Current, p, text.Length, forward: false, wrap: false)!.Value.Offset);

        // チャンクの境界の前後 (前のバイトがチャンクの外) でも境界を正しく判定する。
        byte[] big = new byte[1 << 20];
        Array.Fill(big, (byte)'x');
        Encoding.ASCII.GetBytes(" cat ").CopyTo(big, 0x1FFFE);
        Encoding.ASCII.GetBytes("cat").CopyTo(big, 0x40000);
        Assert.Equal([0x1FFFFL],
            FindAll(big, SearchPattern.FromText("cat", Ascii, word), new SearchOptions { ChunkSize = 0x20000 }).Select(m => m.Offset));
    }
}
