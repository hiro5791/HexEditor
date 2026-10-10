using HexEditor.Core.Clipboard;
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

    /// <summary>
    /// 長さを変えられないドキュメントでの Delete / Backspace による削除をしなかった (EDIT-13 の仕様 5)。UI は InfoBar に
    /// 「00 で塗りつぶす」ボタン (<see cref="EditorState.FillWithZero"/>) を付ける。
    /// </summary>
    FixedLengthDelete,

    /// <summary>読み取り専用・処理中などで編集できない。</summary>
    NotEditable,

    /// <summary>現在の文字コードで表せない文字が含まれる (EDIT-12 の仕様 4)。</summary>
    NotEncodable,

    /// <summary>長さを変えられないドキュメントで、末尾を越える分を書かなかった (EDIT-23 の仕様 5)。</summary>
    Truncated,

    /// <summary>
    /// 長さを変えられないドキュメントで末尾を越える上書き貼り付け。何もしていない。UI は確認ダイアログで「貼り付けるデータのうち
    /// N バイト (<see cref="EditorState.PasteOverflow"/>) は末尾を越えるため貼り付けません」と示し、「末尾まで貼り付ける」なら
    /// allowTruncate を真にしてもう一度呼ぶ (ENG-07 の仕様 5)。
    /// </summary>
    NeedsTruncateConfirmation,

    /// <summary>
    /// Hex 列に Hex として解釈できないテキストを貼り付けたため、テキストとして文字コードで変換して貼った (EDIT-23 の仕様 2)。
    /// UI は InfoBar で「Hex として解釈できないため、テキストとして貼り付けました」と「元に戻す」ボタンを示す。
    /// </summary>
    PastedAsText,

    /// <summary>矩形の行数が、長さが変わる操作の上限を超える (EDIT-17 の仕様 6)。何もしていない。</summary>
    TooManyRows,

    /// <summary>マルチ選択・矩形をカーソルにすると、カーソル数の上限 (10,000) を超える (EDIT-08 の仕様 3)。何もしていない。</summary>
    TooManyCarets,
    /// <summary>
    /// Hex 以外のセルの表示形式 (VIEW-10) で Hex 列に入力した。何もしていない。UI はステータスバーに「この表示形式では直接編集できません。
    /// データインスペクタを使ってください」と出す (VIEW-10 の仕様 7)。
    /// </summary>
    CellFormatNotEditable,

    /// <summary>
    /// プロセスメモリの割り当てられていない範囲 (空き・予約) に入力した。何もしていない。UI は「この範囲は割り当てられていないため
    /// 編集できません」と示す (ENG-34 の仕様 7)。
    /// </summary>
    Unallocated,
}

/// <summary>ジャンプ先の表示位置 (VIEW-34 の仕様 2。設定 <c>view.jump.position</c>)。</summary>
public enum JumpPlacement
{
    /// <summary>移動先の行を表示領域の上端に置く。</summary>
    Top,

    /// <summary>上から 1/3 の位置に置く (既定)。</summary>
    Third,

    /// <summary>中央に置く。</summary>
    Center,
}

/// <summary>
/// 1 つのビューの、カーソル・選択範囲・スクロール位置・入力モード (VIEW-25〜VIEW-27、VIEW-34、EDIT-02、EDIT-10〜EDIT-13)。
/// UI に依存しない。表示側は <see cref="VisibleRows"/> を設定し、<see cref="Changed"/> で再描画する。
/// </summary>
public sealed partial class EditorState
{
    private const string TypingKey = "typing";

    // Delete と Backspace は別の種類の入力としてまとめる (EDIT-19 の仕様 5)。
    private const string DeleteKey = "delete";
    private const string BackspaceKey = "backspace";

    private long _cursor;
    private long _anchor = -1;
    private long _selectionStart;
    private long _selectionLength;
    private long _topRow;
    private int _visibleRows = 1;
    private readonly JumpHistory _jumps = new();

    /// <summary>ジャンプ履歴の最大件数 (VIEW-31 の仕様 4)。</summary>
    public const int JumpHistoryLimit = JumpHistory.Limit;

    public EditorState(Document document, int bytesPerRow = 16)
    {
        Document = document;
        BytesPerRow = Math.Clamp(bytesPerRow, 1, ViewSettings.MaxBytesPerRow);
        _view = ViewSettings.Default with { BytesPerRow = BytesPerRow };
        _documentChanged = (_, e) => OnDocumentChanged(e);
        _readOnlyChanged = (_, _) => RaiseChanged();
        Document.Changed += _documentChanged;
        Document.ReadOnlyChanged += _readOnlyChanged;
    }

    private readonly EventHandler<DocumentChangedEventArgs> _documentChanged;
    private readonly EventHandler _readOnlyChanged;

    /// <summary>
    /// ドキュメントのイベントの購読を外す (このビューだけを閉じ、ドキュメントは開いたままにするとき: 比較タブ、分割したペイン、新しいビュー)。
    /// 外さないと、ドキュメントを閉じるまでこのビューが解放されない。
    /// </summary>
    public void Detach()
    {
        Document.Changed -= _documentChanged;
        Document.ReadOnlyChanged -= _readOnlyChanged;
    }

    public Document Document { get; }

    public int BytesPerRow { get; private set; }

    public HexLayout Layout => new(BytesPerRow, Document.Length, Document.CanResize, View.EffectiveRowShift(BytesPerRow));

    /// <summary>カーソルのオフセット。</summary>
    public long Cursor => _cursor;

    /// <summary>Hex 列でカーソルが下位ニブルにあるか。</summary>
    public bool LowNibble { get; private set; }

    public ActiveColumn ActiveColumn { get; private set; } = ActiveColumn.Hex;

    /// <summary>挿入モードか (EDIT-10)。長さを変えられないドキュメントでは常に偽。</summary>
    public bool InsertMode { get; private set; }

    /// <summary>編集の設定 (Backspace・Delete・貼り付けなどの動作。アプリが設定から渡す)。</summary>
    public EditingOptions Options { get; set; } = EditingOptions.Default;

