using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Devices;

/// <summary>VIEW-32 セクタ単位の移動。</summary>
public sealed class SectorNavigationTests
{
    private static EditorState Editor(IByteSource source) => new(new Document(source)) { VisibleRows = 10 };

    [Fact]
    [Trait("TC", "TC-VIEW-32-01")]
    public void Next_sector_moves_to_the_next_sector_start()
    {
        EditorState editor = Editor(new VirtualByteSource(0x10000));
        editor.GoTo(0x210);
        Assert.True(editor.MoveNextSector());
        Assert.Equal(0x400, editor.Cursor);
        Assert.Equal(editor.Layout.RowOf(0x400), editor.TopRow);
    }

    [Fact]
    [Trait("TC", "TC-VIEW-32-02")]
    public void Previous_sector_goes_to_the_current_then_previous_start()
    {
        EditorState editor = Editor(new VirtualByteSource(0x10000));
        editor.GoTo(0x210);
        Assert.True(editor.MovePreviousSector());
        Assert.Equal(0x200, editor.Cursor);
        Assert.True(editor.MovePreviousSector());
        Assert.Equal(0, editor.Cursor);
        Assert.False(editor.MovePreviousSector());
    }

    [Fact]
    [Trait("TC", "TC-VIEW-32-03")]
    public void Disk_with_4096_byte_sectors_moves_in_4096_byte_steps()
    {
        var access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 1, SectorSize = 4096, Size = 64L * 1024 * 1024 }],
        });
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(1), writable: false);
        var source = new DeviceByteSource(handle, access, new DeviceOpenInfo { Path = handle.Path, DisplayName = "Disk 1" });
        EditorState editor = Editor(source);
        Assert.Equal(4096, editor.SectorSize);
        Assert.True(editor.MoveNextSector());
        Assert.Equal(4096, editor.Cursor);
        Assert.True(editor.MoveNextSector());
        Assert.Equal(8192, editor.Cursor);
        Assert.Equal(2, editor.CursorSector);
    }

    [Fact]
    public void Last_sector_and_end_position_do_not_move()
    {
        // 長さ 1,000 (端数のセクタを含めて 2 セクタ)。末尾の次の位置は最後のセクタに属する。
        Assert.Equal(2, EditorState.SectorCount(1000, 512));
        Assert.Equal(1, EditorState.SectorOf(1000, 1000, 512));
        Assert.Null(EditorState.NextSectorTarget(600, 1000, 512));
        Assert.Equal(512, EditorState.NextSectorTarget(0, 1000, 512));
        Assert.Null(EditorState.NextSectorTarget(0, 0, 512));
        Assert.Equal(1, EditorState.SectorOf(1024, 1024, 512));
    }
}
