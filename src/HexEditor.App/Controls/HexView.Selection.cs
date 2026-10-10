using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// マルチ選択 (EDIT-07)・矩形選択 (EDIT-06)・マルチカーソル (EDIT-08) のマウスとキーの操作と、選択範囲のドラッグ &amp; ドロップ (EDIT-18)。
/// </summary>
public sealed partial class HexView
{
    /// <summary>左ボタンを押したときの操作の種類 (ボタンを離す・ドラッグを始めるまで決まらないものを含む)。</summary>
    private enum PressMode
    {
        Normal,

        /// <summary>Alt で押した: ドラッグすれば矩形選択、動かさずに離せばカーソルの追加・削除 (Alt+クリック)。</summary>
        AltPending,

        /// <summary>Ctrl で選択範囲の中を押した: ドラッグすれば要素の追加、動かさずに離せばその要素を取り除く。</summary>
        CtrlPending,

        /// <summary>Ctrl+ドラッグで要素を追加している。</summary>
        AddSelection,

        /// <summary>Alt+ドラッグで矩形を選んでいる。</summary>
        Rectangle,

        /// <summary>選択範囲の上で押した: ドラッグすれば選択範囲のドラッグ、動かさずに離せばクリック。</summary>
        DropPending,

        /// <summary>選択範囲をドラッグしている (EDIT-18)。</summary>
        SelectionDrag,
    }

    private PressMode _pressMode;
    private HitResult _pressHit;
    private long _dropTarget = -1;
    private bool _dropCtrl;
    private bool _dropShift;
    private bool _dropAllowed;

    /// <summary>選択の操作が上限などで行われなかった (UI は InfoBar で知らせる)。</summary>
    public event EventHandler<SelectionResult>? SelectionRejected;

    /// <summary>マルチカーソルへの入力で、一部のカーソルに入力できなかった (引数はその数。EDIT-08 の「エラー」)。</summary>
    public event EventHandler<int>? CaretInputFailed;

    /// <summary>設定「選択範囲のドラッグ &amp; ドロップを有効にする」(EDIT-18。既定オン)。</summary>
    public bool SelectionDragDropEnabled { get; set; } = true;

    /// <summary>選択範囲をドラッグしているときのドロップの効果 ("move" / "copy" / "overwrite" / "none")。ドラッグしていなければ null (テスト用)。</summary>
    internal string? DropEffect => _pressMode != PressMode.SelectionDrag ? null
        : !_dropAllowed ? "none" : DropKind() switch
        {
            SelectionDropKind.Copy => "copy",
            SelectionDropKind.Overwrite => "overwrite",
            _ => "move",
        };

    private void InitializeSelectionVisuals() => SetHighlightSource("selection", SelectionHighlights);

    /// <summary>
    /// 選択に伴う印: マルチ選択の主要素の太い枠 (EDIT-07 の「画面」。色だけで区別しない)、主カーソル以外の細いカーソル (EDIT-08 の「画面」)、
    /// 選択範囲のドラッグの挿入位置の縦線 (EDIT-18 の「画面」)。
    /// </summary>
    private IEnumerable<HexHighlight> SelectionHighlights(long start, long end)
    {
        if (_editor is null || _palette is null)
        {
            yield break;
        }

        Brush border = _palette.Caret;
        if (_editor.SelectionKind == SelectionKind.Multiple && _editor.PrimaryRange is { } primary && primary.Start < end && primary.End > start)
        {
            yield return new HexHighlight(primary.Start, primary.Length, HexHighlightLayer.Selection, null, border, Tag: "primary", Thickness: 2);
        }

        if (_editor.HasMultipleCarets)
        {
            foreach (Caret caret in _editor.Carets)
            {
                if (caret.Offset != _editor.Cursor && caret.Offset >= start && caret.Offset < end)
                {
                    yield return new HexHighlight(caret.Offset, 0, HexHighlightLayer.Selection, null, border, Tag: "caret");
                }
            }
        }

        if (_pressMode == PressMode.SelectionDrag && _dropTarget >= start && _dropTarget < end)
        {
            yield return new HexHighlight(_dropTarget, 0, HexHighlightLayer.Selection, null, border, Tag: "drop");
        }
    }

