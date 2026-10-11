using HexEditor.Core.Clipboard;
using HexEditor.Core.Coloring;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>「形式を選択してコピー」の変換 (EDIT-25)。</summary>
public sealed class CopyFormatTests
{
    private static readonly byte[] DeadBeef = [0xDE, 0xAD, 0xBE, 0xEF];

    private static string F(CopyFormat format, CopyOptions? options = null, byte[]? data = null, long offset = 0) =>
        CopyFormatter.Format(format, data ?? DeadBeef, offset, options);

    public static TheoryData<CopyFormat, string, string> TableExamples() => new()
    {
        { CopyFormat.HexSpaced, "", "DE AD BE EF" },
        { CopyFormat.HexPlain, "", "DEADBEEF" },
        { CopyFormat.HexCommaPrefixed, "", "0xDE, 0xAD, 0xBE, 0xEF" },
        { CopyFormat.HexEscaped, "", "\\xDE\\xAD\\xBE\\xEF" },
        { CopyFormat.HexUrl, "", "%DE%AD%BE%EF" },
        { CopyFormat.HexCustom, "", "DE:AD:BE:EF" },
        { CopyFormat.Decimal, "", "222 173 190 239" },
        { CopyFormat.Octal, "", "336 255 276 357" },
        { CopyFormat.Binary, "", "11011110 10101101 10111110 11101111" },
        { CopyFormat.ArrayC, "", "unsigned char data[4] = { 0xDE, 0xAD, 0xBE, 0xEF };" },
        { CopyFormat.ArrayCpp, "", "constexpr std::array<std::uint8_t, 4> data = { 0xDE, 0xAD, 0xBE, 0xEF };" },
        { CopyFormat.ArrayCSharp, "", "byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF };" },
        { CopyFormat.ArrayCSharp, "span", "ReadOnlySpan<byte> data => [0xDE, 0xAD, 0xBE, 0xEF];" },
        { CopyFormat.ArrayJava, "", "byte[] data = { (byte) 0xDE, (byte) 0xAD, (byte) 0xBE, (byte) 0xEF };" },
        { CopyFormat.ArrayJavaScript, "", "const data = new Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]);" },
        { CopyFormat.ArrayPython, "", "data = bytes([0xDE, 0xAD, 0xBE, 0xEF])" },
        { CopyFormat.ArrayPython, "bytes", "data = b\"\\xde\\xad\\xbe\\xef\"" },
        { CopyFormat.ArrayRust, "", "let data: [u8; 4] = [0xDE, 0xAD, 0xBE, 0xEF];" },
        { CopyFormat.ArrayGo, "", "data := []byte{0xDE, 0xAD, 0xBE, 0xEF}" },
        { CopyFormat.ArrayPascal, "", "const data: array[0..3] of Byte = ($DE, $AD, $BE, $EF);" },
        { CopyFormat.ArrayVisualBasic, "", "Dim data As Byte() = {&HDE, &HAD, &HBE, &HEF}" },
        { CopyFormat.ArrayPureBasic, "", "DataSection\r\ndata:\r\nData.a $DE, $AD, $BE, $EF\r\nEndDataSection" },
        { CopyFormat.ArrayAssembly, "nasm", "data: db 0xDE, 0xAD, 0xBE, 0xEF" },
        { CopyFormat.ArrayAssembly, "masm", "data db 0DEh, 0ADh, 0BEh, 0EFh" },
        { CopyFormat.ArrayAssembly, "gas", "data: .byte 0xDE, 0xAD, 0xBE, 0xEF" },
        { CopyFormat.Base64, "", "3q2+7w==" },
        { CopyFormat.Base32, "", "32W353Y=" },
        { CopyFormat.Json, "", "{\"offset\": 0, \"length\": 4, \"data\": \"3q2+7w==\"}" },
        { CopyFormat.Position, "", "0x0-0x3 (4 bytes)" },
    };

    private static CopyOptions Variant(string variant) => variant switch
    {
        "span" => new CopyOptions { CSharpSpan = true },
        "bytes" => new CopyOptions { PythonBytesLiteral = true },
        "masm" => new CopyOptions { Assembly = AssemblySyntax.Masm },
        "gas" => new CopyOptions { Assembly = AssemblySyntax.Gas },
        _ => new CopyOptions(),
    };

    [Theory]
    [Trait(TC, "TC-EDIT-25-01")]
    [MemberData(nameof(TableExamples))]
    public void Output_matches_the_table_example(CopyFormat format, string variant, string expected) =>
        Assert.Equal(expected, F(format, Variant(variant)));

