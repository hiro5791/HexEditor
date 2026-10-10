using HexEditor.Core.Engine;
using HexEditor.Core.Selection;

namespace HexEditor.Core.View;

/// <summary>選択の形 (EDIT-01〜EDIT-08)。</summary>
public enum SelectionKind
{
    /// <summary>選択なし (カーソルだけ)。</summary>
    None,

    /// <summary>連続した 1 つの範囲。</summary>
    Single,

    /// <summary>マルチ選択 (EDIT-07)。要素が 2 つ以上。</summary>
    Multiple,

    /// <summary>矩形選択 (EDIT-06)。</summary>
    Rectangle,
}

/// <summary>選択の操作の結果。UI はこれを見て InfoBar を出す。</summary>
public enum SelectionResult
{
    Done,

    /// <summary>対象の選択範囲がない。</summary>
    NoSelection,

    /// <summary>先頭より前、または末尾を越える (EDIT-05 の仕様 3「これ以上ずらせません」)。</summary>
    OutOfRange,

    /// <summary>マルチ選択の要素数の上限を超える (EDIT-07 の仕様 3)。何も変えていない。</summary>
    TooManyElements,

    /// <summary>マルチ選択の要素数の上限を超えたため、先頭から上限までだけを選んだ (FIND-21 の仕様 2)。</summary>
    Truncated,

    /// <summary>カーソル数の上限 (10,000) を超える (EDIT-08 の仕様 3)。何も変えていない。</summary>
    TooManyCarets,
}

/// <summary>
/// マルチカーソル (EDIT-08) のカーソル 1 つ。<see cref="Anchor"/> が 0 以上なら、アンカーとカーソルの間 (カーソルの位置は含まない) を
/// 選択している (各カーソルが自分の選択範囲を持つ。仕様 4)。
/// </summary>
public readonly record struct Caret(long Offset, long Anchor = -1, bool LowNibble = false)
{
    /// <summary>このカーソルの選択範囲 (なければ長さ 0)。</summary>
    public ByteRange Selection => Anchor < 0 || Anchor == Offset ? new ByteRange(Offset, 0) : ByteRange.FromBounds(Math.Min(Anchor, Offset), Math.Max(Anchor, Offset));
}

/// <summary>
/// 選択の通知 (操作の結果ではなく、表示設定の変更などで選択が変わったとき。EDIT-06 の仕様 5)。UI は InfoBar で知らせる。
/// </summary>
public sealed class SelectionNoticeEventArgs(SelectionResult result, long count, long limit) : EventArgs
{
    public SelectionResult Result { get; } = result;

    /// <summary>要素・行の数。</summary>
    public long Count { get; } = count;

    /// <summary>上限。</summary>
    public long Limit { get; } = limit;
}

/// <summary>
/// ある時点の選択の写し (EDIT-07)。検索・ハッシュ・データ演算など、バックグラウンドで選択範囲を読む処理に渡す。作った後に選択が
/// 変わっても変わらない。矩形選択は 4 つの数値のまま持ち、要素は列挙するときに作る (行数に比例するメモリを使わない)。
/// </summary>
public sealed class SelectionSnapshot
{
    private readonly RangeSet? _ranges;

    internal SelectionSnapshot(SelectionKind kind, RangeSet? ranges, RectSelection? rectangle, ByteRange? primary, long documentLength)
    {
        Kind = kind;
        _ranges = ranges;
        Rectangle = rectangle;
        Primary = primary;
        DocumentLength = documentLength;
    }

    public SelectionKind Kind { get; }

    /// <summary>矩形選択のとき、その矩形。</summary>
    public RectSelection? Rectangle { get; }

    /// <summary>主要素 (最後に追加した要素。EDIT-07 の仕様 5)。選択がなければ null。</summary>
    public ByteRange? Primary { get; }

    /// <summary>写しを作ったときのドキュメントの長さ。</summary>
    public long DocumentLength { get; }

    /// <summary>空でない要素の数。</summary>
    public long Count => Rectangle is { } r ? RectangleRowCount(r, DocumentLength) : _ranges?.Count ?? 0;

    /// <summary>要素の合計バイト数。</summary>
    public long TotalLength => Rectangle is { } r ? r.ByteCount(DocumentLength) : _ranges?.TotalLength ?? 0;

    public bool IsEmpty => Count == 0;

    /// <summary>要素 (オフセット順。矩形は上の行から)。</summary>
    public IEnumerable<ByteRange> Ranges => Rectangle is { } r ? r.Ranges(DocumentLength) : (IEnumerable<ByteRange>?)_ranges ?? [];

    /// <summary>要素 (後ろから順。長さが変わる編集はオフセットの大きい要素から行う。EDIT-07 の仕様 8)。</summary>
    public IEnumerable<ByteRange> RangesReversed => Rectangle is not null ? Ranges.Reverse() : _ranges?.Reversed() ?? [];

    /// <summary>すべての要素を含む最小の範囲。</summary>
    public ByteRange Bounds => Rectangle is not null
        ? Ranges.Aggregate(default(ByteRange?), (a, b) => a is { } x ? ByteRange.FromBounds(x.Start, b.End) : b) ?? default
        : _ranges?.Bounds ?? default;

