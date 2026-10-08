using HexEditor.App.Services;
using HexEditor.Core.Notifications;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// 全画面表示 (UI-07): タイトルバー・ツールバー・タブ列を隠してエディタを最大にする。画面の上端にマウスを置くと、隠したバー
/// (メニューを含むタイトルバー、ツールバー、タブ列) をエディタの上に重ねて出す。
/// </summary>
public sealed partial class MainWindow
{
    public const string FullScreenStatusBarKey = "ui.fullScreen.showStatusBar";

    /// <summary>画面の上端からこの距離 (epx) にポインタがあればタブ列を出す (仕様 2)。</summary>
    private const double RevealEdge = 4;

    /// <summary>上端に置いてからタブ列を出すまでの時間 (仕様 2)。</summary>
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>「F11 で全画面表示を終了」を出しておく時間 (仕様 4)。</summary>
    private static readonly TimeSpan FullScreenNoticeTime = TimeSpan.FromSeconds(3);

    private bool _fullScreen;
    private bool _tabsRevealed;
    private RectInt32 _restoreBounds;
    private bool _restoreMaximized;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _revealTimer;
    private Notification? _fullScreenNotice;

    /// <summary>通知を閉じるタイマー (フィールドに持つ。ローカル変数だとガベージコレクションで回収される)。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _fullScreenNoticeTimer;

    /// <summary>全画面表示中か。</summary>
    public bool IsFullScreen => _fullScreen;

