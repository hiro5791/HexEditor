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
/// </summary>
public sealed class VirtualTable : UserControl
{
    private const double RowHeight = 28;

    private readonly Grid _header = new() { Height = RowHeight, ColumnSpacing = 0 };
    private readonly Canvas _body = new() { Background = new SolidColorBrush() };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 10, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private readonly List<Grid> _rows = [];
    private readonly string _automationId;
    private IReadOnlyList<VirtualColumn> _columns = [];
    private long _count;
    private long _top;
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
        Grid.SetRow(_body, 1);
        Grid.SetRow(_scroll, 1);
        Grid.SetColumn(_scroll, 1);
        _header.SetValue(Panel.BackgroundProperty, Application.Current.Resources["LayerFillColorDefaultBrush"]);
        root.Children.Add(_header);
        root.Children.Add(_body);
        root.Children.Add(_scroll);
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
            Grid.SetColumn(text, i);
            _header.Children.Add(text);
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

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0)
        {
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
            row.Width = Math.Max(0, _body.ActualWidth);
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
