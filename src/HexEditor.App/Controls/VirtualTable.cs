using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>仮想化した表の列。<see cref="Width"/> は点。最後の列は残りの幅を使う。</summary>
public sealed record VirtualColumn(string Header, double Width, bool Mono = false);

/// <summary>
/// 結果一覧 (00 の 9 章) の表。行は見えている分だけ作り、スクロールは自前のスクロールバーの行番号で行うので、100 万件を超える行数でも
/// 画面の高さの制限に当たらず、スクロールが止まらない (SearchResultsPanel と同じ考え方)。行の内容は <see cref="RowProvider"/> が必要なときに作る。
/// 矢印・PageUp / PageDown・Home / End で選択を動かし、Enter か行のダブルクリックで <see cref="Activated"/>、Delete で <see cref="DeleteRequested"/> を出す。
/// 列の幅の合計が表の幅より広いときは下に横のスクロールバーを出す (左右の矢印キー、Shift+ホイール、横ホイールでも動く)。
/// </summary>
public sealed class VirtualTable : UserControl
{
    private const double RowHeight = 28;
    private const double HorizontalStep = 40;

    private readonly Canvas _headerHost = new() { Height = RowHeight };
    private readonly Grid _header = new() { Height = RowHeight, ColumnSpacing = 0 };
    private readonly RectangleGeometry _headerClip = new();
    private readonly RectangleGeometry _bodyClip = new();
    private readonly Canvas _body = new() { Background = new SolidColorBrush() };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 10, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, SmallChange = HorizontalStep, IndicatorMode = ScrollingIndicatorMode.MouseIndicator, Visibility = Visibility.Collapsed };
    private readonly List<Grid> _rows = [];
    private readonly string _automationId;
    private IReadOnlyList<VirtualColumn> _columns = [];
    private long _count;
    private long _top;
    private double _left;
    private long _selected = -1;
    private bool _syncing;

    public VirtualTable(string automationId)
    {
        _automationId = automationId;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_body, 1);
        Grid.SetRow(_scroll, 1);
        Grid.SetColumn(_scroll, 1);
        Grid.SetRow(_horizontal, 2);
        _header.SetValue(Panel.BackgroundProperty, Application.Current.Resources["LayerFillColorDefaultBrush"]);
        _headerHost.Children.Add(_header);
        _headerHost.Clip = _headerClip;
        _body.Clip = _bodyClip;
        AutomationProperties.SetAutomationId(_horizontal, automationId + "_HorizontalScroll");
        root.Children.Add(_headerHost);
        root.Children.Add(_body);
        root.Children.Add(_scroll);
        root.Children.Add(_horizontal);
        Content = root;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        AutomationProperties.SetAutomationId(this, automationId);
        AutomationProperties.SetAccessibilityView(_header, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content);
        _body.SizeChanged += (_, _) => Layout();
        _scroll.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                _top = (long)e.NewValue;
                Layout();
            }
        };
        _horizontal.ValueChanged += (_, e) =>
        {
            if (!_syncing)
            {
                _left = e.NewValue;
                Layout();
            }
        };
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
        _body.Tapped += OnBodyTapped;
        _body.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.GetPosition(_body).Y) is { } row && row < _count)
            {
                Select(row);
                Activated?.Invoke(this, row);
            }
        };
        _body.RightTapped += OnBodyRightTapped;
        ContextRequested += (_, e) =>
        {
            // キーボード (Shift+F10・アプリケーションキー) の右クリックメニュー: 選んでいる行のメニューを行の位置に出す。
            if (BuildContextMenu is { } build && !e.TryGetPosition(_body, out Windows.Foundation.Point _))
            {
                var menu = new MenuFlyout();
                build(_selected, menu);
                if (menu.Items.Count > 0)
                {
                    double y = _selected >= _top ? (_selected - _top) * RowHeight : 0;
                    menu.ShowAt(_body, new Windows.Foundation.Point(16, Math.Max(0, y) + (RowHeight / 2)));
                    e.Handled = true;
                }
            }
        };
        GotFocus += (_, _) => Layout();
        LostFocus += (_, _) => Layout();
    }

    /// <summary>行の内容を返す (行番号 → セルの文字列。列の数と同じ長さ)。</summary>
    public Func<long, IReadOnlyList<string>> RowProvider { get; set; } = _ => [];

    /// <summary>行のオートメーションの名前 (既定は全セルをつなげたもの)。</summary>
    public Func<long, string>? RowName { get; set; }

    /// <summary>右クリックされた行の右クリックメニューを作る (行がないところでは -1)。</summary>
    public Action<long, MenuFlyout>? BuildContextMenu { get; set; }

    /// <summary>行の数。</summary>
    public long Count => _count;

    public long SelectedIndex => _selected;

    /// <summary>先頭に表示している行。</summary>
    public long TopIndex => _top;

    /// <summary>見えている行の数。</summary>
    public int VisibleRows => Math.Max(1, (int)(_body.ActualHeight / RowHeight));

    public event EventHandler? SelectionChanged;

    public event EventHandler<long>? Activated;

    public event EventHandler<long>? DeleteRequested;

    /// <summary>列の見出しを押した (並べ替え)。購読する場合は <see cref="SetColumns"/> の前に <see cref="SortableHeaders"/> を true にする。</summary>
    public event EventHandler<int>? HeaderInvoked;

    /// <summary>列の見出しをボタンにする (押すと <see cref="HeaderInvoked"/>)。</summary>
    public bool SortableHeaders { get; set; }

    public void SetColumns(IReadOnlyList<VirtualColumn> columns)
    {
        _columns = columns;
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        for (int i = 0; i < columns.Count; i++)
        {
            _header.ColumnDefinitions.Add(ColumnOf(i));
            var text = new TextBlock
            {
                Text = columns[i].Header,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            AutomationProperties.SetAutomationId(text, $"{_automationId}_Header{i}");
            FrameworkElement cell = text;
            if (SortableHeaders)
            {
                int column = i;
                var button = new HyperlinkButton { Content = columns[i].Header, Padding = new Thickness(8, 0, 8, 0), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                AutomationProperties.SetAutomationId(button, $"{_automationId}_Header{i}");
                AutomationProperties.SetName(button, Services.Loc.Format("ToolTable_SortBy", columns[i].Header));
                button.Click += (_, _) => HeaderInvoked?.Invoke(this, column);
                cell = button;
            }

            Grid.SetColumn(cell, i);
            _header.Children.Add(cell);
        }

        _rows.Clear();
        _body.Children.Clear();
        Layout();
    }

    private ColumnDefinition ColumnOf(int i) =>
        i == _columns.Count - 1 ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = _columns[i].Width } : new ColumnDefinition { Width = new GridLength(_columns[i].Width) };

    /// <summary>行の数が変わった・内容が変わった。選択は行番号のまま保つ (範囲外なら外す)。</summary>
    public void Refresh(long count)
    {
        _count = Math.Max(0, count);
        if (_selected >= _count)
        {
            _selected = -1;
        }

        _top = Math.Clamp(_top, 0, Math.Max(0, _count - VisibleRows));
        Layout();
    }

    public void Select(long index)
    {
        index = _count == 0 ? -1 : Math.Clamp(index, 0, _count - 1);
        if (index == _selected)
        {
            return;
        }

        _selected = index;
        if (index >= 0)
        {
            ScrollIntoView(index);
        }

        Layout();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ScrollIntoView(long index)
    {
        int visible = VisibleRows;
        if (index < _top)
        {
            _top = index;
        }
        else if (index >= _top + visible)
        {
            _top = index - visible + 1;
        }

        Layout();
    }

    private long? RowAt(double y) => y < 0 ? null : _top + (long)(y / RowHeight);

    private void OnBodyTapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        if (RowAt(e.GetPosition(_body).Y) is { } row && row < _count)
        {
            Select(row);
        }
    }

    private void OnBodyRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        long row = RowAt(e.GetPosition(_body).Y) is { } r && r < _count ? r : -1;
        if (row >= 0)
        {
            Select(row);
        }

        if (BuildContextMenu is { } build)
        {
            var menu = new MenuFlyout();
            build(row, menu);
            if (menu.Items.Count > 0)
            {
                menu.ShowAt(_body, e.GetPosition(_body));
                e.Handled = true;
            }
        }
    }

    /// <summary>列の幅の合計 (横のスクロールの全体の幅)。表の幅より狭ければ表の幅。</summary>
    private double ContentWidth => Math.Max(Math.Max(0, _body.ActualWidth), _columns.Sum(c => c.Width));

    private bool CanScrollHorizontally => ContentWidth > _body.ActualWidth;

    private void ScrollHorizontally(double delta)
    {
        _left += delta;
        Layout();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        int delta = properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (properties.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            if (CanScrollHorizontally)
            {
                ScrollHorizontally((properties.IsHorizontalMouseWheel ? 1 : -1) * Math.Sign(delta) * HorizontalStep * 3);
                e.Handled = true;
            }

            return;
        }

        _top = Math.Clamp(_top - Math.Sign(delta) * 3, 0, Math.Max(0, _count - VisibleRows));
        Layout();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        long page = Math.Max(1, VisibleRows - 1);
        switch (e.Key)
        {
            case VirtualKey.Down:
                Select(_selected < 0 ? 0 : _selected + 1);
                break;
            case VirtualKey.Up:
                Select(_selected < 0 ? 0 : _selected - 1);
                break;
            case VirtualKey.PageDown:
                Select(_selected < 0 ? 0 : _selected + page);
                break;
            case VirtualKey.PageUp:
                Select(_selected < 0 ? 0 : _selected - page);
                break;
            case VirtualKey.Home:
                Select(0);
                break;
            case VirtualKey.End:
                Select(_count - 1);
                break;
            case VirtualKey.Enter when _selected >= 0:
                Activated?.Invoke(this, _selected);
                break;
            case VirtualKey.Delete when _selected >= 0:
                DeleteRequested?.Invoke(this, _selected);
                break;
            case VirtualKey.Left when CanScrollHorizontally:
                ScrollHorizontally(FlowDirection == FlowDirection.RightToLeft ? HorizontalStep : -HorizontalStep);
                break;
            case VirtualKey.Right when CanScrollHorizontally:
                ScrollHorizontally(FlowDirection == FlowDirection.RightToLeft ? -HorizontalStep : HorizontalStep);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Layout()
    {
        int visible = VisibleRows + 1;
        long maxTop = Math.Max(0, _count - VisibleRows);
        _top = Math.Clamp(_top, 0, maxTop);
        _syncing = true;
        _scroll.Minimum = 0;
        _scroll.Maximum = maxTop;
        _scroll.ViewportSize = VisibleRows;
        _scroll.LargeChange = Math.Max(1, VisibleRows - 1);
        _scroll.Value = _top;
        _scroll.Visibility = maxTop > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 横: 列が表の幅に収まらないときは、見出しと行を左にずらす (キャンバスの外は切り取る。右から左の表示ではキャンバスごと反転する)
        double viewport = Math.Max(0, _body.ActualWidth);
        double content = ContentWidth;
        double maxLeft = content - viewport;
        _left = Math.Clamp(_left, 0, maxLeft);
        _horizontal.Minimum = 0;
        _horizontal.Maximum = maxLeft;
        _horizontal.ViewportSize = viewport;
        _horizontal.LargeChange = Math.Max(HorizontalStep, viewport - HorizontalStep);
        _horizontal.Value = _left;
        _horizontal.Visibility = maxLeft > 0 ? Visibility.Visible : Visibility.Collapsed;
        _header.Width = content;
        Canvas.SetLeft(_header, -_left);
        _headerClip.Rect = new Windows.Foundation.Rect(0, 0, viewport, RowHeight);
        _bodyClip.Rect = new Windows.Foundation.Rect(0, 0, viewport, Math.Max(0, _body.ActualHeight));
        _syncing = false;

        while (_rows.Count < visible && _columns.Count > 0)
        {
            Grid row = NewRow();
            _rows.Add(row);
            _body.Children.Add(row);
        }

        bool focused = FocusState != FocusState.Unfocused;
        for (int i = 0; i < _rows.Count; i++)
        {
            Grid row = _rows[i];
            long index = _top + i;
            if (i >= visible || index >= _count)
            {
                row.Visibility = Visibility.Collapsed;
                continue;
            }

            row.Visibility = Visibility.Visible;
            Canvas.SetTop(row, i * RowHeight);
            Canvas.SetLeft(row, -_left);
            row.Width = content;
            IReadOnlyList<string> cells = RowProvider(index);
            for (int c = 0; c < _columns.Count; c++)
            {
                var text = (TextBlock)row.Children[c];
                text.Text = c < cells.Count ? cells[c] : string.Empty;
            }

            bool selected = index == _selected;
            row.Background = selected ? (Brush)Application.Current.Resources[focused ? "ListViewItemBackgroundSelected" : "ListViewItemBackgroundSelectedPressed"]
                : (Brush)(index % 2 == 0 ? new SolidColorBrush() : Application.Current.Resources["SubtleFillColorSecondaryBrush"]);
            Brush foreground = selected ? (Brush)Application.Current.Resources["ListViewItemForegroundSelected"] : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            foreach (TextBlock text in row.Children.OfType<TextBlock>())
            {
                text.Foreground = foreground;
            }

            AutomationProperties.SetName(row, RowName?.Invoke(index) ?? string.Join(", ", cells));
            AutomationProperties.SetAutomationId(row, $"{_automationId}_Row{index}");
        }
    }

    private Grid NewRow()
    {
        var row = new Grid { Height = RowHeight, IsHitTestVisible = true };
        for (int i = 0; i < _columns.Count; i++)
        {
            row.ColumnDefinitions.Add(ColumnOf(i));
            var text = new TextBlock
            {
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = false,
                FlowDirection = _columns[i].Mono ? FlowDirection.LeftToRight : FlowDirection,
            };
            if (_columns[i].Mono)
            {
                text.FontFamily = DialogParts.Mono;
            }

            Grid.SetColumn(text, i);
            row.Children.Add(text);
        }

        return row;
    }
}
