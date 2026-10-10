using System.Globalization;
using System.Numerics;
using System.Text;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Inspector;

/// <summary>
/// フェーズ 2 の型の書き換え (INSP-04、INSP-06、INSP-07、INSP-10、INSP-12、INSP-14)。書き込みの規則は INSP-17 に従う。
/// </summary>
public static partial class InspectorEncoder
{
    private static InspectorEncodeResult? EncodeExtended(InspectorType type, string input, Endianness endian, InspectorOptions o,
        IExpressionContext? expressions, ReadOnlySpan<byte> current)
    {
        if (type.Fixed is { } format)
        {
            return FixedPoint(type, format, input, endian, o);
        }

        return type.Id switch
        {
            InspectorTypes.Int24 or InspectorTypes.Int48 => Integer(type, input, endian, signed: true, o, expressions),
            InspectorTypes.UInt24 or InspectorTypes.UInt48 => Integer(type, input, endian, signed: false, o, expressions),
            InspectorTypes.Int128 => Integer128(type, input, endian, signed: true, o, expressions),
            InspectorTypes.UInt128 => Integer128(type, input, endian, signed: false, o, expressions),
            InspectorTypes.Half => HalfValue(input, endian, o),
            InspectorTypes.BFloat16 => BFloat16Value(input, endian, o),
            InspectorTypes.Float80 => Float80Value(input, endian, o),
            InspectorTypes.Real48 => Real48Value(input, o),
            InspectorTypes.ULeb128 => Leb128(input, signed: false, o, expressions, current),
            InspectorTypes.SLeb128 => Leb128(input, signed: true, o, expressions, current),
            InspectorTypes.SqliteVarint => SqliteVarint(input, o, expressions, current),
            InspectorTypes.Rgba8 or InspectorTypes.Bgra8 or InspectorTypes.Rgb8 or InspectorTypes.Rgb565 => Color(type, input, endian, o),
            InspectorTypes.UnixMs or InspectorTypes.HfsPlus or InspectorTypes.Apfs or InspectorTypes.Cocoa => ZonedDateTime(type, input, endian, o),
            InspectorTypes.OleDate or InspectorTypes.SqlDateTime or InspectorTypes.SqlSmallDateTime => PlainDateTime(type, input, endian, o),
            InspectorTypes.DotNetDateTime => DotNetDateTime(input, endian, o, current),
            InspectorTypes.DigitsDateTime => DigitsDateTime(input, o, current),
            _ when InspectorTypes.IsString(type.Id) => StringValue(type, input, endian, o, current),
            _ => null,
        };
    }

    /// <summary>整数の入力: 数だけなら多倍長で、それ以外は入力式 (64 bit) で読む。読めなければ null。</summary>
    private static BigInteger? ParseInteger(string input, IExpressionContext? expressions)
    {
        if (ParseLiteral(input) is { } literal)
        {
            return literal;
        }

        return ExpressionEvaluator.TryEvaluate(input, expressions ?? EmptyContext.Instance, out long evaluated, out _, DefaultRadix.Decimal)
            ? evaluated
            : null;
    }

    // ---- 整数 128 bit (INSP-04 の仕様 3) ----

