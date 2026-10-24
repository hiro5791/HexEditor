namespace HexEditor.Core.Compare.Structure;

/// <summary>構造の差分パネルの種類の絞り込み (ANA-32 の仕様 3)。</summary>
[Flags]
public enum StructDiffFilterKinds
{
    None = 0,
    Changed = 1,
    Added = 2,
    Removed = 4,
    Moved = 8,
    TypeChanged = 16,
    Unparsed = 32,
    All = Changed | Added | Removed | Moved | TypeChanged | Unparsed,
}

/// <summary>構造の差分パネルの絞り込みの条件 (ANA-32 の仕様 3)。</summary>
public sealed record StructDiffFilter
{
    public StructDiffFilterKinds Kinds { get; init; } = StructDiffFilterKinds.All;

    /// <summary>埋め草の行も表示する (既定オフ)。</summary>
    public bool IncludePadding { get; init; }

    /// <summary>名前・パス・値の部分一致 (大文字・小文字を区別しない序数比較。00 の 5.4)。空なら絞り込まない。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>一致したフィールドも表示する (既定オフ)。</summary>
    public bool ShowSame { get; init; }

    /// <summary>行が種類・埋め草の条件に合うか (文字列は見ない)。「移動・変更」は「移動」と「変更」のどちらがオンでも表示する。</summary>
    public bool AcceptsKind(StructDiffNode node)
    {
        if (node.IsPadding && !IncludePadding)
        {
            return false;
        }

        return node.Kind switch
        {
            StructDiffKind.Same => ShowSame,
            StructDiffKind.Changed => Kinds.HasFlag(StructDiffFilterKinds.Changed),
            StructDiffKind.Added => Kinds.HasFlag(StructDiffFilterKinds.Added),
            StructDiffKind.Removed => Kinds.HasFlag(StructDiffFilterKinds.Removed),
            StructDiffKind.Moved => Kinds.HasFlag(StructDiffFilterKinds.Moved),
            StructDiffKind.MovedChanged => (Kinds & (StructDiffFilterKinds.Moved | StructDiffFilterKinds.Changed)) != 0,
            StructDiffKind.TypeChanged => Kinds.HasFlag(StructDiffFilterKinds.TypeChanged),
            StructDiffKind.Unparsed => Kinds.HasFlag(StructDiffFilterKinds.Unparsed),
            _ => false,
        };
    }
}

/// <summary>構造の差分パネルの 1 行 (表示用の写し)。<see cref="Node"/> が null の行は「構造の外の差分」の行。</summary>
public sealed record StructDiffRow(StructDiffNode? Node, int Depth, bool HasChildren, bool IsExpanded, int Index);

/// <summary>
/// 構造の差分パネルの表示の計算 (ANA-32 の仕様 1・3・5)。差分のある葉とその祖先だけを残し (絞り込みの後)、開閉の状態に従って行を
/// 並べる。行は前順の番号で持ち、100 万行でも行ごとのオブジェクトを作らない (行の写しは表示する分だけ <see cref="RowAt"/> で作る)。
/// </summary>
public sealed class StructDiffView
{
    /// <summary>差分がこの件数以下なら、すべて開いた状態で表示する (仕様 1)。</summary>
    public const int ExpandAllLimit = 1000;

    private readonly Dictionary<StructDiffNode, bool> _expanded = new(ReferenceEqualityComparer.Instance);
    private List<StructDiffNode> _nodes = [];
    private int[] _end = [];
    private bool[] _self = [];
    private bool[] _visible = [];
    private List<int> _rows = [];
    private List<int> _leaves = [];
    private readonly Dictionary<int, long[]> _counts = [];
    private bool _expandAll = true;

    /// <summary>表示している結果 (なければ null)。</summary>
    public StructCompareResult? Result { get; private set; }

    public StructDiffFilter Filter { get; private set; } = new();

    /// <summary>行の数 (「構造の外の差分」の行を含む)。</summary>
    public int RowCount => _rows.Count + (HasOutsideRow ? 1 : 0);

    /// <summary>絞り込みの後の葉の行 (次 / 前の構造の差分の対象。仕様 5) の数。</summary>
    public int LeafCount => _leaves.Count;

