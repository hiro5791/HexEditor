using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Compare.CompareTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-01 (範囲の指定)・ANA-02 単純比較。</summary>
public sealed class SimpleCompareTests
{
    private static readonly CompareOptions Simple = new() { Method = CompareMethod.Simple };

    [Fact]
    [Trait(TC, "TC-ANA-01-03")]
    public void BytesBeyondTheGivenLengthAreNotCompared()
    {
        byte[] left = new byte[0x2000];
        byte[] right = new byte[0x2000];
        right[0x1000] = 0xFF;
        right[0x1FFF] = 0xFF;
        ICompareData l = CompareData.FromBytes(left);
        ICompareData r = CompareData.FromBytes(right);

        using CompareResult first = Diff(new CompareRange(l, 0, 0x1000), new CompareRange(r, 0, 0x1000), Simple);
        Assert.Empty(All(first));
        Assert.Equal(4096, first.Left.Length);
        Assert.Equal(4096, first.Right.Length);
        Assert.Equal(4096, first.ComparedBytes);

        using CompareResult second = Diff(new CompareRange(l, 0, 0x1001), new CompareRange(r, 0, 0x1001), Simple);
        Assert.Equal([new DiffRange(DiffKind.Changed, 0x1000, 1, 0x1000, 1)], All(second));
    }

    [Fact]
    [Trait(TC, "TC-ANA-01-02")]
    public void DifferentStartOffsetsAreComparedSideBySide()
    {
        // TD-ANA-SHIFT200-A (AA が 0x200 バイトの後に TD-ANA-DIFF3-A) と TD-ANA-SHIFT200-B (TD-ANA-DIFF3-A と同じ)。
        byte[] seq = [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];
        byte[] a = [.. Enumerable.Repeat((byte)0xAA, 0x200), .. seq];
        using CompareResult result = Diff(new CompareRange(CompareData.FromBytes(a), 0x200, a.Length - 0x200),
            CompareRange.Whole(CompareData.FromBytes(seq)), Simple);
        Assert.Empty(All(result));
        Assert.Equal(100.00, result.MatchPercent);
    }

    [Fact]
    [Trait(TC, "TC-ANA-02-01")]
    public void OneDifferentByteInOneGibibyteIsReported()
    {
        // TD-MARKERS-1G と TD-ANA-1G-ONEBYTE (0x20000001 の 1 バイトを FF にしたもの) を仮想のデータで作る。
        var left = new FunctionData(GiB, (o, s) => Markers(GiB, o, s));
        var right = new FunctionData(GiB, (o, s) =>
        {
            Markers(GiB, o, s);
            if (o <= 0x20000001 && 0x20000001 < o + s.Length)
            {
                s[(int)(0x20000001 - o)] = 0xFF;
            }
        });

        using CompareResult result = Diff(left, right, Simple);

        Assert.Equal([new DiffRange(DiffKind.Changed, 0x20000001, 1, 0x20000001, 1)], All(result));
        Assert.Equal(1, result.DifferentBytes);
        Assert.Equal(100.00, result.MatchPercent);
        Assert.Equal(CompareState.Completed, result.State);
    }

    [Fact]
    [Trait(TC, "TC-ANA-02-02")]
    public void TheRestOfTheLongerSideIsOneDiff()
    {
        // TD-ANA-LEN100 (TD-BYTES-256 の先頭 100 バイト) と TD-ANA-LEN120 (その後に AA を 20 バイト)。
        byte[] len100 = [.. Enumerable.Range(0, 100).Select(i => (byte)i)];
        byte[] len120 = [.. len100, .. Enumerable.Repeat((byte)0xAA, 20)];

        using CompareResult forward = Diff(len100, len120, Simple);
        Assert.Equal([new DiffRange(DiffKind.Inserted, 100, 0, 100, 20)], All(forward));

        using CompareResult swapped = Diff(len120, len100, Simple);
        Assert.Equal([new DiffRange(DiffKind.Deleted, 100, 20, 100, 0)], All(swapped));
    }

