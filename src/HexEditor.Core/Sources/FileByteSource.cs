using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Sources;

/// <summary>
/// ファイルのデータソース。読み込みは <see cref="RandomAccess"/> を使い、スレッドセーフに位置を指定して読む
/// (ENG-01 の仕様 5)。<c>MemoryMappedFile</c> は使わない。
/// </summary>
public sealed class FileByteSource : ByteSourceBase
{
    /// <summary>読み込みエラーのときに分けて読み直す単位 (ENG-06 の仕様 8)。</summary>
    internal const int ErrorSplitSize = 4096;

    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private readonly object _denyLock = new();
    private SafeFileHandle? _denyHandle;
    private bool _disposed;

    private FileByteSource(string path, SafeFileHandle handle, bool readOnlyAttribute, bool sparse)
    {
        Path = path;
        IsSparse = sparse;
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
        Identity = "file:" + path.ToUpperInvariant();
        Stamp = FileStamp.FromHandle(handle);
        Capabilities = SourceCapabilities.CanResize | SourceCapabilities.CanReplace
            | (readOnlyAttribute ? SourceCapabilities.None : SourceCapabilities.CanWrite);
    }

    public string Path { get; }

    public override string DisplayName => System.IO.Path.GetFileName(Path);

    public override string Identity { get; }

    /// <summary>開いた時点の長さ。外部変更は ENG-19 で検知して開き直す。</summary>
    public override long Length => _length;

    public override SourceCapabilities Capabilities { get; }

    /// <summary>スパースファイルか (開いた時点の属性)。安全な保存で一時ファイルもスパースにする (ENG-22 の仕様 5)。</summary>
    public bool IsSparse { get; }

    /// <summary>
    /// [<paramref name="offset"/>, + <paramref name="length"/>) の中で領域が割り当てられている範囲。スパースでなければ範囲全体
    /// (それ以外の範囲は 00 として読める。ENG-22 の仕様 5、ENG-25 の仕様 1 の見積もり)。
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> AllocatedRanges(long offset, long length)
    {
        length = Math.Min(length, Math.Max(0, _length - offset));
        return !IsSparse ? (length > 0 ? [(offset, length)] : []) : SparseFiles.AllocatedRanges(_handle, offset, length);
    }

    /// <summary>開いた時点の長さ・最終更新日時・ファイル ID (復旧用データで、元のファイルが変わっていないかを調べる。ENG-27)。</summary>
    public FileStamp Stamp { get; }

    /// <summary>
    /// 開いているハンドルで読んだ今の長さ・最終更新日時・ファイル ID (外部変更の検知。ENG-19 の仕様 1)。閉じた後・読めなければ null。
    /// </summary>
    public FileStamp? ReadCurrentStamp()
    {
        try
        {
            return _handle.IsClosed ? null : FileStamp.FromHandle(_handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// ファイルを読み取りで開く。保存中の置き換え (ENG-22) と外部の編集を妨げないよう、共有は読み書き・削除を許す。
    /// </summary>
    public static FileByteSource Open(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
        FileAttributes attributes = File.GetAttributes(fullPath);
        return new FileByteSource(fullPath, handle, attributes.HasFlag(FileAttributes.ReadOnly), attributes.HasFlag(FileAttributes.SparseFile));
    }

    /// <summary>他のアプリの書き込みを禁止するハンドルを開いているか (ENG-15 の仕様 2)。</summary>
    public bool IsWriteDenied
    {
        get
        {
            lock (_denyLock)
            {
                return _denyHandle is not null;
            }
        }
    }

    /// <summary>
    /// 同じファイルに 2 つ目のハンドルを、共有モード「読み取り・削除だけ許可」で開き、他のアプリがこの後書き込み用に開けないようにする
    /// (ENG-15 の仕様 2)。他のアプリがすでに書き込み用に開いている場合などで開けなければ false。
    /// </summary>
    public bool DenyWrites()
    {
        lock (_denyLock)
        {
            if (_disposed)
            {
                return false;
            }

            if (_denyHandle is not null)
            {
                return true;
            }

            try
            {
                _denyHandle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>書き込み禁止のハンドルを閉じる (変更をすべて取り消したとき、保存の前後。ENG-15 の仕様 2・5)。</summary>
    public void AllowWrites()
    {
        lock (_denyLock)
        {
            _denyHandle?.Dispose();
            _denyHandle = null;
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        Span<byte> target = buffer[..count];
        try
        {
            ReadFully(offset, target);
            return new ReadResult(count);
        }
        catch (IOException)
        {
            return ReadWithErrorSplit(offset, target);
        }
        catch (UnauthorizedAccessException)
        {
            return ReadWithErrorSplit(offset, target);
        }
    }

    public override async ValueTask<ReadResult> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count = ClampToLength(offset, buffer.Length);
        Memory<byte> target = buffer[..count];
        try
        {
            int done = 0;
            while (done < count)
            {
                int n = await RandomAccess.ReadAsync(_handle, target[done..], offset + done, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    // 開いた後にファイルが短くなった。残りは読めない範囲として扱う。
                    target.Span[done..].Clear();
                    return new ReadResult(count, [new UnreadableRange(offset + done, count - done, UnreadableReason.IoError)]);
                }

                done += n;
            }

            return new ReadResult(count);
        }
        catch (IOException)
        {
            return ReadWithErrorSplit(offset, target.Span);
        }
    }

    private void ReadFully(long offset, Span<byte> target)
    {
        int done = 0;
        while (done < target.Length)
        {
            int n = RandomAccess.Read(_handle, target[done..], offset + done);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            done += n;
        }
    }

    /// <summary>失敗した読み込みを小さな単位に分けて読み直し、失敗した部分だけを読めない範囲にする。</summary>
    private ReadResult ReadWithErrorSplit(long offset, Span<byte> target)
    {
        var bad = new List<UnreadableRange>();
        for (int pos = 0; pos < target.Length; pos += ErrorSplitSize)
        {
            int len = Math.Min(ErrorSplitSize, target.Length - pos);
            Span<byte> part = target.Slice(pos, len);
            try
            {
                ReadFully(offset + pos, part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                part.Clear();
                var reason = ex is UnauthorizedAccessException ? UnreadableReason.AccessDenied : UnreadableReason.IoError;
                AddRange(bad, new UnreadableRange(offset + pos, len, reason, ex.HResult));
            }
        }

        return new ReadResult(target.Length, bad);
    }

    private static void AddRange(List<UnreadableRange> ranges, UnreadableRange range)
    {
        if (ranges.Count > 0)
        {
            UnreadableRange last = ranges[^1];
            if (last.End == range.Offset && last.Reason == range.Reason && last.ErrorCode == range.ErrorCode)
            {
                ranges[^1] = last with { Length = last.Length + range.Length };
                return;
            }
        }

        ranges.Add(range);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_denyLock)
            {
                _disposed = true;
                _denyHandle?.Dispose();
                _denyHandle = null;
            }

            _handle.Dispose();
        }
    }
}
