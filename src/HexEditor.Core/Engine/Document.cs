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
}

/// <summary>内容の変更の通知。範囲はドキュメント上の位置。Undo・Redo では全体が変わったものとして通知する。</summary>
public sealed class DocumentChangedEventArgs(long offset, long removedLength, long insertedLength, bool isWholeDocument)
    : EventArgs
{
    public long Offset { get; } = offset;

    public long RemovedLength { get; } = removedLength;

    public long InsertedLength { get; } = insertedLength;

    public bool IsWholeDocument { get; } = isWholeDocument;
}

/// <summary>
/// 編集対象 1 つ (ENG-02〜ENG-07)。編集は UI スレッドから呼ぶ。各編集は新しいスナップショットを作って履歴に積む。
/// 長さを変えられないデータソースでは上書きだけを受け付ける (ENG-07)。
/// </summary>
public sealed class Document : IDisposable
{
    private readonly List<DocumentStorage> _storages = [];
    private readonly DocumentOptions _options;
    private DocumentStorage _storage;
    private bool _disposed;

    public Document(IByteSource source, DocumentOptions? options = null)
    {
        _options = options ?? new DocumentOptions();
        Id = Guid.NewGuid();
        string spillPath = Path.Combine(_options.TempDirectory, Id.ToString("N"), "add.bin");
        _storage = CreateStorage(source, new AddBuffer(spillPath, _options.AddBufferMemoryLimit));

        // 開いた直後は元データ全体を指すピース 1 つ (長さ 0 ならピースなし)。ENG-02 の仕様 8。
        PieceTree tree = source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, source.Length)) : PieceTree.Empty;
        History = new EditHistory(new DocumentSnapshot(_storage, tree));
    }

    /// <summary>
    /// 復旧用データからドキュメントを作り直す (ENG-27 の仕様 6)。<paramref name="addBuffer"/> は復旧用データの一時ファイルを
    /// 開き直したもので、<paramref name="pieces"/> はその時点の内容。Undo 履歴は復元せず、「変更あり」の状態で始まる。
    /// </summary>
    public static Document Restore(Guid id, IByteSource source, AddBuffer addBuffer, IEnumerable<Piece> pieces, DocumentOptions? options = null)
    {
        var list = pieces.ToList();
        foreach (Piece piece in list)
        {
            bool valid = piece.Kind switch
            {
                PieceKind.Original => piece.Offset >= 0 && piece.Offset + piece.Length <= source.Length,
                PieceKind.Added => piece.Offset >= 0 && piece.Offset + piece.Length <= addBuffer.Length,
                PieceKind.Pattern => piece.Offset >= 0 && piece.Offset + piece.PatternLength <= addBuffer.Length,
                PieceKind.Random => piece.Offset >= 0,
                _ => false,
            };
            if (!valid)
            {
                throw new InvalidDataException("復旧用データのピースが元データまたは追加バッファの範囲外です。");
            }
        }

        var document = new Document(id, source, addBuffer, PieceTree.FromPieces(list), options ?? new DocumentOptions());
        document.History.MarkUnsaved();
        return document;
    }

    private Document(Guid id, IByteSource source, AddBuffer addBuffer, PieceTree tree, DocumentOptions options)
    {
        _options = options;
        Id = id;
        _storage = CreateStorage(source, addBuffer);
        History = new EditHistory(new DocumentSnapshot(_storage, tree));
    }

    public Guid Id { get; }

    /// <summary>
    /// 現在の内容が今の元データと追加バッファだけで表せるか。保存より前の版に Undo した直後は、置き換え前の
    /// 元データを指すため偽 (復旧用データは参照で記録できない)。
    /// </summary>
    public bool CurrentUsesLatestSource => ReferenceEquals(Current.Storage, _storage);

    /// <summary>現在の元データ。保存 (ENG-20) の後は保存したファイルに変わる。</summary>
    public IByteSource Source => _storage.Source;

    public EditHistory History { get; }

    public DocumentSnapshot Current => History.Current;

    public long Length => Current.Length;

    /// <summary>挿入・削除・切り取り・挿入貼り付けができるか (ENG-07)。</summary>
    public bool CanResize => Source.Capabilities.HasFlag(SourceCapabilities.CanResize);

    /// <summary>元のデータソースに保存できるか (ENG-01 の仕様 6)。偽なら「名前を付けて保存」だけになる。</summary>
    public bool CanSave => Source.Capabilities.HasFlag(SourceCapabilities.CanWrite);

    /// <summary>エンジンのメモリ使用量の内訳 (ENG-08 の仕様 5)。</summary>
    public EngineMemoryUsage MemoryUsage => new(
        _storage.Cache.MemoryBytes,
        _storage.AddBuffer.MemoryBytes,
        _storage.AddBuffer.SpilledBytes,
        Current.Tree.PieceCount * EngineMemoryUsage.BytesPerNode);

    public bool IsModified => History.IsModified;

    public AddBuffer AddBuffer => _storage.AddBuffer;

    public BlockCache Cache => _storage.Cache;

    /// <summary>
    /// ドキュメントを変更する長時間処理・保存の実行中は true (ENG-09 の仕様 7)。この間の編集は
    /// <see cref="DocumentLockedException"/> になる。
    /// </summary>
    public bool IsEditLocked { get; internal set; }

    /// <summary>内容が変わった。</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>表示中のデータの読み込みが終わった (再描画のきっかけ)。スレッドプールから呼ばれる。</summary>
    public event EventHandler? DataLoaded;

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
    /// 同じ元データを共有している場合はピースを参照するだけ (O(log n))、そうでなければデータを追加バッファに複製する
    /// (ENG-02 の仕様 9)。
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

    /// <summary>挿入・上書きに使う内容。元データを共有していればピースの参照、違えば追加バッファへの複製。</summary>
    private PieceTree ContentFrom(DocumentSnapshot source, long sourceOffset, long length)
    {
        RequireEditable();
        // 同じ元データ (保存で切り替わる前のスナップショットは別の元データ) ならピースをそのまま共有できる。
        if (ReferenceEquals(source.Storage, _storage))
        {
            return source.Tree.Slice(sourceOffset, length);
        }

        // 別のドキュメント: 1 MiB ずつ読んで追加バッファに追記する。追記は連続するため、ピースは 1 つにまとまる。
        byte[] buffer = new byte[Math.Min(length, 1024 * 1024)];
        PieceTree content = PieceTree.Empty;
        for (long done = 0; done < length; done += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - done);
            source.Read(sourceOffset + done, buffer.AsSpan(0, n));
            long at = _storage.AddBuffer.Append(buffer.AsSpan(0, n));
            content = content.Concat(PieceTree.FromPiece(Piece.Added(at, n)));
        }

        return content;
    }

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

    public void Undo()
    {
        RequireEditable();
        History.Undo();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true));
    }

    public void Redo()
    {
        RequireEditable();
        History.Redo();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true));
    }

    /// <summary>現在の状態を「保存した時点」にする (保存処理から呼ぶ)。</summary>
    public void MarkSaved() => History.MarkSaved();

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
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true));
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
        before.Cache = new BlockCache(overlay, _options.CacheCapacity, _options.MaxConcurrentReads);

        _storage = CreateStorage(result.Source, before.AddBuffer);
        PieceTree tree = result.Source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, result.Source.Length)) : PieceTree.Empty;
        History.ReplaceCurrent(new DocumentSnapshot(_storage, tree));
        History.MarkSaved();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true));
    }

    private Saving.OverlayByteSource? _lastOverlay;

    /// <summary>長時間処理の間、編集を止める・再開する (ENG-09 の仕様 7)。</summary>
    public void SetEditLock(bool locked) => IsEditLocked = locked;

    private DocumentStorage CreateStorage(IByteSource source, AddBuffer addBuffer)
    {
        var cache = new BlockCache(source, _options.CacheCapacity, _options.MaxConcurrentReads);
        cache.BlockLoaded += (offset, length) => DataLoaded?.Invoke(this, EventArgs.Empty);
        var storage = new DocumentStorage(source, addBuffer, cache);
        _storages.Add(storage);
        return storage;
    }

    private void Apply(PieceTree tree, long offset, long removed, long inserted, string description, string? coalesceKey)
    {
        History.Push(new DocumentSnapshot(_storage, tree), description, coalesceKey);
        Changed?.Invoke(this, new DocumentChangedEventArgs(offset, removed, inserted, isWholeDocument: false));
    }

    /// <summary>データを追加バッファに追記し、それを指す木を返す。追記に失敗したら例外 (ドキュメントは変えない)。</summary>
    private PieceTree AppendToBuffer(ReadOnlySpan<byte> data)
    {
        RequireEditable();
        long at = _storage.AddBuffer.Append(data);
        return PieceTree.FromPiece(Piece.Added(at, data.Length));
    }

    private Piece PatternPiece(ReadOnlySpan<byte> pattern, long length)
    {
        if (pattern.Length is < 1 or > GeneratedData.MaxPatternLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pattern), "パターンの長さは 1〜4,096 バイトです。");
        }

        RequireEditable();
        long at = _storage.AddBuffer.Append(pattern);
        return Piece.Pattern(at, pattern.Length, length);
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
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (DocumentStorage storage in _storages)
        {
            storage.Cache.Dispose();
        }

        foreach (IByteSource source in _storages.Select(s => s.Source).Distinct())
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
