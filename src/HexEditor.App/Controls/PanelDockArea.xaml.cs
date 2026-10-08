using HexEditor.App.Services;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Controls;

/// <summary>見出しのタブ 1 つ (パネル ID と見出し)。</summary>
public sealed record PanelTab(string Id, string Title);

/// <summary>パネルの移動の要求 (見出しのメニュー・ドラッグ。UI-05 の仕様 3、4)。</summary>
public sealed record PanelMoveRequest(string PanelId, PanelDock Target);

/// <summary>
/// 左・右・下の 1 か所のパネルの領域 (UI-05)、または浮動パネルの中身。見出しのタブ、「…」(移動・閉じる)、「×」と、
/// 選んでいるパネルの中身を表示する。配置の判断はしない (要求を出し、ウィンドウの <c>MainWindow.Panels</c> が配置を変える)。
/// </summary>
public sealed partial class PanelDockArea : UserControl
{
    /// <summary>ドラッグ中のパネル (ドロップ先の判定に使う)。</summary>
    public static string? DraggingPanel { get; private set; }

    /// <summary>見出しのメニューの項目の処理 (テスト用の命令が、メニューを開かずに項目を押すのに使う)。</summary>
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuFlyoutItem, Action> MenuActions = [];

    /// <summary>ドラッグの開始・終了 (ウィンドウがドロップ先の枠を出す)。</summary>
    public static event Action<string?>? DraggingChanged;

    private string? _active;

    public PanelDockArea()
    {
        InitializeComponent();
        MoreButton.Click += (_, _) =>
        {
            if (_active is { } id)
            {
                CreateMenu(id).ShowAt(MoreButton);
            }
        };
        CloseButton.Click += (_, _) =>
        {
            if (_active is { } id)
            {
                CloseRequested?.Invoke(this, id);
            }
        };
        DragOver += (_, e) =>
        {
            if (DraggingPanel is not null)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                DropHighlight.Visibility = Visibility.Visible;
            }
        };
        DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Drop += (_, _) =>
        {
            DropHighlight.Visibility = Visibility.Collapsed;
            if (DraggingPanel is { } id)
            {
                MoveRequested?.Invoke(this, new PanelMoveRequest(id, Dock));
            }
        };
    }

    /// <summary>この領域の場所。浮動パネルでは <see cref="PanelDock.Floating"/>。</summary>
    public PanelDock Dock { get; set; }

    /// <summary>選んでいるパネル。</summary>
    public string? ActivePanel => _active;

    /// <summary>見出しの並び。</summary>
    public IReadOnlyList<PanelTab> Tabs { get; private set; } = [];

    public event EventHandler<string>? TabSelected;

    public event EventHandler<string>? CloseRequested;

    public event EventHandler<PanelMoveRequest>? MoveRequested;

    /// <summary>見出しのダブルクリック (浮動パネルを元の場所に戻す。UI-05 の仕様 5)。</summary>
    public event EventHandler<string>? HeaderDoubleTapped;

    /// <summary>見出しと中身を表示する。<paramref name="content"/> が null なら「文書が開かれていません」を出す。</summary>
    public void Show(IReadOnlyList<PanelTab> tabs, string? active, FrameworkElement? content)
    {
        Tabs = tabs;
        _active = active;
        TabStrip.Children.Clear();
        foreach (PanelTab tab in tabs)
        {
            TabStrip.Children.Add(CreateTab(tab, tab.Id == active));
        }

        if (!ReferenceEquals(Body.Content, content))
        {
            Body.Content = content;
        }

        NoDocument.Visibility = content is null && tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 中身を外す (別の場所に移るパネルの要素は、先に元の場所から外してから置く。要素は 1 か所にしか置けない)。
    /// <paramref name="keep"/> と同じなら外さない。
    /// </summary>
    public void DetachContent(FrameworkElement? keep)
    {
        if (!ReferenceEquals(Body.Content, keep))
        {
            Body.Content = null;
        }
    }

    /// <summary>見出しのタブにフォーカスを移す (F6 の領域の移動。UI-52)。</summary>
    public bool FocusHeader()
    {
        Button? tab = TabStrip.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == _active)
            ?? TabStrip.Children.OfType<Button>().FirstOrDefault();
        return tab?.Focus(FocusState.Keyboard) ?? false;
    }

    private Button CreateTab(PanelTab tab, bool selected)
    {
        var button = new Button
        {
            Content = tab.Title,
            Tag = tab.Id,
            Style = (Style)Resources[selected ? "PanelTabSelectedStyle" : "PanelTabStyle"],
            CanDrag = true,
        };
        AutomationProperties.SetAutomationId(button, "PanelTab_" + tab.Id);
        AutomationProperties.SetName(button, tab.Title);
        if (selected)
        {
            AutomationProperties.SetItemStatus(button, Loc.Get("Panel_Selected"));
        }

        button.Click += (_, _) => TabSelected?.Invoke(this, tab.Id);
        button.DoubleTapped += (_, e) =>
        {
            HeaderDoubleTapped?.Invoke(this, tab.Id);
            e.Handled = true;
        };

        // 右クリック・Shift+F10・アプリケーションキーで「移動」「閉じる」のメニュー (仕様 4)。
        button.ContextFlyout = CreateMenu(tab.Id);
        button.DragStarting += (_, e) =>
        {
            e.Data.SetText("hexeditor-panel:" + tab.Id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
            SetDragging(tab.Id);
        };
        button.DropCompleted += (_, _) => SetDragging(null);
        return button;
    }

    internal static void SetDragging(string? id)
    {
        DraggingPanel = id;
        DraggingChanged?.Invoke(id);
    }

    /// <summary>見出しのメニュー: 移動 &gt; 左 / 右 / 下 / 浮動、閉じる。</summary>
    public MenuFlyout CreateMenu(string panelId)
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "PanelMenu_" + panelId);
        var move = new MenuFlyoutSubItem { Text = Loc.Get("Panel_Move") };
        AutomationProperties.SetAutomationId(move, "PanelMove_" + panelId);
        foreach ((PanelDock dock, string key) in new[]
        {
            (PanelDock.Left, "Panel_MoveLeft"), (PanelDock.Right, "Panel_MoveRight"),
            (PanelDock.Bottom, "Panel_MoveBottom"), (PanelDock.Floating, "Panel_MoveFloating"),
        })
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = dock != Dock };
            AutomationProperties.SetAutomationId(item, $"PanelMove_{panelId}_{PanelLayout.DockName(dock)}");
            Action moveTo = () => MoveRequested?.Invoke(this, new PanelMoveRequest(panelId, dock));
            MenuActions.AddOrUpdate(item, moveTo);
            item.Click += (_, _) => moveTo();
            move.Items.Add(item);
        }

        menu.Items.Add(move);
        var close = new MenuFlyoutItem { Text = Loc.Get("Panel_CloseMenu") };
        AutomationProperties.SetAutomationId(close, "PanelClose_" + panelId);
        close.Click += (_, _) => CloseRequested?.Invoke(this, panelId);
        menu.Items.Add(close);
        return menu;
    }

    /// <summary>見出しのタブの右クリックメニュー (テスト用の命令と、キーボードでの操作の確認に使う)。</summary>
    public MenuFlyout? MenuOf(string panelId) =>
        TabStrip.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == panelId)?.ContextFlyout as MenuFlyout;

    /// <summary>ドロップ先の枠を出す・消す (ドラッグの開始・終了で、ウィンドウが全部の領域に対して呼ぶ)。</summary>
    public void ShowDropTarget(bool show) => DropHighlight.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
}
