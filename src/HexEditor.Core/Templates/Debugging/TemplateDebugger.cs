using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Debugging;

/// <summary>ブレークポイント (TPL-32 の仕様 1)。行は 1 から。</summary>
public sealed record TemplateBreakpoint(string File, int Line)
{
    /// <summary>条件 (例: <c>$ &gt;= 0x1000</c>)。真のときだけ止まる。</summary>
    public string? Condition { get; init; }

    /// <summary>n 回目に止まる (null は毎回)。</summary>
    public int? HitCount { get; init; }

    /// <summary>ログポイント: 止まらずにこの式の値を出力パネルに出す。</summary>
    public string? LogMessage { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>呼び出し履歴の 1 段 (型と関数の入れ子。TPL-32 の仕様 3)。</summary>
public sealed record TemplateDebugFrame(string Name, TemplateSourceLocation? Location, int Depth);

/// <summary>変数・パターンの 1 行 (名前、値、パターンならオフセット)。</summary>
public sealed record TemplateDebugVariable(string Name, string Value, long? Offset, bool IsPattern);

/// <summary>止まった理由。</summary>
public enum TemplateDebugReason
{
    Breakpoint,
    Step,
    DataBreakpoint,
    Error,
    RunToCursor,
    Entry,
}

/// <summary>止まっているときの状態 (TPL-32 の仕様 3)。</summary>
public sealed record TemplateDebugState(
    TemplateSourceLocation Location,
    long Position,
    TemplateDebugReason Reason,
    IReadOnlyList<TemplateDebugFrame> CallStack,
    IReadOnlyList<TemplateDebugVariable> Locals,
    IReadOnlyList<TemplateDebugVariable> Patterns,
    IReadOnlyList<(string Expression, string Value)> Watches,
    TemplateDiagnostic? Error);

/// <summary>実行の操作 (TPL-32 の仕様 2)。</summary>
public enum TemplateDebugCommand
{
    Continue,
    StepOver,
    StepInto,
    StepOut,
    RunToCursor,
    Stop,
}

/// <summary>
/// テンプレートデバッガ (TPL-32)。<see cref="TemplateApplyOptions.Debugger"/> に渡して適用すると、評価器は文ごとにこのデバッガに
/// 問い合わせ、ブレークポイント・ステップ実行で止まる。止まっている間は評価器のロックを放すので、結果ツリーや変数を読める。
/// 止まっている時間は <c>limit.time</c> に数えない (仕様 5)。操作 (<see cref="Resume"/>) はどのスレッドから呼んでもよい。
/// </summary>
public sealed class TemplateDebugger
{
    private readonly object _lock = new();
    private readonly List<TemplateBreakpoint> _breakpoints = [];
    private readonly List<long> _dataBreakpoints = [];
    private readonly List<string> _watches = [];
    private readonly Dictionary<TemplateBreakpoint, int> _hits = [];

    /// <summary>デバッグ中は遅延評価をしない (書いた順に評価する。既定でオン。仕様 4)。</summary>
    public bool Eager { get; set; } = true;

    /// <summary>評価中のエラーで止まる (「例外で止まる」。既定でオン)。</summary>
    public bool BreakOnError { get; set; } = true;

    /// <summary>最初の文で止まる (F10 / F11 でデバッグを始めたとき)。</summary>
    public bool StopOnEntry { get; set; }

    public IReadOnlyList<TemplateBreakpoint> Breakpoints
    {
        get
        {
            lock (_lock)
            {
                return [.. _breakpoints];
            }
        }
    }

    /// <summary>データのブレークポイント: このオフセットを含むパターンを作るときに止まる (「ここを読むときに止める」)。</summary>
    public IReadOnlyList<long> DataBreakpoints
    {
        get
        {
            lock (_lock)
            {
                return [.. _dataBreakpoints];
            }
        }
    }

    /// <summary>ウォッチ式 (止まるたびに評価する)。</summary>
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

    /// <summary>止まっている。</summary>
    public bool IsPaused { get; private set; }

    /// <summary>最後に止まったときの状態。</summary>
    public TemplateDebugState? State { get; private set; }

    /// <summary>止まった (評価器のスレッドから呼ぶ。UI に移すのは受け取る側)。</summary>
    public event EventHandler<TemplateDebugState>? Paused;

    /// <summary>再開した。</summary>
    public event EventHandler? Resumed;

    /// <summary>止まった回数 (テスト用)。</summary>
    public int PauseCount { get; private set; }

    public void SetBreakpoint(TemplateBreakpoint breakpoint)
    {
        lock (_lock)
        {
            _breakpoints.RemoveAll(b => SameLine(b, breakpoint.File, breakpoint.Line));
            _breakpoints.Add(breakpoint);
        }
    }

    /// <summary>ブレークポイントの設定・解除 (F9)。設定したら true。</summary>
    public bool ToggleBreakpoint(string file, int line)
    {
        lock (_lock)
        {
            if (_breakpoints.RemoveAll(b => SameLine(b, file, line)) > 0)
            {
                return false;
            }

            _breakpoints.Add(new TemplateBreakpoint(file, line));
            return true;
        }
    }

    public void ClearBreakpoints()
    {
        lock (_lock)
        {
            _breakpoints.Clear();
        }
    }

    public void AddDataBreakpoint(long offset)
    {
        lock (_lock)
        {
            if (!_dataBreakpoints.Contains(offset))
            {
                _dataBreakpoints.Add(offset);
            }
        }
    }

    public void RemoveDataBreakpoint(long offset)
    {
        lock (_lock)
        {
            _dataBreakpoints.Remove(offset);
        }
    }

    public void AddWatch(string expression)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(expression))
            {
                _watches.Add(expression.Trim());
            }
        }
    }

    public void RemoveWatch(string expression)
    {
        lock (_lock)
        {
            _watches.Remove(expression);
        }
    }

    // ───────────── 実行の操作 ─────────────

    private TemplateDebugCommand _command = TemplateDebugCommand.Continue;
    private int _commandDepth;
    private (string File, int Line)? _runTo;
    private bool _resumeRequested;
    private bool _stopRequested;

    /// <summary>
    /// 止まっている評価を再開する。<paramref name="file"/> と <paramref name="line"/> は「ここまで実行」の行。止まっていなければ、
    /// 次に止まる条件だけを変える。
    /// </summary>
    public void Resume(TemplateDebugCommand command, string? file = null, int line = 0)
    {
        lock (_lock)
        {
            _command = command;
            _commandDepth = State?.CallStack.Count - 1 ?? 0;
            _runTo = command == TemplateDebugCommand.RunToCursor && file is not null ? (file, line) : null;
            _stopRequested = command == TemplateDebugCommand.Stop;
            _resumeRequested = true;
        }

        WakeUp?.Invoke();
    }

    /// <summary>止まっている評価を起こす (評価器が設定する)。</summary>
    internal Action? WakeUp { get; set; }

    internal bool StopRequested
    {
        get
        {
            lock (_lock)
            {
                return _stopRequested;
            }
        }
    }

    /// <summary>止まり始めた (評価器のスレッド)。</summary>
    internal void EnterPause(TemplateDebugState state)
    {
        lock (_lock)
        {
            _resumeRequested = false;
            State = state;
            IsPaused = true;
            PauseCount++;
        }

        Paused?.Invoke(this, state);
    }

    /// <summary>再開の要求が来たか。</summary>
    internal bool ResumeRequested
    {
        get
        {
            lock (_lock)
            {
                return _resumeRequested;
            }
        }
    }

    internal void LeavePause()
    {
        lock (_lock)
        {
            IsPaused = false;
        }

        Resumed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>文の前で止まるか (評価器のスレッド)。<paramref name="depth"/> は呼び出しの深さ (トップレベルが 0)。</summary>
    internal TemplateDebugReason? ShouldPause(TemplateSourceLocation at, int depth, Func<string, bool> condition, Action<string> log, bool first)
    {
        TemplateDebugCommand command;
        int commandDepth;
        (string File, int Line)? runTo;
        List<TemplateBreakpoint> hits;
        lock (_lock)
        {
            if (_stopRequested)
            {
                return null;
            }

            command = _command;
            commandDepth = _commandDepth;
            runTo = _runTo;
            hits = [.. _breakpoints.Where(b => b.Enabled && SameLine(b, at.File, at.Line))];
        }

        if (first && StopOnEntry)
        {
            return TemplateDebugReason.Entry;
        }

        switch (command)
        {
            case TemplateDebugCommand.StepInto:
                return TemplateDebugReason.Step;
            case TemplateDebugCommand.StepOver when depth <= commandDepth:
                return TemplateDebugReason.Step;
            case TemplateDebugCommand.StepOut when depth < commandDepth:
                return TemplateDebugReason.Step;
            case TemplateDebugCommand.RunToCursor when runTo is { } r && SameLine(new TemplateBreakpoint(r.File, r.Line), at.File, at.Line):
                return TemplateDebugReason.RunToCursor;
        }

        foreach (TemplateBreakpoint b in hits)
        {
            if (b.Condition is { Length: > 0 } c && !condition(c))
            {
                continue;
            }

            int count;
            lock (_lock)
            {
                count = _hits[b] = _hits.GetValueOrDefault(b) + 1;
            }

            if (b.HitCount is { } n && count != n)
            {
                continue;
            }

            if (b.LogMessage is { Length: > 0 } message)
            {
                log(message);
                continue;
            }

            return TemplateDebugReason.Breakpoint;
        }

        return null;
    }

    /// <summary>止まった後、次の操作のための状態を初期化する (「続行」と同じ)。</summary>
    public void ResetHits()
    {
        lock (_lock)
        {
            _hits.Clear();
            _command = TemplateDebugCommand.Continue;
            _runTo = null;
            _stopRequested = false;
            _resumeRequested = false;
        }
    }

    private static bool SameLine(TemplateBreakpoint b, string file, int line) =>
        b.Line == line && (string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(b.File), Path.GetFileName(file), StringComparison.OrdinalIgnoreCase));
}
