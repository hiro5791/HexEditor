using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

public sealed partial class Document
{
    // ---- 履歴の保存と読み込み (EDIT-21) が使う内部の操作 ----

    /// <summary>今の元データの版 (保存のたびに変わる)。</summary>
    internal DocumentStorage LatestStorage => _storage;

    /// <summary>次に加える外部参照の番号。</summary>
    internal int NextExternalIndex => _storage.Externals.Count;

    /// <summary>このドキュメントが持つ外部のデータ (閉じるときに閉じる) を加え、外部参照の番号を返す。</summary>
    internal int AddOwnedExternal(IByteSource source)
    {
        _storage.Externals.Add(source);
        _ownedExternals.Add(source);
        return _storage.Externals.Count - 1;
    }

    /// <summary><paramref name="data"/> の位置にあるパターンを追加バッファに入れ、パターンのピースを返す。</summary>
    internal Piece LoadPatternPiece(IByteSource data, long offset, int patternLength, long length, long phase)
    {
        if (patternLength is < 1 or > GeneratedData.MaxPatternLength || offset < 0 || offset > data.Length - patternLength)
        {
            throw new FormatException("pattern");
        }

        byte[] pattern = new byte[patternLength];
        data.Read(offset, pattern);
        long at = _storage.AddBuffer.Append(pattern);
        return Piece.Pattern(at, patternLength, length, phase);
    }

    /// <summary>読み込んだ木で履歴を置き換え、全体が変わったことを知らせる (EDIT-21 の仕様 9)。</summary>
    internal void ReplaceHistory(HistoryNode root, HistoryNode current, HistoryNode? saved)
    {
        History.Load(root, current, saved);
        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Reloaded));
    }

    // ---- パッチの適用 (EDIT-43) が使う内部の操作 ----

    /// <summary>バイト列を追加バッファに入れ、それを指す木を返す (パッチのデータ)。</summary>
    internal PieceTree BufferBytes(ReadOnlySpan<byte> data) => data.IsEmpty ? PieceTree.Empty : AppendToBuffer(data);

    /// <summary>パターンの繰り返しを表す木 (重なるコピーの展開)。</summary>
    internal PieceTree PatternTree(ReadOnlySpan<byte> pattern, long length) => PieceTree.FromPiece(PatternPiece(pattern, length));

    /// <summary>
    /// 内容全体を <paramref name="tree"/> (現在の木の一部と、追加バッファの内容から作ったもの) にする 1 つの編集。長さが変わる場合は
    /// 長さを変えられるドキュメントだけ (<see cref="FixedLengthException"/>)。
    /// </summary>
    internal void ReplaceAll(PieceTree tree, string description)
    {
        RequireEditable();
        if (tree.Length != Length && !CanResize)
        {
            throw new FixedLengthException();
        }

        Apply(tree, 0, Length, tree.Length, description, null);
    }

    /// <summary>逆変換で書き直す 1 回の大きさ (要素の境界にそろえる)。</summary>
    private const int InverseChunk = 1 << 20;

    /// <summary>
    /// 変換ビューを通した編集 (EDIT-45 の仕様 6): 編集で新しく置いた内容を「表示上の値」とみなし、逆変換して元のデータにする。
    /// 編集の前と同じピースが同じ位置にある部分 (変えていない元のデータ) はそのまま残す。要素の途中だけが変わった場合は、要素全体を
    /// 「編集前の表示上の値に新しい値を重ねたもの」として逆変換する。
    /// </summary>
    private PieceTree InverseThroughView(IViewTransform transform, PieceTree tree, long offset, long removed, long inserted)
    {
        if (!transform.CanInverse)
        {
            throw new DocumentReadOnlyException();
        }

        if (removed != inserted)
        {
            throw new FixedLengthException();
        }

        if (inserted == 0)
        {
            return tree;
        }

        DocumentSnapshot before = History.Current;
        PieceTree old = before.Tree;

        // 1. 新しく置いた (編集前と違うピースの) 範囲。
        var display = new List<(long Offset, long Length)>();
        foreach ((long at, Piece piece) in tree.Enumerate(offset, inserted))
        {
            foreach ((long oldAt, Piece oldPiece) in old.Enumerate(at, piece.Length))
            {
                if (piece.WithRange(oldAt - at, oldPiece.Length) != oldPiece)
                {
                    if (display.Count > 0 && display[^1].Offset + display[^1].Length == oldAt)
                    {
                        display[^1] = (display[^1].Offset, display[^1].Length + oldPiece.Length);
                    }
                    else
                    {
                        display.Add((oldAt, oldPiece.Length));
                    }
                }
            }
        }

        if (display.Count == 0)
        {
            return tree;
        }

        // 2. 要素の境界にそろえた窓ごとに、編集前の表示上の値に新しい値を重ねて逆変換する。
        int align = Math.Max(1, transform.Alignment);
        long origin = transform.AlignmentOrigin;
        long length = tree.Length;
        long AlignDown(long v) => v - (((v - origin) % align) + align) % align;
        long AlignUp(long v)
        {
            long r = (((v - origin) % align) + align) % align;
            return r == 0 ? v : Math.Min(length, v + align - r);
        }

        var windows = new List<(long Start, long End)>();
        foreach ((long o, long l) in display)
        {
            long start = Math.Max(0, AlignDown(o)), end = AlignUp(o + l);
            if (windows.Count > 0 && windows[^1].End >= start)
            {
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, end));
            }
            else
            {
                windows.Add((start, end));
            }
        }

        PieceTree input = tree;
        var inputSnapshot = new DocumentSnapshot(_storage, input);
        int chunk = InverseChunk - InverseChunk % align;
        int d = 0;
        foreach ((long ws, long we) in windows)
        {
            for (long pos = ws; pos < we; pos += chunk)
            {
                int n = (int)Math.Min(chunk, we - pos);
                byte[] buffer = new byte[n];
                before.Read(pos, buffer);
                transform.Forward(pos, buffer);

                // この窓にかかる新しい値を重ねる。
                while (d < display.Count && display[d].Offset + display[d].Length <= pos)
                {
                    d++;
                }

                for (int k = d; k < display.Count && display[k].Offset < pos + n; k++)
                {
                    long from = Math.Max(pos, display[k].Offset), to = Math.Min(pos + n, display[k].Offset + display[k].Length);
                    inputSnapshot.Read(from, buffer.AsSpan((int)(from - pos), (int)(to - from)));
                }

                transform.Inverse(pos, buffer);
                tree = tree.Replace(pos, n, AppendToBuffer(buffer));
            }
        }

        return tree;
    }
}
