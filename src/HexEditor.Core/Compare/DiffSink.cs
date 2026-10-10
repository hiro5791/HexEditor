namespace HexEditor.Core.Compare;

/// <summary>
/// 見つけた差分を一覧に書く。「近い差分をまとめる」(ANA-02 の仕様 3、ANA-03 の仕様 4) のため、直前の差分を 1 つ保留しておき、
/// 間の一致が N バイト以下の差分を続けてまとめる。隣り合う差分 (間の一致が 0 バイト) は N によらずまとめる。読み込み不可の差分は
/// 他の種類とまとめない。
/// </summary>
internal sealed class DiffSink(CompareResult result, int mergeGap)
{
    private DiffRange _pending;
    private long _pendingBytes;
    private bool _hasPending;
    private bool _pendingMergeable;

    /// <param name="differentBytes">この差分の異なるバイト数 (要約の「異なるバイト数」に足す)。</param>
    /// <param name="mergeable">前後の差分とまとめてよいか (単純比較の長さの違いの残りはまとめない)。</param>
    public void Add(DiffRange diff, long differentBytes, bool mergeable = true)
    {
        if (diff.LeftLength == 0 && diff.RightLength == 0)
        {
            return;
        }

        if (_hasPending)
        {
            DiffRange p = _pending;
            long gapLeft = diff.LeftOffset - p.LeftEnd;
            long gapRight = diff.RightOffset - p.RightEnd;
            bool bothUnreadable = p.Kind == DiffKind.Unreadable && diff.Kind == DiffKind.Unreadable;
            bool neitherUnreadable = p.Kind != DiffKind.Unreadable && diff.Kind != DiffKind.Unreadable;
            bool join = bothUnreadable
                ? gapLeft == 0 && gapRight == 0
                : neitherUnreadable && mergeable && _pendingMergeable && gapLeft >= 0 && gapRight >= 0 && gapLeft <= mergeGap && gapRight <= mergeGap;
            if (join)
            {
                long leftLength = diff.LeftEnd - p.LeftOffset;
                long rightLength = diff.RightEnd - p.RightOffset;
                _pending = new DiffRange(bothUnreadable ? DiffKind.Unreadable : DiffRange.KindFor(leftLength, rightLength),
                    p.LeftOffset, leftLength, p.RightOffset, rightLength);
                _pendingBytes += differentBytes;
                return;
            }

            Emit();
        }

        _pending = diff;
        _pendingBytes = differentBytes;
        _pendingMergeable = mergeable;
        _hasPending = true;
    }

    /// <summary>保留している差分を書く (比較の終わり・中止のとき)。</summary>
    public void Flush()
    {
        if (_hasPending)
        {
            Emit();
        }
    }

    private void Emit()
    {
        result.Diffs.Add(_pending);
        result.Count(_pending.Kind, _pending.Kind == DiffKind.Unreadable ? 0 : _pendingBytes);
        _hasPending = false;
    }
}
