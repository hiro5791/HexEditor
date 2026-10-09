using HexEditor.Core.Engine;
using HexEditor.Core.Selection;

namespace HexEditor.Core.View;

/// <summary>
/// マルチ選択・矩形選択・マルチカーソルへの編集 (EDIT-07 の仕様 7・8、EDIT-08 の仕様 4〜7、EDIT-17)。
/// </summary>
public sealed partial class EditorState
{
    // マルチカーソルの各カーソルに同じ操作をしている間 (ForEachCaret)。
    private bool _caretLoop;
    private Caret[]? _loopCarets;
    private int _loopIndex;

    /// <summary>
    /// 直前のマルチカーソルへの入力で、入力できなかったカーソルの数 (EDIT-08 の「エラー」: 「N 個のカーソルで入力できませんでした」)。
    /// </summary>
    public int LastCaretFailures { get; private set; }

    /// <summary>マルチ選択・矩形選択か (要素ごとの処理が要る形)。</summary>
    public bool HasMultipleRanges => _rect is not null || SelectionKind == SelectionKind.Multiple;

    /// <summary>
    /// すべてのカーソルに同じ操作をする (EDIT-08 の仕様 4)。オフセットの大きいカーソルから順に行い、編集でずれた他のカーソルの位置を
    /// 直す (仕様 5)。<paramref name="groupDescription"/> を指定すると、すべてのカーソルへの編集を 1 つの編集グループにし、
    /// <paramref name="coalesceKey"/> が直前の項目と同じなら入力のまとめ (EDIT-19 の仕様 5) の規則でまとめる (仕様 6)。
    /// 重なったカーソルは 1 つにまとめる (仕様 7)。
    /// </summary>
    private EditResult ForEachCaret(Func<EditResult> op, string? groupDescription = null, string? coalesceKey = null)
    {
        // 主カーソルの順番は最後 (最後に追加したもの)。
        var all = new List<(Caret Caret, int Order)>(CaretCount);
        for (int i = 0; i < (_carets?.Count ?? 0); i++)
        {
            all.Add((_carets![i], i));
        }

        all.Add((PrimaryCaret, int.MaxValue));
        all.Sort((a, b) => b.Caret.Offset.CompareTo(a.Caret.Offset));
        _loopCarets = all.Select(a => a.Caret).ToArray();
        int[] orders = all.Select(a => a.Order).ToArray();
        int failures = 0;
        EditResult combined = EditResult.Ignored;
        EditResult failure = EditResult.Ignored;
        IDisposable? group = groupDescription is null ? null : Document.BeginCoalescingGroup(groupDescription, coalesceKey);
        _caretLoop = true;
        try
        {
            for (int i = 0; i < _loopCarets.Length; i++)
            {
                _loopIndex = i;
                LoadCaret(_loopCarets[i]);
                EditResult r = op();
                if (r is EditResult.Done or EditResult.PastedAsText or EditResult.Truncated)
                {
                    combined = combined == EditResult.Ignored ? r : combined;
                }
                else if (r != EditResult.Ignored)
                {
                    failures++;
                    failure = r;
                }

                _loopCarets[i] = new Caret(_cursor, _selectionLength > 0 ? _anchor : -1, LowNibble);
            }
        }
        finally
        {
            _caretLoop = false;
            group?.Dispose();
        }

        // 重なったカーソルは 1 つにまとめる (後から追加したものを残す)。
        var merged = new Dictionary<long, (Caret Caret, int Order)>();
        for (int i = 0; i < _loopCarets.Length; i++)
        {
            Caret c = _loopCarets[i];
            if (!merged.TryGetValue(c.Offset, out var existing) || existing.Order < orders[i])
            {
                merged[c.Offset] = (c, orders[i]);
            }
        }

        var ordered = merged.Values.OrderBy(v => v.Order).ToList();
        _loopCarets = null;
        LoadCaret(ordered[^1].Caret);
        _carets = ordered.Count > 1 ? ordered.Take(ordered.Count - 1).Select(v => v.Caret).ToList() : null;
        LastCaretFailures = failures;
        EnsureCursorVisible();
        RaiseChanged();
        if (combined == EditResult.Ignored)
        {
            return failure;
        }

        return combined;
    }

    /// <summary>移動キーをすべてのカーソルに適用する (EDIT-08 の仕様 4)。各カーソルが自分の選択範囲を持つ。</summary>
    private bool MoveCarets(Action move)
    {
        if (_caretLoop || !HasMultipleCarets)
        {
            return false;
        }

        ForEachCaret(() =>
        {
            move();
            return EditResult.Done;
        });
        return true;
    }

