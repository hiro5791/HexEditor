using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>EDIT-21 分岐する Undo 履歴 (木、名前付け、固定、削除、保存と読み込み)。</summary>
public sealed class BranchingHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "hist-" + Guid.NewGuid().ToString("N"));

    public BranchingHistoryTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Seq1M()
    {
        byte[] data = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, data);
        return data;
    }

    private string WriteSeq()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.WriteAllBytes(path, Seq1M());
        return path;
    }

    private static Document Branching(IByteSource source)
    {
        var doc = new Document(source, Options());
        doc.History.BranchingEnabled = true;
        return doc;
    }

    /// <summary>TC-EDIT-21-01 の手順 1〜4: A、B を編集し、B を元に戻して C を編集する。</summary>
    private static (HistoryNode A, HistoryNode B, HistoryNode C) EditABC(Document doc)
    {
        doc.Overwrite(0x10, "AA"u8);
        HistoryNode a = doc.History.CurrentNode;
        doc.Overwrite(0x20, "BB"u8);
        HistoryNode b = doc.History.CurrentNode;
        doc.Undo();
        doc.Overwrite(0x30, "CC"u8);
        return (a, b, doc.History.CurrentNode);
    }

    private static string Values(Document doc) =>
        string.Join(" ", new long[] { 0x10, 0x20, 0x30 }.Select(o => Convert.ToHexString(Read(doc.Current, o, 2))));

    [Fact]
    [Trait(TC, "TC-EDIT-21-01")]
    public void UndoThenEditMakesBranchAndMovingToNodeRestoresState()
    {
        using Document doc = Branching(new MemoryByteSource(Seq1M()));
        (HistoryNode a, HistoryNode b, HistoryNode c) = EditABC(doc);

        // 根の下に A、A の下に B と C の 2 つの枝。現在の節は C。
        Assert.Same(doc.History.Root, a.Parent);
        Assert.Equal([b, c], a.Children);
        Assert.Same(c, doc.History.CurrentNode);
        Assert.Equal(4, doc.History.NodeCount);

        doc.MoveToHistoryNode(b);
        Assert.Equal("4141 4242 3031", Values(doc));
        Assert.Same(b, doc.History.CurrentNode);

        doc.MoveToHistoryNode(c);
        Assert.Equal("4141 2021 4343", Values(doc));

        // やり直しは最後に通った子へ進む (仕様 2)。
        doc.Undo();
        doc.Redo();
        Assert.Same(c, doc.History.CurrentNode);
        doc.MoveToHistoryNode(b);
        doc.Undo();
        doc.Redo();
        Assert.Same(b, doc.History.CurrentNode);
    }

    [Fact]
    public void WithoutBranchingNewEditDiscardsRedo()
    {
        using var doc = new Document(new MemoryByteSource(Seq1M()), Options());
        doc.Overwrite(0x10, "AA"u8);
        doc.Overwrite(0x20, "BB"u8);
        doc.Undo();
        doc.Overwrite(0x30, "CC"u8);
        Assert.Equal(3, doc.History.NodeCount);
        Assert.Single(doc.History.NodeAt(1).Children);
        Assert.False(doc.History.CanRedo);
    }

    [Fact]
    public void SavedPointOnOtherBranchStillCountsAsUnmodified()
    {
        using Document doc = Branching(new MemoryByteSource(Seq1M()));
        doc.Overwrite(0x10, "AA"u8);
        doc.Overwrite(0x20, "BB"u8);
        HistoryNode b = doc.History.CurrentNode;
        doc.MarkSaved();
        doc.Undo();
        doc.Overwrite(0x30, "CC"u8);
        Assert.True(doc.IsModified);
        Assert.Equal(-1, doc.History.SavedIndex);
        doc.MoveToHistoryNode(b);
        Assert.False(doc.IsModified);
        Assert.Equal(b.Depth, doc.History.SavedIndex);
    }

    [Fact]
    public void TypingAfterUndoDoesNotCoalesceIntoBranchPoint()
    {
        using Document doc = Branching(new MemoryByteSource(new byte[64]));
        doc.Overwrite(0, "A"u8, coalesceKey: "type");
        doc.Overwrite(1, "B"u8, coalesceKey: "type");
        HistoryNode first = doc.History.CurrentNode;
        doc.Overwrite(10, "X"u8);
        doc.Undo();

        // first は子 (X) を持つので、続けて同じ種類の入力をしてもまとめずに新しい枝にする。
        doc.Overwrite(2, "C"u8, coalesceKey: "type");
        Assert.NotSame(first, doc.History.CurrentNode);
        Assert.Same(first, doc.History.CurrentNode.Parent);
        Assert.Equal(2, first.Children.Count());
    }

    [Fact]
    [Trait(TC, "TC-EDIT-21-02")]
    public void NamedNodesAreListedAndReachable()
    {
        using Document doc = Branching(new MemoryByteSource(Seq1M()));
        (_, HistoryNode b, _) = EditABC(doc);
        doc.History.SetName(b, "before-C");
        Assert.Equal([b], doc.History.NamedNodes());
        Assert.Equal("before-C", doc.History.NamedNodes()[0].Name);

        doc.MoveToHistoryNode(doc.History.NamedNodes()[0]);
        Assert.Equal("4242", Convert.ToHexString(Read(doc.Current, 0x20, 2)));
        Assert.Equal("3031", Convert.ToHexString(Read(doc.Current, 0x30, 2)));
        Assert.Same(b, doc.History.CurrentNode);

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.History.SetName(b, new string('x', 101)));
        doc.History.SetName(b, new string('x', 100));
        doc.History.SetName(b, "  ");
        Assert.Null(b.Name);
    }

    [Fact]
    public void DeleteBranchKeepsCurrentAndAncestors()
    {
        using Document doc = Branching(new MemoryByteSource(Seq1M()));
        (HistoryNode a, HistoryNode b, HistoryNode c) = EditABC(doc);
        Assert.False(doc.History.CanDelete(c));
        Assert.False(doc.History.CanDelete(a));
        Assert.False(doc.History.CanDelete(doc.History.Root));
        Assert.True(doc.History.CanDelete(b));
        doc.History.DeleteBranch(b);
        Assert.Equal([c], a.Children);
        Assert.Equal(3, doc.History.NodeCount);

        // やり直しの経路にある枝を消すと、残った子をたどり直す。
        doc.Undo();
        Assert.True(doc.History.CanRedo);
        doc.Overwrite(0x40, "DD"u8);
        HistoryNode d = doc.History.CurrentNode;
        doc.MoveToHistoryNode(c);
        doc.Undo();
        Assert.Same(c, doc.History.NodeAt(doc.History.CurrentIndex + 1));
        doc.History.DeleteBranch(c);
        Assert.Same(d, doc.History.NodeAt(doc.History.CurrentIndex + 1));
    }

    [Fact]
    public void PrunableBranchesSkipPinnedNamedAndCurrentPath()
    {
        using Document doc = Branching(new MemoryByteSource(Seq1M()));
        (HistoryNode a, HistoryNode b, HistoryNode c) = EditABC(doc);
        doc.Undo();
        doc.Overwrite(0x40, "DD"u8);
        HistoryNode d = doc.History.CurrentNode;

        // 経路は 根 → A → D。B と C は枝。
        Assert.Equal([b, c], doc.History.PrunableBranches());
        doc.History.SetPinned(b, true);
        doc.History.SetName(c, "keep");
        Assert.Empty(doc.History.PrunableBranches());
        doc.History.SetPinned(b, false);
        Assert.Equal([b], doc.History.PrunableBranches());
        _ = a;
        _ = d;
    }

    [Fact]
    [Trait(TC, "TC-EDIT-21-03")]
    public void SaveAndLoadRestoresTreeAndEveryNodeState()
    {
        string path = WriteSeq();
        string hist = Path.Combine(_dir, "t.hexhist");
        int aId, bId, cId;
        using (Document doc = Branching(FileByteSource.Open(path)))
        {
            (HistoryNode a, HistoryNode b, HistoryNode c) = EditABC(doc);
            doc.History.SetName(b, "before-C");
            (aId, bId, cId) = (a.Id, b.Id, c.Id);
            using var output = File.Create(hist);
            HistoryArchive.Save(doc, output);
        }

        // 閉じて開き直したドキュメントに読み込む。
        using (Document doc = Branching(FileByteSource.Open(path)))
        {
            using var input = File.OpenRead(hist);
            HistoryArchiveInfo info = HistoryArchive.ReadInfo(input);
            Assert.True(HistoryArchive.Matches(doc, info));
            Assert.Equal(4, info.NodeCount);
            HistoryArchive.Load(doc, input);

            HistoryNode root = doc.History.Root;
            HistoryNode a = Assert.Single(root.Children);
            Assert.Equal(aId, a.Id);
            Assert.Equal([bId, cId], a.Children.Select(n => n.Id));
            HistoryNode b = doc.History.FindNode(bId)!, c = doc.History.FindNode(cId)!;
            Assert.Equal("before-C", b.Name);
            Assert.Same(c, doc.History.CurrentNode);

            doc.MoveToHistoryNode(root);
            Assert.Equal("1011 2021 3031", Values(doc));
            Assert.False(doc.IsModified);
            doc.MoveToHistoryNode(a);
            Assert.Equal("4141 2021 3031", Values(doc));
            doc.MoveToHistoryNode(b);
            Assert.Equal("4141 4242 3031", Values(doc));
            doc.MoveToHistoryNode(c);
            Assert.Equal("4141 2021 4343", Values(doc));

            // 読み込んだ後の編集は続きの番号になる。
            doc.Overwrite(0, "Z"u8);
            Assert.True(doc.History.CurrentNode.Id > cId);
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-21-04")]
    public void DifferentFileDoesNotMatchIdentity()
    {
        string path = WriteSeq();
        string other = Path.Combine(_dir, "zero.bin");
        File.WriteAllBytes(other, new byte[TestDataCatalog.MiB]);
        string hist = Path.Combine(_dir, "t.hexhist");
        using (Document doc = Branching(FileByteSource.Open(path)))
        {
            EditABC(doc);
            using var output = File.Create(hist);
            HistoryArchive.Save(doc, output);
        }

        using (Document doc = Branching(FileByteSource.Open(other)))
        {
            using var input = File.OpenRead(hist);
            HistoryArchiveInfo info = HistoryArchive.ReadInfo(input);
            Assert.False(HistoryArchive.Matches(doc, info));

            // 読み込まなければ履歴は空のまま、データも変わらない。
            Assert.Equal(1, doc.History.NodeCount);
            Assert.All(Read(doc.Current, 0, 64), b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void BrokenArchiveIsReported()
    {
        string path = WriteSeq();
        string hist = Path.Combine(_dir, "bad.hexhist");
        File.WriteAllBytes(hist, [1, 2, 3, 4]);
        using Document doc = Branching(FileByteSource.Open(path));
        using var input = File.OpenRead(hist);
        Assert.Throws<HistoryArchiveException>(() => HistoryArchive.ReadInfo(input));
    }

    [Fact]
    public void RandomEditsRoundTripThroughArchive()
    {
        string path = WriteSeq();
        string hist = Path.Combine(_dir, "r.hexhist");
        var rng = new Random(2101);
        var hashes = new Dictionary<int, byte[]>();
        using (Document doc = Branching(FileByteSource.Open(path)))
        {
            for (int i = 0; i < 300; i++)
            {
                if (doc.History.CanUndo && rng.Next(4) == 0)
                {
                    doc.Undo();
                    continue;
                }

                long pos = rng.NextInt64(doc.Length);
                byte[] data = new byte[rng.Next(1, 64)];
                rng.NextBytes(data);
                switch (rng.Next(5))
                {
                    case 0:
                        doc.Insert(pos, data);
                        break;
                    case 1:
                        doc.Delete(pos, Math.Min(data.Length, doc.Length - pos));
                        break;
                    case 2:
                        doc.OverwritePattern(pos, rng.Next(1, 5000), data.AsSpan(0, Math.Min(4, data.Length)));
                        break;
                    case 3:
                        doc.InsertRandom(pos, rng.Next(1, 100), (ulong)i);
                        break;
                    default:
                        doc.Overwrite(pos, data);
                        break;
                }
            }

            foreach (HistoryNode node in doc.History.AllNodes())
            {
                hashes[node.Id] = Sha256(node.Entry.Snapshot);
            }

            using var output = File.Create(hist);
            HistoryArchive.Save(doc, output);
        }

        using (Document doc = Branching(FileByteSource.Open(path)))
        {
            using var input = File.OpenRead(hist);
            HistoryArchive.Load(doc, input);
            Assert.Equal(hashes.Count, doc.History.NodeCount);
            foreach (HistoryNode node in doc.History.AllNodes().ToList())
            {
                doc.MoveToHistoryNode(node);
                Assert.Equal(hashes[node.Id], Sha256(doc.Current));
            }
        }
    }

    [Fact]
    public void BookmarksFollowBranchSwitch()
    {
        using Document doc = Branching(new MemoryByteSource(new byte[256]));
        BookmarkCollection bookmarks = BookmarkCollection.Attach(doc);
        Bookmark mark = bookmarks.Add(100, 1, "m");
        doc.Insert(0, new byte[10]);      // A: ブックマークは 110
        HistoryNode a = doc.History.CurrentNode;
        doc.Undo();                       // 100
        doc.Insert(0, new byte[20]);      // B: 120
        Assert.Equal(120, mark.Start);
        doc.MoveToHistoryNode(a);
        Assert.Equal(110, mark.Start);
        doc.MoveToHistoryNode(doc.History.Root);
        Assert.Equal(100, mark.Start);
    }
}
