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
        string groupName, BookmarkColor? color = null)
    {
        BookmarkGroup? group = ranges.Count >= 2 ? bookmarks.CreateGroup(null, groupName) : null;
        var added = new List<Bookmark>(ranges.Count);
        bool truncated = false;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (bookmarks.Count >= BookmarkCollection.MaxCount)
            {
                truncated = true;
                break;
            }

            Bookmark b = bookmarks.AddQuiet(ranges[i].Start, ranges[i].Length, name(i + 1), color, group?.Path);
            added.Add(b);
        }

        bookmarks.RaiseAdded(added);
        return new SelectionBookmarksResult(added, group, truncated);
    }

    /// <summary>
    /// ブックマークを選択範囲にする (INSP-28 の仕様 2): 長さ 0 のものを除き、開始位置の順に並べる。上限 (1,000,000 範囲) を超える分は除く。
    /// </summary>
    public static BookmarkSelectionResult ToRanges(IEnumerable<Bookmark> bookmarks, long documentLength)
    {
        int excluded = 0;
        var ranges = new List<SelectedRange>();
        foreach (Bookmark b in bookmarks.OrderBy(b => b.Start))
        {
            long start = Math.Min(b.Start, documentLength);
            long length = Math.Min(b.Length, documentLength - start);
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

        return new BookmarkSelectionResult(ranges, excluded, truncated);
    }
}
