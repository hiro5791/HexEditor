using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>置換 (FIND-22)、すべて置換 (FIND-23)、長さの違う置換 (FIND-24)。</summary>
public sealed class ReplaceTests
{
    private static readonly byte[] Sample24 = [0x00, 0x00, 0xCA, 0xFE, 0xBA, 0xBE, 0x11, 0x22, 0x33, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static ReplaceOneResult ReplaceFirst(Document doc, string find, string replace, ReplaceOptions? options = null)
    {
        SearchPattern p = SearchPattern.FromHex(find);
        SearchHit hit = SearchEngine.Find(doc.Current, p, 0, forward: true, wrap: false)!.Value;
        return Replacer.ReplaceAt(doc, p, hit.Offset, ReplacementTemplate.FromHex(replace), options ?? new ReplaceOptions());
    }

    [Fact]
    [Trait(TC, "TC-FIND-22-03")]
    public void KeepOriginalBytesAtWildcards()
    {
        using Document doc = Doc([0x12, 0x34, 0x12, 0x34, 0xAB, 0xCD]);

        // 手順 1: `12 34` → `?? 00`: 1 バイト目は元のまま。
        Assert.True(ReplaceFirst(doc, "12 34", "?? 00").Applied);
        Assert.Equal(new byte[] { 0x12, 0x00, 0x12, 0x34, 0xAB, 0xCD }, ReadAll(doc.Current));

        // 手順 2: `?? CD` → `EF ??`。
        Assert.True(ReplaceFirst(doc, "?? CD", "EF ??").Applied);
        Assert.Equal(new byte[] { 0x12, 0x00, 0x12, 0x34, 0xEF, 0xCD }, ReadAll(doc.Current));

        // 手順 3: `?` (1 ニブル) と `?? 0` (奇数桁) はエラー。
        Assert.Throws<PatternException>(() => ReplacementTemplate.FromHex("?"));
        Assert.Equal(PatternError.OddDigits, Assert.Throws<PatternException>(() => ReplacementTemplate.FromHex("?? 0")).Error);
        Assert.Equal(PatternError.InvalidReplacementWildcard, Assert.Throws<PatternException>(() => ReplacementTemplate.FromHex("A? 00")).Error);

        // 1 回の置換は 1 回の Undo で戻る (FIND-22 の仕様 5)。
        doc.Undo();
        Assert.Equal(new byte[] { 0x12, 0x00, 0x12, 0x34, 0xAB, 0xCD }, ReadAll(doc.Current));
    }

    [Fact]
    [Trait(TC, "TC-FIND-24-01")]
    public void LengthPoliciesAndFillers()
    {
        byte[] Run(string replacement, ReplaceOptions options, out ReplaceOneResult result)
        {
            using Document doc = Doc((byte[])Sample24.Clone());
            result = ReplaceFirst(doc, "CA FE BA BE", replacement, options);
            return ReadAll(doc.Current);
        }

        var change = new ReplaceOptions { Policy = LengthPolicy.ChangeLength };
        var pad = new ReplaceOptions { Policy = LengthPolicy.PadKeepLength };
        var overwrite = new ReplaceOptions { Policy = LengthPolicy.OverwriteFollowing };

        // 手順 1: 長さを変える: 18 バイト。
        byte[] r1 = Run("01 02 03 04 05 06", change, out _);
        Assert.Equal(18, r1.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x11, 0x22 }, r1[..10]);

        // 手順 2: 埋めて長さを保つ (00)。
        byte[] r2 = Run("01 02", pad, out _);
        Assert.Equal(16, r2.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x11, 0x22 }, r2[..8]);

