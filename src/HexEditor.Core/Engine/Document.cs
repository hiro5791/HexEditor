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
    private readonly DocumentStorage _storage;
    private bool _disposed;

    public Document(IByteSource source, DocumentOptions? options = null)
    {
        options ??= new DocumentOptions();
        Source = source;
        Id = Guid.NewGuid();
        string spillPath = Path.Combine(options.TempDirectory, Id.ToString("N"), "add.bin");
        var cache = new BlockCache(source, options.CacheCapacity, options.MaxConcurrentReads);
        _storage = new DocumentStorage(source, new AddBuffer(spillPath, options.AddBufferMemoryLimit), cache);
        cache.BlockLoaded += (offset, length) => DataLoaded?.Invoke(this, EventArgs.Empty);

        // 開いた直後は元データ全体を指すピース 1 つ (長さ 0 ならピースなし)。ENG-02 の仕様 8。
        PieceTree tree = source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, source.Length)) : PieceTree.Empty;
        History = new EditHistory(new DocumentSnapshot(_storage, tree));
    }

    public Guid Id { get; }

    public IByteSource Source { get; }

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
        _storage.Cache.Dispose();
        _storage.AddBuffer.Dispose();
        Source.Dispose();
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
