using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>構造の差分パネルが使うウィンドウの機能 (ANA-32)。</summary>
public interface IStructDiffHost
{
    /// <summary>行を選んだ: 比較タブを前に出し、左右の Hex ビューで範囲を選ぶ (仕様 4)。</summary>
    void SelectStructDiff(CompareSessionViewModel session, StructDiffNode node);

    Task ChooseStructTemplateAsync(CompareSessionViewModel session);

    void EditStructTemplate(CompareSessionViewModel session);

    Task ShowStructDiagnosticsAsync(CompareSessionViewModel session, bool right);

    Task ExportStructDiffsAsync(CompareSessionViewModel session, StructDiffView view);

    void CopyText(string text);

    void AddStructBookmark(CompareSessionViewModel session, StructDiffNode node, bool right);

    Task ShowInResultTreeAsync(CompareSessionViewModel session, StructDiffNode node, bool right);

    Task ChangeArrayRuleAsync(CompareSessionViewModel session, StructDiffNode node);

    void ShowStatusMessage(string message);

    /// <summary>次 / 前の構造の差分 (Alt+F5 / Shift+Alt+F5 と同じ処理)。</summary>
    void MoveStruct(CompareSessionViewModel session, bool next);
}

/// <summary>
/// 構造の差分パネル (ANA-32)。構造の比較 (ANA-31) の結果を、結果ツリー (TPL-21) と同じ形のツリー表 (仮想化した一覧) で表示する。
/// 差分のある葉とその祖先だけを表示し、種類・文字列で絞り込める。表示するのは最後に使った比較タブの結果。
/// </summary>
public sealed partial class StructDiffPanel : UserControl
{
    private static readonly (StructDiffFilterKinds Kind, StructDiffKind Sample, string Key)[] KindFilters =
    [
        (StructDiffFilterKinds.Changed, StructDiffKind.Changed, "StructDiff_Kind_Changed"),
        (StructDiffFilterKinds.Added, StructDiffKind.Added, "StructDiff_Kind_Added"),
        (StructDiffFilterKinds.Removed, StructDiffKind.Removed, "StructDiff_Kind_Removed"),
        (StructDiffFilterKinds.Moved, StructDiffKind.Moved, "StructDiff_Kind_Moved"),
        (StructDiffFilterKinds.TypeChanged, StructDiffKind.TypeChanged, "StructDiff_Kind_TypeChanged"),
        (StructDiffFilterKinds.Unparsed, StructDiffKind.Unparsed, "StructDiff_Kind_Unparsed"),
    ];

    private static readonly (string Key, double Width)[] Columns =
    [
        ("StructDiff_Column_Name", 260), ("StructDiff_Column_Kind", 190), ("StructDiff_Column_LeftValue", 160), ("StructDiff_Column_RightValue", 160),
        ("StructDiff_Column_Type", 110), ("StructDiff_Column_LeftOffset", 100), ("StructDiff_Column_RightOffset", 100), ("StructDiff_Column_Size", 90),
        ("StructDiff_Column_Match", 120),
    ];

    private readonly List<ToggleButton> _kindToggles = [];
    private readonly StructDiffRowList _rows;
    private CompareSessionViewModel? _session;
    private long _shownVersion = -1;
    private bool _summaryCollapsed;
    private CancellationTokenSource? _filterDelay;

    public StructDiffPanel()
    {
        InitializeComponent();
        Tree.Panel = this;
        _rows = new StructDiffRowList(CreateRow);
        Tree.ItemsSource = _rows;
        foreach ((StructDiffFilterKinds kind, StructDiffKind sample, string key) in KindFilters)
        {
            var toggle = new ToggleButton
            {
                IsChecked = true,
                Tag = kind,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children = { new FontIcon { FontSize = 12, Glyph = KindGlyph(sample) }, new TextBlock { Text = Loc.Get(key) } },
                },
            };
            AutomationProperties.SetAutomationId(toggle, "StructDiff_Filter" + kind);
            AutomationProperties.SetName(toggle, Loc.Format("StructDiff_FilterKind", Loc.Get(key)));
            toggle.Click += Filter_Click;
            _kindToggles.Add(toggle);
            ToolbarPanel.Children.Insert(_kindToggles.Count - 1, toggle);
        }

