using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using HexEditor.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using HexEditor.Core.View;

namespace HexEditor.App.Controls;

/// <summary>比較タブが使うウィンドウの機能 (コマンドの実行と状態、Hex ビューの共通の設定)。</summary>
public interface ICompareViewHost
{
    CommandState StateOf(string commandId);

    Task ExecuteAsync(string commandId);

    /// <summary>Hex ビューに表示の設定・ズーム・キーの振り分けなど、通常のタブと同じ設定をする。</summary>
    void AttachHexView(HexView view, CompareSessionViewModel session, CompareSideViewModel side);
}

/// <summary>
/// 比較タブの中身 (ANA-04)。2 つの Hex ビュー (通常のタブと同じ部品) を左右または上下に並べ、差分を種類ごとに強調し (VIEW-17 の層 11)、
/// 各ビューの右端に差分マップを置く。同期スクロール・差分の間の移動・マージの処理は <see cref="CompareSessionViewModel"/> が持つ。
/// </summary>
public sealed partial class CompareView : UserControl
{
    /// <summary>ツールバーのコマンド (null は区切り)。</summary>
    private static readonly string?[] ToolbarCommands =
    [
        "compare.recompare", null, "compare.previousDiff", "compare.nextDiff", null, "compare.syncScroll", "compare.layout",
        null, "compare.copyLeft", "compare.copyRight", "compare.copyAllLeft", "compare.copyAllRight", null, "compare.saveReport",
    ];

    private readonly ICompareViewHost _host;
    private bool _settingMethod;

    public CompareView(ICompareViewHost host, CompareSessionViewModel session)
    {
        _host = host;
        Session = session;
        InitializeComponent();
        AutomationProperties.SetName(this, session.Title);
        AutomationProperties.SetName(MethodBox, Loc.Get("Compare_Method_Name"));
        ToolTipService.SetToolTip(MethodBox, Loc.Get("Compare_Method_Name"));
        MethodBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Compare_Method_Simple"), Tag = CompareMethod.Simple });
        MethodBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Compare_Method_InsertDelete"), Tag = CompareMethod.InsertDelete });
        if (session.FromPieces)
        {
            // ピースから求めた差分 (ANA-08 の仕様 2) は方式を選べない。
            MethodBox.IsEnabled = false;
        }

        LeftView = CreateHexView(session.Left);
        RightView = CreateHexView(session.Right);
        LeftMap = CreateMap(session.Left);
        RightMap = CreateMap(session.Right);
        LeftHeader = CreateHeader(session.Left);
        RightHeader = CreateHeader(session.Right);
        LeftRegions = CreateRegionBox(session.Left);
        RightRegions = CreateRegionBox(session.Right);
        BuildToolbar();
        Layout();

        session.ResultChanged += Session_ResultChanged;
        session.ViewChanged += Session_ViewChanged;
        session.PropertyChanged += Session_PropertyChanged;
        MessageBar.Closed += (_, _) => _dismissedMessage = Session.StatusMessage;
        Loaded += (_, _) => UpdateAll();
        UpdateAll();
    }

    private string? _dismissedMessage;

    public CompareSessionViewModel Session { get; }

    public HexView LeftView { get; }

    public HexView RightView { get; }

    internal DiffMap LeftMap { get; }

    internal DiffMap RightMap { get; }

    private TextBlock LeftHeader { get; }

    private TextBlock RightHeader { get; }

    /// <summary>領域のコンボボックス (プロセスメモリ・スナップショットの側だけ。ANA-09 の「画面」)。</summary>
    private ComboBox? LeftRegions { get; }

    private ComboBox? RightRegions { get; }

    public HexView ViewOf(bool right) => right ? RightView : LeftView;

    internal DiffMap MapOf(bool right) => right ? RightMap : LeftMap;

    // ---- 部品を作る ----

