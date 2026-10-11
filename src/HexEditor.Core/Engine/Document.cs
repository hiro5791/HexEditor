using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>ドキュメントを作るときの設定 (ENG-04、ENG-06 の既定値)。</summary>
public sealed record DocumentOptions
{
    /// <summary>追加バッファの退避先のフォルダ。ドキュメントごとにサブフォルダを作る。</summary>
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "HexEditor", "recovery");

    public long AddBufferMemoryLimit { get; init; } = 256L * 1024 * 1024;

    public long CacheCapacity { get; init; } = 256L * 1024 * 1024;

    public int MaxConcurrentReads { get; init; } = 4;

    /// <summary>連続した入力をまとめる最大の間隔 (EDIT-19 の仕様 5。0 はまとめない)。</summary>
    public TimeSpan CoalesceInterval { get; init; } = EditHistory.DefaultCoalesceInterval;

    /// <summary>ファイルのロックの方針 (ENG-15 の仕様 3)。</summary>
    public FileLockPolicy LockPolicy { get; init; } = FileLockPolicy.WhileModified;

    /// <summary>入力のまとめの時間を測る時計 (テストで差し替える)。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>ファイルのロックの方針 (ENG-15 の仕様 3)。</summary>
public enum FileLockPolicy
{
    /// <summary>編集中 (未保存の変更があるとき) だけ他のアプリの書き込みを禁止する (既定)。</summary>
    WhileModified,

    /// <summary>開いている間、常に他のアプリの書き込みを禁止する。</summary>
    Always,

    /// <summary>ロックしない。</summary>
    None,
}

/// <summary>書き込み禁止のハンドルの状態 (ENG-15。ドキュメントのプロパティに表示する)。</summary>
public enum FileLockState
{
    /// <summary>ファイルでない、または方針によりロックしていない。</summary>
    Unlocked,

    /// <summary>他のアプリの書き込みを禁止している。</summary>
    Locked,

    /// <summary>
    /// 禁止しようとしたが、他のアプリが書き込み用に開いているため禁止できなかった。UI は InfoBar で
    /// 「他のアプリがこのファイルに書き込める状態です…」と示す (ENG-15 の仕様 2)。
    /// </summary>
    Failed,
}

/// <summary>内容の変更のきっかけ。</summary>
public enum DocumentChangeKind
{
    Edit,
    Undo,
    Redo,

    /// <summary>保存による元データの切り替え (内容は変わらない)。</summary>
    Saved,

    /// <summary>再読み込み・変更の破棄・外部の変更とのマージ (ENG-18、ENG-19)。</summary>
    Reloaded,
}

/// <summary>
/// 内容の変更の通知。範囲はドキュメント上の位置。Undo・Redo では全体が変わったものとして通知し、
/// <see cref="Selection"/> に選択する範囲 (EDIT-19 の仕様 10) を入れる。
/// </summary>
public sealed class DocumentChangedEventArgs(long offset, long removedLength, long insertedLength, bool isWholeDocument,
    DocumentChangeKind kind = DocumentChangeKind.Edit, (long Offset, long Length)? selection = null)
    : EventArgs
{
    public DocumentChangeKind Kind { get; } = kind;

    /// <summary>Undo・Redo の後に選択する範囲 (長さ 0 ならカーソルだけを置く)。編集では null。</summary>
    public (long Offset, long Length)? Selection { get; } = selection;

    public long Offset { get; } = offset;

    public long RemovedLength { get; } = removedLength;

    public long InsertedLength { get; } = insertedLength;

    public bool IsWholeDocument { get; } = isWholeDocument;
}

/// <summary>
/// 編集対象 1 つ (ENG-02〜ENG-07)。編集は UI スレッドから呼ぶ。各編集は新しいスナップショットを作って履歴に積む。
/// 長さを変えられないデータソースでは上書きだけを受け付ける (ENG-07)。
/// </summary>
public sealed partial class Document : IDisposable
{
    private readonly object _lifetimeLock = new();
    private readonly List<DocumentStorage> _storages = [];
    private readonly DocumentOptions _options;
    private readonly List<SnapshotRange> _exported = [];
    private readonly HashSet<SnapshotRange> _imported = [];
    private readonly List<IByteSource> _ownedExternals = [];
    private DocumentStorage _storage;
    private FileByteSource? _lockedFile;
    private bool _lockSuspended;
    private bool _disposed;
    private bool _resourcesReleased;

