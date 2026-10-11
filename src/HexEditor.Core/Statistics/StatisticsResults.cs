namespace HexEditor.Core.Statistics;

/// <summary>ヒストグラム (ANA-10 の仕様 3〜6)。</summary>
public sealed class Histogram
{
    internal Histogram(ElementType type, long[] counts, bool valueBins, long firstValue, double min, double max)
    {
        Type = type;
        Counts = counts;
        ValueBins = valueBins;
        FirstValue = firstValue;
        Min = min;
        Max = max;
    }

    public ElementType Type { get; }

    /// <summary>ビンごとの件数。</summary>
    public long[] Counts { get; }

    public int BinCount => Counts.Length;

    /// <summary>ビンが値 1 つずつ (u8・s8、u16・s16 の「値ごと」)。値は <see cref="FirstValue"/> + 番号。</summary>
    public bool ValueBins { get; }

    public long FirstValue { get; }

    /// <summary>範囲のビンの範囲 (最小値〜最大値)。値ごとのビンでは最初と最後の値。</summary>
    public double Min { get; }

    public double Max { get; }

    /// <summary>範囲より小さい・大きい値の数。</summary>
    public long Below { get; internal set; }

    public long Above { get; internal set; }

    /// <summary>浮動小数点の NaN・+∞・−∞ の数 (ビンに入れない。ANA-10 の仕様 4)。</summary>
    public long NaN { get; internal set; }

    public long PositiveInfinity { get; internal set; }

    public long NegativeInfinity { get; internal set; }

    /// <summary>ビンに入った要素の数。</summary>
    public long InBins
    {
        get
        {
            long n = 0;
            foreach (long c in Counts)
            {
                n += c;
            }

            return n;
        }
    }

    /// <summary>割合の分母 (ビンと範囲外の要素の数。NaN・∞ を除く)。</summary>
    public long Total => InBins + Below + Above;

    public double Width => ValueBins ? 1 : (Max - Min) / BinCount;

    /// <summary>ビンの下端 (値ごとのビンでは値)。</summary>
    public double BinLow(int i) => ValueBins ? FirstValue + i : Min + (i * Width);

    /// <summary>ビンの上端 (最後のビンは最大値を含む)。</summary>
    public double BinHigh(int i) => ValueBins ? FirstValue + i : i == BinCount - 1 ? Max : Min + ((i + 1) * Width);

    /// <summary>値の入るビン。範囲外なら −1 (小さい) または BinCount (大きい)。</summary>
    public int BinOf(double value)
    {
        if (ValueBins)
        {
            double i = value - FirstValue;
            return i < 0 ? -1 : i >= BinCount ? BinCount : (int)i;
        }

        if (value < Min)
        {
            return -1;
        }

        if (value > Max)
        {
            return BinCount;
        }

        if (Max <= Min)
        {
            return 0;
        }

        int bin = (int)((value - Min) / (Max - Min) * BinCount);
        return Math.Clamp(bin, 0, BinCount - 1);
    }

    internal Histogram Clone() => new(Type, (long[])Counts.Clone(), ValueBins, FirstValue, Min, Max)
    {
        Below = Below,
        Above = Above,
        NaN = NaN,
        PositiveInfinity = PositiveInfinity,
        NegativeInfinity = NegativeInfinity,
    };
}

/// <summary>u8 の分類ごとの割合 (ANA-10 の仕様 7)。</summary>
public sealed record ByteClassShares(long Zero, long Ff, long Printable, long Whitespace, long OtherControl, long High, long Total)
{
    public static ByteClassShares From(ReadOnlySpan<long> counts)
    {
        long printable = 0;
        long whitespace = 0;
        long control = 0;
        long high = 0;
        long total = 0;
        for (int b = 0; b < 256; b++)
        {
            long c = counts[b];
            total += c;
            if (StatMath.IsPrintable((byte)b))
            {
                printable += c;
            }
            else if (StatMath.IsWhitespace((byte)b))
            {
                whitespace += c;
            }
            else if (b is > 0 and < 0x20 or 0x7F)
            {
                control += c;
            }

            if (b >= 0x80)
            {
                high += c;
            }
        }

        return new ByteClassShares(counts[0], counts[0xFF], printable, whitespace, control, high, total);
    }

    public static double Percent(long count, long total) => total == 0 ? 0 : count * 100.0 / total;
}

/// <summary>エントロピーの判定の目安 (ANA-12 の仕様 4)。</summary>
public enum EntropyVerdict
{
    Repetitive,
    TextOrStructured,
    Binary,
    CompressedOrEncrypted,
}

/// <summary>エントロピー・冗長度・理論上の最小サイズ (ANA-12 の仕様 1〜2)。</summary>
public sealed record EntropySummary(double Bits, int BinCount, long Elements, int ElementSize)
{
    /// <summary>エントロピー (%) = H / log2(ビンの数) × 100。</summary>
    public double Percent => BinCount > 1 ? Math.Clamp(Bits / Math.Log2(BinCount) * 100, 0, 100) : 0;

