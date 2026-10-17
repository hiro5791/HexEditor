using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>画像表示 (VIEW-36) の画素の形式 (仕様 2 の表)。</summary>
public enum BytePixelFormat
{
    Mono1,
    Gray2,
    Gray4,
    Gray8,

    /// <summary>8 bit (バイトテーマの色)。</summary>
    Theme8,
    Rgb565Le,
    Rgb565Be,
    Rgb555,
    Rgb888,
    Bgr888,
    Rgba8888,
    Bgra8888,
    Argb8888,
}

/// <summary>開始位置の決め方 (VIEW-36 の仕様 2)。</summary>
public enum ByteImageStart
{
    FollowCursor,
    SelectionStart,
    Fixed,
}

/// <summary>
/// 画像として描く範囲: 開始位置、1 行のバイト数 (ストライド)、幅 (画素)、行数。<see cref="BottomUp"/> なら画像の一番上の行がデータの最後の行
/// (BMP 形式)。<see cref="Truncated"/> は表示範囲が 64 MB を超えたため上端から 64 MB までにした印。
/// </summary>
public sealed record ByteImageLayout(long Start, long Stride, int Width, int Rows, BytePixelFormat Format, bool BottomUp, bool Truncated)
{
    /// <summary>読む範囲の長さ (行数 × ストライド)。</summary>
    public long ByteLength => (long)Rows * Stride;

    /// <summary>画像の (x, y) の画素の先頭バイトの位置 (上から y 行目。下から上ならデータの行を逆にする)。</summary>
    public long OffsetOf(int x, int y)
    {
        int row = BottomUp ? Rows - 1 - y : y;
        return Start + row * Stride + (long)x * ByteImage.BitsPerPixel(Format) / 8;
    }

    /// <summary>位置を含む画素 (x, y)。描いている範囲の外なら null。</summary>
    public (int X, int Y)? PixelOf(long offset)
    {
        if (offset < Start || Rows == 0)
        {
            return null;
        }

        long delta = offset - Start;
        long row = delta / Stride;
        long inRow = delta % Stride;
        long x = inRow * 8 / ByteImage.BitsPerPixel(Format);
        if (row >= Rows || x >= Width)
        {
            return null;
        }

        int y = BottomUp ? Rows - 1 - (int)row : (int)row;
        return ((int)x, y);
    }
}

/// <summary>
/// バイト列を画素の並びとして画像にする (VIEW-36)。読むのは表示中の範囲 (幅 × 行数 × 画素のバイト数) だけで、ファイルサイズに依存しない。
/// 画素は 0xAARRGGBB で返す。範囲が末尾を超える部分は市松模様 (透明)、読み取れない範囲は斜線で描く (「エラー」)。
/// </summary>
public static class ByteImage
{
    /// <summary>表示範囲の上限 (64 MB。「巨大ファイル・長時間処理」)。</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public const int MaxWidth = 65_536;

    public static int BitsPerPixel(BytePixelFormat format) => format switch
    {
        BytePixelFormat.Mono1 => 1,
        BytePixelFormat.Gray2 => 2,
        BytePixelFormat.Gray4 => 4,
        BytePixelFormat.Gray8 or BytePixelFormat.Theme8 => 8,
        BytePixelFormat.Rgb565Le or BytePixelFormat.Rgb565Be or BytePixelFormat.Rgb555 => 16,
        BytePixelFormat.Rgb888 or BytePixelFormat.Bgr888 => 24,
        _ => 32,
    };

    /// <summary>1 行のバイト数の最小値 (幅 × 画素のバイト数。1 bit などは切り上げ)。</summary>
    public static long MinStride(BytePixelFormat format, int width) => ((long)width * BitsPerPixel(format) + 7) / 8;

    /// <summary>
    /// 描く範囲を決める。行数は <paramref name="maxRows"/> (パネルに収まる行数) と、データの末尾までの行数の小さい方。表示範囲が 64 MB を超える
    /// 場合は上端から 64 MB までの行にする。<paramref name="stride"/> が 0 以下なら幅 × 画素のバイト数。
    /// </summary>
    public static ByteImageLayout Layout(BytePixelFormat format, int width, long stride, bool bottomUp, long start, long documentLength, int maxRows)
    {
        width = Math.Clamp(width, 1, MaxWidth);
        stride = Math.Max(stride, MinStride(format, width));
        start = Math.Clamp(start, 0, Math.Max(0, documentLength));
        long available = documentLength - start;
        long dataRows = available <= 0 ? 0 : (available + stride - 1) / stride;
        long rows = Math.Min(Math.Max(0, maxRows), dataRows);
        bool truncated = false;
        if (rows * stride > MaxBytes)
        {
            rows = Math.Max(1, MaxBytes / stride);
            truncated = true;
        }

        return new ByteImageLayout(start, stride, width, (int)rows, format, bottomUp, truncated);
    }

    /// <summary>
    /// 画素を描く。<paramref name="data"/> と <paramref name="states"/> は <see cref="ByteImageLayout.Start"/> から読んだバイト (末尾で短くてよい)。
    /// <paramref name="pixels"/> は幅 × 行数。<paramref name="theme"/> は 8 bit (バイトテーマの色) の色 (null なら分類の色)。
    /// </summary>
    public static void Render(ByteImageLayout layout, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, Span<uint> pixels, Func<byte, uint>? theme = null)
    {
        int bits = BitsPerPixel(layout.Format);
        for (int y = 0; y < layout.Rows; y++)
        {
            int row = layout.BottomUp ? layout.Rows - 1 - y : y;
            long rowStart = row * layout.Stride;
            for (int x = 0; x < layout.Width; x++)
            {
                long bitOffset = rowStart * 8 + (long)x * bits;
                long first = bitOffset / 8;
                long last = (bitOffset + bits - 1) / 8;
                uint color;
                if (last >= data.Length)
                {
                    color = Checker(x, y);
                }
                else if (!states.IsEmpty && WorstState(states, first, last) is var state && state != ByteState.Valid)
                {
                    // 読み取れない範囲は斜線、読み込み中は灰色 (読み込みが終われば描き直す)。
                    color = state == ByteState.Loading ? 0xFF808080 : Hatch(x, y);
                }
                else
                {
                    color = Decode(layout.Format, data, first, (int)(bitOffset % 8), theme);
                }

                pixels[y * layout.Width + x] = color;
            }
        }
    }

