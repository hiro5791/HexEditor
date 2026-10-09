using System.Text;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Formats;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>エンコード形式 (TOOL-07)、Hex テキスト・10 進テキスト (TOOL-08)、ソースコードの配列 (TOOL-09) の読み書き。</summary>
public sealed class TextFormatTests
{
    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    public static TheoryData<string, string> EncodingVariants()
    {
        var data = new TheoryData<string, string>();
        foreach (string url in new[] { "std", "url" })
        {
            foreach (string line in new[] { "76", "64", "0" })
            {
                foreach (string pad in new[] { "pad", "nopad" })
                {
                    data.Add(FormatIds.Base64, $"{url},{line},{pad}");
                }
            }
        }

        data.Add(FormatIds.Base32, "pad");
        data.Add(FormatIds.Base32, "nopad");
        data.Add(FormatIds.Ascii85, "delimiters");
        data.Add(FormatIds.Ascii85, "bare");
        data.Add(FormatIds.UUEncode, string.Empty);
        data.Add(FormatIds.QuotedPrintable, string.Empty);
        data.Add(FormatIds.Url, "unreserved");
        data.Add(FormatIds.Url, "all");
        return data;
    }

    private static ExportOptions EncodingOptions(string format, string variant)
    {
        string[] v = variant.Split(',');
        CopyOptions copy = format switch
        {
            FormatIds.Base64 => new CopyOptions
            {
                Base64UrlSafe = v[0] == "url",
                Base64LineLength = int.Parse(v[1]),
                Base64Padding = v[2] == "pad",
            },
            FormatIds.Base32 => new CopyOptions { Base32Padding = variant == "pad", Base32LineLength = 64 },
            FormatIds.Ascii85 => new CopyOptions { Ascii85Delimiters = variant == "delimiters", Ascii85LineLength = 75 },
            FormatIds.Url => new CopyOptions { UrlKeepUnreserved = variant == "unreserved" },
            _ => new CopyOptions(),
        };
        return new ExportOptions { Format = format, Copy = copy };
    }

    [Theory]
    [Trait(TC, "TC-TOOL-07-01")]
    [MemberData(nameof(EncodingVariants))]
    public void Every_encoding_round_trips(string format, string variant)
    {
        foreach (byte[] data in new[] { Bytes256, Array.Empty<byte>() })
        {
            string text = Export(data, EncodingOptions(format, variant));
            using ImportResult result = Import(format, text);
            Assert.True(result.Issues.Count == 0, string.Join("; ", result.Issues.Items.Select(i => $"{i.Line}:{i.Column} {i.Kind} {i.Content}")));
            Assert.Equal(data, Bytes(result));
        }
    }

    [Fact]
    public void Line_lengths_are_applied()
    {
        string base64 = Export(Bytes256, new ExportOptions { Format = FormatIds.Base64, Copy = new CopyOptions { Base64LineLength = 64 } });
        Assert.All(base64.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[..^1], line => Assert.Equal(64, line.Length));
        string a85 = Export(Bytes256, new ExportOptions { Format = FormatIds.Ascii85, Copy = new CopyOptions { Ascii85LineLength = 75 } });
        Assert.All(a85.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[..^1], line => Assert.Equal(75, line.Length));
        string uu = Export(Bytes256, new ExportOptions { Format = FormatIds.UUEncode, Copy = new CopyOptions { EncodedFileMode = "600", EncodedFileName = "x.bin" } });
        Assert.StartsWith("begin 600 x.bin\r\n", uu);
    }

    public static TheoryData<string, string, string> Rfc4648Vectors() => new()
    {
        { "", "", "" },
        { "f", "Zg==", "MY======" },
        { "fo", "Zm8=", "MZXQ====" },
        { "foo", "Zm9v", "MZXW6===" },
        { "foob", "Zm9vYg==", "MZXW6YQ=" },
        { "fooba", "Zm9vYmE=", "MZXW6YTB" },
        { "foobar", "Zm9vYmFy", "MZXW6YTBOI======" },
    };

