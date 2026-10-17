using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Editing;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Presentation;
using HexEditor.Core.Templates.Syntax;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>値の編集 (TPL-22)、エクスポート (TPL-25)、デバッガ (TPL-32)、エディタの言語の機能 (TPL-31)、自動適用 (TPL-30)、管理 (TPL-35)。</summary>
public sealed class TemplateToolingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "tpl-tool-" + Guid.NewGuid().ToString("N"));

    public TemplateToolingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Fixture(string name) => Path.Combine(TemplateFeatureTests.DataFolder, name);

    private static byte[] Apply(byte[] data, TemplateEditPlan plan)
    {
        Assert.True(plan.Success, $"{plan.Error}: {plan.Detail}");
        byte[] copy = (byte[])data.Clone();
        plan.Bytes!.CopyTo(copy, plan.Offset);
        return copy;
    }

    private static TemplateValueEditSession Session(TemplateResult r, string path) =>
        r.Invoke(() => TemplateValueEditSession.Create(Node(r, path), false, out _)) ?? throw new Xunit.Sdk.XunitException("編集できません。");

    // ───────────── 値の編集 (TPL-22) ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-22-01")]
    public void EnumDropDownAndFlagCheckboxesWriteTheValues()
    {
        byte[] data = Hex("64 86 22 20");
        TemplateResult r = RunOk("import std.pe;\nle std::pe::Machine m; le std::pe::Characteristics c;", data);
        TemplateValueEditSession m = Session(r, "m");
        Assert.Equal(TemplateEditorKind.Enum, m.Kind);
        Assert.Contains(m.Choices, c => c.Name == "ARM64");
        Assert.Equal("AMD64", m.InitialText);
        data = Apply(data, m.Plan("ARM64"));
        TemplateValueEditSession c = Session(r, "c");
        Assert.Equal(TemplateEditorKind.Flags, c.Kind);
        Assert.True(c.Choices.Single(x => x.Name == "LargeAddressAware").IsChecked);
        data = Apply(data, c.PlanFlags(c.Choices.Where(x => x.IsChecked && x.Name != "LargeAddressAware").Select(x => x.Name)));
        Assert.Equal(Hex("64 AA 02 20"), data);
    }

    [Fact]
    [Trait(TC, "TC-TPL-22-02")]
    public void BigEndianU32IsWrittenInPlaceAndOutOfRangeIsRejected()
    {
        byte[] data = new byte[16];
        TemplateResult r = RunOk("u8 pad[4]; be u32 v; u8 tail;", data);
        TemplateValueEditSession v = Session(r, "v");
        TemplateEditPlan plan = v.Plan("0x12345678");
        Assert.Equal(4, plan.Offset);
        Assert.Equal(Hex("12 34 56 78"), plan.Bytes);
        TemplateEditPlan bad = v.Plan("0x1_0000_0000");
        Assert.False(bad.Success);
        Assert.Equal(TemplateEditError.OutOfRange, bad.Error);
    }

    [Fact]
    [Trait(TC, "TC-TPL-22-03")]
    public void ShortStringsArePaddedAndLongStringsAreRejected()
    {
        byte[] data = new byte[16];
        TemplateResult r = RunOk("char name[8]; u8 after;", data);
        TemplateValueEditSession s = Session(r, "name");
        Assert.Equal(TemplateEditorKind.String, s.Kind);
        TemplateEditPlan plan = s.Plan("ABC");
        Assert.Equal(Hex("41 42 43 00 00 00 00 00"), plan.Bytes);
        Assert.Equal(3, s.Plan("ABC", padWithZeros: false).Bytes!.Length);
        TemplateEditPlan tooLong = s.Plan("ABCDEFGHI");
        Assert.Equal(TemplateEditError.TooLong, tooLong.Error);
    }

    [Fact]
    [Trait(TC, "TC-TPL-14-02")]
    public void ReadOnlyConversionEditsTheRawValue()
    {
        byte[] data = Hex("EB 00");
        TemplateResult r = RunOk("le s16 temp [[read(fn(v) => v / 10.0)]];", data);
        TemplateValueEditSession s = Session(r, "temp");
        Assert.True(s.EditsRawValue);
        Assert.Equal("235", s.InitialText);
        Assert.Equal(Hex("64 00"), Apply(data, s.Plan("100")));
    }

    [Fact]
    [Trait(TC, "TC-TPL-15-04")]
    public void PatternsInsideSectionsCannotBeEdited()
    {
        byte[] png = File.ReadAllBytes(TestDataCatalog.Get("TD-TPL-PNG-IDAT"));
        TemplateResult r = Run(TemplateLoader.LoadFile(Fixture("png-section.hxt")), TemplateDataSource.FromBytes(png));
        TemplateNode filter = r.Invoke(() => TemplateTreeModel.Search(r.Root, "filter", true, TemplateDisplayContext.Invariant, 10, null, default)
            .First(n => n.Path.Contains("rows[1]", StringComparison.Ordinal)));
        TemplateValueEditSession? s = r.Invoke(() => TemplateValueEditSession.Create(filter, false, out TemplateEditBlock block)
            is var x && block == TemplateEditBlock.InSection ? x : throw new Xunit.Sdk.XunitException(block.ToString()));
        Assert.Null(s);
    }

    [Fact]
    [Trait(TC, "TC-TPL-29-01")]
    public async Task InspectorTypesShowFollowAndWrite()
    {
        // 登録したテンプレートの型は、インスペクタのユーザー定義型 (INSP-22) と同じ経路で読み込み・評価する。
        using var types = new HexEditor.Core.Inspector.InspectorUserTypes(_dir);
        types.SetRegisteredTemplates([Fixture("inspector-rgb565.hxt")]);
        HexEditor.Core.Inspector.InspectorUserType t = Assert.Single(types.Types);
        Assert.Equal(HexEditor.Core.Inspector.InspectorUserTypeSource.Registered, t.Source);
        Assert.Equal("RGB565 色", t.DisplayName);
        Assert.False(t.NotFixedSize);
        byte[] data = Hex("00 F8 E0 07 1F 00 FF FF");
        Assert.Equal("#FF0000", (await types.EvaluateAsync(t, data, HexEditor.Core.Inspector.Endianness.Little)).Display);

        // カーソルを 0x2 に動かす (起点からのデータで評価する)。
        Assert.Equal("#00FF00", (await types.EvaluateAsync(t, data[2..], HexEditor.Core.Inspector.Endianness.Little)).Display);
        using TemplateResult r = types.ApplyForEdit(t, data[2..], HexEditor.Core.Inspector.Endianness.Little)!;
        TemplateNode node = r.Invoke(() => r.Root.Children[0]);
        Assert.True(TemplateInspectorTypes.CanEdit(node));
        TemplateEditPlan plan = TemplateInspectorTypes.PlanEdit(node, "#0000FF");
        Assert.Equal(0, plan.Offset);
        Assert.Equal(Hex("1F 00"), plan.Bytes);
    }

    // ───────────── エクスポート (TPL-25) ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-25-01")]
    public void PngResultExportsAsJson()
    {
        TemplateResult r = Run(TemplateLoader.LoadFile(Fixture("png-example.hxt")), TemplateDataSource.FromBytes(File.ReadAllBytes(TestDataCatalog.Get("TD-PNG"))));
        string path = Path.Combine(_dir, "png.json");
        TemplateResultExporter.ExportToFile(r.Root, path, TemplateExportFormat.Json);
        byte[] bytes = File.ReadAllBytes(path);
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        JsonNode root = JsonNode.Parse(text)!;
        JsonNode type = Find(root, "chunks[0].type")!;
        Assert.Equal("IHDR", (string?)type["value"] ?? (string?)type["display"]);
        foreach (JsonNode n in All(root).Skip(1))
        {
            foreach (string key in new[] { "name", "path", "type", "offset", "size", "display" })
            {
                Assert.True(n[key] is not null, $"{key} がありません: {n.ToJsonString()}");
            }

            Assert.True(n["offset"]!.GetValueKind() == System.Text.Json.JsonValueKind.Number);
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-06-02")]
    public void BitfieldMembersExportBitOffsetAndSize()
    {
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        TemplateResult r = RunOk("u8 pad[16]; bitfield B { a : 3; b : 5; }; B bf;", data);
        JsonNode b = Find(JsonNode.Parse(TemplateJsonExporter.ToJson(r))!, "bf.b")!;
        Assert.Equal(3, (int)b["bit_offset"]!);
        Assert.Equal(5, (int)b["bit_size"]!);
        Assert.Equal(16, (long)b["offset"]!);
    }

    [Fact]
    [Trait(TC, "TC-TPL-25-02")]
    public void CsvHasBomCrlfAndQuotes()
    {
        TemplateResult r = Run(TemplateLoader.LoadFile(Fixture("csv-ja.hxt")), TemplateDataSource.FromBytes(Hex("01 02 03 04")));
        var ms = new MemoryStream();
        TemplateResultExporter.WriteCsv(r.Root, ms);
        byte[] bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith(TemplateResultExporter.CsvHeader + "\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Replace("1 行目\n2 行目", string.Empty, StringComparison.Ordinal)
            .Where(c => c == '\r').Select(c => c.ToString()));
        List<string[]> rows = ParseCsv(text);
        Assert.Equal(["path", "type", "offset", "size", "value", "display", "comment"], rows[0]);
        Assert.Equal("日本語のコメント", rows.Single(x => x[0] == "a")[6]);
        Assert.Equal("件数, \"重要\"", rows.Single(x => x[0] == "b")[6]);
        Assert.Equal("1 行目\n2 行目", rows.Single(x => x[0] == "c")[6]);
        Assert.Contains("\"件数, \"\"重要\"\"\"", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-TPL-25-05")]
    public void CancelledOrFailedExportLeavesNoPartialFile()
    {
        var source = new VirtualByteSource(1L << 30, VirtualContent.Zero);
        TemplateResult r = Run(Load("struct P { u32 a; u32 b; }; P arr[10000000];"), TemplateDataSource.FromByteSource(source));
        string path = Path.Combine(_dir, "big.json");
        using var cts = new CancellationTokenSource();
        var options = new TemplateExportOptions { Progress = n => { if (n > 50_000) { cts.Cancel(); } } };
        Assert.ThrowsAny<OperationCanceledException>(() => TemplateResultExporter.ExportToFile(r.Root, path, TemplateExportFormat.Json, options, cts.Token));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir));

        // 10 MB の位置で書き込みに失敗させる (容量不足)
        Assert.Throws<IOException>(() => TemplateResultExporter.ExportToFile(r.Root, path, TemplateExportFormat.Json, null, default,
            temp => new FailingStream(new FileStream(temp, FileMode.CreateNew), 10 * 1024 * 1024)));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    [Trait(TC, "TC-TPL-25-04")]
    [Trait("Category", "Nightly")]
    public void TenMillionElementExportDoesNotGrowMemory()
    {
        var source = new VirtualByteSource(1L << 30, VirtualContent.Zero);
        TemplateResult r = Run(Load("struct P { u32 a; u32 b; }; P arr[10000000];"), TemplateDataSource.FromByteSource(source));
        foreach (TemplateExportFormat format in new[] { TemplateExportFormat.Json, TemplateExportFormat.Csv })
        {
            long start = GC.GetTotalMemory(true);
            long peak = start;
            // 回収済みでないごみ (世代 0 の予算は環境で大きく違う) を数えないよう、一定の件数ごとに回収してから測る。
            long nextSample = 250_000;
            var options = new TemplateExportOptions
            {
                Indented = false,
                Progress = n =>
                {
                    if (n >= nextSample)
                    {
                        nextSample = n + 250_000;
                        peak = Math.Max(peak, GC.GetTotalMemory(true));
                    }
                },
            };
            var sink = new CountingStream();
            TemplateResultExporter.Write(r.Root, sink, format, options);
            Assert.True(peak - start < 256L * 1024 * 1024, $"{format}: {(peak - start) / 1024 / 1024} MB");
            Assert.True(sink.Length > 10_000_000);
        }
    }

    private sealed class FailingStream(Stream inner, long failAt) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position { get => _written; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_written + count > failAt)
            {
                throw new IOException("There is not enough space on the disk.");
            }

            inner.Write(buffer, offset, count);
            _written += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _length += count;
    }

    private static IEnumerable<JsonNode> All(JsonNode node)
    {
        yield return node;
        if (node["children"] is JsonArray children)
        {
            foreach (JsonNode? c in children)
            {
                foreach (JsonNode n in All(c!))
                {
                    yield return n;
                }
            }
        }
    }

    private static JsonNode? Find(JsonNode root, string path) => All(root).FirstOrDefault(n => (string?)n["path"] == path);

    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
                i++;
            }
            else
            {
                field.Append(c);
            }
        }

        return rows;
    }

    // ───────────── デバッガ (TPL-32) ─────────────

    private sealed class DebugSession : IDisposable
    {
        private readonly BlockingCollection<TemplateDebugState> _paused = [];

        public DebugSession(string template, byte[] data, Action<TemplateDebugger> setup, string path = "debug.hxt")
        {
            Debugger = new TemplateDebugger();
            Path = path;
            setup(Debugger);
            Debugger.Paused += (_, s) => _paused.Add(s);
            Result = TemplateEngine.Apply(TemplateLoader.LoadText(template, path), TemplateDataSource.FromBytes(data), new TemplateApplyOptions { Debugger = Debugger });
        }

        public string Path { get; }

        public TemplateDebugger Debugger { get; }

        public TemplateResult Result { get; }

        public TemplateDebugState Next()
        {
            Assert.True(_paused.TryTake(out TemplateDebugState? s, TimeSpan.FromSeconds(30)), "止まりません。");
            return s!;
        }

        public bool Finished => Result.Completion.Wait(TimeSpan.FromSeconds(30));

        public void Dispose()
        {
            Debugger.Resume(TemplateDebugCommand.Stop);
            Result.Completion.Wait(TimeSpan.FromSeconds(10));
            Result.Dispose();
        }
    }

    private static readonly string DebugTemplate = File.ReadAllText(Fixture("debug.hxt"));

    private static byte[] Seq(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)i)];

    [Fact]
    [Trait(TC, "TC-TPL-32-01")]
    public void BreakpointStopsAndShowsLocalsAndPosition()
    {
        using var s = new DebugSession(DebugTemplate, Seq(256), d => d.ToggleBreakpoint("debug.hxt", 7));
        TemplateDebugState state = s.Next();
        Assert.Equal(7, state.Location.Line);
        Assert.Equal(2, state.Position);
        Assert.Contains(state.Locals, v => v.Name == "sum" && v.Value == "1");
        Assert.Contains(state.Patterns, v => v.Name == "a" && v.Offset == 0);
        Assert.Contains(state.Patterns, v => v.Name == "b" && v.Offset == 1);

        // 止まっている間も結果ツリーを読める
        Assert.Equal(1L, Long(s.Result, "b"));
        s.Debugger.Resume(TemplateDebugCommand.Continue);
        Assert.True(s.Finished);
    }

    [Fact]
    [Trait(TC, "TC-TPL-32-02")]
    public void StepIntoAndStepOut()
    {
        using var s = new DebugSession(DebugTemplate, Seq(256), d => d.ToggleBreakpoint("debug.hxt", 8));
        Assert.Equal(8, s.Next().Location.Line);
        s.Debugger.Resume(TemplateDebugCommand.StepInto);
        TemplateDebugState inside = s.Next();
        Assert.Equal(2, inside.Location.Line);
        Assert.Equal(["twice", "(top level)"], inside.CallStack.Select(f => f.Name));
        s.Debugger.Resume(TemplateDebugCommand.StepOut);
        TemplateDebugState back = s.Next();
        Assert.True(back.Location.Line is 8 or 9, back.Location.ToString());
        Assert.Contains(back.Locals, v => v.Name == "d" && v.Value == "4");
        s.Debugger.AddWatch("c * 3");
        Assert.Equal("6", s.Result.EvaluateInPausedFrame("c * 3"));
        s.Debugger.Resume(TemplateDebugCommand.RunToCursor, "debug.hxt", 12);
        Assert.Equal(12, s.Next().Location.Line);
        s.Debugger.Resume(TemplateDebugCommand.Stop);
        Assert.True(s.Finished);
        Assert.Equal(TemplateRunState.Cancelled, s.Result.State);
    }

    [Fact]
    [Trait(TC, "TC-TPL-32-03")]
    public void ConditionalBreakpointStopsOnlyFrom0x1000()
    {
        using var s = new DebugSession("struct R { u8 data[0x100]; };\nR recs[0x20];\n", Seq(0x4000),
            d => d.SetBreakpoint(new TemplateBreakpoint("debug.hxt", 1) { Condition = "$ >= 0x1000" }));
        var stops = new List<long>();
        for (int i = 0; i < 16; i++)
        {
            stops.Add(s.Next().Position);
            s.Debugger.Resume(TemplateDebugCommand.Continue);
        }

        Assert.True(s.Finished);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => 0x1000L + (i * 0x100)), stops);
    }

    [Fact]
    [Trait(TC, "TC-TPL-32-04")]
    public void DataBreakpointStopsAtTheStatementThatCreatesThePattern()
    {
        using var s = new DebugSession(DebugTemplate, Seq(256), d => d.AddDataBreakpoint(0x10));
        Assert.Equal(10, s.Next().Location.Line);
        s.Debugger.Resume(TemplateDebugCommand.Continue);
        Assert.True(s.Finished);
    }

    [Fact]
    public void ErrorsStopTheDebuggerAtTheFailingLine()
    {
        using var s = new DebugSession(File.ReadAllText(Fixture("runtime-err.hxt")), Seq(256), _ => { }, "runtime-err.hxt");
        TemplateDebugState state = s.Next();
        Assert.Equal(TemplateDebugReason.Error, state.Reason);
        Assert.Equal(4, state.Location.Line);
        s.Debugger.Resume(TemplateDebugCommand.Continue);
        Assert.True(s.Finished);
    }

    // ───────────── エディタの言語の機能 (TPL-31) ─────────────

    [Fact]
    [Trait(TC, "TC-TPL-31-01")]
    public void MemberCompletionListsTheStructFields()
    {
        string text = "struct Header { u32 magic; u16 version; u16 count; };\nHeader hdr;\nu8 x[hdr.";
        TemplateCompletionResult c = TemplateLanguageService.Complete(text, text.Length, Path.Combine(_dir, "t.hxt"));
        Assert.Equal(["count", "magic", "version"], c.Items.Select(i => i.Label).Order());
        Assert.Equal("u32", c.Items.Single(i => i.Label == "magic").Detail);
        Assert.Equal("u16", c.Items.Single(i => i.Label == "version").Detail);

        TemplateCompletionResult attrs = TemplateLanguageService.Complete("u8 a [[col", 10, "t.hxt");
        Assert.Contains(attrs.Items, i => i.Label == "color");
        TemplateCompletionResult std = TemplateLanguageService.Complete("local u32 x = std::mem::re", 26, "t.hxt");
        Assert.Contains(std.Items, i => i.Label == "read_u32");
    }

    /// <summary>エディタの部品 (Monaco の字句解析) に渡す一覧が、色分け (<see cref="TemplateLanguageService.Classify"/>) と同じ分類になる。</summary>
    [Fact]
    public void SyntaxListsMatchTheClassification()
    {
        TemplateSyntaxLists lists = TemplateLanguageService.SyntaxLists();
        Assert.Contains("struct", lists.Keywords);
        Assert.Contains("import", lists.Keywords);
        Assert.Contains("u32", lists.Types);
        Assert.Contains("std::type::magic", lists.Types);
        Assert.Contains("color", lists.Attributes);
        const string text = "struct A { u32 x [[color(\"red\")]]; };";
        IReadOnlyList<TemplateClassifiedSpan> spans = TemplateLanguageService.Classify(text);
        foreach (TemplateClassifiedSpan s in spans)
        {
            string word = text.Substring(s.Start, s.Length);
            switch (s.Class)
            {
                case TemplateTokenClass.Keyword: Assert.Contains(word, lists.Keywords); break;
                case TemplateTokenClass.Type: Assert.Contains(word, lists.Types); break;
                case TemplateTokenClass.Attribute: Assert.Contains(word, lists.Attributes); break;
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-31-02")]
    public void MissingSemicolonIsMarkedAtTheEndOfTheLine()
    {
        IReadOnlyList<TemplateEditorDiagnostic> d = TemplateLanguageService.Diagnose("u8 a\nu8 b;\n", Path.Combine(_dir, "t.hxt"));
        TemplateEditorDiagnostic e = Assert.Single(d, x => x.Diagnostic.Severity == TemplateSeverity.Error);
        Assert.Equal(1, e.Line);
        Assert.Contains(";", e.Diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(d, x => x.Line == 2);
    }

    [Fact]
    [Trait(TC, "TC-TPL-31-04")]
    public void DefinitionOfAnImportedTypeOpensTheImportedFile()
    {
        string main = Path.Combine(TemplateFeatureTests.DataFolder, "import-set", "main.hxt");
        string text = File.ReadAllText(main);
        int caret = text.IndexOf("liba::Rec", StringComparison.Ordinal) + "liba::".Length + 1;
        TemplateSourceLocation? at = TemplateLanguageService.FindDefinition(text, caret, main);
        Assert.NotNull(at);
        Assert.EndsWith(Path.Combine("lib", "a.hxt"), at.Value.File, StringComparison.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(at.Value.File);
        Assert.Contains("struct Rec", lines[at.Value.Line - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStandardLibraryFunctionHasASignatureAndAHelpSection()
    {
        // TPL-18 の仕様 4: 引数・戻り値・エラーをリファレンス (補完の説明と docs/help/template-library.md。英語と日本語) に書く
        string help = File.ReadAllText(Core.Tests.Engine.SourceTests.FindRepoFile(TemplateLibraryReference.HelpPage)).ReplaceLineEndings("\n");
        Assert.Equal(TemplateLanguageService.StdFunctions.Order(StringComparer.Ordinal), TemplateLibraryReference.Signatures.Keys.Order(StringComparer.Ordinal));
        foreach (string name in TemplateLanguageService.StdFunctions)
        {
            int at = help.IndexOf("\n### " + name + "\n", StringComparison.Ordinal);
            Assert.True(at >= 0, "No help section: " + name);
            string section = help[at..(help.IndexOf("\n#", at + 5, StringComparison.Ordinal) is int next and > 0 ? next : help.Length)];
            Assert.Contains("- English: ", section, StringComparison.Ordinal);
            Assert.Contains("- 日本語: ", section, StringComparison.Ordinal);
        }

        TemplateCompletionResult c = TemplateLanguageService.Complete("local u32 x = std::mem::re", 26, "t.hxt");
        Assert.Equal("read_u32(offset, endian?) -> u32", c.Items.Single(i => i.Label == "read_u32").Detail);
    }

    [Fact]
    public void FormatAndToggleComment()
    {
        Assert.Equal("struct A {\n    u8 a;\n};\nA x;", TemplateLanguageService.Format("struct A {\nu8 a;   \n  };\nA x;"));
        Assert.Equal("// u8 a;\n// u8 b;", TemplateLanguageService.ToggleComment("u8 a;\nu8 b;", 1, 2));
        Assert.Equal("u8 a;\nu8 b;", TemplateLanguageService.ToggleComment("// u8 a;\n// u8 b;", 1, 2));
        Assert.Equal(2, TemplateLanguageService.FindReferences("u8 a; u8 b = a;", 3).Count);
        Assert.Contains(TemplateLanguageService.Classify("struct A { u8 a; }; // c"), s => s.Class == TemplateTokenClass.Comment);
        Assert.False(TemplateLoader.LoadText(TemplateLanguageService.NewTemplate("New")).HasErrors);
    }

    // ───────────── 自動適用 (TPL-30)・管理 (TPL-35) ─────────────

    private static DocumentSnapshot Snapshot(byte[] data) => new Document(new MemoryByteSource(data), new DocumentOptions()).Current;

    [Fact]
    [Trait(TC, "TC-TPL-30-01")]
    public void PngIsSuggestedByMagicEvenWithABinExtension()
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        byte[] png = File.ReadAllBytes(TestDataCatalog.Get("TD-PNG"));
        TemplateAutoApplyDecision byExt = TemplateAutoApply.Decide(catalog, Snapshot(png), "a.png", TemplateAutoApplyMode.Suggest, null, false);
        Assert.Equal(TemplateAutoApplyAction.Suggest, byExt.Action);
        Assert.Equal("PNG image", byExt.Top!.Entry.Metadata.Name);
        Assert.True(byExt.Top.Match.Magic);
        TemplateAutoApplyDecision bin = TemplateAutoApply.Decide(catalog, Snapshot(png), "a.bin", TemplateAutoApplyMode.Suggest, null, false);
        Assert.Equal("PNG image", bin.Top!.Entry.Metadata.Name);
        Assert.True(bin.Top.Match.Magic);
        Assert.False(bin.Top.Match.Extension);

        // 適用の記録があれば提案しない (仕様 6)、提案しないにしたテンプレートは出さない (仕様 5)
        Assert.Equal(TemplateAutoApplyAction.None, TemplateAutoApply.Decide(catalog, Snapshot(png), "a.png", TemplateAutoApplyMode.Suggest, null, true).Action);
        var suppressed = new HashSet<string>([byExt.Top.Entry.Path], StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("PNG image", TemplateAutoApply.Decide(catalog, Snapshot(png), "a.png", TemplateAutoApplyMode.Suggest, suppressed, false).Top?.Entry.Metadata.Name);
    }

    private delegate void DiskImage(long length, long offset, Span<byte> destination);

    [Fact]
    [Trait(TC, "TC-TPL-34-03")]
    public void DisksGetThePartitionTableTemplateAndGptOutranksTheProtectiveMbr()
    {
        // 物理ディスク (パスなし) の先頭 1 MiB。GPT のディスクには保護 MBR (0x1FE の 55 AA) もある
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        static byte[] Head(DiskImage image)
        {
            var data = new byte[1 << 20];
            image(100L << 30, 0, data);
            return data;
        }

        TemplateAutoApplyDecision mbr = TemplateAutoApply.Decide(catalog, Snapshot(Head(TestDataCatalog.MbrImage)), null, TemplateAutoApplyMode.Suggest, null, false);
        Assert.Equal("MBR partition table", mbr.Top!.Entry.Metadata.Name);
        TemplateAutoApplyDecision gpt = TemplateAutoApply.Decide(catalog, Snapshot(Head(TestDataCatalog.GptImage)), null, TemplateAutoApplyMode.Suggest, null, false);
        Assert.Equal("GPT partition table", gpt.Top!.Entry.Metadata.Name);
        Assert.Contains(gpt.Candidates, c => c.Entry.Metadata.Name == "MBR partition table");
    }

    [Fact]
    [Trait(TC, "TC-TPL-19-03")]
    public void TemplatesUsingExtensionsAreOnlySuggestedEvenWhenAlwaysApplying()
    {
        string folder = Path.Combine(_dir, "templates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ext-auto.hxt"),
            "#pragma magic 0 \"48 58 45 58\"\nextern fn myfmt(u32 v) -> string;\nu32 magic;\nle u32 v [[format(myfmt)]];\n");
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(folder);
        TemplateAutoApplyDecision d = TemplateAutoApply.Decide(catalog, Snapshot(Hex("48 58 45 58 2A 00 00 00")), "x.bin", TemplateAutoApplyMode.Always, null, false);
        Assert.Equal(TemplateAutoApplyAction.Suggest, d.Action);
        Assert.True(d.Top!.Entry.Metadata.UsesExtensions);
    }

    [Fact]
    [Trait(TC, "TC-TPL-30-02")]
    public void AlwaysApplyPicksTheZipTemplateWhenAvailable()
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        byte[] zip = File.ReadAllBytes(TestDataCatalog.Get("TD-ZIP"));
        TemplateAutoApplyDecision d = TemplateAutoApply.Decide(catalog, Snapshot(zip), "a.zip", TemplateAutoApplyMode.Always, null, false);
        Assert.Equal(TemplateAutoApplyAction.Apply, d.Action);
        Assert.Equal("zip", d.Top!.Entry.Name);

        // 適用した結果の 1 つ目のローカルファイルヘッダのファイル名が、TD-ZIP の 1 つ目のファイル名と一致する
        int nameLength = BitConverter.ToUInt16(zip, 26);
        string firstName = Encoding.UTF8.GetString(zip, 30, nameLength);
        TemplateResult r = Run(catalog.Load(d.Top.Entry), TemplateDataSource.FromBytes(zip));
        Assert.Equal(firstName, Value(r, "local_files[0].header.file_name"));
    }

    [Fact]
    [Trait(TC, "TC-TPL-30-04")]
    public void DetectionWithAThousandTemplatesIsFast()
    {
        string folder = Path.GetDirectoryName(TestDataCatalog.Get("TD-TPL-1000-TEMPLATES"))!;
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(folder);
        Assert.True(catalog.Entries.Count >= 1000); // 索引を作る (起動時)
        byte[] png = File.ReadAllBytes(TestDataCatalog.Get("TD-PNG"));
        DocumentSnapshot random = new Document(Core.Sources.FileByteSource.Open(TestDataCatalog.Get("TD-RANDOM-16M")), new DocumentOptions()).Current;
        for (int i = 0; i < 20; i++)
        {
            TemplateAutoApplyDecision p = TemplateAutoApply.Decide(catalog, Snapshot(png), "a.png", TemplateAutoApplyMode.Suggest, null, false);
            Assert.Equal("PNG image", p.Top!.Entry.Metadata.Name);
            Support.TimeBudget.Limit(p.Elapsed < TimeSpan.FromMilliseconds(200), $"判定に {p.Elapsed.TotalMilliseconds} ms");
            TemplateAutoApplyDecision r = TemplateAutoApply.Decide(catalog, random, "r.bin", TemplateAutoApplyMode.Suggest, null, false);
            Assert.DoesNotContain(r.Candidates, c => c.Entry.Name.StartsWith("gen-", StringComparison.Ordinal));
            Support.TimeBudget.Limit(r.Elapsed < TimeSpan.FromMilliseconds(200), $"判定に {r.Elapsed.TotalMilliseconds} ms");
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-30-05")]
    public void DetectionOnAHundredGigabyteImageReadsOnlyTheHeadAndTail()
    {
        var source = new CountingSource(100L * 1024 * 1024 * 1024);
        using var doc = new Document(source, new DocumentOptions());
        TemplateAutoApplyDecision d = TemplateAutoApply.Decide(TemplateCatalog.CreateDefault(null), doc.Current, "disk.img", TemplateAutoApplyMode.Always, null, false);
        Assert.True(source.BytesRead <= 128 * 1024 + (64 * 1024), $"{source.BytesRead} バイト");
        Assert.True(d.BytesRead <= 128 * 1024);
    }

    private sealed class CountingSource(long length) : ByteSourceBase
    {
        public long BytesRead;

        public override string DisplayName => "counting";

        public override string Identity { get; } = "counting:" + Guid.NewGuid().ToString("N");

        public override long Length => length;

        public override SourceCapabilities Capabilities => SourceCapabilities.None;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int n = (int)Math.Max(0, Math.Min(buffer.Length, length - offset));
            buffer[..n].Clear();
            Interlocked.Add(ref BytesRead, n);
            return new ReadResult(n);
        }
    }

    [Fact]
    [Trait(TC, "TC-TPL-35-02")]
    public void DisabledTemplatesAreNotCandidates()
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        var prefs = new TemplatePreferences();
        prefs.ApplyTo(catalog);
        byte[] png = File.ReadAllBytes(TestDataCatalog.Get("TD-PNG"));
        string pngPath = catalog.Find("png")!.Path;
        prefs.Disabled.Add(pngPath);
        Assert.DoesNotContain(catalog.Detect(Snapshot(png), "a.png"), c => c.Entry.Path == pngPath);
        Assert.DoesNotContain(catalog.Entries, e => e.Path == pngPath);
        prefs.Disabled.Clear();
        prefs.NoAutoApply.Add(pngPath);
        Assert.DoesNotContain(catalog.Detect(Snapshot(png), "a.png"), c => c.Entry.Path == pngPath);
        Assert.Contains(catalog.Entries, e => e.Path == pngPath);
        Assert.Equal(prefs.ToJson().ToJsonString(), TemplatePreferences.FromJson(prefs.ToJson()).ToJson().ToJsonString());
    }

    [Fact]
    [Trait(TC, "TC-TPL-35-03")]
    public void ExportedZipImportsIntoAnotherProfile()
    {
        string p1 = Path.Combine(_dir, "p1");
        CopyDirectory(Path.Combine(TemplateFeatureTests.DataFolder, "import-set"), p1);
        string zip = Path.Combine(_dir, "set.zip");
        IReadOnlyList<string> names = TemplateLibrary.Export([Path.Combine(p1, "main.hxt")], zip);
        Assert.Equal(["lib/a.hxt", "lib/b.hxt", "lib/common.hxt", "main.hxt"], names.Order(StringComparer.Ordinal));

        string p2 = Path.Combine(_dir, "p2");
        IReadOnlyList<string> imported = TemplateLibrary.Import(zip, p2);
        Assert.Equal(4, imported.Count);
        byte[] data = Hex("01 02 03 04 05 06");
        string Json(string folder) => TemplateJsonExporter.ToJson(Run(
            TemplateLoader.LoadFile(Path.Combine(folder, "main.hxt"), new TemplateLoadOptions { SearchPaths = [folder] }), TemplateDataSource.FromBytes(data)));
        Assert.Equal(Json(p1), Json(p2));
    }

    [Fact]
    [Trait(TC, "TC-TPL-34-04")]
    public void DuplicatingAStandardTemplateCopiesItToTheUserFolder()
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(null);
        TemplateCatalogEntry png = catalog.Find("png")!;
        Assert.True(TemplateCatalog.IsStandardPath(png.Path));
        string original = TemplateCatalog.ReadSource(png.Path);
        string copy = TemplateLibrary.Duplicate(png.Path, _dir, "// edit\n" + original);
        Assert.Equal(Path.Combine(_dir, "png.hxt"), copy);
        Assert.StartsWith("// edit", File.ReadAllText(copy), StringComparison.Ordinal);
        Assert.Equal(original, TemplateCatalog.ReadSource(png.Path));
    }

    [Fact]
    [Trait(TC, "TC-TPL-35-01")]
    public void TemplatesPutInTheUserFolderAppearWithoutRestart()
    {
        TemplateCatalog catalog = TemplateCatalog.CreateDefault(_dir);
        Assert.DoesNotContain(catalog.AllEntries, e => e.Entry.Metadata.Name == "Test image format");
        File.Copy(Fixture("name-ja.hxt"), Path.Combine(_dir, "name-ja.hxt"));
        (TemplateCatalogEntry entry, bool shadowed) = catalog.AllEntries.Single(e => e.Entry.Metadata.Name == "Test image format");
        Assert.Equal(TemplateOrigin.User, entry.Origin);
        Assert.False(shadowed);
        File.Delete(Path.Combine(_dir, "name-ja.hxt"));
        Assert.DoesNotContain(catalog.AllEntries, e => e.Entry.Metadata.Name == "Test image format");

        // 同じ名前のテンプレートは後のものが隠れる
        File.WriteAllText(Path.Combine(_dir, "png.hxt"), "#pragma name \"My PNG\"\nu8 x;");
        Assert.True(catalog.AllEntries.Single(e => e.Entry.Origin == TemplateOrigin.Standard && e.Entry.Name == "png").Shadowed);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
