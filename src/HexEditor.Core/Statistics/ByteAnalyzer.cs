namespace HexEditor.Core.Statistics;

/// <summary>
/// バイト単位の集計 (バイトのヒストグラム、ダイグラム、位置ごとのバイト分布、ブロックごとのエントロピー) を、1 回の読み込みで
/// 同時に行う (ANA-10、ANA-13、ANA-14 の「相乗り」)。読み込みの 1 回分を複数のスレッドで分けて数える。
/// メモリは対象の大きさに依存しない (ブロックの結果を除く)。
/// </summary>
internal sealed class ByteAnalyzer
{
    /// <summary>1 つのスレッドに割り当てる最小のバイト数。</summary>
    private const int MinSegment = 512 * 1024;

    private readonly long _length;
    private readonly EntropyBlocks? _blocks;
    private readonly bool _digram;
    private readonly bool _positions;
    private readonly object _blockLock = new();
    private readonly object _positionLock = new();
    private readonly Dictionary<int, BlockAccumulator> _open = [];
    private int _previous = -1;
    private long _end;

    // スレッドごとの集計の表 (ダイグラムは 256 KiB)。読み込みの 1 回分ごとに作り直すと、大きなファイルでは
    // ラージオブジェクトヒープのごみが溜まってメモリ使用量が対象の大きさに応じて増えるため、使い回す。
    private Local[] _locals = [];

    public ByteAnalyzer(long length, EntropyBlocks? blocks, bool digram, bool positions)
    {
        _length = length;
        _blocks = blocks;
        _digram = digram;
        _positions = positions;
        DigramCounts = digram ? new long[65536] : null;
        PositionCounts = positions ? new long[PositionDistribution.Sections * 256] : null;
    }

    public long[] Histogram { get; } = new long[256];

    public long[]? DigramCounts { get; }

    public long[]? PositionCounts { get; }

    /// <summary>読み込みの 1 回分を数える (前回の続きであること)。</summary>
    public void Process(ScanChunk chunk)
    {
        int n = chunk.Length;
        if (n == 0)
        {
            return;
        }

        // 読めないバイトはブロックの「読み込み不可」に数える。
        if (_blocks is not null)
        {
            foreach ((int s, int l) in chunk.Bad)
            {
                AddBad(chunk.Logical + s, l);
            }
        }

        int workers = (int)Math.Clamp(n / MinSegment, 1, Environment.ProcessorCount);
        int[] bounds = new int[workers + 1];
        for (int w = 0; w <= workers; w++)
        {
            bounds[w] = (int)((long)n * w / workers);
        }

        if (_locals.Length < workers)
        {
            Array.Resize(ref _locals, workers);
        }

        Local[] locals = _locals;
        for (int w = 0; w < workers; w++)
        {
            if (locals[w] is { } existing)
            {
                existing.Clear();
            }
            else
            {
                locals[w] = new Local { Digram = _digram ? new int[65536] : null };
            }
        }

        byte[] buffer = chunk.Buffer;
        if (workers == 1)
        {
            ProcessSegment(locals[0], chunk, buffer, 0, n);
        }
        else
        {
            Parallel.For(0, workers, w => ProcessSegment(locals[w], chunk, buffer, bounds[w], bounds[w + 1]));
        }

        for (int w = 0; w < workers; w++)
        {
            Local local = locals[w];
            for (int b = 0; b < 256; b++)
            {
                Histogram[b] += local.Histogram[b];
            }

            if (DigramCounts is not null)
            {
                int[] d = local.Digram!;
                for (int i = 0; i < d.Length; i++)
                {
                    DigramCounts[i] += d[i];
                }
            }
        }

        if (DigramCounts is not null)
        {
            // 区切りをまたぐ組: 前回の最後のバイトと今回の先頭、スレッドの担当の境目。
            if (_previous >= 0 && !IsBad(chunk, 0))
            {
                DigramCounts[(_previous << 8) | buffer[0]]++;
            }

            for (int w = 1; w < workers; w++)
            {
                int i = bounds[w];
                if (!IsBad(chunk, i - 1) && !IsBad(chunk, i))
                {
                    DigramCounts[(buffer[i - 1] << 8) | buffer[i]]++;
                }
            }

            _previous = IsBad(chunk, n - 1) ? -1 : buffer[n - 1];
        }

        _end = chunk.Logical + n;
        FinalizeBlocks(_end);
    }

    /// <summary>最後のブロックを確定する。</summary>
    public void Finish(long end)
    {
        FinalizeBlocks(Math.Max(end, _end), all: true);
    }