    /// <summary>一覧の最後に「構造の外の差分」の行を出す (仕様 8 の要約の値があるとき)。</summary>
    public bool HasOutsideRow => Result?.Outside is { Count: > 0 };

    /// <summary>結果と絞り込みを変えて作り直す。比較中の結果は、その時点までの行で作る。</summary>
    public void Rebuild(StructCompareResult? result, StructDiffFilter? filter = null)
    {
        if (!ReferenceEquals(result, Result))
        {
            _expanded.Clear();
        }

        Result = result;
        if (filter is not null)
        {
            Filter = filter;
        }

        _counts.Clear();
        if (result is null)
        {
            _nodes = [];
            _end = [];
            _self = [];
            _visible = [];
            _rows = [];
            _leaves = [];
            return;
        }

        result.Snapshot(() =>
        {
            Collect(result.Root);
            return 0;
        });
        Layout();
    }

    /// <summary>前順に並べ、絞り込みに合う行 (<see cref="_self"/>) と、表示する行 (<see cref="_visible"/>: 自分か子孫が合う) を求める。</summary>
    private void Collect(StructDiffNode root)
    {
        var nodes = new List<StructDiffNode>();
        var parents = new List<int>();
        var stack = new Stack<(StructDiffNode Node, int Parent)>();
        PushChildren(stack, root, -1);
        while (stack.Count > 0)
        {
            (StructDiffNode n, int parent) = stack.Pop();
            int index = nodes.Count;
            n.ViewIndex = index;
            nodes.Add(n);
            parents.Add(parent);
            PushChildren(stack, n, index);
        }

        int count = nodes.Count;
        int[] end = new int[count];
        bool[] self = new bool[count];
        bool[] visible = new bool[count];
        string text = Filter.Text;
        for (int i = count - 1; i >= 0; i--)
        {
            StructDiffNode n = nodes[i];
            if (end[i] == 0)
            {
                end[i] = i + 1;
            }

            bool pass = (n.IsDiff || n.Kind == StructDiffKind.Same) && Filter.AcceptsKind(n) && (text.Length == 0 || Matches(n, text));
            self[i] = pass;
            visible[i] |= pass;
            int p = parents[i];
            if (p >= 0)
            {
                end[p] = Math.Max(end[p], end[i]);
                visible[p] |= visible[i];
            }
        }

        _nodes = nodes;
        _end = end;
        _self = self;
        _visible = visible;
        int leafDiffs = 0;
        var leaves = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (self[i] && !HasVisibleChild(i))
            {
                leaves.Add(i);
                if (nodes[i].IsDiff)
                {
                    leafDiffs++;
                }
            }
        }