    /// <summary>修飾キー付きで左ボタンを押した。処理したら true (通常のクリックの処理をしない)。</summary>
    private bool PressWithModifiers(HitResult hit, int count, bool shift, bool ctrl, bool alt)
    {
        EditorState editor = _editor!;
        if (alt && !ctrl && shift)
        {
            // Alt+Shift+クリック: アンカーから矩形を広げる (EDIT-06 の仕様 2)。
            editor.RectangleTo(hit.Offset);
            _pressMode = PressMode.Rectangle;
            return true;
        }

        if (alt && !ctrl)
        {
            _pressMode = PressMode.AltPending;
            return true;
        }

        if (ctrl && !alt && shift)
        {
            // Ctrl+Shift+クリック: カーソルからクリックした位置までを要素として加える (00-overview.md 8.3)。
            editor.BeginAddSelection(hit.Offset, hit.Column, fromCursor: true);
            editor.DragTo(hit.Offset);
            _pressMode = PressMode.AddSelection;
            return true;
        }

        if (ctrl && !alt && count == 1)
        {
            if (editor.IsSelected(hit.Offset))
            {
                _pressMode = PressMode.CtrlPending;
            }
            else
            {
                editor.BeginAddSelection(hit.Offset, hit.Column);
                _pressMode = PressMode.AddSelection;
            }

            return true;
        }

        if (!ctrl && !alt && !shift && count == 1 && SelectionDragDropEnabled && editor.SelectionKind == SelectionKind.Single
            && editor.IsSelected(hit.Offset) && hit.Offset < editor.Layout.Length)
        {
            // 選択範囲の上で押した: ドラッグを始めるまで選択を保つ (EDIT-18 の仕様 1)。
            _pressMode = PressMode.DropPending;
            return true;
        }

        return false;
    }

    /// <summary>ドラッグを始めた (4 px 動いた)。押したときの種類からドラッグの種類を決める。</summary>
    private void StartModeDrag()
    {
        EditorState editor = _editor!;
        switch (_pressMode)
        {
            case PressMode.AltPending:
                editor.BeginRectangle(_pressHit.Offset, _pressHit.Column);
                _pressMode = PressMode.Rectangle;
                break;
            case PressMode.CtrlPending:
                editor.BeginAddSelection(_pressHit.Offset, _pressHit.Column);
                _pressMode = PressMode.AddSelection;
                break;
            case PressMode.DropPending:
                _pressMode = PressMode.SelectionDrag;
                _dropTarget = -1;
                break;
        }
    }

    /// <summary>ドラッグ中のポインタの移動。種類ごとの処理をしたら true。</summary>
    private bool DragToPointerWithMode(HitResult hit)
    {
        EditorState editor = _editor!;
        switch (_pressMode)
        {
            case PressMode.Rectangle:
                editor.RectangleTo(hit.Offset);
                return true;
            case PressMode.SelectionDrag:
                UpdateDropTarget(hit.Offset);
                return true;
            default:
                return false;
        }
    }

    /// <summary>ドロップの位置と効果を更新し、ポインタの形を変える (EDIT-18 の「画面」・「エラー」)。</summary>
    private void UpdateDropTarget(long offset)
    {
        _dropTarget = offset;
        _dropCtrl = IsKeyDown(VirtualKey.Control) || _dropCtrl && _injectedModifiers;
        _dropShift = IsKeyDown(VirtualKey.Shift) || _dropShift && _injectedModifiers;
        _dropAllowed = _editor!.CanDropSelectionAt(offset, DropKind());
        ProtectedCursor = InputSystemCursor.Create(!_dropAllowed ? InputSystemCursorShape.UniversalNo
            : DropKind() == SelectionDropKind.Copy ? InputSystemCursorShape.Hand : InputSystemCursorShape.SizeAll);
        QueueRender();
    }

    private SelectionDropKind DropKind() =>
        _dropCtrl ? SelectionDropKind.Copy
        : _dropShift && _editor is { InsertMode: false } ? SelectionDropKind.Overwrite
        : SelectionDropKind.Move;

