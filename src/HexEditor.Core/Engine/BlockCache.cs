using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>読み込み要求の優先度 (ENG-06 の仕様 3)。値が小さいほど先に実行する。</summary>
public enum ReadPriority
{
    Display = 0,
    Prefetch = 1,
    Background = 2,
}

/// <summary>キャッシュした 1 ブロック。内容は変更しない。</summary>
public sealed class CachedBlock(long index, byte[] data, int length, IReadOnlyList<UnreadableRange> unreadable)
{
    public long Index { get; } = index;

    public byte[] Data { get; } = data;

    /// <summary>有効なバイト数 (データソースの末尾のブロックでは短い)。</summary>
    public int Length { get; } = length;

    /// <summary>このブロックの中の読めなかった範囲 (データソース上の絶対位置)。</summary>
    public IReadOnlyList<UnreadableRange> Unreadable { get; } = unreadable;

    public UnreadableRange? FindUnreadable(long sourceOffset)
    {
        foreach (UnreadableRange r in Unreadable)
        {
            if (sourceOffset >= r.Offset && sourceOffset < r.End)
            {
                return r;
            }
        }

        return null;
    }
}

/// <summary>
/// データソースの読み込みをブロック単位でキャッシュし、非同期で読み込む (ENG-06)。
/// 表示はブロックしない: キャッシュにないブロックは読み込みを始めて「読み込み中」として扱い、読み終わったら
/// <see cref="BlockLoaded"/> で知らせる。
/// </summary>
public sealed class BlockCache : IDisposable
{
    public const int DefaultBlockSize = 64 * 1024;

    private readonly IByteSource _source;
    private readonly object _lock = new();
    private readonly Dictionary<long, LinkedListNode<CachedBlock>> _map = [];
    private readonly LinkedList<CachedBlock> _lru = new();
    private readonly PriorityQueue<long, (int Priority, long Sequence)> _pending = new();
    private readonly Dictionary<long, int> _pendingPriority = [];
    private readonly HashSet<long> _inFlight = [];
    private long _capacityBytes;
    private long _sequence;
    private int _running;
    private int _generation;
    private bool _disposed;

    public BlockCache(IByteSource source, long capacityBytes, int maxConcurrentReads = 4)
    {
        _source = source;
        BlockSize = Math.Max(DefaultBlockSize, source.LogicalSectorSize);
        _capacityBytes = capacityBytes;
        MaxConcurrentReads = Math.Clamp(maxConcurrentReads, 1, 16);
    }

    public int BlockSize { get; }

    public int MaxConcurrentReads { get; }

    /// <summary>キャッシュの上限 (バイト)。小さくすると、超えた分をすぐに追い出す。</summary>
    public long CapacityBytes
    {
        get
        {
            lock (_lock)
            {
                return _capacityBytes;
            }
        }

        set
        {
            lock (_lock)
            {
                _capacityBytes = value;
                EvictIfNeeded();
            }
        }
    }

    public long MemoryBytes
    {
        get
        {
            lock (_lock)
            {
                return (long)_map.Count * BlockSize;
            }
        }
    }

    /// <summary>ブロックの読み込みが終わった。引数はデータソース上の範囲 (開始, 長さ)。スレッドプールから呼ばれる。</summary>
    public event Action<long, long>? BlockLoaded;

    public long BlockIndexOf(long sourceOffset) => sourceOffset / BlockSize;