    internal static long RectangleRowCount(RectSelection r, long documentLength)
    {
        if (documentLength <= 0)
        {
            return 0;
        }

        long lastDataRow = (documentLength - 1 + r.RowShift) / r.BytesPerRow;
        long last = Math.Min(r.LastRow, lastDataRow);
        long count = Math.Max(0, last - r.FirstRow + 1);
        if (count > 0 && r.RowRange(r.FirstRow, documentLength).Length == 0)
        {
            count--;
        }

        if (count > 0 && last != r.FirstRow && r.RowRange(last, documentLength).Length == 0)
        {
            count--;
        }

        return count;
    }
}

/// <summary>
/// マルチ選択 (EDIT-07)・矩形選択 (EDIT-06)・マルチカーソル (EDIT-08) と、選択範囲をずらす・広げる (EDIT-05)。
/// </summary>
/// <remarks>
/// 主要素・主カーソルは、これまでの単一の選択と同じフィールド (<see cref="Cursor"/>、<see cref="SelectionStart"/>、
/// <see cref="SelectionLength"/>) で持つ。そのため、単一の選択だけを扱う処理はそのまま主要素を対象にできる。
/// 主要素以外の要素は <see cref="_others"/>、主カーソル以外のカーソルは <see cref="_carets"/> に持つ。
/// </remarks>
public sealed partial class EditorState
{
    /// <summary>マルチ選択の要素数の上限の既定値 (EDIT-07 の仕様 3)。</summary>
    public const int DefaultMaxSelectionElements = 1_000_000;

    /// <summary>カーソル数の上限 (EDIT-08 の仕様 3)。</summary>
    public const int MaxCarets = 10_000;

    /// <summary>長さが変わる矩形の操作の行数の上限の既定値 (EDIT-17 の仕様 6)。</summary>
    public const int DefaultMaxRectangleRows = 1_000_000;

    // 主要素以外の要素 (マルチ選択)。主要素と重なっていてもよい (ドラッグ中)。確定 (CommitSelection) で結合する。
    private RangeSet? _others;

    // 主要素を追加し始める前の主要素 (Ctrl+クリックで何も足さなかったとき戻す)。
    private ByteRange? _previousPrimary;

    // 矩形選択と、その対角の一方 (アンカー) の位置。
    private RectSelection? _rect;
    private long _rectAnchor;

    // 主カーソル以外のカーソル (追加した順。主カーソルは最後に追加したもの)。
    private List<Caret>? _carets;

    /// <summary>マルチ選択の要素数の上限 (設定 <c>edit.multiSelection.maxElements</c>。1,000〜10,000,000)。</summary>
    public int MaxSelectionElements { get; set; } = DefaultMaxSelectionElements;

    /// <summary>長さが変わる矩形の操作の行数の上限 (設定 <c>edit.rectangle.maxRows</c>。1〜10,000,000)。</summary>
    public int MaxRectangleRows { get; set; } = DefaultMaxRectangleRows;

    /// <summary>選択が操作ではなく変わった (矩形からマルチ選択への変換など。EDIT-06 の仕様 5)。</summary>
    public event EventHandler<SelectionNoticeEventArgs>? SelectionNotice;

    /// <summary>選択の形。</summary>
    public SelectionKind SelectionKind => _rect is not null ? SelectionKind.Rectangle
        : _others is { Count: > 0 } && (SelectedRangeCountMulti() > 1) ? SelectionKind.Multiple
        : HasSelection ? SelectionKind.Single : SelectionKind.None;

    /// <summary>矩形選択のとき、その矩形。</summary>
    public RectSelection? Rectangle => _rect;

    /// <summary>主要素 (最後に追加した要素。マルチ選択ではドラッグ中の範囲と結合した後の範囲)。選択がなければ null。</summary>
    public ByteRange? PrimaryRange
    {
        get
        {
            if (_others is { Count: > 0 } && _rect is null)
            {
                return _selectionLength > 0 ? MergedPrimary().Merged : _previousPrimary is { } p && _others.Find(p.Start) is { } q ? q : _others.Last;
            }

            return _selectionLength > 0 ? new ByteRange(_selectionStart, _selectionLength) : null;
        }
    }

    /// <summary>空でない要素の数 (単一の選択なら 1、選択なしなら 0)。</summary>
    public long SelectedRangeCount => _rect is { } r ? SelectionSnapshot.RectangleRowCount(r, Layout.Length)
        : _others is { Count: > 0 } ? SelectedRangeCountMulti()
        : _selectionLength > 0 ? 1 : 0;

    /// <summary>選択しているバイト数の合計。</summary>
    public long SelectedByteCount
    {
        get
        {
            if (_rect is { } r)
            {
                return r.ByteCount(Layout.Length);
            }

            if (_others is { Count: > 0 } others)
            {
                if (_selectionLength == 0)
                {
                    return others.TotalLength;
                }

                (ByteRange merged, int _, long touchedTotal) = MergedPrimary();
                return others.TotalLength - touchedTotal + merged.Length;
            }

            return _selectionLength;
        }
    }

    /// <summary>選択の要素 (オフセット順。矩形は上の行から)。描画には <see cref="SelectedRangesIn"/> を使う。</summary>
    public IEnumerable<ByteRange> SelectedRanges
    {
        get
        {
            if (_rect is { } r)
            {
                return r.Ranges(Layout.Length);
            }

            if (_others is { Count: > 0 } others)
            {
                return MergeWithPrimary(others, 0, long.MaxValue);
            }

            return _selectionLength > 0 ? [new ByteRange(_selectionStart, _selectionLength)] : [];
        }
    }

