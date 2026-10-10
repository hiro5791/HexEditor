using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>マルチ選択 (EDIT-07)・矩形選択 (EDIT-06)・選択範囲をずらす (EDIT-05) のモデル。</summary>
public sealed class MultiSelectionTests
{
    internal static (Document Doc, EditorState State) Create(int length, bool resizable = true)
    {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var caps = resizable ? SourceCapabilities.CanResize | SourceCapabilities.CanWrite : SourceCapabilities.CanWrite;
        var doc = new Document(new FakeByteSource(data, caps), Options());
        return (doc, new EditorState(doc) { VisibleRows = 10 });
    }

    private static List<ByteRange> R(params (long Start, long Length)[] ranges) => ranges.Select(r => new ByteRange(r.Start, r.Length)).ToList();

    // ---- RangeSet ----

    [Fact]
    public void Overlapping_and_adjacent_ranges_are_merged()
    {
        var set = new RangeSet();
        set.Add(new ByteRange(0x10, 0x10));
        set.Add(new ByteRange(0x18, 0x10));
        Assert.Equal(R((0x10, 0x18)), set.ToList());
        set.Add(new ByteRange(0x28, 0x08));
        Assert.Equal(R((0x10, 0x20)), set.ToList());
        set.Add(new ByteRange(0x40, 0x10));
        Assert.Equal(2, set.Count);
        Assert.Equal(0x30, set.TotalLength);
        set.Add(new ByteRange(0, 0x100));
        Assert.Equal(R((0, 0x100)), set.ToList());
    }

    [Fact]
    public void Find_next_previous_and_remove()
    {
        var set = new RangeSet(R((0, 2), (10, 2), (20, 2)));
        Assert.Equal(new ByteRange(10, 2), set.Find(11));
        Assert.Null(set.Find(12));
        Assert.Equal(new ByteRange(20, 2), set.NextAfter(10));
        Assert.Null(set.NextAfter(20));
        Assert.Equal(new ByteRange(0, 2), set.PreviousBefore(10));
        Assert.Equal(new ByteRange(10, 2), set.PreviousBefore(11));
        Assert.Equal(new ByteRange(10, 2), set.RemoveAt(10));
        Assert.Equal(R((0, 2), (20, 2)), set.ToList());
        Assert.Equal(R((20, 2)), set.Overlapping(5, 100).ToList());
    }

