using HexEditor.Core.Selection;

namespace HexEditor.Core.View;

/// <summary>ファイルの断片の選択 (FOR-09 の仕様 5、FOR-15 の仕様 4)。</summary>
public sealed partial class EditorState
{
    /// <summary>
    /// 断片をすべてマルチ選択 (EDIT-07) として選び、カーソルを最初の断片の先頭に置いてそこへジャンプする (FOR-09 の受け入れ基準 2)。
    /// 断片が 1 つなら通常の選択。
    /// </summary>
    public SelectionResult SelectFragments(IEnumerable<ByteRange> fragments)
    {
        List<ByteRange> list = [.. fragments.Where(f => f.Length > 0).OrderBy(f => f.Start)];
        if (list.Count == 0)
        {
            return SelectionResult.NoSelection;
        }

        RecordJump();
        SelectionResult result = SetSelections(list);
        if (result is not (SelectionResult.Done or SelectionResult.Truncated))
        {
            return result;
        }

        // 主要素を最初の断片にする (SetSelections は最後の要素を主要素にする)。
        CommitSelectionCore();
        if (_others is { Count: > 0 } others && PrimaryRange is { } primary && others.First.Start < primary.Start)
        {
            ByteRange first = others.First;
            others.Remove(first);
            others.Add(primary);
            SetPrimaryElement(first, keepOthers: true);
        }

        // カーソルは主要素の先頭 (アンカーは末尾)。
        _anchor = _selectionStart + _selectionLength;
        _cursor = _selectionStart;
        LowNibble = false;
        ScrollToJumpRange(_selectionStart, Math.Min(_selectionLength, Layout.BytesPerRow), JumpPlacement);
        RaiseChanged();
        return result;
    }
}
