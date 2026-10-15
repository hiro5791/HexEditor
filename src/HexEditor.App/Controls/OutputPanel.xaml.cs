using System.Text;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>出力パネル (AUTO-24) の表示。処理は <see cref="OutputPanelViewModel"/>。</summary>
public sealed partial class OutputPanel : UserControl, Panels.IPanelContent
{
    private readonly IScriptPanelHost _host;

    public OutputPanel(OutputPanelViewModel vm, IScriptPanelHost host)
    {
        Vm = vm;
        _host = host;
        InitializeComponent();
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (ClearButton, "Output_Clear"), (CopyButton, "Output_CopyAll"), (SaveButton, "Output_Save"), (WrapButton, "Output_Wrap"),
            (ChannelBox, "Output_ChannelLabel"), (FilterBox, "Output_FilterLabel"),
        })
        {
            AutomationProperties.SetName(control, Loc.Get(key));
            ToolTipService.SetToolTip(control, Loc.Get(key));
        }

        FilterBox.PlaceholderText = Loc.Get("Output_FilterLabel");
        ChannelBox.SelectedIndex = 0;
        vm.Updated += Vm_Updated;
        Loaded += (_, _) => ScrollToEnd();
        Unloaded += (_, _) => vm.Updated -= Vm_Updated;
    }

    public OutputPanelViewModel Vm { get; }

    bool Panels.IPanelContent.FocusContent() => List.Focus(FocusState.Keyboard);

    /// <summary>末尾を見ていたら、新しい行に合わせて末尾へ動く (上へスクロールして読んでいるときは動かさない)。</summary>
    private void Vm_Updated(object? sender, int added)
    {
        if (Vm.Rows.Count == 0)
        {
            return;
        }

        if (added == 0 || IsAtEnd())
        {
            ScrollToEnd();
        }
    }

    private bool IsAtEnd()
    {
        ScrollViewer? viewer = FindScrollViewer(List);
        return viewer is null || viewer.ScrollableHeight - viewer.VerticalOffset < 40;
    }

    private void ScrollToEnd()
    {
        if (Vm.Rows.Count > 0)
        {
            List.ScrollIntoView(Vm.Rows[^1]);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void Channel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelBox.SelectedItem is OutputPanelViewModel.ChannelItem item)
        {
            Vm.SelectedChannel = item.Channel;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Vm.Clear();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        _host.SetClipboardText(Vm.AllText());
        _host.ShowStatus(Loc.Get("Output_Copied"));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _host.PickSavePathAsync("output.txt", ".txt") is { } path)
        {
            await File.WriteAllTextAsync(path, Vm.AllText(), new UTF8Encoding(true));
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OutputRow { HasLink: true } row)
        {
            _host.OpenScriptEditor(row.Line.SourcePath!, row.Line.Line, Math.Max(1, row.Line.Column));
        }
    }
}
