using System.Buffers.Binary;
using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>整数の検索 (FIND-13) と浮動小数点の検索 (FIND-14)。</summary>
public sealed class NumericSearchTests
{
    /// <summary>TC-FIND-13-01 の 24 バイトのデータ。</summary>
    private static readonly byte[] IntegerData =
    [
        0x78, 0x56, 0x34, 0x12, 0x00, 0x34, 0x12, 0x00, 0x12, 0x34, 0x00, 0xFF,
        0x00, 0x56, 0x34, 0x12, 0x00, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12, 0x00,
    ];

    private static SearchMatch[] FindAll(byte[] data, SearchPattern pattern)
    {
        using Document doc = Doc(data);
        return [.. SearchEngine.FindAll(doc.Current, pattern, new SearchOptions { IncludeOverlapping = true }).Matches];
    }

    private static long[] Offsets(byte[] data, SearchPattern pattern) => [.. FindAll(data, pattern).Select(m => m.Offset)];

    private static SearchPattern Int(string value, int bits, SearchEndian endian = SearchEndian.Little, IntegerSign sign = IntegerSign.Either) =>
        NumericSearch.Integer(value, new IntegerSearchOptions { Bits = bits, Endian = endian, Sign = sign });

    [Fact]
    [Trait(TC, "TC-FIND-13-01")]
    public void IntegersWithSizeSignAndEndian()
    {
        // 手順 1: 32 bit LE の 0x12345678: 0 と 19。
        Assert.Equal([0L, 19L], Offsets(IntegerData, Int("0x12345678", 32)));

        // 手順 2: 16 bit「両方」の 0x1234: 2, 5, 14, 21 (LE) と 8 (BE)。各結果にエンディアンが付く。
        SearchPattern both = Int("0x1234", 16, SearchEndian.Both);
        SearchMatch[] matches = FindAll(IntegerData, both);
        Assert.Equal([2L, 5L, 8L, 14L, 21L], matches.Select(m => m.Offset));
        Assert.Equal(["LE", "LE", "BE", "LE", "LE"], matches.Select(m => both.Variants[m.Variant]));

        // 手順 3: 8 bit「どちらでも」の -1 と 255 は同じ結果 (11)。
        Assert.Equal([11L], Offsets(IntegerData, Int("-1", 8)));
        Assert.Equal([11L], Offsets(IntegerData, Int("255", 8)));
        Assert.Equal(Int("-1", 8).Bytes, Int("255", 8).Bytes);

        // 手順 4: 24 bit LE の 0x123456 は 1, 13, 20。48 bit LE の 0x123456789ABC は 17。
        Assert.Equal([1L, 13L, 20L], Offsets(IntegerData, Int("0x123456", 24)));
        Assert.Equal([17L], Offsets(IntegerData, Int("0x123456789ABC", 48)));

        // 手順 5: 範囲外はエラー。256 のエラーには 8 bit の範囲が示される。
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            PatternException e256 = Assert.Throws<PatternException>(() => Int("256", 8));
            Assert.Equal(PatternError.IntegerOutOfRange, e256.Error);
            Assert.Equal(["8", "-128", "127", "0", "255"], e256.Arguments);
            Assert.Equal(PatternError.IntegerOutOfRange, Assert.Throws<PatternException>(() => Int("-129", 8)).Error);
            PatternException unsigned = Assert.Throws<PatternException>(() => Int("-1", 32, sign: IntegerSign.Unsigned));
            Assert.Equal(PatternError.UnsignedOutOfRange, unsigned.Error);
            Assert.Equal(["32", "0", "4,294,967,295"], unsigned.Arguments);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        // 手順 6: 16 bit「両方」の 0x1212 は 1 つのバイト列にまとめる。
        SearchPattern symmetric = Int("0x1212", 16, SearchEndian.Both);
        Assert.True(symmetric.IsLiteral);
        Assert.Equal(new byte[] { 0x12, 0x12 }, symmetric.Bytes);
        Assert.Equal(["LE/BE"], symmetric.Variants);

        // 64 bit の符号なしの最大値も入力できる。
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 8), Int("18446744073709551615", 64).Bytes);
        Assert.Equal(new byte[] { 0x12, 0x34 }, Int("0x1234", 16, SearchEndian.Big).Bytes);
    }

    [Fact]
    public void IntegerValueColumnAndReplacementEncoding()
    {
        SearchPattern both = Int("0x1234", 16, SearchEndian.Both);
        NumericSearchInfo info = both.Numeric!;
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("4,660 (0x1234)", info.FormatValue([0x34, 0x12], 0));
            Assert.Equal("4,660 (0x1234)", info.FormatValue([0x12, 0x34], 1));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        // 置換語は一致したエンディアンで符号化する (FIND-22 の仕様 2)。
        Assert.Equal(new byte[] { 0xCD, 0xAB }, info.EncodeReplacement("0xABCD", 0));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, info.EncodeReplacement("0xABCD", 1));
    }

    /// <summary>値をリトルエンディアンで 8 バイト間隔に並べたデータ (残りは AA で埋める)。</summary>
    private static byte[] Spaced(params byte[][] values)
    {
        byte[] data = new byte[values.Length * 8];
        Array.Fill(data, (byte)0xAA);
        for (int i = 0; i < values.Length; i++)
        {
            values[i].CopyTo(data, i * 8);
        }

        return data;
    }

    private static byte[] F(float v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        return b;
    }

    private static byte[] D(double v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint bits)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, bits);
        return b;
    }

    private static SearchPattern Float(string value, FloatFormat format = FloatFormat.Single, ToleranceKind tolerance = ToleranceKind.None, double t = 0) =>
        NumericSearch.Float(value, new FloatSearchOptions { Format = format, Tolerance = tolerance, ToleranceValue = t });

    [Fact]
    [Trait(TC, "TC-FIND-14-01")]
    public void FloatingPointWithTolerance()
    {
        // 手順 1: float、誤差なし、0.1 は `CD CC CC 3D` だけ。
        byte[] step1 = Spaced([0xCD, 0xCC, 0xCC, 0x3D], [0xCE, 0xCC, 0xCC, 0x3D]);
        Assert.Equal([0L], Offsets(step1, Float("0.1")));

        // 手順 2: double、絶対誤差 0.001、3.14159 は 3.1415926535 だけ。
        byte[] step2 = [.. D(3.1415926535), .. D(3.143)];
        Assert.Equal(new byte[] { 0x44, 0x17, 0x41, 0x54, 0xFB, 0x21, 0x09, 0x40 }, step2[..8]);
        Assert.Equal([0L], Offsets(step2, Float("3.14159", FloatFormat.Double, ToleranceKind.Absolute, 0.001)));

        // 手順 3: float、誤差なし、0 は +0 と −0。非正規化数は一致しない。
        byte[] step3 = Spaced([0, 0, 0, 0], [0, 0, 0, 0x80], [1, 0, 0, 0]);
        Assert.Equal([0L, 8L], Offsets(step3, Float("0")));

        // 手順 4: nan はすべての NaN に一致し、+∞ には一致しない (許容誤差は無視)。
        byte[] step4 = Spaced([0, 0, 0xC0, 0x7F], [1, 0, 0x80, 0x7F], [0, 0, 0xC0, 0xFF], [0, 0, 0x80, 0x7F]);
        Assert.Equal([0L, 8L, 16L], Offsets(step4, Float("nan", tolerance: ToleranceKind.Absolute, t: 1)));
        Assert.Equal([0L, 8L, 16L], Offsets(step4, Float("nan")));

        // 手順 5: ULP 1 で 1.0 は 3F800000、3F800001、3F7FFFFF。3F800002 は一致しない。
        byte[] step5 = Spaced(U32(0x3F800000), U32(0x3F800001), U32(0x3F7FFFFF), U32(0x3F800002));
        Assert.Equal([0L, 8L, 16L], Offsets(step5, Float("1.0", tolerance: ToleranceKind.Ulp, t: 1)));

        // 手順 6: inf は +∞ だけ。
        byte[] step6 = Spaced([0, 0, 0x80, 0x7F], [0, 0, 0x80, 0xFF]);
        Assert.Equal([0L], Offsets(step6, Float("inf")));
        Assert.Equal([0L], Offsets(step6, Float("inf", tolerance: ToleranceKind.Absolute, t: 1)));

        // 手順 7: 相対誤差 0.001 で 1000 は 1000.9 だけ。
        byte[] step7 = Spaced(F(1000.9f), F(1001.1f));
        Assert.Equal([0L], Offsets(step7, Float("1000", tolerance: ToleranceKind.Relative, t: 0.001)));

        // 手順 8: `1,5` はエラー。`1.5e-3` は 0.0015、`-inf` は −∞。half の 70000 は無限大になるためエラー。負の誤差はエラー。
        Assert.Equal(PatternError.InvalidNumber, Assert.Throws<PatternException>(() => NumericSearch.ParseFloat("1,5")).Error);
        Assert.Equal(0.0015, NumericSearch.ParseFloat("1.5e-3"));
        Assert.Equal(double.NegativeInfinity, NumericSearch.ParseFloat("-inf"));
        PatternException overflow = Assert.Throws<PatternException>(() => Float("70000", FloatFormat.Half));
        Assert.Equal(PatternError.FloatOverflow, overflow.Error);
        Assert.Equal(["half"], overflow.Arguments);
        Assert.Equal(PatternError.InvalidTolerance, Assert.Throws<PatternException>(() => Float("1", tolerance: ToleranceKind.Absolute, t: -0.1)).Error);

        // 手順 9: 地域設定が de-DE でも 0.1。
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(0.1, NumericSearch.ParseFloat("0.1"));
            Assert.Equal(PatternError.InvalidNumber, Assert.Throws<PatternException>(() => NumericSearch.ParseFloat("0,1")).Error);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        // 格納される値の表示 (FIND-14 の画面)。
        Assert.Equal("0.100000001490116", NumericSearch.Round(0.1, FloatFormat.Single).ToString("G15", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void BothEndiansForFloats()
    {
        byte[] le = F(1.5f);
        byte[] be = [.. le.Reverse()];
        byte[] data = Spaced(le, be);
        SearchPattern p = NumericSearch.Float("1.5", new FloatSearchOptions { Endian = SearchEndian.Both });
        SearchMatch[] matches = FindAll(data, p);
        Assert.Equal([0L, 8L], matches.Select(m => m.Offset));
        Assert.Equal(["LE", "BE"], matches.Select(m => p.Variants[m.Variant]));

        SearchPattern tolerant = NumericSearch.Float("1.5", new FloatSearchOptions { Endian = SearchEndian.Both, Tolerance = ToleranceKind.Absolute, ToleranceValue = 0.01 });
        matches = FindAll(data, tolerant);
        Assert.Equal([0L, 8L], matches.Select(m => m.Offset));
        Assert.Equal(["LE", "BE"], matches.Select(m => tolerant.Variants[m.Variant]));
    }
}
