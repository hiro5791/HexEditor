using System.Globalization;
using System.IO.Compression;
using System.Text;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>インポート・属性・書式・セクション・プラグマ・標準ライブラリ・拡張点 (TPL-12〜TPL-19)。</summary>
public sealed class TemplateFeatureTests
{
    internal static string DataFolder => Path.Combine(Path.GetDirectoryName(Core.Tests.Engine.SourceTests.FindRepoFile("HexEditor.slnx"))!,
        "tests", "fixtures", "templates", "data");

    /// <summary>読み込み (と、リンクの確認) を記録するファイルシステム。</summary>
    internal sealed class RecordingFileSystem : ITemplateFileSystem
    {
        public List<string> Reads { get; } = [];

        public List<string> Probes { get; } = [];

        public bool FileExists(string fullPath)
        {
            Probes.Add(fullPath);
            return File.Exists(fullPath);
        }

        public byte[] ReadAllBytes(string fullPath)
        {
            Reads.Add(fullPath);
            return PhysicalTemplateFileSystem.Instance.ReadAllBytes(fullPath);
        }

        public string? ResolveLink(string fullPath) => PhysicalTemplateFileSystem.Instance.ResolveLink(fullPath);
    }

    [Fact]
    [Trait(TC, "TC-TPL-12-01")]
    public void ImportStdPeGivesPeTypes()
    {
        TemplateResult r = RunOk("import std.pe; std::pe::DosHeader dos; std::pe::NtHeaders nt @ dos.e_lfanew;", TestDataCatalog.PeX64());
        Assert.Equal(0x5A4DL, Long(r, "dos.e_magic"));
        Assert.StartsWith("MZ", Display(r, "dos.e_magic"), StringComparison.Ordinal);
        Assert.Equal(0x00004550L, Long(r, "nt.Signature"));
        Assert.Equal("AMD64 (0x8664)", Display(r, "nt.FileHeader.Machine"));
        Assert.Equal(["dos", "nt"], r.Root.Children.Select(c => c.Name));
    }

    [Fact]
    [Trait(TC, "TC-TPL-12-02")]
    public void DuplicateImportsAreReadOnceAndNamespacesDoNotCollide()
    {
        var fs = new RecordingFileSystem();
        string dir = Path.Combine(DataFolder, "import-set");
        TemplateProgram p = TemplateLoader.LoadFile(Path.Combine(dir, "main.hxt"), new TemplateLoadOptions { FileSystem = fs });
        Assert.False(p.HasErrors, string.Join("; ", p.Diagnostics));
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(Hex("01 02 03 04 05 06")));
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.Equal("Rec", Node(r, "r1").TypeName);
        Assert.Equal(1L, Long(r, "r1.x"));
        Assert.Equal(0x0302L, Long(r, "r2.y"));
        Assert.Single(fs.Reads, f => f.EndsWith(Path.Combine("lib", "common.hxt"), StringComparison.OrdinalIgnoreCase));

