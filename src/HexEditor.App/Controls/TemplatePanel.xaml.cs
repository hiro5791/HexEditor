using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Templates.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>結果ツリーパネルが頼む操作 (ウィンドウが実装する。Hex ビュー・クリップボード・ブックマークとつなぐ)。</summary>
public interface ITemplatePanelHost
{
    Task ApplyTemplateAsync();

    void RerunTemplates();

    void StopTemplateEvaluation();

    Task ExportTemplateResultAsync(TemplateTreeItem? subtree);

    /// <summary>Hex ビューでノードの範囲を選ぶ (<paramref name="focusEditor"/> なら Hex ビューにフォーカスを移す。Enter)。</summary>
    void GoToTemplateData(TemplateTreeItem item, bool focusEditor);

    Task EditTemplateValueAsync(TemplateTreeItem item, FrameworkElement anchor);

    void CopyTemplateValues(IReadOnlyList<TemplateTreeItem> items);

    void ShowTemplateOutput();

    /// <summary>右クリックメニューの項目を作る (TPL-24 の仕様 1)。</summary>
    void FillTemplateMenu(MenuFlyout menu, IReadOnlyList<TemplateTreeItem> items);

    /// <summary>テンプレートの値の検索 (04 の FIND-29)。結果は共通の結果一覧に出す。誤り (構文エラーの位置など) の文を返す。</summary>
    Task<string?> FindTemplateValuesAsync(string query, bool evaluateAll);
}

/// <summary>
/// 結果ツリーパネル (TPL-21)。ツリー表の行は <see cref="TemplateRowList"/> (仮想化) で、開閉・移動・編集・右クリックメニューの操作は
/// <see cref="ITemplatePanelHost"/> に頼む。
/// </summary>
public sealed partial class TemplatePanel : UserControl
{
    private CancellationTokenSource? _filterCts;
    private bool _syncingSelection;

    public TemplatePanel(TemplatePanelViewModel vm, ITemplatePanelHost host)
    {
        Vm = vm;
        Host = host;
        InitializeComponent();
        vm.Rows.Scope = this;
        Tree.Panel = this;
        Tree.ItemsSource = vm.Rows;
        SearchAllButton.Content = Loc.Get("Tpl_SearchAll");
        vm.SelectRequested += Vm_SelectRequested;
        vm.Layout.PropertyChanged += (_, _) => BuildHeader();
        Unloaded += (_, _) => vm.SelectRequested -= Vm_SelectRequested;
        Tree.ContextFlyout = CreateContextMenu();
        BuildHeader();
    }

    public TemplatePanelViewModel Vm { get; }

    public ITemplatePanelHost Host { get; }

    /// <summary>ツリー表 (テスト用の命令とフォーカスの移動)。</summary>
    internal TemplateTreeListView TreeList => Tree;

    internal TextBox PathInput => PathBox;

    internal TextBox FilterInput => FilterBox;

    /// <summary>選んでいる行 (複数)。</summary>
    internal IReadOnlyList<TemplateTreeItem> SelectedItems =>
        [.. Tree.SelectedItems.OfType<TemplateRowViewModel>().Select(r => r.Item)];

    // ───────────── 見出し (列の幅を変える) ─────────────

    private void BuildHeader()
    {
        HeaderRow.Children.Clear();
        HeaderRow.ColumnDefinitions.Clear();
        TemplateColumnLayout layout = Vm.Layout;
        for (int i = 0; i < layout.Order.Count; i++)
        {
            TemplateColumn c = layout.Order[i];
            bool visible = layout.IsVisible(c);
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = visible ? new GridLength(layout.WidthOf(c)) : new GridLength(0) });
            if (!visible)
            {
                continue;
            }

            var cell = new Grid();
            cell.Children.Add(new TextBlock
            {
                Text = Loc.Get("Tpl_Column_" + c),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var thumb = new Thumb { Width = 6, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0, IsTabStop = false };
            TemplateColumn column = c;
            double start = 0;
            thumb.DragStarted += (_, _) => start = layout.WidthOf(column);
            thumb.DragDelta += (_, e) =>
            {
                start += e.HorizontalChange;
                HeaderRow.ColumnDefinitions[layout.Order.ToList().IndexOf(column)].Width = new GridLength(Math.Max(24, start));
            };
            thumb.DragCompleted += (_, _) => layout.SetWidth(column, start);
            cell.Children.Add(thumb);
            Grid.SetColumn(cell, i);
            HeaderRow.Children.Add(cell);
        }
    }

