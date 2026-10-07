using System.Buffers.Binary;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// ウィンドウの画像 (32 ビット BGRA、上の行から)。PrintWindow (PW_RENDERFULLCONTENT) で取るため、ウィンドウを前面に出さず、
/// ほかのウィンドウに隠れていても取れる。
/// </summary>
public sealed class WindowImage(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Bgra { get; } = bgra;

    /// <summary>(x, y) の色 (0xAARRGGBB)。</summary>
    public uint Pixel(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (uint)((Bgra[i + 3] << 24) | (Bgra[i + 2] << 16) | (Bgra[i + 1] << 8) | Bgra[i]);
    }

    public static unsafe WindowImage Capture(nint hwnd)
    {
        NativeMethods.GetWindowRect(hwnd, out NativeMethods.Rect rect);
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        nint screen = NativeMethods.GetDC(0);
        nint dc = NativeMethods.CreateCompatibleDC(screen);
        nint bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
        nint old = NativeMethods.SelectObject(dc, bitmap);
        try
        {
            NativeMethods.PrintWindow(hwnd, dc, NativeMethods.PwRenderFullContent);
            NativeMethods.SelectObject(dc, old);
            var header = new NativeMethods.BitmapInfoHeader
            {
                Size = (uint)sizeof(NativeMethods.BitmapInfoHeader),
                Width = width,
                Height = -height, // 上の行から
                Planes = 1,
                BitCount = 32,
            };
            byte[] pixels = new byte[width * height * 4];
            fixed (byte* p = pixels)
            {
                NativeMethods.GetDIBits(dc, bitmap, 0, (uint)height, p, &header, 0);
            }

            return new WindowImage(width, height, pixels);
        }
        finally
        {
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(dc);
            NativeMethods.ReleaseDC(0, screen);
        }
    }

    /// <summary>BMP 形式で保存する。</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int headerSize = 14 + 40;
        byte[] file = new byte[headerSize + Bgra.Length];
        Span<byte> s = file;
        s[0] = (byte)'B';
        s[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(s[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[10..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(s[22..], -Height);
        BinaryPrimitives.WriteInt16LittleEndian(s[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(s[34..], Bgra.Length);
        Bgra.CopyTo(s[headerSize..]);
        File.WriteAllBytes(path, file);
    }
}
