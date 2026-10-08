namespace HexEditor.Core.Bookmarks;

/// <summary>
/// ブックマークの区間木 (INSP-23 の「巨大ファイル・長時間処理」)。開始位置 (同じなら作った順) で並べたトリープで、部分木の
/// 終了位置の最大値を持つ。編集に合わせた位置のずらし (ある位置より後ろをまとめて動かす) は遅延して伝える。どれも
/// 件数 n に対して O(log n) (範囲の検索は O(log n + 該当件数))。
/// 各ノードの <see cref="Node.Start"/> は、祖先の <see cref="Node.Lazy"/> を足す前の値。
/// </summary>
internal sealed class BookmarkTree
{
    internal sealed class Node(Bookmark bookmark, long start, int priority)
    {
        public Bookmark Bookmark { get; } = bookmark;

        public long Start = start;

        /// <summary>部分木の終了位置 (開始 + 長さ) の最大値。</summary>
        public long MaxEnd = start + bookmark.Length;

        /// <summary>子の部分木にまだ伝えていないずらし。</summary>
        public long Lazy;

        public int Priority { get; } = priority;

        public int Size = 1;

        public Node? Left;
        public Node? Right;
        public Node? Parent;
    }

    private Node? _root;
    private readonly Random _random = new(0x5EED);

    public int Count => _root?.Size ?? 0;

    public void Clear() => _root = null;

    // ---- 基本の操作 ----

    private static void Push(Node n)
    {
        if (n.Lazy == 0)
        {
            return;
        }

        foreach (Node? c in (Node?[])[n.Left, n.Right])
        {
            if (c is not null)
            {
                c.Start += n.Lazy;
                c.MaxEnd += n.Lazy;
                c.Lazy += n.Lazy;
            }
        }

        n.Lazy = 0;
    }

    /// <summary>子から値を集める (子は伝え終わっていること)。</summary>
    private static void Pull(Node n)
    {
        n.Size = 1 + (n.Left?.Size ?? 0) + (n.Right?.Size ?? 0);
        long max = n.Start + n.Bookmark.Length;
        if (n.Left is { } l)
        {
            max = Math.Max(max, l.MaxEnd + n.Lazy);
            l.Parent = n;
        }

        if (n.Right is { } r)
        {
            max = Math.Max(max, r.MaxEnd + n.Lazy);
            r.Parent = n;
        }

        n.MaxEnd = max;
    }

    /// <summary>(開始, 番号) が key より小さいものを左、それ以外を右に分ける。</summary>
    private static (Node? Left, Node? Right) Split(Node? t, Func<Node, bool> goesLeft)
    {
        if (t is null)
        {
            return (null, null);
        }

        Push(t);
        if (goesLeft(t))
        {
            (Node? a, Node? b) = Split(t.Right, goesLeft);
            t.Right = a;
            Pull(t);
            t.Parent = null;
            if (b is not null)
            {
                b.Parent = null;
            }

            return (t, b);
        }
        else
        {
            (Node? a, Node? b) = Split(t.Left, goesLeft);
            t.Left = b;
            Pull(t);
            t.Parent = null;
            if (a is not null)
            {
                a.Parent = null;
            }

            return (a, t);
        }
    }

    private static Node? Merge(Node? a, Node? b)
    {
        if (a is null || b is null)
        {
            Node? only = a ?? b;
            if (only is not null)
            {
                only.Parent = null;
            }

            return only;
        }

        if (a.Priority > b.Priority)
        {
            Push(a);
            a.Right = Merge(a.Right, b);
            Pull(a);
            a.Parent = null;
            return a;
        }

        Push(b);
        b.Left = Merge(a, b.Left);
        Pull(b);
        b.Parent = null;
        return b;
    }

    private static bool Before(Node n, long start, long id) => n.Start < start || n.Start == start && n.Bookmark.Id < id;

    // ---- 追加・削除 ----

    public Node Insert(Bookmark bookmark, long start)
    {
        var node = new Node(bookmark, start, _random.Next());
        (Node? l, Node? r) = Split(_root, n => Before(n, start, bookmark.Id));
        _root = Merge(Merge(l, node), r);
        bookmark.Node = node;
        return node;
    }

    /// <summary>ノードを取り除く。祖先から順に遅延したずらしを伝えてから外す。</summary>
    public void Remove(Node node)
    {
        PushPath(node);
        Push(node);
        Node? replacement = Merge(node.Left, node.Right);
        Node? parent = node.Parent;
        if (parent is null)
        {
            _root = replacement;
        }
        else if (parent.Left == node)
        {
            parent.Left = replacement;
        }
        else
        {
            parent.Right = replacement;
        }

        if (replacement is not null)
        {
            replacement.Parent = parent;
        }

        for (Node? p = parent; p is not null; p = p.Parent)
        {
            Pull(p);
        }

        node.Left = node.Right = node.Parent = null;
        node.Bookmark.Node = null;
    }

