using System.Runtime.InteropServices;

namespace HexEditor.Core.Statistics;

/// <summary>記述統計の結果 (ANA-11 の仕様 2)。求められない値は NaN (整数の値は null)。</summary>
public sealed record DescriptiveStats
{
    /// <summary>中央値・四分位数・最頻値を正確に求める要素数の上限 (ANA-11 の仕様 3)。</summary>
    public const long ExactLimit = 16_000_000;

    /// <summary>最頻値の表示の上限 (ANA-11 の仕様 2)。</summary>
    public const int MaxModes = 5;

    public ElementType Type { get; init; }

    /// <summary>数えた要素の数 (NaN・∞ を除く)。</summary>
    public long Count { get; init; }

    /// <summary>整数型の合計 (128 bit で正確)。浮動小数点では null。</summary>
    public Int128? IntegerSum { get; init; }

    /// <summary>浮動小数点の合計 (補正付き加算)。整数型では IntegerSum を double にしたもの。</summary>
    public double Sum { get; init; } = double.NaN;

    public double Min { get; init; } = double.NaN;

    public double Max { get; init; } = double.NaN;

    /// <summary>整数型の最小値・最大値 (正確な値。10 進と 16 進の表示に使う)。</summary>
    public Int128? IntegerMin { get; init; }

    public Int128? IntegerMax { get; init; }

    public double Range => Max - Min;

    public double Mean { get; init; } = double.NaN;

    public double PopulationVariance { get; init; } = double.NaN;

    public double SampleVariance { get; init; } = double.NaN;

    public double PopulationStdDev => Math.Sqrt(PopulationVariance);

    public double SampleStdDev => Math.Sqrt(SampleVariance);

    public double Median { get; init; } = double.NaN;

    public double Q1 { get; init; } = double.NaN;

    public double Q3 { get; init; } = double.NaN;

    /// <summary>中央値・四分位数を近似 (t-digest) で求めた (表示で値の前に ≈ を付ける)。</summary>
    public bool QuantilesApproximate { get; init; }

    /// <summary>最頻値 (小さい順に最大 5 個)。求めていなければ空。</summary>
    public IReadOnlyList<double> Modes { get; init; } = [];

    /// <summary>整数型の最頻値 (正確な値)。</summary>
    public IReadOnlyList<Int128> IntegerModes { get; init; } = [];

    /// <summary>表示しなかった同数の最頻値の数 (「ほか n 個」)。</summary>
    public long MoreModes { get; init; }

    /// <summary>最頻値の件数。</summary>
    public long ModeCount { get; init; }

    /// <summary>要素数が多すぎるため最頻値を求めていない (「—」と表示する)。</summary>
    public bool ModeTooMany { get; init; }

    public double Skewness { get; init; } = double.NaN;

    /// <summary>超過尖度 (正規分布で 0)。</summary>
    public double Kurtosis { get; init; } = double.NaN;

    /// <summary>u8 のみ: 一様分布を仮定したカイ二乗値と p 値。</summary>
    public double? ChiSquare { get; init; }

    public double? ChiSquareP { get; init; }

    /// <summary>隣り合う要素の相関係数 (−1〜1)。</summary>
    public double SerialCorrelation { get; init; } = double.NaN;

    /// <summary>u8 のみ: モンテカルロ法での π の推定値と誤差 (%)。</summary>
    public double? Pi { get; init; }

    public double? PiErrorPercent { get; init; }

    /// <summary>浮動小数点の NaN・+∞・−∞ の数 (要素数に含めない)。</summary>
    public long NaN { get; init; }

    public long PositiveInfinity { get; init; }

    public long NegativeInfinity { get; init; }
}

/// <summary>平均・分散・3 次と 4 次の中心モーメントを 1 回の走査で求める (Welford の方法の拡張)。</summary>
internal struct MomentAccumulator
{
    public long N;
    public double Mean;
    public double M2;
    public double M3;
    public double M4;

    public void Add(double x)
    {
        long n1 = N;
        N++;
        double n = N;
        double delta = x - Mean;
        double deltaN = delta / n;
        double deltaN2 = deltaN * deltaN;
        double term1 = delta * deltaN * n1;
        Mean += deltaN;
        M4 += (term1 * deltaN2 * ((n * n) - (3 * n) + 3)) + (6 * deltaN2 * M2) - (4 * deltaN * M3);
        M3 += (term1 * deltaN * (n - 2)) - (3 * deltaN * M2);
        M2 += term1;
    }

    /// <summary>標本歪度 (調整済み Fisher-Pearson 係数。SciPy の skew(bias=False) と同じ)。</summary>
    public readonly double Skewness()
    {
        if (N < 3 || M2 <= 0)
        {
            return double.NaN;
        }

        double n = N;
        double m2 = M2 / n;
        double m3 = M3 / n;
        double g1 = m3 / Math.Pow(m2, 1.5);
        return g1 * Math.Sqrt(n * (n - 1)) / (n - 2);
    }

