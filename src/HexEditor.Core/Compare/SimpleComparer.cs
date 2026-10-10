using System.Numerics;
using System.Runtime.Intrinsics;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Compare;

/// <summary>
/// 単純比較 (ANA-02)。左右の開始位置から同じ相対位置のバイトどうしを比べる。1 MiB 単位で左右を並行して読み、一致の続く長さと
/// 不一致の続く長さをベクトル命令で求める (<see cref="MemoryExtensions.CommonPrefixLength{T}(Span{T}, ReadOnlySpan{T})"/> と
/// <see cref="FirstEqual"/>)。メモリは読み込みの単位の分だけを使う。
/// </summary>
internal static class SimpleComparer
{
    /// <summary>読み込みの単位 (8 の倍数。比較の単位の境界がそろうように)。</summary>
    public const int ChunkSize = 1 << 20;

    public static void Run(CompareOptions options, CompareResult result, CancellationToken cancellationToken, Action<long>? progress, int chunkSize = ChunkSize)
    {
        CompareRange left = result.Left;
        CompareRange right = result.Right;
        var sink = new DiffSink(result, options.MergeGap);
        int unit = options.Unit;
        long common = Math.Min(left.Length, right.Length);
        chunkSize = (int)Math.Max(8, Math.Min(chunkSize, (common + 7) / 8 * 8));
        byte[] a = new byte[chunkSize];
        byte[] b = new byte[chunkSize];
        long pos = 0;
        try
        {
            while (pos < common)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(chunkSize, common - pos);
                long at = pos;
                (ReadResult ra, ReadResult rb) = ReadBoth(left, left.Start + at, a, right, right.Start + at, b, n);

                // 読めなかった範囲 (左右の和。この塊の中の相対位置)。短く返った分も読めなかった扱いにする。
                List<(int Start, int End)> bad = Unreadable(ra, left.Start + at, n);
                bad.AddRange(Unreadable(rb, right.Start + at, n));
                bad = Normalize(bad);

                int s = 0;
                foreach ((int bs, int be) in bad)
                {
                    CompareSegment(a, b, s, bs, pos, unit, left, right, sink, result);
                    sink.Add(new DiffRange(DiffKind.Unreadable, left.Start + pos + bs, be - bs, right.Start + pos + bs, be - bs), 0);
                    s = be;
                }

                CompareSegment(a, b, s, n, pos, unit, left, right, sink, result);
                pos += n;
                result.ReportPosition(pos, pos);
                progress?.Invoke(pos);
            }

            // 長さが違う場合、短い方の末尾より後ろを 1 つの差分にする (仕様 4)。
            if (left.Length > common)
            {
                sink.Add(new DiffRange(DiffKind.Deleted, left.Start + common, left.Length - common, right.Start + common, 0), left.Length - common, mergeable: false);
            }
            else if (right.Length > common)
            {
                sink.Add(new DiffRange(DiffKind.Inserted, left.Start + common, 0, right.Start + common, right.Length - common), right.Length - common, mergeable: false);
            }

            result.ReportPosition(left.Length, right.Length);
        }
        finally
        {
            sink.Flush();
        }
    }

    /// <summary>塊の中の [from, to) を比べる。</summary>
    private static void CompareSegment(byte[] a, byte[] b, int from, int to, long chunkPos, int unit, CompareRange left, CompareRange right,
        DiffSink sink, CompareResult result)
    {
        if (from >= to)
        {
            return;
        }

        int i = from;
        long different = 0;
        while (i < to)
        {
            int k = a.AsSpan(i, to - i).CommonPrefixLength(b.AsSpan(i, to - i));
            i += k;
            if (i >= to)
            {
                break;
            }

            // i から値の違うバイトが続く。単位が 2 以上なら、単位の境界まで広げる (仕様 5)。
            int start = i;
            int end = i + FirstEqual(a.AsSpan(i, to - i), b.AsSpan(i, to - i));
            long count = end - start;
            if (unit > 1)
            {
                long rel = chunkPos + start;
                int down = (int)(rel % unit);
                start = Math.Max(from, start - down);
                long relEnd = chunkPos + end;
                int up = (int)((unit - relEnd % unit) % unit);
                int extended = Math.Min(to, end + up);
                for (int x = end; x < extended; x++)
                {
                    if (a[x] != b[x])
                    {
                        count++;
                    }
                }

                end = extended;
            }

            different += count;
            sink.Add(new DiffRange(DiffKind.Changed, left.Start + chunkPos + start, end - start, right.Start + chunkPos + start, end - start), count);
            i = end;
        }

        result.AddMatched(to - from - different);
    }

    /// <summary>読み込みの結果のうち読めなかった範囲を、塊の中の相対位置 [start, end) にする。</summary>
    private static List<(int Start, int End)> Unreadable(ReadResult r, long chunkStart, int length)
    {
        var list = new List<(int, int)>();
        foreach (UnreadableRange u in r.Unreadable)
        {
            long s = Math.Max(u.Offset, chunkStart) - chunkStart;
            long e = Math.Min(u.End, chunkStart + length) - chunkStart;
            if (s < e)
            {
                list.Add(((int)s, (int)e));
            }
        }

        if (r.BytesReturned < length)
        {
            list.Add((r.BytesReturned, length));
        }

        return list;
    }

    private static List<(int Start, int End)> Normalize(List<(int Start, int End)> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort();
        var merged = new List<(int Start, int End)> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            (int s, int e) = ranges[i];
            (int ps, int pe) = merged[^1];
            if (s <= pe)
            {
                merged[^1] = (ps, Math.Max(pe, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        return merged;
    }

    /// <summary>左右を読む。大きな読み込みは並行して行う (遅い方のストレージの速さで比べられるように)。</summary>
    internal static (ReadResult Left, ReadResult Right) ReadBoth(CompareRange left, long leftOffset, byte[] a, CompareRange right, long rightOffset,
        byte[] b, int n)
    {
        if (n < 64 * 1024)
        {
            return (left.Data.Read(leftOffset, a.AsSpan(0, n)), right.Data.Read(rightOffset, b.AsSpan(0, n)));
        }

        Task<ReadResult> readRight = Task.Run(() => right.Data.Read(rightOffset, b.AsSpan(0, n)), CancellationToken.None);
        ReadResult ra = left.Data.Read(leftOffset, a.AsSpan(0, n));
        return (ra, readRight.GetAwaiter().GetResult());
    }

    /// <summary>値の等しい最初の位置 (なければ長さ)。値の違うバイトが長く続く場合はベクトル命令で探す。</summary>
    internal static int FirstEqual(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        int n = Math.Min(x.Length, y.Length);
        int i = 0;
        for (; i < n && i < 16; i++)
        {
            if (x[i] == y[i])
            {
                return i;
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i + Vector256<byte>.Count <= n; i += Vector256<byte>.Count)
            {
                uint mask = Vector256.Equals(Vector256.Create(x.Slice(i, Vector256<byte>.Count)), Vector256.Create(y.Slice(i, Vector256<byte>.Count)))
                    .ExtractMostSignificantBits();
                if (mask != 0)
                {
                    return i + BitOperations.TrailingZeroCount(mask);
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; i + Vector128<byte>.Count <= n; i += Vector128<byte>.Count)
            {
                uint mask = Vector128.Equals(Vector128.Create(x.Slice(i, Vector128<byte>.Count)), Vector128.Create(y.Slice(i, Vector128<byte>.Count)))
                    .ExtractMostSignificantBits();
                if (mask != 0)
                {
                    return i + BitOperations.TrailingZeroCount(mask);
                }
            }
        }

        for (; i < n; i++)
        {
            if (x[i] == y[i])
            {
                return i;
            }
        }

        return n;
    }
}
