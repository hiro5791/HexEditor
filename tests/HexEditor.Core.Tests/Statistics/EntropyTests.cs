using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Statistics;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-12 エントロピー・冗長度・圧縮率の推定、ANA-13 エントロピーグラフ、ANA-14 ダイグラム。</summary>
public sealed class EntropyTests
{
    [Fact]
    [Trait(TC, "TC-ANA-12-01")]
    public void ConstantAndAllValues()
    {
        using Document zero = File("TD-ZERO-1M");
        EntropySummary z = StatisticsEngine.Compute(zero.Current, new StatisticsRequest()).Entropy;
        Assert.Equal(0, z.Bits);
        Assert.Equal(0, z.Percent);
        Assert.Equal(100, z.Redundancy);
        Assert.Equal(EntropyVerdict.Repetitive, z.Verdict(ElementType.U8));

        using Document all = File("TD-BYTES-256");
        EntropySummary a = StatisticsEngine.Compute(all.Current, new StatisticsRequest()).Entropy;
        Assert.Equal(8, a.Bits, 12);
        Assert.Equal(100, a.Percent, 9);
        Assert.Equal(0, a.Redundancy, 9);
        Assert.Equal(256, a.MinimumBytes, 9);
    }

    [Fact]
    [Trait(TC, "TC-ANA-12-02")]
    public void CryptographicRandom()
    {
        using Document doc = File("TD-ANA-CRYPT-10M");
        StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
        Assert.True(r.Entropy.Bits >= 7.99);
        Assert.Equal(EntropyVerdict.CompressedOrEncrypted, r.Entropy.Verdict(ElementType.U8));
        CompressionEstimate c = CompressionEstimator.Estimate(doc.Current, []);
        Assert.True(c.Ratio >= 99, $"ratio {c.Ratio}");
        Assert.True(c.IsWhole);
    }

    [Fact]
    [Trait(TC, "TC-ANA-12-03")]
    public void HundredGigabyteSampleIsAtMost64Megabytes()
    {
        var source = Virtual(100 * GiB, (o, s) => s.Clear());
        using var doc = Doc(source);
        CompressionEstimate c = CompressionEstimator.Estimate(doc.Current, []);
        long read = source.Reads.Sum(r => (long)r.Length);
        Assert.True(read <= 64 * MiB, $"read {read}");
        Assert.Equal(64 * MiB, c.SampleBytes);
        Assert.Equal(100 * GiB, c.TotalBytes);
        Assert.Equal("64 MB", StatisticsFormat.Size(c.SampleBytes, CultureInfo.InvariantCulture));
        Assert.Equal("100 GB", StatisticsFormat.Size(c.TotalBytes, CultureInfo.InvariantCulture));
        Assert.True(c.Ratio < 1);
    }

