using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace HexEditor.App.Controls;

/// <summary>
/// 画面全体のズーム (UI-08 の仕様 3) をポップアップにも効かせる。ポップアップはウィンドウの中身 (<see cref="ZoomHost"/>) の外に出るので、
/// 暗黙のスタイル (Themes/PopupZoom.xaml) で、アプリのすべてのメニューとフライアウトに付ける (後から作るものも含む)。
/// <list type="bullet">
/// <item>メニュー (<see cref="MenuFlyoutPresenter"/>): サブメニューの位置がずれないように拡大はせず、項目の文字の大きさを倍率に合わせる
/// (仕様 3 の 4)。開くたびに、そのウィンドウで実際に使っている倍率にする。</item>
/// <item>フライアウト (<see cref="FlyoutPresenter"/>): 中身を <see cref="PopupZoomPanel"/> で倍率どおりに拡大する (大きさも倍率に
/// 合わせて測るので、位置の計算と切り取りがずれない)。</item>
/// </list>
/// </summary>
public static class PopupZoom
{
    /// <summary>メニューの項目の文字の大きさを倍率に合わせる (MenuFlyoutPresenter の暗黙のスタイルから付ける。値は true)。</summary>
    public static readonly DependencyProperty FollowMenuProperty = DependencyProperty.RegisterAttached(
        "FollowMenu", typeof(bool), typeof(PopupZoom), new PropertyMetadata(false, OnFollowMenuChanged));

    public static bool GetFollowMenu(DependencyObject element) => (bool)element.GetValue(FollowMenuProperty);

    public static void SetFollowMenu(DependencyObject element, bool value) => element.SetValue(FollowMenuProperty, value);

    /// <summary>
    /// ポップアップに出る項目 (コンボボックスのドロップダウンの項目) の文字の大きさを倍率に合わせる (ComboBoxItem の暗黙のスタイル
    /// から付ける)。ドロップダウンを開くたびに (表示に入るたびに) そのウィンドウの倍率にする。
    /// </summary>
    public static readonly DependencyProperty FollowItemProperty = DependencyProperty.RegisterAttached(
        "FollowItem", typeof(bool), typeof(PopupZoom), new PropertyMetadata(false, OnFollowItemChanged));

    public static bool GetFollowItem(DependencyObject element) => (bool)element.GetValue(FollowItemProperty);

    public static void SetFollowItem(DependencyObject element, bool value) => element.SetValue(FollowItemProperty, value);

    /// <summary>ツールチップの文字の大きさと最大の幅を倍率に合わせる (ToolTip の暗黙のスタイルから付ける)。開くたびに合わせる。</summary>
    public static readonly DependencyProperty FollowToolTipProperty = DependencyProperty.RegisterAttached(
        "FollowToolTip", typeof(bool), typeof(PopupZoom), new PropertyMetadata(false, OnFollowToolTipChanged));

    public static bool GetFollowToolTip(DependencyObject element) => (bool)element.GetValue(FollowToolTipProperty);

    public static void SetFollowToolTip(DependencyObject element, bool value) => element.SetValue(FollowToolTipProperty, value);

    private static void OnFollowItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Control item && e.NewValue is true)
        {
            item.Loading += (_, _) => ApplyToItem(item);
            item.Loaded += (_, _) => ApplyToItem(item);
        }
    }

    private static void ApplyToItem(Control item)
    {
        double factor = ScreenZoom.AppliedFor(item.XamlRoot);
        ApplyFont(item, factor, (double)Application.Current.Resources["ControlContentThemeFontSize"] * factor);
    }

    private static void OnFollowToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolTip tip && e.NewValue is true)
        {
            tip.Opened += (_, _) => ApplyToToolTip(tip);
        }
    }

    /// <summary>ツールチップの文字の大きさ (倍率 × 既定の大きさ) と最大の幅。100% では、変えたものを元に戻す。</summary>
    public static void ApplyToToolTip(ToolTip tip)
    {
        double factor = ScreenZoom.AppliedFor(tip.XamlRoot);
        double fontSize = Application.Current.Resources.TryGetValue("ToolTipContentThemeFontSize", out object? size) && size is double s ? s : 12;
        bool zoomed = (bool)tip.GetValue(ZoomedProperty);
        ApplyFont(tip, factor, fontSize * factor);
        if (factor != 1)
        {
            double maxWidth = Application.Current.Resources.TryGetValue("ToolTipMaxWidth", out object? width) && width is double w ? w : 320;
            tip.MaxWidth = maxWidth * factor;
        }
        else if (zoomed)
        {
            tip.ClearValue(FrameworkElement.MaxWidthProperty);
        }
    }

    /// <summary>このクラスが文字の大きさを変えた項目の印 (100% に戻すときに、変えたものだけを元に戻す)。</summary>
    private static readonly DependencyProperty ZoomedProperty = DependencyProperty.RegisterAttached(
        "Zoomed", typeof(bool), typeof(PopupZoom), new PropertyMetadata(false));

    private static void OnFollowMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ItemsControl presenter && e.NewValue is true)
        {
            // 測る前 (Loading) に文字の大きさを決める (開いた後に大きさが変わると、位置がずれるため)。Loaded は念のため。
            presenter.Loading += (_, _) => ApplyToMenu(presenter);
            presenter.Loaded += (_, _) => ApplyToMenu(presenter);
        }
    }

    /// <summary>メニューの項目の文字の大きさ (倍率 × 既定の大きさ)。100% では、変えたものを元に戻す。</summary>
    public static void ApplyToMenu(ItemsControl presenter)
    {
        double factor = ScreenZoom.AppliedFor(presenter.XamlRoot);
        double fontSize = (double)Application.Current.Resources["ControlContentThemeFontSize"] * factor;
        foreach (Control item in presenter.Items.OfType<Control>())
        {
            ApplyFont(item, factor, fontSize);
        }
    }

    /// <summary>メニューの項目 1 つの文字の大きさ (メニューバーの項目の先読みなど、開く前の項目にも使う)。</summary>
    public static void ApplyFont(Control item, double factor, double fontSize)
    {
        if (factor == 1)
        {
            if ((bool)item.GetValue(ZoomedProperty))
            {
                item.ClearValue(Control.FontSizeProperty);
                item.ClearValue(ZoomedProperty);
            }

            return;
        }

        if (item.FontSize != fontSize)
        {
            item.FontSize = fontSize;
        }

        item.SetValue(ZoomedProperty, true);
    }
}

