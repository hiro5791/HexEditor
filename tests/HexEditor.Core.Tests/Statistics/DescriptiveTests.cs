using System.Buffers.Binary;
using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Statistics;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-11 記述統計。</summary>
public sealed class DescriptiveTests
{
    private static DescriptiveStats Compute(byte[] data, ElementSpec spec) =>
        StatisticsEngine.Compute(Doc(data).Current, new StatisticsRequest { Element = spec, ByteFeatures = false }).Descriptive;

    [Fact]
    [Trait(TC, "TC-ANA-11-01")]
    public void FourBytes()
    {
        DescriptiveStats d = Compute([1, 2, 3, 4], ElementSpec.Bytes);
        Assert.Equal(4, d.Count);
        Assert.Equal((Int128)10, d.IntegerSum);
        Assert.Equal(2.5, d.Mean);
        Assert.Equal(1.25, d.PopulationVariance, 12);
        Assert.Equal("1.666666667", StatisticsFormat.Decimal(d.SampleVariance, CultureInfo.InvariantCulture));
        Assert.Equal(2.5, d.Median);
        Assert.Equal(1.75, d.Q1);
        Assert.Equal(3.25, d.Q3);

        // 1 バイトだけ: 標本分散・歪度・尖度は計算できない (表示は「—」と理由のツールチップ)。
        DescriptiveStats one = Compute([5], ElementSpec.Bytes);
        Assert.True(double.IsNaN(one.SampleVariance));
        Assert.True(double.IsNaN(one.Skewness));
        Assert.True(double.IsNaN(one.Kurtosis));
        Assert.Equal(StatisticsUnavailable.TooFewElements, StatisticsFormat.WhyUnavailable(one, StatisticItem.SampleVariance));
        Assert.Equal(StatisticsUnavailable.TooFewElements, StatisticsFormat.WhyUnavailable(one, StatisticItem.Skewness));
    }

    /// <summary>
    /// TD-ANA-STATS-REF の代わり: 4 つの型 × 3 種類の分布 (一様・正規・指数) の 12 組 (各 4,096 要素) を固定の種で作り、SciPy の
    /// skew(bias=False)・kurtosis(bias=False) と NumPy の var(ddof=0 / 1) と同じ定義の 2 パスの計算 (テストコード) と比べる。
    /// </summary>
    [Theory]
    [Trait(TC, "TC-ANA-11-02")]
    [InlineData(ElementType.U8)]
    [InlineData(ElementType.S16)]
    [InlineData(ElementType.U32)]
    [InlineData(ElementType.F64)]
    public void SkewnessAndKurtosisMatchTheScipyDefinitions(ElementType type)
    {
        foreach (string distribution in new[] { "uniform", "normal", "exponential" })
        {
            double[] values = Sample(type, distribution, 4096, seed: (int)type * 10 + distribution.Length);
            byte[] data = Encode(type, values);
            DescriptiveStats d = Compute(data, new ElementSpec(type));
            (double pop, double sample, double skew, double kurt) = Reference(values);
            Assert.Equal(values.Length, d.Count);
            Assert.True(Math.Abs(d.Skewness - skew) < 1e-6, $"{type} {distribution}: skew {d.Skewness} vs {skew}");
            Assert.True(Math.Abs(d.Kurtosis - kurt) < 1e-6, $"{type} {distribution}: kurtosis {d.Kurtosis} vs {kurt}");
            Assert.True(Math.Abs(d.PopulationVariance - pop) <= Math.Max(1e-6, Math.Abs(pop) * 1e-12), $"{type} {distribution}: var {d.PopulationVariance} vs {pop}");
            Assert.True(Math.Abs(d.SampleVariance - sample) <= Math.Max(1e-6, Math.Abs(sample) * 1e-12));
        }
    }

