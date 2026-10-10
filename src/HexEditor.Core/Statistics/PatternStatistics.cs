using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Statistics;

/// <summary>よく現れるバイト列の指定 (ANA-15 の仕様 1)。</summary>
public sealed record PatternRequest
{
    public const int MinLength = 2;
    public const int MaxLength = 16;
    public const int MinTop = 10;
    public const int MaxTop = 10_000;

    /// <summary>作業用メモリの上限 (ANA-15 の「巨大ファイル・長時間処理」)。</summary>
    public const long DefaultMemoryLimit = 256L * 1024 * 1024;

    public IReadOnlyList<HashRange> Ranges { get; init; } = [];

    /// <summary>列の長さ n (2〜16)。</summary>
    public int Length { get; init; } = 4;

    /// <summary>上位 K 件 (10〜10,000)。</summary>
    public int Top { get; init; } = 100;

    /// <summary>最小件数 (2 以上)。</summary>
    public long MinCount { get; init; } = 2;

    /// <summary>位置の条件「オフセット mod a = b」の a (0 または 1 なら条件なし)。オフセットはドキュメント上の位置。</summary>
    public long AlignModulus { get; init; }

    public long AlignRemainder { get; init; }

    /// <summary>「すべて 0x00」「すべて 0xFF」の列を除く。</summary>
    public bool ExcludeUniform { get; init; } = true;

    /// <summary>作業用メモリの上限。対象がこれを超えるか、ハッシュ表がこれを超えそうなら 2 段階方式で数える。</summary>
    public long MemoryLimit { get; init; } = DefaultMemoryLimit;

    public int ChunkSize { get; init; } = RangeScanner.DefaultChunkSize;
}

/// <summary>よく現れるバイト列の 1 行 (ANA-15 の仕様 2)。</summary>
public sealed record NGramCount(byte[] Bytes, long Count, long FirstOffset);

/// <summary>よく現れるバイト列の結果。</summary>
public sealed record PatternResult(IReadOnlyList<NGramCount> Rows, long Positions, bool TwoPass, long PeakWorkingBytes);

/// <summary>繰り返しの周期の候補 (ANA-15 の仕様 3): ずらし幅と、一致したバイトの割合。</summary>
public sealed record PeriodCandidate(int Period, double Ratio);

/// <summary>
/// パターン統計 (ANA-15)。対象が作業用メモリの上限 (256 MB) 以下なら、すべての n-gram をハッシュ表で正確に数える。超える場合と、
/// ハッシュ表が上限に達した場合は、1 回目の読み込みで Count-Min スケッチと上位候補 (Space-Saving 法、候補数 = K × 10) を求め、
/// 2 回目の読み込みで候補の件数を正確に数え直す。キャンセルした場合は結果を破棄する (例外を投げる)。
/// </summary>
public static class PatternStatistics
{
    /// <summary>ハッシュ表の 1 項目あたりの作業用メモリの見積もり (キー 16、値 16、ハッシュと次の番号 8、バケット 4、余裕)。</summary>
    internal const int ExactEntryBytes = 56;

    /// <summary>自己相関に使う範囲 (対象の先頭 64 MB。ANA-15 の仕様 3)。</summary>
    public const long PeriodWindow = 64L * 1024 * 1024;

    public const int MaxPeriod = 4096;

    /// <summary>自己相関で比べる標本の数と長さ (先頭 64 MB の中に均等に置く)。</summary>
    private const int PeriodSamples = 8;
    private const int PeriodSampleLength = 256 * 1024;

    /// <summary>計算中の作業用メモリ (テスト用の状態表示。TC-ANA-15-02)。</summary>
    public static long CurrentWorkingBytes => Interlocked.Read(ref _currentWorking);

    private static long _currentWorking;

    public static PatternResult FindFrequent(DocumentSnapshot snapshot, PatternRequest request, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        int n = Math.Clamp(request.Length, PatternRequest.MinLength, PatternRequest.MaxLength);
        int top = Math.Clamp(request.Top, PatternRequest.MinTop, PatternRequest.MaxTop);
        var ranges = new LogicalRanges(HashEngine.Normalize(request.Ranges, snapshot.Length));
        try
        {
            if (ranges.Length <= request.MemoryLimit)
            {
                operation?.SetTotal(ranges.Length);
                if (CountExact(snapshot, ranges, request, n, operation, token) is { } exact)
                {
                    return exact with { Rows = Rank(exact.Rows, top, request.MinCount) };
                }
            }

            return TwoPass(snapshot, ranges, request, n, top, operation, token);
        }
        finally
        {
            Interlocked.Exchange(ref _currentWorking, 0);
        }
    }

