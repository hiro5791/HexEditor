namespace HexEditor.Core.Compare;

/// <summary>
/// 差分の分布 (ANA-06 の仕様 7): 左の比較範囲を区間に分け、区間ごとの異なるバイトの割合 (0〜1) を求める。区間の数は既定 512
/// (64〜4,096)。挿入 (右のみ) は左の挿入位置の区間に右の長さを数える (割合は 1 を超えない)。読み込み不可は数えない。
/// </summary>
public sealed class DiffDistribution
{
    public const int DefaultBuckets = 512;
    public const int MinBuckets = 64;
    public const int MaxBuckets = 4096;

    private DiffDistribution(long start, long length, long bucketSize, double[] ratios)
    {
        Start = start;
        Length = length;
        BucketSize = bucketSize;
        Ratios = ratios;
    }

    /// <summary>左の比較範囲の開始位置。</summary>
    public long Start { get; }

    public long Length { get; }

    /// <summary>1 区間のバイト数。</summary>
    public long BucketSize { get; }

    /// <summary>区間ごとの異なるバイトの割合。</summary>
    public IReadOnlyList<double> Ratios { get; }

    public int Count => Ratios.Count;

    /// <summary>区間 <paramref name="bucket"/> の開始位置 (左の絶対位置)。</summary>
    public long BucketStart(int bucket) => Start + bucket * BucketSize;

    public static DiffDistribution Compute(CompareResult result, int buckets = DefaultBuckets, CancellationToken cancellationToken = default)
    {
        buckets = Math.Clamp(buckets, MinBuckets, MaxBuckets);
        long start = result.Left.Start;
        long length = result.Left.Length;
        long size = Math.Max(1, (length + buckets - 1) / buckets);
        int count = length == 0 ? 1 : (int)((length + size - 1) / size);
        double[] bytes = new double[count];
        long seen = 0;
        foreach (DiffRange d in result.Diffs.Enumerate())
        {
            if ((++seen & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (d.Kind == DiffKind.Unreadable)
            {
                continue;
            }

            if (d.LeftLength == 0)
            {
                int b = Bucket(d.LeftOffset);
                bytes[b] += d.RightLength;
                continue;
            }

            long from = d.LeftOffset - start;
            long to = d.LeftEnd - start;
            while (from < to)
            {
                int b = (int)Math.Min(count - 1, from / size);
                long bucketEnd = Math.Min((b + 1) * size, to);
                bytes[b] += Math.Max(1, bucketEnd - from);
                from = bucketEnd;
            }
        }

        double[] ratios = new double[count];
        for (int b = 0; b < count; b++)
        {
            long bucketLength = Math.Max(1, Math.Min(size, length - b * size));
            ratios[b] = Math.Min(1, bytes[b] / bucketLength);
        }

        return new DiffDistribution(start, length, size, ratios);

        int Bucket(long offset) => (int)Math.Clamp((offset - start) / size, 0, count - 1);
    }

    /// <summary>区間 <paramref name="bucket"/> の中で最初に始まる (または区間に重なる) 差分の番号 (なければ null)。</summary>
    public long? FirstDiffIn(DiffStore diffs, int bucket)
    {
        long from = BucketStart(bucket);
        long to = from + BucketSize;
        long index = diffs.FirstEndingAfter(false, from);
        return index < diffs.Count && diffs[index].LeftOffset < to ? index : null;
    }
}
