using System.Globalization;

namespace HexEditor.Core.Tools;

/// <summary>色 (8 bit ずつの RGBA)。</summary>
public readonly record struct ColorValue(byte R, byte G, byte B, byte A = 255)
{
    /// <summary><c>#AARRGGBB</c> (大文字)。</summary>
    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>カラーピッカーのピクセル形式 (TOOL-27 の仕様 2)。</summary>
public sealed record PixelFormatInfo(string Id, string Name, int Bytes, bool HasEndian);

/// <summary>
/// 色とピクセル形式のバイト列の相互変換 (TOOL-27)。16 bit の形式はエンディアンを切り替えられる。成分を 8 bit に広げる規則
/// (上位のビットを下位に繰り返す) と丸め (最も近い値へ、偶数への丸め) はデータインスペクタの色の型 (INSP-12) と同じ。
/// </summary>
public static class ColorFormats
{
    public static IReadOnlyList<PixelFormatInfo> All { get; } =
    [
        new("rgb888", "RGB888", 3, false),
        new("bgr888", "BGR888", 3, false),
        new("rgba8888", "RGBA8888", 4, false),
        new("argb8888", "ARGB8888", 4, false),
        new("bgra8888", "BGRA8888", 4, false),
        new("abgr8888", "ABGR8888", 4, false),
        new("rgb565", "RGB565", 2, true),
        new("bgr565", "BGR565", 2, true),
        new("rgb555", "RGB555 (XRGB1555)", 2, true),
        new("argb1555", "ARGB1555", 2, true),
        new("rgba4444", "RGBA4444", 2, true),
        new("argb4444", "ARGB4444", 2, true),
        new("gray8", "Gray8", 1, false),
    ];

    public static PixelFormatInfo? Find(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private static int Narrow(byte value, int bits) => (int)Math.Round(value * ((1 << bits) - 1) / 255.0, MidpointRounding.ToEven);

    private static byte Widen(int value, int bits)
    {
        int max = (1 << bits) - 1;
        return bits switch
        {
            1 => (byte)(value * 255),
            4 => (byte)(value * 17),
            5 => (byte)((value << 3) | (value >> 2)),
            6 => (byte)((value << 2) | (value >> 4)),
            _ => (byte)(value * 255 / max),
        };
    }

    /// <summary>グレースケール (ITU-R BT.601 の輝度)。</summary>
    public static byte Luma(ColorValue c) => (byte)Math.Clamp((int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B), 0, 255);

    /// <summary>色を、ピクセル形式のバイト列にする。</summary>
    public static byte[] Encode(ColorValue c, string formatId, bool bigEndian)
    {
        switch (formatId.ToLowerInvariant())
        {
            case "rgb888":
                return [c.R, c.G, c.B];
            case "bgr888":
                return [c.B, c.G, c.R];
            case "rgba8888":
                return [c.R, c.G, c.B, c.A];
            case "argb8888":
                return [c.A, c.R, c.G, c.B];
            case "bgra8888":
                return [c.B, c.G, c.R, c.A];
            case "abgr8888":
                return [c.A, c.B, c.G, c.R];
            case "gray8":
                return [Luma(c)];
            case "rgb565":
                return Word((Narrow(c.R, 5) << 11) | (Narrow(c.G, 6) << 5) | Narrow(c.B, 5), bigEndian);
            case "bgr565":
                return Word((Narrow(c.B, 5) << 11) | (Narrow(c.G, 6) << 5) | Narrow(c.R, 5), bigEndian);
            case "rgb555":
                return Word((Narrow(c.R, 5) << 10) | (Narrow(c.G, 5) << 5) | Narrow(c.B, 5), bigEndian);
            case "argb1555":
                return Word((c.A >= 128 ? 0x8000 : 0) | (Narrow(c.R, 5) << 10) | (Narrow(c.G, 5) << 5) | Narrow(c.B, 5), bigEndian);
            case "rgba4444":
                return Word((Narrow(c.R, 4) << 12) | (Narrow(c.G, 4) << 8) | (Narrow(c.B, 4) << 4) | Narrow(c.A, 4), bigEndian);
            case "argb4444":
                return Word((Narrow(c.A, 4) << 12) | (Narrow(c.R, 4) << 8) | (Narrow(c.G, 4) << 4) | Narrow(c.B, 4), bigEndian);
            default:
                throw new ArgumentException($"Unknown pixel format: {formatId}", nameof(formatId));
        }
    }

    private static byte[] Word(int value, bool bigEndian) =>
        bigEndian ? [(byte)(value >> 8), (byte)value] : [(byte)value, (byte)(value >> 8)];

    /// <summary>ピクセル形式のバイト列を色として読む。バイト数が足りなければ null。</summary>
    public static ColorValue? Decode(ReadOnlySpan<byte> bytes, string formatId, bool bigEndian)
    {
        PixelFormatInfo? info = Find(formatId);
        if (info is null || bytes.Length < info.Bytes)
        {
            return null;
        }

        int word = info.Bytes == 2 ? (bigEndian ? (bytes[0] << 8) | bytes[1] : (bytes[1] << 8) | bytes[0]) : 0;
        switch (info.Id)
        {
            case "rgb888":
                return new ColorValue(bytes[0], bytes[1], bytes[2]);
            case "bgr888":
                return new ColorValue(bytes[2], bytes[1], bytes[0]);
            case "rgba8888":
                return new ColorValue(bytes[0], bytes[1], bytes[2], bytes[3]);
            case "argb8888":
                return new ColorValue(bytes[1], bytes[2], bytes[3], bytes[0]);
            case "bgra8888":
                return new ColorValue(bytes[2], bytes[1], bytes[0], bytes[3]);
            case "abgr8888":
                return new ColorValue(bytes[3], bytes[2], bytes[1], bytes[0]);
            case "gray8":
                return new ColorValue(bytes[0], bytes[0], bytes[0]);
            case "rgb565":
                return new ColorValue(Widen(word >> 11, 5), Widen((word >> 5) & 0x3F, 6), Widen(word & 0x1F, 5));
            case "bgr565":
                return new ColorValue(Widen(word & 0x1F, 5), Widen((word >> 5) & 0x3F, 6), Widen(word >> 11, 5));
            case "rgb555":
                return new ColorValue(Widen((word >> 10) & 0x1F, 5), Widen((word >> 5) & 0x1F, 5), Widen(word & 0x1F, 5));
            case "argb1555":
                return new ColorValue(Widen((word >> 10) & 0x1F, 5), Widen((word >> 5) & 0x1F, 5), Widen(word & 0x1F, 5), (word & 0x8000) != 0 ? (byte)255 : (byte)0);
            case "rgba4444":
                return new ColorValue(Widen(word >> 12, 4), Widen((word >> 8) & 0xF, 4), Widen((word >> 4) & 0xF, 4), Widen(word & 0xF, 4));
            case "argb4444":
                return new ColorValue(Widen((word >> 8) & 0xF, 4), Widen((word >> 4) & 0xF, 4), Widen(word & 0xF, 4), Widen(word >> 12, 4));
            default:
                return null;
        }
    }

    /// <summary><c>#RRGGBB</c> / <c>#AARRGGBB</c> (先頭の <c>#</c> は省略できる) を読む。</summary>
    public static ColorValue? ParseHex(string text)
    {
        string t = text.Trim().TrimStart('#');
        if (t.Length is not (6 or 8) || !t.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        uint v = uint.Parse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return t.Length == 6
            ? new ColorValue((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new ColorValue((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
    }

    /// <summary>バイト列の Hex 表記 (<c>00 FC</c>)。</summary>
    public static string BytesText(ReadOnlySpan<byte> bytes) => Clipboard.HexText.Format(bytes);
}