    private static bool IsBad(ScanChunk chunk, int at)
    {
        foreach ((int s, int l) in chunk.Bad)
        {
            if (at >= s && at < s + l)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class Local
    {
        public readonly long[] Histogram = new long[256];
        public int[]? Digram;

        public void Clear()
        {
            Array.Clear(Histogram);
            if (Digram is not null)
            {
                Array.Clear(Digram);
            }
        }
    }

    private sealed class BlockAccumulator
    {
        public readonly long[] Counts = new long[256];
        public long Readable;
        public long Bad;
    }

    private void ProcessSegment(Local local, ScanChunk chunk, byte[] buffer, int start, int end)
    {
        Span<int> counts = stackalloc int[256];
        foreach ((int gs, int gl) in chunk.GoodParts())
        {
            int a = Math.Max(gs, start);
            int b = Math.Min(gs + gl, end);
            if (a >= b)
            {
                continue;
            }

            int previous = -1;
            int at = a;
            while (at < b)
            {
                // ブロックと区間の境目で区切る (区切りごとにブロック・区間の件数に加える)。
                long logical = chunk.Logical + at;
                long next = chunk.Logical + b;
                if (_blocks is not null)
                {
                    next = Math.Min(next, ((logical / _blocks.BlockSize) + 1) * _blocks.BlockSize);
                }

                int section = -1;
                if (PositionCounts is not null)
                {
                    section = PositionDistribution.SectionOf(logical, _length);
                    if (section + 1 < PositionDistribution.Sections)
                    {
                        long boundary = (long)(((Int128)(section + 1) * _length + PositionDistribution.Sections - 1) / PositionDistribution.Sections);
                        next = Math.Min(next, Math.Max(boundary, logical + 1));
                    }
                }

                int stop = (int)(next - chunk.Logical);
                counts.Clear();
                ReadOnlySpan<byte> span = buffer.AsSpan(at, stop - at);
                if (local.Digram is { } digram)
                {
                    int p = previous;
                    int i = 0;
                    if (p < 0)
                    {
                        p = span[0];
                        counts[p]++;
                        i = 1;
                    }

                    for (; i < span.Length; i++)
                    {
                        int c = span[i];
                        counts[c]++;
                        digram[(p << 8) | c]++;
                        p = c;
                    }

                    previous = p;
                }
                else
                {
                    Count(span, counts);
                }

                for (int v = 0; v < 256; v++)
                {
                    local.Histogram[v] += counts[v];
                }

                if (_blocks is not null)
                {
                    AddToBlock((int)(logical / _blocks.BlockSize), counts, span.Length);
                }

                if (section >= 0)
                {
                    lock (_positionLock)
                    {
                        int row = section * 256;
                        for (int v = 0; v < 256; v++)
                        {
                            PositionCounts![row + v] += counts[v];
                        }
                    }
                }

                at = stop;
            }
        }
    }

    /// <summary>4 つの表に分けて数える (同じ値が続くときの書き込みの衝突を減らす)。</summary>
    private static void Count(ReadOnlySpan<byte> span, Span<int> counts)
    {
        if (span.Length < 64)
        {
            foreach (byte b in span)
            {
                counts[b]++;
            }

            return;
        }

        Span<int> c1 = stackalloc int[256];
        Span<int> c2 = stackalloc int[256];
        Span<int> c3 = stackalloc int[256];
        int i = 0;
        for (; i + 4 <= span.Length; i += 4)
        {
            counts[span[i]]++;
            c1[span[i + 1]]++;
            c2[span[i + 2]]++;
            c3[span[i + 3]]++;
        }

        for (; i < span.Length; i++)
        {
            counts[span[i]]++;
        }

        for (int v = 0; v < 256; v++)
        {
            counts[v] += c1[v] + c2[v] + c3[v];
        }
    }

    private void AddToBlock(int index, ReadOnlySpan<int> counts, int length)
    {
        lock (_blockLock)
        {
            if (!_open.TryGetValue(index, out BlockAccumulator? acc))
            {
                acc = new BlockAccumulator();
                _open[index] = acc;
            }

            for (int v = 0; v < 256; v++)
            {
                acc.Counts[v] += counts[v];
            }

            acc.Readable += length;
        }
    }

    private void AddBad(long logical, long length)
    {
        lock (_blockLock)
        {
            long end = logical + length;
            while (logical < end)
            {
                int index = (int)(logical / _blocks!.BlockSize);
                long stop = Math.Min(end, (index + 1) * _blocks.BlockSize);
                if (!_open.TryGetValue(index, out BlockAccumulator? acc))
                {
                    acc = new BlockAccumulator();
                    _open[index] = acc;
                }

                acc.Bad += stop - logical;
                logical = stop;
            }
        }
    }

    /// <summary>終わりが <paramref name="end"/> 以前のブロックを確定する。</summary>
    private void FinalizeBlocks(long end, bool all = false)
    {
        if (_blocks is null)
        {
            return;
        }

        lock (_blockLock)
        {
            foreach (int index in _open.Keys.ToList())
            {
                long blockEnd = Math.Min(_blocks.Length, (index + 1) * _blocks.BlockSize);
                if (all || blockEnd <= end)
                {
                    BlockAccumulator acc = _open[index];
                    if (index < _blocks.Count)
                    {
                        _blocks.Set(index, acc.Counts, acc.Readable, acc.Bad);
                    }

                    _open.Remove(index);
                }
            }
        }
    }
}
