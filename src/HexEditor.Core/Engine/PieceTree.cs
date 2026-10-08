namespace HexEditor.Core.Engine;

/// <summary>
/// ピースの並びを表す永続的な AVL 木 (ENG-02、ENG-05)。
/// すべての操作は既存のノードを変更せず、新しい木を返す。挿入・削除・範囲の複製は分割 (split) と結合 (join) で行い、
/// いずれもピースの数を n として O(log n)。
/// </summary>
public sealed class PieceTree
{
    public static readonly PieceTree Empty = new(null);

    private readonly Node? _root;

    private PieceTree(Node? root) => _root = root;

    /// <summary>ドキュメントの長さ (バイト)。</summary>
    public long Length => Len(_root);

    /// <summary>ピースの数。</summary>
    public long PieceCount => _root?.Count ?? 0;

    public bool IsEmpty => _root is null;

    public static PieceTree FromPiece(Piece piece) => new(new Node(null, piece, null));

    /// <summary>ピースの並びから木を作る。隣り合うピースはまとめる。</summary>
    public static PieceTree FromPieces(IEnumerable<Piece> pieces)
    {
        PieceTree tree = Empty;
        foreach (Piece piece in pieces)
        {
            tree = tree.Concat(FromPiece(piece));
        }

        return tree;
    }

    /// <summary><paramref name="offset"/> に <paramref name="piece"/> を挿入する。</summary>
    public PieceTree Insert(long offset, Piece piece) => Insert(offset, FromPiece(piece));

    /// <summary><paramref name="offset"/> に別の木の内容を挿入する。部分木はそのまま共有する。</summary>
    public PieceTree Insert(long offset, PieceTree content)
    {
        CheckOffset(offset);
        CheckGrowth(content.Length);
        (Node? left, Node? right) = Split(_root, offset);
        return new PieceTree(Concat(Concat(left, content._root), right));
    }

    /// <summary>[offset, offset + length) を削除する。</summary>
    public PieceTree Delete(long offset, long length)
    {
        CheckRange(offset, length);
        if (length == 0)
        {
            return this;
        }

        (Node? left, Node? rest) = Split(_root, offset);
        (_, Node? right) = Split(rest, length);
        return new PieceTree(Concat(left, right));
    }

    /// <summary>[offset, offset + length) を <paramref name="content"/> で置き換える。</summary>
    public PieceTree Replace(long offset, long length, PieceTree content)
    {
        CheckRange(offset, length);
        if (content.Length - length > 0)
        {
            CheckGrowth(content.Length - length);
        }

        (Node? left, Node? rest) = Split(_root, offset);
        (_, Node? right) = Split(rest, length);
        return new PieceTree(Concat(Concat(left, content._root), right));
    }

    /// <summary>[offset, offset + length) の部分を表す木。データはコピーしない。</summary>
    public PieceTree Slice(long offset, long length)
    {
        CheckRange(offset, length);
        (_, Node? rest) = Split(_root, offset);
        (Node? middle, _) = Split(rest, length);
        return new PieceTree(middle);
    }

    /// <summary>この木の後ろに <paramref name="other"/> をつなげる。</summary>
    public PieceTree Concat(PieceTree other)
    {
        CheckGrowth(other.Length);
        return new PieceTree(Concat(_root, other._root));
    }