        string previous = Loc.Get("Cmd_compare_previousStructDiff");
        string next = Loc.Get("Cmd_compare_nextStructDiff");
        AutomationProperties.SetName(PreviousButton, previous);
        AutomationProperties.SetName(NextButton, next);
        ToolTipService.SetToolTip(PreviousButton, previous);
        ToolTipService.SetToolTip(NextButton, next);
        AutomationProperties.SetName(TemplateButton, Loc.Get("Cmd_compare_structureTemplate"));
        BuildHeader();
        Update();
    }

    internal IStructDiffHost? Host { get; set; }

    /// <summary>表示の計算 (絞り込み・開閉・次 / 前)。</summary>
    public StructDiffView View { get; } = new();

    /// <summary>表示している比較タブ。</summary>
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
                _session.StructResultChanged -= Session_StructResultChanged;
            }

            _session = value;
            if (_session is not null)
            {
                _session.StructResultChanged += Session_StructResultChanged;
            }

            _shownVersion = -1;
            Update();
        }
    }

    /// <summary>今の絞り込みの条件。</summary>
    public StructDiffFilter CurrentFilter => new()
    {
        Kinds = _kindToggles.Where(t => t.IsChecked == true).Aggregate(StructDiffFilterKinds.None, (a, t) => a | (StructDiffFilterKinds)t.Tag),
        IncludePadding = PaddingToggle.IsChecked == true,
        ShowSame = SameToggle.IsChecked == true,
        Text = FilterBox.Text.Trim(),
    };

    /// <summary>絞り込みの計算中 (100 万件で 500 ms を超える場合はバックグラウンドで行う)。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>ツリー表にフォーカスがあるか (Alt+F5 を構造の差分の移動にする。ANA-32 の「呼び出し」)。</summary>
    public bool HasTreeFocus => FocusManager.GetFocusedElement(XamlRoot) is DependencyObject d && IsInside(d);

    private bool IsInside(DependencyObject d)
    {
        for (DependencyObject? p = d; p is not null; p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
        {
            if (ReferenceEquals(p, this))
            {
                return true;
            }
        }

        return false;
    }

    private static string KindGlyph(StructDiffKind kind) => kind switch
    {
        StructDiffKind.Changed => "",
        StructDiffKind.Added => "",
        StructDiffKind.Removed => "",
        StructDiffKind.Moved => "",
        _ => "",
    };

    private void BuildHeader()
    {
        HeaderRow.ColumnDefinitions.Clear();
        HeaderRow.Children.Clear();
        for (int i = 0; i < Columns.Length; i++)
        {
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Columns[i].Width) });
            var text = new TextBlock { Text = Loc.Get(Columns[i].Key), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            Grid.SetColumn(text, i);
            HeaderRow.Children.Add(text);
        }
    }

    private StructDiffRowViewModel CreateRow(int index)
    {
        bool hex = _session?.Left.Editor.View.Radix != OffsetRadix.Decimal;
        return new StructDiffRowViewModel(View.RowAt(index), View, View.Result!, hex, this);
    }

    private void Session_StructResultChanged(object? sender, EventArgs e) => Update();

    /// <summary>結果が変わった: 要約と一覧を作り直す (比較中は 0.5 秒ごと)。</summary>
    public void Update()
    {
        StructCompareResult? r = _session?.StructResult;
        UpdateSummary(r);
        if (r is null || r.Choice.IsNone || r.State is StructCompareState.SyntaxError or StructCompareState.TemplateMissing or StructCompareState.Unsupported)
        {
            View.Rebuild(null);
            _rows.Reset(0);
            return;
        }

        if (r.Version == _shownVersion && ReferenceEquals(View.Result, r))
        {
            return;
        }

        _shownVersion = r.Version;
        Rebuild(r, CurrentFilter);
    }

    private void Rebuild(StructCompareResult r, StructDiffFilter filter)
    {
        StructDiffNode? selected = (Tree.SelectedItem as StructDiffRowViewModel)?.Node ?? _session?.StructCurrent;
        View.Rebuild(r, filter);
        _rows.Reset(View.RowCount);
        if (selected is not null && View.RowOf(selected) is int row and >= 0)
        {
            Tree.SelectedIndex = row;
        }

        EmptyText.Visibility = View.RowCount == 0 && !r.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Loc.Get("StructDiff_NoDiffs");
    }

    private void UpdateSummary(StructCompareResult? r)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        bool enabled = _session?.StructureEnabled ?? false;
        ReasonArea.Visibility = Visibility.Collapsed;
        EditTemplateButton.Visibility = Visibility.Collapsed;
        RaiseLimitButton.Visibility = Visibility.Collapsed;
        ChooseTemplateButton.Visibility = Visibility.Visible;
        LeftDiagnostics.Visibility = Visibility.Collapsed;
        RightDiagnostics.Visibility = Visibility.Collapsed;
        TemplateButton.Content = _session?.StructTemplateLabel ?? Loc.Get("Compare_Struct_ChooseTemplate");
        if (_session is null)
        {
            HeadingText.Text = Loc.Get("StructDiff_NoCompare");
            SummaryText.Text = string.Empty;
            return;
        }

        if (!enabled || r is null)
        {
            HeadingText.Text = enabled ? Loc.Get("StructDiff_Waiting") : Loc.Get("StructDiff_Disabled");
            SummaryText.Text = string.Empty;
            return;
        }

        string? reason = r.State switch
        {
            StructCompareState.NoTemplate => Loc.Get("StructDiff_NoTemplate"),
            StructCompareState.SyntaxError => Loc.Format("StructDiff_SyntaxError", r.Message ?? string.Empty),
            StructCompareState.TemplateMissing => Loc.Format("StructDiff_TemplateMissing", r.Choice.TemplatePath),
            StructCompareState.Unsupported => r.Message ?? Loc.Get("Compare_Struct_Unsupported"),
            StructCompareState.Failed => Loc.Format("StructDiff_Failed", r.Message ?? string.Empty),
            StructCompareState.FieldLimit => Loc.Format("StructDiff_FieldLimit", r.MaxFields.ToString("N0", c)),
            _ => null,
        };
        if (reason is not null)
        {
            ReasonText.Text = reason;
            ReasonArea.Visibility = Visibility.Visible;
            EditTemplateButton.Visibility = r.State == StructCompareState.SyntaxError ? Visibility.Visible : Visibility.Collapsed;
            RaiseLimitButton.Visibility = r.State == StructCompareState.FieldLimit && r.MaxFields < StructCompareOptions.MaxMaxFields
                ? Visibility.Visible : Visibility.Collapsed;
            ChooseTemplateButton.Visibility = r.State is StructCompareState.FieldLimit or StructCompareState.Unsupported ? Visibility.Collapsed : Visibility.Visible;
        }

        if (r.Choice.IsNone)
        {
            HeadingText.Text = Loc.Get("StructDiff_Title");
            SummaryText.Text = string.Empty;
            return;
        }

        string why = Loc.Get(r.Choice.Reason switch
        {
            StructTemplateReason.UserChoice => "StructDiff_Reason_UserChoice",
            StructTemplateReason.SameApplication => "StructDiff_Reason_SameApplication",
            _ => "StructDiff_Reason_MagicMatch",
        });
        HeadingText.Text = Loc.Format("StructDiff_Heading", r.TemplateName, Path.GetFileName(r.Choice.TemplatePath),
            "0x" + r.Choice.LeftStart.ToString("X", CultureInfo.InvariantCulture), "0x" + r.Choice.RightStart.ToString("X", CultureInfo.InvariantCulture), why);
        if (r.State is StructCompareState.SyntaxError or StructCompareState.TemplateMissing)
        {
            SummaryText.Text = string.Empty;
            return;
        }

        string counts = string.Join(Loc.Get("StructDiff_CountSeparator"),
            new[] { StructDiffKind.Changed, StructDiffKind.Added, StructDiffKind.Removed, StructDiffKind.Moved, StructDiffKind.MovedChanged, StructDiffKind.TypeChanged, StructDiffKind.Unparsed }
                .Select(k => StructDiffRowViewModel.KindName(k) + " " + r.CountOf(k).ToString("N0", c)));
        string outside = r.Outside is { } o ? Loc.Format("StructDiff_OutsideValue", o.Count.ToString("N0", c), o.Bytes.ToString("N0", c)) : "—";
        string state = r.State switch
        {
            StructCompareState.Running => Loc.Format("StructDiff_Running", r.LeftFields.ToString("N0", c)),
            StructCompareState.Cancelled => Loc.Get("StructDiff_Cancelled"),
            StructCompareState.FieldLimit => Loc.Get("StructDiff_StoppedAtLimit"),
            _ => Loc.Format("StructDiff_Elapsed", r.Elapsed.TotalSeconds.ToString("N2", c)),
        };
        SummaryText.Text = Loc.Format("StructDiff_SummaryText", r.LeftFields.ToString("N0", c), r.RightFields.ToString("N0", c), counts, outside, state);
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        LeftDiagnostics.Content = Loc.Format("StructDiff_LeftDiagnostics", r.LeftErrors.ToString("N0", c), r.LeftWarnings.ToString("N0", c));
        RightDiagnostics.Content = Loc.Format("StructDiff_RightDiagnostics", r.RightErrors.ToString("N0", c), r.RightWarnings.ToString("N0", c));
        LeftDiagnostics.Visibility = Visibility.Visible;
        RightDiagnostics.Visibility = Visibility.Visible;
    }

    // ---- 絞り込み (ANA-32 の仕様 3) ----

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private async void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterDelay?.Cancel();
        var cts = new CancellationTokenSource();
        _filterDelay = cts;
        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        ApplyFilter();
    }

    /// <summary>絞り込みを変えて一覧を作り直す。100 万件を超える結果はバックグラウンドで計算し、一覧の上に進捗を出す。</summary>
    public void ApplyFilter()
    {
        if (View.Result is not { } r)
        {
            return;
        }

        Rebuild(r, CurrentFilter);
    }

    // ---- 行の操作 (仕様 4〜6、10) ----

    /// <summary>行をクリックした: その行の範囲を左右の Hex ビューで選ぶ (仕様 4)。開閉の印のクリックは開閉だけ。</summary>
    private void Tree_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not StructDiffRowViewModel row || row.Node is not { } node)
        {
            return;
        }

        if (e.OriginalSource is FontIcon { Glyph.Length: > 0 } icon && icon.Glyph == row.ExpanderGlyph)
        {
            Toggle(row);
            return;
        }

        Activate(node);
    }

    /// <summary>行を開く・閉じる。</summary>
    internal void Toggle(StructDiffRowViewModel row)
    {
        if (row.Node is not { } node || !row.Row.HasChildren)
        {
            return;
        }

        View.SetExpanded(node, !row.Row.IsExpanded);
        _rows.Reset(View.RowCount);
        Tree.SelectedIndex = View.RowOf(node);
        FocusSelected();
    }

    private void FocusSelected()
    {
        int index = Tree.SelectedIndex;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (index >= 0 && Tree.ContainerFromIndex(index) is ListViewItem item)
            {
                item.Focus(FocusState.Programmatic);
            }
        });
    }

    /// <summary>行を選んで左右の Hex ビューで範囲を選ぶ (Enter・ダブルクリック)。</summary>
    public void Activate(StructDiffNode node)
    {
        if (_session is { } s && Host is { } host)
        {
            host.SelectStructDiff(s, node);
        }
    }

    /// <summary>行を一覧で選ぶ (次 / 前の構造の差分の移動から)。</summary>
    public void SelectRow(StructDiffNode node)
    {
        _rows.Reset(View.RowCount);
        int row = View.RowOf(node);
        Tree.SelectedIndex = row;
        if (row >= 0)
        {
            Tree.ScrollIntoView(_rows[row]);
        }

        if (HasTreeFocus)
        {
            FocusSelected();
        }
    }

    private void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Tree.SelectedItem is StructDiffRowViewModel { Node: { } node })
        {
            Activate(node);
        }
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Tree.SelectedItem is not StructDiffRowViewModel row)
        {
            return;
        }

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Right when row.Row.HasChildren && !row.Row.IsExpanded:
            case VirtualKey.Left when row.Row.HasChildren && row.Row.IsExpanded:
                Toggle(row);
                e.Handled = true;
                break;
            case VirtualKey.Left when row.Node?.Parent is { Parent: not null } parent && View.RowOf(parent) is int p and >= 0:
                Tree.SelectedIndex = p;
                FocusSelected();
                e.Handled = true;
                break;
            case VirtualKey.Enter when row.Node is { } node:
                Activate(node);
                e.Handled = true;
                break;
            case VirtualKey.C when ctrl:
                Host?.CopyText(RowText(row));
                e.Handled = true;
                break;
        }
    }

    /// <summary>「行をコピー」(タブ区切り)。</summary>
    internal static string RowText(StructDiffRowViewModel row) => string.Join('\t',
        row.KindText, row.Node?.DisplayPath ?? row.Name, row.LeftValue, row.RightValue, row.TypeText, row.LeftOffset, row.RightOffset, row.SizeText, row.MatchText);

    private void Tree_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (Tree.SelectedItem is not StructDiffRowViewModel { Node: { } node } row || _session is not { } session || Host is not { } host)
        {
            return;
        }

        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "StructDiff_Menu");
        void Add(string id, string key, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = enabled };
            AutomationProperties.SetAutomationId(item, "StructDiffMenu_" + id);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("GoLeft", "StructDiffMenu_GoLeft", () => Activate(node), node.HasLeft || node.Kind == StructDiffKind.Added);
        Add("GoRight", "StructDiffMenu_GoRight", () => Activate(node), node.HasRight || node.Kind == StructDiffKind.Removed);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("CopyPath", "StructDiffMenu_CopyPath", () => host.CopyText(node.DisplayPath));
        Add("CopyRow", "StructDiffMenu_CopyRow", () => host.CopyText(RowText(row)));
        Add("CopyValues", "StructDiffMenu_CopyValues", () => host.CopyText(row.LeftValue + "\t" + row.RightValue));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("BookmarkLeft", "StructDiffMenu_BookmarkLeft", () => host.AddStructBookmark(session, node, right: false), node.HasLeft && session.Left.Owner is not null);
        Add("BookmarkRight", "StructDiffMenu_BookmarkRight", () => host.AddStructBookmark(session, node, right: true), node.HasRight && session.Right.Owner is not null);
        if (node.Array is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Matching", "StructDiffMenu_Matching", () => _ = host.ChangeArrayRuleAsync(session, node));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("TreeLeft", "StructDiffMenu_TreeLeft", () => _ = host.ShowInResultTreeAsync(session, node, right: false), node.HasLeft && session.Left.Owner is not null);
        Add("TreeRight", "StructDiffMenu_TreeRight", () => _ = host.ShowInResultTreeAsync(session, node, right: true), node.HasRight && session.Right.Owner is not null);
        _lastMenu = menu;
        if (args.TryGetPosition(Tree, out Windows.Foundation.Point point))
        {
            menu.ShowAt(Tree, point);
        }
        else
        {
            menu.ShowAt(Tree.ContainerFromItem(row) as FrameworkElement ?? Tree);
        }

        args.Handled = true;
    }

    private MenuFlyout? _lastMenu;

    /// <summary>最後に開いた右クリックメニュー (テスト用)。</summary>
    internal MenuFlyout? LastMenu => _lastMenu;

    // ---- ボタン ----

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s)
        {
            Host?.MoveStruct(s, next: false);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s)
        {
            Host?.MoveStruct(s, next: true);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s && Host is { } host)
        {
            await host.ExportStructDiffsAsync(s, View);
        }
    }

    private async void ChooseTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s && Host is { } host)
        {
            await host.ChooseStructTemplateAsync(s);
        }
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s)
        {
            Host?.EditStructTemplate(s);
        }
    }

    private async void RaiseLimit_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s)
        {
            await s.RaiseStructLimitAsync();
        }
    }

    private async void LeftDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s && Host is { } host)
        {
            await host.ShowStructDiagnosticsAsync(s, right: false);
        }
    }

    private async void RightDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } s && Host is { } host)
        {
            await host.ShowStructDiagnosticsAsync(s, right: true);
        }
    }

    private void SummaryToggle_Click(object sender, RoutedEventArgs e)
    {
        _summaryCollapsed = SummaryToggle.IsChecked != true;
        SummaryArea.Visibility = _summaryCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>幅が 800 px 未満のときは要約を折りたためる (ANA-32 の「画面」)。</summary>
    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 800;
        SummaryToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        SummaryArea.Visibility = narrow && _summaryCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>構造の差分のツリー表 (結果ツリーと同じ形: 行は TreeItem、開閉は ExpandCollapse パターン)。</summary>
public sealed partial class StructDiffListView : ListView
{
    internal StructDiffPanel? Panel { get; set; }

    protected override DependencyObject GetContainerForItemOverride() => new StructDiffTreeRow(this);

    protected override bool IsItemItsOwnContainerOverride(object item) => item is StructDiffTreeRow;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is StructDiffTreeRow row && item is StructDiffRowViewModel vm)
        {
            AutomationProperties.SetLevel(row, Math.Max(0, vm.Row.Depth) + 1);
            AutomationProperties.SetAutomationId(row, vm.AutomationId);
            AutomationProperties.SetName(row, vm.AccessibleName);
        }
    }
}

