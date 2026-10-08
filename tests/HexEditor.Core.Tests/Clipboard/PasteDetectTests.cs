using HexEditor.Core.Clipboard;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>「形式を選択して貼り付け」の判別 (EDIT-26) と、他のエディタのクリップボード形式 (EDIT-27)。</summary>
public sealed class PasteDetectTests
{
    private static readonly byte[] DeadBeef = [0xDE, 0xAD, 0xBE, 0xEF];

    private static PasteCandidate Top(string text) =>
        PasteDetector.Best(PasteDetector.Detect(text)) ?? throw new Xunit.Sdk.XunitException("候補がありません: " + text);

    // ---- TC-EDIT-26-01 ----

    [Theory]
    [Trait(TC, "TC-EDIT-26-01")]
    [InlineData("{ 0xDE, 0xAD, 0xBE, 0xEF }", PasteFormat.Array)]
    [InlineData("3q2+7w==", PasteFormat.Base64)]
    [InlineData("00000010  DE AD BE EF  ....", PasteFormat.ScreenDump)]
    [InlineData("byte[] data = { 0xDE, 0xAD, /* c */ 0xBE, 0xEF }; // end", PasteFormat.Array)]
    public void Top_candidate_and_result(string text, PasteFormat expected)
    {
        PasteCandidate top = Top(text);
        Assert.Equal(expected, top.Format);
        Assert.Equal(DeadBeef, top.Bytes);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-26-01")]
    public void Ambiguous_1234_prefers_hex()
    {
        IReadOnlyList<PasteCandidate> list = PasteDetector.Detect("1234");
        var valid = list.Where(c => c.IsValid).ToList();
        Assert.Equal([PasteFormat.HexString, PasteFormat.Base64, PasteFormat.Text], valid.Select(c => c.Format));
        Assert.Equal([2L, 3L, 4L], valid.Select(c => c.Length));
        Assert.DoesNotContain(list, c => c.Format == PasteFormat.Decimal);
        Assert.Equal(new byte[] { 0x12, 0x34 }, valid[0].Bytes);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-26-01")]
    public void Odd_hex_digits_are_an_error_with_position()
    {
        IReadOnlyList<PasteCandidate> list = PasteDetector.Detect("DEADBEE");
        PasteCandidate hex = Assert.Single(list, c => c.Format == PasteFormat.HexString);
        Assert.False(hex.IsValid);
        Assert.Equal(new PasteError(1, 7, "odd"), hex.Error);
        Assert.NotEqual(PasteFormat.HexString, PasteDetector.Best(list)!.Format);
    }

    [Fact]
    public void Preferred_format_wins_within_the_same_rank()
    {
        // "DEADBEEF" は Hex 文字列 (順位 3) と Base64 (順位 4) の両方。前回の形式が Base64 でも順位は変えない。
        Assert.Equal(PasteFormat.HexString, PasteDetector.Best(PasteDetector.Detect("DEADBEEF", new PasteOptions { Preferred = PasteFormat.Base64 }))!.Format);

        // "0xDE, 0xAD" は配列表記とエスケープ以外の同順位なし。Hex と Base32hex のように同じ順位なら前回の形式を先にする。
        IReadOnlyList<PasteCandidate> list = PasteDetector.Detect("ABCDEFGH", new PasteOptions { Preferred = PasteFormat.Base64 });
        Assert.Equal(PasteFormat.Base64, list.First(c => c.Rank == 4 && c.IsValid).Format);
    }

    [Fact]
    public void Binary_clipboard_content_is_first()
    {
        IReadOnlyList<PasteCandidate> list = PasteDetector.Detect("DE AD", binary: [1, 2, 3]);
        Assert.Equal(PasteFormat.Binary, PasteDetector.Best(list)!.Format);
    }

    [Fact]
    public void Text_candidate_uses_the_encoding_and_options()
    {
        PasteCandidate text = PasteDetector.Parse(PasteFormat.Text, "a\nb", new PasteOptions { NewLines = NewLineConversion.CrLf, AppendNul = true });
        Assert.Equal("a\r\nb\0"u8.ToArray(), text.Bytes);
        Assert.False(PasteDetector.Parse(PasteFormat.Text, "日本").IsValid);
    }

    [Fact]
    public void Array_notation_variants()
    {
        Assert.Equal(DeadBeef, Top("let data: [u8; 4] = [0xDE, 0xAD, 0xBE, 0xEF];").Bytes);
        Assert.Equal(DeadBeef, Top("const data: array[0..3] of Byte = ($DE, $AD, $BE, $EF);").Bytes);
        Assert.Equal(DeadBeef, Top("Dim data As Byte() = {&HDE, &HAD, &HBE, &HEF}").Bytes);
        Assert.Equal(DeadBeef, Top("data db 0DEh, 0ADh, 0BEh, 0EFh ; comment").Bytes);
        Assert.Equal(DeadBeef, Top("[222, 173, 190, 239]").Bytes);
        Assert.Equal(DeadBeef, Top("data = b\"\\xde\\xad\\xbe\\xef\"").Bytes);
        var two = PasteDetector.Parse(PasteFormat.Array, "{ 0xADDE, 0xEFBE }", new PasteOptions { ElementSize = 2 });
        Assert.Equal(DeadBeef, two.Bytes);
        Assert.False(PasteDetector.Parse(PasteFormat.Array, "{ 0x1DE }").IsValid);
    }

    [Fact]
    public void Common_dumps_are_recognised()
    {
        string hexdump = "00000000  de ad be ef 00 01 02 03  04 05 06 07 08 09 0a 0b  |................|\n" +
            "00000010  0c 0d                                             |..|";
        PasteCandidate top = Top(hexdump);
        Assert.Equal(PasteFormat.ScreenDump, top.Format);
        Assert.Equal(18, top.Length);

        // 2 行目のオフセットが合わなければダンプではない。
        Assert.NotEqual(PasteFormat.ScreenDump, Top("00000000  DE AD  ..\n00000005  BE EF  ..").Format);

        // テキストの列が 16 進に見える場合も、テキストの列の長さで区切りを決める。
        Assert.Equal(new byte[] { 0x41, 0x42 }, Top("00000000  41 42  AB").Bytes);
    }

    // ---- TC-EDIT-26-02: EDIT-25 の全形式の出力 ----

    /// <summary>
    /// 出力の形式ごとに、最上位になってよい判別の形式。`0x` 付きカンマ区切りは配列表記、`\x` エスケープはエスケープ文字列としても
    /// 同じバイト列になる。文書形式 (HTML・RTF) のテキストの形式は画面表示どおりのダンプ。
    /// </summary>
    private static PasteFormat[] Accepted(CopyFormat format, CopyOptions options) => format switch
    {
        CopyFormat.HexSpaced or CopyFormat.HexPlain or CopyFormat.HexCustom => [PasteFormat.HexString],
        CopyFormat.HexCommaPrefixed => [PasteFormat.HexString, PasteFormat.Array],
        CopyFormat.HexEscaped => [PasteFormat.HexString, PasteFormat.Escape],
        CopyFormat.HexUrl => [PasteFormat.UrlEncoded],
        CopyFormat.Decimal => [PasteFormat.Decimal],
        CopyFormat.Octal => [PasteFormat.Octal],
        CopyFormat.Binary => [PasteFormat.BinaryNumbers],
        CopyFormat.ArrayPython when options.PythonBytesLiteral => [PasteFormat.Escape],
        >= CopyFormat.ArrayC and <= CopyFormat.ArrayAssembly => [PasteFormat.Array],
        CopyFormat.Base64 => [PasteFormat.Base64],
        CopyFormat.Base32 => [options.Base32Hex ? PasteFormat.Base32Hex : PasteFormat.Base32],
        CopyFormat.Ascii85 => [options.Ascii85 == Ascii85Variant.Z85 ? PasteFormat.Z85 : PasteFormat.Ascii85],
        CopyFormat.UUEncode => [PasteFormat.UUEncode],
        CopyFormat.XXEncode => [PasteFormat.XXEncode],
        CopyFormat.QuotedPrintable => [PasteFormat.QuotedPrintable],
        CopyFormat.IntelHex => [PasteFormat.IntelHex],
        CopyFormat.SRecord => [PasteFormat.SRecord],
        CopyFormat.Json => [PasteFormat.Json],
        _ => [PasteFormat.ScreenDump],
    };

    public static IEnumerable<object[]> AllFormats()
    {
        foreach (CopyFormat format in Enum.GetValues<CopyFormat>())
        {
            // テキスト・位置はバイト列を表さない。HTML・RTF はテキストの形式 (画面表示どおり) で判別する。表の Markdown・TeX は対象外。
            if (format is CopyFormat.Text or CopyFormat.Position or CopyFormat.Html or CopyFormat.Rtf)
            {
                continue;
            }

            yield return [format, "default"];
        }

        yield return [CopyFormat.ArrayCSharp, "span"];
        yield return [CopyFormat.ArrayPython, "bytes"];
        yield return [CopyFormat.ArrayAssembly, "masm"];
        yield return [CopyFormat.ArrayAssembly, "gas"];
        yield return [CopyFormat.Base64, "url"];
        yield return [CopyFormat.Base32, "hex"];
        yield return [CopyFormat.Ascii85, "z85"];
        yield return [CopyFormat.Json, "hex"];
        yield return [CopyFormat.Json, "numbers"];
    }

    private static CopyOptions Variant(string variant) => variant switch
    {
        "span" => new CopyOptions { CSharpSpan = true },
        "bytes" => new CopyOptions { PythonBytesLiteral = true },
        "masm" => new CopyOptions { Assembly = AssemblySyntax.Masm },
        "gas" => new CopyOptions { Assembly = AssemblySyntax.Gas },
        "url" => new CopyOptions { Base64UrlSafe = true },
        "hex" => new CopyOptions { Base32Hex = true, JsonData = JsonDataEncoding.Hex },
        "z85" => new CopyOptions { Ascii85 = Ascii85Variant.Z85 },
        "numbers" => new CopyOptions { JsonData = JsonDataEncoding.Numbers },
        _ => new CopyOptions(),
    };

    [Theory]
    [Trait(TC, "TC-EDIT-26-02")]
    [MemberData(nameof(AllFormats))]
    public void Every_copy_format_is_detected(CopyFormat format, string variant)
    {
        byte[] random = new byte[64];
        TestDataCatalog.Expected("TD-RANDOM-16M", 0, random);
        CopyOptions options = Variant(variant);
        foreach (byte[] input in new[] { random, DeadBeef })
        {
            string text = CopyFormatter.Format(format, input, 0, options, out _);
            if (format is CopyFormat.Markdown or CopyFormat.Tex && options.Layout == DocumentLayout.Table)
            {
                continue;
            }

            PasteCandidate top = Top(text);
            Assert.True(Accepted(format, options).Contains(top.Format), $"{format}/{variant}: top {top.Format} for\n{text}");
            Assert.True(input.AsSpan().SequenceEqual(top.Bytes), $"{format}/{variant}: bytes differ for\n{text}");
        }
    }

    // ---- Intel HEX のアドレス (TC-EDIT-26-04・05 の部品) ----

    private const string IhexSmall = ":10001000000102030405060708090A0B0C0D0E0F68\r\n:04010000DEADBEEFC3\r\n:00000001FF\r\n";

    [Fact]
    public void Intel_hex_with_a_gap()
    {
        PasteCandidate top = Top(IhexSmall);
        Assert.Equal(PasteFormat.IntelHex, top.Format);
        Assert.Equal(0, top.ChecksumErrors);
        Assert.Equal(2, top.Segments!.Count);
        Assert.Equal(0x10, top.Segments[0].Address);
        Assert.Equal(0x100, top.Segments[1].Address);
        Assert.Equal(0xF4, top.Length);
        Assert.Equal(0xFF, top.Bytes![0x10]);
        Assert.Equal(DeadBeef, top.Bytes[0xF0..0xF4]);
        Assert.Equal(0x00, PasteDetector.Contiguous(top.Segments, 0x00)[0x10]);
    }

    [Fact]
    public void Checksum_errors_are_counted()
    {
        PasteCandidate top = Top(IhexSmall.Replace("C3", "C4"));
        Assert.Equal(PasteFormat.IntelHex, top.Format);
        Assert.Equal(1, top.ChecksumErrors);
    }

    // ---- TC-EDIT-27-01・02: 他のエディタの形式 ----

    private static readonly byte[] Bytes256 = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    [Fact]
    [Trait(TC, "TC-EDIT-27-01")]
    public void Paste_from_each_confirmed_format()
    {
        foreach (CompatClipboardFormat format in CompatClipboardFormats.Formats)
        {
            byte[] raw = CompatClipboardFormats.Encode(format.Layout, Bytes256);
            var clipboard = new Dictionary<string, byte[]> { [format.FormatName] = raw };
            (CompatClipboardFormat Format, byte[] Data)? found = CompatClipboardFormats.FindForPaste(n => clipboard.GetValueOrDefault(n));
            Assert.NotNull(found);
            Assert.Equal(Bytes256, found.Value.Data);

            // 確保の都合で後ろに余りが付いても、前置の長さで切る。
            if (format.Layout == CompatLayout.LengthPrefixed32)
            {
                Assert.True(CompatClipboardFormats.TryDecode(format.Layout, [.. raw, 0, 0, 0], out byte[] trimmed));
                Assert.Equal(Bytes256, trimmed);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-27-01")]
    public void Broken_length_prefix_falls_back_to_the_next_format()
    {
        var prefixed = new CompatClipboardFormat("A", "A.Binary", CompatLayout.LengthPrefixed32);
        var raw = new CompatClipboardFormat("B", "B.Binary", CompatLayout.Raw);
        byte[] broken = CompatClipboardFormats.Encode(CompatLayout.LengthPrefixed32, Bytes256);
        BitConverter.GetBytes(300u).CopyTo(broken, 0);
        var clipboard = new Dictionary<string, byte[]> { ["A.Binary"] = broken, ["B.Binary"] = Bytes256 };
        (CompatClipboardFormat Format, byte[] Data)? found = CompatClipboardFormats.FindForPaste(n => clipboard.GetValueOrDefault(n), [prefixed, raw]);
        Assert.Equal("B", found!.Value.Format.Editor);
        Assert.Equal(Bytes256, found.Value.Data);
        Assert.Null(CompatClipboardFormats.FindForPaste(n => n == "A.Binary" ? broken : null, [prefixed]));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-27-02")]
    public void Copy_produces_every_confirmed_format()
    {
        IReadOnlyList<(string FormatName, byte[] Data)> encoded = CompatClipboardFormats.Encode(Bytes256);
        Assert.Equal(CompatClipboardFormats.Formats.Select(f => f.FormatName), encoded.Select(e => e.FormatName));
        foreach ((string name, byte[] data) in encoded)
        {
            CompatClipboardFormat format = CompatClipboardFormats.Formats.Single(f => f.FormatName == name);
            Assert.True(CompatClipboardFormats.TryDecode(format.Layout, data, out byte[] decoded));
            Assert.Equal(Bytes256, decoded);
        }

        // Frhed の BinaryData は 4 バイトの長さの後に実データ。
        (string _, byte[] binaryData) = encoded.Single(e => e.FormatName == "BinaryData");
        Assert.Equal(260, binaryData.Length);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, binaryData[..4]);
    }
}
