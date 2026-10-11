using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Search;

/// <summary>
/// 一致の列 (追記だけ。複数ファイル検索の結果の置き場所。FIND-30 の仕様 8、FIND-20 の仕様 7)。メモリ上に置くのは
/// <see cref="MemoryLimit"/> 件までで、それを超える分は一時ファイルに書き出す (1 件 24 バイト)。一時ファイルを作れなかった場合は、
/// メモリ上の件数で止める (<see cref="SpillFailed"/>)。どのスレッドからでも使える。使い終わったら <see cref="Dispose"/> で一時ファイルを消す。
/// </summary>
public sealed class MatchStore : IDisposable
{
    /// <summary>メモリ上に置く件数の既定の上限 (FIND-20 の仕様 7)。</summary>
    public const int DefaultMemoryLimit = SearchResults.DefaultMemoryLimit;

    /// <summary>一時ファイルの 1 件のバイト数 (オフセット 8、長さ 8、種類 4、予備 4)。</summary>
    public const int RecordSize = 24;

    /// <summary>一時ファイルにまとめて書く件数。</summary>
    private const int WriteBatch = 4096;

    private readonly object _lock = new();
    private readonly List<SearchMatch> _memory = [];
    private readonly List<SearchMatch> _pending = [];
    private SafeFileHandle? _spill;
    private string? _spillPath;
    private long _spilled;
    private bool _disposed;

    /// <summary>メモリ上に置く件数の上限 (テストでは小さくできる)。</summary>
    public int MemoryLimit { get; init; } = DefaultMemoryLimit;

    /// <summary>一時ファイルを置くフォルダ (null なら %TEMP%\HexEditor\search)。</summary>
    public string? SpillDirectory { get; init; }

    /// <summary>一時ファイルを作れなかった・書けなかったため、メモリ上の件数で止めた (FIND-20 の「エラー」)。</summary>
    public bool SpillFailed { get; private set; }

    public string? SpillError { get; private set; }

    /// <summary>件数。</summary>
    public long Count
    {
        get
        {
            lock (_lock)
            {
                return _memory.Count + _spilled + _pending.Count;
            }
        }
    }

    /// <summary>メモリ上に置いている件数 (書き出す前のものを含む。テスト用)。</summary>
    public int InMemoryCount
    {
        get
        {
            lock (_lock)
            {
                return _memory.Count + _pending.Count;
            }
        }
    }

    /// <summary>一時ファイルに書き出した件数。</summary>
    public long SpilledCount
    {
        get
        {
            lock (_lock)
            {
                return _spilled;
            }
        }
    }

    /// <summary>
    /// <paramref name="matches"/> の [0, <paramref name="count"/>) を末尾に加える。加えた件数を返す (一時ファイルに失敗したら少ないことがある)。
    /// <paramref name="first"/> は最初の 1 件の番号。
    /// </summary>
    public int Append(IReadOnlyList<SearchMatch> matches, int count, out long first)
    {
        lock (_lock)
        {
            first = _memory.Count + _spilled + _pending.Count;
            int added = 0;
            for (; added < count; added++)
            {
                SearchMatch m = matches[added];
                if (_spilled == 0 && _pending.Count == 0 && _memory.Count < MemoryLimit)
                {
                    _memory.Add(m);
                    continue;
                }

                if (SpillFailed || _disposed || (_spill is null && !OpenSpill()))
                {
                    break;
                }

                _pending.Add(m);
                if (_pending.Count >= WriteBatch && !FlushPending())
                {
                    break;
                }
            }

            // 呼び出しごとに書き出す (前の呼び出しの分が、後の書き込みの失敗で失われないように)。
            if (!FlushPending())
            {
                // 書けなかった分は捨てる (メモリ上と書き出し済みの件数で止める)。
                return (int)Math.Max(0, _memory.Count + _spilled - first);
            }

            return (int)(_memory.Count + _spilled - first);
        }
    }

