using System.Runtime.InteropServices;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Input;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビュー (VIEW-01〜VIEW-04)。見えている行の数だけ TextBlock を用意して使い回し、ファイルの大きさに関係なく
/// 同じ手間で描く。カーソル・選択・入力の規則は UI に依存しない <see cref="EditorState"/> が持ち、このコントロールは
/// 描画と入力の振り分けだけを行う。
/// </summary>
public sealed partial class HexView : UserControl
{
    /// <summary>既定のフォントの大きさ (10 pt = 13.33 epx。VIEW-01 の仕様 3)。</summary>
    private const double DefaultFontSize = 13.333;

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    /// <summary>描画面の左端の余白 (XAML の RowsHost の Margin と同じ値)。</summary>
    private const double LeftPadding = 8;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blinkTimer;
    private readonly List<TextBlock> _rows = [];
    private double _cellWidth = 8;
    private double _rowHeight = 16;
    private EditorState? _editor;
    private Palette? _palette;
    private bool _dragging;
    private bool _renderQueued;
    private bool _updatingScrollBar;

    public HexView()
    {
        InitializeComponent();

        // 読み込み完了の通知はスレッドプールから来るため、UI スレッドのキューをここで取っておく。
        _uiQueue = DispatcherQueue;
        _blinkTimer = _uiQueue.CreateTimer();
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, CaretBlinkMilliseconds()));
        _blinkTimer.Tick += (_, _) => Caret.Opacity = Caret.Opacity > 0 ? 0 : 1;
        GotFocus += (_, _) => RestartBlink();
        LostFocus += (_, _) =>
        {
            _blinkTimer.Stop();
            Caret.Opacity = 1;
            Render();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        CharacterReceived += OnCharacterReceived;
        ActualThemeChanged += (_, _) =>
        {
            _palette = Palette.Load();
            Render();
        };
        Loaded += (_, _) =>
        {
            _palette = Palette.Load();
            MeasureCell();
            UpdateVisibleRows();
            UpdateScrollBar();
            Render();
        };
        Unloaded += (_, _) => _blinkTimer.Stop();
    }

    /// <summary>テキスト列の入力に使う文字コード (VIEW-21 の実装までは Latin-1)。</summary>
    public Encoding TextEncoding { get; set; } = Encoding.Latin1;

    /// <summary>入力を拒否したときに呼ぶ (InfoBar を出すため)。</summary>
    public event EventHandler<EditResult>? EditRejected;

    /// <summary>表示するビューの状態。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (_editor is not null)
            {
                _editor.Changed -= OnEditorChanged;
                _editor.Document.DataLoaded -= OnDataLoaded;
            }

            _editor = value;
            if (_editor is not null)
            {
                _editor.Changed += OnEditorChanged;
                _editor.Document.DataLoaded += OnDataLoaded;
                UpdateVisibleRows();
            }

            UpdateScrollBar();
            QueueRender();
        }
    }

    // ---- 描画 ----

    /// <summary>セル幅と行の高さを `0` の送り幅とフォントの高さから求める (VIEW-01 の仕様 4)。</summary>
    private void MeasureCell()
    {
        var probe = new TextBlock { Text = new string('0', 64), FontFamily = MonoFont, FontSize = DefaultFontSize };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _cellWidth = probe.DesiredSize.Width / 64;
        _rowHeight = Math.Ceiling(probe.DesiredSize.Height * 1.2);
        foreach (TextBlock row in _rows)
        {
            row.LineHeight = _rowHeight;
        }
    }

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }

        _renderQueued = true;
        _uiQueue.TryEnqueue(() =>
        {
            _renderQueued = false;
            Render();
        });
    }

    private void Render()
    {
        if (_editor is null || _palette is null)
        {
            return;
        }

        HexLayout layout = _editor.Layout;
        int bytesPerRow = layout.BytesPerRow;
        int digits = layout.MaxCursor > uint.MaxValue ? 16 : 8;
        int rows = (int)Math.Min(_editor.VisibleRows + 1, layout.TotalRows - _editor.TopRow);
        EnsureRowCount(rows);

        long firstOffset = layout.RowStart(_editor.TopRow);
        int span = rows * bytesPerRow;
        byte[] bytes = new byte[span];
        var states = new ByteState[span];
        DocumentSnapshot snapshot = _editor.Document.Current;
        int available = snapshot.ReadForDisplay(firstOffset, bytes, states);

        // 変更されたバイト (VIEW-15)。色に加えて下線でも示す。
        var modified = new bool[span];
        foreach ((long offset, long length) in snapshot.EnumerateModifiedRanges(firstOffset, available))
        {
            modified.AsSpan((int)(offset - firstOffset), (int)length).Fill(true);
        }

        var columns = new RowColumns(digits, bytesPerRow);
        long selStart = _editor.SelectionStart;
        long selEnd = selStart + _editor.SelectionLength;
        for (int r = 0; r < rows; r++)
        {
            long rowStart = firstOffset + (long)r * bytesPerRow;
            int from = r * bytesPerRow;
            int count = Math.Max(0, Math.Min(bytesPerRow, available - from));
            FillRow(_rows[r], rowStart, columns, bytes.AsSpan(from, bytesPerRow), states.AsSpan(from, bytesPerRow), modified.AsSpan(from, bytesPerRow), count);
            Highlight(_rows[r], columns, rowStart, bytesPerRow, selStart, selEnd);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(_rows[r], r * _rowHeight);
            _rows[r].Visibility = Visibility.Visible;
        }

        for (int r = rows; r < _rows.Count; r++)
        {
            _rows[r].Visibility = Visibility.Collapsed;
        }

        PlaceCaret(layout, columns);
    }

    private void EnsureRowCount(int count)
    {
        while (_rows.Count < count)
        {
            var row = new TextBlock
            {
                FontFamily = MonoFont,
                FontSize = DefaultFontSize,
                LineHeight = _rowHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap,
                IsTextScaleFactorEnabled = false,
            };
            _rows.Add(row);
            RowsHost.Children.Insert(0, row);
        }
    }

    /// <summary>1 行の文字列を、色の違う区間ごとの Run に分けて作る。</summary>
    private void FillRow(TextBlock row, long rowStart, RowColumns columns, ReadOnlySpan<byte> bytes, ReadOnlySpan<ByteState> states,
        ReadOnlySpan<bool> modified, int count)
    {
        Palette palette = _palette!;
        row.Inlines.Clear();
        var builder = new RunBuilder(row, palette);
        builder.Append(rowStart.ToString("X" + columns.Digits), palette.OffsetText);
        builder.Append("  ", palette.Text);

        // Hex 列
        for (int c = 0; c < columns.BytesPerRow; c++)
        {
            Kind kind = c >= count ? Kind.Empty : Classify(states[c], modified[c]);
            string cell = kind switch
            {
                Kind.Empty => "  ",
                Kind.Loading => "··",
                Kind.Unreadable => "??",
                _ => bytes[c].ToString("X2"),
            };
            builder.Append(cell, kind);
            builder.Append(c == columns.BytesPerRow / 2 - 1 ? "  " : " ", palette.Text);
        }

        builder.Append(" ", palette.Text);

        // テキスト列
        for (int c = 0; c < count; c++)
        {
            Kind kind = Classify(states[c], modified[c]);
            builder.Append(kind is Kind.Loading or Kind.Unreadable ? " " : ToChar(bytes[c]).ToString(), kind);
        }

        builder.Flush();
    }

    private void Highlight(TextBlock row, RowColumns columns, long rowStart, int bytesPerRow, long selStart, long selEnd)
    {
        row.TextHighlighters.Clear();
        long from = Math.Max(selStart, rowStart);
        long to = Math.Min(selEnd, rowStart + bytesPerRow);
        if (from >= to)
        {
            return;
        }

        int first = (int)(from - rowStart);
        int last = (int)(to - rowStart - 1);
        var highlighter = new TextHighlighter { Background = _palette!.Selection, Foreground = _palette.SelectionText };
        int hexStart = columns.HexIndex(first);
        highlighter.Ranges.Add(new TextRange { StartIndex = hexStart, Length = columns.HexIndex(last) + 2 - hexStart });
        highlighter.Ranges.Add(new TextRange { StartIndex = columns.TextIndex(first), Length = last - first + 1 });
        row.TextHighlighters.Add(highlighter);
    }

    private void PlaceCaret(HexLayout layout, RowColumns columns)
    {
        long row = layout.RowOf(_editor!.Cursor) - _editor.TopRow;
        bool visible = row >= 0 && row <= _editor.VisibleRows;
        Caret.Visibility = SecondaryCaret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        int column = layout.ColumnOf(_editor.Cursor);
        double y = row * _rowHeight;
        double hexX = columns.HexIndex(column) * _cellWidth;
        double textX = columns.TextIndex(column) * _cellWidth;
        bool hexActive = _editor.ActiveColumn == ActiveColumn.Hex;
        double activeX = hexActive ? hexX + (_editor.LowNibble ? _cellWidth : 0) : textX;

        // もう一方の列の対応位置は枠で示す (VIEW-06)。
        SetRect(SecondaryCaret, hexActive ? textX : hexX, y, hexActive ? _cellWidth : _cellWidth * 2, _rowHeight);

        // 上書きモードは枠、挿入モードは縦線 (EDIT-10 の仕様 3。色だけで区別しない)。フォーカスがなければ細い枠。
        bool focused = FocusState != FocusState.Unfocused;
        if (_editor.InsertMode && focused)
        {
            Caret.Fill = _palette!.Caret;
            Caret.Stroke = null;
            SetRect(Caret, activeX - 1, y, 2, _rowHeight);
        }
        else
        {
            Caret.Fill = null;
            Caret.Stroke = _palette!.Caret;
            Caret.StrokeThickness = focused ? 2 : 1;
            SetRect(Caret, activeX, y, _cellWidth, _rowHeight);
        }
    }

    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(element, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(element, y);
        element.Width = width;
        element.Height = height;
    }

    private static Kind Classify(ByteState state, bool modified) => state switch
    {
        ByteState.Loading => Kind.Loading,
        ByteState.Unreadable => Kind.Unreadable,
        _ => modified ? Kind.Modified : Kind.Normal,
    };

    private static char ToChar(byte b) => b is >= 0x20 and < 0x7F ? (char)b : '.';

    /// <summary>セルの種類。種類ごとに色・飾りを変える。</summary>
    private enum Kind
    {
        Normal,
        Modified,
        Loading,
        Unreadable,
        Empty,
    }

    /// <summary>1 行の文字列の中での各列の位置 (文字単位)。</summary>
    private readonly record struct RowColumns(int Digits, int BytesPerRow)
    {
        /// <summary>c 番目のバイトの Hex 列の先頭の文字位置。中央に 1 文字分の区切りを入れる。</summary>
        public int HexIndex(int c) => Digits + 2 + c * 3 + (c >= BytesPerRow / 2 ? 1 : 0);

        public int TextIndex(int c) => Digits + 2 + BytesPerRow * 3 + 2 + c;
    }

    /// <summary>同じ見た目の文字をまとめて Run にする。</summary>
    private sealed class RunBuilder(TextBlock row, Palette palette)
    {
        private readonly StringBuilder _text = new();
        private Brush? _brush;
        private Kind _kind;

        public void Append(string text, Brush brush) => Append(text, Kind.Normal, brush);

        public void Append(string text, Kind kind) => Append(text, kind, palette.For(kind));

        public void Flush()
        {
            if (_text.Length == 0)
            {
                return;
            }

            var run = new Run { Text = _text.ToString(), Foreground = _brush };
            if (_kind == Kind.Modified)
            {
                run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
            }
            else if (_kind == Kind.Unreadable)
            {
                // 読み取れない範囲は取り消し線でも示す (VIEW-03 の仕様 5。色だけに頼らない)。
                run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            }

            row.Inlines.Add(run);
            _text.Clear();
        }

        private void Append(string text, Kind kind, Brush brush)
        {
            if (!ReferenceEquals(brush, _brush) || kind != _kind)
            {
                Flush();
                _brush = brush;
                _kind = kind;
            }

            _text.Append(text);
        }
    }

    // ---- スクロール (VIEW-02、VIEW-28) ----

    private void UpdateVisibleRows()
    {
        if (_editor is not null && _rowHeight > 0 && Surface.ActualHeight > 0)
        {
            _editor.VisibleRows = Math.Max(1, (int)(Surface.ActualHeight / _rowHeight));
        }
    }

    private void UpdateScrollBar()
    {
        _updatingScrollBar = true;
        try
        {
            if (_editor is null)
            {
                VerticalBar.Maximum = 0;
                return;
            }

            long maxTop = _editor.Layout.MaxTopRow(_editor.VisibleRows);
            long scale = ScrollMapping.Scale(maxTop);
            VerticalBar.Minimum = 0;
            VerticalBar.Maximum = scale;
            VerticalBar.SmallChange = 1;
            VerticalBar.LargeChange = Math.Max(1, _editor.VisibleRows - 1);
            VerticalBar.ViewportSize = scale == 0
                ? 1
                : Math.Max(1, (double)scale * _editor.VisibleRows / Math.Max(1, _editor.Layout.TotalRows));
            VerticalBar.Value = ScrollMapping.ToValue(_editor.TopRow, maxTop);
        }
        finally
        {
            _updatingScrollBar = false;
        }
    }

    private void VerticalBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_editor is null || _updatingScrollBar)
        {
            return;
        }

        long page = Math.Max(1, _editor.VisibleRows - 1);
        switch (e.ScrollEventType)
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
            default:
                long maxTop = _editor.Layout.MaxTopRow(_editor.VisibleRows);
                long value = (long)Math.Round(e.NewValue);
                long row = value >= ScrollMapping.Scale(maxTop) ? maxTop : ScrollMapping.ToRow(value, maxTop);
                _editor.ScrollToRow(row);
                break;
        }
    }

    private void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        int delta = e.GetCurrentPoint(Surface).Properties.MouseWheelDelta;
        _editor.ScrollRows(-delta / 120 * 3);
        e.Handled = true;
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SurfaceClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        UpdateVisibleRows();
        UpdateScrollBar();
        QueueRender();
    }

    // ---- マウス (VIEW-25 の仕様 6) ----

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        if (_editor is null || !TryHitTest(e.GetCurrentPoint(Surface).Position, out long offset, out ActiveColumn column, out bool low))
        {
            return;
        }

        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        _editor.Click(offset, column, low, shift);
        _dragging = true;
        Surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging && _editor is not null && TryHitTest(e.GetCurrentPoint(Surface).Position, out long offset, out _, out _))
        {
            _editor.DragTo(offset);
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        Surface.ReleasePointerCapture(e.Pointer);
    }

    private bool TryHitTest(Windows.Foundation.Point point, out long offset, out ActiveColumn column, out bool lowNibble)
    {
        offset = 0;
        column = ActiveColumn.Hex;
        lowNibble = false;
        if (_editor is null)
        {
            return false;
        }

        HexLayout layout = _editor.Layout;
        var columns = new RowColumns(layout.MaxCursor > uint.MaxValue ? 16 : 8, layout.BytesPerRow);
        long row = _editor.TopRow + (long)Math.Max(0, point.Y / _rowHeight);
        if (row >= layout.TotalRows)
        {
            offset = layout.MaxCursor;
            return true;
        }

        int ch = (int)((point.X - LeftPadding) / _cellWidth);
        int b = layout.BytesPerRow;
        if (ch >= columns.TextIndex(0))
        {
            column = ActiveColumn.Text;
            offset = layout.RowStart(row) + Math.Clamp(ch - columns.TextIndex(0), 0, b - 1);
        }
        else if (ch >= columns.HexIndex(0) - 1)
        {
            int c = 0;
            while (c < b - 1 && ch >= columns.HexIndex(c + 1) - 1)
            {
                c++;
            }

            lowNibble = ch - columns.HexIndex(c) >= 1;
            offset = layout.RowStart(row) + c;
        }
        else
        {
            offset = layout.RowStart(row);
        }

        offset = Math.Min(offset, layout.MaxCursor);
        return true;
    }

    // ---- キーボード (VIEW-25〜VIEW-27、EDIT-10〜EDIT-13) ----

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        bool shift = IsDown(VirtualKey.Shift);
        bool ctrl = IsDown(VirtualKey.Control);
        bool handled = true;
        switch (e.Key)
        {
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
                _editor.ToggleColumn();
                break;
            case VirtualKey.Insert when !ctrl && !shift:
                Report(_editor.ToggleInsertMode());
                break;
            case VirtualKey.Delete when !shift:
                Report(_editor.Delete());
                break;
            case VirtualKey.Back:
                Report(_editor.Backspace());
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
            e.Handled = true;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (_editor is null || IsDown(VirtualKey.Control) || char.IsControl(e.Character))
        {
            return;
        }

        EditResult result = _editor.ActiveColumn == ActiveColumn.Hex
            ? _editor.TypeHexDigit(e.Character)
            : _editor.TypeText(e.Character.ToString(), TextEncoding);
        Report(result);
        RestartBlink();
        e.Handled = true;
    }

    private void Report(EditResult result)
    {
        if (result is EditResult.FixedLength or EditResult.NotEditable or EditResult.NotEncodable)
        {
            EditRejected?.Invoke(this, result);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---- 更新 ----

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        UpdateScrollBar();
        QueueRender();
    }

    /// <summary>データの読み込みが終わった (スレッドプールから)。UI スレッドで 1 回だけ描き直す。</summary>
    private void OnDataLoaded(object? sender, EventArgs e) => _uiQueue.TryEnqueue(QueueRender);

    private void RestartBlink()
    {
        Caret.Opacity = 1;
        _blinkTimer.Stop();
        if (CaretBlinkMilliseconds() > 0 && FocusState != FocusState.Unfocused)
        {
            _blinkTimer.Start();
        }

        QueueRender();
    }

    /// <summary>カーソルの点滅間隔 (VIEW-01 の仕様 13。Windows の設定に従い、点滅しない設定なら 0)。</summary>
    private static int CaretBlinkMilliseconds()
    {
        uint t = GetCaretBlinkTime();
        return t is 0 or uint.MaxValue ? 0 : (int)t;
    }

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    /// <summary>
    /// 配色 (VIEW-01 の仕様 10)。テーマのリソースから取るため、ライト / ダーク / ハイコントラストの切り替えに追従する。
    /// </summary>
    private sealed record Palette(Brush Text, Brush OffsetText, Brush Modified, Brush Dim, Brush Selection, Brush SelectionText, Brush Caret)
    {
        public static Palette Load()
        {
            static Brush Get(string key) => (Brush)Application.Current.Resources[key];
            return new Palette(
                Get("TextFillColorPrimaryBrush"),
                Get("TextFillColorSecondaryBrush"),
                Get("SystemFillColorCriticalBrush"),
                Get("TextFillColorDisabledBrush"),
                Get("AccentFillColorDefaultBrush"),
                Get("TextOnAccentFillColorPrimaryBrush"),
                Get("AccentFillColorDefaultBrush"));
        }

        public Brush For(Kind kind) => kind switch
        {
            Kind.Modified => Modified,
            Kind.Loading or Kind.Unreadable or Kind.Empty => Dim,
            _ => Text,
        };
    }
}
