using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>「ポインタとして移動」(INSP-20) と配列モード (INSP-21) の計算。画面の操作は UI テスト (TC-INSP-20-xx、TC-INSP-21-xx)。</summary>
public sealed class InspectorNavigationTests
{
    [Fact]
    public void Reads_integer_rows_as_positions()
    {
        byte[] data = [0x00, 0x01, 0x00, 0x00, 0xF0];
        Assert.Equal((Int128)0x100, InspectorNavigation.ReadInteger(InspectorTypes.UInt32, data, Endianness.Little, out int size));
        Assert.Equal(4, size);
        Assert.Equal((Int128)(-16), InspectorNavigation.ReadInteger(InspectorTypes.Int8, data.AsSpan(4), Endianness.Little, out size));
        Assert.Equal(1, size);
        Assert.Equal((Int128)0x00010000, InspectorNavigation.ReadInteger(InspectorTypes.UInt32, data, Endianness.Big, out _));

        // 可変長整数は実際に使ったバイト数。
        Assert.Equal((Int128)624485, InspectorNavigation.ReadInteger(InspectorTypes.ULeb128, [0xE5, 0x8E, 0x26], Endianness.Little, out size));
        Assert.Equal(3, size);

        // データが足りない・整数でない行は null。
        Assert.Null(InspectorNavigation.ReadInteger(InspectorTypes.UInt32, [0x01, 0x02], Endianness.Little, out _));
        Assert.Null(InspectorNavigation.ReadInteger(InspectorTypes.Float32, data, Endianness.Little, out _));
        Assert.False(InspectorNavigation.IsPointerType(InspectorTypes.Guid));
    }

    [Theory]
    [InlineData(PointerMode.AbsoluteOffset, 0x100, 0x10, 4, 0UL, 0L, 0x100)]
    [InlineData(PointerMode.AbsoluteAddress, 0x401000, 0x20, 4, 0x400000UL, 0L, 0x1000)]
    [InlineData(PointerMode.RelativeToCursor, -16, 0x30, 1, 0UL, 0L, 0x20)]
    [InlineData(PointerMode.RelativeToNext, -16, 0x30, 1, 0UL, 0L, 0x21)]
    [InlineData(PointerMode.RelativeToBase, -16, 0x30, 1, 0UL, 0x100L, 0xF0)]
    public void Resolves_each_pointer_mode(PointerMode mode, long value, long origin, int size, ulong baseAddress, long relativeBase, long expected)
    {
        PointerTarget target = InspectorNavigation.Resolve(mode, value, origin, size, baseAddress, relativeBase, 0x1FFF);
        Assert.True(target.InRange);
        Assert.Equal((Int128)expected, target.Target);
    }

    [Fact]
    public void Targets_outside_the_document_are_reported()
    {
        PointerTarget target = InspectorNavigation.Resolve(PointerMode.AbsoluteOffset, 0x10000000, 0x40, 4, 0, 0, 0x2000);
        Assert.False(target.InRange);
        Assert.Equal((Int128)0x10000000, target.Target);
        Assert.False(InspectorNavigation.Resolve(PointerMode.RelativeToCursor, -0x41, 0x40, 1, 0, 0, 0x2000).InRange);
    }

    [Fact]
    public void Array_mode_steps_and_numbers_elements()
    {
        var array = new InspectorArrayMode(InspectorTypes.UInt32, 0x100, 4);
        long cursor = 0x100;
        for (int i = 0; i < 3; i++)
        {
            cursor = array.Step(cursor, 1, 0xFFFFF)!.Value;
        }

        Assert.Equal(0x10C, cursor);
        Assert.Equal(3, array.IndexOf(cursor));

        // 間隔 0x28: 2 回進めて 0x150、1 回戻して 0x128 (要素 1)、境界でなければ番号なし。
        array.Stride = 0x28;
        cursor = array.Step(array.Step(0x100, 1, 0xFFFFF)!.Value, 1, 0xFFFFF)!.Value;
        Assert.Equal(0x150, cursor);
        cursor = array.Step(cursor, -1, 0xFFFFF)!.Value;
        Assert.Equal(0x128, cursor);
        Assert.Equal(1, array.IndexOf(cursor));
        Assert.Null(array.IndexOf(cursor + 1));

        // 境界でない位置から戻ると、その要素の先頭へ。
        Assert.Equal(0x128, array.Step(0x129, -1, 0xFFFFF));

        // 負の番号、範囲外。
        array.Stride = 4;
        Assert.Equal(0xF0, array.OffsetOf(-4));
        Assert.Null(array.Step(0xFFFFF, 1, 0xFFFFF));
        Assert.Null(new InspectorArrayMode(InspectorTypes.UInt8, 0, 1).Step(0, -1, 10));
    }

    [Fact]
    public void Array_boundaries_cover_only_the_visible_range()
    {
        var array = new InspectorArrayMode(InspectorTypes.UInt32, 0x102, 4);
        Assert.Equal([0x102L, 0x106L, 0x10AL, 0x10EL], array.Boundaries(0x100, 0x110));
        Assert.Equal([0xFEL, 0x102L], array.Boundaries(0xFC, 0x104));
    }

    [Fact]
    public void Moving_without_history_does_not_record_jumps()
    {
        using var doc = new Document(new MemoryByteSource(new byte[0x1000]), Options());
        var editor = new EditorState(doc);
        editor.GoTo(0x100);
        editor.MoveCursorWithoutHistory(0x104);
        editor.MoveCursorWithoutHistory(0x108);
        Assert.Equal(0x108, editor.Cursor);
        editor.GoBack();
        Assert.Equal(0, editor.Cursor);
    }
}
