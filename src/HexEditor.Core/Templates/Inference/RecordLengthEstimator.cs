using System.Numerics;
using System.Runtime.Intrinsics;

namespace HexEditor.Core.Templates.Inference;

/// <summary>
/// レコード長の推定 (TPL-38 の仕様 4)。範囲の先頭 1 MiB (短ければ全体) を k バイトずつのレコードに分け、列ごとの値のエントロピー
/// (Miller–Madow の補正) の平均から得点 <c>1 − (列のエントロピーの平均 / 構造のないバイトの列のエントロピー)</c> を求める。
/// <para>
/// 列のエントロピーは標本の数で偏る (標本が少ないほど小さく出る) ため、どの候補でも同じ数 N0 = ⌊窓の長さ / 最大の候補⌋ のレコードの組
/// ごとに求めて平均する (k の倍数の候補が、標本が少ないだけで高い得点にならない)。分母は、窓から位置を散らして取った N0 バイトの列の
/// エントロピーの平均 (構造がなければこの値になる) で、乱数だけの範囲の得点はほぼ 0 になる。
/// </para>
/// <para>
/// 実装の注記: 候補 k = 2〜4,096 のすべての列のエントロピーを求めると 1 MiB × 4,095 回の読み出しになるため、先に同じ位置どうしの一致の割合
/// (k 離れたバイトが等しい割合。列の値の衝突の確率と同じもので、エントロピーの低い列ほど高い) を SIMD で全候補について求め、
/// その上位と k ≤ 64 の候補だけのエントロピーを求める (得点の上位はこの中に入る)。
/// </para>
/// </summary>
public static class RecordLengthEstimator
{
    /// <summary>得点を求める範囲の最大の長さ。</summary>
    public const int Window = 1024 * 1024;

    /// <summary>候補のレコード長の最大値。</summary>
    public const int MaxCandidate = 4096;

    /// <summary>出す候補の数。</summary>
    public const int Shown = 5;

    /// <summary>得点の差がこれ以内の候補は、小さい k を上にする。</summary>
    public const double Tolerance = 0.02;

    /// <summary>1 位の得点がこれ未満なら「固定長のレコードが見つかりませんでした」。</summary>
    public const double MinScore = 0.15;

    /// <summary>一致の割合で選ぶ、エントロピーを求める候補の数 (k ≤ 64 は常に求める)。</summary>
    private const int Prefiltered = 160;

    private const int AlwaysScored = 64;

    /// <summary>
    /// 候補を得点の順 (差が <see cref="Tolerance"/> 以内なら小さい k を上) に最大 <see cref="Shown"/> 個返す。<paramref name="rangeLength"/> は
    /// 範囲全体の長さ (候補の上限 <c>範囲の長さ / 4</c> とレコードの数に使う)。
    /// </summary>
    public static IReadOnlyList<RecordLengthCandidate> Estimate(ReadOnlySpan<byte> window, long rangeLength, CancellationToken cancellationToken = default)
    {
        int kMax = (int)Math.Min(MaxCandidate, rangeLength / TemplateInferenceRequest.MinRecords);
        if (kMax < 2 || window.Length < 4)
        {
            return [];
        }

        byte[] data = window.ToArray();
        int n0 = Math.Max(2, data.Length / kMax);
        double baseline = Baseline(data, n0);
        // 1. 一致の割合 (全候補)
        double[] match = new double[kMax + 1];
        Parallel.For(2, kMax + 1, new ParallelOptions { CancellationToken = cancellationToken }, k =>
        {
            int len = data.Length - k;
            match[k] = len <= 0 ? 0 : (double)CountEqual(data.AsSpan(0, len), data.AsSpan(k, len)) / len;
        });

        var scoredSet = new HashSet<int>();
        for (int k = 2; k <= Math.Min(kMax, AlwaysScored); k++)
        {
            scoredSet.Add(k);
        }

        foreach (int k in Enumerable.Range(2, kMax - 1).OrderByDescending(k => match[k]).ThenBy(k => k).Take(Prefiltered))
        {
            scoredSet.Add(k);
        }

        // 2. エントロピーの得点
        int[] ks = [.. scoredSet.Order()];
        double[] scores = new double[ks.Length];
        Parallel.For(0, ks.Length, new ParallelOptions { CancellationToken = cancellationToken }, () => new int[256], (i, _, counts) =>
        {
            scores[i] = Score(data, ks[i], n0, baseline, counts);
            return counts;
        }, _ => { });

        var candidates = new List<RecordLengthCandidate>(ks.Length);
        for (int i = 0; i < ks.Length; i++)
        {
            candidates.Add(new RecordLengthCandidate(ks[i], scores[i], rangeLength / ks[i]));
        }

        return Rank(candidates, Shown);
    }

