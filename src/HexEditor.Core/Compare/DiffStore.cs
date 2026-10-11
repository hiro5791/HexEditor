using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Compare;

/// <summary>
/// 差分の一覧 (ANA-02 の「巨大ファイル」)。先頭から順に追加する。メモリには <see cref="MemoryLimit"/> 件 (既定 100 万件) まで持ち、
/// それを超えた分は一時ファイルに書き出す。件数に上限はない。差分は左右とも位置の昇順に並ぶので、位置からの検索は二分探索で行う
/// (ANA-04 の「巨大ファイル」、ANA-05 の「巨大ファイル」)。
/// <para>
/// マージ (ANA-07 の仕様 5) でコピーした差分は一覧から外し、後ろの差分の位置をずらす。どちらも一時ファイルを書き換えず、
/// 外した番号の一覧とずらす量の一覧として重ねて持つ (マージの回数に比例する量)。
/// </para>
/// 比較の処理 (書き込み 1 つ) と画面 (読み込み) から同時に使えるよう、すべての操作をロックで守る。
/// </summary>
public sealed class DiffStore : IDisposable
{
    public const int DefaultMemoryLimit = 1_000_000;

    private const int RecordSize = 40;
    private const int PageEntries = 4096;
    private const int CachedPages = 16;

    private readonly object _lock = new();
    private readonly List<DiffRange> _memory = [];
    private readonly List<DiffRange> _pending = [];
    private readonly string _tempDirectory;
    private readonly List<long> _removed = [];
    private readonly List<(long FromRaw, long DeltaLeft, long DeltaRight)> _shifts = [];
    private readonly Dictionary<long, DiffRange[]> _pages = [];
    private readonly LinkedList<long> _pageOrder = new();
    private SafeFileHandle? _file;
    private string? _filePath;
    private long _written;
    private long _rawCount;
    private bool _disposed;

