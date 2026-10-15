using System.IO.Compression;
using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>画像表示 (VIEW-36) の範囲の計算・画素の解読・PNG の書き出し。画面の操作は UI テスト (TC-VIEW-36-xx)。</summary>
public sealed class ByteImageTests
{
    [Fact]
    public void Bottom_up_bgr_rows_map_to_the_bitmap_layout()
    {
        // 幅 2、高さ 2 の BMP と同じ並び (下の行が先)。
        byte[] data =
        [
            0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, // 下の行: 赤、緑
            0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, // 上の行: 青、白
        ];
        ByteImageLayout layout = ByteImage.Layout(BytePixelFormat.Bgr888, 2, 0, bottomUp: true, start: 0, documentLength: data.Length, maxRows: 100);
        Assert.Equal(2, layout.Rows);
        Assert.Equal(6, layout.Stride);
        uint[] pixels = new uint[4];
        ByteImage.Render(layout, data, [], pixels);
        Assert.Equal([0xFF0000FFu, 0xFFFFFFFFu, 0xFFFF0000u, 0xFF00FF00u], pixels);

        // 上から 0 行目はデータの最後の行。
        Assert.Equal(6, layout.OffsetOf(0, 0));
        Assert.Equal(3, layout.OffsetOf(1, 1));
        Assert.Equal((1, 1), layout.PixelOf(4));
    }

    [Fact]
    public void Rows_stop_at_the_end_and_past_the_end_is_a_checkerboard()
    {
        byte[] data = [1, 2, 3, 4, 5];
        ByteImageLayout layout = ByteImage.Layout(BytePixelFormat.Gray8, 4, 0, false, 0, data.Length, 100);
        Assert.Equal(2, layout.Rows);
        uint[] pixels = new uint[8];
        ByteImage.Render(layout, data, [], pixels);
        Assert.Equal(0xFF050505u, pixels[4]);
        Assert.Equal(ByteImage.Checker(1, 1), pixels[5]);

        // 読み取れないバイトは斜線。
        ByteImage.Render(layout, data, [ByteState.Valid, ByteState.Unreadable, ByteState.Valid, ByteState.Valid, ByteState.Valid], pixels);
        Assert.Equal(ByteImage.Hatch(1, 0), pixels[1]);
    }

    [Fact]
    public void Stride_skips_row_padding_and_the_view_is_capped_at_64_MB()
    {
        ByteImageLayout padded = ByteImage.Layout(BytePixelFormat.Rgb888, 3, 12, false, 0x10, 0x1000, 10);
        Assert.Equal(12, padded.Stride);
        Assert.Equal(0x10 + 12, padded.OffsetOf(0, 1));

        // 幅 65,536 × 4 バイト (256 KB) × 1,000 行 = 250 MB → 64 MB までの 256 行。
        ByteImageLayout big = ByteImage.Layout(BytePixelFormat.Rgba8888, 65536, 0, false, 0, 100L << 30, 1000);
        Assert.True(big.Truncated);
        Assert.True(big.ByteLength <= ByteImage.MaxBytes);
        Assert.Equal(256, big.Rows);
    }

    [Theory]
    [InlineData(BytePixelFormat.Mono1, new byte[] { 0b1000_0000 }, 0xFFFFFFFFu)]
    [InlineData(BytePixelFormat.Gray4, new byte[] { 0xF0 }, 0xFFFFFFFFu)]
    [InlineData(BytePixelFormat.Rgb565Le, new byte[] { 0x00, 0xF8 }, 0xFFFF0000u)]
    [InlineData(BytePixelFormat.Rgb565Be, new byte[] { 0x07, 0xE0 }, 0xFF00FF00u)]
    [InlineData(BytePixelFormat.Rgba8888, new byte[] { 1, 2, 3, 4 }, 0x04010203u)]
    [InlineData(BytePixelFormat.Argb8888, new byte[] { 4, 1, 2, 3 }, 0x04010203u)]
    public void Decodes_pixel_formats(BytePixelFormat format, byte[] data, uint expected)
    {
        ByteImageLayout layout = ByteImage.Layout(format, 1, 0, false, 0, data.Length, 1);
        uint[] pixels = new uint[1];
        ByteImage.Render(layout, data, [], pixels);
        Assert.Equal(expected, pixels[0]);
    }

    [Fact]
    public void Pixel_text_shows_the_value()
    {
        Assert.Equal("RGB(255, 128, 0)", ByteImage.PixelText(BytePixelFormat.Bgr888, 0xFFFF8000));
        Assert.Equal("Gray 128", ByteImage.PixelText(BytePixelFormat.Gray8, 0xFF808080));
    }

    [Fact]
    public void Png_round_trips_the_pixels()
    {
        uint[] pixels = [0xFFFF0000, 0xFF00FF00, 0x800000FF, 0xFFFFFFFF, 0xFF000000, 0xFF123456];
        using var stream = new MemoryStream();
        PngEncoder.Write(stream, 3, 2, pixels);
        Assert.Equal(pixels, DecodePng(stream.ToArray(), out int width, out int height));
        Assert.Equal((3, 2), (width, height));
    }

    /// <summary>このエンコーダーの出力 (8 bit RGBA、フィルタなし) だけを読む最小の PNG の解読 (CRC も確かめる)。</summary>
    internal static uint[] DecodePng(byte[] png, out int width, out int height)
    {
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        int pos = 8;
        width = height = 0;
        using var idat = new MemoryStream();
        while (pos < png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            byte[] body = png[(pos + 8)..(pos + 8 + length)];
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length));
            Assert.Equal(Crc32(png.AsSpan(pos + 4, 4 + length)), crc);
            if (type == "IHDR")
            {
                width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body);
                height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(4));
            }
            else if (type == "IDAT")
            {
                idat.Write(body);
            }

            pos += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        uint[] pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * (1 + width * 4)]);
            for (int x = 0; x < width; x++)
            {
                int o = y * (1 + width * 4) + 1 + x * 4;
                pixels[y * width + x] = (uint)(bytes[o + 3] << 24 | bytes[o] << 16 | bytes[o + 1] << 8 | bytes[o + 2]);
            }
        }

        return pixels;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
