using System.Globalization;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

/// <summary>テンプレートデバッガ (TPL-32) の評価器の側。文の前と、パターンを作った後に、デバッガに問い合わせて止まる。</summary>
internal sealed partial class Evaluator
{
    private readonly List<TemplateSourceLocation?> _debugSites = [];
    private bool _debugFirst = true;
    private Frame? _debugFrame;
    private bool _debugPausing;

    private TemplateDebugger? Debugger => _options.Debugger;

    /// <summary>デバッガがあり、適用の評価のスレッドで実行している (結果ツリーの遅延評価の要求では止まらない)。</summary>
    private bool Debugging => _options.Debugger is not null && IsMainThread && MainHoldsLock && !_debugPausing;

    /// <summary>文の前 (ExecStatement から呼ぶ)。</summary>
    private void DebugBefore(Stmt s, Frame f)
    {
        if (s is BlockStmt or EmptyStmt or DeclStmt or ImportStmt or NamespaceStmt or UsingNamespaceStmt)
        {
            return;
        }

        TemplateDebugger dbg = Debugger!;
        int depth = _callStack.Count;
        while (_debugSites.Count <= depth)
        {
            _debugSites.Add(null);
        }

        _debugSites[depth] = s.Location;
        if (_debugSites.Count > depth + 1)
        {
            _debugSites.RemoveRange(depth + 1, _debugSites.Count - depth - 1);
        }

        _debugFrame = f;
        bool first = _debugFirst;
        _debugFirst = false;
        TemplateDebugReason? reason = dbg.ShouldPause(s.Location, depth, c => DebugCondition(c, f), m => Print(DebugText(m, f)), first);
        if (reason is { } r)
        {
            DebugPause(r, s.Location, f, null);
        }

        if (dbg.StopRequested)
        {
            throw Limit(TemplateMessages.Cancelled);
        }
    }

    /// <summary>
    /// パターンを作った後 (ExecStatement から呼ぶ): デバッグ中は遅延評価をしない (仕様 4) ので中身を書いた順に評価し、データの
    /// ブレークポイントのオフセットを含むパターンを作ったら止まる。
    /// </summary>
    private void DebugAfterPatterns(PatternDeclStmt s, Frame f, int before)
    {
        TemplateDebugger dbg = Debugger!;
        List<TemplateNode> created;
        lock (_result.SyncRoot)
        {
            created = f.Children is null ? [] : [.. f.Children.Skip(before)];
        }

        IReadOnlyList<long> data = dbg.DataBreakpoints;
        if (data.Count > 0 && created.Any(n => n.Section is null && data.Any(x => n.Size > 0 ? x >= n.Offset && x < n.End : x == n.Offset)))
        {
            DebugPause(TemplateDebugReason.DataBreakpoint, s.Location, f, null);
        }

        if (dbg.Eager)
        {
            foreach (TemplateNode node in created)
            {
                EvaluateEagerly(node, 0);
            }
        }
    }

    /// <summary>部分木を書いた順に評価する (デバッグ中。ポインタの指す先は開くまで評価しない)。</summary>
    private void EvaluateEagerly(TemplateNode node, int level)
    {
        if (level > 64 || Debugger!.StopRequested)
        {
            return;
        }

        try
        {
            switch (node.Children0)
            {
                case LazyChildren lc:
                    Materialize(node);
                    foreach (TemplateNode c in lc.Items?.ToList() ?? [])
                    {
                        EvaluateEagerly(c, level + 1);
                    }

                    break;
                case OnDemandChildren od:
                    for (long i = 0; i < od.Count; i++)
                    {
                        EvaluateEagerly(od.Get(i), level + 1);
                    }

                    break;
                case SequenceChildren seq:
                    foreach (TemplateNode c in seq.Items.ToList())
                    {
                        EvaluateEagerly(c, level + 1);
                    }

                    break;
            }
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            // 部分木のエラーはノードに記録済み
            _ = e;
        }
    }

    /// <summary>評価中のエラーで止まる (「例外で止まる」)。</summary>
    private void DebugOnError(TemplateDiagnostic d)
    {
        if (Debugger is { BreakOnError: true } && d.Severity == TemplateSeverity.Error && d.Code != TemplateMessages.Cancelled && Debugging)
        {
            DebugPause(TemplateDebugReason.Error, d.Location ?? _loc ?? new TemplateSourceLocation(_program.Path, 1, 1), _debugFrame ?? _root, d);
        }
    }