        _leaves = leaves;
        _expandAll = leafDiffs <= ExpandAllLimit;
    }

    private static void PushChildren(Stack<(StructDiffNode, int)> stack, StructDiffNode n, int index)
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
            stack.Push((children[i], index));
        }
    }

    private static bool Matches(StructDiffNode n, string text) =>
        n.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || n.DisplayPath.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (n.HasRight && n.RightPath.Contains(text, StringComparison.OrdinalIgnoreCase))
        || (n.LeftDisplay?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
        || (n.RightDisplay?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

    private bool HasVisibleChild(int i)
    {
        for (int c = i + 1; c < _end[i]; c = _end[c])
        {
            if (_visible[c])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>開閉の状態に従って、表示する行を並べる。</summary>
    private void Layout()
    {
        var rows = new List<int>();
        int i = 0;
        while (i < _nodes.Count)
        {
            if (!_visible[i])
            {
                i = _end[i];
                continue;
            }

            rows.Add(i);
            i = IsExpanded(i) ? i + 1 : _end[i];
        }

        _rows = rows;
    }

    private bool IsExpanded(int i)
    {
        StructDiffNode n = _nodes[i];
        if (_expanded.TryGetValue(n, out bool open))
        {
            return open;
        }

        return _expandAll || n.Depth == 0;
    }

    /// <summary>行 <paramref name="row"/> の写し。</summary>
    public StructDiffRow RowAt(int row)
    {
        if (row >= _rows.Count)
        {
            return new StructDiffRow(null, 0, false, false, row);
        }

        int i = _rows[row];
        bool children = HasVisibleChild(i);
        return new StructDiffRow(_nodes[i], _nodes[i].Depth, children, children && IsExpanded(i), row);
    }

    /// <summary>入れ物の行の、子孫の差分の件数 (種類ごと。絞り込みの後。仕様 1)。</summary>
    public IReadOnlyDictionary<StructDiffKind, long> CountsOf(StructDiffNode node)
    {
        int i = IndexOf(node);
        var result = new Dictionary<StructDiffKind, long>();
        if (i < 0)
        {
            return result;
        }

        if (!_counts.TryGetValue(i, out long[]? counts))
        {
            counts = new long[Enum.GetValues<StructDiffKind>().Length];
            for (int k = i + 1; k < _end[i]; k++)
            {
                if (_self[k] && _nodes[k].IsDiff)
                {
                    counts[(int)_nodes[k].Kind]++;
                }
            }

            _counts[i] = counts;
        }

        for (int k = 0; k < counts.Length; k++)
        {
            if (counts[k] > 0)
            {
                result[(StructDiffKind)k] = counts[k];
            }
        }

        return result;
    }

    /// <summary>行の番号 (表示していなければ -1)。</summary>
    public int RowOf(StructDiffNode node)
    {
        int i = IndexOf(node);
        return i < 0 ? -1 : _rows.BinarySearch(i) is int r and >= 0 ? r : -1;
    }

    private int IndexOf(StructDiffNode node)
    {
        int k = node.ViewIndex;
        return k >= 0 && k < _nodes.Count && ReferenceEquals(_nodes[k], node) ? k : -1;
    }

    /// <summary>行を開く・閉じる。</summary>
    public void SetExpanded(StructDiffNode node, bool expanded)
    {
        _expanded[node] = expanded;
        Layout();
    }

    /// <summary>行が見えるように祖先を開く。</summary>
    public void Reveal(StructDiffNode node)
    {
        bool changed = false;
        for (StructDiffNode? p = node.Parent; p is not null && p.Parent is not null; p = p.Parent)
        {
            int i = IndexOf(p);
            if (i >= 0 && !IsExpanded(i))
            {
                _expanded[p] = true;
                changed = true;
            }
        }

        if (changed)
        {
            Layout();
        }
    }

    /// <summary>葉の行 (次 / 前の構造の差分の対象) の一覧の <paramref name="index"/> 番目。</summary>
    public StructDiffNode LeafAt(int index) => _nodes[_leaves[index]];

    /// <summary>葉の行の中の位置 (葉でなければ -1)。</summary>
    public int LeafIndexOf(StructDiffNode? node)
    {
        if (node is null)
        {
            return -1;
        }

        int i = IndexOf(node);
        return i < 0 ? -1 : _leaves.BinarySearch(i) is int r and >= 0 ? r : -1;
    }

    /// <summary>
    /// 次 / 前の葉の行 (仕様 5)。末尾・先頭で折り返したら Wrapped。葉の行がなければ null。<paramref name="current"/> が葉でなければ、
    /// その位置より後ろ (前) の最初の葉。
    /// </summary>
    public (StructDiffNode Node, bool Wrapped)? Step(StructDiffNode? current, bool next)
    {
        if (_leaves.Count == 0)
        {
            return null;
        }

        int at = current is null ? -1 : IndexOf(current);
        int pos;
        if (at < 0)
        {
            pos = next ? 0 : _leaves.Count - 1;
            return (_nodes[_leaves[pos]], false);
        }

        int found = _leaves.BinarySearch(at);
        if (found >= 0)
        {
            pos = next ? found + 1 : found - 1;
        }
        else
        {
            int insert = ~found;
            pos = next ? insert : insert - 1;
        }

        bool wrapped = false;
        if (pos >= _leaves.Count)
        {
            pos = 0;
            wrapped = true;
        }
        else if (pos < 0)
        {
            pos = _leaves.Count - 1;
            wrapped = true;
        }

        return (_nodes[_leaves[pos]], wrapped);
    }

    /// <summary>表示中 (絞り込みの後) の差分の行を木の順に返す (エクスポートの既定の対象。仕様 7)。</summary>
    public IEnumerable<StructDiffNode> FilteredDiffs()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_self[i] && _nodes[i].IsDiff)
            {
                yield return _nodes[i];
            }
        }
    }
}
