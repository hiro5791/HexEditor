using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Search;

/// <summary>
/// 次 / 前を検索の開始位置を決める (FIND-09 の仕様 1)。直前の検索の一致を覚えておき、現在の選択範囲がそれと同じときだけ
/// 一致の次 (前) から探す。そうでなければカーソル位置から探す。検索バーの状態と同じく、ウィンドウごとに 1 つ持つ (FIND-04 の仕様 10)。
/// </summary>
public sealed class FindNavigator
{
    /// <summary>直前の検索の一致 (なければ null)。</summary>
    public SearchHit? LastMatch { get; private set; }

    /// <summary>
    /// 検索の開始位置。前方は「この位置以上で始まる一致」、後方は「この位置未満で始まる一致」を探す
    /// (<see cref="SearchEngine.Find(DocumentSnapshot, SearchPattern, long, bool, bool, SearchOptions, LongRunningOperation?, CancellationToken)"/> の start)。
    /// <list type="bullet">
    /// <item>次を検索: 選択範囲が直前の一致と同じなら「一致の開始 + 1」、そうでなければカーソル位置。</item>
    /// <item>前を検索: 選択範囲が直前の一致と同じなら「一致の開始 − 1」から後方 (start = 一致の開始)、そうでなければ
    /// 「カーソル位置 − 1」から後方 (start = カーソル位置)。</item>
    /// </list>
    /// </summary>
    public long StartFor(bool forward, long cursor, long selectionStart, long selectionLength)
    {
        if (IsLastMatch(selectionStart, selectionLength) && LastMatch is { } m)
        {
            return forward ? m.Offset + 1 : m.Offset;
        }

        return cursor;
    }

    /// <summary>選択範囲が直前の検索の一致と同じか。</summary>
    public bool IsLastMatch(long selectionStart, long selectionLength) =>
        LastMatch is { } m && selectionLength > 0 && m.Offset == selectionStart && m.Length == selectionLength;

    /// <summary>
    /// 検索の結果を覚える。見つからなかった場合 (null) は前の一致を残す (選択範囲は前の一致のままなので、もう一度探しても
    /// 同じ一致に戻らないようにする)。
    /// </summary>
    public void Remember(SearchHit? hit) => LastMatch = hit ?? LastMatch;

    /// <summary>検索語や条件が変わったときに呼ぶ。</summary>
    public void Reset() => LastMatch = null;

    /// <summary>
    /// 開始位置を決めて検索し、結果を覚える。<paramref name="cancellationToken"/> でキャンセルされた場合は覚えている一致を変えない。
    /// </summary>
    public SearchHit? FindNext(DocumentSnapshot snapshot, SearchPattern pattern, bool forward, bool wrap, long cursor,
        long selectionStart, long selectionLength, SearchOptions? options = null, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        long start = StartFor(forward, cursor, selectionStart, selectionLength);
        SearchHit? hit = SearchEngine.Find(snapshot, pattern, start, forward, wrap, options ?? SearchOptions.Default, operation, cancellationToken);
        Remember(hit);
        return hit;
    }
}
