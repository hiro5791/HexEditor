using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Statistics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.System;

namespace HexEditor.App.Controls.Charts;

/// <summary>
/// ヒストグラム (ANA-10 の仕様 5)。棒は 1 つの Path に描く (ビンごとの UI 要素は作らない)。ビンが表示の幅より多い場合は
/// 1 ピクセルの列ごとに最大の件数を描く。棒は UI オートメーションの仮想の子 (名前は値と件数) で、Invoke はダブルクリックと同じ。
/// キーボードでは ← / → で棒を選び、Enter で次の出現位置へ移動する。
/// </summary>
public sealed partial class HistogramChart : UserControl, IChartItems
{
    public static readonly DependencyProperty HistogramProperty = DependencyProperty.Register(nameof(Histogram), typeof(object), typeof(HistogramChart),
        new PropertyMetadata(null, (d, _) => ((HistogramChart)d).Redraw()));

    public static readonly DependencyProperty LogScaleProperty = DependencyProperty.Register(nameof(LogScale), typeof(bool), typeof(HistogramChart),
        new PropertyMetadata(false, (d, _) => ((HistogramChart)d).Redraw()));

    public static readonly DependencyProperty ShowPercentProperty = DependencyProperty.Register(nameof(ShowPercent), typeof(bool), typeof(HistogramChart),
        new PropertyMetadata(false, (d, _) => ((HistogramChart)d).Redraw()));

    private readonly Grid _root = new();
    private readonly Path _bars = new();
    private readonly Path _selection = new() { StrokeThickness = 2 };
    private readonly TextBlock _axisTop = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _axisLeft = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _axisRight = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Canvas _canvas = new();
    private ChartAutomationPeer? _peer;
    private int _selected = -1;

