using System.Globalization;
using HexEditor.App.Controls.Charts;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.FileMap;
using HexEditor.Core.Statistics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace HexEditor.App.Controls;

/// <summary>領域の操作 1 つ (右クリックメニューと詳細のボタン。ANA-34 の仕様 6)。</summary>
internal sealed record FileMapAction(string Id, string Label, Func<Task> Run, bool Enabled = true, string? Reason = null);

/// <summary>
/// 「ファイルマップ」パネル (ANA-34 の仕様 1。既定は下)。上から、状態の行 (段階、進捗、キャンセル、「作り直す」、推定である旨)、帯 (構造と内容の
/// 2 本のレーン)、オフセットの目盛り、凡例、領域の一覧、選んだ領域の詳細 (根拠と操作のボタン)。幅が 800 px 未満のときは詳細を一覧の下に置く。
/// 状態 (結果・並べ替え・絞り込み) はウィンドウの <see cref="MainWindow"/> が持ち、パネルの中身 (浮動パネルとの間を移るたびに作り直す) は
/// それを表示する。
/// </summary>
public sealed partial class FileMapPanel : UserControl, IPanelContent
{
    private readonly MainWindow _window;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Width = 160, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _create = new();
    private readonly Button _rebuild = new();
    private readonly Button _cancel = new();
    private readonly DropDownButton _export = new();
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _stale = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly FileMapBand _band = new();
    private readonly WrapPanel _legend = new();
    private readonly VirtualTable _table = new("FileMap_List");
    private readonly ComboBox _filter = new() { MinWidth = 180 };
    private readonly TextBlock _detailTitle = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailInfo = new() { TextWrapping = TextWrapping.Wrap, FontFamily = DialogParts.Mono, FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _detailEvidence = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly WrapPanel _detailActions = new();
    private readonly Grid _split = new();
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private bool _narrow;
    private bool _syncing;

    public FileMapPanel(MainWindow window)
    {
        _window = window;
        FlowDirection = FlowDirection.LeftToRight;
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];

        Setup(_create, "FileMap_Create", () => _window.Commands.ExecuteAsync("analysis.fileMap"));
        Setup(_rebuild, "FileMap_Rebuild", () => _window.Commands.ExecuteAsync("analysis.fileMap.rebuild"));
        Setup(_cancel, "FileMap_Cancel", () =>
        {
            _window.CancelFileMap();
            return Task.CompletedTask;
        });
        _export.Content = Loc.Get("FileMap_Export");
        AutomationProperties.SetAutomationId(_export, "FileMap_Export");
        var exportMenu = new MenuFlyout();
        foreach (FileMapExportFormat format in Enum.GetValues<FileMapExportFormat>())
        {
            FileMapExportFormat f = format;
            var item = new MenuFlyoutItem { Text = Loc.Get("FileMap_Export_" + f) };
            AutomationProperties.SetAutomationId(item, "FileMap_Export_" + f);
            item.Click += async (_, _) => await _window.ExportFileMapAsync(f);
            exportMenu.Items.Add(item);
        }

        _export.Flyout = exportMenu;
        AutomationProperties.SetAutomationId(_status, "FileMap_Status");
        AutomationProperties.SetAutomationId(_progress, "FileMap_Progress");
        AutomationProperties.SetName(_progress, Loc.Get("FileMap_Progress"));
        AutomationProperties.SetAutomationId(_note, "FileMap_EstimateNote");
        AutomationProperties.SetAutomationId(_stale, "FileMap_Stale");
        AutomationProperties.SetAutomationId(_heading, "FileMap_Heading");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _note.Text = Loc.Get("FileMap_EstimateNote");
        _note.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _stale.Text = Loc.Get("FileMap_Stale");
        _stale.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        var statusRow = new Grid { ColumnSpacing = 8 };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
        {
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        statusRow.Children.Add(_status);
        AddAt(statusRow, _progress, 1);
        AddAt(statusRow, _cancel, 2);
        AddAt(statusRow, _create, 3);
        AddAt(statusRow, _rebuild, 4);
        AddAt(statusRow, _export, 5);

        // 帯の操作 (拡大・縮小・全体、表で表示)。
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tools.Children.Add(IconButton("FileMap_ZoomOut", "", () => _band.ZoomBy(0.5)));
        tools.Children.Add(IconButton("FileMap_ZoomIn", "", () => _band.ZoomBy(2)));
        tools.Children.Add(IconButton("FileMap_ZoomFit", "", _band.ZoomToFit));
        var table = new Button { Content = Loc.Get("FileMap_ShowTable") };
        AutomationProperties.SetAutomationId(table, "FileMap_ShowTable");
        table.Click += (_, _) => _table.Focus(FocusState.Keyboard);
        tools.Children.Add(table);

        _band.Height = (FileMapBand.LaneHeight * 2) + FileMapBand.LaneGap + FileMapBand.RulerHeight + 14;
        _band.NavigateRequested += (_, offset) => _window.FileMapNavigate(offset);
        _band.RegionActivated += (_, region) => _window.FileMapSelectRange(region);
        _band.SelectionChanged += (_, _) => SyncFromBand();
        _band.ContextMenuRequested += (_, e) => ShowMenu(e.Region, _band, e.At);
        _band.EditorChanged += (_, _) => UpdateStale();

        AutomationProperties.SetAutomationId(_legend, "FileMap_Legend");
        AutomationProperties.SetName(_legend, Loc.Get("FileMap_Legend"));

        // 一覧 (共通の結果一覧: 並べ替え・絞り込み・エクスポート・ブックマーク / マルチ選択への変換)。
        _table.SortableHeaders = true;
        _table.SetColumns(
        [
            new VirtualColumn(Loc.Get("FileMap_Col_Start"), 130, true), new VirtualColumn(Loc.Get("FileMap_Col_End"), 130, true),
            new VirtualColumn(Loc.Get("FileMap_Col_Length"), 120, true), new VirtualColumn(Loc.Get("FileMap_Col_Kind"), 150),
            new VirtualColumn(Loc.Get("FileMap_Col_Label"), 200), new VirtualColumn(Loc.Get("FileMap_Col_Confidence"), 70),
            new VirtualColumn(Loc.Get("FileMap_Col_Stage"), 80),
        ]);
        AutomationProperties.SetName(_table, Loc.Get("FileMap_List"));
        _table.RowProvider = RowCells;
        _table.RowName = row => _window.FileMapRowRegion(row) is int r and >= 0 && Map is { } map ? FileMapVisuals.RegionName(map, r) : string.Empty;
        _table.HeaderInvoked += (_, column) => _window.SortFileMapRows(column);
        _table.SelectionChanged += (_, _) => SyncFromTable();
        _table.Activated += (_, row) =>
        {
            if (_window.FileMapRowRegion(row) is int r and >= 0)
            {
                _window.FileMapSelectRange(r);
            }
        };
        _table.BuildContextMenu = (row, menu) =>
        {
            if (_window.FileMapRowRegion(row) is int r and >= 0)
            {
                FillMenu(menu, r);
            }
        };
        AutomationProperties.SetAutomationId(_filter, "FileMap_Filter");
        AutomationProperties.SetName(_filter, Loc.Get("FileMap_Filter"));
        _filter.Items.Add(Loc.Get("FileMap_Filter_All"));
        foreach (FileMapKind kind in Enum.GetValues<FileMapKind>())
        {
            _filter.Items.Add(FileMapVisuals.KindName(kind));
        }

        _filter.SelectionChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _window.FilterFileMapRows(_filter.SelectedIndex <= 0 ? null : (FileMapKind)(_filter.SelectedIndex - 1));
            }
        };
        var listTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        listTools.Children.Add(_filter);
        listTools.Children.Add(TextButton("FileMap_RowsToBookmarks", () => _window.FileMapRowsToBookmarks()));
        listTools.Children.Add(TextButton("FileMap_RowsToSelection", () => _window.FileMapRowsToMultiSelection()));

