using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>文字コードの一覧と 1 バイトの文字コードの表示 (VIEW-21)。</summary>
public sealed class EncodingCatalogTests
{
    [Theory]
    [InlineData("cp1252", 0xE9, 'é')]
    [InlineData("cp437", 0x82, 'é')]
    [InlineData("cp37", 0xC1, 'A')]
    [InlineData("cp37", 0x7F, '"')]
    [InlineData("cp37", 0x4B, '.')]
    [InlineData("cp37", 0x6F, '?')]
    [InlineData("cp1256", 0xC7, 'ا')]
    public void Single_byte_code_pages_show_their_characters(string id, byte value, char expected)
    {
        TextEncoding encoding = TextEncoding.FromId(id);
        Assert.Equal(id, encoding.Id);
        Assert.Equal(expected, encoding.DisplayChar(value));
        Assert.False(encoding.IsHidden(value));
    }

    [Fact]
    public void Ebcdic_control_bytes_are_hidden_and_labelled_with_the_ibm_number()
    {
        TextEncoding ebcdic = TextEncoding.FromId("cp37");
        Assert.True(ebcdic.IsEbcdic);
        Assert.Equal("037", ebcdic.Name);
        Assert.True(ebcdic.IsHidden(0x25)); // LF
        Assert.Equal("␊", ebcdic.NonPrintableSymbol(0x25, NonPrintableStyle.ControlPictures));
        Assert.False(TextEncoding.FromId("cp1252").IsEbcdic);
    }

    [Theory]
    [InlineData(0x00, "␀")]
    [InlineData(0x0A, "␊")]
    [InlineData(0x1F, "␟")]
    [InlineData(0x7F, "␡")]
    public void Control_pictures(byte value, string expected)
    {
        Assert.Equal(expected, TextEncoding.Ascii.NonPrintableSymbol(value, NonPrintableStyle.ControlPictures));
        Assert.Equal(".", TextEncoding.Ascii.NonPrintableSymbol(value, NonPrintableStyle.Dot));
        Assert.Equal(" ", TextEncoding.Ascii.NonPrintableSymbol(value, NonPrintableStyle.Space));

        var cells = new TextCell[1];
        TextCellDecoder.Decode(TextEncoding.Ascii, [value], value, value, cells, nonPrintable: NonPrintableStyle.ControlPictures);
        Assert.Equal((TextCellKind.NonPrintable, expected), (cells[0].Kind, cells[0].Text));
        TextCellDecoder.Decode(TextEncoding.FromId("utf-8"), [value], value, value, cells, nonPrintable: NonPrintableStyle.ControlPictures);
        Assert.Equal((TextCellKind.NonPrintable, expected), (cells[0].Kind, cells[0].Text));
    }

    [Fact]
    public void C1_controls_have_no_picture()
    {
        TextEncoding latin1 = TextEncoding.FromId("cp28591");
        Assert.True(latin1.IsHidden(0x85));
        Assert.Equal(".", latin1.NonPrintableSymbol(0x85, NonPrintableStyle.ControlPictures));
    }

    [Fact]
    public void List_covers_the_spec_groups_and_hides_missing_code_pages()
    {
        IReadOnlyList<EncodingEntry> all = EncodingCatalog.All;
        foreach (string id in new[] { "ascii", "ansi", "oem", "utf-8", "utf-16le", "utf-32be", "cp874", "cp1258", "cp28591", "cp28605", "cp437",
            "cp866", "cp869", "cp10000", "cp10082", "cp932", "cp51932", "cp20932", "cp936", "cp54936", "cp949", "cp51949", "cp950", "cp51936",
            "cp20866", "cp21866", "cp57002", "cp57011", "cp37", "cp20273", "cp500", "cp1047", "cp1140", "cp1149", "cp21025", "cp20924" })
        {
            Assert.Contains(all, e => e.Id == id);
        }

        // OS が提供しないコードページ (ISO-8859-10 など) は出さない。
        Assert.DoesNotContain(all, e => e.Id == "cp28600");
        Assert.Equal(all.Count, all.Select(e => e.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("cp50220")]
    [InlineData("cp50225")]
    [InlineData("cp52936")]
    public void Stateful_encodings_are_listed_but_not_selectable(string id)
    {
        EncodingEntry entry = Assert.Single(EncodingCatalog.All, e => e.Id == id);
        Assert.True(entry.Stateful);
        Assert.False(entry.Selectable);

        // 保存された設定などで渡されても、テキスト列では使わない (ASCII にする)。
        Assert.Same(TextEncoding.Ascii, TextEncoding.FromId(id));
    }

    [Theory]
    [InlineData("932")]
    [InlineData("jis")]
    [InlineData("JIS")]
    [InlineData("shift_jis")]
    public void Filter_matches_number_and_name_ignoring_case(string query)
    {
        string[] matched = [.. EncodingCatalog.All.Where(e => EncodingCatalog.Matches(e, e.EnglishName, query)).Select(e => e.Id)];
        Assert.Contains("cp932", matched);
        Assert.DoesNotContain("cp1252", matched);
    }

    [Fact]
    public void Ebcdic_is_found_by_its_ibm_number()
    {
        Assert.Contains(EncodingCatalog.All, e => e.Id == "cp37" && EncodingCatalog.Matches(e, e.EnglishName, "037"));
        Assert.Contains(EncodingCatalog.All, e => e.Id == "cp20273" && EncodingCatalog.Matches(e, e.EnglishName, "273"));
    }

    [Fact]
    public void Recent_list_keeps_five_most_recent_without_duplicates()
    {
        IReadOnlyList<string> recent = [];
        foreach (string id in new[] { "cp1", "cp2", "cp3", "cp4", "cp5", "cp6", "cp3" })
        {
            recent = EncodingCatalog.PushRecent(recent, id);
        }

        Assert.Equal(["cp3", "cp6", "cp5", "cp4", "cp2"], recent);
    }

    [Fact]
    public void Oem_uses_the_oem_code_page()
    {
        Assert.Equal("oem", TextEncoding.FromId("oem").Id);
        Assert.Same(TextEncoding.Oem, TextEncoding.FromId("oem"));
    }
}