    private void ColumnsMenu_Opening(object sender, object e)
    {
        ColumnsMenu.Items.Clear();
        TemplateColumnLayout layout = Vm.Layout;
        foreach (TemplateColumn c in layout.Order)
        {
            var item = new ToggleMenuFlyoutItem { Text = Loc.Get("Tpl_Column_" + c), IsChecked = layout.IsVisible(c), IsEnabled = c != TemplateColumn.Name };
            AutomationProperties.SetAutomationId(item, "Tpl_Column_" + c);
            TemplateColumn column = c;
            item.Click += (_, _) => layout.SetVisible(column, !layout.IsVisible(column));
            ColumnsMenu.Items.Add(item);
        }

        ColumnsMenu.Items.Add(new MenuFlyoutSeparator());
        var order = new MenuFlyoutSubItem { Text = Loc.Get("Tpl_ColumnOrder") };
        foreach (TemplateColumn c in layout.Order.Where(layout.IsVisible))
        {
            TemplateColumn column = c;
            var left = new MenuFlyoutItem { Text = Loc.Format("Tpl_MoveColumnLeft", Loc.Get("Tpl_Column_" + c)) };
            left.Click += (_, _) => layout.Move(column, -1);
            var right = new MenuFlyoutItem { Text = Loc.Format("Tpl_MoveColumnRight", Loc.Get("Tpl_Column_" + c)) };
            right.Click += (_, _) => layout.Move(column, 1);
            order.Items.Add(left);
            order.Items.Add(right);
        }

        ColumnsMenu.Items.Add(order);
        var reset = new MenuFlyoutItem { Text = Loc.Get("Tpl_ResetColumns") };
        reset.Click += (_, _) => layout.Reset();
        ColumnsMenu.Items.Add(reset);
    }

    // ───────────── ツールバー ─────────────

    private async void Apply_Click(object sender, RoutedEventArgs e) => await Host.ApplyTemplateAsync();

    private void Rerun_Click(object sender, RoutedEventArgs e) => Host.RerunTemplates();

    private void Stop_Click(object sender, RoutedEventArgs e) => Host.StopTemplateEvaluation();

    private async void Export_Click(object sender, RoutedEventArgs e) => await Host.ExportTemplateResultAsync(null);

    private void Counts_Click(object sender, RoutedEventArgs e) => Host.ShowTemplateOutput();