        var listArea = new Grid();
        listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listArea.Children.Add(listTools);
        Grid.SetRow(_table, 1);
        listArea.Children.Add(_table);

        var detail = new StackPanel { Spacing = 6, Padding = new Thickness(12, 4, 12, 8) };
        AutomationProperties.SetAutomationId(_detailTitle, "FileMap_DetailTitle");
        AutomationProperties.SetAutomationId(_detailInfo, "FileMap_DetailInfo");
        AutomationProperties.SetAutomationId(_detailEvidence, "FileMap_DetailEvidence");
        AutomationProperties.SetAutomationId(_detailActions, "FileMap_DetailActions");
        detail.Children.Add(_detailTitle);
        detail.Children.Add(_detailInfo);
        detail.Children.Add(_detailEvidence);
        detail.Children.Add(_detailActions);
        _detailScroll.Content = detail;
        _split.Children.Add(listArea);
        _split.Children.Add(_detailScroll);
        _split.Children.Add(_empty);
        AutomationProperties.SetAutomationId(_empty, "FileMap_Empty");
        _empty.Text = Loc.Get("FileMap_None");

        // 行: 0 状態、1 推定である旨・編集前の内容・見出し (変換ビュー・ほか n 件)、2 操作・帯・凡例、3 一覧と詳細。
        var notes = new StackPanel { Spacing = 2 };
        notes.Children.Add(_note);
        notes.Children.Add(_stale);
        notes.Children.Add(_heading);
        var bandArea = new StackPanel { Spacing = 4 };
        bandArea.Children.Add(tools);
        bandArea.Children.Add(_band);
        bandArea.Children.Add(_legend);
        var root = new Grid { Padding = new Thickness(12, 6, 12, 0), RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(statusRow);
        Grid.SetRow(notes, 1);
        root.Children.Add(notes);
        Grid.SetRow(bandArea, 2);
        root.Children.Add(bandArea);
        Grid.SetRow(_split, 3);
        root.Children.Add(_split);
        Content = root;
        AutomationProperties.SetAutomationId(this, "FileMapPanel");

        SizeChanged += (_, e) =>
        {
            // 幅が 800 px の境を越えたときだけ並べ替える (毎回並べ替えると配置が繰り返される)。
            if ((e.NewSize.Width < 800) != _narrow)
            {
                Arrange(e.NewSize.Width < 800);
            }
        };
        Arrange(narrow: false);
        Loaded += (_, _) =>
        {
            _band.Attach();
            Refresh();
        };
        Unloaded += (_, _) => _band.Detach();
    }

