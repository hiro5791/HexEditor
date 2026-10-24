using HexEditor.Core.Templates;

namespace HexEditor.Core.Compare.Structure;

/// <summary>
/// 構造の比較 (ANA-31) の結果。比較は別のスレッドで行い、見つかった差分を順に木 (<see cref="Root"/>) に加える。画面は
/// <see cref="SyncRoot"/> を取って読む (<see cref="Snapshot"/>)。比較の後に、構造の外の差分 (仕様 8) を
/// <see cref="ComputeOutside"/> で求める。
/// </summary>
public sealed class StructCompareResult : IDisposable
{
    private readonly long[] _counts = new long[Enum.GetValues<StructDiffKind>().Length];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private long _leftFields;
    private long _rightFields;
    private long _leftPosition;
    private long _version;

    internal StructCompareResult(StructTemplateChoice choice, StructCompareSideInfo left, StructCompareSideInfo right, int maxFields)
    {
        Choice = choice;
        Left = left;
        Right = right;
        MaxFields = maxFields;
        Root = new StructDiffNode(null, null, null, -1, -1) { IsContainer = true, HasLeft = true, HasRight = true };
    }

    /// <summary>テンプレートが決まらない・使えないときの結果 (比較はしない)。</summary>
    public static StructCompareResult Empty(StructCompareState state, StructTemplateChoice choice, StructCompareSideInfo left, StructCompareSideInfo right,
        string? message = null)
    {
        var r = new StructCompareResult(choice, left, right, 0) { State = state, Message = message };
        r.Complete();
        return r;
    }

    public object SyncRoot { get; } = new();

    public StructDiffNode Root { get; }

    public StructTemplateChoice Choice { get; }

    /// <summary>テンプレートの表示名 (例: <c>PNG image</c>)。</summary>
    public string TemplateName { get; internal set; } = string.Empty;

    public StructCompareSideInfo Left { get; }

    public StructCompareSideInfo Right { get; }

    /// <summary>比べるフィールドの数の上限 (左右それぞれ)。</summary>
    public int MaxFields { get; }

    public StructCompareState State { get; internal set; } = StructCompareState.Running;

    /// <summary>失敗・構文エラーの理由の文 (なければ null)。</summary>
    public string? Message { get; internal set; }

    /// <summary>左右のテンプレートの評価の結果 (比較の後も、一致した行の値の表示に使う)。</summary>
    public TemplateResult? LeftResult { get; internal set; }

    public TemplateResult? RightResult { get; internal set; }

    /// <summary>比べたフィールドの数 (左)。</summary>
    public long LeftFields => Interlocked.Read(ref _leftFields);

    public long RightFields => Interlocked.Read(ref _rightFields);

    /// <summary>左の評価位置 (進捗。ANA-31 の「巨大ファイル」)。</summary>
    public long LeftPosition => Interlocked.Read(ref _leftPosition);

    /// <summary>左のデータの長さ (進捗の分母)。</summary>
    public long LeftLength { get; internal set; }

    /// <summary>変わるたびに増える (画面の更新の判定)。</summary>
    public long Version => Interlocked.Read(ref _version);

    public TimeSpan Elapsed { get; internal set; }

    /// <summary>構造の外の差分 (<see cref="ComputeOutside"/> の後)。</summary>
    public StructOutsideDiffs? Outside { get; private set; }

    /// <summary>テンプレートのエラー・警告の件数 (左右。TPL-27)。</summary>
    public int LeftErrors => LeftResult?.ErrorCount ?? 0;

    public int RightErrors => RightResult?.ErrorCount ?? 0;

    public int LeftWarnings => LeftResult?.WarningCount ?? 0;

    public int RightWarnings => RightResult?.WarningCount ?? 0;

    /// <summary>一致した行を記録しきれず、途中から記録していない (「一致したフィールドも表示」で一部が出ない)。</summary>
    public bool SameRowsTruncated { get; internal set; }

