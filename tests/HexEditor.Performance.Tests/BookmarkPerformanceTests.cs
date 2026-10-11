using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>ブックマークのグループ (INSP-27 の「巨大ファイル・長時間処理」) の性能テスト。</summary>
[Trait("Category", "Performance")]
public sealed class BookmarkPerformanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// INSP-27: 100 万件のブックマークでも、グループの表示 / 非表示の切り替えを 200 ms 以内に反映する。切り替えと、通知を受けた一覧の
    /// 扱い (並びを作り直さない)、表示範囲の注釈の取り出し (Hex ビューの描画と同じ経路) までを計る。
    /// </summary>
    [Fact]
    public void ToggleGroupVisibilityWithOneMillionBookmarksIsFast()
    {
        var bookmarks = new BookmarkCollection();
        const int Groups = 10;
        const int PerGroup = BookmarkCollection.MaxCount / Groups;
        var added = new List<Bookmark>(BookmarkCollection.MaxCount);
        for (int g = 0; g < Groups; g++)
        {
            BookmarkGroup group = bookmarks.CreateGroup(null, "g" + g);
            for (int i = 0; i < PerGroup; i++)
            {
                long start = ((long)g * PerGroup + i) * 16;
                added.Add(bookmarks.AddQuiet(start, 4, "b", null, group.Path));
            }
        }

        bookmarks.RaiseAdded(added);
        Assert.Equal(BookmarkCollection.MaxCount, bookmarks.Count);
        IReadOnlyList<Bookmark> ordered = bookmarks.Ordered;

        var source = new BookmarkAnnotationSource(bookmarks);
        var visible = new List<Annotation>();
        bool rebuilt = false;
        bookmarks.Changed += (_, e) =>
        {
            // 一覧 (BookmarkListViewModel) と同じ: 表示 / 非表示だけの変更なら作り直さない。
            if (!e.VisibilityOnly)
            {
                rebuilt = true;
                _ = bookmarks.Ordered.Count;
            }
        };

        BookmarkGroup first = bookmarks.FindGroup("g0")!;
        var times = new List<TimeSpan>();
        for (int i = 0; i < 11; i++)
        {
            times.Add(Time(() =>
            {
                bookmarks.SetGroupVisible(first, !first.Visible);
                visible.Clear();
                source.Query(0, 16 * 64 * 40, visible);
            }));
            Assert.Equal(first.Visible ? 64 * 40 : 0, visible.Count);
        }

        Assert.False(rebuilt);
        Assert.Same(ordered, bookmarks.Ordered);

        // 非表示のグループは F2 で飛ばす。
        if (first.Visible)
        {
            bookmarks.SetGroupVisible(first, false);
        }

        Assert.Equal((long)PerGroup * 16, BookmarkActions.Next(bookmarks, 0).Target!.Start);

        output.Report($"toggle group visibility (1,000,000 bookmarks): {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= TimeSpan.FromMilliseconds(200), Summary(times));
    }
}
