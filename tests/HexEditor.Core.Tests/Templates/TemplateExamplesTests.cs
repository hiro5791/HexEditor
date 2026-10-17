using System.Text;
using HexEditor.Core.Templates;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>仕様書の例 (TPL-01 の PNG、TPL-11 の Section) を実際のデータに適用する。</summary>
public sealed class TemplateExamplesTests
{
    /// <summary>TD-TPL-PNG-EX (仕様書 TPL-01 の 5 の例そのもの)。</summary>
    internal const string PngExample = """
        #pragma name "PNG image"
        #pragma magic 0 "89 50 4E 47 0D 0A 1A 0A"
        #pragma extensions "png;apng"
        #pragma mime "image/png"
        #pragma endian big

        import std.core;
        import std.hash;

        enum ColorType : u8 {
            Grayscale      = 0,
            Truecolor      = 2,
            Indexed        = 3,
            GrayscaleAlpha = 4,
            TruecolorAlpha = 6,
        };

        struct IHDR {
            u32       width;
            u32       height;
            u8        bit_depth;
            ColorType color_type;
            u8        compression;
            u8        filter;
            u8        interlace;
        };

        struct Chunk {
            u32     length;
            char    type[4]                 [[color("accent1")]];
            switch (type) {
                case "IHDR": IHDR header;
                case "tEXt": char text[length] [[encoding("latin1")]];
                default:     u8 data[length] [[lazy]];
            }
            u32     crc                     [[format("hex"),
                                              expect(crc == std::hash::crc32(addressof(type), length + 4),
                                                     "CRC が一致しません")]];
        };

        u8    signature[8] [[comment("PNG シグネチャ")]];
        Chunk chunks[until(last.type == "IEND" || std::eof())];

        """;

    [Fact]
    [Trait(TC, "TC-TPL-01-01")]
    public void PngExampleAppliesToAPngFile()
    {
        TemplateResult r = RunOk(PngExample, TestDataCatalog.Png16());
        Assert.Equal(["signature", "chunks"], r.Root.Children.Select(c => c.Name));
        TemplateNode sig = Node(r, "signature");
        Assert.Equal(8L, sig.Size);
        Assert.Equal("PNG シグネチャ", sig.Comment);
        Assert.Equal("IHDR", Value(r, "chunks[0].type"));
        Assert.Equal(16L, Long(r, "chunks[0].header.width"));
        Assert.Equal(16L, Long(r, "chunks[0].header.height"));
        TemplateNode chunks = Node(r, "chunks");
        Assert.Equal("IEND", Value(r, $"chunks[{chunks.ChildCount - 1}].type"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == TemplateSeverity.Warning);
        Assert.Equal("Truecolor (0x2)", Display(r, "chunks[0].header.color_type"));
        Assert.Equal("accent1", Node(r, "chunks[0].type").Attributes.Color);
        Golden.Check("TC-TPL-01-01", TemplateJsonExporter.ToJson(r));
    }

    [Fact]
    [Trait(TC, "TC-TPL-01-03")]
    public void CrLfLfAndBomGiveTheSameResult()
    {
        string lf = PngExample.Replace("\r\n", "\n", StringComparison.Ordinal);
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] png = TestDataCatalog.Png16();
        string Json(byte[] template)
        {
            TemplateResult res = Run(TemplateLoaderBytes(template), TemplateDataSource.FromBytes(png));
            Assert.Equal(TemplateRunState.Completed, res.State);
            return TemplateJsonExporter.ToJson(res);
        }

        byte[] bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(lf)];
        string a = Json(Encoding.UTF8.GetBytes(lf));
        Assert.Equal(a, Json(Encoding.UTF8.GetBytes(crlf)));
        Assert.Equal(a, Json(bom));

        // 12 行目 (IHDR の width の行) の ';' を消す
        static string Break(string text, string nl)
        {
            string[] lines = text.Split(nl);
            int i = Array.FindIndex(lines, l => l.Contains("u32       width;", StringComparison.Ordinal));
            lines[i] = lines[i].Replace("width;", "width", StringComparison.Ordinal);
            return string.Join(nl, lines);
        }

        var e1 = TemplateLoaderBytes(Encoding.UTF8.GetBytes(Break(lf, "\n"))).Diagnostics.Single(d => d.Severity == TemplateSeverity.Error);
        var e2 = TemplateLoaderBytes(Encoding.UTF8.GetBytes(Break(crlf, "\r\n"))).Diagnostics.Single(d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(e1.Location!.Value.Line, e2.Location!.Value.Line);
        Assert.Equal(e1.Location!.Value.Column, e2.Location!.Value.Column);
        Assert.Equal(TemplateMessages.Expected, e1.Code);
    }

    private static Core.Templates.Loading.TemplateProgram TemplateLoaderBytes(byte[] bytes) =>
        Core.Templates.Loading.TemplateLoader.LoadBytes(bytes, "png-example.hxt");

    private const string SectionExample = """
        fn align_up(u64 v, u64 a) -> u64 {
            return (v + a - 1) / a * a;
        }

        struct Section {
            char  name[8];
            u32   virtual_size;
            u32   virtual_address;
            u32   raw_size;
            u32   raw_offset;
            local u64 end = align_up(raw_offset + raw_size, 0x200) [[show]];
        };

        struct Hdr { u8 section_count; u8 kind; };
        Hdr hdr @ 0x50;
        switch (hdr.kind) { case 0: u8 zero @ 0x52; case 1 ... 5: u8 small @ 0x52; default: u8 other @ 0x52; }

        for (local u32 i = 0; i < hdr.section_count; i += 1) {
            Section section;
        }
        """;

    [Fact]
    [Trait(TC, "TC-TPL-11-01")]
    public void SwitchRangeLoopPatternsAndShownLocals()
    {
        byte[] data = new byte[0x53];
        Encoding.ASCII.GetBytes(".text").CopyTo(data, 0);
        Encoding.ASCII.GetBytes(".data").CopyTo(data, 24);
        BitConverter.GetBytes(0x10u).CopyTo(data, 24 + 16);
        BitConverter.GetBytes(0x1234u).CopyTo(data, 24 + 20);
        data[0x50] = 2;
        data[0x51] = 3;
        data[0x52] = 0xFF;
        TemplateResult r = RunOk(SectionExample, data);
        Assert.NotNull(r.FindByPath("small"));
        Assert.Null(r.FindByPath("zero"));
        Assert.Null(r.FindByPath("other"));
        Assert.Contains(r.Root.Children, c => c.Name == "section[0]");
        Assert.Contains(r.Root.Children, c => c.Name == "section[1]");
        TemplateNode end = Node(r, "section[1].end");
        Assert.Equal(TemplateNodeKind.Computed, end.Kind);
        Assert.Equal(0x1400L, Long(r, "section[1].end"));
        Assert.Null(r.FindByPath("i"));
    }
}
