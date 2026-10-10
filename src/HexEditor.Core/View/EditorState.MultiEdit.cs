using HexEditor.Core.Engine;
using HexEditor.Core.Selection;

namespace HexEditor.Core.View;

/// <summary>選択範囲のドロップの種類 (EDIT-18 の仕様 2)。</summary>
public enum SelectionDropKind
{
    /// <summary>移動 (既定)。</summary>
    Move,

    /// <summary>コピー (Ctrl を押しながらドロップ)。</summary>
    Copy,

    /// <summary>ドロップ位置から上書き (上書きモードで Shift を押しながらドロップ)。</summary>
    Overwrite,
}

/// <summary>要素数の多いマルチ選択・矩形への Delete / Backspace を長時間処理で行うときの操作 (EDIT-07 の「巨大ファイル・長時間処理」)。</summary>
public enum RangeDeleteAction
{
    /// <summary>長時間処理にしない (その場で行う)。</summary>
    None,

    /// <summary>すべての要素を削除する。</summary>
    Delete,

    /// <summary>すべての要素を 00 で塗りつぶす (上書きモードで長さを変えない Delete。EDIT-13 の仕様 5)。</summary>
    ZeroFill,
}

/// <summary>
/// マルチ選択・矩形選択・マルチカーソルへの編集 (EDIT-07 の仕様 7・8、EDIT-08 の仕様 4〜7、EDIT-17)。
/// </summary>
public sealed partial class EditorState
{
    // マルチカーソルの各カーソルに同じ操作をしている間 (ForEachCaret)。
    private bool _caretLoop;
    private Caret[]? _loopCarets;
    private int _loopIndex;

    // マルチカーソルの処理の間の長さの変化の合計と、各カーソルを処理し終えたときの値。処理はオフセットの大きいカーソルから行うので、
    // 後の編集で動くのは処理済みのカーソルだけで、動く量はその後の長さの変化の合計になる (カーソル 10,000 個でも編集ごとに全部を動かさない)。
    private long _loopDelta;
    private long[]? _loopStoredDelta;

    /// <summary>
    /// 直前のマルチカーソルへの入力で、入力できなかったカーソルの数 (EDIT-08 の「エラー」: 「N 個のカーソルで入力できませんでした」)。
    /// </summary>
    public int LastCaretFailures { get; private set; }

