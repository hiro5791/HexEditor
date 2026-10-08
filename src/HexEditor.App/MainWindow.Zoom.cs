using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ズーム (UI-08): Hex 表示のズーム (全タブ共通 / タブごと) と画面全体のズーム、表示 > ズーム のメニュー、ステータスバーの倍率の表示。
/// 倍率から Hex ビューの文字の大きさを求めるのは HexView (VIEW-43)。
/// </summary>
public sealed partial class MainWindow
{
    private ZoomHost _zoomHost = null!;
    private Button _statusHexZoom = null!;
    private Button _statusUiZoom = null!;
    private string _hexZoomScope = ZoomSettings.ScopeAll;
    private int _wheelRemainder;

    /// <summary>ウィンドウの中身を画面全体のズームの入れ物に入れ、表示 > ズーム とステータスバーの項目を作る (コンストラクタから 1 回)。</summary>
    private void InitializeZoom()
    {
        // 画面全体のズーム (UI-08 の仕様 3): ウィンドウの中身を ZoomHost に入れる (100% では何もしないのと同じ)。
        Content = null;
        _zoomHost = new ZoomHost { MinContentSize = new Windows.Foundation.Size(MinWidth, MinHeight) };
        _zoomHost.Children.Add(Root);
        Content = _zoomHost;
        _zoomHost.AppliedZoomChanged += ApplyAppliedZoom;

        // 表示 > ズーム
        MenuBarItem view = MainMenu.Items.First(m => m.Items.OfType<MenuFlyoutSubItem>().Any(i => AutomationProperties.GetAutomationId(i) == "Command_Panels"));
        MenuFlyoutSubItem panels = view.Items.OfType<MenuFlyoutSubItem>().First(i => AutomationProperties.GetAutomationId(i) == "Command_Panels");
        var zoom = new MenuFlyoutSubItem { Text = MenuText("Menu_View_Zoom"), AccessKey = MenuAccessKey("Menu_View_Zoom") };
        AutomationProperties.SetAutomationId(zoom, "Command_Zoom");
        zoom.Items.Add(CommandItem("view.zoomIn", "Command_ZoomIn", "Menu_View_ZoomIn"));
        zoom.Items.Add(CommandItem("view.zoomOut", "Command_ZoomOut", "Menu_View_ZoomOut"));
        zoom.Items.Add(CommandItem("view.zoomReset", "Command_ZoomReset", "Menu_View_ZoomReset"));
        zoom.Items.Add(new MenuFlyoutSeparator());
        zoom.Items.Add(CommandItem("view.uiZoomIn", "Command_UiZoomIn", "Menu_View_UiZoomIn"));
        zoom.Items.Add(CommandItem("view.uiZoomOut", "Command_UiZoomOut", "Menu_View_UiZoomOut"));
        zoom.Items.Add(CommandItem("view.uiZoomReset", "Command_UiZoomReset", "Menu_View_UiZoomReset"));
        var fullScreen = new ToggleMenuFlyoutItem { Text = MenuText("Menu_View_FullScreen"), AccessKey = MenuAccessKey("Menu_View_FullScreen") };
        CommandUi.SetId(fullScreen, "view.fullScreen");
        AutomationProperties.SetAutomationId(fullScreen, "Command_FullScreen");
        int at = view.Items.IndexOf(panels) + 1;
        view.Items.Insert(at, zoom);
        view.Items.Insert(at + 1, fullScreen);

        Commands.Register("view.zoomIn", () => ChangeHexZoom(1, null));
        Commands.Register("view.zoomOut", () => ChangeHexZoom(-1, null));
        Commands.Register("view.zoomReset", () => SetHexZoom(ZoomLevels.Default, null));
        Commands.Register("view.uiZoomIn", () => SetUiZoom(ZoomLevels.ZoomIn(UiZoom)));
        Commands.Register("view.uiZoomOut", () => SetUiZoom(ZoomLevels.ZoomOut(UiZoom)));
        Commands.Register("view.uiZoomReset", () => SetUiZoom(ZoomLevels.Default));

        // ステータスバーの倍率 (UI-06 の「ズーム」。100% のときは出さない。クリックで 100% に戻す)。
        var style = (Style)StatusBar.Resources["StatusItemStyle"];
        _statusHexZoom = new Button { Style = style, Tag = "zoom" };
        _statusUiZoom = new Button { Style = style, Tag = "zoom" };
        AutomationProperties.SetAutomationId(_statusHexZoom, "Status_HexZoom");
        AutomationProperties.SetAutomationId(_statusUiZoom, "Status_UiZoom");
        ToolTipService.SetToolTip(_statusHexZoom, Loc.Get("Status_HexZoomTip"));
        ToolTipService.SetToolTip(_statusUiZoom, Loc.Get("Status_UiZoomTip"));
        _statusHexZoom.Click += (_, _) => SetHexZoom(ZoomLevels.Default, null);
        _statusUiZoom.Click += (_, _) => SetUiZoom(ZoomLevels.Default);
        int size = StatusItems.Children.IndexOf(StatusSize) + 1;
        StatusItems.Children.Insert(size, _statusUiZoom);
        StatusItems.Children.Insert(size, _statusHexZoom);
        _statusHexZoom.SizeChanged += (_, _) => QueueStatusBarLayout();
        _statusUiZoom.SizeChanged += (_, _) => QueueStatusBarLayout();

        // 全タブ共通の倍率・適用範囲・画面全体の倍率の変更 (別のウィンドウ・設定画面・設定ファイルの編集) を反映する。
        _hexZoomScope = HexZoomScope();
        // 設定は全ウィンドウに反映する (UI-14 の仕様 2)。閉じたウィンドウは購読をやめる。
        Action<IReadOnlyCollection<string>> zoomSettingsChanged = keys =>
        {
            if (keys.Any(k => k is ZoomSettings.HexKey or ZoomSettings.HexScopeKey or ZoomSettings.UiKey))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closingConfirmed)
                    {
                        ApplyZoomSettings();
                    }
                });
            }
        };
        App.Settings.Changed += zoomSettingsChanged;
        Vm.Documents.CollectionChanged += Documents_CollectionChangedForZoom;
        ScreenZoom.Changed += ApplyScreenZoom;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                ScreenZoom.Changed -= ApplyScreenZoom;
                App.Settings.Changed -= zoomSettingsChanged;
            }
        };
        ScreenZoom.SetFactor(ZoomLevels.Factor(UiZoom));
        ApplyScreenZoom();
    }

    private MenuFlyoutItem CommandItem(string command, string automationId, string key)
    {
        var item = new MenuFlyoutItem { Text = MenuText(key), AccessKey = MenuAccessKey(key) };
        CommandUi.SetId(item, command);
        AutomationProperties.SetAutomationId(item, automationId);
        return item;
    }

    // メニューの項目の表示名 (リソース キー.Text) は MenuText (MainWindow.Tabs.cs)。

    /// <summary>メニューの項目のアクセスキー (リソース <c>キー.AccessKey</c>)。</summary>
    private static string MenuAccessKey(string key) => Loc.Get(key + "/AccessKey");

    // ---- Hex 表示のズーム (UI-08 の仕様 2) ----

    private static string HexZoomScope() =>
        App.Settings.GetString(ZoomSettings.HexScopeKey, ZoomSettings.ScopeAll) == ZoomSettings.ScopeTab ? ZoomSettings.ScopeTab : ZoomSettings.ScopeAll;

    /// <summary>全タブ共通の倍率 (設定 view.zoom.hex)。</summary>
    private static int SharedHexZoom => ZoomLevels.Normalize(App.Settings.GetInt(ZoomSettings.HexKey, ZoomLevels.Default));

    /// <summary>文書の Hex 表示の倍率 (百分率)。</summary>
    private int HexZoomOf(DocumentViewModel? doc) =>
        _hexZoomScope == ZoomSettings.ScopeTab && doc?.HexZoom is { } own ? own : SharedHexZoom;

    /// <summary>選択中のタブの Hex 表示の倍率。</summary>
    internal int CurrentHexZoom => HexZoomOf(Vm.Selected);

    /// <summary>Ctrl+= / Ctrl+- / Ctrl+ホイール: 1 段階ずつ動かす。<paramref name="pointerY"/> はホイールのポインタの位置 (VIEW-43 の仕様 4)。</summary>
    private void ChangeHexZoom(int notches, double? pointerY) => SetHexZoom(ZoomLevels.Step(CurrentHexZoom, notches), pointerY);

    private void SetHexZoom(int percent, double? pointerY)
    {
        percent = ZoomLevels.Normalize(percent);
        if (_hexZoomScope == ZoomSettings.ScopeTab)
        {
            if (Vm.Selected is { } doc)
            {
                doc.HexZoom = percent;
            }
            else
            {
                App.Settings.SetInt(ZoomSettings.HexKey, percent, ZoomLevels.Default);
            }

            if (SelectedView() is { } view)
            {
                view.ZoomAt(ZoomLevels.Factor(percent), pointerY);
            }
        }
        else
        {
            App.Settings.SetInt(ZoomSettings.HexKey, percent, ZoomLevels.Default);
            foreach (HexView view in _views)
            {
                view.ZoomAt(ZoomLevels.Factor(percent), view == SelectedView() ? pointerY : null);
            }
        }

        UpdateZoomStatus();
    }

    /// <summary>Hex ビューが読み込まれた: その文書の倍率にし、Ctrl+ホイールをつなぐ。</summary>
    private void AttachZoom(HexView view)
    {
        view.ZoomWheel -= HexView_ZoomWheel;
        view.ZoomWheel += HexView_ZoomWheel;
        view.ScreenZoom = _zoomHost.AppliedZoom;
        view.ZoomAt(ZoomLevels.Factor(HexZoomOf(view.DataContext as DocumentViewModel)), null);
        UpdateZoomStatus();
    }

    /// <summary>Ctrl+ホイールは 1 ノッチ (120) で 1 段階。高精度タッチパッドの細かい量は貯めて使う (UI-08 の仕様 5)。</summary>
    private void HexView_ZoomWheel(object? sender, HexViewZoomWheelEventArgs e)
    {
        _wheelRemainder += e.Delta;
        int notches = _wheelRemainder / 120;
        if (notches == 0)
        {
            return;
        }

        _wheelRemainder -= notches * 120;
        ChangeHexZoom(notches, e.PointerY);
    }

    /// <summary>適用範囲が「今のタブだけ」のとき、新しいタブは表示中のタブの倍率を引き継ぐ (仕様 2 の 3)。</summary>
    private void Documents_CollectionChangedForZoom(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_hexZoomScope != ZoomSettings.ScopeTab || e.NewItems is null)
        {
            return;
        }

        int current = CurrentHexZoom;
        foreach (DocumentViewModel doc in e.NewItems.OfType<DocumentViewModel>())
        {
            doc.HexZoom ??= current;
        }
    }

    /// <summary>設定が変わった: 適用範囲の切り替え (仕様 2 の 5)、全タブ共通の倍率、画面全体の倍率を反映する。</summary>
    private void ApplyZoomSettings()
    {
        string scope = HexZoomScope();
        if (scope != _hexZoomScope)
        {
            if (scope == ZoomSettings.ScopeAll)
            {
                // 「今のタブだけ」から「全タブ共通」へ: 表示中のタブの倍率を全タブに使う。ウィンドウが複数あるときは、最後にアクティブ
                // だったウィンドウの表示中のタブの倍率 (他のウィンドウは倍率を書かない)。
                int shown = CurrentHexZoom;
                foreach (DocumentViewModel doc in Vm.Documents)
                {
                    doc.HexZoom = null;
                }

                _hexZoomScope = scope;
                if (WindowManager.Windows.Count == 0 || WindowManager.Current == this)
                {
                    App.Settings.SetInt(ZoomSettings.HexKey, shown, ZoomLevels.Default);
                }
            }
            else
            {
                // 「全タブ共通」から「今のタブだけ」へ: 各タブは今の共通の倍率から始める。
                int shared = SharedHexZoom;
                foreach (DocumentViewModel doc in Vm.Documents)
                {
                    doc.HexZoom = shared;
                }

                _hexZoomScope = scope;
            }
        }

        foreach (HexView view in _views)
        {
            view.ZoomAt(ZoomLevels.Factor(HexZoomOf(view.DataContext as DocumentViewModel)), null);
        }

        ScreenZoom.SetFactor(ZoomLevels.Factor(UiZoom));
        UpdateZoomStatus();
    }

    // ---- 画面全体のズーム (UI-08 の仕様 3) ----

    private static int UiZoom => ZoomLevels.Normalize(App.Settings.GetInt(ZoomSettings.UiKey, ZoomLevels.Default));

    private void SetUiZoom(int percent)
    {
        App.Settings.SetInt(ZoomSettings.UiKey, ZoomLevels.Normalize(percent), ZoomLevels.Default);
        ScreenZoom.SetFactor(ZoomLevels.Factor(UiZoom));
        UpdateZoomStatus();
    }

    /// <summary>画面全体の倍率をこのウィンドウに反映する (実際の倍率はウィンドウの大きさで決まる。ZoomHost)。</summary>
    private void ApplyScreenZoom()
    {
        _zoomHost.Zoom = ScreenZoom.Factor;
        UpdateZoomStatus();
    }

    /// <summary>実際に使う倍率が変わった: メニューの文字の大きさ、Hex ビューのピクセル合わせ。</summary>
    private void ApplyAppliedZoom()
    {
        double factor = _zoomHost.AppliedZoom;
        foreach (HexView view in _views)
        {
            view.ScreenZoom = factor;
        }

        // メニューの項目はウィンドウの外 (ポップアップ) に出るので、文字の大きさを倍率に合わせる。
        double fontSize = (double)Application.Current.Resources["ControlContentThemeFontSize"] * factor;
        foreach (MenuFlyoutItemBase item in AllMenuItems(MainMenu.Items.SelectMany(m => m.Items)))
        {
            if (item is Control control)
            {
                if (factor == 1)
                {
                    control.ClearValue(Control.FontSizeProperty);
                }
                else
                {
                    control.FontSize = fontSize;
                }
            }
        }

        UpdateZoomStatus();
    }

    private static IEnumerable<MenuFlyoutItemBase> AllMenuItems(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (MenuFlyoutItemBase item in items)
        {
            yield return item;
            if (item is MenuFlyoutSubItem sub)
            {
                foreach (MenuFlyoutItemBase child in AllMenuItems(sub.Items))
                {
                    yield return child;
                }
            }
        }
    }

    // ---- ステータスバー (UI-06、UI-08 の「画面」) ----

    private void UpdateZoomStatus()
    {
        if (_statusHexZoom is null)
        {
            return;
        }

        int hex = CurrentHexZoom;
        int ui = UiZoom;
        _statusHexZoom.Content = Loc.Format("Status_HexZoom", hex);
        _statusUiZoom.Content = Loc.Format("Status_UiZoom", ui);
        _statusHexZoom.Visibility = Vm.Selected is not null && hex != ZoomLevels.Default ? Visibility.Visible : Visibility.Collapsed;
        _statusUiZoom.Visibility = ui != ZoomLevels.Default ? Visibility.Visible : Visibility.Collapsed;
        QueueStatusBarLayout();
    }

    /// <summary>ステータスバーの倍率の項目を出すか (100% 以外のときだけ)。</summary>
    private bool IsZoomStatusWanted(Button button) =>
        button == _statusHexZoom ? Vm.Selected is not null && CurrentHexZoom != ZoomLevels.Default
        : button == _statusUiZoom && UiZoom != ZoomLevels.Default;
}