        // 手順 3: 埋め草 CC。
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0xCC, 0xCC, 0x11, 0x22 }, Run("01 02", pad with { Filler = [0xCC] }, out _)[..8]);

        // 手順 4: 埋め草 DE AD で 01 に置換: 01 DE AD DE。
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0xDE, 0xAD, 0xDE, 0x11, 0x22 }, Run("01", pad with { Filler = [0xDE, 0xAD] }, out _)[..8]);

        // 手順 5: 埋めて長さを保つで長い置換語はエラー。置換されない。
        byte[] r5 = Run("01 02 03 04 05 06", pad, out ReplaceOneResult tooLong);
        Assert.Equal(ReplaceIssue.TooLong, tooLong.Issue);
        Assert.Equal(Sample24, r5);

        // 手順 6: 後ろを上書きする: 長さ 16、一致の後ろの 2 バイトを上書き。
        byte[] r6 = Run("01 02 03 04 05 06", overwrite, out _);
        Assert.Equal(16, r6.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x33, 0x44 }, r6[..10]);

        // 手順 7: 後ろを上書きするで短い置換語: 残りはそのまま。
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0xBA, 0xBE, 0x11, 0x22 }, Run("01 02", overwrite, out _)[..8]);

        // 手順 8: 同じ長さはどの選択肢でも上書き。
        foreach (ReplaceOptions o in new[] { change, pad, overwrite })
        {
            byte[] r8 = Run("01 02 03 04", o, out _);
            Assert.Equal(16, r8.Length);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x11, 0x22 }, r8[..8]);
        }
    }

    [Fact]
    public void FixedLengthDocumentsCannotChangeLengthAndOverflowNeedsADecision()
    {
        using var doc = new Document(new MemoryByteSource((byte[])Sample24.Clone(), "disk", SourceCapabilities.CanWrite), Options());
        Assert.Equal(LengthPolicy.PadKeepLength, ReplaceOptions.DefaultPolicy(doc.CanResize));
        Assert.Equal(ReplaceIssue.CannotResize, ReplaceFirst(doc, "CA FE BA BE", "01", new ReplaceOptions { Policy = LengthPolicy.ChangeLength }).Issue);
        Assert.Equal(ReplaceIssue.CannotResize, ReplaceFirst(doc, "CA FE BA BE", "", new ReplaceOptions { Policy = LengthPolicy.ChangeLength }).Issue);

        // 末尾の一致を「後ろを上書きする」で長い置換語に: 確認が必要 → 超える分を書かない。
        using Document file = Doc([0x00, 0xAA, 0xBB]);
        var o = new ReplaceOptions { Policy = LengthPolicy.OverwriteFollowing };
        Assert.Equal(ReplaceIssue.ExceedsEnd, ReplaceFirst(file, "AA BB", "01 02 03 04", o).Issue);
        Assert.True(ReplaceFirst(file, "AA BB", "01 02 03 04", o with { Overflow = OverflowMode.Truncate }).Applied);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02 }, ReadAll(file.Current));
        file.Undo();
        Assert.True(ReplaceFirst(file, "AA BB", "01 02 03 04", o with { Overflow = OverflowMode.Append }).Applied);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }, ReadAll(file.Current));
    }

    [Fact]
    public void EmptyReplacementDeletesTheMatch()
    {
        using Document doc = Doc((byte[])Sample24.Clone());
        Assert.True(ReplaceFirst(doc, "CA FE BA BE", "").Applied);
        Assert.Equal(12, doc.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x11, 0x22 }, ReadAll(doc.Current)[..4]);
    }

    [Fact]
    public void ReplaceAllIsOneUndoAndUsesOnePieceOfAddedData()
    {
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, p, SearchOptions.Default);
        Assert.Equal(1000, found.Count);
        long addBefore = doc.AddBuffer.Length;
        long applied = doc.ApplyReplacements(Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("87 65 43 21"), new ReplaceOptions(), doc.CanResize), "すべて置換");
        Assert.Equal(1000, applied);
        Assert.Equal(4, doc.AddBuffer.Length - addBefore); // 同じ置換語は追加バッファに 1 回だけ書く。
        Assert.Equal(0, SearchEngine.FindAll(doc.Current, p).Count);
        Assert.Equal(1000, SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("87 65 43 21")).Count);
        Assert.Equal(2, doc.History.Count);

        // Undo 1 回ですべて戻る (FIND-23 の仕様 3)。
        doc.Undo();
        Assert.Equal(1000, SearchEngine.FindAll(doc.Current, p).Count);
        Assert.Equal(Hits1000(), ReadAll(doc.Current));
    }

    [Fact]
    public void ReplaceAllWithChangingLengthAndOverlappingMatches()
    {
        // 重ならない一致を前から順に選ぶ (重なる一致を含めるオプションは無視する。FIND-23 の仕様 1)。
        using Document doc = Doc(Encoding.ASCII.GetBytes("AAAAA-AA"));
        SearchPattern p = SearchPattern.FromText("AA", Encoding.ASCII);
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, p, new SearchOptions { IncludeOverlapping = true });
        Assert.Equal([0L, 2L, 6L], Offsets(found));
        doc.ApplyReplacements(Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromText("xyz", Encoding.ASCII, false), new ReplaceOptions(), true), "r");
        Assert.Equal("xyzxyzA-xyz", Encoding.ASCII.GetString(ReadAll(doc.Current)));

        // 「後ろを上書きする」で次の一致に食い込む場合、食い込まれた一致は飛ばす。
        using Document doc2 = Doc(Encoding.ASCII.GetBytes("AABAAB"));
        using SearchResults found2 = Replacer.FindForReplaceAll(doc2.Current, p, SearchOptions.Default);
        doc2.ApplyReplacements(Replacer.PlanAll(doc2.Current, found2, ReplacementTemplate.FromText("1234", Encoding.ASCII, false),
            new ReplaceOptions { Policy = LengthPolicy.OverwriteFollowing }, true), "r");
        Assert.Equal("1234AB", Encoding.ASCII.GetString(ReadAll(doc2.Current)));
    }

    [Fact]
    public void ReplaceAllVerifiesMatchesAgainstTheCurrentState()
    {
        // 検索の後に一致を書き換えた場合、その一致は置換しない (FIND-03 の仕様 5)。前への挿入は位置を補正する。
        using Document doc = Doc(Hits1000());
        SearchPattern p = SearchPattern.FromHex("12 34 56 78");
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, p, SearchOptions.Default);
        doc.Insert(0, [0xEE, 0xEE]);
        doc.Overwrite(2 + 0x400, [0x00]);
        long applied = doc.ApplyReplacements(Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("87 65 43 21"), new ReplaceOptions(), true), "r");
        Assert.Equal(999, applied);
        byte[] all = ReadAll(doc.Current);
        Assert.Equal(new byte[] { 0x87, 0x65, 0x43, 0x21 }, all[2..6]);
        Assert.Equal(new byte[] { 0x00, 0x34, 0x56, 0x78 }, all[0x402..0x406]);
    }

    [Fact]
    public void CancelledReplaceAllChangesNothing()
    {
        using Document doc = Doc(Hits1000());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromHex("12 34 56 78"), SearchOptions.Default, null, cts.Token));
        Assert.Equal(1, doc.History.Count);
        Assert.False(doc.IsModified);

        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromHex("12 34 56 78"), SearchOptions.Default);
        Assert.ThrowsAny<OperationCanceledException>(() => doc.PrepareReplacements(
            Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("00"), new ReplaceOptions(), true), cancellationToken: cts.Token));
        Assert.False(doc.IsModified);
    }

    [Fact]
    public void PreparedReplacementIsRejectedAfterAnEdit()
    {
        using Document doc = Doc(Hits1000());
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromHex("12 34 56 78"), SearchOptions.Default);
        PreparedReplacement prepared = doc.PrepareReplacements(Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("00"), new ReplaceOptions(), true));
        Assert.Equal(1000, prepared.Count);
        Assert.Equal(-3000, prepared.LengthDelta);
        doc.Overwrite(5, [1]);
        Assert.Throws<InvalidOperationException>(() => doc.CommitReplacements(prepared, "r"));
    }

    [Fact]
    public void CheckFindsPaddingErrorsBeforeApplying()
    {
        using Document doc = Doc(Hits1000());
        using SearchResults found = Replacer.FindForReplaceAll(doc.Current, SearchPattern.FromHex("12 34"), SearchOptions.Default);
        (ReplaceIssue issue, long offset) = Replacer.Check(found, ReplacementTemplate.FromHex("01 02 03"), new ReplaceOptions { Policy = LengthPolicy.PadKeepLength }, doc.Length, true);
        Assert.Equal(ReplaceIssue.TooLong, issue);
        Assert.Equal(0, offset);
        Assert.Throws<ReplaceException>(() => doc.ApplyReplacements(
            Replacer.PlanAll(doc.Current, found, ReplacementTemplate.FromHex("01 02 03"), new ReplaceOptions { Policy = LengthPolicy.PadKeepLength }, true), "r"));
        Assert.False(doc.IsModified);
    }
}
