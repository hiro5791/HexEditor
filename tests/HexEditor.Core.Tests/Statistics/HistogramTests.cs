using System.Buffers.Binary;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Statistics.StatisticsTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>ANA-10 統計パネルとヒストグラム (型・ストライド・ビン・特殊な浮動小数点・読み込みエラー・キャンセル)。</summary>
public sealed class HistogramTests
{
    private static StatisticsResult Compute(byte[] data, ElementSpec spec, BinSpec? bins = null) =>
        StatisticsEngine.Compute(Doc(data).Current, new StatisticsRequest { Element = spec, Bins = bins ?? new BinSpec() });

    [Fact]
    [Trait(TC, "TC-ANA-10-01")]
    public void All41MegabyteHistogram()
    {
        using Document doc = File("TD-ANA-41-1M");
        StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
        Histogram h = r.Histogram;
        Assert.Equal(256, h.BinCount);
        Assert.Equal(1_048_576, h.Counts[0x41]);
        Assert.All(Enumerable.Range(0, 256).Where(i => i != 0x41), i => Assert.Equal(0, h.Counts[i]));
        HistogramRow row = StatisticsExport.Rows(h)[0x41];
        Assert.Equal(100, row.Percent);
        Assert.Equal(100, row.Cumulative);
        Assert.Equal(100, ByteClassShares.Percent(r.Classes!.Printable, r.Classes.Total));
        Assert.True(r.Completed);
    }