        TemplateProgram cycle = TemplateLoader.LoadFile(Path.Combine(dir, "main-cycle.hxt"));
        Assert.False(cycle.HasErrors, string.Join("; ", cycle.Diagnostics));
        TemplateResult rc = Run(cycle, TemplateDataSource.FromBytes(Hex("01 02 03 04 05 06")));
        Assert.Equal(TemplateRunState.Completed, rc.State);
        Assert.Equal(1L, Long(rc, "v.v"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-12-03")]
    public void ImportsOutsideTheSearchPathsAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "HexEditorTests", "tpl-escape-" + Guid.NewGuid().ToString("N"));
        string templates = Path.Combine(root, "templates");
        Directory.CreateDirectory(Path.Combine(templates, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(root, "outside.hxt"), "struct Outside { u8 x; };");
            File.WriteAllText(Path.Combine(templates, "sub", "escape.hxt"), "import \"../../outside.hxt\"; Outside o;");
            File.WriteAllText(Path.Combine(templates, "escape2.hxt"), "import \"sub/../../outside.hxt\"; Outside o;");

            foreach (string file in new[] { Path.Combine(templates, "sub", "escape.hxt"), Path.Combine(templates, "escape2.hxt") })
            {
                var fs = new RecordingFileSystem();
                TemplateProgram p = TemplateLoader.LoadFile(file, new TemplateLoadOptions { SearchPaths = [templates], FileSystem = fs });
                TemplateDiagnostic e = Assert.Single(p.Diagnostics, d => d.Severity == TemplateSeverity.Error && d.Code == TemplateMessages.NotPermitted);
                Assert.Contains("outside the template search paths", e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(fs.Reads, f => f.EndsWith("outside.hxt", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(fs.Probes, f => f.EndsWith("outside.hxt", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-13-02")]
    public void HiddenInlineExpectAndUnknownAttributes()
    {
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        TemplateResult r = RunOk("u8 h [[hidden]]; struct In { u8 x; u8 y; }; In i [[inline]]; u8 e [[expect(e == 0xFF, \"FF のはず\")]]; u8 z [[colour(\"red\")]]; u8 last;", data);
        string json = TemplateJsonExporter.ToJson(r);
        Assert.DoesNotContain("\"path\": \"h\"", json, StringComparison.Ordinal);
        TemplateNode h = r.Root.Children.Single(c => c.Name == "h");
        Assert.True(h.Attributes.Hidden);
        Assert.Equal((0L, 1L), (h.Offset, h.Size)); // 色分けには 0x0 の 1 バイトの範囲がある (Hex ビューの色分けはする)
        Assert.Equal(["h", "x", "y", "e", "z", "last"], r.Root.Children.Select(c => c.Name));
        Assert.Equal("x", Node(r, "x").Path);
        TemplateNode e = Node(r, "e");
        TemplateDiagnostic w = Assert.Single(e.Diagnostics);
        Assert.Equal(TemplateSeverity.Warning, w.Severity);
        Assert.Equal("FF のはず", w.Message);
        Assert.Contains(r.Diagnostics, d => d.Code == TemplateMessages.UnknownAttribute && d.Arguments[0] == "colour");
        Assert.Equal(5L, Long(r, "last"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-14-01")]
    public void HexFormatReadWriteConversionAndFormatErrors()
    {
        const string t = "le u32 h [[format(\"hex\")]]; le u32 p [[comment(std::format(\"{:08X}\", p))]];"
            + " le s16 temp [[read(fn(v) => v / 10.0), write(fn(v) => v * 10)]];"
            + " fn bad(ref u8 v) -> string { return std::format(\"{}\", 1 / 0); } u8 b [[format(bad)]]; u8 after;"
            + " local auto check = temp + 0 [[show]];";
        TemplateResult r = RunOk(t, Hex("1F 00 00 00 1F 00 00 00 EB 00 05 06"));
        Assert.Equal("0x1F", Display(r, "h"));
        Assert.Equal("0000001F", Node(r, "p").Comment);
        Assert.Equal("23.5", Display(r, "temp"));
        Assert.Equal(23.5, Value(r, "check"));
        string b = Display(r, "b");
        Assert.StartsWith("<error:", b, StringComparison.Ordinal);
        Assert.Contains("Division by zero", b, StringComparison.Ordinal);
        Assert.NotNull(Node(r, "b").DisplayError);
        Assert.Equal(6L, Long(r, "after"));

        TemplateEditResult edit = Node(r, "temp").EncodeValue("-12.5");
        Assert.True(edit.Success, edit.Error);
        Assert.Equal(8L, edit.Offset);
        Assert.Equal(Hex("83 FF"), edit.Bytes);
    }

    [Fact]
    [Trait(TC, "TC-TPL-15-01")]
    public void PngIdatSectionHoldsRows()
    {
        TemplateProgram p = TemplateLoader.LoadFile(Path.Combine(DataFolder, "png-section.hxt"));
        Assert.False(p.HasErrors, string.Join("; ", p.Diagnostics));
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(TestDataCatalog.TplPngIdat()));
        Assert.Equal(TemplateRunState.Completed, r.State);
        TemplateNode section = r.Root.Children.Single(c => c.Kind == TemplateNodeKind.Section);
        Assert.Equal("pixels", section.Name);
        Assert.Equal(784L, section.Size);
        TemplateNode rows = Assert.Single(section.Children);
        Assert.Equal("rows", rows.Name);
        Assert.Equal(16, rows.Children.Count);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal((long)(i % 5), Convert.ToInt64(rows.Children[i].Children[0].Value, CultureInfo.InvariantCulture));
        }

        Assert.Equal(784L, r.Sections.Single().Length);
        Assert.Equal(TemplateEditBlock.InSection, Node(r, "@pixels.rows[1].filter").EditBlock);

        // セクションを読み取り専用のデータソースとして開ける (TPL-15 の仕様 4)
        using IByteSource src = r.Sections.Single().CreateByteSource();
        Assert.Equal(784L, src.Length);
        Assert.False(src.Capabilities.HasFlag(SourceCapabilities.CanWrite));
        byte[] head = new byte[4];
        src.Read(0, head);
        Assert.Equal(TestDataCatalog.TplPngIdatRaw()[..4], head);
    }

    /// <summary>
    /// TD-TPL-ZBOMB-10G と同じ内容 (u32 の長さと、10 GiB の 00 の zlib ストリーム) を、読まれた分だけ作るデータソース。展開は上限で
    /// 打ち切られるため、先頭の一部しか読まれない。
    /// </summary>
    internal sealed class ZlibBombSource : TemplateDataSource
    {
        private readonly MemoryStream _produced = new();
        private readonly ZLibStream _zlib;
        private readonly byte[] _zeros = new byte[1024 * 1024];
        private long _fed;

        public ZlibBombSource()
        {
            _zlib = new ZLibStream(_produced, CompressionLevel.SmallestSize, leaveOpen: true);
        }

        /// <summary>見かけの圧縮後の長さ (u32 の範囲)。</summary>
        public const uint CompressedLength = 10_500_000;

        public override long Length => 4 + CompressedLength;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            unreadableAt = -1;
            lock (_produced)
            {
                int n = (int)Math.Min(destination.Length, Length - offset);
                for (int i = 0; i < n; i++)
                {
                    long pos = offset + i;
                    if (pos < 4)
                    {
                        destination[i] = (byte)(CompressedLength >> (8 * (int)pos));
                        continue;
                    }

                    while (_produced.Length <= pos - 4 && _fed < 10L * 1024 * 1024 * 1024)
                    {
                        _zlib.Write(_zeros);
                        _zlib.Flush();
                        _fed += _zeros.Length;
                    }

                    byte[] buf = _produced.GetBuffer();
                    destination[i] = pos - 4 < _produced.Length ? buf[pos - 4] : (byte)0;
                }

                return n;
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-15-03")]
    public void DecompressionBombStopsAtTheSectionLimit()
    {
        const string t = "u32 len; section s; std::compress::zlib(s, 4, len); u8 tail @ 0 in s; u8 last @ std::section::size(s) - 1 in s;";
        TemplateResult r = Run(Load(t), new ZlibBombSource(), timeout: TimeSpan.FromSeconds(120));
        Assert.Equal(TemplateRunState.Completed, r.State);
        TemplateSection s = r.Sections.Single();
        Assert.Equal(268_435_456L, s.Length);
        Assert.True(s.Truncated);
        Assert.True(s.MemoryLength <= 64L * 1024 * 1024);
        Assert.True(s.FileLength <= 256L * 1024 * 1024);
        TemplateDiagnostic w = Assert.Single(r.Diagnostics, d => d.Code == TemplateMessages.DecompressLimit);
        Assert.Equal("256 MB", w.Arguments[0]);
        Assert.Equal(268_435_455L, Node(r, "@s.last").Offset);
        r.Dispose();

        TemplateResult r2 = Run(Load("#pragma limit.section 1048576\n" + t), new ZlibBombSource(), timeout: TimeSpan.FromSeconds(60));
        Assert.Equal(1_048_576L, r2.Sections.Single().Length);
        Assert.Equal("1 MB", Assert.Single(r2.Diagnostics, d => d.Code == TemplateMessages.DecompressLimit).Arguments[0]);
        r2.Dispose();
    }

    [Fact]
    public void EncodingConvertReencodesTextIntoASection()
    {
        // TPL-18 の std.encoding の「文字コードの変換」: UTF-16LE の "AB" を UTF-8 にしたセクション
        TemplateResult r = RunOk("section s; std::encoding::convert(s, 0, 4, \"utf-16le\", \"utf-8\"); char t[2] @ 0 in s; local u64 n = std::section::size(s) [[show]];",
            Hex("41 00 42 00"));
        Assert.Equal(2L, Long(r, "n"));
        Assert.Equal("AB", Display(r, "@s.t"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-17-02")]
    public void NegativeMagicOffsetIsFromTheEnd()
    {
        TemplateMetadata m = Load("#pragma magic -4 \"49 45 4E 44\"\nu8 x;").Metadata;
        byte[] iend = Hex("49 45 4E 44");
        Assert.NotNull(m.Match(TemplateMatchInput.FromBytes([.. new byte[100], .. iend])));
        Assert.Null(m.Match(TemplateMatchInput.FromBytes([.. iend, .. new byte[100]])));
        Assert.NotNull(m.Match(TemplateMatchInput.FromBytes(iend)));
        Assert.Null(m.Match(TemplateMatchInput.FromBytes(Hex("45 4E 44"))));
        Assert.True(m.Match(TemplateMatchInput.FromBytes(iend))!.Magic);
    }

    [Fact]
    [Trait(TC, "TC-TPL-17-03")]
    public void LimitAboveTheMaximumIsClampedWithAWarning()
    {
        // 最大値 (1e9 ステップ) で止まることの確認は時間がかかるため、最大値を下げた設定で同じ処理を確かめる
        var options = new TemplateApplyOptions { MaximumLimits = TemplateLimits.DefaultMaximum with { Steps = 50_000 } };
        TemplateResult r = Run(Load("#pragma limit.steps 5000000000\nwhile (true) {}"), TemplateDataSource.FromBytes([]), options);
        TemplateDiagnostic w = Assert.Single(r.Diagnostics, d => d.Code == TemplateMessages.LimitClamped);
        Assert.Equal("limit.steps", w.Arguments[0]);
        Assert.Equal(50_000L, r.Limits.Steps);
        Assert.Equal(TemplateLimitKind.Steps, r.LimitReached);
        Assert.InRange(r.StepCount, 50_000, 50_002);

        TemplateResult r2 = Run("#pragma limit.steps 2000\nwhile (true) {}", []);
        Assert.DoesNotContain(r2.Diagnostics, d => d.Code == TemplateMessages.LimitClamped);
        Assert.Equal(2000L, r2.Limits.Steps);
        Assert.InRange(r2.StepCount, 2000, 2002);

        // 既定の最大値は 1e9
        TemplateResult r3 = Run(Load("#pragma limit.steps 5000000000\nu8 x;"), TemplateDataSource.FromBytes([1]));
        Assert.Equal(1_000_000_000L, r3.Limits.Steps);
    }

    [Fact]
    [Trait(TC, "TC-TPL-17-03")]
    [Trait("Category", "Nightly")]
    public void LimitAboveTheMaximumStopsAtOneBillionSteps()
    {
        TemplateResult r = Run(Load("#pragma limit.steps 5000000000\nwhile (true) {}"), TemplateDataSource.FromBytes([]), timeout: TimeSpan.FromMinutes(10));
        Assert.Equal(TemplateLimitKind.Steps, r.LimitReached);
        Assert.InRange(r.StepCount, 1_000_000_000, 1_000_000_002);
    }

    [Fact]
    [Trait(TC, "TC-TPL-17-04")]
    public void AutoApplyStopsAfterFiveSeconds()
    {
        TemplateProgram p = TemplateLoader.LoadFile(Path.Combine(DataFolder, "slow.hxt"));
        byte[] data = [.. "SLOW"u8, .. new byte[12]];
        Assert.NotNull(p.Metadata.Match(TemplateMatchInput.FromBytes(data)));
        // 速い PC では 5 秒より前に文の実行回数の上限 (1 億) に達するため、このテストでは回数の上限を外す
        TemplateResult auto = Run(p, TemplateDataSource.FromBytes(data),
            new TemplateApplyOptions { IsAutoApply = true, LimitOverride = TemplateLimits.Default with { Steps = long.MaxValue } }, TimeSpan.FromSeconds(30));
        Assert.Equal(TemplateLimitKind.Time, auto.LimitReached);
        Assert.InRange(auto.Elapsed.TotalSeconds, 4.9, 30);
        Support.TimeBudget.Limit(auto.Elapsed.TotalSeconds <= 5.5, $"自動適用の評価が {auto.Elapsed.TotalSeconds:F2} 秒で止まりました");
        Assert.NotNull(auto.FindByPath("magic"));

        // 手動の適用には時間の上限がない (既定は無制限)
        TemplateResult manual = TemplateEngine.Apply(p, TemplateDataSource.FromBytes(data), new TemplateApplyOptions { LimitOverride = TemplateLimits.Default with { Steps = long.MaxValue } });
        Assert.False(Wait(manual.Completion, TimeSpan.FromSeconds(6)));
        Assert.Equal(TemplateRunState.Running, manual.State);
        manual.Cancel();
        Assert.True(Wait(manual.Completion, TimeSpan.FromSeconds(10)));
        Assert.Equal(TemplateRunState.Cancelled, manual.State);
    }

    [Theory]
    [Trait(TC, "TC-TPL-18-01")]
    [InlineData("tr-TR")]
    [InlineData("az-Latn-AZ")]
    [InlineData("en-US")]
    public void ReadU32EndianAndCultureIndependentCase(string culture)
    {
        CultureInfo save = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            TemplateResult r = RunOk("local u32 b = std::mem::read_u32(0, big) [[show]]; local u32 l = std::mem::read_u32(0, little) [[show]];"
                + " local string up = std::string::to_upper(\"i\") [[show]]; local string lo = std::string::to_lower(\"I\") [[show]];", Hex("12 34 56 78"));
            Assert.Equal(0x12345678L, Long(r, "b"));
            Assert.Equal(0x78563412L, Long(r, "l"));
            Assert.Equal("I", Value(r, "up"));
            Assert.Equal("i", Value(r, "lo"));

            TemplateResult r2 = Run("local u32 x = std::mem::read_u32(1, big);", Hex("12 34 56 78"));
            TemplateDiagnostic e = Assert.Single(r2.Diagnostics, d => d.Severity == TemplateSeverity.Error);
            Assert.Equal(TemplateMessages.ReadOutOfRange, e.Code);
            Assert.Equal("read_u32(0x1) is outside the data", e.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = save;
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-18-02")]
    public void HashFunctionsMatchTheHashPanel()
    {
        TemplateResult r = RunOk("local u32 c = std::hash::crc32(0, 9) [[show]];", "123456789"u8.ToArray());
        Assert.Equal(0xCBF43926L, Long(r, "c"));

        byte[] data = new byte[16 * 1024 * 1024];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, data);
        var ranges = new List<(long Off, long Len)> { (0, 0), (5, 1), (100, 4095), (12345, 1024 * 1024) };
        var rng = new System.Random(18);
        while (ranges.Count < 20)
        {
            long off = rng.Next(0, data.Length - 70000);
            ranges.Add((off, rng.Next(0, 65536)));
        }

        var program = Load(string.Concat(ranges.Select((x, i) =>
            $"local u32 c{i} = std::hash::crc32({x.Off}, {x.Len}) [[show]]; local u32 a{i} = std::hash::crc({x.Off}, {x.Len}, \"CRC-16/ARC\") [[show]];"
            + $" local auto s{i} = std::format::hex(0, 0) [[show]];")));
        TemplateResult rr = Run(program, TemplateDataSource.FromBytes(data));
        Assert.Equal(TemplateRunState.Completed, rr.State);
        for (int i = 0; i < ranges.Count; i++)
        {
            (long off, long len) = ranges[i];
            ReadOnlySpan<byte> slice = data.AsSpan((int)off, (int)len);
            Assert.Equal(Convert.ToHexString(Compute("crc32", slice)), ((long)rr.FindByPath($"c{i}")!.Value!).ToString("X8", CultureInfo.InvariantCulture));
            Assert.Equal(Convert.ToHexString(Compute("crc16-arc", slice)), ((long)rr.FindByPath($"a{i}")!.Value!).ToString("X4", CultureInfo.InvariantCulture));
        }

        // sha256 はバイト列
        TemplateResult rs = RunOk("local auto h = std::hash::sha256(0, 9) [[show]];", "123456789"u8.ToArray());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("123456789"u8)), Convert.ToHexString((byte[])Value(rs, "h")!));

        TemplateResult big = Run(Load("local u32 c = std::hash::crc32(0, 268435457);"), new BigVirtual(300L * 1024 * 1024));
        Assert.Contains(big.Diagnostics, d => d.Code == TemplateMessages.HashTooLong);
    }

    private static byte[] Compute(string id, ReadOnlySpan<byte> data)
    {
        IHasher h = HashCatalog.Get(id).CreateHasher();
        h.Append(data);
        return h.Finish();
    }

    /// <summary>長さだけを持つ仮想のデータ (中身はすべて 0)。</summary>
    internal sealed class BigVirtual(long length) : TemplateDataSource
    {
        public override long Length => length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            unreadableAt = -1;
            int n = (int)Math.Max(0, Math.Min(destination.Length, length - offset));
            destination[..n].Clear();
            return n;
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-18-03")]
    public void SameTemplateTwiceGivesTheSameTree()
    {
        // TD-TPL-STD-SAMPLES のすべてのサンプルに対応する標準テンプレート (TPL-34) を適用し、すべて評価して JSON にする
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        string Json(CultureInfo culture)
        {
            CultureInfo save = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = culture;
                var sb = new StringBuilder();
                foreach ((string key, byte[] data) in StandardTemplateSamples.All)
                {
                    TemplateCatalogEntry entry = catalog.Find(StandardTemplateSamples.TemplateOf(key))!;
                    TemplateResult r = Run(catalog.Load(entry), TemplateDataSource.FromBytes(data, key + ".bin"));
                    r.EvaluateAll();
                    sb.Append(key).Append('\n').Append(TemplateJsonExporter.ToJson(r));
                    r.Dispose();
                }

                return sb.ToString();
            }
            finally
            {
                CultureInfo.CurrentCulture = save;
            }
        }

        Assert.Equal(Json(CultureInfo.GetCultureInfo("en-US")), Json(CultureInfo.GetCultureInfo("ja-JP")));
    }

    private static TemplateExtensionRegistry Registry(bool permitted)
    {
        var registry = new TemplateExtensionRegistry { Permission = _ => permitted };
        registry.Register(new TemplateExtensionFunction
        {
            Name = "myfmt",
            Point = TemplateExtensionPoint.Format,
            Parameters = [TemplateValueType.Integer],
            Returns = TemplateValueType.String,
            Owner = "myfmt.js",
            Invoke = (args, _) => "value=" + Convert.ToString(args[0], CultureInfo.InvariantCulture),
        });
        return registry;
    }

    [Fact]
    [Trait(TC, "TC-TPL-19-01")]
    public void RegisteredFormatFunctionIsUsed()
    {
        TemplateProgram p = Load("extern fn myfmt(u32 v) -> string; le u32 v [[format(myfmt)]];");
        Assert.True(p.Metadata.UsesExtensions);
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(Hex("2A 00 00 00")), new TemplateApplyOptions { Extensions = Registry(permitted: true) });
        Assert.Equal("value=42", Display(r, "v"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-19-02")]
    public void ExtensionCallsFailWithoutPermissionOrRegistration()
    {
        TemplateProgram p = Load("extern fn myfmt(u32 v) -> string; u8 a; le u32 v [[format(myfmt)]]; u8 b;");
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(Hex("01 2A 00 00 00 02")), new TemplateApplyOptions { Extensions = Registry(permitted: false) });
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.StartsWith("<error:", Display(r, "v"), StringComparison.Ordinal);
        Assert.Equal(1L, Long(r, "a"));
        Assert.Equal(2L, Long(r, "b"));

        TemplateResult r2 = Run("extern fn nothere() -> u32; local u32 x = nothere();", []);
        TemplateDiagnostic e = Assert.Single(r2.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.ExtensionNotRegistered, e.Code);
        Assert.Equal("nothere", e.Arguments[0]);
    }

    [Fact]
    [Trait(TC, "TC-TPL-19-04")]
    public void ScriptsCanApplyATemplateAndReadAPath()
    {
        // スクリプトの API (AUTO-13) が使う Core の入口: 適用して待ち、パスで値を読む
        TemplateProgram p = Load("""
            struct Header { u32 magic; u16 version; u16 count; };
            Header hdr;
            """);
        TemplateResult r = TemplateEngine.ApplyAndWait(p, TemplateDataSource.FromBytes(Hex("4D 41 47 43 02 00 01 00")));
        Assert.Equal(2L, r.FindByPath("hdr.version")!.Value);
    }
}