    /// <summary>比較が終わる (完了・キャンセル・上限・失敗) と完了する。</summary>
    public Task Completion => _completion.Task;

    public bool IsRunning => State == StructCompareState.Running;

    /// <summary>種類ごとの差分の件数。</summary>
    public long CountOf(StructDiffKind kind)
    {
        lock (SyncRoot)
        {
            return _counts[(int)kind];
        }
    }

    /// <summary>差分の件数 (一致・入れ物を除く)。</summary>
    public long DiffCount
    {
        get
        {
            lock (SyncRoot)
            {
                long n = 0;
                for (int k = (int)StructDiffKind.Changed; k < _counts.Length; k++)
                {
                    n += _counts[k];
                }

                return n;
            }
        }
    }

    /// <summary>比較を止める (100 ms 以内に止まる。それまでの差分は残る)。</summary>
    public void Cancel() => _cts.Cancel();

    internal CancellationToken CancellationToken => _cts.Token;

    /// <summary>差分の行を木の順 (行の表示順) にすべて返す (比較中なら、その時点まで)。</summary>
    public IReadOnlyList<StructDiffNode> Diffs()
    {
        var list = new List<StructDiffNode>();
        lock (SyncRoot)
        {
            Walk(Root, n =>
            {
                if (n.IsDiff)
                {
                    list.Add(n);
                }
            });
        }

        return list;
    }

    /// <summary><see cref="SyncRoot"/> を取って <paramref name="read"/> を実行する (比較中に木を読む)。</summary>
    public T Snapshot<T>(Func<T> read)
    {
        lock (SyncRoot)
        {
            return read();
        }
    }

    /// <summary>木を前順にたどる (呼び出し側が <see cref="SyncRoot"/> を取る)。</summary>
    public static void Walk(StructDiffNode root, Action<StructDiffNode> visit)
    {
        var stack = new Stack<StructDiffNode>();
        PushChildren(stack, root);
        while (stack.Count > 0)
        {
            StructDiffNode n = stack.Pop();
            visit(n);
            PushChildren(stack, n);
        }
    }

    private static void PushChildren(Stack<StructDiffNode> stack, StructDiffNode n)
    {
        if (n.FirstChild is null)
        {
            return;
        }

        var children = new List<StructDiffNode>();
        for (StructDiffNode? c = n.FirstChild; c is not null; c = c.NextSibling)
        {
            children.Add(c);
        }

        for (int i = children.Count - 1; i >= 0; i--)
        {
            stack.Push(children[i]);
        }
    }

