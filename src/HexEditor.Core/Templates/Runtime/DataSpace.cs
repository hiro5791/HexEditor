using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Templates;

/// <summary>
/// テンプレートを適用するデータ (TPL-20)。評価は開始時点のスナップショット (不変) に対して行い、読み込みはデータエンジンの
/// ブロックキャッシュを通す (TPL-28 の仕様 7・8)。
/// </summary>
public abstract class TemplateDataSource
{
    public abstract long Length { get; }

    /// <summary>ドキュメントのファイル名 (フォルダを含まない。<c>std::sys::file_name()</c>。TPL-37)。</summary>
    public string? FileName { get; init; }

    /// <summary>ファイル形式の判定の結果の MIME (<c>std::sys::detected_mime()</c>)。</summary>
    public string? DetectedMime { get; init; }

    /// <summary>
    /// 読む。末尾を越えた部分は返さない。読めない範囲があった場合は、その先頭を <paramref name="unreadableAt"/> に返す (なければ -1)。
    /// </summary>
    public abstract int Read(long offset, Span<byte> destination, out long unreadableAt);

    public static TemplateDataSource FromSnapshot(DocumentSnapshot snapshot, string? fileName = null) =>
        new SnapshotData(snapshot) { FileName = fileName is null ? null : Path.GetFileName(fileName) };

    public static TemplateDataSource FromBytes(byte[] data, string? fileName = null) =>
        new BytesData(data) { FileName = fileName is null ? null : Path.GetFileName(fileName) };

    public static TemplateDataSource FromByteSource(IByteSource source, string? fileName = null) =>
        new SourceData(source) { FileName = fileName is null ? null : Path.GetFileName(fileName) };

    private sealed class SnapshotData(DocumentSnapshot snapshot) : TemplateDataSource
    {
        public override long Length => snapshot.Length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            ReadResult r = snapshot.ReadThroughCache(offset, destination);
            unreadableAt = r.IsComplete ? -1 : r.Unreadable.Min(u => u.Offset);
            return r.BytesReturned;
        }
    }

    private sealed class BytesData(byte[] data) : TemplateDataSource
    {
        public override long Length => data.Length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            unreadableAt = -1;
            if (offset >= data.Length)
            {
                return 0;
            }

            int n = (int)Math.Min(destination.Length, data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    private sealed class SourceData(IByteSource source) : TemplateDataSource
    {
        public override long Length => source.Length;

        public override int Read(long offset, Span<byte> destination, out long unreadableAt)
        {
            ReadResult r = source.Read(offset, destination);
            unreadableAt = r.IsComplete ? -1 : r.Unreadable.Min(u => u.Offset);
            return r.BytesReturned;
        }
    }
}

/// <summary>
/// セクション (TPL-15)。ドキュメントとは別の 0 から始まるアドレス空間を持つ読み取り専用のバイト列。メモリには 64 MB まで置き、
/// 超える分は一時ファイルに置く (仕様 6)。
/// </summary>
public sealed class TemplateSection : IDisposable
{
    public const long MemoryBytes = 64 * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;

    private readonly List<byte[]> _chunks = [];
    private FileStream? _file;
    private string? _filePath;
    private long _length;

    internal TemplateSection(string name, string tempDirectory)
    {
        Name = name;
        TempDirectory = tempDirectory;
    }

    public string Name { get; }

    public long Length => Volatile.Read(ref _length);

    /// <summary>上限に達して打ち切った (仕様 8)。</summary>
    public bool Truncated { get; internal set; }

    /// <summary>メモリ上に置いたバイト数 (TC-TPL-15-03)。</summary>
    public long MemoryLength => Math.Min(_length, MemoryBytes);

    /// <summary>一時ファイルに置いたバイト数。</summary>
    public long FileLength => Math.Max(0, _length - MemoryBytes);

    internal string TempDirectory { get; }

    /// <summary>セクションの中身を作る処理 (遅延評価。仕様 7)。</summary>
    internal List<Action> PendingProducers { get; } = [];

    internal TemplateNode? DisplayNode { get; set; }

    internal int Index { get; set; }

    private readonly object _lock = new();

    internal void Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            while (data.Length > 0)
            {
                if (_length < MemoryBytes)
                {
                    int chunk = (int)(_length / ChunkSize);
                    int at = (int)(_length % ChunkSize);
                    if (chunk == _chunks.Count)
                    {
                        _chunks.Add(new byte[ChunkSize]);
                    }

                    int n = Math.Min(data.Length, ChunkSize - at);
                    n = (int)Math.Min(n, MemoryBytes - _length);
                    data[..n].CopyTo(_chunks[chunk].AsSpan(at));
                    data = data[n..];
                    Volatile.Write(ref _length, _length + n);
                    continue;
                }

                if (_file is null)
                {
                    Directory.CreateDirectory(TempDirectory);
                    _filePath = Path.Combine(TempDirectory, $"section-{Guid.NewGuid():N}.tmp");
                    _file = new FileStream(_filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096,
                        FileOptions.DeleteOnClose | FileOptions.RandomAccess);
                }

                _file.Seek(_length - MemoryBytes, SeekOrigin.Begin);
                _file.Write(data);
                Volatile.Write(ref _length, _length + data.Length);
                data = default;
            }
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        lock (_lock)
        {
            if (offset >= _length || offset < 0)
            {
                return 0;
            }

            int total = (int)Math.Min(destination.Length, _length - offset);
            int done = 0;
            while (done < total)
            {
                long pos = offset + done;
                if (pos < MemoryBytes)
                {
                    int chunk = (int)(pos / ChunkSize);
                    int at = (int)(pos % ChunkSize);
                    int n = Math.Min(total - done, ChunkSize - at);
                    n = (int)Math.Min(n, MemoryBytes - pos);
                    _chunks[chunk].AsSpan(at, n).CopyTo(destination[done..]);
                    done += n;
                }
                else
                {
                    _file!.Seek(pos - MemoryBytes, SeekOrigin.Begin);
                    int n = _file.Read(destination.Slice(done, total - done));
                    if (n <= 0)
                    {
                        break;
                    }

                    done += n;
                }
            }

            return done;
        }
    }

    /// <summary>セクションの内容を読み取り専用のデータソースにする (「セクションを新しいタブで開く」。TPL-15 の仕様 4)。</summary>
    public IByteSource CreateByteSource() => new SectionByteSource(this);

    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
            _chunks.Clear();
        }
    }

    private sealed class SectionByteSource(TemplateSection section) : ByteSourceBase
    {
        public override string DisplayName => section.Name;

        public override string Identity => $"template-section:{section.Name}:{section.GetHashCode():X8}";

        public override long Length => section.Length;

        public override SourceCapabilities Capabilities => SourceCapabilities.None;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int n = ClampToLength(offset, buffer.Length);
            return new ReadResult(section.Read(offset, buffer[..n]));
        }
    }
}
