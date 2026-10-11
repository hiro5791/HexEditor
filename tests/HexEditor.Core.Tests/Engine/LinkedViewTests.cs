using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>ENG-39 選択範囲を新しいタブで開く (連動ビューとコピー)。</summary>
public sealed class LinkedViewTests
{
    private static Document Seq()
    {
        byte[] data = new byte[1 << 20];
        TestDataCatalog.Sequence(0, data);
        return new Document(new MemoryByteSource(data), Options());
    }

    private static byte[] Expected(long offset, int length)
    {
        byte[] b = new byte[length];
        TestDataCatalog.Sequence(offset, b);
        return b;
    }

    [Fact]
    public void Linked_view_edits_go_to_the_parent_and_share_undo()
    {
        using Document parent = Seq();
        using Document child = parent.CreateLinkedView(0x1000, 0x1000);
        Assert.False(child.CanResize);
        Assert.Equal(Expected(0x1000, 0x1000), ReadAll(child.Current));

        child.Overwrite(0x10, [0xAA]);
        Assert.Equal(0xAA, Read(parent.Current, 0x1010, 1)[0]);
        Assert.True(parent.IsModified);
        Assert.True(child.IsModified);

        parent.Undo();
        Assert.Equal(0x10, Read(child.Current, 0x10, 1)[0]);
        child.Redo();
        Assert.Equal(0xAA, Read(parent.Current, 0x1010, 1)[0]);
        Assert.Throws<FixedLengthException>(() => child.Insert(0, [1]));
    }

    [Fact]
    public void Range_follows_insertions_before_it()
    {
        using Document parent = Seq();
        using Document child = parent.CreateLinkedView(0x1000, 0x1000);
        parent.InsertPattern(0x100, 100, [0xEE]);
        Assert.Equal(0x1064, child.LinkStart);
        Assert.Equal(Expected(0x1000, 0x1000), ReadAll(child.Current));

        // Undo で範囲も元の位置に戻る。
        parent.Undo();
        Assert.Equal(0x1000, child.LinkStart);
        Assert.Equal(Expected(0x1000, 0x1000), ReadAll(child.Current));

        // 範囲の中の挿入は、長さを固定したまま内容が変わる。
        parent.Insert(0x1800, [1, 2, 3]);
        Assert.Equal(0x1000, child.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, Read(child.Current, 0x800, 3));
    }

    [Fact]
    public void Deleting_the_whole_range_disconnects()
    {
        using Document parent = Seq();
        using Document child = parent.CreateLinkedView(0x1000, 0x1000);
        parent.Delete(0x800, 0x2000);
        Assert.True(child.IsLinkDisconnected);
        Assert.True(child.IsReadOnly);
    }

    [Fact]
    public void Copy_is_independent_of_the_parent()
    {
        using Document parent = Seq();
        using Document copy = Document.CreateCopy(parent.Current, 0x1000, 0x1000, "copy");
        Assert.Equal(0x1000, copy.Length);
        Assert.False(copy.IsModified);
        copy.InsertPattern(0, 16, [0]);
        Assert.Equal(0x1010, copy.Length);
        Assert.False(parent.IsModified);
        parent.Overwrite(0x1000, [0xFF]);
        Assert.Equal(0x00, Read(copy.Current, 0x10, 1)[0]);
    }

    [Fact]
    public void Opening_a_huge_range_is_instant()
    {
        using var parent = new Document(FileByteSource.Open(TestDataCatalog.Get("TD-SPARSE-100G")), Options());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using Document child = parent.CreateLinkedView(2L << 30, 5L << 30);
        using Document copy = Document.CreateCopy(parent.Current, 2L << 30, 5L << 30, "copy");
        Assert.True(watch.ElapsedMilliseconds < 100, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(5L << 30, child.Length);
    }
}
