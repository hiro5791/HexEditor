using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Bookmarks;

/// <summary>ブックマーク (INSP-23〜INSP-26) の位置の調整・番号・前後への移動・保存。</summary>
public sealed class BookmarkTests
{
    /// <summary>仕様 4 を素直に実装した基準モデル: 各ブックマークの [開始, 終了) を 1 件ずつ計算する。</summary>
    private sealed class ReferenceBookmark(long start, long length)
    {
        public long Start = start;
        public long Length = length;
        public bool Deleted;

        public void Insert(long o, long n)
        {
            long end = Start + Length;
            if (Start >= o)
            {
                // ブックマークより前 (開始位置を含む) への挿入: ずらす。
                Start += n;
                end += n;
            }
            else if (end > o)
            {
                // 範囲の内側への挿入: 長さを伸ばす。
                end += n;
            }

            Length = end - Start;
        }

        public void Delete(long o, long n)
        {
            long end = Start + Length;
            long newStart = Start < o ? Start : Start < o + n ? o : Start - n;
            long newEnd = end < o ? end : end < o + n ? o : end - n;
            if (Length > 0 && newEnd == newStart)
            {
                Deleted = true;
            }

            Start = newStart;
            Length = newEnd - newStart;
        }
    }

    private static readonly long[] Hot = [1L << 31, 1L << 32, 1L << 53];

    private static long Near(Random r, long max)
    {
        long baseValue = r.Next(4) switch
        {
            0 => Hot[0],
            1 => Hot[1],
            2 => Hot[2],
            _ => (long)(r.NextDouble() * max),
        };
        return Math.Clamp(baseValue + r.Next(-64, 65), 0, max);
    }

    [Fact]
    [Trait(TC, "TC-INSP-23-04")]
    public void Positions_follow_edits_like_the_reference_model_and_undo_restores_them()
    {
        const long Length = 1L << 54;
        using var doc = new Document(new VirtualByteSource(Length, resizable: true), Options());
        BookmarkCollection bookmarks = BookmarkCollection.Attach(doc);
        var random = new Random(0x2304);
        var pairs = new List<(Bookmark Bookmark, ReferenceBookmark Model)>();

        void AddBookmark(long start, long length)
        {
            Bookmark b = bookmarks.Add(start, length, "b" + pairs.Count);
            pairs.Add((b, new ReferenceBookmark(start, length)));
        }

        AddBookmark((1L << 32) - 2, 4);
        AddBookmark(1L << 53, 0);
        while (pairs.Count < 10_000)
        {
            long start = Near(random, Length - 1000);
            AddBookmark(start, random.Next(4) == 0 ? 0 : random.Next(1, 600));
        }

        var initial = pairs.Select(p => (p.Bookmark.Start, p.Bookmark.Length)).ToList();
        int edits = 0;
        for (int i = 0; i < 300; i++)
        {
            long o = Near(random, doc.Length - 1);
            if (random.Next(2) == 0)
            {
                int n = random.Next(1, 300);
                doc.Insert(o, new byte[n]);
                pairs.ForEach(p => p.Model.Insert(o, n));
            }
            else
            {
                long n = Math.Min(random.Next(3) == 0 ? random.Next(1, 5000) : random.Next(1, 200), doc.Length - o);
                if (n <= 0)
                {
                    continue;
                }

                doc.Delete(o, n);
                pairs.ForEach(p => p.Model.Delete(o, n));
            }

            edits++;
        }

        foreach ((Bookmark b, ReferenceBookmark m) in pairs)
        {
            Assert.Equal((m.Start, m.Length), (b.Start, b.Length));
            Assert.Equal(m.Deleted, b.RangeDeleted);
        }

        Assert.Contains(pairs, p => p.Model.Deleted);

        // 区間木の並びも正しい (開始位置の順に列挙できる)。
        var ordered = bookmarks.AllWithStart.Select(p => p.Start).ToList();
        Assert.Equal(ordered.Order(), ordered);

        for (int i = 0; i < edits; i++)
        {
            doc.Undo();
        }

        Assert.Equal(initial, pairs.Select(p => (p.Bookmark.Start, p.Bookmark.Length)).ToList());
        Assert.All(pairs, p => Assert.False(p.Bookmark.RangeDeleted));

        // やり直すと、もう一度同じ位置になる。
        for (int i = 0; i < edits; i++)
        {
            doc.Redo();
        }

        foreach ((Bookmark b, ReferenceBookmark m) in pairs)
        {
            Assert.Equal((m.Start, m.Length), (b.Start, b.Length));
        }
    }

