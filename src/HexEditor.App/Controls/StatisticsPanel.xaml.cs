using System.ComponentModel;
using HexEditor.App.Controls.Charts;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HexEditor.App.Controls;

/// <summary>
/// 統計パネル (ANA-10〜ANA-16) の表示。状態と処理は <see cref="StatisticsViewModel"/> に置き、ここはタブの切り替え・グラフの
/// イベント・右クリックメニューなど表示に固有の処理だけを持つ。
/// </summary>
public sealed partial class StatisticsPanel : UserControl, IPanelContent
{
    public StatisticsPanel(StatisticsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Panel_Statistics_Title"));
        Histogram.BinLabel = (h, bin) => ViewModel.BinLabel(h, bin);
        Histogram.BinInvoked += (_, bin) => _ = ViewModel.GoToBinAsync(bin);
        Histogram.BinContextRequested += (_, e) => ShowBinMenu(e.Bin, e.Position);
        EntropyGraph.OffsetText = logical => ViewModel.Ranges is { } r ? ViewModel.FormatHex(r.ToDocument(logical)) : string.Empty;
        EntropyGraph.BlockInvoked += (_, i) =>
        {
            if (ViewModel.Blocks is { } b)
            {
                ViewModel.JumpToBlock(b, i);
            }
        };
        EntropyGraph.RangeSelected += (_, e) =>
        {
            if (ViewModel.Blocks is { } b)
            {
                ViewModel.SelectBlocks(b, e.First, e.LastExclusive);
            }
        };
        EntropyGraph.ViewChanged += (_, e) => ViewModel.OnGraphViewChanged(e.From, e.To, e.BlockPixels);
        DigramChart.Describe = (x, y) => ViewModel.DigramCellText(x, y);
        DigramChart.CellInvoked += (_, c) => _ = ViewModel.SearchDigramAsync(c.X, c.Y);
        PositionChart.Describe = (x, y) => ViewModel.PositionCellText(x, y);
        PositionChart.CellClicked += (_, c) => ViewModel.GoToSection(c.X);
        AutomationProperties.SetName(DigramChart, Loc.Get("Stats_DigramChart"));
        AutomationProperties.SetName(PositionChart, Loc.Get("Stats_PositionChart"));

        // view model はウィンドウごとに 1 つで、パネルの中身は浮動パネルとの間を移るたびに作り直す。表示している間だけ通知を受ける。
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            SyncAll();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        SyncAll();
    }

    public StatisticsViewModel ViewModel { get; }

    internal HistogramChart HistogramChart => Histogram;

    internal EntropyChart EntropyChartControl => EntropyGraph;

    internal HeatmapChart DigramHeatmap => DigramChart;

    internal HeatmapChart PositionHeatmap => PositionChart;

    /// <summary>F6 の領域の移動 (UI-52) では、選んでいるタブの最初の部品にフォーカスを移す。</summary>
    public bool FocusContent() => TargetChoice.Focus(FocusState.Keyboard);

    /// <summary>タブを選ぶ (コマンド「解析: 記述統計」など)。</summary>
    public void ShowTab(StatsTab tab)
    {
        ViewModel.Tab = tab;
        SyncTab();
    }