    private HexView CreateHexView(CompareSideViewModel side)
    {
        var view = new HexView { DataContext = side.View };
        AutomationProperties.SetAutomationId(view, side.IsRight ? "Compare_RightView" : "Compare_LeftView");
        view.Editor = side.Editor;
        view.SetHighlightSource("diff", (start, end) => Highlights(view, side.IsRight, start, end));
        view.DiffMarkerSource = (start, end) => MarkerBrush(view, side.IsRight, start, end);
        view.CellStates = offset => Session.CellStates(side.IsRight, offset);
        view.GotFocus += (_, _) => Focused?.Invoke(this, side);
        _host.AttachHexView(view, Session, side);
        return view;
    }

    /// <summary>片側の Hex ビューにフォーカスが移った (コマンドの対象とステータスバーをその側にする)。</summary>
    public event EventHandler<CompareSideViewModel>? Focused;

    private DiffMap CreateMap(CompareSideViewModel side)
    {
        var map = new DiffMap();
        HexView view = ViewOf(side.IsRight);
        map.HighContrastProvider = () => view.IsHighContrast;
        map.Attach(Session, side.IsRight);
        return map;
    }

    private TextBlock CreateHeader(CompareSideViewModel side)
    {
        var header = new TextBlock
        {
            Margin = new Thickness(8, 4, 8, 4),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AutomationProperties.SetAutomationId(header, side.IsRight ? "Compare_RightHeader" : "Compare_LeftHeader");
        return header;
    }

    /// <summary>左右 (既定) または上下に並べる (表示 > 比較のレイアウト)。</summary>
    public void Layout()
    {
        Sides.Children.Clear();
        Sides.RowDefinitions.Clear();
        Sides.ColumnDefinitions.Clear();
        bool stacked = Session.Stacked;
        for (int i = 0; i < 2; i++)
        {
            if (stacked)
            {
                Sides.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }
            else
            {
                Sides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
        }

        foreach (bool right in new[] { false, true })
        {
            Grid panel = SidePanel(right);
            Grid.SetRow(panel, stacked && right ? 1 : 0);
            Grid.SetColumn(panel, !stacked && right ? 1 : 0);
            Sides.Children.Add(panel);
        }
    }

    private Grid SidePanel(bool right)
    {
        TextBlock header = right ? RightHeader : LeftHeader;
        ComboBox? regions = right ? RightRegions : LeftRegions;
        HexView view = ViewOf(right);
        DiffMap map = MapOf(right);
        foreach (FrameworkElement? element in new FrameworkElement?[] { header, regions, view, map })
        {
            if (element?.Parent is Panel old)
            {
                old.Children.Remove(element);
            }
        }

        // 見出しの行: 名前と比較範囲、右に領域のコンボボックス。
        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.Children.Add(header);
        if (regions is not null)
        {
            Grid.SetColumn(regions, 1);
            regions.VerticalAlignment = VerticalAlignment.Center;
            headerRow.Children.Add(regions);
        }

        var panel = new Grid
        {
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = right ? new Thickness(1, 0, 0, 0) : new Thickness(0),
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(headerRow, 2);
        Grid.SetRow(view, 1);
        Grid.SetRow(map, 1);
        Grid.SetColumn(map, 1);
        panel.Children.Add(headerRow);
        panel.Children.Add(view);
        panel.Children.Add(map);
        return panel;
    }

    /// <summary>ツールバーのボタンのアイコン (Segoe Fluent Icons)。</summary>
    private static string GlyphOf(string id) => id switch
    {
        "compare.recompare" => "",
        "compare.previousDiff" => "",
        "compare.nextDiff" => "",
        "compare.syncScroll" => "",
        "compare.layout" => "",
        "compare.copyLeft" => "",
        "compare.copyRight" => "",
        "compare.copyAllLeft" => "",
        "compare.copyAllRight" => "",
        _ => "",
    };

    private void BuildToolbar()
    {
        Toolbar.PrimaryCommands.Clear();
        foreach (string? id in ToolbarCommands)
        {
            if (id is null)
            {
                Toolbar.PrimaryCommands.Add(new AppBarSeparator());
                continue;
            }

            CommandDefinition? command = CommandService.Catalog.Find(id);
            string name = command is null ? id : CommandService.DisplayName(command);
            string keys = CommandService.ShortcutText(id);
            var icon = new FontIcon { Glyph = command?.Icon ?? GlyphOf(id) };
            ButtonBase button;
            if (id is "compare.syncScroll" or "compare.layout")
            {
                button = new AppBarToggleButton { Label = name, Icon = icon };
            }
            else
            {
                button = new AppBarButton { Label = name, Icon = icon };
            }

            button.Tag = id;
            AutomationProperties.SetAutomationId(button, "CompareToolbar_" + id);
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, keys.Length > 0 ? Loc.Format("Toolbar_ToolTip", name, keys) : name);
            button.Click += async (_, _) =>
            {
                await _host.ExecuteAsync(id);
                RefreshToolbarStates();
            };
            Toolbar.PrimaryCommands.Add((ICommandBarElement)button);
        }

        // 方式ごとのオプションを変えて再比較する (ANA-04 の「画面」の「オプション」)。
        Toolbar.PrimaryCommands.Add(new AppBarSeparator());
        Toolbar.PrimaryCommands.Add(CreateOptionsButton());
    }

    // ---- 表示の更新 ----

    private void Session_ResultChanged(object? sender, EventArgs e)
    {
        UpdateAll();
        LeftMap.Refresh();
        RightMap.Refresh();
    }

    private void Session_ViewChanged(object? sender, EventArgs e)
    {
        LeftMap.UpdateViewport();
        RightMap.UpdateViewport();
        RefreshToolbarStates();
        UpdateRegionSelection();
    }

    private void Session_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompareSessionViewModel.Stacked))
        {
            Layout();
        }