    /// <summary>平均 100、標準偏差 15 の正規分布・一様分布・指数分布を型に合わせて丸める。</summary>
    private static double[] Sample(ElementType type, string distribution, int n, int seed)
    {
        var random = new Random(seed);
        double[] values = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = distribution switch
            {
                "uniform" => type switch
                {
                    ElementType.U8 => random.Next(256),
                    ElementType.S16 => random.Next(-32768, 32768),
                    ElementType.U32 => (double)(uint)random.NextInt64(0, 1L << 32),
                    _ => random.NextDouble() * 1000,
                },
                "normal" => 100 + (15 * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())),
                _ => -Math.Log(1 - random.NextDouble()) * 100,
            };
            values[i] = type switch
            {
                ElementType.U8 => Math.Clamp(Math.Round(v), 0, 255),
                ElementType.S16 => Math.Clamp(Math.Round(v), -32768, 32767),
                ElementType.U32 => Math.Clamp(Math.Round(v), 0, uint.MaxValue),
                _ => v,
            };
        }

        return values;
    }

    private static byte[] Encode(ElementType type, double[] values)
    {
        int size = ElementTypes.Size(type);
        byte[] data = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> s = data.AsSpan(i * size, size);
            switch (type)
            {
                case ElementType.U8:
                    s[0] = (byte)values[i];
                    break;
                case ElementType.S16:
                    BinaryPrimitives.WriteInt16LittleEndian(s, (short)values[i]);
                    break;
                case ElementType.U32:
                    BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)values[i]);
                    break;
                default:
                    BinaryPrimitives.WriteDoubleLittleEndian(s, values[i]);
                    break;
            }
        }

        return data;
    }

    /// <summary>SciPy・NumPy と同じ定義を 2 パス (平均を求めてから中心モーメント) で計算する。</summary>
    private static (double Pop, double Sample, double Skew, double Kurt) Reference(double[] x)
    {
        double n = x.Length;
        double mean = x.Average();
        double m2 = x.Sum(v => Math.Pow(v - mean, 2)) / n;
        double m3 = x.Sum(v => Math.Pow(v - mean, 3)) / n;
        double m4 = x.Sum(v => Math.Pow(v - mean, 4)) / n;
        double g1 = m3 / Math.Pow(m2, 1.5);
        double skew = g1 * Math.Sqrt(n * (n - 1)) / (n - 2);
        double g2 = (m4 / (m2 * m2)) - 3;
        double kurt = (((n + 1) * g2) + 6) * (n - 1) / ((n - 2) * (n - 3));
        return (m2, m2 * n / (n - 1), skew, kurt);
    }

    /// <summary>
    /// TD-ANA-U32-100M の代わり: 0〜99,999,999 を並べ替えた u32 (LE) を 1 回ずつ並べた仮想のデータソース。並べ替えは
    /// i → (48,271 × i + 12,345) mod 10^8 (10^8 と互いに素な係数なので 1 回ずつになる)。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-11-03")]
    [Trait("Category", "Nightly")]
    public void HundredMillionU32ApproximatesTheMedianAndSkipsTheMode()
    {
        const long N = 100_000_000;
        var source = Virtual(N * 4, (offset, span) =>
        {
            Span<byte> word = stackalloc byte[4];
            for (int i = 0; i < span.Length; i++)
            {
                long p = offset + i;
                long index = p / 4;
                BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)((48_271 * index + 12_345) % N));
                span[i] = word[(int)(p % 4)];
            }
        });
        using var doc = Doc(source);
        DescriptiveStats d = StatisticsEngine.Compute(doc.Current,
            new StatisticsRequest { Element = new ElementSpec(ElementType.U32), ByteFeatures = false }).Descriptive;
        Assert.Equal(N, d.Count);
        Assert.True(d.QuantilesApproximate);
        Assert.InRange(d.Median, 49_999_999.5 * 0.99, 49_999_999.5 * 1.01);
        Assert.InRange(d.Q1, 24_999_999.75 * 0.99, 24_999_999.75 * 1.01);
        Assert.True(d.ModeTooMany);
        Assert.Equal(StatisticsUnavailable.TooManyElements, StatisticsFormat.WhyUnavailable(d, StatisticItem.Mode));
        Assert.StartsWith("≈", StatisticsFormat.Quantile(d, d.Median, CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TC, "TC-ANA-11-04")]
    public void ChiSquarePValueOfUniformRandom()
    {
        byte[] data = new byte[MiB];
        TestDataCatalog.AesCtr(TestDataCatalog.CryptKey, 0, data);
        DescriptiveStats d = Compute(data, ElementSpec.Bytes);
        Assert.InRange(d.ChiSquareP!.Value, 0.01, 0.99);

        // 定義どおり (自由度 255) にテストコードで計算する。p 値は χ² 分布の密度の上側の数値積分。
        long[] counts = new long[256];
        foreach (byte b in data)
        {
            counts[b]++;
        }

        double expected = data.Length / 256.0;
        double chi = counts.Sum(c => (c - expected) * (c - expected) / expected);
        Assert.Equal(chi, d.ChiSquare!.Value, 6);
        double p = UpperTail(chi, 255);
        Assert.True(Math.Abs(d.ChiSquareP.Value - p) <= Math.Abs(p) * 1e-6, $"{d.ChiSquareP} vs {p}");
    }

    /// <summary>χ² 分布 (自由度 k) の上側確率をシンプソン則で積分する (対数で密度を計算する)。</summary>
    private static double UpperTail(double x, int k)
    {
        double half = k / 2.0;
        double logNorm = (half * Math.Log(2)) + LogGammaHalf(k);
        double Density(double t) => t <= 0 ? 0 : Math.Exp(((half - 1) * Math.Log(t)) - (t / 2) - logNorm);
        double end = x + 2000;
        int steps = 2_000_000;
        double h = (end - x) / steps;
        double sum = Density(x) + Density(end);
        for (int i = 1; i < steps; i++)
        {
            sum += Density(x + (i * h)) * (i % 2 == 0 ? 2 : 4);
        }

        return sum * h / 3;
    }

    /// <summary>log Γ(k / 2) を、Γ(1/2) = √π と Γ(a + 1) = a Γ(a) の積で正確に求める。</summary>
    private static double LogGammaHalf(int k)
    {
        double a = k % 2 == 0 ? 1 : 0.5;
        double log = k % 2 == 0 ? 0 : 0.5 * Math.Log(Math.PI);
        while (a < k / 2.0)
        {
            log += Math.Log(a);
            a += 1;
        }

        return log;
    }

    [Fact]
    [Trait(TC, "TC-ANA-11-05")]
    public void IntegerSumsBeyond64Bits()
    {
        byte[] max = [.. Enumerable.Repeat((byte)0xFF, 32)];
        DescriptiveStats u = Compute(max, new ElementSpec(ElementType.U64));
        Assert.Equal("73,786,976,294,838,206,460", StatisticsFormat.Integer(u.IntegerSum!.Value, CultureInfo.InvariantCulture));
        Assert.Equal("18,446,744,073,709,551,615", StatisticsFormat.Mean(u, CultureInfo.InvariantCulture));

        byte[] min = new byte[32];
        for (int i = 0; i < 4; i++)
        {
            min[(i * 8) + 7] = 0x80;
        }

        DescriptiveStats s = Compute(min, new ElementSpec(ElementType.S64));
        Assert.Equal("-36,893,488,147,419,103,232", StatisticsFormat.Integer(s.IntegerSum!.Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ModesAreTheSmallestFiveWithTheRestCounted()
    {
        DescriptiveStats d = Compute([9, 8, 7, 6, 5, 4, 3, 3], ElementSpec.Bytes);
        Assert.Equal([3], d.IntegerModes.Select(m => (int)m));
        DescriptiveStats tied = Compute([9, 8, 7, 6, 5, 4, 3], ElementSpec.Bytes);
        Assert.Equal([3, 4, 5, 6, 7], tied.IntegerModes.Select(m => (int)m));
        Assert.Equal(2, tied.MoreModes);

        byte[] wide = new byte[7 * 4];
        for (int i = 0; i < 7; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(wide.AsSpan(i * 4), (uint)(100 - i));
        }

        DescriptiveStats w = Compute(wide, new ElementSpec(ElementType.U32));
        Assert.Equal([94, 95, 96, 97, 98], w.IntegerModes.Select(m => (int)m));
        Assert.Equal(2, w.MoreModes);
        Assert.Equal(97, w.Median);
    }

    [Fact]
    public void SerialCorrelationAndPi()
    {
        DescriptiveStats rising = Compute([.. Enumerable.Range(0, 200).Select(i => (byte)i)], ElementSpec.Bytes);
        Assert.Equal(1, rising.SerialCorrelation, 9);

        byte[] random = new byte[6 * 100_000];
        TestDataCatalog.AesCtr(TestDataCatalog.CryptKey, 0, random);
        DescriptiveStats d = Compute(random, ElementSpec.Bytes);
        Assert.InRange(d.Pi!.Value, 3.10, 3.18);
        Assert.InRange(d.SerialCorrelation, -0.01, 0.01);
    }
}