    public Document(IByteSource source, DocumentOptions? options = null)
    {
        _options = options ?? new DocumentOptions();
        Id = Guid.NewGuid();
        LockPolicy = _options.LockPolicy;
        string spillPath = Path.Combine(_options.TempDirectory, Id.ToString("N"), "add.bin");
        _storage = CreateStorage(source, new AddBuffer(spillPath, _options.AddBufferMemoryLimit));

        // 開いた直後は元データ全体を指すピース 1 つ (長さ 0 ならピースなし)。ENG-02 の仕様 8。
        PieceTree tree = source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, source.Length)) : PieceTree.Empty;
        History = new EditHistory(new DocumentSnapshot(_storage, tree), _options.TimeProvider, _options.CoalesceInterval);
        _initialized = true;
        UpdateLock();
    }

    /// <summary>
    /// 復旧用データからドキュメントを作り直す (ENG-27 の仕様 6)。<paramref name="addBuffer"/> は復旧用データの一時ファイルを
    /// 開き直したもので、<paramref name="pieces"/> はその時点の内容。<paramref name="externals"/> は外部参照のピース
    /// (<see cref="PieceKind.External"/>) が指すデータ (復旧用データに書き出したもの。ドキュメントが閉じるときに閉じる)。
    /// Undo 履歴は復元せず、「変更あり」の状態で始まる。
    /// </summary>
    public static Document Restore(Guid id, IByteSource source, AddBuffer addBuffer, IEnumerable<Piece> pieces, DocumentOptions? options = null,
        IReadOnlyList<IByteSource>? externals = null)
    {
        var list = pieces.ToList();
        externals ??= [];
        foreach (Piece piece in list)
        {
            bool valid = piece.Kind switch
            {
                PieceKind.Original => piece.Offset >= 0 && piece.Offset + piece.Length <= source.Length,
                PieceKind.Added => piece.Offset >= 0 && piece.Offset + piece.Length <= addBuffer.Length,
                PieceKind.Pattern => piece.Offset >= 0 && piece.Offset + piece.PatternLength <= addBuffer.Length,
                PieceKind.Random => piece.Offset >= 0,
                PieceKind.External => piece.ExternalIndex >= 0 && piece.ExternalIndex < externals.Count
                    && piece.Offset >= 0 && piece.Offset + piece.Length <= externals[piece.ExternalIndex].Length,
                _ => false,
            };
            if (!valid)
            {
                throw new InvalidDataException("復旧用データのピースが元データまたは追加バッファの範囲外です。");
            }
        }

        var document = new Document(id, source, addBuffer, PieceTree.FromPieces(list), options ?? new DocumentOptions(), externals);
        document.History.MarkUnsaved();
        document.UpdateLock();
        return document;
    }

    private Document(Guid id, IByteSource source, AddBuffer addBuffer, PieceTree tree, DocumentOptions options, IReadOnlyList<IByteSource> externals)
    {
        _options = options;
        Id = id;
        LockPolicy = options.LockPolicy;
        _storage = CreateStorage(source, addBuffer);
        _storage.Externals.AddRange(externals);
        _ownedExternals.AddRange(externals);
        History = new EditHistory(new DocumentSnapshot(_storage, tree), options.TimeProvider, options.CoalesceInterval);
        _initialized = true;
    }

    public Guid Id { get; }

    /// <summary>ドキュメントを作ったときの設定。</summary>
    public DocumentOptions Options => _options;

    /// <summary>
    /// 現在の内容が今の元データと追加バッファだけで表せるか。保存より前の版に Undo した直後は、置き換え前の
    /// 元データを指すため偽 (復旧用データは参照で記録できない)。
    /// </summary>
    public bool CurrentUsesLatestSource => ReferenceEquals(Current.Storage, _storage);

    /// <summary>現在の元データ。保存 (ENG-20) の後は保存したファイルに変わる。</summary>
    public IByteSource Source => _storage.Source;

    /// <summary>Undo 履歴。連動ビュー (ENG-39) では親のドキュメントの履歴 (親でも子でも同じ履歴を Undo する)。</summary>
    public EditHistory History { get; }

    /// <summary>現在の内容。連動ビューでは親の現在の内容の範囲。</summary>
    public DocumentSnapshot Current => _linkView ?? History.Current;

    public long Length => Current.Length;

    /// <summary>挿入・削除・切り取り・挿入貼り付けができるか (ENG-07)。連動ビューは長さ固定 (ENG-39 の仕様 1)。</summary>
    public bool CanResize => LinkedParent is null && Source.Capabilities.HasFlag(SourceCapabilities.CanResize);

    /// <summary>元のデータソースに保存できるか (ENG-01 の仕様 6)。偽なら「名前を付けて保存」だけになる。</summary>
    public bool CanSave => Source.Capabilities.HasFlag(SourceCapabilities.CanWrite);

    /// <summary>エンジンのメモリ使用量の内訳 (ENG-08 の仕様 5)。</summary>
    public EngineMemoryUsage MemoryUsage => new(
        _storage.Cache.MemoryBytes,
        _storage.AddBuffer.MemoryBytes,
        _storage.AddBuffer.SpilledBytes,
        Current.Tree.PieceCount * EngineMemoryUsage.BytesPerNode);

    public bool IsModified => History.IsModified;

    public bool IsDisposed => _disposed;

    public AddBuffer AddBuffer => _storage.AddBuffer;

    public BlockCache Cache => _storage.Cache;

    /// <summary>
    /// ドキュメントを変更する長時間処理・保存の実行中は true (ENG-09 の仕様 7)。この間の編集は
    /// <see cref="DocumentLockedException"/> になる。
    /// </summary>
    public bool IsEditLocked { get; internal set; }

    // ---- 読み取り専用 (EDIT-16) ----

    /// <summary>読み取り専用の理由。<see cref="ReadOnlyReason.None"/> なら編集できる (EDIT-16 の仕様 1)。</summary>
    public ReadOnlyReason ReadOnlyReason { get; private set; }

    /// <summary>読み取り専用か。読み取り専用の間、データを変える操作は <see cref="DocumentReadOnlyException"/> になる (仕様 2)。</summary>
    public bool IsReadOnly => ReadOnlyReason != ReadOnlyReason.None;

    /// <summary>読み取り専用を解除できるか、解除にどの手順が要るか (EDIT-16 の仕様 4、ENG-14 の仕様 2・3)。</summary>
    public ReadOnlyRelease ReadOnlyRelease => ReadOnlyReason switch
    {
        ReadOnlyReason.None or ReadOnlyReason.User => ReadOnlyRelease.Immediate,
        ReadOnlyReason.NoWriteTarget or ReadOnlyReason.WriteProtectionMode or ReadOnlyReason.ReadOnlyMedia => ReadOnlyRelease.NotAllowed,
        _ => ReadOnlyRelease.NeedsConfirmation,
    };

    /// <summary><see cref="ReadOnlyReason"/> が変わった。</summary>
    public event EventHandler? ReadOnlyChanged;

    /// <summary>
    /// 読み取り専用にする、または解除する (<see cref="ReadOnlyReason.None"/>)。未保存の変更はそのまま残す (EDIT-16 の仕様 5)。
    /// 解除できるかの判断 (<see cref="ReadOnlyRelease"/>) と確認は呼び出し側で行う。
    /// </summary>
    public void SetReadOnly(ReadOnlyReason reason)
    {
        if (ReadOnlyReason == reason)
        {
            return;
        }

        ReadOnlyReason = reason;
        History.BreakCoalescing();
        ReadOnlyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>内容が変わった。</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>表示中のデータの読み込みが終わった (再描画のきっかけ)。スレッドプールから呼ばれる。</summary>
    public event EventHandler? DataLoaded;

    // ---- ファイルのロック (ENG-15) ----

    /// <summary>ファイルのロックの方針。「詳細を指定して開く」の「他のアプリの書き込みを禁止する」は <see cref="FileLockPolicy.Always"/> (仕様 4)。</summary>
    public FileLockPolicy LockPolicy
    {
        get => _lockPolicy;
        set
        {
            _lockPolicy = value;
            if (_initialized)
            {
                UpdateLock();
            }
        }
    }

    private FileLockPolicy _lockPolicy;
    private bool _initialized;

    /// <summary>書き込み禁止のハンドルの状態。</summary>
    public FileLockState LockState { get; private set; }

    /// <summary><see cref="LockState"/> が変わった。<see cref="FileLockState.Failed"/> になったら UI は InfoBar で知らせる。</summary>
    public event EventHandler? LockStateChanged;

    /// <summary>
    /// 方針と変更の有無に合わせて、書き込み禁止のハンドルを開く・閉じる (ENG-15 の仕様 2)。編集・Undo・Redo・保存の後に呼ばれる。
    /// </summary>
    private void UpdateLock()
    {
        bool changed;
        lock (_lifetimeLock)
        {
            var file = _lockSuspended || _disposed ? null : _storage.Source as FileByteSource;
            bool want = file is not null && _lockPolicy switch
            {
                FileLockPolicy.Always => true,
                FileLockPolicy.WhileModified => History.IsModified,
                _ => false,
            };

            if (_lockedFile is not null && (!want || !ReferenceEquals(_lockedFile, file) || !_lockedFile.IsWriteDenied))
            {
                _lockedFile.AllowWrites();
                _lockedFile = null;
            }

            FileLockState state;
            if (!want)
            {
                state = FileLockState.Unlocked;
            }
            else if (_lockedFile is not null)
            {
                state = FileLockState.Locked;
            }
            else if (LockState == FileLockState.Failed)
            {
                // 禁止できなかった状態では、変更がなくなる (または保存する) まで開き直さない (InfoBar を何度も出さない)。
                state = FileLockState.Failed;
            }
            else if (file!.DenyWrites())
            {
                _lockedFile = file;
                state = FileLockState.Locked;
            }
            else
            {
                state = FileLockState.Failed;
            }

            changed = state != LockState;
            LockState = state;
        }

        if (changed)
        {
            LockStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>保存の前に書き込み禁止のハンドルを閉じる (ENG-15 の仕様 5)。保存の後は <see cref="ResumeLock"/> で元の方針に戻す。</summary>
    internal void SuspendLock()
    {
        lock (_lifetimeLock)
        {
            _lockSuspended = true;
            _lockedFile?.AllowWrites();
            _lockedFile = null;
        }
    }

    internal void ResumeLock()
    {
        lock (_lifetimeLock)
        {
            _lockSuspended = false;
            if (LockState == FileLockState.Failed)
            {
                LockState = FileLockState.Unlocked;
            }
        }

        UpdateLock();
    }

    // ---- 編集グループ (EDIT-19 の仕様 6) ----

    /// <summary>
    /// 1 つのコマンドの中の複数の編集を 1 つの Undo 単位にまとめる。戻り値を Dispose するまでの編集が 1 つのグループになる。
    /// 入れ子にした場合は一番外側でまとめる。<paramref name="coalesceKey"/> を指定すると、続く同じ種類の入力もこのグループにまとめる。
    /// </summary>
    public IDisposable BeginGroup(string description, string? coalesceKey = null)
    {
        History.BeginGroup(description, coalesceKey);
        return new GroupScope(History);
    }

    private sealed class GroupScope(EditHistory history) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (!_done)
            {
                _done = true;
                history.EndGroup();
            }
        }
    }

    // ---- 挿入 ----

    /// <summary><paramref name="offset"/> にバイト列を挿入する。</summary>
    public void Insert(long offset, ReadOnlySpan<byte> data, string description = "挿入", string? coalesceKey = null)
    {
        if (data.IsEmpty)
        {
            return;
        }

        RequireResizable();
        PieceTree content = AppendToBuffer(data);
        Apply(Current.Tree.Insert(offset, content), offset, 0, data.Length, description, coalesceKey);
    }

    /// <summary><paramref name="offset"/> に、パターンの繰り返しを <paramref name="length"/> バイト挿入する (ENG-03)。</summary>
    public void InsertPattern(long offset, long length, ReadOnlySpan<byte> pattern, string description = "パターンの挿入")
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        Piece piece = PatternPiece(pattern, length);
        Apply(Current.Tree.Insert(offset, piece), offset, 0, length, description, null);
    }

    /// <summary><paramref name="offset"/> に乱数を <paramref name="length"/> バイト挿入する (ENG-03)。</summary>
    public void InsertRandom(long offset, long length, ulong seed, string description = "乱数の挿入")
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        Apply(Current.Tree.Insert(offset, Piece.Random(seed, 0, length)), offset, 0, length, description, null);
    }

    /// <summary>
    /// 同じドキュメントの [sourceOffset, sourceOffset + length) を、データをコピーせず <paramref name="destinationOffset"/>
    /// に挿入する (範囲の複製。ENG-02 の仕様 3)。
    /// </summary>
    public void InsertCopy(long destinationOffset, long sourceOffset, long length, string description = "貼り付け")
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        PieceTree slice = Current.Tree.Slice(sourceOffset, length);
        Apply(Current.Tree.Insert(destinationOffset, slice), destinationOffset, 0, length, description, null);
    }

    /// <summary>
    /// 別のスナップショット (同じドキュメントの過去の状態、または別のドキュメント) の範囲を <paramref name="offset"/> に挿入する。
    /// 同じ元データを共有している場合はピースを共有し、そうでなければ範囲の参照 (<see cref="SnapshotRange"/>) を
    /// 1 つのピースとして挿入する。どちらもデータをコピーしない (ENG-02 の仕様 9、EDIT-24 の仕様 2)。
    /// </summary>
    public void InsertFrom(long offset, DocumentSnapshot source, long sourceOffset, long length, string description = "貼り付け")
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        PieceTree content = ContentFrom(source, sourceOffset, length);
        Apply(Current.Tree.Insert(offset, content), offset, 0, length, description, null);
    }

    /// <summary>範囲の参照 (アプリ内クリップボード) の [rangeOffset, rangeOffset + length) を挿入する (EDIT-24)。</summary>
    public void InsertFrom(long offset, SnapshotRange range, long rangeOffset, long length, string description = "貼り付け")
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        PieceTree content = ContentFrom(range, rangeOffset, length);
        Apply(Current.Tree.Insert(offset, content), offset, 0, length, description, null);
    }

    /// <summary>別のスナップショットの範囲で上書きする。末尾を越える分の扱いは <see cref="Overwrite"/> と同じ。</summary>
    public void OverwriteFrom(long offset, DocumentSnapshot source, long sourceOffset, long length, string description = "上書き貼り付け")
    {
        if (length == 0)
        {
            return;
        }

        long replaced = CheckOverwrite(offset, length);
        PieceTree content = ContentFrom(source, sourceOffset, length);
        Apply(Current.Tree.Replace(offset, replaced, content), offset, replaced, length, description, null);
    }

    /// <summary>範囲の参照で上書きする。</summary>
    public void OverwriteFrom(long offset, SnapshotRange range, long rangeOffset, long length, string description = "上書き貼り付け")
    {
        if (length == 0)
        {
            return;
        }

        long replaced = CheckOverwrite(offset, length);
        PieceTree content = ContentFrom(range, rangeOffset, length);
        Apply(Current.Tree.Replace(offset, replaced, content), offset, replaced, length, description, null);
    }

    /// <summary>
    /// このドキュメントのスナップショットの範囲の参照を作る (アプリ内クリップボード、別のドキュメントへの貼り付け。EDIT-24 の仕様 1)。
    /// 参照する側は <see cref="SnapshotRange.AddReference"/> で参照を記録し、不要になったら手放す。
    /// </summary>
    public SnapshotRange CreateRange(DocumentSnapshot snapshot, long offset, long length)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_storages.Contains(snapshot.Storage))
            {
                throw new ArgumentException("このドキュメントのスナップショットではありません。", nameof(snapshot));
            }

            var range = new SnapshotRange(this, snapshot, offset, length);
            _exported.Add(range);
            return range;
        }
    }

    /// <summary>
    /// 他のドキュメント・アプリ内クリップボードが参照している、このドキュメントの範囲のうち、まだ実体化していないもの
    /// (EDIT-24 の仕様 3)。閉じる前にこれらを <see cref="MaterializeReferences"/> で一時ファイルに書き出す。
    /// </summary>
    public IReadOnlyList<SnapshotRange> PendingReferences
    {
        get
        {
            lock (_lifetimeLock)
            {
                // このドキュメント自身からの参照 (保存前の版からの貼り付け) は、閉じるときに一緒に手放すため数えない。
                return _exported.Where(r => !r.IsMaterialized && r.ReferenceCount - (_imported.Contains(r) ? 1 : 0) > 0).ToList();
            }
        }
    }

    /// <summary>実体化が必要な量の合計 (1 GB を超える場合は閉じる前に確認する。EDIT-24 の仕様 6)。</summary>
    public long PendingReferenceBytes => PendingReferences.Sum(r => r.Length);

    /// <summary>
    /// 参照されている範囲をすべて一時ファイルに書き出す (EDIT-24 の仕様 3・5。長時間処理として呼ぶ)。キャンセルされた場合は
    /// 書き出しの済んでいない範囲は参照のまま残る (参照する側がすべて手放すまで、このドキュメントのデータは解放されない)。
    /// </summary>
    public void MaterializeReferences(LongRunningOperation? operation = null)
    {
        IReadOnlyList<SnapshotRange> pending = PendingReferences;
        long total = pending.Sum(r => r.Length);
        operation?.SetTotal(total);
        long done = 0;
        foreach (SnapshotRange range in pending)
        {
            range.Materialize(_options.TempDirectory, operation, done);
            done += range.Length;
        }
    }

    /// <summary>範囲が実体化された、またはすべての参照が手放された。閉じた後なら、残りがなくなった時点でデータを解放する。</summary>
    internal void OnRangeDetached(SnapshotRange range)
    {
        bool release;
        lock (_lifetimeLock)
        {
            _exported.Remove(range);
            release = _disposed && !_resourcesReleased && _exported.Count == 0;
            _resourcesReleased |= release;
        }

        if (release)
        {
            ReleaseResources();
        }
    }

    /// <summary>挿入・上書きに使う内容。元データを共有していればピースの共有、違えば範囲の参照。</summary>
    private PieceTree ContentFrom(DocumentSnapshot source, long sourceOffset, long length)
    {
        RequireEditable();
        if (LinkedParent is { } linkParent)
        {
            // 連動ビューの内容は親のデータ (参照の記録も親が持つ)。
            return linkParent.ContentFrom(source, sourceOffset, length);
        }
        // 同じ元データ (保存で切り替わる前のスナップショットは別の元データ) ならピースをそのまま共有できる。
        if (ReferenceEquals(source.Storage, _storage))
        {
            return source.Tree.Slice(sourceOffset, length);
        }

        SnapshotRange range = source.Storage.Owner.CreateRange(source, sourceOffset, length);
        range.AddReference();
        try
        {
            return ContentFrom(range, 0, length);
        }
        finally
        {
            // 参照はこのドキュメントが記録した分だけ残す (記録されなければ、ここで手放されて解放される)。
            range.ReleaseReference();
        }
    }

    private PieceTree ContentFrom(SnapshotRange range, long rangeOffset, long length)
    {
        RequireEditable();
        if (LinkedParent is { } linkParent)
        {
            return linkParent.ContentFrom(range, rangeOffset, length);
        }
        if (rangeOffset < 0 || length < 0 || rangeOffset + length > range.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeOffset));
        }

        if (range.Origin is { } origin && ReferenceEquals(origin.Snapshot.Storage, _storage))
        {
            return origin.Snapshot.Tree.Slice(origin.Offset + rangeOffset, length);
        }

        int index = _storage.Externals.IndexOf(range);
        if (index < 0)
        {
            if (_imported.Add(range))
            {
                range.AddReference();
                range.DataLoaded += OnExternalDataLoaded;
            }

            _storage.Externals.Add(range);
            index = _storage.Externals.Count - 1;
        }

        return PieceTree.FromPiece(Piece.External(index, rangeOffset, length));
    }

    private void OnExternalDataLoaded(object? sender, EventArgs e) => DataLoaded?.Invoke(this, EventArgs.Empty);

    // ---- 上書き ----

    /// <summary>
    /// <paramref name="offset"/> からバイト列で上書きする。末尾を越える分は、長さを変えられるデータソースでは追加し、
    /// 変えられないデータソースでは拒否する (ENG-07 の仕様 5 の切り詰めは呼び出し側で確認してから行う)。
    /// </summary>
    public void Overwrite(long offset, ReadOnlySpan<byte> data, string description = "上書き", string? coalesceKey = null)
    {
        if (data.IsEmpty)
        {
            return;
        }

        long replaced = CheckOverwrite(offset, data.Length);
        PieceTree content = AppendToBuffer(data);
        Apply(Current.Tree.Replace(offset, replaced, content), offset, replaced, data.Length, description, coalesceKey);
    }

    /// <summary>[offset, offset + length) をパターンの繰り返しで塗りつぶす (ENG-03)。</summary>
    public void OverwritePattern(long offset, long length, ReadOnlySpan<byte> pattern, string description = "塗りつぶし")
    {
        if (length == 0)
        {
            return;
        }

        long replaced = CheckOverwrite(offset, length);
        Piece piece = PatternPiece(pattern, length);
        Apply(Current.Tree.Replace(offset, replaced, PieceTree.FromPiece(piece)), offset, replaced, length, description, null);
    }

    /// <summary>[offset, offset + length) を乱数で塗りつぶす (ENG-03)。</summary>
    public void OverwriteRandom(long offset, long length, ulong seed, string description = "乱数で塗りつぶし")
    {
        if (length == 0)
        {
            return;
        }

        long replaced = CheckOverwrite(offset, length);
        var tree = PieceTree.FromPiece(Piece.Random(seed, 0, length));
        Apply(Current.Tree.Replace(offset, replaced, tree), offset, replaced, length, description, null);
    }

    // ---- 作った内容の挿入・上書き (EDIT-14、EDIT-15、EDIT-29、EDIT-30) ----

    /// <summary>
    /// <paramref name="offset"/> に <paramref name="content"/> を挿入する。データソースの参照 (一時ファイル) は以後このドキュメントが持ち、
    /// 閉じるときに閉じる。生成ピース・参照のピースで表すため、内容の長さに関係なく一定時間で終わる。
    /// </summary>
    public void InsertContent(long offset, EditContent content, string description = "挿入")
    {
        if (content.Length == 0)
        {
            content.Dispose();
            return;
        }

        RequireResizable();
        if (offset < 0 || offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "オフセットがドキュメントの範囲外です。");
        }

        if (content.Length > long.MaxValue - Length)
        {
            throw new ArgumentOutOfRangeException(nameof(content), "ドキュメントの長さが上限 (2^63 − 1) を超えます。");
        }

        PieceTree tree = TreeOf(content);
        Apply(Current.Tree.Insert(offset, tree), offset, 0, content.Length, description, null);
    }

    /// <summary>
    /// <paramref name="offset"/> から <paramref name="content"/> で上書きする。末尾を越える分は長さを変えられるデータソースでは追加し、
    /// 変えられないデータソースでは拒否する (<see cref="Overwrite"/> と同じ)。
    /// </summary>
    public void OverwriteContent(long offset, EditContent content, string description = "上書き")
    {
        if (content.Length == 0)
        {
            content.Dispose();
            return;
        }

        long replaced = CheckOverwrite(offset, content.Length);
        PieceTree tree = TreeOf(content);
        Apply(Current.Tree.Replace(offset, replaced, tree), offset, replaced, content.Length, description, null);
    }

    /// <summary>内容をピースの木にする。データソースの参照は外部参照の表に加える (以後このドキュメントが持つ)。</summary>
    private PieceTree TreeOf(EditContent content)
    {
        RequireEditable();
        if (LinkedParent is { } linkParent)
        {
            return linkParent.TreeOf(content);
        }
        switch (content.Kind)
        {
            case EditContentKind.Pattern:
                return PieceTree.FromPiece(PatternPiece(content.Data!, content.Length, content.Position));
            case EditContentKind.Random:
                return PieceTree.FromPiece(Piece.Random(content.Seed, content.Position, content.Length));
            case EditContentKind.Bytes:
                return AppendToBuffer(content.Data!.AsSpan(0, (int)content.Length));
            case EditContentKind.Original:
                if (content.Position < 0 || content.Position + content.Length > Source.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(content), "元データの範囲外です。");
                }

                return PieceTree.FromPiece(Piece.Original(content.Position, content.Length));
            default:
                IByteSource source = content.TakeSource();
                int index = _storage.Externals.IndexOf(source);
                if (index < 0)
                {
                    _storage.Externals.Add(source);
                    index = _storage.Externals.Count - 1;
                    if (content.OwnsSource)
                    {
                        _ownedExternals.Add(source);
                    }
                }

                return PieceTree.FromPiece(Piece.External(index, content.Position, content.Length));
        }
    }

    // ---- 削除 ----

    /// <summary>[offset, offset + length) を削除する。</summary>
    public void Delete(long offset, long length, string description = "削除", string? coalesceKey = null)
    {
        if (length == 0)
        {
            return;
        }

        RequireResizable();
        Apply(Current.Tree.Delete(offset, length), offset, length, 0, description, coalesceKey);
    }

    // ---- Undo / Redo ----

    /// <summary>元に戻す。取り消した編集グループの、編集前の範囲を選択するよう通知する (EDIT-19 の仕様 10)。</summary>
    public void Undo()
    {
        if (LinkedParent is { } linkParent)
        {
            RequireEditable();
            linkParent.Undo();
            return;
        }

        RequireEditable();
        HistoryEntry undone = History.Undo();
        (long, long)? selection = undone.Range is { } r ? (r.Offset, r.BeforeLength) : null;
        AfterHistoryMove(DocumentChangeKind.Undo, selection);
    }

    /// <summary>やり直す。やり直した編集グループの、編集後の範囲を選択するよう通知する。</summary>
    public void Redo()
    {
        if (LinkedParent is { } linkParent)
        {
            RequireEditable();
            linkParent.Redo();
            return;
        }

        RequireEditable();
        HistoryEntry redone = History.Redo();
        (long, long)? selection = redone.Range is { } r ? (r.Offset, r.AfterLength) : null;
        AfterHistoryMove(DocumentChangeKind.Redo, selection);
    }

    private void AfterHistoryMove(DocumentChangeKind kind, (long, long)? selection)
    {
        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, kind, selection));
    }

    /// <summary>現在の状態を「保存した時点」にする (保存処理から呼ぶ)。</summary>
    public void MarkSaved()
    {
        History.MarkSaved();
        UpdateLock();
    }

    /// <summary>
    /// 保存の完了 (ENG-20 の仕様 4): 保存したファイルを新しい元データにし、現在の状態を元データ全体を指す
    /// ピース 1 つに置き換えて「保存した時点」にする。保存前の履歴は以前の元データを読み続ける (ENG-05 の仕様 5)。
    /// </summary>
    public void CompleteSave(IByteSource savedSource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (savedSource.Length != Length)
        {
            throw new InvalidOperationException("保存したファイルの長さがドキュメントと違います。");
        }

        _storage = CreateStorage(savedSource, _storage.AddBuffer);
        PieceTree tree = savedSource.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, savedSource.Length)) : PieceTree.Empty;
        History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
        History.MarkSaved();
        ResumeLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Saved));
    }

    /// <summary>
    /// その場保存の完了 (ENG-23)。保存前の版が読む元データを「ファイル + 退避した旧内容」の重ね合わせに差し替え、
    /// 現在の版はファイルをそのまま指す新しい元データにする。
    /// </summary>
    public void CompleteInPlaceSave(Saving.InPlaceSaveResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DocumentStorage before = _storage;
        var overlay = new Saving.OverlayByteSource(result.Source, before.AddBuffer, result.Ranges);

        // 前回のその場保存の重ね合わせは、今回の保存の直前の内容 (= 今回の重ね合わせ) を元にするよう付け替える。
        if (_lastOverlay is { } previous)
        {
            previous.Inner = overlay;
        }

        _lastOverlay = overlay;
        before.Source = overlay;
        before.Cache.Dispose();
        before.Cache = NewCache(overlay);

        _storage = CreateStorage(result.Source, before.AddBuffer);
        PieceTree tree = result.Source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, result.Source.Length)) : PieceTree.Empty;
        History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
        History.MarkSaved();
        ResumeLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Saved));
    }

    private Saving.IRebasableOverlay? _lastOverlay;

    /// <summary>
    /// ずらしながらのその場保存の完了 (ENG-24)。保存前の版が読む元データを「今のファイル + 退避した旧内容」の重ね合わせに差し替え、現在の版は
    /// 書き換えたファイルを指す新しい元データにする。Undo 履歴を破棄する計画だった場合は、履歴を消して今の状態だけを残す (仕様 7)。
    /// </summary>
    public void CompleteShiftSave(Saving.ShiftSaveResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (result.Source.Length != Length)
        {
            throw new InvalidOperationException("保存したファイルの長さがドキュメントと違います。");
        }

        DocumentStorage before = _storage;
        if (!result.DiscardedHistory)
        {
            InstallShiftOverlay(before, result);
        }

        _storage = CreateStorage(result.Source, before.AddBuffer);
        PieceTree tree = result.Source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, result.Source.Length)) : PieceTree.Empty;
        var snapshot = new DocumentSnapshot(_storage, tree);
        if (result.DiscardedHistory)
        {
            History.Reset(snapshot);
        }
        else
        {
            History.ReplaceCurrent(snapshot);
        }

        History.MarkSaved();
        ResumeLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Saved));
    }

    /// <summary>
    /// ずらしながらのその場保存が途中で失敗した (ENG-24 の「エラー」): 退避した旧内容があれば、今のドキュメントの内容 (保存前の元データを指す) を
    /// 退避から読むようにして保つ。
    /// </summary>
    public void RecoverAfterShiftFailure(Saving.ShiftSaveResult? recovered)
    {
        if (recovered is not null && !_disposed)
        {
            InstallShiftOverlay(_storage, recovered);
        }

        ResumeLock();
    }

    private void InstallShiftOverlay(DocumentStorage before, Saving.ShiftSaveResult result)
    {
        var overlay = new Saving.ShiftOverlaySource(before.Source, before.AddBuffer, result.Backups, result.OriginalLength);
        if (_lastOverlay is { } previous)
        {
            previous.Inner = overlay;
        }

        _lastOverlay = overlay;
        before.Source = overlay;
        before.Cache.Dispose();
        before.Cache = NewCache(overlay);
    }

    /// <summary>
    /// 元に戻す・やり直しの履歴を消し、今の状態だけを残す (EDIT-19 の仕様 9 の設定「保存時に履歴を消す」。保存の直後に呼ぶ)。
    /// 今の状態が保存した時点なら、消した後も「変更なし」のまま。
    /// </summary>
    public void ClearHistory()
    {
        bool modified = History.IsModified;
        History.Reset(Current);
        if (modified)
        {
            History.MarkUnsaved();
        }
    }

    /// <summary>長時間処理の間、編集を止める・再開する (ENG-09 の仕様 7)。</summary>
    public void SetEditLock(bool locked) => IsEditLocked = locked;

    private DocumentStorage CreateStorage(IByteSource source, AddBuffer addBuffer)
    {
        var storage = new DocumentStorage(this, source, addBuffer, NewCache(source));
        _storages.Add(storage);
        return storage;
    }

    private BlockCache NewCache(IByteSource source)
    {
        long capacity = _storage is null ? _options.CacheCapacity : _storage.Cache.CapacityBytes;
        var cache = new BlockCache(source, capacity, _options.MaxConcurrentReads);
        cache.BlockLoaded += (offset, length) => DataLoaded?.Invoke(this, EventArgs.Empty);
        return cache;
    }

    private void Apply(PieceTree tree, long offset, long removed, long inserted, string description, string? coalesceKey)
    {
        if (LinkedParent is { } linkParent)
        {
            // 連動ビューの編集は親のドキュメントの編集として記録する (ENG-39 の仕様 1)。子の内容は親の変更の通知で作り直す。
            linkParent.ApplyFromLinkedView(tree, _linkStart, _linkLength, offset, removed, inserted, description, coalesceKey);
            return;
        }

        History.Push(new DocumentSnapshot(_storage, tree), description, coalesceKey, offset, removed, inserted);
        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(offset, removed, inserted, isWholeDocument: false));
    }

    /// <summary>データを追加バッファに追記し、それを指す木を返す。追記に失敗したら例外 (ドキュメントは変えない)。</summary>
    private PieceTree AppendToBuffer(ReadOnlySpan<byte> data)
    {
        RequireEditable();
        long at = _storage.AddBuffer.Append(data);
        return PieceTree.FromPiece(Piece.Added(at, data.Length));
    }

    private Piece PatternPiece(ReadOnlySpan<byte> pattern, long length, long phase = 0)
    {
        if (pattern.Length is < 1 or > GeneratedData.MaxPatternLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pattern), "パターンの長さは 1〜4,096 バイトです。");
        }

        RequireEditable();
        long at = _storage.AddBuffer.Append(pattern);
        return Piece.Pattern(at, pattern.Length, length, phase);
    }

    /// <summary>上書きで置き換える既存の長さを返す。長さを変えられないデータソースで末尾を越える場合は拒否する。</summary>
    private long CheckOverwrite(long offset, long length)
    {
        RequireEditable();
        if (offset < 0 || offset > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "オフセットがドキュメントの範囲外です。");
        }

        long available = Length - offset;
        if (length > available && !CanResize)
        {
            throw new FixedLengthException();
        }

        return Math.Min(length, available);
    }

    private void RequireResizable()
    {
        RequireEditable();
        if (!CanResize)
        {
            throw new FixedLengthException();
        }
    }

    private void RequireEditable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEditLocked)
        {
            throw new DocumentLockedException();
        }

        if (IsReadOnly)
        {
            throw new DocumentReadOnlyException();
        }
    }

    /// <summary>
    /// 閉じる。このドキュメントが参照していた他のドキュメントの範囲を手放す。他のドキュメントやアプリ内クリップボードが
    /// このドキュメントの範囲を参照している間は、元データ・追加バッファ・一時ファイルの解放をそれらが実体化されるか
    /// 手放されるまで遅らせる (EDIT-24 の仕様 3・5)。
    /// </summary>
    public void Dispose()
    {
        if (LinkedParent is not null)
        {
            DisposeLinkedView();
            return;
        }

        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lockedFile?.AllowWrites();
            _lockedFile = null;
        }

        foreach (SnapshotRange range in _imported)
        {
            range.DataLoaded -= OnExternalDataLoaded;
            range.ReleaseReference();
        }

        _imported.Clear();

        bool release;
        lock (_lifetimeLock)
        {
            // 参照されていない (または実体化済みの) 範囲は捨てる。
            _exported.RemoveAll(r => r.IsMaterialized || r.ReferenceCount == 0);
            release = _exported.Count == 0 && !_resourcesReleased;
            _resourcesReleased |= release;
        }

        if (release)
        {
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        foreach (DocumentStorage storage in _storages)
        {
            storage.Cache.Dispose();
        }

        foreach (IByteSource source in _storages.Select(s => s.Source).Concat(_ownedExternals).Distinct())
        {
            source.Dispose();
        }

        _storage.AddBuffer.Dispose();
    }
}

