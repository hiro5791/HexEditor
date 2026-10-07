using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

public sealed class EditorStateTests
{
    private static (Document Doc, EditorState State) Create(int length, bool resizable = true)
    {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var caps = resizable ? SourceCapabilities.CanResize | SourceCapabilities.CanWrite : SourceCapabilities.CanWrite;
        var doc = new Document(new FakeByteSource(data, caps), Options());
        return (doc, new EditorState(doc) { VisibleRows = 10 });
    }

    [Fact]
    public void ScrollMappingIsExactForSmallFiles()
    {
        Assert.Equal(1000, ScrollMapping.Scale(1000));
        Assert.Equal(123, ScrollMapping.ToRow(123, 1000));
        Assert.Equal(123, ScrollMapping.ToValue(123, 1000));
    }

    [Fact]
    public void ScrollMappingHandlesMaximumLength()
    {
        var layout = new HexLayout(16, long.MaxValue, CanResize: false);
        long max = layout.MaxTopRow(40);
        Assert.Equal(ScrollMapping.MaxScrollValue, ScrollMapping.Scale(max));
        Assert.Equal(0, ScrollMapping.ToRow(0, max));
        Assert.Equal(max, ScrollMapping.ToRow(ScrollMapping.MaxScrollValue, max));
        long middle = ScrollMapping.ToRow(ScrollMapping.MaxScrollValue / 2, max);
        Assert.InRange(middle, max / 2 - max / 1_000_000, max / 2 + max / 1_000_000);
        Assert.Equal(ScrollMapping.MaxScrollValue, ScrollMapping.ToValue(max, max));
    }

    [Fact]
    public void LayoutHasTrailingCellOnlyForResizableDocuments()
    {
        Assert.Equal(3, new HexLayout(16, 32, CanResize: true).TotalRows);
        Assert.Equal(2, new HexLayout(16, 32, CanResize: false).TotalRows);
        Assert.Equal(1, new HexLayout(16, 0, CanResize: true).TotalRows);
    }

    [Fact]
    public void ArrowKeysStopAtEdges()
    {
        (Document doc, EditorState s) = Create(40);
        using (doc)
        {
            s.MoveLeft();
            Assert.Equal(0, s.Cursor);
            s.MoveUp();
            Assert.Equal(0, s.Cursor);
            s.MoveDown();
            s.MoveDown();
            Assert.Equal(32, s.Cursor);
            s.MoveDown(); // 最終行 (32..40) の次はないので末尾の次 (40) へ。
            Assert.Equal(32, s.Cursor);
            s.MoveEnd();
            Assert.Equal(40, s.Cursor);
            s.MoveRight();
            Assert.Equal(40, s.Cursor);
        }
    }

    [Fact]
    public void ShiftArrowSelectsBetweenAnchorAndCursor()
    {
        (Document doc, EditorState s) = Create(64);
        using (doc)
        {
            s.MoveRight();
            s.MoveRight(extend: true);
            s.MoveRight(extend: true);
            Assert.Equal((1L, 2L), (s.SelectionStart, s.SelectionLength));
            s.MoveLeft();
            Assert.False(s.HasSelection);
        }
    }

    [Fact]
    public void ShiftClickIncludesBothEnds()
    {
        (Document doc, EditorState s) = Create(64);
        using (doc)
        {
            s.Click(10, ActiveColumn.Hex, false, extend: false);
            s.Click(4, ActiveColumn.Hex, false, extend: true);
            Assert.Equal((4L, 7L), (s.SelectionStart, s.SelectionLength));
        }
    }

    [Fact]
    public void HexTypingInOverwriteModeReplacesNibbles()
    {
        (Document doc, EditorState s) = Create(16);
        using (doc)
        {
            Assert.Equal(EditResult.Done, s.TypeHexDigit('A'));
            Assert.True(s.LowNibble);
            Assert.Equal(EditResult.Done, s.TypeHexDigit('ｂ'));
            Assert.Equal(1, s.Cursor);
            Assert.Equal(EditResult.Ignored, s.TypeHexDigit('g'));
            Assert.Equal(0xAB, Read(doc.Current, 0, 1)[0]);
            Assert.Equal(16, doc.Length);
            Assert.Equal(2, doc.History.Count); // 2 桁で 1 つの Undo 単位
        }
    }

