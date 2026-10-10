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
    private readonly long _rangeStart;
    private readonly bool _rangeResizable;
    private readonly object _denyLock = new();
    private SafeFileHandle? _denyHandle;
    private bool _disposed;

    private FileByteSource(string path, SafeFileHandle handle, bool readOnlyAttribute, bool sparse, (long Start, long Length, bool Resizable)? range = null)
    {
        Path = path;
        IsSparse = sparse;
        _handle = handle;
        FileLength = RandomAccess.GetLength(handle);
        _length = FileLength;
        Identity = "file:" + path.ToUpperInvariant();
        if (range is { } r)
        {
            // 範囲を指定して開く (ENG-13): オフセット 0 は開始位置。終了がファイルの長さを超える分は切り詰める。
            IsRange = true;
            _rangeStart = r.Start;
            _length = Math.Max(0, Math.Min(r.Length, FileLength - r.Start));
            _rangeResizable = r.Resizable;
            Identity += $"#range:{r.Start:X}+{_length:X}";
        }

        Stamp = FileStamp.FromHandle(handle);
        HasReadOnlyAttribute = readOnlyAttribute;
    }

    /// <summary>範囲を指定して開いたか (ENG-13)。</summary>
    public bool IsRange { get; }

    /// <summary>範囲の開始位置 (ファイル上のオフセット)。範囲でなければ 0。</summary>
    public long RangeStart => _rangeStart;

    /// <summary>範囲で「長さの変更を許可する」を選んだ (ENG-13 の仕様 4)。</summary>
    public bool RangeResizable => _rangeResizable;

    /// <summary>開いた時点のファイル全体の長さ (範囲でなければ <see cref="Length"/> と同じ)。</summary>
    public long FileLength { get; }

    /// <summary>オフセット 0 のアドレス (範囲なら開始位置。ENG-13 の仕様 2)。</summary>
    public override long BaseAddress => _rangeStart;

    /// <summary>開いた時点で読み取り専用属性があったか (ENG-14 の仕様 1)。</summary>
    public bool HasReadOnlyAttribute { get; }

    /// <summary>
    /// 読み取り専用属性があるファイルへの書き込みを利用者が承認した (EDIT-16 の「編集を許可する」。ENG-14 の仕様 3)。承認すると保存でき、
    /// 保存では属性を外す (ENG-22 の仕様 5)。
    /// </summary>
    public bool WriteApproved { get; private set; }

    /// <summary>読み取り専用属性があっても保存できるようにする (属性は保存のときに外す)。</summary>
    public void ApproveWriting() => WriteApproved = true;

    public string Path { get; }

    public override string DisplayName => System.IO.Path.GetFileName(Path);

    public override string Identity { get; }

    /// <summary>開いた時点の長さ。外部変更は ENG-19 で検知して開き直す。</summary>
    public override long Length => _length;

    public override SourceCapabilities Capabilities =>
        (IsRange && !_rangeResizable ? SourceCapabilities.None : SourceCapabilities.CanResize | SourceCapabilities.CanReplace)
        | (HasReadOnlyAttribute && !WriteApproved ? SourceCapabilities.None : SourceCapabilities.CanWrite);

    /// <summary>スパースファイルか (開いた時点の属性)。安全な保存で一時ファイルもスパースにする (ENG-22 の仕様 5)。</summary>
    public bool IsSparse { get; }

    /// <summary>
    /// [<paramref name="offset"/>, + <paramref name="length"/>) の中で領域が割り当てられている範囲。スパースでなければ範囲全体
    /// (それ以外の範囲は 00 として読める。ENG-22 の仕様 5、ENG-25 の仕様 1 の見積もり)。
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> AllocatedRanges(long offset, long length)
    {
        length = Math.Min(length, Math.Max(0, _length - offset));
        if (!IsSparse)
        {
            return length > 0 ? [(offset, length)] : [];
        }

        IReadOnlyList<(long Offset, long Length)> ranges = SparseFiles.AllocatedRanges(_handle, _rangeStart + offset, length);
        return _rangeStart == 0 ? ranges : [.. ranges.Select(r => (r.Offset - _rangeStart, r.Length))];
    }

    /// <summary>ファイル全体の中で割り当てられている範囲 (ファイル上のオフセット。範囲の外を書き出すとき)。</summary>
    internal IReadOnlyList<(long Offset, long Length)> AllocatedFileRanges(long offset, long length) =>
        !IsSparse ? (length > 0 ? [(offset, length)] : []) : SparseFiles.AllocatedRanges(_handle, offset, length);

    /// <summary>ファイル上の位置を指定して読む (範囲の外。範囲を指定して開いたドキュメントの保存で前後を書き出す)。</summary>
    internal void ReadFile(long fileOffset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = RandomAccess.Read(_handle, destination[done..], fileOffset + done);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            done += n;
        }
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

    /// <summary>
    /// ファイルの一部の範囲 [<paramref name="start"/>, + <paramref name="length"/>) を開く (ENG-13)。終了がファイルの長さを超える分は切り詰める。
    /// <paramref name="resizable"/> が偽なら長さ固定 (上書きのみ。ENG-07 と同じ制約)。開始位置がファイルの長さ以上、または長さが 0 以下なら例外。
    /// </summary>
    public static FileByteSource OpenRange(string path, long start, long length, bool resizable) => OpenRange(path, start, length, resizable, allowEmpty: false);

    /// <summary><paramref name="allowEmpty"/>: 保存の後に長さ 0 になった範囲 (範囲の中をすべて削除して保存した) も開く。</summary>
    internal static FileByteSource OpenRange(string path, long start, long length, bool resizable, bool allowEmpty)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        long fileLength = RandomAccess.GetLength(handle);
        if (start < 0 || (allowEmpty ? start > fileLength || length < 0 : start >= fileLength || length <= 0))
        {
            handle.Dispose();
            throw new ArgumentOutOfRangeException(nameof(start), "範囲がファイルの外です。");
        }

        FileAttributes attributes = File.GetAttributes(fullPath);
        return new FileByteSource(fullPath, handle, attributes.HasFlag(FileAttributes.ReadOnly), attributes.HasFlag(FileAttributes.SparseFile),
            (start, length, resizable));
    }

    /// <summary>同じ開き方 (範囲を含む) でもう一度開く (再読み込み・保存の後。ENG-18、ENG-19)。範囲の長さは今のファイルの長さに収める。</summary>
    public FileByteSource Reopen(long? rangeLength = null)
    {
        if (!IsRange)
        {
            return Open(Path);
        }

        long fileLength = new FileInfo(Path).Length;
        long length = Math.Min(rangeLength ?? _length, Math.Max(1, fileLength - _rangeStart));
        return OpenRange(Path, Math.Min(_rangeStart, Math.Max(0, fileLength - 1)), length, _rangeResizable);
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
                int n = await RandomAccess.ReadAsync(_handle, target[done..], _rangeStart + offset + done, cancellationToken).ConfigureAwait(false);
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
            int n = RandomAccess.Read(_handle, target[done..], _rangeStart + offset + done);
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
