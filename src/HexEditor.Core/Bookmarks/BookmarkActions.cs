using HexEditor.Core.View;

namespace HexEditor.Core.Bookmarks;

/// <summary>Ctrl+F2 の結果。</summary>
public enum BookmarkToggleOutcome
{
    Added,

    /// <summary>外した (名前を変えた・コメントを書いたものではない)。</summary>
    Removed,

    /// <summary>名前を変えた・コメントを書いたものを外した (「元に戻す」付きの通知を出す)。</summary>
    RemovedCustomized,

    /// <summary>上限 (1,000,000 件) に達していて付けられない。</summary>
    LimitReached,
}

/// <summary>前後のブックマークへの移動の結果 (F2 / Shift+F2)。</summary>
public readonly record struct BookmarkJump(Bookmark? Target, bool Wrapped);

/// <summary>
/// エディタから使うブックマークの操作 (INSP-23 の仕様 2、INSP-25 の仕様 1・2、INSP-26 の仕様 7)。UI に依存しない部分。
/// </summary>
public static class BookmarkActions
{
    /// <summary>ブックマークを付ける・外す位置: 選択範囲があればその先頭、なければカーソル位置。</summary>
    public static long Anchor(EditorState editor) => editor.HasSelection ? editor.SelectionStart : editor.Cursor;

    /// <summary>
    /// Ctrl+F2 (INSP-23 の仕様 2)。その位置から始まるブックマークがあれば外し、なければ付ける (選択範囲があれば選択範囲、
    /// なければカーソル位置の 1 バイト)。名前は「ブックマーク N」(<paramref name="autoName"/> に N を渡して作る)。
    /// </summary>
    public static (BookmarkToggleOutcome Outcome, Bookmark? Bookmark) Toggle(BookmarkCollection bookmarks, EditorState editor, Func<int, string> autoName)
    {
        long at = Anchor(editor);
        if (bookmarks.StartingAt(at) is { } existing)
        {
            bool customized = existing.IsCustomized;
            bookmarks.Remove(existing);
            return (customized ? BookmarkToggleOutcome.RemovedCustomized : BookmarkToggleOutcome.Removed, existing);
        }

        if (bookmarks.Count >= BookmarkCollection.MaxCount)
        {
            return (BookmarkToggleOutcome.LimitReached, null);
        }

        long length = editor.HasSelection ? editor.SelectionLength : Math.Min(1, editor.Document.Length - at);
        int number = bookmarks.NextAutoNumber++;
        return (BookmarkToggleOutcome.Added, bookmarks.Add(at, length, autoName(number)));
    }

    /// <summary>
    /// Ctrl+Shift+N (INSP-25 の仕様 1)。その位置から始まるブックマークに番号を付ける (すでに付いていれば外す)。なければ長さ 1、
    /// 名前「[N]」のブックマークを作る。上限で作れなければ null。
    /// </summary>
    public static Bookmark? SetNumber(BookmarkCollection bookmarks, EditorState editor, int number)
    {
        long at = Anchor(editor);
        if (bookmarks.StartingAt(at) is { } existing)
        {
            bookmarks.SetNumber(existing, existing.Number == number ? 0 : number);
            return existing;
        }

        if (bookmarks.Count >= BookmarkCollection.MaxCount)
        {
            return null;
        }

        Bookmark created = bookmarks.Add(at, Math.Min(1, editor.Document.Length - at), $"[{number}]");
        created.CreatedForNumber = true;
        bookmarks.SetNumber(created, number);
        return created;
    }

    /// <summary>F2: カーソル位置より後ろで開始位置が最も近いもの。末尾まで行ったら先頭に戻る。</summary>
    public static BookmarkJump Next(BookmarkCollection bookmarks, long cursor)
    {
        if (bookmarks.After(cursor) is { } next)
        {
            return new BookmarkJump(next, false);
        }

        return new BookmarkJump(bookmarks.First, bookmarks.First is not null);
    }

    /// <summary>Shift+F2: カーソル位置より前で開始位置が最も近いもの。先頭まで行ったら末尾に戻る。</summary>
    public static BookmarkJump Previous(BookmarkCollection bookmarks, long cursor)
    {
        if (bookmarks.Before(cursor) is { } previous)
        {
            return new BookmarkJump(previous, false);
        }

        return new BookmarkJump(bookmarks.Last, bookmarks.Last is not null);
    }

    /// <summary>ブックマークへ移動して範囲を選択する (ジャンプ履歴に記録する。INSP-26 の仕様 4・7)。</summary>
    public static void GoTo(EditorState editor, Bookmark bookmark)
    {
        long start = Math.Min(bookmark.Start, editor.Document.Length);
        long length = Math.Min(bookmark.Length, editor.Document.Length - start);
        editor.SelectMatch(start, length);
    }
}
