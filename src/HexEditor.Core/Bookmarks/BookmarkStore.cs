using System.Buffers;
using System.Text.Json;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Bookmarks;

/// <summary>読み込んだブックマーク (適用する前に、ファイルが変わっていないかを確かめる)。</summary>
public sealed record LoadedBookmarks(DocumentDataHeader Header, int NextAutoNumber, IReadOnlyList<BookmarkRecord> Items);

/// <summary>保存したブックマーク 1 件 (値の型。100 万件の写しでオブジェクトを 1 件ずつ作らない)。</summary>
public readonly record struct BookmarkRecord(long Start, long Length, string Name, BookmarkColor Color, string Comment, int Number, string? Group,
    DateTime Created, DateTime Updated, bool RangeDeleted, bool CreatedForNumber, bool EditedByUser, bool Customized);

/// <summary>
/// ブックマークの保存と読み込み (INSP-23 の仕様 7)。ファイル本体には書かず、付随データ (00-overview 10 章) の
/// <c>&lt;キー&gt;.bookmarks.json</c> に書く。位置は、ファイルに保存されている内容での位置 (保存していない編集の分は戻す)。
/// </summary>
public static class BookmarkStore
{
    public const string Kind = "bookmarks";
    public const int Version = 1;

    /// <summary>書く。ブックマークがなければ付随データを消す。</summary>
    public static void Save(DocumentDataStore store, string documentPath, FileStamp? stamp, BookmarkCollection bookmarks)
    {
        LoadedBookmarks snapshot = Capture(bookmarks);
        try
        {
            Write(store, documentPath, stamp, snapshot);
        }
        finally
        {
            Release(snapshot);
        }
    }

    /// <summary>
    /// 保存する内容を写し取る (ブックマークの一覧を持つスレッド、つまり UI スレッドで呼ぶ)。写した内容は <see cref="Write"/> で別の
    /// スレッドから書ける (100 万件の書き出しで UI を止めない)。写しの領域は使い回すので、書き終えたら <see cref="Release"/> を呼ぶ
    /// (呼ばなくても GC で回収される)。
    /// </summary>
    public static LoadedBookmarks Capture(BookmarkCollection bookmarks)
    {
        BookmarkRecord[] buffer = bookmarks.Count == 0 ? [] : ArrayPool<BookmarkRecord>.Shared.Rent(bookmarks.Count);
        int count = bookmarks.CaptureRecords(buffer);
        return new LoadedBookmarks(new DocumentDataHeader(string.Empty, null, null), bookmarks.NextAutoNumber, new PooledRecords(buffer, count));
    }

    /// <summary><see cref="Capture"/> の写しの領域を返す (この後は写しを使わない)。</summary>
    public static void Release(LoadedBookmarks snapshot)
    {
        if (snapshot.Items is PooledRecords pooled)
        {
            pooled.Return();
        }
    }

    /// <summary>借りた配列の先頭の部分 (写しの一覧)。</summary>
    private sealed class PooledRecords(BookmarkRecord[] buffer, int count) : IReadOnlyList<BookmarkRecord>
    {
        private BookmarkRecord[]? _buffer = buffer;

        public int Count { get; private set; } = count;

