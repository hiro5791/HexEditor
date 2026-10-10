using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>セルの表示形式とテキスト列の数によるカーソル移動 (VIEW-25、VIEW-27)。ビューの状態だけで確かめられる手順。</summary>
public sealed class CellFormatNavigationTests
{
    private static EditorState Seq1M()
    {
        byte[] data = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, data);
        var doc = new Document(new MemoryByteSource(data), Options());
        return new EditorState(doc) { VisibleRows = 30 };
    }

    [Fact]
    [Trait(TC, "TC-VIEW-25-05")]
    public void Arrow_keys_move_by_four_bytes_in_the_32_bit_format_and_by_one_byte_in_text()
    {
        EditorState e = Seq1M();
        e.ApplyView(e.View with { CellFormat = CellFormat.Int32Hex });

        // 1. → を 3 回で 0x0C。2. ← を 1 回で 0x08。
        for (int i = 0; i < 3; i++)
        {
            e.MoveRight();
        }

        Assert.Equal(0x0C, e.Cursor);
        e.MoveLeft();
        Assert.Equal(0x08, e.Cursor);

        // 3. テキスト列は 1 バイト単位。
        e.ToggleColumn();
        Assert.Equal(ActiveColumn.Text, e.ActiveColumn);
        e.MoveRight();
        Assert.Equal(0x09, e.Cursor);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-27-03")]
    public void Tab_and_shift_tab_cycle_through_two_text_columns()
    {
        EditorState e = Seq1M();
        e.ApplyView(e.View with { ExtraTextColumns = TextColumnSpec.Serialize([new TextColumnSpec("utf-8")]) });
        Assert.Equal(2, e.View.TextColumnCount);

        // 1. Tab を 3 回: テキスト 1 → テキスト 2 → Hex。
        var forward = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            e.ToggleColumn();
            forward.Add(e.ActiveColumn == ActiveColumn.Hex ? "hex" : "text" + (e.TextColumn + 1));
        }

        Assert.Equal(["text1", "text2", "hex"], forward);

        // 2. Shift+Tab を 3 回: テキスト 2 → テキスト 1 → Hex。
        var backward = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            e.ToggleColumn(backward: true);
            backward.Add(e.ActiveColumn == ActiveColumn.Hex ? "hex" : "text" + (e.TextColumn + 1));
        }

        Assert.Equal(["text2", "text1", "hex"], backward);
    }
}
