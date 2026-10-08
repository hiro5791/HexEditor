using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>
/// カーソル移動の設定と表示位置 (VIEW-26、VIEW-34)、キーボードによる選択 (EDIT-02)、移動バーの入力式 (VIEW-29)。
/// UI テストのうち、ビューの状態 (<see cref="EditorState"/>) だけで確かめられる手順をここで確かめる。
/// </summary>
public sealed class CursorPlacementTests
{
    private const int Visible = 30;

    /// <summary>TD-SEQ-1M と同じ内容 (オフセット n の値は n mod 256) の、長さを変えられるドキュメント。</summary>
    private static EditorState Seq1M()
    {
        byte[] data = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, data);
        var doc = new Document(new MemoryByteSource(data), Options());
        return new EditorState(doc) { VisibleRows = Visible };
    }

    private static long Row(EditorState e) => e.Cursor / 16 - e.TopRow;

    [Fact]
    [Trait(TC, "TC-VIEW-26-01")]
    public void Right_and_left_move_by_nibble_when_enabled()
    {
        EditorState e = Seq1M();
        e.NibbleArrowKeys = true;

        e.MoveRight();
        Assert.Equal((0, true), (e.Cursor, e.LowNibble));
        e.MoveRight();
        Assert.Equal((1, false), (e.Cursor, e.LowNibble));

        // ← を 3 回: 0 の下位、0 の上位、そこで止まる。
        e.MoveLeft();
        Assert.Equal((0, true), (e.Cursor, e.LowNibble));
        e.MoveLeft();
        e.MoveLeft();
        Assert.Equal((0, false), (e.Cursor, e.LowNibble));
    }

    [Fact]
    [Trait(TC, "TC-VIEW-26-01")]
    public void Nibble_moves_stop_at_the_end_and_skip_the_text_column()
    {
        EditorState e = Seq1M();
        e.NibbleArrowKeys = true;

        // 末尾位置 (バイトがない) には下位ニブルがないため、→ で動かない。
        e.MoveToEnd();
        e.MoveRight();
        Assert.Equal((TestDataCatalog.MiB, false), (e.Cursor, e.LowNibble));

        // テキスト列と Shift+→ はバイト単位のまま (VIEW-26 の仕様 7、EDIT-02 の仕様 4)。
        e.GoTo(0x10);
        e.ToggleColumn();
        e.MoveRight();
        Assert.Equal(0x11, e.Cursor);
        e.ToggleColumn();
        e.MoveRight(extend: true);
        Assert.Equal((0x12, 0x11L, 1L), (e.Cursor, e.SelectionStart, e.SelectionLength));
    }

    [Fact]
    [Trait(TC, "TC-VIEW-34-01")]
    public void Jump_out_of_view_places_the_target_by_the_setting()
    {
        EditorState e = Seq1M();
        e.GoTo(0x80000);
        Assert.Equal(Visible / 3, Row(e));

        e.JumpPlacement = JumpPlacement.Center;
        e.GoTo(0x40000);
        Assert.Equal(Visible / 2, Row(e));

        e.JumpPlacement = JumpPlacement.Top;
        e.GoTo(0x20000);
        Assert.Equal(0, Row(e));
    }

    [Fact]
    [Trait(TC, "TC-VIEW-34-02")]
    public void Move_within_the_view_does_not_scroll()
    {
        EditorState e = Seq1M();
        long lastVisibleRow = Visible - 1;
        e.GoTo(lastVisibleRow * 16);
        Assert.Equal(0, e.TopRow);

        // ↓ は最小のスクロール (1 行)、↑ ではスクロールしない。
        e.MoveDown();
        Assert.Equal(1, e.TopRow);
        e.MoveUp();
        Assert.Equal(1, e.TopRow);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-34-03")]
    public void Cursor_margin_keeps_rows_below_the_cursor()
    {
        EditorState e = Seq1M();
        e.CursorMargin = 3;
        for (int i = 1; i <= Visible + 5; i++)
        {
            e.MoveDown();
            long cursorRow = e.Cursor / 16;
            if (cursorRow <= Visible - 4)
            {
                Assert.Equal(0, e.TopRow);
            }
            else
            {
                // カーソルの行の下に常に 3 行が表示される。
                Assert.Equal(cursorRow - (Visible - 4), e.TopRow);
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-02-01")]
    public void Shift_arrows_select_and_keep_the_anchor()
    {
        EditorState e = Seq1M();
        e.GoTo(0x10);

        for (int i = 0; i < 4; i++)
        {
            e.MoveRight(extend: true);
        }

        Assert.Equal((0x10L, 4L, 0x14L), (e.SelectionStart, e.SelectionLength, e.Cursor));

        e.MoveLeft(extend: true);
        e.MoveLeft(extend: true);
        Assert.Equal((0x10L, 2L, 0x12L), (e.SelectionStart, e.SelectionLength, e.Cursor));

        // → (Shift なし) は選択を解除し、選択範囲の末尾 (直後の位置) に移る。
        e.MoveRight();
        Assert.Equal((0L, 0x12L), (e.SelectionLength, e.Cursor));

        // 0x20 の下位ニブルをクリックして Shift+→: 0x20 の 1 バイトを選び、カーソルは 0x21 の上位ニブル。
        e.Click(0x20, ActiveColumn.Hex, lowNibble: true, extend: false);
        e.MoveRight(extend: true);
        Assert.Equal((0x20L, 1L, 0x21L, false), (e.SelectionStart, e.SelectionLength, e.Cursor, e.LowNibble));
    }

    [Fact]
    [Trait(TC, "TC-VIEW-29-03")]
    public void Selection_end_and_last()
    {
        EditorState e = Seq1M();
        e.Select(0x10, 16);
        GoToResult end = GoToResolver.Resolve("sel.end", GoToBase.Auto, GoToUnit.Bytes, e);
        e.GoTo(end.Offset);
        Assert.Equal(0x20, e.Cursor);

        e.Select(0x10, 16);
        GoToResult last = GoToResolver.Resolve("sel.last", GoToBase.Auto, GoToUnit.Bytes, e);
        e.GoTo(last.Offset);
        Assert.Equal(0x1F, e.Cursor);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-29-06")]
    public void Sector_and_row_units()
    {
        EditorState e = Seq1M();
        GoToResult sectors = GoToResolver.Resolve("8", GoToBase.Auto, GoToUnit.Sectors, e);
        Assert.True(sectors.IsValid);
        e.GoTo(sectors.Offset);
        Assert.Equal(0x1000, e.Cursor);

        GoToResult rows = GoToResolver.Resolve("8", GoToBase.Auto, GoToUnit.Rows, e);
        e.GoTo(rows.Offset);
        Assert.Equal(0x80, e.Cursor);
    }

    [Fact]
    public void Jump_to_a_range_shows_the_whole_range_when_it_fits()
    {
        // VIEW-34 の仕様 3: 範囲全体が入るならそうする。入らない場合は範囲の先頭を規則どおりに置く。
        EditorState e = Seq1M();

        // 表示外の 20 行の範囲: 先頭を中央に置くと末尾がはみ出すので、末尾が一番下に来るまで戻す。
        e.SelectMatch(0x10000, 20 * 16);
        long first = 0x10000 / 16;
        Assert.True(e.TopRow <= first && first + 19 < e.TopRow + Visible, $"top {e.TopRow}");
        Assert.Equal(first + 19 - Visible + 1, e.TopRow);

        // 表示内の範囲ではスクロールしない。
        long top = e.TopRow;
        e.SelectMatch(0x10000 + 16, 16);
        Assert.Equal(top, e.TopRow);

        // 小さい範囲は中央に置く (FIND-04 の仕様 8)。
        e.SelectMatch(0x40000, 4);
        Assert.Equal(0x40000 / 16 - Visible / 2, e.TopRow);

        // 表示領域より長い範囲は、先頭を規則どおりに置く。
        e.SelectMatch(0x80000, 100 * 16);
        Assert.Equal(0x80000 / 16 - Visible / 2, e.TopRow);
    }
}
