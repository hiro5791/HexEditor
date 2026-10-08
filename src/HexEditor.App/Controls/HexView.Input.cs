using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>Hex ビューのマウス・タッチ・ホイール・キーボード・右クリックメニュー。</summary>
public sealed partial class HexView
{
    /// <summary>ドラッグとみなす移動量 (EDIT-01 の仕様 3。表示倍率 100% 換算の 4 px = 4 epx)。</summary>
    private const double DragThreshold = 4;

    /// <summary>テキスト列のダブルクリックで選ぶ連続の片側の上限 (EDIT-01 の仕様 5)。</summary>
    private const int WordScanLimit = 32 * 1024;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer _autoScrollTimer = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _inertiaTimer = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _hoverTimer = null!;
    private MenuFlyout? _contextMenu;
    private ToolTip? _scrollToolTip;
    private ToolTip? _cellToolTip;

    // マウスの左ボタンの状態
    private bool _pressed;
    private bool _dragging;

    /// <summary>マウスのドラッグの始まりと終わり。ビューの状態にも知らせる (ステータスバーの更新を 30 fps に抑える。VIEW-40 の仕様 4)。</summary>
    private void SetDragging(bool dragging)
    {
        _dragging = dragging;
        if (_editor is not null && _editor.PointerDragging != dragging)
        {
            _editor.PointerDragging = dragging;
        }
    }
    private bool _rowDrag;
    private long _rowDragAnchor;
    private Point _pressPoint;
    private Point _lastPointer;
    private uint _pressPointerId;
    private int _clickCount;
    private long _lastClickTime;
    private Point _lastClickPoint;

    // ドラッグ中の自動スクロール (EDIT-01 の仕様 9)
    private long _autoScrollLast;
    private long _autoScrollFastSince = -1;
    private double _autoScrollAccumulated;

    // タッチのスクロール (VIEW-28 の仕様 2)
    private bool _touchActive;
    private bool _touchMoved;
    private Point _touchStart;
    private Point _touchLast;
    private long _touchLastTime;
    private Point _velocity;

    // ホイール・ピクセル単位のスクロール
    private bool _scrollingByPixels;

    // 読み取れないセルのツールチップ (VIEW-03 の仕様 6)
    private long _hoverOffset = -1;

    private void InitializeInput()
    {
        _autoScrollTimer = _uiQueue.CreateTimer();
        _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();
        _inertiaTimer = _uiQueue.CreateTimer();
        _inertiaTimer.Interval = TimeSpan.FromMilliseconds(16);
        _inertiaTimer.Tick += (_, _) => InertiaTick();
        _hoverTimer = _uiQueue.CreateTimer();
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(500);
        _hoverTimer.Tick += (_, _) => ShowCellToolTip();
        PreviewKeyDown += OnPreviewKeyDown;
        CharacterReceived += OnCharacterReceived;
    }

    private void StopPointerTimers()
    {
        _autoScrollTimer.Stop();
        _inertiaTimer.Stop();
        _hoverTimer.Stop();
        HideCellToolTip();
    }

    // ---- 当たり判定 ----

    /// <summary>ポインタの下の領域。</summary>
    internal enum HitRegion
    {
        Offset,
        Hex,
        Text,

        /// <summary>最終行より下の空白。</summary>
        BelowEnd,
    }

    internal readonly record struct HitResult(long Offset, ActiveColumn Column, bool LowNibble, HitRegion Region, long Row);

    /// <summary>Surface の座標から、セルを求める (VIEW-25 の仕様 6)。y が表示領域の外でも、その延長の行を返す。</summary>
    internal bool TryHitTest(Point point, out HitResult hit)
    {
        hit = default;
        if (_editor is null)
        {
            return false;
        }

        HexLayout layout = _editor.Layout;
        RowColumns columns = Columns;
        long row = _editor.TopRow + (long)Math.Floor((point.Y + _subRowOffset) / _rowHeight);
        row = Math.Max(0, row);
        if (row >= layout.TotalRows)
        {
            hit = new HitResult(layout.MaxCursor, _editor.ActiveColumn, false, HitRegion.BelowEnd, layout.TotalRows - 1);
            return true;
        }

        int b = layout.BytesPerRow;
        long rowStart = layout.RowStart(row);

        // 行の先頭のずれ (VIEW-20) で最初の行の先頭の空白のセルは、オフセット 0 として扱う。
        long firstInRow = Math.Max(0, rowStart);
        if (_showOffset && point.X < ContentLeft - _cellWidth - OffsetColumnShift)
        {
            hit = new HitResult(Math.Min(firstInRow, layout.MaxCursor), ActiveColumn.Hex, false, HitRegion.Offset, row);
            return true;
        }

        int ch = (int)Math.Floor((point.X - ContentLeft + _horizontalOffset) / _cellWidth);
        long offset;
        ActiveColumn column = ActiveColumn.Hex;
        bool low = false;
        if (!columns.ShowHex || (columns.ShowText && ch >= columns.TextIndex(0) - 1))
        {
            column = ActiveColumn.Text;
            int rel = ch - columns.TextIndex(0);
            offset = Math.Max(firstInRow, rowStart + Math.Clamp(rel, 0, b - 1));

            // 最終行の最後のバイトの右側は末尾位置 (EDIT-01 の仕様 11)。
            if (rowStart + Math.Max(0, rel) >= layout.Length)
            {
                offset = layout.MaxCursor;
            }
        }
        else
        {
            int c = 0;
            while (c < b - 1 && ch >= columns.HexIndex(c + 1) - 1)
            {
                c++;
            }

            low = ch - columns.HexIndex(c) >= 1;
            offset = rowStart + c;
            if (offset < firstInRow)
            {
                offset = firstInRow;
                low = false;
            }
        }

        offset = Math.Min(offset, layout.MaxCursor);
        if (offset >= layout.Length)
        {
            low = false;
        }

        hit = new HitResult(offset, column, low, column == ActiveColumn.Hex ? HitRegion.Hex : HitRegion.Text, row);
        return true;
    }

