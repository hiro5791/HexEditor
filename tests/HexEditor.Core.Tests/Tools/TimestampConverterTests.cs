using System.Globalization;
using HexEditor.Core.Inspector;
using HexEditor.Core.Tools;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-28 日時の変換。</summary>
public sealed class TimestampConverterTests
{
    private static readonly InspectorOptions Utc = new()
    {
        TimeZoneMode = DateTimeZoneMode.Utc,
        Culture = CultureInfo.GetCultureInfo("en-US"),
        Language = "en-US",
        DateTimeStyle = DateTimeStyle.Regional,
    };

    private static TimestampConverter Converter(bool bigEndian = false) => new(Utc) { BigEndian = bigEndian };

    private static TimestampRow RowOf(TimestampConverter c, string id) => c.Row(TimestampFormats.Find(id)!);

    [Fact]
    [Trait(TC, "TC-TOOL-28-01")]
    public void FileTimeUnixAndDosConversions()
    {
        TimestampConverter c = Converter();
        Assert.Null(c.SetFromText("2000-01-01T00:00:00Z"));
        Assert.Equal("0x01BF53EB256D4000", RowOf(c, "filetime").Hex);
        Assert.Equal("946684800", RowOf(c, "unix32").Decimal);

        Assert.Null(c.SetFromNumber(TimestampFormats.Find("unix32")!, "0"));
        Assert.Equal("1970-01-01T00:00:00Z", c.Iso());

        Assert.Null(c.SetFromText("1979-12-31T23:59:59Z"));
        TimestampRow dos = RowOf(c, "dos");
        Assert.False(dos.Representable);
        Assert.Empty(dos.Bytes);
        Assert.True(RowOf(c, "filetime").Representable);
    }

    [Fact]
    public void RangesAndEndianness()
    {
        TimestampConverter c = Converter();
        c.SetFromText("2040-01-01T00:00:00Z");
        Assert.False(RowOf(c, "unix32").Representable); // 2038 年以降
        Assert.True(RowOf(c, "unix32u").Representable);
        Assert.True(RowOf(c, "hfsplus").Representable);
        c.SetFromText("2107-12-31T23:59:58Z");
        Assert.True(RowOf(c, "dos").Representable);
        c.SetFromText("2108-01-01T00:00:00Z");
        Assert.False(RowOf(c, "dos").Representable);

        c.SetFromText("2024-02-29T12:34:56Z");
        TimestampRow dosLe = RowOf(c, "dos");
        Assert.Equal(new byte[] { 0x5C, 0x64, 0x5D, 0x58 }, dosLe.Bytes);
        c.BigEndian = true;
        Assert.Equal(new byte[] { 0x64, 0x5C, 0x58, 0x5D }, RowOf(c, "dos").Bytes);
        Assert.Equal(new byte[] { 0x66, 0xE0, 0x13, 0xF0 }.Length, RowOf(c, "unix32").Bytes.Length);
        Assert.Equal(1709210096L, BitConverter.ToInt32(RowOf(c, "unix32").Bytes.Reverse().ToArray()));
    }

