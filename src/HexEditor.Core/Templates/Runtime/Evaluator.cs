using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

/// <summary>
/// HXT の評価器 (TPL-20、TPL-27、TPL-28、TPL-37)。独自の解釈実行器で、ホストの .NET のオブジェクトを公開しない。
/// 評価器の状態は <see cref="TemplateResult.SyncRoot"/> の下で 1 つのスレッドだけが触る。結果ツリーの遅延評価の要求は、
/// 適用の評価を安全な位置 (文の区切り) で止めて実行する。
/// </summary>
internal sealed partial class Evaluator
{
    /// <summary>評価済みのパターン 1 つあたりのメモリの見積もり (TPL-28 の仕様 2 の「小さなレコード」と、その付属情報)。</summary>
    internal const int NodeCost = 160;

    private const int PrintLimit = 10_000;

    private readonly TemplateResult _result;
    private readonly TemplateProgram _program;
    private readonly TemplateApplyOptions _options;
    private readonly Space _docSpace;
    private readonly Stopwatch _clock = new();

    public long PatternCounter;
    public long StepCounter;
    public long MemoryCounter;
    public long ProgressOffset;

    public int Waiters;
    public bool MainHoldsLock;

    public List<TemplateSection> Sections { get; } = [];

    public Dictionary<string, long> TypeEvaluations { get; } = new(StringComparer.Ordinal);

    public TemplateLimits Limits { get; private set; } = TemplateLimits.Default;

    // ───────────── 現在の評価の状態 (遅延評価の要求のたびに保存・復元する) ─────────────

    private long _pos;
    private Space _space;
    private Frame _root = null!;
    private int _depth;
    private CancellationToken _token;
    private long _steps;
    private long _stepLimit;
    private long _opStartTicks;
    private double _timeLimitSeconds;
    private bool _inLazyOp;
    private Thread? _mainThread;
    private TemplateSourceLocation? _loc;
    private TemplateNode? _thisNode;
    private long _touched = -1;
    private Val _returnValue;
    private readonly List<string> _callStack = [];

    private readonly Dictionary<(Space, long, string), TemplateNode> _seen = [];
    private readonly LinkedList<TemplateNode> _lru = new();
    private readonly Dictionary<ConstDecl, Val> _constCache = [];
    private readonly HashSet<ConstDecl> _constEvaluating = [];
    private readonly Dictionary<EnumDecl, EnumInfo> _enumCache = [];
    private readonly HashSet<AttributeNode> _warnedAttributes = [];
    private readonly Dictionary<string, Space> _sectionSpaces = [];
    private long _localBytes;
    private long _sectionTotal;
    private int _printLines;

    public Evaluator(TemplateResult result)
    {
        _result = result;
        _program = result.Program;
        _options = result.Options;
        _docSpace = new Space(result.Data);
        _space = _docSpace;
    }

    internal TemplateResult Result => _result;

    private bool IsMainThread => ReferenceEquals(Thread.CurrentThread, _mainThread);

    internal TemplateProgram Program => _program;

    // ───────────── 評価の開始 ─────────────

    public void Start()
    {
        if (_program.HasErrors)
        {
            foreach (TemplateDiagnostic d in _program.Diagnostics)
            {
                _result.AddOutput(new TemplateOutputEntry(DateTime.UtcNow, d.Severity, d.Message, d));
            }

            _result.State = TemplateRunState.SyntaxError;
            _result.Complete();
            return;
        }

        foreach (TemplateDiagnostic d in _program.Diagnostics)
        {
            _result.AddOutput(new TemplateOutputEntry(DateTime.UtcNow, d.Severity, d.Message, d));
        }

        TemplateThreads.StartDedicated(RunMain, "HexEditor template evaluation");
    }

