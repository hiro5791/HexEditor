using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Search;

namespace HexEditor.Core.Coloring;

/// <summary>ルールの条件の誤りの種類 (INSP-33 の「エラー」。UI はこれを説明文にする)。</summary>
public enum ColoringRuleErrorKind
{
    Empty,
    InvalidByteValues,
    InvalidExpression,
    InvalidPattern,
    InvalidRegex,
    InvalidNumber,
    InvalidPeriod,
    InvalidRange,
}

/// <summary>条件の構文の誤り。<see cref="Detail"/> は誤りのある部分 (語、式、解析の説明)。</summary>
public sealed class ColoringRuleException(ColoringRuleErrorKind kind, string detail = "") : FormatException($"{kind}: {detail}")
{
    public ColoringRuleErrorKind Kind { get; } = kind;

    public string Detail { get; } = detail;
}

/// <summary>
/// 条件を解釈したルール (INSP-33)。<see cref="Collect"/> で、バイト列の中の一致を取り出す。オフセット範囲の入力式 (<c>sel.start</c> など)
/// は解釈したときの値で固定する (値が変われば解釈し直す)。
/// </summary>
public sealed class CompiledColoringRule
{
    /// <summary>正規表現の一致の最大長 (INSP-33 の仕様 2)。</summary>
    public const int MaxRegexMatch = 256;

    private bool[]? ByteSet { get; init; }
    private SearchPattern? Pattern { get; init; }
    private Regex? RegexPattern { get; init; }
    private Encoding? TextEncoding { get; init; }
    private (long Start, long End)? OffsetRange { get; init; }
    private (BigInteger Min, BigInteger Max)? IntegerRange { get; init; }
    private (double Min, double Max)? FloatRange { get; init; }
    private int NumberSize { get; init; }
    private bool NumberSigned { get; init; }
    private bool NumberIsFloat { get; init; }

    private CompiledColoringRule(ColoringRule rule)
    {
        Rule = rule;
    }

    public ColoringRule Rule { get; }

    /// <summary>一致の最大の長さ (表示範囲の前後に足して読む長さ。INSP-33 の「巨大ファイル・長時間処理」)。</summary>
    public int MaxMatchLength { get; private init; } = 1;

    /// <summary>値で決まるルール (読み込み中・読めないバイトには適用しない。VIEW-17 の仕様 7)。</summary>
    public bool ValueBased => Rule.Kind is not (ColoringConditionKind.OffsetRange or ColoringConditionKind.Period);

    /// <summary>一致の件数をバイト数で数える (バイト値の条件)。</summary>
    public bool CountsBytes => Rule.Kind == ColoringConditionKind.ByteValues;

    /// <summary>適用する範囲 [Start, End)。null はドキュメント全体。</summary>
    public (long Start, long End)? Scope { get; private init; }

    /// <summary>Hex パターン・テキストの条件の検索語 (凡例の「次へ」で検索エンジンを使う。INSP-34 の仕様 3)。</summary>
    public SearchPattern? SearchPattern => Pattern;

    /// <summary>
    /// ルールを解釈する。<paramref name="context"/> はオフセット範囲・適用範囲の入力式に使う (null なら名前を使う式は誤り)。
    /// 誤りなら <see cref="ColoringRuleException"/>。
    /// </summary>
    public static CompiledColoringRule Compile(ColoringRule rule, IExpressionContext? context)
    {
        (long, long)? scope = null;
        if (rule.RangeStart.Trim().Length > 0 || rule.RangeEnd.Trim().Length > 0)
        {
            long s = Evaluate(rule.RangeStart.Trim().Length > 0 ? rule.RangeStart : "0", context);
            long e = Evaluate(rule.RangeEnd.Trim().Length > 0 ? rule.RangeEnd : "end-1", context);
            if (e < s)
            {
                throw new ColoringRuleException(ColoringRuleErrorKind.InvalidRange, $"{rule.RangeStart}..{rule.RangeEnd}");
            }

            scope = (s, e + 1);
        }

        switch (rule.Kind)
        {
            case ColoringConditionKind.ByteValues:
                return new CompiledColoringRule(rule) { ByteSet = ParseByteValues(rule.Pattern), Scope = scope };
            case ColoringConditionKind.OffsetRange:
            {
                long s = Evaluate(rule.Pattern, context);
                long e = Evaluate(rule.EndExpression, context);
                if (e < s)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.InvalidRange, $"{rule.Pattern}..{rule.EndExpression}");
                }

                return new CompiledColoringRule(rule) { OffsetRange = (s, e + 1), Scope = scope };
            }