    /// <summary>得点の高い順に並べる。残りの候補のうち、最も高い得点との差が許容内の候補では小さい k を先にする。</summary>
    public static IReadOnlyList<RecordLengthCandidate> Rank(IReadOnlyList<RecordLengthCandidate> candidates, int count)
    {
        var remaining = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Length).ToList();
        var result = new List<RecordLengthCandidate>(count);
        while (remaining.Count > 0 && result.Count < count)
        {
            double best = remaining[0].Score;
            RecordLengthCandidate pick = remaining.Where(c => c.Score >= best - Tolerance).MinBy(c => c.Length)!;
            result.Add(pick);
            remaining.Remove(pick);
        }

        return result;
    }

    /// <summary>レコード長 k の得点。列のエントロピーは <paramref name="n0"/> 個のレコードの組ごとに求めて平均する。</summary>
    internal static double Score(byte[] data, int k, int n0, double baseline, int[] counts)
    {
        int records = data.Length / k;
        int blocks = records / n0;
        if (blocks < 1 || baseline <= 0)
        {
            return 0;
        }

        double sum = 0;
        int[] touched = new int[Math.Min(256, n0)];
        for (int block = 0; block < blocks; block++)
        {
            int first = block * n0 * k;
            for (int j = 0; j < k; j++)
            {
                sum += ColumnEntropy(data, first + j, k, n0, counts, touched);
            }
        }

        double mean = sum / ((double)blocks * k);
        return Math.Clamp(1 - (mean / baseline), -1, 1);
    }

    /// <summary>位置 <paramref name="start"/> から <paramref name="stride"/> おきの <paramref name="n"/> バイトのエントロピー (Miller–Madow の補正。ビット)。</summary>
    private static double ColumnEntropy(byte[] data, int start, int stride, int n, int[] counts, int[] touched)
    {
        int distinct = 0;
        for (int i = 0, p = start; i < n; i++, p += stride)
        {
            byte b = data[p];
            if (counts[b]++ == 0)
            {
                touched[distinct++] = b;
            }
        }

        double h = 0;
        for (int t = 0; t < distinct; t++)
        {
            int c = counts[touched[t]];
            double pr = (double)c / n;
            h -= pr * Math.Log2(pr);
            counts[touched[t]] = 0;
        }

        // Miller–Madow の補正: (m − 1) / (2N) ナット
        return h + ((distinct - 1) / (2.0 * n * Math.Log(2)));
    }

    /// <summary>構造のない列のエントロピー: 窓から決まった擬似乱数の位置で取った <paramref name="n0"/> バイトの列のエントロピーの平均。</summary>
    internal static double Baseline(byte[] data, int n0)
    {
        int columns = Math.Clamp(262_144 / n0, 64, 4096);
        int[] counts = new int[256];
        int[] touched = new int[Math.Min(256, n0)];
        byte[] column = new byte[n0];
        ulong state = 0x9E3779B97F4A7C15UL;
        double sum = 0;
        for (int c = 0; c < columns; c++)
        {
            for (int i = 0; i < n0; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                column[i] = data[(int)(state % (ulong)data.Length)];
            }

            sum += ColumnEntropy(column, 0, 1, n0, counts, touched);
        }

        return sum / columns;
    }

    /// <summary>バイトのエントロピー (ビット)。</summary>
    public static double ByteEntropy(ReadOnlySpan<byte> data)
    {
        Span<long> counts = stackalloc long[256];
        foreach (byte b in data)
        {
            counts[b]++;
        }

        return Statistics.StatMath.Entropy(counts, data.Length);
    }

    private static long CountEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long count = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= a.Length; i += 32)
            {
                Vector256<byte> eq = Vector256.Equals(Vector256.Create(a.Slice(i, 32)), Vector256.Create(b.Slice(i, 32)));
                count += BitOperations.PopCount(eq.ExtractMostSignificantBits());
            }
        }

        for (; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                count++;
            }
        }

        return count;
    }
}
