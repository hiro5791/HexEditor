using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls.Tools;

/// <summary>状態の列のセル: アイコンと文字(色に加えて、アイコンと文字で区別する。TOOL-19 の仕様 3)。</summary>
internal sealed record StatusCell(string Glyph, string Text, string BrushKey);

/// <summary>結果一覧の列。<see cref="Status"/> が指定された列は、アイコン付きの状態のセルにする。</summary>
internal sealed record ToolColumn(string Header, double Width, Func<object, string> Text, Func<object, IComparable>? SortKey = null,
    Func<object, StatusCell>? Status = null);

/// <summary>
/// 結果一覧の部品 (00-overview 9 章の共通の結果一覧のうち、チェックサムの検証とフォルダの比較が使うもの): 仮想化した一覧、列の見出しのクリックによる
/// 並べ替え、状態による絞り込み、行の起動 (Enter・ダブルクリック)、右クリックメニュー、CSV / JSON への書き出し。
/// 結果は確定したものから <see cref="Add"/> で足せる。
/// </summary>
internal sealed partial class ToolResultTable : UserControl, Panels.IPanelContent
{
    private readonly string _prefix;
    private readonly List<object> _all = [];
    private readonly ObservableCollection<object> _view = [];
    private readonly ListView _list = new();
    private readonly Grid _header = new();
    private readonly ComboBox _filter = new();
    private readonly TextBlock _summary = new();
    private readonly StackPanel _extra = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private IReadOnlyList<ToolColumn> _columns = [];
    private IReadOnlyList<(string Label, Predicate<object>? Match)> _filters = [];
    private int _sortColumn = -1;
    private bool _sortDescending;
    private bool _resortPending;

