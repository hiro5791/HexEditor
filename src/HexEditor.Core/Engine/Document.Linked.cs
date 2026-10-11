using System.Runtime.CompilerServices;

namespace HexEditor.Core.Engine;

/// <summary>
/// 選択範囲・ブックマークを新しいタブで開く (ENG-39)。連動ビューは親のドキュメントの範囲を表示する子のドキュメントで、データ (元データ・追加バッファ) と
/// Undo 履歴を親と共有する。編集は親の編集として記録し、親の変更の通知で子の内容を作り直す。範囲は親のピースに対する位置として持ち、親で範囲より前に
/// 挿入・削除すると範囲が追従する。
/// </summary>
public sealed partial class Document
{
    private DocumentSnapshot? _linkView;
    private long _linkStart;
    private long _linkLength;
    private readonly ConditionalWeakTable<DocumentSnapshot, StrongBox<long>> _linkStarts = [];
    private readonly List<Document> _linkedViews = [];

    /// <summary>連動ビューの親 (連動ビューでなければ null)。</summary>
    public Document? LinkedParent { get; }

    /// <summary>連動ビューの、親での範囲の開始位置 (親の変更に追従する)。</summary>
    public long LinkStart => _linkStart;

    /// <summary>連動ビューの元の範囲が親から削除された (「切断」。仕様 1)。</summary>
    public bool IsLinkDisconnected { get; private set; }

    /// <summary>連動ビューの範囲の位置が変わった、または切断された。</summary>
    public event EventHandler? LinkChanged;

    /// <summary>このドキュメントの連動ビュー (親を閉じると子も閉じる)。</summary>
    public IReadOnlyList<Document> LinkedViews => _linkedViews;

    private Document(Document parent, long offset, long length)
    {
        _options = parent._options;
        Id = Guid.NewGuid();
        LinkedParent = parent;
        LockPolicy = FileLockPolicy.None;
        _storage = parent._storage;
        History = parent.History;
        _linkStart = offset;
        _linkLength = length;
        _linkView = new DocumentSnapshot(parent.Current.Storage, parent.Current.Tree.Slice(offset, length));
        _linkStarts.AddOrUpdate(parent.Current, new StrongBox<long>(offset));
        parent.Changed += OnParentChanged;
        parent.DataLoaded += OnParentDataLoaded;
        parent.ReadOnlyChanged += OnParentReadOnlyChanged;
        _initialized = true;
    }

    /// <summary>
    /// 連動ビューを作る (仕様 1)。データはコピーせず、範囲の大きさに関係なく一定時間で終わる。長さ 0 の範囲は作れない。
    /// </summary>
    public Document CreateLinkedView(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (length <= 0 || offset < 0 || offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (LinkedParent is { } parent)
        {
            // 連動ビューの連動ビューは、親の範囲の連動ビューにする。
            return parent.CreateLinkedView(_linkStart + offset, length);
        }

        var child = new Document(this, offset, length);
        _linkedViews.Add(child);
        return child;
    }

    /// <summary>
    /// コピーとして開く (仕様 2): 範囲の内容を持つ無題のドキュメント。データのコピーはせずピースの参照で作る (即座に開く)。長さを変えられ、親とは独立している。
    /// 親のデータソースのファイルが保存などで変わっても、内容は開いたときのまま。
    /// </summary>
    public static Document CreateCopy(DocumentSnapshot snapshot, long offset, long length, string displayName, DocumentOptions? options = null)
    {
        Document owner = snapshot.Storage.Owner;
        var copy = new Document(Sources.MemoryByteSource.CreateEmpty(displayName), options ?? owner._options);
        SnapshotRange range = owner.CreateRange(snapshot, offset, length);
        range.AddReference();
        try
        {
            copy.InsertFrom(0, range, 0, length, "コピーとして開く");
        }
        finally
        {
            range.ReleaseReference();
        }

        copy.History.Reset(copy.Current);
        return copy;
    }

    /// <summary>連動ビューの編集を親の編集として記録する。</summary>
    private void ApplyFromLinkedView(PieceTree childTree, long start, long length, long offset, long removed, long inserted, string description,
        string? coalesceKey)
    {
        RequireEditable();
        PieceTree tree = Current.Tree.Replace(start, length, childTree);
        Apply(tree, start + offset, removed, inserted, description, coalesceKey);
    }

    private void OnParentChanged(object? sender, DocumentChangedEventArgs e)
    {
        Document parent = LinkedParent!;
        DocumentSnapshot current = parent.Current;
        if (e.Kind is DocumentChangeKind.Undo or DocumentChangeKind.Redo && _linkStarts.TryGetValue(current, out StrongBox<long>? known))
        {
            _linkStart = known.Value;
        }
        else if (!e.IsWholeDocument && e.RemovedLength != e.InsertedLength)
        {
            long editEnd = e.Offset + e.RemovedLength;
            if (editEnd <= _linkStart)
            {
                // 範囲より前の挿入・削除: 範囲が追従する。
                _linkStart += e.InsertedLength - e.RemovedLength;
            }
            else if (e.Offset <= _linkStart && editEnd >= _linkStart + _linkLength && e.RemovedLength > 0)
            {
                // 範囲全体が削除された。
                Disconnect();
                return;
            }
            else if (e.Offset < _linkStart)
            {
                _linkStart = e.Offset + e.InsertedLength;
            }
        }

        _linkStarts.AddOrUpdate(current, new StrongBox<long>(_linkStart));
        if (_linkStart < 0 || _linkStart + _linkLength > current.Length)
        {
            Disconnect();
            return;
        }

        if (IsLinkDisconnected)
        {
            IsLinkDisconnected = false;
            SetReadOnly(ReadOnlyReason.None);
            LinkChanged?.Invoke(this, EventArgs.Empty);
        }

        long before = _linkView?.Length ?? 0;
        _storage = parent._storage;
        _linkView = new DocumentSnapshot(current.Storage, current.Tree.Slice(_linkStart, _linkLength));
        LinkChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, before, _linkLength, isWholeDocument: true,
            e.Kind is DocumentChangeKind.Undo or DocumentChangeKind.Redo ? e.Kind : DocumentChangeKind.Reloaded,
            e.Selection is { } s && s.Offset >= _linkStart && s.Offset < _linkStart + _linkLength ? (s.Offset - _linkStart, Math.Min(s.Length, _linkStart + _linkLength - s.Offset)) : null));
    }

    /// <summary>元の範囲がなくなった: 「切断」にして編集を止める (内容は最後の内容のまま)。</summary>
    private void Disconnect()
    {
        if (IsLinkDisconnected)
        {
            return;
        }

        IsLinkDisconnected = true;
        SetReadOnly(ReadOnlyReason.NoWriteTarget);
        LinkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnParentDataLoaded(object? sender, EventArgs e) => DataLoaded?.Invoke(this, EventArgs.Empty);

    private void OnParentReadOnlyChanged(object? sender, EventArgs e) => ReadOnlyChanged?.Invoke(this, EventArgs.Empty);

    private void DisposeLinkedView()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Document parent = LinkedParent!;
        parent.Changed -= OnParentChanged;
        parent.DataLoaded -= OnParentDataLoaded;
        parent.ReadOnlyChanged -= OnParentReadOnlyChanged;
        parent._linkedViews.Remove(this);
    }
}