    /// <summary>
    /// 読み取り専用か (EDIT-16)。ドキュメントの状態 (<see cref="Document.ReadOnlyReason"/>) をそのまま表す。真にすると利用者の切り替え
    /// (<see cref="ReadOnlyReason.User"/>) として読み取り専用にし、偽にすると理由に関係なく解除する (解除できるかの確認は呼び出し側で行う)。
    /// </summary>
    public bool ReadOnly
    {
        get => Document.IsReadOnly;
        set => Document.SetReadOnly(!value ? ReadOnlyReason.None : Document.IsReadOnly ? Document.ReadOnlyReason : ReadOnlyReason.User);
    }

    /// <summary>
    /// テキスト列の文字コード (VIEW-21)。表示・入力・コピー・貼り付けのすべてでこれを使う。変えてもカーソルと選択範囲は変わらない
    /// (仕様 9)。
    /// </summary>
    public TextEncoding TextEncoding
    {
        get => TextColumn == 0 ? _textEncoding : TextEncodingOf(TextColumn);
        set
        {
            int column = TextColumn;
            if (column > 0)
            {
                // 2 列目以降のテキスト列 (VIEW-24): その列の文字コードだけを変える。
                List<TextColumnSpec> columns = [.. _view.TextColumns];
                if (column < columns.Count && !string.Equals(columns[column].Encoding, value.Id, StringComparison.OrdinalIgnoreCase))
                {
                    columns[column] = columns[column] with { Encoding = value.Id };
                    _view = _view.WithTextColumns(columns);
                    ViewChanged?.Invoke(this, EventArgs.Empty);
                    RaiseChanged();
                }

                return;
            }

            if (!ReferenceEquals(_textEncoding, value))
            {
                _textEncoding = value;
                _view = _view with { Encoding = value.Id };
                ViewChanged?.Invoke(this, EventArgs.Empty);
                RaiseChanged();
            }
        }
    }

    /// <summary>テキスト列 <paramref name="column"/> の文字コード (VIEW-24)。</summary>
    public TextEncoding TextEncodingOf(int column)
    {
        if (column <= 0)
        {
            return _textEncoding;
        }

        IReadOnlyList<TextColumnSpec> columns = _view.TextColumns;
        return column < columns.Count ? TextEncoding.FromId(columns[column].Encoding) : _textEncoding;
    }

    private TextEncoding _textEncoding = TextEncoding.Ascii;

    public long SelectionStart => _selectionStart;

    public long SelectionLength => _selectionLength;

    /// <summary>選択があるか (単一・マルチ・矩形のどれか)。</summary>
    public bool HasSelection => _selectionLength > 0 || _rect is not null || _others is { Count: > 0 };

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

    /// <summary>
    /// 設定「Hex 列で ← / → をニブル単位で動かす」(VIEW-26 の仕様 5。既定オフ)。オンのとき、Hex 列での ← / → (Shift なし・選択なし) は
    /// ニブル単位で動く。
    /// </summary>
    public bool NibbleArrowKeys { get; set; }

    /// <summary>設定「カーソルの上下に残す行数」(VIEW-34 の仕様 1。0〜10、既定 0)。</summary>
    public int CursorMargin
    {
        get => _cursorMargin;
        set => _cursorMargin = Math.Clamp(value, 0, 10);
    }

    private int _cursorMargin;

    /// <summary>設定「ジャンプ先の表示位置」(VIEW-34 の仕様 2。既定は上から 1/3)。</summary>
    public JumpPlacement JumpPlacement { get; set; } = JumpPlacement.Third;

    /// <summary>
    /// マウスでドラッグ中 (範囲選択など)。ビューが設定する。表示の更新の頻度を抑えるのに使う (VIEW-40 の仕様 4)。
    /// 終わったときは <see cref="Changed"/> を出す (抑えていた表示を最新にするため)。
    /// </summary>
    public bool PointerDragging
    {
        get => _pointerDragging;
        set
        {
            if (_pointerDragging != value)
            {
                _pointerDragging = value;
                if (!value)
                {
                    RaiseChanged();
                }
            }
        }
    }

    private bool _pointerDragging;

    /// <summary>カーソル・選択範囲・スクロール位置・モードが変わった。</summary>
    public event EventHandler? Changed;

    // ---- 移動 (VIEW-25) ----

    /// <summary>←。選択範囲がある場合 (Shift なし) は選択範囲の先頭に移るだけ (EDIT-02 の仕様 3)。</summary>
    public void MoveLeft(bool extend = false)
    {
        if (MoveCarets(() => MoveLeft(extend)))
        {
            return;
        }

        if (UsesNibbleArrows(extend))
        {
            PreviousNibble();
            return;
        }

        MoveTo(!extend && HasSelection ? _selectionStart : HorizontalTarget(forward: false), extend);
    }

    /// <summary>→。選択範囲がある場合 (Shift なし) は選択範囲の末尾 (最後のバイトの次) に移るだけ。</summary>
    public void MoveRight(bool extend = false)
    {
        if (MoveCarets(() => MoveRight(extend)))
        {
            return;
        }

        if (UsesNibbleArrows(extend))
        {
            NextNibble();
            return;
        }

        MoveTo(!extend && HasSelection ? _selectionStart + _selectionLength : HorizontalTarget(forward: true), extend);
    }

    /// <summary>
    /// 「次のニブルへ」(VIEW-26 の仕様 3): 上位なら同じバイトの下位へ、下位なら次のバイトの上位へ。最大値の位置では動かない
    /// (末尾位置にはバイトがないため、下位ニブルを持たない)。
    /// </summary>
    public void NextNibble()
    {
        if (!LowNibble && _cursor < Layout.Length)
        {
            ClearSelectionAnchor();
            LowNibble = true;
            AfterNibbleMove();
        }
        else if (LowNibble && _cursor < Layout.MaxCursor)
        {
            MoveTo(_cursor + 1, extend: false);
        }
    }

    /// <summary>「前のニブルへ」(VIEW-26 の仕様 4): 下位なら同じバイトの上位へ、上位なら前のバイトの下位へ。オフセット 0 の上位では動かない。</summary>
    public void PreviousNibble()
    {
        if (LowNibble)
        {
            ClearSelectionAnchor();
            LowNibble = false;
            AfterNibbleMove();
        }
        else if (_cursor > 0)
        {
            MoveTo(_cursor - 1, extend: false);
            LowNibble = _cursor < Layout.Length;
            RaiseChanged();
        }
    }

