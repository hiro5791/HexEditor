using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services.Scripting;

/// <summary>スクリプトエディタの実行の状態。</summary>
public enum EditorRunState
{
    Idle,
    Running,
    Paused,
}

/// <summary>
/// スクリプトエディタ 1 つ分の実行とデバッグ (AUTO-20 のツールバー、AUTO-21、AUTO-22)。実行・デバッグ実行・停止・一時停止・ステップを
/// <see cref="ScriptingService"/> と <see cref="JsDebugSession"/> につなぎ、状態の変化を UI のスレッドで知らせる。
/// </summary>
internal sealed class ScriptEditorSession : IDisposable
{
    private readonly DispatcherQueue _queue;
    private ScriptRun? _run;
    private JsDebugSession? _debug;
    private bool _disposed;

    public ScriptEditorSession(string path, DispatcherQueue queue)
    {
        Path = path;
        Language = ScriptHost.LanguageOf(path) ?? ScriptLanguage.JavaScript;
        _queue = queue;
    }

    public string Path { get; }

    public ScriptLanguage Language { get; }

    public EditorRunState State { get; private set; }

    /// <summary>JavaScript のデバッグ中か (一時停止中のコンソールの評価の宛先)。</summary>
    public JsDebugSession? Debug => _debug;

    public DebugPause? CurrentPause => _debug?.Current;

    /// <summary>実行中のスクリプト。</summary>
    public ScriptRun? Run => _run;

    /// <summary>例外で止まる設定 (設定 <c>scripts.debug.exceptions</c>)。</summary>
    public static ExceptionBreakMode ExceptionMode() => App.Settings.GetString("scripts.debug.exceptions", "uncaught") switch
    {
        "none" => ExceptionBreakMode.None,
        "all" => ExceptionBreakMode.All,
        _ => ExceptionBreakMode.Uncaught,
    };

    /// <summary>状態が変わった (実行を始めた・止まった・再開した・終わった)。UI のスレッドで発生する。</summary>
    public event EventHandler? StateChanged;

    /// <summary>止まった。UI のスレッドで発生する。</summary>
    public event EventHandler<DebugPause>? Paused;

    /// <summary>終わった。UI のスレッドで発生する。</summary>
    public event EventHandler<ScriptRunResult>? Ended;

    /// <summary>実行する (F5)。未保存の本文もそのまま実行する (利用者が書いたもの。AUTO-34 の仕様 1。
    /// ただし信頼されていないファイルを書き換えた本文は、ファイルと同じく信頼の確認を通す。ScriptTrustCheck)。</summary>
    public ScriptRun? Start(string code, bool debug, DebugStart start = DebugStart.Run)
    {
        if (State != EditorRunState.Idle)
        {
            return null;
        }

        JsDebugSession? session = null;
        if (debug && Language == ScriptLanguage.JavaScript)
        {
            session = new JsDebugSession(ScriptWorkspace.Breakpoints) { Start = start, ExceptionMode = ExceptionMode() };
            session.Paused += (_, pause) => _queue.TryEnqueue(() => OnPaused(pause));
            session.Resumed += (_, _) => _queue.TryEnqueue(() => SetState(EditorRunState.Running));
        }

        _debug = session;
        ScriptRun run = ScriptingService.Start(new ScriptRunRequest
        {
            Path = Path,
            Code = code,
            Origin = ScriptOrigin.Editor,
            Debugger = session,
            Debug = debug && Language == ScriptLanguage.CSharp,
        });
        _run = run;
        SetState(EditorRunState.Running);
        _ = run.Completion.ContinueWith(task => _queue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_run, run) || _disposed)
            {
                return;
            }

            _run = null;
            _debug = null;
            State = EditorRunState.Idle;
            StateChanged?.Invoke(this, EventArgs.Empty);
            Ended?.Invoke(this, task.Result);
        }), TaskScheduler.Default);
        return run;
    }

    private void OnPaused(DebugPause pause)
    {
        SetState(EditorRunState.Paused);
        Paused?.Invoke(this, pause);
    }

    private void SetState(EditorRunState state)
    {
        if (State != state && !_disposed)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>停止 (Shift+F5)。デバッグ中は一時停止中でも止まる。</summary>
    public void Stop()
    {
        if (_debug is { } debug)
        {
            debug.Stop();
        }
        else
        {
            _run?.Stop();
        }
    }

    public void Continue() => _debug?.Continue();

    public void StepOver() => _debug?.StepOver();

    public void StepIn() => _debug?.StepIn();

    public void StepOut() => _debug?.StepOut();

    public void PauseNow() => _debug?.Pause();

    public void Dispose()
    {
        _disposed = true;
        if (_run is not null)
        {
            Stop();
        }
    }
}
