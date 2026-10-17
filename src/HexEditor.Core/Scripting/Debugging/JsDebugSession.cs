using System.Collections.Concurrent;
using HexEditor.Core.Scripting.Js;
using HexEditor.Core.Scripting.Repl;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Debugger;
using JintEngine = Jint.Engine;

namespace HexEditor.Core.Scripting.Debugging;

/// <summary>デバッグの状態。</summary>
public enum DebugState
{
    /// <summary>まだ実行を始めていない。</summary>
    Idle,
    Running,
    Paused,
    Ended,
}

/// <summary>始め方: そのまま実行するか、最初の行で止まるか (F10 / F11 で始めた場合。AUTO-21 の呼び出し)。</summary>
public enum DebugStart
{
    Run,
    StepIn,
}

/// <summary>例外で止まる設定 (AUTO-21 の仕様 2)。</summary>
public enum ExceptionBreakMode
{
    None,

    /// <summary>捕まえられない例外で止まる (既定)。</summary>
    Uncaught,
    All,
}

/// <summary>呼び出し履歴の 1 行 (関数名・行)。</summary>
public sealed record DebugFrame(string Function, string? Path, int Line, int Column);

/// <summary>スコープ (<c>block</c>・<c>local</c>・<c>closure</c>・<c>script</c>・<c>global</c> など) と、その変数。</summary>
public sealed record DebugScopeInfo(string Kind, IReadOnlyList<DebugVariable> Variables);

public sealed record DebugVariable(string Name, ReplValue Value);

/// <summary>止まったときの情報 (AUTO-21 の仕様 4)。<see cref="Reason"/>: <c>breakpoint</c> / <c>step</c> / <c>exception</c> / <c>pause</c> / <c>debugger</c> / <c>entry</c>。</summary>
public sealed record DebugPause(string Reason, string? Path, int Line, int Column, IReadOnlyList<DebugFrame> Frames, IReadOnlyList<DebugScopeInfo> Scopes,
    string? Exception);

/// <summary>ウォッチ式の結果。評価エラーは <see cref="Error"/> に入れる (その行に赤字で出す。AUTO-21 の「エラー」)。</summary>
public sealed record WatchResult(string Expression, ReplValue? Value, string? Error);

/// <summary>
/// JavaScript のデバッグ (AUTO-21)。Jint のデバッガをつなぎ、ブレークポイント・ステップ実行・変数の確認・式の評価を提供する。
/// 止まっている間はスクリプトのスレッドがここで待ち、その間は実行時間の上限に数えない (仕様 6)。ドキュメントの書き込みの排他は
/// スクリプトの実行が続いているので、そのまま保たれる (仕様 7)。
/// </summary>
public sealed class JsDebugSession
{
    private readonly BreakpointStore _store;
    private readonly BlockingCollection<Command> _commands = [];
    private readonly Lock _lock = new();
    private readonly List<string> _watches = [];
    private readonly Dictionary<int, int> _hits = [];
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private JintEngine? _engine;
    private ExecutionBudget? _budget;
    private ScriptContext? _context;
    private JsSourceMap _map = null!;
    private string _scriptPath = string.Empty;
    private HashSet<string> _globalBaseline = [];
    private volatile bool _breakpointsDirty;
    private volatile bool _pauseRequested;
    private volatile DebugState _state = DebugState.Idle;
    private DebugPause? _current;
    private ScriptRun? _run;

    public JsDebugSession(BreakpointStore store)
    {
        _store = store;
    }

    /// <summary>始め方。実行を始める前に決める。</summary>
    public DebugStart Start { get; set; } = DebugStart.Run;

    public ExceptionBreakMode ExceptionMode { get; set; } = ExceptionBreakMode.Uncaught;

    public DebugState State => _state;

    /// <summary>今止まっている位置 (止まっていなければ null)。</summary>
    public DebugPause? Current => _state == DebugState.Paused ? _current : null;

    /// <summary>実行 (開始後に設定される)。</summary>
    public ScriptRun? Run => _run;

    /// <summary>止まった (スクリプトのスレッドで発生する。UI は自分のスレッドに移して使う)。</summary>
    public event EventHandler<DebugPause>? Paused;

