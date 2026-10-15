using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Files;
using HexEditor.Core.Inspector;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Bookmarks;

/// <summary>型付きブックマークと位置の式 (INSP-29) の Core の部分。画面の操作は UI テスト (TC-INSP-29-xx)。</summary>
public sealed class BookmarkExpressionTests
{
    private static (Document Doc, EditorState Editor, BookmarkCollection Bookmarks) Create(byte[] data)
    {
        var doc = new Document(new MemoryByteSource(data), Options());
        return (doc, new EditorState(doc), BookmarkCollection.Attach(doc));
    }

    [Fact]
    public void Position_expression_reads_the_document()
    {
        byte[] data = new byte[0x400];
        data[0x3C] = 0x80;
        (Document doc, EditorState editor, BookmarkCollection bookmarks) = Create(data);
        using (doc)
        {
            Bookmark pe = bookmarks.Add(0, 1, "pe");
            bookmarks.SetPositionExpression(pe, "u32le(0x3C)");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(0x80, pe.Start);
            Assert.Null(pe.ExpressionError);

            // 参照先の値を書き換えると、再評価で位置が変わる。位置の式を持つブックマークは編集で位置を調整しない (仕様 6)。
            doc.Overwrite(0x3C, [0x00, 0x01, 0x00, 0x00]);
            Assert.Equal(0x80, pe.Start);
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(0x100, pe.Start);

            // bm.名前 と align。
            Bookmark next = bookmarks.Add(0, 1, "next");
            bookmarks.SetPositionExpression(next, "align(bm.pe + 3, 8)");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(0x108, next.Start);
        }
    }

    [Fact]
    public void Circular_references_mark_both_bookmarks_and_keep_positions()
    {
        (Document doc, EditorState editor, BookmarkCollection bookmarks) = Create(new byte[0x1000]);
        using (doc)
        {
            Bookmark a = bookmarks.Add(0x100, 1, "a");
            Bookmark b = bookmarks.Add(0x200, 1, "b");
            bookmarks.SetPositionExpression(a, "bm.b + 4");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(0x204, a.Start);

            bookmarks.SetPositionExpression(b, "bm.a + 4");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(BookmarkExpressionErrorKind.Cycle, a.ExpressionError?.Kind);
            Assert.Equal(BookmarkExpressionErrorKind.Cycle, b.ExpressionError?.Kind);
            Assert.Equal(0x204, a.Start);
            Assert.Equal(0x200, b.Start);

            // 輪を外すと両方とも直る。
            bookmarks.SetPositionExpression(b, "0x300");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Null(a.ExpressionError);
            Assert.Equal(0x304, a.Start);
            Assert.Equal(0x300, b.Start);
        }
    }

    [Fact]
    public void Errors_keep_the_last_position()
    {
        (Document doc, EditorState editor, BookmarkCollection bookmarks) = Create(new byte[0x100]);
        using (doc)
        {
            Bookmark x = bookmarks.Add(0x10, 1, "x");
            bookmarks.SetPositionExpression(x, "u32le(");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(BookmarkExpressionErrorKind.Expression, x.ExpressionError?.Kind);
            Assert.Equal(0x10, x.Start);

            bookmarks.SetPositionExpression(x, "0x1000");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(BookmarkExpressionErrorKind.OutOfRange, x.ExpressionError?.Kind);
            Assert.Equal(0x10, x.Start);

            bookmarks.SetPositionExpression(x, "u32le(0xFE)");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Equal(ExpressionError.Unreadable, x.ExpressionError?.Detail?.Error);
        }
    }

    [Fact]
    public void References_deeper_than_32_levels_are_errors()
    {
        (Document doc, EditorState editor, BookmarkCollection bookmarks) = Create(new byte[0x1000]);
        using (doc)
        {
            var chain = Enumerable.Range(0, 40).Select(i => bookmarks.Add(i, 1, "n" + i)).ToList();
            for (int i = 1; i < chain.Count; i++)
            {
                bookmarks.SetPositionExpression(chain[i], $"bm.n{i - 1} + 1");
            }

            bookmarks.SetPositionExpression(chain[0], "0");
            BookmarkExpressions.Reevaluate(bookmarks, new EditorExpressionContext(editor), doc.Length);
            Assert.Null(chain[31].ExpressionError);
            Assert.Equal(BookmarkExpressionErrorKind.TooDeep, chain[39].ExpressionError?.Kind);
        }
    }

    [Fact]
    public void Align_is_only_available_in_position_expressions()
    {
        (Document doc, EditorState editor, _) = Create(new byte[0x100]);
        using (doc)
        {
            Assert.False(ExpressionEvaluator.TryEvaluate("align(5, 4)", new EditorExpressionContext(editor), out _, out _));
        }
    }

    [Fact]
    public void Type_and_expression_are_saved_and_restored()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "typed-bookmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "data.bin");
            File.WriteAllBytes(file, new byte[0x100]);
            var store = new DocumentDataStore(Path.Combine(dir, "store"));
            (Document doc, _, BookmarkCollection bookmarks) = Create(new byte[0x100]);
            using (doc)
            {
                Bookmark b = bookmarks.Add(0x10, 1, "value");
                bookmarks.SetType(b, InspectorTypes.UInt32, Endianness.Big, 4);
                bookmarks.SetPositionExpression(b, "0x10");
                Assert.Equal(4, b.Length);
                BookmarkStore.Save(store, file, null, bookmarks);
            }

            LoadedBookmarks loaded = BookmarkStore.Load(store, file)!;
            var again = new BookmarkCollection();
            BookmarkStore.Apply(again, loaded, 0x100);
            Bookmark restored = again.All.Single();
            Assert.Equal(InspectorTypes.UInt32, restored.TypeId);
            Assert.Equal(Endianness.Big, restored.TypeEndian);
            Assert.Equal("0x10", restored.PositionExpression);
            Assert.Equal(1, again.ExpressionCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
