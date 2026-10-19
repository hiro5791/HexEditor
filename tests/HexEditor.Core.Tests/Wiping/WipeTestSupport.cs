using HexEditor.Core.Wiping;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.Wiping;

/// <summary>監査ログ・書き込み保護・証拠を記録する偽の口 (ForensicsService の代わり)。</summary>
public sealed class RecordingWipeHooks : IWipeForensicHooks
{
    public bool WriteBlock { get; set; }

    public HashSet<string> Evidence { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Operation, string Target, WipeAuditOutcome Outcome, IReadOnlyDictionary<string, string> Details)> Records { get; } = [];

    public bool IsWriteBlockEnabled => WriteBlock;

    public bool IsEvidence(string identity, string? path) => Evidence.Contains(identity) || path is not null && Evidence.Contains(path);

    public string? EnsureCanWrite(string operation, string target) => null;

    public void Audit(string operation, string target, WipeAuditOutcome outcome, IReadOnlyDictionary<string, string> details) =>
        Records.Add((operation, target, outcome, details));

    public string? CheckOutput(string path) => null;

    public event EventHandler? WriteBlockChanged
    {
        add { }
        remove { }
    }
}

/// <summary>メモリ上のイメージへの書き込み先 (<paramref name="baseOffset"/> から)。書き込み・読み込みの回数を数える。</summary>
public sealed class SparseImageWipeTarget(SparseImage image, long baseOffset = 0, long? length = null) : IWipeTarget
{
    public int Writes;

    public int Flushes;

    public Func<long, bool>? FailAt { get; set; }

    public string Name => "image";

    public long Length { get; } = length ?? image.Length - baseOffset;

    public int SectorSize => 512;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (FailAt?.Invoke(offset) == true)
        {
            throw new IOException($"write error at {offset}");
        }

        Writes++;
        image.Write(baseOffset + offset, data);
    }

    public void Read(long offset, Span<byte> buffer) => image.Read(baseOffset + offset, buffer);

    public void Flush() => Flushes++;

    public void Dispose()
    {
    }
}

/// <summary>
/// OS 経由の空き領域の消去の偽のボリューム。作ったファイルは、イメージの空き領域 (<paramref name="free"/> の範囲) を先頭から順に使って書く
/// (OS がファイルに空きクラスタを割り当てるのと同じ)。空きがなくなると「ディスクがいっぱい」の <see cref="IOException"/> にする。
/// 削除したファイルの領域は空きに戻す (内容は上書きされたまま)。
/// </summary>
public sealed class FakeFreeSpaceVolume : IFreeSpaceVolume
{
    private readonly SparseImage _image;
    private readonly LinkedList<(long Offset, long Length)> _free;
    private readonly Dictionary<string, List<(long Offset, long Length)>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public FakeFreeSpaceVolume(SparseImage image, IEnumerable<(long Offset, long Length)> free, string fileSystem = "NTFS", int smallFileCapacity = 100)
    {
        _image = image;
        _free = new LinkedList<(long, long)>(free);
        FileSystem = fileSystem;
        SmallFileCapacity = smallFileCapacity;
    }

    public string Root => @"X:\";

    public string FileSystem { get; }

    /// <summary>MFT の空きレコードの数 (小さいファイルはこの数まで作れる)。</summary>
    public int SmallFileCapacity { get; set; }

    public long MaxFileSize { get; set; } = long.MaxValue;

    /// <summary>書き込みのたびに呼ぶ (キャンセルのテスト)。</summary>
    public Action? OnWrite { get; set; }

    public IReadOnlyCollection<string> Files => _files.Keys;

    /// <summary>小さいファイルに書いた量。</summary>
    public long SmallBytes { get; private set; }

    public IReadOnlyCollection<string> Directories => _directories;

    public long FreeBytes() => _free.Sum(f => f.Length);

    public void CreateDirectory(string path) => _directories.Add(path);

    public Stream CreateFile(string path)
    {
        if (!_directories.Contains(Path.GetDirectoryName(path)!))
        {
            throw new DirectoryNotFoundException(path);
        }

        if (Path.GetFileName(path).StartsWith('s') && _files.Keys.Count(k => Path.GetFileName(k).StartsWith('s')) >= SmallFileCapacity)
        {
            throw new IOException("no free MFT records") { HResult = unchecked((int)0x80070070) };
        }

        _files[path] = [];
        return new FakeStream(this, path);
    }

    public Stream OpenFile(string path) => _files.ContainsKey(path) ? new FakeStream(this, path) : throw new FileNotFoundException(path);

    public void DeleteFile(string path)
    {
        if (_files.Remove(path, out List<(long, long)>? extents))
        {
            foreach ((long offset, long length) in extents)
            {
                _free.AddLast((offset, length));
            }
        }
    }

    public void DeleteDirectory(string path)
    {
        if (_files.Keys.Any(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("the directory is not empty");
        }

        _directories.Remove(path);
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _directories.Contains(path);

    private void Write(string path, long position, ReadOnlySpan<byte> data)
    {
        OnWrite?.Invoke();
        if (Path.GetFileName(path).StartsWith('s'))
        {
            // 小さいファイルは MFT のレコードに常駐する (クラスタを使わない)。
            SmallBytes += data.Length;
            return;
        }

        List<(long Offset, long Length)> extents = _files[path];
        long allocated = extents.Sum(e => e.Length);
        if (position + data.Length - allocated > FreeBytes())
        {
            // 書けない書き込みはクラスタを割り当てない (OS と同じ)。
            throw new IOException("There is not enough space on the disk.") { HResult = unchecked((int)0x80070070) };
        }

        while (allocated < position + data.Length)
        {
            if (_free.First is not { } node)
            {
                throw new IOException("There is not enough space on the disk.") { HResult = unchecked((int)0x80070070) };
            }

            long need = position + data.Length - allocated;
            (long offset, long length) = node.Value;
            long take = Math.Min(need, length);
            extents.Add((offset, take));
            allocated += take;
            if (take == length)
            {
                _free.RemoveFirst();
            }
            else
            {
                node.Value = (offset + take, length - take);
            }
        }

        // 位置からイメージ上の位置へ。
        long logical = 0;
        int done = 0;
        foreach ((long offset, long length) in extents)
        {
            long from = Math.Max(position + done, logical);
            long to = Math.Min(position + data.Length, logical + length);
            if (from < to)
            {
                _image.Write(offset + (from - logical), data.Slice((int)(from - position), (int)(to - from)));
                done = (int)(to - position);
            }

            logical += length;
        }
    }

    private sealed class FakeStream(FakeFreeSpaceVolume volume, string path) : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => _length + offset,
        };

        public override void SetLength(long value) => _length = Math.Min(_length, value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            volume.Write(path, Position, buffer.AsSpan(offset, count));
            Position += count;
            _length = Math.Max(_length, Position);
        }
    }
}

public static class WipeAssert
{
    /// <summary>範囲に文字列 (ASCII) があるか。</summary>
    public static bool Contains(SparseImage image, long offset, long length, string text)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes(text);
        const int Block = 1 << 20;
        byte[] buffer = new byte[Block + needle.Length];
        for (long at = offset; at < offset + length; at += Block)
        {
            int n = (int)Math.Min(buffer.Length, offset + length - at);
            image.Read(at, buffer.AsSpan(0, n));
            if (buffer.AsSpan(0, n).IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
