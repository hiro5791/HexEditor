using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using HexEditor.Core.Compare;
using static HexEditor.Core.Tests.Compare.CompareTestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Compare;

/// <summary>ANA-03 挿入・削除を考慮した比較。</summary>
public sealed class InsertDeleteCompareTests
{
    private const ulong RandSeed = 0xA0A0_1001;
    private const ulong PayloadSeed = 0xA0A0_2002;
    private const long Middle = 0x20000000;

    private static readonly CompareOptions InsertDelete = new() { Method = CompareMethod.InsertDelete };

    private static Action<long, Span<byte>> Rand(ulong seed) => (o, s) => Random(seed, o, s);

    [Fact]
    [Trait(TC, "TC-ANA-03-01")]
    public void OneInsertedByteAndAThousandDeletedBytesAreSingleDiffs()
    {
        // TD-ANA-RAND-1G と、その中央に 1 バイト挿入したもの (TD-ANA-RAND-1G-INS1) / 1,000 バイト削除したもの (TD-ANA-RAND-1G-DEL1000)。
        Span<byte> around = stackalloc byte[2];
        Random(RandSeed, Middle - 1, around);
        byte value = around[0] == 0x5A || around[1] == 0x5A ? (byte)0xA5 : (byte)0x5A;
        var left = new FunctionData(GiB, Rand(RandSeed));
        var inserted = new FunctionData(GiB + 1, Edited(Rand(RandSeed), Middle, [value], 0));
        var deleted = new FunctionData(GiB - 1000, Edited(Rand(RandSeed), Middle, [], 1000));

        using CompareResult insert = Diff(left, inserted, InsertDelete);
        Assert.Equal([new DiffRange(DiffKind.Inserted, Middle, 0, Middle, 1)], All(insert));
        Assert.Equal(GiB, insert.MatchedBytes);

        using CompareResult delete = Diff(left, deleted, InsertDelete);
        Assert.Equal([new DiffRange(DiffKind.Deleted, Middle, 1000, Middle, 0)], All(delete));
    }

    [Fact]
    [Trait(TC, "TC-ANA-03-02")]
    public void AnInsertionLongerThanTheWindowIsResynchronizedAfterwards()
    {
        // TD-ANA-RAND-1G-INS100K: 中央に別の種の乱数 102,400 バイトを挿入したもの (W = 64 KB、M = 8)。
        byte[] payload = new byte[102_400];
        Random(PayloadSeed, 0, payload);
        var left = new FunctionData(GiB, Rand(RandSeed));
        var right = new FunctionData(GiB + payload.Length, Edited(Rand(RandSeed), Middle, payload, 0));

        using CompareResult result = Diff(left, right, InsertDelete);

        List<DiffRange> diffs = All(result);
        Assert.NotEmpty(diffs);
        Assert.True(diffs.Count <= 2, string.Join(", ", diffs));
        Assert.Equal(Middle, diffs[0].RightOffset);
        Assert.Equal(Middle + payload.Length, diffs[^1].RightEnd);
        Assert.Equal(Middle, diffs[^1].LeftEnd);
        Assert.Equal(DiffKind.Inserted, diffs.Count == 1 ? diffs[0].Kind : diffs[0].Kind);
        Assert.True(diffs.Sum(d => d.LeftLength) <= 64, "再同期点の直前の短い変更だけが左の長さを持つ");
    }

    [Theory]
    [Trait(TC, "TC-ANA-03-03")]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    public void MatchesShorterThanTheMinimumAreNotResyncPoints(int sameLength, int expectedChanges)
    {
        var random = new Random(3303);
        byte[] Bytes(int n)
        {
            byte[] b = new byte[n];
            random.NextBytes(b);
            return b;
        }

        byte[] c1 = Bytes(4096);
        byte[] c2 = Bytes(4096);
        // 互いに一致しない 32 バイトの部分 (左は偶数、右は奇数の値だけにして、偶然の一致をなくす)。
        byte[] x1 = [.. Bytes(32).Select(b => (byte)(b & 0xFE))];
        byte[] x2 = [.. Bytes(32).Select(b => (byte)(b & 0xFE))];
        byte[] y1 = [.. Bytes(32).Select(b => (byte)(b | 1))];
        byte[] y2 = [.. Bytes(32).Select(b => (byte)(b | 1))];
        byte[] s = [.. new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }.Take(sameLength)];
        byte[] left = [.. c1, .. x1, .. s, .. x2, .. c2];
        byte[] right = [.. c1, .. y1, .. s, .. y2, .. c2];

