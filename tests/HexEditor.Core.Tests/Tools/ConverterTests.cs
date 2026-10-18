using System.Globalization;
using HexEditor.Core.Tools;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-24 基数・データ型の変換、TOOL-27 カラーピッカー。</summary>
public sealed class ConverterTests
{
    [Fact]
    [Trait(TC, "TC-TOOL-24-01")]
    public void EveryFieldConvertsToTheOthers()
    {
        var converter = new ValueConverter { BigEndian = true };
        Assert.Null(converter.SetFromField(ConverterField.UnsignedDecimal, "1078530011"));
        Assert.Equal("40 49 0F DB", converter.Get(ConverterField.Hex));
        Assert.Equal("3.1415927", converter.Get(ConverterField.Float));
        Assert.Equal("0x40490FDB", converter.Get(ConverterField.HexInteger));
        Assert.Equal("1078530011", converter.Get(ConverterField.SignedDecimal));
        Assert.Equal("0100 0000 0100 1001 0000 1111 1101 1011", converter.Get(ConverterField.Binary));
        Assert.Equal(string.Empty, converter.Get(ConverterField.Double));

        // エンディアンを切り替えると数値の解釈だけが変わる(バイト列は変わらない)。
        converter.BigEndian = false;
        Assert.Equal("40 49 0F DB", converter.Get(ConverterField.Hex));
        Assert.Equal("0xDB0F4940", converter.Get(ConverterField.HexInteger));

        Assert.Null(converter.SetFromField(ConverterField.Utf8, "あ"));
        Assert.Equal("E3 81 82", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.Utf16Le, "あ"));
        Assert.Equal("42 30", converter.Get(ConverterField.Hex));
        Assert.Equal("B0", converter.Get(ConverterField.Utf8));
        Assert.Equal("あ", converter.Get(ConverterField.Utf16Le));
        Assert.Equal("あ".Length, converter.Get(ConverterField.Utf16Be).Length);
        Assert.Equal("QjA=", converter.Get(ConverterField.Base64));