    [Theory]
    [Trait(TC, "TC-ANA-02-03")]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    public void NearDiffsAreMerged(int gap, int expected)
    {
        byte[] left = new byte[64];
        byte[] right = new byte[64];
        right[0x10] = right[0x11] = right[0x15] = right[0x16] = 0xFF;

        using CompareResult result = Diff(left, right, Simple with { MergeGap = gap });

        List<DiffRange> diffs = All(result);
        Assert.Equal(expected, diffs.Count);
        Assert.Equal(expected == 2
            ? [new DiffRange(DiffKind.Changed, 0x10, 2, 0x10, 2), new DiffRange(DiffKind.Changed, 0x15, 2, 0x15, 2)]
            : [new DiffRange(DiffKind.Changed, 0x10, 7, 0x10, 7)], diffs);
        Assert.Equal(4, result.DifferentBytes);
    }

    /// <summary>基準の実装: 仕様 1〜5 を 1 バイトずつのループでそのまま書いたもの。</summary>
    private static (List<DiffRange> Diffs, long Different, long Matched) Reference(byte[] left, long leftStart, byte[] right, long rightStart, int gap, int unit)
    {
        long nL = left.Length - leftStart;
        long nR = right.Length - rightStart;
        long common = Math.Min(nL, nR);
        var raw = new List<(long Start, long End)>();
        long different = 0;
        for (long unitStart = 0; unitStart < common; unitStart += unit)
        {
            long unitEnd = Math.Min(common, unitStart + unit);
            bool differs = false;
            for (long i = unitStart; i < unitEnd; i++)
            {
                if (left[leftStart + i] != right[rightStart + i])
                {
                    differs = true;
                    different++;
                }
            }

            if (differs)
            {
                if (raw.Count > 0 && unitStart - raw[^1].End <= gap)
                {
                    raw[^1] = (raw[^1].Start, unitEnd);
                }
                else
                {
                    raw.Add((unitStart, unitEnd));
                }
            }
        }

        var diffs = raw.Select(r => new DiffRange(DiffKind.Changed, leftStart + r.Start, r.End - r.Start, rightStart + r.Start, r.End - r.Start)).ToList();
        if (nL > common)
        {
            diffs.Add(new DiffRange(DiffKind.Deleted, leftStart + common, nL - common, rightStart + common, 0));
            different += nL - common;
        }
        else if (nR > common)
        {
            diffs.Add(new DiffRange(DiffKind.Inserted, leftStart + common, 0, rightStart + common, nR - common));
            different += nR - common;
        }

        long changedDifferent = different - Math.Abs(nL - nR);
        return (diffs, different, common - changedDifferent);
    }

    private sealed record RandomCase(int Length, int Edits, int LengthChange, int Gap, int Unit, int LeftStart, int RightStart, int Seed);

    private static Arbitrary<RandomCase> Cases() =>
        (from big in Gen.Frequency((9, Gen.Constant(false)), (1, Gen.Constant(true)))
         from length in big ? Gen.Choose((int)MiB - 4096, (int)MiB + 200_000) : Gen.Choose(0, 70_000)
         from edits in Gen.Choose(0, 40)
         from lengthChange in Gen.Choose(-300, 300)
         from gap in Gen.Choose(0, 16)
         from unit in Gen.Elements(1, 2, 4, 8)
         from leftStart in Gen.Choose(0, 20)
         from rightStart in Gen.Choose(0, 20)
         from seed in Gen.Choose(0, int.MaxValue)
         select new RandomCase(length, edits, lengthChange, gap, unit, leftStart, rightStart, seed)).ToArbitrary();

    [Property(MaxTest = 1000)]
    [Trait(TC, "TC-ANA-02-04")]
    public Property RandomDataMatchesTheReferenceImplementation() => Prop.ForAll(Cases(), c =>
    {
        var random = new Random(c.Seed);
        byte[] left = new byte[c.Length];
        random.NextBytes(left);
        byte[] right = new byte[Math.Max(0, c.Length + c.LengthChange)];
        Array.Copy(left, right, Math.Min(left.Length, right.Length));
        random.NextBytes(right.AsSpan(Math.Min(left.Length, right.Length)));
        for (int e = 0; e < c.Edits && right.Length > 0; e++)
        {
            // 1 MiB の境界の前後に差分を置く組を含める。
            int at = e % 3 == 0 && right.Length > MiB ? (int)MiB - 2 + random.Next(4) : random.Next(right.Length);
            int run = Math.Min(right.Length - at, random.Next(1, 12));
            for (int k = 0; k < run; k++)
            {
                right[at + k] ^= (byte)random.Next(1, 256);
            }
        }

        int ls = Math.Min(c.LeftStart, left.Length);
        int rs = Math.Min(c.RightStart, right.Length);
        var options = Simple with { MergeGap = c.Gap, Unit = c.Unit };
        using CompareResult result = Diff(new CompareRange(CompareData.FromBytes(left), ls, left.Length - ls),
            new CompareRange(CompareData.FromBytes(right), rs, right.Length - rs), options);
        (List<DiffRange> expected, long different, long matched) = Reference(left, ls, right, rs, c.Gap, c.Unit);
        Assert.Equal(expected, All(result));
        Assert.Equal(expected.Count, result.DiffCount);
        Assert.Equal(different, result.DifferentBytes);
        Assert.Equal(matched, result.MatchedBytes);
    });