    [Fact]
    public void HexTypingInInsertModeInsertsBytes()
    {
        (Document doc, EditorState s) = Create(4);
        using (doc)
        {
            s.ToggleInsertMode();
            s.MoveRight();
            s.TypeHexDigit('1');
            s.TypeHexDigit('2');
            s.TypeHexDigit('3');
            s.TypeHexDigit('4');
            Assert.Equal(new byte[] { 0x00, 0x12, 0x34, 0x01, 0x02, 0x03 }, ReadAll(doc.Current));
            Assert.Equal(3, s.Cursor);
        }
    }

    [Fact]
    public void TypingAtEndAppendsEvenInOverwriteMode()
    {
        (Document doc, EditorState s) = Create(2);
        using (doc)
        {
            s.MoveToEnd();
            s.TypeHexDigit('F');
            s.TypeHexDigit('F');
            Assert.Equal(new byte[] { 0x00, 0x01, 0xFF }, ReadAll(doc.Current));
        }
    }

    [Fact]
    public void FixedLengthDocumentRejectsInsertModeAndDelete()
    {
        (Document doc, EditorState s) = Create(16, resizable: false);
        using (doc)
        {
            Assert.Equal(EditResult.FixedLength, s.ToggleInsertMode());
            Assert.False(s.InsertMode);
            Assert.Equal(EditResult.FixedLength, s.Delete());
            s.MoveToEnd();
            Assert.Equal(15, s.Cursor);
        }
    }

    [Fact]
    public void TextTypingUsesEncodingAndRejectsUnencodable()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            s.ToggleColumn();
            Assert.Equal(EditResult.Done, s.TypeText("Hi", Encoding.ASCII));
            Assert.Equal("Hi"u8.ToArray(), Read(doc.Current, 0, 2));
            Assert.Equal(EditResult.NotEncodable, s.TypeText("あ", Encoding.ASCII));
            Assert.Equal(EditResult.Done, s.TypeText("あ", Encoding.UTF8));
            Assert.Equal(new byte[] { 0xE3, 0x81, 0x82 }, Read(doc.Current, 2, 3));
        }
    }

    [Fact]
    public void BackspaceInInsertModeDeletesPreviousByte()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            s.ToggleInsertMode();
            s.MoveRight();
            s.MoveRight();
            Assert.Equal(EditResult.Done, s.Backspace());
            Assert.Equal(new byte[] { 0, 2, 3, 4, 5, 6, 7 }, ReadAll(doc.Current));
            Assert.Equal(1, s.Cursor);
        }
    }

    [Fact]
    public void PageDownScrollsAndKeepsCursorVisible()
    {
        (Document doc, EditorState s) = Create(16 * 100);
        using (doc)
        {
            s.PageDown();
            Assert.Equal(9 * 16, s.Cursor);
            Assert.Equal(9, s.TopRow);
            s.MoveToEnd();
            Assert.Equal(1600, s.Cursor);
            Assert.True(s.TopRow + s.VisibleRows > 100);
        }
    }

    [Fact]
    public void JumpHistoryGoesBackAndForward()
    {
        (Document doc, EditorState s) = Create(16 * 1000);
        using (doc)
        {
            s.GoTo(0x100);
            s.GoTo(0x2000);
            Assert.True(s.CanGoBack);
            s.GoBack();
            Assert.Equal(0x100, s.Cursor);
            s.GoBack();
            Assert.Equal(0, s.Cursor);
            Assert.False(s.CanGoBack);
            s.GoForward();
            Assert.Equal(0x100, s.Cursor);
            s.GoTo(0x300);
            Assert.False(s.CanGoForward);
        }
    }

    [Fact]
    public void JumpHistoryFollowsInsertions()
    {
        (Document doc, EditorState s) = Create(16 * 1000);
        using (doc)
        {
            s.GoTo(0x200);
            s.GoTo(0x1000);
            doc.Insert(0, new byte[0x10]);
            s.GoBack();
            Assert.Equal(0x210, s.Cursor);
        }
    }

    [Fact]
    public void GoToWithSelection()
    {
        (Document doc, EditorState s) = Create(256);
        using (doc)
        {
            s.Click(0x10, ActiveColumn.Hex, false, false);
            s.GoTo(0x20, extendSelection: true);
            Assert.Equal((0x10L, 0x10L), (s.SelectionStart, s.SelectionLength));
        }
    }
}
