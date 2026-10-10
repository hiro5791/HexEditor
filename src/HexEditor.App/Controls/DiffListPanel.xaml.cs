using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>差分の一覧のパネルが使うウィンドウの機能 (マージ・変換・書き出し・比較タブへの切り替え)。</summary>
public interface IDiffListHost
{
    Task CopyDiffsAsync(CompareSessionViewModel session, MergeDirection direction, IReadOnlyCollection<long>? indices, bool all);

    void DiffsToMultiSelection(CompareSessionViewModel session, bool right, IReadOnlyList<long> indices);

    void DiffsToBookmarks(CompareSessionViewModel session, bool right, IReadOnlyList<long> indices);

    Task ExportDiffsAsync(CompareSessionViewModel session, string format, IReadOnlyList<long>? indices);

    /// <summary>比較タブを前に出す (一覧の行を選んだとき)。</summary>
    void ShowCompare(CompareSessionViewModel session);
}

/// <summary>
/// 差分の一覧とグラフ (ANA-06)。共通の結果一覧 (00 の 9 章) と同じく、行は見えている分だけ作り (仮想化)、並べ替え・絞り込み・
/// エクスポート・選択した行のマルチ選択やブックマークへの変換ができる。グラフ「差分の分布」は区間ごとの異なるバイトの割合の棒グラフで、
/// 「表で表示」で同じ内容を表にする。
/// </summary>
public sealed partial class DiffListPanel : UserControl
{
    private const double RowHeight = 24;

    /// <summary>列 (見出しのリソースキー、幅、等幅フォント)。</summary>
    private static readonly (string Key, double Width, bool Mono)[] Columns =
    [
        ("Compare_Column_Number", 70, false),
        ("Compare_Column_Kind", 110, false),
        ("Compare_Column_LeftOffset", 120, true),
        ("Compare_Column_LeftLength", 90, false),
        ("Compare_Column_RightOffset", 120, true),
        ("Compare_Column_RightLength", 90, false),
        ("Compare_Column_LeftBytes", 380, true),
        ("Compare_Column_RightBytes", 380, true),
    ];

    private readonly List<Grid> _rows = [];
    private readonly List<Rectangle> _bars = [];
    private readonly HashSet<long> _selected = [];
    private CompareSessionViewModel? _session;
    private long _top;
    private long _focusRow = -1;
    private long _anchorRow = -1;
    private bool _updatingFilters;

    public DiffListPanel()
    {
        InitializeComponent();
        FilterChanged.Content = Loc.Get("Compare_Kind_Changed");
        FilterInserted.Content = Loc.Get("Compare_Kind_Inserted");
        FilterDeleted.Content = Loc.Get("Compare_Kind_Deleted");
        FilterUnreadable.Content = Loc.Get("Compare_Kind_Unreadable");
        AutomationProperties.SetName(SortBox, Loc.Get("Compare_List_Sort"));
        SortBox.Items.Add(Loc.Get("Compare_Sort_Number"));
        SortBox.Items.Add(Loc.Get("Compare_Sort_LengthDescending"));
        SortBox.Items.Add(Loc.Get("Compare_Sort_LengthAscending"));
        SortBox.SelectedIndex = 0;
        ExportButton.Content = Loc.Get("Compare_List_Export");
        AutomationProperties.SetName(ListHost, Loc.Get("Compare_List_Name"));
        AutomationProperties.SetName(Graph, Loc.Get("Compare_Graph_Title"));
        foreach (int n in BucketChoices)
        {
            BucketsBox.Items.Add(Loc.Format("Compare_Graph_Buckets", n.ToString("N0", CultureInfo.CurrentCulture)));
        }

        BucketsBox.SelectedIndex = Array.IndexOf(BucketChoices, DiffDistribution.DefaultBuckets);
        AutomationProperties.SetName(BucketsBox, Loc.Get("Compare_Graph_BucketsName"));
        AddExportItem("csv", "Compare_Export_Csv");
        AddExportItem("json", "Compare_Export_Json");
        AddExportItem("report", "Compare_Export_Report");
        BuildHeader();
        BuildContextMenu();
        ListHost.PointerPressed += ListHost_PointerPressed;
    }

    public IDiffListHost? Host { get; set; }

