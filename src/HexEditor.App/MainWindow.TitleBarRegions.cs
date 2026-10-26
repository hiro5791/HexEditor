using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// タイトルバーの入力を通す領域 (UI-02 の仕様 5)。TitleBar は LeftHeader・Content の欄全体を入力を通す領域にするので、
/// アイコンとタイトルの文字列まで Passthrough になり、そこをドラッグしてもウィンドウが動かない (長いタイトルではドラッグできる
/// 余白がほとんど残らない)。入力を通す領域を、対話する要素 (メニューバーとコマンドパレットの入口) の矩形だけに置き直す。
/// </summary>
public sealed partial class MainWindow
{
    private bool _titleBarRegionsQueued;

    /// <summary>入力を通す領域を置き直すきっかけを登録する (TitleBar が自分の領域を設定し直す場面のあと)。</summary>
    private void InitializeTitleBarRegions()
    {
        SizeChangedEventHandler changed = (_, _) => QueueTitleBarRegions();
        AppTitleBar.SizeChanged += (_, _) =>
        {
            UpdateTitleMaxWidth();
            QueueTitleBarRegions();
        };
        MainMenu.SizeChanged += (_, _) => UpdateTitleMaxWidth();
        AppTitleBar.Loaded += (_, _) => QueueTitleBarRegions();
        AppTitleBar.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => QueueTitleBarRegions());
        MainMenu.SizeChanged += changed;
        WindowTitle.SizeChanged += changed;
        if (_paletteButton is not null)
        {
            _paletteButton.SizeChanged += changed;
        }

        Root.SizeChanged += changed;
        Root.Loaded += (_, _) =>
        {
            if (Root.XamlRoot is { } xamlRoot)
            {
                xamlRoot.Changed += (_, _) => QueueTitleBarRegions();
            }
        };
    }

    /// <summary>タイトルの右 (入口のボタン) に残す幅 (DIP): アイコンだけの入口 (最大 48) と余白。</summary>
    private const double TitlePaletteReserve = 56;

    /// <summary>
    /// タイトルの最大幅を、タイトルバーの幅から他の欄 (余白・操作ボタンの分・メニューバー) と入口のボタンの分を引いた幅にする。
    /// LeftHeader の欄は幅が Auto なので、上限がないと長いタイトルが省略されずに入口のボタンを右の外へ押し出す。
    /// </summary>
    private void UpdateTitleMaxWidth()
    {
        if (AppTitleBar.ActualWidth <= 0
            || VisualTreeHelper.GetChildrenCount(AppTitleBar) == 0
            || VisualTreeHelper.GetChild(AppTitleBar, 0) is not Grid { ColumnDefinitions.Count: 12 } layoutRoot)
        {
            return;
        }

        // 既定のテンプレートの列: 3 が LeftHeader、8 が Content。それ以外 (左右の余白・操作ボタンの分・最小のドラッグ領域) は固定の幅。
        double others = 0;
        for (int i = 0; i < layoutRoot.ColumnDefinitions.Count; i++)
        {
            if (i is not 3 and not 8)
            {
                others += layoutRoot.ColumnDefinitions[i].ActualWidth;
            }
        }

        // タイトルの前 (左の余白・アイコン・メニューバー・間隔) の幅。ActualOffset は書字方向によらず親の中の位置。
        double before = WindowTitle.ActualOffset.X;
        double max = Math.Max(0, AppTitleBar.ActualWidth - others - before - TitlePaletteReserve);
        if (Math.Abs(WindowTitle.MaxWidth - max) >= 0.5)
        {
            WindowTitle.MaxWidth = max;
        }
    }

    /// <summary>TitleBar の更新 (同じレイアウトの処理の中で動く) が終わったあとに置き直す。</summary>
    private void QueueTitleBarRegions()
    {
        if (_titleBarRegionsQueued)
        {
            return;
        }

        _titleBarRegionsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _titleBarRegionsQueued = false;
            ApplyTitleBarRegions();
        });
    }

    /// <summary>入力を通す領域を、見えている対話する要素の矩形 (クライアント領域の物理ピクセル) だけにする。</summary>
    private void ApplyTitleBarRegions()
    {
        // 閉じたウィンドウでは何もしない。閉じる途中の SizeChanged・XamlRoot.Changed で予約した処理が閉じたあとに動くことがあり、
        // そのときは AppWindow が null になる。DispatcherQueue の処理の中の例外は Application.UnhandledException に来ず、
        // プロセスが 0xC000027B で落ちる (2 つ目のウィンドウを閉じると落ちていた)。
        if (_closingConfirmed || AppWindow is not { } appWindow || Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var source = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
        var rects = new List<RectInt32>();
        if (AppTitleBar.Visibility == Visibility.Visible)
        {
            double scale = xamlRoot.RasterizationScale;
            foreach (FrameworkElement? element in new FrameworkElement?[] { MainMenu, _paletteButton })
            {
                if (element is not { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 })
                {
                    continue;
                }

                // 右から左に書く言語でも、ウィンドウ (null) への変換で左右反転を含んだ実際の位置になる。
                var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                int left = (int)Math.Floor(bounds.Left * scale);
                int top = (int)Math.Floor(bounds.Top * scale);
                int right = (int)Math.Ceiling(bounds.Right * scale);
                int bottom = (int)Math.Ceiling(bounds.Bottom * scale);
                if (right > left && bottom > top && right > 0 && bottom > 0)
                {
                    rects.Add(new RectInt32(left, top, right - left, bottom - top));
                }
            }
        }

        if (source.GetRegionRects(NonClientRegionKind.Passthrough).SequenceEqual(rects))
        {
            return;
        }

        source.SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }
}