    [Theory]
    [Trait(TC, "TC-TOOL-07-02")]
    [MemberData(nameof(Rfc4648Vectors))]
    public void Rfc_4648_test_vectors(string input, string base64, string base32)
    {
        byte[] data = Encoding.ASCII.GetBytes(input);
        var b64 = new ExportOptions { Format = FormatIds.Base64, Copy = new CopyOptions { Base64LineLength = 0 }, FinalNewLine = false };
        var b32 = new ExportOptions { Format = FormatIds.Base32, FinalNewLine = false };
        Assert.Equal(base64, Export(data, b64));
        Assert.Equal(base32, Export(data, b32));
        using ImportResult r64 = Import(FormatIds.Base64, base64);
        using ImportResult r32 = Import(FormatIds.Base32, base32);
        Assert.Equal(data, Bytes(r64));
        Assert.Equal(data, Bytes(r32));
    }

    [Fact]
    public void Pem_armor_is_removed()
    {
        using ImportResult result = ImportFile(FormatIds.Base64, TestDataCatalog.Get("TD-TOOL-PEM"));
        Assert.Equal(0, result.Issues.Count);
        Assert.Equal(TestDataCatalog.PemDer(), Bytes(result));
        Assert.Equal(FormatIds.Base64, Importer.DetectFile(TestDataCatalog.Get("TD-TOOL-PEM")));
    }