    /// <summary>超過尖度 (SciPy の kurtosis(bias=False) と同じ)。</summary>
    public readonly double Kurtosis()
    {
        if (N < 4 || M2 <= 0)
        {
            return double.NaN;
        }

        double n = N;
        double m2 = M2 / n;
        double m4 = M4 / n;
        double g2 = (m4 / (m2 * m2)) - 3;
        return (((n + 1) * g2) + 6) * (n - 1) / ((n - 2) * (n - 3));
    }
}

/// <summary>隣り合う要素の組の和 (系列相関係数を求める)。値は最初の要素を引いて桁落ちを抑える。</summary>
internal struct PairAccumulator
{
    public long N;
    public double Sx;
    public double Sy;
    public double Sxx;
    public double Syy;
    public double Sxy;
    private double _shift;
    private bool _shifted;

    public void Add(double x, double y)
    {
        if (!_shifted)
        {
            _shift = x;
            _shifted = true;
        }

        x -= _shift;
        y -= _shift;
        N++;
        Sx += x;
        Sy += y;
        Sxx += x * x;
        Syy += y * y;
        Sxy += x * y;
    }

    public readonly double Correlation()
    {
        if (N < 2)
        {
            return double.NaN;
        }

        double n = N;
        double den = ((n * Sxx) - (Sx * Sx)) * ((n * Syy) - (Sy * Sy));
        if (den <= 0)
        {
            return double.NaN;
        }

        return Math.Clamp(((n * Sxy) - (Sx * Sy)) / Math.Sqrt(den), -1, 1);
    }
}

/// <summary>記述統計の組み立て。</summary>
internal static class DescriptiveBuilder
{
    /// <summary>
    /// 値ごとの件数 (u8・s8・u16・s16) から正確に求める。<paramref name="pairs"/> は隣り合う要素の組の和、<paramref name="pi"/> は
    /// モンテカルロ法の結果 (u8 のみ)。
    /// </summary>
    public static DescriptiveStats FromValueCounts(ElementType type, long firstValue, long[] counts, PairAccumulator pairs,
        (long Inside, long Total)? pi)
    {
        long n = 0;
        Int128 sum = 0;
        long minIndex = -1;
        long maxIndex = -1;
        for (int i = 0; i < counts.Length; i++)
        {
            long c = counts[i];
            if (c == 0)
            {
                continue;
            }

            n += c;
            sum += (Int128)c * (firstValue + i);
            if (minIndex < 0)
            {
                minIndex = i;
            }

            maxIndex = i;
        }

        if (n == 0)
        {
            return new DescriptiveStats { Type = type, Count = 0, IntegerSum = 0, Sum = 0 };
        }

        double mean = (double)sum / n;
        double m2 = 0;
        double m3 = 0;
        double m4 = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            long c = counts[i];
            if (c == 0)
            {
                continue;
            }

            double d = firstValue + i - mean;
            double d2 = d * d;
            m2 += c * d2;
            m3 += c * d2 * d;
            m4 += c * d2 * d2;
        }

        var moments = new MomentAccumulator { N = n, Mean = mean, M2 = m2, M3 = m3, M4 = m4 };
        double Quantile(double p)
        {
            double h = (n - 1) * p;
            long lo = (long)Math.Floor(h);
            double frac = h - lo;
            double a = firstValue + OrderStatistic(counts, lo);
            double b = frac > 0 ? firstValue + OrderStatistic(counts, Math.Min(n - 1, lo + 1)) : a;
            return a + (frac * (b - a));
        }

        long best = 0;
        foreach (long c in counts)
        {
            best = Math.Max(best, c);
        }

