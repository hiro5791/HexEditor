using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>FOR-21 の仕様 9 (VHD・VHDX・VMDK の読み込み)。テスト用の仮想ディスクは各形式の仕様のとおりに直接書く。</summary>
public sealed class VirtualDiskTests
{
    private const long MiB = 1024 * 1024;

    /// <summary>まばらな内容: 1 MiB ごとの目印と、3 MiB〜3.5 MiB の乱数。それ以外は 0。</summary>
    private static void Pattern(long offset, Span<byte> destination)
    {
        destination.Clear();
        for (long p = (offset + MiB - 1) / MiB * MiB; p < offset + destination.Length; p += MiB)
        {
            byte[] marker = TestDataCatalog.Marker(p);
            int at = (int)(p - offset);
            marker.AsSpan(0, Math.Min(marker.Length, destination.Length - at)).CopyTo(destination[at..]);
        }

        long from = Math.Max(offset, 3 * MiB);
        long to = Math.Min(offset + destination.Length, 3 * MiB + MiB / 2);
        if (from < to)
        {
            TestDataCatalog.Random(9, from, destination.Slice((int)(from - offset), (int)(to - from)));
        }
    }

    private static void Child(long offset, Span<byte> destination)
    {
        // 親の内容に、5 MiB の位置の 1 セクタだけ変えたもの (差分ディスクの内容)。
        Pattern(offset, destination);
        for (int i = 0; i < destination.Length; i++)
        {
            if (offset + i is >= 5 * MiB and < 5 * MiB + 512)
            {
                destination[i] = 0xC5;
            }
        }
    }

    private static void AssertContent(IByteSource source, long size, VirtualDiskWriters.Content expected)
    {
        Assert.Equal(size, source.Length);
        byte[] actual = new byte[MiB];
        byte[] want = new byte[MiB];
        for (long pos = 0; pos < size; pos += MiB)
        {
            int n = (int)Math.Min(MiB, size - pos);
            Assert.True(source.Read(pos, actual.AsSpan(0, n)).IsComplete);
            expected(pos, want.AsSpan(0, n));
            Assert.True(want.AsSpan(0, n).SequenceEqual(actual.AsSpan(0, n)), $"mismatch at block 0x{pos:X}");
        }

        // ブロックの境界をまたぐ読み込み。
        byte[] cross = new byte[4096];
        byte[] crossWant = new byte[4096];
        source.Read(3 * MiB - 2048, cross);
        expected(3 * MiB - 2048, crossWant);
        Assert.Equal(crossWant, cross);
    }

    [Fact]
    public void Vhd_fixed_dynamic_and_differencing_read_like_the_original_disk()
    {
        using var dir = new ImageTempDir();
        long size = 8 * MiB;
        string fixedPath = dir.File("fixed.vhd");
        VirtualDiskWriters.VhdFixed(fixedPath, size, Pattern);
        using (IImageSource image = ImageOpener.Open(fixedPath))
        {
            Assert.Equal(ImageFormat.Vhd, image.Info.Format);
            Assert.Equal("fixed", image.Info.Subtype);
            AssertContent(image, size, Pattern);
        }

        string parent = dir.File("parent.vhd");
        VirtualDiskWriters.VhdDynamic(parent, size, Pattern);
        using (IImageSource image = ImageOpener.Open(parent))
        {
            Assert.Equal("dynamic", image.Info.Subtype);
            AssertContent(image, size, Pattern);
        }

        string child = dir.File("child.vhd");
        VirtualDiskWriters.VhdDynamic(child, size, Child, parent: parent, sectorPresent: o => o is >= 5 * MiB and < 5 * MiB + 512);
        using (IImageSource image = ImageOpener.Open(child))
        {
            Assert.Equal("differencing", image.Info.Subtype);
            Assert.Equal(parent, image.Info.ParentPath);
            AssertContent(image, size, Child);
        }

        File.Delete(parent);
        Assert.Throws<ParentImageNotFoundException>(() => ImageOpener.Open(child));
    }

