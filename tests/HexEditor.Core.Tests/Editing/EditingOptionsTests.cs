using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Editing;

/// <summary>
/// 編集の設定 (UI-22 の「編集」の区画): 上書きモードで選択範囲に入力 (EDIT-11 の仕様 3)、テキスト列の Enter (EDIT-12 の仕様 7)、
/// 上書きモードの Backspace・Delete (EDIT-13 の仕様 3・4)、保存時に履歴を消す (EDIT-19 の仕様 9)、コピーする Hex 文字列の書式
/// (EDIT-22 の仕様 2)、貼り付けの選択と上書き貼り付けの長さ (EDIT-23 の仕様 6・7)。
/// </summary>
public sealed class EditingOptionsTests
{
    private static (Document Doc, EditorState State) Create(int length, EditingOptions options)
    {
        byte[] data = [.. Enumerable.Range(0x10, length).Select(i => (byte)i)];
        var doc = new Document(new FakeByteSource(data, SourceCapabilities.CanResize | SourceCapabilities.CanWrite), Options());
        return (doc, new EditorState(doc) { VisibleRows = 10, Options = options });
    }

    [Fact]
    public void ZeroFirstClearsTheSelectionBeforeOverwriting()
    {
        (Document doc, EditorState s) = Create(6, new EditingOptions { ZeroSelectionBeforeTyping = true });
        using (doc)
        {
            s.Select(1, 4);
            s.TypeHexDigit('A');
            s.TypeHexDigit('B');
            Assert.Equal(new byte[] { 0x10, 0xAB, 0, 0, 0, 0x15 }, ReadAll(doc.Current));

            // 00 にするのと入力は 1 回の元に戻すで戻る。
            s.Undo();
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 }, ReadAll(doc.Current));
        }
    }

    [Fact]
    public void DefaultOverwritesFromTheSelectionStart()
    {
        (Document doc, EditorState s) = Create(6, EditingOptions.Default);
        using (doc)
        {
            s.Select(1, 4);
            s.TypeHexDigit('A');
            s.TypeHexDigit('B');
            Assert.Equal(new byte[] { 0x10, 0xAB, 0x12, 0x13, 0x14, 0x15 }, ReadAll(doc.Current));
        }
    }

    [Theory]
    [InlineData(TextEnterAction.CrLf, new byte[] { 0x0D, 0x0A })]
    [InlineData(TextEnterAction.Lf, new byte[] { 0x0A })]
    [InlineData(TextEnterAction.Cr, new byte[] { 0x0D })]
    public void EnterInTheTextColumnWritesTheLineBreak(TextEnterAction action, byte[] expected)
    {
        (Document doc, EditorState s) = Create(4, new EditingOptions { TextEnter = action });
        using (doc)
        {
            s.ToggleInsertMode();
            s.ToggleColumn();
            Assert.Equal(EditResult.Done, s.TypeEnter());
            Assert.Equal(expected, ReadAll(doc.Current)[..expected.Length]);
            Assert.Equal(4 + expected.Length, doc.Length);
        }
    }

    [Fact]
    public void EnterDoesNothingByDefault()
    {
        (Document doc, EditorState s) = Create(4, EditingOptions.Default);
        using (doc)
        {
            s.ToggleColumn();
            Assert.Equal(EditResult.Ignored, s.TypeEnter());
            Assert.Equal(4, doc.Length);
        }
    }

    [Fact]
    public void BackspaceCanZeroThePreviousByteInOverwriteMode()
    {
        (Document doc, EditorState s) = Create(4, new EditingOptions { BackspaceZeroesInOverwrite = true });
        using (doc)
        {
            s.GoTo(3);
            Assert.Equal(EditResult.Done, s.Backspace());
            Assert.Equal(new byte[] { 0x10, 0x11, 0, 0x13 }, ReadAll(doc.Current));
            Assert.Equal(2, s.Cursor);
            Assert.Equal(4, doc.Length);
        }
    }

    [Fact]
    public void DeleteCanKeepTheLengthInOverwriteMode()
    {
        (Document doc, EditorState s) = Create(4, new EditingOptions { DeleteKeepsLengthInOverwrite = true });
        using (doc)
        {
            s.GoTo(1);
            s.Delete();
            Assert.Equal(new byte[] { 0x10, 0, 0x12, 0x13 }, ReadAll(doc.Current));
            Assert.Equal(2, s.Cursor);

            s.Select(2, 2);
            s.Delete();
            Assert.Equal(new byte[] { 0x10, 0, 0, 0 }, ReadAll(doc.Current));
            Assert.Equal(4, doc.Length);

            // 挿入モードでは長さを変える。
            s.ToggleInsertMode();
            s.GoTo(0);
            s.Delete();
            Assert.Equal(3, doc.Length);
        }
    }

    [Fact]
    public void PastedRangeCanBeLeftUnselected()
    {
        (Document doc, EditorState s) = Create(8, new EditingOptions { SelectPasted = false });
        using (doc)
        {
            s.GoTo(2);
            s.Paste([0xAA, 0xBB], overwrite: true);
            Assert.False(s.HasSelection);
            Assert.Equal(4, s.Cursor);
        }
    }

    [Fact]
    public void OverwritePasteCanFitTheSelection()
    {
        (Document doc, EditorState s) = Create(8, new EditingOptions { FitOverwritePasteToSelection = true });
        using (doc)
        {
            s.Select(2, 2);
            s.Paste([0xAA, 0xBB, 0xCC, 0xDD], overwrite: true);
            Assert.Equal(new byte[] { 0x10, 0x11, 0xAA, 0xBB, 0x14, 0x15, 0x16, 0x17 }, ReadAll(doc.Current));
        }
    }

    [Fact]
    public void HexCopyFormatFollowsTheSettings()
    {
        byte[] bytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x01];
        Assert.Equal("DE AD BE EF 01", HexCopyFormat.Default.Format(bytes));
        var custom = new HexCopyFormat { Separator = ",", UpperCase = false, BytesPerLine = 2 };
        Assert.Equal("de,ad\r\nbe,ef\r\n01", custom.Format(bytes));
        Assert.Equal(custom.Format(bytes).Length, custom.TextLength(bytes.Length));
        Assert.Equal(HexCopyFormat.Default.Format(bytes).Length, HexCopyFormat.Default.TextLength(bytes.Length));
        var none = new HexCopyFormat { Separator = string.Empty };
        Assert.Equal("DEADBEEF01", none.Format(bytes));
        Assert.Equal(10, none.TextLength(bytes.Length));
    }

    [Fact]
    public void SettingsAreReadIntoOptions()
    {
        var values = new Dictionary<string, object>
        {
            [EditingSettings.OverwriteSelectionTypingKey] = "zeroFirst",
            [EditingSettings.TextEnterKey] = "lf",
            [EditingSettings.BackspaceInOverwriteKey] = "zeroAndMove",
            [EditingSettings.DeleteKeepsLengthKey] = true,
            [EditingSettings.SelectPastedKey] = false,
            [EditingSettings.HexSeparatorKey] = ":",
        };
        EditingOptions o = EditingSettings.Read(
            (k, d) => values.TryGetValue(k, out object? v) ? (string)v : d,
            (k, d) => values.TryGetValue(k, out object? v) ? (bool)v : d,
            (k, d) => values.TryGetValue(k, out object? v) ? (int)v : d);
        Assert.True(o.ZeroSelectionBeforeTyping);
        Assert.Equal(TextEnterAction.Lf, o.TextEnter);
        Assert.True(o.BackspaceZeroesInOverwrite);
        Assert.True(o.DeleteKeepsLengthInOverwrite);
        Assert.False(o.SelectPasted);
        Assert.Equal(":", o.HexCopy.Separator);
        Assert.Equal(EditingOptions.Default, EditingSettings.Read((_, d) => d, (_, d) => d, (_, d) => d));
        Assert.Equal(64L * 1024 * 1024, EditingSettings.ClipboardLimitBytes(EditingSettings.DefaultClipboardMaxMiB));
        Assert.Equal(TimeSpan.Zero, EditingSettings.CoalesceInterval(0));
        Assert.Equal(TimeSpan.FromSeconds(10), EditingSettings.CoalesceInterval(99));
    }

    [Fact]
    public void ClearingTheHistoryKeepsTheCurrentState()
    {
        (Document doc, EditorState s) = Create(4, EditingOptions.Default);
        using (doc)
        {
            s.TypeHexDigit('F');
            s.TypeHexDigit('F');
            Assert.True(doc.History.CanUndo);
            doc.ClearHistory();
            Assert.False(doc.History.CanUndo);
            Assert.True(doc.History.IsModified);
            Assert.Equal(0xFF, ReadAll(doc.Current)[0]);
        }
    }

    [Fact]
    public void CoalesceIntervalZeroSeparatesEveryKey()
    {
        (Document doc, EditorState s) = Create(4, EditingOptions.Default);
        using (doc)
        {
            doc.History.CoalesceInterval = TimeSpan.Zero;
            s.TypeHexDigit('A');
            s.TypeHexDigit('B');
            s.Undo();
            Assert.Equal(0xA0, ReadAll(doc.Current)[0]);
            Assert.True(doc.History.CanUndo);
        }
    }
}