    /// <summary>
    /// [offset, offset + length) にかかるピースを先頭から順に列挙する。ピースは範囲で切り詰めて返す。
    /// <c>DocumentOffset</c> はそのピースのドキュメント上の開始位置。
    /// </summary>
    public IEnumerable<(long DocumentOffset, Piece Piece)> Enumerate(long offset, long length)
    {
        CheckRange(offset, length);
        if (length == 0)
        {
            yield break;
        }

        long end = offset + length;
        var stack = new Stack<(Node Node, long Start)>();

        // offset を含むピースまで下りながら、後で訪れる親を積む。
        Node? node = _root;
        long nodeStart = 0;
        while (node is not null)
        {
            long leftLen = Len(node.Left);
            long pieceStart = nodeStart + leftLen;
            if (offset < pieceStart)
            {
                stack.Push((node, nodeStart));
                node = node.Left;
            }
            else if (offset >= pieceStart + node.Piece.Length)
            {
                nodeStart = pieceStart + node.Piece.Length;
                node = node.Right;
            }
            else
            {
                stack.Push((node, nodeStart));
                break;
            }
        }

        while (stack.Count > 0)
        {
            (Node current, long start) = stack.Pop();
            long pieceStart = start + Len(current.Left);
            if (pieceStart >= end)
            {
                yield break;
            }

            long from = Math.Max(offset, pieceStart);
            long to = Math.Min(end, pieceStart + current.Piece.Length);
            yield return (from, current.Piece.WithRange(from - pieceStart, to - from));

            // 右の部分木の最も左のピースまで下りる。
            Node? next = current.Right;
            long nextStart = pieceStart + current.Piece.Length;
            while (next is not null)
            {
                stack.Push((next, nextStart));
                next = next.Left;
            }
        }
    }

    /// <summary><paramref name="offset"/> を含むピース (切り詰めない) とその開始位置。範囲外なら null。</summary>
    public (long Start, Piece Piece)? PieceAt(long offset)
    {
        if (offset < 0 || offset >= Length)
        {
            return null;
        }

        Node? node = _root;
        long nodeStart = 0;
        while (node is not null)
        {
            long pieceStart = nodeStart + Len(node.Left);
            if (offset < pieceStart)
            {
                node = node.Left;
            }
            else if (offset >= pieceStart + node.Piece.Length)
            {
                nodeStart = pieceStart + node.Piece.Length;
                node = node.Right;
            }
            else
            {
                return (pieceStart, node.Piece);
            }
        }

        return null;
    }

    /// <summary>すべてのピースを先頭から列挙する。</summary>
    public IEnumerable<(long DocumentOffset, Piece Piece)> EnumerateAll() => Enumerate(0, Length);

    /// <summary>木の高さ (テストと診断用)。</summary>
    internal int Height => H(_root);

