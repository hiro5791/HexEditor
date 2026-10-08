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

/// <summary>見出しをどの領域にも落とさずにドラッグを終えた (UI-05 の仕様 3。<paramref name="ScreenPoint"/> は終えた位置、物理ピクセル)。</summary>
public sealed record PanelDropOutside(string PanelId, Windows.Graphics.PointInt32 ScreenPoint);

/// <summary>
/// 左・右・下の 1 か所のパネルの領域 (UI-05)、または浮動パネルの中身。見出しのタブ、「…」(移動・閉じる)、「×」と、
/// 選んでいるパネルの中身を表示する。配置の判断はしない (要求を出し、ウィンドウの <c>MainWindow.Panels</c> が配置を変える)。
/// </summary>
public sealed partial class PanelDockArea : UserControl
{
    /// <summary>ドラッグ中のパネル (ドロップ先の判定に使う)。</summary>
    public static string? DraggingPanel { get; private set; }

    /// <summary>
    /// ドラッグ中のパネルを持つウィンドウ (<see cref="Owner"/>)。パネルはウィンドウごとに持つので、別のウィンドウの領域には落とせない。
    /// </summary>
    public static object? DraggingOwner { get; private set; }

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
            // 別のウィンドウのパネルは受け付けない (ドロップできないカーソルになる)。
            if (AcceptsDraggedPanel)
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                DropHighlight.Visibility = Visibility.Visible;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        };
        DragLeave += (_, _) => DropHighlight.Visibility = Visibility.Collapsed;
        Drop += (_, _) =>
        {
            DropHighlight.Visibility = Visibility.Collapsed;
            if (AcceptsDraggedPanel && DraggingPanel is { } id)
            {
                MoveRequested?.Invoke(this, new PanelMoveRequest(id, Dock));
            }
        };
    }

    /// <summary>この領域を持つウィンドウ (浮動パネルでは、そのパネルを持つメインウィンドウ)。</summary>
    public object? Owner { get; set; }

    /// <summary>ドラッグ中のパネルをこの領域に落とせるか (同じウィンドウのパネルだけ)。</summary>
    public bool AcceptsDraggedPanel => DraggingPanel is not null && ReferenceEquals(DraggingOwner, Owner);

    /// <summary>見出しをどの領域にも落とさずにドラッグを終えた (ウィンドウが浮動パネルにするかを決める。UI-05 の仕様 3)。</summary>
    public event EventHandler<PanelDropOutside>? DroppedOutside;

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

    /// <summary>中身 (文書がないときの案内を除く) があるか。</summary>
    public bool HasBody => Body.Content is UIElement { Visibility: Visibility.Visible };

    /// <summary><paramref name="node"/> が中身の中の要素か (見出しではなく)。</summary>
    public bool BodyContains(DependencyObject node) => Body.Content is DependencyObject content && IsWithin(node, content);

    /// <summary>
    /// 中身にフォーカスを移す (F6 の領域の移動で、見出しの次。UI-52)。中身が <see cref="Panels.IPanelContent"/> なら、その決めた要素
    /// (一覧など) に移す。そうでなければ最初にフォーカスできる要素に移す。
    /// </summary>
    public bool FocusBody()
    {
        if (Body.Content is Panels.IPanelContent panel)
        {
            return panel.FocusContent();
        }

        return Body.Content is DependencyObject content
            && Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(content) is Control control
            && control.Focus(FocusState.Keyboard);
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, root))
            {
                return true;
            }
        }

        return false;
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
            SetDragging(tab.Id, Owner);
        };
        button.DropCompleted += (_, e) => EndDrag(tab.Id, e.DropResult, CursorPosition());
        return button;
    }

    /// <summary>
    /// ドラッグの終わり。どの領域にも落とさなかった (結果が <see cref="DataPackageOperation.None"/>) ときは、終えた位置を
    /// <see cref="DroppedOutside"/> で知らせる (テスト用の命令からも呼ぶ)。
    /// </summary>
    public void EndDrag(string panelId, DataPackageOperation result, Windows.Graphics.PointInt32 screenPoint)
    {
        SetDragging(null, null);
        if (result == DataPackageOperation.None)
        {
            DroppedOutside?.Invoke(this, new PanelDropOutside(panelId, screenPoint));
        }
    }

    /// <summary>ドラッグを始めた (テスト用の命令からも呼ぶ)。</summary>
    public void BeginDrag(string panelId) => SetDragging(panelId, Owner);

    private static void SetDragging(string? id, object? owner)
    {
        DraggingPanel = id;
        DraggingOwner = id is null ? null : owner;
        DraggingChanged?.Invoke(id);
    }

    private static Windows.Graphics.PointInt32 CursorPosition() =>
        GetCursorPos(out NativePoint p) ? new Windows.Graphics.PointInt32(p.X, p.Y) : default;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

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
            // 浮動パネルにできないパネル (結果一覧など) は「浮動」を無効にする。
            bool allowed = dock != PanelDock.Floating || Panels.PanelRegistry.Find(panelId)?.CanFloat != false;
            var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = dock != Dock && allowed };
            AutomationProperties.SetAutomationId(item, $"PanelMove_{panelId}_{PanelLayout.DockName(dock)}");
            item.Click += (_, _) => RequestMove(panelId, dock);
            move.Items.Add(item);
        }

        menu.Items.Add(move);
        var close = new MenuFlyoutItem { Text = Loc.Get("Panel_CloseMenu") };
        AutomationProperties.SetAutomationId(close, "PanelClose_" + panelId);
        close.Click += (_, _) => CloseRequested?.Invoke(this, panelId);
        menu.Items.Add(close);
        return menu;
    }

    /// <summary>
    /// 「移動 &gt; …」の項目と同じ処理 (テスト用の命令からも呼ぶ。メニューの項目に処理を結び付けた表は、項目の
    /// ラッパーがガベージコレクションで作り直されると引けなくなるため使わない)。
    /// </summary>
    public void RequestMove(string panelId, PanelDock dock) => MoveRequested?.Invoke(this, new PanelMoveRequest(panelId, dock));

    /// <summary>見出しのタブの右クリックメニュー (テスト用の命令と、キーボードでの操作の確認に使う)。</summary>
    public MenuFlyout? MenuOf(string panelId) =>
        TabStrip.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == panelId)?.ContextFlyout as MenuFlyout;

    /// <summary>ドロップ先の枠を出す・消す (ドラッグの開始・終了で、ウィンドウが全部の領域に対して呼ぶ)。</summary>
    public void ShowDropTarget(bool show) => DropHighlight.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
}
