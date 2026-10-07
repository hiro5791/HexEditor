using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Engine;

/// <summary>
/// あるドキュメントのスナップショットの範囲を、別のドキュメントやアプリ内クリップボードから参照するためのデータソース
/// (EDIT-24)。参照するだけなのでデータ量に関係なく一瞬で作れる。参照元のドキュメントを閉じるときは
/// <see cref="Materialize"/> で一時ファイルに書き出し (実体化)、以降は参照元を読まない。
/// 参照している側 (ドキュメント、アプリ内クリップボード) がすべて手放すまで、参照元のデータは解放されない。
/// </summary>
public sealed class SnapshotRange : ByteSourceBase
{
    private readonly object _lock = new();
    private readonly long _offset;
    private readonly long _length;
    private DocumentSnapshot? _snapshot;
    private Document? _owner;
    private volatile TempFileSource? _materialized;
    private int _references;
    private bool _released;

    internal SnapshotRange(Document owner, DocumentSnapshot snapshot, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > snapshot.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _owner = owner;
        _snapshot = snapshot;
        _offset = offset;
        _length = length;
        DisplayName = owner.Source.DisplayName;
        Identity = "range:" + Id.ToString("N");
        owner.DataLoaded += OnOwnerDataLoaded;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public override string DisplayName { get; }

    public override string Identity { get; }

    public override long Length => _length;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    /// <summary>参照元のドキュメント。実体化した後と、すべての参照が手放された後は null。</summary>
    public Document? Owner
    {
        get
        {
            lock (_lock)
            {
                return _owner;
            }
        }
    }

    /// <summary>一時ファイルに書き出し済みか。</summary>
    public bool IsMaterialized => _materialized is not null;

    /// <summary>参照している側 (ドキュメント・アプリ内クリップボード) の数。</summary>
    public int ReferenceCount
    {
        get
        {
            lock (_lock)
            {
                return _references;
            }
        }
    }

    /// <summary>参照元のデータの読み込みが終わった (参照している側の再描画のきっかけ)。</summary>
    public event EventHandler? DataLoaded;

    /// <summary>参照元のスナップショットの範囲そのもの (同じ元データを共有する貼り付けでピースを共有するため)。</summary>
    internal (DocumentSnapshot Snapshot, long Offset)? Origin
    {
        get
        {
            lock (_lock)
            {
                return _materialized is null && _snapshot is not null ? (_snapshot, _offset) : null;
            }
        }
    }

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        Span<byte> target = buffer[..count];
        if (_materialized is { } file)
        {
            return file.Read(offset, target);
        }

        DocumentSnapshot? snapshot;
        lock (_lock)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null)
        {
            return _materialized is { } done ? done.Read(offset, target) : throw new ObjectDisposedException(nameof(SnapshotRange));
        }