    [Fact]
    public void Invalid_characters_are_listed_and_skipped()
    {
        using ImportResult result = Import(FormatIds.Base64, "3q2+\r\n7w*==\r\n");
        ImportIssue issue = Assert.Single(result.Issues.Items);
        Assert.Equal((2, 3, ImportIssueKind.InvalidCharacter), (issue.Line, issue.Column, issue.Kind));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Bytes(result));
    }

    [Fact]
    public void Base64_settings_are_detected_for_saving()
    {
        using ImportResult result = ImportFile(FormatIds.Base64, TestDataCatalog.Get("TD-BASE64"));
        EncodedFileSettings s = result.Settings!;
        Assert.Equal((76, "\r\n", true, false, true), (s.LineLength, s.NewLine, s.Padding, s.UrlSafe, s.FinalNewLine));
    }

    // ---- Hex テキスト・10 進テキスト ----

    [Theory]
    [Trait(TC, "TC-TOOL-08-01")]
    [InlineData("xxd.txt")]
    [InlineData("hexdump-C.txt")]
    [InlineData("od.txt")]
    [InlineData("certutil.txt")]
    [InlineData("app.txt")]
    public void Dumps_are_read_back(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(TestDataCatalog.Get("TD-TOOL-DUMPS"))!, name);
        using ImportResult result = ImportFile(FormatIds.HexText, path);
        Assert.Equal(0, result.Issues.Count);
        Assert.Equal(Bytes256, Bytes(result));
    }

    [Fact]
    public void The_app_dump_matches_the_text_dump_export()
    {
        string expected = TestDataCatalog.DumpTexts()["app.txt"];
        Assert.Equal(expected, Export(Bytes256, new ExportOptions { Format = FormatIds.DumpText }));
    }

    [Fact]
    public void Plain_hex_text_accepts_the_input_rules()
    {
        using ImportResult result = Import(FormatIds.HexText, "DE AD,0xBE \\xEF\r\nCAFE");
        Assert.Equal(0, result.Issues.Count);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE }, Bytes(result));
        using ImportResult odd = Import(FormatIds.HexText, "DE A");
        Assert.Equal(ImportIssueKind.OddDigits, Assert.Single(odd.Issues.Items).Kind);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-08-02")]
    public void Decimal_text_import()
    {
        using ImportResult bytes = Import(FormatIds.DecimalText, "255, 0, 128");
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x80 }, Bytes(bytes));
        using ImportResult word = Import(FormatIds.DecimalText, "258", new ImportOptions { ValueSize = 2, BigEndian = true });
        Assert.Equal(new byte[] { 0x01, 0x02 }, Bytes(word));
        using ImportResult range = Import(FormatIds.DecimalText, "256");
        ImportIssue issue = Assert.Single(range.Issues.Items);
        Assert.Equal((1, 1, ImportIssueKind.OutOfRange), (issue.Line, issue.Column, issue.Kind));
    }

    [Fact]
    public void Hex_and_decimal_text_round_trip()
    {
        var hex = new ExportOptions { Format = FormatIds.HexText, HexBytesPerLine = 0, HexSeparator = ", ", HexPrefix = "0x", HexOffsets = false };
        using ImportResult h = Import(FormatIds.HexText, Export(Bytes256, hex));
        Assert.Equal(Bytes256, Bytes(h));
        var withOffsets = new ExportOptions { Format = FormatIds.HexText, HexOffsets = true };
        using ImportResult o = Import(FormatIds.HexText, Export(Bytes256, withOffsets));
        Assert.Equal(Bytes256, Bytes(o));
        foreach ((int size, bool signed, bool big) in new[] { (1, false, false), (2, true, true), (4, false, false), (8, true, false) })
        {
            var dec = new ExportOptions { Format = FormatIds.DecimalText, DecimalValueSize = size, DecimalSigned = signed, DecimalBigEndian = big };
            using ImportResult d = Import(FormatIds.DecimalText, Export(Bytes256, dec), new ImportOptions { ValueSize = size, Signed = signed, BigEndian = big });
            Assert.Equal(Bytes256, Bytes(d));
        }
    }

    // ---- ソースコードの配列 ----

    [Fact]
    [Trait(TC, "TC-TOOL-09-03")]
    public void C_array_import()
    {
        using ImportResult a = Import(FormatIds.C, "unsigned char a[] = { 0x01, 0x02, 'A' };");
        Assert.Equal(new byte[] { 0x01, 0x02, 0x41 }, Bytes(a));
        using ImportResult b = Import(FormatIds.C, "uint16_t b[] = { 0x0102, 258 };", new ImportOptions { ValueSize = 0 });
        Assert.Equal(2, b.InferredValueSize);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x02, 0x01 }, Bytes(b));
        const string s = "const char s[] = \"A\\n\\x7F\";";
        using ImportResult c = Import(FormatIds.C, s, new ImportOptions { ValueSize = 0 });
        Assert.Equal(new byte[] { 0x41, 0x0A, 0x7F }, Bytes(c));

        // 「形式を選択して貼り付け」と同じ結果 (終端の 00 は含めない)。
        PasteCandidate paste = PasteDetector.Best(PasteDetector.Detect(s))!;
        Assert.Equal(Bytes(c), paste.Bytes);
    }

    [Fact]
    public void Exported_arrays_import_back()
    {
        foreach (string format in FormatIds.SourceArrays)
        {
            foreach (int size in new[] { 1, 2, 4, 8 })
            {
                var options = new ExportOptions { Format = format, Copy = new CopyOptions { ElementSize = size }, SourceComment = true };
                string text = Export(Bytes256, options);
                using ImportResult result = Import(format, text, new ImportOptions { ValueSize = size });
                Assert.True(result.Issues.Count == 0, $"{format} {size}: {string.Join("; ", result.Issues.Items.Select(i => i.Content))}\n{text}");
                Assert.Equal(Bytes256, Bytes(result));
            }
        }
    }

    [Fact]
    public void C_header_file_has_guard_and_length()
    {
        var options = new ExportOptions { Format = FormatIds.C, HeaderFile = true, LengthConstant = true, SourceComment = false };
        string text = Export([1, 2, 3], options);
        Assert.StartsWith("#ifndef DATA_H\r\n#define DATA_H\r\n", text);
        Assert.Contains("#define DATA_LEN 3\r\n", text);
        Assert.Contains("#endif /* DATA_H */", text);
    }

    // ---- 形式の自動判定 ----

    [Theory]
    [InlineData("TD-IHEX", FormatIds.IntelHex)]
    [InlineData("TD-SREC", FormatIds.SRecord)]
    [InlineData("TD-BASE64", FormatIds.Base64)]
    [InlineData("TD-TOOL-HEXTEXT", FormatIds.HexText)]
    [InlineData("TD-TOOL-IHEX-BADSUM", FormatIds.IntelHex)]
    [InlineData("TD-BYTES-256", FormatIds.Binary)]
    public void Format_is_detected_from_the_content(string id, string expected) =>
        Assert.Equal(expected, Importer.DetectFile(TestDataCatalog.Get(id)));
}