    private bool UsesNibbleArrows(bool extend) => NibbleArrowKeys && ActiveColumn == ActiveColumn.Hex && !extend && !HasSelection
        && View.CellFormat == CellFormat.Hex;

    private void AfterNibbleMove()
    {
        Document.History.BreakCoalescing();
        EnsureCursorVisible();
        RaiseChanged();
    }

    public void MoveUp(bool extend = false)
    {
        if (MoveCarets(() => MoveUp(extend)))
        {
            return;
        }

        if (Layout.RowOf(_cursor) > 0)
        {
            MoveTo(_cursor - BytesPerRow, extend, keepNibble: true);
        }
    }

    public void MoveDown(bool extend = false)
    {
        if (MoveCarets(() => MoveDown(extend)))
        {
            return;
        }

        HexLayout layout = Layout;
        // long を越える移動先は、最大値を越えたものとして扱う。
        bool beyond = _cursor > long.MaxValue - BytesPerRow;
        long target = beyond ? long.MaxValue : _cursor + BytesPerRow;
        if (!beyond && target <= layout.MaxCursor)
        {
            MoveTo(target, extend, keepNibble: true);
        }
        else if (layout.RowOf(_cursor) < layout.RowOf(layout.MaxCursor))
        {
            MoveTo(layout.MaxCursor, extend, keepNibble: true);
        }
    }

    /// <summary>
    /// 「移動: 前のグループへ」(Ctrl+←。VIEW-25 の仕様 8): グループの先頭にいなければそのグループの先頭へ、先頭にいれば前のグループの
    /// 先頭へ。グループ化が 1 のときは ← と同じ。
    /// </summary>
    public void MovePreviousGroup(bool extend = false)
    {
        if (MoveCarets(() => MovePreviousGroup(extend)))
        {
            return;
        }

        int group = Math.Max(1, View.GroupSize);
        if (group == 1)
        {
            MoveLeft(extend);
            return;
        }

        int within = (int)(Layout.ColumnOf(_cursor) % group);
        long target = _cursor - (within == 0 ? group : within);
        MoveTo(Math.Max(0, target), extend);
    }

    /// <summary>「移動: 次のグループへ」(Ctrl+→。VIEW-25 の仕様 8): 次のグループの先頭へ。最大値を超える場合は最大値へ。</summary>
    public void MoveNextGroup(bool extend = false)
    {
        if (MoveCarets(() => MoveNextGroup(extend)))
        {
            return;
        }

        int group = Math.Max(1, View.GroupSize);
        if (group == 1)
        {
            MoveRight(extend);
            return;
        }

        int within = (int)(Layout.ColumnOf(_cursor) % group);
        long target = SaturatingAdd(_cursor, group - within);
        MoveTo(Math.Min(target, Layout.MaxCursor), extend);
    }

    public void MoveHome(bool extend = false)
    {
        if (!MoveCarets(() => MoveHome(extend)))
        {
            MoveTo(Layout.RowStart(Layout.RowOf(_cursor)), extend);
        }
    }

    public void MoveEnd(bool extend = false)
    {
        if (MoveCarets(() => MoveEnd(extend)))
        {
            return;
        }

        HexLayout layout = Layout;
        long rowEnd = SaturatingAdd(layout.RowStart(layout.RowOf(_cursor)), BytesPerRow - 1);
        MoveTo(Math.Min(rowEnd, layout.MaxCursor), extend);
    }

    public void PageUp(bool extend = false)
    {
        if (MoveCarets(() => PageUp(extend)))
        {
            return;
        }

        long page = Math.Max(1, _visibleRows - 1);
        long rows = Math.Min(page, Layout.RowOf(_cursor));
        SetTopRow(_topRow - page);
        MoveTo(_cursor - rows * BytesPerRow, extend, keepNibble: true, scroll: false);
        EnsureCursorVisible();
    }

    public void PageDown(bool extend = false)
    {
        if (MoveCarets(() => PageDown(extend)))
        {
            return;
        }

        long page = Math.Max(1, _visibleRows - 1);
        long target = _cursor > long.MaxValue - page * BytesPerRow ? Layout.MaxCursor : _cursor + page * BytesPerRow;
        SetTopRow(_topRow + page);
        MoveTo(Math.Min(target, Layout.MaxCursor), extend, keepNibble: true, scroll: false);
        EnsureCursorVisible();
    }

    /// <summary>
    /// ファイルの先頭へ (Ctrl+Home。VIEW-30)。移動前の位置をジャンプ履歴に記録し、一番上の行を行 0 にする。
    /// <paramref name="extend"/> は Ctrl+Shift+Home (EDIT-02 の仕様 6)。
    /// </summary>
    public void MoveToStart(bool extend = false)
    {
        CollapseCarets();
        RecordJump();
        MoveTo(0, extend, scroll: false);
        SetTopRow(0);
        RaiseChanged();
    }

    /// <summary>ファイルの末尾へ (Ctrl+End)。移動前の位置を記録し、最終行が表示領域の一番下に来るようにする。</summary>
    public void MoveToEnd(bool extend = false)
    {
        CollapseCarets();
        RecordJump();
        MoveTo(Layout.MaxCursor, extend, scroll: false);
        SetTopRow(Layout.MaxTopRow(_visibleRows));
        RaiseChanged();
    }

    public bool CanGoBack => _jumps.CanGoBack;

    public bool CanGoForward => _jumps.CanGoForward;

    /// <summary>ジャンプ履歴を 1 つ戻る (Alt+←。VIEW-31 の仕様 5)。戻れなければ何もしない (仕様 10)。</summary>
    public void GoBack()
    {
        if (_jumps.Back(CurrentPoint()) is { } point)
        {
            JumpTo(point);
        }
    }

    /// <summary>ジャンプ履歴を 1 つ進む (Alt+→)。</summary>
    public void GoForward()
    {
        if (_jumps.Forward(CurrentPoint()) is { } point)
        {
            JumpTo(point);
        }
    }