    [Fact]
    [Trait(TC, "TC-ANA-10-02")]
    public void TypesStrideAndSpecialFloats()
    {
        // 1. u16 LE、ストライド 2。
        StatisticsResult a = Compute([0x01, 0x00, 0x01, 0x00, 0x02, 0x00], new ElementSpec(ElementType.U16), new BinSpec { PerValue = true });
        Assert.Equal(2, a.Histogram.Counts[1]);
        Assert.Equal(1, a.Histogram.Counts[2]);

        // 2. u16 LE、ストライド 4: 4 バイトごとの先頭 2 バイトだけを数える。末尾の 05 は端数。
        StatisticsResult b = Compute([0x01, 0x00, 0xFF, 0xFF, 0x01, 0x00, 0xFF, 0xFF, 0x02, 0x00, 0xFF, 0xFF, 0x05],
            new ElementSpec(ElementType.U16, Stride: 4), new BinSpec { PerValue = true });
        Assert.Equal(2, b.Histogram.Counts[1]);
        Assert.Equal(1, b.Histogram.Counts[2]);
        Assert.Equal(0, b.Histogram.Counts[0xFFFF]);
        Assert.Equal(1, b.Remainder);

        // 3. f32: NaN・+∞・−∞ は別の欄で数え、ビンには 1.0 と 2.0 だけが入る。
        byte[] floats = new byte[20];
        BinaryPrimitives.WriteSingleLittleEndian(floats, 1.0f);
        new byte[] { 0x00, 0x00, 0xC0, 0x7F }.CopyTo(floats, 4);
        new byte[] { 0x00, 0x00, 0x80, 0x7F }.CopyTo(floats, 8);
        new byte[] { 0x00, 0x00, 0x80, 0xFF }.CopyTo(floats, 12);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(16), 2.0f);
        StatisticsResult c = Compute(floats, new ElementSpec(ElementType.F32));
        Assert.Equal(1, c.Histogram.NaN);
        Assert.Equal(1, c.Histogram.PositiveInfinity);
        Assert.Equal(1, c.Histogram.NegativeInfinity);
        Assert.Equal(2, c.Histogram.InBins);
        Assert.Equal(2, c.Descriptive.Count);
    }

    /// <summary>手順 4: 無作為なデータと型・エンディアン・ストライドの組を、1 要素ずつ読んで数える基準の実装と比べる。</summary>
    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-ANA-10-02")]
    public Property RandomDataMatchesTheReference() => Prop.ForAll(Cases(), c =>
    {
        (byte[] data, ElementSpec spec, BinSpec bins) = c;
        StatisticsResult r = Compute(data, spec, bins);
        (long[] counts, long below, long above, long remainder) = Reference(data, spec, r.Histogram);
        return counts.AsSpan().SequenceEqual(r.Histogram.Counts) && below == r.Histogram.Below && above == r.Histogram.Above
            && remainder == r.Remainder;
    });

    private static Arbitrary<(byte[] Data, ElementSpec Spec, BinSpec Bins)> Cases() =>
        (from length in Gen.Choose(0, 4096)
         from seed in Gen.Choose(0, int.MaxValue)
         from type in Gen.Elements(ElementTypes.All.ToArray())
         from big in Gen.Elements(true, false)
         from stride in Gen.Choose(0, 16)
         from count in Gen.Choose(2, 300)
         from manual in Gen.Elements(true, false)
         select Make(length, seed, type, big, stride, count, manual)).ToArbitrary();

    private static (byte[], ElementSpec, BinSpec) Make(int length, int seed, ElementType type, bool big, int stride, int count, bool manual)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        BinSpec bins = manual && !ElementTypes.IsFloat(type) ? new BinSpec { Count = count, Min = -1000, Max = 1000 } : new BinSpec { Count = count };
        return (data, new ElementSpec(type, big, stride), bins);
    }

    /// <summary>基準の実装: 要素を 1 つずつ読み、結果のヒストグラムと同じビンの境界で数える。</summary>
    private static (long[] Counts, long Below, long Above, long Remainder) Reference(byte[] data, ElementSpec spec, Histogram shape)
    {
        long[] counts = new long[shape.BinCount];
        long below = 0;
        long above = 0;
        int size = spec.Size;
        int stride = spec.EffectiveStride;
        long n = 0;
        for (long at = 0; at + size <= data.Length; at += stride)
        {
            n++;
            double v = ElementTypes.ReadDouble(spec.Type, data.AsSpan((int)at, size), spec.BigEndian);
            if (!double.IsFinite(v))
            {
                continue;
            }

            int bin = shape.BinOf(v);
            if (bin < 0)
            {
                below++;
            }
            else if (bin >= shape.BinCount)
            {
                above++;
            }
            else
            {
                counts[bin]++;
            }
        }

        long remainder = Math.Max(0, data.Length - (n * stride));
        return (counts, below, above, remainder);
    }

    [Fact]
    public void AutoRangeUsesTheMinimumAndMaximum()
    {
        byte[] data = new byte[40];
        for (int i = 0; i < 10; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), (i * 100) - 300);
        }

        StatisticsResult r = Compute(data, new ElementSpec(ElementType.S32), new BinSpec { Count = 10 });
        Assert.Equal(-300, r.Histogram.Min);
        Assert.Equal(600, r.Histogram.Max);
        Assert.All(r.Histogram.Counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void ManualRangeCountsValuesOutside()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 5);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 50);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 500);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 5000);
        StatisticsResult r = Compute(data, new ElementSpec(ElementType.U16), new BinSpec { Count = 4, Min = 10, Max = 1000 });
        Assert.Equal(1, r.Histogram.Below);
        Assert.Equal(1, r.Histogram.Above);
        Assert.Equal(2, r.Histogram.InBins);
    }

    [Fact]
    public void SignedBytesAreOrderedFromMinus128()
    {
        StatisticsResult r = Compute([0x80, 0xFF, 0x00, 0x7F], new ElementSpec(ElementType.S8));
        Assert.Equal(-128, r.Histogram.FirstValue);
        Assert.Equal(1, r.Histogram.Counts[0]);
        Assert.Equal(1, r.Histogram.Counts[127]);
        Assert.Equal(1, r.Histogram.Counts[128]);
        Assert.Equal(1, r.Histogram.Counts[255]);
        Assert.Equal(-0.5, r.Descriptive.Mean);
    }

    /// <summary>ANA-10 の「エラー」: 読めなかった範囲を除いて計算を続け、か所数とバイト数を数える。</summary>
    [Fact]
    public void UnreadableRangesAreSkippedAndCounted()
    {
        var source = Virtual(64 * KiB, (_, s) => s.Fill(0x41));
        source.BadRanges.Add(new UnreadableRange(4096, 4096, UnreadableReason.IoError));
        source.BadRanges.Add(new UnreadableRange(32768, 4096, UnreadableReason.IoError));
        using var doc = Doc(source);
        StatisticsResult r = StatisticsEngine.Compute(doc.Current, new StatisticsRequest());
        Assert.Equal(2, r.Unreadable.Count);
        Assert.Equal(8192, r.Unreadable.Bytes);
        Assert.Equal(64 * KiB - 8192, r.Histogram.Counts[0x41]);
        Assert.Equal(BlockState.Unreadable, r.Blocks!.State[r.Blocks.BlockOf(4096)]);
    }

    /// <summary>06 の 0.1: マルチ選択は連結して扱い、重なりは 1 回だけ数える。</summary>
    [Fact]
    public void MultipleRangesAreConcatenatedOnce()
    {
        byte[] data = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];
        StatisticsResult r = StatisticsEngine.Compute(Doc(data).Current,
            new StatisticsRequest { Ranges = [new HashRange(10, 10), new HashRange(15, 10), new HashRange(100, 5)] });
        Assert.Equal(20, r.Elements);
        Assert.Equal(1, r.Histogram.Counts[24]);
        Assert.Equal(0, r.Histogram.Counts[25]);
        Assert.Equal(1, r.Histogram.Counts[104]);
        Assert.Equal(10, r.Ranges.Start);
        Assert.Equal(105, r.Ranges.End);
    }

    /// <summary>ANA-10 の「巨大ファイル・長時間処理」: キャンセルしたらそこまでの結果を残す (途中で中止 (xx% まで))。</summary>
    [Fact]
    public async Task CancellationKeepsThePartialResult()
    {
        var source = new FaultyByteSource(new VirtualByteSource(64 * MiB, VirtualContent.Fill, fill: 7)) { HoldFromOffset = 16 * MiB };
        source.Hold.Reset();
        using var doc = new Document(source, Options());
        var center = new OperationCenter();
        StatisticsResult? partial = null;
        Task run = center.RunAsync("stats", OperationKind.ReadOnly, doc, null, op =>
        {
            try
            {
                StatisticsEngine.Compute(doc.Current, new StatisticsRequest(), op);
            }
            catch (StatisticsCancelledException e)
            {
                partial = e.Partial;
                throw;
            }

            return Task.CompletedTask;
        });
        LongRunningOperation op = await WaitForAsync(center);
        while (op.ProcessedBytes < 8 * MiB)
        {
            await Task.Yield();
        }

        op.Cancel();
        source.Hold.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.NotNull(partial);
        Assert.False(partial!.Completed);
        Assert.InRange(partial.Fraction, 0.01, 0.99);
        Assert.Equal(partial.Histogram.Counts[7], partial.Histogram.InBins);
        Assert.True(partial.Histogram.Counts[7] >= 8 * MiB);
    }

    private static async Task<LongRunningOperation> WaitForAsync(OperationCenter center)
    {
        while (center.Active.Count == 0)
        {
            await Task.Yield();
        }

        return center.Active[0];
    }

    [Fact]
    public void JsonAndCsvExportsCarryTheCounts()
    {
        StatisticsResult r = Compute("AAB"u8.ToArray(), ElementSpec.Bytes);
        string csv = StatisticsExport.Csv(r);
        Assert.Contains("65,65,2,66.666667,66.666667", csv);
        System.Text.Json.Nodes.JsonNode json = System.Text.Json.Nodes.JsonNode.Parse(StatisticsExport.Json(r))!;
        Assert.Equal(2, json["bins"]![0x41]!["count"]!.GetValue<long>());
        Assert.Equal(256, json["bins"]!.AsArray().Count);
    }

    /// <summary>TC-ANA-10-03 の Core の部分: その値の次の出現位置を探す。</summary>
    [Fact]
    public void NextOccurrenceOfAValue()
    {
        byte[] data = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            data[i] = (byte)i;
        }

        using var doc = Doc(data);
        var ranges = new LogicalRanges([new HashRange(0, 256)]);
        Assert.Equal(0x7F, StatisticsNavigator.FindNext(doc.Current, ranges, ElementSpec.Bytes, 0, v => v == 0x7F));
        Assert.Null(StatisticsNavigator.FindNext(doc.Current, ranges, ElementSpec.Bytes, 0x80, v => v == 0x7F));
    }
}
