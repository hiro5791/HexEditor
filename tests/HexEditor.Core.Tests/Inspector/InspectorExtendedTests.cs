using System.Text;
using HexEditor.Core.Inspector;
using static HexEditor.Core.Tests.Inspector.InspectorDecoderTests;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>フェーズ 2 の型の解釈と書き換え (INSP-04、INSP-06、INSP-07、INSP-10、INSP-12、INSP-14)。</summary>
public sealed class InspectorExtendedTests
{
    private static InspectorValue Value(string type, byte[] bytes, Endianness endian = Endianness.Little, InspectorOptions? options = null) =>
        InspectorDecoder.Decode(type, bytes, [], endian, options ?? Opts());

    private static InspectorEncodeResult Encode(string type, string input, byte[]? current = null, Endianness endian = Endianness.Little,
        InspectorOptions? options = null) =>
        InspectorEncoder.Encode(type, input, endian, options ?? Opts(), 1 << 20, null, current ?? []);

    private static string T(string type, byte[] bytes, Endianness endian = Endianness.Little, InspectorOptions? options = null) =>
        InspectorDecoderTests.Text(type, bytes, endian, options);

    private static string HexOf(byte[]? bytes) => bytes is null ? "(null)" : Convert.ToHexString(bytes);

    // ---- INSP-04 ----

    [Fact]
    [Trait(TC, "TC-INSP-04-01")]
    public void Integers_24_48_128_bit()
    {
        Assert.Equal("8,388,607", T(InspectorTypes.Int24, Hex("FF FF 7F")));
        Assert.Equal("-8,388,608", T(InspectorTypes.Int24, Hex("00 00 80")));
        Assert.Equal("8,388,608", T(InspectorTypes.UInt24, Hex("00 00 80")));
        byte[] ff16 = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        Assert.Equal("-1", T(InspectorTypes.Int128, ff16));
        Assert.Equal("340,282,366,920,938,463,463,374,607,431,768,211,455", T(InspectorTypes.UInt128, ff16));

        // 境界値。
        Assert.Equal("140,737,488,355,327", T(InspectorTypes.Int48, Hex("FF FF FF FF FF 7F")));
        Assert.Equal("-140,737,488,355,328", T(InspectorTypes.Int48, Hex("00 00 00 00 00 80")));
        byte[] min128 = [.. new byte[15], 0x80];
        Assert.Equal("-170,141,183,460,469,231,731,687,303,715,884,105,728", T(InspectorTypes.Int128, min128));

        // エンディアンと表示形式 (INSP-04 の仕様 3 は INSP-03 と同じ)。
        Assert.Equal("8,388,607", T(InspectorTypes.Int24, Hex("7F FF FF"), Endianness.Big));
        Assert.Equal("0xFFFFFF", T(InspectorTypes.Int24, Hex("FF FF FF"), options: Opts(IntegerBase.Hexadecimal)));
        Assert.Equal("0x" + new string('F', 32), T(InspectorTypes.Int128, ff16, options: Opts(IntegerBase.Hexadecimal)));
        Assert.Equal("0o17", T(InspectorTypes.UInt128, [0x0F, .. new byte[15]], options: Opts(IntegerBase.Octal)));
        Assert.Equal(3, Value(InspectorTypes.Int24, Hex("FF FF")).ByteCount);
        Assert.Equal(InspectorStatus.NotEnoughData, Value(InspectorTypes.Int24, Hex("FF FF")).Status);
    }

    [Fact]
    public void Integers_24_48_128_bit_are_written()
    {
        Assert.Equal("FFFF7F", HexOf(Encode(InspectorTypes.Int24, "8388607").Bytes));
        Assert.Equal("000080", HexOf(Encode(InspectorTypes.Int24, "-8388608").Bytes));
        Assert.False(Encode(InspectorTypes.Int24, "8388608").IsValid);
        Assert.Equal(new string('F', 32), HexOf(Encode(InspectorTypes.UInt128, "340282366920938463463374607431768211455").Bytes));
        Assert.Equal("00000000000000000000000000000080", HexOf(Encode(InspectorTypes.Int128, "-170141183460469231731687303715884105728").Bytes));
        Assert.False(Encode(InspectorTypes.UInt128, "-1").IsValid);
        Assert.Equal("000000000001", HexOf(Encode(InspectorTypes.UInt48, "1", endian: Endianness.Big).Bytes));
    }

    // ---- INSP-06 ----