        using CompareResult result = Diff(left, right, InsertDelete with { MinMatch = 8 });

        List<DiffRange> diffs = All(result);
        Assert.Equal(expectedChanges, diffs.Count);
        Assert.All(diffs, d => Assert.Equal(DiffKind.Changed, d.Kind));
        if (expectedChanges == 1)
        {
            Assert.Equal(new DiffRange(DiffKind.Changed, 4096, 64 + sameLength, 4096, 64 + sameLength), diffs[0]);
        }
        else
        {
            Assert.Equal(
            [
                new DiffRange(DiffKind.Changed, 4096, 32, 4096, 32),
                new DiffRange(DiffKind.Changed, 4096 + 32 + 8, 32, 4096 + 32 + 8, 32),
            ], diffs);
        }
    }

    private sealed record EditCase(int Length, int Edits, int Seed);

    private static Arbitrary<EditCase> EditCases() =>
        (from length in Gen.Choose(0, 8192)
         from edits in Gen.Choose(0, 20)
         from seed in Gen.Choose(0, int.MaxValue)
         select new EditCase(length, edits, seed)).ToArbitrary();

    /// <summary>無作為な挿入・削除・上書きを加えたデータ (TC-ANA-03-04、TC-ANA-07-02)。</summary>
    internal static (byte[] Left, byte[] Right) RandomEdits(int length, int edits, int seed)
    {
        var random = new Random(seed);
        byte[] left = new byte[length];
        random.NextBytes(left);
        var right = new List<byte>(left);
        for (int e = 0; e < edits; e++)
        {
            int at = random.Next(right.Count + 1);
            int size = random.Next(1, 17);
            switch (random.Next(3))
            {
                case 0:
                    byte[] insert = new byte[size];
                    random.NextBytes(insert);
                    right.InsertRange(at, insert);
                    break;
                case 1:
                    right.RemoveRange(Math.Min(at, right.Count), Math.Min(size, right.Count - Math.Min(at, right.Count)));
                    break;
                default:
                    for (int k = at; k < Math.Min(right.Count, at + size); k++)
                    {
                        right[k] = (byte)random.Next(256);
                    }

                    break;
            }
        }

        return (left, [.. right]);
    }

    [Property(MaxTest = 2000)]
    [Trait(TC, "TC-ANA-03-04")]
    public Property RandomEditsProduceAShortestEditScript() => Prop.ForAll(EditCases(), c =>
    {
        (byte[] left, byte[] right) = RandomEdits(c.Length, c.Edits, c.Seed);
        using CompareResult result = Diff(left, right, InsertDelete with { MinMatch = 1 });
        List<DiffRange> diffs = All(result);

        // 区間の列を左に適用すると右になる (正しい編集手順)。
        Assert.Equal(right, Apply(left, right, diffs));

        // W (64 KB) がデータ長以上なので、挿入・削除のバイト数の合計は最短の編集距離と一致する。
        long edits = diffs.Sum(d => d.LeftLength + d.RightLength);
        Assert.Equal(left.Length + right.Length - 2 * Lcs.Length(left, right), edits);
    });

    [Fact]
    public void TheBitParallelLcsMatchesThePlainDynamicProgramming()
    {
        var random = new Random(17);
        for (int t = 0; t < 300; t++)
        {
            byte[] a = new byte[random.Next(0, 200)];
            byte[] b = new byte[random.Next(0, 200)];
            random.NextBytes(a);
            random.NextBytes(b);
            for (int i = 0; i < a.Length; i++)
            {
                a[i] &= 3;
            }

            for (int i = 0; i < b.Length; i++)
            {
                b[i] &= 3;
            }

            Assert.Equal(Lcs.Plain(a, b), Lcs.Length(a, b));
        }
    }

    [Fact]
    public void TheSummarySaysApproximateOnlyForInsertDelete()
    {
        // 要約の「(近似)」は表示側 (TC-ANA-03-06) の文言。ここでは方式が結果に記録されることだけを確かめる。
        using CompareResult result = Diff(new byte[10], new byte[12], InsertDelete);
        Assert.Equal(CompareMethod.InsertDelete, result.Method);
        Assert.Equal([new DiffRange(DiffKind.Inserted, 10, 0, 10, 2)], All(result));
    }

    [Fact]
    public void ACompletelyDifferentWindowBecomesOneChange()
    {
        byte[] left = new byte[2000];
        byte[] right = new byte[2000];
        Random(1, 0, left);
        Random(2, 0, right);
        using CompareResult result = Diff(left, right, InsertDelete with { Window = 256 });
        Assert.Equal([new DiffRange(DiffKind.Changed, 0, 2000, 0, 2000)], All(result));
        Assert.Equal(0, result.AbortedWindows);
    }

    [Fact]
    public void UnitsLargerThanOneByteCompareWholeUnits()
    {
        byte[] left = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        // 単位 4 で、要素 (4 バイト) を 1 つ挿入する。
        byte[] right = [.. left[..16], 0xEE, 0xEE, 0xEE, 0xEE, .. left[16..]];
        using CompareResult result = Diff(left, right, InsertDelete with { Unit = 4, MinMatch = 4 });
        Assert.Equal([new DiffRange(DiffKind.Inserted, 16, 0, 16, 4)], All(result));
    }

    [Fact]
    public void ReadErrorsBecomeUnreadableRangesAndComparisonResumes()
    {
        byte[] data = new byte[100_000];
        Random(9, 0, data);
        var left = new FunctionData(data.Length, (o, s) => data.AsSpan((int)o, s.Length).CopyTo(s));
        var right = new FunctionData(data.Length, (o, s) => data.AsSpan((int)o, s.Length).CopyTo(s));
        right.Bad.Add(new Sources.UnreadableRange(50_000, 100, Sources.UnreadableReason.IoError));
        using CompareResult result = Diff(left, right, InsertDelete);
        Assert.Equal([new DiffRange(DiffKind.Unreadable, 50_000, 100, 50_000, 100)], All(result));
    }
}

