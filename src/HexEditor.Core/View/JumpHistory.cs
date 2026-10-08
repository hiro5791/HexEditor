using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>ジャンプ履歴の 1 件 (VIEW-31 の仕様 1): オフセット、一番上の行の先頭オフセット、操作中の列。</summary>
public readonly record struct JumpPoint(long Offset, long TopOffset, ActiveColumn Column);

/// <summary>
/// ジャンプ履歴 (VIEW-31)。Web ブラウザの戻る・進むと同じ。ビューごとに持ち、最大 100 件。セッションの間だけ持つ (仕様 11)。
/// 一番上の行は行番号ではなく先頭オフセットで持つ (1 行のバイト数を変えても同じデータを指すため)。
/// </summary>
public sealed class JumpHistory
{
    /// <summary>最大件数 (仕様 4)。</summary>
    public const int Limit = 100;

    /// <summary>「履歴の一覧」に出す件数 (仕様 8)。</summary>
    public const int ListCount = 20;

    private readonly LinkedList<JumpPoint> _back = new();
    private readonly LinkedList<JumpPoint> _forward = new();

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public int BackCount => _back.Count;

    public int ForwardCount => _forward.Count;

    /// <summary>
    /// 移動の直前の位置を記録する (仕様 1)。直前の記録と同じ行なら記録しない (仕様 3)。どちらの場合も進む側の履歴を消す (仕様 5)。
    /// <paramref name="sameRow"/> は 2 つのオフセットが同じ行かを判定する。
    /// </summary>
    public void Record(JumpPoint point, Func<long, long, bool> sameRow)
    {
        _forward.Clear();
        if (_back.Last is { } last && sameRow(last.Value.Offset, point.Offset))
        {
            return;
        }

        _back.AddLast(point);
        while (_back.Count > Limit)
        {
            _back.RemoveFirst();
        }
    }

    /// <summary>戻る (仕様 5): 現在位置を進む側に積み、1 つ前の位置を返す。戻れなければ null。新たな記録は追加しない (仕様 9)。</summary>
    public JumpPoint? Back(JumpPoint current)
    {
        if (_back.Last is not { } last)
        {
            return null;
        }

        _back.RemoveLast();
        _forward.AddFirst(current);
        return last.Value;
    }

    /// <summary>進む。</summary>
    public JumpPoint? Forward(JumpPoint current)
    {
        if (_forward.First is not { } next)
        {
            return null;
        }

        _forward.RemoveFirst();
        _back.AddLast(current);
        return next.Value;
    }

    /// <summary>
    /// 「履歴の一覧」(仕様 8): 戻る側の新しいものから最大 20 件。<see cref="Back"/> を <c>index + 1</c> 回呼んだのと同じ位置に移るには
    /// <see cref="BackTo"/> を使う。
    /// </summary>
    public IReadOnlyList<JumpPoint> Recent() => [.. _back.Reverse().Take(ListCount)];

    /// <summary>一覧の <paramref name="index"/> 番目 (0 が最新) に移る。途中の位置と現在位置は進む側に積む。</summary>
    public JumpPoint? BackTo(int index, JumpPoint current)
    {
        JumpPoint? result = null;
        for (int i = 0; i <= index && CanGoBack; i++)
        {
            result = Back(current);
            current = result!.Value;
        }

        return result;
    }

    /// <summary>挿入・削除に合わせて位置をずらす (仕様 6)。削除された範囲に入った位置は削除範囲の先頭に移す。</summary>
    public void Adjust(DocumentChangedEventArgs e)
    {
        if (e.IsWholeDocument)
        {
            return;
        }

        foreach (LinkedList<JumpPoint> list in (LinkedList<JumpPoint>[])[_back, _forward])
        {
            for (LinkedListNode<JumpPoint>? node = list.First; node is not null; node = node.Next)
            {
                node.Value = node.Value with
                {
                    Offset = ShiftForEdit(node.Value.Offset, e),
                    TopOffset = ShiftForEdit(node.Value.TopOffset, e),
                };
            }
        }
    }

    /// <summary>挿入・削除による位置の追従 (VIEW-20 の仕様 7 の基準点、VIEW-31 の仕様 6 の履歴)。</summary>
    public static long ShiftForEdit(long offset, DocumentChangedEventArgs e)
    {
        // 上書き (長さが変わらない編集) では位置を変えない。
        if (e.IsWholeDocument || offset < e.Offset || e.RemovedLength == e.InsertedLength)
        {
            return offset;
        }

        if (offset < e.Offset + e.RemovedLength)
        {
            return e.Offset;
        }

        return offset - e.RemovedLength + e.InsertedLength;
    }

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }
}
