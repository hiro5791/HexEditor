using HexEditor.Core.Engine;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>
/// 要素数の多いマルチ選択・矩形の操作を長時間処理に回す判断と、そのための「作る」「入れる」の分割 (EDIT-07・EDIT-17 の「巨大ファイル・長時間処理」)、
/// 選択セットの名前の変更 (EDIT-09)。
/// </summary>
public sealed class LongSelectionOperationTests
{
    private static byte[] Read(Document doc, long offset, int length)
    {
        byte[] buffer = new byte[length];
        doc.Current.Read(offset, buffer);
        return buffer;
    }

    /// <summary>1 バイトおきに <paramref name="count"/> 個の 1 バイトの要素。</summary>
    private static IEnumerable<ByteRange> Every2(int count) => Enumerable.Range(0, count).Select(i => new ByteRange(i * 2L, 1));

    [Fact]
    public void Delete_on_more_than_10000_elements_is_routed_to_a_long_operation()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(30_000);
        using (doc)
        {
            // 10,000 個まではその場で行う。
            Assert.Equal(SelectionResult.Done, s.SetSelections(Every2(EditorState.LongRunningElements)));
            Assert.Equal(RangeDeleteAction.None, s.LongRangeDeleteAction(backspace: false));

            // 10,001 個は長時間処理の削除 (Delete・Backspace とも)。
            Assert.Equal(SelectionResult.Done, s.SetSelections(Every2(EditorState.LongRunningElements + 1)));
            Assert.Equal(RangeDeleteAction.Delete, s.LongRangeDeleteAction(backspace: false));
            Assert.Equal(RangeDeleteAction.Delete, s.LongRangeDeleteAction(backspace: true));

            // 上書きモードで「長さを変えない」設定の Delete は 00 で塗りつぶす (EDIT-13 の仕様 5)。Backspace は削除のまま。
            s.Options = s.Options with { DeleteKeepsLengthInOverwrite = true };
            Assert.Equal(RangeDeleteAction.ZeroFill, s.LongRangeDeleteAction(backspace: false));
            Assert.Equal(RangeDeleteAction.Delete, s.LongRangeDeleteAction(backspace: true));

            // 長時間処理の削除は「作る」と「入れる」に分かれ、結果はその場の削除と同じ (1 回で元に戻る)。
            SelectionSnapshot selection = s.CaptureSelection();
            PreparedReplacement prepared = EditorState.PrepareRangeDeletion(doc, selection);
            s.CommitRangeDeletion(prepared, selection, "削除");
            Assert.Equal(30_000 - 10_001, doc.Length);
            Assert.Equal(new byte[] { 1, 3, 5 }, Read(doc, 0, 3));
            s.Undo();
            Assert.Equal(30_000, doc.Length);
        }
    }

    [Fact]
    public void Delete_on_a_fixed_length_document_stays_synchronous()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(30_000, resizable: false);
        using (doc)
        {
            s.SetSelections(Every2(EditorState.LongRunningElements + 1));

            // 固定長は長さが変わる操作を行わない (その場の処理がすぐ知らせる)。
            Assert.Equal(RangeDeleteAction.None, s.LongRangeDeleteAction(backspace: false));
            Assert.Equal(EditResult.FixedLength, s.Backspace());
        }
    }

    [Fact]
    public void Rectangle_paste_in_insert_mode_is_one_edit_built_at_once()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            byte[][] rows = [[0xA0, 0xA1], [0xB0, 0xB1], [0xC0, 0xC1], [0xD0, 0xD1]];
            s.ToggleInsertMode();
            s.Click(0x48, ActiveColumn.Hex, false, false);
            Assert.True(s.RectanglePasteInserts(overwrite: false));
            Assert.False(s.RectanglePasteInserts(overwrite: true));
            Assert.Equal(0x48, s.RectanglePasteOrigin);

            Assert.Equal(EditResult.Done, s.PasteRectangle(rows, overwrite: false));

            // 各行は元のドキュメントの 0x48、0x58、0x68、0x78 の前に入る (挿入した分だけ後ろの行はずれる)。
            Assert.Equal(0x108, doc.Length);
            Assert.Equal(new byte[] { 0xA0, 0xA1, 0x48 }, Read(doc, 0x48, 3));
            Assert.Equal(new byte[] { 0xB0, 0xB1, 0x58 }, Read(doc, 0x58 + 2, 3));
            Assert.Equal(new byte[] { 0xC0, 0xC1, 0x68 }, Read(doc, 0x68 + 4, 3));
            Assert.Equal(new byte[] { 0xD0, 0xD1, 0x78 }, Read(doc, 0x78 + 6, 3));
            Assert.Equal(0x48, s.Cursor);

            // 1 つの編集なので 1 回で戻る。
            s.Undo();
            Assert.Equal(0x100, doc.Length);
            Assert.False(doc.History.CanUndo);
        }
    }

    [Fact]
    public void Rectangle_rows_insert_prepared_in_the_background_matches_the_synchronous_paste()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            byte[][] rows = [[1], [], [3]];
            s.ToggleInsertMode();
            s.Click(0xF4, ActiveColumn.Hex, false, false);

            // 末尾を越える行 (0x114) は入れない。空の行は飛ばす。
            PreparedReplacement prepared = EditorState.PrepareRectangleRowsInsert(doc, rows, s.RectanglePasteOrigin, s.BytesPerRow);
            s.CommitRectangleRowsInsert(prepared, 0xF4);
            Assert.Equal(0x101, doc.Length);
            Assert.Equal(new byte[] { 1, 0xF4 }, Read(doc, 0xF4, 2));
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-07-03")]
    public void Inverting_a_rectangle_twice_restores_the_rows()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            // 0x04〜0x37 の 4 行 × 4 バイト。反転は 5 要素 (前、行の間 3 つ、後ろ)。
            s.SelectRectangle(0x04, 0x37);
            Assert.Null(s.CheckInvertLimit());
            RangeSet? once = EditorState.ComputeInversion(s.CaptureSelection(), s.MaxSelectionElements);
            Assert.NotNull(once);
            Assert.Equal(
                [new ByteRange(0, 4), new ByteRange(0x08, 12), new ByteRange(0x18, 12), new ByteRange(0x28, 12), new ByteRange(0x38, 0xC8)],
                once!.ToList());
            Assert.Equal(SelectionResult.Done, s.SetSelectionSet(once));
            Assert.Equal(5, s.SelectedRangeCount);

            Assert.Equal(SelectionResult.Done, s.InvertSelection());
            Assert.Equal([new ByteRange(0x04, 4), new ByteRange(0x14, 4), new ByteRange(0x24, 4), new ByteRange(0x34, 4)], s.SelectedRanges.ToList());
        }
    }

    [Fact]
    public void Inverting_a_huge_rectangle_is_refused_before_enumerating_its_rows()
    {
        var source = new FakeByteSource(new byte[1], SourceCapabilities.CanWrite | SourceCapabilities.CanResize);
        var doc = new Document(source, Options());
        using (doc)
        {
            // 長さ 2^31 (1 億 3 千万行あまり)。各行の 2 バイトの矩形の反転は要素数の上限 (1,000,000) を超える。
            doc.InsertPattern(1, (1L << 31) - 1, [0]);
            var s = new EditorState(doc, 16) { VisibleRows = 10 };
            s.SelectRectangle(0, doc.Length - 15);
            Assert.True(s.InvertIsLongRunning);
            Assert.Equal(SelectionResult.TooManyElements, s.CheckInvertLimit());
            Assert.Equal(SelectionResult.TooManyElements, s.InvertSelection());
            Assert.Equal(SelectionKind.Rectangle, s.SelectionKind);

            // 行の幅が 1 行全体なら行は続いているので、反転は小さい (数え上げで上限を判断する)。
            s.SelectRectangle(16, 32L * 16 + 15);
            Assert.Null(s.CheckInvertLimit());
        }
    }

    [Fact]
    public void Inversion_stops_at_the_element_limit()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            s.SetSelections(Every2(10).Select(r => r with { Start = r.Start + 1 }));
            Assert.Null(EditorState.ComputeInversion(s.CaptureSelection(), maxElements: 10));
            Assert.Equal(11, EditorState.ComputeInversion(s.CaptureSelection(), maxElements: 11)!.Count);
        }
    }

    [Fact]
    public void Renaming_a_selection_set_reports_why_it_failed()
    {
        (Document doc, EditorState s) = MultiSelectionTests.Create(0x100);
        using (doc)
        {
            var sets = new SelectionSetCollection();
            s.Select(0, 4);
            Assert.Equal(SelectionSetSaveResult.Saved, sets.Save("a", s.CaptureSelection()));
            Assert.Equal(SelectionSetSaveResult.Saved, sets.Save("b", s.CaptureSelection()));

            Assert.Equal(SelectionSetRenameResult.Duplicate, sets.Rename("a", "b"));
            Assert.Equal(SelectionSetRenameResult.InvalidName, sets.Rename("a", "  "));
            Assert.Equal(SelectionSetRenameResult.InvalidName, sets.Rename("a", new string('x', SelectionSetCollection.MaxNameLength + 1)));
            Assert.Equal(SelectionSetRenameResult.NotFound, sets.Rename("z", "c"));
            Assert.Equal(SelectionSetRenameResult.Renamed, sets.Rename("a", " c "));
            Assert.NotNull(sets.Find("c"));
        }
    }
}
