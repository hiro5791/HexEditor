using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-07 長さ固定のデータソース。</summary>
public sealed class FixedLengthTests
{
    /// <summary>長さ固定の基準モデル。上書きの取り消しは、上書き前の内容を覚えておいて戻す。</summary>
    private sealed class FixedModel(byte[] original)
    {
        private readonly Stack<(int Offset, byte[] Bytes, bool[] Modified)> _undo = new();
        private readonly Stack<(int Offset, byte[] Bytes)> _redo = new();

        public byte[] Bytes { get; } = (byte[])original.Clone();

        public bool[] Modified { get; } = new bool[original.Length];

        public int UndoCount => _undo.Count;

        public int RedoCount => _redo.Count;

        public void Overwrite(int offset, byte[] data)
        {
            _redo.Clear();
            Write(offset, data);
        }

        public void Undo()
        {
            (int offset, byte[] bytes, bool[] modified) = _undo.Pop();
            _redo.Push((offset, Bytes.AsSpan(offset, bytes.Length).ToArray()));
            bytes.CopyTo(Bytes, offset);
            modified.CopyTo(Modified, offset);
        }

        public void Redo()
        {
            (int offset, byte[] bytes) = _redo.Pop();
            Write(offset, bytes);
        }

        private void Write(int offset, byte[] data)
        {
            _undo.Push((offset, Bytes.AsSpan(offset, data.Length).ToArray(), Modified.AsSpan(offset, data.Length).ToArray()));
            data.CopyTo(Bytes, offset);
            Modified.AsSpan(offset, data.Length).Fill(true);
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-07-04")]
    public void RandomOverlappingOverwritesMatchModel()
    {
        byte[] original = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Sequence(0, original);
        using var doc = new Document(new FakeByteSource((byte[])original.Clone(), SourceCapabilities.CanWrite), Options());
        var model = new FixedModel(original);
        var rng = new Random(704);

        for (int i = 1; i <= 100_000; i++)
        {
            int kind = rng.Next(10);
            if (kind == 8 && model.UndoCount > 0)
            {
                doc.Undo();
                model.Undo();
            }
            else if (kind == 9 && model.RedoCount > 0)
            {
                doc.Redo();
                model.Redo();
            }
            else
            {
                int size = rng.Next(1, 4097);
                int pos = rng.Next(4) switch
                {
                    0 => 0,
                    1 => original.Length - size,
                    _ => rng.Next(0, original.Length - size + 1),
                };
                if (kind < 5)
                {
                    byte[] data = new byte[size];
                    rng.NextBytes(data);
                    doc.Overwrite(pos, data);
                    model.Overwrite(pos, data);
                }
                else
                {
                    byte[] pattern = new byte[rng.Next(1, 8)];
                    rng.NextBytes(pattern);
                    doc.OverwritePattern(pos, size, pattern);
                    model.Overwrite(pos, ReferenceModel.Repeat(pattern, size));
                }
            }

            if (i % 1000 == 0)
            {
                Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(model.Bytes), $"{i} 回目で内容が違います。");
                AssertNoMergeableNeighbours(doc.Current.Tree);
                var modified = new bool[original.Length];
                foreach ((long offset, long length) in doc.Current.EnumerateModifiedRanges())
                {
                    modified.AsSpan((int)offset, (int)length).Fill(true);
                }

                for (int p = 0; p < original.Length; p++)
                {
                    if (model.Modified[p] && !modified[p])
                    {
                        Assert.Fail($"{i} 回目: 位置 {p} が変更範囲に含まれていません。");
                    }
                }
            }
        }

        // 手順 3: 末尾を越える上書きは拒否され、何も書かれない。
        byte[] before = ReadAll(doc.Current);
        Assert.Throws<FixedLengthException>(() => doc.Overwrite(original.Length - 2, new byte[4]));
        Assert.Equal(before, ReadAll(doc.Current));
        Assert.Equal(original.Length, doc.Length);
    }

    private static void AssertNoMergeableNeighbours(PieceTree tree)
    {
        Piece? previous = null;
        foreach ((_, Piece piece) in tree.EnumerateAll())
        {
            if (previous is { } p)
            {
                Assert.False(p.TryAppend(piece, out _), $"まとめられるピースが隣り合っています: {p} / {piece}");
            }

            previous = piece;
        }
    }
}
