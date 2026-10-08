using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Search;

/// <summary>整数の検索の符号 (FIND-13 の仕様 3)。</summary>
public enum IntegerSign
{
    /// <summary>符号あり・符号なしのどちらかの範囲に入れば、その表現で探す (既定)。</summary>
    Either,
    Signed,
    Unsigned,
}

/// <summary>数値の検索のエンディアン (FIND-13 の仕様 4)。</summary>
public enum SearchEndian
{
    Little,
    Big,

    /// <summary>両方のバイト列を同時に探す。結果にはどちらで一致したかを示す。</summary>
    Both,
}

/// <summary>浮動小数点の形式 (FIND-14 の仕様 1)。</summary>
public enum FloatFormat
{
    /// <summary>half (16 bit)。</summary>
    Half,

    /// <summary>float (32 bit。既定)。</summary>
    Single,

    /// <summary>double (64 bit)。</summary>
    Double,
}

/// <summary>浮動小数点の許容誤差の種類 (FIND-14 の仕様 3)。</summary>
public enum ToleranceKind
{
    /// <summary>なし (既定)。形式に丸めた値とビット単位で同じものだけ。0 は +0 と −0 の両方。</summary>
    None,

    /// <summary>|x − 値| ≦ 誤差。</summary>
    Absolute,

    /// <summary>|x − 値| ≦ 誤差 × |値|。誤差は比率 (0.001 など)。</summary>
    Relative,

    /// <summary>形式で隣り合う値の個数 (0〜1,000,000) 以内。</summary>
    Ulp,
}

/// <summary>整数の検索の条件 (FIND-13)。</summary>
public sealed record IntegerSearchOptions
{
    /// <summary>サイズ (8 / 16 / 24 / 32 / 48 / 64 bit。既定 32)。</summary>
    public int Bits { get; init; } = 32;

    public IntegerSign Sign { get; init; } = IntegerSign.Either;

    public SearchEndian Endian { get; init; } = SearchEndian.Little;
}

/// <summary>浮動小数点の検索の条件 (FIND-14)。</summary>
public sealed record FloatSearchOptions
{
    public FloatFormat Format { get; init; } = FloatFormat.Single;

    public SearchEndian Endian { get; init; } = SearchEndian.Little;

    public ToleranceKind Tolerance { get; init; } = ToleranceKind.None;

    /// <summary>許容誤差の値 (Absolute・Relative は実数、Ulp は 0〜1,000,000 の整数)。</summary>
    public double ToleranceValue { get; init; }
}

/// <summary>
/// 数値の検索の条件 (結果一覧の「エンディアン」「値」の列と、置換語の符号化に使う。FIND-13 の仕様 7、FIND-22 の仕様 2)。
/// <see cref="VariantBigEndian"/> は、パターンの種類 (<see cref="SearchPattern.Variants"/>) ごとのエンディアン。
/// </summary>
public sealed record NumericSearchInfo(bool IsFloat, int Bits, IntegerSign Sign, FloatFormat Format, IReadOnlyList<bool> VariantBigEndian)
{
    /// <summary>値のバイト数。</summary>
    public int ByteLength => IsFloat ? NumericSearch.ByteLength(Format) : Bits / 8;

    /// <summary>種類 <paramref name="variant"/> のエンディアン (ビッグエンディアンなら true)。</summary>
    public bool IsBigEndian(int variant) => variant >= 0 && variant < VariantBigEndian.Count && VariantBigEndian[variant];