    /// <summary>「履歴の一覧」(VIEW-31 の仕様 8): 戻る側の新しいものから最大 20 件。</summary>
    public IReadOnlyList<JumpPoint> RecentJumps => _jumps.Recent();

    /// <summary>「履歴の一覧」で選んだ位置へ移る (一覧の <paramref name="index"/> 番目。0 が最新)。</summary>
    public void GoBackTo(int index)
    {
        if (_jumps.BackTo(index, CurrentPoint()) is { } point)
        {
            JumpTo(point);
        }
    }

    /// <summary>
    /// オフセットへのジャンプ (VIEW-29)。移動前の位置をジャンプ履歴に記録し、移動先が表示外なら上から 1/3 の位置に置く
    /// (VIEW-34 の仕様 2)。<paramref name="extendSelection"/> は「選択しながら移動」。
    /// </summary>
    public void GoTo(long offset, bool extendSelection = false)
    {
        CollapseCarets();
        _others = null;
        _rect = null;
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
        CollapseCarets();
        _others = null;
        _rect = null;
        RecordJump();
        _anchor = offset;
        SetSelection(offset, length);
        _cursor = Math.Min(offset, Layout.MaxCursor);
        LowNibble = false;
        ScrollToJumpRange(_cursor, length, JumpPlacement.Center);
        RaiseChanged();
    }

    /// <summary>
    /// 範囲へのジャンプの表示位置 (VIEW-34 の仕様 3): 範囲全体が表示領域に入っていればスクロールしない。入っていなければ、範囲全体が
    /// 入るならそうする (先頭の行を <paramref name="placement"/> の位置に置き、末尾がはみ出すなら末尾が一番下に来るまで戻す)。
    /// 入らない場合は範囲の先頭を <paramref name="placement"/> の規則で置く。
    /// </summary>
    private void ScrollToJumpRange(long start, long length, JumpPlacement placement)
    {
        HexLayout layout = Layout;
        long first = layout.RowOf(start);
        long last = length > 0 ? layout.RowOf(Math.Min(start + length - 1, Math.Max(0, layout.Length - 1))) : first;
        last = Math.Max(first, last);
        long visible = Math.Max(1, _visibleRows);
        if (first >= _topRow && last < _topRow + visible)
        {
            return;
        }

        long top = placement switch
        {
            JumpPlacement.Top => first,
            JumpPlacement.Center => first - visible / 2,
            _ => first - visible / 3,
        };
        if (last - first + 1 <= visible && last >= top + visible)
        {
            top = last - visible + 1;
        }

        SetTopRow(Math.Min(top, first));
    }

    private void ScrollToJumpTarget()
    {
        long row = Layout.RowOf(_cursor);
        if (row < _topRow || row >= _topRow + _visibleRows)
        {
            SetTopRow(JumpPlacement switch
            {
                JumpPlacement.Top => row,
                JumpPlacement.Center => row - _visibleRows / 2,
                _ => row - _visibleRows / 3,
            });
        }
    }

    private JumpPoint CurrentPoint() => new(_cursor, Math.Max(0, Layout.RowStart(_topRow)), ActiveColumn);

    /// <summary>
    /// 移動の直前の位置をジャンプ履歴に記録する (VIEW-31 の仕様 1)。直前の記録と同じ行なら記録しない (仕様 3)。検索・ブックマーク・
    /// インスペクタなど、ほかの機能からの移動の前にも呼ぶ。
    /// </summary>
    public void RecordJump()
    {
        HexLayout layout = Layout;
        _jumps.Record(CurrentPoint(), (a, b) => layout.RowOf(a) == layout.RowOf(b));
    }

