using HexEditor.Core.Tools;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-25 IEEE 754 浮動小数点のビジュアライザ。</summary>
public sealed class FloatAnalyzerTests
{
    private static FloatRounding Parse(FloatLayout format, string text)
    {
        (FloatRounding? result, FloatInputError? error) = FloatAnalyzer.FromDecimalText(format, text);
        Assert.Null(error);
        return result!;
    }

    [Fact]
    [Trait(TC, "TC-TOOL-25-01")]
    public void DecimalInputIsRoundedAndShownExactlyWithTheRoundingError()
    {
        FloatRounding rounded = Parse(FloatLayout.Single, "0.1");
        Assert.Equal("0x3DCCCCCD", FloatAnalyzer.HexText(FloatLayout.Single, rounded.Bits));
        Assert.Equal("1.490116119384765625E-9", rounded.Error);

        FloatAnalysis a = FloatAnalyzer.Analyze(FloatLayout.Single, rounded.Bits);
        Assert.Equal("0.100000001490116119384765625", a.ExactDecimal);
        Assert.Equal("0.1", a.Shortest);
        Assert.Equal(FloatClass.Normal, a.Class);
        Assert.Equal(("0", "01111011", "10011001100110011001101"), (a.BitString[..1], a.ExponentBitString, a.MantissaBitString));
        Assert.Equal(-4, a.UnbiasedExponent);
        Assert.Equal("1.10011001100110011001101", a.SignificandText);
        Assert.Equal("0x1.99999ap-4", a.HexFloat);

        // 00000001 は非正規化数。
        FloatAnalysis tiny = FloatAnalyzer.Analyze(FloatLayout.Single, FloatAnalyzer.ParseHex(FloatLayout.Single, "00000001")!.Value);
        Assert.Equal(FloatClass.Subnormal, tiny.Class);
        Assert.Equal("1E-45", tiny.Shortest.Replace("E-045", "E-45").Replace("E-0", "E-"));
        Assert.Equal("0x0.000002p-126", tiny.HexFloat);

        // 1.5 の指数部のビットを全部 1 にすると NaN、仮数部を全部 0 にすると無限大。
        UInt128 bits = FloatAnalyzer.ParseHex(FloatLayout.Single, "3FC00000")!.Value;
        for (int i = 23; i < 31; i++)
        {
            bits |= UInt128.One << i;
        }

        FloatAnalysis nan = FloatAnalyzer.Analyze(FloatLayout.Single, bits);
        Assert.Equal((FloatClass.NaN, true), (nan.Class, nan.IsQuietNaN));
        Assert.Equal("0x7FC00000", FloatAnalyzer.HexText(FloatLayout.Single, bits));
        for (int i = 0; i < 23; i++)
        {
            bits &= ~(UInt128.One << i);
        }

        FloatAnalysis inf = FloatAnalyzer.Analyze(FloatLayout.Single, bits);
        Assert.Equal((FloatClass.Infinity, false), (inf.Class, inf.Negative));
        Assert.Equal("0x7F800000", FloatAnalyzer.HexText(FloatLayout.Single, bits));
    }

    [Fact]
    public void ClassificationNeighboursAndUlpOfSeveralFormats()
    {
        FloatAnalysis zero = FloatAnalyzer.Analyze(FloatLayout.Double, 1UL << 63);
        Assert.Equal((FloatClass.Zero, true, "-0"), (zero.Class, zero.Negative, zero.Shortest));
        Assert.Equal("0x0p+0", zero.HexFloat.Replace("-", string.Empty));

        FloatAnalysis one = FloatAnalyzer.Analyze(FloatLayout.Double, 0x3FF0000000000000UL);
        Assert.Equal(("1", "1", "0x1p+0"), (one.Shortest, one.ExactDecimal, one.HexFloat));
        Assert.Equal("1.0000000000000002", one.NextUp);
        Assert.Equal("0.9999999999999999", one.NextDown);
        Assert.StartsWith("2^-52", one.Ulp, StringComparison.Ordinal);

        FloatAnalysis pi = FloatAnalyzer.Analyze(FloatLayout.Double, BitConverter.DoubleToUInt64Bits(Math.PI));
        Assert.Equal("3.141592653589793", pi.Shortest);
        Assert.Equal("0x1.921fb54442d18p+1", pi.HexFloat);

        FloatAnalysis half = FloatAnalyzer.Analyze(FloatLayout.Half, 0x3C00);
        Assert.Equal(("1", "1.0000000000", "0x1p+0"), (half.Shortest, "1.0000000000", half.HexFloat));
        Assert.Equal("1.001", half.NextUp);

        FloatAnalysis bf = FloatAnalyzer.Analyze(FloatLayout.BFloat16, 0x3FC0);
        Assert.Equal("1.5", bf.Shortest);

        FloatAnalysis max = FloatAnalyzer.Analyze(FloatLayout.Single, 0x7F7FFFFF);
        Assert.Equal("inf", max.NextUp);
        Assert.Equal("3.4028235E+38", max.Shortest);

        FloatAnalysis signaling = FloatAnalyzer.Analyze(FloatLayout.Single, 0x7F800001);
        Assert.Equal((FloatClass.NaN, false), (signaling.Class, signaling.IsQuietNaN));
        Assert.Equal(1UL, (ulong)signaling.NanPayload);
        Assert.Null(signaling.NextUpBits);

        FloatAnalysis negativeZeroUp = FloatAnalyzer.Analyze(FloatLayout.Single, 0x80000000);
        Assert.Equal(1UL, (ulong)negativeZeroUp.NextUpBits!.Value);
        Assert.Equal(0x80000001UL, (ulong)negativeZeroUp.NextDownBits!.Value);
    }