    [Fact]
    public void Simple_rules_of_spec_4()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x1000]), Options());
        BookmarkCollection bm = BookmarkCollection.Attach(doc);
        Bookmark a = bm.Add(0x100, 4, "a");
        doc.Insert(0x10, new byte[10]);
        Assert.Equal(0x10A, a.Start);
        Assert.Equal(4, a.Length);
        doc.Undo();
        Assert.Equal(0x100, a.Start);

        // 範囲内への挿入で伸び、範囲内の削除で縮む。範囲がすべて削除されたら長さ 0 で削除位置に置く (ブックマークは残す)。
        doc.Insert(0x102, new byte[3]);
        Assert.Equal((0x100L, 7L), (a.Start, a.Length));
        doc.Delete(0x101, 2);
        Assert.Equal((0x100L, 5L), (a.Start, a.Length));
        doc.Delete(0xF0, 0x200);
        Assert.Equal((0xF0L, 0L), (a.Start, a.Length));
        Assert.True(a.RangeDeleted);
        Assert.Equal(1, bm.Count);

        // 上書きでは位置は変わらない。
        doc.Undo();
        Assert.False(a.RangeDeleted);
        doc.Overwrite(0x100, [1, 2, 3, 4, 5, 6]);
        Assert.Equal((0x100L, 5L), (a.Start, a.Length));

        // ブックマークの操作は Undo 履歴に入れず、「変更あり」にしない。
        using var clean = new Document(new MemoryByteSource(new byte[16]), Options());
        BookmarkCollection.Attach(clean).Add(0, 1, "x");
        Assert.False(clean.IsModified);
        Assert.False(clean.History.CanUndo);
    }

    [Fact]
    public void Toggle_numbers_and_next_previous()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x10000]), Options());
        var editor = new EditorState(doc);
        BookmarkCollection bm = BookmarkCollection.Attach(doc);

        // Ctrl+F2: カーソル位置の 1 バイトに付け、もう一度で外す。選択範囲があれば選択範囲。
        editor.GoTo(0x100);
        (BookmarkToggleOutcome outcome, Bookmark? b) = BookmarkActions.Toggle(bm, editor, n => $"Bookmark {n}");
        Assert.Equal(BookmarkToggleOutcome.Added, outcome);
        Assert.Equal(("Bookmark 1", 0x100L, 1L), (b!.Name, b.Start, b.Length));
        Assert.Equal(BookmarkToggleOutcome.Removed, BookmarkActions.Toggle(bm, editor, n => $"Bookmark {n}").Outcome);
        Assert.Equal(0, bm.Count);
        editor.Select(0x200, 4);
        b = BookmarkActions.Toggle(bm, editor, n => $"Bookmark {n}").Bookmark;
        Assert.Equal(("Bookmark 2", 0x200L, 4L), (b!.Name, b.Start, b.Length));

        // 名前を変えたものを外すと「元に戻す」付きの通知の対象、削除は取り消せる。
        bm.Rename(b, "header");
        Assert.Equal(BookmarkToggleOutcome.RemovedCustomized, BookmarkActions.Toggle(bm, editor, n => $"Bookmark {n}").Outcome);
        Assert.Equal("header", Assert.Single(bm.UndoDelete()).Name);
        Assert.Equal(0x200, bm.FindByName("header")!.Start);

        // 番号付きブックマーク (INSP-25)。
        editor.GoTo(0x300);
        Bookmark three = BookmarkActions.SetNumber(bm, editor, 3)!;
        Assert.Equal(("[3]", 3, 0x300L), (three.Name, three.Number, three.Start));
        editor.GoTo(0x400);
        Bookmark moved = BookmarkActions.SetNumber(bm, editor, 3)!;
        Assert.Null(three.Owner);
        Assert.Equal(moved, bm.WithNumber(3));
        Bookmark keep = bm.Add(0x500, 1, "keep");
        editor.GoTo(0x500);
        BookmarkActions.SetNumber(bm, editor, 3);
        Assert.Null(moved.Owner);
        Assert.Equal(3, keep.Number);
        BookmarkActions.SetNumber(bm, editor, 3);
        Assert.Equal(0, keep.Number);
        Assert.Equal(keep, bm.StartingAt(0x500));
        Assert.Null(bm.WithNumber(3));

        // F2 / Shift+F2 (INSP-26 の仕様 7)。
        bm.Clear();
        foreach (long p in new long[] { 0x100, 0x200, 0x300 })
        {
            bm.Add(p, 1, "b");
        }

        Assert.Equal(0x100, BookmarkActions.Next(bm, 0).Target!.Start);
        Assert.Equal(0x200, BookmarkActions.Next(bm, 0x100).Target!.Start);
        BookmarkJump wrap = BookmarkActions.Next(bm, 0x300);
        Assert.Equal((0x100L, true), (wrap.Target!.Start, wrap.Wrapped));
        Assert.Equal(0x200, BookmarkActions.Previous(bm, 0x250).Target!.Start);
        BookmarkJump back = BookmarkActions.Previous(bm, 0x100);
        Assert.Equal((0x300L, true), (back.Target!.Start, back.Wrapped));
    }

    [Fact]
    public void Names_usable_in_expressions()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x1000]), Options());
        var editor = new EditorState(doc);
        BookmarkCollection bm = BookmarkCollection.Attach(doc);
        bm.Add(0x100, 1, "header");
        bm.Add(0x200, 1, "header");
        bm.Add(0x300, 1, "2nd header");
        Assert.False(Bookmark.IsExpressionName("2nd header"));
        Assert.True(Bookmark.IsExpressionName("_x1"));
        Assert.Equal(0x108, GoToResolver.Resolve("bm.header+8", GoToBase.Auto, GoToUnit.Bytes, editor).Offset);
        Assert.Null(bm.FindByName("2nd header"));
    }

    [Fact]
    public void Overlapping_query_for_the_view()
    {
        var bm = new BookmarkCollection();
        bm.Add(0x10, 4, "a");
        bm.Add(0x20, 0, "zero");
        bm.Add(0x0, 0x100, "big");
        Assert.Equal(["big", "a"], bm.Overlapping(0x12, 0x14).Select(b => b.Name));
        Assert.Equal(["big", "zero"], bm.Overlapping(0x20, 0x21).Select(b => b.Name).Order());
        Assert.Empty(bm.Overlapping(0x100, 0x200));
    }

    [Fact]
    public void Saving_and_loading_uses_positions_of_the_saved_file()
    {
        string folder = Directory.CreateTempSubdirectory("hexeditor-bm").FullName;
        try
        {
            string file = Path.Combine(folder, "data.bin");
            File.WriteAllBytes(file, new byte[0x1000]);
            var store = new HexEditor.Core.Files.DocumentDataStore(Path.Combine(folder, "documents"));
            using (var doc = new Document(FileByteSource.Open(file), Options()))
            {
                BookmarkCollection bm = BookmarkCollection.Attach(doc);
                Bookmark a = bm.Add(0x100, 1, "Bookmark 1");
                Bookmark b = bm.Add(0x200, 4, "header");
                bm.SetComment(b, "**magic**");
                bm.SetNumber(b, 2);
                bm.SetColor(b, BookmarkColor.Custom(0x123456));

                // 保存していない挿入の分は戻して記録する。
                doc.Insert(0, new byte[0x10]);
                Assert.Equal(0x110, a.Start);
                BookmarkStore.Save(store, file, FileStamp.FromPath(file), bm);
            }

            using var reopened = new Document(FileByteSource.Open(file), Options());
            LoadedBookmarks loaded = BookmarkStore.Load(store, file)!;
            Assert.True(loaded.Header.Matches(FileStamp.FromPath(file)));
            BookmarkCollection again = BookmarkCollection.Attach(reopened);
            BookmarkStore.Apply(again, loaded, reopened.Length);
            Assert.Equal([(0x100L, 1L, "Bookmark 1"), (0x200L, 4L, "header")], again.All.Select(x => (x.Start, x.Length, x.Name)));
            Bookmark h = again.FindByName("header")!;
            Assert.Equal(("**magic**", 2, "#123456"), (h.Comment, h.Number, h.Color.ToString()));
            Assert.Equal(h, again.WithNumber(2));

            // ファイルが変わったら一致しない (適用する前に確認する)。
            File.WriteAllBytes(file, new byte[0x2000]);
            Assert.False(loaded.Header.Matches(FileStamp.FromPath(file)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Limit_is_one_million()
    {
        var bm = new BookmarkCollection();
        for (int i = 0; i < BookmarkCollection.MaxCount; i++)
        {
            bm.Add(i * 16L, 16, "bm");
        }

        Assert.Throws<BookmarkLimitException>(() => bm.Add(0, 1, "over"));
        Assert.Equal(BookmarkCollection.MaxCount, bm.Count);

        // 100 万件でも編集の調整は一部だけを計算する。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            bm.ApplyEdit(i * 1000L, 0, 1);
        }

        Assert.True(watch.ElapsedMilliseconds < 2000, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(16_000_000 - 16 + 1000, bm.Last!.Start);
    }

    [Fact]
    public void Capturing_a_million_bookmarks_for_saving_is_quick_and_reuses_the_buffer()
    {
        // VIEW-04 の受け入れ基準 4: 保存の写し取り (UI スレッド) で 1 件ずつオブジェクトを作らない。
        var bm = new BookmarkCollection();
        for (int i = 0; i < BookmarkCollection.MaxCount; i++)
        {
            bm.Add(i * 16L, 16, "bm" + i);
        }

        LoadedBookmarks warm = BookmarkStore.Capture(bm);
        BookmarkStore.Release(warm);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LoadedBookmarks snapshot = BookmarkStore.Capture(bm);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        try
        {
            Assert.Equal(BookmarkCollection.MaxCount, snapshot.Items.Count);
            Assert.Equal((16L * 999_999, "bm999999"), (snapshot.Items[999_999].Start, snapshot.Items[999_999].Name));

            // 配列を借りて使い回すので、1 件ごとの割り当てはない (数 KB の作業用の領域だけ)。
            Assert.True(allocated < 1_000_000, $"{allocated:N0} bytes");
            // 時間はほかのテストと並んで動くと揺れるため、大まかな上限だけを確かめる (割り当てのなさが本体)。
            Assert.True(watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            BookmarkStore.Release(snapshot);
        }

        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void Saved_positions_undo_unsaved_edits_and_keep_the_order()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x100]), Options());
        BookmarkCollection bm = BookmarkCollection.Attach(doc);
        bm.Add(0x10, 4, "a");
        bm.Add(0x20, 4, "b");
        bm.Add(0x20, 0, "c");

        // 保存していない編集: 先頭に 0x40 バイト挿入、b を含む範囲を削除。
        doc.Insert(0, new byte[0x40]);
        doc.Delete(0x60, 4);
        Assert.Equal(0x60, bm.FindByName("b")!.Start);
        Assert.Equal(
            [new BookmarkPosition(0x10, 4, false), new BookmarkPosition(0x20, 4, false), new BookmarkPosition(0x20, 0, false)],
            bm.PositionsAtSavedState());

        // Undo で保存した時点より前に戻った場合 (保存した時点はやり直せる側にある)。
        doc.CompleteSave(new MemoryByteSource(new byte[doc.Length]));
        doc.Undo();
        doc.Undo();
        Assert.Equal(0x20, bm.FindByName("b")!.Start);
        Assert.Equal(
            [new BookmarkPosition(0x50, 4, false), new BookmarkPosition(0x60, 0, true), new BookmarkPosition(0x60, 0, false)],
            bm.PositionsAtSavedState());
    }

    [Fact]
    public void Names_with_duplicates_are_found_after_removal()
    {
        var bm = new BookmarkCollection();
        Bookmark first = bm.Add(0, 1, "dup");
        Bookmark second = bm.Add(1, 1, "dup");
        Bookmark third = bm.Add(2, 1, "dup");
        Assert.Same(first, bm.FindByName("dup"));
        bm.Remove(first);
        Assert.Same(second, bm.FindByName("dup"));
        bm.Remove(second);
        Assert.Same(third, bm.FindByName("dup"));
        bm.Rename(third, "other");
        Assert.Null(bm.FindByName("dup"));
        Assert.Same(third, bm.FindByName("other"));
    }
}
