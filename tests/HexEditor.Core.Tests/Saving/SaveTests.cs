using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-20〜ENG-22 の保存 (安全な保存)。</summary>
public sealed class SaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-save").FullName;

    private string CopyOf(string id, string name)
    {
        string path = Path.Combine(_dir, name);
        File.Copy(TestDataCatalog.Get(id), path);
        return path;
    }

    private static void Save(Document doc, string path)
    {
        FileByteSource saved = DocumentSaver.Save(doc.Current, path);
        doc.CompleteSave(saved);
    }

    [Fact]
    public void SafeSaveWritesContentAndClearsModified()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0x100, Enumerable.Repeat((byte)0x11, 16).ToArray());
        byte[] expected = ReadAll(doc.Current);

        Save(doc, path);

        Assert.False(doc.IsModified);
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(1, doc.Current.Tree.PieceCount);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    /// <summary>TC-ENG-05-04 の安全な保存の部分 (その場保存の部分は ENG-23 の実装時に追加する)。</summary>
    [Fact]
    public void UndoAfterSafeSaveRestoresPreSaveContent()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 手順 1・2: 挿入して保存し、Undo すると元の内容に戻る。
        doc.Insert(0x100, Enumerable.Repeat((byte)0x11, 16).ToArray());
        Save(doc, path);
        doc.Undo();
        Assert.Equal(SHA256.HashData(original), Sha256(doc.Current));
        Assert.True(doc.IsModified);

        // 手順 5: その状態でもう一度保存すると、ファイルも元に戻る。
        Save(doc, path);
        Assert.Equal(original, File.ReadAllBytes(path));

        // Redo で保存した内容にも戻れる (同じ時点の印が付いている)。
        doc.Redo();
        Assert.Equal(original.Length + 16, doc.Length);
    }

    [Fact]
    public void SaveAsToNewPathLeavesOriginalUnchanged()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        string other = Path.Combine(_dir, "b.bin");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0, [0xFF]);

        Save(doc, other);

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0xFF, File.ReadAllBytes(other)[0]);
        Assert.Equal(other, ((FileByteSource)doc.Source).Path);
    }

    [Fact]
    public void CancelledSaveKeepsOriginalAndRemovesTempFile()
    {
        string path = CopyOf("TD-SEQ-1M", "a.bin");
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.InsertPattern(0, 64 * TestDataCatalog.MiB, [0xAB]);

        var center = new OperationCenter();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            center.RunAsync("保存", OperationKind.WritesExternal, doc, doc.Length, op =>
            {
                op.Cancel();
                DocumentSaver.Save(doc.Current, path, op);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult());

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_dir, "*", new EnumerationOptions { AttributesToSkip = 0 }));
        Assert.True(doc.IsModified);
    }

    [Fact]
    public void UnreadableSourceCannotBeSaved()
    {
        var source = new Support.FakeByteSource(4096, (o, s) => TestDataCatalog.Sequence(o, s), SourceCapabilities.CanResize);
        source.BadRanges.Add(new UnreadableRange(100, 10, UnreadableReason.IoError));
        using var doc = new Document(source, Options());
        string target = Path.Combine(_dir, "out.bin");
        Assert.Throws<UnreadableDataException>(() => DocumentSaver.Save(doc.Current, target));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_dir, "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public void FreeSpaceIsCheckedBeforeWriting()
    {
        var ex = Assert.Throws<InsufficientSpaceException>(() => DocumentSaver.CheckFreeSpace(_dir, long.MaxValue / 2));
        Assert.True(ex.Required > ex.Available);
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
