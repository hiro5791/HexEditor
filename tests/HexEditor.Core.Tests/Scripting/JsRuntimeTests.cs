using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>JavaScript の実行環境 (AUTO-02) とスクリプトの実行 (AUTO-01) の基本。</summary>
public sealed class JsRuntimeTests
{
    [Fact]
    public async Task Console_output_goes_to_the_output_log()
    {
        using var t = new ScriptTestHost();
        ScriptRunResult r = await t.RunCodeAsync("console.log('hello from js'); console.warn('w'); console.error(new Uint8Array([1, 0xAB]));");
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("hello from js", t.OutputText);
        Assert.Contains(t.OutputLines, l => l.Text == "w" && l.Kind == ScriptOutputKind.Warning);
        Assert.Contains(t.OutputLines, l => l.Text == "Uint8Array(2) [01 AB]" && l.Kind == ScriptOutputKind.Error);
        Assert.Contains(t.OutputLines, l => l.Text.StartsWith("成功", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-02-01")]
    public async Task Modern_features_and_modules_work()
    {
        using var t = new ScriptTestHost();
        t.Open([0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8]);
        string lib = t.WriteScript("lib/util.js", "export const twice = x => x * 2;");
        t.WriteScript("tools/helper.mjs", "export function hello() { return 'helper'; }");
        string main = t.WriteScript("tools/main.js", """
            import { twice } from "util";
            import { hello } from "./helper.mjs";
            import { crc32 } from "hexeditor:hash";
            async function work() { await null; return twice(21); }
            const v = await work();
            const big = doc.readInt(4, "u64", "le");
            const bytes = doc.readBytes(0, 4);
            console.log(v, typeof big, big === 0x0807060504030201n, bytes instanceof Uint8Array, bytes.length, hello(), crc32(new Uint8Array([0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x38,0x39])).toString(16));
            """);
        _ = lib;
        t.TrustScript(main);
        ScriptRunResult r = await t.RunFileAsync(main);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("42 bigint true true 4 helper cbf43926", t.OutputText);
    }

    [Fact]
    public async Task Imports_outside_the_allowed_folders_are_refused()
    {
        using var t = new ScriptTestHost();
        string outside = Path.Combine(t.Root, "outside.js");
        File.WriteAllText(outside, "export const x = 1;");
        string main = t.WriteScript("main.js", $"import {{ x }} from {System.Text.Json.JsonSerializer.Serialize(outside)}; console.log(x);");
        t.TrustScript(main);
        ScriptRunResult r = await t.RunFileAsync(main);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Contains("このモジュールは読み込めません", r.Message);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-02-02")]
    public async Task Dotnet_types_are_not_reachable()
    {
        using var t = new ScriptTestHost();
        ScriptRunResult r = await t.RunCodeAsync("""
            const results = [];
            for (const probe of [() => System.IO.File, () => importNamespace('System'), () => clr, () => host]) {
              try { const v = probe(); results.push(v === undefined ? 'undefined' : 'reachable'); } catch (e) { results.push(e.name); }
            }
            results.push(typeof app.getType, typeof app.GetType, Object.keys(app).includes('getType') ? 'leak' : 'ok');
            console.log(results.join(','));
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("ReferenceError,ReferenceError,ReferenceError,ReferenceError,undefined,undefined,ok", t.OutputText);

        // API のオブジェクトのプロトタイプ・コンストラクタ・文字列化から .NET の型に届かない。ファイルも作れない (TC-AUTO-02-02 の期待結果 2・3)。
        string escape = Path.Combine(t.Root, "clr-escape.txt");
        ScriptRunResult probes = await t.RunCodeAsync($$"""
            const out = [];
            const tries = [
              () => Object.getPrototypeOf(app).constructor,
              () => Object.getPrototypeOf(app).constructor.name,
              () => app.constructor.constructor('return this')().System,
              () => String(app),
              () => String(app.documents),
              () => String(Object.getPrototypeOf(app.hash)),
              () => JSON.stringify(Object.getOwnPropertyNames(Object.getPrototypeOf(app))),
              () => importNamespace('System.IO').File.WriteAllText({{System.Text.Json.JsonSerializer.Serialize(escape)}}, 'x'),
            ];
            for (const f of tries) {
              try { out.push('probe:' + String(f())); } catch (e) { out.push('probe:blocked ' + e.name); }
            }
            console.log(out.join('\n'));
            """);
        ScriptTestHost.AssertSucceeded(probes);
        IReadOnlyList<string> lines = [.. t.OutputText.SelectMany(l => l.Split('\n')).Where(l => l.StartsWith("probe:", StringComparison.Ordinal))];
        Assert.Equal(8, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("System.", StringComparison.Ordinal) || l.Contains("HexEditor.", StringComparison.Ordinal));
        Assert.False(File.Exists(escape));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-02-05")]
    public async Task Infinite_recursion_ends_with_the_recursion_limit()
    {
        using var t = new ScriptTestHost(new ScriptLimits { RecursionDepth = 1000 });
        ScriptRunResult r = await t.RunCodeAsync("function f(n) { return f(n + 1) + 1; } f(0);");
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal(ScriptFailure.Limit, r.Failure);
        Assert.Contains("再帰の深さの上限 1000", r.Message);
    }

    [Fact]
    public async Task Maximum_recursion_setting_does_not_overflow_the_stack()
    {
        using var t = new ScriptTestHost(new ScriptLimits { RecursionDepth = ScriptLimits.MaxRecursion });
        ScriptRunResult r = await t.RunCodeAsync("function f(n) { return n === 0 ? 0 : f(n - 1) + 1; } console.log(f(9000)); function g(n) { return g(n + 1); } g(0);");
        Assert.Equal(ScriptFailure.Limit, r.Failure);
        Assert.Contains("9000", t.OutputText);
    }


    [Fact]
    [Trait("TC", "TC-AUTO-02-03")]
    public async Task Time_limit_pauses_and_continue_gives_another_period()
    {
        using var t = new ScriptTestHost(new ScriptLimits { Time = TimeSpan.FromSeconds(1) });
        t.Ui.ContinueAnswer = n => n == 1;
        ScriptRunResult r = await t.RunEditorAsync("while (true) {}");
        Assert.Equal(ScriptFailure.Limit, r.Failure);
        Assert.Equal(2, t.Ui.TimeoutPrompts);
        Assert.Contains("実行時間の上限 1 秒", r.Message);
    }

    [Fact]
    public async Task Headless_time_limit_ends_with_exit_code_7()
    {
        using var t = new ScriptTestHost(new ScriptLimits { Time = TimeSpan.FromSeconds(1) });
        string path = t.WriteScript("loop.js", "while (true) {}");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        Assert.Equal(7, r.ProcessExitCode);
        Assert.Equal(0, t.Ui.TimeoutPrompts);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-01-04")]
    public async Task Stop_ends_an_infinite_loop_within_three_seconds()
    {
        using var t = new ScriptTestHost();
        ScriptRun run = t.Start(new ScriptRunRequest { Code = "while(true){}", Origin = ScriptOrigin.Console, DisplayName = "loop.js" });
        await WaitUntilAsync(() => run.Status == ScriptRunStatus.Running);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        run.Stop();
        ScriptRunResult r = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Stopped, r.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3) + TimeSpan.FromSeconds(0.5) || !Support.TimeBudget.IsPerfMachine, $"{watch.Elapsed}");
    }

    [Fact]
    public async Task Cooperative_script_stops_with_the_signal()
    {
        using var t = new ScriptTestHost();
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "let n = 0; while (!script.signal.aborted) { n++; } console.log('stopped cleanly');",
            Origin = ScriptOrigin.Console,
        });
        await WaitUntilAsync(() => run.Status == ScriptRunStatus.Running);
        run.Stop();
        ScriptRunResult r = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Stopped, r.Status);
        Assert.Contains("stopped cleanly", t.OutputText);
    }

    [Fact]
    public async Task Syntax_errors_are_reported_with_the_line()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("bad.js", "const a = 1;\nconst b = ;\n");
        t.TrustScript(path);
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal(4, r.ProcessExitCode);
        Assert.Contains(r.Diagnostics, d => d.Line == 2);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-01-05")]
    public async Task Runtime_exception_reports_the_script_line_in_the_output()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("throws.js", "const x = 1;\n\nfunction boom() {\n  throw new Error('boom here');\n}\nboom();\n");
        t.TrustScript(path);
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        ScriptDiagnostic d = Assert.Single(r.Diagnostics);
        Assert.Equal(4, d.Line);
        Assert.Equal(Path.GetFullPath(path), d.Path);
        Assert.Contains(t.OutputLines, l => l.Kind == ScriptOutputKind.Error && l.Line == 4 && l.SourcePath == Path.GetFullPath(path) && l.Text.Contains("boom here"));
    }

    [Fact]
    public async Task Each_run_has_a_fresh_engine()
    {
        using var t = new ScriptTestHost();
        ScriptTestHost.AssertSucceeded(await t.RunCodeAsync("globalThis.leftover = 1;"));
        ScriptTestHost.AssertSucceeded(await t.RunCodeAsync("console.log(typeof leftover);"));
        Assert.Contains("undefined", t.OutputText);
    }

    [Fact]
    public async Task Uncaught_cancel_is_recorded_as_stopped_not_error()
    {
        using var t = new ScriptTestHost();
        ScriptRunResult r = await t.RunCodeAsync("throw new CanceledError('x');");
        Assert.Equal(ScriptRunStatus.Stopped, r.Status);
    }

    [Fact]
    public async Task Api_errors_have_the_common_names()
    {
        using var t = new ScriptTestHost();
        t.Open(new byte[16]);
        ScriptRunResult r = await t.RunCodeAsync("""
            const names = [];
            const tryIt = f => { try { f(); names.push('none'); } catch (e) { names.push(e.name + (e instanceof Error ? '' : '!')); } };
            tryIt(() => doc.readBytes(0, 17 * 1024 * 1024));
            tryIt(() => doc.readByte(100));
            tryIt(() => doc.readInt(0, 'q32'));
            tryIt(() => fs.readText('C:/Windows/win.ini'));
            tryIt(() => doc.readByte(2 ** 54));
            console.log(names.join(','));
            """);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("LimitError,RangeError,ArgumentError,PermissionError,RangeError", t.OutputText);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("条件が満たされませんでした。");
            }

            await Task.Delay(10);
        }
    }
}