    private void JumpTo(JumpPoint point)
    {
        CollapseCarets();
        ClearSelectionAnchor();

        // 移動先がドキュメントの最大値を超えている場合は最大値に移す (仕様 7)。
        HexLayout layout = Layout;
        _cursor = Math.Clamp(point.Offset, 0, layout.MaxCursor);
        LowNibble = false;
        ActiveColumn = VisibleColumn(point.Column);
        SetTopRow(layout.RowOf(Math.Clamp(point.TopOffset, 0, layout.MaxCursor)));
        EnsureCursorVisible();
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>カーソルを動かさずに表示だけを動かす (Ctrl+↑ / Ctrl+↓、ホイール)。</summary>
    public void ScrollRows(long rows)
    {
        if (!ScrollWithinSection(rows))
        {
            SetTopRow(_topRow + rows);
        }

        RaiseChanged();
    }

    /// <summary>スクロールバーなどから一番上の行を直接決める。</summary>
    public void ScrollToRow(long row)
    {
        SetTopRow(row);
        RaiseChanged();
    }

    /// <summary>マウスのクリック (VIEW-25 の仕様 6)。<paramref name="extend"/> は Shift+クリック (EDIT-01 の仕様 4: 両端を含む)。</summary>
    /// <remarks><paramref name="textColumn"/> はテキスト列をクリックしたときのテキスト列の番号 (VIEW-24。-1 なら変えない)。</remarks>
    public void Click(long offset, ActiveColumn column, bool lowNibble, bool extend, int textColumn = -1)
    {
        offset = Math.Clamp(offset, 0, Layout.MaxCursor);
        CollapseCarets();
        _rect = null;
        if (column == ActiveColumn.Text && textColumn >= 0)
        {
            _textColumn = Math.Clamp(textColumn, 0, Math.Max(0, View.TextColumnCount - 1));
        }

        // 元の位置から 1 画面分 (b × V バイト) 以上離れたクリックはジャンプ履歴に記録する (VIEW-31 の仕様 1)。
        if (!extend && Math.Abs((decimal)offset - _cursor) >= (decimal)BytesPerRow * _visibleRows)
        {
            RecordJump();
        }

        ActiveColumn = VisibleColumn(column);

        // 2 バイト以上のセルの表示形式の Hex 列では、カーソルはセルの先頭に置く (VIEW-10 の仕様 6)。
        if (ActiveColumn == ActiveColumn.Hex && View.CellUnit > 1 && offset < Layout.Length)
        {
            offset = CellStartOf(offset);
        }

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
        _rect = null;
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

    /// <summary>
    /// Tab / Shift+Tab で列を切り替える (VIEW-27)。テキスト列が複数あるときは Hex 列 → テキスト列 1 → テキスト列 2 … → Hex 列の順に移る
    /// (VIEW-24 の仕様 4)。<paramref name="backward"/> は Shift+Tab (逆順)。
    /// </summary>
    public void ToggleColumn(bool backward = false)
    {
        // 表示されている列の間だけを移る。表示されている列が 1 つのときは何もしない (VIEW-16 の仕様 5)。
        int texts = View.ShowTextColumn ? Math.Clamp(View.TextColumnCount, 1, ViewSettings.MaxTextColumns) : 0;
        int positions = (View.ShowHexColumn ? 1 : 0) + texts;
        if (positions <= 1)
        {
            return;
        }

        // 位置の番号: Hex 列が 0、テキスト列 i が i + 1 (Hex 列がなければテキスト列 i が i)。
        int hexSlots = View.ShowHexColumn ? 1 : 0;
        int current = ActiveColumn == ActiveColumn.Hex ? 0 : hexSlots + Math.Clamp(TextColumn, 0, texts - 1);
        int next = (current + (backward ? positions - 1 : 1)) % positions;
        if (hexSlots == 1 && next == 0)
        {
            ActiveColumn = ActiveColumn.Hex;
        }
        else
        {
            ActiveColumn = ActiveColumn.Text;
            TextColumn = next - hexSlots;
        }

        LowNibble = false;
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>
    /// 操作中のテキスト列 (VIEW-24。0 始まり)。カーソルが Hex 列にあるときは最後に操作したテキスト列。入力・コピー・ステータスバーの
    /// 文字コードは、このテキスト列の文字コードを使う (VIEW-24 の仕様 5・6)。
    /// </summary>
    public int TextColumn
    {
        get => Math.Clamp(_textColumn, 0, Math.Max(0, View.TextColumnCount - 1));
        private set => _textColumn = value;
    }

    private int _textColumn;

    /// <summary>テキスト列をクリックした (VIEW-24)。その列を操作中のテキスト列にする。</summary>
    public void SetTextColumn(int column)
    {
        int clamped = Math.Clamp(column, 0, Math.Max(0, View.TextColumnCount - 1));
        if (clamped != _textColumn)
        {
            _textColumn = clamped;
            RaiseChanged();
        }
    }

    /// <summary>
    /// すべて選択 (EDIT-03 の仕様 1・2)。0 から末尾までを選択し、アンカーを 0、カーソルを末尾位置に置く。スクロール位置は変えない。
    /// 長さ 0 のドキュメントでは何もしない。
    /// </summary>
    public void SelectAll()
    {
        HexLayout layout = Layout;
        if (layout.Length == 0)
        {
            return;
        }

        // マルチ選択・矩形選択・マルチカーソルは単一の選択に戻してから全体を選ぶ (EDIT-03 の仕様 3)。
        CollapseCarets();
        _others = null;
        _rect = null;
        _anchor = 0;
        SetSelection(0, layout.Length);
        _cursor = layout.MaxCursor;
        LowNibble = false;
        Document.History.BreakCoalescing();
        RaiseChanged();
    }

    /// <summary>選択を解除する (Esc)。マルチカーソル・マルチ選択では主カーソルだけを残す (EDIT-03 の仕様 4)。</summary>
    public void ClearSelection() => CollapseToPrimary();

    /// <summary>
    /// 範囲を選択する (Ctrl+E。EDIT-04)。<paramref name="length"/> は選択するバイト数。カーソルは範囲の末尾 (最後のバイトの次) に置く。
    /// <paramref name="cursorAtStart"/> なら、カーソルを範囲の先頭に、アンカーを末尾に置く (オフセット列のクリック。VIEW-25 の仕様 6)。
    /// </summary>
    public void Select(long start, long length, bool cursorAtStart = false)
    {
        CollapseCarets();
        _others = null;
        _rect = null;
        start = Math.Clamp(start, 0, Layout.Length);
        length = Math.Clamp(length, 0, Layout.Length - start);
        _anchor = cursorAtStart ? start + length : start;
        SetSelection(start, length);
        _cursor = cursorAtStart ? Math.Min(start, Layout.MaxCursor) : Math.Min(start + length, Layout.MaxCursor);
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

        // Hex 以外のセルの表示形式では、セルに直接入力しない (VIEW-10 の仕様 7。データインスペクタを使う)。
        if (View.CellFormat != CellFormat.Hex)
        {
            return EditResult.CellFormatNotEditable;
        }

        if (IsUnallocated(HasSelection ? _selectionStart : _cursor, 1))
        {
            return EditResult.Unallocated;
        }

        if (!_caretLoop && (HasMultipleRanges || HasMultipleCarets))
        {
            // マルチ選択・矩形・マルチカーソルへの入力 (EDIT-07 の仕様 7、EDIT-08 の仕様 4・6)。
            if (!PrepareCaretsForInput(keepSelections: true))
            {
                return EditResult.TooManyCarets;
            }

            return TypeHexDigitAtCarets(digit) ?? ForEachCaret(() => TypeHexDigit(c), "入力", TypingKey);
        }

        long at = HasSelection ? _selectionStart : _cursor;
        if (at >= Document.Length && !Document.CanResize)
        {
            return EditResult.FixedLength;
        }

        using IDisposable? group = BeginTypingGroup();
        PrepareTyping();
        HexLayout layout = Layout;
        bool atEnd = _cursor >= layout.Length;
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

    /// <summary>
    /// Hex 列での確定した文字列の入力 (IME の確定など。EDIT-11 の仕様 5)。1 文字ずつ <see cref="TypeHexDigit"/> で処理し、16 進数字以外は
    /// 無視する。1 桁でも書いたら <see cref="EditResult.Done"/>。
    /// </summary>
    public EditResult TypeHexText(string committed)
    {
        EditResult result = EditResult.Ignored;
        foreach (char c in committed)
        {
            EditResult one = TypeHexDigit(c);
            if (one == EditResult.Done)
            {
                result = EditResult.Done;
            }
            else if (one != EditResult.Ignored)
            {
                return one;
            }
        }

        return result;
    }

    /// <summary>テキスト列での確定した文字列の入力 (EDIT-12)。<see cref="TextEncoding"/> で変換したバイト列を書き込む。</summary>
    public EditResult TypeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EditResult.Ignored;
        }

        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!TextEncoding.TryEncode(text, out byte[] bytes))
        {
            return EditResult.NotEncodable;
        }

        if (!_caretLoop && (HasMultipleRanges || HasMultipleCarets))
        {
            return PrepareCaretsForInput(keepSelections: true) ? ForEachCaret(() => TypeText(text), "入力", TypingKey) : EditResult.TooManyCarets;
        }

        if (IsUnallocated(HasSelection ? _selectionStart : _cursor, bytes.Length))
        {
            return EditResult.Unallocated;
        }

        long at = HasSelection ? _selectionStart : _cursor;
        if (!Document.CanResize && bytes.Length > Document.Length - at)
        {
            return EditResult.FixedLength;
        }

        using (BeginTypingGroup())
        {
            PrepareTyping();
            if (InsertMode)
            {
                Document.Insert(_cursor, bytes, "入力", TypingKey);
            }
            else
            {
                Document.Overwrite(_cursor, bytes, "入力", TypingKey);
            }
        }

        _cursor = Math.Min(_cursor + bytes.Length, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>
    /// テキスト列での Enter (EDIT-12 の仕様 7)。設定「テキスト列での Enter」が CR LF / LF / CR なら、それを今の文字コードで書き込む。
    /// 「何もしない」(既定) なら <see cref="EditResult.Ignored"/>。
    /// </summary>
    public EditResult TypeEnter() => Options.TextEnter switch
    {
        TextEnterAction.CrLf => TypeText("\r\n"),
        TextEnterAction.Lf => TypeText("\n"),
        TextEnterAction.Cr => TypeText("\r"),
        _ => EditResult.Ignored,
    };

    // ---- 削除 (EDIT-13) ----

    /// <summary>
    /// Delete キー。選択範囲、またはカーソル位置の 1 バイトを削除する。設定「上書きモードでは Delete で長さを変えない」がオンで上書きモードなら、
    /// 削除せずに 00 で塗りつぶす (EDIT-13 の仕様 4)。
    /// </summary>
    public EditResult Delete()
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!_caretLoop && HasMultipleCarets && !HasMultipleRanges)
        {
            return ForEachCaret(Delete, "削除", DeleteKey);
        }

        if (!InsertMode && Options.DeleteKeepsLengthInOverwrite)
        {
            return ZeroForDelete();
        }

        if (!_caretLoop && HasMultipleRanges)
        {
            // マルチ選択・矩形のすべての要素を削除する (EDIT-07 の仕様 7、EDIT-17 の仕様 1)。
            return DeleteSelectedRanges();
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
        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>上書きモードの Delete で 00 にする (EDIT-13 の仕様 4)。選択範囲は先頭にカーソルを置き、1 バイトなら次のバイトへ進む。</summary>
    private EditResult ZeroForDelete()
    {
        if (!_caretLoop && HasMultipleRanges)
        {
            return FillSelectedRangesWithZero("削除");
        }

        if (HasSelection)
        {
            long start = _selectionStart;
            Document.OverwritePattern(start, _selectionLength, [0], "削除");
            _cursor = start;
            ClearSelectionAnchor();
        }
        else if (_cursor < Document.Length)
        {
            Document.Overwrite(_cursor, [0], "削除", DeleteKey);
            _cursor = Math.Min(_cursor + 1, Layout.MaxCursor);
        }
        else
        {
            return EditResult.Ignored;
        }

        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return EditResult.Done;
    }

    /// <summary>
    /// 00 で塗りつぶす (EDIT-13 の仕様 5 の InfoBar のボタン)。選択範囲を、選択がなければカーソル位置の 1 バイトを 00 にする。長さは変えない。
    /// </summary>
    public EditResult FillWithZero()
    {
        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (HasMultipleRanges)
        {
            return FillSelectedRangesWithZero("00 で塗りつぶし");
        }

        long start = HasSelection ? _selectionStart : _cursor;
        long length = HasSelection ? _selectionLength : _cursor < Document.Length ? 1 : 0;
        if (length == 0)
        {
            return EditResult.Ignored;
        }

        Document.OverwritePattern(start, length, [0], "00 で塗りつぶし");
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

        if (!_caretLoop && HasMultipleCarets && !HasMultipleRanges)
        {
            return ForEachCaret(Backspace, "削除", BackspaceKey);
        }

        if (!_caretLoop && HasMultipleRanges)
        {
            return Document.CanResize ? DeleteSelectedRanges() : EditResult.FixedLength;
        }

        if (HasSelection)
        {
            return Document.CanResize ? DeleteSelection() : EditResult.FixedLength;
        }

        if (!InsertMode)
        {
            if (Options.BackspaceZeroesInOverwrite)
            {
                // 設定「直前のバイトを 00 にして戻る」(EDIT-13 の仕様 3)。Hex 列の下位ニブルなら、入力中のそのバイトを 00 にする。
                long target = ActiveColumn == ActiveColumn.Hex && LowNibble ? _cursor : _cursor - 1;
                if (target < 0 || target >= Document.Length)
                {
                    return EditResult.Ignored;
                }

                Document.Overwrite(target, [0], "削除", BackspaceKey);
                _cursor = target;
                LowNibble = false;
            }
            else if (ActiveColumn == ActiveColumn.Hex && LowNibble)
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
            Document.Delete(_cursor, 1, "削除", BackspaceKey);
            LowNibble = false;
        }
        else if (_cursor > 0)
        {
            Document.Delete(_cursor - 1, 1, "削除", BackspaceKey);
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
    /// 長さを変えられないドキュメントで <paramref name="length"/> バイトを上書き貼り付けしたとき、末尾を越えるため書かれない量
    /// (ENG-07 の仕様 5 の確認ダイアログの N)。長さを変えられるドキュメントでは 0。
    /// </summary>
    public long PasteOverflow(long length)
    {
        if (Document.CanResize)
        {
            return 0;
        }

        long at = HasSelection ? _selectionStart : _cursor;
        return Math.Max(0, length - Math.Max(0, Document.Length - at));
    }

    /// <summary>
    /// 貼り付け。<paramref name="overwrite"/> は上書き貼り付け (Ctrl+B)。挿入モードでは選択範囲を置き換えるかカーソル位置に挿入し、
    /// 上書きモードではカーソル (選択範囲があればその先頭) から上書きする。固定長ドキュメントでは常に上書きし、末尾を越える場合は
    /// <paramref name="allowTruncate"/> が偽なら何もせず <see cref="EditResult.NeedsTruncateConfirmation"/> を返す。
    /// 選択範囲の削除と貼り付けは 1 つの編集グループにする (EDIT-19 の仕様 6)。貼り付けた範囲を選択する (EDIT-23 の仕様 7)。
    /// </summary>
    public EditResult Paste(DocumentSnapshot source, long sourceOffset, long length, bool overwrite, bool allowTruncate = false) =>
        PasteCore(length, overwrite, allowTruncate, (insert, at, n) =>
        {
            if (insert)
            {
                Document.InsertFrom(at, source, sourceOffset, n);
            }
            else
            {
                Document.OverwriteFrom(at, source, sourceOffset, n);
            }
        });

    /// <summary>アプリ内クリップボードの範囲の参照からの貼り付け (EDIT-24)。データをコピーしない。</summary>
    public EditResult Paste(SnapshotRange range, bool overwrite, bool allowTruncate = false) =>
        PasteCore(range.Length, overwrite, allowTruncate, (insert, at, n) =>
        {
            if (insert)
            {
                Document.InsertFrom(at, range, 0, n);
            }
            else
            {
                Document.OverwriteFrom(at, range, 0, n);
            }
        });

    /// <summary>バイト列の貼り付け (システムのクリップボードから)。</summary>
    public EditResult Paste(byte[] data, bool overwrite, bool allowTruncate = false) =>
        PasteCore(data.Length, overwrite, allowTruncate, (insert, at, n) =>
        {
            if (insert)
            {
                Document.Insert(at, data, "貼り付け");
            }
            else
            {
                Document.Overwrite(at, data.AsSpan(0, (int)n), "上書き貼り付け");
            }
        });

    /// <summary>
    /// テキストだけがクリップボードにある場合の貼り付け (EDIT-23 の仕様 2)。テキスト列では文字コードで変換して貼る。Hex 列では
    /// Hex バイト列として解釈できればそのバイト列を貼り、できなければテキストとして文字コードで変換して貼って
    /// <see cref="EditResult.PastedAsText"/> を返す (UI は「元に戻す」付きの InfoBar を出す)。表せない文字があれば何もしない。
    /// </summary>
    public EditResult PasteText(string text, bool overwrite, bool allowTruncate = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return EditResult.Ignored;
        }

        if (ActiveColumn == ActiveColumn.Hex && HexText.TryParse(text) is { } hex)
        {
            return Paste(hex, overwrite, allowTruncate);
        }

        if (!TextEncoding.TryEncode(text, out byte[] bytes))
        {
            return EditResult.NotEncodable;
        }

        EditResult result = Paste(bytes, overwrite, allowTruncate);
        return ActiveColumn == ActiveColumn.Hex && result == EditResult.Done ? EditResult.PastedAsText : result;
    }

    /// <summary>
    /// コピーするテキスト形式 (EDIT-22 の仕様 2): Hex 列なら `DE AD BE EF` の Hex 文字列、テキスト列なら現在の文字コードで
    /// 解釈した文字列 (解釈できないバイトと NUL は U+FFFD)。
    /// </summary>
    public string FormatForClipboard(ReadOnlySpan<byte> bytes) =>
        ActiveColumn == ActiveColumn.Hex ? Options.HexCopy.Format(bytes) : TextEncoding.Decode(bytes);

    private EditResult PasteCore(long length, bool overwrite, bool allowTruncate, Action<bool, long, long> write)
    {
        if (length <= 0)
        {
            return EditResult.Ignored;
        }

        if (!CanEdit())
        {
            return EditResult.NotEditable;
        }

        if (!_caretLoop && (HasMultipleRanges || HasMultipleCarets))
        {
            // 各要素・各カーソルに同じ内容を貼る (EDIT-07 の仕様 7、EDIT-08 の仕様 4)。
            return PrepareCaretsForInput(keepSelections: true)
                ? ForEachCaret(() => PasteCore(length, overwrite, allowTruncate, write), overwrite || !InsertMode ? "上書き貼り付け" : "貼り付け")
                : EditResult.TooManyCarets;
        }

        long at = HasSelection ? _selectionStart : _cursor;
        bool insert = InsertMode && !overwrite && Document.CanResize;
        EditResult result = EditResult.Done;
        if (overwrite && HasSelection && Options.FitOverwritePasteToSelection)
        {
            // 設定「上書き貼り付けで選択範囲の長さに合わせる」(EDIT-23 の仕様 6)。内容の方が短ければ内容の長さだけ書く。
            length = Math.Min(length, _selectionLength);
        }

        if (!insert)
        {
            long overflow = PasteOverflow(length);
            if (overflow > 0)
            {
                if (!allowTruncate)
                {
                    return EditResult.NeedsTruncateConfirmation;
                }

                length -= overflow;
                result = EditResult.Truncated;
                if (length <= 0)
                {
                    return result;
                }
            }
        }

        using (Document.BeginGroup(insert ? "貼り付け" : "上書き貼り付け"))
        {
            if (insert && HasSelection)
            {
                Document.Delete(_selectionStart, _selectionLength, "削除");
            }

            write(insert, at, length);
        }

        // 貼り付けた範囲を選択する。設定でオフなら、カーソルを貼り付けた範囲の直後に置く (EDIT-23 の仕様 7)。
        if (Options.SelectPasted)
        {
            _anchor = at;
            SetSelection(at, length);
        }
        else
        {
            ClearSelectionAnchor();
        }

        _cursor = Math.Min(at + length, Layout.MaxCursor);
        LowNibble = false;
        EnsureCursorVisible();
        RaiseChanged();
        return result;
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

        if (HasMultipleRanges)
        {
            return DeleteSelectedRanges("切り取り");
        }

        return HasSelection ? DeleteSelection() : EditResult.Ignored;
    }

    /// <summary>元に戻す。読み取り専用・処理中は何もしない (EDIT-16 の仕様 2)。</summary>
    public void Undo()
    {
        if (Document.History.CanUndo && CanEdit())
        {
            Document.Undo();
        }
    }

    public void Redo()
    {
        if (Document.History.CanRedo && CanEdit())
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

    /// <summary>選択範囲がある状態での入力の準備 (EDIT-11 の仕様 3)。挿入モードでは選択範囲を削除する。</summary>
    private void PrepareTyping()
    {
        if (!HasSelection)
        {
            return;
        }

        if (InsertMode)
        {
            Document.Delete(_selectionStart, _selectionLength, "削除");
        }
        else if (Options.ZeroSelectionBeforeTyping)
        {
            // 設定「選択範囲を 00 にしてから上書き」(EDIT-11 の仕様 3)。
            Document.OverwritePattern(_selectionStart, _selectionLength, [0], "入力");
        }

        _cursor = _selectionStart;
        LowNibble = false;
        ClearSelectionAnchor();
    }

    /// <summary>
    /// 挿入モードで選択範囲を置き換える入力は、削除と入力を 1 つの編集グループにし、続く入力もまとめる (EDIT-19 の仕様 6)。
    /// 選択範囲がなければ null (通常の入力のまとめに任せる)。
    /// </summary>
    private IDisposable? BeginTypingGroup() =>
        HasSelection && (InsertMode || Options.ZeroSelectionBeforeTyping) ? Document.BeginGroup("入力", TypingKey) : null;

    private bool CanEdit() => !Document.IsReadOnly && !Document.IsEditLocked;

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

    /// <summary>
    /// カーソルの行が表示領域の外なら、入る最小のスクロールをする (VIEW-34 の仕様 1)。「カーソルの上下に残す行数」の行を
    /// カーソルの上下に残す (表示行数が足りない場合は、上下に同じだけ残せる行数まで減らす)。
    /// </summary>
    private void EnsureCursorVisible()
    {
        long row = Layout.RowOf(_cursor);
        int margin = Math.Min(_cursorMargin, (_visibleRows - 1) / 2);
        if (PageSection() is { } section && !SameSection(section, row, _topRow))
        {
            // ページ単位で表示: カーソルが別の区切りに移ったら、その区切りを表示する (VIEW-33 の仕様 4)。
            SetTopRow(row < _topRow ? row - margin : row - _visibleRows + 1 + margin, row);
            return;
        }

        if (row < _topRow + margin)
        {
            SetTopRow(row - margin, row);
        }
        else if (row >= _topRow + _visibleRows - margin)
        {
            SetTopRow(row - _visibleRows + 1 + margin, row);
        }
    }

    /// <summary>
    /// 一番上の行を決める。ページ単位で表示 (VIEW-33 の仕様 4) のときは、<paramref name="anchorRow"/> (省略時は <paramref name="row"/>) の
    /// ある区切りの中に収める。
    /// </summary>
    private void SetTopRow(long row, long? anchorRow = null)
    {
        long top = Math.Clamp(row, 0, Layout.MaxTopRow(_visibleRows));
        if (PageSection() is { } section)
        {
            (long first, long last) = SectionRows(section, anchorRow ?? top);
            top = Math.Clamp(top, first, Math.Max(first, last - _visibleRows + 1));
        }

        _topRow = top;
    }

    private void SetSelection(long start, long length)
    {
        _selectionStart = start;
        _selectionLength = length;
    }

    private void ClearSelectionAnchor()
    {
        _others = null;
        _rect = null;
        _anchor = -1;
        _selectionLength = 0;
        _selectionStart = _cursor;
    }

    private void OnDocumentChanged(DocumentChangedEventArgs e)
    {
        ShiftSelectionsForEdit(e);
        _jumps.Adjust(e);
        OnDocumentChangedForView(e);
        if (FollowsEdits)
        {
            // 操作中でないビュー: 表示中のデータが動かないようにずらす。Undo の範囲の選択はしない (VIEW-37 の仕様 4、VIEW-38 の仕様 5)。
            FollowEdit(e);
            _knownLength = Document.Length;
            _cursor = Math.Min(_cursor, Layout.MaxCursor);
            SetTopRow(_topRow);
            RaiseChanged();
            return;
        }

        _knownLength = Document.Length;
        if (e.Selection is { } range)
        {
            // 元に戻す・やり直しの後は、その編集グループの範囲を選択して見える位置に出す (EDIT-19 の仕様 10)。
            SelectEditedRange(range.Offset, range.Length);
            return;
        }

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

    private void SelectEditedRange(long offset, long length)
    {
        CollapseCarets();
        _others = null;
        _rect = null;
        HexLayout layout = Layout;
        offset = Math.Clamp(offset, 0, layout.MaxCursor);
        length = Math.Clamp(length, 0, layout.Length - offset);
        if (length > 0)
        {
            _anchor = offset;
            SetSelection(offset, length);
        }
        else
        {
            _cursor = offset;
            ClearSelectionAnchor();
        }

        Document.History.BreakCoalescing();
        _cursor = length > 0 ? Math.Min(offset + length, layout.MaxCursor) : offset;
        LowNibble = false;
        SetTopRow(_topRow);
        long first = layout.RowOf(offset);
        long last = layout.RowOf(Math.Max(offset, offset + length - 1));
        if (first < _topRow)
        {
            SetTopRow(first);
        }
        else if (last >= _topRow + _visibleRows)
        {
            // 範囲が画面に収まれば最後の行が一番下に来るように、収まらなければ先頭の行を一番上に出す。
            SetTopRow(last - first < _visibleRows ? last - _visibleRows + 1 : first);
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_caretLoop)
        {
            // マルチカーソルの処理の間はまとめて 1 回にする (EDIT-08 の「巨大ファイル・長時間処理」)。
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>long を越えない足し算 (2^63 − 1 の近くのカーソル移動)。</summary>
    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

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
