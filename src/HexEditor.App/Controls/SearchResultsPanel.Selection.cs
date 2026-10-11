using HexEditor.App.Services;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Controls;

/// <summary>結果一覧の「変換 &gt; 選択範囲に」(FIND-21 の仕様 2。F2-12): 対象の一致の範囲をマルチ選択にする。</summary>
public sealed partial class SearchResultsPanel
{
    /// <summary>
    /// 一致の範囲を選択にするよう頼む (エディタと、一致の範囲の一覧。一覧はオフセット順でなくてもよい)。範囲は先頭から上限
    /// (マルチ選択の要素数の上限。既定 1,000,000 範囲) までで、それを超える結果があった場合は Truncated が true (受け取る側が InfoBar で知らせる)。
    /// </summary>
    public event EventHandler<(EditorState Editor, IReadOnlyList<ByteRange> Ranges, bool Truncated)>? SelectionRequested;

    /// <summary>対象の一致を選択範囲にする (コマンド・ボタンから。完了を待たない)。</summary>
    internal void ToSelection() => _ = ToSelectionAsync();

    /// <summary>
    /// 対象の一致を選択範囲にする。「開いているすべてのドキュメント」の結果は、表示中のドキュメントの結果だけを変換する (選択はドキュメントごと)。
    /// 一致の位置は、検索の後の編集に合わせて動かした位置を使う。上限を超える結果は数え上げない。結果が多い場合
    /// (<see cref="SearchResultsConversion.SelectionBackgroundThreshold"/> 件超) は長時間処理としてバックグラウンドで変換する。
    /// </summary>
    internal async Task ToSelectionAsync()
    {
        if (_groups.Count == 0 || SelectionRequested is null)
        {
            return;
        }

        // 対象の行 (結果の番号) は少しずつ数え上げる (結果の数に比例したリストを作らない)。
        IEnumerable<long> indices;
        long total;
        if (TargetsSelectedRows)
        {
            (long lo, long hi) = SelectionRange;
            indices = LongSequence(lo, hi + 1).Select(Map);
            total = hi - lo + 1;
        }
        else if (_view is { } view)
        {
            indices = view;
            total = view.LongLength;
        }
        else
        {
            total = TotalCount;
            indices = LongSequence(0, total);
        }

        // 位置の補正の部品は UI スレッドで用意し、変換の間に一覧が変わっても影響を受けないよう、まとまりを写しておく。
        Group[] groups = [.. _groups];
        var factories = groups.ToDictionary(g => g, Factory);
        int limit = groups.Max(g => g.Editor.MaxSelectionElements);

        (Group Key, ByteRange Range)? Locate(long index)
        {
            if (index < 0)
            {
                return null;
            }

            foreach (Group g in groups)
            {
                long count = g.Results.LongCount;
                if (index < count)
                {
                    TrackedMatch t = factories[g].Track(g.Results[index]);
                    return (g, new ByteRange(t.Offset, t.Length));
                }

                index -= count;
            }

            return null;
        }

        SelectionConversion<Group> converted;
        if (total > SearchResultsConversion.SelectionBackgroundThreshold && Operations is not null)
        {
            try
            {
                converted = await Operations.RunAsync(Loc.Get("Operation_ResultsToSelection"), OperationKind.ReadOnly, null, total,
                    op => Task.FromResult(SearchResultsConversion.ToSelectionRanges(indices, Locate, limit, op)));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        else
        {
            converted = SearchResultsConversion.ToSelectionRanges(indices, Locate, limit);
        }

        foreach ((Group g, List<ByteRange> ranges) in converted.Ranges)
        {
            SelectionRequested?.Invoke(this, (g.Editor, ranges, converted.Truncated));
        }
    }

    private static IEnumerable<long> LongSequence(long from, long to)
    {
        for (long i = from; i < to; i++)
        {
            yield return i;
        }
    }

    private void ToSelection_Click(object sender, RoutedEventArgs e) => ToSelection();
}
