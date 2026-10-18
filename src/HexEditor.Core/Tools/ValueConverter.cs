using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Calculator;
using HexEditor.Core.Clipboard;
using HexEditor.Core.View;

namespace HexEditor.Core.Tools;

/// <summary>基数・データ型の変換の欄 (TOOL-24 の仕様 1)。どの欄に入力しても他の欄を更新する。</summary>
public enum ConverterField
{
    Hex,
    HexInteger,
    UnsignedDecimal,
    SignedDecimal,
    Octal,
    Binary,
    Half,
    Float,
    Double,
    Ascii,
    Utf8,
    Utf16Le,
    Utf16Be,
    Utf32Le,
    Utf32Be,
    CodePage,
    Base64,
}

/// <summary>入力の誤り。<see cref="Code"/> は説明文のリソース (<c>Converter_Error_&lt;Code&gt;</c>) の名前、<see cref="Detail"/> は文字など。</summary>
public sealed record ConversionError(ConverterField Field, string Code, string? Detail = null);

/// <summary>
/// 基数・データ型の変換 (TOOL-24)。バイト列 (最大 64 バイト) が基準で、各欄はそのバイト列の解釈。エンディアンを切り替えると数値の欄の
/// 解釈だけが変わる (バイト列は変わらない)。文字列の欄に入力した場合は、その文字コードでバイト列に変換する (変換できない文字があれば
/// 誤りにして変換しない)。
/// </summary>
public sealed class ValueConverter
{
    /// <summary>扱う最大のバイト数 (仕様 3)。</summary>
    public const int MaxBytes = 64;

    private byte[] _bytes = [];

    public ReadOnlySpan<byte> Bytes => _bytes;

    public byte[] ToArray() => [.. _bytes];

    /// <summary>数値の欄の解釈。true ならビッグエンディアン。</summary>
    public bool BigEndian { get; set; }

    /// <summary>コードページの欄の文字コード (<see cref="EncodingCatalog"/> の ID)。</summary>
    public string CodePageId { get; set; } = "cp437";

    /// <summary>バイト列を置き換える。<see cref="MaxBytes"/> を超える分は捨てる。</summary>
    public void SetBytes(ReadOnlySpan<byte> bytes) => _bytes = [.. bytes[..Math.Min(bytes.Length, MaxBytes)]];

    // ---- 欄の表示 ----