    [Fact]
    public void X87ExtendedPrecisionIsDecomposedWithTheExplicitIntegerBit()
    {
        // 1.0 = 3FFF 8000000000000000
        UInt128 one = ((UInt128)0x3FFF << 64) | ((UInt128)1 << 63);
        FloatAnalysis a = FloatAnalyzer.Analyze(FloatLayout.X87, one);
        Assert.Equal((FloatClass.Normal, "1", 0), (a.Class, a.Shortest, a.UnbiasedExponent));
        Assert.Equal("0x1p+0", a.HexFloat);
        Assert.Equal(80, a.BitString.Length);

        FloatRounding third = Parse(FloatLayout.X87, "0.1");
        FloatAnalysis t = FloatAnalyzer.Analyze(FloatLayout.X87, third.Bits);
        Assert.Equal("0.1", t.Shortest);
        Assert.Equal(FloatClass.Normal, t.Class);

        // 整数ビットが 0 で指数が 0 でない値は正しくない表現。
        UInt128 unnormal = (UInt128)0x3FFF << 64;
        Assert.Equal(FloatClass.Invalid, FloatAnalyzer.Analyze(FloatLayout.X87, unnormal | 1).Class);
        Assert.Equal(FloatClass.Infinity, FloatAnalyzer.Analyze(FloatLayout.X87, ((UInt128)0x7FFF << 64) | ((UInt128)1 << 63)).Class);
        Assert.Equal(10, FloatAnalyzer.ToBytes(FloatLayout.X87, one, bigEndian: false).Length);
        Assert.Equal(one, FloatAnalyzer.FromBytes(FloatLayout.X87, FloatAnalyzer.ToBytes(FloatLayout.X87, one, bigEndian: true), bigEndian: true));
    }

    [Fact]
    public void InputAndConversionBetweenFormats()
    {
        Assert.Equal(0x7F800000UL, (ulong)Parse(FloatLayout.Single, "inf").Bits);
        Assert.Equal(0xFF800000UL, (ulong)Parse(FloatLayout.Single, "-inf").Bits);
        Assert.Equal(0x7FC00000UL, (ulong)Parse(FloatLayout.Single, "nan").Bits);
        Assert.Equal(0x7F800000UL, (ulong)Parse(FloatLayout.Single, "1e39").Bits);
        Assert.Equal(0UL, (ulong)Parse(FloatLayout.Single, "1e-60").Bits);
        Assert.Equal(0x3C00UL, (ulong)Parse(FloatLayout.Half, "1").Bits);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(0.1), (ulong)Parse(FloatLayout.Double, "0.1").Bits);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(-2.5e-300), (ulong)Parse(FloatLayout.Double, "-2.5e-300").Bits);
        Assert.Equal("0", Parse(FloatLayout.Double, "0.5").Error);
        Assert.Equal("InvalidNumber", FloatAnalyzer.FromDecimalText(FloatLayout.Single, "1.2.3").Error!.Code);
        Assert.Equal("InvalidNumber", FloatAnalyzer.FromDecimalText(FloatLayout.Single, "").Error!.Code);

        // 0.1 (float) を double にすると正確に表せる(誤差 0)。double の 0.1 を float にすると丸めの誤差が出る。
        FloatRounding widened = FloatAnalyzer.ConvertTo(FloatLayout.Single, 0x3DCCCCCD, FloatLayout.Double);
        Assert.Equal("0", widened.Error);
        FloatRounding narrowed = FloatAnalyzer.ConvertTo(FloatLayout.Double, BitConverter.DoubleToUInt64Bits(0.1), FloatLayout.Single);
        Assert.Equal(0x3DCCCCCDUL, (ulong)narrowed.Bits);
        Assert.StartsWith("1.49011611383365", narrowed.Error, StringComparison.Ordinal);

        Assert.Equal(0x80000000UL, (ulong)FloatAnalyzer.ToggleBit(FloatLayout.Single, 0, 31));
        Assert.Equal(0UL, (ulong)FloatAnalyzer.ToggleBit(FloatLayout.Single, 0, 99));
        Assert.Equal((0, 1, 2), (FloatAnalyzer.SectionOf(FloatLayout.Single, 31), FloatAnalyzer.SectionOf(FloatLayout.Single, 30), FloatAnalyzer.SectionOf(FloatLayout.Single, 22)));
        Assert.Equal(new byte[] { 0xCD, 0xCC, 0xCC, 0x3D }, FloatAnalyzer.ToBytes(FloatLayout.Single, 0x3DCCCCCD, bigEndian: false));
        Assert.Null(FloatAnalyzer.FromBytes(FloatLayout.Single, new byte[3], false));
        Assert.Null(FloatAnalyzer.ParseHex(FloatLayout.Half, "12345"));
    }
}
