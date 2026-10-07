using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>
/// 入力・貼り付けなどで加わったデータを追記していくバッファ (ENG-04)。
/// 追記専用で、一度書いた位置の内容は変えない。メモリ上のチャンクが上限を超えたら、最も長く使われていない
/// チャンクを一時ファイルへ退避する。読み込みはスレッドセーフ。
/// </summary>
public sealed class AddBuffer : IDisposable
{
    public const int ChunkSize = 1024 * 1024;

    private const int InitialChunkCapacity = 4096;

    private readonly object _lock = new();
    private readonly List<Chunk> _chunks = [];
    private readonly string _spillPath;
    private SafeFileHandle? _spillFile;
    private long _length;
    private long _persistedLength;
    private long _useClock;
    private bool _disposed;

    /// <param name="spillPath">退避先の一時ファイルのパス。最初に退避するときに作る。</param>
    /// <param name="memoryLimit">メモリに置くチャンクの合計の上限 (バイト)。</param>
    public AddBuffer(string spillPath, long memoryLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryLimit, ChunkSize);
        _spillPath = spillPath;
        MemoryLimit = memoryLimit;
    }

    /// <summary>メモリに置くチャンクの合計の上限。</summary>
    public long MemoryLimit { get; set; }

    /// <summary>追記したデータの合計 (メモリと一時ファイルの合計)。</summary>
    public long Length => Interlocked.Read(ref _length);

    /// <summary>メモリ上のチャンクの合計 (バイト)。</summary>
    public long MemoryBytes
    {
        get
        {
            lock (_lock)
            {
                return _chunks.Sum(c => (long)(c.Data?.Length ?? 0));
            }
        }
    }

    /// <summary>一時ファイルに退避したデータの量 (バイト)。</summary>
    public long SpilledBytes
    {
        get
        {
            lock (_lock)
            {
                return _chunks.Count(c => c.Data is null) * (long)ChunkSize;
            }
        }
    }

    public string SpillPath => _spillPath;

    /// <summary>
    /// 既存の一時ファイルを追加バッファとして開き直す (復旧。ENG-27 の仕様 6)。内容はすべて一時ファイルにあるものとして扱い、
    /// 続きの追記もできる。
    /// </summary>
    public static AddBuffer OpenExisting(string spillPath, long length, long memoryLimit)
    {
        var buffer = new AddBuffer(spillPath, memoryLimit);
        SafeFileHandle file = File.OpenHandle(spillPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.RandomAccess);
        if (RandomAccess.GetLength(file) < length)
        {
            file.Dispose();
            throw new InvalidDataException("追加バッファの一時ファイルが記録より短いため、復旧できません。");
        }

        buffer._spillFile = file;
        int chunks = (int)((length + ChunkSize - 1) / ChunkSize);
        for (int i = 0; i < chunks; i++)
        {
            buffer._chunks.Add(new Chunk { Data = null });
        }

        buffer._length = length;
        buffer._persistedLength = length;
        return buffer;
    }

    /// <summary>
    /// メモリ上にしかない追記分を一時ファイルに書き、ディスクに反映する (復旧用データ。ENG-27 の仕様 2)。
    /// 書くのは前回からの追記分だけで、メモリ上のチャンクはそのまま残す。書いた後の長さを返す。
    /// </summary>
    public long Persist()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long end = _length;
            if (_persistedLength < end)
            {
                SafeFileHandle file = EnsureSpillFile();
                long pos = _persistedLength;
                while (pos < end)
                {
                    int index = (int)(pos / ChunkSize);
                    int inChunk = (int)(pos % ChunkSize);
                    int n = (int)Math.Min(ChunkSize - inChunk, end - pos);
                    if (_chunks[index].Data is { } data)
                    {
                        RandomAccess.Write(file, data.AsSpan(inChunk, n), pos);
                    }

                    pos += n;
                }

                _persistedLength = end;
            }

            if (_spillFile is not null)
            {
                RandomAccess.FlushToDisk(_spillFile);
            }

            return end;
        }
    }


    /// <summary>
    /// データを追記し、追記した位置を返す。失敗した場合 (一時ファイルへの書き込みの失敗など) は例外を投げ、
    /// 追記しなかったものとして扱う (追記済みの長さは変わらない)。
    /// </summary>
    public long Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long start = _length;
            try
            {
                if (data.Length > MemoryLimit)
                {
                    // 上限を超える 1 回の追記はメモリを経由せず直接一時ファイルに書く (ENG-04 の仕様 6)。
                    AppendDirectToFile(data, start);
                }
                else
                {
                    AppendToMemory(data, start);
                    SpillIfNeeded();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new TempFileWriteException(_spillPath, ex);
            }

            Interlocked.Exchange(ref _length, start + data.Length);
            return start;
        }
    }

    /// <summary><paramref name="offset"/> から <paramref name="destination"/> の長さ分を読む。</summary>
    public void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || destination.Length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        int done = 0;
        while (done < destination.Length)
        {
            long pos = offset + done;
            int index = (int)(pos / ChunkSize);
            int inChunk = (int)(pos % ChunkSize);
            int n = Math.Min(ChunkSize - inChunk, destination.Length - done);
            byte[]? data;
            lock (_lock)
            {
                Chunk chunk = _chunks[index];
                chunk.LastUse = ++_useClock;
                data = chunk.Data;
            }

            if (data is not null)
            {
                data.AsSpan(inChunk, n).CopyTo(destination[done..]);
            }
            else
            {
                ReadSpilled(pos, destination.Slice(done, n));
            }

            done += n;
        }
    }

    /// <summary>メモリ上のチャンクを上限まで退避する (メモリ不足時に呼ぶ。ENG-08)。</summary>
    public void Trim(long targetMemoryBytes)
    {
        lock (_lock)
        {
            SpillUntil(targetMemoryBytes);
        }
    }

    private void AppendToMemory(ReadOnlySpan<byte> data, long start)
    {
        int done = 0;
        while (done < data.Length)
        {
            long pos = start + done;
            int index = (int)(pos / ChunkSize);
            int inChunk = (int)(pos % ChunkSize);
            Chunk chunk = GetOrAddChunk(index, inMemory: true);
            if (chunk.Data is null)
            {
                // 退避済みのチャンクの続きに書く場合は読み戻す (末尾のチャンクは通常退避しないため稀)。
                byte[] restored = new byte[ChunkSize];
                ReadSpilled((long)index * ChunkSize, restored.AsSpan(0, inChunk));
                chunk.Data = restored;
            }

            int n = Math.Min(ChunkSize - inChunk, data.Length - done);
            EnsureCapacity(chunk, inChunk + n);
            data.Slice(done, n).CopyTo(chunk.Data.AsSpan(inChunk));
            chunk.LastUse = ++_useClock;
            done += n;
        }
    }

    private void AppendDirectToFile(ReadOnlySpan<byte> data, long start)
    {
        SafeFileHandle file = EnsureSpillFile();

        // 書きかけのメモリ上のチャンクがあれば、先にその続きとして一緒に書けるよう退避する。
        int firstIndex = (int)(start / ChunkSize);
        if (firstIndex < _chunks.Count && _chunks[firstIndex].Data is { } partial)
        {
            RandomAccess.Write(file, partial.AsSpan(0, (int)(start % ChunkSize)), (long)firstIndex * ChunkSize);
            _chunks[firstIndex].Data = null;
        }

        RandomAccess.Write(file, data, start);
        int lastIndex = (int)((start + data.Length - 1) / ChunkSize);
        for (int i = firstIndex; i <= lastIndex; i++)
        {
            GetOrAddChunk(i, inMemory: false).Data = null;
        }
    }

    private Chunk GetOrAddChunk(int index, bool inMemory)
    {
        while (_chunks.Count <= index)
        {
            _chunks.Add(new Chunk { Data = inMemory ? new byte[InitialChunkCapacity] : null });
        }

        return _chunks[index];
    }

    /// <summary>
    /// 書きかけのチャンクは小さく確保して必要に応じて広げる。少しの入力でメモリを 1 MiB 使わないようにするため。
    /// </summary>
    private static void EnsureCapacity(Chunk chunk, int required)
    {
        if (chunk.Data!.Length >= required)
        {
            return;
        }

        int capacity = chunk.Data.Length;
        while (capacity < required)
        {
            capacity = Math.Min(ChunkSize, capacity * 2);
        }

        byte[] grown = new byte[capacity];
        chunk.Data.CopyTo(grown, 0);
        chunk.Data = grown;
    }

    private void SpillIfNeeded() => SpillUntil(MemoryLimit);

    private void SpillUntil(long targetMemoryBytes)
    {
        long inMemory = _chunks.Sum(c => (long)(c.Data?.Length ?? 0));
        int tail = _chunks.Count - 1;
        while (inMemory > targetMemoryBytes)
        {
            // 末尾 (書きかけ) 以外で最も長く使われていないチャンクを選ぶ。
            int victim = -1;
            for (int i = 0; i < tail; i++)
            {
                if (_chunks[i].Data is not null && (victim < 0 || _chunks[i].LastUse < _chunks[victim].LastUse))
                {
                    victim = i;
                }
            }

            if (victim < 0)
            {
                return;
            }

            byte[] victimData = _chunks[victim].Data!;
            RandomAccess.Write(EnsureSpillFile(), victimData, (long)victim * ChunkSize);
            _chunks[victim].Data = null;
            inMemory -= victimData.Length;
        }
    }

    private SafeFileHandle EnsureSpillFile()
    {
        if (_spillFile is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_spillPath)!);
            _spillFile = File.OpenHandle(_spillPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.RandomAccess);
        }

        return _spillFile;
    }

    private void ReadSpilled(long offset, Span<byte> destination)
    {
        SafeFileHandle file = _spillFile ?? throw new InvalidOperationException("一時ファイルがありません。");
        int done = 0;
        while (done < destination.Length)
        {
            int n = RandomAccess.Read(file, destination[done..], offset + done);
            if (n == 0)
            {
                throw new IOException("一時ファイルの内容が足りません。");
            }

            done += n;
        }
    }

    /// <summary>バッファを解放するが、一時ファイルは残す (復旧に失敗したとき、復旧用データを消さないため)。</summary>
    public void DisposeKeepingFile()
    {
        lock (_lock)
        {
            _disposed = true;
            _chunks.Clear();
            _spillFile?.Dispose();
            _spillFile = null;
        }
    }

    /// <summary>バッファを解放し、一時ファイルを削除する (ENG-04 の仕様 5)。</summary>
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
            if (_spillFile is not null)
            {
                _spillFile.Dispose();
                _spillFile = null;
            }

            // ドキュメントごとのフォルダ (recovery/<ドキュメント ID>/) ごと消す。
            try
            {
                string? folder = Path.GetDirectoryName(_spillPath);
                if (File.Exists(_spillPath))
                {
                    File.Delete(_spillPath);
                }

                if (folder is not null && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (IOException)
            {
                // 削除できなかったものは次回起動時の復旧処理 (ENG-27) で片付ける。
            }
        }
    }

    private sealed class Chunk
    {
        public byte[]? Data { get; set; }

        public long LastUse { get; set; }
    }
}

/// <summary>追加バッファの一時ファイルに書き込めない (ENG-04 の「エラー」)。編集は適用されない。</summary>
public sealed class TempFileWriteException(string path, Exception inner)
    : IOException($"一時ファイルに書き込めないため、この編集を適用できませんでした。理由: {inner.Message} 一時ファイルの場所: {path}", inner)
{
    public string TempFilePath { get; } = path;
}
