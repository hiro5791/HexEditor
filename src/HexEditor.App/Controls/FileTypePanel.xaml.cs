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

    /// <summary>「埋め込まれた形式を探す」の対象範囲 (選択範囲があれば選択範囲、なければ全体)。</summary>
    public Func<IReadOnlyList<HashRange>>? EmbeddedRanges { get; set; }

    public bool FocusContent() => CandidateList.Focus(FocusState.Keyboard);

    private void Detect_Click(object sender, RoutedEventArgs e) => _ = ViewModel.DetectAsync();

    private void DetectHere_Click(object sender, RoutedEventArgs e) => _ = ViewModel.DetectHereAsync();

    private void FindEmbedded_Click(object sender, RoutedEventArgs e) => _ = ViewModel.FindEmbeddedAsync(EmbeddedRanges?.Invoke() ?? []);

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
