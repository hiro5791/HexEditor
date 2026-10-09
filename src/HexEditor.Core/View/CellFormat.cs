using System.Buffers.Binary;
using System.Globalization;

namespace HexEditor.Core.View;

/// <summary>Hex 列のセルの表示形式 (VIEW-10 の仕様 1)。</summary>
public enum CellFormat
{
    /// <summary>16 進 (既定。1 バイト、2 文字)。</summary>
    Hex,

    /// <summary>10 進 (符号なし。1 バイト、3 文字)。</summary>
    Decimal,

    /// <summary>10 進 (符号あり。1 バイト、4 文字)。</summary>
    SignedDecimal,

    /// <summary>8 進 (1 バイト、3 文字)。</summary>
    Octal,

    /// <summary>2 進 (1 バイト、8 文字)。</summary>
    Binary,

    Int16Hex,
    Int16Decimal,
    Int16SignedDecimal,
    Int32Hex,
    Int32Decimal,
    Int32SignedDecimal,
    Int64Hex,
    Int64Decimal,
    Int64SignedDecimal,

    /// <summary>IEEE 754 単精度 (4 バイト)。</summary>
    Float,

    /// <summary>IEEE 754 倍精度 (8 バイト)。</summary>
    Double,
}

/// <summary>
/// セルの表示形式の文字列化 (VIEW-10)。データは変えず、表示だけを作る。数値は地域設定に依存しない (小数点は常に <c>.</c>。仕様 4)。
/// 2 バイト以上の単位の値はエンディアン (VIEW-11) に従って解釈する (仕様 3)。
/// </summary>
public static class CellFormatter
{
    /// <summary>形式の単位 (1 セルのバイト数)。</summary>
    public static int Unit(CellFormat format) => format switch
    {
        CellFormat.Int16Hex or CellFormat.Int16Decimal or CellFormat.Int16SignedDecimal => 2,
        CellFormat.Int32Hex or CellFormat.Int32Decimal or CellFormat.Int32SignedDecimal or CellFormat.Float => 4,
        CellFormat.Int64Hex or CellFormat.Int64Decimal or CellFormat.Int64SignedDecimal or CellFormat.Double => 8,
        _ => 1,
    };

    /// <summary>
    /// セルの文字数 (仕様 1 の表)。float / double は符号と非正規化数の印 <c>d</c> を含めて最長の表記が入る幅にする
    /// (float 15、double 24。仕様の表の注)。
    /// </summary>
    public static int Chars(CellFormat format) => format switch
    {
        CellFormat.Hex => 2,
        CellFormat.Decimal => 3,
        CellFormat.SignedDecimal => 4,
        CellFormat.Octal => 3,
        CellFormat.Binary => 8,
        CellFormat.Int16Hex => 4,
        CellFormat.Int16Decimal => 5,
        CellFormat.Int16SignedDecimal => 6,
        CellFormat.Int32Hex => 8,
        CellFormat.Int32Decimal => 10,
        CellFormat.Int32SignedDecimal => 11,
        CellFormat.Int64Hex => 16,
        CellFormat.Int64Decimal => 20,
        CellFormat.Int64SignedDecimal => 20,
        CellFormat.Float => 15,
        CellFormat.Double => 24,
        _ => 2,
    };

    /// <summary>16 進の形式 (1 バイトの Hex と、16 / 32 / 64 bit の Hex)。セルの中の各バイトに 2 文字ずつの位置がある。</summary>
    public static bool IsHexLike(CellFormat format) => format is CellFormat.Hex or CellFormat.Int16Hex or CellFormat.Int32Hex or CellFormat.Int64Hex;

    /// <summary>ステータスバーの表示形式の名前 (VIEW-40 の「表示形式」。<c>int32</c>、<c>float</c> など)。Hex は null。</summary>
    public static string? StatusName(CellFormat format) => format switch
    {
        CellFormat.Hex => null,
        CellFormat.Decimal => "uint8",
        CellFormat.SignedDecimal => "int8",
        CellFormat.Octal => "oct8",
        CellFormat.Binary => "bin8",
        CellFormat.Int16Hex => "hex16",
        CellFormat.Int16Decimal => "uint16",
        CellFormat.Int16SignedDecimal => "int16",
        CellFormat.Int32Hex => "hex32",
        CellFormat.Int32Decimal => "uint32",
        CellFormat.Int32SignedDecimal => "int32",
        CellFormat.Int64Hex => "hex64",
        CellFormat.Int64Decimal => "uint64",
        CellFormat.Int64SignedDecimal => "int64",
        CellFormat.Float => "float",
        CellFormat.Double => "double",
        _ => null,
    };

