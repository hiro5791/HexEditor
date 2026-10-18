using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>履歴パネル (EDIT-20、EDIT-21) の表示。処理は <see cref="HistoryPanelViewModel"/>、ダイアログを伴う操作は <see cref="IHistoryPanelHost"/>。</summary>
public sealed partial class HistoryPanel : UserControl, Panels.IPanelContent
{
    public HistoryPanel(HistoryPanelViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (UndoButton, "History_Undo"), (RedoButton, "History_Redo"), (CompareButton, "History_Compare"), (TreeToggle, "History_TreeView"),
            (SaveHistoryButton, "History_SaveHistory"), (LoadHistoryButton, "History_LoadHistory"),
        })
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, Services.Loc.Get(key));
            ToolTipService.SetToolTip(control, Services.Loc.Get(key));
        }

        vm.Refreshed += Vm_Refreshed;
        Unloaded += (_, _) => vm.Refreshed -= Vm_Refreshed;
    }

    public HistoryPanelViewModel Vm { get; }

    bool Panels.IPanelContent.FocusContent() => (Vm.IsTreeView ? Tree : List).Focus(FocusState.Keyboard);

    /// <summary>一覧を作り直したら、現在の状態の行を見える位置に出す。</summary>
    private void Vm_Refreshed(object? sender, EventArgs e)
    {
        if (Vm.IsTreeView)
        {
            if (Vm.TreeRows.FirstOrDefault(r => r.IsCurrent) is { } current)
            {
                Tree.ScrollIntoView(current);
            }

            return;
        }

        int row = Vm.CurrentIndex;
        if (row >= 0 && row < Vm.Rows.Count)
        {
            List.ScrollIntoView(Vm.Rows[row]);
        }
    }

    private int? SelectedIndex() => List.SelectedItem is HistoryRow row ? row.Index : null;

    private HistoryNode? SelectedNode() => Tree.SelectedItem is HistoryTreeRow row ? row.Node : null;

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryRow row)
        {
            Vm.GoTo(row.Index);
        }
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SelectedIndex() is int index)
        {
            Vm.GoTo(index);
            e.Handled = true;
        }
    }

    private void GoToState_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex() is int index)
        {
            Vm.GoTo(index);
        }
    }

    private void SelectRange_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIndex() is int index)
        {
            Vm.SelectRangeOf(index);
        }
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        // 2 行を選んで比較する (EDIT-20 の仕様 5)。1 行だけなら、その行と現在の状態を比べる。
        var rows = List.SelectedItems.OfType<HistoryRow>().Select(r => r.Index).ToList();
        if (rows.Count == 1)
        {
            rows.Add(Vm.CurrentIndex);
        }

        if (rows.Count >= 2)
        {
            await Vm.CompareAsync(rows[0], rows[1]);
        }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Vm.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Vm.Redo();

    // ---- 分岐する履歴 (EDIT-21) ----

    private void TreeToggle_Click(object sender, RoutedEventArgs e) => Vm.IsTreeView = TreeToggle.IsChecked == true;

    private async void SaveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Host is { } host)
        {
            await host.SaveHistoryAsync();
        }
    }

    private async void LoadHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Host is { } host)
        {
            await host.LoadHistoryAsync();
        }
    }

    /// <summary>「この 2 時点からパッチを作成」(一覧): 2 行を選べばその 2 時点、1 行ならその行と現在の状態。</summary>
    private async void CreatePatch_Click(object sender, RoutedEventArgs e)
    {
        var rows = List.SelectedItems.OfType<HistoryRow>().Select(r => r.Index).OrderBy(i => i).ToList();
        if (rows.Count == 1)
        {
            rows.Add(Vm.CurrentIndex);
        }

        if (rows.Count >= 2 && Vm.Host is { } host && Vm.History is { } history)
        {
            await host.CreatePatchAsync(history.NodeAt(Math.Min(rows[0], rows[1])), history.NodeAt(Math.Max(rows[0], rows[1])));
        }
    }

    private void Tree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryTreeRow row)
        {
            Vm.GoToNode(row.Node);
        }
    }

    private void Tree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SelectedNode() is { } node)
        {
            Vm.GoToNode(node);
            e.Handled = true;
        }
    }

    private void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        TreeDetails.Text = SelectedNode() is { } node ? Vm.DetailsOf(node) : string.Empty;

    private void TreeMenu_Opening(object? sender, object e)
    {
        HistoryNode? node = SelectedNode();
        PinItem.Text = Services.Loc.Get(node?.IsPinned == true ? "History_Unpin" : "History_Pin");
        DeleteItem.IsEnabled = node is not null && Vm.History?.CanDelete(node) == true;
    }

    private void TreeGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode() is { } node)
        {
            Vm.GoToNode(node);
        }
    }

    private async void NameNode_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode() is { } node && Vm.Host is { } host)
        {
            await host.NameNodeAsync(node);
        }
    }

    private void PinNode_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode() is { } node && Vm.History is { } history)
        {
            history.SetPinned(node, !node.IsPinned);
        }
    }

    private async void DeleteBranch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode() is { } node && Vm.Host is { } host)
        {
            await host.DeleteBranchAsync(node);
        }
    }

    /// <summary>「この 2 時点からパッチを作成」(木): 2 つの節を選べばその 2 つ、1 つならその節と現在の節。</summary>
    private async void TreeCreatePatch_Click(object sender, RoutedEventArgs e)
    {
        var nodes = Tree.SelectedItems.OfType<HistoryTreeRow>().Select(r => r.Node).OrderBy(n => n.Id).ToList();
        if (nodes.Count == 1 && Vm.History is { } history && !ReferenceEquals(nodes[0], history.CurrentNode))
        {
            nodes.Add(history.CurrentNode);
            nodes = [.. nodes.OrderBy(n => n.Id)];
        }

        if (nodes.Count >= 2 && Vm.Host is { } host)
        {
            await host.CreatePatchAsync(nodes[0], nodes[1]);
        }
    }
}

/// <summary>履歴パネルからのダイアログを伴う操作 (ウィンドウが実装する)。</summary>
public interface IHistoryPanelHost
{
    Task SaveHistoryAsync();

    Task LoadHistoryAsync();

    Task NameNodeAsync(HistoryNode node);

    Task DeleteBranchAsync(HistoryNode node);

    Task CreatePatchAsync(HistoryNode from, HistoryNode to);
}
