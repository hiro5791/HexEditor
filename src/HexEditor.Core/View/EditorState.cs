using System.Text;
using HexEditor.Core.Engine;

namespace HexEditor.Core.View;

/// <summary>キー入力を受け付ける列 (VIEW-27)。</summary>
public enum ActiveColumn
{
    Hex,
    Text,
}

/// <summary>入力の結果。UI はこれを見て InfoBar などを出す。</summary>
public enum EditResult
{
    Done,
    Ignored,

    /// <summary>長さを変えられないドキュメントのため、長さを変える操作をしなかった (EDIT-10 の仕様 2、EDIT-13 の仕様 5)。</summary>
    FixedLength,

    /// <summary>読み取り専用・処理中などで編集できない。</summary>
    NotEditable,

    /// <summary>現在の文字コードで表せない文字が含まれる (EDIT-12 の仕様 4)。</summary>
    NotEncodable,

    /// <summary>長さを変えられないドキュメントで、末尾を越える分を書かなかった (EDIT-23 の仕様 5)。</summary>
    Truncated,
}

/// <summary>
/// 1 つのビューの、カーソル・選択範囲・スクロール位置・入力モード (VIEW-25〜VIEW-27、VIEW-34、EDIT-02、EDIT-10〜EDIT-13)。
/// UI に依存しない。表示側は <see cref="VisibleRows"/> を設定し、<see cref="Changed"/> で再描画する。
/// </summary>
public sealed class EditorState
{
    private const string TypingKey = "typing";
    private const string DeleteKey = "delete";

    private long _cursor;
    private long _anchor = -1;
    private long _selectionStart;
    private long _selectionLength;
    private long _topRow;
    private int _visibleRows = 1;
    private readonly LinkedList<JumpPoint> _back = new();
    private readonly Stack<JumpPoint> _forward = new();

    /// <summary>ジャンプ履歴の 1 件 (VIEW-31)。</summary>
    private readonly record struct JumpPoint(long Offset, long TopRow, ActiveColumn Column);

    /// <summary>ジャンプ履歴の最大件数 (VIEW-31 の仕様 4)。</summary>
    public const int JumpHistoryLimit = 100;

    public EditorState(Document document, int bytesPerRow = 16)
    {
        Document = document;
        BytesPerRow = bytesPerRow;
        Document.Changed += (_, e) => OnDocumentChanged(e);
    }

    public Document Document { get; }

    public int BytesPerRow { get; private set; }

    public HexLayout Layout => new(BytesPerRow, Document.Length, Document.CanResize);

    /// <summary>カーソルのオフセット。</summary>
    public long Cursor => _cursor;

    /// <summary>Hex 列でカーソルが下位ニブルにあるか。</summary>
    public bool LowNibble { get; private set; }

    public ActiveColumn ActiveColumn { get; private set; } = ActiveColumn.Hex;

    /// <summary>挿入モードか (EDIT-10)。長さを変えられないドキュメントでは常に偽。</summary>
    public bool InsertMode { get; private set; }

    /// <summary>読み取り専用か (EDIT-16 の簡易版。切り替えの規則は EDIT-16 で定める)。</summary>
    public bool ReadOnly { get; set; }

    public long SelectionStart => _selectionStart;

    public long SelectionLength => _selectionLength;

    public bool HasSelection => _selectionLength > 0;

    /// <summary>一番上に表示している行。</summary>
    public long TopRow => _topRow;

    /// <summary>画面に完全に収まる行数 V。表示側が設定する。</summary>
    public int VisibleRows
    {
        get => _visibleRows;
        set
        {
            _visibleRows = Math.Max(1, value);
            SetTopRow(_topRow);
        }
    }

    /// <summary>カーソル・選択範囲・スクロール位置・モードが変わった。</summary>
    public event EventHandler? Changed;

    // ---- 移動 (VIEW-25) ----

    public void MoveLeft(bool extend = false) => MoveTo(_cursor - 1, extend);

    public void MoveRight(bool extend = false) => MoveTo(_cursor + 1, extend);

    public void MoveUp(bool extend = false)
    {
        if (Layout.RowOf(_cursor) > 0)
        {
            MoveTo(_cursor - BytesPerRow, extend, keepNibble: true);
        }
    }

    public void MoveDown(bool extend = false)
    {
        HexLayout layout = Layout;
        long target = _cursor + BytesPerRow;
        if (target <= layout.MaxCursor)
        {
            MoveTo(target, extend, keepNibble: true);
        }
        else if (layout.RowOf(_cursor) < layout.RowOf(layout.MaxCursor))
        {
            MoveTo(layout.MaxCursor, extend, keepNibble: true);
        }
    }