    private static IReadOnlyList<NGramCount> Rank(IEnumerable<NGramCount> rows, int top, long minCount) =>
        [.. rows.Where(r => r.Count >= Math.Max(2, minCount)).OrderByDescending(r => r.Count).ThenBy(r => r.FirstOffset).Take(top)];

    /// <summary>n-gram の位置を順に列挙する (読めないバイトを含む列と、位置の条件に合わない列は除く)。</summary>
    private sealed class GramWalker(LogicalRanges ranges, PatternRequest request, int n)
    {
        private readonly UInt128 _mask = n == 16 ? UInt128.MaxValue : (UInt128.One << (8 * n)) - 1;
        private readonly UInt128 _zero = 0;
        private readonly UInt128 _ff = n == 16 ? UInt128.MaxValue : (UInt128.One << (8 * n)) - 1;
        private UInt128 _key;
        private int _valid;

        public long Positions { get; private set; }

        /// <summary>
        /// 読み込みの 1 回分を渡す。<paramref name="visit"/> は (キー, 列の先頭のドキュメント上のオフセット) で呼ぶ。
        /// </summary>
        public void Feed(ScanChunk chunk, Action<UInt128, long> visit)
        {
            ReadOnlySpan<byte> span = chunk.Span;
            int badIndex = 0;
            IReadOnlyList<(int Start, int Length)> bad = chunk.Bad;
            long modulus = request.AlignModulus;
            for (int i = 0; i < span.Length; i++)
            {
                // 読めないバイトで列を切る。
                if (badIndex < bad.Count && i >= bad[badIndex].Start)
                {
                    if (i < bad[badIndex].Start + bad[badIndex].Length)
                    {
                        _valid = 0;
                        continue;
                    }

                    badIndex++;
                    i--;
                    continue;
                }

                _key = ((_key << 8) | span[i]) & _mask;
                if (++_valid < n)
                {
                    continue;
                }

                if (request.ExcludeUniform && (_key == _zero || _key == _ff))
                {
                    continue;
                }

                long logicalStart = chunk.Logical + i - n + 1;
                long offset = ranges.ToDocument(logicalStart);
                if (modulus > 1 && ((offset % modulus) + modulus) % modulus != request.AlignRemainder)
                {
                    continue;
                }

                Positions++;
                visit(_key, offset);
            }
        }
    }

    private struct ExactEntry
    {
        public long Count;
        public long First;
    }

    /// <summary>ハッシュ表で正確に数える。作業用メモリの上限に達したら null (2 段階方式に切り替える)。</summary>
    private static PatternResult? CountExact(DocumentSnapshot snapshot, LogicalRanges ranges, PatternRequest request, int n,
        LongRunningOperation? operation, CancellationToken token)
    {
        long limitEntries = request.MemoryLimit / ExactEntryBytes;
        var table = new Dictionary<UInt128, ExactEntry>();
        var walker = new GramWalker(ranges, request, n);
        var scanner = new RangeScanner(snapshot, ranges, request.ChunkSize, token);
        bool overflow = false;
        void Visit(UInt128 key, long offset)
        {
            ref ExactEntry e = ref CollectionsMarshal.GetValueRefOrAddDefault(table, key, out bool exists);
            if (!exists)
            {
                e.First = offset;
            }

            e.Count++;
        }

        foreach (ScanChunk chunk in scanner.Read())
        {
            token.ThrowIfCancellationRequested();
            walker.Feed(chunk, Visit);
            Interlocked.Exchange(ref _currentWorking, (long)table.Count * ExactEntryBytes);
            operation?.Report(scanner.BytesRead);
            if (table.Count > limitEntries)
            {
                overflow = true;
                break;
            }
        }

        if (overflow)
        {
            return null;
        }

        var rows = table.Where(kv => kv.Value.Count >= 2).Select(kv => new NGramCount(ToBytes(kv.Key, n), kv.Value.Count, kv.Value.First));
        return new PatternResult([.. rows], walker.Positions, false, (long)table.Count * ExactEntryBytes);
    }

