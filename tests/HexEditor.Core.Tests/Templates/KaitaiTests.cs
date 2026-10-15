using System.Diagnostics;
using System.Text;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>.ksy の読み込みと実行 (TPL-33)。</summary>
public sealed class KaitaiTests
{
    private static string Ksy(string name) => Path.Combine(TemplateFeatureTests.DataFolder, "ksy", name);

    private static TemplateResult RunKsy(string name, byte[] data)
    {
        TemplateProgram p = TemplateLoader.LoadFile(Ksy(name));
        Assert.False(p.HasErrors, string.Join("; ", p.Diagnostics) + "\n" + p.ConvertedHxt);
        Assert.True(p.Metadata.IsKaitai);
        return Run(p, TemplateDataSource.FromBytes(data));
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-02")]
    public void InstancesAreEvaluatedOnlyWhenOpened()
    {
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        TemplateResult r = RunKsy("instances.ksy", data);
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.Equal(0, r.EvaluationCounts.GetValueOrDefault("instance:far"));
        TemplateNode far = Node(r, "far");
        Assert.Equal(0, r.EvaluationCounts.GetValueOrDefault("instance:far"));
        Assert.Equal(0x83828180L, far.Value);
        Assert.Equal(1, r.EvaluationCounts.GetValueOrDefault("instance:far"));
        Assert.Equal(0x80L, far.Offset);
        Assert.Equal(0L, Long(r, "first"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-03")]
    public void EndianSwitchOnReadsBothByteOrders()
    {
        foreach (byte[] data in new[] { TestDataCatalog.TiffII(), TestDataCatalog.TiffMM() })
        {
            TemplateResult r = RunKsy("tiff-endian.ksy", data);
            Assert.Equal(TemplateRunState.Completed, r.State);
            Assert.Equal(42L, Long(r, "magic"));
            Assert.Equal(8L, Long(r, "ifd_offset"));
            Assert.Equal(1L, Long(r, "ifd.num_fields"));
            Assert.Equal(0x100L, Long(r, "ifd.fields[0].tag"));
            Assert.Equal(16L, Long(r, "ifd.fields[0].ofs_or_data") >> (data[0] == 0x49 ? 0 : 16));
        }
    }

    /// <summary>TC-TPL-33-01 の代わり (kaitai_struct_formats の取り込みまで)。</summary>
    [Fact]
    public void PngKsyMatchesTheData()
    {
        // kaitai_struct_formats の png.ksy と公式のコンパイラの期待値はリポジトリに取り込んでいないため (取り込みにはダウンロードと
        // コンパイラの導入が要る)、同じ書き方 (contents、valid、enum、repeat-until、switch-on、instances の value) を使うテスト用の
        // .ksy の値を、データの作り方から求めた値と比べる
        byte[] png = TestDataCatalog.Png16();
        TemplateResult r = RunKsy("png-lite.ksy", png);
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity >= TemplateSeverity.Warning);
        Assert.Equal(16L, Long(r, "ihdr.width"));
        Assert.Equal(16L, Long(r, "ihdr.height"));
        Assert.Equal("truecolor (2)", Display(r, "ihdr.color_type"));
        TemplateNode chunks = Node(r, "chunks");
        Assert.Equal(2, chunks.Children.Count);
        Assert.Equal("IDAT", Value(r, "chunks[0].type"));
        Assert.Equal("IEND", Value(r, "chunks[1].type"));
        Assert.Equal(true, Value(r, "chunks[1].is_last"));
        Assert.Equal(false, Value(r, "chunks[0].is_last"));
        Assert.Equal(33L, Node(r, "chunks[0]").Offset);
        Assert.Equal((long)png.Length, Node(r, "chunks[1]").End);
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-04")]
    public void ConvertedHxtGivesTheSameTree()
    {
        foreach ((string name, byte[] data) in new[]
        {
            ("png-lite.ksy", TestDataCatalog.Png16()),
            ("tiff-endian.ksy", TestDataCatalog.TiffMM()),
            ("instances.ksy", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()),
        })
        {
            TemplateProgram ksy = TemplateLoader.LoadFile(Ksy(name));
            TemplateResult a = Run(ksy, TemplateDataSource.FromBytes(data));
            a.EvaluateAll();
            string hxtPath = Path.Combine(Path.GetTempPath(), "HexEditorTests", "ksy-" + Guid.NewGuid().ToString("N") + ".hxt");
            Directory.CreateDirectory(Path.GetDirectoryName(hxtPath)!);
            File.WriteAllText(hxtPath, ksy.ConvertedHxt!, new UTF8Encoding(false));
            try
            {
                TemplateProgram hxt = TemplateLoader.LoadFile(hxtPath);
                Assert.False(hxt.HasErrors, string.Join("; ", hxt.Diagnostics));
                TemplateResult b = Run(hxt, TemplateDataSource.FromBytes(data));
                b.EvaluateAll();
                Assert.Equal(TemplateJsonExporter.ToJson(a), TemplateJsonExporter.ToJson(b));
            }
            finally
            {
                File.Delete(hxtPath);
            }
        }
    }

    [Theory]
    [Trait(TC, "TC-TPL-33-05")]
    [InlineData("indent.ksy", TemplateMessages.YamlSyntax)]
    [InlineData("quote.ksy", TemplateMessages.YamlSyntax)]
    [InlineData("tab.ksy", TemplateMessages.YamlTab)]
    [InlineData("dupkey.ksy", TemplateMessages.YamlDuplicateKey)]
    [InlineData("tag.ksy", TemplateMessages.YamlTag)]
    public void InvalidYamlIsReportedWithAPosition(string file, string code)
    {
        TemplateProgram p = TemplateLoader.LoadFile(Path.Combine(Ksy("bad"), file));
        TemplateDiagnostic e = Assert.Single(p.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(code, e.Code);
        Assert.True(e.Location!.Value.Line >= 1);
        Assert.True(e.Location!.Value.Column >= 1);
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-05")]
    public void DeepNestingAndAliasBombsAreRejected()
    {
        var sw = Stopwatch.StartNew();
        string deep = "meta: {id: deep}\nseq: " + new string('[', 100_000) + new string(']', 100_000) + "\n";
        TemplateProgram p = TemplateLoader.LoadBytes(Encoding.UTF8.GetBytes(deep), "deep.ksy");
        Assert.Contains(p.Diagnostics, d => d.Code == TemplateMessages.YamlTooDeep);

        var laughs = new StringBuilder("meta: {id: laughs}\n");
        laughs.Append("a0: &a0 [lol, lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (int i = 1; i <= 9; i++)
        {
            laughs.Append($"a{i}: &a{i} [").Append(string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 10))).Append("]\n");
        }

        TemplateProgram p2 = TemplateLoader.LoadBytes(Encoding.UTF8.GetBytes(laughs.ToString()), "laughs.ksy");
        Assert.Contains(p2.Diagnostics, d => d.Code == TemplateMessages.YamlAliasLimit);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-05")]
    public void OpaqueTypesFailOnlyThatField()
    {
        TemplateResult r = RunKsy(Path.Combine("bad", "opaque.ksy"), Hex("01 02 03"));
        TemplateNode hidden = Node(r, "hidden");
        Assert.Equal(TemplateNodeKind.Error, hidden.Kind);
        TemplateDiagnostic e = Assert.Single(hidden.Diagnostics);
        Assert.Equal(TemplateMessages.UnsupportedKaitai, e.Code);
        Assert.Equal("Kaitai Struct feature 'opaque-types' is not supported", e.Message);
        Assert.Equal(1L, Long(r, "a"));
        Assert.Equal(2L, Long(r, "b"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-33-06")]
    [Trait("Category", "Nightly")]
    public void MutatedKsyNeverCrashes()
    {
        string[] seeds = [.. new[] { "png-lite.ksy", "tiff-endian.ksy", "instances.ksy" }.Select(n => File.ReadAllText(Ksy(n)))];
        var rng = new Random(3306);
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        const string alphabet = " -:[]{}'\"#&*!|>\n\tabcxyz0123_.";
        for (int i = 0; i < 2000; i++)
        {
            var sb = new StringBuilder(seeds[rng.Next(seeds.Length)]);
            for (int k = rng.Next(1, 6); k > 0 && sb.Length > 0; k--)
            {
                int at = rng.Next(sb.Length);
                switch (rng.Next(3))
                {
                    case 0:
                        sb.Remove(at, Math.Min(rng.Next(1, 4), sb.Length - at));
                        break;
                    case 1:
                        sb.Insert(at, alphabet[rng.Next(alphabet.Length)]);
                        break;
                    default:
                        sb[at] = alphabet[rng.Next(alphabet.Length)];
                        break;
                }
            }

            var sw = Stopwatch.StartNew();
            TemplateProgram p = TemplateLoader.LoadBytes(Encoding.UTF8.GetBytes(sb.ToString()), "fuzz.ksy");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            if (!p.HasErrors)
            {
                TemplateResult r = Run(p, TemplateDataSource.FromBytes(data),
                    new TemplateApplyOptions { LimitOverride = TemplateLimits.Default with { TimeSeconds = 5 } }, TimeSpan.FromSeconds(30));
                r.EvaluateAll();
                TemplateDiagnostic? internalError = r.Diagnostics.FirstOrDefault(d => d.Code == TemplateMessages.Internal);
                Assert.True(internalError is null, $"{internalError?.InternalDetail}\n---\n{sb}");
            }
            else
            {
                TemplateDiagnostic? internalError = p.Diagnostics.FirstOrDefault(d => d.Code == TemplateMessages.Internal);
                Assert.True(internalError is null, $"{internalError?.InternalDetail}\n---\n{sb}");
            }
        }
    }
}
