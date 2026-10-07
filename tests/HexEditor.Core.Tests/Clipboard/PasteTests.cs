using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>EDIT-22〜EDIT-24 のコピー・貼り付け (Core の部分)。</summary>
public sealed class PasteTests
{
    private static (Document Doc, EditorState State) Create(int length, bool resizable = true)
    {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var caps = resizable ? SourceCapabilities.CanResize | SourceCapabilities.CanWrite : SourceCapabilities.CanWrite;
        var doc = new Document(new FakeByteSource(data, caps), Options());
        return (doc, new EditorState(doc) { VisibleRows = 10 });
    }

    [Fact]
    public void HexTextRoundTrips()
    {
        Assert.Equal("DE AD BE EF", HexText.Format([0xDE, 0xAD, 0xBE, 0xEF]));
        Assert.Equal(new byte[] { 0xDE, 0xAD }, HexText.TryParse("0xDE, 0xAD"));
        Assert.Null(HexText.TryParse("DE A"));
        Assert.Null(HexText.TryParse("DE ??"));
        Assert.Null(HexText.TryParse("hello"));
    }

    [Fact]
    public void InsertModePasteReplacesSelection()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            s.ToggleInsertMode();
            s.Select(2, 3);
            Assert.Equal(EditResult.Done, s.Paste([0xAA, 0xBB], overwrite: false));
            Assert.Equal(new byte[] { 0, 1, 0xAA, 0xBB, 5, 6, 7 }, ReadAll(doc.Current));
            Assert.Equal((2L, 2L), (s.SelectionStart, s.SelectionLength));
        }
    }

    [Fact]
    public void OverwritePasteKeepsLength()
    {
        (Document doc, EditorState s) = Create(8);
        using (doc)
        {
            s.ToggleInsertMode();
            s.Click(6, ActiveColumn.Hex, false, false);
            s.Paste([0xAA, 0xBB, 0xCC], overwrite: true);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 0xAA, 0xBB, 0xCC }, ReadAll(doc.Current));
        }
    }

    [Fact]
    public void FixedLengthPasteIsTruncatedAtEnd()
    {
        (Document doc, EditorState s) = Create(8, resizable: false);
        using (doc)
        {
            s.Click(6, ActiveColumn.Hex, false, false);

            // ENG-07 の仕様 5: 切り詰める前に確認する。確認するまでは何も変えない。
            Assert.Equal(1, s.PasteOverflow(3));
            Assert.Equal(EditResult.NeedsTruncateConfirmation, s.Paste([0xAA, 0xBB, 0xCC], overwrite: false));
            Assert.False(doc.IsModified);

            // 「末尾まで貼り付ける」
            Assert.Equal(EditResult.Truncated, s.Paste([0xAA, 0xBB, 0xCC], overwrite: false, allowTruncate: true));
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 0xAA, 0xBB }, ReadAll(doc.Current));
        }
    }

    [Fact]
    public void CopyWithinDocumentSharesPiecesForHugeRanges()
    {
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        var s = new EditorState(doc) { VisibleRows = 10 };
        s.ToggleInsertMode();
        DocumentSnapshot copied = doc.Current;
        s.Click(0, ActiveColumn.Hex, false, false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        s.Paste(copied, 1L << 32, 10 * TestDataCatalog.GiB, overwrite: false);
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(110 * TestDataCatalog.GiB, doc.Length);
        Assert.Equal(TestDataCatalog.Marker(1L << 32), Read(doc.Current, 0, 17));
        Assert.Equal(0, doc.AddBuffer.Length);
    }

    [Fact]
    public void PasteFromAnotherDocumentReferencesData()
    {
        (Document a, EditorState sa) = Create(16);
        (Document b, EditorState sb) = Create(4);
        using (b)
        {
            sb.ToggleInsertMode();
            sb.Paste(a.Current, 4, 4, overwrite: false);
            Assert.Equal(new byte[] { 4, 5, 6, 7, 0, 1, 2, 3 }, ReadAll(b.Current));
            Assert.Equal(0, b.AddBuffer.Length); // 複製しない (EDIT-24 の仕様 2)

            // 参照元を閉じても、貼り付けた内容は読める。
            a.Dispose();
            Assert.Equal(new byte[] { 4, 5, 6, 7, 0, 1, 2, 3 }, ReadAll(b.Current));
        }
    }

    [Fact]
    public void SnapshotFromBeforeSaveIsCopiedNotShared()
    {
        string dir = Directory.CreateTempSubdirectory("hexeditor-paste").FullName;
        string path = Path.Combine(dir, "a.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        using (var doc = new Document(FileByteSource.Open(path), Options()))
        {
            DocumentSnapshot beforeSave = doc.Current;
            doc.Insert(0, [0xFF, 0xFF]);
            doc.CompleteSave(DocumentSaver.Save(doc.Current, path));
            var s = new EditorState(doc) { VisibleRows = 10 };
            s.ToggleInsertMode();
            s.Click(doc.Length, ActiveColumn.Hex, false, false);
            s.Paste(beforeSave, 0, 4, overwrite: false);
            Assert.Equal(new byte[] { 0, 1, 2, 3 }, Read(doc.Current, doc.Length - 4, 4));
        }

        Directory.Delete(dir, recursive: true);
    }
}