    /// <summary>
    /// 行の値の表示。比較で求めた表示がなければ (一致した行)、評価の結果からノードを探して求める。見つからなければ空。
    /// UI スレッドからは呼ばない (評価器のスレッドを待つ)。
    /// </summary>
    public string DisplayOf(StructDiffNode node, bool right)
    {
        string? known = right ? node.RightDisplay : node.LeftDisplay;
        if (known is not null)
        {
            return known;
        }

        if (!(right ? node.HasRight : node.HasLeft) || (right ? RightResult : LeftResult) is not { } result)
        {
            return string.Empty;
        }

        try
        {
            TemplateNode? n = result.FindByPath(node.TreePath(right));
            return n is null || n.HasChildren && !n.IsByteOrCharArray ? string.Empty : StructComparer.Truncate(n.DisplayValue);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>行の値 (エクスポートの <c>value</c>。TPL-25 の <c>value</c> と同じ形)。求められなければ null。</summary>
    public object? ValueOf(StructDiffNode node, bool right)
    {
        if (!(right ? node.HasRight : node.HasLeft) || node.IsContainer || node.LeftIndexEnd >= 0 || node.RightIndexEnd >= 0
            || (right ? RightResult : LeftResult) is not { } result)
        {
            return null;
        }

        try
        {
            TemplateNode? n = result.FindByPath(node.TreePath(right));
            return n is null || n.HasChildren && !n.IsByteOrCharArray ? null : n.Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 構造の外の差分 (ANA-31 の仕様 8) を求める: バイトの差分のうち、どの葉のパターンの範囲とも重ならない部分の件数とバイト数。
    /// バイトの比較と構造の比較の両方が終わってから呼ぶ。
    /// </summary>
    public StructOutsideDiffs ComputeOutside(IEnumerable<DiffRange> byteDiffs)
    {
        long count = 0;
        long bytes = 0;
        foreach (DiffRange d in byteDiffs)
        {
            if (d.Kind == DiffKind.Unreadable)
            {
                continue;
            }

            long left = LeftCoverage.Uncovered(d.LeftOffset, d.LeftLength);
            long right = RightCoverage.Uncovered(d.RightOffset, d.RightLength);
            if (left > 0 || right > 0)
            {
                count++;
                bytes += Math.Max(left, right);
            }
        }

        var outside = new StructOutsideDiffs(count, bytes);
        Outside = outside;
        Touch();
        return outside;
    }

    public void Dispose()
    {
        Cancel();
        LeftResult?.Dispose();
        RightResult?.Dispose();
    }

    // ───────────── 比較から使う ─────────────

    internal StructCoverage LeftCoverage { get; } = new();

    internal StructCoverage RightCoverage { get; } = new();

    internal void SetKind(StructDiffNode node, StructDiffKind kind)
    {
        lock (SyncRoot)
        {
            _counts[(int)node.Kind]--;
            node.Kind = kind;
            _counts[(int)kind]++;
        }

        Touch();
    }

    internal void Add(StructDiffNode parent, StructDiffNode child)
    {
        lock (SyncRoot)
        {
            parent.Append(child);
            _counts[(int)child.Kind]++;
        }

        Touch();
    }

    internal void AddFields(long left, long right)
    {
        Interlocked.Add(ref _leftFields, left);
        Interlocked.Add(ref _rightFields, right);
    }

    internal void SetPosition(long position) => Interlocked.Exchange(ref _leftPosition, position);

    internal void Touch() => Interlocked.Increment(ref _version);

    internal void Complete()
    {
        Touch();
        _completion.TrySetResult();
    }
}

/// <summary>葉のパターンが占める範囲 (重ならない区間の並び。構造の外の差分の計算に使う)。</summary>
internal sealed class StructCoverage
{
    private readonly object _lock = new();
    private List<(long Start, long End)> _items = [];
    private bool _sorted = true;

    public void Add(long start, long length)
    {
        if (length <= 0)
        {
            return;
        }

        long end = start + length;
        lock (_lock)
        {
            if (_items.Count > 0)
            {
                (long s, long e) = _items[^1];
                if (start >= s && start <= e)
                {
                    _items[^1] = (s, Math.Max(e, end));
                    return;
                }

                if (start < s)
                {
                    _sorted = false;
                }
            }

            _items.Add((start, end));
        }
    }

    /// <summary>範囲のうち、どの区間とも重ならないバイト数。</summary>
    public long Uncovered(long start, long length)
    {
        if (length <= 0)
        {
            return 0;
        }

        lock (_lock)
        {
            Normalize();
            long end = start + length;
            long covered = 0;

            // start より後ろで終わる最初の区間から
            int lo = 0;
            int hi = _items.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_items[mid].End <= start)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (int i = lo; i < _items.Count && _items[i].Start < end; i++)
            {
                covered += Math.Min(end, _items[i].End) - Math.Max(start, _items[i].Start);
            }

            return length - covered;
        }
    }

    private void Normalize()
    {
        if (_sorted)
        {
            return;
        }

        _items.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(long Start, long End)>(_items.Count);
        foreach ((long s, long e) in _items)
        {
            if (merged.Count > 0 && s <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        _items = merged;
        _sorted = true;
    }
}
