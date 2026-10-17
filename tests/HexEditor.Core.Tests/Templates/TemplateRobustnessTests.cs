using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>エラー報告と部分結果 (TPL-27)、巨大ファイル・上限・キャンセル (TPL-28)、サンドボックス (TPL-37)。</summary>
public sealed class TemplateRobustnessTests
{
    private static string Data(string name) => Path.Combine(TemplateFeatureTests.DataFolder, name);

    [Fact]
    [Trait(TC, "TC-TPL-27-01")]
    public void SyntaxErrorsAreReportedWithLineAndColumn()
    {
        TemplateProgram p = TemplateLoader.LoadFile(Data("syntax3.hxt"));
        List<TemplateDiagnostic> errors = [.. p.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error)];
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Data("syntax3.expected-errors.json")));
        Assert.Equal(expected.RootElement.GetArrayLength(), errors.Count);
        for (int i = 0; i < errors.Count; i++)
        {
            JsonElement e = expected.RootElement[i];
            Assert.Equal(e.GetProperty("line").GetInt32(), errors[i].Location!.Value.Line);
            Assert.Equal(e.GetProperty("column").GetInt32(), errors[i].Location!.Value.Column);
            Assert.Equal(e.GetProperty("code").GetString(), errors[i].Code);
        }

        // 評価を始めない
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(new byte[16]));
        Assert.Equal(TemplateRunState.SyntaxError, r.State);
        Assert.Empty(r.Root.Children);

        string many = string.Concat(Enumerable.Repeat("u8 a\n", 101));
        TemplateProgram p2 = Load(many);
        Assert.Equal(100, p2.Diagnostics.Count(d => d.Severity == TemplateSeverity.Error));
    }

    [Fact]
    [Trait(TC, "TC-TPL-27-02")]
    public void OutOfRangePlacementLeavesAnErrorNodeAndContinues()
    {
        TemplateResult r = Run("struct H { u8 n; u8 m; }; H h; u32 big @ 0x100; u8 tail; struct L { u8 x; }; L *lazy : u8 [[lazy]];", Hex("01 02 03 FF"));
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.Equal(1L, Long(r, "h.n"));
        Assert.Equal(2L, Long(r, "h.m"));
        TemplateNode big = Node(r, "big");
        Assert.Equal(TemplateNodeKind.Error, big.Kind);
        TemplateDiagnostic e = Assert.Single(big.Diagnostics);
        Assert.Equal(TemplateMessages.PlacementOutOfRange, e.Code);
        Assert.Equal("100", e.Arguments[0]);
        Assert.Equal(1, e.Location!.Value.Line);
        Assert.Equal(0x100L, e.DataOffset);
        Assert.Equal("big", e.Path);
        Assert.NotNull(e.CallStack);
        Assert.Equal(2L, Node(r, "tail").Offset);
        TemplateNode lazy = Node(r, "lazy");
        Assert.Equal(3L, lazy.Offset);
        int before = r.Root.Children.Count;
        Assert.Empty(lazy.Children);
        Assert.True(lazy.Pointer!.OutOfRange);
        Assert.Contains(lazy.Diagnostics, d => d.Code == TemplateMessages.PointerOutOfRange && d.Severity == TemplateSeverity.Warning);
        Assert.Equal(before, r.Root.Children.Count);
        Assert.Equal(1L, Long(r, "h.n"));
    }

    /// <summary>TC-TPL-27-04 (UI: ヘルプを開く) の前提: すべてのエラーにコードがある。</summary>
    [Fact]
    public void EveryErrorHasACode()
    {
        // ヘルプの説明ページはコードに対応させる (リソースの検査は UI の作業で行う)
        Assert.All(TemplateMessages.AllCodes, c => Assert.Matches("^HXT[0-9]{4}$", c));
        string app = Path.GetDirectoryName(Core.Tests.Engine.SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;
        foreach (string language in new[] { "en", "ja" })
        {
            Dictionary<string, string> strings = I18n.ResourceChecker.LoadResw(Path.Combine(app, "Strings", language, "Resources.resw"));
            Assert.All(TemplateMessages.AllCodes, c => Assert.True(strings.ContainsKey("TplDiag_" + c), $"{language}: TplDiag_{c}"));
        }

        TemplateResult r = Run(File.ReadAllText(Data("runtime-err.hxt")), Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        TemplateDiagnostic e = Assert.Single(r.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.AssertFailed, e.Code);
        Assert.Equal(4, e.Location!.Value.Line);
        Assert.Equal(0x40L, e.DataOffset);
        Assert.StartsWith("HXT", e.ToString().Split(' ')[2], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-TPL-07-01")]
    public void HundredMillionElementArrayIsImmediate()
    {
        var source = new HexEditor.Core.Sources.VirtualByteSource(1L << 30, HexEditor.Core.Sources.VirtualContent.Zero);
        var sw = Stopwatch.StartNew();
        TemplateResult r = Run(Load("u32 a[100000000]; u32 after;"), TemplateDataSource.FromByteSource(source));
        Assert.Equal(400_000_000L, Node(r, "after").Offset);
        Support.TimeBudget.Limit(sw.Elapsed < TimeSpan.FromSeconds(1), $"適用に {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        TemplateNode last = Node(r, "a[99999999]");
        Assert.Equal(399_999_996L, last.Offset);
        Assert.Equal(0L, last.Value);
        Support.TimeBudget.Limit(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"a[99999999] に {sw.ElapsedMilliseconds} ms");
        Assert.True(r.PatternCount < 1000, $"評価済み {r.PatternCount}");
    }

    [Fact]
    [Trait(TC, "TC-TPL-28-01")]
    public void GptAndMbrOnHundredGigabyteImages()
    {
        const long Length = 100L * 1024 * 1024 * 1024;
        const string Gpt = """
            struct Mbr { u8 boot[446]; struct { u8 status; u8 chs1[3]; u8 type [[format("hex")]]; u8 chs2[3]; le u32 lba; le u32 count; } parts[4]; le u16 sig [[format("hex")]]; };
            struct Header { char signature[8]; le u32 revision; le u32 size; le u32 crc; le u32 reserved; le u64 current; le u64 backup;
                le u64 first; le u64 last; guid disk; le u64 entries_lba; le u32 entry_count; le u32 entry_size; le u32 entries_crc; };
            struct Entry { guid type; guid unique; le u64 first; le u64 last; le u64 attributes; char16 name[36]; };
            Mbr mbr;
            Header header @ 512;
            Entry entries[header.entry_count] @ header.entries_lba * 512;
            """;
        TemplateDataSource gpt = new Virtual(Length, TestDataCatalog.GptImage);
        var sw = Stopwatch.StartNew();
        TemplateResult r = Run(Load(Gpt), gpt);
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.Equal("HXTEST-DATA", Value(r, "entries[0].name"));
        Support.TimeBudget.Limit(sw.Elapsed < TimeSpan.FromSeconds(1), $"GPT の適用に {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        TemplateResult m = Run(Load("struct P { u8 status; u8 chs1[3]; u8 type; u8 chs2[3]; le u32 lba; le u32 count; }; u8 boot[446]; P parts[4]; le u16 sig;"),
            new Virtual(Length, TestDataCatalog.MbrImage));
        Assert.Equal(7L, Long(m, "parts[0].type"));
        Assert.Equal(2048L, Long(m, "parts[0].lba"));
        Assert.Equal(0xAA55L, Long(m, "sig"));
        Support.TimeBudget.Limit(sw.Elapsed < TimeSpan.FromSeconds(1), $"MBR の適用に {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>内容を関数で計算する仮想のデータ。</summary>
    internal sealed class Virtual(long length, Action<long, long, Span<byte>> fill) : TemplateDataSource
    {
        public override long Length => length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            unreadableAt = -1;
            int n = (int)Math.Max(0, Math.Min(destination.Length, length - offset));
            fill(length, offset, destination[..n]);
            return n;
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-28-02")]
    public void EofArrayOnHundredGigabytesIsCappedAndMemoryStaysBelowTheLimit()
    {
        var source = new HexEditor.Core.Sources.VirtualByteSource(100L * 1024 * 1024 * 1024, HexEditor.Core.Sources.VirtualContent.Offset64);
        TemplateResult r = Run(Load("#pragma limit.memory 268435456\nu64 a[eof]; u8 after;"), TemplateDataSource.FromByteSource(source));
        TemplateNode a = Node(r, "a");
        Assert.Equal(100_000_000L, a.ChildCount);
        Assert.Contains(r.Diagnostics, d => d.Code == TemplateMessages.ArrayTruncated && d.Arguments[0] == "100000000");
        Assert.Equal(800_000_000L, Node(r, "after").Offset);
        long max = 0;
        foreach (long start in new[] { 0L, 50_000_000L, 99_999_000L })
        {
            for (long i = start; i < start + 1000; i++)
            {
                Assert.Equal(i * 8, Convert.ToInt64(a.GetChild(i).Value, System.Globalization.CultureInfo.InvariantCulture));
                max = Math.Max(max, r.MemoryBytes);
            }
        }

        r.EvaluateAll();
        Assert.NotNull(r.FindByPath("after"));
        max = Math.Max(max, r.MemoryBytes);
        Assert.True(max <= 256L * 1024 * 1024, $"評価器のメモリ {max}");
    }

    [Fact]
    [Trait(TC, "TC-TPL-28-02")]
    public void VisitingManyStructElementsDoesNotAccumulatePatterns()
    {
        var source = new HexEditor.Core.Sources.VirtualByteSource(1L << 30, HexEditor.Core.Sources.VirtualContent.OffsetLowByte);
        TemplateResult r = Run(Load("#pragma limit.patterns 20000\nstruct P { u8 a; u8 b; }; P p[100000000];"), TemplateDataSource.FromByteSource(source));
        TemplateNode p = Node(r, "p");
        long sum = 0;
        r.Invoke(() =>
        {
            for (long i = 0; i < 200_000; i++)
            {
                sum += Convert.ToInt64(p.GetChild(i * 37).FindChild("b")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return 0;
        });
        Assert.True(sum > 0);
        Assert.Equal(TemplateRunState.Completed, r.State);
        Assert.True(r.PatternCount < 20_000, $"評価済み {r.PatternCount}");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == TemplateMessages.PatternLimit);
    }

    [Theory]
    [Trait(TC, "TC-TPL-28-03")]
    [InlineData("while (true) {}", false)]
    [InlineData("HEAVY", false)]
    [InlineData("FIND", false)]
    [InlineData("HEAVY", true)]
    public void CancellationStopsWithinOneHundredMilliseconds(string kind, bool slow)
    {
        byte[] data = new byte[16 * 1024 * 1024];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, data);
        string template = kind switch
        {
            "HEAVY" => File.ReadAllText(Data("heavy.hxt")) + "\nHeavy h[eof];",
            "FIND" => "for (local u32 i = 0; i < 1000; i += 1) { local u64 n = std::mem::find(x\"DE AD BE EF 01 02 03 04\", 0, std::core::size()); }",
            _ => kind,
        };
        TemplateDataSource source = slow ? new Slow(data) : TemplateDataSource.FromBytes(data);
        // 空ループは速い CPU だと既定の steps 上限 (1 億) を 1 秒未満で使い切るので、キャンセル以外で終わらないよう上限を外す
        var options = new TemplateApplyOptions { LimitOverride = TemplateLimits.Default with { Steps = long.MaxValue } };
        TemplateResult r = TemplateEngine.Apply(Load(template), source, options);
        Assert.False(Wait(r.Completion, TimeSpan.FromSeconds(1)));
        var sw = Stopwatch.StartNew();
        r.Cancel();
        Assert.True(Wait(r.Completion, TimeSpan.FromSeconds(10)));
        Assert.Equal(TemplateRunState.Cancelled, r.State);
        Support.TimeBudget.Limit(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"キャンセルに {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>読み込みごとに 50 ms 待つデータ。</summary>
    private sealed class Slow(byte[] data) : TemplateDataSource
    {
        public override long Length => data.Length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            Thread.Sleep(50);
            unreadableAt = -1;
            int n = (int)Math.Max(0, Math.Min(destination.Length, data.Length - offset));
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-28-04")]
    public void PatternLimitStopsWithPartialResults()
    {
        const string t = "#pragma limit.patterns 1000\nstruct R { u8 a; u8 b; }; for (local u32 i = 0; i < 2000; i += 1) { R r; }";
        byte[] data = new byte[16 * 1024];
        TemplateResult r = Run(t, data);
        Assert.Equal(TemplateRunState.LimitReached, r.State);
        Assert.Equal(TemplateLimitKind.Patterns, r.LimitReached);
        TemplateDiagnostic e = Assert.Single(r.Diagnostics, d => d.Code == TemplateMessages.PatternLimit);
        Assert.Equal("1000", e.Arguments[0]);
        Assert.NotNull(r.FindByPath("r[0]"));
        Assert.Null(r.FindByPath("r[1999]"));

        // 「上限を上げて再実行」(上限は設定の最大値まで)
        TemplateResult again = Run(Load(t), TemplateDataSource.FromBytes(data),
            new TemplateApplyOptions { LimitOverride = r.Limits with { Patterns = TemplateLimits.DefaultMaximum.Patterns } });
        Assert.Equal(TemplateRunState.Completed, again.State);
        Assert.NotNull(again.FindByPath("r[1999]"));
    }

    // ───────────── サンドボックス (TPL-37) ─────────────

    [Theory]
    [Trait(TC, "TC-TPL-37-01")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\\Windows\\win.ini")]
    [InlineData(@"\\\\?\\C:\\Windows\\win.ini")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void AbsoluteImportsAreRejectedWithoutReadingTheFile(string path)
    {
        var fs = new TemplateFeatureTests.RecordingFileSystem();
        TemplateProgram p = TemplateLoader.LoadText($"import \"{path}\";", "abs.hxt", Path.GetTempPath(), new TemplateLoadOptions { FileSystem = fs });
        TemplateDiagnostic e = Assert.Single(p.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.NotPermitted, e.Code);
        Assert.StartsWith("Templates cannot perform this operation:", e.Message, StringComparison.Ordinal);
        Assert.Empty(fs.Reads);
        Assert.DoesNotContain(fs.Probes, x => x.Contains("win.ini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TC, "TC-TPL-37-02")]
    public void MillionDeepRecursionDoesNotCrash()
    {
        var sw = Stopwatch.StartNew();
        TemplateResult f = Run("#pragma limit.recursion 1000000\nfn f(u64 n) -> u64 { return n == 0 ? 0 : f(n - 1) + 1; } local u64 x = f(1000000);", []);
        TemplateDiagnostic e = Assert.Single(f.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.RecursionTooDeep, e.Code);
        Assert.Equal("2560", e.Arguments[0]);
        Assert.Contains(f.Diagnostics, d => d.Code == TemplateMessages.LimitClamped);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));

        sw.Restart();
        TemplateResult s = Run("#pragma limit.recursion 1000000\nstruct N { N child; }; N root;", new byte[1024 * 1024]);
        Assert.Contains(s.Diagnostics, d => d.Code == TemplateMessages.RecursionTooDeep);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));

        sw.Restart();
        TemplateProgram parens = Load("local u8 x = " + new string('(', 1_000_000) + "1;");
        Assert.Contains(parens.Diagnostics, d => d.Code == TemplateMessages.NestingTooDeep);
        TemplateProgram blocks = Load(new string('{', 1_000_000));
        Assert.Contains(blocks.Diagnostics, d => d.Code == TemplateMessages.NestingTooDeep);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    [Trait(TC, "TC-TPL-37-03")]
    public void StringDoublingStopsAtTheMemoryLimit()
    {
        long before = GC.GetTotalMemory(forceFullCollection: true);
        TemplateResult r = Run("local string s = \"x\"; while (true) { s = s + s; }", [], timeout: TimeSpan.FromSeconds(60));
        TemplateDiagnostic e = Assert.Single(r.Diagnostics, d => d.Severity == TemplateSeverity.Error);
        Assert.Equal(TemplateMessages.MemoryLimit, e.Code);
        Assert.Equal("512 MB", e.Arguments[0]);
        long peak = GC.GetGCMemoryInfo().HeapSizeBytes;
        Assert.True(peak - before <= (512L + 64) * 1024 * 1024 + (256L * 1024 * 1024), $"ヒープ {peak - before}");
    }

    [Fact]
    [Trait(TC, "TC-TPL-37-04")]
    public void MaliciousTemplatesEndWithTheExpectedErrors()
    {
        byte[] bytes256 = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var cases = new (string Template, byte[] Data, string Code)[]
        {
            ("#pragma limit.steps 100000\nwhile (true) {}", bytes256, TemplateMessages.StepLimit),
            ("fn f(u64 n) -> u64 { return f(n + 1); } local u64 x = f(0);", bytes256, TemplateMessages.RecursionTooDeep),
            ("struct S { S s; }; S s;", bytes256, TemplateMessages.RecursionTooDeep),
            ("u8 a[0x7FFFFFFFFFFFFFFF];", bytes256, TemplateMessages.PatternOutOfRange),
            ("struct S { u8 x; }; S s[1000000000000];", bytes256, TemplateMessages.PatternOutOfRange),
            ("le u8 *p : s8; u8 x;", [0xF0, 0], TemplateMessages.PointerOutOfRange),
            ("le u8 *p : u64;", [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F], TemplateMessages.PointerOutOfRange),
            ("le u8 *p : u8;", [1], TemplateMessages.PointerOutOfRange),
            ("local string s = \"x\"; while (true) { s = s + s; }", bytes256, TemplateMessages.MemoryLimit),
            ("import \"../x.hxt\";", bytes256, TemplateMessages.NotPermitted),
            ("import \"C:/Windows/win.ini\";", bytes256, TemplateMessages.NotPermitted),
            (@"import ""\\\\localhost\\c$\\Windows\\win.ini"";", bytes256, TemplateMessages.NotPermitted),
            ("import \"CON.hxt\";", bytes256, TemplateMessages.NotPermitted),
            (@"import ""\\\\.\\PhysicalDrive0"";", bytes256, TemplateMessages.NotPermitted),
            ("import \"a.hxt:evil\";", bytes256, TemplateMessages.NotPermitted),
        };
        foreach ((string template, byte[] data, string code) in cases)
        {
            var sw = Stopwatch.StartNew();
            TemplateProgram p = TemplateLoader.LoadText(template, "malicious.hxt", Path.Combine(Path.GetTempPath(), "HexEditorTests", "malicious"));
            TemplateResult r = Run(p, TemplateDataSource.FromBytes(data), timeout: TimeSpan.FromSeconds(30));
            if (r.State == TemplateRunState.Completed)
            {
                r.EvaluateAll();
            }

            Assert.True(r.Diagnostics.Any(d => d.Code == code) || p.Diagnostics.Any(d => d.Code == code),
                $"{template}: {string.Join("; ", r.Diagnostics)}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), template);
        }

        // 展開爆弾
        TemplateResult bomb = Run(Load("u32 len; section s; std::compress::zlib(s, 4, len); u8 last @ std::section::size(s) - 1 in s;"),
            new TemplateFeatureTests.ZlibBombSource(), timeout: TimeSpan.FromSeconds(60));
        Assert.Contains(bomb.Diagnostics, d => d.Code == TemplateMessages.DecompressLimit);
        bomb.Dispose();
    }

    [Fact]
    [Trait(TC, "TC-TPL-37-05")]
    public void EvaluationDoesNotTouchFilesOtherThanImports()
    {
        // 評価器のファイルの読み込みはテンプレートのファイルシステムを通す。インポート以外の読み込みが起きないことを確かめる
        var fs = new TemplateFeatureTests.RecordingFileSystem();
        string dir = Path.Combine(TemplateFeatureTests.DataFolder, "import-set");
        TemplateProgram p = TemplateLoader.LoadFile(Path.Combine(dir, "main.hxt"), new TemplateLoadOptions { FileSystem = fs });
        int reads = fs.Reads.Count;
        TemplateResult r = Run(p, TemplateDataSource.FromBytes(new byte[64]));
        r.EvaluateAll();
        Assert.Equal(reads, fs.Reads.Count);
        Assert.All(fs.Reads, f => Assert.EndsWith(".hxt", f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TC, "TC-TPL-37-06")]
    public void EscapeAttemptsAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "HexEditorTests", "tpl-sandbox-" + Guid.NewGuid().ToString("N"));
        string templates = Path.Combine(root, "templates");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(templates);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            File.WriteAllText(Path.Combine(outside, "evil.hxt"), "struct Evil { u8 x; };");
            File.WriteAllText(Path.Combine(templates, "secret.txt"), "secret");
            bool linked = TryCreateLink(Path.Combine(templates, "link"), outside);
            var fs = new TemplateFeatureTests.RecordingFileSystem();
            var options = new TemplateLoadOptions { SearchPaths = [templates], FileSystem = fs };
            TemplateProgram Load2(string text) => TemplateLoader.LoadText(text, "escape.hxt", templates, options);

            // (a) リンクを通したインポート (リンクを作れる環境だけ)
            if (linked)
            {
                Assert.Contains(Load2("import \"link/evil.hxt\";").Diagnostics, d => d.Code == TemplateMessages.NotPermitted);
                Assert.Contains(Load2("import link.evil;").Diagnostics, d => d.Code == TemplateMessages.ImportNotFound);
            }

            // (b) .hxt / .ksy 以外 (c) #include
            Assert.Contains(Load2("import \"secret.txt\";").Diagnostics, d => d.Code == TemplateMessages.NotPermitted);
            Assert.Contains(Load2("#include \"secret.txt\"\nu8 x;").Diagnostics, d => d.Code == TemplateMessages.NotPermitted);
            Assert.Contains(Load2("#include \"../outside/evil.hxt\"\nu8 x;").Diagnostics, d => d.Code == TemplateMessages.NotPermitted);
            Assert.DoesNotContain(fs.Reads, f => f.StartsWith(outside, StringComparison.OrdinalIgnoreCase) || f.EndsWith("secret.txt", StringComparison.OrdinalIgnoreCase));

            // (h) .ksy の imports での ../ と絶対パス
            foreach (string import in new[] { "../outside/evil", "C:/Windows/win" })
            {
                string ksy = Path.Combine(templates, "escape-" + Guid.NewGuid().ToString("N") + ".ksy");
                File.WriteAllText(ksy, $"meta:\n  id: escape\n  imports:\n    - {import}\nseq:\n  - id: a\n    type: u1\n");
                TemplateProgram k = TemplateLoader.LoadFile(ksy, options);
                Assert.Contains(k.Diagnostics, d => d.Code == TemplateMessages.NotPermitted);
            }

            Assert.DoesNotContain(fs.Reads, f => f.StartsWith(outside, StringComparison.OrdinalIgnoreCase)
                || f.EndsWith("win.ksy", StringComparison.OrdinalIgnoreCase) || f.EndsWith("evil.ksy", StringComparison.OrdinalIgnoreCase));

            // (d) ファイル名だけでフォルダのパスを含まない
            TemplateResult d = Run(Load("std::core::print(std::sys::file_name());"),
                TemplateDataSource.FromBytes([1], Path.Combine(root, "HXSECRET7F3A-report.bin")));
            Assert.Contains(d.Output, o => o.Text == "HXSECRET7F3A-report.bin");
            Assert.DoesNotContain(d.Output, o => o.Text.Contains(root, StringComparison.OrdinalIgnoreCase));

            // (e) .NET の型名 (f) 登録されていない extern fn (g) パスを std::mem に渡す
            Assert.Contains(Run("local auto t = System.IO.File;", [1]).Diagnostics, x => x.Code == TemplateMessages.Undefined);
            Assert.Contains(Run("local auto t = typeof(System::IO::File);", [1]).Diagnostics, x => x.Code == TemplateMessages.Undefined);
            Assert.Contains(Run("extern fn Start(string s) -> u32; local u32 x = Start(\"calc\");", [1]).Diagnostics,
                x => x.Code == TemplateMessages.ExtensionNotRegistered);
            Assert.Contains(Run("local u32 x = std::mem::read_u32(\"C:/Windows/win.ini\");", [1]).Diagnostics, x => x.Code == TemplateMessages.TypeMismatch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool TryCreateLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────── ファジング (SharpFuzz の代わりの決定的な変異) ─────────────

    private static readonly string[] Seeds =
    [
        TemplateExamplesTests.PngExample,
        "struct Header { u32 magic; u16 version; u16 count; }; Header hdr; u8 data[hdr.count] @ 0x10;",
        "enum E : u8 { A = 1, B ... 5 }; bitfield F : u16 { a : 3; b : 13; }; E e; F f; u16 x : 4, y : 12;",
        "fn f(u32 v) -> u32 { return v * 2; } local auto x = f(3) [[show]]; struct N { u8 n; if (n) { N next; } }; N head;",
        "section s; std::section::copy(s, 0, 4); u32 v @ 0 in s; le u8 *p : u8 [[relative]]; char c[]; u8 rest[eof];",
    ];

    [Fact]
    [Trait(TC, "TC-TPL-27-05")]
    [Trait(TC, "TC-TPL-37-07")]
    [Trait("Category", "Nightly")]
    public void MutatedTemplatesNeverCrash()
    {
        var rng = new Random(2705);
        byte[] data = new byte[4096];
        rng.NextBytes(data);
        const string alphabet = "{}[]();:,.<>=+-*/%&|^!~?@$\"'\\ \n\tabcdefxyz0123456789_#";
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 2000 && sw.Elapsed < TimeSpan.FromMinutes(5); i++)
        {
            var sb = new StringBuilder(Seeds[rng.Next(Seeds.Length)]);
            int edits = rng.Next(1, 8);
            for (int k = 0; k < edits && sb.Length > 0; k++)
            {
                int at = rng.Next(sb.Length);
                switch (rng.Next(3))
                {
                    case 0:
                        sb.Remove(at, Math.Min(rng.Next(1, 6), sb.Length - at));
                        break;
                    case 1:
                        sb.Insert(at, alphabet[rng.Next(alphabet.Length)]);
                        break;
                    default:
                        sb[at] = alphabet[rng.Next(alphabet.Length)];
                        break;
                }
            }

            string text = sb.ToString();
            var one = Stopwatch.StartNew();
            TemplateProgram p = Load(text);
            Assert.True(one.Elapsed < TimeSpan.FromSeconds(1) + TimeSpan.FromSeconds(4), text);
            Assert.All(p.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error && d.Code != TemplateMessages.TooManyErrors), d => Assert.NotNull(d.Location));
            Assert.True(p.Diagnostics.Count(d => d.Severity == TemplateSeverity.Error) <= 100);
            if (!p.HasErrors)
            {
                TemplateResult r = Run(p, TemplateDataSource.FromBytes(data),
                    new TemplateApplyOptions { LimitOverride = TemplateLimits.Default with { TimeSeconds = 5, Memory = 256L * 1024 * 1024 } },
                    TimeSpan.FromSeconds(30));
                Assert.DoesNotContain(r.Diagnostics, d => d.Code == TemplateMessages.Internal);
                r.EvaluateAll();
                _ = TemplateJsonExporter.ToJson(r);
                Assert.DoesNotContain(r.Diagnostics, d => d.Code == TemplateMessages.Internal);
                r.Dispose();
            }
        }
    }
}
