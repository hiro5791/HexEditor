using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Search;

/// <summary>
/// 結果一覧に出す、値の解釈の名前 (範囲の検索の「符号あり」「符号なし」。FIND-15 の仕様 3)。表示言語の文字列を UI が入れる
/// (既定は英語)。
/// </summary>
public static class SearchLabels
{
    public static string Signed { get; set; } = "signed";

    public static string Unsigned { get; set; } = "unsigned";

    public static string SignedAndUnsigned { get; set; } = "signed/unsigned";
}

/// <summary>値の範囲の検索 (FIND-15)。</summary>
public static class NumericRange
{
    /// <summary>範囲の区切り。</summary>
    public const string Separator = "..";

    /// <summary>検索欄の入力が範囲 (`最小..最大`) か (FIND-15 の仕様 1)。</summary>
    public static bool IsRange(string text) => text.Contains(Separator, StringComparison.Ordinal);

    /// <summary>
    /// 整数の範囲の検索 (FIND-15)。`最小..最大` (両端を含む)、`..最大`、`最小..` を受け付ける。最小が最大より大きい場合、端が型の範囲外の
    /// 場合はエラー。符号「どちらでも」では、符号あり・符号なしのどちらかで範囲に入れば一致とし、どちらで一致したかを結果に示す
    /// (仕様 3)。<paramref name="exclude"/> なら範囲に入らない値に一致する (仕様 5)。
    /// </summary>
    public static SearchPattern Integer(string text, IntegerSearchOptions options, bool exclude = false, IExpressionContext? context = null)
    {
        if (!NumericSearch.IntegerSizes.Contains(options.Bits))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Bits, "サイズは 8 / 16 / 24 / 32 / 48 / 64 bit です。");
        }

        (string minText, string maxText) = Split(text);
        BigInteger? min = minText.Length == 0 ? null : NumericSearch.ParseInteger(minText, context);
        BigInteger? max = maxText.Length == 0 ? null : NumericSearch.ParseInteger(maxText, context);
        if (min is null && max is null)
        {
            throw new PatternException(PatternError.Empty);
        }

        int bits = options.Bits;
        foreach (BigInteger? bound in new[] { min, max })
        {
            if (bound is { } b)
            {
                NumericSearch.CheckRange(b, bits, options.Sign);
            }
        }

        if (min is { } lo && max is { } hi && lo > hi)
        {
            throw new PatternException(PatternError.RangeOrder, text, null,
                [lo.ToString("N0", CultureInfo.CurrentCulture), hi.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        bool[] endians = Endians(options.Endian);
        var matcher = new IntegerRangeMatcher(bits, endians, options.Sign, min, max, exclude);
        string[] labels = IntegerLabels(endians, options.Sign);
        byte[] display = NumericSearch.EncodeInteger(min ?? max ?? 0, bits, endians[0]);
        SearchPattern pattern = SearchPattern.FromMatcher(matcher, display, labels).WithVariantColumn(VariantColumn.Interpretation);
        var info = new NumericSearchInfo(false, bits, options.Sign, FloatFormat.Single, [.. labels.Select((_, i) => endians[i / 3])])
        {
            Range = new NumericRangeInfo(min?.ToString("N0", CultureInfo.CurrentCulture), max?.ToString("N0", CultureInfo.CurrentCulture), exclude),
        };
        return pattern.WithNumeric(info);
    }

    /// <summary>
    /// 浮動小数点の範囲の検索 (FIND-15)。小数点は `.` だけ。NaN はどの範囲にも一致しない (仕様 4。除外のときも)。
    /// </summary>
    public static SearchPattern Float(string text, FloatSearchOptions options, bool exclude = false)
    {
        (string minText, string maxText) = Split(text);
        double? min = minText.Length == 0 ? null : NumericSearch.ParseFloat(minText);
        double? max = maxText.Length == 0 ? null : NumericSearch.ParseFloat(maxText);
        if (min is null && max is null)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (min is double a && double.IsNaN(a) || max is double b && double.IsNaN(b))
        {
            throw new PatternException(PatternError.InvalidNumber, text);
        }

        if (min is { } lo && max is { } hi && lo > hi)
        {
            throw new PatternException(PatternError.RangeOrder, text, null,
                [lo.ToString("G", CultureInfo.CurrentCulture), hi.ToString("G", CultureInfo.CurrentCulture)]);
        }

        bool[] endians = Endians(options.Endian);
        double l = min ?? double.NegativeInfinity;
        double h = max ?? double.PositiveInfinity;
        var matcher = new FloatRangeMatcher(options.Format, endians, l, h, exclude);
        string[] labels = [.. endians.Select(e => e ? "BE" : "LE")];
        byte[] display = NumericSearch.EncodeFloat(double.IsInfinity(l) ? (double.IsInfinity(h) ? 0 : h) : l, options.Format, endians[0]);
        SearchPattern pattern = SearchPattern.FromMatcher(matcher, display, labels);
        var info = new NumericSearchInfo(true, NumericSearch.ByteLength(options.Format) * 8, IntegerSign.Either, options.Format, endians)
        {
            Range = new NumericRangeInfo(min?.ToString("G", CultureInfo.CurrentCulture), max?.ToString("G", CultureInfo.CurrentCulture), exclude),
        };
        return pattern.WithNumeric(info);
    }

    private static (string Min, string Max) Split(string text)
    {
        int at = text.IndexOf(Separator, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new PatternException(PatternError.InvalidNumber, text);
        }

        string min = text[..at].Trim();
        string max = text[(at + Separator.Length)..].Trim();
        if (max.StartsWith('.'))
        {
            // `1...2` のような書き方は誤り。
            throw new PatternException(PatternError.InvalidNumber, text, at + 3);
        }

        return (min, max);
    }

    private static bool[] Endians(SearchEndian endian) => endian switch
    {
        SearchEndian.Little => [false],
        SearchEndian.Big => [true],
        _ => [false, true],
    };

    /// <summary>種類 (エンディアン × 符号の解釈「符号あり」「符号なし」「両方」) の名前。</summary>
    private static string[] IntegerLabels(bool[] endians, IntegerSign sign)
    {
        var labels = new List<string>();
        foreach (bool big in endians)
        {
            string e = big ? "BE" : "LE";
            labels.Add($"{e}, {SearchLabels.Signed}");
            labels.Add($"{e}, {SearchLabels.Unsigned}");
            labels.Add($"{e}, {(sign == IntegerSign.Either ? SearchLabels.SignedAndUnsigned : sign == IntegerSign.Signed ? SearchLabels.Signed : SearchLabels.Unsigned)}");
        }

        return [.. labels];
    }

    /// <summary>整数の範囲の照合 (種類 = エンディアンの番号 × 3 + 解釈 (0 符号あり、1 符号なし、2 両方))。</summary>
    private sealed class IntegerRangeMatcher : ValueMatcher
    {
        private readonly int _bits;
        private readonly bool[] _endians;
        private readonly IntegerSign _sign;
        private readonly Int128 _sMin;
        private readonly Int128 _sMax;
        private readonly Int128 _uMin;
        private readonly Int128 _uMax;
        private readonly bool _exclude;

        public IntegerRangeMatcher(int bits, bool[] endians, IntegerSign sign, BigInteger? min, BigInteger? max, bool exclude)
            : base(bits / 8)
        {
            _bits = bits;
            _endians = endians;
            _sign = sign;
            _exclude = exclude;
            Int128 typeSMin = -(Int128.One << (bits - 1));
            Int128 typeSMax = (Int128.One << (bits - 1)) - 1;
            Int128 typeUMax = (Int128.One << bits) - 1;
            _sMin = min is { } a ? (Int128)a : typeSMin;
            _sMax = max is { } b ? (Int128)b : typeSMax;
            _uMin = min is { } c ? (Int128)c : 0;
            _uMax = max is { } d ? (Int128)d : typeUMax;
        }

        public override int Match(ReadOnlySpan<byte> data)
        {
            int n = _bits / 8;
            for (int e = 0; e < _endians.Length; e++)
            {
                ulong u = 0;
                if (_endians[e])
                {
                    for (int i = 0; i < n; i++)
                    {
                        u = (u << 8) | data[i];
                    }
                }
                else
                {
                    for (int i = n - 1; i >= 0; i--)
                    {
                        u = (u << 8) | data[i];
                    }
                }

                long s = _bits == 64 ? (long)u : (long)(u << (64 - _bits)) >> (64 - _bits);
                bool inS = _sign != IntegerSign.Unsigned && s >= _sMin && s <= _sMax;
                bool inU = _sign != IntegerSign.Signed && u >= _uMin && u <= _uMax;
                if (_exclude)
                {
                    if (!inS && !inU)
                    {
                        return (e * 3) + 2;
                    }

                    continue;
                }

                if (inS || inU)
                {
                    return (e * 3) + (inS && inU ? 2 : inS ? 0 : 1);
                }
            }

            return -1;
        }
    }

    /// <summary>浮動小数点の範囲の照合 (種類 = エンディアンの番号)。NaN は一致しない。</summary>
    private sealed class FloatRangeMatcher(FloatFormat format, bool[] endians, double min, double max, bool exclude)
        : ValueMatcher(NumericSearch.ByteLength(format))
    {
        public override int Match(ReadOnlySpan<byte> data)
        {
            for (int e = 0; e < endians.Length; e++)
            {
                double x = format switch
                {
                    FloatFormat.Half => (double)(endians[e] ? BinaryPrimitives.ReadHalfBigEndian(data) : BinaryPrimitives.ReadHalfLittleEndian(data)),
                    FloatFormat.Single => endians[e] ? BinaryPrimitives.ReadSingleBigEndian(data) : BinaryPrimitives.ReadSingleLittleEndian(data),
                    _ => endians[e] ? BinaryPrimitives.ReadDoubleBigEndian(data) : BinaryPrimitives.ReadDoubleLittleEndian(data),
                };
                if (double.IsNaN(x))
                {
                    continue;
                }

                bool inside = x >= min && x <= max;
                if (inside != exclude)
                {
                    return e;
                }
            }

            return -1;
        }
    }
}

/// <summary>範囲の検索の条件 (検索欄の横の「範囲: 1,000〜2,000」の表示。FIND-15 の画面)。端がなければ null。</summary>
public sealed record NumericRangeInfo(string? Min, string? Max, bool Exclude);
