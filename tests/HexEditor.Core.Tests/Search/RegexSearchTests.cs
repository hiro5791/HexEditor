using System.Diagnostics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>正規表現によるテキスト検索 (FIND-18) とバイト列の検索 (FIND-19)。</summary>
public sealed class RegexSearchTests
{
    private static readonly Encoding Utf8 = TextEncodings.Get(TextEncodingId.Utf8);
    private static readonly Encoding Utf16LE = TextEncodings.Get(TextEncodingId.Utf16LE);
    private static readonly RegexSearchOptions Bytes = new() { Singleline = true };

    private static SearchMatch[] FindAll(byte[] data, SearchPattern pattern, bool overlapping = false)
    {
        using Document doc = Doc(data);
        return [.. SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { IncludeOverlapping = overlapping }).Matches];
    }

    /// <summary>置換を 1 回行い、置換後のバイト列を返す。</summary>
    private static byte[] ReplaceFirst(byte[] data, SearchPattern pattern, string replacement)
    {
        using Document doc = Doc(data);
        SearchHit hit = SearchEngine.Find(doc.Current, pattern, 0, forward: true, wrap: false)!.Value;
        ReplaceOneResult result = Replacer.ReplaceAt(doc, pattern, hit.Offset, ReplacementTemplate.FromRegex(pattern, replacement), new ReplaceOptions());
        Assert.True(result.Applied, result.Issue.ToString());
        byte[] after = new byte[doc.Length];
        doc.Current.Read(0, after);
        return after;
    }

    [Fact]
    [Trait(TC, "TC-FIND-18-01")]
    public void TextRegexAndReplacements()
    {
        // 手順 1: UTF-8 の `xx ABC12 ab123` で `[A-Z]{3}\d{2}` → 3 (`ABC12`、長さ 5) の 1 件。
        Assert.Equal([new SearchMatch(3, 5)], FindAll(Utf8.GetBytes("xx ABC12 ab123"), RegexSearch.Text(@"[A-Z]{3}\d{2}", Utf8, new RegexSearchOptions())));

        // 手順 2: UTF-16LE の `mail: user@host.com.` で `\w+@\w+\.com` → 開始 12、長さ 26。
        Assert.Equal([new SearchMatch(12, 26)],
            FindAll(Utf16LE.GetBytes("mail: user@host.com."), RegexSearch.Text(@"\w+@\w+\.com", Utf16LE, new RegexSearchOptions())));

        // 手順 3: UTF-8 の `x 日本語 y` で `\p{L}{3}` → 開始 2、長さ 9。
        Assert.Equal([new SearchMatch(2, 9)], FindAll(Utf8.GetBytes("x 日本語 y"), RegexSearch.Text(@"\p{L}{3}", Utf8, new RegexSearchOptions())));

        // 手順 4: UTF-8 のバイト列 `41 FF 42` で `A.B` → 開始 0、長さ 3 (FF は U+FFFD の 1 文字)。
        Assert.Equal([new SearchMatch(0, 3)], FindAll([0x41, 0xFF, 0x42], RegexSearch.Text("A.B", Utf8, new RegexSearchOptions())));

        // 手順 5: UTF-16LE の `user@host` に `(\w+)@(\w+)` で `$2-$1` → `host-user` の 18 バイト。
        byte[] mail = Utf16LE.GetBytes("user@host");
        SearchPattern groups = RegexSearch.Text(@"(\w+)@(\w+)", Utf16LE, new RegexSearchOptions());
        Assert.Equal(Utf16LE.GetBytes("host-user"), ReplaceFirst(mail, groups, "$2-$1"));
        SearchPattern named = RegexSearch.Text(@"(?<n>\w+)@\w+", Utf16LE, new RegexSearchOptions());
        Assert.Equal(Utf16LE.GetBytes("user"), ReplaceFirst(mail, named, "$<n>"));
        Assert.Equal(mail, ReplaceFirst(mail, named, "$&"));
        Assert.Equal(Utf16LE.GetBytes("$"), ReplaceFirst(mail, named, "$$"));

        // 手順 6: 長さ 0 の一致は報告しない。
        Assert.Empty(FindAll(Utf8.GetBytes("bbb"), RegexSearch.Text("a*", Utf8, new RegexSearchOptions())));
        Assert.Empty(FindAll(Utf8.GetBytes("bbb"), RegexSearch.Text("^", Utf8, new RegexSearchOptions { Multiline = true })));

        // 手順 7: `(abc` は位置と理由 (閉じ括弧がない) のエラー。
        PatternException error = Assert.Throws<PatternException>(() => RegexSearch.Text("(abc", Utf8, new RegexSearchOptions()));
        Assert.Equal(PatternError.RegexSyntax, error.Error);
        Assert.Equal("InsufficientClosingParentheses", error.Detail);
        Assert.NotNull(error.Position);

        // フラグ i (Invariant の対応)、s (改行)、後戻りしない方式の判定 (仕様 6)。
        Assert.Single(FindAll(Utf8.GetBytes("FILE"), RegexSearch.Text("file", Utf8, new RegexSearchOptions { IgnoreCase = true })));
        Assert.Empty(FindAll(Utf8.GetBytes("a\nb"), RegexSearch.Text("a.b", Utf8, new RegexSearchOptions())));
        Assert.Single(FindAll(Utf8.GetBytes("a\nb"), RegexSearch.Text("a.b", Utf8, new RegexSearchOptions { Singleline = true })));
        Assert.True(RegexSearch.IsNonBacktracking(RegexSearch.Text("(a+)+b", Utf8, new RegexSearchOptions())));
        Assert.False(RegexSearch.IsNonBacktracking(RegexSearch.Text("(a+)+(?=b)", Utf8, new RegexSearchOptions())));
    }

    [Fact]
    [Trait(TC, "TC-FIND-18-01")]
    public void MatchesAcrossChunksAndTheMaximumLength()
    {
        // チャンクの境界から始まる一致が見つかり、重なり部分で 2 回報告されない (FIND-01 の仕様 3・4)。後読み・\B は前のチャンクの文字を見る。
        byte[] data = new byte[300_000];
        Encoding.ASCII.GetBytes("Xabc123def").CopyTo(data, 262_143);
        using Document doc = Doc(data);
        foreach (string pattern in new[] { @"abc\d+def", @"(?<=X)abc\d+", @"\Babc\d+" })
        {
            SearchPattern p = RegexSearch.Text(pattern, Encoding.ASCII, new RegexSearchOptions());
            foreach (int chunk in new[] { 65_536, 262_144, 4 * 1024 * 1024 })
            {
                IReadOnlyList<SearchMatch> all = SearchEngine.FindAll(doc.Current, p, new SearchOptions { ChunkSize = chunk }).Matches;
                Assert.True(all.Count == 1, $"{pattern} {chunk}: {string.Join(",", all)}");
                SearchMatch m = all[0];
                Assert.Equal(262_144, m.Offset);
            }
        }

        // 一致の最大長 (既定 4,096 バイト) を超える一致は報告しない (FIND-01 の仕様 3)。
        byte[] runs = new byte[20_000];
        Array.Fill(runs, (byte)'a', 0, 4096);
        Array.Fill(runs, (byte)'a', 8000, 4097);
        SearchPattern aPlus = RegexSearch.Text("a+", Encoding.ASCII, new RegexSearchOptions());
        Assert.Equal([new SearchMatch(0, 4096)], FindAll(runs, aPlus));

        // 後方検索 (前を検索) も見つかる。
        using Document small = Doc(Utf8.GetBytes("abc1 abc2 abc3"));
        SearchPattern abc = RegexSearch.Text(@"abc\d", Utf8, new RegexSearchOptions());
        Assert.Equal(10, SearchEngine.Find(small.Current, abc, small.Length, forward: false, wrap: false)!.Value.Offset);
        Assert.Equal(5, SearchEngine.Find(small.Current, abc, 1, forward: true, wrap: false)!.Value.Offset);
    }

    [Fact]
    [Trait(TC, "TC-FIND-18-02")]
    public void CatastrophicBacktrackingStopsAtTheTimeLimit()
    {
        // TC-FIND-18-02 の Core の部分: 後戻りしない方式の (a+)+b はすぐ終わり、後戻りする (a+)+(?=b) は時間の上限で止まって知らせる。
        byte[] data = new byte[0x200];
        Array.Fill(data, (byte)'a', 0x100, 40);
        using Document doc = Doc(data);
        var watch = Stopwatch.StartNew();
        Assert.Null(SearchEngine.Find(doc.Current, RegexSearch.Text("(a+)+b", Encoding.ASCII, new RegexSearchOptions()), 0, true, false));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());

        var limit = TimeSpan.FromMilliseconds(300);
        SearchPattern slow = RegexSearch.Text("(a+)+(?=b)", Encoding.ASCII, new RegexSearchOptions { TimeLimit = limit });
        var asked = new List<SearchRange>();
        var options = new SearchOptions { OnTimeout = r => { asked.Add(r); return UnreadableAction.Abort; } };
        Assert.Throws<SearchTimedOutException>(() => SearchEngine.Find(doc.Current, slow, 0, true, false, options));
        Assert.Equal([new SearchRange(0, 0x200)], asked);

        // 「飛ばす」なら飛ばした範囲を結果に記録する。
        using SearchResults results = SearchEngine.FindAll(doc.Current, slow, new SearchOptions { OnTimeout = _ => UnreadableAction.Skip });
        Assert.Equal(SearchResultsState.Completed, results.State);
        Assert.Equal([new SearchRange(0, 0x200)], results.TimedOutRanges);
    }

    /// <summary>TD-PNG の代わりの小さい PNG (シグネチャと IHDR・IEND)。</summary>
    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0xF3, 0xFF, 0x61, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    [Fact]
    [Trait(TC, "TC-FIND-19-01")]
    public void ByteRegex()
    {
        // 手順 1: TD-PE-X64 の代わりに x64 の Windows 実行ファイル (このテストの DLL) で `MZ.{58}` → 開始 0、長さ 60。
        byte[] pe = File.ReadAllBytes(typeof(SearchPattern).Assembly.Location).AsSpan(0, 4096).ToArray();
        Assert.Equal(new SearchMatch(0, 60), FindAll(pe, RegexSearch.Bytes("MZ.{58}", Bytes))[0]);

        // 手順 2: TD-PNG で `\x89PNG\r\n\x1A\n` → 開始 0、長さ 8。
        Assert.Equal([new SearchMatch(0, 8)], FindAll(Png(), RegexSearch.Bytes(@"\x89PNG\r\n\x1A\n", Bytes)));

        // 手順 3: `[\x80-\xFF]{4,}` は開始 1、長さ 5 の 1 件。`.{4}` は 0、4、8 から長さ 4 (`.` は既定で 0A に一致する)。
        byte[] data = [0x41, 0x80, 0x81, 0x82, 0x83, 0x84, 0x41, 0x90, 0x91, 0x92, 0x41, 0x0A, 0x0A, 0x0A, 0x0A];
        Assert.Equal([new SearchMatch(1, 5)], FindAll(data, RegexSearch.Bytes(@"[\x80-\xFF]{4,}", Bytes)));
        Assert.Equal([0L, 4, 8], FindAll(data, RegexSearch.Bytes(".{4}", Bytes)).Select(m => m.Offset));
        Assert.All(FindAll(data, RegexSearch.Bytes(".{4}", Bytes)), m => Assert.Equal(4, m.Length));

        // 手順 4: `\p{L}` と `\u0041` はエラー。`\p{...}` は Unicode プロパティのエラー。
        Assert.Equal(PatternError.RegexUnicodeProperty, Assert.Throws<PatternException>(() => RegexSearch.Bytes(@"\p{L}", Bytes)).Error);
        Assert.Equal(PatternError.RegexUnicodeEscape, Assert.Throws<PatternException>(() => RegexSearch.Bytes(@"\" + "u0041", Bytes)).Error);

        // 手順 5: `4D 5A 90 00` に `(MZ)(.)` で `$2 00 $1` → `90 00 4D 5A 00`。
        Assert.Equal([0x90, 0x00, 0x4D, 0x5A, 0x00], ReplaceFirst([0x4D, 0x5A, 0x90, 0x00], RegexSearch.Bytes("(MZ)(.)", Bytes), "$2 00 $1"));

        // `A` は 41 に一致する。`\d` `\w` `\b` は ASCII だけ、`i` は ASCII の英字だけ (仕様 2・4)。
        Assert.Equal([1L], FindAll([0x00, 0x41], RegexSearch.Bytes("A", Bytes)).Select(m => m.Offset));
        Assert.Empty(FindAll([0xE9, 0xC9], RegexSearch.Bytes(@"\w", Bytes)));
        Assert.Empty(FindAll([0xE9], RegexSearch.Bytes("É", Bytes with { IgnoreCase = true })));
        Assert.Single(FindAll("a"u8.ToArray(), RegexSearch.Bytes("A", Bytes with { IgnoreCase = true })));
        Assert.Equal([1L], FindAll([0xE9, 0x41, 0x20], RegexSearch.Bytes(@"\bA\b", Bytes)).Select(m => m.Offset));
        Assert.Equal(PatternError.RegexNonByteCharacter, Assert.Throws<PatternException>(() => RegexSearch.Bytes("あ", Bytes)).Error);

        // 範囲の中で 7F と 80 をまたぐ範囲。
        Assert.Equal([0L, 1, 2], FindAll([0x7E, 0x7F, 0x80, 0x81], RegexSearch.Bytes(@"[\x7E-\x80]", Bytes)).Select(m => m.Offset));
    }
}
