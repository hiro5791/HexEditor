using HexEditor.Core.Operations;

namespace HexEditor.Core.Engine;

/// <summary>置換で書く内容の部品の種類。</summary>
public enum ReplacementPartKind
{
    /// <summary>バイト列 (<see cref="ReplacementPart.Data"/>)。同じ配列を使う部品は追加バッファに 1 回だけ書く。</summary>
    Literal,

    /// <summary>置換の前の状態の範囲 [Offset, Offset + Length) をそのまま使う (置換語の `??`。データをコピーしない)。</summary>
    Copy,

    /// <summary><see cref="ReplacementPart.Data"/> の繰り返しを <see cref="ReplacementPart.Length"/> バイト (埋め草。FIND-24 の仕様 3)。</summary>
    Fill,
}

/// <summary>置換で書く内容の部品 1 つ。</summary>
public readonly record struct ReplacementPart(ReplacementPartKind Kind, byte[]? Data, long Offset, long Length)
{
    public static ReplacementPart Literal(byte[] data) => new(ReplacementPartKind.Literal, data, 0, data.Length);

    public static ReplacementPart Copy(long offset, long length) => new(ReplacementPartKind.Copy, null, offset, length);

    public static ReplacementPart Fill(byte[] pattern, long length) => new(ReplacementPartKind.Fill, pattern, 0, length);
}

/// <summary>
/// 置換 1 件: 置換の前の状態の [Offset, Offset + RemoveLength) を <see cref="Parts"/> を並べたものに置き換える。
/// </summary>
public sealed record ReplacementEdit(long Offset, long RemoveLength, IReadOnlyList<ReplacementPart> Parts)
{
    /// <summary>置き換えたあとの長さ。</summary>
    public long InsertLength => Parts.Sum(p => p.Length);
}

/// <summary>
/// 適用する前の置換 (<see cref="Document.PrepareReplacements"/> の結果)。<see cref="Base"/> の状態に対してだけ適用できる。
/// </summary>
public sealed record PreparedReplacement(PieceTree Base, PieceTree Result, long Count, long Offset, long Removed, long Inserted)
{
    /// <summary>置換で変わる長さ (長くなれば正)。</summary>
    public long LengthDelta => Result.Length - Base.Length;
}

public sealed partial class Document
{
    /// <summary>
    /// 複数の置換をまとめて 1 回の編集として適用する (すべて置換。FIND-23 の仕様 2・3)。置換は開始位置の昇順で重ならないこと。
    /// 新しい木はピースを並べて一度に作るため、件数に比例した時間で済み、変更の通知も 1 回だけ出す。Undo 1 回ですべて戻る。
    /// 長さを変えられないデータソースで長さが変わる場合は <see cref="FixedLengthException"/>。キャンセルされた場合は何も変えない。
    /// UI のスレッドで呼ぶ (変更の通知を出すため)。件数が多い場合は <see cref="PrepareReplacements"/> をバックグラウンドで呼び、
    /// <see cref="CommitReplacements"/> だけを UI のスレッドで呼ぶ。
    /// </summary>
    /// <returns>適用した置換の件数。</returns>
    public long ApplyReplacements(IEnumerable<ReplacementEdit> edits, string description)
    {
        PreparedReplacement prepared = PrepareReplacements(edits);
        CommitReplacements(prepared, description);
        return prepared.Count;
    }

    /// <summary>
    /// 置換を適用した木を作る (ドキュメントはまだ変えない)。どのスレッドからも呼べるが、作ってから
    /// <see cref="CommitReplacements"/> までの間にドキュメントを編集してはいけない (すべて置換は編集を止めた長時間処理の中で呼ぶ)。
    /// 置換語のバイト列は追加バッファに書く。
    /// </summary>
    /// <param name="ignoreEditLock">
    /// すべて置換の長時間処理自身がドキュメントの編集を止めている (ENG-09 の仕様 7) ときに true。それ以外は false。
    /// </param>
    public PreparedReplacement PrepareReplacements(IEnumerable<ReplacementEdit> edits, LongRunningOperation? operation = null,
        bool ignoreEditLock = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEditLocked && !ignoreEditLock)
        {
            throw new DocumentLockedException();
        }

        PieceTree before = Current.Tree;
        long length = before.Length;
        var pieces = new List<Piece>((int)Math.Min(before.PieceCount + 16, 1 << 20));
        var walker = new PieceTree.Walker(before);
        var appended = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        long cursor = 0;
        long count = 0;
        long first = -1;
        long lastEnd = 0;
        foreach (ReplacementEdit edit in edits)
        {
            if (edit.Offset < cursor || edit.RemoveLength < 0 || edit.Offset + edit.RemoveLength > length)
            {
                throw new ArgumentException("置換は開始位置の昇順で、重ならず、ドキュメントの中にある必要があります。", nameof(edits));
            }

            if ((count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation?.CancellationToken.ThrowIfCancellationRequested();
            }

            walker.CopyTo(edit.Offset, pieces);
            foreach (ReplacementPart part in edit.Parts)
            {
                if (part.Length == 0)
                {
                    continue;
                }

                switch (part.Kind)
                {
                    case ReplacementPartKind.Literal:
                        if (!appended.TryGetValue(part.Data!, out long at))
                        {
                            at = _storage.AddBuffer.Append(part.Data);
                            appended[part.Data!] = at;
                        }

                        pieces.Add(Piece.Added(at, part.Data!.Length));
                        break;
                    case ReplacementPartKind.Copy:
                        AddRange(before, part.Offset, part.Length, pieces);
                        break;
                    case ReplacementPartKind.Fill:
                        if (part.Data!.Length == 1 || part.Length <= part.Data.Length)
                        {
                            byte[] bytes = new byte[part.Length];
                            for (int i = 0; i < bytes.Length; i++)
                            {
                                bytes[i] = part.Data[i % part.Data.Length];
                            }

                            pieces.Add(Piece.Added(_storage.AddBuffer.Append(bytes), bytes.Length));
                        }
                        else
                        {
                            pieces.Add(Piece.Pattern(_storage.AddBuffer.Append(part.Data), part.Data.Length, part.Length));
                        }

                        break;
                }
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

    /// <summary>
    /// <see cref="PrepareReplacements"/> で作った木を 1 回の編集として適用する。作った後にドキュメントが変わっていたら
    /// <see cref="InvalidOperationException"/> (何も変えない)。件数が 0 なら何もしない。
    /// </summary>
    public void CommitReplacements(PreparedReplacement prepared, string description, bool ignoreEditLock = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsEditLocked && !ignoreEditLock)
        {
            throw new DocumentLockedException();
        }

        if (prepared.Count == 0)
        {
            return;
        }

        if (!ReferenceEquals(Current.Tree, prepared.Base))
        {
            throw new InvalidOperationException("置換の準備の後にドキュメントが変更されました。");
        }

        Apply(prepared.Result, prepared.Offset, prepared.Removed, prepared.Inserted, description, null);
    }

    private static void AddRange(PieceTree tree, long offset, long length, List<Piece> sink)
    {
        if (length <= 0)
        {
            return;
        }

        foreach ((long _, Piece piece) in tree.Enumerate(offset, length))
        {
            sink.Add(piece);
        }
    }
}
