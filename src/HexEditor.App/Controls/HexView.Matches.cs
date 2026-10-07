using HexEditor.Core.Engine;

namespace HexEditor.App.Controls;

/// <summary>
/// 表示中の範囲にある検索の一致の強調 (FIND-04 の仕様 9)。一致の計算は呼び出し側 (検索バー) が与える関数で行い、
/// 計算は表示範囲だけを対象にする。
/// </summary>
public sealed partial class HexView
{
    private Func<DocumentSnapshot, long, long, IReadOnlyList<(long Offset, long Length)>>? _matchProvider;

    /// <summary>
    /// [offset, offset + length) と重なる一致を返す関数。null なら強調しない。表示のたびに呼ぶため、ブロックしないこと。
    /// </summary>
    public Func<DocumentSnapshot, long, long, IReadOnlyList<(long Offset, long Length)>>? MatchProvider
    {
        get => _matchProvider;
        set
        {
            _matchProvider = value;
            RefreshMatches();
        }
    }

    /// <summary>一致が変わった (検索語・条件・データの読み込み) ので描き直す。</summary>
    public void RefreshMatches() => QueueRender();

    private bool[] ComputeMatched(DocumentSnapshot snapshot, long firstOffset, int span)
    {
        var matched = new bool[span];
        if (_matchProvider is null || span == 0)
        {
            return matched;
        }

        foreach ((long offset, long length) in _matchProvider(snapshot, firstOffset, span))
        {
            long from = Math.Max(offset, firstOffset) - firstOffset;
            long to = Math.Min(offset + length, firstOffset + span) - firstOffset;
            if (from < to)
            {
                matched.AsSpan((int)from, (int)(to - from)).Fill(true);
            }
        }

        return matched;
    }
}