            case ColoringConditionKind.Period:
                if (rule.Modulus < 1 || rule.PeriodLength < 1 || rule.Remainder < 0 || rule.Remainder >= rule.Modulus)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.InvalidPeriod, $"{rule.Modulus}, {rule.Remainder}, {rule.PeriodLength}");
                }

                return new CompiledColoringRule(rule) { MaxMatchLength = (int)Math.Min(rule.PeriodLength, 1 << 20), Scope = scope };
            case ColoringConditionKind.HexPattern:
            case ColoringConditionKind.Text:
            {
                if (rule.Pattern.Length == 0)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.Empty);
                }

                try
                {
                    SearchPattern pattern = rule.Kind == ColoringConditionKind.HexPattern
                        ? SearchPattern.FromHex(rule.Pattern)
                        : SearchPattern.FromText(rule.Pattern, EncodingOf(rule.CodePage), new TextSearchOptions { CaseSensitive = rule.CaseSensitive });
                    return new CompiledColoringRule(rule) { Pattern = pattern, MaxMatchLength = Math.Max(1, pattern.MaxMatchLength), Scope = scope };
                }
                catch (PatternException ex)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.InvalidPattern, ex.Position is int p ? $"{ex.Error} ({p})" : ex.Error.ToString());
                }
            }

            case ColoringConditionKind.Regex:
            {
                if (rule.Pattern.Length == 0)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.Empty);
                }

                try
                {
                    RegexOptions options = RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    var regex = new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(100));
                    return new CompiledColoringRule(rule)
                    {
                        RegexPattern = regex,
                        TextEncoding = rule.RegexOnText ? EncodingOf(rule.CodePage) : null,
                        MaxMatchLength = MaxRegexMatch,
                        Scope = scope,
                    };
                }
                catch (ArgumentException ex)
                {
                    throw new ColoringRuleException(ColoringRuleErrorKind.InvalidRegex, ex.Message);
                }
            }

            default:
                return CompileNumber(rule, scope);
        }
    }

    private static long Evaluate(string expression, IExpressionContext? context)
    {
        if (expression.Trim().Length == 0)
        {
            throw new ColoringRuleException(ColoringRuleErrorKind.InvalidExpression, expression);
        }

        if (!ExpressionEvaluator.TryEvaluate(expression, context ?? NoContext.Instance, out long value, out _, DefaultRadix.Hexadecimal))
        {
            throw new ColoringRuleException(ColoringRuleErrorKind.InvalidExpression, expression);
        }

        return value;
    }

    /// <summary>文字コード (見つからなければ UTF-8)。</summary>
    public static Encoding EncodingOf(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new UTF8Encoding(false);
        }
    }

    /// <summary>バイト値の並び (<c>00</c>、<c>20-7E</c>、<c>7F, 80-FF</c>) を読む。</summary>
    public static bool[] ParseByteValues(string text)
    {
        var set = new bool[256];
        string[] tokens = text.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new ColoringRuleException(ColoringRuleErrorKind.Empty);
        }

        foreach (string token in tokens)
        {
            string[] parts = token.Split('-');
            if (parts.Length is not (1 or 2) || !TryByte(parts[0], out byte from) || !TryByte(parts[^1], out byte to) || to < from)
            {
                throw new ColoringRuleException(ColoringRuleErrorKind.InvalidByteValues, token);
            }

            for (int v = from; v <= to; v++)
            {
                set[v] = true;
            }
        }

        return set;

        static bool TryByte(string s, out byte value)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                s = s[2..];
            }

            return s.Length is 1 or 2 && byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) || Fail(out value);
        }

        static bool Fail(out byte value)
        {
            value = 0;
            return false;
        }
    }

    private static CompiledColoringRule CompileNumber(ColoringRule rule, (long, long)? scope)
    {
        (int size, bool signed, bool isFloat) = rule.NumberType switch
        {
            "int8" => (1, true, false),
            "uint8" => (1, false, false),
            "int16" => (2, true, false),
            "uint16" => (2, false, false),
            "int32" => (4, true, false),
            "uint32" => (4, false, false),
            "int64" => (8, true, false),
            "uint64" => (8, false, false),
            "float" => (4, true, true),
            "double" => (8, true, true),
            _ => throw new ColoringRuleException(ColoringRuleErrorKind.InvalidNumber, rule.NumberType),
        };
        if (rule.Modulus < 1 || rule.Remainder < 0 || rule.Remainder >= rule.Modulus)
        {
            throw new ColoringRuleException(ColoringRuleErrorKind.InvalidPeriod, $"{rule.Modulus}, {rule.Remainder}");
        }

        string text = rule.Pattern.Trim();
        int dots = text.IndexOf("..", StringComparison.Ordinal);
        string low = dots >= 0 ? text[..dots] : text;
        string high = dots >= 0 ? text[(dots + 2)..] : text;
        if (isFloat)
        {
            if (!double.TryParse(low, NumberStyles.Float, CultureInfo.InvariantCulture, out double min)
                || !double.TryParse(high, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) || max < min)
            {
                throw new ColoringRuleException(ColoringRuleErrorKind.InvalidNumber, text);
            }

            return new CompiledColoringRule(rule)
            {
                FloatRange = (min, max), NumberSize = size, NumberSigned = true, NumberIsFloat = true, MaxMatchLength = size, Scope = scope,
            };
        }

        if (ParseInteger(low) is not { } imin || ParseInteger(high) is not { } imax || imax < imin)
        {
            throw new ColoringRuleException(ColoringRuleErrorKind.InvalidNumber, text);
        }

        return new CompiledColoringRule(rule)
        {
            IntegerRange = (imin, imax), NumberSize = size, NumberSigned = signed, MaxMatchLength = size, Scope = scope,
        };
    }

    /// <summary>整数 (10 進、<c>0x</c>、<c>h</c> で終わる 16 進、負の値)。読めなければ null。</summary>
    private static BigInteger? ParseInteger(string text)
    {
        text = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        bool negative = text.StartsWith('-');
        text = text.TrimStart('-', '+');
        BigInteger value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2
            && BigInteger.TryParse("0" + text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
        }
        else if (text.EndsWith('h') && text.Length > 1 && BigInteger.TryParse("0" + text[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
        {
        }
        else if (!BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }

        return negative ? -value : value;
    }

    /// <summary>
    /// <paramref name="data"/> (ドキュメントの <paramref name="baseOffset"/> から) の中で、開始が [minStart, maxStart) の一致を
    /// <paramref name="output"/> に加える (開始, 長さ)。値で決まる条件では、読めないバイトを含む一致を除く (<paramref name="states"/> が空ならすべて有効)。
    /// 一致は適用範囲 (<see cref="Scope"/>) に収まる部分に切る。
    /// </summary>
    public void Collect(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long baseOffset, long minStart, long maxStart,
        List<(long Start, int Length)> output)
    {
        long lo = Math.Max(minStart, baseOffset);
        long hi = Math.Min(maxStart, baseOffset + data.Length);
        if (Scope is { } scope)
        {
            lo = Math.Max(lo, scope.Start);
            hi = Math.Min(hi, scope.End);
        }

        if (lo >= hi && Rule.Kind is not (ColoringConditionKind.OffsetRange or ColoringConditionKind.Period))
        {
            return;
        }

        int first = output.Count;
        switch (Rule.Kind)
        {
            case ColoringConditionKind.ByteValues:
                CollectBytes(data, states, baseOffset, lo, hi, output);
                break;
            case ColoringConditionKind.OffsetRange:
            {
                (long s, long e) = OffsetRange!.Value;
                long from = Math.Max(s, Math.Max(minStart, baseOffset));
                long to = Math.Min(e, baseOffset + data.Length);
                if (from < to && from < maxStart)
                {
                    output.Add((from, (int)Math.Min(int.MaxValue, to - from)));
                }

                break;
            }

            case ColoringConditionKind.Period:
                CollectPeriod(baseOffset, data.Length, minStart, maxStart, output);
                break;
            case ColoringConditionKind.HexPattern:
            case ColoringConditionKind.Text:
                CollectPattern(data, states, baseOffset, lo, hi, output);
                break;
            case ColoringConditionKind.Regex:
                CollectRegex(data, states, baseOffset, lo, hi, output);
                break;
            default:
                CollectNumbers(data, states, baseOffset, lo, hi, output);
                break;
        }

        if (Scope is { } clip)
        {
            for (int i = output.Count - 1; i >= first; i--)
            {
                (long s, int len) = output[i];
                long e = Math.Min(s + len, clip.End);
                s = Math.Max(s, clip.Start);
                if (e <= s)
                {
                    output.RemoveAt(i);
                }
                else
                {
                    output[i] = (s, (int)(e - s));
                }
            }
        }
    }

    private static bool Valid(ReadOnlySpan<ByteState> states, int from, int length)
    {
        if (states.IsEmpty)
        {
            return true;
        }

        int end = Math.Min(states.Length, from + length);
        for (int i = from; i < end; i++)
        {
            if (states[i] != ByteState.Valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>バイト値: 続けて一致するバイトは 1 つの区間にまとめる。</summary>
    private void CollectBytes(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long baseOffset, long lo, long hi, List<(long, int)> output)
    {
        bool[] set = ByteSet!;
        int runStart = -1;
        int from = (int)(lo - baseOffset);
        int to = (int)(hi - baseOffset);
        for (int i = from; i < to; i++)
        {
            bool hit = set[data[i]] && (states.IsEmpty || states[i] == ByteState.Valid);
            if (hit && runStart < 0)
            {
                runStart = i;
            }
            else if (!hit && runStart >= 0)
            {
                output.Add((baseOffset + runStart, i - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            output.Add((baseOffset + runStart, to - runStart));
        }
    }

    private void CollectPeriod(long baseOffset, int length, long minStart, long maxStart, List<(long, int)> output)
    {
        long m = Rule.Modulus;
        long r = Rule.Remainder;
        long len = Rule.PeriodLength;
        long dataEnd = baseOffset + length;
        long from = Math.Max(minStart, 0);
        long to = Math.Min(maxStart, dataEnd);
        if (len >= m)
        {
            // 区間がつながる: 全体を 1 つの区間にする。
            if (from < to)
            {
                output.Add((from, (int)(Math.Min(dataEnd, to) - from)));
            }

            return;
        }

        // from 以上で p ≡ r (mod m) の最初の p。
        long p = from - ((from - r) % m + m) % m;
        if (p < from)
        {
            p += m;
        }

        // 前から伸びてくる区間 (開始が from より前で from に届くもの)。
        long previous = p - m;
        if (previous >= 0 && previous + len > from)
        {
            output.Add((from, (int)Math.Min(previous + len - from, dataEnd - from)));
        }

        for (; p < to; p += m)
        {
            output.Add((p, (int)Math.Min(len, dataEnd - p)));
        }
    }

    private void CollectPattern(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long baseOffset, long lo, long hi, List<(long, int)> output)
    {
        SearchPattern pattern = Pattern!;
        int from = (int)(lo - baseOffset);
        int limit = (int)(hi - baseOffset);
        while (from < limit)
        {
            int found = pattern.IndexOf(data[from..], baseOffset + from, out int length);
            if (found < 0 || from + found >= limit)
            {
                return;
            }

            int at = from + found;
            if (Valid(states, at, Math.Max(1, length)))
            {
                output.Add((baseOffset + at, Math.Max(1, length)));
            }

            from = at + 1;
        }
    }

    private void CollectRegex(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long baseOffset, long lo, long hi, List<(long, int)> output)
    {
        Regex regex = RegexPattern!;
        string text;
        int[]? map = null;
        if (TextEncoding is { } encoding)
        {
            (text, map) = DecodeWithOffsets(data, encoding);
        }
        else
        {
            text = Encoding.Latin1.GetString(data);
        }

        int dataLength = data.Length;
        int ByteOf(int charIndex) => map is null ? charIndex : charIndex < map.Length ? map[charIndex] : dataLength;
        try
        {
            int startChar = map is null ? (int)(lo - baseOffset) : Math.Max(0, Array.BinarySearch(map, (int)(lo - baseOffset)) is int k && k >= 0 ? k : ~k);
            for (Match m = regex.Match(text, Math.Min(startChar, text.Length)); m.Success; m = m.NextMatch())
            {
                int start = ByteOf(m.Index);
                if (baseOffset + start >= hi)
                {
                    break;
                }

                int length = Math.Min(MaxRegexMatch, ByteOf(m.Index + m.Length) - start);
                if (length > 0 && baseOffset + start >= lo && Valid(states, start, length))
                {
                    output.Add((baseOffset + start, length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 時間切れの範囲は色を付けない (描画を止めない)。
        }
    }

    /// <summary>文字コードで解読し、各文字の始まりのバイトの位置を返す。</summary>
    private static (string Text, int[] Map) DecodeWithOffsets(ReadOnlySpan<byte> data, Encoding encoding)
    {
        Decoder decoder = encoding.GetDecoder();
        var text = new StringBuilder(data.Length);
        var map = new List<int>(data.Length);
        Span<char> chars = stackalloc char[8];
        int sequenceStart = 0;
        for (int i = 0; i < data.Length; i++)
        {
            int produced = decoder.GetChars(data.Slice(i, 1), chars, flush: false);
            for (int c = 0; c < produced; c++)
            {
                text.Append(chars[c]);
                map.Add(sequenceStart);
            }

            if (produced > 0)
            {
                sequenceStart = i + 1;
            }
        }

        return (text.ToString(), [.. map]);
    }

    private void CollectNumbers(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, long baseOffset, long lo, long hi, List<(long, int)> output)
    {
        int size = NumberSize;
        long m = Rule.Modulus;
        long r = Rule.Remainder;
        long p = lo - ((lo - r) % m + m) % m;
        if (p < lo)
        {
            p += m;
        }

        for (; p < hi; p += m)
        {
            int at = (int)(p - baseOffset);
            if (at + size > data.Length || !Valid(states, at, size))
            {
                continue;
            }

            ulong bits = 0;
            ReadOnlySpan<byte> bytes = data.Slice(at, size);
            if (Rule.BigEndian)
            {
                foreach (byte b in bytes)
                {
                    bits = (bits << 8) | b;
                }
            }
            else
            {
                for (int i = size - 1; i >= 0; i--)
                {
                    bits = (bits << 8) | bytes[i];
                }
            }

            bool hit;
            if (NumberIsFloat)
            {
                double value = size == 4 ? BitConverter.Int32BitsToSingle((int)(uint)bits) : BitConverter.Int64BitsToDouble((long)bits);
                (double min, double max) = FloatRange!.Value;
                hit = value >= min && value <= max;
            }
            else
            {
                BigInteger value = NumberSigned ? (BigInteger)(size == 8 ? (long)bits : ((long)(bits << (64 - size * 8))) >> (64 - size * 8)) : bits;
                (BigInteger min, BigInteger max) = IntegerRange!.Value;
                hit = value >= min && value <= max;
            }

            if (hit)
            {
                output.Add((p, size));
            }
        }
    }

    /// <summary>名前を使わない入力式の文脈 (名前を使う式は誤りになる)。</summary>
    private sealed class NoContext : IExpressionContext
    {
        public static readonly NoContext Instance = new();

        public long Cursor => throw new ExpressionException(ExpressionError.NotAvailable, 0, "cur");

        public long Length => throw new ExpressionException(ExpressionError.NotAvailable, 0, "end");

        public long SelectionStart => throw new ExpressionException(ExpressionError.NotAvailable, 0, "sel");

        public long SelectionLength => throw new ExpressionException(ExpressionError.NotAvailable, 0, "sel");

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