        converter.CodePageId = "cp37";
        Assert.Null(converter.SetFromField(ConverterField.Hex, "C1 C2 C3"));
        Assert.Equal("ABC", converter.Get(ConverterField.CodePage));
        Assert.Equal("\\xC1\\xC2\\xC3", converter.Get(ConverterField.Ascii));
    }

    [Fact]
    public void NumbersChooseTheSmallestSizeAndKeepTheCurrentOneIfItFits()
    {
        var converter = new ValueConverter { BigEndian = false };
        Assert.Null(converter.SetFromField(ConverterField.UnsignedDecimal, "5"));
        Assert.Equal("05", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.UnsignedDecimal, "70000"));
        Assert.Equal("70 11 01 00", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.UnsignedDecimal, "5"));
        Assert.Equal("05 00 00 00", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.SignedDecimal, "-2"));
        Assert.Equal("FE FF FF FF", converter.Get(ConverterField.Hex));
        Assert.Equal("-2", converter.Get(ConverterField.SignedDecimal));
        Assert.Equal("4294967294", converter.Get(ConverterField.UnsignedDecimal));
        Assert.Equal("37777777776", converter.Get(ConverterField.Octal));
        Assert.Null(converter.SetFromField(ConverterField.HexInteger, "0x1_0000_0000"));
        Assert.Equal("00 00 00 00 01 00 00 00", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.Binary, "1010 1010"));
        Assert.Equal("AA 00 00 00 00 00 00 00", converter.Get(ConverterField.Hex));

        Assert.Equal("OutOfRange", converter.SetFromField(ConverterField.UnsignedDecimal, "18446744073709551616")!.Code);
        Assert.Equal("InvalidNumber", converter.SetFromField(ConverterField.UnsignedDecimal, "12x")!.Code);
        Assert.Equal("InvalidNumber", converter.SetFromField(ConverterField.Octal, "9")!.Code);
        Assert.Equal("InvalidHex", converter.SetFromField(ConverterField.Hex, "ABC")!.Code);
        Assert.Equal("AA 00 00 00 00 00 00 00", converter.Get(ConverterField.Hex));

        converter.SetBytes(new byte[9]);
        Assert.Equal(string.Empty, converter.Get(ConverterField.UnsignedDecimal));
        converter.SetBytes(new byte[100]);
        Assert.Equal(ValueConverter.MaxBytes, converter.Bytes.Length);
    }

    [Fact]
    public void FloatsAndTextFieldsReportProblems()
    {
        var converter = new ValueConverter();
        Assert.Null(converter.SetFromField(ConverterField.Double, "1.5"));
        Assert.Equal("00 00 00 00 00 00 F8 3F", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.Half, "1"));
        Assert.Equal("00 3C", converter.Get(ConverterField.Hex));
        Assert.Equal("InvalidNumber", converter.SetFromField(ConverterField.Float, "abc")!.Code);
        Assert.Null(converter.SetFromField(ConverterField.Float, "-inf"));
        Assert.Equal("-Infinity", converter.Get(ConverterField.Float));

        // 変換できない文字は赤字で示し、バイト列は変えない。
        Assert.Null(converter.SetFromField(ConverterField.Hex, "41 42"));
        ConversionError? error = converter.SetFromField(ConverterField.Ascii, "Aあ");
        Assert.Equal(("Unencodable", "あ"), (error!.Code, error.Detail));
        Assert.Equal("41 42", converter.Get(ConverterField.Hex));
        Assert.Null(converter.SetFromField(ConverterField.Ascii, "A\\x80"));
        Assert.Equal("41 80", converter.Get(ConverterField.Hex));

        // 不正な UTF-8 は置換文字と警告で示す。
        Assert.True(converter.HasWarning(ConverterField.Utf8));
        Assert.Contains('�', converter.Get(ConverterField.Utf8));
        Assert.False(converter.HasWarning(ConverterField.Base64));
        converter.CodePageId = "cp932";
        Assert.Null(converter.SetFromField(ConverterField.CodePage, "あ"));
        Assert.Equal("82 A0", converter.Get(ConverterField.Hex));
        Assert.Equal("InvalidBase64", converter.SetFromField(ConverterField.Base64, "***")!.Code);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-27-01")]
    public void ColorBytesInEveryPixelFormat()
    {
        ColorValue orange = ColorFormats.ParseHex("#FF8000")!.Value;
        Assert.Equal([0x00, 0xFC], ColorFormats.Encode(orange, "rgb565", bigEndian: false));
        Assert.Equal([0xFC, 0x00], ColorFormats.Encode(orange, "rgb565", bigEndian: true));
        Assert.Equal([0xFF, 0x80, 0x00], ColorFormats.Encode(orange, "rgb888", false));
        Assert.Equal([0x00, 0x80, 0xFF], ColorFormats.Encode(orange, "bgr888", false));
        Assert.Equal([0xFF, 0x80, 0x00, 0xFF], ColorFormats.Encode(orange, "rgba8888", false));
        Assert.Equal([0xFF, 0xFF, 0x80, 0x00], ColorFormats.Encode(orange, "argb8888", false));
        Assert.Equal([0x00, 0x80, 0xFF, 0xFF], ColorFormats.Encode(orange, "bgra8888", false));
        Assert.Equal([0xFF, 0x00, 0x80, 0xFF], ColorFormats.Encode(orange, "abgr8888", false));
        Assert.Equal([0x1F, 0x04], ColorFormats.Encode(orange, "bgr565", false));
        Assert.Equal([0x00, 0x7E], ColorFormats.Encode(orange, "rgb555", false));
        Assert.Equal([0x00, 0xFE], ColorFormats.Encode(orange, "argb1555", false));
        Assert.Equal([0x0F, 0xF8], ColorFormats.Encode(orange, "rgba4444", false));
        Assert.Equal([0x80, 0xFF], ColorFormats.Encode(orange, "argb4444", false));
        Assert.Single(ColorFormats.Encode(orange, "gray8", false));
        Assert.Equal(13, ColorFormats.All.Count);
    }

    [Fact]
    public void ColorsRoundTripAndReadLikeTheInspector()
    {
        foreach (PixelFormatInfo format in ColorFormats.All)
        {
            foreach (bool big in new[] { false, true })
            {
                ColorValue source = new(0x12, 0xAB, 0xEF, 0xCC);
                byte[] bytes = ColorFormats.Encode(source, format.Id, big);
                Assert.Equal(format.Bytes, bytes.Length);
                ColorValue? back = ColorFormats.Decode(bytes, format.Id, big);
                Assert.NotNull(back);
                Assert.Equal(bytes, ColorFormats.Encode(back.Value, format.Id, big));
            }
        }

        // INSP-12 の解釈と同じ: RGB565 は上位のビットを下位に繰り返して広げる。
        ColorValue c = ColorFormats.Decode([0x00, 0xFC], "rgb565", bigEndian: false)!.Value;
        (byte r, byte g, byte b) = Inspector565(0xFC00);
        Assert.Equal((r, g, b), (c.R, c.G, c.B));
        Assert.Null(ColorFormats.Decode([1, 2], "rgb888", false));
    }

    private static (byte R, byte G, byte B) Inspector565(ushort value) => HexEditor.Core.Inspector.InspectorDecoder.ExpandRgb565(value);

    [Fact]
    [Trait(TC, "TC-TOOL-27-02")]
    public void FourBytesReadAsBgra8888GiveTheColourAndItsText()
    {
        ColorValue color = ColorFormats.Decode([0x00, 0x80, 0xFF, 0xFF], "bgra8888", false)!.Value;
        Assert.Equal("#FFFF8000", color.ToHex());
        Assert.Equal((255, 128, 0, 255), (color.R, color.G, color.B, color.A));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-27-03")]
    public void SearchBytesOfTheColourInTheChosenFormat()
    {
        byte[] bytes = ColorFormats.Encode(ColorFormats.ParseHex("#FF8000")!.Value, "bgra8888", false);
        Assert.Equal("00 80 FF FF", ColorFormats.BytesText(bytes));
        Assert.Equal(new ColorValue(1, 2, 3, 0x80), ColorFormats.ParseHex("80010203"));
        Assert.Null(ColorFormats.ParseHex("#12345"));
        Assert.Equal("#FF000000", new ColorValue(0, 0, 0).ToHex());
        _ = CultureInfo.InvariantCulture;
    }
}