    [Fact]
    [Trait(TC, "TC-EDIT-25-01")]
    public void Every_format_produces_output()
    {
        foreach (CopyFormat format in Enum.GetValues<CopyFormat>())
        {
            Assert.False(string.IsNullOrEmpty(F(format)), format.ToString());
        }

        // 例の書かれていない形式の中身。
        Assert.Equal("<~hQ=N\\~>", F(CopyFormat.Ascii85));
        Assert.Equal("begin 644 data.bin\r\n$WJV^[P``\r\n`\r\nend\r\n", F(CopyFormat.UUEncode));
        Assert.Equal("=DE=AD=BE=EF", F(CopyFormat.QuotedPrintable));
        Assert.Equal("00000000  DE AD BE EF" + new string(' ', 36) + "  ....", F(CopyFormat.ScreenDump));
        Assert.Equal(":04000000DEADBEEFC4\r\n:00000001FF\r\n", F(CopyFormat.IntelHex));
        Assert.StartsWith("S0030000FC\r\nS107000", F(CopyFormat.SRecord));
        Assert.StartsWith("<pre style=", F(CopyFormat.Html));
        Assert.StartsWith("{\\rtf1", F(CopyFormat.Rtf));
        Assert.StartsWith("```text\r\n00000000  DE AD BE EF", F(CopyFormat.Markdown));
        Assert.StartsWith("\\begin{verbatim}", F(CopyFormat.Tex));
        Assert.Equal("{\"offset\": 0, \"length\": 4, \"data\": \"DEADBEEF\"}", F(CopyFormat.Json, new CopyOptions { JsonData = JsonDataEncoding.Hex }));
        Assert.Equal("{\"offset\": 0, \"length\": 4, \"data\": [222, 173, 190, 239]}", F(CopyFormat.Json, new CopyOptions { JsonData = JsonDataEncoding.Numbers }));
        Assert.Equal("256-511 (256 bytes)", CopyFormatter.Format(CopyFormat.Position, new byte[256], 256, new CopyOptions { DecimalPosition = true }));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-25-01")]
    public void Two_byte_elements_and_padded_last_element()
    {
        var two = new CopyOptions { ElementSize = 2 };
        Assert.Equal("uint16_t data[2] = { 0xADDE, 0xEFBE };", F(CopyFormat.ArrayC, two));
        Assert.Equal("data: dw 0xADDE, 0xEFBE", F(CopyFormat.ArrayAssembly, two));

        string c = CopyFormatter.Format(CopyFormat.ArrayC, [0xDE, 0xAD, 0xBE, 0xEF, 0x01], 0, new CopyOptions { ElementSize = 4 }, out var notes);
        Assert.Equal("uint32_t data[2] = { 0xEFBEADDE, 0x00000001 };", c);
        Assert.Contains(CopyNote.PaddedLastElement, notes);
    }

    [Fact]
    public void Multi_line_arrays_wrap_at_bytes_per_line()
    {
        byte[] data = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        string c = F(CopyFormat.ArrayC, data: data);
        Assert.Equal(
            "unsigned char data[20] = {\r\n" +
            "    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,\r\n" +
            "    0x10, 0x11, 0x12, 0x13\r\n};", c);
        Assert.EndsWith("    0x10, 0x11, 0x12, 0x13,\r\n}", F(CopyFormat.ArrayGo, data: data));
        Assert.Equal("DE AD\r\nBE EF", F(CopyFormat.HexSpaced, new CopyOptions { BytesPerLine = 2 }));
        Assert.Equal("0xDE, 0xAD,\nBE".Length, F(CopyFormat.HexCommaPrefixed, new CopyOptions { BytesPerLine = 2, NewLine = "\n" }).IndexOf("0xBE") + 2);
        Assert.Equal("de ad be ef", F(CopyFormat.HexSpaced, new CopyOptions { UpperCase = false }));
        Assert.Equal("data = (\r\n    b\"\\x00\\x01\"\r\n    b\"\\x02\"\r\n)",
            F(CopyFormat.ArrayPython, new CopyOptions { PythonBytesLiteral = true, BytesPerLine = 2 }, [0, 1, 2]));
    }

    [Theory]
    [InlineData("data", true)]
    [InlineData("_x1", true)]
    [InlineData("1abc", false)]
    [InlineData("int", false)]
    [InlineData("a-b", false)]
    public void Variable_names_must_be_identifiers(string name, bool valid)
    {
        Assert.Equal(valid, CopyFormatter.IsValidIdentifier(CopyFormat.ArrayC, name));
        Assert.Equal(valid ? null : CopyOptionError.InvalidVariableName,
            CopyFormatter.Validate(CopyFormat.ArrayC, new CopyOptions { VariableName = name }, 0, 4));
    }

    [Fact]
    public void Validation_of_z85_and_addresses()
    {
        Assert.Equal(CopyOptionError.Z85Length, CopyFormatter.Validate(CopyFormat.Ascii85, new CopyOptions { Ascii85 = Ascii85Variant.Z85 }, 0, 5));
        Assert.Null(CopyFormatter.Validate(CopyFormat.Ascii85, new CopyOptions { Ascii85 = Ascii85Variant.Z85 }, 0, 8));
        Assert.Equal(CopyOptionError.AddressTooLarge, CopyFormatter.Validate(CopyFormat.IntelHex, new CopyOptions(), 0xFFFFFFFFL, 2));
    }

    [Fact]
    public void Estimate_scales_the_sample()
    {
        byte[] data = new byte[100_000];
        long estimate = CopyFormatter.EstimateChars(CopyFormat.ArrayC, new CopyOptions(), (o, d) => d.Clear(), 0, data.Length);
        long actual = F(CopyFormat.ArrayC, data: data).Length;
        Assert.InRange(estimate, actual * 0.98, actual * 1.02);
    }

    // ---- TC-EDIT-25-03: 往復 ----

    public static TheoryData<string, CopyFormat, PasteFormat, CopyOptions> RoundTripFormats() => new()
    {
        { "base64", CopyFormat.Base64, PasteFormat.Base64, new CopyOptions() },
        { "base64url", CopyFormat.Base64, PasteFormat.Base64, new CopyOptions { Base64UrlSafe = true } },
        { "base64wrap", CopyFormat.Base64, PasteFormat.Base64, new CopyOptions { Base64Wrap = true } },
        { "base64urlwrap", CopyFormat.Base64, PasteFormat.Base64, new CopyOptions { Base64UrlSafe = true, Base64Wrap = true } },
        { "base32", CopyFormat.Base32, PasteFormat.Base32, new CopyOptions() },
        { "base32hex", CopyFormat.Base32, PasteFormat.Base32Hex, new CopyOptions { Base32Hex = true } },
        { "ascii85", CopyFormat.Ascii85, PasteFormat.Ascii85, new CopyOptions() },
        { "z85", CopyFormat.Ascii85, PasteFormat.Z85, new CopyOptions { Ascii85 = Ascii85Variant.Z85 } },
        { "uu", CopyFormat.UUEncode, PasteFormat.UUEncode, new CopyOptions() },
        { "xx", CopyFormat.XXEncode, PasteFormat.XXEncode, new CopyOptions() },
        { "qp", CopyFormat.QuotedPrintable, PasteFormat.QuotedPrintable, new CopyOptions() },
        { "ihex", CopyFormat.IntelHex, PasteFormat.IntelHex, new CopyOptions() },
        { "srec", CopyFormat.SRecord, PasteFormat.SRecord, new CopyOptions() },
        { "json64", CopyFormat.Json, PasteFormat.Json, new CopyOptions() },
        { "jsonhex", CopyFormat.Json, PasteFormat.Json, new CopyOptions { JsonData = JsonDataEncoding.Hex } },
        { "jsonnum", CopyFormat.Json, PasteFormat.Json, new CopyOptions { JsonData = JsonDataEncoding.Numbers } },
    };

    private static readonly int[] Lengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 255, 256, 4096, 70_000];