    /// <summary>キャッシュにあれば返す (ブロックしない)。</summary>
    public bool TryGetBlock(long index, out CachedBlock block)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(index, out LinkedListNode<CachedBlock>? node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                block = node.Value;
                return true;
            }
        }

        block = null!;
        return false;
    }

    /// <summary>ブロックの読み込みを要求する。読み込み済み・読み込み中なら何もしない。</summary>
    public void Request(long index, ReadPriority priority)
    {
        if (index < 0 || (long)index * BlockSize >= _source.Length)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed || _map.ContainsKey(index) || _inFlight.Contains(index))
            {
                return;
            }

            int p = (int)priority;
            if (_pendingPriority.TryGetValue(index, out int existing) && existing <= p)
            {
                return;
            }

            // 優先度を上げる場合は新しい項目として積み、古い項目は取り出したときに捨てる。
            _pendingPriority[index] = p;
            _pending.Enqueue(index, (p, _sequence++));
            while (_running < MaxConcurrentReads && _pending.Count > 0)
            {
                _running++;
                _ = Task.Run(WorkerLoop);
            }
        }
    }

    /// <summary>
    /// バックグラウンド処理用の読み込み (ENG-06 の仕様 6)。キャッシュにあるブロックは再利用し、ないブロックは
    /// キャッシュに入れずにデータソースから直接読む (表示用のキャッシュを追い出さない)。
    /// </summary>
    public ReadResult ReadDirect(long sourceOffset, Span<byte> destination)
    {
        int count = (int)Math.Min(destination.Length, Math.Max(0, _source.Length - sourceOffset));
        var bad = new List<UnreadableRange>();
        int done = 0;
        while (done < count)
        {
            long pos = sourceOffset + done;
            long index = pos / BlockSize;
            int inBlock = (int)(pos % BlockSize);
            if (TryGetBlock(index, out CachedBlock cached))
            {
                int n = Math.Min(cached.Length - inBlock, count - done);
                cached.Data.AsSpan(inBlock, n).CopyTo(destination[done..]);
                Clip(cached.Unreadable, pos, n, bad);
                done += n;
                continue;
            }

            // 次にキャッシュにあるブロックの手前まで、まとめて直接読む。
            int run = Math.Min(BlockSize - inBlock, count - done);
            while (done + run < count && !IsCached((sourceOffset + done + run) / BlockSize))
            {
                run = Math.Min(run + BlockSize, count - done);
            }

            ReadResult r = ReadWithRetry(pos, destination.Slice(done, run));
            bad.AddRange(r.Unreadable);
            done += run;
        }

        return new ReadResult(count, bad);
    }

    /// <summary>キャッシュをすべて捨てる (外部変更、表示の更新。ENG-06 の仕様 9・10)。</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _map.Clear();
            _lru.Clear();
        }
    }

    private bool IsCached(long index)
    {
        lock (_lock)
        {
            return _map.ContainsKey(index);
        }
    }

    private async Task WorkerLoop()
    {
        while (true)
        {
            long index;
            int generation;
            lock (_lock)
            {
                if (_disposed || !TryDequeue(out index))
                {
                    _running--;
                    return;
                }

                _inFlight.Add(index);
                generation = _generation;
            }

            CachedBlock? block = null;
            try
            {
                block = await LoadAsync(index).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight.Remove(index);
                    if (block is not null && !_disposed && generation == _generation && !_map.ContainsKey(index))
                    {
                        _map[index] = _lru.AddFirst(block);
                        EvictIfNeeded();
                    }
                }
            }

            if (block is not null)
            {
                BlockLoaded?.Invoke(index * BlockSize, block.Length);
            }
        }
    }

    private bool TryDequeue(out long index)
    {
        while (_pending.TryDequeue(out index, out (int Priority, long Sequence) key))
        {
            if (_pendingPriority.TryGetValue(index, out int p) && p == key.Priority)
            {
                _pendingPriority.Remove(index);
                return true;
            }
        }

        return false;
    }

    private async Task<CachedBlock> LoadAsync(long index)
    {
        long offset = index * BlockSize;
        int length = (int)Math.Min(BlockSize, _source.Length - offset);
        byte[] data = new byte[BlockSize];
        ReadResult result = await _source.ReadAsync(offset, data.AsMemory(0, length)).ConfigureAwait(false);
        if (!result.IsComplete)
        {
            result = ReadAfterFailure(offset, data.AsSpan(0, length));
        }

        return new CachedBlock(index, data, result.BytesReturned, result.Unreadable);
    }

    private ReadResult ReadWithRetry(long offset, Span<byte> destination)
    {
        ReadResult result = _source.Read(offset, destination);
        return result.IsComplete ? result : ReadAfterFailure(offset, destination);
    }

    /// <summary>
    /// 読み込みに失敗したときの処理 (ENG-06 の仕様 8): 1 回だけ再試行し、それでも失敗したら論理セクタ単位
    /// (ファイルは 4 KiB 単位) に分けて読み、失敗した部分だけを読めない範囲にする。
    /// </summary>
    private ReadResult ReadAfterFailure(long offset, Span<byte> destination)
    {
        ReadResult retry = _source.Read(offset, destination);
        if (retry.IsComplete)
        {
            return retry;
        }

        int unit = _source.LogicalSectorSize > 1 ? _source.LogicalSectorSize : FileByteSource.ErrorSplitSize;
        var bad = new List<UnreadableRange>();
        for (int pos = 0; pos < destination.Length; pos += unit)
        {
            int n = Math.Min(unit, destination.Length - pos);
            Span<byte> part = destination.Slice(pos, n);
            ReadResult r = _source.Read(offset + pos, part);
            foreach (UnreadableRange u in r.Unreadable)
            {
                if (bad.Count > 0 && bad[^1].End == u.Offset && bad[^1].Reason == u.Reason && bad[^1].ErrorCode == u.ErrorCode)
                {
                    bad[^1] = bad[^1] with { Length = bad[^1].Length + u.Length };
                }
                else
                {
                    bad.Add(u);
                }
            }
        }

        return new ReadResult(destination.Length, bad);
    }

    /// <summary>キャッシュにあるブロックの番号 (テストと診断用)。</summary>
    public IReadOnlyList<long> CachedBlockIndexes()
    {
        lock (_lock)
        {
            return [.. _map.Keys];
        }
    }

    /// <summary>キャッシュを指定の大きさまで縮める (メモリ不足時。ENG-08 の仕様 3)。上限は変えない。</summary>
    public void Trim(long targetBytes)
    {
        lock (_lock)
        {
            while ((long)_map.Count * BlockSize > targetBytes && _lru.Last is { } last)
            {
                _map.Remove(last.Value.Index);
                _lru.RemoveLast();
            }
        }
    }

    private void EvictIfNeeded()
    {
        while ((long)_map.Count * BlockSize > _capacityBytes && _lru.Last is { } last)
        {
            _map.Remove(last.Value.Index);
            _lru.RemoveLast();
        }
    }

    private static void Clip(IReadOnlyList<UnreadableRange> ranges, long offset, int length, List<UnreadableRange> output)
    {
        long end = offset + length;
        foreach (UnreadableRange r in ranges)
        {
            long from = Math.Max(r.Offset, offset);
            long to = Math.Min(r.End, end);
            if (from < to)
            {
                output.Add(r with { Offset = from, Length = to - from });
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending.Clear();
            _pendingPriority.Clear();
            _map.Clear();
            _lru.Clear();
        }
    }
}
