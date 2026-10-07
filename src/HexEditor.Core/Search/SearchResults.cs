using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>すべて検索・件数の数え上げで見つかった一致 1 件。</summary>
public readonly record struct SearchMatch(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>すべて検索の状態 (FIND-20 の「画面」の見出し)。</summary>
public enum SearchResultsState
{
    Running,
    Completed,

    /// <summary>キャンセルされた (見つかった分は残る。FIND-02 の仕様 3)。</summary>
    Cancelled,

    /// <summary>件数の上限に達して止めた (FIND-12 の仕様 3、FIND-20 の仕様 6)。</summary>
    LimitReached,

    /// <summary>読めない範囲のため中止した、またはそのほかの失敗。</summary>
    Failed,
}

/// <summary>
/// すべて検索・件数の数え上げの結果 (FIND-12、FIND-20 の基盤)。検索のスレッドが開始オフセットの昇順に追加していき、
/// 追加のたびに <see cref="MatchesAdded"/> を出す (ストリーミング)。読み取りはどのスレッドからでもできる。
/// 一致は検索した <see cref="Snapshot"/> 上の位置で、その後の編集に合わせた位置は <see cref="MatchTracker"/> で求める (FIND-03)。
/// </summary>
public sealed class SearchResults
{
    private readonly object _lock = new();
    private readonly List<SearchMatch> _matches = [];
    private readonly List<UnreadableRange> _skipped = [];

    public SearchResults(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options)
    {
        Snapshot = snapshot;
        Pattern = pattern;
        Options = options;
        TotalBytes = options.Scope.TotalLength(snapshot.Length);
    }

    /// <summary>検索したスナップショット (一致の版。FIND-03 の仕様 2)。</summary>
    public DocumentSnapshot Snapshot { get; }

    public SearchPattern Pattern { get; }

    public SearchOptions Options { get; }

    /// <summary>検索範囲の合計のバイト数。</summary>
    public long TotalBytes { get; }

    public SearchResultsState State { get; private set; } = SearchResultsState.Running;

    /// <summary>上限に達して止めた (「100 万件以上」の表示)。</summary>
    public bool LimitReached => State == SearchResultsState.LimitReached;

    /// <summary>これまでに見つかった件数。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _matches.Count;
            }
        }
    }

    /// <summary>これまでに見つかった一致 (開始オフセットの昇順) の写し。</summary>
    public IReadOnlyList<SearchMatch> Matches
    {
        get
        {
            lock (_lock)
            {
                return [.. _matches];
            }
        }
    }

    /// <summary>飛ばした読めない範囲 (結果一覧の「読み込めなかった範囲」。FIND-01 の「エラー」)。</summary>
    public IReadOnlyList<UnreadableRange> SkippedRanges
    {
        get
        {
            lock (_lock)
            {
                return [.. _skipped];
            }
        }
    }

    /// <summary>一致が追加された (検索のスレッドで呼ぶ)。引数は追加後の件数。</summary>
    public event EventHandler<int>? MatchesAdded;

    /// <summary>状態が変わった (完了・キャンセル・上限・失敗)。</summary>
    public event EventHandler? StateChanged;

    /// <summary><paramref name="index"/> 番目の一致 (0 から)。</summary>
    public SearchMatch this[int index]
    {
        get
        {
            lock (_lock)
            {
                return _matches[index];
            }
        }
    }

    /// <summary>
    /// 開始が <paramref name="offset"/> の一致の番号 (0 から)。なければ −1。「3 / 125」の 3 は、これ + 1 (FIND-12 の仕様 1)。
    /// </summary>
    public int IndexOf(long offset)
    {
        lock (_lock)
        {
            int i = LowerBound(offset);
            return i < _matches.Count && _matches[i].Offset == offset ? i : -1;
        }
    }

    /// <summary>開始が <paramref name="offset"/> 未満の一致の件数。</summary>
    public int CountBefore(long offset)
    {
        lock (_lock)
        {
            return LowerBound(offset);
        }
    }

    /// <summary>[offset, offset + length) と重なる一致 (表示中の範囲の強調用)。</summary>
    public IReadOnlyList<SearchMatch> Overlapping(long offset, long length)
    {
        lock (_lock)
        {
            // 開始が offset − 最長の一致 + 1 以上のものだけが重なりうる。
            int i = LowerBound(offset - Pattern.MaxMatchLength + 1);
            var result = new List<SearchMatch>();
            for (; i < _matches.Count && _matches[i].Offset < offset + length; i++)
            {
                if (_matches[i].End > offset)
                {
                    result.Add(_matches[i]);
                }
            }

            return result;
        }
    }

    internal void Add(List<SearchMatch> matches)
    {
        if (matches.Count == 0)
        {
            return;
        }

        int count;
        lock (_lock)
        {
            _matches.AddRange(matches);
            count = _matches.Count;
        }

        MatchesAdded?.Invoke(this, count);
    }

    internal void AddSkipped(UnreadableRange range)
    {
        lock (_lock)
        {
            if (_skipped.Count > 0 && _skipped[^1].End == range.Offset && _skipped[^1].Reason == range.Reason)
            {
                _skipped[^1] = _skipped[^1] with { Length = _skipped[^1].Length + range.Length };
            }
            else
            {
                _skipped.Add(range);
            }
        }
    }

    internal void SetState(SearchResultsState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private int LowerBound(long offset)
    {
        int lo = 0;
        int hi = _matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_matches[mid].Offset < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
