using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Remote.Udp;

/// <summary>
/// 末尾に追記し、先頭から捨てられるバイト列 (ENG-42 の仕様 1・4)。位置は受信開始からの通算の位置 (捨てたバイトを含む)。
/// 1 MiB の塊で持ち、メモリに置く量が <see cref="MemoryLimit"/> を超えたら古い塊から一時ファイルに移す (一時ファイルの塊の場所は使い回すので、
/// ファイルは保持する量より大きくならない)。スレッドセーフ。
/// </summary>
public sealed class AppendStore : IDisposable
{
    /// <summary>塊の大きさ。</summary>
    public const int ChunkSize = 1024 * 1024;

    private readonly object _lock = new();
    private readonly Dictionary<long, Chunk> _chunks = [];
    private readonly Stack<long> _freeSlots = new();
    private readonly string _tempDirectory;
    private SafeFileHandle? _file;
    private string? _filePath;
    private long _nextSlot;
    private long _start;
    private long _end;
    private long _inMemory;
    private bool _disposed;

    private sealed class Chunk
    {
        public byte[]? Data;

        /// <summary>一時ファイルの中の位置 (塊の番号)。メモリにある間は -1。</summary>
        public long Slot = -1;
    }

    public AppendStore(long memoryLimit, string tempDirectory)
    {
        MemoryLimit = Math.Max(ChunkSize, memoryLimit);
        _tempDirectory = tempDirectory;
    }

    /// <summary>メモリに置く上限 (超えた分は一時ファイル)。</summary>
    public long MemoryLimit { get; }

    /// <summary>保持している先頭の通算の位置 (= 捨てたバイト数)。</summary>
    public long Start
    {
        get
        {
            lock (_lock)
            {
                return _start;
            }
        }
    }

    /// <summary>記録したバイトの総数 (捨てたバイトを含む)。</summary>
    public long End
    {
        get
        {
            lock (_lock)
            {
                return _end;
            }
        }
    }

    /// <summary>保持しているバイト数。</summary>
    public long Length
    {
        get
        {
            lock (_lock)
            {
                return _end - _start;
            }
        }
    }

    /// <summary>一時ファイルに移したバイト数 (診断・テスト用)。</summary>
    public long SpilledBytes
    {
        get
        {
            lock (_lock)
            {
                return _chunks.Values.Count(c => c.Slot >= 0) * (long)ChunkSize;
            }
        }
    }

    /// <summary>末尾に追記する。追記した先頭の通算の位置を返す。</summary>
    public long Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long at = _end;
            int pos = 0;
            while (pos < data.Length)
            {
                long index = _end / ChunkSize;
                int inChunk = (int)(_end % ChunkSize);
                if (!_chunks.TryGetValue(index, out Chunk? chunk))
                {
                    chunk = new Chunk { Data = new byte[ChunkSize] };
                    _chunks[index] = chunk;
                    _inMemory += ChunkSize;
                }

                int n = Math.Min(ChunkSize - inChunk, data.Length - pos);
                if (chunk.Data is null)
                {
                    // 一時ファイルに移した塊には追記しない (末尾の塊は常にメモリにある) が、念のため読み戻す。
                    Load(chunk);
                }

                data.Slice(pos, n).CopyTo(chunk.Data.AsSpan(inChunk));
                pos += n;
                _end += n;
            }

            SpillIfNeeded();
            return at;
        }
    }

    /// <summary>先頭から <paramref name="count"/> バイトを捨てる (ENG-42 の仕様 4 の「古いデータを捨てて続ける」)。</summary>
    public void DiscardFront(long count)
    {
        lock (_lock)
        {
            long newStart = Math.Min(_end, _start + Math.Max(0, count));
            for (long index = _start / ChunkSize; index < newStart / ChunkSize; index++)
            {
                if (_chunks.Remove(index, out Chunk? chunk))
                {
                    Release(chunk);
                }
            }

            _start = newStart;
        }
    }

    /// <summary>すべて捨てて、通算の位置を 0 に戻す (「消去」。ENG-42 の仕様 5)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (Chunk chunk in _chunks.Values)
            {
                Release(chunk);
            }

            _chunks.Clear();
            _start = 0;
            _end = 0;
        }
    }

    /// <summary>通算の位置 <paramref name="position"/> から読む。保持していない範囲は 0 にし、読めたバイト数 (保持している範囲) を返す。</summary>
    public int Read(long position, Span<byte> destination, out int skippedFront)
    {
        lock (_lock)
        {
            destination.Clear();
            skippedFront = 0;
            if (position < _start)
            {
                skippedFront = (int)Math.Min(destination.Length, _start - position);
            }

            long from = Math.Max(position, _start);
            long to = Math.Min(position + destination.Length, _end);
            for (long at = from; at < to;)
            {
                long index = at / ChunkSize;
                int inChunk = (int)(at % ChunkSize);
                int n = (int)Math.Min(ChunkSize - inChunk, to - at);
                Chunk chunk = _chunks[index];
                Span<byte> target = destination.Slice((int)(at - position), n);
                if (chunk.Data is not null)
                {
                    chunk.Data.AsSpan(inChunk, n).CopyTo(target);
                }
                else
                {
                    RandomAccess.Read(_file!, target, (chunk.Slot * ChunkSize) + inChunk);
                }

                at += n;
            }

            return (int)Math.Max(0, to - from);
        }
    }

    private void SpillIfNeeded()
    {
        if (_inMemory <= MemoryLimit)
        {
            return;
        }

        long last = (_end - 1) / ChunkSize;
        foreach (long index in _chunks.Keys.Order())
        {
            if (_inMemory <= MemoryLimit || index >= last)
            {
                break;
            }

            Chunk chunk = _chunks[index];
            if (chunk.Data is null)
            {
                continue;
            }

            EnsureFile();
            long slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot++;
            RandomAccess.Write(_file!, chunk.Data, slot * ChunkSize);
            chunk.Slot = slot;
            chunk.Data = null;
            _inMemory -= ChunkSize;
        }
    }

    private void Load(Chunk chunk)
    {
        byte[] data = new byte[ChunkSize];
        RandomAccess.Read(_file!, data, chunk.Slot * ChunkSize);
        _freeSlots.Push(chunk.Slot);
        chunk.Slot = -1;
        chunk.Data = data;
        _inMemory += ChunkSize;
    }

    private void Release(Chunk chunk)
    {
        if (chunk.Data is not null)
        {
            _inMemory -= ChunkSize;
        }
        else if (chunk.Slot >= 0)
        {
            _freeSlots.Push(chunk.Slot);
        }

        chunk.Data = null;
        chunk.Slot = -1;
    }

    private void EnsureFile()
    {
        if (_file is not null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDirectory);
        _filePath = Path.Combine(_tempDirectory, $"udp-{Guid.NewGuid():N}.tmp");
        _file = File.OpenHandle(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
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
            _chunks.Clear();
            _file?.Dispose();
        }
    }
}