/// <summary>
/// 中身を画面全体の倍率で拡大して置くパネル (フライアウトの中身。<see cref="PopupZoom"/>)。中身は (大きさ ÷ 倍率) で測って配置し、
/// 自分の大きさは (中身の大きさ × 倍率) として返すので、フライアウトの位置の計算とポップアップの大きさが拡大後の大きさに合う。
/// 倍率はこのパネルのウィンドウ (XamlRoot) で実際に使っている倍率 (<see cref="ScreenZoom.AppliedFor"/>)。
/// </summary>
public sealed partial class PopupZoomPanel : Panel
{
    private double _applied = 1;

    public PopupZoomPanel()
    {
        // 測る前に、フライアウトの最小・最大の大きさを倍率に合わせる (既定の大きさのままだと、拡大した中身が切れる)。
        Loading += (_, _) => FitOwnerLimits();
    }

    /// <summary>今使っている倍率 (テスト用)。</summary>
    public double AppliedZoom => _applied;

    private double Factor => ScreenZoom.AppliedFor(XamlRoot);

    private void FitOwnerLimits()
    {
        if (VisualTreeHelper.GetParent(this) is not FrameworkElement owner)
        {
            return;
        }

        double factor = Factor;
        foreach ((DependencyProperty property, string key) in new[]
        {
            (FrameworkElement.MaxWidthProperty, "FlyoutThemeMaxWidth"), (FrameworkElement.MaxHeightProperty, "FlyoutThemeMaxHeight"),
            (FrameworkElement.MinWidthProperty, "FlyoutThemeMinWidth"), (FrameworkElement.MinHeightProperty, "FlyoutThemeMinHeight"),
        })
        {
            if (Application.Current.Resources.TryGetValue(key, out object? value) && value is double size && !double.IsInfinity(size))
            {
                if (factor == 1)
                {
                    owner.ClearValue(property);
                }
                else
                {
                    owner.SetValue(property, size * factor);
                }
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double zoom = Factor;
        var inner = new Size(Inner(availableSize.Width, zoom), Inner(availableSize.Height, zoom));
        double width = 0, height = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(inner);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width * zoom, height * zoom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double zoom = Factor;
        if (zoom != _applied)
        {
            _applied = zoom;
            RenderTransform = zoom == 1 ? null : new ScaleTransform { ScaleX = zoom, ScaleY = zoom };
        }

        // 文字と画像は (表示倍率 × ズームの倍率) で描く (ぼやけないように)。
        if (XamlRoot is { } root && zoom != 1)
        {
            foreach (UIElement child in Children)
            {
                try
                {
                    child.RasterizationScale = root.RasterizationScale * zoom;
                }
                catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    Services.AppLog.Warning($"PopupZoom: {ex.GetType().Name} {ex.Message}");
                }
            }
        }

        var rect = new Rect(0, 0, finalSize.Width / zoom, finalSize.Height / zoom);
        foreach (UIElement child in Children)
        {
            child.Arrange(rect);
        }

        return finalSize;
    }

    private static double Inner(double length, double zoom) => double.IsInfinity(length) ? length : length / zoom;
}