    /// <summary>ボタンを離した。種類ごとの確定をする (Alt+クリック、Ctrl+クリック、要素の追加の確定、ドロップ)。</summary>
    private void ReleaseWithMode(bool ctrl, bool shift)
    {
        if (_editor is not { } editor || !_pressed)
        {
            return;
        }

        PressMode mode = _pressMode;
        _pressMode = PressMode.Normal;
        switch (mode)
        {
            case PressMode.AltPending:
                // Alt+クリック: カーソルを追加する。既にカーソルがある位置なら取り除く (EDIT-08 の仕様 1)。
                ReportSelection(editor.ToggleCaretAt(_pressHit.Offset, _pressHit.Column));
                break;
            case PressMode.CtrlPending:
                // 選択されている範囲の中での Ctrl+クリックは、その要素を取り除く (EDIT-07 の仕様 2)。
                editor.BeginAddSelection(_pressHit.Offset, _pressHit.Column);
                editor.RemoveSelectionAt(_pressHit.Offset);
                editor.CommitSelection();
                break;
            case PressMode.AddSelection:
                ReportSelection(editor.CommitSelection());
                break;
            case PressMode.DropPending:
                // 動かさずに離した: 通常のクリック (選択を解除してカーソルを置く)。
                editor.Click(_pressHit.Offset, _pressHit.Column, _pressHit.LowNibble, false);
                break;
            case PressMode.SelectionDrag:
                _dropCtrl |= ctrl;
                _dropShift |= shift;
                if (_dropTarget >= 0 && editor.CanDropSelectionAt(_dropTarget, DropKind()))
                {
                    Report(editor.DropSelection(_dropTarget, DropKind()));
                }

                ResetDrop();
                break;
        }
    }

    /// <summary>ドラッグの中止 (ポインタを失った、Esc。EDIT-18 の仕様 8)。</summary>
    private void CancelModeDrag()
    {
        if (_pressMode == PressMode.SelectionDrag)
        {
            ResetDrop();
        }

        if (_pressMode == PressMode.AddSelection && _editor is { } editor)
        {
            editor.CommitSelection();
        }

        _pressMode = PressMode.Normal;
    }

    private void ResetDrop()
    {
        _dropTarget = -1;
        _dropCtrl = _dropShift = false;
        _dropAllowed = false;
        _injectedModifiers = false;
        ProtectedCursor = null;
        QueueRender();
    }

    // テストのポインタの注入で修飾キーを指定した (実際のキーの状態を読まない)。
    private bool _injectedModifiers;

    /// <summary>テスト用: ドラッグ中の修飾キーを指定する (実際のキーを押さずに Ctrl / Shift のドロップを確かめる)。</summary>
    internal void InjectDropModifiers(bool ctrl, bool shift)
    {
        _injectedModifiers = true;
        _dropCtrl = ctrl;
        _dropShift = shift;
        if (_dropTarget >= 0)
        {
            UpdateDropTarget(_dropTarget);
        }
    }

    private void ReportSelection(SelectionResult result)
    {
        if (result is SelectionResult.TooManyElements or SelectionResult.TooManyCarets or SelectionResult.Truncated)
        {
            SelectionRejected?.Invoke(this, result);
        }
    }

    /// <summary>
    /// 選択のキー (00-overview.md 8.3): Alt+Shift+矢印 (矩形選択)、Ctrl+Alt+↑ / ↓ (カーソルを上 / 下の行に追加)、選択範囲のドラッグ中の Esc。
    /// </summary>
    private bool HandleSelectionKey(VirtualKey key, bool shift, bool ctrl, bool alt)
    {
        EditorState editor = _editor!;
        if (key == VirtualKey.Escape && _pressMode == PressMode.SelectionDrag)
        {
            // ドラッグ中の Esc は中止 (EDIT-18 の仕様 8)。
            ResetDrop();
            _pressMode = PressMode.Normal;
            _pressed = false;
            SetDragging(false);
            return true;
        }

        if (_rectangleKeys && (!shift || ctrl || editor.SelectionKind != SelectionKind.Rectangle) && key is not (VirtualKey.Shift or VirtualKey.LeftShift
            or VirtualKey.RightShift))
        {
            _rectangleKeys = false;
        }

        if ((alt || _rectangleKeys) && shift && !ctrl)
        {
            (long rows, int columns) = key switch
            {
                VirtualKey.Left => (0L, -1),
                VirtualKey.Right => (0L, 1),
                VirtualKey.Up => (-1L, 0),
                VirtualKey.Down => (1L, 0),
                _ => (0L, 0),
            };
            if (rows != 0 || columns != 0)
            {
                editor.ExtendRectangle(rows, columns);
                return true;
            }
        }

        if (ctrl && alt && !shift && key is VirtualKey.Up or VirtualKey.Down)
        {
            SelectionResult result = key == VirtualKey.Up ? editor.AddCaretAbove() : editor.AddCaretBelow();
            ReportSelection(result);
            return true;
        }

        return false;
    }

    // 「矩形選択を開始」の後: Shift+矢印でも矩形を広げる (Esc・Shift のないキーで終わる)。
    private bool _rectangleKeys;

    /// <summary>「矩形選択を開始」(EDIT-06 の「呼び出し」): 続く Shift+矢印で矩形を広げる。</summary>
    public void BeginRectangleKeys() => _rectangleKeys = true;

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