    /// <summary>
    /// マルチ選択・矩形選択への入力・貼り付けの前に、各要素の先頭に要素を選択したカーソルを置く (EDIT-07 の仕様 7「入力」、EDIT-17 の仕様 5)。
    /// 矩形では各行の左端に選択なしのカーソルを置く。カーソル数の上限を超える場合は false。
    /// </summary>
    private bool PrepareCaretsForInput(bool keepSelections)
    {
        if (_caretLoop || !HasMultipleRanges)
        {
            return true;
        }

        return CaretsAtSelectionElements(keepSelections && _rect is null) == SelectionResult.Done;
    }

    /// <summary>
    /// 選択の要素ごとに操作をする (マルチ選択・矩形選択の貼り付け。EDIT-07 の仕様 7)。各要素を選択したカーソルに変えてから、
    /// オフセットの大きい要素から順に <paramref name="op"/> を呼ぶ。引数は要素の番号 (オフセット順で 0 から)。全体を 1 つの編集グループにする。
    /// </summary>
    public EditResult ForEachSelectionElement(Func<int, EditResult> op, string description, bool keepSelections = true)
    {
        if (!HasMultipleRanges && !HasMultipleCarets)
        {
            return op(0);
        }

        if (!PrepareCaretsForInput(keepSelections))
        {
            return EditResult.TooManyCarets;
        }

        int count = CaretCount;
        return ForEachCaret(() => op(count - 1 - _loopIndex), description);
    }

    // ---- 要素ごとの削除 (EDIT-07 の仕様 7・8、EDIT-17 の仕様 1・6) ----

    /// <summary>
    /// マルチ選択・矩形選択のすべての要素を削除する (1 つの編集グループ。仕様 7)。新しい木を一度に作るので、要素数に比例した時間で済む。
    /// 矩形で行数が上限を超える場合は <see cref="EditResult.TooManyRows"/> (EDIT-17 の仕様 6)。
    /// </summary>
    public EditResult DeleteSelectedRanges(string description = "削除")
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        if (CheckRectangleRows() is { } tooMany)
        {
            return tooMany;
        }

        SelectionSnapshot selection = CaptureSelection();
        if (selection.IsEmpty)
        {
            return EditResult.Ignored;
        }