    private static InspectorEncodeResult Integer128(InspectorType type, string input, Endianness endian, bool signed, InspectorOptions o,
        IExpressionContext? expressions)
    {
        BigInteger min = signed ? -(BigInteger.One << 127) : BigInteger.Zero;
        BigInteger max = signed ? (BigInteger.One << 127) - 1 : (BigInteger.One << 128) - 1;
        if (ParseInteger(input, expressions) is not { } value)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorInteger);
        }

        if (value < min || value > max)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorIntegerRange, type.Id, min.ToString("N0", o.Culture), max.ToString("N0", o.Culture)));
        }

        var bits = (UInt128)(value & ((BigInteger.One << 128) - 1));
        return new InspectorEncodeResult(InspectorDecoder.Write128(bits, endian), null);
    }

    // ---- 浮動小数点 (INSP-06 の仕様 5) ----

    /// <summary>入力を double で読む (INSP-05 の仕様 4 の書式)。読めなければ誤り。</summary>
    private static InspectorEncodeResult? ParseDouble(string input, string typeName, InspectorOptions o, out double value)
    {
        value = 0;
        if (NumberFormat.ParseFloat(input, isFloat: false, out bool overflow) is not double parsed)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorFloat);
        }

        if (overflow)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, typeName));
        }

        value = parsed;
        return null;
    }

    private static string? Stored(string input, double stored, InspectorOptions o) =>
        NumberFormat.RoundingChanged(input, stored, isFloat: false) ? stored.ToString("R", o.Culture) : null;

    private static InspectorEncodeResult HalfValue(string input, Endianness endian, InspectorOptions o)
    {
        if (ParseDouble(input, InspectorTypes.Half, o, out double value) is { } error)
        {
            return error;
        }

        Half h = double.IsNaN(value) ? BitConverter.UInt16BitsToHalf(0x7E00) : (Half)value;
        if (double.IsFinite(value) && Half.IsInfinity(h))
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, InspectorTypes.Half));
        }

        return new InspectorEncodeResult(InspectorDecoder.WriteBits(BitConverter.HalfToUInt16Bits(h), 2, endian), null, Stored(input, (double)h, o));
    }

    private static InspectorEncodeResult BFloat16Value(string input, Endianness endian, InspectorOptions o)
    {
        if (ParseDouble(input, InspectorTypes.BFloat16, o, out double value) is { } error)
        {
            return error;
        }

        ushort bits = double.IsNaN(value) ? (ushort)0x7FC0 : SmallFloats.ToBFloat16((float)value);
        float stored = SmallFloats.FromBFloat16(bits);
        if (double.IsFinite(value) && float.IsInfinity(stored))
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, InspectorTypes.BFloat16));
        }

        return new InspectorEncodeResult(InspectorDecoder.WriteBits(bits, 2, endian), null, Stored(input, stored, o));
    }

    private static InspectorEncodeResult Real48Value(string input, InspectorOptions o)
    {
        if (ParseDouble(input, InspectorTypes.Real48, o, out double value) is { } error)
        {
            return error;
        }

        if (!double.IsFinite(value) || SmallFloats.ToReal48(value) is not { } bytes)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, InspectorTypes.Real48));
        }

        return new InspectorEncodeResult(bytes, null, Stored(input, SmallFloats.FromReal48(bytes), o));
    }

    /// <summary>入力の正確な値 (符号と、正の有理数 num / den)。10 進・指数表記・16 進浮動小数点。読めなければ false。</summary>
    internal static bool TryRational(string input, out bool negative, out BigInteger num, out BigInteger den)
    {
        string text = input.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        negative = text.StartsWith('-');
        string body = text.TrimStart('+', '-');
        num = BigInteger.Zero;
        den = BigInteger.One;
        if (!NumberFormat.TryExact(body, out BigInteger n, out int pow10, out long pow2))
        {
            return false;
        }

        num = n;
        if (pow10 >= 0)
        {
            num *= BigInteger.Pow(10, pow10);
        }
        else
        {
            den *= BigInteger.Pow(10, -pow10);
        }

        if (pow2 >= 0)
        {
            num <<= (int)pow2;
        }
        else
        {
            den <<= (int)-pow2;
        }

        return true;
    }

    private static InspectorEncodeResult Float80Value(string input, Endianness endian, InspectorOptions o)
    {
        string lower = input.Trim().ToLowerInvariant();
        Float80 value;
        string? stored = null;
        switch (lower)
        {
            case "inf" or "+inf" or "infinity" or "+infinity" or "∞" or "+∞":
                value = Float80.Infinity(false);
                break;
            case "-inf" or "-infinity" or "-∞":
                value = Float80.Infinity(true);
                break;
            case "nan" or "+nan" or "-nan":
                value = Float80.QuietNaN;
                break;
            default:
                if (!TryRational(input, out bool negative, out BigInteger num, out BigInteger den))
                {
                    return InspectorEncodeResult.Fail(o.Text.ErrorFloat);
                }

                value = Float80.Round(num, den, negative);
                if (value.Kind == Float80Kind.Infinity)
                {
                    return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorFloatOverflow, InspectorTypes.Float80));
                }

                (BigInteger sn, BigInteger sd) = value.Rational();
                if (sn * den != num * sd)
                {
                    stored = InspectorDecoder.Float80Text(value, o with { FloatFormat = FloatFormat.Shortest });
                }

                break;
        }

        return new InspectorEncodeResult(value.ToBytes(endian), null, stored);
    }

    /// <summary>固定小数点 (INSP-06 の仕様 5): 最も近い値に丸め (偶数への丸め)、範囲外は誤り。</summary>
    private static InspectorEncodeResult FixedPoint(InspectorType type, FixedPointFormat format, string input, Endianness endian, InspectorOptions o)
    {
        if (!TryRational(input, out bool negative, out BigInteger num, out BigInteger den))
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorFloat);
        }

        BigInteger raw = Float80.RoundDiv(num << format.FractionBits, den);
        if (negative)
        {
            raw = -raw;
        }

        BigInteger min = format.Signed ? -(BigInteger.One << (format.TotalBits - 1)) : BigInteger.Zero;
        BigInteger max = format.Signed ? (BigInteger.One << (format.TotalBits - 1)) - 1 : (BigInteger.One << format.TotalBits) - 1;
        if (raw < min || raw > max)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorIntegerRange, type.Id,
                InspectorDecoder.FixedText(min, format.FractionBits, o), InspectorDecoder.FixedText(max, format.FractionBits, o)));
        }

        bool exact = (raw.Sign < 0 ? -raw : raw) * den == num << format.FractionBits;
        string? stored = exact ? null : InspectorDecoder.FixedText(raw, format.FractionBits, o);
        ulong bits = (ulong)(raw & ((BigInteger.One << format.TotalBits) - 1));
        return new InspectorEncodeResult(InspectorDecoder.WriteBits(bits, format.TotalBits / 8, endian), null, stored);
    }

    // ---- 可変長整数 (INSP-07 の仕様 5) ----

    /// <summary>LEB128 で <paramref name="length"/> バイトに符号化する (短い値は継続ビット付きの冗長な符号化で長さを合わせる)。</summary>
    public static byte[] EncodeLeb128(BigInteger value, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int part = (int)(value & 0x7F);
            value >>= 7;
            bytes[i] = (byte)(i < length - 1 ? part | 0x80 : part);
        }

        return bytes;
    }

    /// <summary>LEB128 の最短のバイト数。</summary>
    public static int Leb128Length(BigInteger value, bool signed)
    {
        int n = 1;
        while (true)
        {
            BigInteger rest = value >> (7 * n);
            if (signed)
            {
                // 残りが符号だけになり、最後のバイトの符号ビット (0x40) が値の符号と一致すれば終わり。
                bool sign = ((value >> (7 * n - 1)) & 1) != 0;
                if (rest.IsZero && !sign || rest == BigInteger.MinusOne && sign)
                {
                    return n;
                }
            }
            else if (rest.IsZero)
            {
                return n;
            }

            n++;
        }
    }

    private static InspectorEncodeResult Leb128(string input, bool signed, InspectorOptions o, IExpressionContext? expressions, ReadOnlySpan<byte> current)
    {
        if (ParseInteger(input, expressions) is not { } value)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorInteger);
        }

        BigInteger min = signed ? long.MinValue : BigInteger.Zero;
        BigInteger max = signed ? long.MaxValue : ulong.MaxValue;
        string name = signed ? InspectorTypes.SLeb128 : InspectorTypes.ULeb128;
        if (value < min || value > max)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorIntegerRange, name, min.ToString("N0", o.Culture), max.ToString("N0", o.Culture)));
        }

        int needed = Leb128Length(value, signed);
        (_, int original, bool overflow, bool incomplete) = InspectorDecoder.ReadLeb128(current, signed);
        int length = overflow ? InspectorDecoder.MaxLeb128Bytes : incomplete || current.IsEmpty ? needed : original;
        if (needed > length)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorVarIntLength, length, needed));
        }

        return new InspectorEncodeResult(EncodeLeb128(value, length), null);
    }

    /// <summary>SQLite varint の最短の符号化 (sqlite3PutVarint と同じ)。</summary>
    public static byte[] EncodeSqliteVarint(ulong v)
    {
        if ((v & 0xFF00_0000_0000_0000UL) != 0)
        {
            byte[] nine = new byte[9];
            nine[8] = (byte)v;
            v >>= 8;
            for (int i = 7; i >= 0; i--)
            {
                nine[i] = (byte)((v & 0x7F) | 0x80);
                v >>= 7;
            }

            return nine;
        }

        var reversed = new List<byte>();
        do
        {
            reversed.Add((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        while (v != 0);
        reversed[0] &= 0x7F;
        reversed.Reverse();
        return [.. reversed];
    }

    private static InspectorEncodeResult SqliteVarint(string input, InspectorOptions o, IExpressionContext? expressions, ReadOnlySpan<byte> current)
    {
        if (ParseInteger(input, expressions) is not { } value)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorInteger);
        }

        if (value < long.MinValue || value > ulong.MaxValue)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorIntegerRange, InspectorTypes.SqliteVarint,
                long.MinValue.ToString("N0", o.Culture), ulong.MaxValue.ToString("N0", o.Culture)));
        }

        byte[] bytes = EncodeSqliteVarint((ulong)(value & ulong.MaxValue));
        (_, int original, bool incomplete) = InspectorDecoder.ReadSqliteVarint(current);
        if (!current.IsEmpty && !incomplete && bytes.Length != original)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorVarIntLength, original, bytes.Length));
        }

        return new InspectorEncodeResult(bytes, null);
    }

    // ---- 文字列 (INSP-10 の仕様 5) ----

    /// <summary>
    /// 文字列の入力: 引用符で囲めば表示と同じエスケープ (<c>\n</c>、<c>\t</c>、<c>\0</c>、<c>\xNN</c>、<c>\"</c>、<c>\\</c>) を使える。
    /// 囲まなければそのまま。不正なエスケープは null。
    /// </summary>
    public static string? UnquoteString(string input)
    {
        if (input.Length < 2 || input[0] != '"' || input[^1] != '"')
        {
            return input;
        }

        var text = new StringBuilder();
        string body = input[1..^1];
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c != '\\')
            {
                text.Append(c);
                continue;
            }

            if (++i >= body.Length)
            {
                return null;
            }

            switch (body[i])
            {
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case '0':
                    text.Append('\0');
                    break;
                case '"' or '\\':
                    text.Append(body[i]);
                    break;
                case 'x' when i + 2 < body.Length
                    && int.TryParse(body.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code):
                    text.Append((char)code);
                    i += 2;
                    break;
                default:
                    return null;
            }
        }

        return text.ToString();
    }

    private static InspectorEncodeResult StringValue(InspectorType type, string input, Endianness endian, InspectorOptions o, ReadOnlySpan<byte> current)
    {
        if (UnquoteString(input) is not { } text)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorEscape);
        }

        bool ansi = type.Id.EndsWith("Ansi", StringComparison.Ordinal);
        bool utf16 = type.Id == InspectorTypes.CStringUtf16;
        Encoding encoding = ansi ? o.AnsiEncoding ?? InspectorDecoder.DefaultAnsi : utf16 ? new UnicodeEncoding(endian == Endianness.Big, false) : new UTF8Encoding(false);
        byte[] body;
        try
        {
            Encoding strict = ansi ? Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) : encoding;
            body = strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorNotEncodable, encoding.WebName));
        }

        // 元の長さ (終端・長さの部分を含む)。
        InspectorValue original = InspectorDecoder.Decode(type, current, [], endian, o);
        if (original.Status == InspectorStatus.Invalid)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorNotTerminated);
        }

        int limit = original.Status == InspectorStatus.Ok ? original.ByteCount : int.MaxValue;
        byte[] encoded;
        if (type.Id.StartsWith("cstr", StringComparison.Ordinal))
        {
            encoded = [.. body, .. new byte[utf16 ? 2 : 1]];
        }
        else
        {
            int prefix = type.Size;
            ulong maxLength = prefix == 4 ? uint.MaxValue : (1UL << (prefix * 8)) - 1;
            if ((ulong)body.Length > maxLength)
            {
                return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorStringTooLong, limit));
            }

            encoded = [.. InspectorDecoder.WriteBits((ulong)body.Length, prefix, endian), .. body];
        }

        if (encoded.Length > limit)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorStringTooLong, limit));
        }

        // 元より短ければ残りを 00 で埋める (INSP-10 の仕様 5)。
        if (limit != int.MaxValue && encoded.Length < limit)
        {
            Array.Resize(ref encoded, limit);
        }

        return new InspectorEncodeResult(encoded, null);
    }

    // ---- 色 (INSP-12 の仕様 4) ----

    /// <summary><c>#RRGGBB</c> か <c>#RRGGBBAA</c> (<c>#</c> は省略可) を読む。読めなければ null。</summary>
    public static (byte R, byte G, byte B, byte A)? ParseColor(string input)
    {
        string text = input.Trim().TrimStart('#');
        if (text.Length is not (6 or 8) || !text.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        byte Part(int i) => byte.Parse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (Part(0), Part(2), Part(4), text.Length == 8 ? Part(6) : (byte)255);
    }

    /// <summary>8 bit の成分を n bit に丸める (最も近い値)。</summary>
    private static int Narrow(byte value, int bits)
    {
        int max = (1 << bits) - 1;
        return (int)Math.Round(value * max / 255.0, MidpointRounding.ToEven);
    }

    private static InspectorEncodeResult Color(InspectorType type, string input, Endianness endian, InspectorOptions o)
    {
        if (ParseColor(input) is not { } c)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorColor);
        }

        switch (type.Id)
        {
            case InspectorTypes.Rgba8:
                return new InspectorEncodeResult([c.R, c.G, c.B, c.A], null);
            case InspectorTypes.Bgra8:
                return new InspectorEncodeResult([c.B, c.G, c.R, c.A], null);
            case InspectorTypes.Rgb8:
                return new InspectorEncodeResult([c.R, c.G, c.B], null);
            default:
            {
                // RGB565 は最も近い値に丸め、丸めた結果を「格納される値」に示す。
                ushort v = (ushort)((Narrow(c.R, 5) << 11) | (Narrow(c.G, 6) << 5) | Narrow(c.B, 5));
                (byte r, byte g, byte b) = InspectorDecoder.ExpandRgb565(v);
                string? stored = (r, g, b, (byte)255) != (c.R, c.G, c.B, c.A) ? InspectorDecoder.ColorText(r, g, b, 255, o) : null;
                return new InspectorEncodeResult(InspectorDecoder.WriteBits(v, 2, endian), null, stored);
            }
        }
    }

    // ---- 日時 (INSP-14 の仕様 4) ----

    private static InspectorEncodeResult DateRange(InspectorType type, DateTime min, DateTime max, InspectorOptions o) =>
        InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorDateRange, type.Id,
            min.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), max.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

    /// <summary>a / b の床 (負の値は小さい方へ)。</summary>
    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : (a - b + 1) / b;

    /// <summary>タイムゾーンが UTC の形式 (Unix ミリ秒、HFS+、APFS、Mac 絶対時刻)。</summary>
    private static InspectorEncodeResult ZonedDateTime(InspectorType type, string input, Endianness endian, InspectorOptions o)
    {
        if (InspectorDateTime.Parse(input, hasZone: true, o) is not { } p)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorDate);
        }

        DateTime value = p.Value;
        switch (type.Id)
        {
            case InspectorTypes.UnixMs:
            {
                long ms = FloorDiv(value.Ticks - InspectorDateTime.UnixEpoch.Ticks, TimeSpan.TicksPerMillisecond);
                DateTime kept = InspectorDateTime.UnixEpoch.AddTicks(ms * TimeSpan.TicksPerMillisecond);
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)ms, 8, endian), null,
                    kept.Ticks != value.Ticks ? InspectorDateTime.FormatInstant(kept, 3, o) : null);
            }

            case InspectorTypes.HfsPlus:
            {
                long seconds = FloorDiv(value.Ticks - InspectorDecoder.HfsEpoch.Ticks, TimeSpan.TicksPerSecond);
                if (seconds < 0 || seconds > uint.MaxValue)
                {
                    return DateRange(type, InspectorDecoder.HfsEpoch, InspectorDecoder.HfsEpoch.AddSeconds(uint.MaxValue), o);
                }

                DateTime kept = InspectorDecoder.HfsEpoch.AddSeconds(seconds);
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)seconds, 4, endian), null,
                    kept.Ticks != value.Ticks ? InspectorDateTime.FormatInstant(kept, 0, o) : null);
            }

            case InspectorTypes.Apfs:
            {
                long ticks = value.Ticks - InspectorDateTime.UnixEpoch.Ticks;
                if (ticks < 0)
                {
                    return DateRange(type, InspectorDateTime.UnixEpoch, DateTime.MaxValue, o);
                }

                string? stored = p.FractionDigits > 7 ? InspectorDateTime.FormatInstant(value, 7, o) : null;
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)ticks * 100, 8, endian), null, stored);
            }

            default:
            {
                double seconds = (value.Ticks - InspectorDecoder.CocoaEpoch.Ticks) / (double)TimeSpan.TicksPerSecond;
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)BitConverter.DoubleToInt64Bits(seconds), 8, endian), null);
            }
        }
    }

    /// <summary>タイムゾーンのない形式 (OLE オートメーション日付、SQL Server datetime / smalldatetime)。</summary>
    private static InspectorEncodeResult PlainDateTime(InspectorType type, string input, Endianness endian, InspectorOptions o)
    {
        if (InspectorDateTime.Parse(input, hasZone: false, o) is not { } p)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorDate);
        }

        DateTime value = p.Value;
        switch (type.Id)
        {
            case InspectorTypes.OleDate:
            {
                if (value.Year < 100)
                {
                    return DateRange(type, new DateTime(100, 1, 1), DateTime.MaxValue, o);
                }

                double days = value.ToOADate();
                DateTime kept = DateTime.FromOADate(days);
                return new InspectorEncodeResult(InspectorDecoder.WriteBits((ulong)BitConverter.DoubleToInt64Bits(days), 8, endian), null,
                    kept.Ticks != value.Ticks ? InspectorDateTime.FormatNoZone(kept, 3, o) : null);
            }

            case InspectorTypes.SqlDateTime:
            {
                long dayTicks = value.Ticks - InspectorDecoder.SqlEpoch.Ticks;
                long days = FloorDiv(dayTicks, TimeSpan.TicksPerDay);
                long rest = dayTicks - days * TimeSpan.TicksPerDay;

                // 1/300 秒単位に丸める (SQL Server と同じく最も近い値)。日をまたいだら翌日の 0 時。
                long time = (long)Math.Round(rest * 300.0 / TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
                if (time >= InspectorDecoder.SqlTicksPerDay)
                {
                    time = 0;
                    days++;
                }

                DateTime kept = InspectorDecoder.SqlEpoch.AddTicks(days * TimeSpan.TicksPerDay + time * TimeSpan.TicksPerSecond / 300);
                byte[] bytes = [.. InspectorDecoder.WriteBits((ulong)days, 4, endian), .. InspectorDecoder.WriteBits((ulong)time, 4, endian)];
                return new InspectorEncodeResult(bytes, null, kept.Ticks != value.Ticks ? InspectorDateTime.FormatNoZone(kept, 3, o) : null);
            }

            default:
            {
                long ticks = value.Ticks - InspectorDecoder.SqlEpoch.Ticks;
                long days = FloorDiv(ticks, TimeSpan.TicksPerDay);
                if (days < 0 || days > ushort.MaxValue)
                {
                    return DateRange(type, InspectorDecoder.SqlEpoch, InspectorDecoder.SqlEpoch.AddDays(ushort.MaxValue + 1).AddMinutes(-1), o);
                }

                long minutes = (ticks - days * TimeSpan.TicksPerDay) / TimeSpan.TicksPerMinute;
                DateTime kept = InspectorDecoder.SqlEpoch.AddDays(days).AddMinutes(minutes);
                byte[] bytes = [.. InspectorDecoder.WriteBits((ulong)days, 2, endian), .. InspectorDecoder.WriteBits((ulong)minutes, 2, endian)];
                return new InspectorEncodeResult(bytes, null, kept.Ticks != value.Ticks ? InspectorDateTime.FormatNoZone(kept, 0, o) : null);
            }
        }
    }

    /// <summary>.NET の DateTime: 元の種類 (UTC / ローカル / 指定なし) を保つ。UTC の値はタイムゾーンを考えて入力する。</summary>
    private static InspectorEncodeResult DotNetDateTime(string input, Endianness endian, InspectorOptions o, ReadOnlySpan<byte> current)
    {
        ulong kindBits = current.Length >= 8 ? InspectorDecoder.ReadBits(current[..8], endian) & 0xC000_0000_0000_0000UL : 0;
        bool utc = kindBits == 0x4000_0000_0000_0000UL;
        if (InspectorDateTime.Parse(input, hasZone: utc, o) is not { } p)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorDate);
        }

        string? stored = p.FractionDigits > 7
            ? utc ? InspectorDateTime.FormatInstant(p.Value, 7, o) : InspectorDateTime.FormatNoZone(p.Value, 7, o)
            : null;
        return new InspectorEncodeResult(InspectorDecoder.WriteBits(kindBits | (ulong)p.Value.Ticks, 8, endian), null, stored);
    }

    /// <summary>10 進の数字の日時: 元と同じ形式・桁数で書き込む (INSP-14 の仕様 4)。</summary>
    private static InspectorEncodeResult DigitsDateTime(string input, InspectorOptions o, ReadOnlySpan<byte> current)
    {
        int digits = InspectorDecoder.CountDigits(current);
        InspectorDecoder.DigitsFormat format;
        if (InspectorDecoder.DetectDigits(current) is { } found)
        {
            format = found.Format;
        }
        else if (digits >= 14)
        {
            format = InspectorDecoder.DigitsFormat.YyyyMmDdHhMmSs;
        }
        else
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.DigitsNeeded, digits));
        }

        bool zoned = format is InspectorDecoder.DigitsFormat.UnixSeconds or InspectorDecoder.DigitsFormat.UnixMilliseconds;
        if (InspectorDateTime.Parse(input, zoned, o) is not { } p)
        {
            return InspectorEncodeResult.Fail(o.Text.ErrorDate);
        }

        DateTime v = p.Value;
        string text = format switch
        {
            InspectorDecoder.DigitsFormat.UnixSeconds =>
                FloorDiv(v.Ticks - InspectorDateTime.UnixEpoch.Ticks, TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture),
            InspectorDecoder.DigitsFormat.UnixMilliseconds =>
                FloorDiv(v.Ticks - InspectorDateTime.UnixEpoch.Ticks, TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture),
            InspectorDecoder.DigitsFormat.YyyyMmDd => v.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            InspectorDecoder.DigitsFormat.YyMmDdHhMmSs when v.Year is >= 1950 and <= 2049 => v.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture),
            InspectorDecoder.DigitsFormat.YyMmDdHhMmSs => string.Empty,
            _ => v.Year <= 9999 ? v.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : string.Empty,
        };
        if (text.Length != (int)format)
        {
            return InspectorEncodeResult.Fail(string.Format(o.Culture, o.Text.ErrorDigits, (int)format, text.Length));
        }

        return new InspectorEncodeResult(Encoding.ASCII.GetBytes(text), null);
    }
}