    /// <summary>2 段階方式 (Count-Min スケッチ + Space-Saving の候補を数え直す)。</summary>
    private static PatternResult TwoPass(DocumentSnapshot snapshot, LogicalRanges ranges, PatternRequest request, int n, int top,
        LongRunningOperation? operation, CancellationToken token)
    {
        int capacity = Math.Max(1000, top * 10);
        var sketch = new CountMinSketch(depth: 4, widthBits: 22);
        var heavy = new SpaceSaving(capacity);
        long working = sketch.Bytes + heavy.Bytes;
        Interlocked.Exchange(ref _currentWorking, working);
        operation?.SetTotal(ranges.Length * 2);

        // 1 回目: スケッチで件数を見積もり、上位候補を保つ。
        var walker = new GramWalker(ranges, request, n);
        var scanner = new RangeScanner(snapshot, ranges, request.ChunkSize, token);
        foreach (ScanChunk chunk in scanner.Read())
        {
            token.ThrowIfCancellationRequested();
            walker.Feed(chunk, (key, _) => heavy.Offer(key, sketch.AddAndEstimate(key)));
            operation?.Report(scanner.BytesRead);
        }

        // 2 回目: 候補の件数を正確に数え直す。
        var exact = heavy.Keys.ToDictionary(k => k, _ => new ExactEntry { First = -1 });
        var second = new GramWalker(ranges, request, n);
        var scanner2 = new RangeScanner(snapshot, ranges, request.ChunkSize, token);
        foreach (ScanChunk chunk in scanner2.Read())
        {
            token.ThrowIfCancellationRequested();
            second.Feed(chunk, (key, offset) =>
            {
                ref ExactEntry e = ref CollectionsMarshal.GetValueRefOrNullRef(exact, key);
                if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref e))
                {
                    if (e.First < 0)
                    {
                        e.First = offset;
                    }

                    e.Count++;
                }
            });
            operation?.Report(ranges.Length + scanner2.BytesRead);
        }

        working += (long)exact.Count * ExactEntryBytes;
        Interlocked.Exchange(ref _currentWorking, working);
        IReadOnlyList<NGramCount> rows = Rank(exact.Select(kv => new NGramCount(ToBytes(kv.Key, n), kv.Value.Count, kv.Value.First)), top, request.MinCount);
        return new PatternResult(rows, walker.Positions, true, working);
    }

    private static byte[] ToBytes(UInt128 key, int n)
    {
        byte[] bytes = new byte[n];
        for (int i = n - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(key & 0xFF);
            key >>= 8;
        }

        return bytes;
    }

    /// <summary>Count-Min スケッチ (件数の上限の見積もり)。</summary>
    private sealed class CountMinSketch(int depth, int widthBits)
    {
        private readonly uint[] _table = new uint[depth << widthBits];
        private readonly int _mask = (1 << widthBits) - 1;

        public long Bytes => _table.Length * sizeof(uint);

        public long AddAndEstimate(UInt128 key)
        {
            ulong lo = (ulong)key;
            ulong hi = (ulong)(key >> 64);
            ulong h1 = Mix(lo ^ (hi * 0x9E3779B97F4A7C15UL));
            ulong h2 = Mix(h1 ^ 0xC2B2AE3D27D4EB4FUL) | 1;
            uint estimate = uint.MaxValue;
            for (int d = 0; d < depth; d++)
            {
                int index = (d << widthBits) | (int)((h1 + ((ulong)d * h2)) & (ulong)_mask);
                uint v = _table[index];
                if (v != uint.MaxValue)
                {
                    v++;
                    _table[index] = v;
                }

                estimate = Math.Min(estimate, v);
            }

            return estimate;
        }

        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// 上位候補 (Space-Saving 法の変形): 候補でないキーは、スケッチの見積もりが候補の最小の件数を超えたときだけ、最小の候補と入れ替える。
    /// 候補の件数は最小ヒープで持つ。
    /// </summary>
    private sealed class SpaceSaving(int capacity)
    {
        private readonly Dictionary<UInt128, int> _index = new(capacity);
        private readonly UInt128[] _keys = new UInt128[capacity];
        private readonly long[] _counts = new long[capacity];
        private int _size;

        public long Bytes => (long)capacity * (16 + 8 + 48);

        public IEnumerable<UInt128> Keys => _keys.Take(_size);

        public void Offer(UInt128 key, long estimate)
        {
            if (_index.TryGetValue(key, out int at))
            {
                _counts[at] = Math.Max(_counts[at] + 1, estimate);
                SiftDown(at);
                return;
            }

            if (_size < capacity)
            {
                _keys[_size] = key;
                _counts[_size] = estimate;
                _index[key] = _size;
                SiftUp(_size++);
                return;
            }

            if (estimate <= _counts[0])
            {
                return;
            }

            _index.Remove(_keys[0]);
            _keys[0] = key;
            _counts[0] = estimate;
            _index[key] = 0;
            SiftDown(0);
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_counts[parent] <= _counts[i])
                {
                    break;
                }

                Swap(i, parent);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                int l = (2 * i) + 1;
                int r = l + 1;
                int smallest = i;
                if (l < _size && _counts[l] < _counts[smallest])
                {
                    smallest = l;
                }

                if (r < _size && _counts[r] < _counts[smallest])
                {
                    smallest = r;
                }

                if (smallest == i)
                {
                    return;
                }

                Swap(i, smallest);
                i = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            (_keys[a], _keys[b]) = (_keys[b], _keys[a]);
            (_counts[a], _counts[b]) = (_counts[b], _counts[a]);
            _index[_keys[a]] = a;
            _index[_keys[b]] = b;
        }
    }

    // ---- 繰り返しの周期 (ANA-15 の仕様 3) ----

    /// <summary>
    /// 自己相関 (位置を k だけずらしたときに一致するバイトの割合) を k = 1〜4,096 について求め、割合の高い順に上位 10 件を返す。
    /// 対象の先頭 64 MB の中に均等に置いた標本 (8 か所 × 256 KB) を使う。割合がほぼ同じ (差が 0.5 ポイント以内) なら、
    /// その約数の周期を先にする (64 と 128 なら 64)。
    /// </summary>
    public static IReadOnlyList<PeriodCandidate> EstimatePeriods(DocumentSnapshot snapshot, IReadOnlyList<HashRange> ranges, int count = 10,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        var logical = new LogicalRanges(HashEngine.Normalize(ranges, snapshot.Length));
        long window = Math.Min(PeriodWindow, logical.Length);
        long[] matches = new long[MaxPeriod + 1];
        long[] compared = new long[MaxPeriod + 1];
        int samples = window <= (long)PeriodSamples * (PeriodSampleLength + MaxPeriod) ? 1 : PeriodSamples;
        int sampleLength = samples == 1 ? (int)window : PeriodSampleLength + MaxPeriod;
        operation?.SetTotal((long)samples * sampleLength);
        var scanner = new RangeScanner(snapshot, logical, RangeScanner.DefaultChunkSize, token);
        byte[] data = new byte[sampleLength];
        for (int s = 0; s < samples; s++)
        {
            long at = samples == 1 ? 0 : (long)((Int128)(window - sampleLength) * s / (samples - 1));
            int filled = 0;
            bool hasBad = false;
            foreach (ScanChunk chunk in scanner.Read(at, at + sampleLength))
            {
                chunk.Span.CopyTo(data.AsSpan(filled));
                filled += chunk.Length;
                hasBad |= chunk.HasBad;
            }

            operation?.Report(scanner.BytesRead);
            if (hasBad)
            {
                continue;
            }

            int compareLength = samples == 1 ? filled : PeriodSampleLength;
            Parallel.For(1, MaxPeriod + 1, new ParallelOptions { CancellationToken = token }, k =>
            {
                int len = Math.Min(compareLength, filled - k);
                if (len <= 0)
                {
                    return;
                }

                matches[k] += CountEqual(data.AsSpan(0, len), data.AsSpan(k, len));
                compared[k] += len;
            });
        }

        var ratios = Enumerable.Range(1, MaxPeriod).Where(k => compared[k] > 0)
            .Select(k => new PeriodCandidate(k, (double)matches[k] / compared[k])).ToList();
        return RankPeriods(ratios, count);
    }

    /// <summary>割合の高い順に並べ、割合がほぼ同じ約数の周期を先にする。</summary>
    internal static IReadOnlyList<PeriodCandidate> RankPeriods(IReadOnlyList<PeriodCandidate> candidates, int count)
    {
        const double Tolerance = 0.005;
        var byPeriod = candidates.ToDictionary(c => c.Period);
        var ordered = candidates.OrderByDescending(c => c.Ratio).ThenBy(c => c.Period).ToList();
        var result = new List<PeriodCandidate>();
        var used = new HashSet<int>();
        foreach (PeriodCandidate c in ordered)
        {
            if (result.Count >= count)
            {
                break;
            }

            // 割合がほぼ同じ約数があれば、そちらを先に入れる (小さい順)。
            for (int d = 1; d < c.Period; d++)
            {
                if (c.Period % d == 0 && byPeriod.TryGetValue(d, out PeriodCandidate? divisor) && divisor.Ratio >= c.Ratio - Tolerance
                    && used.Add(d) && result.Count < count)
                {
                    result.Add(divisor);
                }
            }

            if (used.Add(c.Period) && result.Count < count)
            {
                result.Add(c);
            }
        }

        return result;
    }

    /// <summary>一致するバイトの数 (SIMD)。</summary>
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
