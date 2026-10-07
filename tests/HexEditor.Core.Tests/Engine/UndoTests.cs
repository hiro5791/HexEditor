using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>EDIT-19 元に戻す・やり直し (入力のまとめ、編集グループ、範囲の選択)。</summary>
public sealed class UndoTests
{
    private static (Document Doc, EditorState State, ManualTimeProvider Clock) Create(int length, TimeSpan? interval = null)
    {
        var clock = new ManualTimeProvider();
        DocumentOptions options = Options() with
        {
            TimeProvider = clock,
            CoalesceInterval = interval ?? EditHistory.DefaultCoalesceInterval,
        };
        var doc = new Document(new MemoryByteSource(new byte[length]), options);
        var state = new EditorState(doc) { VisibleRows = 20 };
        state.ToggleColumn(); // テキスト列で 1 文字 = 1 バイトの入力
        return (doc, state, clock);
    }

    /// <summary>直前の入力からの履歴の項目 (編集グループ) の増え方。</summary>
    private static int Groups(Document doc, int before) => doc.History.Count - before;

    [Fact]
    [Trait(TC, "TC-EDIT-19-05")]
    public void CoalescingRulesAndBoundaries()
    {
        // 手順 1: 間隔 1.999 秒の入力は 1 つのグループ。
        (Document doc, EditorState s, ManualTimeProvider clock) = Create(64 * 1024);
        using (doc)
        {
            int start = doc.History.Count;
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(1999));
                Assert.Equal(EditResult.Done, s.TypeText("A"));
            }