    /// <summary>[start, start + length) と重なる選択の要素 (見えている行の描画・UI オートメーション用)。</summary>
    public IEnumerable<ByteRange> SelectedRangesIn(long start, long length)
    {
        if (_rect is { } r)
        {
            return r.RangesIn(start, length, Layout.Length);
        }

        if (_others is { Count: > 0 } others)
        {
            return MergeWithPrimary(others, start, length);
        }

        return _selectionLength > 0 && _selectionStart < start + length && start < _selectionStart + _selectionLength
            ? [new ByteRange(_selectionStart, _selectionLength)]
            : [];
    }

    /// <summary>オフセットのバイトが選択されているか。</summary>
    public bool IsSelected(long offset) =>
        _rect is { } r ? r.Contains(offset, Layout.Length)
        : offset >= _selectionStart && offset < _selectionStart + _selectionLength || _others?.Contains(offset) == true;

    /// <summary>今の選択の写し (バックグラウンドの処理に渡す)。</summary>
    public SelectionSnapshot CaptureSelection()
    {
        if (_rect is { } r)
        {
            return new SelectionSnapshot(SelectionKind.Rectangle, null, r, PrimaryRange, Layout.Length);
        }

        var set = new RangeSet(SelectedRanges);
        SelectionKind kind = set.Count > 1 ? SelectionKind.Multiple : set.Count == 1 ? SelectionKind.Single : SelectionKind.None;
        return new SelectionSnapshot(kind, set, null, PrimaryRange, Layout.Length);
    }

    // ---- マルチ選択 (EDIT-07) ----