    private async void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsValueQuery(FilterBox.Text))
        {
            // 値の検索の条件 (FIND-29) は入力中に絞り込まない (Enter で検索する)
            if (Vm.FilterActive)
            {
                await Vm.FilterAsync(string.Empty, evaluateAll: false);
            }

            MessageLine.Text = Loc.Get("Tpl_ValueSearchHint");
            return;
        }

        await FilterAsync(evaluateAll: false);
    }

    private async void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (IsValueQuery(FilterBox.Text))
            {
                await SearchValuesAsync(evaluateAll: false);
            }
            else
            {
                await FilterAsync(evaluateAll: false);
            }
        }
        else if (e.Key == VirtualKey.Escape && FilterBox.Text.Length > 0)
        {
            e.Handled = true;
            FilterBox.Text = string.Empty;
        }
    }

    private async void SearchAll_Click(object sender, RoutedEventArgs e)
    {
        if (IsValueQuery(FilterBox.Text))
        {
            await SearchValuesAsync(evaluateAll: true);
        }
        else
        {
            await FilterAsync(evaluateAll: true);
        }
    }

    /// <summary>
    /// 検索欄の文字列がテンプレートの値の検索の条件か (<c>name:</c>・<c>type:</c>・<c>value</c> と比較の演算子。FIND-29 の仕様 1)。
    /// それ以外は結果ツリーの絞り込み (TPL-21 の仕様 5)。
    /// </summary>
    internal static bool IsValueQuery(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\s)(name:|type:|value\s*(!=|<=|>=|=|<|>|~))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>テンプレートの値の検索 (FIND-29): 結果は共通の結果一覧。構文エラーは位置を検索欄の下に出す。</summary>
    internal async Task SearchValuesAsync(bool evaluateAll)
    {
        MessageLine.Text = string.Empty;
        string? error = await Host.FindTemplateValuesAsync(FilterBox.Text, evaluateAll);
        MessageLine.Text = error ?? string.Empty;
    }

    /// <summary>検索欄にフォーカスを移す (コマンド「テンプレートの値を検索」)。</summary>
    internal void FocusSearchBox() => FilterBox.Focus(FocusState.Programmatic);

    /// <summary>テスト用: 検索欄に入力して Enter を押したのと同じ。</summary>
    internal async Task SubmitSearchForTestAsync(string text, bool evaluateAll)
    {
        FilterBox.Text = text;
        if (IsValueQuery(text))
        {
            await SearchValuesAsync(evaluateAll);
        }
        else
        {
            await FilterAsync(evaluateAll);
        }
    }

    /// <summary>絞り込み (評価済みのノード。「すべて評価して検索」は未評価の部分も評価する。TPL-21 の仕様 5)。</summary>
    internal async Task FilterAsync(bool evaluateAll)
    {
        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;
        try
        {
            int n = await Vm.FilterAsync(FilterBox.Text, evaluateAll, cts.Token);
            MessageLine.Text = FilterBox.Text.Trim().Length == 0 ? string.Empty : Loc.Format("Tpl_FilterCount", n);
        }
        catch (OperationCanceledException)
        {
            // 新しい入力で取り消した
        }
    }

    // ───────────── パス表示 ─────────────

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await GoToPathAsync(PathBox.Text);
    }

    /// <summary>パスを入力して移動する (TPL-21 の仕様 6)。見つからなければその旨を表示し、選択は変えない。</summary>
    internal async Task<bool> GoToPathAsync(string path)
    {
        bool found = await Vm.GoToPathAsync(path);
        MessageLine.Text = found ? string.Empty : Loc.Format("Tpl_PathNotFound", path);
        return found;
    }

    internal string Message => MessageLine.Text;

    // ───────────── ツリー表 ─────────────

    private void Vm_SelectRequested(object? sender, TemplateTreeItem item)
    {
        int index = Vm.Model.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            TemplateRowViewModel row = Vm.Rows.Row(item);
            Tree.SelectedItems.Clear();
            Tree.SelectedIndex = index;
            Tree.ScrollIntoView(row);
            Vm.SelectedItem = item;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TemplateTreeItem? item = (Tree.SelectedItem as TemplateRowViewModel)?.Item;
        Vm.SelectedItem = item;
        if (!_syncingSelection && item is not null)
        {
            // ノードを選ぶと Hex ビューでその範囲を選んで表示する (TPL-23 の仕様 6)
            Host.GoToTemplateData(item, focusEditor: false);
        }
    }

    private async void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Tree.SelectedItem is not TemplateRowViewModel row)
        {
            return;
        }

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        e.Handled = await HandleKeyAsync(e.Key, ctrl, row.Item);
    }

    /// <summary>ツリーのキー (TPL-21 の仕様 7)。テスト用の命令からも使う。処理したら true。</summary>
    internal async Task<bool> HandleKeyAsync(VirtualKey key, bool ctrl, TemplateTreeItem item)
    {
        switch (key)
        {
            case VirtualKey.Right:
                if (item.HasChildren && !item.IsExpanded)
                {
                    await Vm.Model.ExpandAsync(item);
                    Vm_SelectRequested(this, item);
                }
                else if (item.IsExpanded && item.MaterializedChildren is { Count: > 0 } children)
                {
                    Vm_SelectRequested(this, children[0]);
                }

                return true;
            case VirtualKey.Left:
                if (item.IsExpanded)
                {
                    Vm.Model.Collapse(item);
                    Vm_SelectRequested(this, item);
                }
                else if (item.Parent is { } parent)
                {
                    Vm_SelectRequested(this, parent);
                }

                return true;
            case VirtualKey.Enter:
                Host.GoToTemplateData(item, focusEditor: true);
                return true;
            case VirtualKey.C when ctrl:
                Host.CopyTemplateValues(SelectedItems.Count > 0 ? SelectedItems : [item]);
                return true;
        }

        return false;
    }

    private async void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TemplateRowViewModel row)
        {
            e.Handled = true;
            await EditAsync(row.Item);
        }
    }

    /// <summary>値の編集を始める (ダブルクリック、「値を編集」。TPL-22)。</summary>
    internal async Task EditAsync(TemplateTreeItem item)
    {
        FrameworkElement anchor = Tree.ContainerFromItem(Vm.Rows.Row(item)) as FrameworkElement ?? Tree;
        await Host.EditTemplateValueAsync(item, anchor);
    }

    internal async Task ToggleAsync(TemplateTreeItem item) => await Vm.ToggleAsync(item);

    private MenuFlyout CreateContextMenu()
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "Tpl_ContextMenu");
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            IReadOnlyList<TemplateTreeItem> items = SelectedItems;
            Host.FillTemplateMenu(menu, items);
        };
        return menu;
    }

    /// <summary>右クリックメニューを作って返す (テスト用の命令)。</summary>
    internal MenuFlyout BuildMenu(IReadOnlyList<TemplateTreeItem> items)
    {
        var menu = new MenuFlyout();
        Host.FillTemplateMenu(menu, items);
        return menu;
    }

    /// <summary>ツリーにフォーカスを移す (F6)。</summary>
    public void FocusTree()
    {
        if (Tree.SelectedIndex < 0 && Vm.Rows.Count > 0)
        {
            Tree.SelectedIndex = 0;
        }

        Tree.Focus(FocusState.Keyboard);
    }
}