    private static byte[] Random16M(int length)
    {
        byte[] bytes = new byte[length];
        TestDataCatalog.Expected("TD-RANDOM-16M", 0, bytes);
        return bytes;
    }

    [Theory]
    [Trait(TC, "TC-EDIT-25-03")]
    [MemberData(nameof(RoundTripFormats))]
    public void Encodings_round_trip(string name, CopyFormat format, PasteFormat paste, CopyOptions options)
    {
        foreach (int length in Lengths)
        {
            if (format == CopyFormat.Ascii85 && options.Ascii85 == Ascii85Variant.Z85 && length % 4 != 0)
            {
                continue;
            }

            // 長さ 0 の入力は、アドレス・内容を持たない形式では表せない (Intel HEX・S-record は空のレコードの列になる)。
            if (length == 0 && format is CopyFormat.IntelHex or CopyFormat.SRecord or CopyFormat.QuotedPrintable)
            {
                continue;
            }

            byte[] input = Random16M(length);
            long[] addresses = format is CopyFormat.IntelHex or CopyFormat.SRecord ? [0, 0xFFF0, 0x12345678] : [0];
            foreach (long address in addresses)
            {
                string text = CopyFormatter.Format(format, input, 0, options with { BaseAddress = address }, out _);
                PasteCandidate result = PasteDetector.Parse(paste, text);
                Assert.True(result.IsValid, $"{name} {length} @{address:X}: {result.Error}");
                Assert.True(input.AsSpan().SequenceEqual(result.Bytes), $"{name} {length} @{address:X}");
                if (result.Segments is { } segments)
                {
                    Assert.Equal(address, segments[0].Address);
                }
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-25-03")]
    public void Address_extensions_are_chosen_automatically()
    {
        byte[] input = Random16M(256);
        string fromFff0 = CopyFormatter.Format(CopyFormat.IntelHex, input, 0, new CopyOptions { BaseAddress = 0xFFF0 }, out _);
        Assert.Contains(fromFff0.Split("\r\n"), l => l.Length > 8 && l[7..9] is "02" or "04");
        string high = CopyFormatter.Format(CopyFormat.IntelHex, input, 0, new CopyOptions { BaseAddress = 0x12345678 }, out _);
        Assert.StartsWith(":020000041234B4\r\n:10567800", high);
        string srec = CopyFormatter.Format(CopyFormat.SRecord, input, 0, new CopyOptions { BaseAddress = 0x12345678 }, out _);
        Assert.Contains("\r\nS3", srec);
        Assert.Contains("\r\nS7", srec);
    }

    // ---- TC-EDIT-25-05: HTML 形式の中身 ----

    [Fact]
    [Trait(TC, "TC-EDIT-25-05")]
    public void Html_dump_has_cf_html_header_and_colored_changes()
    {
        byte[] data = Enumerable.Range(0, 0x20).Select(i => (byte)i).ToArray();
        data[5] = 0xFF;
        var options = new CopyOptions { IncludeColors = true, ModifiedRanges = [(5, 1)], ModifiedColor = "#C42B1C" };
        string fragment = CopyFormatter.Format(CopyFormat.Html, data, 0, options, out _);
        string cf = HtmlClipboard.Wrap(fragment);
        IReadOnlyDictionary<string, string> header = HtmlClipboard.ReadHeader(cf);
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(cf);
        int startHtml = int.Parse(header["StartHTML"]), endHtml = int.Parse(header["EndHTML"]);
        int startFragment = int.Parse(header["StartFragment"]), endFragment = int.Parse(header["EndFragment"]);
        Assert.Equal("0.9", header["Version"]);
        Assert.StartsWith("<html>", System.Text.Encoding.UTF8.GetString(utf8, startHtml, 6));
        Assert.Equal(utf8.Length, endHtml);
        Assert.Equal(fragment, System.Text.Encoding.UTF8.GetString(utf8, startFragment, endFragment - startFragment));

        Assert.StartsWith("<pre style=\"font-family: 'Cascadia Mono', Consolas, monospace;\">", fragment);
        Assert.EndsWith("</pre>", fragment);
        Assert.Equal(2, fragment.Split("\r\n").Length);
        Assert.Contains("<span style=\"color: #C42B1C; font-weight: bold;\">FF</span>", fragment);

        // テキストの形式にも同じダンプを入れる。
        string text = CopyFormatter.Format(CopyFormat.ScreenDump, data, 0, options, out _);
        Assert.Equal(2, text.Split("\r\n").Length);
        Assert.StartsWith("00000000  00 01 02 03 04 FF 06", text);
    }

    /// <summary>
    /// 画面表示どおりは、グループ化・中央区切り・グループ内の逆順表示・セルの表示形式も画面に合わせる (EDIT-25 の仕様 6、VIEW-11 の仕様 5)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-EDIT-25-01")]
    public void Screen_dump_follows_grouping_reversal_and_cell_format()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var hexOnly = new CopyOptions { ScreenBytesPerRow = 8, ShowText = false };

        // グループ化 4 (グループの間に空白)。
        CopyOptions grouped = hexOnly with { ScreenGroupSize = 4 };
        Assert.Equal("00000000  01020304 05060708\r\n00000008  090A", F(CopyFormat.ScreenDump, grouped, data));

        // グループ内の逆順表示。末尾の不完全なグループは逆にしない (VIEW-11 の仕様 7)。テキスト列は逆にしない (仕様 8)。
        Assert.Equal("00000000  04030201 08070605\r\n00000008  090A", F(CopyFormat.ScreenDump, grouped with { ScreenReverseGroups = true }, data));
        Assert.Equal("00000000  04030201  ....",
            F(CopyFormat.ScreenDump, new CopyOptions { ScreenBytesPerRow = 4, ScreenGroupSize = 4, ScreenReverseGroups = true }, [1, 2, 3, 4]));

        // グループ化 1 では逆順表示は効かない (VIEW-11 の仕様 10)。
        Assert.Equal("00000000  01 02 03 04", F(CopyFormat.ScreenDump, hexOnly with { ScreenReverseGroups = true }, [1, 2, 3, 4]));

        // 中央区切り (8 バイトごとにもう 1 文字)。
        Assert.Equal("00000000  01 02 03 04 05 06 07 08  09 0A",
            F(CopyFormat.ScreenDump, new CopyOptions { ScreenMiddleSeparator = true, ShowText = false }, data));

        // セルの表示形式 (16 bit の 10 進と Hex。単位に満たない端数は 1 バイトずつ Hex。VIEW-10 の仕様 5)。
        CopyOptions int16 = hexOnly with { ScreenCellFormat = Core.View.CellFormat.Int16Decimal };
        Assert.Equal("00000000  00513 01027 02", F(CopyFormat.ScreenDump, int16, [1, 2, 3, 4, 2]));
        Assert.Equal("00000000  3412", F(CopyFormat.ScreenDump, hexOnly with { ScreenCellFormat = Core.View.CellFormat.Int16Hex }, [0x12, 0x34]));
        Assert.Equal("00000000  1234",
            F(CopyFormat.ScreenDump, hexOnly with { ScreenCellFormat = Core.View.CellFormat.Int16Hex, ScreenBigEndian = true }, [0x12, 0x34]));

        // 既定 (グループ化 1、Hex) はこれまでと同じ。
        Assert.Equal("00000000  DE AD BE EF" + new string(' ', 36) + "  ....", F(CopyFormat.ScreenDump));
    }

    [Fact]
    public void Html_and_rtf_include_coloring_rule_colors()
    {
        // 色付けルール (INSP-33 の仕様 8): 00 の文字色を赤、FF の背景色を緑。変更されたバイト (5) は変更の色が文字色より優先。
        byte[] data = [0x00, 0x41, 0xFF, 0x42, 0x00, 0x00, 0x43, 0x44];
        using var doc = new Document(new MemoryByteSource(data), Options());
        ColoringRuleSet rules = ColoringRuleSet.Compile(
            [
                new ColoringRule { Name = "zero", Pattern = "00", Foreground = 0xFF0000 },
                new ColoringRule { Name = "ff", Pattern = "FF", Background = 0x00FF00, Target = ColoringTarget.Hex },
            ], [], null);
        var options = new CopyOptions
        {
            IncludeColors = true,
            ModifiedRanges = [(5, 1)],
            ModifiedColor = "#C42B1C",
            Coloring = rules.ForCopy(doc.Current),
        };

        string html = CopyFormatter.Format(CopyFormat.Html, data, 0, options, out _);
        Assert.Contains("<span style=\"color: #FF0000;\">00</span> 41 <span style=\"background-color: #00FF00;\">FF</span>", html);
        Assert.Contains("<span style=\"color: #C42B1C; font-weight: bold;\">00</span>", html);
        // テキストの列: 00 は文字色、FF のルールは Hex 列だけ。
        Assert.Contains("<span style=\"color: #FF0000;\">.</span>A.B", html);

        string rtf = CopyFormatter.Format(CopyFormat.Rtf, data, 0, options, out _);
        Assert.Contains("{\\colortbl ;\\red196\\green43\\blue28;\\red255\\green0\\blue0;\\red0\\green255\\blue0;}", rtf);
        Assert.Contains("{\\cf2 00} 41 {\\chcbpat3 FF}", rtf);
        Assert.Contains("{\\cf1\\b 00}", rtf);

        // 「色を含める」がオフなら色を書かない。
        string plain = CopyFormatter.Format(CopyFormat.Html, data, 0, options with { IncludeColors = false }, out _);
        Assert.DoesNotContain("<span", plain);

        // パターンが行の境界をまたいでも色が付く (前後を読む)。
        ColoringRuleSet pattern = ColoringRuleSet.Compile([new ColoringRule { Kind = ColoringConditionKind.HexPattern, Pattern = "42 00 00", Background = 0x0000FF }], [], null);
        string rows = CopyFormatter.Format(CopyFormat.Html, data, 0, new CopyOptions { ScreenBytesPerRow = 4, Coloring = pattern.ForCopy(doc.Current) }, out _);
        Assert.Contains("<span style=\"background-color: #0000FF;\">42</span>", rows);
        Assert.Contains("<span style=\"background-color: #0000FF;\">00</span> <span style=\"background-color: #0000FF;\">00</span> 43", rows);
    }
}
