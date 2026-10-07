using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Recovery;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>EDIT-24 大きな範囲のアプリ内クリップボード (範囲の参照と実体化)。</summary>
public sealed class InAppClipboardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-clip").FullName;

    private DocumentOptions Opts() => Options() with { TempDirectory = Path.Combine(_dir, "recovery") };

    private static Document Huge(DocumentOptions options) =>
        new(new FakeByteSource(100 * TestDataCatalog.GiB, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize | SourceCapabilities.CanWrite), options);

    [Fact]
    public void CopyingHugeRangeAndPastingIntoAnotherDocumentDoesNotCopyData()
    {
        using var clipboard = new InAppClipboard();
        using Document source = Huge(Opts());
        using var target = new Document(MemoryByteSource.CreateEmpty("無題 1"), Opts());
        var editor = new EditorState(target) { VisibleRows = 10 };
        editor.ToggleInsertMode();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        InAppClip clip = clipboard.Copy(source, 0, source.Length);
        Assert.Equal(EditResult.Done, editor.Paste(clip.Range, overwrite: false));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");

        Assert.Equal(source.Length, target.Length);
        Assert.Equal(0, target.AddBuffer.Length);
        Assert.Equal(1, target.Current.Tree.PieceCount);
        byte[] expected = new byte[16];
        TestDataCatalog.Sequence(77 * TestDataCatalog.GiB, expected);
        Assert.Equal(expected, Read(target.Current, 77 * TestDataCatalog.GiB, 16));
    }

    [Fact]
    public void PastedContentStaysReadableAfterSourceIsMaterializedAndClosed()
    {
        string path = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        using var clipboard = new InAppClipboard();
        var source = new Document(FileByteSource.Open(path), Opts());
        source.Overwrite(0x100, [0xAA, 0xBB]);
        using var target = new Document(MemoryByteSource.CreateEmpty("無題 1"), Opts());
        var editor = new EditorState(target) { VisibleRows = 10 };
        editor.ToggleInsertMode();
        InAppClip clip = clipboard.Copy(source, 0xF0, 0x100);
        editor.Paste(clip.Range, overwrite: false);
        byte[] expected = Read(source.Current, 0xF0, 0x100);

        // 閉じる前に、参照されている範囲を一時ファイルに書き出す (EDIT-24 の仕様 3)。
        Assert.Single(source.PendingReferences);
        Assert.Equal(0x100, source.PendingReferenceBytes);
        source.MaterializeReferences();
        Assert.Empty(source.PendingReferences);
        Assert.True(clip.Range.IsMaterialized);
        source.Dispose();

        // 元のファイルを外部で書き換えても、貼り付けた内容とクリップボードの内容は変わらない。
        File.WriteAllBytes(path, new byte[0x1000]);
        Assert.Equal(expected, ReadAll(target.Current));
        editor.Click(target.Length, ActiveColumn.Hex, false, false);
        editor.Paste(clip.Range, overwrite: false);
        Assert.Equal([.. expected, .. expected], ReadAll(target.Current));

        // 実体化した一時ファイルは、すべての参照を手放すと消える。
        Assert.Single(Directory.GetFiles(Opts().TempDirectory, "clip-*.bin"));
        clipboard.Clear();
        target.Dispose();
        Assert.Empty(Directory.GetFiles(Opts().TempDirectory, "clip-*.bin"));
    }

    [Fact]
    public void ClosingSourceWithoutMaterializingKeepsDataUntilReferencesAreReleased()
    {
        using var clipboard = new InAppClipboard();
        var source = new Document(new MemoryByteSource(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()), Opts());
        source.Insert(0, [0xEE]);
        string spill = source.AddBuffer.SpillPath;
        InAppClip clip = clipboard.Copy(source, 0, 4);
        source.Dispose();

        // 参照元を閉じても、参照が残っている間はデータを読める (解放が遅れる)。
        byte[] four = new byte[4];
        Assert.True(clip.Range.Read(0, four).IsComplete);
        Assert.Equal(new byte[] { 0xEE, 0, 1, 2 }, four);

        // 手放すと解放される (追加バッファの一時ファイルのフォルダも消える)。
        clipboard.Clear();
        Assert.Null(clip.Range.Owner);
        Assert.False(File.Exists(spill));
    }

    [Fact]
    public void MismatchedSerialDiscardsInAppClipboard()
    {
        using var clipboard = new InAppClipboard();
        using var source = new Document(new MemoryByteSource(new byte[64]), Opts());
        InAppClip clip = clipboard.Copy(source, 0, 8);
        Assert.Same(clip, clipboard.Match(clip.Serial));
        Assert.Null(clipboard.Match(clip.Serial + 1));
        Assert.Null(clipboard.Current);
        Assert.Empty(source.PendingReferences);
    }

    [Fact]
    public void PasteWithinSameDocumentSharesPieces()
    {
        using var clipboard = new InAppClipboard();
        using var doc = new Document(new MemoryByteSource(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()), Opts());
        var editor = new EditorState(doc) { VisibleRows = 10 };
        editor.ToggleInsertMode();
        InAppClip clip = clipboard.Copy(doc, 8, 8);
        editor.Click(0, ActiveColumn.Hex, false, false);
        editor.Paste(clip.Range, overwrite: false);
        Assert.Equal(Enumerable.Range(8, 8).Select(i => (byte)i).ToArray(), Read(doc.Current, 0, 8));
        Assert.DoesNotContain(doc.Current.Tree.EnumerateAll(), p => p.Piece.Kind == PieceKind.External);
    }

    [Fact]
    public void RecoveryKeepsPastedExternalData()
    {
        string root = Opts().TempDirectory;
        using var clipboard = new InAppClipboard();
        var source = new Document(new MemoryByteSource(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()), Opts());
        var target = new Document(MemoryByteSource.CreateEmpty("無題 1"), Opts());
        var recovery = new DocumentRecovery(root, target.Id);
        var editor = new EditorState(target) { VisibleRows = 10 };
        editor.ToggleInsertMode();
        editor.Paste(clipboard.Copy(source, 16, 32).Range, overwrite: false);
        editor.Click(target.Length, ActiveColumn.Hex, false, false);
        editor.TypeHexDigit('F');
        byte[] expected = ReadAll(target.Current);
        recovery.Write(DocumentRecovery.Capture(target, 0, 0, 0)!);

        // 異常終了: 参照元も含めて何も閉じずにハンドルを放す。
        recovery.Release();
        target.AddBuffer.DisposeKeepingFile();
        clipboard.Clear();
        source.Dispose();

        RecoveryEntry entry = Assert.Single(RecoveryStore.Scan(root), e => e.Record.DocumentId == target.Id);
        Assert.Single(entry.Record.Externals);
        RestoredDocument restored = RecoveryStore.Restore(entry, Opts());
        using (restored.Document)
        {
            Assert.Equal(expected, ReadAll(restored.Document.Current));

            // 復旧したドキュメントの次の書き出しでも、同じファイルを使う (コピーし直さない)。
            restored.Recovery.Write(DocumentRecovery.Capture(restored.Document, 0, 0, 0)!);
            Assert.Single(Directory.GetFiles(entry.Folder, "ext-*.bin"));
        }

        restored.Recovery.Dispose();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