    /// <summary>一致したバイト列の値 (結果一覧の「値」列。FIND-20 の仕様 4)。整数は「4,660 (0x1234)」の形。</summary>
    public string FormatValue(ReadOnlySpan<byte> bytes, int variant)
    {
        if (bytes.Length < ByteLength)
        {
            return string.Empty;
        }

        bool big = IsBigEndian(variant);
        ReadOnlySpan<byte> value = bytes[..ByteLength];
        if (IsFloat)
        {
            double d = NumericSearch.DecodeFloat(value, Format, big);
            return d.ToString(Format switch { FloatFormat.Half => "G5", FloatFormat.Single => "G9", _ => "G17" }, CultureInfo.CurrentCulture);
        }

        BigInteger unsigned = NumericSearch.DecodeUnsigned(value, big);
        BigInteger signed = unsigned >= (BigInteger.One << (Bits - 1)) ? unsigned - (BigInteger.One << Bits) : unsigned;
        string hex = "0x" + unsigned.ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(1, '0');
        return Sign switch
        {
            IntegerSign.Signed => $"{signed.ToString("N0", CultureInfo.CurrentCulture)} ({hex})",
            IntegerSign.Unsigned => $"{unsigned.ToString("N0", CultureInfo.CurrentCulture)} ({hex})",
            _ => signed == unsigned
                ? $"{unsigned.ToString("N0", CultureInfo.CurrentCulture)} ({hex})"
                : $"{unsigned.ToString("N0", CultureInfo.CurrentCulture)} / {signed.ToString("N0", CultureInfo.CurrentCulture)} ({hex})",
        };
    }

    /// <summary>
    /// 置換語を同じサイズ・形式で符号化する (FIND-22 の仕様 2)。エンディアン「両方」では、一致した種類のエンディアンで符号化する。
    /// </summary>
    public byte[] EncodeReplacement(string text, int variant, IExpressionContext? context = null)
    {
        bool big = IsBigEndian(variant);
        if (IsFloat)
        {
            double value = NumericSearch.ParseFloat(text);
            return NumericSearch.EncodeFloat(NumericSearch.Round(value, Format), Format, big);
        }

        BigInteger v = NumericSearch.ParseInteger(text, context);
        NumericSearch.CheckRange(v, Bits, Sign);
        return NumericSearch.EncodeInteger(v, Bits, big);
    }
}

/// <summary>数値の検索語を作る (FIND-13、FIND-14)。</summary>
public static class NumericSearch
{
    /// <summary>整数のサイズの選択肢 (FIND-13 の仕様 2)。</summary>
    public static IReadOnlyList<int> IntegerSizes { get; } = [8, 16, 24, 32, 48, 64];

    /// <summary>ULP の上限 (FIND-14 の仕様 3)。</summary>
    public const int MaxUlp = 1_000_000;

    private const string LittleLabel = "LE";
    private const string BigLabel = "BE";