    private void SyncAll()
    {
        TargetChoice.SelectedIndex = (int)ViewModel.TargetKind;
        SyncCustom();
        SyncTab();
        SyncTables();
        StaleText.Visibility = ViewModel.IsStale ? Visibility.Visible : Visibility.Collapsed;
        UpdateComputeStyle();
        EntropyGraph.Cursor = ViewModel.CursorLogical;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StatisticsViewModel.TargetKind):
                TargetChoice.SelectedIndex = (int)ViewModel.TargetKind;
                SyncCustom();
                break;
            case nameof(StatisticsViewModel.Tab):
                SyncTab();
                break;
            case nameof(StatisticsViewModel.HistogramAsTable) or nameof(StatisticsViewModel.GraphAsTable)
                or nameof(StatisticsViewModel.DigramAsTable) or nameof(StatisticsViewModel.DigramMode) or nameof(StatisticsViewModel.PatternView):
                SyncTables();
                break;
            case nameof(StatisticsViewModel.IsStale):
                StaleText.Visibility = ViewModel.IsStale ? Visibility.Visible : Visibility.Collapsed;
                UpdateComputeStyle();
                break;
            case nameof(StatisticsViewModel.ComputeHighlighted):
                UpdateComputeStyle();
                break;
            case nameof(StatisticsViewModel.CursorLogical):
                EntropyGraph.Cursor = ViewModel.CursorLogical;
                break;
            case nameof(StatisticsViewModel.DigramSummary):
                AutomationProperties.SetHelpText(DigramChart, ViewModel.DigramSummary);
                break;
        }
    }

    /// <summary>64 MB を超える対象・編集後は「再計算」を強調表示する (ANA-10 の仕様 9、06 の 0.2)。</summary>
    private void UpdateComputeStyle() =>
        ComputeButton.Style = ViewModel.ComputeHighlighted || ViewModel.IsStale ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;

    private void SyncCustom()
    {
        Visibility v = ViewModel.TargetKind == StatsTargetKind.Custom ? Visibility.Visible : Visibility.Collapsed;
        CustomStartBox.Visibility = v;
        CustomLengthBox.Visibility = v;
        CustomEndBox.Visibility = v;
    }

    private void SyncTab()
    {
        string tag = ViewModel.Tab.ToString();
        foreach (SelectorBarItem item in Tabs.Items)
        {
            if ((string)item.Tag == tag && !item.IsSelected)
            {
                item.IsSelected = true;
            }
        }

        HistogramPage.Visibility = Show(StatsTab.Histogram);
        DescriptivePage.Visibility = Show(StatsTab.Descriptive);
        EntropyPage.Visibility = Show(StatsTab.Entropy);
        DigramPage.Visibility = Show(StatsTab.Digram);
        PatternPage.Visibility = Show(StatsTab.Pattern);
        ClassifyPage.Visibility = Show(StatsTab.Classify);
    }

    private Visibility Show(StatsTab tab) => ViewModel.Tab == tab ? Visibility.Visible : Visibility.Collapsed;

    private void SyncTables()
    {
        HistogramTable.Visibility = ViewModel.HistogramAsTable ? Visibility.Visible : Visibility.Collapsed;
        Histogram.Visibility = ViewModel.HistogramAsTable ? Visibility.Collapsed : Visibility.Visible;
        BlockTable.Visibility = ViewModel.GraphAsTable ? Visibility.Visible : Visibility.Collapsed;
        EntropyGraph.Visibility = ViewModel.GraphAsTable ? Visibility.Collapsed : Visibility.Visible;
        bool table = ViewModel.DigramAsTable && ViewModel.DigramMode == 0;
        DigramTable.Visibility = table ? Visibility.Visible : Visibility.Collapsed;
        DigramBox.Visibility = !table && ViewModel.DigramMode == 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionBox.Visibility = ViewModel.DigramMode == 1 ? Visibility.Visible : Visibility.Collapsed;
        NGramList.Visibility = ViewModel.PatternView == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeriodList.Visibility = ViewModel.PatternView == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { Tag: string tag } && Enum.TryParse(tag, out StatsTab tab) && ViewModel.Tab != tab)
        {
            ViewModel.Tab = tab;
        }
    }

    private void TargetChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetChoice.SelectedIndex >= 0 && (StatsTargetKind)TargetChoice.SelectedIndex != ViewModel.TargetKind)
        {
            ViewModel.ChooseTarget((StatsTargetKind)TargetChoice.SelectedIndex);
        }
    }

    private void Compute_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ComputeAsync();

    private void CopyTable_Click(object sender, RoutedEventArgs e) => ViewModel.CopyHistogramTable();

    private void ExportCsv_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ExportAsync(json: false);

    private void ExportJson_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ExportAsync(json: true);

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (DescriptiveList.SelectedItem is NameValueRowViewModel row)
        {
            ViewModel.CopyDescriptive(row);
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e) => ViewModel.CopyDescriptive(null);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => EntropyGraph.ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => EntropyGraph.ZoomOut();

    private void ExportGraph_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ExportGraphAsync();

    private void ComputePatterns_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ComputePatternsAsync();

    private void CancelPatterns_Click(object sender, RoutedEventArgs e) => ViewModel.CancelPatterns();

    private void Classify_Click(object sender, RoutedEventArgs e) => _ = ViewModel.ClassifyAsync();

    private void CancelClassify_Click(object sender, RoutedEventArgs e) => ViewModel.CancelClassify();

    private void HistogramTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HistogramTable.SelectedItem is HistogramRowViewModel row)
        {
            _ = ViewModel.GoToBinAsync(row.Bin);
        }
    }

    private void BlockTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (BlockTable.SelectedItem is EntropyBlockRowViewModel row)
        {
            // 行のオフセットはドキュメントの位置 (16 進)。
            ViewModel.GoTo?.Invoke(long.Parse(row.Offset[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void DigramTable_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DigramTable.SelectedItem is DigramRowViewModel row)
        {
            _ = ViewModel.SearchDigramAsync(row.First, row.Second);
        }
    }

    private void NGramList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (NGramList.SelectedItem is NGramRowViewModel row)
        {
            ViewModel.GoToNGram(row);
        }
    }

    private void ClassList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ClassList.SelectedItem is ClassRowViewModel row)
        {
            ViewModel.GoToClassRow(row);
        }
    }

    private void ClassBand_RegionInvoked(object? sender, int index)
    {
        if (ViewModel.Classification is { } c && index >= 0 && index < c.Regions.Count)
        {
            ViewModel.GoTo?.Invoke(c.Regions[index].Offset);
        }
    }

    // ---- 右クリックメニュー ----

    private void ShowBinMenu(int bin, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        var find = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_FindAll"), IsEnabled = ViewModel.CanFindAll(bin) };
        AutomationProperties.SetAutomationId(find, "Stats_Menu_FindAll");
        find.Click += (_, _) => _ = ViewModel.FindAllBinAsync(bin);
        var go = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_GoToNext") };
        AutomationProperties.SetAutomationId(go, "Stats_Menu_GoToNext");
        go.Click += (_, _) => _ = ViewModel.GoToBinAsync(bin);
        menu.Items.Add(go);
        menu.Items.Add(find);
        menu.ShowAt(Histogram, position);
    }

    private void NGramList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not NGramRowViewModel row)
        {
            return;
        }

        var menu = new MenuFlyout();
        var go = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_GoToFirst") };
        go.Click += (_, _) => ViewModel.GoToNGram(row);
        var find = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_FindAll") };
        AutomationProperties.SetAutomationId(find, "Stats_Menu_FindAllNGram");
        find.Click += (_, _) => _ = ViewModel.FindAllNGramAsync(row);
        menu.Items.Add(go);
        menu.Items.Add(find);
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
        e.Handled = true;
    }

    private void PeriodList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PeriodRowViewModel row)
        {
            PeriodMenu(row).ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
            e.Handled = true;
        }
    }

    /// <summary>周期の行の右クリックメニュー「この長さでレコード表示」(ANA-15 の仕様 3)。</summary>
    internal MenuFlyout PeriodMenu(PeriodRowViewModel row)
    {
        var menu = new MenuFlyout();
        var records = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_RecordView"), IsEnabled = ViewModel.CanShowRecords };
        AutomationProperties.SetAutomationId(records, "Stats_Menu_RecordView");
        records.Click += (_, _) => ViewModel.ShowRecords(row);
        menu.Items.Add(records);
        return menu;
    }

    private void ClassList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ClassRowViewModel row)
        {
            ClassMenu(row).ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
            e.Handled = true;
        }
    }

    /// <summary>分類の一覧の右クリックメニュー (圧縮形式のシグネチャの行には「ここから展開...」。ANA-16 の仕様 5)。</summary>
    internal MenuFlyout ClassMenu(ClassRowViewModel row)
    {
        var menu = new MenuFlyout();
        var go = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_GoTo") };
        go.Click += (_, _) => ViewModel.GoToClassRow(row);
        menu.Items.Add(go);
        if (row.Signature is { IsCompression: true })
        {
            var expand = new MenuFlyoutItem { Text = Loc.Get("Stats_Menu_Decompress"), IsEnabled = ViewModel.CanDecompress(row) };
            AutomationProperties.SetAutomationId(expand, "Stats_Menu_Decompress");
            if (!expand.IsEnabled)
            {
                ToolTipService.SetToolTip(expand, Loc.Get("Stats_DecompressUnavailable"));
            }

            expand.Click += (_, _) => _ = ViewModel.DecompressAsync(row);
            menu.Items.Add(expand);
        }

        return menu;
    }
}
