using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using static HexEditor.Core.Tests.Search.SearchTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Search;

/// <summary>検索の後の編集に合わせた一致の位置と状態 (FIND-03 の仕様 2〜4)。</summary>
public sealed class MatchTrackerTests
{
    private const int DataLength = 64 * 1024;

    [Fact]
    public void InsertBeforeShiftsAndUndoRestores()
    {
        // TC-FIND-03-02 の流れ (UI の部分を除く)。
        using Document doc = Doc(Hits1000());
        SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("12 34 56 78"));
        SearchMatch third = results[2];
        Assert.Equal(0x800, third.Offset);

        doc.Insert(0, new byte[100]);
        Assert.Equal(new TrackedMatch(0x864, 4, MatchStatus.Unchanged), new MatchTracker(results.Snapshot, doc.Current).Track(third));

        doc.Undo();
        Assert.Equal(new TrackedMatch(0x800, 4, MatchStatus.Unchanged), new MatchTracker(results.Snapshot, doc.Current).Track(third));
    }

    [Fact]
    public void ChangedAndDeletedMatchesAreMarkedUntilUndone()
    {
        // TC-FIND-03-03 の流れ (UI の部分を除く)。
        using Document doc = Doc(Hits1000());
        SearchResults results = SearchEngine.FindAll(doc.Current, SearchPattern.FromHex("12 34 56 78"));
        doc.Overwrite(0x401, [0xFF]);
        doc.Delete(0x800, 4);
        var tracker = new MatchTracker(results.Snapshot, doc.Current);
        Assert.Equal(new TrackedMatch(0x400, 4, MatchStatus.Modified), tracker.Track(results[1]));
        Assert.Equal(new TrackedMatch(0x800, 0, MatchStatus.Deleted), tracker.Track(results[2]));
        Assert.Equal(new TrackedMatch(0xC00 - 4, 4, MatchStatus.Unchanged), tracker.Track(results[3]));

        doc.Undo();
        doc.Undo();
        tracker = new MatchTracker(results.Snapshot, doc.Current);
        Assert.Equal(MatchStatus.Unchanged, tracker.Track(results[1]).Status);
        Assert.Equal(new TrackedMatch(0x800, 4, MatchStatus.Unchanged), tracker.Track(results[2]));
    }

    [Fact]
    public void RedoAfterUndoMarksAgain()
    {
        using Document doc = Doc(Hits1000());
        DocumentSnapshot searched = doc.Current;
        doc.Delete(0x3FF, 3);
        doc.Undo();
        doc.Redo();
        TrackedMatch m = new MatchTracker(searched, doc.Current).Track(0x400, 4);
        Assert.Equal(new TrackedMatch(0x3FF, 2, MatchStatus.Modified), m);
    }

    [Fact]
    public void CopiedAndGeneratedBytesAreTracked()
    {
        using Document doc = Doc(Hits1000());
        doc.InsertPattern(0x10, 64, [0xAA, 0xBB, 0xCC]);
        doc.InsertRandom(0x100, 32, seed: 7);
        DocumentSnapshot searched = doc.Current;

        // 範囲の複製 (同じ識別のバイトが 2 か所にある) の後も、元の位置が分かる。
        doc.InsertCopy(0, 0x400 + 96, 4);
        var tracker = new MatchTracker(searched, doc.Current);
        Assert.Equal(new TrackedMatch(0x400 + 96 + 4, 4, MatchStatus.Unchanged), tracker.Track(0x400 + 96, 4));

        // パターンと乱数のピースの中の一致。
        Assert.Equal(new TrackedMatch(0x10 + 4 + 3, 6, MatchStatus.Unchanged), tracker.Track(0x10 + 3, 6));
        Assert.Equal(new TrackedMatch(0x100 + 4 + 5, 10, MatchStatus.Unchanged), tracker.Track(0x100 + 5, 10));
    }

    [Fact]
    public void SavedDocumentResultsAreStale()
    {
        // 元データが切り替わった後の結果は「古い結果」(FIND-03 の「エラー」)。
        using Document doc = Doc(Hits1000());
        DocumentSnapshot searched = doc.Current;
        doc.Overwrite(0, [0x00]);
        doc.CompleteSave(new Sources.MemoryByteSource(ReadAll(doc.Current)));
        Assert.Equal(MatchStatus.Stale, new MatchTracker(searched, doc.Current).Track(0x400, 4).Status);
    }

    /// <summary>
    /// TC-FIND-03-04: 64 KiB の無作為なデータと一致 100 件に、挿入・削除・上書きの無作為な列を適用し、各バイトに元の位置の番号を
    /// 付けた基準のモデルと比べる。すべて Undo すると元の位置に戻り、印が消える。
    /// </summary>
    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-FIND-03-04")]
    public Property MatchPositionsFollowEditsLikeTheReferenceModel() => Prop.ForAll(Edits(), c =>
    {
        var rng = new Random(c.Seed);
        byte[] original = new byte[DataLength];
        rng.NextBytes(original);
        using Document doc = Doc((byte[])original.Clone());
        DocumentSnapshot searched = doc.Current;
        var model = new OriginModel(original.Length);
        var matches = Enumerable.Range(0, 100).Select(_ =>
        {
            int length = rng.Next(1, 65);
            return new SearchMatch(rng.Next(DataLength - length + 1), length);
        }).ToList();

        foreach (Edit e in c.Edits)
        {
            int at = (int)(e.Position % (uint)(model.Length + 1));
            switch (e.Kind)
            {
                case EditKind.Insert:
                    byte[] ins = Enumerable.Repeat(e.Value, e.Length).ToArray();
                    doc.Insert(at, ins);
                    model.Insert(at, ins);
                    break;
                case EditKind.Delete:
                    int del = Math.Min(e.Length, model.Length - at);
                    if (del > 0)
                    {
                        doc.Delete(at, del);
                        model.Delete(at, del);
                    }

                    break;
                case EditKind.Overwrite:
                    byte[] ow = Enumerable.Repeat(e.Value, e.Length).ToArray();
                    doc.Overwrite(at, ow);
                    model.Overwrite(at, ow);
                    break;
            }
        }

        long[] origin = [.. model.Origins];
        var index = new Dictionary<long, int>();
        for (int i = 0; i < origin.Length; i++)
        {
            if (origin[i] >= 0)
            {
                index[origin[i]] = i;
            }
        }

        var tracker = new MatchTracker(searched, doc.Current);
        foreach (SearchMatch m in matches)
        {
            TrackedMatch t = tracker.Track(m);
            int survivors = 0;
            for (long b = m.Offset; b < m.End; b++)
            {
                survivors += index.ContainsKey(b) ? 1 : 0;
            }

            // 削除された位置: 一致の前で残っている最後のバイトの直後。
            long DeletionPoint(long x)
            {
                for (long b = x - 1; b >= 0; b--)
                {
                    if (index.TryGetValue(b, out int p))
                    {
                        return p + 1;
                    }
                }

                return 0;
            }

            bool intact = index.TryGetValue(m.Offset, out int first)
                && Enumerable.Range(0, (int)m.Length).All(k => first + k < origin.Length && origin[first + k] == m.Offset + k);
            if (intact)
            {
                Assert.Equal(new TrackedMatch(first, m.Length, MatchStatus.Unchanged), t);
            }
            else if (survivors == 0)
            {
                Assert.Equal(new TrackedMatch(DeletionPoint(m.Offset), 0, MatchStatus.Deleted), t);
            }
            else
            {
                Assert.Equal(MatchStatus.Modified, t.Status);
                long expectedStart = index.TryGetValue(m.Offset, out int s) ? s : DeletionPoint(m.Offset);
                Assert.Equal(expectedStart, t.Offset);
            }
        }

        // 手順 4: すべて Undo すると、元の位置に戻り印がない。
        while (doc.History.CanUndo)
        {
            doc.Undo();
        }

        var undone = new MatchTracker(searched, doc.Current);
        Assert.All(matches, m => Assert.Equal(new TrackedMatch(m.Offset, m.Length, MatchStatus.Unchanged), undone.Track(m)));
    });

    /// <summary>基準のモデル: 各バイトの元の位置 (元データ以外は -1)。挿入・削除・上書きは ReferenceModel と同じ規則。</summary>
    private sealed class OriginModel(int length)
    {
        public List<long> Origins { get; } = [.. Enumerable.Range(0, length).Select(i => (long)i)];

        public int Length => Origins.Count;

        public void Insert(int offset, byte[] data) => Origins.InsertRange(offset, Enumerable.Repeat(-1L, data.Length));

        public void Delete(int offset, int count) => Origins.RemoveRange(offset, count);

        public void Overwrite(int offset, byte[] data)
        {
            Delete(offset, Math.Min(data.Length, Length - offset));
            Insert(offset, data);
        }
    }

    private enum EditKind
    {
        Insert,
        Delete,
        Overwrite,
    }

    private sealed record Edit(EditKind Kind, uint Position, int Length, byte Value);

    private sealed record EditCase(int Seed, Edit[] Edits);

    private static Arbitrary<EditCase> Edits() =>
        (from seed in Gen.Choose(0, int.MaxValue)
         from count in Gen.Choose(1, 50)
         from edits in (from kind in Gen.Elements(EditKind.Insert, EditKind.Delete, EditKind.Overwrite)
                        from position in ArbMap.Default.GeneratorFor<uint>()
                        from length in Gen.Frequency((3, Gen.Choose(1, 16)), (1, Gen.Choose(1, 4096)))
                        from value in ArbMap.Default.GeneratorFor<byte>()
                        select new Edit(kind, position, length, value)).ArrayOf(count)
         select new EditCase(seed, edits)).ToArbitrary();
}
