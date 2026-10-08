using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Foundation;

namespace HexEditor.App.Controls;

/// <summary>
/// 画面全体のズーム (UI-08 の仕様 3)。実装の方法 (仕様 3 の 4) は「ルート要素の拡大」とする: ウィンドウの中身を
/// <see cref="ZoomHost"/> に入れ、中身を (ウィンドウの大きさ ÷ 倍率) で配置してから倍率で拡大する。文字と画像は
/// <see cref="UIElement.RasterizationScale"/> を (表示倍率 × ズームの倍率) にして描くので、ぼやけない。
/// ウィンドウの外に開くダイアログは、暗黙のスタイル (App.xaml) の <see cref="FollowProperty"/> で同じ倍率に拡大する。
/// メニューの項目は文字の大きさを倍率に合わせる (サブメニューの位置がずれないように、拡大はしない)。
/// </summary>
public static class ScreenZoom
{
    /// <summary>今の倍率 (1.0 が 100%)。全ウィンドウで共通 (設定 view.zoom.ui)。</summary>
    public static double Factor { get; private set; } = 1;

    /// <summary>倍率が変わった。</summary>
    public static event Action? Changed;

    public static void SetFactor(double factor)
    {
        factor = Math.Clamp(factor, 0.5, 4);
        if (factor == Factor)
        {
            return;
        }

        Factor = factor;
        Changed?.Invoke();
    }

    /// <summary>ダイアログを今の倍率で拡大する (暗黙のスタイルから付ける。値は true)。</summary>
    public static readonly DependencyProperty FollowProperty = DependencyProperty.RegisterAttached(
        "Follow", typeof(bool), typeof(ScreenZoom), new PropertyMetadata(false, OnFollowChanged));

    public static bool GetFollow(DependencyObject element) => (bool)element.GetValue(FollowProperty);

    public static void SetFollow(DependencyObject element, bool value) => element.SetValue(FollowProperty, value);

    private static void OnFollowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += (_, _) => ApplyCentered(element);
        }
    }

    private static readonly ConditionalWeakTable<XamlRoot, ZoomHost> Hosts = new();

    /// <summary>ウィンドウ (XamlRoot) で実際に使っている倍率 (ウィンドウが狭くて下げた場合を含む)。</summary>
    public static double AppliedFor(XamlRoot? root) =>
        root is not null && Hosts.TryGetValue(root, out ZoomHost? host) ? host.AppliedZoom : Factor;

    internal static void Register(XamlRoot root, ZoomHost host) => Hosts.AddOrUpdate(root, host);

    /// <summary>要素を中心を基準に、そのウィンドウの倍率で拡大する (ダイアログ)。100% なら何もしない。</summary>
    public static void ApplyCentered(FrameworkElement element)
    {
        double factor = AppliedFor(element.XamlRoot);
        if (factor == 1)
        {
            return;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new ScaleTransform { ScaleX = factor, ScaleY = factor };
        if (element.XamlRoot is { } root)
        {
            try
            {
                element.RasterizationScale = root.RasterizationScale * factor;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                Services.AppLog.Warning($"ScreenZoom: {ex.GetType().Name} {ex.Message}");
            }
        }
    }
}

/// <summary>
/// 中身を倍率で拡大して置くパネル (画面全体のズーム。UI-08 の仕様 3)。中身はパネルの大きさ ÷ 倍率で配置し、パネル自身を倍率で
/// 拡大する。倍率 1 では、中身をそのまま置くのと同じ。
/// 拡大した結果、中身がウィンドウの最小サイズ (<see cref="MinContentSize"/>。UI-01 の仕様 4) より小さくなる場合は、最小サイズを
/// 下回らない倍率まで下げて表示する (ウィンドウを広げると、指定の倍率まで大きくなる)。
/// </summary>
public sealed partial class ZoomHost : Panel
{
    private double _zoom = 1;
    private double _applied = 1;
    private bool _rasterizationSet;

    public ZoomHost()
    {
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root)
            {
                root.Changed += (_, _) => UpdateRasterization();
                ScreenZoom.Register(root, this);
            }

            UpdateRasterization();
        };
    }

    /// <summary>指定の倍率 (1.0 が 100%)。</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (value == _zoom)
            {
                return;
            }

            _zoom = value;
            InvalidateMeasure();
        }
    }

    /// <summary>実際に使っている倍率 (ウィンドウが狭くて下げた場合は <see cref="Zoom"/> より小さい)。</summary>
    public double AppliedZoom => _applied;

    /// <summary>中身を置く最小の大きさ (epx。ウィンドウの最小サイズ。UI-01 の仕様 4)。</summary>
    public Size MinContentSize { get; set; }

    /// <summary>倍率が変わった (実際に使う倍率)。</summary>
    public event Action? AppliedZoomChanged;

    /// <summary>ウィンドウの大きさ <paramref name="size"/> で使う倍率。</summary>
    private double Effective(Size size)
    {
        double zoom = _zoom;
        if (zoom > 1 && MinContentSize.Width > 0 && !double.IsInfinity(size.Width) && !double.IsInfinity(size.Height) && size.Width > 0 && size.Height > 0)
        {
            zoom = Math.Max(1, Math.Min(zoom, Math.Min(size.Width / MinContentSize.Width, size.Height / MinContentSize.Height)));
        }

        return zoom;
    }

    private void Apply(double zoom)
    {
        if (zoom == _applied)
        {
            return;
        }

        _applied = zoom;
        RenderTransform = zoom == 1 ? null : new ScaleTransform { ScaleX = zoom, ScaleY = zoom };
        UpdateRasterization();
        AppliedZoomChanged?.Invoke();
    }

    /// <summary>文字と画像を (表示倍率 × ズームの倍率) で描く (ぼやけないように)。</summary>
    private void UpdateRasterization()
    {
        if (XamlRoot is not { } root)
        {
            return;
        }

        // 100% のままなら設定しない (表示倍率の変更に XAML がそのまま追従する)。
        if (_applied == 1 && !_rasterizationSet)
        {
            return;
        }

        _rasterizationSet = true;
        foreach (UIElement child in Children)
        {
            child.RasterizationScale = root.RasterizationScale * _applied;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double zoom = Effective(availableSize);
        var inner = new Size(Inner(availableSize.Width, zoom), Inner(availableSize.Height, zoom));
        foreach (UIElement child in Children)
        {
            child.Measure(inner);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double zoom = Effective(finalSize);
        Apply(zoom);
        var rect = new Rect(0, 0, Inner(finalSize.Width, zoom), Inner(finalSize.Height, zoom));
        foreach (UIElement child in Children)
        {
            child.Arrange(rect);
        }

        return finalSize;
    }

    private static double Inner(double length, double zoom) => double.IsInfinity(length) ? length : length / zoom;
}