    /// <summary>表示する比較 (最後に使った比較タブ)。</summary>
    public CompareSessionViewModel? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value))
            {
                return;
            }

            if (_session is not null)
            {
                _session.ResultChanged -= Session_ResultChanged;
            }

            _session = value;
            _selected.Clear();
            _top = 0;
            _focusRow = -1;
            if (_session is not null)
            {
                _session.ResultChanged += Session_ResultChanged;
                _updatingFilters = true;
                FilterChanged.IsChecked = _session.KindFilter.HasFlag(DiffKindFilter.Changed);
                FilterInserted.IsChecked = _session.KindFilter.HasFlag(DiffKindFilter.Inserted);
                FilterDeleted.IsChecked = _session.KindFilter.HasFlag(DiffKindFilter.Deleted);
                FilterUnreadable.IsChecked = _session.KindFilter.HasFlag(DiffKindFilter.Unreadable);
                MinLengthBox.Text = _session.MinLength > 0 ? _session.MinLength.ToString(CultureInfo.InvariantCulture) : string.Empty;
                SortBox.SelectedIndex = (int)_session.SortOrder;
                BucketsBox.SelectedIndex = Array.IndexOf(BucketChoices, _session.Buckets);
                _updatingFilters = false;
            }

            Refresh();
        }
    }

    /// <summary>選んでいる行の差分の番号 (一覧の順)。</summary>
    public IReadOnlyList<long> SelectedIndices => [.. _selected.Order()];

    private void Session_ResultChanged(object? sender, EventArgs e) => Refresh();

    // ---- 表示 ----

    /// <summary>一覧・要約・グラフを今の結果で描き直す。</summary>
    public void Refresh()
    {
        bool has = _session?.Result is not null;
        EmptyText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ListArea.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        SideArea.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            return;
        }

        BusyText.Visibility = _session!.IsListBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = _session.IsListBusy ? Loc.Get("Compare_List_Sorting") : string.Empty;
        UpdateScroll();
        RenderRows();
        RenderSummary();
        RenderGraph();
    }

    private int VisibleRows => Math.Max(1, (int)(ListHost.ActualHeight / RowHeight));

    private long RowCount => _session?.ListCount ?? 0;

    private void UpdateScroll()
    {
        long max = Math.Max(0, RowCount - VisibleRows);
        _top = Math.Clamp(_top, 0, max);
        RowsScroll.Maximum = max;
        RowsScroll.ViewportSize = VisibleRows;
        RowsScroll.LargeChange = Math.Max(1, VisibleRows - 1);
        RowsScroll.SmallChange = 1;
        RowsScroll.Value = _top;
        RowsScroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        RowsScroll.Height = ListHost.ActualHeight;
    }

    private void BuildHeader()
    {
        HeaderRow.ColumnDefinitions.Clear();
        foreach ((string key, double width, _) in Columns)
        {
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
            var text = new TextBlock
            {
                Text = Loc.Get(key),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Margin = new Thickness(0, 4, 8, 4),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, HeaderRow.ColumnDefinitions.Count - 1);
            HeaderRow.Children.Add(text);
        }
    }

    private void RenderRows()
    {
        int visible = VisibleRows;
        long count = RowCount;
        int used = 0;
        CompareResult? r = _session?.Result;
        for (int i = 0; i < visible && r is not null; i++)
        {
            long row = _top + i;
            if (row >= count)
            {
                break;
            }

            long index = _session!.ListIndex(row);
            if (index >= r.Diffs.Count)
            {
                continue;
            }

            DiffRange d = r.Diffs[index];
            Grid visual = TakeRow(used++);
            Canvas.SetTop(visual, i * RowHeight);
            // 領域ごとの比較 (ANA-09 の仕様 5・7): 片方にしかない領域は「領域の追加」「領域の削除」、位置には領域名を添える。
            string kind = r.ByRegion && RegionComparer.ChangeOf(r, d) is { } change
                ? Loc.Get(change == RegionChange.Added ? "Compare_Kind_RegionAdded" : "Compare_Kind_RegionRemoved")
                : CompareSessionViewModel.KindName(d.Kind);
            string[] cells =
            [
                (index + 1).ToString("N0", CultureInfo.CurrentCulture),
                kind,
                WithRegion(r, left: true, d.LeftOffset),
                d.LeftLength.ToString("N0", CultureInfo.CurrentCulture),
                WithRegion(r, left: false, d.RightOffset),
                d.RightLength.ToString("N0", CultureInfo.CurrentCulture),
                DiffExport.PreviewHex(r.Left, d.LeftOffset, d.LeftLength),
                DiffExport.PreviewHex(r.Right, d.RightOffset, d.RightLength),
            ];
            for (int c = 0; c < cells.Length; c++)
            {
                ((TextBlock)visual.Children[c]).Text = cells[c];
            }

            bool selected = _selected.Contains(index);
            visual.Background = selected
                ? (Brush)Application.Current.Resources["AccentFillColorTertiaryBrush"]
                : row == _focusRow ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] : null;
            foreach (TextBlock t in visual.Children.OfType<TextBlock>())
            {
                t.Foreground = selected ? (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] : null;
            }

            visual.Tag = row;
            AutomationProperties.SetAutomationId(visual, "DiffList_Row_" + row.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(visual, string.Join(", ", cells.Where(c => c.Length > 0)));
            AutomationProperties.SetItemStatus(visual, selected ? Loc.Get("Compare_List_Selected") : string.Empty);
        }

        for (int i = used; i < _rows.Count; i++)
        {
            _rows[i].Visibility = Visibility.Collapsed;
        }
    }

    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    private Grid TakeRow(int index)
    {
        if (index >= _rows.Count)
        {
            var row = new Grid { Height = RowHeight, Padding = new Thickness(12, 0, 0, 0) };
            foreach ((_, double width, bool mono) in Columns)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
                var text = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = FlowDirection.LeftToRight,
                };
                if (mono)
                {
                    text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                }

                Grid.SetColumn(text, row.ColumnDefinitions.Count - 1);
                row.Children.Add(text);
            }

            _rows.Add(row);
            RowsCanvas.Children.Add(row);
        }

        Grid visual = _rows[index];
        visual.Visibility = Visibility.Visible;
        return visual;
    }

    private void RenderSummary()
    {
        SummaryRows.Children.Clear();
        foreach ((string label, string value, string id) in _session!.Summary)
        {
            var text = new TextBlock { Text = Loc.Format("Compare_Summary_Row", label, value), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(text, "DiffList_Summary_" + id);
            SummaryRows.Children.Add(text);
        }
    }

    private void RenderGraph()
    {
        DiffDistribution? distribution = _session?.Distribution;
        if (distribution is not null && TableToggle.IsChecked == true)
        {
            GraphTable.ItemsSource = Enumerable.Range(0, distribution.Count)
                .Select(b => Loc.Format("Compare_Graph_TableRow", Hex(distribution.BucketStart(b)),
                    (distribution.Ratios[b] * 100).ToString("N2", CultureInfo.CurrentCulture)))
                .ToList();
        }

        int used = 0;
        double width = Graph.ActualWidth;
        double height = Graph.Height;
        if (distribution is not null && width > 0)
        {
            double barWidth = width / distribution.Count;
            Brush fill = CompareBrushes.Mark(DiffKind.Changed, this, new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast);
            for (int b = 0; b < distribution.Count; b++)
            {
                double ratio = distribution.Ratios[b];
                if (ratio <= 0)
                {
                    continue;
                }

                Rectangle bar = TakeBar(used++);
                bar.Fill = fill;
                bar.Width = Math.Max(1, barWidth);
                bar.Height = Math.Max(1, ratio * height);
                Canvas.SetLeft(bar, b * barWidth);
                Canvas.SetTop(bar, height - bar.Height);
            }

            // スクリーンリーダー向けの名前と要約 (06 の 0.4)。
            double max = distribution.Ratios.Max();
            int maxAt = Array.IndexOf([.. distribution.Ratios], max);
            AutomationProperties.SetName(Graph, Loc.Format("Compare_Graph_Summary", distribution.Count.ToString("N0", CultureInfo.CurrentCulture),
                (max * 100).ToString("N2", CultureInfo.CurrentCulture), Hex(distribution.BucketStart(Math.Max(0, maxAt)))));
        }

        for (int i = used; i < _bars.Count; i++)
        {
            _bars[i].Visibility = Visibility.Collapsed;
        }

        Graph.Visibility = TableToggle.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        GraphTable.Visibility = TableToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private Rectangle TakeBar(int index)
    {
        if (index >= _bars.Count)
        {
            var bar = new Rectangle { IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            _bars.Add(bar);
            Graph.Children.Add(bar);
        }

        Rectangle r = _bars[index];
        r.Visibility = Visibility.Visible;
        return r;
    }

    // ---- 絞り込み・並べ替え (仕様 3・4) ----

    private async void Filter_Click(object sender, RoutedEventArgs e) => await ApplyListViewAsync();

    private async void MinLength_TextChanged(object sender, TextChangedEventArgs e) => await ApplyListViewAsync();

    private async void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplyListViewAsync();

    private async Task ApplyListViewAsync()
    {
        if (_updatingFilters || _session is null)
        {
            return;
        }

        DiffKindFilter kinds = (FilterChanged.IsChecked == true ? DiffKindFilter.Changed : 0)
            | (FilterInserted.IsChecked == true ? DiffKindFilter.Inserted : 0)
            | (FilterDeleted.IsChecked == true ? DiffKindFilter.Deleted : 0)
            | (FilterUnreadable.IsChecked == true ? DiffKindFilter.Unreadable : 0);
        long min = Core.Expressions.ExpressionEvaluator.TryEvaluate(MinLengthBox.Text, new CompareDialog.LengthContext(0), out long value, out _, Core.Expressions.DefaultRadix.Decimal) ? value : 0;
        if (string.IsNullOrWhiteSpace(MinLengthBox.Text))
        {
            min = 0;
        }

        _selected.Clear();
        _top = 0;
        _focusRow = -1;
        await _session.SetListViewAsync(kinds, min, (DiffSortOrder)Math.Max(0, SortBox.SelectedIndex));
    }

    /// <summary>種類の絞り込みを設定する (テスト用の命令からも使う)。</summary>
    public Task SetFilterAsync(DiffKindFilter kinds, long minLength)
    {
        _updatingFilters = true;
        FilterChanged.IsChecked = kinds.HasFlag(DiffKindFilter.Changed);
        FilterInserted.IsChecked = kinds.HasFlag(DiffKindFilter.Inserted);
        FilterDeleted.IsChecked = kinds.HasFlag(DiffKindFilter.Deleted);
        FilterUnreadable.IsChecked = kinds.HasFlag(DiffKindFilter.Unreadable);
        MinLengthBox.Text = minLength > 0 ? minLength.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _updatingFilters = false;
        return ApplyListViewAsync();
    }

    // ---- 行の選択と移動 (仕様 2) ----

    /// <summary>
    /// 行をクリックした: 修飾キーなしならその行だけを選び、比較ビューでその差分へ移動する。Ctrl は選択に足す・外す、Shift は範囲で選ぶ。
    /// </summary>
    public void ClickRow(long row, bool ctrl, bool shift)
    {
        if (_session is null || row < 0 || row >= RowCount)
        {
            return;
        }

        long index = _session.ListIndex(row);
        if (shift && _anchorRow >= 0)
        {
            _selected.Clear();
            for (long r = Math.Min(_anchorRow, row); r <= Math.Max(_anchorRow, row); r++)
            {
                _selected.Add(_session.ListIndex(r));
            }
        }
        else if (ctrl)
        {
            if (!_selected.Remove(index))
            {
                _selected.Add(index);
            }

            _anchorRow = row;
        }
        else
        {
            _selected.Clear();
            _selected.Add(index);
            _anchorRow = row;
            Host?.ShowCompare(_session);
            _session.FocusedRight = false;
            _session.SelectDiff(index);
        }

        _focusRow = row;
        EnsureVisible(row);
        RenderRows();
    }

    private void EnsureVisible(long row)
    {
        int visible = VisibleRows;
        if (row < _top)
        {
            _top = row;
        }
        else if (row >= _top + visible)
        {
            _top = row - visible + 1;
        }

        UpdateScroll();
    }

    private void ListHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        double y = e.GetCurrentPoint(RowsCanvas).Position.Y;
        long row = _top + (long)(y / RowHeight);
        bool right = e.GetCurrentPoint(RowsCanvas).Properties.IsRightButtonPressed;
        ListHost.Focus(FocusState.Pointer);
        if (right)
        {
            if (_session is not null && row < RowCount && !_selected.Contains(_session.ListIndex(row)))
            {
                ClickRow(row, false, false);
            }

            return;
        }

        ClickRow(row, Down(VirtualKey.Control), Down(VirtualKey.Shift));
        e.Handled = true;
    }

    private static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void ListHost_KeyDown(object sender, KeyRoutedEventArgs e) => e.Handled = HandleKey(e.Key, Down(VirtualKey.Shift), Down(VirtualKey.Control));

    /// <summary>一覧のキー操作 (↑↓、PageUp/Down、Home/End で移動して選ぶ、Ctrl+A ですべて選ぶ)。テスト用の命令からも使う。</summary>
    public bool HandleKey(VirtualKey key, bool shift, bool ctrl)
    {
        long count = RowCount;
        if (count == 0)
        {
            return false;
        }

        long row = Math.Max(0, _focusRow);
        long target = key switch
        {
            VirtualKey.Up => row - 1,
            VirtualKey.Down => _focusRow < 0 ? 0 : row + 1,
            VirtualKey.PageUp => row - (VisibleRows - 1),
            VirtualKey.PageDown => row + (VisibleRows - 1),
            VirtualKey.Home => 0,
            VirtualKey.End => count - 1,
            VirtualKey.Enter => row,
            _ => -2,
        };
        if (key == VirtualKey.A && ctrl)
        {
            _selected.Clear();
            for (long r = 0; r < count; r++)
            {
                _selected.Add(_session!.ListIndex(r));
            }

            RenderRows();
            return true;
        }

        if (target == -2)
        {
            return false;
        }

        ClickRow(Math.Clamp(target, 0, count - 1), false, shift);
        return true;
    }

    private void ListHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(ListHost).Properties.MouseWheelDelta;
        _top -= delta / 40;
        UpdateScroll();
        RenderRows();
        e.Handled = true;
    }

    private void RowsScroll_Scroll(object sender, ScrollEventArgs e)
    {
        _top = (long)Math.Round(e.NewValue);
        RenderRows();
    }

    private void ListHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScroll();
        RenderRows();
    }

    // ---- 右クリックメニュー・エクスポート (仕様 5・8) ----

    private void BuildContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("DiffListMenu_CopyRight", "Cmd_compare_copyRight", () => Copy(MergeDirection.ToRight)));
        menu.Items.Add(MenuItem("DiffListMenu_CopyLeft", "Cmd_compare_copyLeft", () => Copy(MergeDirection.ToLeft)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var multi = new MenuFlyoutSubItem { Text = Loc.Get("Compare_List_ToMultiSelection") };
        AutomationProperties.SetAutomationId(multi, "DiffListMenu_ToMultiSelection");
        multi.Items.Add(MenuItem("DiffListMenu_ToMultiSelectionLeft", "Compare_Side_Left", () => Host?.DiffsToMultiSelection(_session!, false, SelectedIndices)));
        multi.Items.Add(MenuItem("DiffListMenu_ToMultiSelectionRight", "Compare_Side_Right", () => Host?.DiffsToMultiSelection(_session!, true, SelectedIndices)));
        menu.Items.Add(multi);
        var bookmarks = new MenuFlyoutSubItem { Text = Loc.Get("Compare_List_ToBookmarks") };
        AutomationProperties.SetAutomationId(bookmarks, "DiffListMenu_ToBookmarks");
        bookmarks.Items.Add(MenuItem("DiffListMenu_ToBookmarksLeft", "Compare_Side_Left", () => Host?.DiffsToBookmarks(_session!, false, SelectedIndices)));
        bookmarks.Items.Add(MenuItem("DiffListMenu_ToBookmarksRight", "Compare_Side_Right", () => Host?.DiffsToBookmarks(_session!, true, SelectedIndices)));
        menu.Items.Add(bookmarks);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("DiffListMenu_ExportCsv", "Compare_Export_SelectedCsv", () => _ = Host?.ExportDiffsAsync(_session!, "csv", SelectedIndices)));
        menu.Opening += (_, _) => UpdateMenuStates(menu);
        _menu = menu;
        ListHost.ContextFlyout = menu;
    }

    private MenuFlyout? _menu;

    /// <summary>
    /// 右クリックメニューの項目の有効・無効。コピーは書き込み先が読み取り専用なら無効にし、ツールチップに理由 (「右側は読み取り専用です」) を
    /// 出す (ANA-07 の「エラー」)。
    /// </summary>
    private void UpdateMenuStates(MenuFlyout menu)
    {
        bool any = _selected.Count > 0 && _session?.Result is not null;
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            string id = AutomationProperties.GetAutomationId(item);
            MergeDirection? direction = id switch
            {
                "DiffListMenu_CopyRight" => MergeDirection.ToRight,
                "DiffListMenu_CopyLeft" => MergeDirection.ToLeft,
                _ => null,
            };
            string? reason = direction is { } d && any ? _session!.CopyBlockedReason(d) : null;
            item.IsEnabled = any && reason is null;
            if (direction is not null)
            {
                ToolTipService.SetToolTip(item, reason);
            }
        }
    }

    /// <summary>右クリックメニューの項目の状態 (テスト用): 自動化 ID → (有効か、ツールチップ)。</summary>
    internal System.Text.Json.Nodes.JsonObject MenuState()
    {
        var state = new System.Text.Json.Nodes.JsonObject();
        if (_menu is null)
        {
            return state;
        }

        UpdateMenuStates(_menu);
        foreach (MenuFlyoutItemBase item in _menu.Items)
        {
            if (AutomationProperties.GetAutomationId(item) is { Length: > 0 } id)
            {
                state[id] = new System.Text.Json.Nodes.JsonObject
                {
                    ["enabled"] = item.IsEnabled,
                    ["toolTip"] = ToolTipService.GetToolTip(item) as string,
                };
            }
        }

        return state;
    }

    private void Copy(MergeDirection direction)
    {
        if (_session is not null && Host is not null)
        {
            _ = Host.CopyDiffsAsync(_session, direction, SelectedIndices, all: false);
        }
    }

    private static MenuFlyoutItem MenuItem(string id, string textKey, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(textKey) };
        AutomationProperties.SetAutomationId(item, id);
        item.Click += (_, _) => action();
        return item;
    }

    private void AddExportItem(string format, string textKey)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(textKey) };
        AutomationProperties.SetAutomationId(item, "DiffList_Export_" + format);
        item.Click += (_, _) =>
        {
            if (_session is not null && Host is not null)
            {
                _ = Host.ExportDiffsAsync(_session, format, null);
            }
        };
        ExportMenu.Items.Add(item);
    }

    // ---- グラフ (仕様 7) ----

    private void Graph_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session?.Distribution is { } distribution && Graph.ActualWidth > 0)
        {
            ClickBar((int)(e.GetCurrentPoint(Graph).Position.X / Graph.ActualWidth * distribution.Count));
        }
    }

    /// <summary>棒をクリックした: その区間の最初の差分へ移動する (テスト用の命令からも使う)。</summary>
    public bool ClickBar(int bucket)
    {
        if (_session is null)
        {
            return false;
        }

        Host?.ShowCompare(_session);
        return _session.JumpToBucket(bucket);
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => RenderGraph();

    /// <summary>グラフの区間の数の選択肢。</summary>
    private static readonly int[] BucketChoices = [64, 128, 256, 512, 1024, 2048, 4096];

    private async void BucketsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is not null && BucketsBox.SelectedIndex >= 0 && BucketChoices[BucketsBox.SelectedIndex] != _session.Buckets)
        {
            await _session.SetBucketsAsync(BucketChoices[BucketsBox.SelectedIndex]);
        }
    }

    private void TableToggle_Click(object sender, RoutedEventArgs e) => RenderGraph();

    /// <summary>「表で表示」を切り替える (テスト用の命令からも使う)。</summary>
    public void ShowTable(bool on)
    {
        TableToggle.IsChecked = on;
        RenderGraph();
    }

    /// <summary>表の行 (テスト用)。</summary>
    internal IReadOnlyList<string> TableRows => GraphTable.ItemsSource as IReadOnlyList<string> ?? [];

    /// <summary>グラフの棒の数 (テスト用)。</summary>
    internal int BarCount => _bars.Count(b => b.Visibility == Visibility.Visible);

    // ---- 配置 (幅が 800 px 未満のときは上下に並べる。「画面」) ----

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 800;
        SideColumn.Width = narrow ? new GridLength(0) : new GridLength(360);
        Grid.SetColumn(SideArea, narrow ? 0 : 1);
        Grid.SetRow(SideArea, narrow ? 1 : 0);
        SideArea.BorderThickness = narrow ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        SideArea.MaxHeight = narrow ? 200 : double.PositiveInfinity;
    }

    /// <summary>位置の表記。領域ごとの比較では「仮想アドレス 領域名」(ANA-09 の仕様 7)。</summary>
    private static string WithRegion(CompareResult r, bool left, long offset)
    {
        if (!r.ByRegion)
        {
            return Hex(offset);
        }

        ICompareData data = left ? r.Left.Data : r.Right.Data;
        string name = CompareData.RegionsOf(data) is { } regions ? RegionComparer.RegionName(regions, offset, RegionComparer.ModulesOf(data)) : string.Empty;
        return name.Length == 0 ? Hex(offset) : Hex(offset) + " " + name;
    }
}