    public ToolResultTable(string idPrefix)
    {
        _prefix = idPrefix;
        Background = ToolParts.ThemeBrush("LayerFillColorDefaultBrush");

        _filter.MinWidth = 160;
        AutomationProperties.SetAutomationId(_filter, idPrefix + "_Filter");
        AutomationProperties.SetName(_filter, Loc.Get("ToolTable_Filter"));
        _filter.SelectionChanged += (_, _) => ApplyFilter();

        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(_summary, idPrefix + "_Summary");

        Button csv = ToolParts.TextButton(idPrefix + "_ExportCsv", "ToolTable_ExportCsv", () => ExportRequested?.Invoke(this, "csv"));
        Button json = ToolParts.TextButton(idPrefix + "_ExportJson", "ToolTable_ExportJson", () => ExportRequested?.Invoke(this, "json"));

        var top = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 4) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_summary);
        Grid.SetColumn(_extra, 1);
        top.Children.Add(_extra);
        Grid.SetColumn(_filter, 2);
        top.Children.Add(_filter);
        Grid.SetColumn(csv, 3);
        top.Children.Add(csv);
        Grid.SetColumn(json, 4);
        top.Children.Add(json);

        _list.ItemsSource = _view;

        // 行は、空の Grid のテンプレートに、ContainerContentChanging でセルを作る (Content を直接差し替えると、既定の表示 (行の ToString) が
        // 残って、セルが見えない)。
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid /></DataTemplate>");
        _list.SelectionMode = ListViewSelectionMode.Extended;
        _list.IsItemClickEnabled = false;
        _list.ContainerContentChanging += List_ContainerContentChanging;
        _list.DoubleTapped += (_, _) => ActivateSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && _list.SelectedItem is not null)
            {
                ActivateSelected();
                e.Handled = true;
            }
        };
        _list.RightTapped += List_RightTapped;
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetAutomationId(_list, idPrefix + "_List");
        AutomationProperties.SetName(_list, Loc.Get("ToolTable_List"));

        var root = new Grid { RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(top);
        _header.Padding = new Thickness(12, 0, 12, 0);
        Grid.SetRow(_header, 1);
        root.Children.Add(_header);
        Grid.SetRow(_list, 2);
        root.Children.Add(_list);
        Content = root;
        FlowDirection = FlowDirection.LeftToRight;
    }

    /// <summary>行を起動した (Enter、ダブルクリック)。</summary>
    public event EventHandler<object>? RowActivated;

    /// <summary>選択している行が変わった。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>行を選ぶ (テスト・操作の続きに使う)。</summary>
    public void SelectRow(object? row) => _list.SelectedItem = row;

    /// <summary>CSV / JSON の書き出しを求められた(引数は "csv" か "json")。</summary>
    public event EventHandler<string>? ExportRequested;

    /// <summary>右クリックメニューの項目。</summary>
    public List<(string Label, Func<object, bool> Enabled, Action<object> Run)> ContextItems { get; } = [];

    /// <summary>上部の右に置く追加の部品(キャンセルのボタンなど)。</summary>
    public StackPanel ExtraControls => _extra;

    public IReadOnlyList<object> Rows => _all;

    /// <summary>絞り込みのあとに見えている行。</summary>
    public IReadOnlyList<object> VisibleRows => _view;

    public object? SelectedRow => _list.SelectedItem;

    public bool FocusContent() => _list.Focus(FocusState.Programmatic);

    public void SetSummary(string text) => _summary.Text = text;

    public string Summary => _summary.Text;

    /// <summary>列と、絞り込みの選択肢(先頭が既定。<c>null</c> の述語はすべて表示)を設定する。</summary>
    public void Configure(IReadOnlyList<ToolColumn> columns, IReadOnlyList<(string Label, Predicate<object>? Match)> filters)
    {
        _columns = columns;
        _filters = filters;
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        for (int i = 0; i < columns.Count; i++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = ColumnWidth(columns[i]) });
            int captured = i;
            var button = new HyperlinkButton
            {
                Content = columns[i].Header,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            AutomationProperties.SetAutomationId(button, _prefix + "_Header" + i.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(button, Loc.Format("ToolTable_SortBy", columns[i].Header));
            button.Click += (_, _) => SortBy(captured);
            Grid.SetColumn(button, i);
            _header.Children.Add(button);
        }

        _filter.Items.Clear();
        foreach ((string label, _) in filters)
        {
            _filter.Items.Add(label);
        }

        _filter.SelectedIndex = 0;
        _filter.Visibility = filters.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static GridLength ColumnWidth(ToolColumn column) => column.Width <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(column.Width);

    public void Clear()
    {
        _all.Clear();
        _view.Clear();
    }

    /// <summary>行を足す(UI スレッドで呼ぶ)。絞り込みに合えば一覧にも出す。</summary>
    public void Add(object row)
    {
        _all.Add(row);
        if (Matches(row))
        {
            _view.Add(row);
            if (_sortColumn >= 0)
            {
                QueueResort();
            }
        }
    }

    private bool Matches(object row) => _filter.SelectedIndex < 0 || _filter.SelectedIndex >= _filters.Count || _filters[_filter.SelectedIndex].Match?.Invoke(row) != false;

    /// <summary>絞り込みの選択(インデックス)。</summary>
    public int FilterIndex
    {
        get => _filter.SelectedIndex;
        set => _filter.SelectedIndex = value;
    }

    private void ApplyFilter()
    {
        _view.Clear();
        foreach (object row in _all.Where(Matches))
        {
            _view.Add(row);
        }

        if (_sortColumn >= 0)
        {
            Resort();
        }
    }

    // ---- 並べ替え ----

    private void SortBy(int column)
    {
        _sortDescending = _sortColumn == column && !_sortDescending;
        _sortColumn = column;
        Resort();
    }

    private void QueueResort()
    {
        if (_resortPending)
        {
            return;
        }

        _resortPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _resortPending = false;
            Resort();
        });
    }

    private void Resort()
    {
        if (_sortColumn < 0 || _sortColumn >= _columns.Count)
        {
            return;
        }

        ToolColumn column = _columns[_sortColumn];
        Func<object, IComparable> key = column.SortKey ?? (row => column.Text(row));
        object[] sorted = _sortDescending ? [.. _view.OrderByDescending(key)] : [.. _view.OrderBy(key)];
        object? selected = _list.SelectedItem;
        for (int i = 0; i < sorted.Length; i++)
        {
            int at = _view.IndexOf(sorted[i]);
            if (at != i)
            {
                _view.Move(at, i);
            }
        }

        if (selected is not null)
        {
            _list.SelectedItem = selected;
        }
    }

    // ---- 行の表示 ----

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not { } item || args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        Grid grid = container.ContentTemplateRoot as Grid ?? new Grid();
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.ColumnSpacing = 0;
        grid.Padding = new Thickness(0, 2, 0, 2);
        var names = new List<string>();
        for (int i = 0; i < _columns.Count; i++)
        {
            ToolColumn column = _columns[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = ColumnWidth(column) });
            FrameworkElement cell;
            if (column.Status is { } status)
            {
                StatusCell s = status(item);
                var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var icon = new FontIcon { Glyph = s.Glyph, FontSize = 14, Foreground = ToolParts.ThemeBrush(s.BrushKey) };
                stack.Children.Add(icon);
                stack.Children.Add(new TextBlock { Text = s.Text });
                AutomationProperties.SetName(stack, s.Text);
                cell = stack;
                names.Add(s.Text);
            }
            else
            {
                string text = column.Text(item);
                cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = i == 0 ? FontFamily.XamlAutoFontFamily : ToolParts.Mono, FontSize = 13 };
                ToolTipService.SetToolTip(cell, text);
                names.Add(text);
            }

            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        if (!ReferenceEquals(container.ContentTemplateRoot, grid))
        {
            container.Content = grid;
        }

        AutomationProperties.SetName(container, string.Join(", ", names));
    }

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ContextItems.Count == 0)
        {
            return;
        }

        if (e.OriginalSource is FrameworkElement { DataContext: not null } source && source.DataContext is { } dc && _view.Contains(dc))
        {
            _list.SelectedItem = dc;
        }

        if (_list.SelectedItem is not { } row)
        {
            return;
        }

        var menu = new MenuFlyout();
        foreach ((string label, Func<object, bool> enabled, Action<object> run) in ContextItems)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled(row) };
            item.Click += (_, _) => run(row);
            menu.Items.Add(item);
        }

        menu.ShowAt(_list, e.GetPosition(_list));
        e.Handled = true;
    }

    /// <summary>選んでいる行を起動する(Enter、ダブルクリック)。</summary>
    public void ActivateSelected()
    {
        if (_list.SelectedItem is { } row)
        {
            RowActivated?.Invoke(this, row);
        }
    }

    /// <summary>テスト用: 行を選んで起動する。</summary>
    internal void Activate(object row)
    {
        _list.SelectedItem = row;
        RowActivated?.Invoke(this, row);
    }

    /// <summary>テスト用: 列の見出しで並べ替える。</summary>
    internal void SortColumn(int column, bool descending)
    {
        _sortColumn = column;
        _sortDescending = descending;
        Resort();
    }

    // ---- 書き出し (00-overview 9 章: CSV / JSON) ----

    public string ToCsv(bool visibleOnly = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', _columns.Select(c => Field(c.Header))));
        foreach (object row in visibleOnly ? (IEnumerable<object>)_view : _all)
        {
            sb.AppendLine(string.Join(',', _columns.Select(c => Field(CellText(c, row)))));
        }

        return sb.ToString();
    }

    public string ToJson(bool visibleOnly = true)
    {
        var rows = (visibleOnly ? (IEnumerable<object>)_view : _all).Select(row =>
        {
            var dict = new Dictionary<string, string>();
            foreach (ToolColumn c in _columns)
            {
                dict[c.Header] = CellText(c, row);
            }

            return dict;
        });
        return JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string CellText(ToolColumn column, object row) => column.Status is { } status ? status(row).Text : column.Text(row);

    private static string Field(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>テスト用: 見えている行のセルの内容(状態の列はアイコンの文字と表示の文字)。</summary>
    internal IReadOnlyList<IReadOnlyList<(string Text, string? Glyph)>> CellTexts() =>
    [
        .. _view.Select(row => (IReadOnlyList<(string, string?)>)[.. _columns.Select(c => c.Status is { } s ? (s(row).Text, (string?)s(row).Glyph) : (c.Text(row), null))]),
    ];
}
