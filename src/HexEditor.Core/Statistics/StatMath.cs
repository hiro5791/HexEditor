namespace HexEditor.Core.Statistics;

/// <summary>統計の計算に使う数学関数。</summary>
public static class StatMath
{
    /// <summary>件数からシャノンエントロピー (ビット) を求める: H = −Σ p log2 p。</summary>
    public static double Entropy(ReadOnlySpan<long> counts)
    {
        long total = 0;
        foreach (long c in counts)
        {
            total += c;
        }

        return Entropy(counts, total);
    }

    public static double Entropy(ReadOnlySpan<long> counts, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        double h = 0;
        double t = total;
        foreach (long c in counts)
        {
            if (c > 0)
            {
                double p = c / t;
                h -= p * Math.Log2(p);
            }
        }

        return Math.Max(0, h);
    }

    public static double Entropy(ReadOnlySpan<int> counts, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        double h = 0;
        double t = total;
        foreach (int c in counts)
        {
            if (c > 0)
            {
                double p = c / t;
                h -= p * Math.Log2(p);
            }
        }

        return Math.Max(0, h);
    }

    /// <summary>256 個の値の一様分布を仮定したカイ二乗値 (自由度 255)。</summary>
    public static double ChiSquareUniform(ReadOnlySpan<long> counts, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        double expected = (double)total / counts.Length;
        double chi = 0;
        foreach (long c in counts)
        {
            double d = c - expected;
            chi += d * d / expected;
        }

        return chi;
    }

    public static double ChiSquareUniform(ReadOnlySpan<int> counts, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        double expected = (double)total / counts.Length;
        double chi = 0;
        foreach (int c in counts)
        {
            double d = c - expected;
            chi += d * d / expected;
        }

        return chi;
    }

    /// <summary>カイ二乗分布の上側確率 (p 値) = Q(df / 2, x / 2)。</summary>
    public static double ChiSquarePValue(double chiSquare, int degreesOfFreedom) =>
        chiSquare <= 0 ? 1 : RegularizedGammaQ(degreesOfFreedom / 2.0, chiSquare / 2.0);

    /// <summary>正則化された上側不完全ガンマ関数 Q(a, x) (級数と連分数。Numerical Recipes の gammq と同じ方法)。</summary>
    public static double RegularizedGammaQ(double a, double x)
    {
        if (x <= 0)
        {
            return 1;
        }

        if (x < a + 1)
        {
            return 1 - GammaSeries(a, x);
        }

        return GammaContinuedFraction(a, x);
    }

    /// <summary>ガンマ関数の対数 (Lanczos 近似、g = 7)。</summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }

        x -= 1;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < Lanczos.Length; i++)
        {
            a += Lanczos[i] / (x + i);
        }

        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(a);
    }

    private static readonly double[] Lanczos =
    [
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    ];

    private static double GammaSeries(double a, double x)
    {
        double ap = a;
        double sum = 1 / a;
        double del = sum;
        for (int n = 0; n < 10_000; n++)
        {
            ap += 1;
            del *= x / ap;
            sum += del;
            if (Math.Abs(del) < Math.Abs(sum) * 1e-16)
            {
                break;
            }
        }

        return sum * Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a));
    }

    private static double GammaContinuedFraction(double a, double x)
    {
        const double Tiny = 1e-300;
        double b = x + 1 - a;
        double c = 1 / Tiny;
        double d = 1 / b;
        double h = d;
        for (int i = 1; i < 10_000; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = (an * d) + b;
            if (Math.Abs(d) < Tiny)
            {
                d = Tiny;
            }

            c = b + (an / c);
            if (Math.Abs(c) < Tiny)
            {
                c = Tiny;
            }

            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < 1e-16)
            {
                break;
            }
        }

        return Math.Exp(-x + (a * Math.Log(x)) - LogGamma(a)) * h;
    }

    /// <summary>以上で最小の 2 の累乗 (最小 <paramref name="minimum"/>)。</summary>
    public static long CeilPowerOfTwo(long value, long minimum)
    {
        long p = minimum;
        while (p < value && p < (1L << 62))
        {
            p <<= 1;
        }

        return p;
    }

    /// <summary>印字可能な ASCII (0x20〜0x7E)。</summary>
    public static bool IsPrintable(byte b) => b is >= 0x20 and <= 0x7E;

    /// <summary>空白類 (0x09、0x0A、0x0D)。</summary>
    public static bool IsWhitespace(byte b) => b is 0x09 or 0x0A or 0x0D;
}
