using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>VIEW-29 の移動先の求め方。</summary>
public sealed class GoToResolverTests
{
    private static EditorState Editor(int length = 0x10000)
    {
        byte[] data = new byte[length];
        data[0x3C] = 0x80; // PE ヘッダの位置 (u32le(0x3C)) の代わり
        var doc = new Document(new MemoryByteSource(data), Options());
        return new EditorState(doc) { VisibleRows = 20 };
    }

    [Theory]
    [InlineData("0x1F00")]
    [InlineData("1F00h")]
    [InlineData("$1F00")]
    public void HexNotationsAllWork(string text) =>
        Assert.Equal(0x1F00, GoToResolver.Resolve(text, GoToBase.Auto, GoToUnit.Bytes, Editor()).Offset);

    [Fact]
    public void By_address_subtracts_the_base_address()
    {
        // VIEW-20 の仕様 4・VIEW-29 の仕様 5: アドレスで指定した値からベースアドレスを引いてオフセットにする。
        EditorState e = Editor();
        e.ApplyView(e.View with { BaseAddress = 0x400000 });
        Assert.Equal(0x1F00, GoToResolver.Resolve("0x401F00", GoToBase.Auto, GoToUnit.Bytes, e, byAddress: true).Offset);
        Assert.Equal(0x1F00, GoToResolver.Resolve("0x1F00", GoToBase.Auto, GoToUnit.Bytes, e, byAddress: false).Offset);
        Assert.True(GoToResolver.Resolve("0x3FFFFF", GoToBase.Auto, GoToUnit.Bytes, e, byAddress: true).OutOfRange);
        Assert.True(GoToResolver.Resolve("0x420000", GoToBase.Auto, GoToUnit.Bytes, e, byAddress: true).OutOfRange);

        // 相対の移動はアドレスに関係しない。
        e.GoTo(0x100);
        Assert.Equal(0x110, GoToResolver.Resolve("+0x10", GoToBase.Auto, GoToUnit.Bytes, e, byAddress: true).Offset);
    }

    [Fact]
    public void RelativeAndFromEnd()
    {
        EditorState e = Editor();
        e.GoTo(0x100);
        Assert.Equal(0x110, GoToResolver.Resolve("+0x10", GoToBase.Auto, GoToUnit.Bytes, e).Offset);
        Assert.Equal(0xF0, GoToResolver.Resolve("-0x10", GoToBase.Auto, GoToUnit.Bytes, e).Offset);
        Assert.Equal(0xFFF0, GoToResolver.Resolve("end-0x10", GoToBase.Auto, GoToUnit.Bytes, e).Offset);
        Assert.Equal(0xFFF0, GoToResolver.Resolve("0x10", GoToBase.FromEnd, GoToUnit.Bytes, e).Offset);
        Assert.Equal(0x200, GoToResolver.Resolve("0x100", GoToBase.FromCursor, GoToUnit.Bytes, e).Offset);
    }

    [Fact]
    public void SelectionNames()
    {
        EditorState e = Editor();
        e.Select(0x10, 16);
        Assert.Equal(0x20, GoToResolver.Resolve("sel.end", GoToBase.Auto, GoToUnit.Bytes, e).Offset);
        Assert.Equal(0x1F, GoToResolver.Resolve("sel.last", GoToBase.Auto, GoToUnit.Bytes, e).Offset);
    }

    [Fact]
    public void PointerFunction() =>
        Assert.Equal(0x80, GoToResolver.Resolve("u32le(0x3C)", GoToBase.Auto, GoToUnit.Bytes, Editor()).Offset);

    [Fact]
    public void SectorAndRowUnits()
    {
        Assert.Equal(4096, GoToResolver.Resolve("8", GoToBase.Auto, GoToUnit.Sectors, Editor()).Offset);
        Assert.Equal(0x100, GoToResolver.Resolve("0x10", GoToBase.Auto, GoToUnit.Rows, Editor()).Offset);
    }

    [Fact]
    public void BeyondEndIsRejected()
    {
        GoToResult r = GoToResolver.Resolve("0x10001", GoToBase.Auto, GoToUnit.Bytes, Editor());
        Assert.True(r.OutOfRange);
        Assert.False(r.IsValid);
        Assert.True(GoToResolver.Resolve("0x10000", GoToBase.Auto, GoToUnit.Bytes, Editor()).IsValid);
    }
}
