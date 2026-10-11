using HexEditor.Core.Engine;
using HexEditor.Core.Statistics;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-15 パターン統計 (よく現れるバイト列と繰り返しの周期)。</summary>
public sealed class PatternTests
{
    [Fact]
    [Trait(TC, "TC-ANA-15-01")]
    public void DeadBeefIsTheMostFrequentFourByteSequence()
    {
        using Document doc = File("TD-ANA-DEADBEEF");
        PatternResult r = PatternStatistics.FindFrequent(doc.Current, new PatternRequest { Length = 4, Top = 100, MinCount = 2 });
        Assert.False(r.TwoPass);
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], r.Rows[0].Bytes);
        Assert.Equal(1000, r.Rows[0].Count);
        Assert.Equal(0, r.Rows[0].FirstOffset);
    }

    [Fact]
    public void OptionsExcludeUniformAndApplyAlignment()
    {
        byte[] data = new byte[4096];
        for (int i = 0; i < 4096; i += 8)
        {
            new byte[] { 1, 2, 3, 4 }.CopyTo(data, i + 3);
        }

        using var doc = Doc(data);
        PatternResult r = PatternStatistics.FindFrequent(doc.Current, new PatternRequest { Length = 4, Top = 10 });
        Assert.DoesNotContain(r.Rows, row => row.Bytes.All(b => b == 0));
        NGramCount row = Assert.Single(r.Rows, x => x.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(512, row.Count);
        Assert.Equal(3, row.FirstOffset);

        // 位置の条件 offset mod 8 = 0 では 01 02 03 04 (位置 3 mod 8) を数えない。
        PatternResult aligned = PatternStatistics.FindFrequent(doc.Current,
            new PatternRequest { Length = 4, Top = 10, AlignModulus = 8, AlignRemainder = 0, ExcludeUniform = false });
        Assert.DoesNotContain(aligned.Rows, row => row.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal([0, 0, 0, 1], Assert.Single(aligned.Rows).Bytes);
    }

    [Fact]
    public void TwoPassGivesTheSameCountsAsExact()
    {
        byte[] data = Zipf(2 * (int)MiB, seed: 3);
        using var doc = Doc(data);
        PatternResult exact = PatternStatistics.FindFrequent(doc.Current, new PatternRequest { Length = 4, Top = 50 });
        PatternResult twoPass = PatternStatistics.FindFrequent(doc.Current, new PatternRequest { Length = 4, Top = 50, MemoryLimit = 64 * KiB });
        Assert.False(exact.TwoPass);
        Assert.True(twoPass.TwoPass);
        Assert.Equal(exact.Rows.Select(r => (Convert.ToHexString(r.Bytes), r.Count, r.FirstOffset)),
            twoPass.Rows.Select(r => (Convert.ToHexString(r.Bytes), r.Count, r.FirstOffset)));
    }

    /// <summary>4 バイトの語 65,536 個の語彙から Zipf 分布 (指数 1.1) で語を選んで並べる (TD-ANA-NGRAM-1G の作り方)。</summary>
    private static byte[] Zipf(int length, int seed)
    {
        var generator = new ZipfWords(seed);
        byte[] data = new byte[length];
        generator.Fill(0, data);
        return data;
    }

    /// <summary>TD-ANA-NGRAM-1G の内容を位置から計算する (語の番号の乱数は位置から決める)。</summary>
    private sealed class ZipfWords
    {
        private readonly uint[] _vocabulary = new uint[65536];
        private readonly double[] _cdf = new double[65536];
        private readonly ulong _seed;

        public ZipfWords(int seed)
        {
            _seed = (ulong)seed;
            byte[] words = new byte[65536 * 4];
            TestDataCatalog.Random(0x2149 + _seed, 0, words);
            double sum = 0;
            for (int i = 0; i < 65536; i++)
            {
                _vocabulary[i] = BitConverter.ToUInt32(words, i * 4);
                sum += Math.Pow(i + 1, -1.1);
                _cdf[i] = sum;
            }

            for (int i = 0; i < 65536; i++)
            {
                _cdf[i] /= sum;
            }
        }

        public void Fill(long offset, Span<byte> destination)
        {
            Span<byte> r = stackalloc byte[8];
            Span<byte> word = stackalloc byte[4];
            long cached = -1;
            for (int i = 0; i < destination.Length; i++)
            {
                long p = offset + i;
                long index = p / 4;
                if (index != cached)
                {
                    cached = index;
                    TestDataCatalog.Random(_seed * 0x9E37 + 1, index * 8, r);
                    double u = BitConverter.ToUInt64(r) / (double)ulong.MaxValue;
                    int k = Array.BinarySearch(_cdf, u);
                    k = k < 0 ? Math.Min(65535, ~k) : k;
                    BitConverter.TryWriteBytes(word, _vocabulary[k]);
                }

                destination[i] = word[(int)(p % 4)];
            }
        }
    }

    /// <summary>
    /// TD-ANA-NGRAM-1G の代わり: 同じ作り方の 1 GiB の仮想のデータソース。上位 100 件の件数は、結果の列だけを数える別の方法 (2 回目の
    /// 全体の走査) で数え直して比べる。作業用メモリは 256 MB 以下。
    /// </summary>
    [Fact]
    [Trait(TC, "TC-ANA-15-02")]
    [Trait("Category", "Nightly")]
    public void GigabyteStaysWithinTheWorkingMemoryAndCountsExactly()
    {
        var words = new ZipfWords(7);
        var source = Virtual(GiB, words.Fill);
        using var doc = Doc(source);
        long peak = 0;
        using var sampler = new Timer(_ => peak = Math.Max(peak, PatternStatistics.CurrentWorkingBytes), null, 0, 100);
        PatternResult r = PatternStatistics.FindFrequent(doc.Current, new PatternRequest { Length = 4, Top = 100 });
        peak = Math.Max(peak, r.PeakWorkingBytes);
        Assert.True(r.TwoPass);
        Assert.True(peak <= 256L * 1024 * 1024, $"peak {peak}");
        Assert.Equal(100, r.Rows.Count);

        // 別の方法: 結果のバイト列だけを数える表で全体を数え直す。
        var counts = r.Rows.ToDictionary(row => BitConverter.ToUInt32(row.Bytes.Reverse().ToArray()), _ => 0L);
        byte[] buffer = new byte[4 * MiB + 3];
        uint key = 0;
        long seen = 0;
        for (long at = 0; at < GiB; at += 4 * MiB)
        {
            int n = (int)Math.Min(4 * MiB, GiB - at);
            words.Fill(at, buffer.AsSpan(0, n));
            for (int i = 0; i < n; i++)
            {
                key = (key << 8) | buffer[i];
                if (++seen >= 4 && counts.TryGetValue(key, out long c))
                {
                    counts[key] = c + 1;
                }
            }
        }

        Assert.All(r.Rows, row => Assert.Equal(counts[BitConverter.ToUInt32(row.Bytes.Reverse().ToArray())], row.Count));
    }

    [Fact]
    [Trait(TC, "TC-ANA-15-03")]
    public void FixedRecordsGivePeriod64()
    {
        using Document doc = File("TD-ANA-REC64");
        IReadOnlyList<PeriodCandidate> periods = PatternStatistics.EstimatePeriods(doc.Current, []);
        Assert.Equal(64, periods[0].Period);
        Assert.Equal(10, periods.Count);
    }

    [Fact]
    public void DivisorsWithTheSameRatioRankFirst()
    {
        IReadOnlyList<PeriodCandidate> ranked = PatternStatistics.RankPeriods(
            [new(128, 0.301), new(64, 0.299), new(3, 0.1), new(192, 0.2995)], 3);
        Assert.Equal([64, 128, 192], ranked.Select(p => p.Period));
    }
}
