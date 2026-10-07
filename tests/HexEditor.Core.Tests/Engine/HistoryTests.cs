using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-05 永続木によるスナップショット。</summary>
public sealed class HistoryTests
{
    private static byte[] Seq1M()
    {
        byte[] data = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, data);
        return data;
    }

    private static void RandomEdit(Document doc, Random rng)
    {
        long length = doc.Length;
        long pos = length == 0 ? 0 : rng.NextInt64(length);
        int size = rng.Next(1, 4097);
        byte[] data = new byte[size];
        rng.NextBytes(data);
        switch (rng.Next(4))
        {
            case 0:
                doc.Insert(pos, data);
                break;
            case 1 when length > 0:
                doc.Delete(pos, Math.Min(size, length - pos));
                break;
            case 2:
                doc.Overwrite(pos, data);
                break;
            default:
                doc.OverwritePattern(pos, size, data.AsSpan(0, Math.Min(size, rng.Next(1, 8))));
                break;
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-05-02")]
    public void UndoToAnyPointAfterTenThousandEditsRestoresContent()
    {
        using var doc = new Document(new MemoryByteSource(Seq1M()), Options());
        var rng = new Random(502);
        var hashes = new List<byte[]> { Sha256(doc.Current) };
        for (int i = 0; i < 10_000; i++)
        {
            RandomEdit(doc, rng);
            hashes.Add(Sha256(doc.Current));
        }

        Assert.Equal(10_001, doc.History.Count);

        var pick = new Random(5021);
        int[] points = [0, 1, 5000, 9999, .. Enumerable.Range(0, 46).Select(_ => pick.Next(0, 10_001))];
        foreach (int point in points)
        {
            while (doc.History.CurrentIndex > point)
            {
                doc.Undo();
            }

            Assert.Equal(hashes[point], Sha256(doc.Current));
            while (doc.History.CanRedo)
            {
                doc.Redo();
            }
        }

        Assert.Equal(hashes[^1], Sha256(doc.Current));
    }

    [Fact]
    public void SnapshotIsUnaffectedByLaterEdits()
    {
        using var doc = new Document(new MemoryByteSource(Seq1M()), Options());
        DocumentSnapshot before = doc.Current;
        byte[] expected = ReadAll(before);
        doc.Insert(0, new byte[1024 * 1024]);
        doc.OverwritePattern(0x20000000 % doc.Length, 16, [0]);
        Assert.Equal(expected, ReadAll(before));
    }

    [Fact]
    public void ModifiedFlagFollowsSavedPoint()
    {
        using var doc = new Document(new MemoryByteSource(Seq1M()), Options());
        Assert.False(doc.IsModified);
        doc.Overwrite(0, [0xAA]);
        doc.MarkSaved();
        Assert.False(doc.IsModified);
        doc.Overwrite(1, [0xBB]);
        Assert.True(doc.IsModified);
        doc.Undo();
        Assert.False(doc.IsModified);
        doc.Undo();
        Assert.True(doc.IsModified);
    }

    [Fact]
    public void TypingIsCoalescedIntoOneUndoStep()
    {
        using var doc = new Document(new MemoryByteSource(Seq1M()), Options());
        for (int i = 0; i < 10; i++)
        {
            doc.Insert(i, [0x41], coalesceKey: "typing");
        }

        Assert.Equal(2, doc.History.Count);
        doc.Undo();
        Assert.Equal(TestDataCatalog.MiB, doc.Length);
    }
}
