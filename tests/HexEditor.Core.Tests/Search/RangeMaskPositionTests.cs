using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>値の範囲の検索 (FIND-15)、ビットマスク検索 (FIND-16)、位置の条件 (FIND-17)。</summary>
public sealed class RangeMaskPositionTests
{
    private static SearchMatch[] FindAll(byte[] data, SearchPattern pattern, bool overlapping = false)
    {
        using Document doc = Doc(data);
        return [.. SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { IncludeOverlapping = overlapping }).Matches];
    }

    /// <summary>16 bit リトルエンディアンの値 100、150、200、201、99、0、0xFFFF、0x8000 を並べたもの (TC-FIND-15-01 の前提)。</summary>
    private static byte[] RangeData()
    {
        ushort[] values = [100, 150, 200, 201, 99, 0, 0xFFFF, 0x8000];
        byte[] data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            data[i * 2] = (byte)values[i];
            data[(i * 2) + 1] = (byte)(values[i] >> 8);
        }

        return data;
    }

    /// <summary>範囲の検索の既定の位置の条件 (サイズの倍数) を付けたパターン。</summary>
    private static SearchPattern Aligned(SearchPattern pattern, int size) => pattern.WithPosition(PositionCondition.Create(size, 0));

    [Fact]
    [Trait(TC, "TC-FIND-15-01")]
    public void RangeSyntaxAndMatches()
    {
        byte[] data = RangeData();
        var u16 = new IntegerSearchOptions { Bits = 16, Sign = IntegerSign.Unsigned, Endian = SearchEndian.Little };
        var e16 = new IntegerSearchOptions { Bits = 16, Sign = IntegerSign.Either, Endian = SearchEndian.Little };
        long Value(SearchMatch m) => data[m.Offset] | (data[m.Offset + 1] << 8);

        // 手順 1: 100..200 → 100、150 (`96 00`)、200 の 3 件。201 と 99 は一致しない。
        SearchMatch[] step1 = FindAll(data, Aligned(NumericRange.Integer("100..200", u16), 2));
        Assert.Equal([100L, 150, 200], step1.Select(Value));
        Assert.Equal(2, step1[1].Offset);
        Assert.Equal([0x96, 0x00], data.AsSpan(2, 2).ToArray());

        // 手順 2: どちらでも、..0 → 0 (両方)、0xFFFF (−1、符号あり)、0x8000 (−32768、符号あり)。
        SearchPattern either = Aligned(NumericRange.Integer("..0", e16), 2);
        SearchMatch[] step2 = FindAll(data, either);
        Assert.Equal([0L, 0xFFFF, 0x8000], step2.Select(Value));
        Assert.Equal(VariantColumn.Interpretation, either.VariantColumn);
        Assert.Equal($"LE, {SearchLabels.SignedAndUnsigned}", either.Variants[step2[0].Variant]);
        Assert.Equal($"LE, {SearchLabels.Signed}", either.Variants[step2[1].Variant]);
        Assert.Equal($"LE, {SearchLabels.Signed}", either.Variants[step2[2].Variant]);

        // 手順 3: 150.. → 150、200、201、0xFFFF、0x8000。
        Assert.Equal([150L, 200, 201, 0xFFFF, 0x8000], FindAll(data, Aligned(NumericRange.Integer("150..", u16), 2)).Select(Value));

        // 手順 4: 除外 → 201、99、0、0xFFFF、0x8000。
        Assert.Equal([201L, 99, 0, 0xFFFF, 0x8000], FindAll(data, Aligned(NumericRange.Integer("100..200", u16, exclude: true), 2)).Select(Value));

        // 手順 5: 200..100 (最小が最大より大きい) と 0..70000 (16 bit の範囲外) はエラー。
        Assert.Equal(PatternError.RangeOrder, Assert.Throws<PatternException>(() => NumericRange.Integer("200..100", u16)).Error);
        Assert.Equal(PatternError.UnsignedOutOfRange, Assert.Throws<PatternException>(() => NumericRange.Integer("0..70000", u16)).Error);
        Assert.Equal(PatternError.IntegerOutOfRange, Assert.Throws<PatternException>(() => NumericRange.Integer("0..70000", e16)).Error);

        // 手順 6: float、0..1 で 0.5 と NaN と 2.0 → 0.5 だけ。
        byte[] floats = new byte[12];
        BitConverter.GetBytes(0.5f).CopyTo(floats, 0);
        BitConverter.GetBytes(float.NaN).CopyTo(floats, 4);
        BitConverter.GetBytes(2.0f).CopyTo(floats, 8);
        SearchPattern f = Aligned(NumericRange.Float("0..1", new FloatSearchOptions { Format = FloatFormat.Single }), 4);
        Assert.Equal([0L], FindAll(floats, f).Select(m => m.Offset));
        Assert.Equal([8L], FindAll(floats, Aligned(NumericRange.Float("0..1", new FloatSearchOptions(), exclude: true), 4)).Select(m => m.Offset));

        // 端が形式の範囲を超えるとエラー (仕様の「エラー」): half の最大は 65504。無限大の端は受け付ける。
        var half = new FloatSearchOptions { Format = FloatFormat.Half };
        Assert.Equal(PatternError.FloatOverflow, Assert.Throws<PatternException>(() => NumericRange.Float("0..70000", half)).Error);
        Assert.Equal(PatternError.FloatOverflow, Assert.Throws<PatternException>(() => NumericRange.Float("-70000..", half)).Error);
        Assert.Equal(PatternError.FloatOverflow, Assert.Throws<PatternException>(() => NumericRange.Float("..1e39", new FloatSearchOptions())).Error);
        Assert.NotNull(NumericRange.Float("0..65504", half));
        Assert.NotNull(NumericRange.Float("-inf..inf", half));
        Assert.NotNull(NumericRange.Float("0..1e39", new FloatSearchOptions { Format = FloatFormat.Double }));

        // 範囲の表示 (「範囲: 100〜200」) の材料。
        Assert.NotNull(NumericRange.Integer("100..200", u16).Numeric!.Range);
        Assert.True(NumericRange.IsRange("100..200"));
        Assert.False(NumericRange.IsRange("100"));
    }

    [Fact]
    [Trait(TC, "TC-FIND-16-01")]
    public void ValueMaskAndBitPattern()
    {
        byte[] bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

        // 手順 1: 値 80、マスク 80 → 0x80〜0xFF の 128 件。
        Assert.Equal(Enumerable.Range(0x80, 128).Select(i => (long)i), FindAll(bytes256, SearchPattern.FromValueAndMask("80", "80")).Select(m => m.Offset));

        // 手順 2: xxxx 0001 → 0x01、0x11、…、0xF1 の 16 件。02 は一致しない。
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (long)((i * 16) + 1)), FindAll(bytes256, SearchPattern.FromBitPattern("xxxx 0001")).Select(m => m.Offset));

        // 手順 3: マスク 00 はエラー。
        PatternException zero = Assert.Throws<PatternException>(() => SearchPattern.FromValueAndMask("80", "00"));
        Assert.Equal(PatternError.MaskAllZero, zero.Error);
        Assert.True(zero.InMask);
        Assert.Equal(PatternError.MaskAllZero, Assert.Throws<PatternException>(() => SearchPattern.FromBitPattern("xxxx xxxx")).Error);

        // 手順 4: 長さが違う値とマスク、7 文字のビットパターンはエラー。
        Assert.Equal(PatternError.MaskLengthMismatch, Assert.Throws<PatternException>(() => SearchPattern.FromValueAndMask("80 00", "80")).Error);
        Assert.Equal(PatternError.BitPatternLength, Assert.Throws<PatternException>(() => SearchPattern.FromBitPattern("xxxx 000")).Error);

        // 手順 5: 0000 0001 0000 0010 → オフセット 1 (`01 02`) の 1 件。
        Assert.Equal([new SearchMatch(1, 2)], FindAll(bytes256, SearchPattern.FromBitPattern("0000 0001 0000 0010")));

        // マスクの長さは 256 バイトまで (FIND-16 の仕様 2)。
        string value257 = string.Concat(Enumerable.Repeat("FF", 257));
        Assert.Equal(PatternError.MaskTooLong, Assert.Throws<PatternException>(() => SearchPattern.FromValueAndMask(value257, value257)).Error);

        // ビットマスクの検索も基準の実装 (1 バイトずつの照合) と同じ結果になる (候補の絞り込みの確認)。
        byte[] random = RandomData16M.AsSpan(0, 1 << 20).ToArray();
        foreach (SearchPattern p in new[] { SearchPattern.FromBitPattern("1x0x 1x0x xxxx 0011"), SearchPattern.FromValueAndMask("A5 0F", "F0 0F") })
        {
            Assert.Equal(Naive(random, p, overlapping: false), FindAll(random, p));
        }
    }

    /// <summary>TD-FIND-ALIGN: すべて 00 の中に、0x000、0x001、0x100、0x200、0x201、0x400、0x500、0x5FF、0x600 に `EB`。</summary>
    private static byte[] Align()
    {
        byte[] data = new byte[4096];
        foreach (int at in new[] { 0x000, 0x001, 0x100, 0x200, 0x201, 0x400, 0x500, 0x5FF, 0x600 })
        {
            data[at] = 0xEB;
        }

        return data;
    }

    [Fact]
    [Trait(TC, "TC-FIND-17-01")]
    public void PositionConditionFiltersMatches()
    {
        byte[] data = Align();
        SearchPattern eb = SearchPattern.FromHex("EB");

        // 手順 1: mod 512 = 0 → 0x000、0x200、0x400、0x600。
        long[] expected = [0x000, 0x200, 0x400, 0x600];
        Assert.Equal(expected, FindAll(data, eb.WithPosition(PositionCondition.Create(512, 0))).Select(m => m.Offset));

        // 手順 2: ベースアドレス 0x100、表示上のアドレスで mod 512 = 0 → 0x100 と 0x500。
        Assert.Equal([0x100L, 0x500], FindAll(data, eb.WithPosition(PositionCondition.Create(512, 0, baseAddress: 0x100))).Select(m => m.Offset));

        // 手順 3: x = sector (512)、y = 0x1FF → 0x5FF。
        Assert.Equal([0x5FFL], FindAll(data, eb.WithPosition(PositionCondition.Create(512, 0x1FF))).Select(m => m.Offset));

        // 手順 4: y = 512 と x = 0 はエラー。
        Assert.Equal(PatternError.PositionRemainder, Assert.Throws<PatternException>(() => PositionCondition.Create(512, 512)).Error);
        Assert.Equal(PatternError.PositionModulus, Assert.Throws<PatternException>(() => PositionCondition.Create(0, 0)).Error);
        Assert.Equal(PatternError.PositionModulus, Assert.Throws<PatternException>(() => PositionCondition.Create(PositionCondition.MaxModulus + 1, 0)).Error);

        // 手順 5: 正規表現 (バイト列) `\xEB` と、テキスト (ASCII、エスケープ文字オン) `\xEB` も同じ 4 件。
        PositionCondition c = PositionCondition.Create(512, 0);
        Assert.Equal(expected, FindAll(data, RegexSearch.Bytes(@"\xEB", new RegexSearchOptions { Singleline = true }).WithPosition(c)).Select(m => m.Offset));
        Assert.Equal(expected, FindAll(data, SearchPattern.FromText(@"\xEB", Encoding.ASCII, new TextSearchOptions { UseEscapes = true }).WithPosition(c))
            .Select(m => m.Offset));

        // 数値・複数語・一致しない箇所でも使える (仕様 3)。
        Assert.Equal(expected, FindAll(data, NumericSearch.Integer("0xEB", new IntegerSearchOptions { Bits = 8 }).WithPosition(c)).Select(m => m.Offset));
        SearchPattern union = SearchPattern.Union([SearchPattern.FromHex("EB"), SearchPattern.FromHex("EB 00")], ["a", "b"]).WithPosition(c);
        Assert.Equal([0x000L, 0x200, 0x400, 0x400, 0x600, 0x600], FindAll(data, union).Select(m => m.Offset));

        // 次 / 前を検索も条件を満たす位置だけに止まる。
        using Document doc = Doc(data);
        SearchPattern p = eb.WithPosition(c);
        Assert.Equal(0x200, SearchEngine.Find(doc.Current, p, 1, forward: true, wrap: false)!.Value.Offset);
        Assert.Equal(0x400, SearchEngine.Find(doc.Current, p, 0x5FF, forward: false, wrap: false)!.Value.Offset);
    }
}