/// <summary>エンジンのメモリ使用量 (バイト)。木のノードは概算。</summary>
public readonly record struct EngineMemoryUsage(long Cache, long AddBufferMemory, long AddBufferSpilled, long TreeNodes)
{
    /// <summary>木のノード 1 つあたりの概算のメモリ量。</summary>
    public const long BytesPerNode = 96;

    /// <summary>メモリ上の合計 (一時ファイルに退避した分は含まない)。</summary>
    public long TotalInMemory => Cache + AddBufferMemory + TreeNodes;
}

/// <summary>長さを変えられないデータソースへの長さを変える操作 (ENG-07 の「エラー」)。</summary>
public sealed class FixedLengthException() : InvalidOperationException("このデータソースは長さを変えられません。");

/// <summary>処理中で編集できない (ENG-09 の仕様 7)。</summary>
public sealed class DocumentLockedException() : InvalidOperationException("処理中のため編集できません。");

/// <summary>読み取り専用のドキュメントを変えようとした (EDIT-16 の仕様 2)。</summary>
public sealed class DocumentReadOnlyException() : InvalidOperationException("このドキュメントは読み取り専用です。");

/// <summary>
/// 読み取り専用の理由 (EDIT-16 の仕様 1、ENG-14 の仕様 1)。どの理由で読み取り専用にするかは開く処理 (ENG-14) が決める。
/// </summary>
public enum ReadOnlyReason
{
    /// <summary>読み取り専用ではない。</summary>
    None,

