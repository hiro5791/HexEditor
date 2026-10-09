using System.Globalization;
using System.Numerics;
using System.Text;

namespace HexEditor.Core.Inspector;

/// <summary>整数と浮動小数点の表示と、浮動小数点の入力の解釈 (INSP-02、INSP-03、INSP-05)。</summary>
public static class NumberFormat
{
    // ---- 整数 ----

    /// <summary>
    /// 整数の表示。<paramref name="bits"/> は 2 の補数の表現 (符号ありでも下位 <paramref name="size"/> バイトのビット列)。
    /// 16 進は <c>0x</c> 付きで型のサイズの桁数まで 0 で埋め、8 進は <c>0o</c> 付き (入力式と同じ書き方)。
    /// </summary>
    public static string Integer(BigInteger value, ulong bits, int size, IntegerBase radix, bool grouping, CultureInfo culture)
    {
        switch (radix)
        {
            case IntegerBase.Hexadecimal:
                return "0x" + bits.ToString("X" + (size * 2), CultureInfo.InvariantCulture);
            case IntegerBase.Octal:
                return "0o" + ToOctal(bits);
            default:
                return grouping ? value.ToString("N0", culture) : value.ToString(culture);
        }
    }

    /// <summary>128 bit の整数の表示 (INSP-04 の仕様 2。10 進では最大 39 桁)。<paramref name="bits"/> は 2 の補数の表現。</summary>
    public static string Integer128(BigInteger value, UInt128 bits, IntegerBase radix, bool grouping, CultureInfo culture)
    {
        switch (radix)
        {
            case IntegerBase.Hexadecimal:
                return "0x" + bits.ToString("X32", CultureInfo.InvariantCulture);
            case IntegerBase.Octal:
                if (bits == UInt128.Zero)
                {
                    return "0o0";
                }

                var text = new StringBuilder();
                for (UInt128 v = bits; v != UInt128.Zero; v >>= 3)
                {
                    text.Insert(0, (char)('0' + (int)(v & 7)));
                }

                return "0o" + text;
            default:
                return grouping ? value.ToString("N0", culture) : value.ToString(culture);
        }
    }

    internal static string ToOctal(ulong value)
    {
        if (value == 0)
        {
            return "0";
        }

        var text = new StringBuilder();
        while (value > 0)
        {
            text.Insert(0, (char)('0' + (int)(value & 7)));
            value >>= 3;
        }

        return text.ToString();
    }

    // ---- 浮動小数点 ----

    /// <summary>特殊な値・非正規化数の判定 (INSP-05 の仕様 2)。</summary>
    public static bool IsSubnormal(double value) => double.IsSubnormal(value);

    /// <summary>
    /// 浮動小数点の表示 (INSP-05 の仕様 2、INSP-02 の仕様 6)。<paramref name="isFloat"/> なら単精度。特殊な値は <c>+0</c>、<c>-0</c>、
    /// <c>∞</c>、<c>-∞</c>、<c>NaN (0x7FC00000)</c>。非正規化数の注記は呼び出し側で付ける。
    /// </summary>
    public static string Float(double value, ulong rawBits, bool isFloat, FloatFormat format, CultureInfo culture)
    {
        if (double.IsNaN(value))
        {
            return "NaN (0x" + rawBits.ToString(isFloat ? "X8" : "X16", CultureInfo.InvariantCulture) + ")";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "∞";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-∞";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0" : "+0";
        }

        return format switch
        {
            FloatFormat.HexFloat => HexFloat(rawBits, isFloat),
            FloatFormat.Exponent => Exponent(value, isFloat, culture),
            _ => isFloat ? ((float)value).ToString("R", culture) : value.ToString("R", culture),
        };
    }

    /// <summary>指数表記。有効数字は値を元に戻せる最短の桁数。</summary>
    private static string Exponent(double value, bool isFloat, CultureInfo culture)
    {
        string roundTrip = isFloat ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
        int digits = SignificantDigits(roundTrip);
        return isFloat
            ? ((float)value).ToString("E" + Math.Max(0, digits - 1), culture)
            : value.ToString("E" + Math.Max(0, digits - 1), culture);
    }