    private static ByteState WorstState(ReadOnlySpan<ByteState> states, long first, long last)
    {
        ByteState worst = ByteState.Valid;
        for (long i = first; i <= last && i < states.Length; i++)
        {
            ByteState st = states[(int)i];
            if (st is ByteState.Unreadable)
            {
                return st;
            }

            if (st is ByteState.Loading)
            {
                worst = st;
            }
        }

        return worst;
    }

    /// <summary>末尾を超えた画素の市松模様 (4 画素ごと)。</summary>
    public static uint Checker(int x, int y) => ((x / 4) + (y / 4)) % 2 == 0 ? 0xFFCCCCCC : 0xFF999999;

    /// <summary>読み取れない画素の斜線。</summary>
    public static uint Hatch(int x, int y) => (x + y) % 6 < 2 ? 0xFF606060 : 0xFFE0E0E0;

    private static uint Gray(int v) => 0xFF000000 | (uint)(v << 16) | (uint)(v << 8) | (uint)v;

    private static uint Rgb(int r, int g, int b, int a = 255) => (uint)(a << 24) | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

    private static uint Decode(BytePixelFormat format, ReadOnlySpan<byte> data, long first, int bitShift, Func<byte, uint>? theme)
    {
        int i = (int)first;
        switch (format)
        {
            case BytePixelFormat.Mono1:
                return ((data[i] >> (7 - bitShift)) & 1) != 0 ? 0xFFFFFFFF : 0xFF000000;
            case BytePixelFormat.Gray2:
                return Gray(((data[i] >> (6 - bitShift)) & 3) * 255 / 3);
            case BytePixelFormat.Gray4:
                return Gray(((data[i] >> (4 - bitShift)) & 15) * 255 / 15);
            case BytePixelFormat.Gray8:
                return Gray(data[i]);
            case BytePixelFormat.Theme8:
                if (theme is not null)
                {
                    return theme(data[i]);
                }

                SchemeColor c = ByteTheme.CategoryColor(ByteTheme.Classify(data[i]), dark: false);
                return Rgb(c.R, c.G, c.B);
            case BytePixelFormat.Rgb565Le:
            case BytePixelFormat.Rgb565Be:
            {
                ushort v = format == BytePixelFormat.Rgb565Le ? BinaryPrimitives.ReadUInt16LittleEndian(data[i..]) : BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
                int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31;
                return Rgb((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));
            }

            case BytePixelFormat.Rgb555:
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                return Rgb((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2));
            }

            case BytePixelFormat.Rgb888:
                return Rgb(data[i], data[i + 1], data[i + 2]);
            case BytePixelFormat.Bgr888:
                return Rgb(data[i + 2], data[i + 1], data[i]);
            case BytePixelFormat.Rgba8888:
                return Rgb(data[i], data[i + 1], data[i + 2], data[i + 3]);
            case BytePixelFormat.Bgra8888:
                return Rgb(data[i + 2], data[i + 1], data[i], data[i + 3]);
            default:
                return Rgb(data[i + 1], data[i + 2], data[i + 3], data[i]);
        }
    }

    /// <summary>
    /// 画素の値の表示 (仕様 6。例: <c>RGB(255, 128, 0)</c>、グレーは <c>Gray 128</c>、透明度のある形式は <c>RGBA(…)</c>)。
    /// </summary>
    public static string PixelText(BytePixelFormat format, uint argb)
    {
        int a = (int)(argb >> 24), r = (int)(argb >> 16) & 255, g = (int)(argb >> 8) & 255, b = (int)argb & 255;
        return format switch
        {
            BytePixelFormat.Mono1 or BytePixelFormat.Gray2 or BytePixelFormat.Gray4 or BytePixelFormat.Gray8 =>
                string.Create(CultureInfo.InvariantCulture, $"Gray {r}"),
            BytePixelFormat.Rgba8888 or BytePixelFormat.Bgra8888 or BytePixelFormat.Argb8888 =>
                string.Create(CultureInfo.InvariantCulture, $"RGBA({r}, {g}, {b}, {a})"),
            _ => string.Create(CultureInfo.InvariantCulture, $"RGB({r}, {g}, {b})"),
        };
    }
}

/// <summary>画素 (0xAARRGGBB) を PNG (8 bit RGBA、フィルタなし) に書く (VIEW-36 の仕様 7)。</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void Write(Stream output, int width, int height, ReadOnlySpan<uint> argb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (argb.Length < (long)width * height)
        {
            throw new ArgumentException("画素が足りません。", nameof(argb));
        }

        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // ビット深度
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] line = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                line[0] = 0;
                for (int x = 0; x < width; x++)
                {
                    uint p = argb[y * width + x];
                    int o = 1 + x * 4;
                    line[o] = (byte)(p >> 16);
                    line[o + 1] = (byte)(p >> 8);
                    line[o + 2] = (byte)p;
                    line[o + 3] = (byte)(p >> 24);
                }

                zlib.Write(line);
            }
        }

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = 0xFFFFFFFF;
        crc = Update(crc, typeBytes);
        crc = Update(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        output.Write(crcBytes);
    }

    private static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