    /// <summary>利用者が切り替えた (編集 > 読み取り専用)。そのまま解除できる。</summary>
    User,

    /// <summary>「読み取り専用で開く」を指定して開いた (データソースは読み取りのアクセス権だけで開いている)。</summary>
    OpenedReadOnly,

    /// <summary>ファイルに読み取り専用属性がある。解除には確認が要る (保存時に属性を外す。ENG-14 の仕様 3)。</summary>
    FileAttribute,

    /// <summary>書き込み権限がない。解除するときに書き込みで開けるかを確かめ、開けなければ読み取り専用のまま (ENG-14 の仕様 3)。</summary>
    AccessDenied,

    /// <summary>他のアプリが書き込みのために開いている (共有違反)。解除には書き込みで開き直す。</summary>
    SharingViolation,

    /// <summary>読み取り専用のメディアにある。解除できない。</summary>
    ReadOnlyMedia,

    /// <summary>ディスク・ボリューム・プロセスメモリの既定。解除には確認ダイアログと開き直しが要る。</summary>
    Device,

    /// <summary>書き戻す先のないデータソース (スナップショットなど)。解除できない (ENG-14 の仕様 2)。</summary>
    NoWriteTarget,

    /// <summary>書き込み保護モード (FOR-01) が有効。解除できない。</summary>
    WriteProtectionMode,
}

/// <summary>読み取り専用の解除のしかた (EDIT-16 の仕様 4)。</summary>
public enum ReadOnlyRelease
{
    /// <summary>そのまま解除できる (利用者が読み取り専用にした)。</summary>
    Immediate,

    /// <summary>確認ダイアログ・書き込みでの開き直し (ENG-14 の仕様 3) を経て解除する。</summary>
    NeedsConfirmation,

    /// <summary>解除できない。「編集を許可する」ボタンを出さない。</summary>
    NotAllowed,
}