    public void MoveHome(bool extend = false) => MoveTo(Layout.RowStart(Layout.RowOf(_cursor)), extend);

    public void MoveEnd(bool extend = false)
    {
        HexLayout layout = Layout;
        long rowEnd = layout.RowStart(layout.RowOf(_cursor)) + BytesPerRow - 1;
        MoveTo(Math.Min(rowEnd, layout.MaxCursor), extend);
    }

    public void PageUp(bool extend = false)
    {
        long page = Math.Max(1, _visibleRows - 1);
        long rows = Math.Min(page, Layout.RowOf(_cursor));
        SetTopRow(_topRow - page);
        MoveTo(_cursor - rows * BytesPerRow, extend, keepNibble: true, scroll: false);
        EnsureCursorVisible();
    }

    public void PageDown(bool extend = false)
    {
        long page = Math.Max(1, _visibleRows - 1);
        long target = _cursor > long.MaxValue - page * BytesPerRow ? Layout.MaxCursor : _cursor + page * BytesPerRow;
        SetTopRow(_topRow + page);
        MoveTo(Math.Min(target, Layout.MaxCursor), extend, keepNibble: true, scroll: false);
        EnsureCursorVisible();
    }

    public void MoveToStart(bool extend = false) => MoveTo(0, extend);

    public void MoveToEnd(bool extend = false) => MoveTo(Layout.MaxCursor, extend);

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    /// <summary>ジャンプ履歴を 1 つ戻る (Alt+←。VIEW-31 の仕様 5)。</summary>
    public void GoBack()
    {
        if (_back.Last is not { } last)
        {
            return;
        }

        _back.RemoveLast();
        _forward.Push(CurrentPoint());
        JumpTo(last.Value);
    }