    /// <summary>再開した (続行・ステップ)。</summary>
    public event EventHandler? Resumed;

    /// <summary>実行が終わった。</summary>
    public event EventHandler? Ended;

    /// <summary>実行が終わるまで待つ。</summary>
    public Task WaitEndedAsync() => _ended.Task;

    /// <summary>ウォッチ式の一覧 (利用者が追加した式。止まるたびに評価する。AUTO-21 の仕様 4)。</summary>
    public IReadOnlyList<string> Watches
    {
        get
        {
            lock (_lock)
            {
                return [.. _watches];
            }
        }
    }

    public void AddWatch(string expression)
    {
        lock (_lock)
        {
            _watches.Add(expression);
        }
    }

    public void RemoveWatch(string expression)
    {
        lock (_lock)
        {
            _watches.Remove(expression);
        }
    }

    // ---- 操作 (どのスレッドからでも呼べる) ----

    public void Continue() => Resume(StepMode.None);

    public void StepOver() => Resume(StepMode.Over);

    public void StepIn() => Resume(StepMode.Into);

    public void StepOut() => Resume(StepMode.Out);

    /// <summary>実行中に止める (一時停止。次の文で止まる)。</summary>
    public void Pause()
    {
        if (_state != DebugState.Running || _engine is null)
        {
            return;
        }

        // Skip の返す値は、ステップの指定を置き換える: 実行中の指定がないときだけ (一時停止の要求の間だけ) 購読する。
        _pauseRequested = true;
        _engine.Debugger.Skip -= OnSkip;
        _engine.Debugger.Skip += OnSkip;
    }

    /// <summary>デバッグを止める (スクリプトを停止する)。</summary>
    public void Stop()
    {
        _run?.Stop();
        if (_state == DebugState.Paused)
        {
            _commands.Add(new ResumeCommand(StepMode.None, Abort: true));
        }
    }

    private void Resume(StepMode mode)
    {
        if (_state == DebugState.Paused)
        {
            _commands.Add(new ResumeCommand(mode, Abort: false));
        }
    }

    /// <summary>止まっている位置のスコープで式を評価する (コンソール・ウォッチ。AUTO-21 の仕様 5)。止まっていなければ例外。</summary>
    public Task<ReplValue> EvaluateAsync(string expression) => OnPausedThreadAsync(() => EvaluateNow(expression));

    /// <summary>ウォッチ式をすべて評価する。</summary>
    public Task<IReadOnlyList<WatchResult>> EvaluateWatchesAsync() => OnPausedThreadAsync<IReadOnlyList<WatchResult>>(() =>
    {
        var results = new List<WatchResult>();
        foreach (string expression in Watches)
        {
            try
            {
                results.Add(new WatchResult(expression, EvaluateNow(expression), null));
            }
            catch (DebugEvaluationFailed ex)
            {
                results.Add(new WatchResult(expression, null, ex.Message));
            }
        }

        return results;
    });

