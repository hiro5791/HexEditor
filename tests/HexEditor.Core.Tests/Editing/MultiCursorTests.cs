using HexEditor.Core.Engine;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>マルチカーソル (EDIT-08) と矩形範囲の編集 (EDIT-17)。</summary>
public sealed class MultiCursorTests
{
    private static byte[] Read(Document doc, long offset, int length)
    {
        byte[] buffer = new byte[length];
        doc.Current.Read(offset, buffer);
        return buffer;
    }

    [Fact]
    public void Typing_into_four_vertical_carets_is_one_undo()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(SelectionResult.Done, s.AddCaretBelow());
            }

            Assert.Equal(4, s.CaretCount);
            Assert.Equal(0x40, s.Cursor);
            Assert.Equal(EditResult.Done, s.TypeHexDigit('F'));
            Assert.Equal(EditResult.Done, s.TypeHexDigit('F'));
            Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], new[] { 0x10, 0x20, 0x30, 0x40 }.Select(o => Read(doc, o, 1)[0]));
            Assert.Equal(0x11, Read(doc, 0x11, 1)[0]);
            Assert.Equal([0x11L, 0x21, 0x31, 0x41], s.Carets.Select(c => c.Offset));

            s.Undo();
            Assert.Equal([0x10, 0x20, 0x30, 0x40], new[] { 0x10, 0x20, 0x30, 0x40 }.Select(o => (int)Read(doc, o, 1)[0]));
            Assert.False(doc.History.CanUndo);
        }
    }

    [Fact]
    public void Insert_mode_typing_shifts_later_carets()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            Assert.Equal(SelectionResult.Done, s.ToggleCaretAt(0x20, ActiveColumn.Hex));
            s.ToggleInsertMode();
            s.TypeHexDigit('A');
            s.TypeHexDigit('B');
            Assert.Equal(0x102, doc.Length);
            Assert.Equal(new byte[] { 0x0F, 0xAB, 0x10, 0x11 }, Read(doc, 0x0F, 4));
            Assert.Equal(new byte[] { 0x1F, 0xAB, 0x20, 0x21, 0x22 }, Read(doc, 0x20, 5));
            Assert.Equal([0x11L, 0x22], s.Carets.Select(c => c.Offset));
        }
    }

    [Fact]
    public void Alt_click_on_existing_caret_removes_it_but_not_the_last()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.Click(0x34, ActiveColumn.Hex, false, false);
            s.ToggleCaretAt(0x50, ActiveColumn.Hex);
            s.ToggleCaretAt(0x60, ActiveColumn.Hex);
            Assert.Equal(3, s.CaretCount);
            s.ToggleCaretAt(0x50, ActiveColumn.Hex);
            Assert.Equal(2, s.CaretCount);

            // Esc は主カーソル (最後に追加した 0x60) だけを残す。
            s.ClearSelection();
            Assert.Equal(1, s.CaretCount);
            Assert.Equal(0x60, s.Cursor);
            s.ToggleCaretAt(0x60, ActiveColumn.Hex);
            Assert.Equal(1, s.CaretCount);
        }
    }

    [Fact]
    public void Carets_merge_when_they_meet()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            s.AddCaret(0x11);
            s.MoveLeft();
            Assert.Equal(2, s.CaretCount);
            s.MoveToStart();
            Assert.Equal(1, s.CaretCount);
            s.AddCaret(1);
            s.MoveLeft();
            s.MoveLeft();
            Assert.Equal(1, s.CaretCount);
        }
    }

    [Fact]
    public void Shift_moves_give_each_caret_its_own_selection_and_delete_removes_all()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            s.AddCaret(0x30);
            s.MoveRight(extend: true);
            s.MoveRight(extend: true);
            Assert.All(s.Carets, c => Assert.Equal(2, c.Selection.Length));
            s.Delete();
            Assert.Equal(0x100 - 4, doc.Length);
            Assert.Equal(new byte[] { 0x0F, 0x12 }, Read(doc, 0x0F, 2));
            Assert.Equal(new byte[] { 0x2F, 0x32 }, Read(doc, 0x2D, 2));
            s.Undo();
            Assert.Equal(0x100, doc.Length);
        }
    }

    [Fact]
    public void Typing_into_a_multi_selection_overwrites_each_element()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.SetSelections([new ByteRange(0x10, 2), new ByteRange(0x40, 2)]);
            s.TypeHexDigit('E');
            s.TypeHexDigit('E');
            Assert.Equal(new byte[] { 0xEE, 0x11 }, Read(doc, 0x10, 2));
            Assert.Equal(new byte[] { 0xEE, 0x41 }, Read(doc, 0x40, 2));
            Assert.Equal(2, s.CaretCount);
        }
    }

    [Fact]
    public void Pasting_into_a_multi_selection_pastes_into_each_element()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.SetSelections([new ByteRange(0x10, 2), new ByteRange(0x40, 2)]);
            s.ForEachSelectionElement(i => s.Paste([(byte)(0xA0 + i)], overwrite: true), "上書き貼り付け");
            Assert.Equal(new byte[] { 0xA0, 0x11 }, Read(doc, 0x10, 2));
            Assert.Equal(new byte[] { 0xA1, 0x41 }, Read(doc, 0x40, 2));
            s.Undo();
            Assert.Equal(new byte[] { 0x10, 0x40 }, new[] { Read(doc, 0x10, 1)[0], Read(doc, 0x40, 1)[0] });
        }
    }

    // ---- 矩形の編集 (EDIT-17) ----

    [Fact]
    public void Deleting_a_rectangle_removes_each_row_part()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            // TC-EDIT-17-01 の Core 版: 0x04 から 0x35 (4 行 × 2 バイト)。
            s.BeginRectangle(0x04, ActiveColumn.Hex);
            s.RectangleTo(0x35);
            Assert.Equal(8, s.SelectedByteCount);
            Assert.Equal(EditResult.Done, s.Delete());
            Assert.Equal(0x100 - 8, doc.Length);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 6, 7 }, Read(doc, 0, 6));
            s.Undo();
            Assert.Equal(0x100, doc.Length);
        }
    }

    [Fact]
    public void Rectangle_paste_overwrites_rows_under_the_cursor()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            byte[][] rows = [[0x04, 0x05], [0x14, 0x15], [0x24, 0x25], [0x34, 0x35]];
            s.ToggleInsertMode();
            s.Click(0x48, ActiveColumn.Hex, false, false);
            Assert.Equal(EditResult.Done, s.PasteRectangle(rows, overwrite: true));
            Assert.Equal(0x100, doc.Length);
            Assert.Equal(new byte[] { 0x04, 0x05, 0x4A }, Read(doc, 0x48, 3));
            Assert.Equal(new byte[] { 0x34, 0x35, 0x7A }, Read(doc, 0x78, 3));
            s.Undo();
            Assert.Equal(new byte[] { 0x48, 0x49 }, Read(doc, 0x48, 2));
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-17-04")]
    public void Rectangle_row_limit_boundary()
    {
        var source = new FakeByteSource(new byte[1], SourceCapabilities.CanWrite | SourceCapabilities.CanResize);
        var doc = new Document(source, Options());
        using (doc)
        {
            // 長さ 2^31 の 00 (塗りつぶしピース 1 つ)。
            doc.InsertPattern(1, (1L << 31) - 1, [0]);
            doc.History.BreakCoalescing();
            var s = new EditorState(doc, 16) { VisibleRows = 10 };

            // 1. 1,000,000 行 × 列 0〜1 の矩形の削除は実行され、2,000,000 バイト減る。
            s.SelectRectangle(0, 999_999L * 16 + 1);
            Assert.Equal(1_000_000, s.SelectedRangeCount);
            Assert.Equal(EditResult.Done, s.Delete());
            Assert.Equal((1L << 31) - 2_000_000, doc.Length);
            s.Undo();
            Assert.Equal(1L << 31, doc.Length);

            // 2. 1,000,001 行は実行されず、上限を超えた旨のエラー。
            s.SelectRectangle(0, 1_000_000L * 16 + 1);
            Assert.Equal(EditResult.TooManyRows, s.Delete());
            Assert.Equal(1L << 31, doc.Length);

            // 3. 上限を 10,000,000 にすると実行される。
            s.MaxRectangleRows = 10_000_000;
            Assert.Equal(EditResult.Done, s.Delete());
            Assert.Equal((1L << 31) - 2_000_002, doc.Length);
        }

        // 4. 上限の設定に 10,000,001 は入れられない。
        Core.Settings.SettingDefinition definition = Core.Settings.BuiltInSettings.All.First(d => d.Key == "edit.rectangle.maxRows");
        Assert.Equal(10_000_000, definition.Max);
        Assert.False(definition.Validate(System.Text.Json.Nodes.JsonValue.Create(10_000_001)));
        Assert.True(definition.Validate(System.Text.Json.Nodes.JsonValue.Create(10_000_000)));
    }

    // ---- 選択範囲のドラッグ & ドロップ (EDIT-18) ----

    [Fact]
    public void Dropping_the_selection_moves_copies_or_overwrites()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            // 受け入れ基準 1: 0x00〜0x0F を 0x40 へ移動し、Ctrl+Z 1 回で戻る。
            s.Select(0, 0x10);
            Assert.False(s.CanDropSelectionAt(0x08, SelectionDropKind.Move));
            Assert.Equal(EditResult.Done, s.DropSelection(0x40, SelectionDropKind.Move));
            Assert.Equal(0x100, doc.Length);
            Assert.Equal(new byte[] { 0x10, 0x11 }, Read(doc, 0, 2));
            Assert.Equal(new byte[] { 0x3F, 0x00 }, Read(doc, 0x2F, 2));
            Assert.Equal(new byte[] { 0x0F, 0x40 }, Read(doc, 0x3F, 2));
            Assert.Equal((0x30L, 0x10L), (s.SelectionStart, s.SelectionLength));
            s.Undo();
            Assert.Equal(Enumerable.Range(0, 0x50).Select(i => (byte)i), Read(doc, 0, 0x50));
            Assert.False(doc.History.CanUndo);

            // 受け入れ基準 2: Ctrl でコピー。
            s.Select(0, 0x10);
            Assert.Equal(EditResult.Done, s.DropSelection(0x40, SelectionDropKind.Copy));
            Assert.Equal(0x110, doc.Length);
            Assert.Equal(new byte[] { 0x00, 0x0F, 0x40 }, new[] { Read(doc, 0x40, 1)[0], Read(doc, 0x4F, 1)[0], Read(doc, 0x50, 1)[0] });
            s.Undo();

            // 上書きモードで Shift: ドロップ位置から上書き。
            s.Select(0, 4);
            Assert.Equal(EditResult.Done, s.DropSelection(0x80, SelectionDropKind.Overwrite));
            Assert.Equal(0x100, doc.Length);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 0x84 }, Read(doc, 0x80, 5));
        }
    }

    [Fact]
    public void Copy_as_writes_multiple_ranges_concatenated_or_separately()
    {
        byte[] data = [.. Enumerable.Range(0, 0x40).Select(i => (byte)i)];
        Core.Clipboard.ByteReader read = (o, d) => data.AsSpan((int)o, d.Length).CopyTo(d);
        ByteRange[] ranges = [new(0x10, 2), new(0x20, 2)];
        var options = new Core.Clipboard.CopyOptions();
        string Write(Core.Clipboard.CopyFormat format, Core.Clipboard.CopyRangesMode mode)
        {
            var writer = new StringWriter();
            Core.Clipboard.CopyFormatter.WriteRanges(format, options, read, ranges, mode, writer);
            return writer.ToString();
        }

        Assert.Equal("10 11 20 21", Write(Core.Clipboard.CopyFormat.HexSpaced, Core.Clipboard.CopyRangesMode.Concatenate));
        Assert.Equal("10 11\r\n\r\n20 21", Write(Core.Clipboard.CopyFormat.HexSpaced, Core.Clipboard.CopyRangesMode.Separate));
        Assert.Equal("10 11\r\n20 21", Write(Core.Clipboard.CopyFormat.HexSpaced, Core.Clipboard.CopyRangesMode.Rows));
        string c = Write(Core.Clipboard.CopyFormat.ArrayC, Core.Clipboard.CopyRangesMode.Separate);
        Assert.Contains("data_0[2]", c);
        Assert.Contains("data_1[2]", c);
        Assert.StartsWith("[", Write(Core.Clipboard.CopyFormat.Json, Core.Clipboard.CopyRangesMode.Separate));
    }
}
