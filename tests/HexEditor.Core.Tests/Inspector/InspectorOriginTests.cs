using HexEditor.Core.Inspector;
using HexEditor.Core.Selection;
using HexEditor.Core.Tests.Editing;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>解釈の起点 (INSP-01 の仕様 1): マルチ選択では主要素の先頭。</summary>
public sealed class InspectorOriginTests
{
    [Fact]
    public void Origin_is_cursor_without_selection_and_selection_start_with_one()
    {
        (_, EditorState s) = MultiSelectionTests.Create(0x100);
        s.GoTo(0x20);
        Assert.Equal(0x20, DataInspector.OriginOf(s, useCursorWithSelection: false));

        s.Select(0x30, 0x10);
        Assert.Equal(0x30, DataInspector.OriginOf(s, useCursorWithSelection: false));
        Assert.Equal(s.Cursor, DataInspector.OriginOf(s, useCursorWithSelection: true));
    }

    [Fact]
    public void Multi_selection_uses_the_start_of_the_primary_element()
    {
        (_, EditorState s) = MultiSelectionTests.Create(0x100);
        s.SetSelections([new ByteRange(0x10, 2), new ByteRange(0x40, 2)]);
        Assert.Equal(0x40, DataInspector.OriginOf(s, false));

        // Ctrl+クリックで主要素を取り除くと、残った要素の最後が主要素になる (選択の開始位置の値は古いまま)。
        s.BeginAddSelection(0x40, ActiveColumn.Hex);
        Assert.True(s.RemoveSelectionAt(0x40));
        Assert.Equal(s.PrimaryRange!.Value.Start, DataInspector.OriginOf(s, false));
        Assert.Equal(0x10, DataInspector.OriginOf(s, false));
    }

    [Fact]
    public void Primary_element_merged_with_a_neighbor_uses_the_merged_start()
    {
        (_, EditorState s) = MultiSelectionTests.Create(0x100);
        s.SetSelections([new ByteRange(0x10, 0x10), new ByteRange(0x80, 2)]);
        // 0x18 から新しい要素を作り始めて伸ばすと、0x10 の要素と結合する。
        s.BeginAddSelection(0x18, ActiveColumn.Hex);
        s.DragTo(0x28);
        Assert.Equal(0x10, s.PrimaryRange!.Value.Start);
        Assert.Equal(0x10, DataInspector.OriginOf(s, false));
    }
}
