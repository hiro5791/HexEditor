using System.Collections;
using System.Globalization;
using HexEditor.Core.Bookmarks;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// <c>doc.bookmarks</c> (AUTO-11)。ドキュメントのブックマーク (05) を読み書きする。スクリプトが追加したものも、利用者が追加したものと
/// 同じく付随データとして保存される (仕様 4)。
/// </summary>
internal sealed class ScriptBookmarks(ScriptDocument doc) : IBookmarks
{
    /// <summary>一括で追加できる件数 (仕様の「巨大ファイル」)。</summary>
    public const int MaxResults = 1_000_000;

    private T Ui<T>(Func<BookmarkCollection, T> work)
    {
        doc.ThrowIfClosed();
        return doc.Context.Dispatcher.Invoke(() =>
        {
            doc.ThrowIfClosed();
            BookmarkCollection c = BookmarkCollection.For(doc.Document) ?? BookmarkCollection.Attach(doc.Document);
            return work(c);
        });
    }

    private List<IBookmark> Snapshot() => Ui(c => c.AllWithStart.Select(p => (IBookmark)Read(c, p.Bookmark, p.Start)).ToList());

    private static ScriptBookmark Read(BookmarkCollection c, Bookmark b, long start) =>
        new(b, b.Name, start, b.Length, ScriptColors.NameOf(b.Color), b.Comment, b.Number is >= 1 and <= 9 ? b.Number : null);

    public int Count => Ui(c => c.Count);

    public IBookmark this[int index]
    {
        get
        {
            List<IBookmark> all = Snapshot();
            return index >= 0 && index < all.Count ? all[index] : throw new Sdk.RangeError(Msg.T($"ブックマークの番号が範囲外です: {index}", $"The bookmark index is out of range: {index}"));
        }
    }

    public IEnumerator<IBookmark> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IBookmark Add(BookmarkSpec bookmark)
    {
        long start = doc.ResolveOffset(bookmark.Start);
        long length = bookmark.Length;
        CheckRange(start, length);
        BookmarkColor? color = bookmark.Color is null ? null : ScriptColors.Bookmark(bookmark.Color);
        return Ui(c =>
        {
            Bookmark b = c.Add(start, length, bookmark.Name ?? DefaultName(c), color);
            if (!string.IsNullOrEmpty(bookmark.Comment))
            {
                c.SetComment(b, bookmark.Comment);
            }

            return (IBookmark)Read(c, b, start);
        });
    }

    private void CheckRange(long start, long length)
    {
        long docLength = doc.Snapshot.Length;
        if (start < 0 || length < 0 || start > docLength || length > docLength - start)
        {
            throw new Sdk.RangeError(Msg.T($"ブックマークの範囲がドキュメントの外です: 0x{start:X}+0x{length:X}", $"The bookmark range is outside the document: 0x{start:X}+0x{length:X}"));
        }
    }

    private static string DefaultName(BookmarkCollection c) => string.Create(CultureInfo.InvariantCulture, $"bookmark {c.Count + 1}");

    public void Remove(IBookmark bookmark)
    {
        if (bookmark is not ScriptBookmark sb)
        {
            throw new Sdk.ArgumentError(Msg.T("bookmark には doc.bookmarks のブックマークを指定してください。", "Specify a bookmark from doc.bookmarks for bookmark."));
        }

        Ui(c =>
        {
            if (c.All.Contains(sb.Source))
            {
                c.Remove(sb.Source);
            }

            return true;
        });
    }

    public void Clear() => Ui(c =>
    {
        c.Clear();
        return true;
    });

    public IBookmark? Find(string name) => Ui(c => c.AllWithStart.FirstOrDefault(p => p.Bookmark.Name == name) is { Bookmark: { } b } p ? (IBookmark)Read(c, b, p.Start) : null);

    public long AddFromResults(IReadOnlyList<SdkRange> results, string nameFormat = "{index}")
    {
        if (results.Count > MaxResults)
        {
            throw new LimitError(Msg.T($"一度に追加できるのは {MaxResults:N0} 件までです ({results.Count:N0} 件)。", $"At most {MaxResults:N0} items can be added at once ({results.Count:N0} items).")) { Limit = "count" };
        }

        foreach (SdkRange r in results)
        {
            CheckRange(r.Start, r.Length);
        }

        return Ui(c =>
        {
            if (c.Count + results.Count > BookmarkCollection.MaxCount)
            {
                throw new LimitError(Msg.T($"ブックマークの上限 {BookmarkCollection.MaxCount:N0} 件を超えます。", $"This exceeds the bookmark limit of {BookmarkCollection.MaxCount:N0}.")) { Limit = "count" };
            }

            // 1 件ずつ通知すると一覧の更新が重いので、まとめて加えて 1 回だけ知らせる (1 万件を 1 秒以内。AUTO-11 の受け入れ基準 2)。
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < results.Count; i++)
            {
                string name = nameFormat.Replace("{index}", (i + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                c.Restore(results[i].Start, results[i].Length, name, BookmarkColor.Default, "", 0, null, now, now,
                    rangeDeleted: false, createdForNumber: false, editedByUser: true, customized: false);
            }

            c.RaiseReset();
            return (long)results.Count;
        });
    }
}

/// <summary>1 つのブックマーク (読んだ時点の値)。</summary>
internal sealed record ScriptBookmark(Bookmark Source, string Name, long Start, long Length, string Color, string Comment, int? Number) : IBookmark;