    /// <summary><see cref="LastCaretFailures"/> を読んで 0 に戻す (InfoBar を 1 回だけ出すため)。</summary>
    public int TakeCaretFailures()
    {
        int failures = LastCaretFailures;
        LastCaretFailures = 0;
        return failures;
    }

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
        _loopDelta = 0;
        _loopStoredDelta = new long[_loopCarets.Length];
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
                _loopStoredDelta[i] = _loopDelta;
            }
        }
        finally
        {
            _caretLoop = false;
            group?.Dispose();
        }

        // 処理した後の編集で後ろへずれた分を足す (EDIT-08 の仕様 5)。
        for (int i = 0; i < _loopCarets.Length; i++)
        {
            long shift = _loopDelta - _loopStoredDelta[i];
            if (shift != 0)
            {
                Caret c = _loopCarets[i];
                _loopCarets[i] = c with
                {
                    Offset = Math.Clamp(c.Offset + shift, 0, Layout.MaxCursor),
                    Anchor = c.Anchor < 0 ? -1 : Math.Clamp(c.Anchor + shift, 0, Layout.Length),
                };
            }
        }

        _loopStoredDelta = null;

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

    /// <summary>
    /// 選択のないマルチカーソルへの Hex 列の 1 桁の入力を、新しい木を一度に作って 1 回で入れる (カーソル 10,000 個で 50 ms 以内。
    /// EDIT-08 の「巨大ファイル・長時間処理」)。どれかのカーソルが選択を持つ場合は null (カーソルごとの処理にする)。
    /// </summary>
    private EditResult? TypeHexDigitAtCarets(int digit)
    {
        if (_carets is null || _selectionLength > 0 || _carets.Any(c => c.Selection.Length > 0))
        {
            return null;
        }

        var all = new List<(Caret Caret, int Order)>(CaretCount);
        for (int i = 0; i < _carets.Count; i++)
        {
            all.Add((_carets[i], i));
        }

        all.Add((PrimaryCaret, int.MaxValue));
        all.Sort((a, b) => a.Caret.Offset.CompareTo(b.Caret.Offset));
        DocumentSnapshot snapshot = Document.Current;
        long length = snapshot.Length;
        var edits = new List<ContentEdit>(all.Count);
        var results = new Caret[all.Count];
        int failures = 0;
        long shift = 0, previous = -1;
        byte[] currents = ReadBytesUnderCarets(snapshot, all, InsertMode);
        for (int i = 0; i < all.Count; i++)
        {
            Caret c = all[i].Caret;
            bool atEnd = c.Offset >= length;
            if (c.Offset == previous || (atEnd && !Document.CanResize))
            {
                // 同じ位置のカーソルはまとめる (仕様 7)。長さを変えられないドキュメントの末尾位置には入力できない (「エラー」)。
                failures += c.Offset == previous ? 0 : 1;
                results[i] = c with { Offset = c.Offset + shift };
                continue;
            }

            previous = c.Offset;
            byte current = currents[i];

            if (!c.LowNibble && (InsertMode || atEnd))
            {
                edits.Add(new ContentEdit(c.Offset, 0, EditContent.Bytes([(byte)(digit << 4)])));
                results[i] = new Caret(c.Offset + shift, -1, LowNibble: true);
                shift++;
            }
            else if (!c.LowNibble)
            {
                edits.Add(new ContentEdit(c.Offset, 1, EditContent.Bytes([(byte)((current & 0x0F) | (digit << 4))])));
                results[i] = new Caret(c.Offset + shift, -1, LowNibble: true);
            }
            else
            {
                edits.Add(new ContentEdit(c.Offset, 1, EditContent.Bytes([(byte)((current & 0xF0) | digit)])));
                results[i] = new Caret(c.Offset + shift + 1, -1, LowNibble: false);
            }
        }

        if (edits.Count > 0)
        {
            PreparedReplacement prepared = Document.PrepareContentEdits(edits);
            using (Document.BeginCoalescingGroup("入力", TypingKey))
            {
                _caretLoop = true;
                try
                {
                    Document.CommitReplacements(prepared, "入力");
                }
                finally
                {
                    _caretLoop = false;
                }
            }
        }

        // 主カーソルは最後に追加したもの。重なったカーソルはまとめる。
        var merged = new Dictionary<long, (Caret Caret, int Order)>();
        for (int i = 0; i < all.Count; i++)
        {
            Caret c = results[i] with { Offset = Math.Clamp(results[i].Offset, 0, Layout.MaxCursor) };
            if (!merged.TryGetValue(c.Offset, out var existing) || existing.Order < all[i].Order)
            {
                merged[c.Offset] = (c, all[i].Order);
            }
        }

        var ordered = merged.Values.OrderBy(v => v.Order).ToList();
        LoadCaret(ordered[^1].Caret);
        _carets = ordered.Count > 1 ? ordered.Take(ordered.Count - 1).Select(v => v.Caret).ToList() : null;
        LastCaretFailures = failures;
        EnsureCursorVisible();
        RaiseChanged();
        return edits.Count > 0 ? EditResult.Done : EditResult.FixedLength;
    }

    /// <summary>
    /// 各カーソルの位置のバイトを読む (上位ニブルへの挿入・末尾では読まない)。カーソルは位置の昇順に並んでいること。
    /// 元データの読み込みは 1 回ごとにファイルの読み込みになる (同じファイルへの読み込みは並列にしても OS の中で順番になる)。
    /// カーソルが密にある (キャッシュの 1 ブロックに平均 4 個以上) 場合は、ブロックごと表示用のキャッシュに入れて読み、読み込みの回数を
    /// 減らす (次の入力ではキャッシュから読める。カーソル 10,000 個で 50 ms 以内。EDIT-08 の「巨大ファイル・長時間処理」)。
    /// まばらな場合は 1 バイトずつ直接読む (キャッシュを追い出さない)。
    /// </summary>
    private static byte[] ReadBytesUnderCarets(DocumentSnapshot snapshot, List<(Caret Caret, int Order)> carets, bool insertMode)
    {
        var bytes = new byte[carets.Count];
        long length = snapshot.Length;
        int blockSize = snapshot.CacheBlockSize;
        int needed = 0, blocks = 0;
        long lastBlock = -1;
        foreach ((Caret c, _) in carets)
        {
            if (c.Offset < length && (c.LowNibble || !insertMode))
            {
                needed++;
                if (c.Offset / blockSize != lastBlock)
                {
                    lastBlock = c.Offset / blockSize;
                    blocks++;
                }
            }
        }

        bool throughCache = needed >= 64 && needed >= blocks * 4L;
        Span<byte> one = stackalloc byte[1];
        for (int i = 0; i < carets.Count; i++)
        {
            Caret c = carets[i].Caret;
            if (c.Offset < length && (c.LowNibble || !insertMode))
            {
                _ = throughCache ? snapshot.ReadThroughCache(c.Offset, one) : snapshot.Read(c.Offset, one);
                bytes[i] = one[0];
            }
        }

        return bytes;
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

    /// <summary>
    /// 要素数がこれを超える要素ごとの操作 (削除、反転、塗りつぶし、矩形の長さが変わる操作) は長時間処理として行う
    /// (EDIT-07・EDIT-17 の「巨大ファイル・長時間処理」)。
    /// </summary>
    public const int LongRunningElements = 10_000;

    /// <summary>
    /// Delete / Backspace をマルチ選択・矩形に対して押したとき、要素数が <see cref="LongRunningElements"/> を超えるなら長時間処理で行う操作を返す
    /// (UI はキー操作をその場で行わずに長時間処理に回す)。その場で行う (要素数が少ない、編集できないなど) なら <see cref="RangeDeleteAction.None"/>。
    /// </summary>
    public RangeDeleteAction LongRangeDeleteAction(bool backspace)
    {
        if (_caretLoop || !HasMultipleRanges || !CanEdit() || SelectedRangeCount <= LongRunningElements)
        {
            return RangeDeleteAction.None;
        }

        // 上書きモードの Delete で「長さを変えない」設定なら 00 で塗りつぶす (EDIT-13 の仕様 5)。
        if (!backspace && !InsertMode && Options.DeleteKeepsLengthInOverwrite)
        {
            return RangeDeleteAction.ZeroFill;
        }

        // 固定長・矩形の行数の上限超過は、その場の処理がすぐ知らせる。
        return Document.CanResize && CheckRectangleRows() is null ? RangeDeleteAction.Delete : RangeDeleteAction.None;
    }

    /// <summary>
    /// 要素 (範囲) の一覧を作ってから行う操作 (データ演算、文字コード変換、大文字・小文字の変換) の前に、矩形の行数が上限を超えていないかを
    /// 確かめる。超えていればその上限を返す (一覧を作らずに実行しない)。上限はマルチ選択の要素数の上限 (EDIT-07 の仕様 3) で、
    /// 長さが変わりうる操作では矩形の行数の上限 (EDIT-17 の仕様 6) も加える。マルチ選択は要素数が上限以内なので null。
    /// </summary>
    public long? RectangleListLimitExceeded(bool changesLength)
    {
        if (_rect is null)
        {
            return null;
        }

        long limit = changesLength ? Math.Min(MaxRectangleRows, MaxSelectionElements) : MaxSelectionElements;
        return SelectedRangeCount > limit ? limit : null;
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

    // ---- 選択範囲のドラッグ & ドロップ (EDIT-18) ----

    /// <summary>
    /// 選択範囲を <paramref name="target"/> (そのバイトの前) にドロップできるか (EDIT-18 の仕様 3・7、「エラー」)。単一の選択だけを
    /// ドラッグできる。選択範囲の内側にはドロップできない。
    /// </summary>
    public bool CanDropSelectionAt(long target, SelectionDropKind kind)
    {
        if (!CanEdit() || SelectionKind != SelectionKind.Single || target < 0 || target > Document.Length)
        {
            return false;
        }

        if (target > _selectionStart && target < _selectionStart + _selectionLength)
        {
            return false;
        }

        // 長さを変えられないドキュメントでは、Shift による上書きだけを受け付ける (仕様 7)。
        return kind == SelectionDropKind.Overwrite ? target < Document.Length : Document.CanResize;
    }

    /// <summary>
    /// 選択範囲をドロップする (EDIT-18 の仕様 2・3)。移動は「削除と挿入」を 1 つの編集グループにする。データはピースの参照で動かすので、
    /// 量に関係なく一定時間で終わる。ドロップした後は、動かした (書いた) 範囲を選択する。
    /// </summary>
    public EditResult DropSelection(long target, SelectionDropKind kind)
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!CanDropSelectionAt(target, kind))
        {
            return kind != SelectionDropKind.Overwrite && !Document.CanResize ? EditResult.FixedLength : EditResult.Ignored;
        }

        long start = _selectionStart, length = _selectionLength;
        long placed;
        switch (kind)
        {
            case SelectionDropKind.Copy:
                Document.InsertCopy(target, start, length, "コピー");
                placed = target;
                break;
            case SelectionDropKind.Overwrite:
                length = Document.CanResize ? length : Math.Min(length, Document.Length - target);
                Document.OverwriteFrom(target, Document.Current, start, length, "上書き");
                placed = target;
                break;
            default:
                if (target == start || target == start + length)
                {
                    return EditResult.Ignored;
                }

                using (Document.BeginGroup("移動"))
                {
                    Document.InsertCopy(target, start, length, "移動");
                    if (target < start)
                    {
                        Document.Delete(start + length, length, "移動");
                        placed = target;
                    }
                    else
                    {
                        Document.Delete(start, length, "移動");
                        placed = target - length;
                    }
                }

                break;
        }

        Select(placed, length);
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
    /// 矩形の貼り付け (EDIT-17 の仕様 3) が各行の位置に挿入するか (通常の貼り付けで挿入モード、長さを変えられるドキュメント)。
    /// 挿入は長さが変わる操作なので行数の上限があり、<see cref="LongRunningElements"/> 行を超える場合は長時間処理にする。
    /// </summary>
    public bool RectanglePasteInserts(bool overwrite) => !overwrite && InsertMode && Document.CanResize;

    /// <summary>矩形の貼り付けの左上 (矩形選択の左上、選択範囲の先頭、またはカーソル)。</summary>
    public long RectanglePasteOrigin => _rect is { } rect ? rect.RowLeft(rect.FirstRow) : _selectionLength > 0 ? _selectionStart : _cursor;

    /// <summary>
    /// 矩形の各行を <paramref name="origin"/> から 1 行ずつ下の行の位置に挿入した木を作る (ドキュメントはまだ変えない)。行数ぶんの
    /// ピース操作を 1 回の木の作り直しで行う (EDIT-17 の「巨大ファイル・長時間処理」)。行数が多い場合は長時間処理の中で呼ぶ。
    /// </summary>
    public static PreparedReplacement PrepareRectangleRowsInsert(Document document, IReadOnlyList<byte[]> rows, long origin, int bytesPerRow,
        Operations.LongRunningOperation? operation = null, bool ignoreEditLock = false, CancellationToken cancellationToken = default)
    {
        long length = document.Length;
        IEnumerable<ContentEdit> Edits()
        {
            // 挿入位置は元のドキュメントのオフセット (上の行の挿入で下の行の位置はずれない)。
            for (int k = 0; k < rows.Count; k++)
            {
                long at = origin + (long)k * bytesPerRow;
                if (at > length)
                {
                    yield break;
                }

                if (rows[k].Length > 0)
                {
                    yield return new ContentEdit(at, 0, EditContent.Bytes(rows[k]));
                }
            }
        }

        return document.PrepareContentEdits(Edits(), operation, ignoreEditLock, cancellationToken);
    }

    /// <summary><see cref="PrepareRectangleRowsInsert"/> で作った木を 1 回の編集として入れ、カーソルを左上に置く。</summary>
    public void CommitRectangleRowsInsert(PreparedReplacement prepared, long origin)
    {
        CollapseCarets();
        ClearSelectionAnchor();
        Document.CommitReplacements(prepared, "貼り付け");
        _cursor = Math.Min(origin, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
    }

    /// <summary>
    /// 矩形の貼り付け (EDIT-17 の仕様 3): クリップボードの矩形の各行を、主カーソルの列を左端として 1 行ずつ下の行に貼る。
    /// <paramref name="overwrite"/> (上書き貼り付け、または上書きモード) なら各行の該当バイトを上書きし、そうでなければ各行の位置に挿入する
    /// (後ろの行の配置はずれる)。全体を 1 つの編集グループにする。
    /// </summary>
    public EditResult PasteRectangle(IReadOnlyList<byte[]> rows, bool overwrite)
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (rows.Count == 0)
        {
            return EditResult.Ignored;
        }

        bool insert = RectanglePasteInserts(overwrite);
        if (insert && rows.Count > MaxRectangleRows)
        {
            return EditResult.TooManyRows;
        }

        long start = RectanglePasteOrigin;
        if (insert)
        {
            CommitRectangleRowsInsert(PrepareRectangleRowsInsert(Document, rows, start, BytesPerRow), start);
            return EditResult.Done;
        }

        CollapseCarets();
        ClearSelectionAnchor();
        EditResult result = EditResult.Done;
        using (Document.BeginGroup("上書き貼り付け"))
        {
            for (int k = rows.Count - 1; k >= 0; k--)
            {
                byte[] row = rows[k];
                long at = start + (long)k * BytesPerRow;
                if (at > Document.Length || row.Length == 0)
                {
                    continue;
                }

                long available = Document.CanResize ? row.Length : Math.Max(0, Math.Min(row.Length, Document.Length - at));
                if (available < row.Length)
                {
                    result = EditResult.Truncated;
                }

                if (available > 0)
                {
                    Document.Overwrite(at, row.AsSpan(0, (int)available), "上書き貼り付け");
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
