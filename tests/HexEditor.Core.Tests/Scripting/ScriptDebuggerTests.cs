using System.Threading.Channels;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using HexEditor.Core.Scripting.Repl;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// JavaScript のデバッガ (AUTO-21)。画面の操作 (余白のクリック・F9〜F11) は UI のテストで確かめる。ここでは Core のデバッグの
/// セッション (ブレークポイント・ステップ・変数・式の評価・時間の扱い) を、TD-AUTO-JS-DEBUG と同じ 21 行のスクリプトで確かめる。
/// </summary>
public sealed class ScriptDebuggerTests : IDisposable
{
    /// <summary>TD-AUTO-JS-DEBUG (21 行)。</summary>
    public const string DebugScript =
        "function inner() {\n" +
        "  const p = 1;\n" +
        "  const q = p + 1;\n" +
        "  return q;\n" +
        "}\n" +
        "function main() {\n" +
        "  const a = 1;\n" +
        "  const b = \"two\";\n" +
        "  const arr = [1, 2, 3];\n" +
        "  console.log(a, b, arr.length);\n" +
        "  let r = 0;\n" +
        "  r += 1;\n" +
        "  r = inner();\n" +
        "  console.log(\"r\", r);\n" +
        "  let s = 0;\n" +
        "  for (let i = 0; i < 10; i++) {\n" +
        "    s += i;\n" +
        "  }\n" +
        "  console.log(\"end\");\n" +
        "}\n" +
        "main();\n";

    private readonly ScriptTestHost _host = new();

    public void Dispose() => _host.Dispose();

    /// <summary>止まった位置を順に受け取る。</summary>
    private sealed class Pauses
    {
        private readonly Channel<DebugPause> _channel = Channel.CreateUnbounded<DebugPause>();

        public Pauses(JsDebugSession session) => session.Paused += (_, p) => _channel.Writer.TryWrite(p);

        public ScriptRun? Run { get; set; }

        public async Task<DebugPause> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<DebugPause> read = _channel.Reader.ReadAsync(timeout.Token).AsTask();
            if (Run is not null)
            {
                Task first = await Task.WhenAny(read, Run.Completion);
                if (first != read && !read.IsCompleted)
                {
                    ScriptRunResult result = await Run.Completion;
                    throw new InvalidOperationException($"The run ended without pausing: {result.Status} {result.Message} {result.StackTrace}");
                }
            }

