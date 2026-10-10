using HexEditor.Core.View;

namespace HexEditor.Core.Bookmarks;

/// <summary>選択範囲 1 つ (開始と長さ)。マルチ選択との受け渡しに使う。</summary>
public readonly record struct SelectedRange(long Start, long Length)
{
    public long End => Start + Length;
}

/// <summary>
/// エディタの選択範囲 (マルチ選択・矩形選択を含む。EDIT-07) の取得と設定 (INSP-28)。矩形選択は行ごとの範囲を返す (INSP-28 の仕様 3)。
/// </summary>
public static class MultiSelectionBridge
{
    /// <summary>今の選択範囲 (開始位置の順。長さ 0 の要素は含めない。選択がなければ空)。</summary>
    public static IReadOnlyList<SelectedRange> RangesOf(EditorState editor) =>
        [.. editor.SelectedRanges.Where(r => r.Length > 0).Select(r => new SelectedRange(r.Start, r.Length))];

    /// <summary>選択範囲を置き換える (2 つ以上ならマルチ選択にする)。</summary>
    public static void Select(EditorState editor, IReadOnlyList<SelectedRange> ranges)
    {
        if (ranges.Count == 1)
        {
            editor.Select(ranges[0].Start, ranges[0].Length);
        }
        else if (ranges.Count > 1)
        {
            editor.SetSelections(ranges.Select(r => new Selection.ByteRange(r.Start, r.Length)));
        }
    }
}

/// <summary>ブックマークを選択範囲にした結果。</summary>
public sealed record BookmarkSelectionResult(IReadOnlyList<SelectedRange> Ranges, int Excluded, bool Truncated);

/// <summary>選択範囲をブックマークにした結果。</summary>
public sealed record SelectionBookmarksResult(IReadOnlyList<Bookmark> Added, BookmarkGroup? Group, bool Truncated);

/// <summary>選択範囲とブックマークの相互変換 (INSP-28)。</summary>
public static class BookmarkConversions
{
    /// <summary>マルチ選択の範囲の上限 (INSP-28 の仕様 2)。</summary>
    public const int MaxRanges = 1_000_000;

    /// <summary>長時間処理として扱う件数 (INSP-28 の「巨大ファイル・長時間処理」)。</summary>
    public const int LongRunningThreshold = 100_000;

    /// <summary>
    /// 選択範囲をブックマークにする (INSP-28 の仕様 1): 各範囲を 1 件のブックマークにする (名前は <paramref name="name"/> に 1 から
    /// 数えた番号を渡して作る)。範囲が 2 つ以上なら <paramref name="groupName"/> のグループを作って入れる。上限を超える分は付けない。
    /// </summary>
    public static SelectionBookmarksResult FromRanges(BookmarkCollection bookmarks, IReadOnlyList<SelectedRange> ranges, Func<int, string> name,
        string groupName, BookmarkColor? color = null) =>
        PrepareFromRanges(ranges, name, groupName, color).Commit(bookmarks);

    /// <summary>
    /// 選択範囲をブックマークにする準備 (名前を作る。ブックマークに触れないので別のスレッドで行える。10 万件を超える変換は長時間処理。
    /// INSP-28 の「巨大ファイル・長時間処理」)。<paramref name="progress"/> には処理した件数を渡す。キャンセルは 4,096 件ごとに確かめる。
    /// </summary>
    public static PreparedSelectionBookmarks PrepareFromRanges(IReadOnlyList<SelectedRange> ranges, Func<int, string> name, string groupName,
        BookmarkColor? color = null, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        int count = Math.Min(ranges.Count, BookmarkCollection.MaxCount);
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(i);
            }