    /// <param name="memoryLimit">メモリに持つ件数。これを超えた分は一時ファイルに書く。</param>
    /// <param name="tempDirectory">一時ファイルの置き場所 (既定は一時フォルダの HexEditor\compare)。</param>
    public DiffStore(int memoryLimit = DefaultMemoryLimit, string? tempDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memoryLimit);
        MemoryLimit = memoryLimit;
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "HexEditor", "compare");
    }

    public int MemoryLimit { get; }

    /// <summary>一覧の件数 (マージで外した差分を除く)。</summary>
    public long Count
    {
        get
        {
            lock (_lock)
            {
                return _rawCount - _removed.Count;
            }
        }
    }

    /// <summary>一時ファイルに書き出した件数 (テスト用)。</summary>
    public long SpilledCount
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(0, _rawCount - MemoryLimit);
            }
        }
    }

    /// <summary>一時ファイルのパス (書き出していなければ null。テスト用)。</summary>
    public string? SpillPath => _filePath;

    /// <summary>末尾に追加する。位置は直前の差分より後ろでなければならない。一時ファイルに書けない場合は <see cref="IOException"/>。</summary>
    public void Add(DiffRange diff)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_rawCount < MemoryLimit)
            {
                _memory.Add(diff);
            }
            else
            {
                _pending.Add(diff);
                if (_pending.Count >= PageEntries)
                {
                    FlushPending();
                }
            }

            _rawCount++;
        }
    }

    /// <summary><paramref name="index"/> 番目 (0 始まり、外した差分を除いた番号) の差分。</summary>
    public DiffRange this[long index]
    {
        get
        {
            lock (_lock)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _rawCount - _removed.Count);
                long raw = RawIndex(index);
                DiffRange d = ReadRaw(raw);
                if (_shifts.Count == 0)
                {
                    return d;
                }

                long dl = 0;
                long dr = 0;
                foreach ((long from, long l, long r) in _shifts)
                {
                    if (raw >= from)
                    {
                        dl += l;
                        dr += r;
                    }
                }

                return d with { LeftOffset = d.LeftOffset + dl, RightOffset = d.RightOffset + dr };
            }
        }
    }

    /// <summary>
    /// <paramref name="index"/> 番目の差分を外し、その後ろの差分の位置を左は <paramref name="deltaLeft"/>、右は <paramref name="deltaRight"/>
    /// だけずらす (マージで長さが変わった分。ANA-07 の仕様 5)。複数外す場合は、番号の大きい方から外す。
    /// </summary>
    public void RemoveAt(long index, long deltaLeft = 0, long deltaRight = 0)
    {
        lock (_lock)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _rawCount - _removed.Count);
            long raw = RawIndex(index);
            int at = _removed.BinarySearch(raw);
            _removed.Insert(~at, raw);
            if (deltaLeft != 0 || deltaRight != 0)
            {
                _shifts.Add((raw + 1, deltaLeft, deltaRight));
            }
        }
    }

    /// <summary>すべての差分を外す (すべての差分をコピーした)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _memory.Clear();
            _pending.Clear();
            _removed.Clear();
            _shifts.Clear();
            _pages.Clear();
            _pageOrder.Clear();
            _rawCount = 0;
            _written = 0;
            CloseFile();
        }
    }

    /// <summary>
    /// 片側の位置で、<paramref name="offset"/> 以上の位置から始まる最初の差分の番号 (なければ <see cref="Count"/>)。
    /// </summary>
    public long FirstStartingAtOrAfter(bool right, long offset)
    {
        lock (_lock)
        {
            long lo = 0;
            long hi = Count;
            while (lo < hi)
            {
                long mid = lo + (hi - lo) / 2;
                if (this[mid].Start(right) < offset)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }

    /// <summary>片側の位置で、末尾が <paramref name="offset"/> より後ろにある最初の差分の番号 (範囲と重なる差分の先頭を探す)。</summary>
    public long FirstEndingAfter(bool right, long offset)
    {
        lock (_lock)
        {
            long lo = 0;
            long hi = Count;
            while (lo < hi)
            {
                long mid = lo + (hi - lo) / 2;
                DiffRange d = this[mid];
                if (d.Start(right) + d.Length(right) <= offset && !(d.Length(right) == 0 && d.Start(right) == offset))
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }

    /// <summary>[<paramref name="start"/>, <paramref name="end"/>) と重なる差分 (片側の長さ 0 の差分は位置が範囲内のもの)。</summary>
    public List<(long Index, DiffRange Diff)> Overlapping(bool right, long start, long end, int limit = int.MaxValue)
    {
        var result = new List<(long, DiffRange)>();
        lock (_lock)
        {
            long count = Count;
            for (long i = FirstEndingAfter(right, start); i < count && result.Count < limit; i++)
            {
                DiffRange d = this[i];
                if (d.Start(right) >= end)
                {
                    break;
                }

                result.Add((i, d));
            }
        }

        return result;
    }

    /// <summary>片側の位置 <paramref name="offset"/> を含む差分の番号 (なければ -1)。</summary>
    public long IndexContaining(bool right, long offset)
    {
        lock (_lock)
        {
            long i = FirstStartingAtOrAfter(right, offset + 1) - 1;
            for (; i >= 0; i--)
            {
                DiffRange d = this[i];
                if (d.Start(right) + d.Length(right) > offset && d.Start(right) <= offset)
                {
                    return i;
                }

                if (d.Length(right) > 0 || d.Start(right) < offset)
                {
                    break;
                }
            }

            return -1;
        }
    }

    /// <summary>先頭から順に読む (エクスポート・グラフの計算など。読んでいる間も追加できる)。</summary>
    public IEnumerable<DiffRange> Enumerate(long from = 0)
    {
        for (long i = from; i < Count; i++)
        {
            yield return this[i];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            CloseFile();
            _memory.Clear();
            _pending.Clear();
        }
    }

    // ---- 内部 ----

    /// <summary>外した差分を除いた番号から、追加した順の番号を求める (外した番号の数を足していく。最小の不動点が答え)。</summary>
    private long RawIndex(long visible)
    {
        if (_removed.Count == 0)
        {
            return visible;
        }

        long r = visible;
        while (true)
        {
            int at = _removed.BinarySearch(r);
            long removedUpTo = at >= 0 ? at + 1 : ~at;
            long next = visible + removedUpTo;
            if (next == r)
            {
                return r;
            }

            r = next;
        }
    }

    private DiffRange ReadRaw(long raw)
    {
        if (raw < _memory.Count)
        {
            return _memory[(int)raw];
        }

        long spilled = raw - MemoryLimit;
        if (spilled >= _written)
        {
            return _pending[(int)(spilled - _written)];
        }

        long page = spilled / PageEntries;
        if (!_pages.TryGetValue(page, out DiffRange[]? entries))
        {
            entries = LoadPage(page);
            _pages[page] = entries;
            _pageOrder.AddFirst(page);
            if (_pageOrder.Count > CachedPages)
            {
                _pages.Remove(_pageOrder.Last!.Value);
                _pageOrder.RemoveLast();
            }
        }

        return entries[(int)(spilled % PageEntries)];
    }

    private DiffRange[] LoadPage(long page)
    {
        long first = page * PageEntries;
        int count = (int)Math.Min(PageEntries, _written - first);
        byte[] buffer = new byte[count * RecordSize];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = RandomAccess.Read(_file!, buffer.AsSpan(read), first * RecordSize + read);
            if (n <= 0)
            {
                throw new IOException("差分の一時ファイルを読めません。");
            }

            read += n;
        }

        var entries = new DiffRange[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> r = buffer.AsSpan(i * RecordSize, RecordSize);
            entries[i] = new DiffRange(
                (DiffKind)BinaryPrimitives.ReadInt64LittleEndian(r),
                BinaryPrimitives.ReadInt64LittleEndian(r[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[32..]));
        }

        return entries;
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        if (_file is null)
        {
            Directory.CreateDirectory(_tempDirectory);
            _filePath = Path.Combine(_tempDirectory, "diff-" + Guid.NewGuid().ToString("N") + ".tmp");
            _file = File.OpenHandle(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
        }

        byte[] buffer = new byte[_pending.Count * RecordSize];
        for (int i = 0; i < _pending.Count; i++)
        {
            Span<byte> r = buffer.AsSpan(i * RecordSize, RecordSize);
            DiffRange d = _pending[i];
            BinaryPrimitives.WriteInt64LittleEndian(r, (long)d.Kind);
            BinaryPrimitives.WriteInt64LittleEndian(r[8..], d.LeftOffset);
            BinaryPrimitives.WriteInt64LittleEndian(r[16..], d.LeftLength);
            BinaryPrimitives.WriteInt64LittleEndian(r[24..], d.RightOffset);
            BinaryPrimitives.WriteInt64LittleEndian(r[32..], d.RightLength);
        }

        RandomAccess.Write(_file, buffer, _written * RecordSize);
        _written += _pending.Count;
        _pending.Clear();
    }

    private void CloseFile()
    {
        _file?.Dispose();
        _file = null;
        _filePath = null;
    }
}