    private void CheckOffset(long offset)
    {
        if (offset < 0 || offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "オフセットがドキュメントの範囲外です。");
        }
    }

    private void CheckRange(long offset, long length)
    {
        CheckOffset(offset);
        if (length < 0 || length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "範囲がドキュメントの範囲外です。");
        }
    }

    private void CheckGrowth(long growth)
    {
        if (growth > long.MaxValue - Length)
        {
            throw new InvalidOperationException("ドキュメントの長さの上限 (2^63 − 1 バイト) を超えます。");
        }
    }

    private static long Len(Node? node) => node?.Length ?? 0;

    private static int H(Node? node) => node?.Height ?? 0;

    // ---- 分割と結合 ----

    /// <summary>先頭から <paramref name="offset"/> バイトの位置で 2 つの木に分ける。</summary>
    private static (Node? Left, Node? Right) Split(Node? node, long offset)
    {
        if (node is null)
        {
            return (null, null);
        }

        long leftLen = Len(node.Left);
        long pieceEnd = leftLen + node.Piece.Length;
        if (offset < leftLen)
        {
            (Node? ll, Node? lr) = Split(node.Left, offset);
            return (ll, Join(lr, node.Piece, node.Right));
        }

        if (offset > pieceEnd)
        {
            (Node? rl, Node? rr) = Split(node.Right, offset - pieceEnd);
            return (Join(node.Left, node.Piece, rl), rr);
        }

        if (offset == leftLen)
        {
            return (node.Left, Join(null, node.Piece, node.Right));
        }

        if (offset == pieceEnd)
        {
            return (Join(node.Left, node.Piece, null), node.Right);
        }

        (Piece a, Piece b) = node.Piece.Split(offset - leftLen);
        return (Join(node.Left, a, null), Join(null, b, node.Right));
    }

    /// <summary>2 つの木をつなげる。境目のピースが連続していれば 1 つにまとめる。</summary>
    private static Node? Concat(Node? left, Node? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        (Node? leftRest, Piece last) = SplitLast(left);
        (Piece first, Node? rightRest) = SplitFirst(right);
        if (last.TryAppend(first, out Piece merged))
        {
            return Join(leftRest, merged, rightRest);
        }

        return Join(leftRest, last, Join(null, first, rightRest));
    }

    private static (Node? Remaining, Piece Last) SplitLast(Node node)
    {
        if (node.Right is null)
        {
            return (node.Left, node.Piece);
        }

        (Node? rest, Piece last) = SplitLast(node.Right);
        return (Join(node.Left, node.Piece, rest), last);
    }

    private static (Piece First, Node? Remaining) SplitFirst(Node node)
    {
        if (node.Left is null)
        {
            return (node.Piece, node.Right);
        }

        (Piece first, Node? rest) = SplitFirst(node.Left);
        return (first, Join(rest, node.Piece, node.Right));
    }

    /// <summary>left のすべて &lt; middle &lt; right のすべて、の順で 1 つの平衡木にする (Blelloch らの join)。</summary>
    private static Node Join(Node? left, Piece middle, Node? right)
    {
        int hl = H(left);
        int hr = H(right);
        if (hl > hr + 1)
        {
            return JoinRight(left!, middle, right);
        }

        if (hr > hl + 1)
        {
            return JoinLeft(left, middle, right!);
        }

        return new Node(left, middle, right);
    }

    private static Node JoinRight(Node left, Piece middle, Node? right)
    {
        Node? l = left.Left;
        Node? c = left.Right;
        if (H(c) <= H(right) + 1)
        {
            var t = new Node(c, middle, right);
            if (t.Height <= H(l) + 1)
            {
                return new Node(l, left.Piece, t);
            }

            return RotateLeft(new Node(l, left.Piece, RotateRight(t)));
        }

        Node t2 = JoinRight(c!, middle, right);
        var t3 = new Node(l, left.Piece, t2);
        return t2.Height <= H(l) + 1 ? t3 : RotateLeft(t3);
    }

    private static Node JoinLeft(Node? left, Piece middle, Node right)
    {
        Node? r = right.Right;
        Node? c = right.Left;
        if (H(c) <= H(left) + 1)
        {
            var t = new Node(left, middle, c);
            if (t.Height <= H(r) + 1)
            {
                return new Node(t, right.Piece, r);
            }

            return RotateRight(new Node(RotateLeft(t), right.Piece, r));
        }

        Node t2 = JoinLeft(left, middle, c!);
        var t3 = new Node(t2, right.Piece, r);
        return t2.Height <= H(r) + 1 ? t3 : RotateRight(t3);
    }

    private static Node RotateLeft(Node n)
    {
        Node r = n.Right!;
        return new Node(new Node(n.Left, n.Piece, r.Left), r.Piece, r.Right);
    }

    private static Node RotateRight(Node n)
    {
        Node l = n.Left!;
        return new Node(l.Left, l.Piece, new Node(l.Right, n.Piece, n.Right));
    }

    /// <summary>不変のノード。部分木の合計の長さ・ピースの数・高さを持つ。</summary>
    private sealed class Node
    {
        public Node(Node? left, Piece piece, Node? right)
        {
            Left = left;
            Piece = piece;
            Right = right;
            Height = 1 + Math.Max(H(left), H(right));
            Length = Len(left) + piece.Length + Len(right);
            Count = (left?.Count ?? 0) + 1 + (right?.Count ?? 0);
        }

        public Node? Left { get; }

        public Piece Piece { get; }

        public Node? Right { get; }

        public int Height { get; }

        public long Length { get; }

        public long Count { get; }
    }
}
