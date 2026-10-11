using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary>
/// まとめて行う編集 1 件: [Offset, Offset + RemoveLength) を <see cref="Content"/> (null なら何も入れない = 削除) に置き換える。
/// マルチ選択の要素ごとの塗りつぶし・矩形の挿入など (EDIT-07 の仕様 7、EDIT-17 の仕様 4)。
/// </summary>
public sealed record ContentEdit(long Offset, long RemoveLength, EditContent? Content);

public sealed partial class Document
{
    /// <summary>
    /// 入力をまとめる編集グループ (マルチカーソルへの入力。EDIT-08 の仕様 6)。グループの最初の編集が直前の項目と同じ種類
    /// (<paramref name="coalesceKey"/>) の入力で、入力のまとめ (EDIT-19 の仕様 5) の条件に合えば、直前の項目にまとめる。
    /// </summary>
    public IDisposable BeginCoalescingGroup(string description, string? coalesceKey)
    {
        History.BeginGroup(description, coalesceKey, mergeWithPrevious: coalesceKey is not null);
        return new GroupScope(History);
    }

    /// <summary>
    /// 複数の範囲を置き換えた木を作る (ドキュメントはまだ変えない)。編集は開始位置の昇順で重ならないこと。新しい木はピースを並べて一度に
    /// 作るので、件数に比例した時間で済む。<see cref="CommitReplacements"/> で 1 回の編集として入れる (Undo 1 回ですべて戻る)。
    /// 内容のデータソース (一時ファイル) は、このドキュメントが持つ (コミットしなかった場合も閉じるときに閉じる)。
    /// </summary>
    public PreparedReplacement PrepareContentEdits(IEnumerable<ContentEdit> edits, LongRunningOperation? operation = null,
        bool ignoreEditLock = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEditLocked && !ignoreEditLock)
        {
            throw new DocumentLockedException();
        }

        if (IsReadOnly)
        {
            throw new DocumentReadOnlyException();
        }

        PieceTree before = Current.Tree;
        long length = before.Length;
        var pieces = new List<Piece>((int)Math.Min(before.PieceCount + 16, 1 << 20));
        var walker = new PieceTree.Walker(before);
        var patterns = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        long cursor = 0, count = 0, first = -1, lastEnd = 0;
        foreach (ContentEdit edit in edits)
        {
            if (edit.Offset < cursor || edit.RemoveLength < 0 || edit.Offset + edit.RemoveLength > length)
            {
                edit.Content?.Dispose();
                throw new ArgumentException("編集は開始位置の昇順で、重ならず、ドキュメントの中にある必要があります。", nameof(edits));
            }

            if ((count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation?.CancellationToken.ThrowIfCancellationRequested();
            }

            walker.CopyTo(edit.Offset, pieces);
            if (edit.Content is { Length: > 0 } content)
            {
                pieces.Add(PieceOf(content, patterns));
            }

            if (first < 0)
            {
                first = edit.Offset;
            }

            cursor = edit.Offset + edit.RemoveLength;
            walker.SkipTo(cursor);
            lastEnd = cursor;
            count++;
            operation?.Report(edit.Offset);
        }

        if (count == 0)
        {
            return new PreparedReplacement(before, before, 0, 0, 0, 0);
        }

        walker.CopyTo(length, pieces);
        PieceTree tree = PieceTree.Build(pieces);
        if (tree.Length != length && !CanResize)
        {
            throw new FixedLengthException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        long removed = lastEnd - first;
        long inserted = removed + (tree.Length - length);
        return new PreparedReplacement(before, tree, count, first, removed, inserted);
    }

    /// <summary>内容 1 つをピース 1 つにする (同じパターンの配列は追加バッファに 1 回だけ書く)。</summary>
    private Piece PieceOf(EditContent content, Dictionary<byte[], long> patterns)
    {
        switch (content.Kind)
        {
            case EditContentKind.Pattern:
                if (!patterns.TryGetValue(content.Data!, out long at))
                {
                    at = _storage.AddBuffer.Append(content.Data);
                    patterns[content.Data!] = at;
                }

                return Piece.Pattern(at, content.Data!.Length, content.Length, content.Position);
            case EditContentKind.Random:
                return Piece.Random(content.Seed, content.Position, content.Length);
            case EditContentKind.Bytes:
                return Piece.Added(_storage.AddBuffer.Append(content.Data.AsSpan(0, (int)content.Length)), content.Length);
            case EditContentKind.Original:
                return Piece.Original(content.Position, content.Length);
            default:
                IByteSource source = content.TakeSource();
                int index = _storage.Externals.IndexOf(source);
                if (index < 0)
                {
                    _storage.Externals.Add(source);
                    index = _storage.Externals.Count - 1;
                }

                if (content.OwnsSource && !_ownedExternals.Contains(source))
                {
                    _ownedExternals.Add(source);
                }

                return Piece.External(index, content.Position, content.Length);
        }
    }

    // ---- 履歴の任意の時点への移動 (EDIT-20 の仕様 3) ----

    /// <summary>
    /// 履歴の <paramref name="index"/> 番目の項目 (0 は開いた時点) の直後の状態に移る。途中の元に戻す / やり直しを一度に行い、変更の通知は
    /// 1 回だけ出す (ルートの差し替えなので一定時間)。移った先の項目の範囲を選択するよう通知する。
    /// </summary>
    public void MoveToHistory(int index)
    {
        RequireEditable();
        if (index < 0 || index >= History.Count || index == History.CurrentIndex)
        {
            return;
        }

        bool undo = index < History.CurrentIndex;
        HistoryEntry target = History.MoveTo(index);
        (long, long)? selection = undo
            ? History.Entries[index + 1].Range is { } r ? (r.Offset, r.BeforeLength) : null
            : target.Range is { } t ? (t.Offset, t.AfterLength) : null;
        AfterHistoryMove(undo ? DocumentChangeKind.Undo : DocumentChangeKind.Redo, selection);
    }
}
