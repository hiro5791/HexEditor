using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// 狭い幅での左右のパネルの折りたたみ (UI-01 の仕様 4): 幅 1024 px 未満では左右のパネルを折りたたみ、パネルの見出しだけを縦に並べた
/// 帯にする。帯の見出しを押すと、そのパネルをエディタの上に重ねて開く (エディタの幅は変わらない)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>この幅 (epx) 未満で左右のパネルを折りたたむ。</summary>
    public const double PanelCollapseWidth = 1024;

    private StackPanel? _leftStrip;
    private StackPanel? _rightStrip;
    private PanelDock? _overlayDock;
    private bool _panelsCollapsed;

    /// <summary>左右のパネルを折りたたんでいるか。</summary>
    internal bool PanelsCollapsed => _panelsCollapsed;

    private void InitializePanelCollapse()
    {
        _leftStrip = CreateStrip("PanelStrip_Left", 0);
        _rightStrip = CreateStrip("PanelStrip_Right", 4);
        Root.SizeChanged += (_, _) =>
        {
            if (WantsCollapse() != _panelsCollapsed)
            {
                ApplyPanelLayout();
            }
        };

        // 重ねて開いたパネルは、エディタなどパネルの外にフォーカスが移ったら閉じる。
        Root.GotFocus += (_, e) =>
        {
            if (_overlayDock is { } dock && e.OriginalSource is DependencyObject focused && !IsWithinOverlay(focused, dock))
            {
                _overlayDock = null;
                ApplyPanelLayout();
            }
        };
    }

    private StackPanel CreateStrip(string automationId, int column)
    {
        var strip = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(2, 4, 2, 4),
            Visibility = Visibility.Collapsed,
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            TabFocusNavigation = KeyboardNavigationMode.Once,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
        };
        AutomationProperties.SetAutomationId(strip, automationId);
        Grid.SetColumn(strip, column);
        Grid.SetRowSpan(strip, 3);
        WorkArea.Children.Add(strip);
        return strip;
    }

    private bool WantsCollapse() => Root.ActualWidth > 0 && Root.ActualWidth < PanelCollapseWidth;

    private bool IsWithinOverlay(DependencyObject node, PanelDock dock)
    {
        UIElement area = dock == PanelDock.Left ? LeftPanel : RightPanel;
        StackPanel? strip = dock == PanelDock.Left ? _leftStrip : _rightStrip;
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, area) || ReferenceEquals(n, strip))
            {
                return true;
            }

            // ポップアップ (見出しのメニューなど) の中は、木をたどってもパネルに届かないので閉じない。
            if (n is Microsoft.UI.Xaml.Controls.Primitives.Popup)
            {
                return true;
            }
        }

        return VisualTreeHelper.GetParent(node) is null;
    }

    /// <summary>
    /// 折りたたみを反映する (ApplyPanelLayout の最後に呼ぶ)。折りたたむときは、左右のパネルの領域を隠して見出しの帯を出し、
    /// 重ねて開いているパネルだけをエディタの列の上に置く。
    /// </summary>
    private void ApplyPanelCollapse()
    {
        if (_leftStrip is null || _rightStrip is null)
        {
            return;
        }

        _panelsCollapsed = WantsCollapse();
        foreach ((PanelDock dock, PanelDockArea area, StackPanel strip, Splitter splitter, ColumnDefinition column, int homeColumn) in new[]
        {
            (PanelDock.Left, LeftPanel, _leftStrip, LeftSplitter, LeftColumn, 0),
            (PanelDock.Right, RightPanel, _rightStrip, RightSplitter, RightColumn, 4),
        })
        {
            var tabs = _panelLayout.VisibleIn(dock).Select(p => PanelRegistry.Find(p.Id)).OfType<PanelRegistration>().ToList();
            bool shown = tabs.Count > 0 && !_panelLayout.HiddenDocks.Contains(dock);
            bool collapse = _panelsCollapsed && shown;

            // 元の場所に戻す (折りたたまない場合と、重ねて開かない場合)。
            Grid.SetColumn(area, homeColumn);
            Grid.SetColumnSpan(area, 1);
            Grid.SetRowSpan(area, 3);
            area.HorizontalAlignment = HorizontalAlignment.Stretch;
            area.Width = double.NaN;
            Canvas.SetZIndex(area, 0);
            strip.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
            if (!collapse)
            {
                if (_overlayDock == dock)
                {
                    _overlayDock = null;
                }

                continue;
            }

            FillStrip(strip, dock, tabs);
            column.Width = GridLength.Auto;
            splitter.Visibility = Visibility.Collapsed;
            if (_overlayDock == dock)
            {
                // エディタの列の上に、保存している幅で重ねる。
                area.Visibility = Visibility.Visible;
                Grid.SetColumn(area, 2);
                Grid.SetRowSpan(area, 1);
                area.HorizontalAlignment = dock == PanelDock.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                area.Width = dock == PanelDock.Left ? _panelLayout.LeftWidth : _panelLayout.RightWidth;
                Canvas.SetZIndex(area, 10);
            }
            else
            {
                area.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>帯にパネルの見出しを縦書きで並べる。</summary>
    private void FillStrip(StackPanel strip, PanelDock dock, IReadOnlyList<PanelRegistration> tabs)
    {
        strip.Children.Clear();
        foreach (PanelRegistration tab in tabs)
        {
            string title = PanelTitle(tab);
            var text = new TextBlock { Text = title };
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Windows.Foundation.Size size = text.DesiredSize;

            // 90 度回して縦に置く (レイアウトの大きさは回した後の大きさにする)。
            text.RenderTransform = new RotateTransform { Angle = 90 };
            Canvas.SetLeft(text, size.Height);
            var canvas = new Canvas { Width = size.Height, Height = size.Width };
            canvas.Children.Add(text);
            var button = new Button
            {
                Content = canvas,
                Padding = new Thickness(4, 8, 4, 8),
                Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
                Tag = tab.Id,
            };
            AutomationProperties.SetAutomationId(button, "PanelStrip_" + tab.Id);
            AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, title);
            string id = tab.Id;
            button.Click += (_, _) => ToggleOverlay(dock, id);
            strip.Children.Add(button);
        }
    }

    /// <summary>帯の見出しを押した: そのパネルを重ねて開く。開いているパネルの見出しなら閉じる。</summary>
    private void ToggleOverlay(PanelDock dock, string id)
    {
        PanelDockArea area = dock == PanelDock.Left ? LeftPanel : RightPanel;
        bool open = _overlayDock == dock && area.ActivePanel == id;
        _panelLayout.ActiveTab[dock] = id;
        _overlayDock = open ? null : dock;
        ApplyPanelLayout();
        if (!open)
        {
            DispatcherQueue.TryEnqueue(() => area.FocusHeader());
        }
    }
}