    /// <summary>
    /// 範囲をマルチ選択に加え、主要素にする (範囲を選択の「現在の選択に追加」、選択範囲の読み込みの「追加」)。重なる・隣り合う要素とは結合する。
    /// 上限を超える場合は何もしない。
    /// </summary>
    public SelectionResult AddSelection(long start, long length)
    {
        CollapseCarets();
        start = Math.Clamp(start, 0, Layout.Length);
        length = Math.Clamp(length, 0, Layout.Length - start);
        if (length == 0)
        {
            return SelectionResult.NoSelection;
        }

        CommitSelectionCore();
        RangeSet others = TakeIntoOthers();
        int touching = others.Touching(start, length).Take(2).Count();
        if (touching == 0 && others.Count + 1 > MaxSelectionElements)
        {
            RestorePrimaryFromOthers();
            return SelectionResult.TooManyElements;
        }

        ByteRange merged = others.Add(new ByteRange(start, length));
        SetPrimaryElement(merged);
        EnsureCursorVisible();
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>
    /// 選択を、要素の一覧で置き換える (検索結果の「選択範囲に変換」FIND-21、選択範囲の読み込み EDIT-09)。要素は結合して並べ直す。
    /// 上限を超える場合は先頭から上限までだけを選び <see cref="SelectionResult.Truncated"/> を返す。主要素は最後の要素。
    /// </summary>
    public SelectionResult SetSelections(IEnumerable<ByteRange> ranges, bool add = false)
    {
        CollapseCarets();
        long length = Layout.Length;
        RangeSet set;
        if (add)
        {
            CommitSelectionCore();
            set = TakeIntoOthers();
        }
        else
        {
            set = new RangeSet();
        }

        bool truncated = false;
        foreach (ByteRange r in ranges)
        {
            long s = Math.Clamp(r.Start, 0, length);
            long n = Math.Clamp(r.Length, 0, length - s);
            if (n <= 0)
            {
                continue;
            }

            if (set.Count >= MaxSelectionElements && !set.Touching(s, n).Any())
            {
                truncated = true;
                break;
            }

            set.Add(new ByteRange(s, n));
        }

        if (set.Count == 0)
        {
            ClearSelectionAnchor();
            RaiseChanged();
            return SelectionResult.NoSelection;
        }

        ByteRange primary = set.Last;
        set.Remove(primary);
        _rect = null;
        _others = set.Count > 0 ? set : null;
        _previousPrimary = null;
        SetPrimaryElement(primary, keepOthers: true);
        EnsureCursorVisible();
        RaiseChanged();
        return truncated ? SelectionResult.Truncated : SelectionResult.Done;
    }

    /// <summary>
    /// Ctrl+ドラッグ・Ctrl+Shift+クリックの始まり (EDIT-07 の仕様 2): 今の選択を残したまま、<paramref name="offset"/> から新しい要素を作り始める。
    /// 新しい要素は <see cref="DragTo"/> で伸ばし、<see cref="CommitSelection"/> で確定する。<paramref name="fromCursor"/> なら
    /// 今のカーソル位置を新しい要素のアンカーにする (Ctrl+Shift+クリック)。
    /// </summary>
    public void BeginAddSelection(long offset, ActiveColumn column, bool fromCursor = false)
    {
        CollapseCarets();
        _rect = null;
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        long anchor = fromCursor ? _cursor : offset;
        CommitSelectionCore();
        _previousPrimary = PrimaryRange;
        TakeIntoOthers();
        ActiveColumn = VisibleColumn(column);
        _anchor = Math.Clamp(anchor, 0, Layout.MaxCursor);
        _cursor = offset;
        _selectionStart = offset;
        _selectionLength = 0;
        LowNibble = false;
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>
    /// Ctrl+クリック (ドラッグしなかった): 選択されている要素の中なら、その要素を取り除く (EDIT-07 の仕様 2)。取り除いたら true。
    /// </summary>
    public bool RemoveSelectionAt(long offset)
    {
        if (_others is not { } others || others.Find(offset) is not { } element)
        {
            return false;
        }

        others.Remove(element);
        _selectionLength = 0;
        if (_previousPrimary == element)
        {
            _previousPrimary = null;
        }

        if (others.Count == 0)
        {
            _others = null;
            ClearSelectionAnchor();
        }
        else
        {
            RestorePrimaryFromOthers();
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 作っている要素を確定する (ドラッグの終わり)。重なる・隣り合う要素と結合し、上限を超える場合は加えない。
    /// </summary>
    public SelectionResult CommitSelection()
    {
        SelectionResult result = CommitSelectionCore();
        RaiseChanged();
        return result;
    }

    /// <summary>「選択を反転」(EDIT-07 の仕様 4): ドキュメント全体のうち選択されていない範囲を選ぶ。</summary>
    public SelectionResult InvertSelection()
    {
        CollapseCarets();
        long length = Layout.Length;
        var current = new RangeSet(SelectedRanges);
        RangeSet inverted = current.Invert(length);
        if (inverted.Count > MaxSelectionElements)
        {
            return SelectionResult.TooManyElements;
        }

        return SetSelections(inverted);
    }

    /// <summary>「次の要素へ」(EDIT-07 の仕様 6): 主要素を次の要素に切り替え、その位置へスクロールする。最後の要素からは最初へ戻る。</summary>
    public SelectionResult NextElement() => SwitchElement(next: true);

    /// <summary>「前の要素へ」。</summary>
    public SelectionResult PreviousElement() => SwitchElement(next: false);

    private SelectionResult SwitchElement(bool next)
    {
        if (_rect is not null)
        {
            SelectionResult converted = ConvertRectangleToMulti();
            if (converted != SelectionResult.Done)
            {
                return converted;
            }
        }

        CommitSelectionCore();
        if (_others is not { Count: > 0 } others || PrimaryRange is not { } primary)
        {
            return SelectionResult.NoSelection;
        }

        ByteRange target = (next ? others.NextAfter(primary.Start) ?? others.First : others.PreviousBefore(primary.Start) ?? others.Last);
        others.Remove(target);
        others.Add(primary);
        SetPrimaryElement(target, keepOthers: true);
        RecordJump();
        ScrollToJumpRange(target.Start, target.Length, JumpPlacement);
        RaiseChanged();
        return SelectionResult.Done;
    }

    // ---- 矩形選択 (EDIT-06) ----

    /// <summary>Alt+ドラッグの始まり: <paramref name="offset"/> を対角の一方にした 1 バイトの矩形にする。</summary>
    public void BeginRectangle(long offset, ActiveColumn column)
    {
        CollapseCarets();
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        ClearSelectionAnchor();
        ActiveColumn = VisibleColumn(column);
        _cursor = offset;
        _rectAnchor = offset;
        _rect = RectSelection.FromCorners(offset, offset, BytesPerRow, Layout.RowShift);
        SyncRectanglePrimary();
        LowNibble = false;
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>Alt+ドラッグ: アンカーと <paramref name="offset"/> を対角とする矩形にする (EDIT-06 の仕様 2)。</summary>
    public void RectangleTo(long offset)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        if (_rect is null)
        {
            _rectAnchor = _anchor >= 0 ? _anchor : _cursor;
        }

        RectangleToCore(offset);
    }

    private void RectangleToCore(long offset)
    {
        CollapseCarets();
        _others = null;
        _cursor = offset;
        _rect = RectSelection.FromCorners(_rectAnchor, offset, BytesPerRow, Layout.RowShift);
        SyncRectanglePrimary();
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
    }

    /// <summary>
    /// Alt+Shift+矢印 (EDIT-06 の仕様 2): アンカーを対角の一方にして、カーソルを <paramref name="rows"/> 行・<paramref name="columns"/> 列動かして
    /// 矩形を広げる。矩形でなければ今のカーソル位置をアンカーにして始める。
    /// </summary>
    public void ExtendRectangle(long rows, int columns)
    {
        if (_rect is null)
        {
            _rectAnchor = _cursor;
        }

        HexLayout layout = Layout;
        long row = Math.Clamp(layout.RowOf(_cursor) + rows, 0, layout.LastRow);
        int column = Math.Clamp(layout.ColumnOf(_cursor) + columns, 0, BytesPerRow - 1);
        long target = Math.Clamp(layout.RowStart(row) + column, 0, layout.MaxCursor);
        RectangleTo(target);
    }

    /// <summary>矩形を選ぶ (範囲を選択の「矩形として選択」。EDIT-04 の仕様 5)。カーソルは終わりの角に置く。</summary>
    public void SelectRectangle(long startCorner, long endCorner)
    {
        CollapseCarets();
        ClearSelectionAnchor();
        _rectAnchor = Math.Clamp(startCorner, 0, Layout.MaxCursor);
        RectangleToCore(Math.Clamp(endCorner, 0, Layout.MaxCursor));
    }

    /// <summary>
    /// 「マルチ選択に変換」(EDIT-06 の仕様 7): 矩形を行ごとに 1 要素のマルチ選択にする。要素数が上限を超える場合は何もしない。
    /// </summary>
    public SelectionResult ConvertRectangleToMulti()
    {
        if (_rect is not { } r)
        {
            return SelectionResult.NoSelection;
        }

        long rows = SelectionSnapshot.RectangleRowCount(r, Layout.Length);
        if (rows > MaxSelectionElements)
        {
            return SelectionResult.TooManyElements;
        }

        // 矩形が作られたときの 1 行のバイト数で数える (表示設定が変わった後でも同じバイトを指す)。
        long cursorRow = (_cursor + r.RowShift) / r.BytesPerRow;
        var set = new RangeSet(r.Ranges(Layout.Length));
        _rect = null;
        if (set.Count == 0)
        {
            ClearSelectionAnchor();
            RaiseChanged();
            return SelectionResult.NoSelection;
        }

        ByteRange primary = r.RowRange(cursorRow, Layout.Length) is { Length: > 0 } p ? p : set.Last;
        set.Remove(primary);
        _others = set.Count > 0 ? set : null;
        SetPrimaryElement(primary, keepOthers: true);
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>
    /// 1 行のバイト数・行の先頭のずれが変わったとき (EDIT-06 の仕様 5): 矩形を同じバイトを指すマルチ選択に変える。上限を超える場合は
    /// 選択を解除して <see cref="SelectionNotice"/> で知らせる。
    /// </summary>
    private void OnLayoutChangedForSelection()
    {
        if (_rect is not { } r || (r.BytesPerRow == BytesPerRow && r.RowShift == Layout.RowShift))
        {
            return;
        }

        // 変換は作ったときの 1 行のバイト数で行う (矩形が指すバイトは変わらない)。
        long rows = SelectionSnapshot.RectangleRowCount(r, Layout.Length);
        if (ConvertRectangleToMulti() == SelectionResult.TooManyElements)
        {
            _rect = null;
            ClearSelectionAnchor();
            SelectionNotice?.Invoke(this, new SelectionNoticeEventArgs(SelectionResult.TooManyElements, rows, MaxSelectionElements));
        }
    }

    // ---- マルチカーソル (EDIT-08) ----

    /// <summary>カーソルの数 (1 以上)。</summary>
    public int CaretCount => 1 + (_carets?.Count ?? 0);

    /// <summary>カーソルが 2 つ以上あるか。</summary>
    public bool HasMultipleCarets => _carets is { Count: > 0 };

    /// <summary>すべてのカーソル (オフセット順)。主カーソルは <see cref="PrimaryCaret"/>。</summary>
    public IReadOnlyList<Caret> Carets
    {
        get
        {
            var all = new List<Caret>(CaretCount) { PrimaryCaret };
            if (_carets is not null)
            {
                all.AddRange(_carets);
            }

            all.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return all;
        }
    }

    /// <summary>主カーソル (最後に追加したカーソル)。</summary>
    public Caret PrimaryCaret => new(_cursor, _selectionLength > 0 ? _anchor : -1, LowNibble);

    /// <summary>
    /// Alt+クリック (EDIT-08 の仕様 1): クリックした位置にカーソルを追加して主カーソルにする。既にカーソルがある位置ならそのカーソルを
    /// 取り除く (最後の 1 つは取り除かない)。
    /// </summary>
    public SelectionResult ToggleCaretAt(long offset, ActiveColumn column)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        _rect = null;
        _others = null;
        if (offset == _cursor && HasMultipleCarets)
        {
            // 主カーソルを取り除き、その前に追加したカーソルを主カーソルにする。
            Caret previous = _carets![^1];
            _carets.RemoveAt(_carets.Count - 1);
            LoadCaret(previous);
            RaiseChanged();
            return SelectionResult.Done;
        }

        int existing = _carets?.FindIndex(c => c.Offset == offset) ?? -1;
        if (existing >= 0)
        {
            _carets!.RemoveAt(existing);
            RaiseChanged();
            return SelectionResult.Done;
        }

        if (offset == _cursor)
        {
            return SelectionResult.Done;
        }

        ActiveColumn = VisibleColumn(column);
        return AddCaret(offset);
    }

    /// <summary>カーソルを追加して主カーソルにする。上限を超える場合は何もしない。</summary>
    public SelectionResult AddCaret(long offset)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        if (CaretCount + 1 > MaxCarets)
        {
            return SelectionResult.TooManyCarets;
        }

        _rect = null;
        _others = null;
        _carets ??= [];
        _carets.RemoveAll(c => c.Offset == offset);
        if (offset != _cursor)
        {
            _carets.Add(PrimaryCaret);
        }

        LoadCaret(new Caret(offset));
        Document.History.BreakCoalescing();
        EnsureCursorVisible();
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>Ctrl+Alt+↑ (EDIT-08 の仕様 2): 主カーソルと同じ列の上の行にカーソルを追加する。</summary>
    public SelectionResult AddCaretAbove() =>
        _cursor >= BytesPerRow && Layout.RowOf(_cursor) > 0 ? AddCaret(_cursor - BytesPerRow) : SelectionResult.OutOfRange;

    /// <summary>Ctrl+Alt+↓: 主カーソルと同じ列の下の行にカーソルを追加する。</summary>
    public SelectionResult AddCaretBelow() =>
        _cursor <= Layout.MaxCursor - BytesPerRow ? AddCaret(_cursor + BytesPerRow) : SelectionResult.OutOfRange;

    /// <summary>
    /// 「選択範囲の各要素にカーソルを置く」(EDIT-08 の呼び出し): 各要素の先頭にカーソルを置く。<paramref name="keepSelections"/> なら
    /// 各カーソルがその要素を選択したままにする (マルチ選択への入力。EDIT-07 の仕様 7)。矩形選択では各行の左端に置く (EDIT-17 の仕様 5)。
    /// </summary>
    public SelectionResult CaretsAtSelectionElements(bool keepSelections = false)
    {
        CommitSelectionCore();
        long count = SelectedRangeCount;
        if (count == 0)
        {
            return SelectionResult.NoSelection;
        }

        if (count > MaxCarets)
        {
            return SelectionResult.TooManyCarets;
        }

        ByteRange? primary = PrimaryRange;
        var carets = new List<Caret>((int)count);
        Caret? primaryCaret = null;
        foreach (ByteRange r in SelectedRanges)
        {
            Caret caret = keepSelections ? new Caret(r.Start, r.End) : new Caret(r.Start);
            if (primary is { } p && r.Contains(p.Start))
            {
                primaryCaret = caret;
            }
            else
            {
                carets.Add(caret);
            }
        }

        _rect = null;
        _others = null;
        _carets = carets;
        if (primaryCaret is null)
        {
            primaryCaret = _carets[^1];
            _carets.RemoveAt(_carets.Count - 1);
        }

        LoadCaret(primaryCaret.Value);
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>
    /// 主カーソルだけに戻す (Esc。EDIT-03 の仕様 4、EDIT-08 の呼び出し)。マルチ選択では主要素の末尾にカーソルを置く。選択も解除する。
    /// </summary>
    public void CollapseToPrimary()
    {
        CommitSelectionCore();
        if (_others is { Count: > 0 } && PrimaryRange is { } primary)
        {
            _cursor = Math.Min(primary.End, Layout.MaxCursor);
        }

        CollapseCarets();
        ClearSelectionAnchor();
        RaiseChanged();
    }

    // ---- ずらす・広げる (EDIT-05) ----

    /// <summary>最後に「N バイトずらす」で指定した量 (仕様 2。「ずらす量: 最後に指定した量」の設定のとき次へ / 前へずらすに使う)。</summary>
    public long? LastShiftAmount { get; set; }

    /// <summary>
    /// 選択範囲を <paramref name="delta"/> バイトずらす (仕様 1・2・6)。マルチ選択ではすべての要素を同じ量だけずらす。1 つでも範囲外に出る
    /// 場合は何もせず <see cref="SelectionResult.OutOfRange"/>。ずらした後は選択範囲の開始が見えるようにスクロールする (仕様 7)。
    /// </summary>
    public SelectionResult ShiftSelection(long delta)
    {
        CollapseCarets();
        if (_rect is not null && ConvertRectangleToMulti() is var converted && converted != SelectionResult.Done)
        {
            return converted;
        }

        CommitSelectionCore();
        if (PrimaryRange is not { } primary)
        {
            return SelectionResult.NoSelection;
        }

        ByteRange bounds = _others is { Count: > 0 } others
            ? ByteRange.FromBounds(Math.Min(others.First.Start, primary.Start), Math.Max(others.Last.End, primary.End))
            : primary;
        if (delta < -bounds.Start || delta > Layout.Length - bounds.End)
        {
            return SelectionResult.OutOfRange;
        }

        if (delta == 0)
        {
            return SelectionResult.Done;
        }

        _others = _others?.Shifted(delta);
        bool cursorAtStart = _cursor <= primary.Start && _anchor >= primary.End;
        long anchor = _anchor;
        long cursor = _cursor;
        _selectionStart = primary.Start + delta;
        _selectionLength = primary.Length;
        _anchor = anchor >= 0 ? Math.Clamp(anchor + delta, 0, Layout.Length) : _selectionStart;
        _cursor = Math.Clamp(cursor + delta, 0, Layout.MaxCursor);
        if (cursorAtStart)
        {
            _cursor = _selectionStart;
        }

        LowNibble = false;
        Document.History.BreakCoalescing();
        ScrollToJumpRange(_selectionStart, _selectionLength, JumpPlacement);
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>
    /// 「次へずらす」/「前へずらす」(仕様 1・2): 選択範囲 (主要素) の長さ、または <paramref name="useLastAmount"/> なら最後に指定した量だけずらす。
    /// </summary>
    public SelectionResult ShiftSelectionStep(bool forward, bool useLastAmount = false)
    {
        long amount = useLastAmount && LastShiftAmount is { } last && last != 0 ? Math.Abs(last) : PrimaryRange?.Length ?? 0;
        if (amount == 0)
        {
            return SelectionResult.NoSelection;
        }

        return ShiftSelection(forward ? amount : -amount);
    }

    /// <summary>
    /// 「広げる / 狭める」(仕様 4): 開始側を <paramref name="startDelta"/> バイト前へ、終了側を <paramref name="endDelta"/> バイト後ろへ動かす
    /// (負の値で狭める)。マルチ選択ではすべての要素に行う (重なった要素は結合する)。範囲外に出る・長さが 0 以下になる場合は何もしない。
    /// </summary>
    public SelectionResult ResizeSelection(long startDelta, long endDelta)
    {
        CollapseCarets();
        if (_rect is not null && ConvertRectangleToMulti() is var converted && converted != SelectionResult.Done)
        {
            return converted;
        }

        CommitSelectionCore();
        if (PrimaryRange is not { } primary)
        {
            return SelectionResult.NoSelection;
        }

        long length = Layout.Length;
        var resized = new List<ByteRange>();
        foreach (ByteRange r in SelectedRanges)
        {
            long s = r.Start - startDelta, e = r.End + endDelta;
            if (s < 0 || e > length || e <= s)
            {
                return SelectionResult.OutOfRange;
            }

            resized.Add(ByteRange.FromBounds(s, e));
        }

        var newPrimary = ByteRange.FromBounds(primary.Start - startDelta, primary.End + endDelta);
        if (resized.Count == 1)
        {
            _others = null;
            SetPrimaryElement(newPrimary);
        }
        else
        {
            var set = new RangeSet(resized);
            ByteRange merged = set.Find(newPrimary.Start) ?? set.Last;
            set.Remove(merged);
            _others = set.Count > 0 ? set : null;
            SetPrimaryElement(merged, keepOthers: true);
        }

        Document.History.BreakCoalescing();
        ScrollToJumpRange(_selectionStart, _selectionLength, JumpPlacement);
        RaiseChanged();
        return SelectionResult.Done;
    }

    /// <summary>
    /// 「アンカーとカーソルを入れ替える」(仕様 5): 選択範囲は変えず、カーソルを反対側の端に移して表示もその端へスクロールする。
    /// カーソルが末尾側にあれば先頭へ (アンカーは末尾の次)、先頭側にあれば末尾の次へ (アンカーは先頭) 移す。
    /// </summary>
    public SelectionResult SwapAnchorAndCursor()
    {
        CollapseCarets();
        if (_rect is { } rect)
        {
            // 矩形は対角を入れ替える。
            (long anchor, long cursor) = (_cursor, _rectAnchor);
            _rectAnchor = anchor;
            _cursor = cursor;
            SyncRectanglePrimary();
            EnsureCursorVisible();
            RaiseChanged();
            return SelectionResult.Done;
        }

        CommitSelectionCore();
        if (_selectionLength == 0)
        {
            return SelectionResult.NoSelection;
        }

        long start = _selectionStart, end = _selectionStart + _selectionLength;
        if (_cursor > start)
        {
            _cursor = start;
            _anchor = end;
        }
        else
        {
            _cursor = Math.Min(end, Layout.MaxCursor);
            _anchor = start;
        }

        LowNibble = false;
        Document.History.BreakCoalescing();
        EnsureCursorVisible();
        RaiseChanged();
        return SelectionResult.Done;
    }

    // ---- 内部 ----

    /// <summary>主要素と、それに重なる・隣り合う他の要素を結合した範囲と、結合した要素の数・合計バイト数。</summary>
    private (ByteRange Merged, int TouchedCount, long TouchedTotal) MergedPrimary()
    {
        var primary = new ByteRange(_selectionStart, _selectionLength);
        long start = primary.Start, end = primary.End, total = 0;
        int count = 0;
        if (_others is { } others && primary.Length > 0)
        {
            foreach (ByteRange r in others.Touching(primary.Start, primary.Length))
            {
                start = Math.Min(start, r.Start);
                end = Math.Max(end, r.End);
                total += r.Length;
                count++;
            }
        }

        return (ByteRange.FromBounds(start, end), count, total);
    }

    private long SelectedRangeCountMulti()
    {
        if (_others is not { } others)
        {
            return _selectionLength > 0 ? 1 : 0;
        }

        if (_selectionLength == 0)
        {
            return others.Count;
        }

        return others.Count - MergedPrimary().TouchedCount + 1;
    }

    /// <summary>他の要素と、結合した主要素を、オフセット順に [start, start + length) の範囲で列挙する。</summary>
    private IEnumerable<ByteRange> MergeWithPrimary(RangeSet others, long start, long length)
    {
        ByteRange? merged = _selectionLength > 0 ? MergedPrimary().Merged : null;
        long end = length == long.MaxValue ? long.MaxValue : start + length;
        bool emitted = merged is null || merged.Value.End <= start || merged.Value.Start >= end;
        foreach (ByteRange r in others.Overlapping(start, length == long.MaxValue ? long.MaxValue - start : length))
        {
            if (merged is { } m && r.Start >= m.Start && r.End <= m.End)
            {
                continue;
            }

            if (!emitted && r.Start > merged!.Value.Start)
            {
                emitted = true;
                yield return merged.Value;
            }

            yield return r;
        }

        if (!emitted)
        {
            yield return merged!.Value;
        }
    }

    private SelectionResult CommitSelectionCore()
    {
        if (_others is not { } others)
        {
            _previousPrimary = null;
            return SelectionResult.Done;
        }

        SelectionResult result = SelectionResult.Done;
        if (_selectionLength > 0)
        {
            (ByteRange merged, int touched, long _) = MergedPrimary();
            if (touched == 0 && others.Count + 1 > MaxSelectionElements)
            {
                // 上限を超える追加はしない (EDIT-07 の仕様 3)。
                _selectionLength = 0;
                result = SelectionResult.TooManyElements;
            }
            else
            {
                others.Add(merged);
                others.Remove(merged);
                long cursor = _cursor;
                long anchor = _anchor;
                _selectionStart = merged.Start;
                _selectionLength = merged.Length;
                _cursor = cursor;
                _anchor = anchor;
            }
        }

        if (_selectionLength == 0)
        {
            RestorePrimaryFromOthers();
        }

        if (_others is { Count: 0 })
        {
            _others = null;
        }

        _previousPrimary = null;
        return result;
    }

    /// <summary>今の主要素を他の要素に移し、他の要素の集合を返す (なければ作る)。</summary>
    private RangeSet TakeIntoOthers()
    {
        _others ??= new RangeSet();
        if (_selectionLength > 0)
        {
            _others.Add(new ByteRange(_selectionStart, _selectionLength));
            _selectionLength = 0;
        }

        return _others;
    }

    /// <summary>主要素がないとき、前の主要素 (なければ最後の要素) を他の要素から取り出して主要素にする。</summary>
    private void RestorePrimaryFromOthers()
    {
        if (_others is not { Count: > 0 } others)
        {
            _others = null;
            return;
        }

        ByteRange primary = _previousPrimary is { } p && others.Find(p.Start) is { } found ? found : others.Last;
        others.Remove(primary);
        SetPrimaryElement(primary, keepOthers: true);
        if (others.Count == 0)
        {
            _others = null;
        }
    }

    /// <summary>主要素を <paramref name="range"/> にする。アンカーを開始、カーソルを末尾 (最後のバイトの次) に置く。</summary>
    private void SetPrimaryElement(ByteRange range, bool keepOthers = false)
    {
        if (!keepOthers && _others is not null && _others.Remove(range) && _others.Count == 0)
        {
            _others = null;
        }

        _selectionStart = range.Start;
        _selectionLength = range.Length;
        _anchor = range.Start;
        _cursor = Math.Min(range.End, Layout.MaxCursor);
        LowNibble = false;
    }

    /// <summary>矩形のとき、主要素 (互換のための単一の選択) をカーソルの行の範囲にする。</summary>
    private void SyncRectanglePrimary()
    {
        if (_rect is not { } r)
        {
            return;
        }

        long length = Layout.Length;
        ByteRange row = r.RowRange(Layout.RowOf(_cursor), length);
        if (row.Length == 0)
        {
            row = r.Ranges(length).FirstOrDefault();
        }

        _selectionStart = row.Start;
        _selectionLength = row.Length;
        _anchor = _rectAnchor;
    }

    /// <summary>主カーソル以外のカーソルを捨てる。</summary>
    private void CollapseCarets()
    {
        if (!_caretLoop)
        {
            _carets = null;
        }
    }

    private void LoadCaret(Caret caret)
    {
        _cursor = Math.Clamp(caret.Offset, 0, Layout.MaxCursor);
        ByteRange selection = caret.Selection;
        if (selection.Length > 0)
        {
            _anchor = caret.Anchor;
            _selectionStart = selection.Start;
            _selectionLength = Math.Min(selection.Length, Math.Max(0, Layout.Length - selection.Start));
        }
        else
        {
            _anchor = -1;
            _selectionStart = _cursor;
            _selectionLength = 0;
        }

        LowNibble = caret.LowNibble && _cursor < Layout.Length;
    }

    /// <summary>編集 (offset, removed, inserted) の後の位置 (他のカーソル・要素を動かす。EDIT-07 の仕様 8、EDIT-08 の仕様 5)。</summary>
    private static long ShiftPosition(long x, long offset, long removed, long inserted)
    {
        if (x <= offset)
        {
            return x;
        }

        return x >= offset + removed ? x - removed + inserted : offset;
    }

    /// <summary>マルチ選択・マルチカーソルを、ドキュメントの編集に合わせて動かす。</summary>
    private void ShiftSelectionsForEdit(DocumentChangedEventArgs e)
    {
        if (e.IsWholeDocument)
        {
            return;
        }

        if (_caretLoop && _loopCarets is not null)
        {
            // 処理済みのカーソル (後ろのカーソル) は、処理の最後にまとめて動かす (ForEachCaret)。
            _loopDelta += e.InsertedLength - e.RemovedLength;
            return;
        }

        if (e.RemovedLength == e.InsertedLength)
        {
            return;
        }

        if (_carets is { Count: > 0 } carets)
        {
            for (int i = 0; i < carets.Count; i++)
            {
                carets[i] = ShiftCaret(carets[i], e);
            }
        }

        if (_others is { Count: > 0 } others)
        {
            var shifted = new RangeSet();
            foreach (ByteRange r in others)
            {
                long s = ShiftPosition(r.Start, e.Offset, e.RemovedLength, e.InsertedLength);
                long t = r.End <= e.Offset ? r.End : ShiftPosition(r.End, e.Offset, e.RemovedLength, e.InsertedLength);
                if (t > s)
                {
                    shifted.Add(ByteRange.FromBounds(s, t));
                }
            }

            _others = shifted.Count > 0 ? shifted : null;
        }
    }

    private static Caret ShiftCaret(Caret c, DocumentChangedEventArgs e) => c with
    {
        Offset = ShiftPosition(c.Offset, e.Offset, e.RemovedLength, e.InsertedLength),
        Anchor = c.Anchor < 0 ? -1 : ShiftPosition(c.Anchor, e.Offset, e.RemovedLength, e.InsertedLength),
    };
}