    private void RunMain()
    {
        _mainThread = Thread.CurrentThread;
        lock (_result.SyncRoot)
        {
            MainHoldsLock = true;
            try
            {
                Limits = ResolveLimits();
                _token = _result.CancellationToken;
                _steps = 0;
                _stepLimit = Limits.Steps;
                _timeLimitSeconds = Limits.TimeSeconds;
                _clock.Start();
                _opStartTicks = _clock.ElapsedTicks;
                SetupRoot();
                Guard(() =>
                {
                    RunTopLevel();
                    return 0;
                }, isMain: true);
            }
            finally
            {
                _result.Elapsed = _clock.Elapsed;
                MainHoldsLock = false;
                Monitor.PulseAll(_result.SyncRoot);
                _result.Complete();
            }
        }
    }

    private void SetupRoot()
    {
        TemplateNode root = _result.Root;
        var items = new ListChildren();
        root.Children0 = items;
        TemplateMetadata meta = _program.Metadata;
        _root = new Frame
        {
            Node = root,
            Locals = new Scope(null),
            Children = items.Items,
            Space = _docSpace,
            Start = _options.StartOffset,
            BigEndian = meta.BigEndian ?? _options.BigEndian ?? false,
            MsbFirst = meta.MsbFirst ?? false,
            Encoding = meta.Encoding ?? _options.DefaultEncoding,
            MaxEnd = _options.StartOffset,
        };
        root.BigEndian = _root.BigEndian;
        _pos = _options.StartOffset;
        _space = _docSpace;
    }

    private void RunTopLevel()
    {
        if (_options.TypeName is { } typeName)
        {
            // 追従: 1 つの型をカーソル位置で評価する (TPL-26 の仕様 2・3)
            string[] parts = typeName.Split("::");
            var decl = new PatternDeclStmt
            {
                Type = new TypeRef { Parts = parts, Location = new TemplateSourceLocation(_program.Path, 1, 1) },
                Declarators = [new Declarator { Name = parts[^1], Location = new TemplateSourceLocation(_program.Path, 1, 1) }],
                Location = new TemplateSourceLocation(_program.Path, 1, 1),
            };
            ExecStatement(decl, _root);
        }
        else
        {
            foreach (Stmt s in _program.TopLevel)
            {
                Flow flow = ExecStatement(s, _root);
                if (flow != Flow.Normal)
                {
                    break;
                }
            }
        }

        CloseBitGroup(_root);
        _result.Root.Size = Math.Max(0, _root.MaxEnd - _options.StartOffset);
        _result.State = TemplateRunState.Completed;
    }

    /// <summary>プラグマの上限を、設定の最大値で切り詰める (TPL-17 の仕様 4)。</summary>
    private TemplateLimits ResolveLimits()
    {
        TemplateLimits d = TemplateLimits.Default;
        TemplateLimits max = _options.MaximumLimits;
        var req = _program.Metadata.RequestedLimits;
        double Pick(string key, double def, double cap)
        {
            if (!req.TryGetValue(key, out var r))
            {
                return def;
            }

            if (cap > 0 && r.Value > cap)
            {
                Report(new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.LimitClamped,
                    ["limit." + key, r.Value.ToString(CultureInfo.InvariantCulture), cap.ToString(CultureInfo.InvariantCulture)], r.At), null);
                return cap;
            }

            return r.Value;
        }

        var limits = new TemplateLimits
        {
            Steps = (long)Pick("steps", d.Steps, max.Steps),
            Recursion = (int)Pick("recursion", d.Recursion, max.Recursion),
            Array = (long)Pick("array", d.Array, max.Array),
            Patterns = (long)Pick("patterns", d.Patterns, max.Patterns),
            Memory = (long)Pick("memory", d.Memory, max.Memory),
            Section = (long)Pick("section", d.Section, max.Section),
            SectionTotal = (long)Pick("section_total", d.SectionTotal, max.SectionTotal),
            TimeSeconds = Pick("time", d.TimeSeconds, max.TimeSeconds),
        };
        if (_options.LimitOverride is { } o)
        {
            limits = o;
        }

        if (_options.IsAutoApply)
        {
            limits = limits with { TimeSeconds = 5 };
        }