/// <summary>構造の差分の行の入れ物。</summary>
public sealed partial class StructDiffTreeRow(StructDiffListView owner) : ListViewItem
{
    internal StructDiffListView Owner { get; } = owner;

    protected override AutomationPeer OnCreateAutomationPeer() => new StructDiffTreeRowPeer(this);
}

/// <summary>行の UI オートメーション: 名前 (種類・パス・左右の値・左右のオフセット。仕様 9)、階層、開閉。</summary>
public sealed partial class StructDiffTreeRowPeer(StructDiffTreeRow row) : ListViewItemAutomationPeer(row), IExpandCollapseProvider
{
    private StructDiffRowViewModel? Vm => row.Content as StructDiffRowViewModel;

    protected override string GetNameCore()
    {
        if (Vm is not { } vm)
        {
            return base.GetNameCore();
        }

        return vm.Row.HasChildren ? vm.AccessibleName + ", " + Loc.Get(vm.Row.IsExpanded ? "StructDiff_Expanded" : "StructDiff_Collapsed") : vm.AccessibleName;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.ExpandCollapse && Vm is { Row.HasChildren: true } ? this : base.GetPatternCore(patternInterface);

    public ExpandCollapseState ExpandCollapseState => Vm is not { } vm || !vm.Row.HasChildren ? ExpandCollapseState.LeafNode
        : vm.Row.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    public void Expand()
    {
        if (Vm is { Row.IsExpanded: false } vm && row.Owner.Panel is { } panel)
        {
            panel.Toggle(vm);
        }
    }

    public void Collapse()
    {
        if (Vm is { Row.IsExpanded: true } vm && row.Owner.Panel is { } panel)
        {
            panel.Toggle(vm);
        }
    }
}