    [Fact]
    public void Many_elements_span_several_leaves()
    {
        var set = new RangeSet();
        for (int i = 0; i < 10_000; i++)
        {
            set.Add(new ByteRange(i * 10L, 5));
        }

        // 逆順の追加・途中の追加でも並びが保たれる。
        for (int i = 9_999; i >= 0; i--)
        {
            set.Add(new ByteRange(i * 10L + 7, 1));
        }

        Assert.Equal(20_000, set.Count);
        long previous = -1;
        foreach (ByteRange r in set)
        {
            Assert.True(r.Start > previous);
            previous = r.End;
        }

        // 全体を覆う範囲を加えると 1 つになる。
        set.Add(new ByteRange(0, 100_000));
        Assert.Single(set);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-07-03")]
    public void Invert_twice_restores_the_selection()
    {
        // 1〜2. 長さ 1,000 のドキュメントで (0, 10)、(100, 1)、(990, 10)。
        var set = new RangeSet(R((0, 10), (100, 1), (990, 10)));
        RangeSet once = set.Invert(1000);
        Assert.Equal(R((10, 90), (101, 889)), once.ToList());
        Assert.Equal(set.ToList(), once.Invert(1000).ToList());

        // 4. 選択なしの反転は全体 1 要素、全体 1 要素の反転は選択なし。
        Assert.Equal(R((0, 1000)), new RangeSet().Invert(1000).ToList());
        Assert.Empty(new RangeSet(R((0, 1000))).Invert(1000));
    }

    private static Arbitrary<(long Length, List<ByteRange> Ranges)> Selections() =>
        Gen.Choose(1, 1_000_000).SelectMany(length => Gen.Choose(0, 1000).SelectMany(n =>
            Gen.ArrayOf(Gen.Choose(0, length - 1), 2 * n).Select(points =>
            {
                // 無作為な点を並べ、隣り合う 2 点を 1 要素にする (重なりも隣接もしない)。
                long[] sorted = points.Select(p => (long)p).Distinct().Order().ToArray();
                var ranges = new List<ByteRange>();
                for (int i = 0; i + 1 < sorted.Length; i += 2)
                {
                    long start = sorted[i], end = sorted[i + 1];
                    if (ranges.Count == 0 || start > ranges[^1].End)
                    {
                        ranges.Add(ByteRange.FromBounds(start, end));
                    }
                }

                return ((long)length, ranges);
            }))).ToArbitrary();

    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-EDIT-07-03")]
    public Property Invert_is_an_involution() => Prop.ForAll(Selections(), sample =>
    {
        // 3. 2 回の反転で元に戻り、1 回の反転は元と重ならず、和がドキュメント全体になる。
        var set = new RangeSet(sample.Ranges);
        RangeSet once = set.Invert(sample.Length);
        RangeSet twice = once.Invert(sample.Length);
        bool disjoint = set.All(r => !once.Overlapping(r.Start, r.Length).Any());
        bool cover = set.TotalLength + once.TotalLength == sample.Length;
        return twice.SequenceEqual(set) && disjoint && cover;
    });

    // ---- 矩形 ----

    [Fact]
    public void Rectangle_ranges_and_byte_count_match_brute_force()
    {
        foreach (int shift in new[] { 0, 3 })
        {
            foreach (long length in new long[] { 0, 1, 37, 64, 65, 100 })
            {
                var rect = new RectSelection(0, 8, 2, 5, 16, shift);
                long brute = Enumerable.Range(0, (int)Math.Max(1, length)).Count(o => rect.Contains(o, length));
                Assert.Equal(brute, rect.ByteCount(length));
                Assert.Equal(brute, rect.Ranges(length).Sum(r => r.Length));
                Assert.Equal(rect.Ranges(length).LongCount(), SelectionSnapshot.RectangleRowCount(rect, length));
            }
        }
    }

    [Fact]
    public void Rectangle_selection_selects_columns_and_converts_to_multi_on_row_change()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            // EDIT-06 の受け入れ基準 1: 0x04 から 3 行下の列 7 まで。
            s.BeginRectangle(0x04, ActiveColumn.Hex);
            s.RectangleTo(0x37);
            Assert.Equal(SelectionKind.Rectangle, s.SelectionKind);
            Assert.Equal(R((0x04, 4), (0x14, 4), (0x24, 4), (0x34, 4)), s.SelectedRanges.ToList());
            Assert.Equal(16, s.SelectedByteCount);
            Assert.Equal(4, s.SelectedRangeCount);

            // 受け入れ基準 3: 1 行のバイト数を 8 にすると同じ 16 バイトを指す 4 要素のマルチ選択になる。
            s.ApplyView(s.View with { BytesPerRow = 8 });
            Assert.Equal(SelectionKind.Multiple, s.SelectionKind);
            Assert.Equal(R((0x04, 4), (0x14, 4), (0x24, 4), (0x34, 4)), s.SelectedRanges.ToList());
        }
    }

    [Fact]
    public void Rectangle_over_huge_document_is_constant_size()
    {
        var doc = new Document(new FakeByteSource(new byte[16], SourceCapabilities.CanWrite | SourceCapabilities.CanResize), Options());
        using (doc)
        {
            doc.InsertPattern(16, 100L << 30, [0]);
            var s = new EditorState(doc, 4096) { VisibleRows = 10 };
            long lastRowStart = (doc.Length - 1) / 4096 * 4096;
            s.SelectRectangle(0, lastRowStart + 3);
            long rows = lastRowStart / 4096 + 1;
            Assert.True(rows > 26_214_000);
            Assert.Equal(rows, s.SelectedRangeCount);
            Assert.Equal(rows * 4, s.SelectedByteCount);
            Assert.True(s.IsSelected(lastRowStart + 2));
            Assert.False(s.IsSelected(lastRowStart + 4));
        }
    }

    // ---- マルチ選択の操作 ----

    [Fact]
    public void Ctrl_drag_adds_and_ctrl_click_removes_elements()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            s.DragTo(0x13);
            s.BeginAddSelection(0x20, ActiveColumn.Hex);
            s.DragTo(0x23);
            s.CommitSelection();
            s.BeginAddSelection(0x30, ActiveColumn.Hex);
            s.DragTo(0x33);
            Assert.Equal(3, s.SelectedRangeCount);
            Assert.Equal(12, s.SelectedByteCount);
            s.CommitSelection();
            Assert.Equal(SelectionKind.Multiple, s.SelectionKind);
            Assert.Equal(new ByteRange(0x30, 4), s.PrimaryRange);

            // 選択されている範囲の中で Ctrl+クリックすると、その要素を取り除く。
            s.BeginAddSelection(0x21, ActiveColumn.Hex);
            Assert.True(s.RemoveSelectionAt(0x21));
            s.CommitSelection();
            Assert.Equal(R((0x10, 4), (0x30, 4)), s.SelectedRanges.ToList());
            Assert.Equal(new ByteRange(0x30, 4), s.PrimaryRange);

            // Esc は主要素の末尾にカーソルを残す (TC-EDIT-03-03)。
            s.ClearSelection();
            Assert.False(s.HasSelection);
            Assert.Equal(0x34, s.Cursor);
        }
    }

    [Fact]
    public void Adding_beyond_the_limit_is_refused()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            s.MaxSelectionElements = 2;
            Assert.Equal(SelectionResult.Done, s.AddSelection(0, 1));
            Assert.Equal(SelectionResult.Done, s.AddSelection(4, 1));
            Assert.Equal(SelectionResult.TooManyElements, s.AddSelection(8, 1));
            Assert.Equal(2, s.SelectedRangeCount);

            // 結合されるなら上限内。
            Assert.Equal(SelectionResult.Done, s.AddSelection(5, 1));
            Assert.Equal(R((0, 1), (4, 2)), s.SelectedRanges.ToList());

            Assert.Equal(SelectionResult.Truncated, s.SetSelections(R((0, 1), (2, 1), (4, 1))));
            Assert.Equal(2, s.SelectedRangeCount);
        }
    }

    [Fact]
    public void Deleting_multiple_ranges_is_one_undo_group()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            s.SetSelections(R((0x10, 4), (0x20, 4), (0x30, 4)));
            Assert.Equal(EditResult.Done, s.Delete());
            Assert.Equal(0x100 - 12, doc.Length);
            byte[] head = Read(doc, 0x0E, 0x10);
            Assert.Equal(new byte[] { 0x0E, 0x0F, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x24, 0x25 }, head);
            Assert.False(s.HasSelection);
            Assert.Equal(0x30 - 8, s.Cursor);
            s.Undo();
            Assert.Equal(0x100, doc.Length);
            Assert.Equal(Enumerable.Range(0, 0x40).Select(i => (byte)i), Read(doc, 0, 0x40));
            Assert.False(doc.History.CanUndo);
        }
    }

    [Fact]
    public void Shift_and_swap()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            // EDIT-05 の受け入れ基準 1: 0x00〜0x1F を選んで次へずらす 3 回で 0x60〜0x7F。
            s.Select(0, 0x20);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(SelectionResult.Done, s.ShiftSelectionStep(forward: true));
            }

            Assert.Equal((0x60L, 0x20L), (s.SelectionStart, s.SelectionLength));

            // 受け入れ基準 2: 末尾の 32 バイトはずらせない。
            s.Select(0xE0, 0x20);
            Assert.Equal(SelectionResult.OutOfRange, s.ShiftSelectionStep(forward: true));
            Assert.Equal((0xE0L, 0x20L), (s.SelectionStart, s.SelectionLength));
            s.Select(0x10, 0x20);
            Assert.Equal(SelectionResult.OutOfRange, s.ShiftSelectionStep(forward: false));

            // マルチ選択はすべての要素をずらす。1 つでも範囲外なら何もしない。
            s.SetSelections(R((0x10, 2), (0x40, 2)));
            Assert.Equal(SelectionResult.Done, s.ShiftSelection(0x10));
            Assert.Equal(R((0x20, 2), (0x50, 2)), s.SelectedRanges.ToList());
            Assert.Equal(SelectionResult.OutOfRange, s.ShiftSelection(-0x21));

            // 広げる / 狭める。
            s.Select(0x10, 0x10);
            Assert.Equal(SelectionResult.Done, s.ResizeSelection(2, 3));
            Assert.Equal((0x0EL, 0x15L), (s.SelectionStart, s.SelectionLength));
            Assert.Equal(SelectionResult.OutOfRange, s.ResizeSelection(-0x10, -0x10));

            // 受け入れ基準 3 (TC-EDIT-05-02): 0x100 から Shift+↓ で選んだ範囲のアンカーとカーソルを入れ替える。
            (Document doc2, EditorState t) = Create(0x1000);
            using (doc2)
            {
                t.Click(0x100, ActiveColumn.Hex, false, false);
                for (int i = 0; i < 64; i++)
                {
                    t.MoveDown(extend: true);
                }

                Assert.Equal((0x100L, 0x400L, 0x500L), (t.SelectionStart, t.SelectionLength, t.Cursor));
                Assert.Equal(SelectionResult.Done, t.SwapAnchorAndCursor());
                Assert.Equal((0x100L, 0x400L, 0x100L), (t.SelectionStart, t.SelectionLength, t.Cursor));
                Assert.InRange(0x100 / 16, t.TopRow, t.TopRow + t.VisibleRows - 1);
                t.MoveRight(extend: true);
                Assert.Equal((0x101L, 0x3FFL), (t.SelectionStart, t.SelectionLength));
            }
        }
    }

    [Fact]
    public void Next_element_cycles_through_elements()
    {
        (Document doc, EditorState s) = Create(0x100);
        using (doc)
        {
            s.SetSelections(R((0x04, 4), (0x14, 4), (0x24, 4), (0x34, 4)));
            var starts = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                s.NextElement();
                starts.Add(s.PrimaryRange!.Value.Start);
            }

            Assert.Equal([0x04L, 0x14, 0x24, 0x34], starts.Order());
            Assert.Equal(4, s.SelectedRangeCount);
        }
    }

    private static byte[] Read(Document doc, long offset, int length)
    {
        byte[] buffer = new byte[length];
        doc.Current.Read(offset, buffer);
        return buffer;
    }
}
