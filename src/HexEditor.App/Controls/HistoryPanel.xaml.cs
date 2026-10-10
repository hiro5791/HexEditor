using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>履歴パネル (EDIT-20) の表示。処理は <see cref="HistoryPanelViewModel"/>。</summary>
public sealed partial class HistoryPanel : UserControl, Panels.IPanelContent
{
    public HistoryPanel(HistoryPanelViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        foreach ((Button button, string key) in new[] { (UndoButton, "History_Undo"), (RedoButton, "History_Redo"), (CompareButton, "History_Compare") })
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Services.Loc.Get(key));
            ToolTipService.SetToolTip(button, Services.Loc.Get(key));
        }

        vm.Refreshed += Vm_Refreshed;
        Unloaded += (_, _) => vm.Refreshed -= Vm_Refreshed;
    }

    public HistoryPanelViewModel Vm { get; }

    bool Panels.IPanelContent.FocusContent() => List.Focus(FocusState.Keyboard);

    /// <summary>一覧を作り直したら、現在の状態の行を見える位置に出す。</summary>
    private void Vm_Refreshed(object? sender, EventArgs e)
    {
        int row = Vm.CurrentIndex;
        if (row >= 0 && row < Vm.Rows.Count)
        {
            List.ScrollIntoView(Vm.Rows[row]);
        }
    }

    private int? SelectedIndex() => List.SelectedItem is HistoryRow row ? row.Index : null;

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
}
