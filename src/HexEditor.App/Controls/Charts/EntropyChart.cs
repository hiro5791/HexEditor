using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Statistics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.System;

namespace HexEditor.App.Controls.Charts;

/// <summary>
/// エントロピーグラフ (ANA-13)。横軸はオフセット、縦軸はエントロピー (0〜8)。線・しきい値・しきい値を超える区間の模様・読めない
/// ブロックの斜線・カーソルの縦線を、それぞれ 1 つの Path に描く。拡大すると表示範囲の細かいブロック (<see cref="DetailBlocks"/>) も描く。
/// クリックでブロックの先頭へ、ドラッグでブロックの境界にそろえた範囲を選択する。キーボードでは ← / → でブロックを選び、Enter で移動する。
/// </summary>
public sealed partial class EntropyChart : UserControl, IChartItems
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(nameof(Blocks), typeof(object), typeof(EntropyChart),
        new PropertyMetadata(null, (d, _) => ((EntropyChart)d).OnBlocksChanged()));

    public static readonly DependencyProperty DetailBlocksProperty = DependencyProperty.Register(nameof(DetailBlocks), typeof(object), typeof(EntropyChart),
        new PropertyMetadata(null, (d, _) => ((EntropyChart)d).Redraw()));

    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.Register(nameof(Threshold), typeof(double), typeof(EntropyChart),
        new PropertyMetadata(7.2, (d, _) => ((EntropyChart)d).Redraw()));

    public static readonly DependencyProperty ShowZeroProperty = DependencyProperty.Register(nameof(ShowZero), typeof(bool), typeof(EntropyChart),
        new PropertyMetadata(false, (d, _) => ((EntropyChart)d).Redraw()));

    public static readonly DependencyProperty ShowPrintableProperty = DependencyProperty.Register(nameof(ShowPrintable), typeof(bool), typeof(EntropyChart),
        new PropertyMetadata(false, (d, _) => ((EntropyChart)d).Redraw()));

    public static readonly DependencyProperty CursorProperty = DependencyProperty.Register(nameof(Cursor), typeof(object), typeof(EntropyChart),
        new PropertyMetadata(null, (d, _) => ((EntropyChart)d).DrawCursor()));

    public static readonly DependencyProperty SelectedBlockProperty = DependencyProperty.Register(nameof(SelectedBlock), typeof(int), typeof(EntropyChart),
        new PropertyMetadata(-1, (d, _) => ((EntropyChart)d).DrawCursor()));

    private const double MaxEntropy = 8;

    private readonly Grid _root = new();
    private readonly Canvas _canvas = new() { Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"] };
    private readonly Path _hatch = new() { StrokeThickness = 1, Opacity = 0.5 };
    private readonly Path _unreadable = new() { StrokeThickness = 1 };
    private readonly Path _line = new() { StrokeThickness = 1.5 };
    private readonly Path _detail = new() { StrokeThickness = 1.5 };
    private readonly Path _zero = new() { StrokeThickness = 1, StrokeDashArray = [4, 2] };
    private readonly Path _printable = new() { StrokeThickness = 1, StrokeDashArray = [1, 2] };
    private readonly Path _threshold = new() { StrokeThickness = 1, StrokeDashArray = [6, 3] };
    private readonly Path _cursor = new() { StrokeThickness = 1 };
    private readonly Path _selected = new() { StrokeThickness = 2 };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Horizontal, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, Visibility = Visibility.Collapsed };
    private ChartAutomationPeer? _peer;
    private double _zoom = 1;
    private double _start;
    private Point? _pressed;

    public EntropyChart()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        MinHeight = 140;
        foreach (Path p in new[] { _hatch, _unreadable, _threshold, _zero, _printable, _line, _detail, _cursor, _selected })
        {
            _canvas.Children.Add(p);
        }

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(_canvas);
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);
        Content = _root;
        _scroll.Scroll += (_, e) => SetView(_zoom, e.NewValue);
        _canvas.SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        _canvas.PointerPressed += (_, e) =>
        {
            Focus(FocusState.Pointer);
            _pressed = e.GetCurrentPoint(_canvas).Position;
            _canvas.CapturePointer(e.Pointer);
        };
        _canvas.PointerReleased += (_, e) =>
        {
            if (_pressed is Point start)
            {
                _pressed = null;
                _canvas.ReleasePointerCapture(e.Pointer);
                Release(start.X, e.GetCurrentPoint(_canvas).Position.X);
            }
        };
        _canvas.PointerMoved += (_, e) =>
        {
            int block = BlockAt(e.GetCurrentPoint(_canvas).Position.X);
            ChartSupport.SetToolTip(this, ItemName(block));
        };
        _canvas.PointerWheelChanged += (_, e) =>
        {
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                PointerPoint point = e.GetCurrentPoint(_canvas);
                ZoomAt(point.Properties.MouseWheelDelta > 0 ? 2 : 0.5, point.Position.X);
                e.Handled = true;
            }
        };
        GotFocus += (_, _) =>
        {
            if (SelectedBlock < 0 && ItemCount > 0)
            {
                SelectItem(0);
            }
        };
        AutomationProperties.SetName(this, Loc.Get("Stats_EntropyChart"));
    }

    /// <summary>ブロックごとのエントロピー (<see cref="EntropyBlocks"/>)。</summary>
    public object? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>拡大したときの表示範囲の細かいブロック。</summary>
    public object? DetailBlocks
    {
        get => GetValue(DetailBlocksProperty);
        set => SetValue(DetailBlocksProperty, value);
    }

    public double Threshold
    {
        get => (double)GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public bool ShowZero
    {
        get => (bool)GetValue(ShowZeroProperty);
        set => SetValue(ShowZeroProperty, value);
    }

    public bool ShowPrintable
    {
        get => (bool)GetValue(ShowPrintableProperty);
        set => SetValue(ShowPrintableProperty, value);
    }

    /// <summary>Hex ビューのカーソル位置 (論理位置、long?)。</summary>
    public object? Cursor
    {
        get => GetValue(CursorProperty);
        set => SetValue(CursorProperty, value);
    }

    public int SelectedBlock
    {
        get => (int)GetValue(SelectedBlockProperty);
        set => SetValue(SelectedBlockProperty, value);
    }

    /// <summary>オフセットの表示 (論理位置 → ドキュメントの 16 進の位置)。</summary>
    public Func<long, string>? OffsetText { get; set; }

    public event EventHandler<int>? BlockInvoked;

    /// <summary>ドラッグで選んだブロックの範囲 (先頭、末尾の次)。</summary>
    public event EventHandler<(int First, int LastExclusive)>? RangeSelected;

    /// <summary>表示範囲が変わった (論理位置の範囲、1 ブロックの幅 (ピクセル))。</summary>
    public event EventHandler<(long From, long To, double BlockPixels)>? ViewChanged;

    public double Zoom => _zoom;

    private EntropyBlocks? Data => Blocks as EntropyBlocks;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ChartAutomationPeer(this, this, nameof(EntropyChart));

    /// <summary>表示範囲 (論理位置)。</summary>
    public (long From, long To) View
    {
        get
        {
            long length = Data?.Length ?? 0;
            long from = (long)(_start * length);
            return (from, Math.Min(length, from + (long)Math.Ceiling(length / _zoom)));
        }
    }

    public void ZoomIn() => ZoomAt(2, _canvas.ActualWidth / 2);

    public void ZoomOut() => ZoomAt(0.5, _canvas.ActualWidth / 2);

    private void ZoomAt(double factor, double x)
    {
        if (Data is not { Length: > 0 } b)
        {
            return;
        }

        // 最大で 1 ピクセルが 1 バイト程度になるまで拡大する。
        double maxZoom = Math.Max(1, b.Length / Math.Max(1, _canvas.ActualWidth) / 4);
        double zoom = Math.Clamp(_zoom * factor, 1, Math.Max(1, maxZoom));
        double anchor = _start + (x / Math.Max(1, _canvas.ActualWidth) / _zoom);
        SetView(zoom, anchor - (x / Math.Max(1, _canvas.ActualWidth) / zoom));
    }

    private void SetView(double zoom, double start)
    {
        _zoom = zoom;
        _start = Math.Clamp(start, 0, Math.Max(0, 1 - (1 / zoom)));
        _scroll.Visibility = zoom > 1 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Minimum = 0;
        _scroll.Maximum = Math.Max(0, 1 - (1 / zoom));
        _scroll.ViewportSize = 1 / zoom;
        _scroll.SmallChange = 0.1 / zoom;
        _scroll.LargeChange = 1 / zoom;
        _scroll.Value = _start;
        Redraw();
        if (Data is { } b)
        {
            (long from, long to) = View;
            ViewChanged?.Invoke(this, (from, to, b.BlockSize * Math.Max(1, _canvas.ActualWidth) / Math.Max(1, to - from)));
        }
    }

    private void OnBlocksChanged()
    {
        if (Data is not { } b || SelectedBlock >= b.Count)
        {
            SelectedBlock = -1;
        }

        Redraw();
    }

    private double X(long logical)
    {
        (long from, long to) = View;
        return (logical - from) * _canvas.ActualWidth / Math.Max(1, to - from);
    }

    private double Y(double entropy) => _canvas.ActualHeight * (1 - Math.Clamp(entropy / MaxEntropy, 0, 1));

    /// <summary>描画面の x 座標のブロック (範囲外は −1)。</summary>
    internal int BlockAt(double x)
    {
        if (Data is not { Count: > 0 } b || _canvas.ActualWidth <= 0)
        {
            return -1;
        }

        (long from, long to) = View;
        double fraction = Math.Clamp(x / _canvas.ActualWidth, 0, 1);
        long logical = from + (long)(fraction * (to - from));
        return x >= _canvas.ActualWidth ? b.Count : b.BlockOf(logical) + (logical >= b.Length ? 1 : 0);
    }

    /// <summary>押して離した: ほとんど動いていなければクリック (ブロックへ移動)、動いていればドラッグ (範囲を選択)。</summary>
    internal void Release(double x1, double x2)
    {
        if (Math.Abs(x2 - x1) < 4)
        {
            int block = BlockAt(x1);
            if (block >= 0 && block < ItemCount)
            {
                SelectItem(block);
                BlockInvoked?.Invoke(this, block);
            }

            return;
        }

        int first = BlockAt(Math.Min(x1, x2));
        int last = BlockAt(Math.Max(x1, x2));
        if (first >= 0 && last > first)
        {
            RangeSelected?.Invoke(this, (first, last));
        }
    }

    /// <summary>グラフの横方向の割合の位置 (テストでクリックとドラッグの位置を求める)。</summary>
    internal double XOf(double fraction) => fraction * _canvas.ActualWidth;

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (HandleKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>キーの処理 (← / → でブロックを選び、Enter で移動、+ / − で拡大・縮小)。処理したら true。</summary>
    internal bool HandleKey(VirtualKey key)
    {
        int count = ItemCount;

        // フォーカスを受けた時点で先頭のブロックを選ぶ (まだ選ばれていなければ先頭から動かす)。
        int current = Math.Max(0, SelectedBlock);
        switch (key)
        {
            case VirtualKey.Left when count > 0:
                SelectItem(Math.Max(0, current - 1));
                return true;
            case VirtualKey.Right when count > 0:
                SelectItem(Math.Min(count - 1, current + 1));
                return true;
            case VirtualKey.Enter when SelectedBlock >= 0:
                BlockInvoked?.Invoke(this, SelectedBlock);
                return true;
            case VirtualKey.Add or (VirtualKey)187:
                ZoomIn();
                return true;
            case VirtualKey.Subtract or (VirtualKey)189:
                ZoomOut();
                return true;
            default:
                return false;
        }
    }

    private void Redraw()
    {
        _peer?.Invalidate();
        if (Data is not { Count: > 0 } b || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        {
            foreach (Path p in new[] { _hatch, _unreadable, _threshold, _zero, _printable, _line, _detail, _cursor, _selected })
            {
                p.Data = null;
            }

            return;
        }

        _line.Stroke = ChartSupport.Brush("AccentFillColorDefaultBrush", this);
        _detail.Stroke = ChartSupport.Brush("TextFillColorPrimaryBrush", this);
        _threshold.Stroke = ChartSupport.Brush("SystemFillColorCriticalBrush", this);
        _hatch.Stroke = ChartSupport.Brush("SystemFillColorCautionBrush", this);
        _unreadable.Stroke = ChartSupport.Brush("TextFillColorDisabledBrush", this);
        _zero.Stroke = ChartSupport.Brush("TextFillColorSecondaryBrush", this);
        _printable.Stroke = ChartSupport.Brush("TextFillColorSecondaryBrush", this);
        _cursor.Stroke = ChartSupport.Brush("TextFillColorPrimaryBrush", this);
        _selected.Stroke = ChartSupport.Brush("FocusStrokeColorOuterBrush", this);

        (long from, long to) = View;
        _line.Data = Series(b, from, to, i => b.Entropy[i]);
        _zero.Data = ShowZero ? Series(b, from, to, i => b.Zero[i] * MaxEntropy) : null;
        _printable.Data = ShowPrintable ? Series(b, from, to, i => b.Printable[i] * MaxEntropy) : null;
        _detail.Data = DetailBlocks is EntropyBlocks d && d.BlockSize < b.BlockSize ? Series(d, from, to, i => d.Entropy[i]) : null;
        var threshold = new GeometryGroup();
        threshold.Children.Add(new LineGeometry { StartPoint = new Point(0, Y(Threshold)), EndPoint = new Point(_canvas.ActualWidth, Y(Threshold)) });
        _threshold.Data = threshold;

        // しきい値を超えるブロックが続く区間の背景に模様 (ANA-13 の仕様 7)、読めないブロックにグレーの斜線 (「エラー」)。
        var hatch = new GeometryGroup();
        var unreadable = new GeometryGroup();
        int first = b.BlockOf(from);
        int last = Math.Min(b.Count - 1, b.BlockOf(Math.Max(from, to - 1)));
        int run = -1;
        for (int i = first; i <= last + 1; i++)
        {
            bool above = i <= last && b.State[i] == BlockState.Valid && b.Entropy[i] > Threshold;
            if (above && run < 0)
            {
                run = i;
            }
            else if (!above && run >= 0)
            {
                if (i - run >= 2)
                {
                    hatch.Children.Add(ChartSupport.Pattern("Pattern_Diagonal", RectOf(b, run, i), 8));
                }

                run = -1;
            }

            if (i <= last && b.State[i] == BlockState.Unreadable)
            {
                unreadable.Children.Add(ChartSupport.Pattern("Pattern_Cross", RectOf(b, i, i + 1), 4));
            }
        }

        _hatch.Data = hatch;
        _unreadable.Data = unreadable;
        DrawCursor();
    }

    private Rect RectOf(EntropyBlocks b, int first, int lastExclusive)
    {
        double x1 = Math.Max(0, X(b.BlockStart(first)));
        double x2 = Math.Min(_canvas.ActualWidth, X(b.BlockStart(first) + ((long)(lastExclusive - first) * b.BlockSize)));
        return new Rect(x1, 0, Math.Max(0, x2 - x1), _canvas.ActualHeight);
    }

    /// <summary>ブロックの値の折れ線 (計算していない・読めないブロックで線を途切れさせる)。</summary>
    private PathGeometry Series(EntropyBlocks b, long from, long to, Func<int, double> value)
    {
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        int first = b.BlockOf(from);
        int last = Math.Min(b.Count - 1, b.BlockOf(Math.Max(from, to - 1)));

        // 1 ピクセルに複数のブロックが入る場合は間引く (最大でピクセルの列の 2 倍の点)。
        int step = Math.Max(1, (int)((last - first + 1) / Math.Max(1, _canvas.ActualWidth * 2)));
        for (int i = first; i <= last; i += step)
        {
            if (b.State[i] != BlockState.Valid)
            {
                figure = null;
                continue;
            }

            var point = new Point(X(b.BlockStart(i) + (b.BlockLength(i) / 2)), Y(value(i)));
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = point, IsFilled = false };
                geometry.Figures.Add(figure);
                if (b.Count == 1 || step == last - first + 1)
                {
                    figure.Segments.Add(new LineSegment { Point = new Point(point.X + 1, point.Y) });
                }
            }
            else
            {
                figure.Segments.Add(new LineSegment { Point = point });
            }
        }

        return geometry;
    }

    private void DrawCursor()
    {
        if (Data is not { Count: > 0 } b || _canvas.ActualWidth <= 0)
        {
            _cursor.Data = null;
            _selected.Data = null;
            return;
        }

        if (Cursor is long c)
        {
            double x = X(c);
            _cursor.Data = x >= 0 && x <= _canvas.ActualWidth
                ? new LineGeometry { StartPoint = new Point(x, 0), EndPoint = new Point(x, _canvas.ActualHeight) }
                : null;
        }
        else
        {
            _cursor.Data = null;
        }

        _selected.Data = SelectedBlock >= 0 && SelectedBlock < b.Count ? new RectangleGeometry { Rect = RectOf(b, SelectedBlock, SelectedBlock + 1) } : null;
    }

    // ---- IChartItems ----

    public int ItemCount => Data?.Count ?? 0;

    public string ItemName(int index)
    {
        if (Data is not { } b || index < 0 || index >= b.Count)
        {
            return string.Empty;
        }

        string offset = OffsetText?.Invoke(b.BlockStart(index)) ?? "0x" + b.BlockStart(index).ToString("X", CultureInfo.InvariantCulture);
        string value = b.State[index] switch
        {
            BlockState.Valid => b.Entropy[index].ToString("N3", CultureInfo.CurrentCulture),
            BlockState.Unreadable => Loc.Get("Stats_Unreadable_Cell"),
            _ => "—",
        };
        return Loc.Format("Stats_BlockName", (index + 1).ToString("N0", CultureInfo.CurrentCulture), offset, value);
    }

    public string ItemAutomationId(int index) => $"Stats_Block_{index}";

    public Rect ItemBounds(int index) => Data is { } b && index >= 0 && index < b.Count ? RectOf(b, index, index + 1) : default;

    public string ItemStatus(int index) => Data is { } b && index >= 0 && index < b.Count ? "state=" + b.State[index] : string.Empty;

    public bool InvokeItem(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return false;
        }

        SelectItem(index);
        BlockInvoked?.Invoke(this, index);
        return true;
    }

    public int SelectedItem => SelectedBlock;

    public bool CanSelect => true;

    public void SelectItem(int index)
    {
        SelectedBlock = index;
        _peer?.RaiseSelected(index);
        ChartSupport.SetToolTip(this, ItemName(index));
    }
}
