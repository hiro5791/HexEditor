using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Formats;
using HexEditor.Core.Hashing;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Core.Tests.Scripting;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.Tests.Templates;
using HexEditor.Sdk.Plugins;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Plugins;

/// <summary>拡張ポイント (08 の AUTO-31) を本体の同じ種類の機能につないだ結果。</summary>
[Collection(SerialCollection.Name)]
public sealed class PluginExtensionTests
{
    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static async Task<PluginTestHost> SampleAsync()
    {
        var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Sample), "test.sample");
        await t.StartAsync();
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("test.sample")!.State);
        return t;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-31-01")]
    public async Task Sample_plugin_extensions_appear_in_the_same_places_as_built_in_features()
    {
        using PluginTestHost t = await SampleAsync();
        PluginManager m = t.Manager!;

        // 1. データインスペクタの型 (スクリプトの inspector.types と同じ一覧)。
        PluginIntegration.InspectorTypeEntry nibble = PluginIntegration.InspectorTypes(m).Single(x => x.Name == "nibble-swap8");
        Assert.Equal("21", nibble.Decode([0x12], bigEndian: false, uiThread: true));
        Assert.Equal("Sample Plugin", nibble.PluginName);

        // 2. ハッシュパネルの一覧 (HashCatalog.All) に出て、全体の和の下位 8 bit が 0x80。
        HashAlgorithmInfo sum8 = HashCatalog.All.Single(a => a.Id == "test.sample.sum8");
        Assert.Equal("Sample Plugin", sum8.PluginName);
        Assert.Equal([0x80], HashEngine.ComputeBytes(sum8, Bytes256));

        // 3. インポート・エクスポート (Copy As を含む本体の Importer・Exporter の形式)。
        Assert.Contains(PluginIntegration.ImportFormats(), f => f.Name == "sample-hexlines" && f.Plugin == "Sample Plugin");
        Assert.Contains(PluginIntegration.ExportFormats(), f => f.Name == "sample-hexlines" && f.IsText);
        var output = new MemoryStream();
        var source = new ExportSource { Read = (o, d) => Bytes256.AsSpan((int)o, d.Length).CopyTo(d), Length = 256, FileName = "bytes.bin" };
        Exporter.Write(source, [(0, 256)], new ExportOptions { Format = "test.sample.hexlines" }, output);
        string text = Encoding.ASCII.GetString(output.ToArray());
        Assert.StartsWith("00 01 02", text, StringComparison.Ordinal);
        Assert.Equal(16, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using (ImportResult imported = Importer.Decode(new MemoryStream(Encoding.ASCII.GetBytes("#hexlines\n" + text)), new ImportOptions { Format = "test.sample.hexlines" },
            t.Root, "x.hexlines"))
        {
            Assert.Equal(256, imported.Length);
        }

        Assert.Equal("test.sample.hexlines", Importer.Detect(Encoding.ASCII.GetBytes("#hexlines\n00 01\n")));

        // 4. データソース: 長さ 256 で開く。
        (RegisteredExtension reg, IByteSourceProvider provider) = m.Registry.Usable<IByteSourceProvider>().Single();
        Assert.Equal("sample-pattern", reg.DisplayName);
        using (var opened = PluginByteSource.Open(reg, provider, "256"))
        {
            Assert.Equal(256, opened.Length);
            using var doc = new Document(opened);
            Assert.False(doc.CanResize);
            byte[] head = new byte[4];
            doc.Current.Read(0, head);
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, head);
        }

        // 5. 変換ビュー: 00 の NOT は FF。
        ITransformView view = m.Registry.Usable<ITransformView>().Single().Extension;
        byte[] cell = [0x00];
        view.Forward(0, cell);
        Assert.Equal(0xFF, cell[0]);

        // データ演算の一覧にも出る。
        Assert.Contains(PluginIntegration.DataTransforms(), x => x.Registration.DisplayName == "sample-not");

        // 6. コマンド: 出力に hello。
        Assert.True(t.Execute("test.sample.hello"));
        Assert.Contains("hello", t.OutputOf("Sample Plugin"));
        Assert.Contains(m.Registry.Usable<IMenuContribution>(), x => x.Extension.CommandId == "test.sample.hello");

        // 7. テンプレートの関数 sample_double(x)。
        var registry = new TemplateExtensionRegistry { Permission = f => f.Owner?.StartsWith("plugin:", StringComparison.Ordinal) == true };
        PluginIntegration.RegisterTemplateFunctions(m, registry);
        TemplateResult r = TemplateTestSupport.RunOk("extern fn sample_double(u64 v) -> u64; le u32 x; local u64 doubled = sample_double(x) [[show]];",
            [21, 0, 0, 0], new TemplateApplyOptions { Extensions = registry });
        Assert.Equal(42L, TemplateTestSupport.Long(r, "doubled"));

        // ファイル形式の判定 (テンプレートの自動適用に使われる)。
        FileTypeReport report = new FileTypeDetector(FileTypeDatabase.BuiltIn).Detect(new BytesMagicData("SMPL...."u8.ToArray()));
        Assert.Contains(report.Candidates, c => c.Name == "Sample format" && c.Source.Contains("Sample Plugin", StringComparison.Ordinal));

        // 8. プラグイン名 (ツールチップ・設定画面) を持たない拡張ポイントはない。
        Assert.All(m.Registry.All, e => Assert.Equal("Sample Plugin", e.Plugin.NameFor("en")));
        Assert.All(m.Registry.All, e => Assert.Equal(ExtensionStatus.Active, e.Status));
    }

    private sealed class BytesMagicData(byte[] data) : IMagicData
    {
        public long Length => data.Length;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= data.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(data.Length - offset, destination.Length);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    [Fact]
    public async Task Scripts_see_plugin_inspector_types_and_plugin_apis()
    {
        using PluginTestHost t = await SampleAsync();
        using var s = new ScriptTestHost();
        s.Open(Bytes256);
        ScriptRunResult r = await s.RunCodeAsync("""
            const found = inspector.types.some(t => t.id === "test.sample.nibble-swap8");
            console.log("found=" + found);
            console.log(inspector.decode(doc, 0x12, "test.sample.nibble-swap8").text);
            const api = app.plugins.get("test.sample").api;
            console.log(api.greeting + " " + api.twice(21));
            console.log(app.plugins.list().map(p => p.id + ":" + p.state).join(","));
            console.log("encoded=" + Array.from(inspector.encode("test.sample.nibble-swap8", "21")).join(","));
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("found=true", s.OutputText);
        Assert.Contains("21", s.OutputText);
        Assert.Contains("hello from sample 42", s.OutputText);
        Assert.Contains("test.sample:enabled", s.OutputText);
        Assert.Contains("encoded=18", s.OutputText);
    }

    [PerfMachineFact]
    [Trait("TC", "TC-AUTO-31-02")]
    [Trait("Category", "Performance")]
    [Trait("Category", "Nightly")]
    public async Task Plugin_hash_of_10_gb_does_not_use_memory_proportional_to_the_size()
    {
        using PluginTestHost t = await SampleAsync();
        HashAlgorithmInfo sum8 = HashCatalog.All.Single(a => a.Id == "test.sample.sum8");
        using var doc = new Document(new VirtualByteSource(10L << 30, VirtualContent.Zero, resizable: false));
        GC.Collect();
        long before = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        long peak = before;
        using var sampling = new Timer(_ => Interlocked.Exchange(ref peak, Math.Max(Interlocked.Read(ref peak), System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64)),
            null, 0, 100);
        HashComputation c = await Task.Run(() => HashEngine.Compute(doc.Current, new HashRequest { Algorithms = [new HashAlgorithmChoice(sum8)] }));
        Assert.Equal([0x00], c.Rows.Single().Value);
        Assert.True(peak - before < 100L * 1024 * 1024, $"memory grew by {(peak - before) / 1024 / 1024} MB");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-31-03")]
    public async Task A_slow_inspector_type_on_the_ui_thread_is_warned_and_then_stopped()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Slow), "slow");
        await t.StartAsync();
        PluginIntegration.InspectorTypeEntry slow = PluginIntegration.InspectorTypes(t.Manager).Single(x => x.Name == "slow-type");

        // 1. 1 回目: 100 ms を超えた旨の警告。
        Assert.Equal("01", slow.Decode([1], bigEndian: false, uiThread: true));
        Assert.Contains(t.Output.LinesOf("plugin:slow"), l => l.Kind == ScriptOutputKind.Warning && l.Text.Contains("100 ms", StringComparison.Ordinal));

        // 2〜3. 続けて超えたら止め、以後は呼ばない (すぐに戻る)。
        slow.Decode([1], bigEndian: false, uiThread: true);
        Assert.Equal(PluginState.Suspended, t.Manager!.Find("slow")!.State);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            // 4. 値の代わりに停止の表示 (null)。
            Assert.Null(slow.Decode([1], bigEndian: false, uiThread: true));
        }

        Assert.True(watch.ElapsedMilliseconds < 200 * 2, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("TC", "TC-INSP-22-02")]
    public async Task Plugin_inspector_types_are_user_defined_types_with_the_time_limit_and_row_errors()
    {
        // インスペクタのユーザー定義型 (INSP-22) と同じ経路: Loop は時間切れ、Throw はその行だけのエラー、Echo は値。
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.InspectorTypes), "insptypes");
        await t.StartAsync();
        using var types = new HexEditor.Core.Inspector.InspectorUserTypes(Path.Combine(t.Root, "settings"))
        {
            PluginTypes = () => PluginIntegration.InspectorTypes(t.Manager),
        };
        types.Load();
        HexEditor.Core.Inspector.InspectorUserType Type(string name) => types.Types.Single(x => x.DisplayName == name);
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05];
        Assert.All(types.Types, x => Assert.Equal(HexEditor.Core.Inspector.InspectorUserTypeSource.Plugin, x.Source));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        HexEditor.Core.Inspector.InspectorUserValue loop = await types.EvaluateAsync(Type("Loop"), data, HexEditor.Core.Inspector.Endianness.Little);
        Assert.True(loop.TimedOut);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");

        // 止まったままの呼び出しがある間は、同じ型をもう一度呼ばずにすぐ時間切れにする。
        watch.Restart();
        Assert.True((await types.EvaluateAsync(Type("Loop"), data, HexEditor.Core.Inspector.Endianness.Little)).TimedOut);
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");

        Assert.Equal("test failure", (await types.EvaluateAsync(Type("Throw"), data, HexEditor.Core.Inspector.Endianness.Little)).Error);
        HexEditor.Core.Inspector.InspectorUserValue echo = await types.EvaluateAsync(Type("Echo"), data, HexEditor.Core.Inspector.Endianness.Little);
        Assert.Null(echo.Error);
        Assert.Equal("01 02 03 04", echo.Display);
        Assert.True((await types.EvaluateAsync(Type("Echo"), [0x01], HexEditor.Core.Inspector.Endianness.Little)).NotEnoughData);
    }

    [Fact]
    public async Task Slow_calls_off_the_ui_thread_are_not_limited()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Slow), "slow");
        await t.StartAsync();
        PluginIntegration.InspectorTypeEntry slow = PluginIntegration.InspectorTypes(t.Manager).Single(x => x.Name == "slow-type");
        Assert.Equal("01", slow.Decode([1], bigEndian: false, uiThread: false));
        Assert.Equal("02", slow.Decode([2], bigEndian: false, uiThread: false));
        Assert.Equal(PluginState.Enabled, t.Manager!.Find("slow")!.State);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-31-04")]
    public async Task Of_two_plugins_with_the_same_extension_id_the_later_one_is_disabled_with_a_warning()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Dup1), "dup1");
        t.Trust(t.Install(TestPlugins.Dup2), "dup2");
        await t.StartAsync();

        // 2. 警告と、dup2 の dup1.cmd が無効。
        Assert.Contains(t.Output.LinesOf("plugin:dup2"), l => l.Kind == ScriptOutputKind.Warning && l.Text.Contains("dup1.cmd", StringComparison.Ordinal));
        RegisteredExtension second = t.Manager!.Find("dup2")!.Extensions.Single();
        Assert.Equal(ExtensionStatus.Duplicate, second.Status);
        Assert.Equal(ExtensionStatus.Active, t.Manager.Find("dup1")!.Extensions.Single().Status);

        // 3. 実行すると dup1 のもの。
        Assert.True(t.Execute("dup1.cmd"));
        Assert.Contains("from dup1", t.OutputOf("dup1"));
        Assert.DoesNotContain("from dup2", t.OutputOf("dup2"));
    }

    [Fact]
    public async Task Extension_ids_must_be_scoped_to_the_plugin_and_must_not_shadow_built_ins()
    {
        using var t = new PluginTestHost();
        t.Trust(t.Install(TestPlugins.Throw with
        {
            Id = "scoped",
            TestJson = new System.Text.Json.Nodes.JsonObject
            {
                ["commands"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["id"] = "other.cmd", ["title"] = "x" },
                    new System.Text.Json.Nodes.JsonObject { ["id"] = "Bad Id", ["title"] = "x" },
                    new System.Text.Json.Nodes.JsonObject { ["id"] = "scoped.ok", ["title"] = "x", ["output"] = "ok" }),
            },
        }), "scoped");
        await t.StartAsync();
        IReadOnlyList<RegisteredExtension> ext = t.Manager!.Find("scoped")!.Extensions;
        Assert.Equal(ExtensionStatus.Invalid, ext.Single(e => e.Id == "other.cmd").Status);
        Assert.Equal(ExtensionStatus.Invalid, ext.Single(e => e.Id == "Bad Id").Status);
        Assert.Equal(ExtensionStatus.Active, ext.Single(e => e.Id == "scoped.ok").Status);
        Assert.Null(t.Manager.Registry.Find("other.cmd"));
    }

    [Fact]
    public async Task Plugin_documents_read_and_write_through_undoable_edits()
    {
        using PluginTestHost t = await SampleAsync();
        using var doc = new Document(new MemoryByteSource(new byte[16]));
        var pd = new PluginDocument(doc, "x.bin", null, () => 3, () => (2, 4));
        pd.Write(1, [0xAA, 0xBB]);
        Assert.Equal(new byte[] { 0, 0xAA, 0xBB, 0 }, pd.Read(0, 4));
        doc.Undo();
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pd.Read(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => pd.Write(15, [1, 2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => pd.Read(0, PluginDocument.MaxRead + 1));
    }
}