        public BookmarkRecord this[int index] =>
            (uint)index < (uint)Count ? _buffer![index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<BookmarkRecord> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return _buffer![i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Return()
        {
            if (Interlocked.Exchange(ref _buffer, null) is { Length: > 0 } array)
            {
                // 名前などの文字列を握り続けないよう消してから返す。
                Array.Clear(array, 0, Count);
                ArrayPool<BookmarkRecord>.Shared.Return(array);
            }

            Count = 0;
        }
    }

    /// <summary>写し取った内容を書く (どのスレッドからでもよい)。ブックマークがなければ付随データを消す。</summary>
    public static void Write(DocumentDataStore store, string documentPath, FileStamp? stamp, LoadedBookmarks snapshot)
    {
        if (snapshot.Items.Count == 0 && snapshot.NextAutoNumber <= 1)
        {
            store.Delete(documentPath, Kind);
            return;
        }

        store.Write(documentPath, Kind, stamp, writer =>
        {
            writer.WriteNumber("version", Version);
            writer.WriteNumber("nextNumber", snapshot.NextAutoNumber);
            writer.WriteStartArray("bookmarks");
            foreach (BookmarkRecord b in snapshot.Items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("start", b.Start);
                writer.WriteNumber("length", b.Length);
                writer.WriteString("name", b.Name);
                writer.WriteString("color", b.Color.ToString());
                if (b.Comment.Length > 0)
                {
                    writer.WriteString("comment", b.Comment);
                }

                if (b.Number != 0)
                {
                    writer.WriteNumber("number", b.Number);
                }

                if (b.Group is not null)
                {
                    writer.WriteString("group", b.Group);
                }

                writer.WriteString("created", b.Created);
                writer.WriteString("updated", b.Updated);
                if (b.RangeDeleted)
                {
                    writer.WriteBoolean("rangeDeleted", true);
                }

                if (b.CreatedForNumber)
                {
                    writer.WriteBoolean("createdForNumber", true);
                }

                if (b.EditedByUser)
                {
                    writer.WriteBoolean("edited", true);
                }

                if (b.Customized)
                {
                    writer.WriteBoolean("customized", true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>読む。なければ null。壊れていれば <see cref="JsonException"/>。</summary>
    public static LoadedBookmarks? Load(DocumentDataStore store, string documentPath)
    {
        if (store.Read(documentPath, Kind) is not { } read)
        {
            return null;
        }

        using JsonDocument doc = read.Body;
        JsonElement root = doc.RootElement;
        int next = root.TryGetProperty("nextNumber", out JsonElement n) ? n.GetInt32() : 1;
        var items = new List<BookmarkRecord>();
        if (root.TryGetProperty("bookmarks", out JsonElement list))
        {
            foreach (JsonElement e in list.EnumerateArray())
            {
                long start = e.GetProperty("start").GetInt64();
                long length = e.TryGetProperty("length", out JsonElement l) ? l.GetInt64() : 1;
                if (start < 0 || length < 0)
                {
                    continue;
                }

                items.Add(new BookmarkRecord(
                    start,
                    length,
                    e.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty,
                    BookmarkColor.Parse(e.TryGetProperty("color", out JsonElement color) ? color.ToString() : null),
                    e.TryGetProperty("comment", out JsonElement comment) ? comment.GetString() ?? string.Empty : string.Empty,
                    e.TryGetProperty("number", out JsonElement number) ? number.GetInt32() : 0,
                    e.TryGetProperty("group", out JsonElement group) ? group.GetString() : null,
                    e.TryGetProperty("created", out JsonElement created) ? created.GetDateTime() : DateTime.UtcNow,
                    e.TryGetProperty("updated", out JsonElement updated) ? updated.GetDateTime() : DateTime.UtcNow,
                    e.TryGetProperty("rangeDeleted", out JsonElement deleted) && deleted.GetBoolean(),
                    e.TryGetProperty("createdForNumber", out JsonElement auto) && auto.GetBoolean(),
                    e.TryGetProperty("edited", out JsonElement edited) && edited.GetBoolean(),
                    e.TryGetProperty("customized", out JsonElement customized) && customized.GetBoolean()));
            }
        }

        return new LoadedBookmarks(read.Header, Math.Max(1, next), items);
    }

    /// <summary>読み込んだブックマークをドキュメントの長さの中に収めて加える (それまでのものは消す)。</summary>
    public static void Apply(BookmarkCollection bookmarks, LoadedBookmarks loaded, long documentLength)
    {
        bookmarks.Clear();
        bookmarks.NextAutoNumber = loaded.NextAutoNumber;
        foreach (BookmarkRecord r in loaded.Items.Take(BookmarkCollection.MaxCount))
        {
            long start = Math.Min(r.Start, documentLength);
            long length = Math.Min(r.Length, documentLength - start);
            bookmarks.Restore(start, length, r.Name, r.Color, r.Comment, r.Number, r.Group, r.Created, r.Updated, r.RangeDeleted,
                r.CreatedForNumber, r.EditedByUser, r.Customized);
        }

        bookmarks.RaiseReset();
    }
}
