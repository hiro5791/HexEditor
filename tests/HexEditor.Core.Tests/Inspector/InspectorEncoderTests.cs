using System.Globalization;
using System.Text;
using HexEditor.Core.Inspector;
using static HexEditor.Core.Tests.Inspector.InspectorDecoderTests;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>インスペクタからの値の書き換え (INSP-17) の、入力から書き込むバイト列への変換。</summary>
public sealed class InspectorEncoderTests
{
    private static InspectorEncodeResult Encode(string type, string input, Endianness endian = Endianness.Little, InspectorOptions? o = null,
        long available = 16) =>
        InspectorEncoder.Encode(type, input, endian, o ?? Opts(), available);

    private static string Bytes(InspectorEncodeResult r) => Convert.ToHexString(r.Bytes ?? throw new Xunit.Sdk.XunitException(r.Error));

    [Fact]
    public void Integers_accept_expressions_and_check_the_range()
    {
        Assert.Equal("3412", Bytes(Encode(InspectorTypes.UInt16, "0x1234")));
        Assert.Equal("1234", Bytes(Encode(InspectorTypes.UInt16, "0x1234", Endianness.Big)));
        Assert.Equal("FFFF", Bytes(Encode(InspectorTypes.Int16, "-1")));
        Assert.Equal("1000", Bytes(Encode(InspectorTypes.UInt16, "0x100*4/64")));
        Assert.Equal("FFFFFFFFFFFFFFFF", Bytes(Encode(InspectorTypes.UInt64, "18446744073709551615")));
        Assert.Equal("FFFFFFFFFFFFFFFF", Bytes(Encode(InspectorTypes.UInt64, "0xFFFF_FFFF_FFFF_FFFF")));
        Assert.Equal("0080", Bytes(Encode(InspectorTypes.Int16, "-32768")));

        InspectorEncodeResult over = Encode(InspectorTypes.Int16, "32768");
        Assert.False(over.IsValid);
        Assert.Equal("Out of the int16 range (-32,768 to 32,767)", over.Error);
        Assert.False(Encode(InspectorTypes.UInt8, "-1").IsValid);
        Assert.False(Encode(InspectorTypes.UInt8, "abc!").IsValid);
    }

    [Fact]
    public void Float_input_rounds_to_nearest_and_reports_the_stored_value()
    {
        InspectorEncodeResult r = Encode(InspectorTypes.Float32, "0.1");
        Assert.Equal("CDCCCC3D", Bytes(r));
        Assert.Equal("0.100000001490116", r.StoredText);

        Assert.Null(Encode(InspectorTypes.Float32, "1").StoredText);
        Assert.Equal("0.10000000000000001", Encode(InspectorTypes.Float64, "0.1").StoredText);
        Assert.Null(Encode(InspectorTypes.Float64, "0.5").StoredText);
        Assert.Equal("0000803F", Bytes(Encode(InspectorTypes.Float32, "1e0")));
        Assert.Equal("0000807F", Bytes(Encode(InspectorTypes.Float32, "inf")));
        Assert.Equal("000080FF", Bytes(Encode(InspectorTypes.Float32, "-inf")));
        Assert.Equal("0000C07F", Bytes(Encode(InspectorTypes.Float32, "nan")));
        Assert.Equal("0000000000000840", Bytes(Encode(InspectorTypes.Float64, "0x1.8p+1")));
        Assert.Equal("00004040", Bytes(Encode(InspectorTypes.Float32, "0x1.8p+1")));

        // 範囲を超える値は無限大になる旨のエラー、地域設定の小数点 (カンマ) は受け付けない。
        Assert.Equal("The value is too large for float and would become infinity", Encode(InspectorTypes.Float32, "1e39").Error);
        Assert.False(Encode(InspectorTypes.Float64, "1,5").IsValid);
        Assert.False(Encode(InspectorTypes.Float64, "1.2.3").IsValid);

        // 16 進浮動小数点の偶数への丸め (float の仮数 24 bit の次の桁がちょうど半分)。
        Assert.Equal("0000803F", Bytes(Encode(InspectorTypes.Float32, "0x1.000001p+0")));
        Assert.Equal("0200803F", Bytes(Encode(InspectorTypes.Float32, "0x1.000003p+0")));
        Assert.Equal("0100803F", Bytes(Encode(InspectorTypes.Float32, "0x1.0000018p+0")));
    }

