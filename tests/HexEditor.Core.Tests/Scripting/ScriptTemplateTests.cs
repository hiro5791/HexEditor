using System.Security.Cryptography;
using HexEditor.Core.Scripting;
using HexEditor.Core.Templates;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// スクリプトとテンプレートエンジンの結合 (AUTO-13、TPL-19): 本物のテンプレートエンジン (<see cref="TemplateScriptEngine"/>) と
/// 標準テンプレート (TPL-34) を使う。
/// </summary>
public sealed class ScriptTemplateTests
{
    private static TemplateScriptEngine Engine(string? userFolder = null) => new(TemplateCatalog.CreateDefault(userFolder));

    [Fact]
    [Trait("TC", "TC-AUTO-13-03")]
    public async Task Assigning_a_node_value_writes_the_document_and_one_undo_restores_it()
    {
        using var t = new ScriptTestHost(language: "en");
        byte[] pe = TestDataCatalog.PeX64();
        CoreDocumentHost doc = t.Open(pe, "pe.exe");
        t.Env.Templates = Engine();
        ScriptRunResult r = await t.RunCodeAsync("""
            const root = await doc.applyTemplate("pe", { show: false });
            root.get("header.e_lfanew").value = 0x80;
            """);
        ScriptTestHost.AssertSucceeded(r);

        byte[] after = t.OnUi(() =>
        {
            byte[] b = new byte[4];
            doc.Document.Current.Read(0x3C, b);
            return b;
        });
        Assert.Equal(new byte[] { 0x80, 0, 0, 0 }, after);

        t.OnUi(doc.Document.Undo);
        byte[] restored = t.OnUi(() => CSharpScriptTests.ReadAll(doc));
        Assert.Equal(SHA256.HashData(pe), SHA256.HashData(restored));
    }

    [Fact]
    [Trait("TC", "TC-TPL-19-01")]
    public async Task A_format_function_registered_from_javascript_is_used_by_a_template()
    {
        using var t = new ScriptTestHost(language: "en");
        string folder = Path.Combine(t.Root, "templates");
        Directory.CreateDirectory(folder);
        string template = Path.Combine(folder, "myfmt.hxt");
        File.WriteAllText(template, "extern fn myfmt(u32 v) -> string;\nu8 a;\nle u32 v [[format(myfmt)]];\nu8 b;\n");
        t.Open([0x01, 0x2A, 0x00, 0x00, 0x00, 0x02], "ext.bin");
        TemplateScriptEngine engine = Engine(folder);
        t.Env.Templates = engine;

        ScriptRunResult r = await t.RunCodeAsync("""
            const ext = templates.registerExtension("myfmt", v => "value=" + v, { point: "format", params: ["integer"], returns: "string" });
            const root = await doc.applyTemplate("myfmt", { show: false });
            console.log(root.get("v").displayValue, root.get("a").value, root.get("b").value);
            ext.off();
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("value=42 1 2", t.OutputText);
        Assert.True(engine.Catalog.Find("myfmt")!.Metadata.UsesExtensions);

        // 登録を外した後 (スクリプトの終了後) は呼べない: その部分だけエラーになり、ほかのパターンは表示される (TPL-19 の仕様 2・4)。
        Assert.Empty(engine.Extensions.Functions);
        ScriptRunResult r2 = await t.RunCodeAsync("""
            const root = await doc.applyTemplate("myfmt", { show: false });
            console.log(root.get("v").displayValue.startsWith("<error"), root.get("b").value);
            """);
        ScriptTestHost.AssertSucceeded(r2);
        Assert.Contains("true 2", t.OutputText);
    }

    [Fact]
    public async Task Extensions_are_removed_when_the_script_is_stopped_and_bad_arguments_are_rejected()
    {
        using var t = new ScriptTestHost(language: "en");
        TemplateScriptEngine engine = Engine();
        t.Env.Templates = engine;
        t.Open([0x00], "x.bin");
        ScriptRun run = t.Start(new ScriptRunRequest { Code = "templates.registerExtension('twice', x => x * 2, { params: ['integer'], returns: 'integer' }); console.log('registered');", Origin = ScriptOrigin.Console, DisplayName = "ext.js" });
        await JsRuntimeTests.WaitUntilAsync(() => engine.Extensions.Find("twice") is not null && run.IsResident);
        Assert.Equal(42L, engine.Extensions.Find("twice")!.Invoke([21L], CancellationToken.None));
        run.Stop();
        await run.Completion;
        Assert.Null(engine.Extensions.Find("twice"));

        ScriptRunResult bad = await t.RunCodeAsync("""
            for (const f of [() => templates.registerExtension("1x", () => 0), () => templates.registerExtension("ok", () => 0, { point: "nope" }),
                             () => templates.registerExtension("ok", () => 0, { params: ["int"] })]) {
                try { f(); console.log("no error"); } catch (e) { console.log(e.name); }
            }
            """);
        ScriptTestHost.AssertSucceeded(bad);
        Assert.Equal(3, t.OutputText.Count(l => l == "ArgumentError"));
        Assert.Empty(engine.Extensions.Functions);
    }

    [Fact]
    public async Task List_detect_and_template_errors_with_positions()
    {
        using var t = new ScriptTestHost(language: "en");
        string folder = Path.Combine(t.Root, "templates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "broken.hxt"), "u8 a;\nstruct { ;\n");
        t.Env.Templates = Engine(folder);
        t.Open(TestDataCatalog.Png16(), "image.dat");
        ScriptRunResult r = await t.RunCodeAsync("""
            const names = templates.list().map(x => x.name);
            console.log(names.includes("pe"), names.includes("png"), names.includes("broken"));
            console.log(templates.detect(doc)[0].name);
            try { await doc.applyTemplate("broken"); } catch (e) { console.log(e.name, /broken\.hxt:\d+:\d+\)/.test(e.message)); }
            try { await doc.applyTemplate("no-such"); } catch (e) { console.log(e.name); }
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("true true true", t.OutputText);
        Assert.Contains("png", t.OutputText);
        Assert.Contains("ArgumentError true", t.OutputText);
        Assert.Contains("ArgumentError", t.OutputText);
    }
}
