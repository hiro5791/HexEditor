using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>検索語の解釈 (FIND-05、FIND-07、FIND-10)。</summary>
public sealed class SearchPatternTests
{
    private static readonly Encoding Ascii = TextEncodings.Get(TextEncodingId.Ascii);
    private static readonly Encoding Utf8 = TextEncodings.Get(TextEncodingId.Utf8);
    private static readonly Encoding Utf16LE = TextEncodings.Get(TextEncodingId.Utf16LE);
    private static readonly Encoding Utf16BE = TextEncodings.Get(TextEncodingId.Utf16BE);
    private static readonly Encoding ShiftJis = TextEncodings.Get(TextEncodingId.ShiftJis);

    private static readonly TextSearchOptions IgnoreCase = new() { CaseSensitive = false };

    private static long[] FindAll(byte[] data, SearchPattern pattern)
    {
        using Document doc = Doc(data);
        return Offsets(SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { IncludeOverlapping = true }));
    }

    [Fact]
    [Trait(TC, "TC-FIND-05-01")]
    public void HexNotationsGiveTheSameResult()
    {
        string[] notations = ["DEADBEEF", "DE AD BE EF", "0xDE,0xAD,0xBE,0xEF", "\\xDE\\xAD\\xBE\\xEF", "de ad be ef", "DE\r\nAD BE\tEF"];
        foreach (string text in notations)
        {
            SearchPattern pattern = SearchPattern.FromHex(text);
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, pattern.Bytes);
            Assert.Equal([0L], FindAll(EditSample(), pattern));
        }

        Assert.Equal(PatternError.OddDigits, Assert.Throws<PatternException>(() => SearchPattern.FromHex("ABC")).Error);
        Assert.Equal(PatternError.OddDigits, Assert.Throws<PatternException>(() => SearchPattern.FromHex("DE AD B")).Error);
        PatternException invalid = Assert.Throws<PatternException>(() => SearchPattern.FromHex("DEADBEEG"));
        Assert.Equal(PatternError.InvalidCharacter, invalid.Error);
        Assert.Equal("G", invalid.Detail);
        Assert.Equal(8, invalid.Position);
    }

    [Theory]
    [InlineData("DE AD B", PatternError.OddDigits, 7)]
    [InlineData("0x12, 0xZ1", PatternError.InvalidCharacter, 9)]
    [InlineData("12 あ", PatternError.InvalidCharacter, 4)]
    public void HexErrorsCarryThePosition(string text, PatternError error, int position)
    {
        PatternException ex = Assert.Throws<PatternException>(() => SearchPattern.FromHex(text));
        Assert.Equal(error, ex.Error);
        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void HexOfExactlyOneMebibyteIsAcceptedAndOneMoreByteIsNot()
    {
        string ok = new('A', 2 * SearchPattern.MaxLength);
        Assert.Equal(SearchPattern.MaxLength, SearchPattern.FromHex(ok).Length);
        Assert.Equal(PatternError.TooLong, Assert.Throws<PatternException>(() => SearchPattern.FromHex(ok + "AB")).Error);
    }

    [Fact]
    [Trait(TC, "TC-FIND-07-01")]
    public void TextIsSearchedInTheChosenEncoding()
    {
        // 手順 1: UTF-16LE / BE。オフセット 5 の `41 00 42` は末尾で切れているため一致しない。
        byte[] step1 = [0x41, 0x00, 0x42, 0x00, 0x00, 0x41, 0x00, 0x42];
        Assert.Equal([0L], FindAll(step1, SearchPattern.FromText("AB", Utf16LE)));
        Assert.Equal([4L], FindAll(step1, SearchPattern.FromText("AB", Utf16BE)));

        // 手順 2: Shift_JIS。
        Assert.Equal([1L], FindAll([0x00, 0x93, 0xFA, 0x96, 0x7B, 0x00], SearchPattern.FromText("日本", ShiftJis)));

        // 手順 3: 文字の境界に揃える。
        byte[] step3 = [0x00, 0x41, 0x00, 0x42, 0x00, 0x41, 0x00, 0x42, 0x00];
        Assert.Equal([1L, 5L], FindAll(step3, SearchPattern.FromText("AB", Utf16LE)));
        Assert.Empty(FindAll(step3, SearchPattern.FromText("AB", Utf16LE, new TextSearchOptions { AlignToCharacters = true })));

        // 手順 4: エスケープ文字。オフでは文字どおりの 8 バイトになり一致しない。
        byte[] step4 = [0x41, 0x00, 0x42, 0x0A];
        var escapes = new TextSearchOptions { UseEscapes = true };
        Assert.Equal([0L], FindAll(step4, SearchPattern.FromText("A\\x00B\\n", Ascii, escapes)));
        SearchPattern literal = SearchPattern.FromText("A\\x00B\\n", Ascii);
        Assert.Equal(8, literal.Length);
        Assert.Empty(FindAll(step4, literal));

        // 手順 5: 表せない文字は、文字と位置を示してエラーにする (`?` に置き換えない)。
        PatternException sharpS = Assert.Throws<PatternException>(() => SearchPattern.FromText("ß", ShiftJis));
        Assert.Equal((PatternError.NotEncodable, "ß", (int?)1), (sharpS.Error, sharpS.Detail, sharpS.Position));
        PatternException kanji = Assert.Throws<PatternException>(() => SearchPattern.FromText("日", Ascii));
        Assert.Equal((PatternError.NotEncodable, "日", (int?)1), (kanji.Error, kanji.Detail, kanji.Position));

        // 手順 6: BOM は付かない。
        Assert.Equal(new byte[] { 0x41, 0xE3, 0x81, 0x82 }, SearchPattern.FromText("Aあ", Utf8, escapes).Bytes);
    }

    [Fact]
    public void NotEncodableCharacterReportsItsPosition()
    {
        PatternException ex = Assert.Throws<PatternException>(() => SearchPattern.FromText("abß", ShiftJis));
        Assert.Equal(3, ex.Position);

        // サロゲートペアは 1 文字と数える。
        PatternException emoji = Assert.Throws<PatternException>(() => SearchPattern.FromText("😀aé", Ascii));
        Assert.Equal(("😀", (int?)1), (emoji.Detail, emoji.Position));
        PatternException accent = Assert.Throws<PatternException>(() => SearchPattern.FromText("😀aé", Encoding.GetEncoding(20127)));
        Assert.Equal(1, accent.Position);
        PatternException third = Assert.Throws<PatternException>(() => SearchPattern.FromText("aaé", Ascii));
        Assert.Equal(3, third.Position);
    }

    [Theory]
    [InlineData("a\\n\\r\\t\\0\\\\", new byte[] { 0x61, 0x0A, 0x0D, 0x09, 0x00, 0x5C })]
    [InlineData("\\x41\\xff", new byte[] { 0x41, 0xFF })]
    [InlineData("\\u3042", new byte[] { 0xE3, 0x81, 0x82 })]
    [InlineData("\\uD83D\\uDE00", new byte[] { 0xF0, 0x9F, 0x98, 0x80 })]
    public void EscapesAreDecoded(string text, byte[] expected) =>
        Assert.Equal(expected, SearchPattern.FromText(text, Utf8, new TextSearchOptions { UseEscapes = true }).Bytes);

    [Theory]
    [InlineData("ab\\q", 3)]
    [InlineData("\\x4", 1)]
    [InlineData("x\\u12G4", 2)]
    [InlineData("abc\\", 4)]
    public void InvalidEscapesReportThePosition(string text, int position)
    {
        PatternException ex = Assert.Throws<PatternException>(() => SearchPattern.FromText(text, Utf8, new TextSearchOptions { UseEscapes = true }));
        Assert.Equal(PatternError.InvalidEscape, ex.Error);
        Assert.Equal(position, ex.Position);
    }

    [Fact]
    public void ErrorPositionAfterEscapesCountsInputCharacters()
    {
        // `\x41` は 4 文字と数える。`日` は 5 文字目。
        PatternException ex = Assert.Throws<PatternException>(() =>
            SearchPattern.FromText("\\x41日", Ascii, new TextSearchOptions { UseEscapes = true }));
        Assert.Equal(5, ex.Position);
    }

    [Fact]
    [Trait(TC, "TC-FIND-10-01")]
    public void CaseInsensitiveSearchUsesInvariantSimpleCaseMapping()
    {
        // 手順 1。
        byte[] files = Encoding.ASCII.GetBytes("FILE File fIlE fi1e");
        Assert.Equal([0L, 5L, 10L], FindAll(files, SearchPattern.FromText("file", Ascii, IgnoreCase)));

        // 手順 2: 地域設定がトルコ語でも同じ。`İ` と `ı` は自分自身とだけ一致する。
        CultureInfo saved = CultureInfo.CurrentCulture;
        CultureInfo savedUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            Assert.Equal([0L, 5L, 10L], FindAll(files, SearchPattern.FromText("file", Ascii, IgnoreCase)));
            byte[] turkish = Encoding.UTF8.GetBytes("i I İ ı");
            Assert.Equal([0L, 2L], FindAll(turkish, SearchPattern.FromText("i", Utf8, IgnoreCase)));
            Assert.Equal([4L], FindAll(turkish, SearchPattern.FromText("İ", Utf8, IgnoreCase)));
            Assert.Equal([7L], FindAll(turkish, SearchPattern.FromText("ı", Utf8, IgnoreCase)));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
            CultureInfo.CurrentUICulture = savedUi;
        }

        // 手順 3: 全角の大文字・小文字は対応し、半角とは一致しない。
        byte[] wide = Encoding.UTF8.GetBytes("ＡＢ AB ａｂ");
        Assert.Equal([0L, 10L], FindAll(wide, SearchPattern.FromText("ａｂ", Utf8, IgnoreCase)));

        // 手順 4: 1 文字対複数文字の対応 (ß と SS) は使わない。
        byte[] strasse = Encoding.UTF8.GetBytes("STRASSE straße");
        Assert.Equal([12L], FindAll(strasse, SearchPattern.FromText("ß", Utf8, IgnoreCase)));

        // 手順 5: UTF-16LE。
        Assert.Equal([0L, 4L], FindAll([0x41, 0x00, 0x42, 0x00, 0x61, 0x00, 0x62, 0x00], SearchPattern.FromText("ab", Utf16LE, IgnoreCase)));
    }

    [Fact]
    public void CaseInsensitivePatternWithoutCasedLettersIsLiteral()
    {
        Assert.True(SearchPattern.FromText("123-", Ascii, IgnoreCase).IsLiteral);
        SearchPattern p = SearchPattern.FromText("ab", Ascii, IgnoreCase);
        Assert.False(p.IsLiteral);
        Assert.True(p.IsCaseInsensitive);
        Assert.Equal(new byte[] { 0x61, 0x62 }, p.Bytes); // 変換結果の表示は入力どおり
    }

    [Fact]
    public void CaseInsensitiveEscapedBytesAreMatchedExactly()
    {
        // `\x41` は符号化後のバイト列にそのまま入れるため、大文字・小文字の変種を作らない。
        SearchPattern p = SearchPattern.FromText("\\x41b", Ascii, new TextSearchOptions { CaseSensitive = false, UseEscapes = true });
        Assert.Equal([0L, 6L], FindAll(Encoding.ASCII.GetBytes("Ab ab AB"), p));
    }

    [Fact]
    public void CaseVariantsExcludeTurkishDottedAndDotlessI()
    {
        Assert.Empty(SearchPattern.CaseVariants(new Rune('İ')));
        Assert.Empty(SearchPattern.CaseVariants(new Rune('ı')));
        Assert.Equal([new Rune('I')], SearchPattern.CaseVariants(new Rune('i')));
        Assert.Empty(SearchPattern.CaseVariants(new Rune('1')));
    }

    [Theory]
    [InlineData(TextEncodingId.Ascii)]
    [InlineData(TextEncodingId.Ansi)]
    [InlineData(TextEncodingId.Oem)]
    [InlineData(TextEncodingId.Ebcdic)]
    [InlineData(TextEncodingId.Utf8)]
    [InlineData(TextEncodingId.Utf16LE)]
    [InlineData(TextEncodingId.Utf16BE)]
    [InlineData(TextEncodingId.Utf32LE)]
    [InlineData(TextEncodingId.Utf32BE)]
    [InlineData(TextEncodingId.ShiftJis)]
    [InlineData(TextEncodingId.EucJp)]
    [InlineData(TextEncodingId.Gb18030)]
    [InlineData(TextEncodingId.Big5)]
    [InlineData(TextEncodingId.EucKr)]
    public void EveryListedEncodingCanBeSearched(TextEncodingId id)
    {
        Encoding encoding = TextEncodings.Get(id);
        byte[] text = encoding.GetBytes("xxHello, World");
        Assert.Equal(TextEncodings.CodePage(id), encoding.CodePage);
        Assert.Equal([2L * (text.Length / 14)], FindAll(text, SearchPattern.FromText("Hello", encoding)));
        Assert.Equal([2L * (text.Length / 14)], FindAll(text, SearchPattern.FromText("hELLO", encoding, IgnoreCase)));
    }

    [Fact]
    public void Utf32AlignmentUsesFourByteUnits()
    {
        Encoding utf32 = TextEncodings.Get(TextEncodingId.Utf32LE);
        byte[] data = [0x00, .. utf32.GetBytes("A"), 0x00, 0x00, 0x00, .. utf32.GetBytes("A")];
        var aligned = new TextSearchOptions { AlignToCharacters = true };
        Assert.Equal([1L, 8L], FindAll(data, SearchPattern.FromText("A", utf32)));
        Assert.Equal([8L], FindAll(data, SearchPattern.FromText("A", utf32, aligned)));
        Assert.Equal(4, SearchPattern.FromText("A", utf32, aligned).Alignment);
        Assert.Equal(1, SearchPattern.FromText("A", Utf8, aligned).Alignment);
    }
}