        PreparedReplacement prepared = PrepareRangeDeletion(Document, selection);
        CommitRangeDeletion(prepared, selection, description);
        return EditResult.Done;
    }

    /// <summary>矩形の行数が長さの変わる操作の上限 (EDIT-17 の仕様 6) を超えていれば <see cref="EditResult.TooManyRows"/>。</summary>
    public EditResult? CheckRectangleRows() =>
        _rect is { } r && SelectionSnapshot.RectangleRowCount(r, Layout.Length) > MaxRectangleRows ? EditResult.TooManyRows : null;

    /// <summary>
    /// 選択の要素をすべて削除した木を作る (ドキュメントはまだ変えない)。要素数が多い (10,000 を超える) 場合は長時間処理の中で呼ぶ
    /// (EDIT-07 の「巨大ファイル・長時間処理」)。
    /// </summary>
    public static PreparedReplacement PrepareRangeDeletion(Document document, SelectionSnapshot selection,
        Operations.LongRunningOperation? operation = null, bool ignoreEditLock = false, CancellationToken cancellationToken = default) =>
        document.PrepareReplacements(selection.Ranges.Select(r => new ReplacementEdit(r.Start, r.Length, [])), operation, ignoreEditLock,
            cancellationToken);

    /// <summary>
    /// <see cref="PrepareRangeDeletion"/> で作った木を 1 回の編集として入れ、カーソルを主要素があった位置 (削除で詰まった後の位置) に置く。
    /// </summary>
    public void CommitRangeDeletion(PreparedReplacement prepared, SelectionSnapshot selection, string description, bool ignoreEditLock = false)
    {
        // 主要素より前で削除するバイト数だけ、主要素の位置は前へずれる。
        long position = selection.Primary?.Start ?? selection.Bounds.Start;
        long before = 0;
        foreach (ByteRange r in selection.Ranges)
        {
            if (r.Start >= position)
            {
                break;
            }

            before += Math.Min(r.Length, position - r.Start);
        }

        CollapseCarets();
        _rect = null;
        _others = null;
        Document.CommitReplacements(prepared, description, ignoreEditLock);
        _cursor = Math.Clamp(position - before, 0, Layout.MaxCursor);
        ClearSelectionAnchor();
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
    }

    // ---- 要素ごとの上書き (塗りつぶしなど。EDIT-07 の仕様 7、EDIT-29 の仕様 4) ----

    /// <summary>
    /// 選択の要素ごとに <paramref name="contentFor"/> の内容で上書きした木を作る (長さは変えない)。内容は要素の長さと同じであること。
    /// </summary>
    public static PreparedReplacement PrepareRangeOverwrite(Document document, IEnumerable<ByteRange> ranges, Func<ByteRange, EditContent> contentFor,
        Operations.LongRunningOperation? operation = null, bool ignoreEditLock = false, CancellationToken cancellationToken = default) =>
        document.PrepareContentEdits(ranges.Select(r => new ContentEdit(r.Start, r.Length, contentFor(r))), operation, ignoreEditLock, cancellationToken);

    /// <summary>マルチ選択・矩形のすべての要素を 00 にする (上書きモードの Delete、「00 で塗りつぶす」)。長さは変えない。</summary>
    private EditResult FillSelectedRangesWithZero(string description)
    {
        SelectionSnapshot selection = CaptureSelection();
        if (selection.IsEmpty)
        {
            return EditResult.Ignored;
        }

        PreparedReplacement prepared = PrepareRangeOverwrite(Document, selection.Ranges, r => EditContent.Fill(0, r.Length));
        Document.CommitReplacements(prepared, description);
        RaiseChanged();
        return EditResult.Done;
    }

    // ---- 矩形の挿入・貼り付け (EDIT-17 の仕様 3・4) ----

    /// <summary>
    /// 矩形挿入 (EDIT-17 の仕様 4): 矩形の各行の左端の列に <paramref name="contentFor"/> の内容を挿入した木を作る。
    /// </summary>
    public PreparedReplacement PrepareRectangleInsert(Func<long, EditContent> contentFor, Operations.LongRunningOperation? operation = null,
        bool ignoreEditLock = false, CancellationToken cancellationToken = default)
    {
        if (_rect is not { } r)
        {
            throw new InvalidOperationException("矩形選択がありません。");
        }

        long length = Layout.Length;
        IEnumerable<ContentEdit> Edits()
        {
            foreach (ByteRange row in r.Ranges(length))
            {
                yield return new ContentEdit(row.Start, 0, contentFor(row.Start));
            }
        }

        return Document.PrepareContentEdits(Edits(), operation, ignoreEditLock, cancellationToken);
    }

    /// <summary>
    /// 矩形の貼り付け (EDIT-17 の仕様 3): クリップボードの矩形の各行を、主カーソルの列を左端として 1 行ずつ下の行に貼る。
    /// <paramref name="overwrite"/> (上書き貼り付け、または上書きモード) なら各行の該当バイトを上書きし、そうでなければ各行の位置に挿入する。
    /// 行はクリップボードの行の内容 (<paramref name="rowSource"/> の行 k のバイト列) を参照で渡す。
    /// </summary>
    public EditResult PasteRectangle(IReadOnlyList<(SnapshotRange Range, long Offset, long Length)> rows, bool overwrite)
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (rows.Count == 0)
        {
            return EditResult.Ignored;
        }

        bool insert = !overwrite && InsertMode && Document.CanResize;
        if (insert && rows.Count > MaxRectangleRows)
        {
            return EditResult.TooManyRows;
        }

        CollapseCarets();
        long start = HasSelection && _rect is { } rect ? rect.RowLeft(rect.FirstRow) : _cursor;
        ClearSelectionAnchor();
        long length = Document.Length;
        EditResult result = EditResult.Done;
        using (Document.BeginGroup(insert ? "貼り付け" : "上書き貼り付け"))
        {
            // 下の行から順に貼る (挿入で上の行の位置がずれないように)。
            for (int k = rows.Count - 1; k >= 0; k--)
            {
                (SnapshotRange range, long offset, long n) = rows[k];
                long at = start + (long)k * BytesPerRow;
                if (at > length)
                {
                    continue;
                }

                if (!insert)
                {
                    long available = Document.CanResize ? n : Math.Max(0, Math.Min(n, Document.Length - at));
                    if (available < n)
                    {
                        result = EditResult.Truncated;
                    }

                    if (available > 0)
                    {
                        Document.OverwriteFrom(at, range, offset, available);
                    }
                }
                else
                {
                    Document.InsertFrom(at, range, offset, n);
                }
            }
        }

        _cursor = Math.Min(start, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return result;
    }
}