    /// <summary>根からノードまでの遅延したずらしを伝える (ノードの Start が本当の値になる)。</summary>
    private static void PushPath(Node node)
    {
        var path = new Stack<Node>();
        for (Node? p = node.Parent; p is not null; p = p.Parent)
        {
            path.Push(p);
        }

        while (path.Count > 0)
        {
            Push(path.Pop());
        }
    }

    /// <summary>ノードの本当の開始位置 (祖先の遅延したずらしを足す)。</summary>
    public static long StartOf(Node node)
    {
        long start = node.Start;
        for (Node? p = node.Parent; p is not null; p = p.Parent)
        {
            start += p.Lazy;
        }

        return start;
    }

    /// <summary>
    /// 開始位置が <paramref name="threshold"/> より後ろ (<paramref name="inclusive"/> なら以上) のものを <paramref name="delta"/> だけずらす。
    /// 並び順が変わらない (ずらした後も前のものより後ろにある) ことは呼び出し側が保証する。
    /// </summary>
    public void ShiftFrom(long threshold, bool inclusive, long delta)
    {
        if (delta == 0 || _root is null)
        {
            return;
        }

        (Node? l, Node? r) = Split(_root, n => inclusive ? n.Start < threshold : n.Start <= threshold);
        if (r is not null)
        {
            r.Start += delta;
            r.MaxEnd += delta;
            r.Lazy += delta;
        }

        _root = Merge(l, r);
    }

    // ---- 検索 ----

    /// <summary>
    /// 開始 ≤ <paramref name="maxStart"/> かつ 終了 ≥ <paramref name="minEnd"/> のもの (開始位置の順)。
    /// <paramref name="strict"/> なら 開始 &lt; maxStart かつ 終了 &gt; minEnd。
    /// </summary>
    public void Collect(long maxStart, long minEnd, bool strict, List<Node> output) => Collect(_root, 0, maxStart, minEnd, strict, output);

    private static void Collect(Node? n, long offset, long maxStart, long minEnd, bool strict, List<Node> output)
    {
        if (n is null)
        {
            return;
        }

        long maxEnd = n.MaxEnd + offset;
        if (strict ? maxEnd <= minEnd : maxEnd < minEnd)
        {
            return;
        }

        long start = n.Start + offset;
        long childOffset = offset + n.Lazy;
        Collect(n.Left, childOffset, maxStart, minEnd, strict, output);
        if (strict ? start >= maxStart : start > maxStart)
        {
            return;
        }

        long end = start + n.Bookmark.Length;
        if (strict ? end > minEnd : end >= minEnd)
        {
            output.Add(n);
        }

        Collect(n.Right, childOffset, maxStart, minEnd, strict, output);
    }

    /// <summary>開始位置が <paramref name="offset"/> 以上で最も前のもの (<paramref name="strictlyAfter"/> なら より後ろ)。</summary>
    public Node? FirstFrom(long offset, bool strictlyAfter)
    {
        Node? best = null;
        long acc = 0;
        for (Node? n = _root; n is not null;)
        {
            long start = n.Start + acc;
            if (strictlyAfter ? start > offset : start >= offset)
            {
                best = n;
                acc += n.Lazy;
                n = n.Left;
            }
            else
            {
                acc += n.Lazy;
                n = n.Right;
            }
        }

        return best;
    }

    /// <summary>開始位置が <paramref name="offset"/> より前で最も後ろのもの。</summary>
    public Node? LastBefore(long offset)
    {
        Node? best = null;
        long acc = 0;
        for (Node? n = _root; n is not null;)
        {
            long start = n.Start + acc;
            if (start < offset)
            {
                best = n;
                acc += n.Lazy;
                n = n.Right;
            }
            else
            {
                acc += n.Lazy;
                n = n.Left;
            }
        }

        return best;
    }

    public Node? First()
    {
        Node? n = _root;
        while (n?.Left is not null)
        {
            n = n.Left;
        }

        return n;
    }

    public Node? Last()
    {
        Node? n = _root;
        while (n?.Right is not null)
        {
            n = n.Right;
        }

        return n;
    }

    /// <summary>開始位置の順にすべて (本当の開始位置を添える)。</summary>
    public IEnumerable<(Node Node, long Start)> InOrder()
    {
        var stack = new Stack<(Node Node, long Offset)>();
        Node? n = _root;
        long offset = 0;
        while (stack.Count > 0 || n is not null)
        {
            while (n is not null)
            {
                stack.Push((n, offset));
                offset += n.Lazy;
                n = n.Left;
            }

            (Node top, long topOffset) = stack.Pop();
            yield return (top, top.Start + topOffset);
            offset = topOffset + top.Lazy;
            n = top.Right;
        }
    }

    /// <summary>長さを変えた後に、祖先の終了位置の最大値を直す。</summary>
    public static void Refresh(Node node)
    {
        for (Node? p = node; p is not null; p = p.Parent)
        {
            long max = p.Start + p.Bookmark.Length;
            if (p.Left is { } l)
            {
                max = Math.Max(max, l.MaxEnd + p.Lazy);
            }

            if (p.Right is { } r)
            {
                max = Math.Max(max, r.MaxEnd + p.Lazy);
            }

            p.MaxEnd = max;
        }
    }
}
