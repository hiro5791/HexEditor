using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>ドキュメント上の範囲 [Offset, Offset + Length)。</summary>
public readonly record struct SearchRange(long Offset, long Length)
{
    public long End => Offset + Length;

    /// <summary>開始と終了 (このバイトを含む) から作る (FIND-11 の「オフセット範囲」と同じ数え方)。</summary>
    public static SearchRange FromInclusive(long first, long last) => new(first, last - first + 1);
}

/// <summary>
/// 検索範囲 (FIND-11)。ドキュメント全体、または 1 つ以上の範囲 (選択範囲・マルチ選択の各要素)。一致は範囲の 1 つにすっかり
/// 収まるものだけを報告し、範囲の端をまたぐ一致は報告しない (仕様 2)。折り返しも範囲の中で行う (仕様 4)。
/// </summary>
public sealed class SearchScope
{
    private readonly SearchRange[]? _ranges;

    private SearchScope(SearchRange[]? ranges) => _ranges = ranges;

    /// <summary>ドキュメント全体 (既定)。</summary>
    public static SearchScope WholeDocument { get; } = new(null);

    /// <summary>ドキュメント全体か。</summary>
    public bool IsWholeDocument => _ranges is null;

    /// <summary>指定した範囲 (ドキュメント全体なら空)。</summary>
    public IReadOnlyList<SearchRange> Ranges => _ranges ?? [];

    /// <summary>1 つの範囲 (選択範囲)。</summary>
    public static SearchScope Of(long offset, long length) => Of([new SearchRange(offset, length)]);

    /// <summary>
    /// いくつかの範囲 (マルチ選択)。開始の順に並べ、重なる範囲は 1 つにまとめる。接しているだけの範囲はまとめない
    /// (一致は要素ごとに収まる必要がある)。
    /// </summary>
    public static SearchScope Of(IEnumerable<SearchRange> ranges)
    {
        var sorted = ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset).ToList();
        if (sorted.Any(r => r.Offset < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(ranges), "範囲の開始が負です。");
        }

        var merged = new List<SearchRange>(sorted.Count);
        foreach (SearchRange r in sorted)
        {
            if (merged.Count > 0 && r.Offset < merged[^1].End)
            {
                SearchRange last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.End, r.End) - last.Offset };
            }
            else
            {
                merged.Add(r);
            }
        }

        return new SearchScope([.. merged]);
    }

    /// <summary>長さ <paramref name="documentLength"/> のドキュメントでの実際の範囲 (末尾で切り詰め、空の範囲は除く)。</summary>
    public IReadOnlyList<SearchRange> Resolve(long documentLength)
    {
        if (_ranges is null)
        {
            return documentLength > 0 ? [new SearchRange(0, documentLength)] : [];
        }

        var result = new List<SearchRange>(_ranges.Length);
        foreach (SearchRange r in _ranges)
        {
            long end = Math.Min(r.End, documentLength);
            if (end > r.Offset)
            {
                result.Add(new SearchRange(r.Offset, end - r.Offset));
            }
        }

        return result;
    }

    /// <summary>範囲の合計のバイト数 (進捗の全体量。FIND-12 の 1 GiB の判定)。</summary>
    public long TotalLength(long documentLength) => Resolve(documentLength).Sum(r => r.Length);

    /// <summary>
    /// [start, start + length) と重なる部分だけの範囲 (インクリメンタルサーチの「起点から前方 256 MB」。FIND-27 の仕様 6)。
    /// <paramref name="truncated"/> は、切った先にも範囲が残っているか (「Enter で続きを検索」を出すかどうか)。
    /// </summary>
    public SearchScope Clip(long start, long length, long documentLength, out bool truncated)
    {
        long end = length >= long.MaxValue - start ? long.MaxValue : start + length;
        var clipped = new List<SearchRange>();
        truncated = false;
        foreach (SearchRange r in Resolve(documentLength))
        {
            long lo = Math.Max(r.Offset, start);
            long hi = Math.Min(r.End, end);
            if (hi > lo)
            {
                clipped.Add(new SearchRange(lo, hi - lo));
            }

            truncated |= r.End > end;
        }

        // 範囲が空になった場合も「ドキュメント全体」に戻らないよう、長さ 0 の範囲 1 つにする。
        return clipped.Count == 0 ? new SearchScope([]) : new SearchScope([.. clipped]);
    }

    /// <summary>インクリメンタルサーチで 1 回に探す長さ (起点から前方 256 MB。FIND-27 の仕様 6)。</summary>
    public const long IncrementalWindow = 256L * 1024 * 1024;
}

/// <summary>読めない範囲に出会ったときの動作 (FIND-01 の「エラー」)。</summary>
public enum UnreadableAction
{
    /// <summary>読めない範囲を飛ばして続ける。読めないバイトを含む一致は報告しない。</summary>
    Skip,

    /// <summary>検索を中止する (<see cref="SearchAbortedException"/>)。</summary>
    Abort,
}

/// <summary>読めない範囲のために検索を中止した (FIND-01 の「エラー」で「中止する」を選んだ)。</summary>
public sealed class SearchAbortedException(UnreadableRange range)
    : IOException($"読めない範囲があるため検索を中止しました: 0x{range.Offset:X}〜 ({range.Length} バイト, {range.Reason})")
{
    /// <summary>中止のきっかけになった読めない範囲 (ドキュメント上の位置)。</summary>
    public UnreadableRange Range { get; } = range;
}

/// <summary>検索の設定 (範囲、チャンク、並列数、読めない範囲の扱い)。</summary>
public sealed record SearchOptions
{
    public static SearchOptions Default { get; } = new();

    /// <summary>検索範囲 (FIND-11)。</summary>
    public SearchScope Scope { get; init; } = SearchScope.WholeDocument;

    /// <summary>チャンクのサイズ (FIND-01 の仕様 2。設定の範囲 256 KiB〜64 MiB の検証は設定画面で行う)。</summary>
    public int ChunkSize { get; init; } = SearchEngine.DefaultChunkSize;

    /// <summary>すべて検索・件数の数え上げの並列数 (FIND-01 の仕様 7)。</summary>
    public int MaxDegreeOfParallelism { get; init; } = SearchEngine.DefaultParallelism;

    /// <summary>
    /// 読めない範囲に出会ったときに呼ぶ (検索のスレッドから、同時に 1 つずつ)。同じバイトについては 1 回だけ呼ぶ。
    /// null なら <see cref="UnreadableAction.Skip"/>。UI の「確認する」は、ここで InfoBar の応答を待つ。
    /// </summary>
    public Func<UnreadableRange, UnreadableAction>? OnUnreadable { get; init; }

    /// <summary>
    /// 正規表現の照合がチャンクの時間の上限に達したときに呼ぶ (FIND-18 の「エラー」。検索のスレッドから)。<see cref="UnreadableAction.Skip"/> なら
    /// そのチャンクを飛ばして続け (結果一覧に記録する)、<see cref="UnreadableAction.Abort"/> なら <see cref="SearchTimedOutException"/> で中止する。
    /// null なら飛ばす。
    /// </summary>
    public Func<SearchRange, UnreadableAction>? OnTimeout { get; init; }

    /// <summary>すべて検索で重なる一致を含める (FIND-20 の仕様 5。既定オフ)。次 / 前を検索と件数の数え上げは常に重なる一致を含める。</summary>
    public bool IncludeOverlapping { get; init; }

    /// <summary>すべて検索で集める件数の上限 (FIND-20 の仕様 6)。超えたら止めて <see cref="SearchResults.LimitReached"/> にする。</summary>
    public long MaxMatches { get; init; } = long.MaxValue;
}
