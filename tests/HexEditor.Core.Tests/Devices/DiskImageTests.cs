using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Devices;

/// <summary>ENG-31 ディスクイメージを開く (セクタサイズの指定)。</summary>
public sealed class DiskImageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-image").FullName;

    [Fact]
    [Trait("TC", "TC-ENG-31-01")]
    public void Default_sector_size_depends_on_the_extension()
    {
        Assert.Equal(2048, DiskImage.DefaultSectorSize("disk.iso"));
        Assert.Equal(512, DiskImage.DefaultSectorSize("disk.img"));
        Assert.False(DiskImage.IsValidSectorSize(1000));
        Assert.False(DiskImage.IsValidSectorSize(131072));
        Assert.True(DiskImage.IsValidSectorSize(4096));
    }

    [Fact]
    [Trait("TC", "TC-ENG-31-02")]
    public void Sector_size_4096_drives_the_sector_expression_and_insert_is_disabled()
    {
        string path = Path.Combine(_dir, "disk.img");
        File.WriteAllBytes(path, new byte[4 * 1024 * 1024]);
        using DiskImageByteSource source = DiskImage.Open(path, new DiskImageOptions { SectorSize = 4096 });
        Assert.Equal(4096, source.LogicalSectorSize);
        Assert.False(source.Capabilities.HasFlag(SourceCapabilities.CanResize));

        var doc = new Document(source);
        Assert.False(doc.CanResize);
        var editor = new EditorState(doc) { VisibleRows = 10 };
        Assert.Equal(8192, editor.SectorSize * 2);
        Assert.Throws<FixedLengthException>(() => doc.Insert(0, new byte[] { 1 }));
    }

    [Fact]
    public void Allow_resize_keeps_the_file_capabilities()
    {
        string path = Path.Combine(_dir, "resize.img");
        File.WriteAllBytes(path, new byte[8192]);
        using DiskImageByteSource source = DiskImage.Open(path, new DiskImageOptions { SectorSize = 512, AllowResize = true });
        Assert.True(source.Capabilities.HasFlag(SourceCapabilities.CanResize));
        Assert.Equal(512, source.LogicalSectorSize);
    }

    [Fact]
    public void Remainder_reports_a_partial_last_sector()
    {
        Assert.Equal(0, DiskImage.Remainder(8192, 4096));
        Assert.Equal(100, DiskImage.Remainder(8292, 4096));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