    [Fact]
    [Trait(TC, "TC-ANA-02-07")]
    public void CancellingKeepsTheResultsUpToTheStoppedPosition()
    {
        // TD-ANA-ALT2M-A (すべて 00) と TD-ANA-ALT2M-B (奇数のオフセットが FF)。右の読み込みを遅くし、1 MiB の比較が終わってから中止する。
        const long Length = 2_000_000;
        var left = new FunctionData(Length, (_, s) => s.Clear());
        var right = new FunctionData(Length, Alternating) { OnRead = (_, _) => Thread.Sleep(50) };
        using var cancel = new CancellationTokenSource();
        var result = new CompareResult(CompareMethod.Simple, CompareRange.Whole(left), CompareRange.Whole(right));
        Assert.Throws<OperationCanceledException>(() => DataComparer.Run(Simple, result, cancel.Token, position =>
        {
            if (position >= MiB)
            {
                cancel.Cancel();
            }
        }));

        Assert.Equal(CompareState.Cancelled, result.State);
        Assert.True(result.StoppedAt >= 0x100000, $"0x{result.StoppedAt:X}");
        List<DiffRange> diffs = All(result);
        Assert.Equal(result.StoppedAt / 2, diffs.Count);
        Assert.All(diffs, d => Assert.True(d.LeftOffset < result.StoppedAt && d.LeftOffset % 2 == 1 && d.LeftLength == 1));
        result.Dispose();
    }

    [Fact]
    public void UnreadableRangesAreRecordedAndComparisonContinues()
    {
        byte[] data = new byte[3 * (int)MiB];
        new Random(1).NextBytes(data);
        var left = new FunctionData(data.Length, (o, s) => data.AsSpan((int)o, s.Length).CopyTo(s));
        byte[] other = (byte[])data.Clone();
        other[0x50] ^= 0xFF;
        other[0x2FFFF0] ^= 0xFF;
        var right = new FunctionData(other.Length, (o, s) => other.AsSpan((int)o, s.Length).CopyTo(s));
        right.Bad.Add(new UnreadableRange(MiB - 10, 30, UnreadableReason.IoError));

        using CompareResult result = Diff(left, right, Simple);

        Assert.Equal(
        [
            new DiffRange(DiffKind.Changed, 0x50, 1, 0x50, 1),
            new DiffRange(DiffKind.Unreadable, MiB - 10, 30, MiB - 10, 30),
            new DiffRange(DiffKind.Changed, 0x2FFFF0, 1, 0x2FFFF0, 1),
        ], All(result));
        Assert.Equal(1, result.CountOf(DiffKind.Unreadable));
        Assert.Equal(2, result.DifferentBytes);
    }

    [Fact]
    public void DiffsBeyondTheMemoryLimitAreSpilledToATemporaryFile()
    {
        const int Length = 20_000;
        var left = new FunctionData(Length, (_, s) => s.Clear());
        var right = new FunctionData(Length, Alternating);
        var store = new DiffStore(memoryLimit: 1000);
        using CompareResult result = Diff(CompareRange.Whole(left), CompareRange.Whole(right), Simple, store);

        Assert.Equal(10_000, result.DiffCount);
        Assert.Equal(9_000, store.SpilledCount);
        Assert.NotNull(store.SpillPath);
        Assert.Equal(new DiffRange(DiffKind.Changed, 19_999, 1, 19_999, 1), store[9_999]);
        Assert.Equal(new DiffRange(DiffKind.Changed, 2_001, 1, 2_001, 1), store[1_000]);
        Assert.Equal(5_000, store.FirstStartingAtOrAfter(false, 10_000));
    }

    internal static void Alternating(long offset, Span<byte> s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = ((offset + i) & 1) == 1 ? (byte)0xFF : (byte)0;
        }
    }
}
