using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-23 その場保存。</summary>
[Collection("InPlaceSaver")]
public sealed class InPlaceSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-inplace").FullName;

    private string Journals => Path.Combine(_dir, "recovery");

    private static void SaveInPlace(Document doc, string journals)
    {
        Assert.True(InPlaceSaver.CanSaveInPlace(doc.Current, ((FileByteSource)doc.Source).Path));
        doc.CompleteInPlaceSave(InPlaceSaver.Save(doc.Current, journals));
    }

    /// <summary>アプリと同じ選び方: その場保存できればその場保存、できなければ安全な保存 (ENG-20 の仕様 1)。</summary>
    private static void Save(Document doc, string path, string journals)
    {
        if (InPlaceSaver.CanSaveInPlace(doc.Current, path))
        {
            doc.CompleteInPlaceSave(InPlaceSaver.Save(doc.Current, journals));
        }
        else
        {
            doc.CompleteSave(DocumentSaver.Save(doc.Current, path));
        }
    }

    [Fact]
    public void WritesOnlyChangedBytesOfHugeFileAndKeepsUndo()
    {
        // 1 GiB のスパースファイル (先頭・2^20 ごと・末尾に目印)。
        string path = TestDataCatalog.Generate("TD-MARKERS-1G", _dir);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0x20000000, [0xAA, 0xBB, 0xCC, 0xDD]);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SaveInPlace(doc, Journals);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.False(doc.IsModified);
        Assert.Empty(Directory.GetFiles(Journals));

        using (var check = FileByteSource.Open(path))
        {
            byte[] four = new byte[4];
            check.Read(0x20000000, four);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, four);
            byte[] marker = new byte[TestDataCatalog.MarkerLength];
            check.Read(0x20100000, marker);
            Assert.Equal(TestDataCatalog.Marker(0x20100000), marker);
        }

        // Undo すると保存前の内容 (元の目印) に戻る。
        doc.Undo();
        Assert.Equal(TestDataCatalog.Marker(0x20000000), Read(doc.Current, 0x20000000, TestDataCatalog.MarkerLength));
        doc.Redo();
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, Read(doc.Current, 0x20000000, 4));
    }

    [Fact]
    [Trait(TC, "TC-ENG-05-04")]
    public void UndoAfterSafeAndInPlaceSaveRestoresOriginal()
    {
        string a = Path.Combine(_dir, "a.bin");
        string b = Path.Combine(_dir, "b.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), a);
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), b);
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(a));

        // 手順 1・2: 挿入して安全な保存、Undo で元に戻る。
        using (var docA = new Document(FileByteSource.Open(a), Options()))
        {
            docA.Insert(0x100, Enumerable.Repeat((byte)0x11, 16).ToArray());
            Assert.False(InPlaceSaver.CanSaveInPlace(docA.Current, a)); // 長さが変わるので安全な保存
            docA.CompleteSave(DocumentSaver.Save(docA.Current, a));
            docA.Undo();
            Assert.Equal(originalHash, Sha256(docA.Current));

            // 手順 5
            docA.CompleteSave(DocumentSaver.Save(docA.Current, a));
            Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(a)));
        }

        // 手順 3・4: 上書きしてその場保存、Undo で元に戻る。
        using var docB = new Document(FileByteSource.Open(b), Options());
        docB.Overwrite(0x100, Enumerable.Repeat((byte)0x22, 16).ToArray());
        SaveInPlace(docB, Journals);
        docB.Undo();
        Assert.Equal(originalHash, Sha256(docB.Current));

        // 手順 5: 保存前の版に戻った状態は元データが重ね合わせなので、安全な保存が選ばれる。
        Save(docB, b, Journals);
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(b)));
    }

    [Fact]
    public void RepeatedInPlaceSavesKeepEveryHistoryPointExact()
    {
        string path = Path.Combine(_dir, "c.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        var hashes = new List<byte[]> { Sha256(doc.Current) };
        var rng = new Random(23);
        for (int save = 0; save < 5; save++)
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] data = new byte[rng.Next(1, 300)];
                rng.NextBytes(data);
                doc.Overwrite(rng.Next(0, (int)doc.Length - data.Length), data);
                hashes.Add(Sha256(doc.Current));
            }

            SaveInPlace(doc, Journals);
        }

        for (int point = hashes.Count - 1; point >= 0; point--)
        {
            Assert.Equal(hashes[point], Sha256(doc.Current));
            if (point > 0)
            {
                doc.Undo();
            }
        }
    }

    [Fact]
    public void JournalRestoresFileAfterInterruptedSave()
    {
        string path = Path.Combine(_dir, "d.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0x1000, Enumerable.Repeat((byte)0xEE, 0x2000).ToArray());

        // ジャーナルを書いた直後に「落ちた」ことにし、そこで書きかけを再現するため一部を手で書き換える。
        InPlaceSaver.AfterJournalWritten = () =>
        {
            using var w = File.OpenWrite(path);
            w.Position = 0x1000;
            w.Write(new byte[0x800]);
            throw new IOException("simulated crash");
        };
        try
        {
            Assert.Throws<IOException>(() => InPlaceSaver.Save(doc.Current, Journals));
        }
        finally
        {
            InPlaceSaver.AfterJournalWritten = null;
        }

        string journal = Assert.Single(Directory.GetFiles(Journals));
        Assert.Equal(path, InPlaceSaver.Rollback(journal));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Journals));
    }

    [Fact]
    public void LargeChangesExceedJournalLimit()
    {
        string path = Path.Combine(_dir, "e.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.OverwritePattern(0, 0x80000, [0x00]);
        Assert.Throws<JournalLimitException>(() => InPlaceSaver.Save(doc.Current, Journals, journalLimit: 0x1000));
        Assert.Equal(File.ReadAllBytes(TestDataCatalog.Get("TD-SEQ-1M")), File.ReadAllBytes(path));
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
