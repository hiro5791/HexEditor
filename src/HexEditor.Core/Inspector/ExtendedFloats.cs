using System.Globalization;
using System.Numerics;
using System.Text;

namespace HexEditor.Core.Inspector;

/// <summary>80 bit の拡張倍精度の値の種類 (INSP-06 の仕様 2)。</summary>
public enum Float80Kind
{
    Zero,
    Normal,

    /// <summary>非正規化数 (指数 0)。</summary>
    Denormal,
    Infinity,
    NaN,

    /// <summary>整数ビットが 0 なのに指数が 0 でない値 (非正規の表現)。</summary>
    Unnormal,
}

/// <summary>
/// x87 の 80 bit 拡張倍精度 (INSP-06): 符号 1 bit、指数 15 bit (バイアス 16383)、整数ビットを明示した仮数 64 bit。
/// 値は <see cref="Mantissa"/> × 2^(指数 − 16383 − 63) (指数 0 は 1 として扱う)。
/// </summary>
public readonly record struct Float80(bool Negative, int Exponent, ulong Mantissa)
{
    public const int Bias = 16383;
    public const int MaxExponent = 0x7FFF;

    /// <summary>リトルエンディアンの並びの 10 バイト (仮数 8 バイト、符号と指数 2 バイト)。</summary>
    public static Float80 FromBytes(ReadOnlySpan<byte> bytes, Endianness endian)
    {
        Span<byte> le = stackalloc byte[10];
        bytes[..10].CopyTo(le);
        if (endian == Endianness.Big)
        {
            le.Reverse();
        }

        ulong mantissa = 0;
        for (int i = 7; i >= 0; i--)
        {
            mantissa = (mantissa << 8) | le[i];
        }

        int top = le[8] | (le[9] << 8);
        return new Float80((top & 0x8000) != 0, top & 0x7FFF, mantissa);
    }

    public byte[] ToBytes(Endianness endian)
    {
        byte[] bytes = new byte[10];
        for (int i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(Mantissa >> (8 * i));
        }

        int top = (Negative ? 0x8000 : 0) | (Exponent & 0x7FFF);
        bytes[8] = (byte)top;
        bytes[9] = (byte)(top >> 8);
        if (endian == Endianness.Big)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private const ulong IntegerBit = 1UL << 63;

    public Float80Kind Kind
    {
        get
        {
            if (Exponent == MaxExponent)
            {
                // 整数ビットが 0 の無限大・NaN (疑似無限大・疑似 NaN) は、x87 では不正な値。
                if ((Mantissa & IntegerBit) == 0)
                {
                    return Float80Kind.Unnormal;
                }

                return (Mantissa & ~IntegerBit) == 0 ? Float80Kind.Infinity : Float80Kind.NaN;
            }

            if (Exponent == 0)
            {
                return Mantissa == 0 ? Float80Kind.Zero : Float80Kind.Denormal;
            }

            return (Mantissa & IntegerBit) == 0 ? Float80Kind.Unnormal : Float80Kind.Normal;
        }
    }

    /// <summary>値の 2 の指数 (値 = <see cref="Mantissa"/> × 2^これ)。</summary>
    public int BinaryExponent => (Exponent == 0 ? 1 : Exponent) - Bias - 63;

    public static Float80 Infinity(bool negative) => new(negative, MaxExponent, IntegerBit);

    /// <summary>正の静かな NaN (<c>7FFF C000000000000000</c>)。</summary>
    public static Float80 QuietNaN => new(false, MaxExponent, 0xC000000000000000UL);

    /// <summary>
    /// 正の有理数 num / den を最も近い値に丸める (偶数への丸め)。大きすぎれば無限大、小さすぎれば非正規化数か 0。
    /// </summary>
    public static Float80 Round(BigInteger num, BigInteger den, bool negative)
    {
        if (num.IsZero)
        {
            return new Float80(negative, 0, 0);
        }

        // 2^e ≤ num / den < 2^(e+1) となる e。
        long e = (long)num.GetBitLength() - (long)den.GetBitLength();
        if (Compare(num, den, e) < 0)
        {
            e--;
        }

        long biased = e + Bias;

        // q = round(値 × 2^scale)。正規化数は q が [2^63, 2^64)、非正規化数は値 = q × 2^(1 − 16383 − 63)。
        long scale = biased <= 0 ? Bias - 1 + 63 : 63 - e;
        BigInteger q = RoundDiv(scale >= 0 ? num << (int)scale : num, scale >= 0 ? den : den << (int)-scale);
        if (biased <= 0)
        {
            // 丸めで最小の正規化数に届いたら、指数 1 になる (整数ビットが立つ)。
            return q >= (BigInteger.One << 63) ? new Float80(negative, 1, (ulong)q) : new Float80(negative, 0, (ulong)q);
        }

        if (q >= (BigInteger.One << 64))
        {
            q >>= 1;
            biased++;
        }

        return biased >= MaxExponent ? Infinity(negative) : new Float80(negative, (int)biased, (ulong)q);
    }

    /// <summary>num / den と 2^e を比べる。</summary>
    private static int Compare(BigInteger num, BigInteger den, long e) =>
        e >= 0 ? num.CompareTo(den << (int)e) : (num << (int)-e).CompareTo(den);

    /// <summary>num / den を整数に丸める (偶数への丸め)。</summary>
    internal static BigInteger RoundDiv(BigInteger num, BigInteger den)
    {
        BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
        int c = (r * 2).CompareTo(den);
        return c > 0 || c == 0 && !q.IsEven ? q + 1 : q;
    }

    /// <summary>値の絶対値を有理数で返す (num / den)。有限の値だけ。</summary>
    public (BigInteger Num, BigInteger Den) Rational()
    {
        int e = BinaryExponent;
        BigInteger m = Mantissa;
        return e >= 0 ? (m << e, BigInteger.One) : (m, BigInteger.One << -e);
    }
}

/// <summary>
/// 有理数の 10 進表記 (INSP-06 の仕様 2): 指定の形式に丸めたときに同じ値に戻る、桁数が最も少ない表記を <see cref="BigInteger"/> で
/// 正確に求める。
/// </summary>
public static class DecimalText
{
    /// <summary>
    /// 正の有理数 num / den を、有効数字 <paramref name="digits"/> 桁に丸めた値 (q × 10^exp10。q は digits 桁、繰り上がりで digits + 1 桁)。
    /// </summary>
    public static (BigInteger Q, int Exp10) RoundToDigits(BigInteger num, BigInteger den, int digits)
    {
        // 10^k ≤ 値 < 10^(k+1) となる k を、2 進の桁数からの見積もりを直して求める。
        int k = (int)Math.Floor(((double)num.GetBitLength() - (double)den.GetBitLength()) * 0.30102999566398120);
        while (Pow10Compare(num, den, k) < 0)
        {
            k--;
        }

        while (Pow10Compare(num, den, k + 1) >= 0)
        {
            k++;
        }

        int exp10 = k - digits + 1;
        BigInteger q = exp10 >= 0
            ? Float80.RoundDiv(num, den * BigInteger.Pow(10, exp10))
            : Float80.RoundDiv(num * BigInteger.Pow(10, -exp10), den);
        return (q, exp10);
    }

    /// <summary>num / den と 10^k を比べる。</summary>
    private static int Pow10Compare(BigInteger num, BigInteger den, int k) =>
        k >= 0 ? num.CompareTo(den * BigInteger.Pow(10, k)) : (num * BigInteger.Pow(10, -k)).CompareTo(den);

    /// <summary>
    /// 最短の表記: 有効数字 1〜<paramref name="maxDigits"/> 桁で丸めた値のうち、<paramref name="roundTrips"/> (その値を形式に丸めると
    /// 元の値に戻るか) を満たす最初のもの。
    /// </summary>
    public static (BigInteger Q, int Exp10) Shortest(BigInteger num, BigInteger den, int maxDigits, Func<BigInteger, BigInteger, bool> roundTrips)
    {
        (BigInteger Q, int Exp10) last = default;
        for (int p = 1; p <= maxDigits; p++)
        {
            last = RoundToDigits(num, den, p);
            (BigInteger cn, BigInteger cd) = last.Exp10 >= 0 ? (last.Q * BigInteger.Pow(10, last.Exp10), BigInteger.One) : (last.Q, BigInteger.Pow(10, -last.Exp10));
            if (roundTrips(cn, cd))
            {
                return last;
            }
        }

        return last;
    }

    /// <summary>
    /// q × 10^exp10 の表記。.NET の "R" と同じく、10 進の指数が −5 未満か 15 以上なら指数表記 (<c>1.5E+20</c>) にする。
    /// 小数点は地域設定に従う (INSP-02 の仕様 6)。
    /// </summary>
    public static string Format(bool negative, BigInteger q, int exp10, CultureInfo culture)
    {
        string digits = q.ToString(CultureInfo.InvariantCulture);
        int trimmed = digits.Length;
        while (trimmed > 1 && digits[trimmed - 1] == '0')
        {
            trimmed--;
        }

        exp10 += digits.Length - trimmed;
        digits = digits[..trimmed];
        int k = digits.Length - 1 + exp10;
        string point = culture.NumberFormat.NumberDecimalSeparator;
        var text = new StringBuilder();
        if (negative)
        {
            text.Append(culture.NumberFormat.NegativeSign);
        }

        if (k < -5 || k >= 15)
        {
            text.Append(digits[0]);
            if (digits.Length > 1)
            {
                text.Append(point).Append(digits, 1, digits.Length - 1);
            }

            text.Append('E').Append(k < 0 ? '-' : '+').Append(Math.Abs(k).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (k < 0)
        {
            text.Append('0').Append(point).Append('0', -k - 1).Append(digits);
        }
        else if (k + 1 >= digits.Length)
        {
            text.Append(digits).Append('0', k + 1 - digits.Length);
        }
        else
        {
            text.Append(digits, 0, k + 1).Append(point).Append(digits, k + 1, digits.Length - k - 1);
        }

        return text.ToString();
    }

    /// <summary>指数表記 (<c>3.1415926535897932385E+00</c>)。</summary>
    public static string FormatExponent(bool negative, BigInteger q, int exp10, CultureInfo culture)
    {
        string digits = q.ToString(CultureInfo.InvariantCulture).TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        int k = q.ToString(CultureInfo.InvariantCulture).Length - 1 + exp10;
        string mantissa = digits.Length > 1 ? digits[0] + culture.NumberFormat.NumberDecimalSeparator + digits[1..] : digits;
        return (negative ? culture.NumberFormat.NegativeSign : string.Empty) + mantissa + "E" + (k < 0 ? "-" : "+")
            + Math.Abs(k).ToString("000", CultureInfo.InvariantCulture);
    }
}

/// <summary>小さな浮動小数点の形式 (half、bfloat16) と Real48 の変換 (INSP-06)。</summary>
public static class SmallFloats
{
    /// <summary>float のビット列を bfloat16 (上位 16 bit) に偶数への丸めで丸める。NaN は静かな NaN。</summary>
    public static ushort ToBFloat16(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        if (float.IsNaN(value))
        {
            return (ushort)((bits >> 16) | 0x0040);
        }

        uint lsb = (bits >> 16) & 1;
        return (ushort)((bits + 0x7FFF + lsb) >> 16);
    }

    public static float FromBFloat16(ushort bits) => BitConverter.UInt32BitsToSingle((uint)bits << 16);

    /// <summary>Real48 (Turbo Pascal) の 6 バイトを読む。指数 0 は 0。値は double で正確に表せる。</summary>
    public static double FromReal48(ReadOnlySpan<byte> b)
    {
        if (b[0] == 0)
        {
            return 0;
        }

        ulong mantissa = ((ulong)(b[5] & 0x7F) << 32) | ((ulong)b[4] << 24) | ((ulong)b[3] << 16) | ((ulong)b[2] << 8) | b[1];
        double value = (1 + mantissa / (double)(1UL << 39)) * Math.Pow(2, b[0] - 129);
        return (b[5] & 0x80) != 0 ? -value : value;
    }

    /// <summary>double を Real48 に丸める (偶数への丸め)。範囲を超えれば null、小さすぎれば 0。</summary>
    public static byte[]? ToReal48(double value)
    {
        byte[] bytes = new byte[6];
        if (value == 0)
        {
            return bytes;
        }

        if (!double.IsFinite(value))
        {
            return null;
        }

        bool negative = value < 0;
        long bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        int exponent = (int)((bits >> 52) & 0x7FF);
        ulong fraction = (ulong)bits & ((1UL << 52) - 1);
        if (exponent == 0)
        {
            // double の非正規化数は Real48 では表せないほど小さい。
            return bytes;
        }

        int e = exponent - 1023 + 129;

        // 52 bit の小数部を 39 bit に丸める。
        ulong m = fraction >> 13;
        ulong rest = fraction & 0x1FFF;
        if (rest > 0x1000 || rest == 0x1000 && (m & 1) != 0)
        {
            m++;
        }

        if (m >> 39 != 0)
        {
            m = 0;
            e++;
        }

        if (e > 255)
        {
            return null;
        }

        if (e < 1)
        {
            return bytes;
        }

        bytes[0] = (byte)e;
        bytes[1] = (byte)m;
        bytes[2] = (byte)(m >> 8);
        bytes[3] = (byte)(m >> 16);
        bytes[4] = (byte)(m >> 24);
        bytes[5] = (byte)(((m >> 32) & 0x7F) | (negative ? 0x80UL : 0));
        return bytes;
    }

    /// <summary>
    /// 値を元に戻せる最短の 10 進表記 (double で表せる形式用): 有効数字 1〜17 桁で表記し、<paramref name="roundTrips"/> を満たす最初の表記の
    /// double を返す (その double の "R" の表記はその桁数以下になる)。
    /// </summary>
    public static (double Value, int Digits) Shortest(double value, Func<double, bool> roundTrips)
    {
        for (int p = 1; p <= 17; p++)
        {
            string s = value.ToString("E" + (p - 1), CultureInfo.InvariantCulture);
            double parsed = double.Parse(s, CultureInfo.InvariantCulture);
            if (roundTrips(parsed))
            {
                return (parsed, p);
            }
        }

        return (value, 17);
    }
}
