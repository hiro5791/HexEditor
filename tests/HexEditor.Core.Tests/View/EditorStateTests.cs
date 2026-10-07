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
            Assert.Same(TextEncoding.Ascii, s.TextEncoding);
            Assert.Equal(EditResult.Done, s.TypeText("Hi"));
            Assert.Equal("Hi"u8.ToArray(), Read(doc.Current, 0, 2));
            Assert.Equal(EditResult.NotEncodable, s.TypeText("é"));
            Assert.Equal(EditResult.NotEncodable, s.TypeText("あ"));
            Assert.Equal(new byte[] { 2, 3 }, Read(doc.Current, 2, 2));
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
    [Fact]
    [Trait(TC, "TC-EDIT-11-05")]
    public void FullWidthHexDigitsAreAccepted()
    {
        (Document doc, EditorState s) = Create(16);
        using (doc)
        {
            // 手順 1
            Assert.Equal(EditResult.Done, s.TypeHexText("０１２３４５６７８９"));
            Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89 }, Read(doc.Current, 0, 5));

            // 手順 2
            Assert.Equal(EditResult.Done, s.TypeHexText("ＡＢＣＤＥＦａｂｃｄｅｆ"));
            Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF, 0xAB, 0xCD, 0xEF }, Read(doc.Current, 5, 6));

            // 手順 3: 16 進数字でない全角文字ではデータもカーソルも変わらない。
            byte[] before = ReadAll(doc.Current);
            long cursor = s.Cursor;
            Assert.Equal(EditResult.Ignored, s.TypeHexText("Ｇｇ－"));
            Assert.Equal(before, ReadAll(doc.Current));
            Assert.Equal(cursor, s.Cursor);

            // 手順 4: 変換中の文字列は確定するまで渡されない (確定前に取り消すと何も呼ばれない) ため、変わらない。
            Assert.Equal(before, ReadAll(doc.Current));
            Assert.Equal(11, s.Cursor);
        }
    }

    [Fact]
    public void CtrlHomeAndEndRecordJumpHistoryAndPlaceRows()
    {
        (Document doc, EditorState s) = Create(16 * 100);
        using (doc)
        {
            s.GoTo(0x300);
            s.Select(0x300, 4);
            s.MoveToEnd();
            Assert.False(s.HasSelection);
            Assert.Equal(1600, s.Cursor);
            Assert.Equal(s.Layout.MaxTopRow(s.VisibleRows), s.TopRow); // 最終行が一番下 (VIEW-30 の仕様 2)
            s.MoveToStart();
            Assert.Equal((0L, 0L), (s.Cursor, s.TopRow));

            // Alt+← で Ctrl+Home・Ctrl+End の前の位置に戻る (VIEW-30 の仕様 3)。
            s.GoBack();
            Assert.Equal(1600, s.Cursor);
            s.GoBack();
            Assert.Equal(0x300 + 4, s.Cursor);
        }
    }

    [Fact]
    public void SelectAllPutsCursorAtEndWithoutScrolling()
    {
        (Document doc, EditorState s) = Create(16 * 100);
        using (doc)
        {
            s.ScrollToRow(30);
            s.SelectAll();
            Assert.Equal((0L, 1600L), (s.SelectionStart, s.SelectionLength));
            Assert.Equal(1600, s.Cursor);
            Assert.Equal(30, s.TopRow);

            // Shift+← はアンカー 0 から伸ばし直す。
            s.MoveLeft(extend: true);
            Assert.Equal((0L, 1599L), (s.SelectionStart, s.SelectionLength));
        }

        (doc, s) = Create(0);
        using (doc)
        {
            s.SelectAll();
            Assert.False(s.HasSelection);
        }
    }

    [Fact]
    public void ArrowsCollapseSelectionToItsEdges()
    {
        (Document doc, EditorState s) = Create(64);
        using (doc)
        {
            s.Select(10, 5);
            s.MoveLeft();
            Assert.Equal(10, s.Cursor);
            Assert.False(s.HasSelection);
            s.Select(10, 5);
            s.MoveRight();
            Assert.Equal(15, s.Cursor);
            Assert.False(s.HasSelection);
        }
    }

    [Fact]
    public void TextEncodingIsUsedForDisplayInputAndCopy()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            // ASCII: 20〜7E が文字、それ以外は「.」。コピーでは解釈できないバイトと NUL を U+FFFD にする。
            Assert.Equal('A', s.TextEncoding.DisplayChar(0x41));
            Assert.Equal('.', s.TextEncoding.DisplayChar(0xE9));
            Assert.Equal('.', s.TextEncoding.DisplayChar(0x00));
            s.ToggleColumn();
            Assert.Equal("A��", s.FormatForClipboard([0x41, 0x00, 0xE9]));
            Assert.Equal("é", s.TextEncoding.FirstUnencodable("abé"));

            // ANSI (システムのコードページ): 1252 なら E9 は é。表示・入力・コピーで同じ文字コード。
            s.TextEncoding = TextEncoding.Ansi;
            if (TextEncoding.Ansi.CodePage == 1252)
            {
                Assert.Equal('é', s.TextEncoding.DisplayChar(0xE9));
                Assert.Equal('.', s.TextEncoding.DisplayChar(0x81)); // 未定義
                Assert.Equal(EditResult.Done, s.TypeText("é"));
                Assert.Equal(0xE9, Read(doc.Current, 0, 1)[0]);
                Assert.Equal("é", s.FormatForClipboard([0xE9]));
            }

            Assert.Equal(EditResult.NotEncodable, s.TypeText("\U0001F600"));
        }
    }

    [Fact]
    public void PasteTextIntoHexColumnFallsBackToText()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            s.ToggleInsertMode();

            // Hex として読めるテキストはバイト列 (EDIT-23 の受け入れ基準 1)。
            Assert.Equal(EditResult.Done, s.PasteText("0xDE, 0xAD", overwrite: false));
            Assert.Equal(new byte[] { 0xDE, 0xAD }, Read(doc.Current, 0, 2));

            // 読めなければテキストとして貼り、UI に知らせる (「元に戻す」で取り消せる)。
            s.Click(0, ActiveColumn.Hex, false, false);
            Assert.Equal(EditResult.PastedAsText, s.PasteText("Hello", overwrite: false));
            Assert.Equal("Hello"u8.ToArray(), Read(doc.Current, 0, 5));
            doc.Undo();
            Assert.Equal(new byte[] { 0xDE, 0xAD }, Read(doc.Current, 0, 2));

            // 文字コードで表せなければ何もしない。
            Assert.Equal(EditResult.NotEncodable, s.PasteText("日本", overwrite: false));
            Assert.Equal(10, doc.Length);
        }
    }
}
