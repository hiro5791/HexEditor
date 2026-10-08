using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// ウィンドウの領域 (UI-01): 最小サイズ、文書がないときのスタートページ、F6 / Shift+F6 による領域の移動 (UI-52 の仕様 1)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ウィンドウの最小サイズ (表示倍率 100% 換算。UI-01 の仕様 4)。</summary>
    public const int MinWidth = 640;
    public const int MinHeight = 400;

    private void InitializeRegions()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            double scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
            presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        }

        Vm.Documents.CollectionChanged += (_, _) => UpdateStartPage();
        Tabs.SizeChanged += (_, _) => PlaceStartPage();
        Tabs.Loaded += (_, _) => PlaceStartPage();
        UpdateStartPage();

        // F6 / Shift+F6 はグローバルのコマンド (view.nextRegion / view.previousRegion)。キーの振り分けは KeyDispatcher。
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>
    /// 文書が 1 つもないときはスタートページを出す (UI-01 の仕様 2)。タブ列 (仕様 1 の 3) は文書がなくても表示したままにし、
    /// スタートページはタブ列の下 (エディタ領域) に重ねる。
    /// </summary>
    private void UpdateStartPage()
    {
        bool empty = Vm.Documents.Count == 0;
        StartPage.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PlaceStartPage();
    }

    /// <summary>スタートページの上端をタブ列の下端に合わせる。</summary>
    private void PlaceStartPage()
    {
        if (FindByName(Tabs, "TabContainerGrid") is FrameworkElement strip && strip.ActualHeight > 0)
        {
            StartPage.Margin = new Thickness(0, strip.ActualHeight, 0, 0);
        }
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } fe && n == name)
            {
                return fe;
            }

            if (FindByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private enum Region
    {
        Tabs,
        LeftPanel,
        Editor,
        RightPanel,
        BottomPanel,
        StatusBar,
    }

    /// <summary>タブ列 → 左パネル → エディタ → 右パネル → 下パネル → ステータスバー の順にフォーカスを移す (表示されている領域だけ)。</summary>
    private void MoveToRegion(bool forward)
    {
        var regions = new List<Region>();
        if (Tabs.Visibility == Visibility.Visible && Vm.Documents.Count > 0)
        {
            regions.Add(Region.Tabs);
        }

        if (LeftPanel.Visibility == Visibility.Visible)
        {
            regions.Add(Region.LeftPanel);
        }

        regions.Add(Region.Editor);
        if (RightPanel.Visibility == Visibility.Visible)
        {
            regions.Add(Region.RightPanel);
        }

        if (BottomPanel.Visibility == Visibility.Visible)
        {
            regions.Add(Region.BottomPanel);
        }

        if (StatusBar.Visibility == Visibility.Visible)
        {
            regions.Add(Region.StatusBar);
        }

        Region? current = CurrentRegion();
        int index = current is { } c ? regions.IndexOf(c) : -1;
        int target = index < 0 ? (forward ? 0 : regions.Count - 1)
            : (index + (forward ? 1 : regions.Count - 1)) % regions.Count;
        Focus(regions[target]);
    }

    private Region? CurrentRegion()
    {
        if (Root.XamlRoot is null || FocusManager.GetFocusedElement(Root.XamlRoot) is not DependencyObject focused)
        {
            return null;
        }

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == StatusBar)
            {
                return Region.StatusBar;
            }

            if (node == LeftPanel)
            {
                return Region.LeftPanel;
            }

            if (node == RightPanel)
            {
                return Region.RightPanel;
            }

            if (node == BottomPanel)
            {
                return Region.BottomPanel;
            }

            if (node is Controls.HexView || node == StartPage)
            {
                return Region.Editor;
            }

            if (node is TabViewItem)
            {
                return Region.Tabs;
            }
        }

        return null;
    }

    private void Focus(Region region)
    {
        switch (region)
        {
            case Region.Tabs:
                if (Tabs.ContainerFromItem(Vm.Selected) is TabViewItem item)
                {
                    item.Focus(FocusState.Keyboard);
                }

                break;
            case Region.Editor:
                if (Vm.Documents.Count == 0)
                {
                    // スタートページの最初のボタン (「開く」) にフォーカスを置く。
                    if (FocusManager.FindFirstFocusableElement(StartPage) is Control first)
                    {
                        first.Focus(FocusState.Keyboard);
                    }
                }
                else
                {
                    FocusEditor();
                }

                break;
            case Region.LeftPanel:
                LeftPanel.FocusHeader();
                break;
            case Region.RightPanel:
                RightPanel.FocusHeader();
                break;
            case Region.BottomPanel:
                BottomPanel.FocusHeader();
                break;
            case Region.StatusBar:
                StatusButtons.FirstOrDefault(b => b.Visibility == Visibility.Visible)?.Focus(FocusState.Keyboard);
                break;
        }
    }
}
