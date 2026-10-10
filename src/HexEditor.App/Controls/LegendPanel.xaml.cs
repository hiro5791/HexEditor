using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>凡例のパネル (INSP-34 の仕様 3)。移動と全体の件数はウィンドウが行う (イベント)。</summary>
public sealed partial class LegendPanel : UserControl, Panels.IPanelContent
{
    public LegendPanel(LegendViewModel vm)
    {
        Vm = vm;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Legend_PanelName"));
        AutomationProperties.SetName(List, Loc.Get("Legend_PanelName"));
    }

    public LegendViewModel Vm { get; }

    /// <summary>「次へ」「前へ」(true なら次)。</summary>
    public event EventHandler<(LegendItem Item, bool Forward)>? NavigateRequested;

    /// <summary>「全体の件数を数える」(長時間処理)。</summary>
    public event EventHandler? CountAllRequested;

    bool Panels.IPanelContent.FocusContent() => Vm.Items.Count > 0 ? List.Focus(FocusState.Keyboard) : CountAllButton.Focus(FocusState.Keyboard);

    private void List_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is LegendItem item && args.ItemContainer is ListViewItem container)
        {
            AutomationProperties.SetName(container, item.AutomationName);
            AutomationProperties.SetAutomationId(container, item.AutomationId);
        }
    }

    private void NavButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LegendItem item } button)
        {
            bool next = AutomationProperties.GetAutomationId(button) == "Legend_Next";
            string name = Loc.Format(next ? "Legend_NextName" : "Legend_PreviousName", item.Name);
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LegendItem item })
        {
            NavigateRequested?.Invoke(this, (item, true));
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LegendItem item })
        {
            NavigateRequested?.Invoke(this, (item, false));
        }
    }

    private void CountAll_Click(object sender, RoutedEventArgs e) => CountAllRequested?.Invoke(this, EventArgs.Empty);
}