    private Task<T> OnPausedThreadAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_state != DebugState.Paused)
        {
            tcs.SetException(new InvalidOperationException(Msg.T("一時停止中ではありません。", "The script is not paused.")));
            return tcs.Task;
        }

        _commands.Add(new WorkCommand(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));
        return tcs.Task;
    }

    private sealed class DebugEvaluationFailed(string message) : Exception(message);

    private ReplValue EvaluateNow(string expression)
    {
        try
        {
            JsValue value = _engine!.Debugger.Evaluate(expression);
            return JsReplFormatter.Format(value);
        }
        catch (JavaScriptException ex)
        {
            throw new DebugEvaluationFailed(ErrorText(ex));
        }
        catch (DebugEvaluationException ex)
        {
            throw new DebugEvaluationFailed(ex.InnerException is JavaScriptException js ? ErrorText(js) : ex.Message);
        }
        catch (Acornima.ParseErrorException ex)
        {
            throw new DebugEvaluationFailed("SyntaxError: " + ex.Description);
        }
    }

    private static string ErrorText(JavaScriptException ex) =>
        ex.Error is Jint.Native.Object.ObjectInstance o && o.Get("message").IsString() ? $"{o.Get("name")}: {o.Get("message")}" : ex.Message;

    // ---- 実行環境からの接続 ----

    private abstract record Command;

    private sealed record ResumeCommand(StepMode Mode, bool Abort) : Command;

    private sealed record WorkCommand(Action Work) : Command;

    internal StepMode InitialStepMode => Start == DebugStart.StepIn ? StepMode.Into : StepMode.None;

    internal void Attach(ScriptContext context, JintEngine engine, ExecutionBudget budget, string mainKey, string code)
    {
        _context = context;
        _run = context.Run;
        _engine = engine;
        _budget = budget;
        _scriptPath = context.Path is not null ? Path.GetFullPath(context.Path) : mainKey;
        _map = JsSourceMap.Build(code);
        _globalBaseline = [.. engine.Global.GetOwnPropertyKeys(Types.String).Select(k => k.ToString())];
        SyncBreakpoints();
        engine.Debugger.Break += OnBreak;
        engine.Debugger.Step += OnStep;
        engine.Debugger.ExceptionThrown += OnException;
        _store.Changed += OnStoreChanged;
        budget.OnCheck = OnCheck;
        _lastMode = InitialStepMode;
        _state = DebugState.Running;
    }

    internal void Detach()
    {
        _store.Changed -= OnStoreChanged;
        if (_budget is not null)
        {
            _budget.OnCheck = null;
        }

        _state = DebugState.Ended;
        _commands.CompleteAdding();
        Ended?.Invoke(this, EventArgs.Empty);
        _ended.TrySetResult();
    }

    private void OnStoreChanged(object? sender, string path)
    {
        if (string.Equals(Path.GetFullPath(path), _scriptPath, StringComparison.OrdinalIgnoreCase))
        {
            _breakpointsDirty = true;
        }
    }

    /// <summary>実行の合間 (制約の確認) に呼ばれる。スクリプトのスレッドでブレークポイントの変更を反映する。</summary>
    private void OnCheck()
    {
        if (_breakpointsDirty)
        {
            _breakpointsDirty = false;
            SyncBreakpoints();
        }
    }

    private void SyncBreakpoints()
    {
        BreakPointCollection points = _engine!.Debugger.BreakPoints;
        points.Clear();
        if (!_map.Valid)
        {
            return;
        }

        foreach (Breakpoint bp in _store.For(_scriptPath).Where(b => b.Enabled))
        {
            if (_map.Resolve(bp.Line) is { } resolved)
            {
                points.Set(new BreakPoint(null, resolved.Line, resolved.Columns[0]));
            }
        }
    }

    // ---- Jint のイベント ----

    private StepMode OnBreak(object sender, DebugInformation info)
    {
        _stepOutFrom = null;
        int line = info.Location.Start.Line;
        if (info.PauseType == PauseType.DebuggerStatement)
        {
            return PauseAt(info, "debugger");
        }

        Breakpoint? bp = _store.For(_scriptPath).FirstOrDefault(b => b.Enabled && _map.Resolve(b.Line)?.Line == line);
        if (bp is null)
        {
            return StepMode.None;
        }

        _hits[bp.Line] = _hits.GetValueOrDefault(bp.Line) + 1;
        if (bp.HitCount > 0 && _hits[bp.Line] < bp.HitCount)
        {
            return StepMode.None;
        }

        if (bp.HasCondition)
        {
            try
            {
                JsValue condition = _engine!.Debugger.Evaluate(bp.Condition!);
                if (!TypeConverter.ToBoolean(condition))
                {
                    return StepMode.None;
                }
            }
            catch (Exception ex) when (ex is JavaScriptException or DebugEvaluationException or Acornima.ParseErrorException)
            {
                // 条件を評価できない場合は止める (エラーは出力パネルに出す)。
                _context?.Output(ScriptOutputKind.Warning, Msg.T($"ブレークポイントの条件を評価できません (行 {bp.Line}): {ex.Message}", $"Cannot evaluate the breakpoint condition (line {bp.Line}): {ex.Message}"));
            }
        }

        return PauseAt(info, "breakpoint");
    }

    private StepMode OnStep(object sender, DebugInformation info)
    {
        // 関数の宣言は実行する文ではない (巻き上げ)。ステップの対象にせず、同じ指定で次へ進む。
        if (info.CurrentNode is Acornima.Ast.FunctionDeclaration)
        {
            return _lastMode;
        }

        string reason = _first ? "entry" : "step";
        _first = false;
        DebugPause real = Snapshot(info.Location, info.CallStack, info.CurrentScopeChain, reason, null);

        // ステップアウト: 呼び出し元に戻った位置 (呼び出しの行) で止まる。次の操作で本当の次の文の位置を見せる (関数が値を返した後の位置)。
        if (_stepOutFrom is { } from)
        {
            _stepOutFrom = null;
            if (real.Frames.Count == from.Depth - 1 && real.Line != from.CallerLine)
            {
                _virtualReal = real;
                return PauseWith(real with { Line = from.CallerLine, Column = from.CallerColumn });
            }
        }

        return PauseWith(real);
    }

    private JsValue? _lastThrown;
    private StepMode _lastMode = StepMode.None;
    private (int Depth, int CallerLine, int CallerColumn)? _stepOutFrom;
    private DebugPause? _virtualReal;

    /// <summary>ステップ実行の対象にならない実行位置。一時停止の要求 (実行中に止める) をここで受ける。</summary>
    private StepMode OnSkip(object sender, DebugInformation info)
    {
        _engine!.Debugger.Skip -= OnSkip;
        if (_pauseRequested)
        {
            _pauseRequested = false;
            return PauseAt(info, "pause");
        }

        return StepMode.None;
    }

    private bool _first = true;

    private void OnException(object? sender, ExceptionThrownEventArgs e)
    {
        if (ExceptionMode == ExceptionBreakMode.None)
        {
            return;
        }

        // 同じ例外が呼び出しの層を伝わるたびに通知されることがある: 一度止まった例外では止まらない。
        if (ReferenceEquals(_lastThrown, e.ThrownValue))
        {
            return;
        }

        if (ExceptionMode == ExceptionBreakMode.Uncaught && IsCaughtSomewhere(e))
        {
            return;
        }

        string text = e.ThrownValue is Jint.Native.Object.ObjectInstance o && o.Get("message").IsString()
            ? $"{o.Get("name")}: {o.Get("message")}"
            : e.ThrownValue.ToString();
        // 例外の投げられた位置で止まる (再開してもステップの指定は使えない。例外はそのまま伝わる)。
        _lastThrown = e.ThrownValue;
        DebugCallStack stack = e.CallStack;
        CallFrame? top = stack.Count > 0 ? stack[0] : null;
        PauseWith(Snapshot(e.Location, stack, top?.ScopeChain, "exception", text));
    }

    private bool IsCaughtSomewhere(ExceptionThrownEventArgs e)
    {
        if (_map.IsCaught(e.Location.Start.Line, e.Location.Start.Column))
        {
            return true;
        }

        foreach (CallFrame frame in e.CallStack)
        {
            if (_map.IsCaught(frame.Location.Start.Line, frame.Location.Start.Column))
            {
                return true;
            }
        }

        return false;
    }

    // ---- 止まる ----

    private StepMode PauseAt(DebugInformation info, string reason) =>
        PauseWith(Snapshot(info.Location, info.CallStack, info.CurrentScopeChain, reason, null));

    private StepMode PauseWith(DebugPause pause)
    {
        _current = pause;
        _state = DebugState.Paused;
        using (_budget!.Pause())
        {
            Paused?.Invoke(this, pause);
            try
            {
                while (true)
                {
                    Command command;
                    try
                    {
                        command = _commands.Take(_context!.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 停止された (停止ボタン): スクリプトを終わらせる。
                        throw new ScriptTerminatedException(false);
                    }
                    catch (InvalidOperationException)
                    {
                        throw new ScriptTerminatedException(false);
                    }

                    switch (command)
                    {
                        case WorkCommand work:
                            work.Work();
                            break;
                        case ResumeCommand { Abort: true }:
                            _context!.RequestStop();
                            throw new ScriptTerminatedException(false);
                        case ResumeCommand resume:
                            if (_virtualReal is { } realPause && resume.Mode != StepMode.None)
                            {
                                // ステップアウトで「呼び出しの行」を見せていた: 次の操作で本当の次の文の位置を見せる (実行は進めない)。
                                _virtualReal = null;
                                _current = realPause;
                                Paused?.Invoke(this, realPause);
                                break;
                            }

                            _virtualReal = null;
                            if (resume.Mode == StepMode.Out && _current is { Frames.Count: >= 2 } leaving)
                            {
                                _stepOutFrom = (leaving.Frames.Count, leaving.Frames[1].Line, leaving.Frames[1].Column);
                            }

                            _lastMode = resume.Mode;
                            _state = DebugState.Running;
                            _current = null;
                            Resumed?.Invoke(this, EventArgs.Empty);
                            return resume.Mode;
                    }
                }
            }
            finally
            {
                if (_state == DebugState.Paused)
                {
                    _state = DebugState.Running;
                }
            }
        }
    }

    private DebugPause Snapshot(Acornima.SourceLocation location, DebugCallStack callStack, DebugScopes? scopeChain, string reason, string? exception)
    {
        var frames = new List<DebugFrame>();
        foreach (CallFrame frame in callStack)
        {
            frames.Add(new DebugFrame(string.IsNullOrEmpty(frame.FunctionName) ? Msg.T("(トップレベル)", "(top level)") : frame.FunctionName,
                PathOf(frame.Location), frame.Location.Start.Line, frame.Location.Start.Column + 1));
        }

        var scopes = new List<DebugScopeInfo>();
        var localVariables = new List<DebugVariable>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool localAdded = false;
        void AddLocal()
        {
            if (!localAdded)
            {
                localAdded = true;
                scopes.Add(new DebugScopeInfo("local", localVariables));
            }
        }

        foreach (DebugScope scope in scopeChain is { } chain ? chain : Enumerable.Empty<DebugScope>())
        {
            bool isLocal = scope.ScopeType is DebugScopeType.Block or DebugScopeType.Local or DebugScopeType.Catch;
            var variables = isLocal ? localVariables : [];
            if (isLocal)
            {
                AddLocal();
            }

            var formatter = new JsReplFormatter { MaxDepth = 2 };
            foreach (string name in scope.BindingNames)
            {
                if (scope.ScopeType == DebugScopeType.Global && (_globalBaseline.Contains(name) || name == "undefined"))
                {
                    continue;
                }

                // 内側のスコープ (ブロック) の値を優先し、関数のスコープの巻き上げた同名の束縛は出さない。
                if (isLocal && !seen.Add(name) || name == "arguments")
                {
                    continue;
                }

                try
                {
                    variables.Add(new DebugVariable(name, formatter.Convert(scope.GetBindingValue(name) ?? JsValue.Undefined, 0)));
                }
                catch (JavaScriptException)
                {
                    variables.Add(new DebugVariable(name, ReplValue.Of(ReplValueKind.Other, "uninitialized", "<uninitialized>")));
                }
            }

            if (!isLocal && variables.Count > 0)
            {
                scopes.Add(new DebugScopeInfo(KindOf(scope.ScopeType), variables));
            }
        }

        return new DebugPause(reason, PathOf(location), location.Start.Line, location.Start.Column + 1, frames, scopes, exception);
    }

    private static string KindOf(DebugScopeType type) => type switch
    {
        DebugScopeType.Local => "local",
        DebugScopeType.Closure => "closure",
        DebugScopeType.Block => "block",
        DebugScopeType.Catch => "catch",
        DebugScopeType.Global => "global",
        DebugScopeType.Script or DebugScopeType.Module => "script",
        _ => type.ToString().ToLowerInvariant(),
    };

    private string? PathOf(Acornima.SourceLocation location)
    {
        string? source = location.SourceFile;
        if (string.IsNullOrEmpty(source) || source.StartsWith("script:", StringComparison.Ordinal) || source == _scriptPath)
        {
            return _context?.Path is not null ? Path.GetFullPath(_context.Path) : null;
        }

        return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : source;
    }
}