        return limits;
    }

    // ───────────── 遅延評価の要求の実行 ─────────────

    /// <summary>
    /// 評価器の状態を保存して <paramref name="body"/> を実行する。部分木のエラーは記録済みなので捨てる (TPL-27 の仕様 2)。
    /// 内部エラー (評価器の不具合) はアプリを落とさず、その適用をエラーにする (TPL-37 の仕様 5)。
    /// </summary>
    public T Guard<T>(Func<T> body, bool isMain = false)
    {
        long pos = _pos;
        Space space = _space;
        int depth = _depth;
        CancellationToken token = _token;
        long steps = _steps;
        long stepLimit = _stepLimit;
        long start = _opStartTicks;
        bool lazy = _inLazyOp;
        TemplateSourceLocation? loc = _loc;
        TemplateNode? thisNode = _thisNode;
        long touched = _touched;
        int stack = _callStack.Count;
        if (!isMain)
        {
            _inLazyOp = true;
            _steps = 0;
            _stepLimit = Limits.Steps;
            _opStartTicks = _clock.ElapsedTicks;
            _token = CancellationToken.None;

            if (!_clock.IsRunning)
            {
                _clock.Start();
            }
        }

        try
        {
            return body();
        }
        catch (TemplateError e) when (!isMain)
        {
            // 部分木のエラー: ノードに記録済み
            _ = e;
            return default!;
        }
        catch (TemplateError e)
        {
            if (e.IsLimit)
            {
                _result.State = e.Diagnostic.Code == TemplateMessages.Cancelled ? TemplateRunState.Cancelled : TemplateRunState.LimitReached;
                _result.LimitReached = e.Diagnostic.Code switch
                {
                    TemplateMessages.StepLimit => TemplateLimitKind.Steps,
                    TemplateMessages.PatternLimit => TemplateLimitKind.Patterns,
                    TemplateMessages.MemoryLimit => TemplateLimitKind.Memory,
                    TemplateMessages.TimeLimit => TemplateLimitKind.Time,
                    _ => TemplateLimitKind.None,
                };
            }
            else
            {
                _result.State = TemplateRunState.Failed;
            }

            if (!Reported.Contains(e))
            {
                Report(e.Diagnostic, null);
            }

            FinishPartialRoot();
            return default!;
        }
        catch (OperationCanceledException) when (isMain)
        {
            _result.State = TemplateRunState.Cancelled;
            Report(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.Cancelled, []), null);
            FinishPartialRoot();
            return default!;
        }
        catch (OperationCanceledException)
        {
            return default!;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException || isMain)
        {
            var d = new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.Internal, [ex.GetType().Name + ": " + ex.Message], _loc)
            {
                InternalDetail = ex.ToString(),
            };
            Report(d, null);
            if (isMain)
            {
                _result.State = TemplateRunState.Failed;
                FinishPartialRoot();
            }

            return default!;
        }
        finally
        {
            _pos = pos;
            _space = space;
            _depth = depth;
            _token = token;
            _steps = steps;
            _stepLimit = stepLimit;
            _opStartTicks = start;
            _inLazyOp = lazy;
            _loc = loc;
            _thisNode = thisNode;
            _touched = touched;
            if (_callStack.Count > stack)
            {
                _callStack.RemoveRange(stack, _callStack.Count - stack);
            }
        }
    }

    private void FinishPartialRoot()
    {
        CloseBitGroupSafe(_root);
        _result.Root.Size = Math.Max(0, _root.MaxEnd - _options.StartOffset);
    }

    private void CloseBitGroupSafe(Frame f)
    {
        if (f.OpenGroup is { } g)
        {
            f.MaxEnd = Math.Max(f.MaxEnd, g.Start + g.Base.Size);
            f.OpenGroup = null;
        }
    }

    // ───────────── 上限・キャンセル ─────────────

    private readonly HashSet<TemplateError> Reported = new(ReferenceEqualityComparer.Instance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Step()
    {
        _steps++;
        StepCounter++;
        if (_steps > _stepLimit)
        {
            throw Limit(TemplateMessages.StepLimit, _stepLimit.ToString(CultureInfo.InvariantCulture));
        }

        if ((_steps & 1023) == 0)
        {
            Checkpoint();
        }
    }

    /// <summary>1,000 ステップごとの確認 (キャンセル・時間・遅延評価の要求。TPL-28 の仕様 4)。</summary>
    private void Checkpoint()
    {
        CheckCancel();
        if (_timeLimitSeconds > 0 && (_clock.ElapsedTicks - _opStartTicks) > _timeLimitSeconds * Stopwatch.Frequency)
        {
            throw Limit(TemplateMessages.TimeLimit, _timeLimitSeconds.ToString(CultureInfo.InvariantCulture));
        }

        Interlocked.Exchange(ref ProgressOffset, _pos);
        if (IsMainThread && MainHoldsLock && Waiters > 0)
        {
            MainHoldsLock = false;
            Monitor.PulseAll(_result.SyncRoot);
            while (Waiters > 0)
            {
                Monitor.Wait(_result.SyncRoot);
            }

            MainHoldsLock = true;
        }
    }

    public void CheckCancel()
    {
        if (_token.IsCancellationRequested)
        {
            throw Limit(TemplateMessages.Cancelled);
        }
    }

    /// <summary>重い組み込み関数 (find など) が消費したステップ (TPL-18 の「巨大ファイル・長時間処理」)。</summary>
    private void ConsumeSteps(long n)
    {
        _steps += n;
        StepCounter += n;
        if (_steps > _stepLimit)
        {
            throw Limit(TemplateMessages.StepLimit, _stepLimit.ToString(CultureInfo.InvariantCulture));
        }

        Checkpoint();
    }

    private void EnterDepth(string what)
    {
        _depth++;
        if (_depth > Limits.Recursion || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _depth--;
            throw Error(TemplateMessages.RecursionTooDeep, Limits.Recursion.ToString(CultureInfo.InvariantCulture));
        }

        _callStack.Add(what);
    }

    private void LeaveDepth()
    {
        _depth--;
        if (_callStack.Count > 0)
        {
            _callStack.RemoveAt(_callStack.Count - 1);
        }
    }

    /// <summary>パターンを 1 つ実体化した (パターン数・メモリの上限。TPL-17 の仕様 4、TPL-28 の仕様 2)。</summary>
    private void CountPattern()
    {
        PatternCounter++;
        MemoryCounter += NodeCost;
        if (PatternCounter > Limits.Patterns)
        {
            throw Limit(TemplateMessages.PatternLimit, Limits.Patterns.ToString(CultureInfo.InvariantCulture));
        }

        if (MemoryCounter + _localBytes > Limits.Memory * 8 / 10)
        {
            DropSubtrees();
            if (MemoryCounter + _localBytes > Limits.Memory)
            {
                throw Limit(TemplateMessages.MemoryLimit, FormatBytes(Limits.Memory));
            }
        }
    }

    private void CheckStringMemory(long chars)
    {
        if (chars * 2 + MemoryCounter + _localBytes > Limits.Memory)
        {
            throw Limit(TemplateMessages.MemoryLimit, FormatBytes(Limits.Memory));
        }
    }

    internal static string FormatBytes(long bytes) =>
        bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MB" : bytes.ToString(CultureInfo.InvariantCulture);

    /// <summary>表示していない (再評価できる) 部分木を古い順に捨てる (TPL-28 の仕様 2)。</summary>
    private void DropSubtrees()
    {
        long target = Limits.Memory * 6 / 10;
        LinkedListNode<TemplateNode>? entry = _lru.First;
        while (entry is not null && MemoryCounter + _localBytes > target)
        {
            LinkedListNode<TemplateNode>? next = entry.Next;
            if (entry.Value.Children0 is LazyChildren { Pinned: false, Items: not null } lc)
            {
                Release(entry.Value, lc);
            }

            entry = next;
        }
    }

    private void Release(TemplateNode node, LazyChildren lc)
    {
        if (lc.Items is null)
        {
            return;
        }

        foreach (TemplateNode child in lc.Items)
        {
            UnlinkSubtree(child);
        }

        PatternCounter -= lc.Items.Count;
        MemoryCounter -= lc.Items.Count * (long)NodeCost;
        lc.Items = null;
        if (lc.LruEntry is not null)
        {
            _lru.Remove(lc.LruEntry);
            lc.LruEntry = null;
        }
    }

    /// <summary>オンデマンド配列のキャッシュから捨てた要素の分を、パターン数とメモリの見積もりから引く。</summary>
    private void ReleaseElements(IEnumerable<TemplateNode> nodes)
    {
        foreach (TemplateNode n in nodes)
        {
            UnlinkSubtree(n);
            PatternCounter--;
            MemoryCounter -= NodeCost;
        }
    }

    private void UnlinkSubtree(TemplateNode node)
    {
        if (node.Children0 is LazyChildren { Items: not null } lc)
        {
            Release(node, lc);
        }
    }

    // ───────────── 診断 ─────────────

    public void Report(TemplateDiagnostic d, TemplateNode? node)
    {
        node?.AddDiagnostic(d);
        _result.AddOutput(new TemplateOutputEntry(DateTime.UtcNow, d.Severity, d.Message, d));
    }

    public void Warn(TemplateNode? node, string code, params string[] args)
    {
        var d = new TemplateDiagnostic(TemplateSeverity.Warning, code, args, _loc, node?.Offset ?? _pos, node?.Path, [.. _callStack])
        {
            SectionName = node?.Section?.Name ?? _space.Name,
        };
        Report(d, node);
    }

    public TemplateError Error(string code, params string[] args) => ErrorAt(_loc, code, args);

    private TemplateError ErrorAt(TemplateSourceLocation? at, string code, params string[] args)
    {
        long data = _touched >= 0 ? _touched : _pos;
        var d = new TemplateDiagnostic(TemplateSeverity.Error, code, args, at, data, null, [.. _callStack])
        {
            SectionName = _space.Name,
        };
        return new TemplateError(d);
    }

    public TemplateError Limit(string code, params string[] args)
    {
        var d = new TemplateDiagnostic(TemplateSeverity.Error, code, args, _loc, _pos, null, [.. _callStack]);
        return new TemplateError(d, isLimit: true);
    }

    public TemplateError OutOfRange(string what, long offset)
    {
        TemplateError e = ErrorAt(_loc, TemplateMessages.ReadOutOfRange, what, offset.ToString("X", CultureInfo.InvariantCulture));
        e.Diagnostic = e.Diagnostic with { DataOffset = offset };
        return new TemplateError(e.Diagnostic) { IsOutOfRange = true };
    }

    public TemplateError Unreadable(long offset)
    {
        TemplateError e = ErrorAt(_loc, TemplateMessages.DataUnreadable, offset.ToString("X", CultureInfo.InvariantCulture));
        return new TemplateError(e.Diagnostic with { DataOffset = offset });
    }

    /// <summary>エラーをノードに記録し、出力パネルに出す (同じエラーは 1 回だけ)。</summary>
    private void RecordError(TemplateError e, TemplateNode? node)
    {
        if (Reported.Add(e))
        {
            TemplateDiagnostic d = e.Diagnostic;
            if (node is not null && d.Path is null)
            {
                d = d with { Path = node.Path };
                e.Diagnostic = d;
            }

            Report(d, node);
        }
    }

    private void Print(string text)
    {
        if (_printLines >= PrintLimit)
        {
            if (_printLines == PrintLimit)
            {
                _printLines++;
                var d = new TemplateDiagnostic(TemplateSeverity.Info, TemplateMessages.OutputTruncated, []);
                _result.AddOutput(new TemplateOutputEntry(DateTime.UtcNow, TemplateSeverity.Info, d.Message, null));
            }

            return;
        }

        _printLines++;
        _result.AddOutput(new TemplateOutputEntry(DateTime.UtcNow, TemplateSeverity.Info, text, null));
    }
}
