using HexEditor.Core.Tools;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-26 文字コード表。</summary>
public sealed class CharacterTableTests
{
    private static CharacterTable Table(string id) => CharacterTable.TryCreate(EncodingCatalog.Find(id)!)!;

    [Fact]
    [Trait(TC, "TC-TOOL-26-01")]
    public void CodePage437ShowsTheLightShadeAndControlAbbreviations()
    {
        CharacterTable table = Table("cp437");
        CharacterCell shade = table.Cell(0xB0);
        Assert.Equal(("░", 0x2591, "LIGHT SHADE"), (shade.Text, shade.CodePoint, shade.Name));
        CharacterDetail detail = CharacterTable.Detail(shade, utf16BigEndian: false);
        Assert.Equal(("U+2591", "E2 96 91", "91 25", "0xB0", "176"), (detail.CodePointText, detail.Utf8, detail.Utf16, detail.ByteHex, detail.ByteDecimal));

        Assert.Equal("NUL", table.Cell(0x00).Label);
        Assert.Equal("LF", table.Cell(0x0A).Label);
        Assert.Equal("SP", table.Cell(0x20).Label);
        Assert.Equal("DEL", table.Cell(0x7F).Label);
        Assert.False(table.IsMultiByte);
        Assert.Equal("0x41, A, LATIN CAPITAL LETTER A", table.Cell(0x41).SpokenText);
        Assert.Equal(256, table.List().Count);
        Assert.Equal(new CharacterListRow("41", "65", "101", "A", "U+0041", "LATIN CAPITAL LETTER A"), table.List()[0x41]);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-26-02")]
    public void ShiftJisLeadByteShowsASecondTableWithHiragana()
    {
        CharacterTable table = Table("cp932");
        Assert.True(table.IsMultiByte);
        Assert.Equal(CharacterCellKind.Lead, table.Cell(0x82).Kind);
        Assert.Equal(CharacterCellKind.Character, table.Cell(0xB1).Kind); // 半角カナ
        Assert.Equal(CharacterCellKind.Character, table.Cell(0x41).Kind);

        Assert.Equal("ぁ", table.TrailCell(0x82, 0x9F).Text);
        CharacterCell a = table.TrailCell(0x82, 0xA0);
        Assert.Equal(("あ", "HIRAGANA LETTER A"), (a.Text, a.Name));
        Assert.Equal("0x82A0, あ, HIRAGANA LETTER A", a.SpokenText);
        CharacterDetail le = CharacterTable.Detail(a, utf16BigEndian: false);
        Assert.Equal(("U+3042", "E3 81 82", "42 30"), (le.CodePointText, le.Utf8, le.Utf16));
        Assert.Equal("30 42", CharacterTable.Detail(a, utf16BigEndian: true).Utf16);
        Assert.Equal(CharacterCellKind.Undefined, table.TrailCell(0x82, 0x20).Kind);
        Assert.Equal(256, table.List(0x82).Count);
    }

    [Fact]
    public void SearchByCharacterNameAndCodePoint()
    {
        CharacterTable sjis = Table("cp932");
        Assert.Equal((0x82, 0xA0), Pair(sjis.Find("あ")));
        Assert.Equal((0x82, 0xA0), Pair(sjis.Find("U+3042")));
        Assert.Equal((0x82, 0xA0), Pair(sjis.Find("hiragana letter a")));
        Assert.Equal((-1, 0x41), Pair(sjis.Find("A")));
        Assert.Null(sjis.Find("☃☃"));

        CharacterTable dos = Table("cp437");
        Assert.Equal((-1, 0xB0), Pair(dos.Find("light shade")));
        Assert.Equal((-1, 0xB0), Pair(dos.Find("0x2591")));
        Assert.Equal((-1, 0xB0), Pair(dos.Find("░")));
        Assert.Null(dos.Find("zzzz"));
    }

    private static (int Lead, int Value) Pair((byte? Lead, byte Value)? found) => (found!.Value.Lead ?? -1, found.Value.Value);

    [Fact]
    public void EbcdicTableAndUnsupportedEncodings()
    {
        CharacterTable ebcdic = Table("cp37");
        Assert.Equal("A", ebcdic.Cell(0xC1).Text);
        Assert.Equal("NUL", ebcdic.Cell(0x00).Label);
        Assert.Equal(CharacterCellKind.Character, ebcdic.Cell(0x40).Kind);
        Assert.Null(CharacterTable.TryCreate(EncodingCatalog.Find("utf-16le")!));
        Assert.NotNull(CharacterTable.TryCreate(EncodingCatalog.Find("utf-8")!));
        Assert.True(Table("utf-8").Cell(0xC3).Kind == CharacterCellKind.Lead);
        Assert.Equal("é", Table("utf-8").TrailCell(0xC3, 0xA9).Text);
        Assert.True(Table("cp936").IsMultiByte);
        Assert.True(Table("cp950").IsMultiByte);
        Assert.True(Table("cp949").IsMultiByte);
    }
}