    [Fact]
    [Trait(TC, "TC-INSP-06-01")]
    public void Extended_floats_and_fixed_point()
    {
        Assert.Equal("1", T(InspectorTypes.Half, Hex("00 3C")));
        Assert.Equal("1", T(InspectorTypes.BFloat16, Hex("80 3F")));
        Assert.Equal("1", T(InspectorTypes.Float80, Hex("00 00 00 00 00 00 00 80 FF 3F")));
        Assert.Equal("1", T(InspectorTypes.Fixed16_16, Hex("00 00 01 00")));
        Assert.Equal("-0.5", T(InspectorTypes.Fixed8_8, Hex("80 FF")));
        Assert.Equal("Unnormal representation", T(InspectorTypes.Float80, Hex("00 00 00 00 00 00 00 00 FF 3F")));
        Assert.Equal(InspectorStatus.Invalid, Value(InspectorTypes.Float80, Hex("00 00 00 00 00 00 00 00 FF 3F")).Status);
        Assert.Equal("1", T(InspectorTypes.Real48, Hex("81 00 00 00 00 00")));

        // π に最も近い 80 bit の値は、同じ値に戻る最短の表記 (20 桁) で表示する (INSP-06 の仕様 2)。
        Assert.Equal("3.1415926535897932385", T(InspectorTypes.Float80, Hex("35 C2 68 21 A2 DA 0F C9 00 40")));

        // 値を元に戻せる最短の表記 (half の 0.1 に最も近い値は 0.1 と表示する)。
        Assert.Equal("0.1", T(InspectorTypes.Half, BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)0.1))));
        Assert.Equal("0.1", T(InspectorTypes.BFloat16, BitConverter.GetBytes(SmallFloats.ToBFloat16(0.1f))));

        // 特殊な値・非正規化数 (INSP-05 の仕様 2 と同じ表示)。
        Assert.Equal("∞", T(InspectorTypes.Half, Hex("00 7C")));
        Assert.Equal("NaN (0x7E00)", T(InspectorTypes.Half, Hex("00 7E")));
        Assert.Equal("-0", T(InspectorTypes.BFloat16, Hex("00 80")));
        Assert.Equal("6E-08 (denormal)", T(InspectorTypes.Half, Hex("01 00")));
        Assert.Equal("-∞", T(InspectorTypes.Float80, Hex("00 00 00 00 00 00 00 80 FF FF")));

        // 固定小数点は小数部のビット数から正確に表せる桁数まで表示する (16.16 なら小数点以下 16 桁まで)。
        Assert.Equal("0.0000152587890625", T(InspectorTypes.Fixed16_16, Hex("01 00 00 00")));
        Assert.Equal("-32,768", T(InspectorTypes.Fixed16_16, Hex("00 00 00 80")));
        Assert.Equal("255.99609375", T(new FixedPointFormat(false, 16, 8).Id, Hex("FF FF")));

        // 表示形式 (INSP-02 の仕様 6)。
        Assert.Equal("0x1.921fb54442d1846ap+1", T(InspectorTypes.Float80, Hex("35 C2 68 21 A2 DA 0F C9 00 40"),
            options: Opts() with { FloatFormat = FloatFormat.HexFloat }));
        Assert.Equal("1E+000", T(InspectorTypes.Half, Hex("00 3C"), options: Opts() with { FloatFormat = FloatFormat.Exponent }));
        Assert.Equal("0x1p+0", T(InspectorTypes.Half, Hex("00 3C"), options: Opts() with { FloatFormat = FloatFormat.HexFloat }));
    }

    [Fact]
    [Trait(TC, "TC-INSP-06-02")]
    public void Long_double_round_trips_through_the_shortest_text()
    {
        // 書き込んだ 80 bit の値と、表示の文字列を 80 bit の値に戻したものが一致する (INSP-06 の受け入れ基準 5)。
        InspectorEncodeResult written = Encode(InspectorTypes.Float80, "3.14159265358979323846");
        Assert.Equal("35C26821A2DA0FC90040", HexOf(written.Bytes));
        string shown = T(InspectorTypes.Float80, written.Bytes!);
        Assert.Equal("3.1415926535897932385", shown);
        Assert.Equal(HexOf(written.Bytes), HexOf(Encode(InspectorTypes.Float80, shown).Bytes));
        Assert.Equal("3.1415926535897932385", written.StoredText);

        // 無作為な 80 bit の値で、表示が元の値に戻る (最短で正確)。
        var random = new Random(0x8086);
        for (int i = 0; i < 2_000; i++)
        {
            byte[] b = new byte[10];
            random.NextBytes(b);
            b[7] |= 0x80;
            int exponent = random.Next(1, 0x7FFE);
            b[8] = (byte)exponent;
            b[9] = (byte)((exponent >> 8) | (random.Next(2) == 0 ? 0 : 0x80));
            string text = T(InspectorTypes.Float80, b);
            Assert.Equal(HexOf(b), HexOf(Encode(InspectorTypes.Float80, text).Bytes));
        }
    }

    [Fact]
    public void Extended_floats_and_fixed_point_are_written()
    {
        Assert.Equal("003C", HexOf(Encode(InspectorTypes.Half, "1").Bytes));
        Assert.Equal("803F", HexOf(Encode(InspectorTypes.BFloat16, "1").Bytes));
        Assert.Equal("810000000000", HexOf(Encode(InspectorTypes.Real48, "1").Bytes));
        Assert.Equal("00000100", HexOf(Encode(InspectorTypes.Fixed16_16, "1").Bytes));
        Assert.Equal("80FF", HexOf(Encode(InspectorTypes.Fixed8_8, "-0.5").Bytes));
        Assert.False(Encode(InspectorTypes.Fixed8_8, "128").IsValid);
        Assert.False(Encode(InspectorTypes.Half, "70000").IsValid);
        Assert.Equal("0.0999755859375", Encode(InspectorTypes.Half, "0.1").StoredText);
        Assert.Null(Encode(InspectorTypes.Half, "0.5").StoredText);
        Assert.Equal("0.1015625", Encode(InspectorTypes.Fixed8_8, "0.1").StoredText);
        Assert.Equal("0000000000000080FF3F", HexOf(Encode(InspectorTypes.Float80, "1").Bytes));
        Assert.Equal("000000000000C0FF7F", HexOf(Encode(InspectorTypes.Float80, "nan").Bytes)[2..]);
        Assert.Equal("6C3F", HexOf(Encode(InspectorTypes.Fixed8_8, "63.421875").Bytes));
    }

    [Fact]
    public void Fixed_point_formats_parse_and_layout_rows()
    {
        Assert.Equal(new FixedPointFormat(true, 32, 16), FixedPointFormat.Parse("fixed_s32_16"));
        Assert.Null(FixedPointFormat.Parse("fixed_s24_8"));
        Assert.Null(FixedPointFormat.Parse("fixed_s16_17"));
        InspectorLayout layout = InspectorLayout.Default.WithFixedRow(new FixedPointFormat(false, 8, 4));
        Assert.True(layout.Row("fixed_u8_4")!.Visible);
        InspectorLayout parsed = InspectorLayout.Parse(layout.Serialize());
        Assert.Equal(layout, parsed);
        Assert.Equal("1", T("fixed_u8_4", Hex("10")));

        // 最大 16 行 (INSP-06 の仕様 3)。
        for (int f = 0; f <= 16; f++)
        {
            layout = layout.WithFixedRow(new FixedPointFormat(true, 64, f));
        }

        Assert.Equal(InspectorTypes.MaxFixedRows, layout.FixedRowCount);
        Assert.Null(layout.WithoutRow("fixed_u8_4").Row("fixed_u8_4"));
        Assert.False(layout.WithoutRow(InspectorTypes.Fixed16_16).Row(InspectorTypes.Fixed16_16)!.Visible);
    }

    // ---- INSP-07 ----

    [Fact]
    [Trait(TC, "TC-INSP-07-01")]
    public void Leb128_and_sqlite_varint()
    {
        Assert.Equal("624,485 (3 bytes)", T(InspectorTypes.ULeb128, Hex("E5 8E 26")));
        Assert.Equal(3, Value(InspectorTypes.ULeb128, Hex("E5 8E 26 FF")).ByteCount);
        Assert.Equal("-123,456 (3 bytes)", T(InspectorTypes.SLeb128, Hex("C0 BB 78")));
        Assert.Equal("128 (2 bytes)", T(InspectorTypes.SqliteVarint, Hex("81 00")));
        Assert.Equal("128 (2 bytes)", T(InspectorTypes.SqliteVarint, Hex("81 00"), Endianness.Big));

        // 境界値: 10 バイト以内で終わらない、10 バイトで 64 bit を超える、10 バイトでちょうど 2^64 − 1。
        Assert.Equal("Exceeds 64 bit", T(InspectorTypes.ULeb128, [.. Enumerable.Repeat((byte)0x80, 10), 0x01]));
        Assert.Equal("Exceeds 64 bit", T(InspectorTypes.ULeb128, [.. Enumerable.Repeat((byte)0xFF, 9), 0x02]));
        Assert.Equal("18,446,744,073,709,551,615 (10 bytes)", T(InspectorTypes.ULeb128, [.. Enumerable.Repeat((byte)0xFF, 9), 0x01]));

        // 1 バイトの値、9 バイトの SQLite varint (9 バイト目は 8 bit すべて)、途中で終わるデータ。
        Assert.Equal("1 (1 byte)", T(InspectorTypes.ULeb128, Hex("01")));
        Assert.Equal("-1 (1 byte)", T(InspectorTypes.SLeb128, Hex("7F")));
        Assert.Equal("-1 (9 bytes)", T(InspectorTypes.SqliteVarint, Hex("FF FF FF FF FF FF FF FF FF")));
        Assert.Equal(InspectorStatus.NotEnoughData, Value(InspectorTypes.ULeb128, Hex("80 80")).Status);
    }

    [Fact]
    [Trait(TC, "TC-INSP-07-02")]
    public void Varints_are_written_with_the_same_length()
    {
        // 新しい値の方が短ければ、継続ビット付きの冗長な符号化で元の長さに合わせる (INSP-07 の仕様 5)。
        Assert.Equal("818000", HexOf(Encode(InspectorTypes.ULeb128, "1", Hex("E5 8E 26")).Bytes));
        InspectorEncodeResult longer = Encode(InspectorTypes.ULeb128, "2097152", Hex("E5 8E 26"));
        Assert.Null(longer.Bytes);
        Assert.Equal("The byte count would change, so the value can't be written (was 3 bytes, now 4 bytes)", longer.Error);
        Assert.Equal("The byte count would change, so the value can't be written (was 2 bytes, now 1 bytes)",
            Encode(InspectorTypes.SqliteVarint, "1", Hex("81 00")).Error);
        Assert.Equal("8100", HexOf(Encode(InspectorTypes.SqliteVarint, "128", Hex("81 00")).Bytes));
        Assert.Equal("FFFF7F", HexOf(Encode(InspectorTypes.SLeb128, "-1", Hex("C0 BB 78")).Bytes));
        Assert.Equal("C0BB78", HexOf(Encode(InspectorTypes.SLeb128, "-123456", Hex("C0 BB 78")).Bytes));
        Assert.Equal("E58E26", HexOf(Encode(InspectorTypes.ULeb128, "624485", Hex("E5 8E 26")).Bytes));
        Assert.Equal("FFFFFFFFFFFFFFFFFF", HexOf(Encode(InspectorTypes.SqliteVarint, "-1", Hex("FF FF FF FF FF FF FF FF FF")).Bytes));
    }

    // ---- INSP-10 ----

    [Fact]
    [Trait(TC, "TC-INSP-10-01")]
    public void Nul_terminated_and_pascal_strings()
    {
        Assert.Equal("\"Hello\" (5 chars, 6 bytes)", T(InspectorTypes.CStringAnsi, Hex("48 65 6C 6C 6F 00"), options: Opts(ansi: Encoding.Latin1)));
        Assert.Equal("\"Hello\" (5 chars, 6 bytes)", T(InspectorTypes.PString8Ansi, Hex("05 48 65 6C 6C 6F"), options: Opts(ansi: Encoding.Latin1)));
        Assert.Equal("No terminator (4 KB or more)", T(InspectorTypes.CStringAnsi, Enumerable.Repeat((byte)0x41, 4096).ToArray()));

        InspectorValue long300 = Value(InspectorTypes.CStringAnsi, [.. Enumerable.Repeat((byte)0x41, 300), 0], options: Opts(ansi: Encoding.Latin1));
        Assert.StartsWith("\"" + new string('A', 256) + "…\"", long300.Text);
        Assert.Equal(new string('A', 300), long300.FullText);

        // UTF-8・UTF-16 (エンディアンに従う)、2 / 4 バイトの長さ。
        Assert.Equal("\"あ\" (1 char, 4 bytes)", T(InspectorTypes.CStringUtf8, Hex("E3 81 82 00")));
        Assert.Equal("\"Hi\" (2 chars, 6 bytes)", T(InspectorTypes.CStringUtf16, Hex("00 48 00 69 00 00"), Endianness.Big));
        Assert.Equal("\"Hi\" (2 chars, 6 bytes)", T(InspectorTypes.PString32Utf8, Hex("02 00 00 00 48 69")));
        Assert.Equal("\"Hi\" (2 chars, 4 bytes)", T(InspectorTypes.PString16Utf8, Hex("00 02 48 69"), Endianness.Big));
        Assert.Equal("\"a\\nb\" (3 chars, 4 bytes)", T(InspectorTypes.CStringUtf8, Hex("61 0A 62 00")));
        Assert.Equal(InspectorStatus.NotEnoughData, Value(InspectorTypes.PString8Utf8, Hex("05 48 65")).Status);
        Assert.Equal("No terminator before the end of the data", T(InspectorTypes.CStringUtf8, Hex("41 42")));
    }

    [Fact]
    [Trait(TC, "TC-INSP-10-02")]
    public void Strings_are_written_within_the_original_length()
    {
        byte[] hello = Hex("48 65 6C 6C 6F 00");
        Assert.Equal("486900000000", HexOf(Encode(InspectorTypes.CStringAnsi, "\"Hi\"", hello, options: Opts(ansi: Encoding.Latin1)).Bytes));
        Assert.Equal("486900000000", HexOf(Encode(InspectorTypes.CStringAnsi, "Hi", hello, options: Opts(ansi: Encoding.Latin1)).Bytes));
        Assert.Equal("Longer than the original length (6 bytes)", Encode(InspectorTypes.CStringAnsi, "HelloWorld", hello).Error);
        Assert.Equal("024869000000", HexOf(Encode(InspectorTypes.PString8Utf8, "Hi", Hex("05 48 65 6C 6C 6F")).Bytes));
        Assert.Equal("610A00", HexOf(Encode(InspectorTypes.CStringUtf8, "\"a\\n\"", Hex("61 62 00")).Bytes));
        Assert.Equal("\"a\\nb\"", InspectorDecoder.EditText(InspectorTypes.CStringUtf8, Hex("61 0A 62 00"), [], Endianness.Little, Opts()));
        Assert.Equal("The original string has no terminator, so its length is unknown", Encode(InspectorTypes.CStringUtf8, "x", Hex("41 42")).Error);
    }

    // ---- INSP-12 ----

    [Fact]
    [Trait(TC, "TC-INSP-12-01")]
    public void Colors()
    {
        InspectorValue rgba = Value(InspectorTypes.Rgba8, Hex("FF 80 00 FF"));
        Assert.Equal("#FF8000FF  R 255, G 128, B 0, A 255", rgba.Text);
        Assert.Equal(0xFF8000FFu, rgba.Rgba);
        Assert.StartsWith("#0080FFFF", T(InspectorTypes.Bgra8, Hex("FF 80 00 FF")));
        Assert.Equal("#FF0000FF  R 255, G 0, B 0, A 255", T(InspectorTypes.Rgb565, Hex("00 F8")));
        Assert.Equal("#0000FFFF  R 0, G 0, B 255, A 255", T(InspectorTypes.Rgb565, Hex("1F 00")));
        Assert.Equal("#00FF00FF  R 0, G 255, B 0, A 255", T(InspectorTypes.Rgb565, Hex("E0 07")));
        Assert.StartsWith("#112233FF", T(InspectorTypes.Rgb8, Hex("11 22 33")));

        // 5 bit の 31 は 255、6 bit の 32 は 130 (INSP-12 の仕様 1)。
        Assert.Equal((byte)130, InspectorDecoder.ExpandRgb565(32 << 5).G);
    }

    [Fact]
    public void Colors_are_written()
    {
        Assert.Equal("FF8000FF", HexOf(Encode(InspectorTypes.Rgba8, "#FF8000FF").Bytes));
        Assert.Equal("0080FF80", HexOf(Encode(InspectorTypes.Bgra8, "#FF800080").Bytes));
        Assert.Equal("FF8000", HexOf(Encode(InspectorTypes.Rgb8, "FF8000").Bytes));
        Assert.Equal("00F8", HexOf(Encode(InspectorTypes.Rgb565, "#FF0000").Bytes));
        InspectorEncodeResult rounded = Encode(InspectorTypes.Rgb565, "#808080");
        Assert.Equal("#848284FF  R 132, G 130, B 132, A 255", rounded.StoredText);
        Assert.False(Encode(InspectorTypes.Rgba8, "red").IsValid);
        Assert.Equal("#FF8000FF", InspectorDecoder.EditText(InspectorTypes.Rgba8, Hex("FF 80 00 FF"), [], Endianness.Little, Opts()));
    }

    // ---- INSP-14 ----

    [Fact]
    [Trait(TC, "TC-INSP-14-01")]
    public void Extended_date_formats()
    {
        InspectorOptions iso = Opts(style: DateTimeStyle.Iso8601);
        Assert.Equal("1899-12-30T00:00:00 (no time zone)", T(InspectorTypes.OleDate, new byte[8], options: iso));
        Assert.Equal("2001-01-01T00:00:00Z", T(InspectorTypes.Cocoa, new byte[8], options: iso));
        Assert.Equal("1904-01-01T00:00:00Z", T(InspectorTypes.HfsPlus, new byte[4], options: iso));
        Assert.Equal("2026-10-07T12:34:56 (no time zone) (YYYYMMDDhhmmss)", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("20261007123456"), options: iso));
        Assert.Equal("1900-01-01T00:00:01 (no time zone)", T(InspectorTypes.SqlDateTime, Hex("00 00 00 00 2C 01 00 00"), options: iso));
        Assert.Equal("—", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("1234567"), options: iso));
        Assert.Equal("2026-10-07 (no time zone) (YYYYMMDD)", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("20261007"), options: iso));

        // 地域設定の書式 (en-US) とタイムゾーンの表示。
        Assert.Equal("12/30/1899 00:00:00 (no time zone)", T(InspectorTypes.OleDate, new byte[8]));
        Assert.Equal("1/1/1904 00:00:00 (UTC)", T(InspectorTypes.HfsPlus, new byte[4]));

        // 不正な日付は次に短い形式を試す (14 桁の月 13 → 13 桁の Unix ミリ秒)。
        Assert.EndsWith("(Unix milliseconds)", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("20261307123456"), options: iso));
        Assert.Equal("Invalid", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("20261307"), options: iso));
        Assert.Equal("1970-01-01T00:00:01Z (Unix seconds)", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("0000000001"), options: iso));
        Assert.Equal("1999-12-31T23:59:59 (no time zone) (YYMMDDhhmmss)", T(InspectorTypes.DigitsDateTime, Encoding.ASCII.GetBytes("991231235959"), options: iso));

        // その他の形式。
        Assert.Equal("1970-01-01T00:00:00.001Z", T(InspectorTypes.UnixMs, Hex("01 00 00 00 00 00 00 00"), options: iso));
        Assert.Equal("1970-01-01T00:00:00.0000001Z", T(InspectorTypes.Apfs, Hex("64 00 00 00 00 00 00 00"), options: iso));
        Assert.Equal("1900-01-01T00:01:00 (no time zone)", T(InspectorTypes.SqlSmallDateTime, Hex("00 00 01 00"), options: iso));
        Assert.Equal("0001-01-01T00:00:00Z", T(InspectorTypes.DotNetDateTime, Hex("00 00 00 00 00 00 00 40"), options: iso));
        Assert.Equal("0001-01-01T00:00:00 (local time)", T(InspectorTypes.DotNetDateTime, Hex("00 00 00 00 00 00 00 80"), options: iso));
        Assert.StartsWith("Out of range", T(InspectorTypes.SqlDateTime, Hex("00 00 00 00 FF FF FF FF"), options: iso));
        Assert.StartsWith("Out of range", T(InspectorTypes.OleDate, BitConverter.GetBytes(double.NaN), options: iso));

        // タイムゾーンのない形式は、ローカル / UTC を切り替えても表示が変わらない (INSP-15 の仕様 2)。
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
        InspectorOptions local = Opts(zone: DateTimeZoneMode.Local, style: DateTimeStyle.Iso8601, local: tokyo);
        Assert.Equal("1900-01-01T00:00:01 (no time zone)", T(InspectorTypes.SqlDateTime, Hex("00 00 00 00 2C 01 00 00"), options: local));
        Assert.Equal("1904-01-01T09:00:00+09:00", T(InspectorTypes.HfsPlus, new byte[4], options: local));
    }

    [Fact]
    public void Extended_dates_are_written()
    {
        InspectorOptions iso = Opts(style: DateTimeStyle.Iso8601);
        Assert.Equal("0000000000000000", HexOf(Encode(InspectorTypes.OleDate, "1899-12-30", options: iso).Bytes));
        Assert.Equal("00000000", HexOf(Encode(InspectorTypes.HfsPlus, "1904-01-01T00:00:00Z", options: iso).Bytes));
        Assert.Equal("000000002C010000", HexOf(Encode(InspectorTypes.SqlDateTime, "1900-01-01 00:00:01", options: iso).Bytes));
        Assert.Equal("00000100", HexOf(Encode(InspectorTypes.SqlSmallDateTime, "1900-01-01 00:01:30", options: iso).Bytes));
        Assert.NotNull(Encode(InspectorTypes.SqlSmallDateTime, "1900-01-01 00:01:30", options: iso).StoredText);
        Assert.Equal("0100000000000000", HexOf(Encode(InspectorTypes.UnixMs, "1970-01-01T00:00:00.001Z", options: iso).Bytes));
        Assert.Equal("6400000000000000", HexOf(Encode(InspectorTypes.Apfs, "1970-01-01T00:00:00.0000001Z", options: iso).Bytes));
        Assert.Equal("0000000000000000", HexOf(Encode(InspectorTypes.Cocoa, "2001-01-01T00:00:00Z", options: iso).Bytes));

        // 10 進の数字の日時は、元と同じ形式・桁数で書き込む (INSP-14 の仕様 4)。
        byte[] digits = Encoding.ASCII.GetBytes("20261007123456");
        Assert.Equal("20200101000000", Encoding.ASCII.GetString(Encode(InspectorTypes.DigitsDateTime, "2020-01-01", digits, options: iso).Bytes!));
        Assert.Equal("20200101", Encoding.ASCII.GetString(Encode(InspectorTypes.DigitsDateTime, "2020-01-01 10:00", Encoding.ASCII.GetBytes("20261007"), options: iso).Bytes!));
        Assert.Equal("The value needs 5 digits, but the original has 10",
            Encode(InspectorTypes.DigitsDateTime, "1970-01-02T00:00:00Z", Encoding.ASCII.GetBytes("1234567890"), options: iso).Error);

        // .NET の DateTime は元の種類を保つ。
        Assert.Equal("0000000000000040", HexOf(Encode(InspectorTypes.DotNetDateTime, "0001-01-01T00:00:00Z", Hex("00 00 00 00 00 00 00 40"), options: iso).Bytes));
        Assert.Equal("0000000000000080", HexOf(Encode(InspectorTypes.DotNetDateTime, "0001-01-01", Hex("00 00 00 00 00 00 00 80"), options: iso).Bytes));
    }

    // ---- INSP-19 (プリセット「すべて」) ----

    [Fact]
    public void Preset_all_contains_every_phase_2_type()
    {
        var shown = InspectorLayout.FromPreset(InspectorPreset.All).VisibleGroups().SelectMany(g => g.Rows).Select(r => r.TypeId).ToHashSet();
        foreach (string id in new[]
        {
            InspectorTypes.Int24, InspectorTypes.UInt128, InspectorTypes.Half, InspectorTypes.BFloat16, InspectorTypes.Float80, InspectorTypes.Real48,
            InspectorTypes.Fixed16_16, InspectorTypes.Fixed8_8, InspectorTypes.ULeb128, InspectorTypes.SLeb128, InspectorTypes.SqliteVarint,
            InspectorTypes.CStringAnsi, InspectorTypes.PString32Utf8, InspectorTypes.Rgba8, InspectorTypes.Rgb565, InspectorTypes.DigitsDateTime,
            InspectorTypes.DotNetDateTime,
        })
        {
            Assert.Contains(id, shown);
        }

        Assert.Equal(InspectorDecoder.MaxReadLength, InspectorLayout.FromPreset(InspectorPreset.All).ReadLength);
        Assert.Equal(InspectorDecoder.ReadLength, InspectorLayout.Default.ReadLength);
        var embedded = InspectorLayout.FromPreset(InspectorPreset.Embedded).VisibleGroups().SelectMany(g => g.Rows).Select(r => r.TypeId).ToHashSet();
        Assert.Contains(InspectorTypes.Fixed16_16, embedded);
        Assert.Contains(InspectorTypes.Half, embedded);
        Assert.DoesNotContain(InspectorTypes.Float32, embedded);
    }
}
