using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Controls;

/// <summary>
/// タブの内容の領域を 2 つのペインに分ける (VIEW-37)。1 つ目のペインの Hex ビューは XAML に置き、分割すると 2 つ目の Hex ビューと分割バー
/// (幅 4 epx。ドラッグで動かし、ダブルクリックで 1:1。仕様 5) を加える。操作中のペインには 2 px のアクセント色の枠を付ける (仕様 6)。
/// </summary>
public sealed partial class PaneHost : Grid
{
    /// <summary>分割バーの幅 (VIEW-37 の「画面」)。</summary>
    public const double SplitterSize = 4;

    private readonly Border _splitter;
    private readonly Rectangle _activeFrame;
    private HexView? _second;
    private bool _sideBySide;
    private double _ratio = 0.5;
    private bool _dragging;
    private Windows.Foundation.Point _dragStart;
    private double _dragStartRatio;

    public PaneHost()
    {
        _splitter = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_splitter, "PaneSplitter");
        AutomationProperties.SetName(_splitter, Services.Loc.Get("Menu_View_SplitMenu/Text"));
        _splitter.PointerPressed += Splitter_PointerPressed;
        _splitter.PointerMoved += Splitter_PointerMoved;
        _splitter.PointerReleased += Splitter_PointerReleased;
        _splitter.PointerCaptureLost += (_, _) => _dragging = false;
        _splitter.DoubleTapped += (_, _) => SetRatio(0.5);
        _activeFrame = new Rectangle
        {
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        AutomationProperties.SetAccessibilityView(_activeFrame, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Loaded += (_, _) =>
        {
            if (!Children.Contains(_splitter))
            {
                Children.Add(_splitter);
                Children.Add(_activeFrame);
            }
        };
    }

    /// <summary>1 つ目のペインの Hex ビュー (XAML に置いたもの)。</summary>
    public HexView? First => Children.OfType<HexView>().FirstOrDefault(v => !ReferenceEquals(v, _second));

    /// <summary>2 つ目のペインの Hex ビュー (分割していなければ null)。</summary>
    public HexView? Second => _second;

    /// <summary>分割の比率 (1 つ目のペインの割合)。分割バーの操作で変わる。</summary>
    public double Ratio => _ratio;

    /// <summary>分割バーで比率を変えた。</summary>
    public event EventHandler<double>? RatioChanged;

    /// <summary>
    /// ペインの構成を、ビューの状態に合わせる。<paramref name="second"/> が null なら分割を解除する。<paramref name="createSecond"/> は 2 つ目の
    /// Hex ビューを作る (まだなければ)。
    /// </summary>
    public void Update(EditorState primary, EditorState? second, bool sideBySide, double ratio, int activePane, Func<HexView> createSecond)
    {
        if (First is { } first && !ReferenceEquals(first.Editor, primary))
        {
            first.Editor = primary;
        }

        if (second is null)
        {
            if (_second is not null)
            {
                Children.Remove(_second);
                _second.Editor = null;
                _second = null;
            }

            _splitter.Visibility = Visibility.Collapsed;
            _activeFrame.Visibility = Visibility.Collapsed;
            RowDefinitions.Clear();
            ColumnDefinitions.Clear();
            if (First is { } only)
            {
                SetRow(only, 0);
                SetColumn(only, 0);
            }

            return;
        }

        if (_second is null)
        {
            _second = createSecond();
            Children.Add(_second);
        }

        if (!ReferenceEquals(_second.Editor, second))
        {
            _second.Editor = second;
        }

        _sideBySide = sideBySide;
        _ratio = Math.Clamp(ratio, 0.05, 0.95);
        Layout();
        ShowActive(activePane);
    }

    /// <summary>操作中のペインに枠を付ける。</summary>
    public void ShowActive(int pane)
    {
        if (_second is null || First is not { } first)
        {
            _activeFrame.Visibility = Visibility.Collapsed;
            return;
        }

        HexView target = pane == 1 ? _second : first;
        _activeFrame.Visibility = Visibility.Visible;
        SetRow(_activeFrame, GetRow(target));
        SetColumn(_activeFrame, GetColumn(target));
        if (!Children.Contains(_activeFrame))
        {
            Children.Add(_activeFrame);
        }
    }

    private void Layout()
    {
        if (!Children.Contains(_splitter))
        {
            Children.Add(_splitter);
        }

        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        HexView first = First!;
        var a = new GridLength(_ratio, GridUnitType.Star);
        var b = new GridLength(1 - _ratio, GridUnitType.Star);
        if (_sideBySide)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = a, MinWidth = 120 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SplitterSize) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = b, MinWidth = 120 });
            Place(first, 0, 0);
            Place(_splitter, 0, 1);
            Place(_second!, 0, 2);
            _splitter.Width = SplitterSize;
            _splitter.Height = double.NaN;
        }
        else
        {
            // 最小の大きさは 3 行分 (仕様 5)。
            double minHeight = 3 * Math.Max(16, first.RowHeight) + 2 * Math.Max(16, first.RowHeight);
            RowDefinitions.Add(new RowDefinition { Height = a, MinHeight = minHeight });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(SplitterSize) });
            RowDefinitions.Add(new RowDefinition { Height = b, MinHeight = minHeight });
            Place(first, 0, 0);
            Place(_splitter, 1, 0);
            Place(_second!, 2, 0);
            _splitter.Height = SplitterSize;
            _splitter.Width = double.NaN;
        }

        _splitter.Visibility = Visibility.Visible;

        static void Place(FrameworkElement e, int row, int column)
        {
            SetRow(e, row);
            SetColumn(e, column);
        }
    }

    private void SetRatio(double ratio)
    {
        _ratio = Math.Clamp(ratio, 0.05, 0.95);
        if (_second is not null)
        {
            Layout();
        }

        RatioChanged?.Invoke(this, _ratio);
    }

    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetCurrentPoint(this).Position;
        _dragStartRatio = _ratio;
        _splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Windows.Foundation.Point p = e.GetCurrentPoint(this).Position;
        double size = (_sideBySide ? ActualWidth : ActualHeight) - SplitterSize;
        if (size > 0)
        {
            double delta = (_sideBySide ? p.X - _dragStart.X : p.Y - _dragStart.Y) / size;
            SetRatio(_dragStartRatio + delta);
        }

        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        _splitter.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>分割バーのダブルクリックと同じ処理 (テスト用)。</summary>
    internal void ResetRatio() => SetRatio(0.5);
}