        var modes = new List<Int128>();
        long tied = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == best)
            {
                tied++;
                if (modes.Count < DescriptiveStats.MaxModes)
                {
                    modes.Add(firstValue + i);
                }
            }
        }

        double? chi = null;
        double? chiP = null;
        if (type == ElementType.U8)
        {
            chi = StatMath.ChiSquareUniform(counts, n);
            chiP = StatMath.ChiSquarePValue(chi.Value, 255);
        }

        (double? piValue, double? piError) = PiOf(pi);
        return new DescriptiveStats
        {
            Type = type,
            Count = n,
            IntegerSum = sum,
            Sum = (double)sum,
            IntegerMin = firstValue + minIndex,
            IntegerMax = firstValue + maxIndex,
            Min = firstValue + minIndex,
            Max = firstValue + maxIndex,
            Mean = mean,
            PopulationVariance = m2 / n,
            SampleVariance = n > 1 ? m2 / (n - 1) : double.NaN,
            Median = Quantile(0.5),
            Q1 = Quantile(0.25),
            Q3 = Quantile(0.75),
            Modes = [.. modes.Select(m => (double)m)],
            IntegerModes = modes,
            MoreModes = tied - modes.Count,
            ModeCount = best,
            Skewness = moments.Skewness(),
            Kurtosis = moments.Kurtosis(),
            ChiSquare = chi,
            ChiSquareP = chiP,
            SerialCorrelation = pairs.Correlation(),
            Pi = piValue,
            PiErrorPercent = piError,
        };
    }

    public static (double? Value, double? ErrorPercent) PiOf((long Inside, long Total)? pi)
    {
        if (pi is not { Total: > 0 } p)
        {
            return (null, null);
        }

        double value = 4.0 * p.Inside / p.Total;
        return (value, Math.Abs(value - Math.PI) / Math.PI * 100);
    }

    /// <summary>件数の並びで k 番目 (0 始まり) の要素のビンの番号。</summary>
    private static long OrderStatistic(ReadOnlySpan<long> counts, long k)
    {
        long cumulative = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            cumulative += counts[i];
            if (cumulative > k)
            {
                return i;
            }
        }

        return counts.Length - 1;
    }
}

/// <summary>
/// 32 / 64 bit の整数・浮動小数点の記述統計を 1 回の走査で求める。中央値・四分位数・最頻値のため、要素数が 1,600 万以下なら
/// 全要素を保持して並べ替える (最大 128 MB)。超える場合は t-digest で近似する (ANA-11 の仕様 3)。
/// </summary>
internal sealed class WideDescriptive
{
    private readonly ElementType _type;
    private readonly bool _float;
    private readonly long[]? _integers;
    private readonly double[]? _doubles;
    private int _stored;
    private TDigest? _digest;
    private MomentAccumulator _moments;
    private Int128 _sum;
    private double _floatSum;
    private double _compensation;
    private Int128 _min = Int128.MaxValue;
    private Int128 _max = Int128.MinValue;
    private double _dmin = double.PositiveInfinity;
    private double _dmax = double.NegativeInfinity;

    public WideDescriptive(ElementType type, long expectedElements)
    {
        _type = type;
        _float = ElementTypes.IsFloat(type);
        if (expectedElements <= DescriptiveStats.ExactLimit)
        {
            if (_float)
            {
                _doubles = new double[expectedElements];
            }
            else
            {
                _integers = new long[expectedElements];
            }
        }
        else
        {
            _digest = new TDigest();
        }
    }

    public long NaN { get; private set; }

    public long PositiveInfinity { get; private set; }

    public long NegativeInfinity { get; private set; }

    public PairAccumulator Pairs;

    /// <summary>保持している値 (並べ替える前。2 回目の読み込みなしでヒストグラムを作るのに使う)。全要素を保持していなければ null。</summary>
    public bool HasAllValues => _digest is null;

    public void AddInteger(Int128 value)
    {
        _sum += value;
        if (value < _min)
        {
            _min = value;
        }

        if (value > _max)
        {
            _max = value;
        }

        double d = (double)value;
        _moments.Add(d);
        if (_integers is not null && _stored < _integers.Length)
        {
            // u64 はビット列のまま保持し、並べ替えは符号なしで行う。
            _integers[_stored++] = _type == ElementType.U64 ? (long)(ulong)value : (long)value;
        }
        else
        {
            _digest?.Add(d);
        }
    }

    /// <summary>浮動小数点の値を加える。NaN・∞ は別に数え、false を返す。</summary>
    public bool AddFloat(double value)
    {
        if (double.IsNaN(value))
        {
            NaN++;
            return false;
        }

        if (double.IsPositiveInfinity(value))
        {
            PositiveInfinity++;
            return false;
        }

        if (double.IsNegativeInfinity(value))
        {
            NegativeInfinity++;
            return false;
        }

        // Neumaier の補正付き加算。
        double t = _floatSum + value;
        _compensation += Math.Abs(_floatSum) >= Math.Abs(value) ? (_floatSum - t) + value : (value - t) + _floatSum;
        _floatSum = t;
        _dmin = Math.Min(_dmin, value);
        _dmax = Math.Max(_dmax, value);
        _moments.Add(value);
        if (_doubles is not null && _stored < _doubles.Length)
        {
            _doubles[_stored++] = value;
        }
        else
        {
            _digest?.Add(value);
        }

        return true;
    }

    public double MinValue => _float ? _dmin : (double)_min;

    public double MaxValue => _float ? _dmax : (double)_max;

    public long Count => _moments.N;

    /// <summary>保持している値を順に返す (ヒストグラムを 2 回目の読み込みなしで作る)。</summary>
    public IEnumerable<double> StoredValues()
    {
        for (int i = 0; i < _stored; i++)
        {
            yield return _float ? _doubles![i] : _type == ElementType.U64 ? (ulong)_integers![i] : _integers![i];
        }
    }