    /// <summary>
    /// 整数の検索語 (FIND-13)。値は入力式 (00-overview 6 章。接頭辞のない数値は 10 進) で、負の値は先頭の `-` で表す。
    /// 範囲外の値は範囲を示してエラーにする。エンディアン「両方」で同じバイト列になる場合は 1 つにまとめる。
    /// </summary>
    public static SearchPattern Integer(string text, IntegerSearchOptions options, IExpressionContext? context = null)
    {
        if (!IntegerSizes.Contains(options.Bits))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Bits, "サイズは 8 / 16 / 24 / 32 / 48 / 64 bit です。");
        }

        BigInteger value = ParseInteger(text, context);
        CheckRange(value, options.Bits, options.Sign);
        (byte[][] alternatives, string[] labels) = ForEndians(options.Endian, big => EncodeInteger(value, options.Bits, big));
        SearchPattern pattern = SearchPattern.FromAlternatives(alternatives, labels);
        return WithInfo(pattern, new NumericSearchInfo(false, options.Bits, options.Sign, FloatFormat.Single, BigFlags(pattern)));
    }

    /// <summary>
    /// 浮動小数点の検索語 (FIND-14)。値は小数点 `.` だけを使い (地域設定に依存しない)、指数表記・`inf`・`-inf`・`nan` を受け付ける。
    /// 許容誤差がなければバイト列のリテラル検索 (0 は +0 と −0)、あれば各位置で値を復号して比べる。
    /// </summary>
    public static SearchPattern Float(string text, FloatSearchOptions options)
    {
        double value = ParseFloat(text);
        double stored = Round(value, options.Format);
        int length = ByteLength(options.Format);
        CheckTolerance(options);
        bool[] endians = options.Endian switch
        {
            SearchEndian.Little => [false],
            SearchEndian.Big => [true],
            _ => [false, true],
        };
        string[] endianLabels = [.. endians.Select(b => b ? BigLabel : LittleLabel)];
        byte[] display = EncodeFloat(stored, options.Format, endians[0]);

        SearchPattern pattern;
        if (double.IsNaN(stored))
        {
            // NaN はすべての NaN に一致する (符号・ペイロードを問わない。許容誤差は無視する。仕様 4)。
            pattern = SearchPattern.FromMatcher(new FloatMatcher(options.Format, endians, x => double.IsNaN(x)), display, endianLabels);
        }
        else if (options.Tolerance == ToleranceKind.None || double.IsInfinity(stored))
        {
            // 誤差なし: 丸めた値のバイト列。0 は +0 と −0 の両方 (仕様 3・8)。無限大は同じ符号の無限大だけ (仕様 5)。
            var alternatives = new List<byte[]>();
            var labels = new List<string>();
            double[] values = stored == 0 ? [0.0, -0.0] : [stored];
            foreach (double v in values)
            {
                for (int e = 0; e < endians.Length; e++)
                {
                    alternatives.Add(EncodeFloat(v, options.Format, endians[e]));
                    labels.Add(endianLabels[e]);
                }
            }

            pattern = SearchPattern.FromAlternatives(alternatives, labels);
        }
        else
        {
            Func<double, bool> test = options.Tolerance switch
            {
                ToleranceKind.Absolute => x => Math.Abs(x - stored) <= options.ToleranceValue,
                ToleranceKind.Relative => x => Math.Abs(x - stored) <= options.ToleranceValue * Math.Abs(stored),
                _ => UlpTest(stored, options.Format, (long)options.ToleranceValue),
            };
            pattern = SearchPattern.FromMatcher(new FloatMatcher(options.Format, endians, x => !double.IsNaN(x) && test(x)), display, endianLabels);
        }

        return WithInfo(pattern, new NumericSearchInfo(true, length * 8, IntegerSign.Either, options.Format, BigFlags(pattern)));
    }

    /// <summary>形式に丸めたあとの値 (検索欄の横の「float として格納される値」。FIND-14 の画面)。範囲を超えて無限大になる場合はエラー。</summary>
    public static double Round(double value, FloatFormat format)
    {
        double rounded = format switch
        {
            FloatFormat.Half => (double)(Half)value,
            FloatFormat.Single => (float)value,
            _ => value,
        };
        if (double.IsInfinity(rounded) && !double.IsInfinity(value))
        {
            throw new PatternException(PatternError.FloatOverflow, value.ToString(CultureInfo.InvariantCulture), null, [FormatName(format)]);
        }

        return rounded;
    }

    /// <summary>形式の名前 (half / float / double)。</summary>
    public static string FormatName(FloatFormat format) => format switch
    {
        FloatFormat.Half => "half",
        FloatFormat.Single => "float",
        _ => "double",
    };

    /// <summary>形式のバイト数。</summary>
    public static int ByteLength(FloatFormat format) => format switch
    {
        FloatFormat.Half => 2,
        FloatFormat.Single => 4,
        _ => 8,
    };

    /// <summary>
    /// 浮動小数点の値を読む。小数点は `.` だけ (地域設定に依存しない。FIND-14 の仕様 2)。`inf`・`+inf`・`-inf`・`nan` を受け付ける。
    /// </summary>
    public static double ParseFloat(string text)
    {
        string t = text.Trim();
        switch (t.ToLowerInvariant())
        {
            case "inf" or "+inf" or "infinity" or "+infinity":
                return double.PositiveInfinity;
            case "-inf" or "-infinity":
                return double.NegativeInfinity;
            case "nan" or "+nan" or "-nan":
                return double.NaN;
        }

        if (t.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (!double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out double value))
        {
            throw new PatternException(PatternError.InvalidNumber, t, FirstBadPosition(t));
        }

        if (double.IsInfinity(value))
        {
            throw new PatternException(PatternError.FloatOverflow, t, null, ["double"]);
        }

        return value;
    }

    /// <summary>
    /// 整数の値を読む (入力式。接頭辞のない数値は 10 進)。64 bit の符号なしの上限までの値は、式でなく数値だけを書いた場合にも受け付ける。
    /// </summary>
    public static BigInteger ParseInteger(string text, IExpressionContext? context = null)
    {
        string t = text.Trim();
        if (t.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (ExpressionEvaluator.TryEvaluate(t, context ?? EmptyContext.Instance, out long value, out ExpressionException? error, DefaultRadix.Decimal))
        {
            return value;
        }

        // 64 bit の符号なしの範囲 (long を超える値) の数値だけの入力。
        if (TryParseBigLiteral(t, out BigInteger big))
        {
            return big;
        }

        throw new PatternException(PatternError.InvalidNumber, t, error is null ? null : error.Position + 1);
    }

    /// <summary>
    /// 値がサイズ・符号の範囲に入るか確かめる (FIND-13 の仕様 3)。範囲外なら、範囲 (地域設定で書式化した数値) を付けてエラーにする。
    /// </summary>
    public static void CheckRange(BigInteger value, int bits, IntegerSign sign)
    {
        BigInteger smin = -(BigInteger.One << (bits - 1));
        BigInteger smax = (BigInteger.One << (bits - 1)) - 1;
        BigInteger umax = (BigInteger.One << bits) - 1;
        bool inSigned = value >= smin && value <= smax;
        bool inUnsigned = value >= 0 && value <= umax;
        string F(BigInteger v) => v.ToString("N0", CultureInfo.CurrentCulture);
        string b = bits.ToString(CultureInfo.CurrentCulture);
        switch (sign)
        {
            case IntegerSign.Signed when !inSigned:
                throw new PatternException(PatternError.SignedOutOfRange, value.ToString(CultureInfo.InvariantCulture), null, [b, F(smin), F(smax)]);
            case IntegerSign.Unsigned when !inUnsigned:
                throw new PatternException(PatternError.UnsignedOutOfRange, value.ToString(CultureInfo.InvariantCulture), null, [b, F(0), F(umax)]);
            case IntegerSign.Either when !inSigned && !inUnsigned:
                throw new PatternException(PatternError.IntegerOutOfRange, value.ToString(CultureInfo.InvariantCulture), null, [b, F(smin), F(smax), F(0), F(umax)]);
        }
    }

    /// <summary>整数を 2 の補数で <paramref name="bits"/> bit に符号化する。</summary>
    public static byte[] EncodeInteger(BigInteger value, int bits, bool bigEndian)
    {
        int n = bits / 8;
        byte[] raw = value.ToByteArray(isUnsigned: false, isBigEndian: false);
        byte[] result = new byte[n];
        byte fill = value.Sign < 0 ? (byte)0xFF : (byte)0x00;
        for (int i = 0; i < n; i++)
        {
            result[i] = i < raw.Length ? raw[i] : fill;
        }

        if (bigEndian)
        {
            Array.Reverse(result);
        }

        return result;
    }

    /// <summary>浮動小数点を符号化する。</summary>
    public static byte[] EncodeFloat(double value, FloatFormat format, bool bigEndian)
    {
        byte[] result = new byte[ByteLength(format)];
        switch (format)
        {
            case FloatFormat.Half when bigEndian: BinaryPrimitives.WriteHalfBigEndian(result, (Half)value); break;
            case FloatFormat.Half: BinaryPrimitives.WriteHalfLittleEndian(result, (Half)value); break;
            case FloatFormat.Single when bigEndian: BinaryPrimitives.WriteSingleBigEndian(result, (float)value); break;
            case FloatFormat.Single: BinaryPrimitives.WriteSingleLittleEndian(result, (float)value); break;
            case FloatFormat.Double when bigEndian: BinaryPrimitives.WriteDoubleBigEndian(result, value); break;
            default: BinaryPrimitives.WriteDoubleLittleEndian(result, value); break;
        }

        return result;
    }

    /// <summary>浮動小数点を復号する。</summary>
    public static double DecodeFloat(ReadOnlySpan<byte> bytes, FloatFormat format, bool bigEndian) => format switch
    {
        FloatFormat.Half => (double)(bigEndian ? BinaryPrimitives.ReadHalfBigEndian(bytes) : BinaryPrimitives.ReadHalfLittleEndian(bytes)),
        FloatFormat.Single => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(bytes) : BinaryPrimitives.ReadSingleLittleEndian(bytes),
        _ => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : BinaryPrimitives.ReadDoubleLittleEndian(bytes),
    };

    /// <summary>符号なしの整数として復号する。</summary>
    public static BigInteger DecodeUnsigned(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        new(bytes, isUnsigned: true, isBigEndian: bigEndian);

    private static void CheckTolerance(FloatSearchOptions options)
    {
        double t = options.ToleranceValue;
        bool ok = options.Tolerance switch
        {
            ToleranceKind.None => true,
            ToleranceKind.Ulp => t >= 0 && t <= MaxUlp && Math.Floor(t) == t,
            _ => t >= 0 && double.IsFinite(t),
        };
        if (!ok)
        {
            throw new PatternException(PatternError.InvalidTolerance, t.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>ULP の比較: 形式の値を、隣り合う値の差が 1 になる整数の並びに写して差を比べる。</summary>
    private static Func<double, bool> UlpTest(double value, FloatFormat format, long ulps)
    {
        long target = Ordered(value, format);
        return x => !double.IsNaN(x) && Math.Abs(Ordered(x, format) - target) <= ulps;
    }

    /// <summary>浮動小数点のビット列を、値の順に並ぶ整数に写す (+0 と −0 は同じ 0)。</summary>
    private static long Ordered(double value, FloatFormat format)
    {
        long bits;
        long signBit;
        switch (format)
        {
            case FloatFormat.Half:
                bits = BitConverter.HalfToInt16Bits((Half)value) & 0xFFFF;
                signBit = 0x8000;
                break;
            case FloatFormat.Single:
                bits = BitConverter.SingleToInt32Bits((float)value) & 0xFFFFFFFFL;
                signBit = 0x80000000L;
                break;
            default:
                ulong u = BitConverter.DoubleToUInt64Bits(value);
                return (long)u < 0 ? -(long)(u & 0x7FFFFFFFFFFFFFFFUL) : (long)u;
        }

        return (bits & signBit) != 0 ? -(bits & (signBit - 1)) : bits;
    }

    private static (byte[][] Alternatives, string[] Labels) ForEndians(SearchEndian endian, Func<bool, byte[]> encode) => endian switch
    {
        SearchEndian.Little => ([encode(false)], [LittleLabel]),
        SearchEndian.Big => ([encode(true)], [BigLabel]),
        _ => ([encode(false), encode(true)], [LittleLabel, BigLabel]),
    };

    /// <summary>パターンの種類の名前から、種類ごとのエンディアンを決める (「LE/BE」のようにまとめたものは対称なのでどちらでもよい)。</summary>
    private static bool[] BigFlags(SearchPattern pattern) =>
        pattern.Variants.Count == 0 ? [false] : [.. pattern.Variants.Select(v => v == BigLabel)];

    private static SearchPattern WithInfo(SearchPattern pattern, NumericSearchInfo info) => pattern.WithNumeric(info);

    private static int? FirstBadPosition(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!(char.IsAsciiDigit(c) || c is '.' or '+' or '-' or 'e' or 'E'))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static bool TryParseBigLiteral(string text, out BigInteger value)
    {
        value = default;
        string t = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return BigInteger.TryParse("0" + t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        if (t.EndsWith('h') || t.EndsWith('H'))
        {
            return BigInteger.TryParse("0" + t[..^1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        return t.All(char.IsAsciiDigit) && BigInteger.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>名前と読み取り関数を使わない入力式の文脈。</summary>
    private sealed class EmptyContext : IExpressionContext
    {
        public static readonly EmptyContext Instance = new();

        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    /// <summary>各位置で浮動小数点を復号して比べる (許容誤差・NaN)。エンディアンごとに試し、一致したエンディアンの番号を返す。</summary>
    private sealed class FloatMatcher(FloatFormat format, bool[] endians, Func<double, bool> test) : ValueMatcher(ByteLength(format))
    {
        public override int Match(ReadOnlySpan<byte> data)
        {
            for (int e = 0; e < endians.Length; e++)
            {
                if (test(DecodeFloat(data, format, endians[e])))
                {
                    return e;
                }
            }

            return -1;
        }
    }
}