    /// <summary>冗長度 (%) = 1 − H / log2(ビンの数)。</summary>
    public double Redundancy => BinCount > 1 ? 100 - Percent : 100;

    /// <summary>理論上の最小サイズ (バイト) = H × 要素数 / 8。</summary>
    public double MinimumBytes => Bits * Elements / 8;

    /// <summary>判定の目安 (u8 のときだけ)。</summary>
    public EntropyVerdict? Verdict(ElementType type) => type != ElementType.U8 || Elements == 0 ? null : Bits switch
    {
        < 1 => EntropyVerdict.Repetitive,
        < 5 => EntropyVerdict.TextOrStructured,
        < 7.2 => EntropyVerdict.Binary,
        _ => EntropyVerdict.CompressedOrEncrypted,
    };
}

/// <summary>位置ごとのバイト分布 (ANA-14 の仕様 2): 対象を 512 区間に分けた、区間ごとの各バイト値の件数。</summary>
public sealed class PositionDistribution
{
    public const int Sections = 512;

    internal PositionDistribution(long length, long[] counts)
    {
        Length = length;
        Counts = counts;
    }

    /// <summary>対象の長さ (論理)。</summary>
    public long Length { get; }

    /// <summary>[区間 × 256 + バイト値] の件数。</summary>
    public long[] Counts { get; }

    public long Count(int section, int value) => Counts[(section * 256) + value];

    /// <summary>区間の先頭の論理位置。</summary>
    public long SectionStart(int section) => (long)(((Int128)section * Length + Sections - 1) / Sections);

    /// <summary>論理位置の区間。</summary>
    public static int SectionOf(long position, long length) =>
        length <= 0 ? 0 : (int)Math.Min(Sections - 1, (long)((Int128)position * Sections / length));

    public long SectionTotal(int section)
    {
        long n = 0;
        for (int v = 0; v < 256; v++)
        {
            n += Counts[(section * 256) + v];
        }

        return n;
    }

    internal PositionDistribution Clone() => new(Length, (long[])Counts.Clone());
}

/// <summary>ダイグラム (ANA-14 の仕様 1): (b[i], b[i+1]) の組の件数。</summary>
public sealed class Digram
{
    internal Digram(long[] counts) => Counts = counts;

    /// <summary>[最初のバイト × 256 + 次のバイト] の件数。</summary>
    public long[] Counts { get; }

    public long Count(int first, int second) => Counts[(first << 8) | second];

    public long Total
    {
        get
        {
            long n = 0;
            foreach (long c in Counts)
            {
                n += c;
            }

            return n;
        }
    }

    /// <summary>件数の多い組 (上位 <paramref name="limit"/> 件、件数順。ANA-14 の仕様 6)。</summary>
    public IReadOnlyList<(int First, int Second, long Count)> Top(int limit = 1000) =>
        [.. Enumerable.Range(0, 65536).Where(i => Counts[i] > 0).OrderByDescending(i => Counts[i]).ThenBy(i => i)
            .Take(limit).Select(i => (i >> 8, i & 0xFF, Counts[i]))];

    internal Digram Clone() => new((long[])Counts.Clone());
}

/// <summary>統計の計算結果 (統計パネルの各タブの元データ)。</summary>
public sealed class StatisticsResult
{
    public required LogicalRanges Ranges { get; init; }

    public required ElementSpec Element { get; init; }

    public required Histogram Histogram { get; init; }

    public required DescriptiveStats Descriptive { get; init; }

    public required EntropySummary Entropy { get; init; }

    /// <summary>u8 のときだけ。</summary>
    public ByteClassShares? Classes { get; init; }

    public EntropyBlocks? Blocks { get; init; }

    public Digram? Digram { get; init; }

    public PositionDistribution? Positions { get; init; }

    /// <summary>数えた要素の数 (読めなかった要素を除く。NaN・∞ を含む)。</summary>
    public long Elements { get; init; }

    /// <summary>対象の末尾の要素に満たないバイト数 (端数)。</summary>
    public long Remainder { get; init; }

    public required UnreadableSummary Unreadable { get; init; }

    /// <summary>最後まで計算した (false なら途中経過か、キャンセルした)。</summary>
    public bool Completed { get; init; }

    /// <summary>計算した割合 (0〜1)。</summary>
    public double Fraction { get; init; }

    public long BytesRead { get; init; }
}

/// <summary>キャンセルされた: そこまでの結果を持つ (ANA-10: 途中で中止 (xx% まで) と表示して残す)。</summary>
public sealed class StatisticsCancelledException(StatisticsResult partial, CancellationToken token)
    : OperationCanceledException("統計の計算をキャンセルしました。", token)
{
    public StatisticsResult Partial { get; } = partial;
}