            names[i] = name(i + 1);
        }

        progress?.Invoke(count);
        return new PreparedSelectionBookmarks(ranges, names, groupName, color);
    }

    /// <summary>
    /// ブックマークを選択範囲にする (INSP-28 の仕様 2): 長さ 0 のものを除き、開始位置の順に並べる。上限 (1,000,000 範囲) を超える分は除く。
    /// </summary>
    public static BookmarkSelectionResult ToRanges(IEnumerable<Bookmark> bookmarks, long documentLength) =>
        ToRanges([.. bookmarks.Select(b => new SelectedRange(b.Start, b.Length))], documentLength);

    /// <summary>
    /// ブックマークの範囲 (UI スレッドで写したもの) を選択範囲にする。ブックマークに触れないので別のスレッドで行える (10 万件を超える変換は
    /// 長時間処理)。<paramref name="progress"/> には処理した件数を渡す。
    /// </summary>
    public static BookmarkSelectionResult ToRanges(IReadOnlyList<SelectedRange> bookmarkRanges, long documentLength,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        SelectedRange[] sorted = [.. bookmarkRanges];
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(sorted, static (a, b) => a.Start.CompareTo(b.Start));
        int excluded = 0;
        var ranges = new List<SelectedRange>(Math.Min(sorted.Length, MaxRanges + 1));
        for (int i = 0; i < sorted.Length; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(i);
            }

            long start = Math.Min(sorted[i].Start, documentLength);
            long length = Math.Min(sorted[i].Length, documentLength - start);
            if (length <= 0)
            {
                excluded++;
                continue;
            }

            ranges.Add(new SelectedRange(start, length));
        }

        bool truncated = ranges.Count > MaxRanges;
        if (truncated)
        {
            ranges.RemoveRange(MaxRanges, ranges.Count - MaxRanges);
        }

        progress?.Invoke(sorted.Length);
        return new BookmarkSelectionResult(ranges, excluded, truncated);
    }
}

/// <summary>
/// ブックマークにする準備のできた選択範囲 (<see cref="BookmarkConversions.PrepareFromRanges"/>)。<see cref="CommitInSteps"/> は UI スレッドで
/// 少しずつ加える。加え始めたら最後まで進めること。
/// </summary>
public sealed class PreparedSelectionBookmarks
{
    private readonly IReadOnlyList<SelectedRange> _ranges;
    private readonly string[] _names;
    private readonly string _groupName;
    private readonly BookmarkColor? _color;

    internal PreparedSelectionBookmarks(IReadOnlyList<SelectedRange> ranges, string[] names, string groupName, BookmarkColor? color)
    {
        _ranges = ranges;
        _names = names;
        _groupName = groupName;
        _color = color;
    }

    public int Count => _ranges.Count;

    /// <summary>加え終えた結果 (<see cref="CommitInSteps"/> を最後まで進めたら決まる)。</summary>
    public SelectionBookmarksResult? Result { get; private set; }

    public SelectionBookmarksResult Commit(BookmarkCollection bookmarks)
    {
        foreach (int _ in CommitInSteps(bookmarks, int.MaxValue))
        {
        }

        return Result!;
    }

    /// <summary><paramref name="chunk"/> 件ずつ加え、そのたびに加えた件数を返す。通知は最後に 1 回だけ出す。</summary>
    public IEnumerable<int> CommitInSteps(BookmarkCollection bookmarks, int chunk)
    {
        BookmarkGroup? group = _ranges.Count >= 2 ? bookmarks.CreateGroup(null, _groupName) : null;
        var added = new List<Bookmark>(_names.Length);
        bool truncated = _names.Length < _ranges.Count;
        for (int i = 0; i < _names.Length; i++)
        {
            if (i > 0 && i % Math.Max(1, chunk) == 0)
            {
                yield return i;
            }

            if (bookmarks.Count >= BookmarkCollection.MaxCount)
            {
                truncated = true;
                break;
            }

            added.Add(bookmarks.AddQuiet(_ranges[i].Start, _ranges[i].Length, _names[i], _color, group?.Path));
        }

        bookmarks.RaiseAdded(added);
        Result = new SelectionBookmarksResult(added, group, truncated);
        yield return _names.Length;
    }
}