    /// <summary>欄の文字列。数値の欄は解釈できないとき (バイト数が合わない) 空欄。</summary>
    public string Get(ConverterField field)
    {
        ReadOnlySpan<byte> b = _bytes;
        switch (field)
        {
            case ConverterField.Hex:
                return HexText.Format(b);
            case ConverterField.HexInteger:
                return Unsigned() is { } hex ? "0x" + hex.ToString("X", CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.UnsignedDecimal:
                return Unsigned() is { } u ? u.ToString(CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.SignedDecimal:
                return Signed() is { } s ? s.ToString(CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.Octal:
                return Unsigned() is { } o ? ToRadix(o, 8) : string.Empty;
            case ConverterField.Binary:
                return Unsigned() is { } bin ? CalculatorFormat.Group4(ToRadix(bin, 2).PadLeft(b.Length * 8, '0')) : string.Empty;
            case ConverterField.Half:
                return b.Length == 2 ? BitConverter.UInt16BitsToHalf((ushort)ReadBits(b)).ToString("R", CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.Float:
                return b.Length == 4 ? BitConverter.UInt32BitsToSingle((uint)ReadBits(b)).ToString("R", CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.Double:
                return b.Length == 8 ? BitConverter.UInt64BitsToDouble(ReadBits(b)).ToString("R", CultureInfo.InvariantCulture) : string.Empty;
            case ConverterField.Ascii:
                return AsciiText(b);
            case ConverterField.Utf8:
                return Decode(new UTF8Encoding(false), b);
            case ConverterField.Utf16Le:
                return Decode(new UnicodeEncoding(false, false), b);
            case ConverterField.Utf16Be:
                return Decode(new UnicodeEncoding(true, false), b);
            case ConverterField.Utf32Le:
                return Decode(new UTF32Encoding(false, false), b);
            case ConverterField.Utf32Be:
                return Decode(new UTF32Encoding(true, false), b);
            case ConverterField.CodePage:
                return CodePageEncoding() is { } enc ? Decode(enc, b) : string.Empty;
            case ConverterField.Base64:
                return Convert.ToBase64String(b);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 不正なバイト列の警告があるか (仕様 1: UTF-8 / UTF-16 / UTF-32 / コードページは、不正なバイト列を置換文字と警告で示す)。
    /// </summary>
    public bool HasWarning(ConverterField field)
    {
        Encoding? strict = field switch
        {
            ConverterField.Utf8 => new UTF8Encoding(false, true),
            ConverterField.Utf16Le => new UnicodeEncoding(false, false, true),
            ConverterField.Utf16Be => new UnicodeEncoding(true, false, true),
            ConverterField.Utf32Le => new UTF32Encoding(false, false, true),
            ConverterField.Utf32Be => new UTF32Encoding(true, false, true),
            ConverterField.CodePage => StrictCodePage(),
            _ => null,
        };
        if (strict is null)
        {
            return false;
        }

        try
        {
            _ = strict.GetString(_bytes);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private Encoding? CodePageEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        EncodingEntry? entry = EncodingCatalog.Find(CodePageId);
        if (entry is null || entry.CodePage <= 1)
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(entry.CodePage, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("�"));
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private Encoding? StrictCodePage()
    {
        EncodingEntry? entry = EncodingCatalog.Find(CodePageId);
        if (entry is null || entry.CodePage <= 1)
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(entry.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string Decode(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        // 置換文字で復号する。制御文字は制御文字の絵 (U+2400 台) にして、欄の中で見えるようにする。
        Encoding lenient = (Encoding)encoding.Clone();
        lenient.DecoderFallback = new DecoderReplacementFallback("�");
        string text = lenient.GetString(bytes);
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c < 0x20 ? (char)(0x2400 + c) : c == 0x7F ? '␡' : c);
        }

        return sb.ToString();
    }

    /// <summary>ASCII の欄: 0x20〜0x7E 以外は <c>\xNN</c> (仕様 1)。</summary>
    public static string AsciiText(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b is >= 0x20 and <= 0x7E)
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    // ---- 整数の解釈 ----

    private ulong ReadBits(ReadOnlySpan<byte> b)
    {
        ulong value = 0;
        for (int i = 0; i < b.Length; i++)
        {
            byte x = BigEndian ? b[i] : b[b.Length - 1 - i];
            value = (value << 8) | x;
        }

        return value;
    }

    private ulong? Unsigned() => _bytes.Length is >= 1 and <= 8 ? ReadBits(_bytes) : null;

    private long? Signed()
    {
        if (Unsigned() is not { } u)
        {
            return null;
        }

        int bits = _bytes.Length * 8;
        return bits == 64 ? unchecked((long)u) : (u & (1UL << (bits - 1))) != 0 ? unchecked((long)(u | (ulong.MaxValue << bits))) : (long)u;
    }

    private static string ToRadix(ulong value, int radix)
    {
        if (value == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value != 0)
        {
            sb.Insert(0, (char)('0' + (int)(value % (ulong)radix)));
            value /= (ulong)radix;
        }

        return sb.ToString();
    }

    // ---- 入力 ----

    /// <summary>欄に入力した文字列を反映する。誤りなら null でなく誤りを返し、バイト列は変えない。</summary>
    public ConversionError? SetFromField(ConverterField field, string text)
    {
        switch (field)
        {
            case ConverterField.Hex:
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _bytes = [];
                        return null;
                    }

                    byte[]? parsed = HexText.TryParse(text);
                    if (parsed is null)
                    {
                        return new ConversionError(field, "InvalidHex");
                    }

                    if (parsed.Length > MaxBytes)
                    {
                        return new ConversionError(field, "TooLong", MaxBytes.ToString(CultureInfo.InvariantCulture));
                    }

                    _bytes = parsed;
                    return null;
                }

            case ConverterField.HexInteger:
                return SetInteger(field, text, 16, signed: false);
            case ConverterField.UnsignedDecimal:
                return SetInteger(field, text, 10, signed: false);
            case ConverterField.SignedDecimal:
                return SetInteger(field, text, 10, signed: true);
            case ConverterField.Octal:
                return SetInteger(field, text, 8, signed: false);
            case ConverterField.Binary:
                return SetInteger(field, text, 2, signed: false);
            case ConverterField.Half:
                return SetFloat(field, text, 2);
            case ConverterField.Float:
                return SetFloat(field, text, 4);
            case ConverterField.Double:
                return SetFloat(field, text, 8);
            case ConverterField.Ascii:
                return SetAscii(text);
            case ConverterField.Utf8:
                return SetText(field, text, new UTF8Encoding(false, true));
            case ConverterField.Utf16Le:
                return SetText(field, text, new UnicodeEncoding(false, false, true));
            case ConverterField.Utf16Be:
                return SetText(field, text, new UnicodeEncoding(true, false, true));
            case ConverterField.Utf32Le:
                return SetText(field, text, new UTF32Encoding(false, false, true));
            case ConverterField.Utf32Be:
                return SetText(field, text, new UTF32Encoding(true, false, true));
            case ConverterField.CodePage:
                return CodePageEncoding() is null
                    ? new ConversionError(field, "UnknownCodePage")
                    : SetText(field, text, StrictCodePage() ?? Encoding.ASCII);
            case ConverterField.Base64:
                {
                    try
                    {
                        byte[] decoded = Convert.FromBase64String(text.Trim());
                        if (decoded.Length > MaxBytes)
                        {
                            return new ConversionError(field, "TooLong", MaxBytes.ToString(CultureInfo.InvariantCulture));
                        }

                        _bytes = decoded;
                        return null;
                    }
                    catch (FormatException)
                    {
                        return new ConversionError(field, "InvalidBase64");
                    }
                }

            default:
                return null;
        }
    }

    private ConversionError? SetInteger(ConverterField field, string text, int radix, bool signed)
    {
        string t = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (t.Length == 0)
        {
            _bytes = [];
            return null;
        }

        bool negative = false;
        if (signed && t.StartsWith('-'))
        {
            negative = true;
            t = t[1..];
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }

        if (radix == 16 && t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        if (t.Length == 0 || !t.All(c => DigitValue(c) < radix))
        {
            return new ConversionError(field, "InvalidNumber");
        }

        BigInteger magnitude = BigInteger.Zero;
        foreach (char c in t)
        {
            magnitude = magnitude * radix + DigitValue(c);
        }

        BigInteger value = negative ? -magnitude : magnitude;
        int size = SizeFor(value, signed);
        if (size == 0)
        {
            return new ConversionError(field, "OutOfRange");
        }

        ulong bits = (ulong)(value & ulong.MaxValue);
        _bytes = ArrangeBytes(bits, size);
        return null;
    }

    private static int DigitValue(char c) =>
        char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) - 'A' + 10 : 99;

    /// <summary>値が収まるバイト数。今のバイト数 (1〜8) に収まるならそのまま、収まらなければ 1 / 2 / 4 / 8 の最小。収まらなければ 0。</summary>
    private int SizeFor(BigInteger value, bool signed)
    {
        bool Fits(int size)
        {
            int bits = size * 8;
            BigInteger min = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
            BigInteger max = signed ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
            // 符号なしの欄に負の値は入れない。符号付きの欄でも、符号なしとして収まる大きな値 (例: 0xFF) は同じバイト数に収める。
            return value >= min && value <= max;
        }

        if (_bytes.Length is >= 1 and <= 8 && (Fits(_bytes.Length) || (signed && value >= 0 && value < (BigInteger.One << (_bytes.Length * 8)))))
        {
            return _bytes.Length;
        }

        foreach (int size in new[] { 1, 2, 4, 8 })
        {
            if (Fits(size))
            {
                return size;
            }
        }

        return 0;
    }

    private byte[] ArrangeBytes(ulong bits, int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < size; i++)
        {
            bytes[BigEndian ? size - 1 - i : i] = (byte)(bits >> (8 * i));
        }

        return bytes;
    }

    private ConversionError? SetFloat(ConverterField field, string text, int size)
    {
        string t = text.Trim();
        if (t.Length == 0)
        {
            _bytes = [];
            return null;
        }

        double d;
        string lower = t.ToLowerInvariant();
        if (lower is "nan")
        {
            d = double.NaN;
        }
        else if (lower is "inf" or "+inf" or "infinity" or "+infinity")
        {
            d = double.PositiveInfinity;
        }
        else if (lower is "-inf" or "-infinity")
        {
            d = double.NegativeInfinity;
        }
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            return new ConversionError(field, "InvalidNumber");
        }

        ulong bits = size switch
        {
            2 => BitConverter.HalfToUInt16Bits((Half)d),
            4 => BitConverter.SingleToUInt32Bits((float)d),
            _ => BitConverter.DoubleToUInt64Bits(d),
        };
        _bytes = ArrangeBytes(bits, size);
        return null;
    }

    private ConversionError? SetAscii(string text)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 3 < text.Length + 0 && text[i + 1] == 'x' && i + 3 < text.Length + 1
                && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte escaped))
            {
                bytes.Add(escaped);
                i += 3;
                continue;
            }

            if (c > 0x7F)
            {
                return new ConversionError(ConverterField.Ascii, "Unencodable", c.ToString());
            }

            bytes.Add((byte)c);
        }

        if (bytes.Count > MaxBytes)
        {
            return new ConversionError(ConverterField.Ascii, "TooLong", MaxBytes.ToString(CultureInfo.InvariantCulture));
        }

        _bytes = [.. bytes];
        return null;
    }

    private ConversionError? SetText(ConverterField field, string text, Encoding strict)
    {
        // 変換できない最初の文字を探して知らせる (赤字で示すため)。変換はしない。
        for (int i = 0; i < text.Length; i++)
        {
            int n = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            try
            {
                _ = strict.GetBytes(text.Substring(i, n));
            }
            catch (EncoderFallbackException)
            {
                return new ConversionError(field, "Unencodable", text.Substring(i, n));
            }

            i += n - 1;
        }

        byte[] bytes = strict.GetBytes(text);
        if (bytes.Length > MaxBytes)
        {
            return new ConversionError(field, "TooLong", MaxBytes.ToString(CultureInfo.InvariantCulture));
        }

        _bytes = bytes;
        return null;
    }
}