    private static int SignificantDigits(string invariant)
    {
        string mantissa = invariant.Split('E', 'e')[0].TrimStart('-', '+').Replace(".", string.Empty, StringComparison.Ordinal);
        mantissa = mantissa.TrimStart('0');
        mantissa = mantissa.TrimEnd('0');
        return Math.Max(1, mantissa.Length);
    }

    /// <summary>16 進浮動小数点 (<c>0x1.8p+1</c>)。非正規化数は <c>0x0.…p-126</c> の形。</summary>
    public static string HexFloat(ulong bits, bool isFloat) => HexFloatBits(bits, isFloat ? 23 : 52, isFloat ? 8 : 11);

    /// <summary>
    /// IEEE 754 の形式 (仮数 <paramref name="mantissaBits"/> bit、指数 <paramref name="exponentBits"/> bit) の 16 進浮動小数点
    /// (half、bfloat16 にも使う。INSP-06)。
    /// </summary>
    public static string HexFloatBits(ulong bits, int mantissaBits, int exponentBits)
    {
        int bias = (1 << (exponentBits - 1)) - 1;
        bool negative = (bits >> (mantissaBits + exponentBits) & 1) != 0;
        long exponent = (long)(bits >> mantissaBits) & ((1L << exponentBits) - 1);
        ulong mantissa = bits & ((1UL << mantissaBits) - 1);
        if (exponent == 0 && mantissa == 0)
        {
            return (negative ? "-" : string.Empty) + "0x0p+0";
        }

        // 仮数を 4 bit 単位にそろえる (float の 23 bit は 1 bit 左へずらして 24 bit = 6 桁にする)。
        int hexDigits = (mantissaBits + 3) / 4;
        ulong aligned = mantissa << (hexDigits * 4 - mantissaBits);
        string fraction = aligned.ToString("x" + hexDigits, CultureInfo.InvariantCulture).TrimEnd('0');
        string lead = exponent == 0 ? "0" : "1";
        long e = exponent == 0 ? 1 - bias : exponent - bias;
        return (negative ? "-" : string.Empty) + "0x" + lead + (fraction.Length > 0 ? "." + fraction : string.Empty)
            + "p" + (e >= 0 ? "+" : "-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 浮動小数点の入力を解釈する (INSP-05 の仕様 4)。小数点は <c>.</c> だけ。10 進・指数表記・<c>inf</c>・<c>-inf</c>・<c>nan</c>・
    /// 16 進浮動小数点を受け付け、最も近い値に偶数への丸めで丸める。解釈できなければ null。<paramref name="overflow"/> は
    /// 有限の入力が無限大に丸められた場合に true。
    /// </summary>
    public static double? ParseFloat(string input, bool isFloat, out bool overflow)
    {
        overflow = false;
        string text = input.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
        {
            return null;
        }

        string lower = text.ToLowerInvariant();
        switch (lower)
        {
            case "inf" or "+inf" or "infinity" or "+infinity" or "∞" or "+∞":
                return double.PositiveInfinity;
            case "-inf" or "-infinity" or "-∞":
                return double.NegativeInfinity;
            case "nan" or "+nan" or "-nan":
                return double.NaN;
        }

        bool negative = lower.StartsWith('-');
        string body = lower.TrimStart('+', '-');
        if (body.StartsWith("0x", StringComparison.Ordinal))
        {
            double? hex = ParseHexFloat(body[2..], isFloat);
            if (hex is null)
            {
                return null;
            }

            overflow = double.IsInfinity(hex.Value);
            return negative ? -hex.Value : hex.Value;
        }

        // 10 進: 符号・数字・小数点 1 つ・指数だけ。桁区切りや地域設定の小数点は受け付けない (00-overview 5.4)。
        if (!IsDecimalLiteral(body))
        {
            return null;
        }

        if (isFloat)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                return null;
            }

            overflow = float.IsInfinity(f);
            return f;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return null;
        }

        overflow = double.IsInfinity(d);
        return d;
    }

