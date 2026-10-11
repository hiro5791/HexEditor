using System.Security.Cryptography;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-02 ピースツリー。</summary>
public sealed class PieceTreeTests
{
    public enum OpKind
    {
        Insert,
        Delete,
        Overwrite,
        Copy,
        FillPattern,
        InsertPattern,
    }

    /// <summary>操作 1 つ。位置と長さは、適用時のドキュメントの長さに合わせて丸める。</summary>
    public sealed record Op(OpKind Kind, uint A, uint B, uint C, byte Value);

    private static byte[] Seq(int length)
    {
        byte[] data = new byte[length];
        TestDataCatalog.Sequence(0, data);
        return data;
    }

    /// <summary>無作為な位置。0、末尾、末尾 − 1 を高い頻度で選ぶ。</summary>
    private static int Position(uint r, int length) => (r % 8) switch
    {
        0 => 0,
        1 => length,
        2 => Math.Max(0, length - 1),
        _ => (int)(r / 8 % (uint)(length + 1)),
    };

    private static void Apply(Document doc, ReferenceModel model, Op op)
    {
        int length = model.Length;
        int pos = Position(op.A, length);
        int size = (int)(op.B % 4096) + 1;
        byte[] data = Enumerable.Range(0, size).Select(i => (byte)(op.Value + i)).ToArray();
        switch (op.Kind)
        {
            case OpKind.Insert:
                doc.Insert(pos, data);
                model.Insert(pos, data);
                break;
            case OpKind.Delete when length > 0:
                pos = Math.Min(pos, length - 1);
                int del = Math.Min(size, length - pos);
                doc.Delete(pos, del);
                model.Delete(pos, del);
                break;
            case OpKind.Overwrite:
                doc.Overwrite(pos, data);
                model.Overwrite(pos, data);
                break;
            case OpKind.Copy when length > 0:
                int src = Math.Min(Position(op.C, length), length - 1);
                int len = Math.Min(size, length - src);
                doc.InsertCopy(pos, src, len);
                model.InsertCopy(pos, src, len);
                break;
            case OpKind.FillPattern:
                byte[] pattern = data[..Math.Min(size, (int)(op.C % 7) + 1)];
                doc.OverwritePattern(pos, size, pattern);
                model.Overwrite(pos, ReferenceModel.Repeat(pattern, size));
                break;
            case OpKind.InsertPattern:
                byte[] p2 = data[..Math.Min(size, (int)(op.C % 5) + 1)];
                doc.InsertPattern(pos, size, p2);
                model.Insert(pos, ReferenceModel.Repeat(p2, size));
                break;
        }
    }

    private static void AssertMatches(Document doc, ReferenceModel model)
    {
        Assert.Equal(model.Length, doc.Length);
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(model.ToArray()), "内容が基準モデルと違います。");
        var modified = new HashSet<long>();
        foreach ((long offset, long length) in doc.Current.EnumerateModifiedRanges())
        {
            for (long i = offset; i < offset + length; i++)
            {
                modified.Add(i);
            }
        }