    /// <summary>TD-ANA-HALF-2G の代わり: 前半 1 GiB が 00、後半 1 GiB が AES-CTR の鍵ストリームの仮想のデータソース。</summary>
    [Fact]
    [Trait(TC, "TC-ANA-13-01")]
    public void HalfZeroHalfRandomGraph()
    {
        using var doc = Doc(Virtual(2 * GiB, HalfZeroHalfRandom));
        StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
        EntropyBlocks blocks = r.Blocks!;
        Assert.Equal(2 * MiB, blocks.BlockSize);
        Assert.Equal(1024, blocks.Count);
        string[] lines = blocks.ToCsv(r.Ranges).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1..];
        Assert.Equal(1024, lines.Length);
        double[] values = [.. lines.Select(l => double.Parse(l.Split(',')[1], CultureInfo.InvariantCulture))];
        Assert.All(values[..512], v => Assert.Equal(0, v));
        Assert.All(values[512..], v => Assert.True(v >= 7.99, $"{v}"));
        Assert.Equal("0x40000000", lines[512].Split(',')[0]);
    }

    [Fact]
    public void AutoBlockSizeRule()
    {
        Assert.Equal(16 * KiB, EntropyBlocks.AutoBlockSize(16 * MiB));
        Assert.Equal(256, EntropyBlocks.AutoBlockSize(1000));
        Assert.Equal(256, EntropyBlocks.EffectiveBlockSize(100, 16 * MiB));
        Assert.Equal(64 * KiB, EntropyBlocks.EffectiveBlockSize(65536, 16 * MiB));

        // ブロック数の上限: 100 GB を 256 バイトのブロックにはしない。
        Assert.True(100 * GiB / EntropyBlocks.EffectiveBlockSize(256, 100 * GiB) <= EntropyBlocks.MaxBlocks);
    }

    /// <summary>TC-ANA-13-03 の Core の部分: 表示範囲だけを細かいブロックで計算し、範囲の外は読まない。</summary>
    [Fact]
    public void DetailBlocksAreComputedOnlyForTheVisibleRange()
    {
        var source = Virtual(16 * MiB, (o, s) => TestDataCatalog.Expected("TD-RANDOM-16M", o, s));
        using var doc = Doc(source);
        EntropyCache cache = EntropyCache.For(doc);
        long detail = EntropyGraph.DetailBlockSize(MiB);
        Assert.Equal(1024, detail);
        EntropyBlocks blocks = cache.GetOrCompute(doc.Current, detail, 4 * MiB, 5 * MiB);
        Assert.Equal([(detail, 4 * MiB, MiB)], cache.ComputeLog);
        Assert.Equal(1024, blocks.ComputedCount);
        Assert.True(blocks.Entropy[blocks.BlockOf(4 * MiB)] > 7.5);
        Assert.Equal(BlockState.None, blocks.State[0]);
    }

    [Fact]
    [Trait(TC, "TC-ANA-13-04")]
    public void MinimapReusesTheStatisticsResult()
    {
        var source = Virtual(16 * MiB, (o, s) => TestDataCatalog.Expected("TD-RANDOM-16M", o, s));
        using var doc = Doc(source);

        // 1. 統計パネルでブロックの大きさ 64 KB のグラフを計算する (統計パネルは完了した結果をキャッシュに入れる)。
        StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest { BlockSize = 64 * KiB });
        EntropyCache cache = EntropyCache.For(doc);
        cache.Publish(doc.Current, r.Blocks!);

        // 2〜3. 読み込みの計数を 0 に戻し、ミニマップ (同じキャッシュ) がブロックの大きさ 64 KB のエントロピーを求める。
        source.Reads.Clear();
        cache.ClearLog();
        EntropyBlocks minimap = cache.GetOrCompute(doc.Current, 64 * KiB);
        Assert.Empty(source.Reads);
        Assert.Equal(256, minimap.ComputedCount);

        // 4. 0x10000 の 1 バイトを書き換えると、0x10000〜0x1FFFF のブロックだけを計算し直す。
        doc.Overwrite(0x10000, [0x00]);
        source.Reads.Clear();
        EntropyBlocks again = cache.GetOrCompute(doc.Current, 64 * KiB);
        Assert.Equal([(64 * KiB, 0x10000L, 0x10000L)], cache.ComputeLog);
        Assert.InRange(source.Reads.Sum(x => (long)x.Length), 1, 64 * KiB);
        Assert.Equal(256, again.ComputedCount);
    }

    [Fact]
    public void LengthChangingEditsInvalidateTheBlocksAfterTheEdit()
    {
        using var doc = Doc(new byte[MiB]);
        EntropyCache cache = EntropyCache.For(doc);
        cache.GetOrCompute(doc.Current, 64 * KiB);
        doc.Insert(5 * 64 * KiB + 10, [1, 2, 3]);
        EntropyBlocks? cached = cache.TryGet(64 * KiB);
        Assert.NotNull(cached);
        Assert.Equal(5, cached!.ComputedCount);
    }

    [Fact]
    [Trait(TC, "TC-ANA-14-01")]
    public void DigramCounts()
    {
        StatisticsResult r = StatisticsEngine.Compute(Doc([0x41, 0x42, 0x41, 0x42]).Current, new StatisticsRequest());
        Assert.Equal(2, r.Digram!.Count(0x41, 0x42));
        Assert.Equal(1, r.Digram.Count(0x42, 0x41));
        Assert.Equal(3, r.Digram.Total);
        Assert.Equal((0x41, 0x42, 2L), r.Digram.Top()[0]);

        // 1 バイトだけ: 組がない (「2 バイト以上の範囲を指定してください」と表示する)。
        StatisticsResult one = StatisticsEngine.Compute(Doc([0x41]).Current, new StatisticsRequest());
        Assert.Equal(0, one.Digram!.Total);
        Assert.True(one.Ranges.Length < 2);
    }

    [Fact]
    public void DigramPairsAcrossChunksAndThreads()
    {
        byte[] data = new byte[3 * MiB + 17];
        new Random(5).NextBytes(data);
        StatisticsResult r = StatisticsEngine.Compute(Doc(data).Current, new StatisticsRequest { ChunkSize = (int)MiB });
        long[] expected = new long[65536];
        for (int i = 0; i + 1 < data.Length; i++)
        {
            expected[(data[i] << 8) | data[i + 1]]++;
        }

        Assert.Equal(expected, r.Digram!.Counts);
        long[] positions = r.Positions!.Counts;
        Assert.Equal(data.Length, positions.Sum());
        for (int s = 0; s < PositionDistribution.Sections; s += 97)
        {
            long start = r.Positions.SectionStart(s);
            long end = s + 1 < PositionDistribution.Sections ? r.Positions.SectionStart(s + 1) : data.Length;
            Assert.Equal(data.AsSpan((int)start, (int)(end - start)).Count((byte)7), r.Positions.Count(s, 7));
        }
    }

    [Fact]
    public void BlockEntropyMatchesAFullComputation()
    {
        byte[] data = new byte[MiB + 1000];
        new Random(9).NextBytes(data);
        data.AsSpan(0, 300_000).Clear();
        StatisticsResult r = StatisticsEngine.Compute(Doc(data).Current,
            new StatisticsRequest { BlockSize = 4096, ChunkSize = 100_000, Ranges = [new HashRange(5, data.Length - 5)] });
        EntropyBlocks b = r.Blocks!;
        for (int i = 0; i < b.Count; i += 37)
        {
            long start = b.BlockStart(i) + 5;
            int len = (int)b.BlockLength(i);
            long[] counts = new long[256];
            foreach (byte x in data.AsSpan((int)start, len))
            {
                counts[x]++;
            }

            Assert.Equal(StatMath.Entropy(counts), b.Entropy[i], 4);
        }
    }
}