/// <summary>結果ツリーの一覧 (行の UI オートメーションに開閉と階層を付ける。TPL-21 の仕様 8)。</summary>
public sealed partial class TemplateTreeListView : ListView
{
    internal TemplatePanel? Panel { get; set; }

    protected override DependencyObject GetContainerForItemOverride() => new TemplateTreeRow(this);

    protected override bool IsItemItsOwnContainerOverride(object item) => item is TemplateTreeRow;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is TemplateTreeRow row && item is TemplateRowViewModel vm)
        {
            AutomationProperties.SetLevel(row, vm.Item.Depth + 1);
            AutomationProperties.SetAutomationId(row, "Tpl_Row_" + (vm.Item.Kind == TemplateTreeItemKind.Node ? vm.Item.Node!.Path : vm.Item.Key));
        }
    }
}

/// <summary>結果ツリーの行の入れ物。</summary>
public sealed partial class TemplateTreeRow(TemplateTreeListView owner) : ListViewItem
{
    internal TemplateTreeListView Owner { get; } = owner;

    protected override AutomationPeer OnCreateAutomationPeer() => new TemplateTreeRowPeer(this);
}

/// <summary>行の UI オートメーション: 名前 (名前・型・値・オフセット・サイズ)、階層、開閉 (ExpandCollapse パターン)。</summary>
public sealed partial class TemplateTreeRowPeer(TemplateTreeRow row) : ListViewItemAutomationPeer(row), IExpandCollapseProvider
{
    private TemplateRowViewModel? Vm => row.Content as TemplateRowViewModel;

    protected override string GetNameCore() => Vm?.AccessibleName ?? base.GetNameCore();

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.ExpandCollapse && Vm is { Item.HasChildren: true } ? this : base.GetPatternCore(patternInterface);

    public ExpandCollapseState ExpandCollapseState => Vm is not { } vm || !vm.Item.HasChildren ? ExpandCollapseState.LeafNode
        : vm.Item.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

    public void Expand()
    {
        if (Vm is { Item: { IsExpanded: false } item } && row.Owner.Panel is { } panel)
        {
            _ = panel.ToggleAsync(item);
        }
    }

    public void Collapse()
    {
        if (Vm is { Item: { IsExpanded: true } item } && row.Owner.Panel is { } panel)
        {
            _ = panel.ToggleAsync(item);
        }
    }
}