    /// <summary><paramref name="index"/> 番目 (0 から) の一致。</summary>
    public SearchMatch this[long index]
    {
        get
        {
            lock (_lock)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index < _memory.Count)
                {
                    return _memory[(int)index];
                }

                long fileIndex = index - _memory.Count;
                if (fileIndex < _spilled)
                {
                    var one = new List<SearchMatch>(1);
                    ReadSpilled(fileIndex, 1, one);
                    return one[0];
                }

                return _pending[(int)(fileIndex - _spilled)];
            }
        }
    }

    /// <summary>[start, start + count) 番目の一致の写し。</summary>
    public IReadOnlyList<SearchMatch> GetRange(long start, int count)
    {
        lock (_lock)
        {
            long total = _memory.Count + _spilled + _pending.Count;
            long end = Math.Min(total, start + Math.Max(0, count));
            var result = new List<SearchMatch>((int)Math.Max(0, end - start));
            for (long i = Math.Max(0, start); i < end; i++)
            {
                if (i < _memory.Count)
                {
                    result.Add(_memory[(int)i]);
                    continue;
                }

                long fileIndex = i - _memory.Count;
                if (fileIndex < _spilled)
                {
                    int n = (int)Math.Min(end - i, _spilled - fileIndex);
                    ReadSpilled(fileIndex, n, result);
                    i += n - 1;
                    continue;
                }

                result.Add(_pending[(int)(fileIndex - _spilled)]);
            }

            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _spill?.Dispose();
            _spill = null;
            TryDelete(_spillPath);
        }
    }

    private void ReadSpilled(long fileIndex, int count, List<SearchMatch> sink)
    {
        byte[] buffer = new byte[Math.Min(count, WriteBatch) * RecordSize];
        long at = fileIndex;
        int left = count;
        while (left > 0)
        {
            int n = Math.Min(left, WriteBatch);
            Span<byte> span = buffer.AsSpan(0, n * RecordSize);
            int read = 0;
            while (read < span.Length)
            {
                int r = RandomAccess.Read(_spill!, span[read..], (at * RecordSize) + read);
                if (r <= 0)
                {
                    throw new IOException("検索結果の一時ファイルを読めません。");
                }

                read += r;
            }

            for (int k = 0; k < n; k++)
            {
                ReadOnlySpan<byte> rec = span.Slice(k * RecordSize, RecordSize);
                sink.Add(new SearchMatch(
                    BinaryPrimitives.ReadInt64LittleEndian(rec),
                    BinaryPrimitives.ReadInt64LittleEndian(rec[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(rec[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(rec[20..])));
            }

            at += n;
            left -= n;
        }
    }

    /// <summary>まだ書いていない一致を一時ファイルに書く。失敗したら書いていない分を捨て、それ以上は加えない (書き出し済みの分は残る)。</summary>
    private bool FlushPending()
    {
        if (_pending.Count == 0)
        {
            return true;
        }

        if (_disposed || _spill is null)
        {
            _pending.Clear();
            return false;
        }

        try
        {
            byte[] buffer = new byte[_pending.Count * RecordSize];
            for (int k = 0; k < _pending.Count; k++)
            {
                Span<byte> rec = buffer.AsSpan(k * RecordSize, RecordSize);
                BinaryPrimitives.WriteInt64LittleEndian(rec, _pending[k].Offset);
                BinaryPrimitives.WriteInt64LittleEndian(rec[8..], _pending[k].Length);
                BinaryPrimitives.WriteInt32LittleEndian(rec[16..], _pending[k].Variant);
                BinaryPrimitives.WriteInt32LittleEndian(rec[20..], _pending[k].Extra);
            }

            RandomAccess.Write(_spill, buffer, _spilled * RecordSize);
            _spilled += _pending.Count;
            _pending.Clear();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SpillFailed = true;
            SpillError = ex.Message;
            _pending.Clear();
            return false;
        }
    }

    /// <summary>一時ファイルを作る。作れなければ <see cref="SpillFailed"/> にして false。</summary>
    private bool OpenSpill()
    {
        try
        {
            string folder = SpillDirectory ?? Path.Combine(Path.GetTempPath(), "HexEditor", "search");
            Directory.CreateDirectory(folder);
            _spillPath = Path.Combine(folder, $"multifile-{Guid.NewGuid():N}.bin");
            _spill = File.OpenHandle(_spillPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SpillFailed = true;
            SpillError = ex.Message;
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
