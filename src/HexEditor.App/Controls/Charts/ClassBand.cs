using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Statistics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace HexEditor.App.Controls.Charts;

/// <summary>
/// 分類の区間の割合を示す横長の帯 (ANA-16 の「画面」)。分類ごとに色と模様を変える (色が見えなくても区別できる)。区間は UI オートメーションの
/// 仮想の子で、名前に分類名・開始オフセット・長さ、ItemStatus に模様のリソース名を入れる。分類ごとに 1 つの Path にまとめて描く。
/// </summary>
public sealed partial class ClassBand : UserControl, IChartItems
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(nameof(Result), typeof(object), typeof(ClassBand),
        new PropertyMetadata(null, (d, _) => ((ClassBand)d).Redraw()));

    private readonly Canvas _canvas = new() { Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"] };
    private ChartAutomationPeer? _peer;

    public ClassBand()
    {
        Height = 28;
        Content = _canvas;
        _canvas.SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerMoved += (_, e) => ChartSupport.SetToolTip(this, ItemName(RegionAt(e.GetCurrentPoint(_canvas).Position.X)));
        Tapped += (_, e) =>
        {
            int i = RegionAt(e.GetPosition(_canvas).X);
            if (i >= 0)
            {
                RegionInvoked?.Invoke(this, i);
            }
        };
        AutomationProperties.SetName(this, Loc.Get("Stats_ClassBand"));
    }

    /// <summary>分類の結果 (<see cref="ClassificationResult"/>)。</summary>
    public object? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    public event EventHandler<int>? RegionInvoked;

    private ClassificationResult? Data => Result as ClassificationResult;

    /// <summary>分類の色 (ThemeResource)。</summary>
    public static string BrushKey(DataClass c) => c switch
    {
        DataClass.Zero => "ControlStrongFillColorDisabledBrush",
        DataClass.Constant => "SystemFillColorNeutralBrush",
        DataClass.Text => "SystemFillColorSuccessBrush",
        DataClass.Encrypted => "SystemFillColorCriticalBrush",
        DataClass.Compressed => "SystemFillColorCautionBrush",
        DataClass.Binary => "AccentFillColorDefaultBrush",
        _ => "TextFillColorDisabledBrush",
    };

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ChartAutomationPeer(this, this, nameof(ClassBand));

    private int RegionAt(double x)
    {
        if (Data is not { } r || _canvas.ActualWidth <= 0)
        {
            return -1;
        }

        long logical = (long)(x / _canvas.ActualWidth * r.Ranges.Length);
        for (int i = 0; i < r.Regions.Count; i++)
        {
            if (logical >= r.Regions[i].LogicalStart && logical < r.Regions[i].LogicalStart + r.Regions[i].Length)
            {
                return i;
            }
        }

        return -1;
    }

    private void Redraw()
    {
        _peer?.Invalidate();
        _canvas.Children.Clear();
        if (Data is not { } r || r.Ranges.Length == 0 || _canvas.ActualWidth <= 0)
        {
            return;
        }

        // 分類ごとに 1 つの塗りの Path と 1 つの模様の Path にまとめる (区間の数に関係なく要素は 14 個まで)。
        Brush pattern = ChartSupport.Brush("TextFillColorPrimaryBrush", this);
        foreach (IGrouping<DataClass, int> group in Enumerable.Range(0, r.Regions.Count).GroupBy(i => r.Regions[i].Class))
        {
            var fill = new GeometryGroup();
            var marks = new GeometryGroup();
            foreach (int i in group)
            {
                Rect rect = ItemBounds(i);
                if (rect.Width <= 0)
                {
                    continue;
                }

                fill.Children.Add(new RectangleGeometry { Rect = rect });
                marks.Children.Add(ChartSupport.Pattern(StatisticsViewModel.PatternOf(group.Key), rect, 6));
            }

            _canvas.Children.Add(new Path { Data = fill, Fill = ChartSupport.Brush(BrushKey(group.Key), this) });
            _canvas.Children.Add(new Path { Data = marks, Stroke = pattern, Fill = pattern, StrokeThickness = 1 });
        }
    }

    // ---- IChartItems ----

    public int ItemCount => Data?.Regions.Count ?? 0;

    public string ItemName(int index)
    {
        if (Data is not { } r || index < 0 || index >= r.Regions.Count)
        {
            return string.Empty;
        }

        ClassRegion region = r.Regions[index];
        return Loc.Format("Stats_RegionName", StatisticsViewModel.ClassName(region.Class),
            "0x" + region.Offset.ToString("X", CultureInfo.InvariantCulture), region.Length.ToString("N0", CultureInfo.CurrentCulture));
    }

    public string ItemAutomationId(int index) => $"Stats_Region_{index}";

    public Rect ItemBounds(int index)
    {
        if (Data is not { } r || index < 0 || index >= r.Regions.Count || r.Ranges.Length == 0)
        {
            return default;
        }

        ClassRegion region = r.Regions[index];
        double scale = _canvas.ActualWidth / r.Ranges.Length;
        double x = region.LogicalStart * scale;

        // 小さな区間も 1 ピクセルは描く。
        return new Rect(x, 0, Math.Max(1, region.Length * scale), Math.Max(1, _canvas.ActualHeight));
    }

    public string ItemStatus(int index) =>
        Data is { } r && index >= 0 && index < r.Regions.Count ? "pattern=" + StatisticsViewModel.PatternOf(r.Regions[index].Class) : string.Empty;

    public bool InvokeItem(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            return false;
        }

        RegionInvoked?.Invoke(this, index);
        return true;
    }

    public int SelectedItem => -1;

    public bool CanSelect => false;

    public void SelectItem(int index)
    {
    }
}

/// <summary>凡例の模様の見本 (分類の色と模様)。</summary>
public sealed partial class PatternSwatch : UserControl
{
    public static readonly DependencyProperty ClassProperty = DependencyProperty.Register(nameof(Class), typeof(DataClass), typeof(PatternSwatch),
        new PropertyMetadata(DataClass.None, (d, _) => ((PatternSwatch)d).Redraw()));

    private readonly Canvas _canvas = new();

    public PatternSwatch()
    {
        Width = 28;
        Height = 16;
        Content = _canvas;
        Loaded += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public DataClass Class
    {
        get => (DataClass)GetValue(ClassProperty);
        set => SetValue(ClassProperty, value);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var rect = new Rect(0, 0, Width, Height);
        Brush pattern = ChartSupport.Brush("TextFillColorPrimaryBrush", this);
        _canvas.Children.Add(new Path
        {
            Data = new RectangleGeometry { Rect = rect },
            Fill = ChartSupport.Brush(ClassBand.BrushKey(Class), this),
            Stroke = pattern,
            StrokeThickness = 1,
        });
        _canvas.Children.Add(new Path { Data = ChartSupport.Pattern(StatisticsViewModel.PatternOf(Class), rect, 5), Stroke = pattern, Fill = pattern });
    }
}
