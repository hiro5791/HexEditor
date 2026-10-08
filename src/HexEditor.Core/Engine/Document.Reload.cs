using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>マージの結果 (ENG-19 の仕様 5)。</summary>
public sealed record MergeResult(long OldSourceLength, long NewSourceLength)
{
    /// <summary>外部の変更で長さが変わった (オフセットがずれて正しく適用できない可能性がある)。</summary>
    public bool LengthChanged => OldSourceLength != NewSourceLength;
}

/// <summary>再読み込みと変更の破棄 (ENG-18)、外部変更の後の元データの差し替えとマージ (ENG-19)。</summary>
public sealed partial class Document
{
    /// <summary>「変更の破棄」の Undo の説明。</summary>
    public const string DiscardDescription = "変更の破棄";

    /// <summary>「マージ」の Undo の説明。</summary>
    public const string MergeDescription = "外部の変更とのマージ";

    /// <summary>現在の内容が指す元データの長さ (マージの前に、外部の変更で長さが変わったかを調べる)。</summary>
    public long CurrentSourceLength => Current.Storage.Source.Length;

    /// <summary>
    /// 再読み込み (変更を残す。ENG-18 の仕様 1): ブロックキャッシュと読めなかった範囲の記録を捨てて読み直す。変更していないバイトは
    /// データソースの今の内容で表示し、変更したバイトはそのまま残す。
    /// </summary>
    public void RefreshFromSource()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (DocumentStorage storage in _storages)
        {
            storage.Cache.Invalidate();
        }

        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Reloaded));
    }

    /// <summary>
    /// 変更を破棄して再読み込み (データソースが外部で変更されていない場合。ENG-18 の仕様 3): 今の元データ全体を指す状態を
    /// 1 つの Undo 単位として積み、その状態を「保存した時点」にする。Ctrl+Z で破棄の前に戻せる。
    /// </summary>
    public void DiscardChanges(string description = DiscardDescription)
    {
        RequireEditable();
        long length = Source.Length;
        PieceTree tree = length > 0 ? PieceTree.FromPiece(Piece.Original(0, length)) : PieceTree.Empty;
        long before = Length;
        History.Push(new DocumentSnapshot(_storage, tree), description, null, 0, before, length);
        History.MarkSaved();
        foreach (DocumentStorage storage in _storages)
        {
            storage.Cache.Invalidate();
        }

        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, before, length, isWholeDocument: true, DocumentChangeKind.Reloaded));
    }

    /// <summary>
    /// 外部で変更されたデータソースに差し替えて読み直す (ENG-18 の仕様 3 の後半、ENG-19 の「再読み込み」・未編集の自動の再読み込み)。
    /// すべての変更を捨て、Undo 履歴を消去する (元に戻せない)。以前の元データは、参照している範囲 (他のタブ・クリップボード) が
    /// 手放されるか閉じるまで開いたままにする。
    /// </summary>
    public void ReplaceSource(IByteSource source)
    {
        RequireEditable();
        _storage = CreateStorage(source, _storage.AddBuffer);
        PieceTree tree = source.Length > 0 ? PieceTree.FromPiece(Piece.Original(0, source.Length)) : PieceTree.Empty;
        History.Reset(new DocumentSnapshot(_storage, tree));
        if (LockState == FileLockState.Failed)
        {
            LockState = FileLockState.Unlocked;
        }

        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, 0, 0, isWholeDocument: true, DocumentChangeKind.Reloaded));
    }

    /// <summary>
    /// マージ (ENG-19 の仕様 5): 新しいデータソースを元データとし、自分の変更を同じオフセットに適用し直す。変更していない部分 (元データの
    /// ピース) は新しいデータソースの同じ位置を指すようにし、挿入・上書きしたデータはそのまま残す。新しいデータソースが長い場合は、
    /// 伸びた分を末尾に加える。短い場合は、範囲外になった部分を除く。1 つの Undo 単位として記録する (Ctrl+Z でマージの前に戻る)。
    /// 処理は編集の数 (ピースの数) に比例し、ファイルサイズには依存しない。
    /// </summary>
    public MergeResult MergeOnto(IByteSource source, string description = MergeDescription)
    {
        RequireEditable();
        DocumentSnapshot current = Current;
        long oldLength = current.Storage.Source.Length;
        long newLength = source.Length;
        DocumentStorage previous = _storage;
        _storage = CreateStorage(source, previous.AddBuffer);
        CopyExternals(current.Storage, _storage);

        var pieces = new List<Piece>();
        foreach ((_, Piece piece) in current.Tree.EnumerateAll())
        {
            if (piece.Kind != PieceKind.Original)
            {
                pieces.Add(piece);
                continue;
            }

            long end = Math.Min(piece.Offset + piece.Length, newLength);
            if (end > piece.Offset)
            {
                pieces.Add(Piece.Original(piece.Offset, end - piece.Offset));
            }
        }

        if (newLength > oldLength)
        {
            pieces.Add(Piece.Original(oldLength, newLength - oldLength));
        }

        PieceTree tree = PieceTree.FromPieces(pieces);
        long before = Length;
        History.Push(new DocumentSnapshot(_storage, tree), description, null, 0, before, tree.Length);
        if (LockState == FileLockState.Failed)
        {
            LockState = FileLockState.Unlocked;
        }

        UpdateLock();
        Changed?.Invoke(this, new DocumentChangedEventArgs(0, before, tree.Length, isWholeDocument: true, DocumentChangeKind.Reloaded));
        return new MergeResult(oldLength, newLength);
    }

    /// <summary>外部参照のピースの番号を保つため、表を写す (番号は変えない)。</summary>
    private static void CopyExternals(DocumentStorage from, DocumentStorage to)
    {
        if (!ReferenceEquals(from, to) && to.Externals.Count == 0)
        {
            to.Externals.AddRange(from.Externals);
        }
    }
}
