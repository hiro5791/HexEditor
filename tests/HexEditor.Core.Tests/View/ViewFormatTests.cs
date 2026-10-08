using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>
/// 表示の書式 (VIEW-05 列見出し、VIEW-08 1 行のバイト数、VIEW-09 グループ化、VIEW-12 大文字・小文字、VIEW-19 オフセットの基数、
/// VIEW-20 ベースアドレス・基準点、VIEW-42 表示設定の JSON)。
/// </summary>
public sealed class ViewFormatTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // ---- VIEW-19 ----

    [Fact]
    [Trait(TC, "TC-VIEW-19-02")]
    public void Sector_format_is_decimal_sector_and_hex_position()
    {
        Assert.Equal("15:100", OffsetFormat.Sector(0x1F00, 512));
        Assert.Equal("0:0", OffsetFormat.Sector(0, 512));
        Assert.Equal("0:1FF", OffsetFormat.Sector(0x1FF, 512));
        Assert.Equal("1:0", OffsetFormat.Sector(0x200, 512));
        Assert.Equal("8388608:0", OffsetFormat.Sector(1UL << 32, 512));
        Assert.Equal("18014398509481983:1FF", OffsetFormat.Sector(long.MaxValue, 512));
        Assert.Equal("1:F00", OffsetFormat.Sector(0x1F00, 4096));

        // 書式の部品からも同じ (オフセット列は右寄せ)。
        var format = new OffsetFormat(ViewSettings.Default with { Radix = OffsetRadix.Sector }, 0x10000, 512);
        Assert.Equal("15:100", format.Column(0x1F00).Trim());
        Assert.Equal("15:100", format.Status(0x1F00, English));
    }

    [Fact]
    public void Hex_column_has_at_least_eight_digits_and_grows_with_the_length()
    {
        var format = new OffsetFormat(ViewSettings.Default, 1024 * 1024, 512);
        Assert.Equal("00001F00", format.Column(0x1F00));
        Assert.Equal("0x00001F00", format.Status(0x1F00, English));

        // 100 GiB − 1 = 0x18_FFFF_FFFF は 10 桁 (TC-VIEW-19-03)。
        var big = new OffsetFormat(ViewSettings.Default, 100L * 1024 * 1024 * 1024, 512);
        Assert.Equal(10, big.ColumnWidth);
        Assert.Equal("0000000000", big.Column(0));
        var dec = new OffsetFormat(ViewSettings.Default with { Radix = OffsetRadix.Decimal }, 100L * 1024 * 1024 * 1024, 512);
        Assert.Equal(12, dec.ColumnWidth);

        // 桁数は増えるときだけ変わる (VIEW-19 の仕様 3)。
        Assert.False(format.Grow(10));
        Assert.True(format.Grow(0x1_0000_0000));
        Assert.Equal(9, format.Digits);
    }

    [Fact]
    public void Decimal_column_is_right_aligned_without_separators_and_status_uses_the_culture()
    {
        var format = new OffsetFormat(ViewSettings.Default with { Radix = OffsetRadix.Decimal }, 1024 * 1024, 512);
        Assert.Equal("   7936", format.Column(7936));
        Assert.Equal("     16", format.Column(16));
        Assert.Equal("7,936", format.Status(7936, English));
        Assert.Equal("7.936", format.Status(7936, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Octal_and_digit_separator()
    {
        var octal = new OffsetFormat(ViewSettings.Default with { Radix = OffsetRadix.Octal }, 1024, 512);
        Assert.Equal("00017400", octal.Column(0x1F00));
        var separated = new OffsetFormat(ViewSettings.Default with { HexDigitSeparator = true }, 1024, 512);
        Assert.Equal("0000:1F00", separated.Column(0x1F00));
    }

    [Fact]
    public void Lowercase_hex_keeps_the_0x_prefix()
    {
        var format = new OffsetFormat(ViewSettings.Default with { LowercaseHex = true }, 1024 * 1024, 512);
        Assert.Equal("000abcd0", format.Column(0xABCD0));
        Assert.Equal("0x000abcde", format.Status(0xABCDE, English));
        Assert.Equal("0xff", OffsetFormat.Hex(0xFF, lowercase: true));
    }

    // ---- VIEW-20 ----

    [Fact]
    public void Base_address_is_added_and_marked_with_at_sign()
    {
        var format = new OffsetFormat(ViewSettings.Default with { BaseAddress = 0x400000 }, 256, 512);
        Assert.Equal("00400000", format.Column(0));
        Assert.Equal("@00400000", format.Status(0, English));

        // 2^64 − 1 まで表せる。
        var top = new OffsetFormat(ViewSettings.Default with { BaseAddress = 0xFFFFFFFFFFFFFEFF }, 256, 512);
        Assert.Equal("FFFFFFFFFFFFFFF0", top.Column(0xF1));
        Assert.Equal("@FFFFFFFFFFFFFFFF", top.Status(256, English));
    }

    [Fact]
    public void Reference_point_shows_signed_relative_offsets()
    {
        var format = new OffsetFormat(ViewSettings.Default, 256, 512, referencePoint: 0x40);
        Assert.Equal("-00000010", format.Column(0x30));
        Assert.Equal("+00000000", format.Column(0x40));
        Assert.Equal("+00000020", format.Column(0x60));
        Assert.Equal("+00000000", format.Status(0x40, English));
    }

    [Fact]
    public void Rows_align_to_the_base_address()
    {
        ViewSettings settings = ViewSettings.Default with { BaseAddress = 0x401004 };
        Assert.Equal(4, settings.EffectiveRowShift(16));
        var layout = new HexLayout(16, 256, CanResize: true, RowShift: 4);
        Assert.Equal(-4, layout.RowStart(0));
        Assert.Equal(12, layout.RowStart(1));
        Assert.Equal(1, layout.RowOf(12));
        Assert.Equal(0, layout.RowOf(11));
        Assert.Equal(4, layout.ColumnOf(0));
        Assert.Equal(17, layout.TotalRows);

        // 2^63 − 1 の長さでも桁あふれしない。
        var max = new HexLayout(16, long.MaxValue, CanResize: false, RowShift: 15);
        Assert.Equal((long)(((ulong)long.MaxValue - 1 + 15) / 16), max.LastRow);
        Assert.True(max.RowStart(max.LastRow) <= max.MaxCursor);

        // そろえない設定では、指定のずれを使う。
        Assert.Equal(3, (ViewSettings.Default with { AlignRowsToAddress = false, RowShift = 3 }).EffectiveRowShift(16));
    }

    // ---- VIEW-08、VIEW-09 ----

    [Fact]
    public void Bytes_per_row_must_be_in_range_and_a_multiple_of_the_group()
    {
        Assert.Null(ViewSettings.Default.ValidateBytesPerRow(1));
        Assert.Null(ViewSettings.Default.ValidateBytesPerRow(4096));
        Assert.Equal(BytesPerRowError.OutOfRange, ViewSettings.Default.ValidateBytesPerRow(4097));
        Assert.Equal(BytesPerRowError.OutOfRange, ViewSettings.Default.ValidateBytesPerRow(0));
        Assert.Equal(BytesPerRowError.NotMultipleOfGroup, (ViewSettings.Default with { GroupSize = 8 }).ValidateBytesPerRow(12));
    }

    [Fact]
    public void Larger_group_rounds_bytes_per_row_up()
    {
        ViewSettings settings = (ViewSettings.Default with { GroupSize = 4, BytesPerRow = 12 }).WithGroupSize(8, out int? rounded);
        Assert.Equal(16, settings.BytesPerRow);
        Assert.Equal(16, rounded);
        (ViewSettings.Default with { BytesPerRow = 16 }).WithGroupSize(4, out int? same);
        Assert.Null(same);
    }

    [Fact]
    public void Auto_fit_is_the_largest_multiple_that_fits()
    {
        ViewSettings settings = ViewSettings.Default with { GroupSize = 4, AutoBytesPerRow = true };
        Assert.Equal(36, settings.AutoFit(n => n <= 37));
        Assert.Equal(4, settings.AutoFit(_ => false));
        Assert.Equal(4096, settings.AutoFit(_ => true));
        Assert.Equal(32, (settings with { AutoPowerOfTwo = true }).AutoFit(n => n <= 37));
    }

    [Fact]
    public void Row_format_groups_bytes_and_keeps_text_spacing()
    {
        var one = new RowFormat(16, 1, MiddleSeparator: true);
        Assert.Equal(0, one.HexIndex(0));
        Assert.Equal(3, one.HexIndex(1));
        Assert.Equal(25, one.HexIndex(8));
        Assert.Equal(48, one.HexWidth);
        Assert.Equal(50, one.TextIndex(0));

        // グループ化 4: `DEADBEEF 01020304`。
        var four = new RowFormat(16, 4, MiddleSeparator: true);
        Assert.Equal(0, four.HexIndex(0));
        Assert.Equal(2, four.HexIndex(1));
        Assert.Equal(9, four.HexIndex(4));
        Assert.Equal(19, four.HexIndex(8));
        Assert.Equal(8, four.GroupWidth(0));
        Assert.Equal(1, four.GroupOf(4));
        Assert.Equal(4, four.ByteAtHexIndex(10));

        // テキスト列の間隔はグループ化によらない (TC-VIEW-09-02)。
        foreach (int g in ViewSettings.GroupSizes)
        {
            var f = new RowFormat(16, g, MiddleSeparator: g < 8);
            Assert.Equal(1, f.TextIndex(1) - f.TextIndex(0));
        }

        // 列の表示・非表示 (VIEW-16)。
        Assert.Equal(16, new RowFormat(16, 1, false, ShowHex: false).LineLength);
        Assert.Equal(new RowFormat(16, 1, true).HexWidth, new RowFormat(16, 1, true, ShowText: false).LineLength);
    }

    [Fact]
    public void Ruler_labels_use_the_low_digits()
    {
        Assert.Equal("0F", OffsetFormat.RulerLabel(15, OffsetRadix.Hex, false, 2));
        Assert.Equal("0f", OffsetFormat.RulerLabel(15, OffsetRadix.Hex, true, 2));
        Assert.Equal("15", OffsetFormat.RulerLabel(15, OffsetRadix.Decimal, false, 2));
        Assert.Equal("17", OffsetFormat.RulerLabel(0x17, OffsetRadix.Hex, false, 2));
        Assert.Equal("F", OffsetFormat.RulerLabel(0x1F, OffsetRadix.Hex, false, 1));
    }

    // ---- VIEW-42 ----

    [Fact]
    public void Settings_round_trip_through_json_and_ignore_unknown_keys()
    {
        ViewSettings settings = ViewSettings.Default with
        {
            BytesPerRow = 32, GroupSize = 4, Radix = OffsetRadix.Decimal, BaseAddress = 0x1000, LowercaseHex = true,
        };
        JsonObject json = settings.ToJson();
        json["futureOption"] = new JsonObject { ["x"] = 1 };
        Assert.Equal(settings, ViewSettings.FromJson(json));

        // ドキュメント固有の項目は既定に含めない (VIEW-42 の仕様 4)。
        JsonObject defaults = settings.ToJson(includeDocumentSpecific: false);
        Assert.False(defaults.ContainsKey("baseAddress"));
        Assert.Equal(0UL, ViewSettings.FromJson(defaults).BaseAddress);

        // 型の合わない項目は無視し、ほかの項目は読む。
        var broken = new JsonObject { ["bytesPerRow"] = "many", ["groupSize"] = 8 };
        ViewSettings read = ViewSettings.FromJson(broken);
        Assert.Equal(16, read.BytesPerRow);
        Assert.Equal(8, read.GroupSize);
    }
}