    internal FileMapBand Band => _band;

    internal VirtualTable Table => _table;

    internal WrapPanel Legend => _legend;

    internal bool IsNarrow => _narrow;

    internal string StatusText => _status.Text;

    internal bool StaleShown => _stale.Visibility == Visibility.Visible;

    internal bool RebuildShown => _rebuild.Visibility == Visibility.Visible;

    internal string HeadingText => _heading.Visibility == Visibility.Visible ? _heading.Text : string.Empty;

    internal string DetailText => string.Join(Environment.NewLine, _detailTitle.Text, _detailInfo.Text, _detailEvidence.Text);

    internal IReadOnlyList<string> DetailActionIds => [.. _detailActions.Children.OfType<Button>().Select(AutomationProperties.GetAutomationId)];

    private FileMapResult? Map => _window.CurrentFileMap;

    public bool FocusContent() => (_table.Count > 0 ? _table : (Control)_create).Focus(FocusState.Programmatic);

    /// <summary>状態と結果を表示し直す (結果・選択・並べ替えが変わったとき)。</summary>
    public void Refresh()
    {
        _syncing = true;
        try
        {
            FileMapState? state = _window.CurrentFileMapState;
            FileMapResult? map = state?.Result;
            bool building = state?.IsBuilding == true;
            _band.Editor = _window.FileMapEditor;
            _band.Map = map;
            _progress.Visibility = building ? Visibility.Visible : Visibility.Collapsed;
            if (building && state!.Operation is { } op)
            {
                _progress.IsIndeterminate = op.Fraction is null;
                _progress.Value = (op.Fraction ?? 0) * 100;
            }

            _status.Text = StatusOf(state);
            _cancel.Visibility = building ? Visibility.Visible : Visibility.Collapsed;
            _create.Visibility = map is null && !building ? Visibility.Visible : Visibility.Collapsed;
            bool stale = state is not null && _window.FileMapEditor is { } editor && state.IsStale(editor.Document);
            _rebuild.Visibility = map is not null && !building ? Visibility.Visible : Visibility.Collapsed;
            _stale.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
            _export.IsEnabled = map is not null && !building;
            var heading = new List<string>();
            if (state?.TransformLabel is { } transform)
            {
                heading.Add(transform);
            }

            if (map is { OmittedRegions: > 0 })
            {
                heading.Add(FileMapVisuals.Text(new FileMapMessage("FileMap_Omitted", map.OmittedRegions)));
            }

            if (map is { OmittedCandidates: > 0 })
            {
                heading.Add(Loc.Format("FileMap_OmittedCandidates", map.OmittedCandidates));
            }

            _heading.Text = string.Join("  ", heading);
            _heading.Visibility = heading.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(_heading, _heading.Text);
            RefreshLegend(map);
            _table.Refresh(_window.FileMapRowCount);
            _empty.Visibility = map is null ? Visibility.Visible : Visibility.Collapsed;
            _filter.SelectedIndex = _window.FileMapFilter is { } k ? (int)k + 1 : 0;
            if (_band.SelectedRegion != _window.FileMapSelected)
            {
                _band.Select(_window.FileMapSelected);
            }

            long row = _window.FileMapRowOf(_window.FileMapSelected);
            if (row != _table.SelectedIndex && row >= 0)
            {
                _table.Select(row);
            }

            RefreshDetail();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>作った後に編集されたら「結果は編集前の内容のものです」と「作り直す」を出す (ANA-33 の仕様 6)。</summary>
    private void UpdateStale()
    {
        FileMapState? state = _window.CurrentFileMapState;
        bool stale = state is not null && _window.FileMapEditor is { } editor && state.IsStale(editor.Document);
        _stale.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string StatusOf(FileMapState? state)
    {
        if (state is null || (state.Result is null && !state.IsBuilding))
        {
            return state?.Error ?? Loc.Get("FileMap_Status_None");
        }

        if (state.IsBuilding)
        {
            string stage = Loc.Get("FileMap_StageName_" + (state.RunningStage ?? FileMapStage.Sample));
            double percent = (state.Operation?.Fraction ?? 0) * 100;
            return Loc.Format("FileMap_Status_Running", stage, percent.ToString("N0", CultureInfo.CurrentCulture));
        }

        FileMapResult map = state.Result!;
        string count = map.Regions.Count.ToString("N0", CultureInfo.CurrentCulture);
        return map.Complete ? Loc.Format("FileMap_Status_Done", count)
            : Loc.Format("FileMap_Status_Cancelled", count, (map.Stages.ScanFraction * 100).ToString("N0", CultureInfo.CurrentCulture));
    }

    /// <summary>凡例: 種類の名前と模様、その種類のバイト数と割合 (仕様 3)。「標本による推定」の項目も置く。</summary>
    private void RefreshLegend(FileMapResult? map)
    {
        _legend.Children.Clear();
        if (map is null)
        {
            return;
        }

        bool hc = ChartSupport.IsHighContrast;
        long total = Math.Max(1, map.Length);
        foreach (FileMapKind kind in Enum.GetValues<FileMapKind>())
        {
            long bytes = map.BytesOf(kind);
            string text = Loc.Format("FileMap_LegendItem", FileMapVisuals.KindName(kind), bytes, ((double)bytes / total).ToString("P1", CultureInfo.CurrentCulture));
            _legend.Children.Add(LegendItem("FileMap_Legend_" + kind, text, FileMapVisuals.PatternOf(kind), FileMapVisuals.FillKey(kind, hc), hc, sample: false));
        }

        _legend.Children.Add(LegendItem("FileMap_Legend_Sample", Loc.Get("FileMap_Kind_Sample"), FileMapVisuals.SamplePattern,
            hc ? "SystemColorWindowColorBrush" : "SubtleFillColorSecondaryBrush", hc, sample: true));
    }

    private StackPanel LegendItem(string id, string text, string pattern, string fillKey, bool hc, bool sample)
    {
        var swatch = new Canvas { Width = 28, Height = 16 };
        var rect = new Rect(0, 0, 28, 16);
        Brush patternBrush = ChartSupport.Brush(FileMapVisuals.PatternBrushKey(hc), this);
        swatch.Children.Add(new Path { Data = new RectangleGeometry { Rect = rect }, Fill = ChartSupport.Brush(fillKey, this), Stroke = patternBrush, StrokeThickness = 1, Opacity = sample && !hc ? 0.6 : 1 });
        swatch.Children.Add(new Path { Data = ChartSupport.Pattern(pattern, rect, sample ? 4 : 5), Stroke = patternBrush, Fill = patternBrush });
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 14, 2) };
        item.Children.Add(swatch);
        item.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        AutomationProperties.SetAutomationId(item, id);
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetItemStatus(item, $"pattern={pattern};color={(hc ? fillKey : fillKey)};stroke={FileMapVisuals.PatternBrushKey(hc)}");
        return item;
    }

    private IReadOnlyList<string> RowCells(long row)
    {
        int r = _window.FileMapRowRegion(row);
        if (Map is not { } map || r < 0)
        {
            return [];
        }

        FileMapRegion region = map.Regions[r];
        string indent = region.Parent >= 0 ? "  " : string.Empty;
        return
        [
            indent + "0x" + region.Offset.ToString("X", CultureInfo.InvariantCulture), "0x" + region.Last.ToString("X", CultureInfo.InvariantCulture),
            region.Length.ToString("N0", CultureInfo.CurrentCulture), FileMapVisuals.KindName(region.Kind), FileMapVisuals.Label(map, r),
            Loc.Get("FileMap_Confidence_" + region.Confidence), Loc.Get("FileMap_Stage_" + region.Stage),
        ];
    }

    private void SyncFromBand()
    {
        if (_syncing)
        {
            return;
        }

        _window.FileMapSelect(_band.SelectedRegion, fromBand: true);
    }

    private void SyncFromTable()
    {
        if (_syncing)
        {
            return;
        }

        _window.FileMapSelect(_window.FileMapRowRegion(_table.SelectedIndex), fromBand: false);
    }

    private void RefreshDetail()
    {
        _detailActions.Children.Clear();
        int r = _window.FileMapSelected;
        FileMapResult? map = Map;
        if (map is null || r < 0 || r >= map.Regions.Count)
        {
            _detailTitle.Text = map is null ? string.Empty : Loc.Get("FileMap_NoSelection");
            _detailInfo.Text = string.Empty;
            _detailEvidence.Text = string.Empty;
            return;
        }

        FileMapRegion region = map.Regions[r];
        _detailTitle.Text = FileMapVisuals.KindName(region.Kind) + " — " + FileMapVisuals.Label(map, r);
        _detailInfo.Text = string.Join(Environment.NewLine, FileMapVisuals.Range(region),
            Loc.Format("FileMap_ConfidenceText", Loc.Get("FileMap_Confidence_" + region.Confidence)) + "  " + Loc.Format("FileMap_StageText", Loc.Get("FileMap_Stage_" + region.Stage))
            + (region.Detail >= 0 && map.Details[region.Detail].Template is { } t ? "  " + Loc.Format("FileMap_TemplateText", t.DisplayName(CultureInfo.CurrentUICulture)) : string.Empty));
        _detailEvidence.Text = FileMapVisuals.Evidence(map, r);
        foreach (FileMapAction action in _window.FileMapActions(r))
        {
            var button = new Button { Content = action.Label, IsEnabled = action.Enabled, Margin = new Thickness(0, 0, 6, 6) };
            AutomationProperties.SetAutomationId(button, "FileMap_Action_" + action.Id);
            if (action.Reason is { } reason)
            {
                ToolTipService.SetToolTip(button, reason);
                AutomationProperties.SetHelpText(button, reason);
            }

            button.Click += async (_, _) => await action.Run();
            _detailActions.Children.Add(button);
        }
    }

    /// <summary>右クリックメニュー (帯と一覧で同じ。その種類の領域で使えるものだけ)。</summary>
    internal void ShowMenu(int region, FrameworkElement target, Point at)
    {
        var menu = new MenuFlyout();
        FillMenu(menu, region);
        if (menu.Items.Count > 0)
        {
            menu.ShowAt(target, at);
        }
    }

    internal MenuFlyout? LastMenu { get; private set; }

    private void FillMenu(MenuFlyout menu, int region)
    {
        AutomationProperties.SetAutomationId(menu, "FileMap_Menu");
        foreach (FileMapAction action in _window.FileMapActions(region))
        {
            var item = new MenuFlyoutItem { Text = action.Label, IsEnabled = action.Enabled };
            AutomationProperties.SetAutomationId(item, "FileMap_Menu_" + action.Id);
            if (action.Reason is { } reason)
            {
                ToolTipService.SetToolTip(item, reason);
            }

            item.Click += async (_, _) => await action.Run();
            menu.Items.Add(item);
        }

        LastMenu = menu;
    }

    private void Arrange(bool narrow)
    {
        _narrow = narrow;
        _split.RowDefinitions.Clear();
        _split.ColumnDefinitions.Clear();
        if (narrow)
        {
            _split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            _split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(_detailScroll, 0);
            Grid.SetRow(_detailScroll, 1);
            _detailScroll.MaxHeight = 220;
        }
        else
        {
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            Grid.SetRow(_detailScroll, 0);
            Grid.SetColumn(_detailScroll, 1);
            _detailScroll.MaxHeight = double.PositiveInfinity;
        }
    }

    private static void AddAt(Grid grid, FrameworkElement e, int column)
    {
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }

    private static void Setup(Button button, string key, Func<Task> run)
    {
        button.Content = Loc.Get(key);
        AutomationProperties.SetAutomationId(button, key);
        button.Click += async (_, _) => await run();
    }

    private static Button TextButton(string key, Action run)
    {
        var button = new Button { Content = Loc.Get(key) };
        AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => run();
        return button;
    }

    private static Button IconButton(string key, string glyph, Action run)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetAutomationId(button, key);
        AutomationProperties.SetName(button, Loc.Get(key));
        ToolTipService.SetToolTip(button, Loc.Get(key));
        button.Click += (_, _) => run();
        return button;
    }
}
