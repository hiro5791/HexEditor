using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>
/// ビューの表示設定と状態 (VIEW-08 の仕様 7、VIEW-15 の変更の区別、VIEW-16 の仕様 3・5、VIEW-20 の基準点、VIEW-31 ジャンプ履歴)。
/// UI テストのうち、ビューの状態 (<see cref="EditorState"/>) だけで確かめられる手順をここで確かめる。
/// </summary>
public sealed class EditorViewTests
{
    private const int Visible = 30;

    private static EditorState Seq1M()
    {
        byte[] data = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, data);
        var doc = new Document(new MemoryByteSource(data), Options());
        return new EditorState(doc) { VisibleRows = Visible };
    }

    private static EditorState Bytes256()
    {
        byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var doc = new Document(new MemoryByteSource(data), Options());
        return new EditorState(doc) { VisibleRows = Visible };
    }

    // ---- VIEW-31 ----

    [Fact]
    [Trait(TC, "TC-VIEW-31-01")]
    public void Back_and_forward_walk_the_jumps()
    {
        EditorState e = Seq1M();
        e.GoTo(0x1000);
        e.GoTo(0x2000);
        e.GoTo(0x3000);
        long[] back = [.. Enumerable.Range(0, 3).Select(_ => { e.GoBack(); return e.Cursor; })];
        Assert.Equal([0x2000L, 0x1000, 0], back);
        long[] forward = [.. Enumerable.Range(0, 3).Select(_ => { e.GoForward(); return e.Cursor; })];
        Assert.Equal([0x1000L, 0x2000, 0x3000], forward);
        Assert.True(e.CanGoBack);
        Assert.False(e.CanGoForward);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-31-02")]
    public void New_jump_after_back_clears_forward()
    {
        EditorState e = Seq1M();
        e.GoTo(0x1000);
        e.GoTo(0x2000);
        e.GoBack();
        e.GoTo(0x5000);
        Assert.False(e.CanGoForward);
        e.GoForward();
        Assert.Equal(0x5000, e.Cursor);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-31-03")]
    public void Arrow_keys_and_scrolling_do_not_record()
    {
        EditorState e = Seq1M();
        e.GoTo(0x1000);
        for (int i = 0; i < 50; i++)
        {
            e.MoveDown();
        }

        for (int i = 0; i < 20; i++)
        {
            e.MoveRight();
        }

        for (int i = 0; i < 5; i++)
        {
            e.PageDown();
        }

        e.ScrollRows(30);
        Assert.Single(e.RecentJumps);
        Assert.Equal(0, e.RecentJumps[0].Offset);
        e.GoBack();
        Assert.Equal(0, e.Cursor);
    }

    [Fact]
    [Trait(TC, "TC-VIEW-31-04")]
    public void History_follows_insertions()
    {
        EditorState e = Seq1M();
        e.GoTo(0x1000);
        e.GoTo(0x5000);
        e.ToggleInsertMode();
        e.GoTo(0x100);
        e.Paste(new byte[16], overwrite: false);
        e.GoBack();
        Assert.Equal(0x5010, e.Cursor);
        e.GoBack();
        Assert.Equal(0x1010, e.Cursor);
        byte[] value = new byte[4];
        e.Document.Current.Read(e.Cursor, value);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, value);
    }

    [Fact]
    public void Far_click_records_and_history_is_limited()
    {
        EditorState e = Seq1M();
        e.Click(16 * Visible * 3, ActiveColumn.Hex, lowNibble: false, extend: false);
        Assert.True(e.CanGoBack);
        e.Click(16 * Visible * 3 + 1, ActiveColumn.Hex, lowNibble: false, extend: false);
        Assert.Single(e.RecentJumps);

        for (int i = 1; i <= 150; i++)
        {
            e.GoTo(i * 0x1000L);
        }

        Assert.Equal(JumpHistory.ListCount, e.RecentJumps.Count);
        int count = 0;
        while (e.CanGoBack)
        {
            e.GoBack();
            count++;
        }

        Assert.Equal(JumpHistory.Limit, count);
    }

    // ---- VIEW-08 ----

    [Fact]
    [Trait(TC, "TC-VIEW-08-03")]
    public void Changing_bytes_per_row_keeps_the_cursor_and_its_screen_row()
    {
        EditorState e = Seq1M();
        e.GoTo(0x12345);
        long screenRow = e.Layout.RowOf(e.Cursor) - e.TopRow;
        foreach (int b in new[] { 32, 8, 64 })
        {
            e.ApplyView(e.View with { BytesPerRow = b });
            Assert.Equal(b, e.BytesPerRow);
            Assert.Equal(0x12345, e.Cursor);
            Assert.Equal(screenRow, e.Layout.RowOf(e.Cursor) - e.TopRow);
        }
    }

    [Fact]
    public void Auto_bytes_per_row_is_set_by_the_view()
    {
        EditorState e = Seq1M();
        e.ApplyView(e.View with { AutoBytesPerRow = true });
        e.SetAutoBytesPerRow(40);
        Assert.Equal(40, e.BytesPerRow);
        e.ApplyView(e.View with { AutoBytesPerRow = false });
        Assert.Equal(16, e.BytesPerRow);
    }

    // ---- VIEW-16 ----

    [Fact]
    [Trait(TC, "TC-VIEW-16-03")]
    public void Hiding_the_active_column_moves_the_cursor_to_the_other()
    {
        EditorState e = Seq1M();
        e.GoTo(0x123);
        e.ToggleColumn();
        Assert.Equal(ActiveColumn.Text, e.ActiveColumn);
        e.ApplyView(e.View with { ShowTextColumn = false });
        Assert.Equal(ActiveColumn.Hex, e.ActiveColumn);
        Assert.Equal(0x123, e.Cursor);

        // 表示されている列が 1 つのときは Tab で何もしない (仕様 5)。
        e.ToggleColumn();
        Assert.Equal(ActiveColumn.Hex, e.ActiveColumn);

        // Hex 列とテキスト列の両方は隠せない (仕様 2)。
        e.ApplyView(e.View with { ShowHexColumn = false });
        Assert.True(e.View.ShowHexColumn || e.View.ShowTextColumn);
    }

    // ---- VIEW-20 ----

    [Fact]
    [Trait(TC, "TC-VIEW-20-04")]
    public void Reference_point_follows_insertions_and_deletions()
    {
        EditorState e = Bytes256();
        e.GoTo(0x40);
        e.SetReferencePoint();
        Assert.Equal(0x40, e.ReferencePoint);

        e.ToggleInsertMode();
        e.GoTo(0x10);
        e.Paste(new byte[16], overwrite: false);
        Assert.Equal(0x50, e.ReferencePoint);
        var format = new OffsetFormat(e.View, e.Layout.MaxCursor, 512, e.ReferencePoint);
        Assert.Equal("+00000000", format.Column(0x50));

        e.Select(0, 0x60);
        e.Delete();
        Assert.Equal(0, e.ReferencePoint);

        e.ClearReferencePoint();
        Assert.Null(e.ReferencePoint);
    }

    [Fact]
    public void Base_address_shifts_the_rows()
    {
        EditorState e = Bytes256();
        e.ApplyView(e.View with { BaseAddress = 0x401004 });
        Assert.Equal(4, e.Layout.RowShift);
        Assert.Equal(-4, e.Layout.RowStart(0));
        e.MoveHome();
        Assert.Equal(0, e.Cursor);
        e.MoveDown();
        Assert.Equal(16, e.Cursor);
        Assert.Equal(1, e.Layout.RowOf(16));
    }

    // ---- VIEW-15 ----

    [Fact]
    [Trait(TC, "TC-VIEW-15-02")]
    public void Inserted_bytes_are_distinguished_from_overwritten_ones()
    {
        EditorState e = Seq1M();
        e.GoTo(0x20);
        e.TypeHexText("AB");
        e.ToggleInsertMode();
        e.GoTo(0x40);
        e.TypeHexText("ABCD");
        var changes = e.Document.Current.EnumerateChanges(0, 0x100).ToList();
        Assert.Equal([(0x20L, 1L, false), (0x40L, 2L, true)], changes);

        // Undo で元データに戻ったバイトは変更として示さない (VIEW-15 の仕様 2)。
        e.Undo();
        Assert.Equal([(0x20L, 1L, false)], e.Document.Current.EnumerateChanges(0, 0x100).ToList());
    }

    [Fact]
    public void Deleted_positions_are_reported_at_the_closed_up_boundary()
    {
        // VIEW-15 の仕様 5: 削除によって詰まった境界 (削除したバイトは p − 1 と p の間にあった)。
        EditorState e = Seq1M();
        Document doc = e.Document;
        doc.Delete(0x30, 4);
        Assert.Equal([0x30L], doc.Current.EnumerateDeletions(0, 0x100).ToList());

        // 表示範囲の先頭の境界は含めず、末尾の境界は含める。
        Assert.Empty(doc.Current.EnumerateDeletions(0x30, 0x10));
        Assert.Equal([0x30L], doc.Current.EnumerateDeletions(0x20, 0x10).ToList());

        // 挿入より多く置き換えた所は、変更の範囲の後ろの境界。挿入だけ・上書きだけは削除ではない。
        doc.Insert(0x80, [1, 2]);
        doc.Overwrite(0x90, [7]);
        Assert.Equal([0x30L], doc.Current.EnumerateDeletions(0, 0x100).ToList());
        using (doc.BeginGroup("置換"))
        {
            doc.Delete(0x40, 8);
            doc.Insert(0x40, [9, 9]);
        }

        Assert.Equal([0x30L, 0x42L], doc.Current.EnumerateDeletions(0, 0x100).ToList());

        // 末尾の削除。
        doc.Delete(doc.Length - 0x10, 0x10);
        Assert.Equal([doc.Length], doc.Current.EnumerateDeletions(doc.Length - 0x20, 0x20).ToList());

        // Undo で元に戻ると削除の位置もなくなる。
        while (doc.History.CanUndo)
        {
            doc.Undo();
        }

        Assert.Empty(doc.Current.EnumerateDeletions(0, doc.Length));
    }

    private static void Save(Document doc)
    {
        var settings = new Core.Saving.SaveSettings { JournalDirectory = Path.Combine(Path.GetTempPath(), "hexeditor-view-journal") };
        Core.Saving.SavePlan plan = Core.Saving.SavePlanner.Plan(doc, null, settings);
        Core.Saving.SavePlanner.Complete(plan, Core.Saving.SavePlanner.Execute(plan));
    }

    [Fact]
    public void Saved_changes_are_kept_until_closed_when_enabled()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hexeditor-view-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[256]);
        try
        {
            using var doc = new Document(FileByteSource.Open(path), Options());
            var e = new EditorState(doc) { VisibleRows = 10, KeepChangesAfterSave = true };
            e.GoTo(0x41);
            e.TypeHexText("CD");
            Save(doc);
            Assert.Empty(doc.Current.EnumerateChanges(0, 256));
            Assert.Equal([(0x41L, 1L)], e.SavedChangesIn(0, 256).ToList());

            e.KeepChangesAfterSave = false;
            e.TypeHexText("EE");
            Save(doc);
            Assert.Empty(e.SavedChangesIn(0, 256));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
