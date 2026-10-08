namespace HexEditor.Core.Bookmarks;

/// <summary>
/// ブックマークの区間木 (INSP-23 の「巨大ファイル・長時間処理」)。開始位置 (同じなら作った順) で並べたトリープで、部分木の
/// 終了位置の最大値を持つ。編集に合わせた位置のずらし (ある位置より後ろをまとめて動かす) は遅延して伝える。どれも
/// 件数 n に対して O(log n) (範囲の検索は O(log n + 該当件数))。
/// 各ノードの <see cref="Bookmark.TreeStart"/> は、祖先の <see cref="Bookmark.Lazy"/> を足す前の値。
/// </summary>
internal sealed class BookmarkTree
{
    private Bookmark? _root;
    private readonly Random _random = new(0x5EED);

    public int Count => _root?.Size ?? 0;

    public void Clear() => _root = null;

    // ---- 基本の操作 ----

    private static void Push(Bookmark n)
    {
        if (n.Lazy == 0)
        {
            return;
        }

        Shift(n.Left, n.Lazy);
        Shift(n.Right, n.Lazy);
        n.Lazy = 0;
    }

    private static void Shift(Bookmark? c, long delta)
    {
        if (c is not null)
        {
            c.TreeStart += delta;
            c.MaxEnd += delta;
            c.Lazy += delta;
        }
    }

    /// <summary>子から値を集める (子は伝え終わっていること)。</summary>
    private static void Pull(Bookmark n)
    {
        n.Size = 1 + (n.Left?.Size ?? 0) + (n.Right?.Size ?? 0);
        long max = n.TreeStart + n.Length;
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
    private static (Bookmark? Left, Bookmark? Right) Split(Bookmark? t, Func<Bookmark, bool> goesLeft)
    {
        if (t is null)
        {
            return (null, null);
        }

        Push(t);
        if (goesLeft(t))
        {
            (Bookmark? a, Bookmark? b) = Split(t.Right, goesLeft);
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
            (Bookmark? a, Bookmark? b) = Split(t.Left, goesLeft);
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

    private static Bookmark? Merge(Bookmark? a, Bookmark? b)
    {
        if (a is null || b is null)
        {
            Bookmark? only = a ?? b;
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

    private static bool Before(Bookmark n, long start, long id) => n.TreeStart < start || n.TreeStart == start && n.Id < id;

    // ---- 追加・削除 ----

    /// <summary>ブックマーク自体をノードとして木に入れる (ノードを別のオブジェクトにしない。100 万件で GC の対象を減らす)。</summary>
    public void Insert(Bookmark bookmark, long start)
    {
        bookmark.TreeStart = start;
        bookmark.MaxEnd = start + bookmark.Length;
        bookmark.Lazy = 0;
        bookmark.Size = 1;
        bookmark.Priority = _random.Next();
        bookmark.Left = bookmark.Right = bookmark.Parent = null;
        (Bookmark? l, Bookmark? r) = Split(_root, n => Before(n, start, bookmark.Id));
        _root = Merge(Merge(l, bookmark), r);
        bookmark.InTree = true;
    }

    /// <summary>ノードを取り除く。祖先から順に遅延したずらしを伝えてから外す。</summary>
    public void Remove(Bookmark node)
    {
        PushPath(node);
        Push(node);
        Bookmark? replacement = Merge(node.Left, node.Right);
        Bookmark? parent = node.Parent;
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

        for (Bookmark? p = parent; p is not null; p = p.Parent)
        {
            Pull(p);
        }

        node.Left = node.Right = node.Parent = null;
        node.InTree = false;
    }

    /// <summary>根からノードまでの遅延したずらしを伝える (ノードの Start が本当の値になる)。</summary>
    private static void PushPath(Bookmark node)
    {
        var path = new Stack<Bookmark>();
        for (Bookmark? p = node.Parent; p is not null; p = p.Parent)
        {
            path.Push(p);
        }

        while (path.Count > 0)
        {
            Push(path.Pop());
        }
    }

    /// <summary>ノードの本当の開始位置 (祖先の遅延したずらしを足す)。</summary>
    public static long StartOf(Bookmark node)
    {
        long start = node.TreeStart;
        for (Bookmark? p = node.Parent; p is not null; p = p.Parent)
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

        (Bookmark? l, Bookmark? r) = Split(_root, n => inclusive ? n.TreeStart < threshold : n.TreeStart <= threshold);
        if (r is not null)
        {
            r.TreeStart += delta;
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
    public void Collect(long maxStart, long minEnd, bool strict, List<Bookmark> output) => Collect(_root, 0, maxStart, minEnd, strict, output);

    private static void Collect(Bookmark? n, long offset, long maxStart, long minEnd, bool strict, List<Bookmark> output)
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

        long start = n.TreeStart + offset;
        long childOffset = offset + n.Lazy;
        Collect(n.Left, childOffset, maxStart, minEnd, strict, output);
        if (strict ? start >= maxStart : start > maxStart)
        {
            return;
        }

        long end = start + n.Length;
        if (strict ? end > minEnd : end >= minEnd)
        {
            output.Add(n);
        }

        Collect(n.Right, childOffset, maxStart, minEnd, strict, output);
    }

    /// <summary>開始位置が <paramref name="offset"/> 以上で最も前のもの (<paramref name="strictlyAfter"/> なら より後ろ)。</summary>
    public Bookmark? FirstFrom(long offset, bool strictlyAfter)
    {
        Bookmark? best = null;
        long acc = 0;
        for (Bookmark? n = _root; n is not null;)
        {
            long start = n.TreeStart + acc;
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
    public Bookmark? LastBefore(long offset)
    {
        Bookmark? best = null;
        long acc = 0;
        for (Bookmark? n = _root; n is not null;)
        {
            long start = n.TreeStart + acc;
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

    public Bookmark? First()
    {
        Bookmark? n = _root;
        while (n?.Left is not null)
        {
            n = n.Left;
        }

        return n;
    }

    public Bookmark? Last()
    {
        Bookmark? n = _root;
        while (n?.Right is not null)
        {
            n = n.Right;
        }

        return n;
    }

    /// <summary>開始位置の順にすべて (本当の開始位置を添える)。</summary>
    public IEnumerable<(Bookmark Bookmark, long Start)> InOrder()
    {
        var stack = new Stack<(Bookmark Bookmark, long Offset)>();
        Bookmark? n = _root;
        long offset = 0;
        while (stack.Count > 0 || n is not null)
        {
            while (n is not null)
            {
                stack.Push((n, offset));
                offset += n.Lazy;
                n = n.Left;
            }

            (Bookmark top, long topOffset) = stack.Pop();
            yield return (top, top.TreeStart + topOffset);
            offset = topOffset + top.Lazy;
            n = top.Right;
        }
    }

    /// <summary>長さを変えた後に、祖先の終了位置の最大値を直す。</summary>
    public static void Refresh(Bookmark node)
    {
        for (Bookmark? p = node; p is not null; p = p.Parent)
        {
            long max = p.TreeStart + p.Length;
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