    /// <summary>ジャンプ履歴を 1 つ進む (Alt+→)。</summary>
    public void GoForward()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        JumpPoint next = _forward.Pop();
        _back.AddLast(CurrentPoint());
        JumpTo(next);
    }

    /// <summary>
    /// オフセットへのジャンプ (VIEW-29)。移動前の位置をジャンプ履歴に記録し、移動先が表示外なら上から 1/3 の位置に置く
    /// (VIEW-34 の仕様 2)。<paramref name="extendSelection"/> は「選択しながら移動」。
    /// </summary>
    public void GoTo(long offset, bool extendSelection = false)
    {
        RecordJump();
        if (extendSelection)
        {
            long from = _cursor;
            long to = Math.Clamp(offset, 0, Layout.MaxCursor);
            _anchor = from;
            SetSelection(Math.Min(from, to), Math.Abs(to - from));
            _cursor = to;
            LowNibble = false;
            ScrollToJumpTarget();
            RaiseChanged();
            return;
        }

        ClearSelectionAnchor();
        MoveTo(offset, extend: false, scroll: false);
        ScrollToJumpTarget();
        RaiseChanged();
    }

    /// <summary>
    /// 検索の一致を選択する (FIND-04 の仕様 8)。移動前の位置をジャンプ履歴に記録し、一致が画面の中央付近に来るようにする。
    /// </summary>
    public void SelectMatch(long offset, long length)
    {
        RecordJump();
        _anchor = offset;
        SetSelection(offset, length);
        _cursor = Math.Min(offset, Layout.MaxCursor);
        LowNibble = false;
        long row = Layout.RowOf(_cursor);
        if (row < _topRow || row >= _topRow + _visibleRows)
        {
            SetTopRow(row - _visibleRows / 2);
        }

        RaiseChanged();
    }

    private void ScrollToJumpTarget()
    {
        long row = Layout.RowOf(_cursor);
        if (row < _topRow || row >= _topRow + _visibleRows)
        {
            SetTopRow(row - _visibleRows / 3);
        }
    }

    private JumpPoint CurrentPoint() => new(_cursor, _topRow, ActiveColumn);

    /// <summary>移動前の位置を記録する。直前の記録と同じ行なら記録しない (VIEW-31 の仕様 3)。</summary>
    private void RecordJump()
    {
        JumpPoint point = CurrentPoint();
        if (_back.Last is { } last && Layout.RowOf(last.Value.Offset) == Layout.RowOf(point.Offset))
        {
            _forward.Clear();
            return;
        }

        _back.AddLast(point);
        if (_back.Count > JumpHistoryLimit)
        {
            _back.RemoveFirst();
        }

        _forward.Clear();
    }

    private void JumpTo(JumpPoint point)
    {
        ClearSelectionAnchor();
        _cursor = Math.Clamp(point.Offset, 0, Layout.MaxCursor);
        LowNibble = false;
        ActiveColumn = point.Column;
        SetTopRow(point.TopRow);
        EnsureCursorVisible();
        RaiseChanged();
    }

    /// <summary>挿入・削除に合わせて履歴の位置をずらす (VIEW-31 の仕様 6)。</summary>
    private void AdjustJumpHistory(DocumentChangedEventArgs e)
    {
        if (e.IsWholeDocument)
        {
            return;
        }

        long Shift(long offset)
        {
            if (offset < e.Offset)
            {
                return offset;
            }

            if (offset < e.Offset + e.RemovedLength)
            {
                return e.Offset;
            }

            return offset - e.RemovedLength + e.InsertedLength;
        }

        for (LinkedListNode<JumpPoint>? node = _back.First; node is not null; node = node.Next)
        {
            node.Value = node.Value with { Offset = Shift(node.Value.Offset) };
        }

        JumpPoint[] forward = [.. _forward.Reverse().Select(p => p with { Offset = Shift(p.Offset) })];
        _forward.Clear();
        foreach (JumpPoint p in forward)
        {
            _forward.Push(p);
        }
    }

    /// <summary>カーソルを動かさずに表示だけを動かす (Ctrl+↑ / Ctrl+↓、ホイール)。</summary>
    public void ScrollRows(long rows)
    {
        SetTopRow(_topRow + rows);
        RaiseChanged();
    }

    /// <summary>スクロールバーなどから一番上の行を直接決める。</summary>
    public void ScrollToRow(long row)
    {
        SetTopRow(row);
        RaiseChanged();
    }

    /// <summary>マウスのクリック (VIEW-25 の仕様 6)。<paramref name="extend"/> は Shift+クリック (EDIT-01 の仕様 4: 両端を含む)。</summary>
    public void Click(long offset, ActiveColumn column, bool lowNibble, bool extend)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        ActiveColumn = column;
        if (extend)
        {
            long anchor = _anchor >= 0 ? _anchor : _cursor;
            _anchor = anchor;
            long start = Math.Min(anchor, offset);
            long end = Math.Min(Math.Max(anchor, offset) + 1, Layout.Length);
            SetSelection(start, Math.Max(0, end - start));
            _cursor = offset;
            LowNibble = false;
        }
        else
        {
            ClearSelectionAnchor();
            _cursor = offset;
            LowNibble = column == ActiveColumn.Hex && lowNibble && offset < Layout.Length;
        }

        EnsureCursorVisible();
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>マウスのドラッグで選択範囲を伸ばす (両端を含む)。</summary>
    public void DragTo(long offset)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        if (_anchor < 0)
        {
            _anchor = _cursor;
        }

        long start = Math.Min(_anchor, offset);
        long end = Math.Min(Math.Max(_anchor, offset) + 1, Layout.Length);
        SetSelection(start, Math.Max(0, end - start));
        _cursor = offset;
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
    }

    /// <summary>Tab / Shift+Tab で列を切り替える (VIEW-27)。</summary>
    public void ToggleColumn()
    {
        ActiveColumn = ActiveColumn == ActiveColumn.Hex ? ActiveColumn.Text : ActiveColumn.Hex;
        LowNibble = false;
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>すべて選択 (EDIT-03)。</summary>
    public void SelectAll()
    {
        _anchor = 0;
        SetSelection(0, Layout.Length);
        RaiseChanged();
    }

    /// <summary>選択を解除する (Esc)。</summary>
    public void ClearSelection()
    {
        ClearSelectionAnchor();
        RaiseChanged();
    }

    /// <summary>範囲を選択する (Ctrl+E。EDIT-04)。<paramref name="length"/> は選択するバイト数。</summary>
    public void Select(long start, long length)
    {
        start = Math.Clamp(start, 0, Layout.Length);
        length = Math.Clamp(length, 0, Layout.Length - start);
        _anchor = start;
        SetSelection(start, length);
        _cursor = Math.Min(start + length, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
    }

    // ---- モード (EDIT-10) ----

    /// <summary>上書き / 挿入を切り替える。長さを変えられないドキュメントでは切り替えない。</summary>
    public EditResult ToggleInsertMode()
    {
        if (!Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        InsertMode = !InsertMode;
        Document.History.BreakCoalescing();
        RaiseChanged();
        return EditResult.Done;
    }

    // ---- 入力 (EDIT-11、EDIT-12) ----

    /// <summary>Hex 列での 1 桁の入力。16 進数字以外は無視する。全角の数字・英字も受け付ける。</summary>
    public EditResult TypeHexDigit(char c)
    {
        int digit = HexDigit(c);
        if (digit < 0)
        {
            return EditResult.Ignored;
        }

        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        EditResult prepared = PrepareTyping();
        if (prepared != EditResult.Done)
        {
            return prepared;
        }

        HexLayout layout = Layout;
        bool atEnd = _cursor >= layout.Length;
        if (atEnd && !Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        if (!LowNibble)
        {
            byte value = (byte)(digit << 4);
            if (InsertMode || atEnd)
            {
                Document.Insert(_cursor, [value], "入力", TypingKey);
            }
            else
            {
                byte current = ReadByte(_cursor);
                Document.Overwrite(_cursor, [(byte)((current & 0x0F) | value)], "入力", TypingKey);
            }

            LowNibble = true;
        }
        else
        {
            byte current = ReadByte(_cursor);
            Document.Overwrite(_cursor, [(byte)((current & 0xF0) | digit)], "入力", TypingKey);
            LowNibble = false;
            _cursor = Math.Min(_cursor + 1, Layout.MaxCursor);
        }

        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>テキスト列での確定した文字列の入力 (EDIT-12)。<paramref name="encoding"/> で変換したバイト列を書き込む。</summary>
    public EditResult TypeText(string text, Encoding encoding)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EditResult.Ignored;
        }

        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        byte[] bytes;
        try
        {
            Encoding strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            bytes = strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return EditResult.NotEncodable;
        }

        EditResult prepared = PrepareTyping();
        if (prepared != EditResult.Done)
        {
            return prepared;
        }

        if (InsertMode)
        {
            Document.Insert(_cursor, bytes, "入力", TypingKey);
        }
        else
        {
            long room = Document.Length - _cursor;
            if (!Document.CanResize && bytes.Length > room)
            {
                return EditResult.FixedLength;
            }

            Document.Overwrite(_cursor, bytes, "入力", TypingKey);
        }

        _cursor = Math.Min(_cursor + bytes.Length, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    // ---- 削除 (EDIT-13) ----

    /// <summary>Delete キー。選択範囲、またはカーソル位置の 1 バイトを削除する。</summary>
    public EditResult Delete()
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        if (HasSelection)
        {
            return DeleteSelection();
        }

        if (_cursor >= Document.Length)
        {
            return EditResult.Ignored;
        }

        Document.Delete(_cursor, 1, "削除", DeleteKey);
        LowNibble = false;
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>Backspace キー。挿入モードでは直前の 1 バイトを削除し、上書きモードではカーソルを戻すだけ。</summary>
    public EditResult Backspace()
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (HasSelection)
        {
            return Document.CanResize ? DeleteSelection() : EditResult.FixedLength;
        }

        if (!InsertMode)
        {
            if (ActiveColumn == ActiveColumn.Hex && LowNibble)
            {
                LowNibble = false;
            }
            else if (_cursor > 0)
            {
                _cursor--;
                LowNibble = ActiveColumn == ActiveColumn.Hex;
            }

            EnsureCursorVisible();
            RaiseChanged();
            return EditResult.Done;
        }

        if (ActiveColumn == ActiveColumn.Hex && LowNibble)
        {
            // 上位ニブルだけ入力した直後のバイトを削除する。
            Document.Delete(_cursor, 1, "削除", DeleteKey);
            LowNibble = false;
        }
        else if (_cursor > 0)
        {
            Document.Delete(_cursor - 1, 1, "削除", DeleteKey);
            _cursor--;
        }
        else
        {
            return EditResult.Ignored;
        }

        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    // ---- 貼り付け (EDIT-23) ----

    /// <summary>
    /// 貼り付け。<paramref name="overwrite"/> は上書き貼り付け (Ctrl+B)。挿入モードでは選択範囲を置き換えるかカーソル位置に挿入し、
    /// 上書きモードではカーソル (選択範囲があればその先頭) から上書きする。固定長ドキュメントでは常に上書きし、末尾を越える分は書かない。
    /// 貼り付けた範囲を選択する (EDIT-23 の仕様 7)。
    /// </summary>
    public EditResult Paste(DocumentSnapshot source, long sourceOffset, long length, bool overwrite)
    {
        if (length <= 0)
        {
            return EditResult.Ignored;
        }

        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        long at = HasSelection ? _selectionStart : _cursor;
        bool insert = InsertMode && !overwrite && Document.CanResize;
        EditResult result = EditResult.Done;
        if (insert)
        {
            if (HasSelection)
            {
                Document.Delete(_selectionStart, _selectionLength, "削除");
            }

            Document.InsertFrom(at, source, sourceOffset, length);
        }
        else
        {
            long room = Document.Length - at;
            if (!Document.CanResize && length > room)
            {
                length = room;
                result = EditResult.Truncated;
            }

            Document.OverwriteFrom(at, source, sourceOffset, length);
        }

        _anchor = at;
        SetSelection(at, length);
        _cursor = Math.Min(at + length, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return result;
    }

    /// <summary>バイト列の貼り付け (システムのクリップボードから)。</summary>
    public EditResult Paste(byte[] data, bool overwrite)
    {
        var temp = new Document(new Sources.MemoryByteSource(data));
        try
        {
            return Paste(temp.Current, 0, data.Length, overwrite);
        }
        finally
        {
            temp.Dispose();
        }
    }

    /// <summary>切り取りの後半: 選択範囲を削除する (コピーは呼び出し側が先に済ませる。EDIT-22 の仕様 7)。</summary>
    public EditResult DeleteSelectionForCut()
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        return HasSelection ? DeleteSelection() : EditResult.Ignored;
    }

    public void Undo()
    {
        if (Document.History.CanUndo && !Document.IsEditLocked)
        {
            Document.Undo();
        }
    }

    public void Redo()
    {
        if (Document.History.CanRedo && !Document.IsEditLocked)
        {
            Document.Redo();
        }
    }

    // ---- 内部 ----

    private EditResult DeleteSelection()
    {
        Document.Delete(_selectionStart, _selectionLength, "削除");
        _cursor = _selectionStart;
        ClearSelectionAnchor();
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>選択範囲がある状態での入力の準備 (EDIT-11 の仕様 3)。</summary>
    private EditResult PrepareTyping()
    {
        if (!HasSelection)
        {
            return EditResult.Done;
        }

        if (InsertMode)
        {
            Document.Delete(_selectionStart, _selectionLength, "削除");
        }

        _cursor = _selectionStart;
        LowNibble = false;
        ClearSelectionAnchor();
        return EditResult.Done;
    }

    private bool CanEdit() => !ReadOnly && !Document.IsEditLocked;

    private byte ReadByte(long offset)
    {
        Span<byte> one = stackalloc byte[1];
        Document.Current.Read(offset, one);
        return one[0];
    }

    private void MoveTo(long target, bool extend, bool keepNibble = false, bool scroll = true)
    {
        HexLayout layout = Layout;
        target = Math.Clamp(target, 0, layout.MaxCursor);
        if (extend)
        {
            if (_anchor < 0)
            {
                _anchor = HasSelection ? _selectionStart : _cursor;
            }

            // キーボードの選択は、アンカーとカーソルの間 (カーソル位置は含まない)。
            SetSelection(Math.Min(_anchor, target), Math.Abs(target - _anchor));
        }
        else
        {
            ClearSelectionAnchor();
        }

        if (!keepNibble || target >= layout.Length)
        {
            LowNibble = false;
        }

        _cursor = target;
        Document.History.BreakCoalescing();
        if (scroll)
        {
            EnsureCursorVisible();
        }

        RaiseChanged();
    }

    /// <summary>カーソルの行が表示領域の外なら、入る最小のスクロールをする (VIEW-34 の仕様 1)。</summary>
    private void EnsureCursorVisible()
    {
        long row = Layout.RowOf(_cursor);
        if (row < _topRow)
        {
            SetTopRow(row);
        }
        else if (row >= _topRow + _visibleRows)
        {
            SetTopRow(row - _visibleRows + 1);
        }
    }

    private void SetTopRow(long row) => _topRow = Math.Clamp(row, 0, Layout.MaxTopRow(_visibleRows));

    private void SetSelection(long start, long length)
    {
        _selectionStart = start;
        _selectionLength = length;
    }

    private void ClearSelectionAnchor()
    {
        _anchor = -1;
        _selectionLength = 0;
        _selectionStart = _cursor;
    }

    private void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        AdjustJumpHistory(e);
        HexLayout layout = Layout;
        if (_cursor > layout.MaxCursor)
        {
            _cursor = layout.MaxCursor;
            LowNibble = false;
        }

        if (_selectionStart + _selectionLength > layout.Length)
        {
            ClearSelectionAnchor();
        }

        SetTopRow(_topRow);
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= '０' and <= '９' => c - '０',
        >= 'ａ' and <= 'ｆ' => c - 'ａ' + 10,
        >= 'Ａ' and <= 'Ｆ' => c - 'Ａ' + 10,
        _ => -1,
    };
}