    internal static bool IsDecimalLiteral(string body)
    {
        int i = 0;
        int digits = 0;
        while (i < body.Length && char.IsAsciiDigit(body[i]))
        {
            i++;
            digits++;
        }

        if (i < body.Length && body[i] == '.')
        {
            i++;
            while (i < body.Length && char.IsAsciiDigit(body[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < body.Length && body[i] == 'e')
        {
            i++;
            if (i < body.Length && body[i] is '+' or '-')
            {
                i++;
            }

            int expDigits = 0;
            while (i < body.Length && char.IsAsciiDigit(body[i]))
            {
                i++;
                expDigits++;
            }

            if (expDigits == 0)
            {
                return false;
            }
        }

        return i == body.Length;
    }

    /// <summary>16 進浮動小数点の本体 (<c>1.8p+1</c>。<c>0x</c> と符号を除いたもの)。</summary>
    private static double? ParseHexFloat(string body, bool isFloat)
    {
        int p = body.IndexOf('p', StringComparison.Ordinal);
        string mantissaText = p >= 0 ? body[..p] : body;
        int exponent = 0;
        if (p >= 0 && !int.TryParse(body[(p + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return null;
        }

        int dot = mantissaText.IndexOf('.', StringComparison.Ordinal);
        string digits = dot >= 0 ? mantissaText.Remove(dot, 1) : mantissaText;
        int fractionDigits = dot >= 0 ? mantissaText.Length - dot - 1 : 0;
        if (digits.Length == 0 || !digits.All(char.IsAsciiHexDigit) || mantissaText.Count(c => c == '.') > 1)
        {
            return null;
        }

        BigInteger m = BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        long e2 = (long)exponent - 4L * fractionDigits;
        return isFloat ? BitConverter.Int32BitsToSingle((int)RoundToIeee(m, e2, 23, 8)) : BitConverter.Int64BitsToDouble((long)RoundToIeee(m, e2, 52, 11));
    }

    /// <summary>
    /// 正の値 m × 2^e2 を IEEE 754 (仮数 <paramref name="mantissaBits"/> bit、指数 <paramref name="exponentBits"/> bit) の
    /// ビット列に、偶数への丸めで丸める。大きすぎれば無限大。
    /// </summary>
    internal static ulong RoundToIeee(BigInteger m, long e2, int mantissaBits, int exponentBits)
    {
        if (m.IsZero)
        {
            return 0;
        }

        int bias = (1 << (exponentBits - 1)) - 1;
        long maxExponent = (1L << exponentBits) - 1;
        long length = (long)m.GetBitLength();

        // 値 = 1.xxx × 2^(length - 1 + e2)。
        long exponent = length - 1 + e2;
        long biased = exponent + bias;
        int shift;
        if (biased <= 0)
        {
            // 非正規化数: 仮数を 2^(1 - bias - mantissaBits) 単位にそろえる。
            shift = (int)(1 - bias - mantissaBits - e2);
            biased = 0;
        }
        else
        {
            shift = (int)(length - 1 - mantissaBits);
        }

        BigInteger q;
        if (shift > 0)
        {
            q = m >> shift;
            BigInteger remainder = m - (q << shift);
            BigInteger half = BigInteger.One << (shift - 1);
            if (remainder > half || remainder == half && !q.IsEven)
            {
                q += 1;
            }
        }
        else
        {
            q = m << -shift;
        }

        ulong significand = (ulong)q;
        if (biased == 0)
        {
            // 丸めで正規化数になった場合は、そのまま指数 1 になる (仮数の最上位ビットが指数の最下位ビットに入る)。
            return significand;
        }

        if (significand >> (mantissaBits + 1) != 0)
        {
            significand >>= 1;
            biased++;
        }

        if (biased >= maxExponent)
        {
            return (ulong)maxExponent << mantissaBits;
        }

        return ((ulong)biased << mantissaBits) | (significand & ((1UL << mantissaBits) - 1));
    }

    /// <summary>
    /// 丸めで値が変わったか (INSP-05 の仕様 4「格納される値」を出すか)。入力が 10 進なら 10 進の値どうしで、それ以外は double で比べる。
    /// </summary>
    public static bool RoundingChanged(string input, double stored, bool isFloat)
    {
        if (double.IsNaN(stored) || double.IsInfinity(stored))
        {
            return false;
        }

        string text = input.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        bool negative = text.StartsWith('-');
        string body = text.TrimStart('+', '-');
        if (!TryExact(body, out BigInteger num, out int pow10, out long pow2))
        {
            return false;
        }

        if (num.IsZero || stored == 0)
        {
            return !(num.IsZero && stored == 0);
        }

        if (negative != double.IsNegative(stored))
        {
            return true;
        }

        // 格納した値 = m × 2^e (正確な値)。
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(stored));
        int exponent = (int)((bits >> 52) & 0x7FF);
        long mantissa = bits & ((1L << 52) - 1);
        BigInteger m = exponent == 0 ? mantissa : mantissa | (1L << 52);
        long e = exponent == 0 ? -1074 : exponent - 1075;

        BigInteger left = num * (pow10 >= 0 ? BigInteger.Pow(10, pow10) : 1) * (pow2 >= 0 ? BigInteger.One << (int)pow2 : 1);
        BigInteger leftDen = (pow10 < 0 ? BigInteger.Pow(10, -pow10) : 1) * (pow2 < 0 ? BigInteger.One << (int)-pow2 : 1);
        BigInteger right = e >= 0 ? m << (int)e : m;
        BigInteger rightDen = e < 0 ? BigInteger.One << (int)-e : 1;
        return left * rightDen != right * leftDen;
    }

    /// <summary>入力の正確な値 = num × 10^pow10 × 2^pow2 (10 進は pow2 = 0、16 進浮動小数点は pow10 = 0)。</summary>
    internal static bool TryExact(string body, out BigInteger num, out int pow10, out long pow2)
    {
        num = 0;
        pow10 = 0;
        pow2 = 0;
        if (body.StartsWith("0x", StringComparison.Ordinal))
        {
            string hex = body[2..];
            int p = hex.IndexOf('p', StringComparison.Ordinal);
            string mantissaText = p >= 0 ? hex[..p] : hex;
            int exponent = 0;
            if (p >= 0 && !int.TryParse(hex[(p + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                return false;
            }

            int dot = mantissaText.IndexOf('.', StringComparison.Ordinal);
            string digits = dot >= 0 ? mantissaText.Remove(dot, 1) : mantissaText;
            if (digits.Length == 0 || !digits.All(char.IsAsciiHexDigit))
            {
                return false;
            }

            num = BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            pow2 = exponent - 4L * (dot >= 0 ? mantissaText.Length - dot - 1 : 0);
            return Math.Abs(pow2) < 20_000;
        }

        if (!IsDecimalLiteral(body))
        {
            return false;
        }

        int e = body.IndexOf('e', StringComparison.Ordinal);
        string mantissa = e >= 0 ? body[..e] : body;
        int exp10 = 0;
        if (e >= 0 && !int.TryParse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp10))
        {
            return false;
        }

        int point = mantissa.IndexOf('.', StringComparison.Ordinal);
        string all = point >= 0 ? mantissa.Remove(point, 1) : mantissa;
        num = BigInteger.Parse("0" + all, CultureInfo.InvariantCulture);
        pow10 = exp10 - (point >= 0 ? mantissa.Length - point - 1 : 0);
        return Math.Abs(pow10) < 5_000;
    }

    /// <summary>格納される値の表示 (float は 15 桁、double は 17 桁の有効数字)。</summary>
    public static string StoredValue(double stored, bool isFloat, CultureInfo culture) =>
        stored.ToString(isFloat ? "G15" : "G17", culture);
}