    [Fact]
    public void EveryInputFieldUpdatesTheOthers()
    {
        TimestampConverter c = Converter();
        Assert.Null(c.SetFromNumber(TimestampFormats.Find("unixms")!, "1709210096789"));
        Assert.Equal("2024-02-29T12:34:56.789Z", c.Iso());
        Assert.Equal("1709210096789000000", RowOf(c, "unixns").Decimal);
        Assert.Equal("1709210096789000", RowOf(c, "unixus").Decimal);
        Assert.Equal("1709210096", RowOf(c, "unix64").Decimal);
        Assert.Equal("3792054896", RowOf(c, "hfsplus").Decimal);
        Assert.Equal("1393245314", RowOf(c, "gps").Decimal);
        Assert.Equal("133536836967890000", RowOf(c, "filetime").Decimal);
        Assert.Equal("730902896.789", RowOf(c, "cocoa").Decimal);
        Assert.Equal(new DateTime(2024, 2, 29, 12, 34, 56, 789).Ticks.ToString(CultureInfo.InvariantCulture), RowOf(c, "dotnet").Decimal);
        Assert.Equal("2/29/2024 12:34:56.789 (UTC)", c.Regional());

        Assert.Null(c.SetFromNumber(TimestampFormats.Find("filetime")!, "0x01BF53EB256D4000"));
        Assert.Equal("2000-01-01T00:00:00Z", c.Iso());
        Assert.Null(c.SetFromHexBytes(TimestampFormats.Find("unix32")!, "80 51 01 00"));
        Assert.Equal("1970-01-02T00:00:00Z", c.Iso());
        Assert.Equal("OutOfRange", c.SetFromNumber(TimestampFormats.Find("unix32")!, "99999999999")!.Code);
        Assert.Equal("InvalidNumber", c.SetFromNumber(TimestampFormats.Find("unix32")!, "abc")!.Code);
        Assert.Equal("InvalidDate", c.SetFromText("yesterday")!.Code);
        Assert.Equal("InvalidHex", c.SetFromHexBytes(TimestampFormats.Find("unix32")!, "01")!.Code);

        // 地域設定の書式と ISO 8601 の両方、タイムゾーン付きの入力。
        Assert.Null(c.SetFromText("2024-02-29T21:34:56+09:00"));
        Assert.Equal("2024-02-29T12:34:56Z", c.Iso());
        Assert.Equal("1709210096", RowOf(c, "unix64").Decimal);
        c.SetNow();
        Assert.True(c.HasZone);
    }

    [Fact]
    public void LocalTimeZoneChangesWallClockFormatsAndTheDisplay()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
        var c = new TimestampConverter(Utc with { TimeZoneMode = DateTimeZoneMode.Local, LocalTimeZone = tokyo });
        Assert.Null(c.SetFromText("2024-02-29T21:34:56"));
        Assert.Equal("2024-02-29T21:34:56+09:00", c.Iso());
        Assert.Equal("1709210096", RowOf(c, "unix64").Decimal);

        // DOS は現地の日時を持つので、日時の 21:34:56 のまま。
        Assert.Equal(new byte[] { 0x5C, 0xAC, 0x5D, 0x58 }, RowOf(c, "dos").Bytes);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-28-02")]
    public void ReadingTheSameBytesShowsTheSameTextAsTheInspector()
    {
        string path = TestDataCatalog.Get("TD-TOOL-TIMESTAMPS");
        byte[] data = File.ReadAllBytes(path);
        var options = Utc with { Culture = CultureInfo.GetCultureInfo("en-US") };
        for (int slot = 0; slot < TestDataCatalog.ToolTimestampSlots.Count; slot++)
        {
            TimestampFormat format = TimestampFormats.Find(TestDataCatalog.ToolTimestampSlots[slot])!;
            ReadOnlySpan<byte> bytes = data.AsSpan(slot * 16, format.Bytes);
            var c = new TimestampConverter(options);
            Assert.Null(c.SetFromBytes(format, bytes));
            string shown = c.Regional();

            // 同じバイト列を、インスペクタで同じ型として読んだ表示と比べる。
            if (format.InspectorTypeId is { } typeId)
            {
                InspectorValue value = InspectorDecoder.Decode(InspectorTypes.Get(typeId), bytes, [], Endianness.Little, options);
                Assert.Equal(value.Text, shown);
            }
            else
            {
                Assert.Equal("2/29/2024 12:34:56.789 (UTC)".Replace(".789", string.Empty), shown.Replace(".789", string.Empty));
            }

            // どの形式でも、元の日時に戻る(精度は形式の分)。
            Assert.StartsWith("2024-02-29T12:34:56", c.Iso(), StringComparison.Ordinal);
        }
    }
}