        UpdateAll();
    }

    /// <summary>見出し・InfoBar・進捗・強調・ツールバーの状態を今の比較の状態に合わせる。</summary>
    public void UpdateAll()
    {
        CompareResult? r = Session.Result;
        UpdateHeader(LeftHeader, Session.Left, r?.Left);
        UpdateHeader(RightHeader, Session.Right, r?.Right);

        _settingMethod = true;
        MethodBox.SelectedIndex = (r?.Method ?? Session.Options.Method) == CompareMethod.Simple && !Session.FromPieces ? 0 : 1;
        _settingMethod = false;

        StaleBar.IsOpen = Session.IsStale && !Session.IsRunning;
        StaleBar.Message = Loc.Get(Session.StaleByExternalChange ? "Compare_StaleBar_External" : "Compare_StaleBar/Message");
        string? message = Session.StatusMessage;
        MessageBar.Message = message ?? string.Empty;
        MessageBar.IsOpen = message is not null && message != _dismissedMessage;

        Progress.Visibility = Session.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        if (r is not null && Session.IsRunning)
        {
            long total = Math.Max(1, Math.Max(r.Left.Length, r.Right.Length));
            Progress.Value = Math.Clamp((double)Math.Max(r.LeftPosition, r.RightPosition) / total, 0, 1);
        }

        LeftView.RefreshHighlights();
        RightView.RefreshHighlights();
        LeftView.RefreshMarkers();
        RightView.RefreshMarkers();
        RefreshToolbarStates();
    }

    /// <summary>見出し: 「名前  0x200〜0xFFF (3,584 バイト)」(比較範囲。06 の 0.1 の表示と同じ形)。</summary>
    private static void UpdateHeader(TextBlock header, CompareSideViewModel side, CompareRange? range)
    {
        string text = side.Name;
        if (range is { } r)
        {
            string last = r.Length > 0 ? "0x" + (r.Start + r.Length - 1).ToString("X", CultureInfo.InvariantCulture) : "—";
            text += "  " + Loc.Format("Compare_Range", "0x" + r.Start.ToString("X", CultureInfo.InvariantCulture), last,
                r.Length.ToString("N0", CultureInfo.CurrentCulture));
        }

        header.Text = text;
        AutomationProperties.SetName(header, text);
    }

    public void RefreshToolbarStates()
    {
        foreach (ICommandBarElement element in Toolbar.PrimaryCommands)
        {
            if (element is AppBarButton { Tag: string id } button)
            {
                CommandState state = _host.StateOf(id);
                button.IsEnabled = state.Enabled;

                // 使えない理由をツールチップに出す (「右側は読み取り専用です」。ANA-07 の「エラー」)。
                string name = AutomationProperties.GetName(button);
                ToolTipService.SetToolTip(button, state.Enabled || state.Reason is null ? name : Loc.Format("Compare_DisabledToolTip", name, state.Reason));
            }
            else if (element is AppBarToggleButton { Tag: string toggleId } toggle)
            {
                CommandState state = _host.StateOf(toggleId);
                toggle.IsEnabled = state.Enabled;
                toggle.IsChecked = state.Checked ?? false;
            }
        }
    }

    // ---- 差分の強調 (VIEW-17 の層 11、ANA-04 の仕様 3) ----

    private IEnumerable<HexHighlight> Highlights(HexView view, bool right, long start, long end)
    {
        if (Session.Result is not { } r)
        {
            yield break;
        }

        bool highContrast = view.IsHighContrast;
        Core.View.ColorScheme? scheme = view.ColorScheme;
        foreach ((_, DiffRange d) in r.Diffs.Overlapping(right, start, end, 4096))
        {
            long offset = d.Start(right);
            long length = d.Length(right);
            if (length == 0)
            {
                // 挿入・削除の相手側: 揃えるための空白の位置に斜線の模様を描く (ANA-03 の「画面」)。
                yield return new HexHighlight(offset, 0, CellLayer.Difference, CompareBrushes.Padding(view, highContrast), null, null,
                    "diff-padding:" + CompareBrushes.PaddingKey, Mark: HexMark.Hatch, MarkBrush: CompareBrushes.PaddingMark(view, highContrast));
                continue;
            }

            Brush background = CompareBrushes.Background(d.Kind, view, highContrast, scheme);
            Brush mark = CompareBrushes.Mark(d.Kind, view, highContrast);
            string tag = "diff-" + DiffExport.KindName(d.Kind) + ":" + CompareBrushes.BackgroundKey(d.Kind);
            yield return d.Kind == DiffKind.Changed
                ? new HexHighlight(offset, length, CellLayer.Difference, background, mark, CompareBrushes.ChangedDash, tag)
                : new HexHighlight(offset, length, CellLayer.Difference, background, null, null, tag, Mark: CompareBrushes.MarkOf(d.Kind), MarkBrush: mark);
        }
    }

    /// <summary>スクロールバーの差分の印 (VIEW-02 の仕様 9): その範囲に重なる差分の最も多い種類の色。</summary>
    private Brush? MarkerBrush(HexView view, bool right, long start, long end)
    {
        if (Session.Result is not { } r)
        {
            return null;
        }

        List<(long Index, DiffRange Diff)> found = r.Diffs.Overlapping(right, start, end, 16);
        if (found.Count == 0)
        {
            return null;
        }

        DiffKind kind = found.GroupBy(f => f.Diff.Kind).OrderByDescending(g => g.Sum(f => f.Diff.Length(right))).First().Key;
        return CompareBrushes.Mark(kind, view, view.IsHighContrast);
    }

    // ---- 操作 ----

    /// <summary>方式のコンボボックスで選ぶ (テスト用の命令からも使う)。</summary>
    public void SelectMethod(int index) => MethodBox.SelectedIndex = index;

    private async void Recompare_Click(object sender, RoutedEventArgs e) => await _host.ExecuteAsync("compare.recompare");

    private async void MethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingMethod || MethodBox.SelectedItem is not ComboBoxItem { Tag: CompareMethod method } || method == Session.Options.Method)
        {
            return;
        }

        await Session.RecompareWithAsync(Session.Options with { Method = method });
    }
}
