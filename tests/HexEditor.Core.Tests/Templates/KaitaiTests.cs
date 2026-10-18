using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// TC-TPL-33-01 の参照のフォルダ (環境変数 HEXEDITOR_KAITAI_REF) がないときはスキップする Fact。フォルダは CI のジョブ kaitai-reference
/// (毎晩と手動の実行) が、固定の版のフォーマット集・コンパイラ・実行時ライブラリをダウンロードして作る。開発者の PC では作らない。
/// </summary>
public sealed class KaitaiReferenceFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_KAITAI_REF";

    public KaitaiReferenceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } dir || !Directory.Exists(dir))
        {
            Skip = $"{Variable} がない (CI のジョブ kaitai-reference だけで動く)。";
        }
    }
}

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

    /// <summary>
    /// TC-TPL-33-01 本体。CI のジョブ kaitai-reference だけで動く (HEXEDITOR_KAITAI_REF のフォルダを CI が用意する。なければスキップ)。
    /// フォルダの中: <c>formats/</c> (固定のコミットの kaitai_struct_formats)、<c>data/&lt;TD の ID&gt;.bin</c> (TestDataGen)、
    /// <c>ref/&lt;key&gt;.json</c> (tools/KaitaiReference/dump.js の参照の解析結果)。報告は <c>report/kaitai-report.md</c> に書く。
    /// </summary>
    [KaitaiReferenceFact]
    [Trait(TC, "TC-TPL-33-01")]
    public void KaitaiStructFormatsRunUnchanged()
    {
        string root = Environment.GetEnvironmentVariable(KaitaiReferenceFactAttribute.Variable)!;
        string formatsDir = Path.Combine(root, "formats");
        JsonArray formats = (JsonArray)JsonNode.Parse(File.ReadAllText(Engine.SourceTests.FindRepoFile("tools/KaitaiReference/formats.json")))!;
        IReadOnlyList<KaitaiAllowEntry> allow = KaitaiReferenceComparer.LoadAllowList(KaitaiFixture("allowlist.tsv"));
        var results = new List<KaitaiComparison>();
        string header = string.Empty;
        foreach (JsonObject f in formats.OfType<JsonObject>())
        {
            string key = (string)f["key"]!;
            string ksy = (string)f["ksy"]!;
            var cmp = new KaitaiComparison(key);
            results.Add(cmp);
            string dumpPath = Path.Combine(root, "ref", key + ".json");
            if (!File.Exists(dumpPath))
            {
                cmp.Mismatches.Add(new KaitaiMismatch("(ref)", "reference", "参照の JSON がある", dumpPath));
                continue;
            }

            JsonNode dump = JsonNode.Parse(File.ReadAllText(dumpPath))!;
            header = $"参照: {dump["toolchain"]?.ToJsonString()}";
            byte[] data = File.ReadAllBytes(Path.Combine(root, "data", (string)f["data"]! + ".bin"));
            string ksyPath = Path.Combine(formatsDir, ksy.Replace('/', Path.DirectorySeparatorChar));
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)) != (string?)dump["sha256"]
                || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ksyPath))) != (string?)dump["ksySha256"])
            {
                cmp.Mismatches.Add(new KaitaiMismatch("(data)", "reference", "参照と同じデータと .ksy", "ハッシュが違う"));
                continue;
            }

            // .ksy は変更せずに読む。imports (/common/... など) はフォーマット集の最上位を検索パスにして解決する (TPL-33 の仕様 3)
            TemplateProgram p = TemplateLoader.LoadFile(ksyPath, new TemplateLoadOptions { SearchPaths = [formatsDir] });
            if (p.HasErrors)
            {
                foreach (TemplateDiagnostic d in p.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error))
                {
                    cmp.Mismatches.Add(new KaitaiMismatch("(ksy)", "load", "エラーなく読める", d.ToString()));
                }

                continue;
            }

            TemplateResult r = Run(p, TemplateDataSource.FromBytes(data), timeout: TimeSpan.FromMinutes(2));
            r.EvaluateAll();
            if (r.State != TemplateRunState.Completed)
            {
                cmp.Mismatches.Add(new KaitaiMismatch("(run)", "run", "Completed", $"{r.State}: {string.Join("; ", r.Diagnostics)}"));
            }

            KaitaiComparison c = KaitaiReferenceComparer.Compare(key, dump, JsonNode.Parse(TemplateJsonExporter.ToJson(r))!, allow);
            cmp.Mismatches.AddRange(c.Mismatches);
            cmp.Notes.AddRange(c.Notes);
            cmp.Fields = c.Fields;
            cmp.Values = c.Values;
            cmp.Positions = c.Positions;
            cmp.Extra = c.Extra;
        }

        string report = KaitaiReferenceComparer.ToMarkdown(results, header);
        string reportDir = Path.Combine(root, "report");
        Directory.CreateDirectory(reportDir);
        File.WriteAllText(Path.Combine(reportDir, "kaitai-report.md"), report, new UTF8Encoding(false));
        Assert.True(results.All(x => x.Passed), "kaitai_struct_formats の照合に不一致があります (report/kaitai-report.md):\n"
            + string.Join("\n", results.SelectMany(x => x.Failures.Take(10).Select(m => $"{x.Format}: {m.Path} [{m.Aspect}] 期待 {m.Expected} / 結果 {m.Actual}"))));
    }

    internal static string KaitaiFixture(string name) =>
        Path.Combine(Path.GetDirectoryName(Engine.SourceTests.FindRepoFile("HexEditor.slnx"))!, "tests", "fixtures", "templates", "kaitai", name);

    /// <summary>TC-TPL-33-01 の照合 (KaitaiReferenceComparer) を、手で書いた参照の JSON で確かめる (ダウンロードなしで毎回動く)。</summary>
    [Fact]
    public void ReferenceComparerMatchesAHandmadeDump()
    {
        TemplateResult r = RunKsy("png-lite.ksy", TestDataCatalog.Png16());
        r.EvaluateAll();
        JsonNode ours = JsonNode.Parse(TemplateJsonExporter.ToJson(r))!;
        JsonNode reference = JsonNode.Parse(File.ReadAllText(KaitaiFixture("png-lite.ref.json")))!;
        KaitaiComparison c = KaitaiReferenceComparer.Compare("png-lite", reference, ours, []);
        Assert.True(c.Passed, string.Join("\n", c.Failures));
        Assert.Equal(0, c.Extra);
        Assert.True(c.Fields >= 24, c.Fields.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(c.Positions >= 20);

        // 値・位置・大きさ・欠けたフィールド・配列の要素の数・参照側の失敗を、それぞれ不一致として見つける
        JsonNode Mutated(Action<JsonNode> change)
        {
            JsonNode copy = reference.DeepClone();
            change(copy);
            return copy;
        }

        JsonArray Fields(JsonNode n) => (JsonArray)n["root"]!["fields"]!;
        JsonObject Chunk(JsonNode n, int i) => (JsonObject)((JsonArray)Fields(n)[5]!["items"]!)[i]!;
        (string Path, string Aspect)[] Found(JsonNode changed) =>
            [.. KaitaiReferenceComparer.Compare("png-lite", changed, ours, []).Failures.Select(m => (m.Path, m.Aspect))];

        Assert.Equal([("ihdr.width", "value")], Found(Mutated(n => n["root"]!["fields"]![3]!["fields"]![0]!["value"] = 17)));
        Assert.Equal([("chunks[1].crc", "offset")], Found(Mutated(n => Chunk(n, 1)["fields"]![3]!["offset"] = 555)));
        Assert.Equal([("chunks[0].body", "size")], Found(Mutated(n => Chunk(n, 0)["fields"]![2]!["size"] = 500)));
        Assert.Equal([("chunks[0].type", "value")], Found(Mutated(n => Chunk(n, 0)["fields"]![1]!["value"] = "IDAX")));
        Assert.Equal([("chunks[0].is_last", "value")], Found(Mutated(n => Chunk(n, 0)["fields"]![4]!["value"] = true)));
        Assert.Equal([("magic", "value")], Found(Mutated(n => Fields(n)[0]!["value"] = "89504E470D0A1A0B")));
        Assert.Equal([("extra", "missing")], Found(Mutated(n => Fields(n).Add(JsonNode.Parse("""{"name":"extra","kind":"number","value":1}""")))));
        Assert.Equal([("chunks", "count")], Found(Mutated(n => ((JsonArray)Fields(n)[5]!["items"]!).RemoveAt(1))));
        Assert.Equal([("(root)", "reference")], Found(Mutated(n => n["error"] = "EOF")));
        Assert.Equal([("(ksy)", "compile")], Found(Mutated(n => n["compileError"] = "bad")));

        // 列挙型は数値で比べる。参照側でエラーになったフィールドは比べない。許容リストで認めた違いは失敗にしない
        Assert.Empty(Found(Mutated(n => n["root"]!["fields"]![3]!["fields"]![3]!["error"] = "boom")));
        KaitaiComparison allowed = KaitaiReferenceComparer.Compare("png-lite", Mutated(n => Chunk(n, 0)["fields"]![1]!["value"] = "IDAX"), ours,
            [new KaitaiAllowEntry("png-lite", new System.Text.RegularExpressions.Regex("^chunks\\[.*\\]\\.type$"), "value", "test")]);
        Assert.True(allowed.Passed);
        Assert.Single(allowed.Mismatches);
        Assert.Contains("| png-lite | 合格 |", KaitaiReferenceComparer.ToMarkdown([allowed], "h"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("color_type", "colorType")]
    [InlineData("len_", "len")]
    [InlineData("ofs_local_header", "ofsLocalHeader")]
    [InlineData("b", "b")]
    [InlineData("_unnamed0", "_unnamed0")]
    [InlineData("rgb_32", "rgb32")]
    public void ReferenceNamesUseKaitaiLowerCamelCase(string ksy, string js) => Assert.Equal(js, KaitaiReferenceComparer.LowerCamel(ksy));

    [Fact]
    public void AllowListEntriesCiteTheSpec()
    {
        IReadOnlyList<KaitaiAllowEntry> list = KaitaiReferenceComparer.LoadAllowList(KaitaiFixture("allowlist.tsv"));
        Assert.All(list, e => Assert.Matches(@"TPL-\d+ の仕様 \d+", e.Reason));
        Assert.All(list, e => Assert.Contains(e.Aspect, new[] { "value", "offset", "size", "missing", "count", "error", "all" }));
        Assert.True(list.Count <= 10, "許容リストは短く保つ");
    }

    /// <summary>TC-TPL-33-01 の CI だけの照合を補う、ローカルで毎回動く確認 (同じ書き方のテスト用の .ksy)。</summary>
    [Fact]
    public void PngKsyMatchesTheData()
    {
        // kaitai_struct_formats と公式のコンパイラの期待値は CI のジョブ (kaitai-reference) だけで用意する (ダウンロードが要る) ため、
        // 毎回のテストでは同じ書き方 (contents、valid、enum、repeat-until、switch-on、instances の value) を使うテスト用の
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