    /// <summary>途中経過 (並べ替えをしない。中央値などは NaN)。</summary>
    public DescriptiveStats Partial() => Build(sortValues: false);

    public DescriptiveStats Finish() => Build(sortValues: true);

    private DescriptiveStats Build(bool sortValues)
    {
        long n = _moments.N;
        var result = new DescriptiveStats
        {
            Type = _type,
            Count = n,
            NaN = NaN,
            PositiveInfinity = PositiveInfinity,
            NegativeInfinity = NegativeInfinity,
            SerialCorrelation = Pairs.Correlation(),
        };
        if (n == 0)
        {
            return result with { IntegerSum = _float ? null : 0, Sum = 0 };
        }

        result = result with
        {
            IntegerSum = _float ? null : _sum,
            Sum = _float ? _floatSum + _compensation : (double)_sum,
            IntegerMin = _float ? null : _min,
            IntegerMax = _float ? null : _max,
            Min = MinValue,
            Max = MaxValue,
            Mean = _float ? (_floatSum + _compensation) / n : (double)_sum / n,
            PopulationVariance = _moments.M2 / n,
            SampleVariance = n > 1 ? _moments.M2 / (n - 1) : double.NaN,
            Skewness = _moments.Skewness(),
            Kurtosis = _moments.Kurtosis(),
        };
        if (!sortValues)
        {
            return result;
        }

        if (_digest is not null)
        {
            return result with
            {
                Median = _digest.Quantile(0.5),
                Q1 = _digest.Quantile(0.25),
                Q3 = _digest.Quantile(0.75),
                QuantilesApproximate = true,
                ModeTooMany = true,
            };
        }

        // 全要素を並べ替えて正確に求める。
        double[] sorted = new double[_stored];
        List<Int128> modes = [];
        long best = 0;
        long tied = 0;
        if (_float)
        {
            Array.Copy(_doubles!, sorted, _stored);
            Array.Sort(sorted);
            List<double> fmodes = [];
            (best, tied) = Modes(sorted.AsSpan(), (a, b) => a.Equals(b), v => fmodes.Add(v));
            return WithQuantiles(result, sorted) with { Modes = fmodes, MoreModes = tied - fmodes.Count, ModeCount = best };
        }

        Span<long> values = _integers.AsSpan(0, _stored);
        if (_type == ElementType.U64)
        {
            Span<ulong> unsigned = MemoryMarshal.Cast<long, ulong>(values);
            unsigned.Sort();
            for (int i = 0; i < sorted.Length; i++)
            {
                sorted[i] = unsigned[i];
            }

            (best, tied) = Modes<ulong>(unsigned, (a, b) => a == b, v => modes.Add(v));
        }
        else
        {
            values.Sort();
            for (int i = 0; i < sorted.Length; i++)
            {
                sorted[i] = values[i];
            }

            (best, tied) = Modes<long>(values, (a, b) => a == b, v => modes.Add(v));
        }

        return WithQuantiles(result, sorted) with
        {
            IntegerModes = modes,
            Modes = [.. modes.Select(m => (double)m)],
            MoreModes = tied - modes.Count,
            ModeCount = best,
        };
    }

    private static DescriptiveStats WithQuantiles(DescriptiveStats result, double[] sorted)
    {
        double Quantile(double p)
        {
            double h = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(h);
            double frac = h - lo;
            double a = sorted[lo];
            double b = frac > 0 ? sorted[Math.Min(sorted.Length - 1, lo + 1)] : a;
            return a + (frac * (b - a));
        }

        return result with { Median = Quantile(0.5), Q1 = Quantile(0.25), Q3 = Quantile(0.75) };
    }

    /// <summary>並べ替えた値の連続の長さから最頻値を求める (同数なら小さい順に最大 5 個)。</summary>
    private static (long Best, long Tied) Modes<T>(ReadOnlySpan<T> sorted, Func<T, T, bool> equal, Action<T> add)
    {
        long best = 0;
        int i = 0;
        while (i < sorted.Length)
        {
            int j = i + 1;
            while (j < sorted.Length && equal(sorted[i], sorted[j]))
            {
                j++;
            }

            best = Math.Max(best, j - i);
            i = j;
        }

        long tied = 0;
        i = 0;
        while (i < sorted.Length)
        {
            int j = i + 1;
            while (j < sorted.Length && equal(sorted[i], sorted[j]))
            {
                j++;
            }

            if (j - i == best)
            {
                tied++;
                if (tied <= DescriptiveStats.MaxModes)
                {
                    add(sorted[i]);
                }
            }

            i = j;
        }

        return (best, tied);
    }
}
