using System.Globalization;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>VIEW-40 ステータスバーの値の書式。</summary>
public sealed class StatusFormatTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData(0L, 8)]
    [InlineData(0xFFFFFFFFL, 8)]
    [InlineData(0x100000000L, 9)]
    [InlineData(100L * 1024 * 1024 * 1024, 10)]
    [InlineData(long.MaxValue, 16)]
    public void HexDigitsAreAtLeastEight(long max, int digits) => Assert.Equal(digits, StatusFormat.HexDigits(max));

    [Fact]
    public void OffsetIsZeroPadded() => Assert.Equal("0x00001F00", StatusFormat.Offset(0x1F00, 8));

    [Fact]
    public void SizeUsesBinaryUnitsAndCulture()
    {
        Assert.Equal("1.50 GB", StatusFormat.ShortSize(1_610_612_736, English));
        Assert.Equal("1,50 GB", StatusFormat.ShortSize(1_610_612_736, German));
        Assert.Equal("1.610.612.736", StatusFormat.Number(1_610_612_736, German));
        Assert.Null(StatusFormat.ShortSize(1023, English));
        Assert.Equal("1.00 KB", StatusFormat.ShortSize(1024, English));
        Assert.Equal("1.00 MB", StatusFormat.ShortSize(1024 * 1024 - 1, English));
        Assert.Equal("8.00 EB", StatusFormat.ShortSize(long.MaxValue, English));
    }

    [Fact]
    public void ByteValueIsHexAndUnsignedDecimal() => Assert.Equal(("4F", "79"), StatusFormat.ByteValue(0x4F, English));
}
