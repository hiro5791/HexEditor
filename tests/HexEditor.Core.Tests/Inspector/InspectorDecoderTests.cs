using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HexEditor.Core.Inspector;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>データインスペクタの解釈 (INSP-02、INSP-03、INSP-05、INSP-08、INSP-09、INSP-11、INSP-13)。</summary>
public sealed class InspectorDecoderTests
{
    internal static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    internal static InspectorOptions Opts(IntegerBase radix = IntegerBase.Decimal, DateTimeZoneMode zone = DateTimeZoneMode.Utc,
        DateTimeStyle style = DateTimeStyle.Regional, Encoding? ansi = null, TimeZoneInfo? local = null) => new()
    {
        IntegerBase = radix,
        Culture = EnUs,
        Language = "en-US",
        TimeZoneMode = zone,
        DateTimeStyle = style,
        AnsiEncoding = ansi,
        LocalTimeZone = local ?? TimeZoneInfo.Utc,
    };

    internal static string Text(string type, byte[] bytes, Endianness endian = Endianness.Little, InspectorOptions? options = null) =>
        InspectorDecoder.Decode(type, bytes, [], endian, options ?? Opts()).Text;

    internal static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Fact]
    [Trait(TC, "TC-INSP-02-01")]
    public void Endianness_and_integer_formats()
    {
        Assert.Equal("67,305,985", Text(InspectorTypes.UInt32, Hex("01 02 03 04")));
        Assert.Equal("16,909,060", Text(InspectorTypes.UInt32, Hex("01 02 03 04"), Endianness.Big));
        Assert.Equal("0xFFFF", Text(InspectorTypes.Int16, Hex("FF FF"), options: Opts(IntegerBase.Hexadecimal)));
        Assert.Equal("0x0000002A", Text(InspectorTypes.Int32, Hex("2A 00 00 00"), options: Opts(IntegerBase.Hexadecimal)));
        Assert.Equal("0o52", Text(InspectorTypes.Int32, Hex("2A 00 00 00"), options: Opts(IntegerBase.Octal)));
        Assert.Equal("0x80000000", Text(InspectorTypes.Int32, Hex("00 00 00 80"), options: Opts(IntegerBase.Hexadecimal)));

        // 桁区切りを使わない設定 (INSP-02 の仕様 5)。
        Assert.Equal("67305985", InspectorDecoder.Decode(InspectorTypes.UInt32, Hex("01 02 03 04"), [], Endianness.Little,
            Opts() with { DigitGrouping = false }).Text);
    }

    [Fact]
    [Trait(TC, "TC-INSP-03-01")]
    public void Integers_8_to_64_bit()
    {
        Assert.Equal("-1", Text(InspectorTypes.Int8, Hex("FF")));
        Assert.Equal("255", Text(InspectorTypes.UInt8, Hex("FF")));
        Assert.Equal("-2,147,483,648", Text(InspectorTypes.Int32, Hex("00 00 00 80")));
        Assert.Equal("18,446,744,073,709,551,615", Text(InspectorTypes.UInt64, Hex("FFFFFFFFFFFFFFFF")));
        Assert.Equal("-1", Text(InspectorTypes.Int64, Hex("FFFFFFFFFFFFFFFF")));

        Assert.Equal("127", Text(InspectorTypes.Int8, Hex("7F")));
        Assert.Equal("-128", Text(InspectorTypes.Int8, Hex("80")));
        Assert.Equal("32,767", Text(InspectorTypes.Int16, Hex("FF 7F")));
        Assert.Equal("-32,768", Text(InspectorTypes.Int16, Hex("00 80")));
        Assert.Equal("2,147,483,647", Text(InspectorTypes.Int32, Hex("FF FF FF 7F")));
        Assert.Equal("9,223,372,036,854,775,807", Text(InspectorTypes.Int64, Hex("FF FF FF FF FF FF FF 7F")));
        Assert.Equal("-9,223,372,036,854,775,808", Text(InspectorTypes.Int64, Hex("00 00 00 00 00 00 00 80")));

        // 無作為なバイト列を BinaryPrimitives の結果と比べる (10,000 個)。
        var random = new Random(0x1503);
        byte[] b = new byte[8];
        for (int i = 0; i < 10_000; i++)
        {
            random.NextBytes(b);
            foreach (Endianness e in new[] { Endianness.Little, Endianness.Big })
            {
                bool le = e == Endianness.Little;
                Check(InspectorTypes.Int8, ((sbyte)b[0]).ToString("N0", EnUs));
                Check(InspectorTypes.UInt8, b[0].ToString("N0", EnUs));
                Check(InspectorTypes.Int16, (le ? BinaryPrimitives.ReadInt16LittleEndian(b) : BinaryPrimitives.ReadInt16BigEndian(b)).ToString("N0", EnUs));
                Check(InspectorTypes.UInt16, (le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b)).ToString("N0", EnUs));
                Check(InspectorTypes.Int32, (le ? BinaryPrimitives.ReadInt32LittleEndian(b) : BinaryPrimitives.ReadInt32BigEndian(b)).ToString("N0", EnUs));
                Check(InspectorTypes.UInt32, (le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b)).ToString("N0", EnUs));
                Check(InspectorTypes.Int64, (le ? BinaryPrimitives.ReadInt64LittleEndian(b) : BinaryPrimitives.ReadInt64BigEndian(b)).ToString("N0", EnUs));
                Check(InspectorTypes.UInt64, (le ? BinaryPrimitives.ReadUInt64LittleEndian(b) : BinaryPrimitives.ReadUInt64BigEndian(b)).ToString("N0", EnUs));

                void Check(string type, string expected) => Assert.Equal(expected, Text(type, b, e));
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-INSP-05-01")]
    public void Float_and_double()
    {
        Assert.Equal("1", Text(InspectorTypes.Float32, Hex("00 00 80 3F")));
        Assert.Equal("∞", Text(InspectorTypes.Float64, Hex("00 00 00 00 00 00 F0 7F")));
        Assert.Equal("+0", Text(InspectorTypes.Float32, Hex("00 00 00 00")));
        Assert.Equal("-0", Text(InspectorTypes.Float32, Hex("00 00 00 80")));
        Assert.Equal("-∞", Text(InspectorTypes.Float32, Hex("00 00 80 FF")));
        Assert.Equal("NaN (0x7FC00000)", Text(InspectorTypes.Float32, Hex("00 00 C0 7F")));
        Assert.Equal("1E-45 (denormal)", Text(InspectorTypes.Float32, Hex("01 00 00 00")));

        // 「最短で正確」の表示を Invariant で読み直すと、元のビット列に戻る (NaN を除く)。
        var random = new Random(0x0501);
        byte[] b = new byte[8];
        for (int i = 0; i < 10_000; i++)
        {
            random.NextBytes(b);
            float f = BinaryPrimitives.ReadSingleLittleEndian(b);
            double d = BinaryPrimitives.ReadDoubleLittleEndian(b);
            if (!float.IsNaN(f))
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(f), BitConverter.SingleToInt32Bits(ParseShown(Text(InspectorTypes.Float32, b[..4]), float.Parse)));
            }

            if (!double.IsNaN(d))
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(d), BitConverter.DoubleToInt64Bits(ParseShown(Text(InspectorTypes.Float64, b), double.Parse)));
            }
        }

        static T ParseShown<T>(string shown, Func<string, IFormatProvider, T> parse)
        {
            string text = shown.Replace(" (denormal)", string.Empty, StringComparison.Ordinal);
            text = text switch { "+0" => "0", "∞" => "Infinity", "-∞" => "-Infinity", _ => text };
            return parse(text, CultureInfo.InvariantCulture);
        }
    }

    [Fact]
    public void Float_formats()
    {
        InspectorOptions hex = Opts() with { FloatFormat = FloatFormat.HexFloat };
        Assert.Equal("0x1.8p+1", InspectorDecoder.Decode(InspectorTypes.Float64, BitConverter.GetBytes(3.0), [], Endianness.Little, hex).Text);
        Assert.Equal("0x1p+0", InspectorDecoder.Decode(InspectorTypes.Float32, BitConverter.GetBytes(1.0f), [], Endianness.Little, hex).Text);
        Assert.Equal("1.5E+000", InspectorDecoder.Decode(InspectorTypes.Float64, BitConverter.GetBytes(1.5), [], Endianness.Little,
            Opts() with { FloatFormat = FloatFormat.Exponent }).Text);

        // 小数点は地域設定に従う。
        Assert.Equal("1,5", InspectorDecoder.Decode(InspectorTypes.Float64, BitConverter.GetBytes(1.5), [], Endianness.Little,
            Opts() with { Culture = CultureInfo.GetCultureInfo("de-DE") }).Text);
    }

    [Fact]
    public void Not_enough_data_and_loading()
    {
        InspectorValue v = InspectorDecoder.Decode(InspectorTypes.Int32, Hex("01 02"), [], Endianness.Little, Opts());
        Assert.Equal(InspectorStatus.NotEnoughData, v.Status);
        Assert.Equal("—", v.Text);
        Assert.Equal("Not enough data (4 bytes needed, 2 left)", v.ToolTip);
        Assert.Equal("513", Text(InspectorTypes.Int16, Hex("01 02")));

        InspectorValue loading = InspectorDecoder.Decode(InspectorTypes.Int16, Hex("01 02"),
            [Core.Engine.ByteState.Valid, Core.Engine.ByteState.Loading], Endianness.Little, Opts());
        Assert.Equal("…", loading.Text);
        InspectorValue unreadable = InspectorDecoder.Decode(InspectorTypes.Int16, Hex("01 02"),
            [Core.Engine.ByteState.Unreadable, Core.Engine.ByteState.Valid], Endianness.Little, Opts());
        Assert.Equal(InspectorStatus.Unreadable, unreadable.Status);
    }

    [Fact]
    public void Binary_rows()
    {
        Assert.Equal("0100 0001", Text(InspectorTypes.Binary8, Hex("41")));
        Assert.Equal("0000 0010 0000 0001", Text(InspectorTypes.Binary16, Hex("01 02")));
        Assert.Equal("0000 0001 0000 0010", Text(InspectorTypes.Binary16, Hex("01 02"), Endianness.Big));
        Assert.Equal([0x40], InspectorEncoder.FlipBit([0x41], 0, Endianness.Little));
        Assert.Equal([0x01, 0x82], InspectorEncoder.FlipBit([0x01, 0x02], 15, Endianness.Little));
        Assert.Equal([0x81, 0x02], InspectorEncoder.FlipBit([0x01, 0x02], 15, Endianness.Big));
    }

    [Fact]
    [Trait(TC, "TC-INSP-09-01")]
    public void Characters()
    {
        Assert.Equal("あ  U+3042  (3 bytes)", Text(InspectorTypes.Utf8, Hex("E3 81 82")));
        Assert.Equal("😀  U+1F600  (4 bytes)", Text(InspectorTypes.Utf16, Hex("3D D8 00 DE")));
        Assert.Equal("Invalid (continuation byte)", Text(InspectorTypes.Utf8, Hex("80")));

        InspectorOptions ascii = Opts(ansi: Encoding.ASCII);
        Assert.StartsWith("LF  U+000A", Text(InspectorTypes.Ansi, Hex("0A"), options: ascii));
        Assert.StartsWith("NUL  U+0000", Text(InspectorTypes.Ansi, Hex("00"), options: ascii));
        Assert.StartsWith("ESC  U+001B", Text(InspectorTypes.Ansi, Hex("1B"), options: ascii));
        Assert.Equal("Invalid (unpaired high surrogate)", Text(InspectorTypes.Utf16, Hex("00 D8 41 00")));
        Assert.Equal("Invalid (unpaired low surrogate)", Text(InspectorTypes.Utf16, Hex("00 DC 41 00")));

        // 不正な UTF-8 の並びの理由。
        Assert.Equal("Invalid (overlong encoding)", Text(InspectorTypes.Utf8, Hex("C0 80")));
        Assert.Equal("Invalid (missing continuation byte)", Text(InspectorTypes.Utf8, Hex("E3 41 82")));
        Assert.Equal("Invalid (incomplete sequence)", Text(InspectorTypes.Utf8, Hex("E3 81")));
        Assert.Equal("Invalid (surrogate code point)", Text(InspectorTypes.Utf8, Hex("ED A0 80")));
        Assert.Equal("Invalid (invalid lead byte)", Text(InspectorTypes.Utf8, Hex("F8 80 80 80")));
        Assert.Equal("A  U+0041  (1 byte)", Text(InspectorTypes.Utf8, Hex("41")));
        Assert.Equal("A  U+0041  (2 bytes)", Text(InspectorTypes.Utf16, Hex("00 41"), Endianness.Big));

        // 2 バイト文字コードの先行バイト (Shift_JIS) は 2 バイトで解釈する。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("あ  U+3042  (2 bytes)", Text(InspectorTypes.Ansi, Hex("82 A0"), options: Opts(ansi: Encoding.GetEncoding(932))));
        Assert.Equal("Invalid (not defined in the code page)", Text(InspectorTypes.Ansi, Hex("80"), options: ascii));
    }

    [Fact]
    public void Characters_no_font_can_show_are_shown_as_the_code_point_only()
    {
        // INSP-09 の仕様 3: 代替フォントでも表示できない文字は、コードポイントだけを表示する。
        InspectorOptions noGlyph = Opts() with { CanDisplay = cp => cp != 0x0378 };
        Assert.Equal("U+0378  (2 bytes)", Text(InspectorTypes.Utf8, Hex("CD B8"), options: noGlyph));
        Assert.Equal("U+0378  (2 bytes)", Text(InspectorTypes.Utf16, Hex("78 03"), options: noGlyph));
        Assert.Equal("あ  U+3042  (3 bytes)", Text(InspectorTypes.Utf8, Hex("E3 81 82"), options: noGlyph));

        // 制御文字は名前のまま (フォントを調べない)。
        Assert.StartsWith("LF  U+000A", Text(InspectorTypes.Utf8, Hex("0A"), options: Opts() with { CanDisplay = _ => false }));
    }

    [Fact]
    [Trait(TC, "TC-INSP-11-01")]
    public void Guid_and_uuid_components()
    {
        byte[] bytes = Hex("33 22 11 00 55 44 77 66 88 99 AA BB CC DD EE FF");
        Assert.Equal("{00112233-4455-6677-8899-AABBCCDDEEFF}", Text(InspectorTypes.Guid, bytes));
        Assert.Equal("33221100-5544-7766-8899-aabbccddeeff", Text(InspectorTypes.Uuid, bytes));

        // GUID はエンディアンの指定に従わない (INSP-02 の仕様 3)。
        Assert.Equal("{00112233-4455-6677-8899-AABBCCDDEEFF}", Text(InspectorTypes.Guid, bytes, Endianness.Big));

        // RFC 9562 の付録 A.1 (バージョン 1)。
        byte[] v1 = Hex("C2 32 AB 00 94 14 11 EC B3 C8 9F 6B DE CE D8 46");
        IReadOnlyList<InspectorComponent> c1 = Components(v1);
        Assert.Equal("1", Value(c1, "version"));
        Assert.Equal("RFC", Value(c1, "variant"));
        Assert.Equal("2022-02-22T19:22:22Z", Value(c1, "timestamp", style: DateTimeStyle.Iso8601, bytes: v1));
        Assert.Equal("2/22/2022 19:22:22 (UTC)", Value(c1, "timestamp"));
        Assert.Equal("0x33C8", Value(c1, "clockSequence"));
        Assert.Equal("9F-6B-DE-CE-D8-46 (random node)", Value(c1, "node"));

        // 付録 A.6 (バージョン 7)。
        byte[] v7 = Hex("01 7F 22 E2 79 B0 7C C3 98 C4 DC 0C 0C 07 39 8F");
        IReadOnlyList<InspectorComponent> c7 = Components(v7);
        Assert.Equal("7", Value(c7, "version"));
        Assert.Equal("2022-02-22T19:22:22Z", Value(c7, "unixTime", style: DateTimeStyle.Iso8601, bytes: v7));

        // マルチキャストビットが 0 のノード。
        byte[] mac = (byte[])v1.Clone();
        mac[10] = 0x9E;
        Assert.Equal("9E-6B-DE-CE-D8-46", Value(Components(mac), "node"));

        static IReadOnlyList<InspectorComponent> Components(byte[] b, DateTimeStyle style = DateTimeStyle.Regional) =>
            InspectorDecoder.Decode(InspectorTypes.Uuid, b, [], Endianness.Little, Opts(style: style)).Components!;

        static string Value(IReadOnlyList<InspectorComponent> list, string id, DateTimeStyle style = DateTimeStyle.Regional, byte[]? bytes = null) =>
            (bytes is null ? list : Components(bytes, style)).Single(c => c.Id == id).Value;
    }

    [Fact]
    [Trait(TC, "TC-INSP-13-01")]
    public void Unix_filetime_and_dos()
    {
        InspectorOptions iso = Opts(style: DateTimeStyle.Iso8601);
        Assert.Equal("1970-01-01T00:00:00Z", Text(InspectorTypes.Unix32, Hex("00 00 00 00"), options: iso));
        Assert.Equal("1970-01-01T00:00:00Z", Text(InspectorTypes.FileTime, Hex("00 80 3E D5 DE B1 9D 01"), options: iso));
        Assert.Equal("1980-01-01T00:00:00 (no time zone)", Text(InspectorTypes.DosDateTime, Hex("00 00 21 00"), options: iso));
        Assert.Equal("Invalid (month = 13)", Text(InspectorTypes.DosDate, Hex("A1 01"), options: iso));
        Assert.Equal("Invalid (month = 13)", Text(InspectorTypes.DosDateTime, Hex("00 00 A1 01"), options: iso));
        Assert.Equal("2038-01-19T03:14:07Z", Text(InspectorTypes.Unix32, Hex("FF FF FF 7F"), options: iso));
        Assert.Equal("1901-12-13T20:45:52Z", Text(InspectorTypes.Unix32, Hex("00 00 00 80"), options: iso));
        Assert.Equal("Out of range (9223372036854775807)", Text(InspectorTypes.Unix64, Hex("FF FF FF FF FF FF FF 7F"), options: iso));

        // 地域設定の書式 (en-US) と、タイムゾーンのない形式の表示。
        Assert.Equal("1/1/1970 00:00:00 (UTC)", Text(InspectorTypes.Unix32, Hex("00 00 00 00")));
        Assert.Equal("1/1/1980 00:00:00 (no time zone)", Text(InspectorTypes.DosDateTime, Hex("00 00 21 00")));
        Assert.Equal("2106-02-07T06:28:15Z", Text(InspectorTypes.Unix32U, Hex("FF FF FF FF"), options: iso));

        // 不正なフィールドの理由 (日・時・分・秒)。
        Assert.Equal("Invalid (day = 0)", Text(InspectorTypes.DosDate, Hex("20 00"), options: iso));
        Assert.Equal("Invalid (hour = 24)", Text(InspectorTypes.DosTime, Hex("00 C0"), options: iso));
        Assert.Equal("00:00:00 (no time zone)", Text(InspectorTypes.DosTime, Hex("00 00"), options: iso));

        // FILETIME の小数部は 7 桁まで (末尾の 0 は省く)。
        Assert.Equal("1970-01-01T00:00:00.0000001Z", Text(InspectorTypes.FileTime, BitConverter.GetBytes(0x019DB1DED53E8001L), options: iso));
    }

    [Fact]
    public void Local_time_zone_is_injectable()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("Test Tokyo", TimeSpan.FromHours(9), "Tokyo", "Tokyo");
        InspectorOptions local = Opts(zone: DateTimeZoneMode.Local, local: tokyo);
        Assert.Equal("1/1/1970 09:00:00 (UTC+09:00)", Text(InspectorTypes.Unix32, Hex("00 00 00 00"), options: local));
        Assert.Equal("1970-01-01T09:00:00+09:00", Text(InspectorTypes.Unix32, Hex("00 00 00 00"), options: local with { DateTimeStyle = DateTimeStyle.Iso8601 }));

        // タイムゾーンのない形式は切り替えても変わらない (INSP-15 の仕様 2)。
        Assert.Equal(Text(InspectorTypes.DosDateTime, Hex("00 00 21 00"), options: local),
            Text(InspectorTypes.DosDateTime, Hex("00 00 21 00"), options: local with { TimeZoneMode = DateTimeZoneMode.Utc }));
    }
}
