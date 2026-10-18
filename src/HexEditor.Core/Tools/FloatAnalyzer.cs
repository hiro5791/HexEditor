using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Inspector;

namespace HexEditor.Core.Tools;

/// <summary>浮動小数点数の形式 (TOOL-25 の仕様 1)。</summary>
public sealed record FloatLayout(string Id, int TotalBits, int ExponentBits, int FractionBits, bool ExplicitInteger)
{
    public static readonly FloatLayout Half = new("half", 16, 5, 10, false);
    public static readonly FloatLayout BFloat16 = new("bfloat16", 16, 8, 7, false);
    public static readonly FloatLayout Single = new("float", 32, 8, 23, false);
    public static readonly FloatLayout Double = new("double", 64, 11, 52, false);

    /// <summary>x87 拡張精度 (80 bit。整数ビットを明示した 64 bit の仮数)。</summary>
    public static readonly FloatLayout X87 = new("x87", 80, 15, 63, true);

    public static IReadOnlyList<FloatLayout> All { get; } = [Half, BFloat16, Single, Double, X87];

    public static FloatLayout? Find(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public int Bias => (1 << (ExponentBits - 1)) - 1;

    /// <summary>仮数部として並ぶビット数 (x87 は整数ビットを含めて 64)。</summary>
    public int MantissaBits => FractionBits + (ExplicitInteger ? 1 : 0);

    /// <summary>精度 (有効数字のビット数。整数ビットを含む)。</summary>
    public int Precision => FractionBits + 1;

    public int Bytes => TotalBits / 8;

    public int MaxBiasedExponent => (1 << ExponentBits) - 1;
}

/// <summary>分類 (TOOL-25 の仕様 2)。</summary>
public enum FloatClass
{
    Normal,
    Subnormal,
    Zero,
    Infinity,
    NaN,

    /// <summary>x87 の正しくない表現 (整数ビットが 0 の正規化されていない数、擬似無限大、擬似 NaN)。</summary>
    Invalid,
}

/// <summary>入力の誤り。<see cref="Code"/> は説明文のリソース (<c>Float_Error_&lt;Code&gt;</c>) の名前。</summary>
public sealed record FloatInputError(string Code);

/// <summary>1 つの値の解析結果。</summary>
public sealed record FloatAnalysis(
    FloatLayout Format,
    UInt128 Bits,
    bool Negative,
    UInt128 ExponentRaw,
    UInt128 MantissaRaw,
    string ExponentBitString,
    string MantissaBitString,
    int? UnbiasedExponent,
    string SignificandText,
    FloatClass Class,
    bool IsQuietNaN,
    UInt128 NanPayload,
    string Shortest,
    string? ExactDecimal,
    string HexFloat,
    string NextUp,
    string NextDown,
    string Ulp,
    UInt128? NextUpBits,
    UInt128? NextDownBits)
{
    /// <summary>全ビットを上位から並べた文字列。</summary>
    public string BitString => (Negative ? "1" : "0") + ExponentBitString + MantissaBitString;
}

/// <summary>入力した値を最も近い表現可能な値に丸めた結果。<see cref="Error"/> は「表現した値 − 入力した値」を正確な 10 進で表したもの。</summary>
public sealed record FloatRounding(UInt128 Bits, string Error);

/// <summary>
/// IEEE 754 浮動小数点のビジュアライザの計算 (TOOL-25)。符号・指数部・仮数部への分解、分類、最短の 10 進表現、正確な 10 進展開、16 進の
/// 浮動小数点表記、隣の値と ULP、10 進の入力の丸めと誤差を、<see cref="BigInteger"/> で正確に求める。
/// </summary>
public static partial class FloatAnalyzer
{
    private static readonly System.Text.RegularExpressions.Regex DecimalPattern = DecimalRegex();

