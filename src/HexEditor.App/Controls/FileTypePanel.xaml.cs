using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Hashing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HexEditor.App.Controls;

/// <summary>「ファイル形式」パネル (ANA-17) の表示。状態と処理は <see cref="FileTypeViewModel"/>。</summary>
public sealed partial class FileTypePanel : UserControl, IPanelContent
{
    public FileTypePanel(FileTypeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AutomationProperties.SetName(this, Loc.Get("Panel_FileType_Title"));
    }

    public FileTypeViewModel ViewModel { get; }

    public bool FocusContent() => CandidateList.Focus(FocusState.Keyboard);

    private void Detect_Click(object sender, RoutedEventArgs e) => _ = ViewModel.DetectAsync();

    private void DetectHere_Click(object sender, RoutedEventArgs e) => _ = ViewModel.DetectHereAsync();

    private void FindEmbedded_Click(object sender, RoutedEventArgs e) => _ = ViewModel.FindEmbeddedAsync();

    /// <summary>対象範囲の選択欄を利用者が選んだ (以後、選択範囲の有無で既定を切り替えない。06 の 0.1)。</summary>
    private void EmbeddedTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EmbeddedTarget.SelectedIndex >= 0 && EmbeddedTarget.SelectedIndex != ViewModel.EmbeddedTargetIndex)
        {
            ViewModel.ChooseEmbeddedTarget((Core.Statistics.AnalysisTargetKind)EmbeddedTarget.SelectedIndex);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void Match_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: long offset })
        {
            ViewModel.GoTo?.Invoke(offset);
        }
    }

    private void EmbeddedList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => GoToSelected();

    private void EmbeddedList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            GoToSelected();
            e.Handled = true;
        }
    }

    private void GoToSelected()
    {
        if (EmbeddedList.SelectedItem is EmbeddedFormatViewModel row)
        {
            ViewModel.GoTo?.Invoke(row.Format.Offset);
        }
    }
}