    [Fact]
    public void Hex_float_parsing_matches_the_decimal_value()
    {
        var random = new Random(0x1EEE);
        for (int i = 0; i < 2000; i++)
        {
            double d = BitConverter.Int64BitsToDouble(random.NextInt64());
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                continue;
            }

            string hex = NumberFormat.HexFloat((ulong)BitConverter.DoubleToInt64Bits(d), isFloat: false);
            Assert.Equal(d, NumberFormat.ParseFloat(hex, isFloat: false, out _));
        }
    }

    [Fact]
    public void Binary_guid_and_characters()
    {
        Assert.Equal("40", Bytes(Encode(InspectorTypes.Binary8, "0b0100 0000")));
        Assert.Equal("0102", Bytes(Encode(InspectorTypes.Binary16, "0000 0010 0000 0001")));
        Assert.False(Encode(InspectorTypes.Binary8, "1 0000 0000").IsValid);
        Assert.False(Encode(InspectorTypes.Binary8, "12").IsValid);

        Assert.Equal("33221100554477668899AABBCCDDEEFF", Bytes(Encode(InspectorTypes.Guid, "{00112233-4455-6677-8899-AABBCCDDEEFF}")));
        Assert.Equal("00112233445566778899AABBCCDDEEFF", Bytes(Encode(InspectorTypes.Uuid, "00112233445566778899aabbccddeeff")));
        Assert.Equal("Enter 32 hexadecimal digits", Encode(InspectorTypes.Uuid, "0011").Error);

        Assert.Equal("E38182", Bytes(Encode(InspectorTypes.Utf8, "あ")));
        Assert.Equal("3D4200DE".Length, Bytes(Encode(InspectorTypes.Utf16, "😀")).Length);
        Assert.Equal("3DD800DE", Bytes(Encode(InspectorTypes.Utf16, "😀")));
        Assert.Equal("41", Bytes(Encode(InspectorTypes.Ansi, "A", o: Opts(ansi: Encoding.ASCII))));
        Assert.False(Encode(InspectorTypes.Ansi, "あ", o: Opts(ansi: Encoding.ASCII)).IsValid);
        Assert.False(Encode(InspectorTypes.Utf8, "ab").IsValid);
    }

    [Fact]
    public void Writing_past_the_end_is_an_error()
    {
        InspectorEncodeResult r = Encode(InspectorTypes.Int32, "1", available: 2);
        Assert.False(r.IsValid);
        Assert.Equal("The value goes past the end of the document (4 bytes needed, 2 left)", r.Error);
        Assert.True(Encode(InspectorTypes.Int32, "1", available: 4).IsValid);
    }

    [Fact]
    public void Date_input_for_utc_formats()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("Test Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");
        InspectorOptions local = Opts(zone: DateTimeZoneMode.Local, local: tokyo);
        Assert.Equal("0040C1CBEE55DD01", Bytes(Encode(InspectorTypes.FileTime, "2026-10-07T00:00:00Z", o: local)));
        Assert.Equal("0040C1CBEE55DD01", Bytes(Encode(InspectorTypes.FileTime, "2026-10-07 09:00:00", o: local)));
        Assert.Equal("0040C1CBEE55DD01", Bytes(Encode(InspectorTypes.FileTime, "2026-10-07T09:00:00+09:00", o: local)));
        Assert.Equal("Enter the date and time in the form 2026-10-07 12:34:56", Encode(InspectorTypes.FileTime, "2026/10/07", o: local).Error);

        // 秒の形式に小数部を入力すると切り捨て、格納される値を示す。
        InspectorEncodeResult r = Encode(InspectorTypes.Unix32, "1970-01-01T00:00:01.5Z");
        Assert.Equal("01000000", Bytes(r));
        Assert.Equal("1/1/1970 00:00:01 (UTC)", r.StoredText);
        Assert.False(Encode(InspectorTypes.Unix32, "2100-01-01").IsValid);

        // now (固定した時刻)。
        var fixedTime = new FixedTime(new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("0040C1CBEE55DD01", Bytes(Encode(InspectorTypes.FileTime, "now", o: Opts() with { Time = fixedTime })));
    }

    [Fact]
    public void Dos_input_truncates_to_two_seconds()
    {
        InspectorEncodeResult r = Encode(InspectorTypes.DosDateTime, "1980-01-01 00:00:03");
        Assert.Equal("01002100", Bytes(r));
        Assert.Equal("1/1/1980 00:00:02 (no time zone)", r.StoredText);
        Assert.Null(Encode(InspectorTypes.DosDateTime, "1980-01-01 00:00:02").StoredText);
        Assert.Equal("Out of the dosdate range (1980-01-01 00:00:00 to 2107-12-31 23:59:58)", Encode(InspectorTypes.DosDate, "1979-12-31").Error);

        // タイムゾーンのない形式は、ローカル / UTC の切り替えに関係なく入力のとおり。
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("Test Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");
        Assert.Equal("00002100", Bytes(Encode(InspectorTypes.DosDateTime, "1980-01-01", o: Opts(zone: DateTimeZoneMode.Local, local: tokyo))));
        Assert.Equal("6F65", Bytes(Encode(InspectorTypes.DosTime, "12:43:30")));
    }

    [Fact]
    [Trait(TC, "TC-INSP-15-04")]
    public void Date_input_does_not_depend_on_the_regional_format()
    {
        // ドイツ語の地域設定でも ISO 8601 の入力を受け付け、地域設定の書式 (07.10.2026) は受け付けない。
        InspectorOptions german = Opts() with { Culture = CultureInfo.GetCultureInfo("de-DE") };
        InspectorEncodeResult r = Encode(InspectorTypes.Unix32, "2026-10-07 12:34:56", o: german);
        Assert.Equal("703CC66A", Bytes(r));
        string shown = InspectorDecoder.Decode(InspectorTypes.Unix32, r.Bytes, [], Endianness.Little, german).Text;
        Assert.Equal("07.10.2026 12:34:56 (UTC)", shown);
        Assert.False(Encode(InspectorTypes.Unix32, "07.10.2026 12:34:56", o: german).IsValid);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