            Assert.Equal(1, Groups(doc, start));
        }

        // 手順 2: 間隔 2.000 秒なら入力ごとに別のグループ。
        (doc, s, clock) = Create(64 * 1024);
        using (doc)
        {
            int start = doc.History.Count;
            for (int i = 0; i < 3; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                s.TypeText("A");
            }

            Assert.Equal(3, Groups(doc, start));
        }

        // 手順 3: 4,096 バイトまでが 1 グループで、4,097 バイト目から新しいグループ。
        (doc, s, _) = Create(64 * 1024);
        using (doc)
        {
            int start = doc.History.Count;
            for (int i = 0; i < 4096; i++)
            {
                s.TypeText("B");
            }

            Assert.Equal(1, Groups(doc, start));
            s.TypeText("B");
            Assert.Equal(2, Groups(doc, start));
            doc.Undo();
            Assert.Equal((4096L, 1L), (s.SelectionStart, s.SelectionLength)); // 4,097 バイト目だけが戻る
            Assert.Equal((byte)'B', Read(doc.Current, 4095, 1)[0]);
            Assert.Equal(0, Read(doc.Current, 4096, 1)[0]);
        }

        // 手順 4: 文字入力、Delete、Backspace は別々のグループ (挿入モード)。
        (doc, s, _) = Create(64 * 1024);
        using (doc)
        {
            s.ToggleInsertMode();
            s.Click(10, ActiveColumn.Text, false, false);
            int start = doc.History.Count;
            s.TypeText("A");
            s.Delete();
            s.Delete();
            s.Backspace();
            s.Backspace();
            Assert.Equal(3, Groups(doc, start));
        }

        // 手順 5: 入力モードの切り替え、カーソルの移動、保存、別のコマンドを挟むと、前後は別のグループ。
        foreach (Action<Document, EditorState> between in new Action<Document, EditorState>[]
        {
            (_, st) => st.ToggleInsertMode(),
            (_, st) => { st.MoveLeft(); st.MoveRight(); },
            (d, _) => d.MarkSaved(),
            (d, _) => d.OverwritePattern(1000, 16, [0xEE]),
        })
        {
            (doc, s, _) = Create(64 * 1024);
            using (doc)
            {
                int start = doc.History.Count;
                s.TypeText("A");
                int afterFirst = doc.History.Count;
                between(doc, s);
                int afterBetween = doc.History.Count;
                s.TypeText("A");
                Assert.Equal(1, afterFirst - start);
                Assert.Equal(afterBetween + 1, doc.History.Count);
            }
        }

        // 手順 6: まとめる時間 0 秒では、間隔 0 秒でもまとめない。
        (doc, s, _) = Create(64 * 1024, TimeSpan.Zero);
        using (doc)
        {
            int start = doc.History.Count;
            s.TypeText("A");
            s.TypeText("A");
            s.TypeText("A");
            Assert.Equal(3, Groups(doc, start));
        }
    }

    [Fact]
    public void InsertModePasteOverSelectionIsOneUndoGroup()
    {
        (Document doc, EditorState s, _) = Create(64);
        using (doc)
        {
            s.ToggleInsertMode();
            s.Select(8, 16);
            int before = doc.History.Count;
            Assert.Equal(EditResult.Done, s.Paste([1, 2, 3], overwrite: false));
            Assert.Equal(before + 1, doc.History.Count);
            Assert.Equal(64 - 16 + 3, doc.Length);
            Assert.Equal((8L, 3L), (s.SelectionStart, s.SelectionLength));

            // 1 回の Undo で選択範囲の削除と貼り付けの両方が戻り、置き換える前の範囲が選択される (EDIT-19 の仕様 10)。
            doc.Undo();
            Assert.Equal(64, doc.Length);
            Assert.Equal((8L, 16L), (s.SelectionStart, s.SelectionLength));

            // Redo で貼り付けた範囲が選択される。
            doc.Redo();
            Assert.Equal(51, doc.Length);
            Assert.Equal((8L, 3L), (s.SelectionStart, s.SelectionLength));
        }
    }

    [Fact]
    public void TypingOverSelectionInInsertModeIsOneUndoGroup()
    {
        (Document doc, EditorState s, _) = Create(64);
        using (doc)
        {
            s.ToggleInsertMode();
            s.Select(4, 10);
            int before = doc.History.Count;
            s.TypeText("X");
            s.TypeText("Y");
            Assert.Equal(before + 1, doc.History.Count);
            Assert.Equal(56, doc.Length);
            doc.Undo();
            Assert.Equal(new byte[64], ReadAll(doc.Current));
            Assert.Equal((4L, 10L), (s.SelectionStart, s.SelectionLength));
        }

        // Hex 列でも同じ。
        (doc, s, _) = Create(64);
        using (doc)
        {
            s.ToggleColumn();
            s.ToggleInsertMode();
            s.Select(4, 10);
            int before = doc.History.Count;
            s.TypeHexDigit('A');
            s.TypeHexDigit('B');
            Assert.Equal(before + 1, doc.History.Count);
            Assert.Equal(0xAB, Read(doc.Current, 4, 1)[0]);
            doc.Undo();
            Assert.Equal(64, doc.Length);
        }
    }

    [Fact]
    public void UndoOfInsertPlacesCursorAndScrollsToEditedRange()
    {
        (Document doc, EditorState s, _) = Create(16 * 1000);
        using (doc)
        {
            doc.Insert(16 * 500, [1, 2, 3, 4]);
            s.MoveToStart();
            Assert.Equal(0, s.TopRow);

            // 挿入の取り消し: 編集前の範囲は長さ 0 なので、カーソルをその位置に置き、見える位置にスクロールする。
            doc.Undo();
            Assert.False(s.HasSelection);
            Assert.Equal(16 * 500, s.Cursor);
            Assert.InRange(500, s.TopRow, s.TopRow + s.VisibleRows - 1);

            s.MoveToStart();
            doc.Redo();
            Assert.Equal((16L * 500, 4L), (s.SelectionStart, s.SelectionLength));
            Assert.InRange(500, s.TopRow, s.TopRow + s.VisibleRows - 1);
        }
    }

    // ---- TC-EDIT-19-01: 無作為な編集の列と基準モデル ----

    public enum OpKind
    {
        Insert,
        Delete,
        Overwrite,
        Fill,
        PasteCopy,
        PasteFromOther,
        Undo,
        Redo,
        SavePoint,
    }

    public sealed record Op(OpKind Kind, uint A, uint B, uint C, byte Value);

    /// <summary>履歴を持つ基準モデル: 各時点の内容の配列。</summary>
    private sealed class HistoryModel(byte[] initial)
    {
        private readonly List<byte[]> _states = [initial];

        public int Index { get; private set; }

        public int Saved { get; private set; }

        public byte[] Current => _states[Index];

        public void Push(byte[] next)
        {
            _states.RemoveRange(Index + 1, _states.Count - Index - 1);
            if (Saved > Index)
            {
                Saved = -1;
            }

            _states.Add(next);
            Index++;
        }

        public bool Undo()
        {
            if (Index == 0)
            {
                return false;
            }

            Index--;
            return true;
        }

        public bool Redo()
        {
            if (Index == _states.Count - 1)
            {
                return false;
            }

            Index++;
            return true;
        }

        public void MarkSaved() => Saved = Index;
    }

    /// <summary>無作為な位置。0、末尾、末尾 − 1、ピースの境界とその前後を多く選ぶ。</summary>
    private static int Position(uint r, int length, Document doc)
    {
        switch (r % 10)
        {
            case 0:
                return 0;
            case 1:
                return length;
            case 2:
                return Math.Max(0, length - 1);
            case 3:
            case 4:
                var bounds = doc.Current.Tree.EnumerateAll().Select(p => (int)p.DocumentOffset).ToList();
                if (bounds.Count == 0)
                {
                    return 0;
                }

                int b = bounds[(int)(r / 10 % (uint)bounds.Count)] + (int)(r / 7 % 3) - 1;
                return Math.Clamp(b, 0, length);
            default:
                return (int)(r / 10 % (uint)(length + 1));
        }
    }

    private static void Apply(Document doc, HistoryModel model, Document other, Op op)
    {
        byte[] cur = model.Current;
        int length = cur.Length;
        int pos = Position(op.A, length, doc);
        int size = (int)(op.B % 512) + 1;
        byte[] data = Enumerable.Range(0, size).Select(i => (byte)(op.Value + i)).ToArray();
        switch (op.Kind)
        {
            case OpKind.Insert:
                doc.Insert(pos, data);
                model.Push([.. cur[..pos], .. data, .. cur[pos..]]);
                break;
            case OpKind.Delete when length > 0:
                pos = Math.Min(pos, length - 1);
                int del = Math.Min(size, length - pos);
                doc.Delete(pos, del);
                model.Push([.. cur[..pos], .. cur[(pos + del)..]]);
                break;
            case OpKind.Overwrite:
                doc.Overwrite(pos, data);
                model.Push(Overwritten(cur, pos, data));
                break;
            case OpKind.Fill:
                byte[] pattern = data[..Math.Min(size, (int)(op.C % 5) + 1)];
                doc.OverwritePattern(pos, size, pattern);
                model.Push(Overwritten(cur, pos, ReferenceModel.Repeat(pattern, size)));
                break;
            case OpKind.PasteCopy when length > 0:
                int src = Math.Min(Position(op.C, length, doc), length - 1);
                int len = Math.Min(size, length - src);
                doc.InsertCopy(pos, src, len);
                model.Push([.. cur[..pos], .. cur[src..(src + len)], .. cur[pos..]]);
                break;
            case OpKind.PasteFromOther:
                // 別のドキュメントからの貼り付け (範囲の参照。EDIT-24)。
                int from = (int)(op.C % (uint)(other.Length - size));
                doc.InsertFrom(pos, other.Current, from, size);
                model.Push([.. cur[..pos], .. Read(other.Current, from, size), .. cur[pos..]]);
                break;
            case OpKind.Undo:
                if (model.Undo())
                {
                    doc.Undo();
                }

                break;
            case OpKind.Redo:
                if (model.Redo())
                {
                    doc.Redo();
                }

                break;
            case OpKind.SavePoint:
                doc.MarkSaved();
                model.MarkSaved();
                break;
        }
    }

    private static byte[] Overwritten(byte[] cur, int pos, byte[] data)
    {
        byte[] next = new byte[Math.Max(cur.Length, pos + data.Length)];
        cur.CopyTo(next, 0);
        data.CopyTo(next, pos);
        return next;
    }

    private static void AssertMatches(Document doc, HistoryModel model)
    {
        Assert.Equal(model.Current.Length, doc.Length);
        Assert.True(ReadAll(doc.Current).AsSpan().SequenceEqual(model.Current), "内容が基準モデルと違います。");
        Assert.Equal(model.Index != model.Saved, doc.IsModified);
    }

    private static void RunSequence(byte[] initial, IEnumerable<Op> ops)
    {
        using var other = new Document(new MemoryByteSource(Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray()), Options());
        using var doc = new Document(new MemoryByteSource((byte[])initial.Clone()), Options());
        var model = new HistoryModel(initial);
        foreach (Op op in ops)
        {
            Apply(doc, model, other, op);
            AssertMatches(doc, model);
        }

        // 手順 3: すべて元に戻すと初期データに、すべてやり直すと最後の状態に一致する。
        while (doc.History.CanUndo)
        {
            doc.Undo();
            model.Undo();
        }

        Assert.Equal(0, model.Index);
        AssertMatches(doc, model);
        while (doc.History.CanRedo)
        {
            doc.Redo();
            model.Redo();
        }

        AssertMatches(doc, model);
    }

    private static Op RandomOp(Random rng) =>
        new((OpKind)rng.Next(9), (uint)rng.Next(), (uint)rng.Next(), (uint)rng.Next(), (byte)rng.Next(256));

    private static void RunRandomSequences(int sequences, int seed)
    {
        var rng = new Random(seed);
        for (int s = 0; s < sequences; s++)
        {
            byte[] initial = new byte[rng.Next(0, 64 * 1024 + 1)];
            rng.NextBytes(initial);
            int count = rng.Next(1, 501);
            RunSequence(initial, Enumerable.Range(0, count).Select(_ => RandomOp(rng)).ToList());
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-19-01")]
    public void RandomEditsWithUndoRedoMatchModel()
    {
        // プルリクエストごとの規模。2,000 通りの全規模は Nightly のテストで行う。
        RunRandomSequences(sequences: 100, seed: 1901);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-19-01")]
    [Trait("Category", "Nightly")]
    public void RandomEditsWithUndoRedoMatchModelFullScale() => RunRandomSequences(sequences: 2000, seed: 1902);

    private static Arbitrary<(int Length, Op[] Ops)> Sequences() =>
        (from length in Gen.Choose(0, 64 * 1024)
         from ops in (from kind in Gen.Elements(Enum.GetValues<OpKind>())
                      from a in ArbMap.Default.GeneratorFor<uint>()
                      from b in Gen.Choose(0, 64).Select(i => (uint)i)
                      from c in ArbMap.Default.GeneratorFor<uint>()
                      from v in ArbMap.Default.GeneratorFor<byte>()
                      select new Op(kind, a, b, c, v)).ArrayOf()
         select (length, ops)).ToArbitrary();

    /// <summary>失敗した場合に FsCheck が最小の操作列に縮める (TC-EDIT-19-01 の期待結果 3)。</summary>
    [Property(MaxTest = 200)]
    [Trait(TC, "TC-EDIT-19-01")]
    public Property ShrinkableSequencesMatchModel() => Prop.ForAll(Sequences(), seq =>
    {
        byte[] initial = Enumerable.Range(0, seq.Length).Select(i => (byte)(i * 31)).ToArray();
        RunSequence(initial, seq.Ops);
    });
}