    [GeneratedRegex(@"^(?<sign>[+-])?(?<int>\d*)(?:\.(?<frac>\d*))?(?:[eE](?<exp>[+-]?\d+))?$", RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DecimalRegex();

    private static UInt128 Mask(int bits) => bits >= 128 ? UInt128.MaxValue : (UInt128.One << bits) - 1;

    // ---- バイト列との変換 ----

    /// <summary>形式のバイト数ぶんのバイト列からビット列にする。足りなければ null。</summary>
    public static UInt128? FromBytes(FloatLayout format, ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        if (bytes.Length < format.Bytes)
        {
            return null;
        }

        UInt128 value = 0;
        for (int i = 0; i < format.Bytes; i++)
        {
            byte b = bigEndian ? bytes[i] : bytes[format.Bytes - 1 - i];
            value = (value << 8) | b;
        }

        return value;
    }

    public static byte[] ToBytes(FloatLayout format, UInt128 bits, bool bigEndian)
    {
        byte[] bytes = new byte[format.Bytes];
        for (int i = 0; i < format.Bytes; i++)
        {
            bytes[bigEndian ? format.Bytes - 1 - i : i] = (byte)(bits >> (8 * i));
        }

        return bytes;
    }

    /// <summary>ビット <paramref name="index"/> (0 が最下位) を反転する。</summary>
    public static UInt128 ToggleBit(FloatLayout format, UInt128 bits, int index) =>
        index < 0 || index >= format.TotalBits ? bits : (bits ^ (UInt128.One << index)) & Mask(format.TotalBits);

    /// <summary>ビット列の文字 (上位から) を 1 つ置き換える入力用: Hex 文字列を解釈する (<c>0x</c> と空白を許す)。</summary>
    public static UInt128? ParseHex(FloatLayout format, string text)
    {
        string t = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        if (t.Length == 0 || t.Length > format.Bytes * 2 || !t.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return UInt128.Parse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>ビット列の Hex 表記 (<c>0x3DCCCCCD</c>)。</summary>
    public static string HexText(FloatLayout format, UInt128 bits) =>
        "0x" + bits.ToString("X" + (format.Bytes * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // ---- 解析 ----

    public static FloatAnalysis Analyze(FloatLayout format, UInt128 bits)
    {
        bits &= Mask(format.TotalBits);
        bool negative = ((bits >> (format.TotalBits - 1)) & 1) != 0;
        UInt128 exponent = (bits >> format.MantissaBits) & Mask(format.ExponentBits);
        UInt128 mantissa = bits & Mask(format.MantissaBits);
        UInt128 fraction = mantissa & Mask(format.FractionBits);
        bool integerBit = format.ExplicitInteger && ((mantissa >> format.FractionBits) & 1) != 0;
        bool maxExponent = exponent == (UInt128)format.MaxBiasedExponent;
        UInt128 topFraction = UInt128.One << (format.FractionBits - 1);

        FloatClass cls;
        bool quiet = false;
        UInt128 payload = 0;
        if (maxExponent)
        {
            if (format.ExplicitInteger && !integerBit)
            {
                cls = FloatClass.Invalid;
            }
            else if (fraction == 0)
            {
                cls = FloatClass.Infinity;
            }
            else
            {
                cls = FloatClass.NaN;
                quiet = (fraction & topFraction) != 0;
                payload = fraction & ~topFraction;
            }
        }
        else if (exponent == 0)
        {
            cls = mantissa == 0 ? FloatClass.Zero : FloatClass.Subnormal;
        }
        else
        {
            cls = format.ExplicitInteger && !integerBit ? FloatClass.Invalid : FloatClass.Normal;
        }

        bool finite = cls is FloatClass.Normal or FloatClass.Subnormal or FloatClass.Zero || (cls == FloatClass.Invalid && !maxExponent);
        int? unbiased = cls is FloatClass.Normal or FloatClass.Invalid && !maxExponent ? (int?)((int)exponent - format.Bias)
            : cls == FloatClass.Subnormal ? 1 - format.Bias : null;

        string expBits = Binary(exponent, format.ExponentBits);
        string manBits = Binary(mantissa, format.MantissaBits);
        string significand = SignificandText(format, cls, mantissa, fraction);

        string? exact = null;
        string shortest;
        if (finite)
        {
            (BigInteger num, BigInteger den) = Rational(format, exponent, mantissa);
            exact = ExactDecimal(num, den, negative);
            shortest = cls == FloatClass.Zero ? (negative ? "-0" : "0") : ShortestDecimal(format, num, den, negative, bits);
        }
        else
        {
            shortest = cls switch
            {
                FloatClass.Infinity => negative ? "-inf" : "inf",
                FloatClass.NaN => "nan",
                _ => string.Empty,
            };
        }

        UInt128? up = NextBits(format, bits, up: true);
        UInt128? down = NextBits(format, bits, up: false);
        return new FloatAnalysis(
            format, bits, negative, exponent, mantissa, expBits, manBits, unbiased, significand, cls, quiet, payload, shortest, exact,
            HexFloat(format, cls, negative, exponent, mantissa), NeighbourText(format, up), NeighbourText(format, down), UlpText(format, bits, cls, up, down),
            up, down);
    }

    private static string Binary(UInt128 value, int width)
    {
        var sb = new StringBuilder(width);
        for (int i = width - 1; i >= 0; i--)
        {
            sb.Append(((value >> i) & 1) != 0 ? '1' : '0');
        }

        return sb.ToString();
    }

    /// <summary>仮数部の表記 (暗黙の 1 を含めた 2 進。正規化数は <c>1.0100…</c>、非正規化数は <c>0.0100…</c>)。</summary>
    private static string SignificandText(FloatLayout format, FloatClass cls, UInt128 mantissa, UInt128 fraction)
    {
        if (format.ExplicitInteger)
        {
            return ((mantissa >> format.FractionBits) & 1) + "." + Binary(fraction, format.FractionBits);
        }

        string lead = cls is FloatClass.Normal ? "1" : "0";
        return lead + "." + Binary(fraction, format.FractionBits);
    }

    /// <summary>有限の値の絶対値を有理数で返す (num / den)。</summary>
    private static (BigInteger Num, BigInteger Den) Rational(FloatLayout format, UInt128 exponent, UInt128 mantissa)
    {
        BigInteger significand = (BigInteger)mantissa;
        if (!format.ExplicitInteger && exponent != 0)
        {
            significand |= BigInteger.One << format.FractionBits;
        }

        int e = (exponent == 0 ? 1 : (int)exponent) - format.Bias - format.FractionBits;
        return e >= 0 ? (significand << e, BigInteger.One) : (significand, BigInteger.One << -e);
    }

    // ---- 10 進の表記 ----

    /// <summary>正確な 10 進展開 (全桁)。分母が 2 の累乗の有理数が対象。</summary>
    public static string ExactDecimal(BigInteger num, BigInteger den, bool negative)
    {
        (string digits, int exp10) = ExactDigits(num, den);
        var sb = new StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }

        if (digits == "0")
        {
            return sb.Append('0').ToString();
        }

        if (exp10 >= 0)
        {
            sb.Append(digits).Append('0', exp10);
        }
        else if (-exp10 >= digits.Length)
        {
            sb.Append("0.").Append('0', -exp10 - digits.Length).Append(digits);
        }
        else
        {
            sb.Append(digits, 0, digits.Length + exp10).Append('.').Append(digits, digits.Length + exp10, -exp10);
        }

        return sb.ToString();
    }

    /// <summary>num / den (分母は 2 と 5 の積だけを素因数に持つ) を、桁の列と 10 の指数 (値 = 桁 × 10^指数) にする。末尾の 0 は省く。</summary>
    private static (string Digits, int Exp10) ExactDigits(BigInteger num, BigInteger den)
    {
        if (num.IsZero)
        {
            return ("0", 0);
        }

        // den = 2^a × 5^b → num × 10^m / den (m = max(a, b)) が整数。
        int twos = 0, fives = 0;
        BigInteger d = den;
        while (d.IsEven && !d.IsZero)
        {
            d >>= 1;
            twos++;
        }

        while ((d % 5).IsZero)
        {
            d /= 5;
            fives++;
        }

        if (!d.IsOne)
        {
            throw new ArgumentException("The denominator has a prime factor other than 2 and 5.", nameof(den));
        }

        int m = Math.Max(twos, fives);
        BigInteger integer = num * BigInteger.Pow(10, m) / den;
        string digits = integer.ToString(CultureInfo.InvariantCulture);
        int exp10 = -m;
        int trimmed = digits.Length;
        while (trimmed > 1 && digits[trimmed - 1] == '0')
        {
            trimmed--;
        }

        exp10 += digits.Length - trimmed;
        return (digits[..trimmed], exp10);
    }

    /// <summary>指数表記 (<c>1.490116119384765625E-9</c>)。0 は <c>0</c>。</summary>
    public static string ScientificText(BigInteger num, BigInteger den, bool negative)
    {
        (string digits, int exp10) = ExactDigits(num, den);
        if (digits == "0")
        {
            return "0";
        }

        int k = digits.Length - 1 + exp10;
        string mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
        return (negative ? "-" : string.Empty) + mantissa + "E" + k.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>往復で同じビットになる最短の 10 進表記。</summary>
    private static string ShortestDecimal(FloatLayout format, BigInteger num, BigInteger den, bool negative, UInt128 bits)
    {
        if (num.IsZero)
        {
            return negative ? "-0" : "0";
        }

        (BigInteger q, int exp10) = DecimalText.Shortest(num, den, maxDigits: 40, (cn, cd) => Encode(format, cn, cd, negative) == bits);
        return DecimalText.Format(negative, q, exp10, CultureInfo.InvariantCulture);
    }

    // ---- 16 進の浮動小数点表記 ----

    private static string HexFloat(FloatLayout format, FloatClass cls, bool negative, UInt128 exponent, UInt128 mantissa)
    {
        string sign = negative ? "-" : string.Empty;
        switch (cls)
        {
            case FloatClass.Infinity:
                return sign + "inf";
            case FloatClass.NaN:
                return "nan";
            case FloatClass.Invalid:
                return string.Empty;
        }

        UInt128 fraction = mantissa & Mask(format.FractionBits);
        int digitsCount = (format.FractionBits + 3) / 4;
        UInt128 aligned = fraction << (digitsCount * 4 - format.FractionBits);
        string hex = aligned.ToString("x" + digitsCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).TrimEnd('0');
        string lead = format.ExplicitInteger ? ((mantissa >> format.FractionBits) & 1).ToString(CultureInfo.InvariantCulture) : exponent == 0 ? "0" : "1";
        int exp = cls == FloatClass.Zero ? 0 : (exponent == 0 ? 1 : (int)exponent) - format.Bias;
        string body = "0x" + lead + (hex.Length > 0 ? "." + hex : string.Empty);
        return sign + body + "p" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
    }

    // ---- 隣の値と ULP ----

    private static UInt128? NextBits(FloatLayout format, UInt128 bits, bool up)
    {
        UInt128 signBit = UInt128.One << (format.TotalBits - 1);
        bool negative = (bits & signBit) != 0;
        UInt128 magnitude = bits & ~signBit & Mask(format.TotalBits);
        UInt128 exponent = (magnitude >> format.MantissaBits) & Mask(format.ExponentBits);
        if (exponent == (UInt128)format.MaxBiasedExponent && ((magnitude & Mask(format.FractionBits)) != 0 || format.ExplicitInteger && ((magnitude >> format.FractionBits) & 1) == 0))
        {
            return null; // NaN など
        }

        bool awayFromZero = up != negative;
        if (awayFromZero)
        {
            if (exponent == (UInt128)format.MaxBiasedExponent)
            {
                return null; // 無限大の先はない
            }

            UInt128 next = NextMagnitude(format, magnitude);
            return (negative ? signBit : 0) | next;
        }

        if (magnitude == 0)
        {
            // ゼロから符号の向きと反対へ: 最小の非正規化数。
            return (negative ? 0 : signBit) | 1;
        }

        UInt128 prev = PreviousMagnitude(format, magnitude);
        return (negative ? signBit : 0) | prev;
    }

    private static UInt128 NextMagnitude(FloatLayout format, UInt128 magnitude)
    {
        if (!format.ExplicitInteger)
        {
            return magnitude + 1;
        }

        UInt128 exponent = (magnitude >> format.MantissaBits) & Mask(format.ExponentBits);
        UInt128 mantissa = magnitude & Mask(format.MantissaBits);
        if (exponent == 0)
        {
            mantissa++;
            if ((mantissa >> format.FractionBits) != 0)
            {
                exponent = 1;
            }
        }
        else if (mantissa == Mask(format.MantissaBits))
        {
            exponent++;
            mantissa = UInt128.One << format.FractionBits;
        }
        else
        {
            mantissa++;
        }

        return (exponent << format.MantissaBits) | mantissa;
    }

    private static UInt128 PreviousMagnitude(FloatLayout format, UInt128 magnitude)
    {
        if (!format.ExplicitInteger)
        {
            return magnitude - 1;
        }

        UInt128 exponent = (magnitude >> format.MantissaBits) & Mask(format.ExponentBits);
        UInt128 mantissa = magnitude & Mask(format.MantissaBits);
        if (exponent > 1 && mantissa == (UInt128.One << format.FractionBits))
        {
            return ((exponent - 1) << format.MantissaBits) | Mask(format.MantissaBits);
        }

        if (exponent == 1 && mantissa == (UInt128.One << format.FractionBits))
        {
            return Mask(format.FractionBits);
        }

        return (exponent << format.MantissaBits) | (mantissa - 1);
    }

    private static string NeighbourText(FloatLayout format, UInt128? bits)
    {
        if (bits is not { } b)
        {
            return string.Empty;
        }

        return AnalyzeLight(format, b);
    }

    /// <summary>隣の値の表示用の簡単な解析 (最短の 10 進表記だけ)。</summary>
    private static string AnalyzeLight(FloatLayout format, UInt128 bits)
    {
        bool negative = ((bits >> (format.TotalBits - 1)) & 1) != 0;
        UInt128 exponent = (bits >> format.MantissaBits) & Mask(format.ExponentBits);
        UInt128 mantissa = bits & Mask(format.MantissaBits);
        if (exponent == (UInt128)format.MaxBiasedExponent)
        {
            return negative ? "-inf" : "inf";
        }

        if (mantissa == 0 && exponent == 0)
        {
            return negative ? "-0" : "0";
        }

        (BigInteger num, BigInteger den) = Rational(format, exponent, mantissa);
        return ShortestDecimal(format, num, den, negative, bits);
    }

    /// <summary>ULP (隣の表現可能な値との間隔)。2 の累乗なので <c>2^-26</c> と 10 進で示す。無限大・NaN では空。</summary>
    private static string UlpText(FloatLayout format, UInt128 bits, FloatClass cls, UInt128? up, UInt128? down)
    {
        if (cls is FloatClass.Infinity or FloatClass.NaN or FloatClass.Invalid)
        {
            return string.Empty;
        }

        UInt128 signBit = UInt128.One << (format.TotalBits - 1);
        UInt128 magnitude = bits & ~signBit & Mask(format.TotalBits);
        UInt128 exponent = (magnitude >> format.MantissaBits) & Mask(format.ExponentBits);
        int e = (exponent == 0 ? 1 : (int)exponent) - format.Bias - format.FractionBits;
        (BigInteger num, BigInteger den) = e >= 0 ? (BigInteger.One << e, BigInteger.One) : (BigInteger.One, BigInteger.One << -e);
        (BigInteger q, int exp10) = DecimalText.RoundToDigits(num, den, 9);
        return $"2^{e.ToString(CultureInfo.InvariantCulture)} ({DecimalText.Format(false, q, exp10, CultureInfo.InvariantCulture)})";
    }

    // ---- 10 進の入力 ----

    /// <summary>
    /// 10 進の値 (<c>0.1</c>、<c>-1.5e3</c>、<c>inf</c>、<c>-inf</c>、<c>nan</c>) を最も近い表現可能な値 (偶数への丸め) にする。
    /// 誤差は「表現した値 − 入力した値」。解釈できなければ誤りを返す。
    /// </summary>
    public static (FloatRounding? Result, FloatInputError? Error) FromDecimalText(FloatLayout format, string text)
    {
        string t = text.Trim();
        string lower = t.ToLowerInvariant();
        UInt128 signBit = UInt128.One << (format.TotalBits - 1);
        UInt128 expAll = (UInt128)format.MaxBiasedExponent << format.MantissaBits;
        UInt128 integer = format.ExplicitInteger ? UInt128.One << format.FractionBits : 0;
        if (lower is "inf" or "+inf" or "infinity" or "+infinity")
        {
            return (new FloatRounding(expAll | integer, string.Empty), null);
        }

        if (lower is "-inf" or "-infinity")
        {
            return (new FloatRounding(signBit | expAll | integer, string.Empty), null);
        }

        if (lower is "nan" or "+nan" or "-nan")
        {
            UInt128 quiet = UInt128.One << (format.FractionBits - 1);
            return (new FloatRounding((lower[0] == '-' ? signBit : 0) | expAll | integer | quiet, string.Empty), null);
        }

        Match m = DecimalPattern.Match(t);
        if (!m.Success || (m.Groups["int"].Length == 0 && m.Groups["frac"].Length == 0))
        {
            return (null, new FloatInputError("InvalidNumber"));
        }

        bool negative = m.Groups["sign"].Value == "-";
        string digits = m.Groups["int"].Value + m.Groups["frac"].Value;
        long exp10 = -m.Groups["frac"].Length;
        if (m.Groups["exp"].Success)
        {
            if (!long.TryParse(m.Groups["exp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long e) || Math.Abs(e) > 100000)
            {
                return (null, new FloatInputError("OutOfRange"));
            }

            exp10 += e;
        }

        BigInteger d = BigInteger.Parse(digits.Length == 0 ? "0" : digits, CultureInfo.InvariantCulture);
        if (digits.Length > 100000 || Math.Abs(exp10) > 100000)
        {
            return (null, new FloatInputError("OutOfRange"));
        }

        (BigInteger num, BigInteger den) = exp10 >= 0 ? (d * BigInteger.Pow(10, (int)exp10), BigInteger.One) : (d, BigInteger.Pow(10, (int)-exp10));
        UInt128 bits = Encode(format, num, den, negative);
        return (new FloatRounding(bits, RoundingError(format, bits, num, den, negative)), null);
    }

    /// <summary>
    /// 値を別の形式に丸める (TOOL-25 の仕様 5)。無限大・NaN はそのまま対応する値にし、誤差は空。
    /// </summary>
    public static FloatRounding ConvertTo(FloatLayout from, UInt128 bits, FloatLayout to)
    {
        FloatAnalysis a = Analyze(from, bits);
        UInt128 signBit = UInt128.One << (to.TotalBits - 1);
        UInt128 expAll = (UInt128)to.MaxBiasedExponent << to.MantissaBits;
        UInt128 integer = to.ExplicitInteger ? UInt128.One << to.FractionBits : 0;
        switch (a.Class)
        {
            case FloatClass.Infinity:
                return new FloatRounding((a.Negative ? signBit : 0) | expAll | integer, string.Empty);
            case FloatClass.NaN:
                return new FloatRounding((a.Negative ? signBit : 0) | expAll | integer | (UInt128.One << (to.FractionBits - 1)), string.Empty);
            case FloatClass.Invalid:
                return new FloatRounding(0, string.Empty);
        }

        (BigInteger num, BigInteger den) = Rational(from, a.ExponentRaw, a.MantissaRaw);
        UInt128 converted = Encode(to, num, den, a.Negative);
        return new FloatRounding(converted, RoundingError(to, converted, num, den, a.Negative));
    }

    private static string RoundingError(FloatLayout format, UInt128 bits, BigInteger num, BigInteger den, bool negative)
    {
        UInt128 exponent = (bits >> format.MantissaBits) & Mask(format.ExponentBits);
        if (exponent == (UInt128)format.MaxBiasedExponent)
        {
            return string.Empty; // 無限大に丸まった
        }

        (BigInteger rn, BigInteger rd) = Rational(format, exponent, bits & Mask(format.MantissaBits));
        BigInteger stored = negative ? -rn : rn;
        BigInteger input = negative ? -num : num;

        // 差 = stored / rd − input / den = (stored × den − input × rd) / (rd × den)。
        BigInteger diffNum = stored * den - input * rd;
        BigInteger diffDen = rd * den;
        if (diffNum.IsZero)
        {
            return "0";
        }

        bool diffNegative = diffNum.Sign < 0;
        BigInteger g = BigInteger.GreatestCommonDivisor(BigInteger.Abs(diffNum), diffDen);
        return ScientificText(BigInteger.Abs(diffNum) / g, diffDen / g, diffNegative);
    }

    /// <summary>正の有理数 num / den を、形式の最も近い値 (偶数への丸め) のビット列にする。大きすぎれば無限大。</summary>
    public static UInt128 Encode(FloatLayout format, BigInteger num, BigInteger den, bool negative)
    {
        UInt128 signBit = negative ? UInt128.One << (format.TotalBits - 1) : 0;
        if (num.IsZero)
        {
            return signBit;
        }

        int p = format.Precision;
        int emin = 1 - format.Bias;
        int emax = format.MaxBiasedExponent - 1 - format.Bias;

        // 2^e ≤ num / den < 2^(e+1) となる e。
        long e = (long)num.GetBitLength() - (long)den.GetBitLength();
        if ((e >= 0 ? num.CompareTo(den << (int)e) : (num << (int)-e).CompareTo(den)) < 0)
        {
            e--;
        }

        if (e < emin)
        {
            // 非正規化数: 値 = sig × 2^(emin − (p − 1))
            long scale = p - 1 - emin;
            BigInteger sig = Float80.RoundDiv(scale >= 0 ? num << (int)scale : num, scale >= 0 ? den : den << (int)-scale);
            if (sig >= (BigInteger.One << (p - 1)))
            {
                // 丸めで最小の正規化数に届いた。
                return signBit | ((UInt128)1 << format.MantissaBits) | (format.ExplicitInteger ? (UInt128)sig : (UInt128)(sig - (BigInteger.One << (p - 1))));
            }

            return signBit | (UInt128)sig;
        }

        long shift = p - 1 - e;
        BigInteger significand = Float80.RoundDiv(shift >= 0 ? num << (int)shift : num, shift >= 0 ? den : den << (int)-shift);
        if (significand >= (BigInteger.One << p))
        {
            significand >>= 1;
            e++;
        }

        if (e > emax)
        {
            return signBit | ((UInt128)format.MaxBiasedExponent << format.MantissaBits) | (format.ExplicitInteger ? UInt128.One << format.FractionBits : 0);
        }

        UInt128 mantissa = format.ExplicitInteger ? (UInt128)significand : (UInt128)(significand - (BigInteger.One << (p - 1)));
        return signBit | ((UInt128)(e + format.Bias) << format.MantissaBits) | mantissa;
    }

    // ---- 区間のラベル (画面用) ----

    /// <summary>ビット <paramref name="index"/> (0 が最下位) が属する区間: 0 = 符号、1 = 指数部、2 = 仮数部。</summary>
    public static int SectionOf(FloatLayout format, int index) =>
        index == format.TotalBits - 1 ? 0 : index >= format.MantissaBits ? 1 : 2;

    /// <summary>区間の中でのビット番号 (区間の最下位が 0)。</summary>
    public static int IndexInSection(FloatLayout format, int index) => SectionOf(format, index) switch
    {
        0 => 0,
        1 => index - format.MantissaBits,
        _ => index,
    };
}