/// <summary>最長共通部分列の長さ (テストの基準。ビット並列の動的計画法)。</summary>
internal static class Lcs
{
    /// <summary>Hyyrö のビット並列の LCS: V' = (V + (V &amp; M)) | (V &amp; ~M)。0 のビットの数が LCS の長さ。</summary>
    public static int Length(byte[] a, byte[] b)
    {
        int n = a.Length;
        if (n == 0 || b.Length == 0)
        {
            return 0;
        }

        int words = (n + 63) / 64;
        var masks = new ulong[256][];
        for (int i = 0; i < n; i++)
        {
            masks[a[i]] ??= new ulong[words];
            masks[a[i]][i >> 6] |= 1UL << (i & 63);
        }

        ulong[] v = new ulong[words];
        Array.Fill(v, ulong.MaxValue);
        ulong[] zero = new ulong[words];
        foreach (byte c in b)
        {
            ulong[] m = masks[c] ?? zero;
            ulong carry = 0;
            for (int w = 0; w < words; w++)
            {
                ulong u = v[w] & m[w];
                ulong sum = v[w] + u;
                ulong c1 = sum < v[w] ? 1UL : 0UL;
                ulong total = sum + carry;
                ulong c2 = total < sum ? 1UL : 0UL;
                carry = c1 | c2;
                v[w] = total | (v[w] & ~m[w]);
            }
        }

        int ones = 0;
        for (int w = 0; w < words; w++)
        {
            ulong x = v[w];
            if (w == words - 1 && (n & 63) != 0)
            {
                x |= ~0UL << (n & 63);
            }

            ones += System.Numerics.BitOperations.PopCount(~x);
        }

        return ones;
    }

    /// <summary>普通の動的計画法 (小さなデータでビット並列の実装を確かめる)。</summary>
    public static int Plain(byte[] a, byte[] b)
    {
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