    /// <summary>
    /// 1 セルの文字列。<paramref name="bytes"/> は単位のバイト数ちょうど。<paramref name="spacePad"/> は 10 進・8 進を 0 ではなく空白で
    /// 埋める (仕様 2)。<paramref name="lowercase"/> は 16 進の小文字 (VIEW-12)。結果はセルの文字数に右寄せでそろえる。
    /// </summary>
    public static string Format(CellFormat format, ReadOnlySpan<byte> bytes, bool bigEndian, bool spacePad = false, bool lowercase = false)
    {
        int chars = Chars(format);
        string text = format switch
        {
            CellFormat.Hex => bytes[0].ToString(lowercase ? "x2" : "X2", CultureInfo.InvariantCulture),
            CellFormat.Decimal => Unsigned(bytes[0], 3, spacePad),
            CellFormat.SignedDecimal => Signed((sbyte)bytes[0], 3, spacePad),
            CellFormat.Octal => Pad(Convert.ToString(bytes[0], 8), 3, spacePad),
            CellFormat.Binary => Convert.ToString(bytes[0], 2).PadLeft(8, '0'),
            CellFormat.Int16Hex => U16(bytes, bigEndian).ToString(lowercase ? "x4" : "X4", CultureInfo.InvariantCulture),
            CellFormat.Int16Decimal => Unsigned(U16(bytes, bigEndian), 5, spacePad),
            CellFormat.Int16SignedDecimal => Signed((short)U16(bytes, bigEndian), 5, spacePad),
            CellFormat.Int32Hex => U32(bytes, bigEndian).ToString(lowercase ? "x8" : "X8", CultureInfo.InvariantCulture),
            CellFormat.Int32Decimal => Unsigned(U32(bytes, bigEndian), 10, spacePad),
            CellFormat.Int32SignedDecimal => Signed((int)U32(bytes, bigEndian), 10, spacePad),
            CellFormat.Int64Hex => U64(bytes, bigEndian).ToString(lowercase ? "x16" : "X16", CultureInfo.InvariantCulture),
            CellFormat.Int64Decimal => Unsigned(U64(bytes, bigEndian), 20, spacePad),
            CellFormat.Int64SignedDecimal => Signed((long)U64(bytes, bigEndian), 19, spacePad),
            CellFormat.Float => FloatText(BitConverter.Int32BitsToSingle((int)U32(bytes, bigEndian))),
            CellFormat.Double => DoubleText(BitConverter.Int64BitsToDouble((long)U64(bytes, bigEndian))),
            _ => string.Empty,
        };
        return text.Length < chars ? text.PadLeft(chars) : text;
    }

    /// <summary>セルの値が 0 か (2 バイト以上のセルは値全体が 0 のときだけ薄く表示する。VIEW-13 の仕様 3)。</summary>
    public static bool IsZero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);

    private static ushort U16(ReadOnlySpan<byte> b, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);

    private static ulong U64(ReadOnlySpan<byte> b, bool be) => be ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);

    private static string Pad(string digits, int width, bool spacePad) => digits.PadLeft(width, spacePad ? ' ' : '0');

    private static string Unsigned(ulong value, int digits, bool spacePad) =>
        Pad(value.ToString(CultureInfo.InvariantCulture), digits, spacePad);

    /// <summary>符号あり 10 進: 符号を常に付け、数字は <paramref name="digits"/> 桁にそろえる (例: <c>-001</c>、<c>+079</c>)。</summary>
    private static string Signed(long value, int digits, bool spacePad)
    {
        string magnitude = value == long.MinValue
            ? "9223372036854775808"
            : Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        string sign = value < 0 ? "-" : "+";
        return spacePad ? (sign + magnitude).PadLeft(digits + 1) : sign + magnitude.PadLeft(digits, '0');
    }

    /// <summary>float: 有効桁 8 桁の指数表記 (仕様 4)。NaN・無限大・非正規化数の表記も含む。</summary>
    private static string FloatText(float value)
    {
        if (float.IsNaN(value))
        {
            return "NaN";
        }

        if (float.IsInfinity(value))
        {
            return value > 0 ? "+Inf" : "-Inf";
        }

        string text = Exponent(value.ToString("E7", CultureInfo.InvariantCulture));
        return float.IsSubnormal(value) ? text + "d" : text;
    }

    /// <summary>double: 有効桁 17 桁の指数表記 (仕様 4)。</summary>
    private static string DoubleText(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "+Inf" : "-Inf";
        }

        string text = Exponent(value.ToString("E16", CultureInfo.InvariantCulture));
        return double.IsSubnormal(value) ? text + "d" : text;
    }

    /// <summary>指数の桁を最低 2 桁にする (.NET の <c>E</c> 書式は 3 桁: <c>E+008</c> → <c>E+08</c>)。</summary>
    private static string Exponent(string text)
    {
        int e = text.IndexOf('E');
        if (e < 0 || e + 2 >= text.Length)
        {
            return text;
        }

        string digits = text[(e + 2)..].TrimStart('0');
        return text[..(e + 2)] + digits.PadLeft(2, '0');
    }
}