        try
        {
            ReadResult result = snapshot.Read(_offset + offset, target);
            if (result.IsComplete)
            {
                return new ReadResult(count);
            }

            return new ReadResult(count, [.. result.Unreadable.Select(u => u with { Offset = u.Offset - _offset })]);
        }
        catch (ObjectDisposedException) when (_materialized is not null)
        {
            // 読んでいる途中で実体化が終わり、参照元が解放された。
            return _materialized.Read(offset, target);
        }
    }

    /// <summary>表示用の読み込み (ブロックしない)。実体化した後は一時ファイルから読む。</summary>
    internal int ReadForDisplay(long offset, Span<byte> destination, Span<ByteState> states)
    {
        int count = ClampToLength(offset, destination.Length);
        DocumentSnapshot? snapshot;
        lock (_lock)
        {
            snapshot = _materialized is null ? _snapshot : null;
        }

        if (snapshot is not null)
        {
            try
            {
                return snapshot.ReadForDisplay(_offset + offset, destination[..count], states[..count]);
            }
            catch (ObjectDisposedException) when (_materialized is not null)
            {
            }
        }

        ReadResult result = Read(offset, destination[..count]);
        states[..count].Fill(ByteState.Valid);
        foreach (UnreadableRange u in result.Unreadable)
        {
            states.Slice((int)(u.Offset - offset), (int)u.Length).Fill(ByteState.Unreadable);
        }

        return count;
    }

    /// <summary>
    /// 範囲の内容を <paramref name="directory"/> の一時ファイルに書き出し、以降はそこから読む (EDIT-24 の仕様 3・5)。
    /// 一時ファイルはこの範囲を手放したとき (異常終了を含む) に OS が削除する。キャンセルした場合は何も変えない。
    /// 実体化が終わると参照元のドキュメントからは切り離される。<paramref name="progressBase"/> は進捗の報告に足す量
    /// (複数の範囲を 1 つの処理で書き出すとき)。
    /// </summary>
    public void Materialize(string directory, LongRunningOperation? operation = null, long progressBase = 0)
    {
        DocumentSnapshot? snapshot;
        lock (_lock)
        {
            if (_materialized is not null || _released)
            {
                return;
            }

            snapshot = _snapshot;
        }

        if (snapshot is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"clip-{Id:N}.bin");
        SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess, preallocationSize: _length);
        var unreadable = new List<UnreadableRange>();
        try
        {
            byte[] buffer = new byte[(int)Math.Min(Math.Max(_length, 1), 4 * 1024 * 1024)];
            for (long done = 0; done < _length; done += buffer.Length)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (_released)
                    {
                        throw new OperationCanceledException();
                    }
                }

                int n = (int)Math.Min(buffer.Length, _length - done);
                ReadResult read = snapshot.Read(_offset + done, buffer.AsSpan(0, n));
                foreach (UnreadableRange u in read.Unreadable)
                {
                    unreadable.Add(u with { Offset = u.Offset - _offset });
                }

                RandomAccess.Write(handle, buffer.AsSpan(0, n), done);
                operation?.Report(progressBase + done + n);
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        Document? owner;
        lock (_lock)
        {
            if (_released)
            {
                handle.Dispose();
                return;
            }

            _materialized = new TempFileSource(handle, _length, unreadable);
            owner = _owner;
            _owner = null;
            _snapshot = null;
        }

        if (owner is not null)
        {
            owner.DataLoaded -= OnOwnerDataLoaded;
            owner.OnRangeDetached(this);
        }
    }

    /// <summary>参照する側が増えた (ドキュメントへの貼り付け、アプリ内クリップボードへの記録)。</summary>
    public void AddReference()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            _references++;
        }
    }

    /// <summary>参照する側が手放した。すべて手放されたら一時ファイルを消し、参照元から切り離す。</summary>
    public void ReleaseReference()
    {
        Document? owner;
        TempFileSource? file;
        lock (_lock)
        {
            if (_released || --_references > 0)
            {
                return;
            }

            _released = true;
            owner = _owner;
            file = _materialized;
            _owner = null;
            _snapshot = null;
        }

        file?.Dispose();
        if (owner is not null)
        {
            owner.DataLoaded -= OnOwnerDataLoaded;
            owner.OnRangeDetached(this);
        }
    }

    private void OnOwnerDataLoaded(object? sender, EventArgs e) => DataLoaded?.Invoke(this, EventArgs.Empty);

    // 参照の数で管理するため、Dispose では何もしない (ReleaseReference を使う)。
    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>実体化した範囲の一時ファイル。閉じると OS が削除する (DeleteOnClose)。</summary>
internal sealed class TempFileSource(SafeFileHandle handle, long length, IReadOnlyList<UnreadableRange> unreadable) : ByteSourceBase
{
    public override string DisplayName => string.Empty;

    public override string Identity { get; } = "temp:" + Guid.NewGuid().ToString("N");

    public override long Length => length;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        int done = 0;
        while (done < count)
        {
            int n = RandomAccess.Read(handle, buffer[done..count], offset + done);
            if (n == 0)
            {
                throw new IOException("一時ファイルの内容が足りません。");
            }

            done += n;
        }

        List<UnreadableRange>? bad = null;
        foreach (UnreadableRange u in unreadable)
        {
            long from = Math.Max(u.Offset, offset);
            long to = Math.Min(u.End, offset + count);
            if (from < to)
            {
                (bad ??= []).Add(u with { Offset = from, Length = to - from });
            }
        }

        return new ReadResult(count, bad);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            handle.Dispose();
        }
    }
}