        foreach (int p in model.ModifiedPositions())
        {
            Assert.Contains(p, modified);
        }
    }

    private static void RunRandomSequences(byte[] original, int sequences, int opsPerSequence, int seed)
    {
        var rng = new Random(seed);
        for (int s = 0; s < sequences; s++)
        {
            using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
            var model = new ReferenceModel(original);
            for (int i = 0; i < opsPerSequence; i++)
            {
                var op = new Op((OpKind)rng.Next(6), (uint)rng.Next(), (uint)rng.Next(), (uint)rng.Next(), (byte)rng.Next(256));
                Apply(doc, model, op);
            }

            AssertMatches(doc, model);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-02-03")]
    public void RandomOperationsMatchReferenceModel()
    {
        // プルリクエストごとの規模。100 万回の全規模は Nightly のテストで行う。
        RunRandomSequences(Seq(1024 * 1024), sequences: 20, opsPerSequence: 1000, seed: 2026);
        RunRandomSequences([], sequences: 100, opsPerSequence: 1000, seed: 2027);
    }

    [Fact]
    [Trait(TC, "TC-ENG-02-03")]
    [Trait("Category", "Nightly")]
    public void RandomOperationsMatchReferenceModelFullScale()
    {
        RunRandomSequences(Seq(1024 * 1024), sequences: 1000, opsPerSequence: 1000, seed: 3026);
        RunRandomSequences([], sequences: 100, opsPerSequence: 1000, seed: 3027);
    }

    private static Arbitrary<Op[]> Ops() =>
        (from kind in Gen.Elements(Enum.GetValues<OpKind>())
         from a in ArbMap.Default.GeneratorFor<uint>()
         from b in Gen.Choose(0, 64).Select(i => (uint)i)
         from c in ArbMap.Default.GeneratorFor<uint>()
         from v in ArbMap.Default.GeneratorFor<byte>()
         select new Op(kind, a, b, c, v)).ArrayOf().ToArbitrary();

    /// <summary>失敗した場合に FsCheck が最小の操作列に縮める (TC-ENG-02-03 の手順 4)。</summary>
    [Property(MaxTest = 300)]
    [Trait(TC, "TC-ENG-02-03")]
    public Property ShrinkableOperationSequencesMatchModel() => Prop.ForAll(Ops(), ops =>
    {
        byte[] original = Seq(300);
        using var doc = new Document(new MemoryByteSource((byte[])original.Clone()), Options());
        var model = new ReferenceModel(original);
        foreach (Op op in ops)
        {
            Apply(doc, model, op);
        }

        AssertMatches(doc, model);
    });

    [Fact]
    [Trait(TC, "TC-ENG-02-04")]
    public void ConsecutiveTypingDoesNotMultiplyPieces()
    {
        using var doc = new Document(new MemoryByteSource(Seq(1024 * 1024)), Options());
        for (int i = 0; i < 1000; i++)
        {
            doc.Insert(0x8000 + i, [(byte)i], coalesceKey: "typing");
        }

        Assert.Equal(3, doc.Current.Tree.PieceCount);

        for (int i = 0; i < 1000; i++)
        {
            doc.Overwrite(0x20000 + i, [(byte)i], coalesceKey: "typing");
        }

        Assert.True(doc.Current.Tree.PieceCount <= 6, $"ピースの数: {doc.Current.Tree.PieceCount}");
    }

    [Fact]
    [Trait(TC, "TC-ENG-02-05")]
    public void OriginalFileIsNotModifiedUntilSaved()
    {
        string dir = Directory.CreateTempSubdirectory("hexeditor-eng0205").FullName;
        string path = Path.Combine(dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        byte[] hashBefore = SHA256.HashData(File.ReadAllBytes(path));
        DateTime timeBefore = File.GetLastWriteTimeUtc(path);

        // 書き込みを拒否するハンドルを持ち続ける。エンジンが書き込みのハンドルを開こうとすれば失敗する。
        using (FileStream guard = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var doc = new Document(FileByteSource.Open(path), Options()))
        {
            var model = new ReferenceModel(File.ReadAllBytes(path));
            var rng = new Random(205);
            for (int i = 0; i < 1000; i++)
            {
                Apply(doc, model, new Op((OpKind)rng.Next(6), (uint)rng.Next(), (uint)rng.Next(), (uint)rng.Next(), (byte)rng.Next(256)));
            }

            for (int i = 0; i < 500; i++)
            {
                doc.Undo();
            }

            for (int i = 0; i < 200; i++)
            {
                doc.Redo();
            }

            Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        }

        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(timeBefore, File.GetLastWriteTimeUtc(path));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void TreeStaysBalanced()
    {
        PieceTree tree = PieceTree.Empty;
        for (int i = 0; i < 100_000; i++)
        {
            // 連続しないピースを末尾に積み続けると、平衡しない木では高さが 10 万になる。
            tree = tree.Insert(tree.Length, Piece.Added(i * 2L, 1));
        }

        Assert.Equal(100_000, tree.PieceCount);
        Assert.True(tree.Height <= 25, $"高さ: {tree.Height}");
    }

    /// <summary>
    /// まとめて行う編集 (ピースを先頭から 1 回たどって木を作り直す) は、同じ編集を後ろから 1 つずつ行った結果と同じ
    /// (ピースが細かく分かれた木でも)。
    /// </summary>
    [Fact]
    public void BatchContentEditsMatchSequentialEdits()
    {
        var random = new Random(1234);
        byte[] initial = new byte[64 * 1024];
        random.NextBytes(initial);
        using var batch = new Document(new MemoryByteSource((byte[])initial.Clone()), Options());
        using var sequential = new Document(new MemoryByteSource((byte[])initial.Clone()), Options());
        for (int round = 0; round < 20; round++)
        {
            var edits = new List<(long Offset, long Remove, byte[] Data)>();
            long at = random.Next(0, 100);
            while (at < batch.Length)
            {
                long remove = Math.Min(random.Next(0, 4), batch.Length - at);
                byte[] data = new byte[random.Next(0, 3)];
                random.NextBytes(data);
                edits.Add((at, remove, data));
                at += remove + random.Next(1, 3000);
            }

            batch.CommitReplacements(batch.PrepareContentEdits(edits.Select(e =>
                new ContentEdit(e.Offset, e.Remove, e.Data.Length == 0 ? null : EditContent.Bytes(e.Data)))), "batch");
            for (int k = edits.Count - 1; k >= 0; k--)
            {
                (long offset, long remove, byte[] data) = edits[k];
                if (remove > 0)
                {
                    sequential.Delete(offset, remove);
                }

                if (data.Length > 0)
                {
                    sequential.Insert(offset, data);
                }
            }

            Assert.Equal(sequential.Length, batch.Length);
            Assert.Equal(ReadAll(sequential), ReadAll(batch));
        }

        static byte[] ReadAll(Document d)
        {
            byte[] bytes = new byte[d.Length];
            d.Current.Read(0, bytes);
            return bytes;
        }
    }
}
