using HexEditor.Core.Search;
using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Controls;

/// <summary>結果一覧の「変換 &gt; 選択範囲に」(FIND-21 の仕様 2。F2-12): 対象の一致の範囲をマルチ選択にする。</summary>
public sealed partial class SearchResultsPanel
{
    /// <summary>
    /// 一致の範囲を選択にするよう頼む (エディタと、一致の範囲の一覧。一覧はオフセット順でなくてもよい)。上限 (1,000,000 範囲) を超える場合は
    /// 受け取る側が先頭から上限までだけを選ぶ。
    /// </summary>
    public event EventHandler<(EditorState Editor, IReadOnlyList<ByteRange> Ranges)>? SelectionRequested;

    /// <summary>
    /// 対象の一致を選択範囲にする。「開いているすべてのドキュメント」の結果は、表示中のドキュメントの結果だけを変換する (選択はドキュメントごと)。
    /// 一致の位置は、検索の後の編集に合わせて動かした位置を使う。
    /// </summary>
    internal void ToSelection()
    {
        if (_groups.Count == 0 || SelectionRequested is null)
        {
            return;
        }

        IReadOnlyList<long> indices = TargetIndices() ?? LongRange(TotalCount);
        var byEditor = new Dictionary<EditorState, List<ByteRange>>();
        foreach (long i in indices)
        {
            if (LocateResult(i) is not (Group g, long local))
            {
                continue;
            }

            TrackedMatch t = Factory(g).Track(g.Results[local]);
            if (t.Length <= 0)
            {
                continue;
            }

            if (!byEditor.TryGetValue(g.Editor, out List<ByteRange>? ranges))
            {
                byEditor[g.Editor] = ranges = [];
            }

            ranges.Add(new ByteRange(t.Offset, t.Length));
        }

        foreach ((EditorState editor, List<ByteRange> ranges) in byEditor)
        {
            SelectionRequested.Invoke(this, (editor, ranges));
        }
    }

    private void ToSelection_Click(object sender, RoutedEventArgs e) => ToSelection();
}