    // ---- マウス (VIEW-25 の仕様 6、EDIT-01) ----

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        _inertiaTimer.Stop();
        HideCellToolTip();
        if (_editor is null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(Surface);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            TouchPressed(point.Position);
            Surface.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        if (point.Properties.IsXButton1Pressed || point.Properties.IsXButton2Pressed)
        {
            // マウスの「戻る」「進む」ボタン (VIEW-31)。
            MouseHistoryButton(point.Properties.IsXButton1Pressed);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Handled にしない (右クリックのジェスチャから ContextRequested でメニューを開く)。
            RightButtonPressed(point.Position);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (LeftButtonPressed(point.Position, shift, e.Pointer.PointerId))
        {
            Surface.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    /// <summary>タッチの開始。タッチはスクロールに使い、動かさずに離したらタップ (クリック) とする。</summary>
    private void TouchPressed(Point position)
    {
        _touchActive = true;
        _touchMoved = false;
        _touchStart = _touchLast = position;
        _touchLastTime = Stopwatch.GetTimestamp();
        _velocity = default;
    }

    /// <summary>右ボタンを押した。選択範囲の中なら選択を保ち、外ならその位置にカーソルを移して選択を解除する (EDIT-01 の仕様 10)。</summary>
    private void RightButtonPressed(Point position)
    {
        if (_editor is null || !TryHitTest(position, out HitResult hit))
        {
            return;
        }

        bool inside = _editor.HasSelection && hit.Offset >= _editor.SelectionStart
            && hit.Offset < _editor.SelectionStart + _editor.SelectionLength;
        if (!inside)
        {
            _editor.Click(hit.Offset, hit.Column, hit.LowNibble, false);
        }
    }

    /// <summary>左ボタンを押した (クリック・Shift+クリック・ダブルクリック・トリプルクリック)。ポインタを捕まえるなら true。</summary>
    private bool LeftButtonPressed(Point position, bool shift, uint pointerId)
    {
        if (_editor is null || !TryHitTest(position, out HitResult hit))
        {
            return false;
        }

        int count = NextClickCount(position);
        _pressed = true;
        SetDragging(false);
        _rowDrag = false;
        _pressPoint = _lastPointer = position;
        _pressPointerId = pointerId;
        if (shift)
        {
            // アンカーを変えずにクリックした位置までを選ぶ (EDIT-01 の仕様 4)。
            _editor.Click(hit.Offset, hit.Column, hit.LowNibble, true);
        }
        else if (hit.Region == HitRegion.Offset)
        {
            // オフセット列のクリックは行全体、ドラッグは行単位 (EDIT-01 の仕様 7)。カーソルは行の先頭に置く (VIEW-25 の仕様 6)。
            _rowDrag = true;
            _rowDragAnchor = hit.Row;
            SelectRows(hit.Row, hit.Row, cursorAtStart: true);
        }
        else if (count == 2)
        {
            DoubleClick(hit);
            _pressed = false;
        }
        else if (count == 3)
        {
            // トリプルクリックは行全体 (EDIT-01 の仕様 6)。
            SelectRows(hit.Row, hit.Row, hit.Column);
            _pressed = false;
        }
        else
        {
            _editor.Click(hit.Offset, hit.Column, hit.LowNibble, false);
        }

        return true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(Surface);
        if (_touchActive)
        {
            TouchMoved(point.Position);
            e.Handled = true;
            return;
        }

        if (!_pressed)
        {
            UpdateHover(point.Position);
            return;
        }

        if (_editor is null || e.Pointer.PointerId != _pressPointerId)
        {
            return;
        }

        if (DragMoved(point.Position))
        {
            e.Handled = true;
        }
    }

    /// <summary>左ボタンを押したままの移動。ドラッグとして処理したら true。</summary>
    private bool DragMoved(Point position)
    {
        _lastPointer = position;
        if (!_dragging)
        {
            double dx = position.X - _pressPoint.X;
            double dy = position.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < DragThreshold * DragThreshold)
            {
                // 4 px 未満の動きはクリックのまま (EDIT-01 の仕様 3)。
                return false;
            }

            SetDragging(true);
        }

        DragToPointer(position);
        UpdateAutoScroll(position);
        return true;
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_touchActive)
        {
            TouchReleased(e.GetCurrentPoint(Surface).Position);
        }

        EndPointer();
        Surface.ReleasePointerCapture(e.Pointer);
    }

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndPointer();

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndPointer();

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            _hoverTimer.Stop();
            HideCellToolTip();
        }
    }

    private void EndPointer()
    {
        _pressed = false;
        SetDragging(false);
        _rowDrag = false;
        _touchActive = false;
        _autoScrollTimer.Stop();
        _autoScrollFastSince = -1;
        _autoScrollAccumulated = 0;
    }

    /// <summary>ダブルクリック・トリプルクリックの判定 (Windows の設定の時間と範囲)。</summary>
    private int NextClickCount(Point position)
    {
        long now = Stopwatch.GetTimestamp();
        double limitX = Math.Max(4, GetSystemMetrics(SmCxDoubleClk)) / 2.0;
        double limitY = Math.Max(4, GetSystemMetrics(SmCyDoubleClk)) / 2.0;
        bool near = Math.Abs(position.X - _lastClickPoint.X) <= limitX && Math.Abs(position.Y - _lastClickPoint.Y) <= limitY;
        bool soon = Stopwatch.GetElapsedTime(_lastClickTime, now).TotalMilliseconds <= GetDoubleClickTime();
        _clickCount = near && soon && _clickCount < 3 ? _clickCount + 1 : 1;
        _lastClickTime = now;
        _lastClickPoint = position;
        return _clickCount;
    }

    /// <summary>行 first〜last の全体を選ぶ (両端を含む)。</summary>
    private void SelectRows(long first, long last, ActiveColumn? column = null, bool cursorAtStart = false)
    {
        if (_editor is null)
        {
            return;
        }

        HexLayout layout = _editor.Layout;
        long from = Math.Min(first, last);
        long to = Math.Max(first, last);
        long start = layout.RowStart(from);
        long end = Math.Min(layout.RowStart(to) + layout.BytesPerRow, layout.Length);
        if (column is { } c && c != _editor.ActiveColumn)
        {
            _editor.Click(Math.Min(start, layout.MaxCursor), c, false, false);
        }

        _editor.Select(start, Math.Max(0, end - start), cursorAtStart);
    }

    /// <summary>
    /// ダブルクリック (EDIT-01 の仕様 5)。Hex 列はグループ (グループ化は VIEW-09 で入るまで 1 バイト)、
    /// テキスト列は印字可能な文字の連続 (前後 32 KiB まで)。
    /// </summary>
    private void DoubleClick(HitResult hit)
    {
        EditorState editor = _editor!;
        if (hit.Offset >= editor.Layout.Length)
        {
            editor.Click(hit.Offset, hit.Column, false, false);
            return;
        }

        if (hit.Column == ActiveColumn.Hex)
        {
            editor.Click(hit.Offset, ActiveColumn.Hex, false, false);
            editor.Select(hit.Offset, 1);
            return;
        }

        editor.Click(hit.Offset, ActiveColumn.Text, false, false);
        DocumentSnapshot snapshot = editor.Document.Current;
        long offset = hit.Offset;

        // 前後 32 KiB を読む。読み込みを待つことがあるため UI スレッドでは読まない。
        _ = Task.Run(() =>
        {
            long from = Math.Max(0, offset - WordScanLimit);
            long to = Math.Min(snapshot.Length, offset + WordScanLimit + 1);
            byte[] buffer = new byte[to - from];
            ReadResult result = snapshot.Read(from, buffer);
            int at = (int)(offset - from);
            bool Printable(int i)
            {
                if (!IsPrintable(buffer[i]))
                {
                    return false;
                }

                long doc = from + i;
                return !result.Unreadable.Any(u => doc >= u.Offset && doc < u.End);
            }

            if (!Printable(at))
            {
                return;
            }

            int start = at;
            while (start > 0 && Printable(start - 1))
            {
                start--;
            }

            int end = at + 1;
            while (end < result.BytesReturned && Printable(end))
            {
                end++;
            }

            _uiQueue.TryEnqueue(() =>
            {
                // 読んでいる間に編集されたら選ばない。
                if (ReferenceEquals(editor.Document.Current, snapshot) && ReferenceEquals(_editor, editor))
                {
                    editor.Select(from + start, end - start);
                }
            });
        });
    }

    /// <summary>テキスト列で印字可能な文字か (現在の文字コード。VIEW-21 の実装までは ASCII の表示と同じ)。</summary>
    private static bool IsPrintable(byte b) => b is >= 0x20 and < 0x7F;

    private void DragToPointer(Point position)
    {
        if (_editor is null)
        {
            return;
        }

        // 表示領域の外では、その端の行を対象にする (自動スクロールで行が入ってくる)。
        double y = Math.Clamp(position.Y, 0, Math.Max(0, _editor.VisibleRows * _rowHeight - 1));
        if (!TryHitTest(new Point(position.X, y), out HitResult hit))
        {
            return;
        }

        if (_rowDrag)
        {
            SelectRows(_rowDragAnchor, hit.Row);
        }
        else
        {
            _editor.DragTo(hit.Offset);
        }
    }

    // ---- 自動スクロール (EDIT-01 の仕様 9、VIEW-28 の仕様 7) ----

    private void UpdateAutoScroll(Point position)
    {
        bool outside = position.Y < 0 || position.Y > Surface.ActualHeight;
        if (outside && !_autoScrollTimer.IsRunning)
        {
            _autoScrollLast = Stopwatch.GetTimestamp();
            _autoScrollAccumulated = 0;
            _autoScrollTimer.Start();
        }
        else if (!outside)
        {
            _autoScrollTimer.Stop();
            _autoScrollFastSince = -1;
        }
    }

    private void AutoScrollTick()
    {
        if (_editor is null || !_dragging)
        {
            _autoScrollTimer.Stop();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Min(0.1, Stopwatch.GetElapsedTime(_autoScrollLast, now).TotalSeconds);
        _autoScrollLast = now;
        double distance = _lastPointer.Y < 0 ? -_lastPointer.Y : _lastPointer.Y - Surface.ActualHeight;
        if (distance <= 0)
        {
            _autoScrollTimer.Stop();
            return;
        }

        if (distance > 50 && _autoScrollFastSince < 0)
        {
            _autoScrollFastSince = now;
        }
        else if (distance <= 50)
        {
            _autoScrollFastSince = -1;
        }

        double held = _autoScrollFastSince < 0 ? 0 : Stopwatch.GetElapsedTime(_autoScrollFastSince, now).TotalSeconds;
        double rowsPerSecond = AutoScrollSpeed(distance, _editor.VisibleRows, held);
        _autoScrollAccumulated += rowsPerSecond * seconds;
        long rows = (long)Math.Floor(_autoScrollAccumulated);
        if (rows == 0)
        {
            return;
        }

        _autoScrollAccumulated -= rows;
        _editor.ScrollRows(_lastPointer.Y < 0 ? -rows : rows);
        DragToPointer(_lastPointer);
    }

    /// <summary>
    /// 自動スクロールの速さ (行 / 秒。EDIT-01 の仕様 9)。0〜50 px で 1〜20 行、150 px で表示行数の 10 倍 (最低 20 行)。
    /// 50 px を超えたまま保つと 1 秒ごとに 2 倍にする。最大 1,000,000 行。
    /// </summary>
    internal static double AutoScrollSpeed(double distance, int visibleRows, double fastSeconds)
    {
        const double MaxRowsPerSecond = 1_000_000;
        if (distance <= 50)
        {
            return 1 + 19 * Math.Max(0, distance) / 50;
        }

        double fast = Math.Max(20, visibleRows * 10.0);
        double speed = 20 + (fast - 20) * Math.Min(1, (distance - 50) / 100);
        speed *= Math.Pow(2, Math.Min(30, fastSeconds));
        return Math.Min(MaxRowsPerSecond, speed);
    }

    // ---- ホイール・横スクロール・タッチ (VIEW-28) ----

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0)
        {
            // Ctrl+ホイールはズーム (VIEW-43、UI-08)。倍率の段階はウィンドウ (UI-08) が決め、ポインタの下の行を基準に ZoomAt を呼ぶ。
            if (ZoomWheel is not null)
            {
                PointerPoint at = e.GetCurrentPoint(Surface);
                ZoomWheel.Invoke(this, new HexViewZoomWheelEventArgs(at.Properties.MouseWheelDelta, at.Position.Y));
                e.Handled = true;
            }

            return;
        }

        PointerPointProperties props = e.GetCurrentPoint(Surface).Properties;
        Wheel(props.MouseWheelDelta, props.IsHorizontalMouseWheel, (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0);
        e.Handled = true;
    }

    /// <summary>ホイールの入力 1 回 (<paramref name="delta"/> は 1 ノッチ 120 単位。下・左へ回すと負)。</summary>
    private void Wheel(int delta, bool horizontal, bool shift)
    {
        if (_editor is null)
        {
            return;
        }

        if (!horizontal && shift)
        {
            // Shift+ホイールは横 (仕様 3)。手前に回すと右へ。
            horizontal = true;
            delta = -delta;
        }

        if (horizontal)
        {
            // 1 ノッチで 3 セル分 (仕様 3)。
            SetHorizontalOffset(_horizontalOffset + delta / 120.0 * 3 * _cellWidth);
        }
        else
        {
            // 1 ノッチで Windows の設定の行数 (仕様 1)。120 未満の入力はピクセルとして蓄積する (仕様 2)。
            uint lines = WheelScrollLines();
            double rowsPerNotch = lines == WheelPageScroll ? Math.Max(1, _editor.VisibleRows - 1) : lines;
            ScrollByPixels(-delta / 120.0 * rowsPerNotch * _rowHeight);
        }
    }

    /// <summary>ピクセル単位で縦にスクロールする。一番上の行 (long) と行内のずれを分けて持つ (VIEW-28 の仕様 2)。</summary>
    private void ScrollByPixels(double pixels)
    {
        if (_editor is null || pixels == 0 || _rowHeight <= 0)
        {
            return;
        }

        long maxTop = _editor.Layout.MaxTopRow(_editor.VisibleRows);
        double total = _subRowOffset + pixels;
        long rows = (long)Math.Floor(total / _rowHeight);
        double sub = total - rows * _rowHeight;
        long top = _editor.TopRow + rows;
        if (top < 0 || (rows < 0 && top < 0))
        {
            top = 0;
            sub = 0;
        }

        if (top >= maxTop)
        {
            top = maxTop;
            sub = 0;
        }

        _subRowOffset = sub;
        _scrollingByPixels = true;
        try
        {
            if (top != _editor.TopRow)
            {
                _editor.ScrollToRow(top);
            }
            else
            {
                QueueRender();
            }
        }
        finally
        {
            _scrollingByPixels = false;
        }
    }

    private void TouchMoved(Point position)
    {
        long now = Stopwatch.GetTimestamp();
        double dx = position.X - _touchLast.X;
        double dy = position.Y - _touchLast.Y;
        if (!_touchMoved)
        {
            double tx = position.X - _touchStart.X;
            double ty = position.Y - _touchStart.Y;
            if (tx * tx + ty * ty < DragThreshold * DragThreshold)
            {
                return;
            }

            _touchMoved = true;
        }

        double ms = Math.Max(1, Stopwatch.GetElapsedTime(_touchLastTime, now).TotalMilliseconds);
        _velocity = new Point(dx / ms, dy / ms);
        _touchLast = position;
        _touchLastTime = now;
        ScrollByPixels(-dy);
        SetHorizontalOffset(_horizontalOffset - dx);
    }

    private void TouchReleased(Point position)
    {
        _touchActive = false;
        if (!_touchMoved)
        {
            // タップ: クリックと同じ。
            if (TryHitTest(position, out HitResult hit))
            {
                _editor?.Click(hit.Offset, hit.Column, hit.LowNibble, false);
            }

            return;
        }

        // 指を離した後の慣性スクロール (VIEW-28 の仕様 2)。
        if (Math.Abs(_velocity.X) + Math.Abs(_velocity.Y) > 0.05)
        {
            _touchLastTime = Stopwatch.GetTimestamp();
            _inertiaTimer.Start();
        }
    }

    private void InertiaTick()
    {
        long now = Stopwatch.GetTimestamp();
        double ms = Math.Min(50, Stopwatch.GetElapsedTime(_touchLastTime, now).TotalMilliseconds);
        _touchLastTime = now;
        double decay = Math.Pow(0.996, ms);
        _velocity = new Point(_velocity.X * decay, _velocity.Y * decay);
        ScrollByPixels(-_velocity.Y * ms);
        SetHorizontalOffset(_horizontalOffset - _velocity.X * ms);
        if (Math.Abs(_velocity.X) + Math.Abs(_velocity.Y) < 0.02)
        {
            _inertiaTimer.Stop();
        }
    }

    private void HorizontalBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (!_updatingScrollBar)
        {
            SetHorizontalOffset(e.NewValue);
        }
    }

    // ---- 縦スクロールバー (VIEW-02) ----

    private void VerticalBar_Scroll(object sender, ScrollEventArgs e) => VerticalScroll(e.ScrollEventType, e.NewValue);

    /// <summary>
    /// スクロールバーの値の変化 (UI オートメーションの RangeValue など、Scroll イベントを伴わない変化。VIEW-02)。
    /// Scroll イベントを伴う変化は <see cref="VerticalScroll"/> が処理するため、同じ処理の中で Scroll が来たら何もしない。
    /// </summary>
    private void VerticalBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_editor is null || _updatingScrollBar)
        {
            return;
        }

        _valueChangePending = true;
        _uiQueue.TryEnqueue(() =>
        {
            if (!_valueChangePending || _editor is null)
            {
                return;
            }

            _valueChangePending = false;
            ScrollToBarValue(VerticalBar.Value);
        });
    }

    /// <summary>true なら、Scroll イベントを伴わないスクロールバーの値の変化を待っている。</summary>
    private bool _valueChangePending;

    private void ScrollToBarValue(double newValue)
    {
        long maxTop = _editor!.Layout.MaxTopRow(_editor.VisibleRows);
        long value = (long)Math.Round(newValue);
        long row = value >= ScrollMapping.Scale(maxTop) ? maxTop : ScrollMapping.ToRow(value, maxTop);
        _editor.ScrollToRow(row);
    }

    /// <summary>縦スクロールバーの操作 (矢印ボタン・トラック・つまみ。VIEW-02 の仕様 4・5・8)。</summary>
    private void VerticalScroll(ScrollEventType type, double newValue)
    {
        _valueChangePending = false;
        if (_editor is null || _updatingScrollBar)
        {
            return;
        }

        long page = Math.Max(1, _editor.VisibleRows - 1);
        switch (type)
        {
            // 矢印ボタンは縮尺に関係なく 1 行 (VIEW-02 の仕様 4)、トラックは 1 画面 (仕様 5)。
            case ScrollEventType.SmallDecrement:
                _editor.ScrollRows(-1);
                break;
            case ScrollEventType.SmallIncrement:
                _editor.ScrollRows(1);
                break;
            case ScrollEventType.LargeDecrement:
                _editor.ScrollRows(-page);
                break;
            case ScrollEventType.LargeIncrement:
                _editor.ScrollRows(page);
                break;
            case ScrollEventType.EndScroll:
                HideScrollToolTip();
                break;
            default:
                ScrollToBarValue(newValue);
                if (type == ScrollEventType.ThumbTrack)
                {
                    ShowScrollToolTip(newValue);
                }

                break;
        }
    }

    /// <summary>つまみのドラッグ中に、一番上の行の先頭アドレスを出す (VIEW-02 の仕様 8)。</summary>
    private void ShowScrollToolTip(double value)
    {
        if (_editor is null)
        {
            return;
        }

        if (_scrollToolTip is null)
        {
            _scrollToolTip = new ToolTip { Placement = PlacementMode.Left };
            AutomationProperties.SetAutomationId(_scrollToolTip, "HexViewScrollToolTip");
            ToolTipService.SetToolTip(VerticalBar, _scrollToolTip);
        }

        long offset = _editor.Layout.RowStart(_editor.TopRow);
        _scrollToolTip.Content = FormatOffset(offset);
        double height = VerticalBar.ActualHeight;
        double max = Math.Max(1, VerticalBar.Maximum);
        double thumb = Math.Max(16, height * VerticalBar.ViewportSize / (max + VerticalBar.ViewportSize));
        double y = (height - thumb) * Math.Clamp(value / max, 0, 1) + thumb / 2;
        _scrollToolTip.PlacementRect = new Rect(0, Math.Max(0, y - 1), VerticalBar.ActualWidth, 2);
        _scrollToolTip.IsOpen = true;
    }

    private void HideScrollToolTip()
    {
        if (_scrollToolTip is not null)
        {
            _scrollToolTip.IsOpen = false;
        }
    }

    /// <summary>オフセットの書式 (VIEW-19・VIEW-20。ステータスバーと同じ: `0x00001F00`、`@00401F00`、10 進は桁区切り付き)。</summary>
    internal string FormatOffset(long offset) =>
        _editor?.OffsetFormat.Status(offset, System.Globalization.CultureInfo.CurrentCulture) ?? "0x" + offset.ToString("X8");

    // ---- マウスを合わせたときのツールチップ (VIEW-07、VIEW-03 の仕様 6) ----

    private void UpdateHover(Point position)
    {
        long offset = -1;
        HitRegion region = HitRegion.Hex;
        if (_editor is not null && TryHitTest(position, out HitResult hit) && hit.Region is HitRegion.Hex or HitRegion.Text or HitRegion.Offset
            && (ShowToolTips || IsUnreadableShown(hit.Offset)) && (hit.Region == HitRegion.Offset || hit.Offset < _editor.Layout.Length))
        {
            offset = hit.Region == HitRegion.Offset ? -2 - hit.Row : hit.Offset;
            region = hit.Region;
        }

        if (offset == _hoverOffset && region == _hoverRegion)
        {
            return;
        }

        _hoverOffset = offset;
        _hoverRegion = region;
        HideCellToolTip();
        _hoverTimer.Stop();
        if (offset != -1)
        {
            _hoverTimer.Start();
        }
    }

    private HitRegion _hoverRegion;

    private bool IsUnreadableShown(long offset)
    {
        if (_editor is null)
        {
            return false;
        }

        HexLayout layout = _editor.Layout;
        long r = layout.RowOf(offset) - _editor.TopRow;
        if (r < 0 || r >= _rows.Count || !_rows[(int)r].Visible)
        {
            return false;
        }

        return _rows[(int)r].KindAt(layout.ColumnOf(offset)) == CellKind.Unreadable;
    }

    private void ShowCellToolTip()
    {
        if (_editor is null || _hoverOffset == -1)
        {
            return;
        }

        long hover = _hoverOffset;
        if (hover <= -2)
        {
            // オフセット列: 行の先頭と末尾のアドレスと行番号 (VIEW-07 の仕様 4)。
            long row = -2 - hover;
            OpenCellToolTip(hover, RowToolTipText(row), null);
            return;
        }

        long offset = hover;
        ActiveColumn column = _hoverRegion == HitRegion.Text ? ActiveColumn.Text : ActiveColumn.Hex;
        if (!IsUnreadableShown(offset))
        {
            OpenCellToolTip(hover, ShowToolTips ? CellToolTipText(offset, null) : string.Empty, (offset, column));
            return;
        }

        DocumentSnapshot snapshot = _editor.Document.Current;
        _ = Task.Run(() =>
        {
            // 読めない理由はデータソースに尋ねる (キャッシュ済みならすぐ返る)。
            byte[] one = new byte[1];
            ReadResult result = snapshot.Read(offset, one);
            UnreadableRange? range = result.Unreadable.Count > 0 ? result.Unreadable[0] : null;
            string reason = UnreadableReasonText(range);
            _uiQueue.TryEnqueue(() =>
            {
                if (_hoverOffset == hover)
                {
                    OpenCellToolTip(hover, ShowToolTips ? CellToolTipText(offset, reason) : reason, (offset, column));
                }
            });
        });
    }

    private void OpenCellToolTip(long hover, string text, (long Offset, ActiveColumn Column)? cell)
    {
        if (_hoverOffset != hover || string.IsNullOrEmpty(text))
        {
            return;
        }

        _cellToolTip ??= new ToolTip();
        AutomationProperties.SetAutomationId(_cellToolTip, "HexViewCellToolTip");
        _cellToolTip.Content = text;
        ToolTipService.SetToolTip(Surface, _cellToolTip);
        if (cell is { } c && TryGetCellRect(c.Offset, out Rect rect, c.Column))
        {
            _cellToolTip.PlacementRect = rect;
        }

        _cellToolTip.IsOpen = true;
        LastToolTip = text;
    }

    /// <summary>最後に出したツールチップの文字列 (テスト用)。閉じたら null。</summary>
    internal string? LastToolTip { get; private set; }

    /// <summary>ツールチップが開いているか。</summary>
    internal bool CellToolTipOpen => _cellToolTip?.IsOpen ?? false;

    private void HideCellToolTip()
    {
        LastToolTip = null;
        if (_cellToolTip is not null)
        {
            _cellToolTip.IsOpen = false;
            ToolTipService.SetToolTip(Surface, null);
        }
    }

    internal static string UnreadableReasonText(UnreadableRange? range)
    {
        if (range is not { } r)
        {
            return Loc.Get("HexView_Unreadable");
        }

        string code = "0x" + r.ErrorCode.ToString("X8");
        return r.Reason switch
        {
            UnreadableReason.Unallocated => Loc.Get("HexView_Unreadable_Unallocated"),
            UnreadableReason.AccessDenied => Loc.Format("HexView_Unreadable_AccessDenied", code),
            UnreadableReason.Disconnected => Loc.Get("HexView_Unreadable_Disconnected"),
            _ => Loc.Format("HexView_Unreadable_IoError", code),
        };
    }

    /// <summary>Hex 列のセルの外接矩形 (Surface の座標)。表示されていなければ false。</summary>
    internal bool TryGetCellRect(long offset, out Rect rect, ActiveColumn column = ActiveColumn.Hex)
    {
        rect = default;
        if (_editor is null)
        {
            return false;
        }

        HexLayout layout = _editor.Layout;
        long r = layout.RowOf(offset) - _editor.TopRow;
        if (r < 0 || r > _editor.VisibleRows + 1)
        {
            return false;
        }

        RowColumns columns = Columns;
        int c = layout.ColumnOf(offset);
        double x = column == ActiveColumn.Hex && columns.ShowHex ? columns.HexIndex(c) : columns.TextIndex(c);
        double width = column == ActiveColumn.Hex ? 2 : 1;
        rect = new Rect(ContentLeft + x * _cellWidth - _horizontalOffset, r * _rowHeight - _subRowOffset, width * _cellWidth, _rowHeight);
        return true;
    }

    // ---- 右クリックメニュー (EDIT-01 の仕様 10、UI-52 の仕様 5) ----

    /// <summary>右クリック、Shift+F10、アプリケーションキー、タッチの長押しで開く。</summary>
    private void HexView_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        ShowContextMenu(args.TryGetPosition(this, out Point position) ? position : null);
        args.Handled = true;
    }

    /// <summary>右クリックメニューを開く。<paramref name="position"/> が null (キーボードから開いた) ならカーソルの位置に出す。</summary>
    private void ShowContextMenu(Point? position)
    {
        if (_editor is null)
        {
            return;
        }

        MenuFlyout menu = _contextMenu ??= CreateContextMenu();
        UpdateContextMenu(menu);
        Point at = position ?? (TryGetCellRect(_editor.Cursor, out Rect rect, _editor.ActiveColumn)
            ? Surface.TransformToVisual(this).TransformPoint(new Point(rect.Left, rect.Bottom))
            : new Point(ContentLeft, 0));
        menu.ShowAt(this, new FlyoutShowOptions { Position = at, ShowMode = FlyoutShowMode.Standard });
    }

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "HexViewContextMenu");
        menu.Items.Add(MenuItem("Cut", Loc.Get("Menu_Edit_Cut/Text"), "Ctrl+X", () => CommandRequested?.Invoke(this, EditorCommand.Cut)));
        menu.Items.Add(MenuItem("Copy", Loc.Get("Menu_Edit_Copy/Text"), "Ctrl+C", () => CommandRequested?.Invoke(this, EditorCommand.Copy)));
        menu.Items.Add(MenuItem("Paste", Loc.Get("Menu_Edit_Paste/Text"), "Ctrl+V", () => CommandRequested?.Invoke(this, EditorCommand.Paste)));
        menu.Items.Add(MenuItem("PasteOverwrite", Loc.Get("Menu_Edit_PasteOverwrite/Text"), "Ctrl+B",
            () => CommandRequested?.Invoke(this, EditorCommand.PasteOverwrite)));
        menu.Items.Add(MenuItem("Delete", Loc.Get("HexView_Menu_Delete"), "Delete", () =>
        {
            if (_editor is not null)
            {
                DeleteWithAnnouncement(_editor.Delete);
            }
        }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("SelectAll", Loc.Get("Menu_Edit_SelectAll/Text"), "Ctrl+A",
            () => CommandRequested?.Invoke(this, EditorCommand.SelectAll)));
        menu.Items.Add(MenuItem("ClearSelection", Loc.Get("HexView_Menu_ClearSelection"), "Esc", () => _editor?.ClearSelection()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("ToggleInsert", Loc.Get("Menu_Edit_ToggleInsert/Text"), "Insert", () =>
        {
            if (_editor is not null)
            {
                Report(_editor.ToggleInsertMode());
            }
        }));
        menu.Closed += (_, _) => Focus(FocusState.Programmatic);
        return menu;

        MenuFlyoutItem MenuItem(string id, string text, string keys, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, KeyboardAcceleratorTextOverride = keys, Tag = id };
            AutomationProperties.SetAutomationId(item, "HexViewMenu_" + id);
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>今の状態で使えない項目を無効にする。</summary>
    private void UpdateContextMenu(MenuFlyout menu)
    {
        EditorState editor = _editor!;
        bool editable = !editor.ReadOnly && !editor.Document.IsEditLocked;
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            if (item is MenuFlyoutItem m)
            {
                m.IsEnabled = (string)m.Tag switch
                {
                    "Cut" => editor.HasSelection && editable,
                    "Copy" => editor.HasSelection,
                    "Paste" or "PasteOverwrite" => editable,
                    "Delete" => editable && (editor.HasSelection || editor.Cursor < editor.Layout.Length),
                    "SelectAll" => editor.Layout.Length > 0,
                    "ClearSelection" => editor.HasSelection,
                    _ => true,
                };
            }
        }
    }

    // ---- キーボード (VIEW-25〜VIEW-27、EDIT-10〜EDIT-13) ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        _diagnostics.RecordKey(e.Key);

        // IME が処理中のキー (VK_PROCESSKEY) と変換中の入力は IME に任せる (EDIT-12 の仕様 2)。
        if ((int)e.Key == 229 || _composing)
        {
            return;
        }

        if (HandleKey(e.Key, IsDown(VirtualKey.Shift), IsDown(VirtualKey.Control), IsDown(VirtualKey.Menu)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// キー 1 つを処理する。処理したら true。実際のキー入力 (<see cref="OnPreviewKeyDown"/>) と、テスト用のビルドの
    /// キー入力の注入 (テスト方針 7.2) の両方から呼ぶ。
    /// </summary>
    private bool HandleKey(VirtualKey key, bool shift, bool ctrl, bool alt)
    {
        if (_editor is null)
        {
            return false;
        }

        // キーボード操作を始めたらツールチップを消す (VIEW-07 の仕様 1)。
        _hoverTimer.Stop();
        HideCellToolTip();

        if (key == VirtualKey.F6 && !ctrl)
        {
            // Hex ビューから他の領域へ移る (VIEW-27 の仕様 4、UI-52)。Tab は列の切り替えに使うため、F6 が出口になる。
            MoveFocusToRegion(!shift);
            return true;
        }

        if (alt)
        {
            // Alt を含むキーはアプリのコマンド (Alt+← / Alt+→ など) に渡す。
            return false;
        }

        bool handled = true;
        EditorCommand? command = (key, ctrl, shift) switch
        {
            (VirtualKey.C, true, false) or (VirtualKey.Insert, true, false) => EditorCommand.Copy,
            (VirtualKey.X, true, false) or (VirtualKey.Delete, false, true) => EditorCommand.Cut,
            (VirtualKey.V, true, false) or (VirtualKey.Insert, false, true) => EditorCommand.Paste,
            (VirtualKey.B, true, false) => EditorCommand.PasteOverwrite,
            (VirtualKey.A, true, false) => EditorCommand.SelectAll,
            _ => null,
        };
        if (command is { } c)
        {
            CommandRequested?.Invoke(this, c);
            RestartBlink();
            return true;
        }

        switch (key)
        {
            case VirtualKey.Left when ctrl:
                // 前 / 次のグループへ (VIEW-25 の仕様 8。コマンド go.previousGroup / go.nextGroup の既定のキー)。
                _editor.MovePreviousGroup(shift);
                break;
            case VirtualKey.Right when ctrl:
                _editor.MoveNextGroup(shift);
                break;
            case VirtualKey.Left:
                _editor.MoveLeft(shift);
                break;
            case VirtualKey.Right:
                _editor.MoveRight(shift);
                break;
            case VirtualKey.Up when ctrl:
                _editor.ScrollRows(-1);
                break;
            case VirtualKey.Down when ctrl:
                _editor.ScrollRows(1);
                break;
            case VirtualKey.Up:
                _editor.MoveUp(shift);
                break;
            case VirtualKey.Down:
                _editor.MoveDown(shift);
                break;
            case VirtualKey.Home when ctrl:
                _editor.MoveToStart(shift);
                break;
            case VirtualKey.End when ctrl:
                _editor.MoveToEnd(shift);
                break;
            case VirtualKey.Home:
                _editor.MoveHome(shift);
                break;
            case VirtualKey.End:
                _editor.MoveEnd(shift);
                break;
            case VirtualKey.PageUp when !ctrl:
                _editor.PageUp(shift);
                break;
            case VirtualKey.PageDown when !ctrl:
                _editor.PageDown(shift);
                break;
            case VirtualKey.Tab when !ctrl:
                // Tab / Shift+Tab で列を切り替える (VIEW-27 の仕様 1。列が 2 つなので順と逆は同じ)。
                _editor.ToggleColumn();
                break;
            case VirtualKey.Insert when !ctrl && !shift:
                Report(_editor.ToggleInsertMode());
                break;
            case VirtualKey.Delete when !shift:
                DeleteWithAnnouncement(_editor.Delete);
                break;
            case VirtualKey.Back:
                DeleteWithAnnouncement(_editor.Backspace);
                break;
            case VirtualKey.Escape:
                _editor.ClearSelection();
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            RestartBlink();
        }

        return handled;
    }

    /// <summary>削除して、削除したバイト数を読み上げる (EDIT-13 の仕様 6)。</summary>
    private void DeleteWithAnnouncement(Func<EditResult> delete)
    {
        long before = _editor!.Document.Length;
        EditResult result = delete();

        // 長さを変えられないドキュメントでの削除は、「00 で塗りつぶす」を付けて知らせる (EDIT-13 の仕様 5)。
        Report(result == EditResult.FixedLength ? EditResult.FixedLengthDelete : result);
        long removed = before - _editor.Document.Length;
        if (removed > 0)
        {
            Announce(Loc.Format("HexView_Announce_Deleted", removed.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)), "HexViewDeleted");
        }
    }

    /// <summary>F6 / Shift+F6。ウィンドウが処理しなければ、次 / 前のフォーカス可能な要素へ移す。</summary>
    private void MoveFocusToRegion(bool forward)
    {
        var args = new HexViewFocusRegionEventArgs(forward);
        FocusRegionRequested?.Invoke(this, args);
        if (args.Handled)
        {
            return;
        }

        try
        {
            var options = new FindNextElementOptions { SearchRoot = XamlRoot?.Content };
            FocusManager.TryMoveFocus(forward ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous, options);
        }
        catch (ArgumentException ex)
        {
            AppLog.Warning($"HexView: フォーカスを移せません ({ex.HResult:X8})");
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // TSF (HexView.TextInput.cs) で受け取った文字と同じなら二重に書かない。
        if (_editor is not null && !char.IsControl(e.Character) && !IsDown(VirtualKey.Control) && ConsumeIfDeliveredByTextInput(e.Character))
        {
            e.Handled = true;
            return;
        }

        if (HandleCharacter(e.Character, IsDown(VirtualKey.Control)))
        {
            RememberCharacterInput(e.Character);
            e.Handled = true;
        }
    }

    /// <summary>入力した文字 1 つを書き込む。処理したら true (テスト用のビルドの文字入力の注入からも呼ぶ)。</summary>
    private bool HandleCharacter(char character, bool ctrl)
    {
        if (_editor is null || ctrl || char.IsControl(character))
        {
            return false;
        }

        TypeCharacters(character.ToString());
        return true;
    }

    /// <summary>確定した文字を書き込む (EDIT-11、EDIT-12)。</summary>
    private void TypeCharacters(string text)
    {
        if (_editor is null || text.Length == 0)
        {
            return;
        }

        if (_editor.ActiveColumn == ActiveColumn.Hex)
        {
            foreach (char ch in text)
            {
                EditResult r = _editor.TypeHexDigit(ch);
                Report(r);
                if (r is EditResult.FixedLength or EditResult.NotEditable)
                {
                    break;
                }
            }
        }
        else
        {
            LastRejectedText = text;
            Report(_editor.TypeText(text));
            LastRejectedText = null;
        }

        RestartBlink();
        AnnounceTyped();
    }

    /// <summary>入力を拒否したときの、入力した文字列 (<see cref="EditRejected"/> の処理の中だけで読める。表せない文字の InfoBar に使う)。</summary>
    internal string? LastRejectedText { get; private set; }

    private void Report(EditResult result)
    {
        if (result is EditResult.FixedLength or EditResult.FixedLengthDelete or EditResult.NotEditable or EditResult.NotEncodable)
        {
            EditRejected?.Invoke(this, result);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---- Windows の設定 ----

    private const uint WheelPageScroll = uint.MaxValue;
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;

    /// <summary>ホイール 1 ノッチの行数 (SPI_GETWHEELSCROLLLINES。既定 3。「1 画面ずつ」は uint.MaxValue)。</summary>
    private static uint WheelScrollLines()
    {
#if HEX_TEST_HOOKS
        // テストでは Windows の設定を変えずに、設定の値だけを差し替える (利用者の設定を変えないため)。
        if (TestWheelScrollLines is { } testLines)
        {
            return testLines;
        }
#endif
        uint lines = 3;
        return SystemParametersInfo(SpiGetWheelScrollLines, 0, ref lines, 0) ? lines : 3;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