    public HistogramChart()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        MinHeight = 120;
        _canvas.Children.Add(_bars);
        _canvas.Children.Add(_selection);
        _root.Children.Add(_canvas);
        _root.Children.Add(_axisTop);
        _root.Children.Add(_axisLeft);
        _root.Children.Add(_axisRight);
        _root.Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        _canvas.Margin = new Thickness(0, 16, 0, 16);
        Content = _root;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        Loaded += (_, _) => Redraw();
        DoubleTapped += (_, e) => InvokeItem(BinAt(e.GetPosition(_canvas).X));
        RightTapped += (_, e) =>
        {
            int bin = BinAt(e.GetPosition(_canvas).X);
            if (bin >= 0)
            {
                Select(bin);
                BinContextRequested?.Invoke(this, (bin, e.GetPosition(this)));
                e.Handled = true;
            }
        };
        PointerMoved += (_, e) => ChartSupport.SetToolTip(this, ItemName(BinAt(e.GetCurrentPoint(_canvas).Position.X)));
        AutomationProperties.SetName(this, Loc.Get("Stats_HistogramChart"));
    }

    /// <summary>描く <see cref="Core.Statistics.Histogram"/>。</summary>
    public object? Histogram
    {
        get => GetValue(HistogramProperty);
        set => SetValue(HistogramProperty, value);
    }

    public bool LogScale
    {
        get => (bool)GetValue(LogScaleProperty);
        set => SetValue(LogScaleProperty, value);
    }

    public bool ShowPercent
    {
        get => (bool)GetValue(ShowPercentProperty);
        set => SetValue(ShowPercentProperty, value);
    }

    /// <summary>ビンの表示 (値または範囲)。</summary>
    public Func<Histogram, int, string>? BinLabel { get; set; }

    /// <summary>棒がダブルクリックされた (またはキーボードの Enter、UI オートメーションの Invoke)。</summary>
    public event EventHandler<int>? BinInvoked;

    /// <summary>棒の右クリック (「この値をすべて検索」のメニュー)。</summary>
    public event EventHandler<(int Bin, Point Position)>? BinContextRequested;

    private Histogram? Data => Histogram as Histogram;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ChartAutomationPeer(this, this, nameof(HistogramChart));

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        int count = Data?.BinCount ?? 0;
        if (count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                Select(Math.Max(0, _selected - 1));
                e.Handled = true;
                break;
            case VirtualKey.Right:
                Select(Math.Min(count - 1, _selected + 1));
                e.Handled = true;
                break;
            case VirtualKey.Home:
                Select(0);
                e.Handled = true;
                break;
            case VirtualKey.End:
                Select(count - 1);
                e.Handled = true;
                break;
            case VirtualKey.Enter when _selected >= 0:
                InvokeItem(_selected);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }

    private void Select(int bin)
    {
        _selected = bin;
        DrawSelection();
        _peer?.RaiseSelected(bin);
        ChartSupport.SetToolTip(this, ItemName(bin));
    }

    /// <summary>描画面の x 座標のビン (範囲外は −1)。</summary>
    internal int BinAt(double x)
    {
        int count = Data?.BinCount ?? 0;
        double width = _canvas.ActualWidth;
        if (count == 0 || width <= 0 || x < 0 || x >= width)
        {
            return -1;
        }

        return Math.Clamp((int)(x / width * count), 0, count - 1);
    }

    /// <summary>棒の中央の x 座標 (テストで位置を求める)。</summary>
    internal double BinCenter(int bin) => Data is { BinCount: > 0 } h ? (bin + 0.5) * _canvas.ActualWidth / h.BinCount : 0;

    private double Scale(long count, long max, long total)
    {
        if (count <= 0 || max <= 0)
        {
            return 0;
        }

        if (LogScale)
        {
            return Math.Log10(count + 1) / Math.Log10(max + 1);
        }

        return (double)count / max;
    }

    private void Redraw()
    {
        _peer?.Invalidate();
        if (Shown() is not { } h || _canvas.ActualWidth <= 0)
        {
            _bars.Data = null;
            _selection.Data = null;
            return;
        }

        double width = _canvas.ActualWidth;
        double height = Math.Max(1, ActualHeight - 32);
        _bars.Fill = ChartSupport.Brush("AccentFillColorDefaultBrush", this);
        _selection.Stroke = ChartSupport.Brush("TextFillColorPrimaryBrush", this);
        foreach (TextBlock t in new[] { _axisTop, _axisLeft, _axisRight })
        {
            t.Foreground = ChartSupport.Brush("TextFillColorSecondaryBrush", this);
        }

        long max = h.Counts.Length == 0 ? 0 : h.Counts.Max();
        long total = h.Total;
        var group = new GeometryGroup();
        int columns = (int)Math.Min(h.BinCount, Math.Max(1, width));
        for (int c = 0; c < columns; c++)
        {
            // 列に入るビンのうち最大の件数を描く。
            int from = (int)((long)c * h.BinCount / columns);
            int to = (int)((long)(c + 1) * h.BinCount / columns);
            long value = 0;
            for (int b = from; b < Math.Max(from + 1, to); b++)
            {
                value = Math.Max(value, h.Counts[b]);
            }

            double bar = Scale(value, max, total) * height;
            if (bar <= 0)
            {
                continue;
            }

            double x = c * width / columns;
            double w = Math.Max(1, (width / columns) - (columns <= 256 ? 1 : 0));
            group.Children.Add(new RectangleGeometry { Rect = new Rect(x, height - bar, w, bar) });
        }

        _bars.Data = group;
        CultureInfo culture = CultureInfo.CurrentCulture;
        _axisTop.Text = ShowPercent
            ? (total == 0 ? 0 : max * 100.0 / total).ToString("N2", culture) + "%" + (LogScale ? " (log)" : string.Empty)
            : max.ToString("N0", culture) + (LogScale ? " (log)" : string.Empty);
        _axisLeft.Text = h.BinCount > 0 ? Label(h, 0) : string.Empty;
        _axisRight.Text = h.BinCount > 0 ? Label(h, h.BinCount - 1) : string.Empty;
        DrawSelection();
    }

    private Histogram? Shown() => Data is { BinCount: > 0 } h ? h : null;

    private void DrawSelection()
    {
        if (Shown() is not { } h || _selected < 0 || _selected >= h.BinCount)
        {
            _selection.Data = null;
            return;
        }

        Rect r = ItemBounds(_selected);
        Point origin = new(r.X, r.Y - 16);
        _selection.Data = new RectangleGeometry { Rect = new Rect(origin.X, 0, Math.Max(2, r.Width), Math.Max(1, ActualHeight - 32)) };
    }

    private string Label(Histogram h, int bin) => BinLabel?.Invoke(h, bin) ?? bin.ToString(CultureInfo.CurrentCulture);

    // ---- IChartItems ----

    public int ItemCount => Shown()?.BinCount ?? 0;

    public string ItemName(int index)
    {
        if (Shown() is not { } h || index < 0 || index >= h.BinCount)
        {
            return string.Empty;
        }

        long total = h.Total;
        return Loc.Format("Stats_BarName", Label(h, index), h.Counts[index].ToString("N0", CultureInfo.CurrentCulture),
            (total == 0 ? 0 : h.Counts[index] * 100.0 / total).ToString("N2", CultureInfo.CurrentCulture));
    }

    public string ItemAutomationId(int index) =>
        Shown() is { ValueBins: true, Type: ElementType.U8 } ? $"Stats_Bar_{index:X2}" : $"Stats_Bar_{index}";

    public Rect ItemBounds(int index)
    {
        if (Shown() is not { } h || _canvas.ActualWidth <= 0)
        {
            return default;
        }

        double w = _canvas.ActualWidth / h.BinCount;
        return new Rect(index * w, 16, Math.Max(1, w), Math.Max(1, ActualHeight - 32));
    }

    public string ItemStatus(int index) => Shown() is { } h && index >= 0 && index < h.BinCount ? "count=" + h.Counts[index].ToString(CultureInfo.InvariantCulture) : string.Empty;

    public bool InvokeItem(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return false;
        }

        Select(index);
        BinInvoked?.Invoke(this, index);
        return true;
    }

    public int SelectedItem => _selected;

    public bool CanSelect => false;

    public void SelectItem(int index) => Select(index);

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        base.OnPointerPressed(e);
    }
}
