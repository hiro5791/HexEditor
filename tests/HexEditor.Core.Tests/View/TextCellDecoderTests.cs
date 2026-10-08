using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>VIEW-22 マルチバイト文字の表示規則 (テキスト列の解読)。</summary>
public sealed class TextCellDecoderTests
{
    /// <summary>
    /// 表示範囲の先頭を <paramref name="windowStart"/> にして解読する (呼び出し側の Hex ビューと同じく、読み戻しと先読みを付けて渡す)。
    /// </summary>
    private static TextCell[] Decode(TextEncoding encoding, byte[] data, long windowStart, int count, int utf16Phase = 0)
    {
        long dataStart = Math.Max(0, windowStart - TextCellDecoder.LookbackFor(encoding));
        long dataEnd = Math.Min(data.Length, windowStart + count + TextCellDecoder.Lookahead);
        var cells = new TextCell[Math.Max(0, (int)Math.Min(count, data.Length - windowStart))];
        TextCellDecoder.Decode(encoding, data.AsSpan((int)dataStart, (int)(dataEnd - dataStart)), dataStart, windowStart, cells, utf16Phase: utf16Phase);
        return cells;
    }

    public static TheoryData<string, string> Encodings() => new()
    {
        { "TD-TEXT-UTF8", "utf-8" },
        { "TD-TEXT-SJIS", "cp932" },
        { "TD-TEXT-UTF16LE", "utf-16le" },
        { "TD-TEXT-UTF8", "cp54936" },
        { "TD-RANDOM-16M", "utf-8" },
        { "TD-RANDOM-16M", "cp932" },
        { "TD-RANDOM-16M", "utf-16le" },
        { "TD-RANDOM-16M", "cp54936" },
    };

    [Theory]
    [Trait(TC, "TC-VIEW-22-03")]
    [MemberData(nameof(Encodings))]
    public void Same_byte_gets_the_same_character_from_any_start(string id, string encodingId)
    {
        byte[] all = File.ReadAllBytes(TestDataCatalog.Get(id));
        byte[] data = all.AsSpan(0, Math.Min(all.Length, 64 * 1024)).ToArray();
        TextEncoding encoding = TextEncoding.FromId(encodingId);
        Assert.NotEqual(TextEncodingKind.SingleByte, encoding.Kind);

        // 基準: 先頭から解読した結果。表示範囲の先頭を 0〜4,096 のすべての位置にして、同じオフセットに同じ文字が割り当てられる。
        const int Window = 512;
        TextCell[] reference = Decode(encoding, data, 0, 4096 + Window);
        for (int start = 0; start <= 4096; start++)
        {
            TextCell[] cells = Decode(encoding, data, start, Window);
            for (int i = 0; i < cells.Length; i++)
            {
                TextCell expected = reference[start + i];
                TextCell actual = cells[i];
                Assert.True(expected.Kind == actual.Kind && expected.Text == actual.Text && expected.Offset == actual.Offset,
                    $"{id} {encodingId}: start {start}, offset {start + i}: {expected} != {actual}");
            }
        }

        // 読み戻しは 4 KiB 以内。
        Assert.True(TextCellDecoder.LookbackFor(encoding) <= TextCellDecoder.MaxLookback);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-22-03")]
    public void Invalid_sequences_and_surrogates()
    {
        // `C3 28`: C3 が不正、28 は `(`。
        TextCell[] utf8 = Decode(TextEncoding.FromId("utf-8"), [0xC3, 0x28], 0, 2);
        Assert.Equal(TextCellKind.Invalid, utf8[0].Kind);
        Assert.Equal("(", utf8[1].Text);

        // UTF-16 LE `3D D8 00 DE` は U+1F600 の 1 文字。
        TextCell[] pair = Decode(TextEncoding.FromId("utf-16le"), [0x3D, 0xD8, 0x00, 0xDE], 0, 4);
        Assert.Equal(char.ConvertFromUtf32(0x1F600), pair[0].Text);
        Assert.Equal(4, pair[0].Span);
        Assert.True(pair[0].Wide);
        Assert.All(pair[1..], c => Assert.Equal(TextCellKind.Continuation, c.Kind));

        // 対になっていない上位サロゲート `00 D8` は不正、`41 00` は `A`。
        TextCell[] lone = Decode(TextEncoding.FromId("utf-16le"), [0x00, 0xD8, 0x41, 0x00], 0, 4);
        Assert.Equal(TextCellKind.Invalid, lone[0].Kind);
        Assert.Equal(TextCellKind.Invalid, lone[1].Kind);
        Assert.Equal("A", lone[2].Text);
    }

    [Fact]
    public void Utf8_three_byte_character_is_wide_and_continuation_cells_are_blank()
    {
        TextCell[] cells = Decode(TextEncoding.FromId("utf-8"), [0xE3, 0x81, 0x82, 0x41], 0, 4);
        Assert.Equal("あ", cells[0].Text);
        Assert.True(cells[0].Wide);
        Assert.Equal(3, cells[0].Span);
        Assert.Equal(TextCellKind.Continuation, cells[1].Kind);
        Assert.Equal(TextCellKind.Continuation, cells[2].Kind);
        Assert.Equal("A", cells[3].Text);

        // 結合文字は前の文字と合成せず、点線の円に付ける (仕様 6)。
        TextCell[] combining = Decode(TextEncoding.FromId("utf-8"), [0x41, 0xCC, 0x81], 0, 3);
        Assert.Equal("A", combining[0].Text);
        Assert.Equal("◌́", combining[1].Text);
        Assert.Equal(TextCellKind.Continuation, combining[2].Kind);

        // BOM は U+FEFF として表示しない文字にする (仕様 8)。
        Assert.Equal(TextCellKind.NonPrintable, Decode(TextEncoding.FromId("utf-8"), [0xEF, 0xBB, 0xBF], 0, 3)[0].Kind);
    }

    [Fact]
    public void Utf16_odd_phase_shifts_the_units()
    {
        byte[] data = TestDataCatalog.ViewPatterns();
        TextEncoding utf16 = TextEncoding.FromId("utf-16le");
        TextCell[] even = Decode(utf16, data, 0xE0, 12);
        Assert.DoesNotContain(even, c => c.Text == "H");
        TextCell[] odd = Decode(utf16, data, 0xE0, 12, utf16Phase: 1);
        Assert.Equal("H", odd[1].Text);
        Assert.Equal("e", odd[3].Text);
        Assert.Equal("o", odd[9].Text);
    }

    [Fact]
    public void Shift_jis_two_byte_characters()
    {
        TextEncoding sjis = TextEncoding.FromId("cp932");
        Assert.Equal(TextEncodingKind.DoubleByte, sjis.Kind);

        // 「あ」= 82 A0、半角カナ「ｱ」= B1。
        TextCell[] cells = Decode(sjis, [0x82, 0xA0, 0xB1, 0x41], 0, 4);
        Assert.Equal("あ", cells[0].Text);
        Assert.Equal(TextCellKind.Continuation, cells[1].Kind);
        Assert.Equal("ｱ", cells[2].Text);
        Assert.False(cells[2].Wide);
        Assert.Equal("A", cells[3].Text);
    }
}
