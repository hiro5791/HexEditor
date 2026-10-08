using System.Text.Json;
using HexEditor.Core.Documents;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Bookmarks;

/// <summary>読み込んだブックマーク (適用する前に、ファイルが変わっていないかを確かめる)。</summary>
public sealed record LoadedBookmarks(DocumentDataHeader Header, int NextAutoNumber, IReadOnlyList<BookmarkRecord> Items);

/// <summary>保存したブックマーク 1 件。</summary>
public sealed record BookmarkRecord(long Start, long Length, string Name, BookmarkColor Color, string Comment, int Number, string? Group,
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
        IReadOnlyList<(Bookmark Bookmark, BookmarkPosition Position)> items = bookmarks.PositionsAtSavedState();
        if (items.Count == 0 && bookmarks.NextAutoNumber <= 1)
        {
            store.Delete(documentPath, Kind);
            return;
        }

        store.Write(documentPath, Kind, stamp, writer =>
        {
            writer.WriteNumber("version", Version);
            writer.WriteNumber("nextNumber", bookmarks.NextAutoNumber);
            writer.WriteStartArray("bookmarks");
            foreach ((Bookmark b, BookmarkPosition p) in items)
            {
                writer.WriteStartObject();
                writer.WriteNumber("start", p.Start);
                writer.WriteNumber("length", p.Length);
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
                if (p.RangeDeleted)
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

                if (b.IsCustomized)
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