            return await read;
        }
    }

    private (ScriptRun Run, JsDebugSession Session, Pauses Pauses, string Path) Begin(BreakpointStore store, Action<JsDebugSession>? configure = null,
        string code = DebugScript, string name = "debug.js")
    {
        string path = _host.WriteScript(name, code);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        configure?.Invoke(session);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;
        return (run, session, pauses, path);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-21-01")]
    public async Task Pauses_at_a_breakpoint_and_shows_the_local_variables()
    {
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Toggle(path, 10);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;

        DebugPause pause = await pauses.NextAsync();
        Assert.Equal(10, pause.Line);
        Assert.Equal("breakpoint", pause.Reason);
        string dump = string.Join(" | ", pause.Scopes.Select(s => s.Kind + ": " + string.Join(", ", s.Variables.Select(v => v.Name + "=" + v.Value.Text))));
        DebugScopeInfo local = Assert.Single(pause.Scopes, s => s.Kind == "local");
        Assert.True(local.Variables.Any(v => v.Name == "a" && v.Value.Text == "1"), dump);
        Assert.Equal("1", local.Variables.Single(v => v.Name == "a").Value.Text);
        Assert.Equal("\"two\"", local.Variables.Single(v => v.Name == "b").Value.Text);
        ReplValue arr = local.Variables.Single(v => v.Name == "arr").Value;
        Assert.Equal(3, arr.Children.Count);
        Assert.Equal("main", pause.Frames[0].Function);

        session.Stop();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Stopped, result.Status);
        await session.WaitEndedAsync();
    }

    [Fact]
    [Trait("TC", "TC-AUTO-21-02")]
    public async Task Steps_over_into_and_out_to_the_expected_lines()
    {
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Toggle(path, 13);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;

        Assert.Equal(13, (await pauses.NextAsync()).Line);

        session.StepIn();
        Assert.Equal(2, (await pauses.NextAsync()).Line);
        session.StepOver();
        Assert.Equal(3, (await pauses.NextAsync()).Line);
        session.StepOut();
        Assert.Equal(13, (await pauses.NextAsync()).Line);
        session.StepOver();
        Assert.Equal(14, (await pauses.NextAsync()).Line);

        session.Continue();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        ScriptTestHost.AssertSucceeded(result);
        Assert.Contains("end", _host.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-21-03")]
    public async Task A_conditional_breakpoint_stops_only_when_the_condition_is_true()
    {
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Set(path, new Breakpoint(17) { Condition = "i === 7" });
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;

        DebugPause pause = await pauses.NextAsync();
        Assert.Equal(17, pause.Line);
        ReplValue i = await session.EvaluateAsync("i");
        Assert.Equal("7", i.Text);

        session.Continue();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        ScriptTestHost.AssertSucceeded(result);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-21-04")]
    public async Task Evaluates_expressions_in_the_paused_scope()
    {
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Toggle(path, 10);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;
        await pauses.NextAsync();

        Assert.Equal("42", (await session.EvaluateAsync("a + 41")).Text);
        Assert.Equal("3", (await session.EvaluateAsync("b.length")).Text);

        // ウォッチ式: 評価エラーはその式の結果に入り、デバッグは続く。
        session.AddWatch("a * 2");
        session.AddWatch("notDefined + 1");
        IReadOnlyList<WatchResult> watches = await session.EvaluateWatchesAsync();
        Assert.Equal("2", watches[0].Value!.Text);
        Assert.Null(watches[1].Value);
        Assert.Contains("notDefined", watches[1].Error);

        session.Continue();
        ScriptTestHost.AssertSucceeded(await run.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Debug_start_with_step_in_stops_on_the_first_line()
    {
        var store = new BreakpointStore(null);
        var (run, session, pauses, _) = Begin(store, s => s.Start = DebugStart.StepIn);
        DebugPause pause = await pauses.NextAsync();
        Assert.Equal(21, pause.Line);
        session.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stopping_while_paused_ends_the_script()
    {
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Toggle(path, 12);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session });
        pauses.Run = run;
        await pauses.NextAsync();
        session.Stop();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Stopped, result.Status);
        Assert.DoesNotContain("end", _host.OutputText);
    }

    [Fact]
    public async Task An_uncaught_exception_stops_at_the_throw_by_default_but_a_caught_one_does_not()
    {
        const string code = "try { throw new Error('caught-one'); } catch (e) { console.log('c'); }\nfunction f() {\n  throw new Error('uncaught-one');\n}\nf();\n";
        var store = new BreakpointStore(null);
        var (run, session, pauses, _) = Begin(store, code: code, name: "ex.js");
        DebugPause pause = await pauses.NextAsync();
        Assert.Equal("exception", pause.Reason);
        Assert.Equal(3, pause.Line);
        Assert.Contains("uncaught-one", pause.Exception);
        session.Continue();
        ScriptRunResult result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Breakpoints_are_stored_per_script_and_survive_a_restart()
    {
        string file = Path.Combine(_host.Root, "breakpoints.json");
        string a = _host.WriteScript("a.js", "1;");
        string b = _host.WriteScript("b.js", "2;");
        var store = new BreakpointStore(file);
        store.Toggle(a, 3);
        store.Set(a, new Breakpoint(7) { Condition = "x > 1", HitCount = 2, Enabled = false });
        store.Toggle(b, 1);

        var reloaded = new BreakpointStore(file);
        Assert.Equal([3, 7], reloaded.For(a).Select(x => x.Line));
        Assert.Equal("x > 1", reloaded.Find(a, 7)!.Condition);
        Assert.False(reloaded.Find(a, 7)!.Enabled);
        Assert.Equal(2, reloaded.Find(a, 7)!.HitCount);
        Assert.Equal([1], reloaded.For(b).Select(x => x.Line));
        await Task.CompletedTask;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-21-05")]
    public async Task Time_spent_paused_does_not_count_toward_the_execution_limit()
    {
        // 実行時間の上限を 1 秒にして 3 秒止める (実際の 5 分と同じ仕組み: 一時停止の間は時間を数えない)。
        _host.Limits = new ScriptLimits { Time = TimeSpan.FromSeconds(1) };
        var store = new BreakpointStore(null);
        string path = _host.WriteScript("debug.js", DebugScript);
        store.Toggle(path, 10);
        _host.TrustScript(path);
        var session = new JsDebugSession(store);
        var pauses = new Pauses(session);
        ScriptRun run = _host.Start(new ScriptRunRequest { Path = path, Debugger = session, StopOnTimeLimit = true });
        pauses.Run = run;
        await pauses.NextAsync();
        await Task.Delay(3000);
        Assert.Equal(DebugState.Paused, session.State);
        Assert.False(run.Completion.IsCompleted);
        session.Continue();
        ScriptTestHost.AssertSucceeded(await run.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