    /// <summary>
    /// 止まる: 状態を写し、評価器のロックを放して (結果ツリー・変数を読めるように) 再開の要求を待つ。止まっていた時間は時間の上限に数えない。
    /// </summary>
    private void DebugPause(TemplateDebugReason reason, TemplateSourceLocation at, Frame f, TemplateDiagnostic? error)
    {
        TemplateDebugger dbg = Debugger!;
        _debugPausing = true;
        try
        {
            TemplateDebugState state = DebugState(reason, at, f, error);
            long started = _clock.ElapsedTicks;
            object sync = _result.SyncRoot;
            dbg.WakeUp = () =>
            {
                lock (sync)
                {
                    Monitor.PulseAll(sync);
                }
            };
            Interlocked.Exchange(ref ProgressOffset, _pos);
            MainHoldsLock = false;
            Monitor.PulseAll(sync);
            dbg.EnterPause(state);
            while (!dbg.ResumeRequested && !_token.IsCancellationRequested)
            {
                Monitor.Wait(sync, 200);
            }

            MainHoldsLock = true;
            _opStartTicks += _clock.ElapsedTicks - started;
            dbg.LeavePause();
        }
        finally
        {
            _debugPausing = false;
        }

        if (dbg.StopRequested)
        {
            throw Limit(TemplateMessages.Cancelled);
        }
    }

    private TemplateDebugState DebugState(TemplateDebugReason reason, TemplateSourceLocation at, Frame f, TemplateDiagnostic? error)
    {
        var stack = new List<TemplateDebugFrame>();
        for (int i = _callStack.Count; i >= 0; i--)
        {
            string name = i == 0 ? "(top level)" : _callStack[i - 1];
            TemplateSourceLocation? site = i < _debugSites.Count ? _debugSites[i] : null;
            if (i == _callStack.Count)
            {
                site = at;
            }

            stack.Add(new TemplateDebugFrame(name, site, i));
        }

        var locals = new List<TemplateDebugVariable>();
        foreach (string name in f.Locals.Names().Distinct(StringComparer.Ordinal))
        {
            if (f.Locals.Find(name) is { } v)
            {
                locals.Add(new TemplateDebugVariable(name, SafeDisplay(v.Value), null, false));
            }
        }

        var patterns = new List<TemplateDebugVariable>();
        List<TemplateNode> children;
        lock (_result.SyncRoot)
        {
            children = f.Children is null ? [] : [.. f.Children];
        }

        foreach (TemplateNode n in children.TakeLast(1000))
        {
            string value;
            try
            {
                value = n.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union ? string.Empty : Display(n, TemplateDisplayContext.Invariant);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                value = "<" + ex.Message + ">";
            }

            patterns.Add(new TemplateDebugVariable(n.DisplayName, value, n.Offset, true));
        }

        var watches = new List<(string, string)>();
        foreach (string w in Debugger!.Watches)
        {
            watches.Add((w, DebugText(w, f)));
        }

        return new TemplateDebugState(at, _pos, reason, stack, locals, patterns, watches, error);
    }

    private string SafeDisplay(Val v)
    {
        try
        {
            return ToDisplayString(Deref(v));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "<" + ex.Message + ">";
        }
    }

    /// <summary>止まっている文脈で式を評価して表示の文字列にする (ウォッチ式・ログポイント)。</summary>
    private string DebugText(string expression, Frame f)
    {
        if (ParseDebugExpression(expression) is not { } e)
        {
            return "<syntax error>";
        }

        TemplateSourceLocation? saveLoc = _loc;
        long savePos = _pos;
        bool savePausing = _debugPausing;
        _debugPausing = true;
        try
        {
            return ToDisplayString(Deref(Eval(e, f)));
        }
        catch (TemplateError ex) when (!ex.IsLimit)
        {
            return "<error: " + ex.Diagnostic.Message + ">";
        }
        finally
        {
            _loc = saveLoc;
            _pos = savePos;
            _debugPausing = savePausing;
        }
    }

    private bool DebugCondition(string expression, Frame f)
    {
        if (ParseDebugExpression(expression) is not { } e)
        {
            return false;
        }

        TemplateSourceLocation? saveLoc = _loc;
        long savePos = _pos;
        bool savePausing = _debugPausing;
        _debugPausing = true;
        try
        {
            return Truthy(Eval(e, f));
        }
        catch (TemplateError ex) when (!ex.IsLimit)
        {
            return false;
        }
        finally
        {
            _loc = saveLoc;
            _pos = savePos;
            _debugPausing = savePausing;
        }
    }

    private readonly Dictionary<string, Expr?> _debugExpressions = new(StringComparer.Ordinal);

    private Expr? ParseDebugExpression(string text)
    {
        if (!_debugExpressions.TryGetValue(text, out Expr? e))
        {
            var bag = new DiagnosticBag();
            e = Parser.ParseExpression(text, "<watch>", bag);
            if (bag.HasErrors)
            {
                e = null;
            }

            _debugExpressions[text] = e;
        }

        return e;
    }

    /// <summary>止まっている間に、止まった文脈で式を評価する (ウォッチ式の追加。UI から <see cref="TemplateResult.Invoke{T}(Func{T})"/> で呼ぶ)。</summary>
    internal string EvaluateInPausedFrame(string expression) =>
        DebugText(expression, _debugFrame ?? _root);

    internal static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
}