    [Fact]
    public void Vhdx_dynamic_fixed_4k_and_differencing_read_like_the_original_disk()
    {
        using var dir = new ImageTempDir();
        long size = 8 * MiB;
        string dynamicPath = dir.File("d.vhdx");
        VirtualDiskWriters.Vhdx(dynamicPath, size, Pattern);
        using (IImageSource image = ImageOpener.Open(dynamicPath))
        {
            Assert.Equal(ImageFormat.Vhdx, image.Info.Format);
            Assert.Equal("dynamic", image.Info.Subtype);
            Assert.Equal(512, image.LogicalSectorSize);
            AssertContent(image, size, Pattern);
        }

        string fixedPath = dir.File("f4k.vhdx");
        VirtualDiskWriters.Vhdx(fixedPath, size, Pattern, blockSize: MiB, sectorSize: 4096, fixedDisk: true);
        using (IImageSource image = ImageOpener.Open(fixedPath))
        {
            Assert.Equal("fixed", image.Info.Subtype);
            Assert.Equal(4096, image.LogicalSectorSize);
            AssertContent(image, size, Pattern);
        }

        string child = dir.File("child.avhdx");
        VirtualDiskWriters.Vhdx(child, size, Child, parent: dynamicPath, sectorPresent: o => o is >= 5 * MiB and < 5 * MiB + 512);
        using (IImageSource image = ImageOpener.Open(child))
        {
            Assert.Equal("differencing", image.Info.Subtype);
            AssertContent(image, size, Child);
        }
    }

    [Fact]
    public void Vmdk_monolithic_sparse_stream_optimized_and_flat_extents_read_like_the_original_disk()
    {
        using var dir = new ImageTempDir();
        long size = 8 * MiB;
        string sparse = dir.File("s.vmdk");
        VirtualDiskWriters.VmdkSparse(sparse, size, Pattern);
        using (IImageSource image = ImageOpener.Open(sparse))
        {
            Assert.Equal(ImageFormat.Vmdk, image.Info.Format);
            Assert.Equal("monolithicSparse", image.Info.Subtype);
            AssertContent(image, size, Pattern);
        }

        string stream = dir.File("o.vmdk");
        VirtualDiskWriters.VmdkSparse(stream, size, Pattern, streamOptimized: true);
        using (IImageSource image = ImageOpener.Open(stream))
        {
            Assert.Equal("streamOptimized", image.Info.Subtype);
            AssertContent(image, size, Pattern);
        }

        string flat = dir.File("flat.vmdk");
        VirtualDiskWriters.VmdkFlat(flat, 3 * MiB, 2 * MiB, 3 * MiB, (o, d) =>
        {
            Pattern(o, d);
            if (o >= 3 * MiB && o < 5 * MiB)
            {
                d.Clear();
            }
        });
        using (IImageSource image = ImageOpener.Open(flat))
        {
            Assert.Equal("twoGbMaxExtentFlat", image.Info.Subtype);
            Assert.Equal(3, image.Info.Files.Count);
            byte[] zero = new byte[512];
            Assert.True(image.Read(4 * MiB, zero).IsComplete);
            Assert.All(zero, b => Assert.Equal(0, b));
            byte[] marker = new byte[17];
            image.Read(6 * MiB, marker);
            Assert.Equal(TestDataCatalog.Marker(6 * MiB), marker);
        }

        string delta = dir.File("delta.vmdk");
        VirtualDiskWriters.VmdkSparse(delta, size, (o, d) =>
        {
            d.Clear();
            for (int i = 0; i < d.Length; i++)
            {
                if (o + i is >= 5 * MiB and < 5 * MiB + 512)
                {
                    d[i] = 0xC5;
                }
            }
        }, parentHint: "s.vmdk");
        using (IImageSource image = ImageOpener.Open(delta))
        {
            Assert.EndsWith("s.vmdk", image.Info.ParentPath);
            byte[] changed = new byte[512];
            image.Read(5 * MiB, changed);
            Assert.All(changed, b => Assert.Equal(0xC5, b));
            byte[] fromParent = new byte[17];
            image.Read(MiB, fromParent);
            Assert.Equal(TestDataCatalog.Marker(MiB), fromParent);
        }
    }
}
