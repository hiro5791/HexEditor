using System.Globalization;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>セルの表示形式 (VIEW-10) と、行の中の位置 (VIEW-10・VIEW-11・VIEW-24 の RowFormat)。</summary>
public sealed class CellFormatTests
{
    private static string F(CellFormat format, bool bigEndian = false, params byte[] bytes) => CellFormatter.Format(format, bytes, bigEndian);

    [Fact]
    [Trait(TC, "TC-VIEW-10-01")]
    public void Cell_strings_follow_the_format_table()
    {
        // 1. 2 進。
        Assert.Equal("01001111", F(CellFormat.Binary, false, 0x4F));

        // 2. 32 bit 符号あり 10 進 (LE) の FF FF FF FF は -1 (11 文字、0 埋め)。
        string minusOne = F(CellFormat.Int32SignedDecimal, false, 0xFF, 0xFF, 0xFF, 0xFF);
        Assert.Equal("-0000000001", minusOne);
        Assert.Equal(11, minusOne.Length);

        // 3. float の NaN、+Inf、-Inf、非正規化数。
        Assert.Equal("NaN", F(CellFormat.Float, false, 0x00, 0x00, 0xC0, 0x7F).Trim());
        Assert.Equal("+Inf", F(CellFormat.Float, false, 0x00, 0x00, 0x80, 0x7F).Trim());
        Assert.Equal("-Inf", F(CellFormat.Float, false, 0x00, 0x00, 0x80, 0xFF).Trim());
        string subnormal = F(CellFormat.Float, false, 0x01, 0x00, 0x00, 0x00).Trim();
        Assert.EndsWith("d", subnormal);
        Assert.StartsWith("1.4012985E-45", subnormal);

        // 4. 地域設定がフランス語でも小数点は「.」。
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string value = F(CellFormat.Float, false, 0xA4, 0x70, 0x9D, 0x3F);
            Assert.Contains('.', value);
            Assert.DoesNotContain(',', value);
            Assert.Equal("1.2300000E+00", value.Trim());
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        // 5. 境界値: 16 bit 符号あり 10 進の 00 80 は -32768 (6 文字)、64 bit 符号なし 10 進の FF × 8 は 20 文字。
        string min16 = F(CellFormat.Int16SignedDecimal, false, 0x00, 0x80);
        Assert.Equal("-32768", min16);
        Assert.Equal(6, min16.Length);
        Assert.Equal("18446744073709551615", F(CellFormat.Int64Decimal, false, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
    }

    [Fact]
    public void Decimal_and_octal_pad_with_zero_or_space_and_sign_is_explicit()
    {
        Assert.Equal("079", F(CellFormat.Decimal, false, 0x4F));
        Assert.Equal(" 79", CellFormatter.Format(CellFormat.Decimal, [0x4F], false, spacePad: true));
        Assert.Equal("+079", F(CellFormat.SignedDecimal, false, 0x4F));
        Assert.Equal("-001", F(CellFormat.SignedDecimal, false, 0xFF));
        Assert.Equal("  -1", CellFormatter.Format(CellFormat.SignedDecimal, [0xFF], false, spacePad: true));
        Assert.Equal("117", F(CellFormat.Octal, false, 0x4F));
        Assert.Equal("4F2A", F(CellFormat.Int16Hex, true, 0x4F, 0x2A));
        Assert.Equal("2A4F", F(CellFormat.Int16Hex, false, 0x4F, 0x2A));
        Assert.Equal("20266", F(CellFormat.Int16Decimal, true, 0x4F, 0x2A));
        Assert.Equal("-9223372036854775808", F(CellFormat.Int64SignedDecimal, false, 0, 0, 0, 0, 0, 0, 0, 0x80));

        // 各形式の文字列はセルの文字数にそろう。
        foreach (CellFormat format in Enum.GetValues<CellFormat>())
        {
            byte[] value = new byte[CellFormatter.Unit(format)];
            value[^1] = 0x80;
            Assert.Equal(CellFormatter.Chars(format), CellFormatter.Format(format, value, false).Length);
            Assert.Equal(CellFormatter.Chars(format), CellFormatter.Format(format, value, true).Length);
        }
    }

    [Fact]
    public void Double_uses_seventeen_significant_digits()
    {
        byte[] bytes = BitConverter.GetBytes(123456789.01234567);
        Assert.Equal("1.2345678901234567E+08", CellFormatter.Format(CellFormat.Double, bytes, false).Trim());
    }

    [Fact]
    public void Row_format_places_multi_byte_cells_and_trailing_bytes_in_hex()
    {
        // 32 bit Hex、1 行 16 バイト: セルは 8 文字 + 1 文字の区切り。
        var row = new RowFormat(16, 1, false, CellFormat: CellFormat.Int32Hex);
        Assert.Equal(4, row.CellsPerRow);
        Assert.Equal(0, row.CellStart(0));
        Assert.Equal(9, row.CellStart(1));
        Assert.Equal(4 * 8 + 3, row.HexWidth);

        // リトルエンディアンでは上位のバイト (3 番目) がセルの左端。
        Assert.Equal((0, 2), row.ByteSpan(3, 16));
        Assert.Equal((6, 2), row.ByteSpan(0, 16));

        // 長さ 10: 8・9 番目は端数で、2 文字ずつ Hex で置く (VIEW-10 の仕様 5)。
        Assert.False(row.IsCompleteCell(2, 10));
        Assert.Equal((18, 2), row.ByteSpan(8, 10));
        Assert.Equal((20, 2), row.ByteSpan(9, 10));

        // 10 進の形式はセル全体。
        var dec = new RowFormat(16, 1, false, CellFormat: CellFormat.Int32Decimal);
        Assert.Equal((11, 10), dec.ByteSpan(5, 16));
        Assert.Equal(4, dec.ByteAtHexIndex(13));
    }

    [Fact]
    public void Reversed_groups_swap_positions_but_not_the_trailing_partial_group()
    {
        var row = new RowFormat(16, 4, false, Reverse: true);
        Assert.Equal(3, row.SlotOf(0, 16));
        Assert.Equal(0, row.SlotOf(3, 16));
        Assert.Equal(4, row.SlotOf(7, 16));
        Assert.Equal(row.CellStart(3), row.HexIndex(0));

        // 長さ 14 の最後の行: 12・13 番目は不完全なグループなので逆順にしない (VIEW-11 の仕様 7)。
        Assert.Equal(12, row.SlotOf(12, 14));
        Assert.True(row.IsIncompleteReversedGroup(13, 14));
        Assert.False(row.IsIncompleteReversedGroup(3, 14));

        // 範囲の強調: 1〜5 番目は表示で 2 か所に分かれる。
        Span<(int, int)> spans = stackalloc (int, int)[8];
        int n = row.HexSpans(1, 5, 16, spans);
        Assert.Equal(2, n);
    }

    [Fact]
    public void Multiple_text_columns_are_placed_after_the_hex_column()
    {
        var row = new RowFormat(16, 1, true, TextColumns: 3);
        int hex = row.HexWidth;
        Assert.Equal(hex + 2, row.TextColumnStart(0));
        Assert.Equal(hex + 2 + 18, row.TextColumnStart(1));
        Assert.Equal(row.TextColumnStart(2) + 16, row.LineLength);
    }

    [Fact]
    public void View_settings_raise_the_group_and_bytes_per_row_to_the_unit()
    {
        ViewSettings view = ViewSettings.Default with { BytesPerRow = 6 };
        ViewSettings next = view.WithCellFormat(CellFormat.Int32Hex, out int? rounded);
        Assert.Equal(8, rounded);
        Assert.Equal(4, next.EffectiveGroupSize);
        Assert.Equal(BytesPerRowError.NotMultipleOfGroup, next.ValidateBytesPerRow(6));

        // テキスト列の一覧は文字列で持ち、読み書きできる。
        ViewSettings columns = ViewSettings.Default.WithTextColumns([new("utf-16le", 0), new("utf-16le", 1), new("cp932")]);
        Assert.Equal(3, columns.TextColumnCount);
        Assert.Equal(new TextColumnSpec("utf-16le", 1, 0), columns.TextColumns[1]);
        Assert.Equal(columns, ViewSettings.FromJson(columns.ToJson()));
    }
}