    private void InitializeFullScreen()
    {
        Commands.Register("view.fullScreen", ToggleFullScreen, () => Toggle(_fullScreen));
        _revealTimer = DispatcherQueue.CreateTimer();
        _revealTimer.IsRepeating = false;
        _revealTimer.Interval = RevealDelay;
        _revealTimer.Tick += (_, _) => RevealTabs(true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => OnFullScreenPointer(e.GetCurrentPoint(Root).Position.Y)), true);
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(FullScreen_PreviewKeyDown), true);
    }

    /// <summary>F11 / 表示 > 全画面表示: 全画面と元の表示を切り替える (仕様 1・3)。</summary>
    private void ToggleFullScreen()
    {
        if (_fullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        if (_fullScreen)
        {
            return;
        }

        // 元に戻すときの位置と大きさ (最大化していたら最大化に戻す)。
        _restoreMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _restoreBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _fullScreen = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        ApplyFullScreenLayout();

        // 「F11 で全画面表示を終了」を 3 秒出す (仕様 4)。
        _fullScreenNotice = ShowNotice(Loc.Get("Notice_FullScreenExit"), InfoBarSeverity.Informational);
        Notification shown = _fullScreenNotice;
        _fullScreenNoticeTimer?.Stop();
        _fullScreenNoticeTimer = DispatcherQueue.CreateTimer();
        _fullScreenNoticeTimer.IsRepeating = false;
        _fullScreenNoticeTimer.Interval = FullScreenNoticeTime;
        _fullScreenNoticeTimer.Tick += (_, _) => Vm.Notifications.Dismiss(shown);
        _fullScreenNoticeTimer.Start();
        RefreshCommandUi();
    }

    private void ExitFullScreen()
    {
        if (!_fullScreen)
        {
            return;
        }

        _fullScreen = false;
        _revealTimer?.Stop();
        if (_fullScreenNotice is { } notice)
        {
            Vm.Notifications.Dismiss(notice);
            _fullScreenNotice = null;
        }

        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        if (_restoreMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        else if (_restoreBounds.Width > 0)
        {
            AppWindow.MoveAndResize(_restoreBounds);
        }

        ApplyFullScreenLayout();
        RefreshCommandUi();
    }

    /// <summary>全画面ではタイトルバー・ツールバー・タブ列を隠す。ステータスバーは設定 ui.fullScreen.showStatusBar (仕様 2)。</summary>
    private void ApplyFullScreenLayout()
    {
        UpdateThemeMenu();
        _tabsRevealed = false;
        PlaceTabStrip();
    }

    /// <summary>全画面でタイトルバー (メニューを含む)・ツールバーを表示しているか (通常の表示、または上端で重ねて出している)。</summary>
    private bool AreTopBarsShown => !_fullScreen || _tabsRevealed;

    /// <summary>
    /// タイトルバー (メニューを含む) とツールバーの表示: 通常は表示。全画面では隠し、上端にマウスを置いたときはエディタの上に重ねて出す
    /// (下の領域の位置は動かさない。高さの分だけ下の余白を負にし、前面に置く)。戻り値は重ねて出した高さ (epx)。
    /// </summary>
    private double PlaceTopBars()
    {
        AppTitleBar.Visibility = AreTopBarsShown ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = AreTopBarsShown && IsToolbarWanted ? Visibility.Visible : Visibility.Collapsed;
        bool overlay = _fullScreen && _tabsRevealed;
        double top = 0;
        foreach (FrameworkElement bar in new FrameworkElement[] { AppTitleBar, Toolbar })
        {
            if (!overlay || bar.Visibility != Visibility.Visible)
            {
                bar.Margin = new Thickness(0);
                bar.RenderTransform = null;
                Canvas.SetZIndex(bar, 0);
                continue;
            }

            bar.Margin = new Thickness(0);
            bar.Measure(new Windows.Foundation.Size(Root.ActualWidth, double.PositiveInfinity));
            double height = bar.DesiredSize.Height;

            // 行の高さを 0 にして (下の余白を負にする)、前に並べたバーの下に重ねる。
            bar.Margin = new Thickness(0, 0, 0, -height);
            bar.RenderTransform = new TranslateTransform { Y = top };
            Canvas.SetZIndex(bar, 2);
            top += height;
        }

        return top;
    }

    /// <summary>全画面ではステータスバーを隠すか。</summary>
    private bool HidesStatusBarForFullScreen => _fullScreen && !App.Settings.GetBool(FullScreenStatusBarKey, true);

    /// <summary>タブ列 (TabView の見出しの帯)。</summary>
    private FrameworkElement? TabStrip => FindByName(Tabs, "TabContainerGrid");

    /// <summary>
    /// タブ列の表示: 通常は表示。全画面では隠し、上端にマウスを置いたときはエディタの上に重ねて出す (エディタの位置は動かさない)。
    /// </summary>
    private void PlaceTabStrip()
    {
        double bars = PlaceTopBars();
        _revealBottom = bars;
        if (TabStrip is not { } strip)
        {
            return;
        }

        if (!_fullScreen)
        {
            strip.Visibility = Visibility.Visible;
            strip.RenderTransform = null;
            Canvas.SetZIndex(strip, 0);
            Tabs.Margin = new Thickness(0);
        }
        else if (!_tabsRevealed)
        {
            strip.Visibility = Visibility.Collapsed;
            strip.RenderTransform = null;
            Tabs.Margin = new Thickness(0);
        }
        else
        {
            // 帯の高さの分だけタブ全体を上にずらし (エディタの位置はそのまま)、帯だけを下に戻して重ねる。重ねて出したタイトルバーと
            // ツールバーがあれば、その下に置く。
            strip.Visibility = Visibility.Visible;
            strip.Measure(new Windows.Foundation.Size(Tabs.ActualWidth, double.PositiveInfinity));
            double height = strip.DesiredSize.Height;
            double editorTop = EditorArea.TransformToVisual(Root).TransformPoint(default).Y;
            double below = Math.Max(0, bars - editorTop);
            Tabs.Margin = new Thickness(0, -height, 0, 0);
            strip.RenderTransform = new TranslateTransform { Y = height + below };
            Canvas.SetZIndex(strip, 1);
            _revealBottom = editorTop + below + height;
        }

        PlaceStartPage();
    }

    /// <summary>全画面中のポインタの位置 (Root の座標): 上端に 300 ms 置いたらタブ列を出し、離れたら隠す。</summary>
    private void OnFullScreenPointer(double y)
    {
        if (!_fullScreen)
        {
            return;
        }

        if (y <= RevealEdge)
        {
            if (!_tabsRevealed && _revealTimer is { IsRunning: false })
            {
                _revealTimer.Start();
            }

            return;
        }

        _revealTimer?.Stop();

        // メニューなどのポップアップを開いている間は隠さない (メニューの項目の上にポインタがあるとき)。
        if (_tabsRevealed && y > _revealBottom + RevealEdge
            && (Root.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count == 0))
        {
            RevealTabs(false);
        }
    }

    /// <summary>上端で重ねて出したバー (タイトルバー・ツールバー・タブ列) の下端 (Root の座標)。</summary>
    private double _revealBottom;

    private void RevealTabs(bool reveal)
    {
        if (!_fullScreen || reveal == _tabsRevealed)
        {
            return;
        }

        _tabsRevealed = reveal;
        PlaceTabStrip();
    }

    /// <summary>Esc: エディタにフォーカスがあり、検索バーなどが開いておらず、選択もないときは全画面を終える (仕様 3)。</summary>
    private void FullScreen_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_fullScreen || e.Handled || e.Key != VirtualKey.Escape || FindBar.IsOpen || GoToBar.Visibility == Visibility.Visible
            || Editor is not { HasSelection: false } || CurrentKeyContext().Scope != Core.Commands.KeyScope.Editor)
        {
            return;
        }

        ExitFullScreen();
        e.Handled = true;
    }
}
