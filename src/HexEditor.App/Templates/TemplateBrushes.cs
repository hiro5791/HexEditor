using HexEditor.App.Services;
using HexEditor.Core.Templates.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HexEditor.App.Templates;

/// <summary>
/// テンプレートの色分けの色 (TPL-23 の仕様 2・3、TPL-13 の <c>color</c> 属性)。色はテーマのリソース (Themes/AnnotationPalette.xaml の
/// <c>TemplateAccentNColor</c>、<c>TemplateRedColor</c> など) を、設定の不透明度 (既定 35%) で Hex ビューの背景に重ねた色にする。
/// <c>#RRGGBB</c> は利用者のデータの色なので、そのまま重ねる。ハイコントラストでは使わない (境目の線にする)。
/// </summary>
internal static class TemplateBrushes
{
    private static readonly Dictionary<(string Theme, string Key, int Opacity), SolidColorBrush> Cache = [];

    /// <summary>色のリソースの名前 (<c>TemplateAccent3Color</c>、<c>TemplateTealColor</c>)。<c>#RRGGBB</c> は null。</summary>
    public static string? ResourceKey(TemplateColor color) => color.Kind switch
    {
        TemplateColorKind.Accent => "Template" + color.Name + "Color",
        TemplateColorKind.Named => "Template" + color.Name + "Color",
        _ => null,
    };

    /// <summary>色見本 (結果ツリーの名前の左。重ねる前の色)。</summary>
    public static Brush Swatch(TemplateColor color, FrameworkElement scope)
    {
        string theme = AnnotationBrushes.ThemeOf(scope, false);
        return Get(theme, "swatch:" + color.Name, 100, () => Base(color, theme));
    }

    /// <summary>Hex ビューの背景の塗り。</summary>
    public static Brush Background(TemplateColor color, FrameworkElement scope, double opacity)
    {
        string theme = AnnotationBrushes.ThemeOf(scope, false);
        int percent = (int)Math.Round(opacity * 100);
        return Get(theme, color.Name, percent, () =>
        {
            Color fg = Base(color, theme);
            Color bg = BackgroundColor(scope);
            uint rgb = TemplateColoring.Blend((uint)(fg.R << 16 | fg.G << 8 | fg.B), (uint)(bg.R << 16 | bg.G << 8 | bg.B), opacity);
            return Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        });
    }

    /// <summary>ハイコントラストの境目の線・追従の枠線・デバッガの <c>$</c> の印の色。</summary>
    public static Brush Line(string key, FrameworkElement scope, bool highContrast) => AnnotationBrushes.Get(key, scope, highContrast);

    private static SolidColorBrush Get(string theme, string key, int opacity, Func<Color> make)
    {
        if (!Cache.TryGetValue((theme, key, opacity), out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(make());
            Cache[(theme, key, opacity)] = brush;
        }

        return brush;
    }

    private static Color Base(TemplateColor color, string theme)
    {
        if (ResourceKey(color) is not { } key)
        {
            uint rgb = color.Rgb;
            return Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return ThemeColor(key, theme) ?? ThemeColor("TemplateGrayColor", theme) ?? default;
    }

    /// <summary>テーマ (<c>Light</c> / <c>Dark</c> / <c>HighContrast</c>) の色のリソース。そのテーマにない色は null。</summary>
    public static Color? ThemeColor(string key, string theme)
    {
        foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out object? found) && found is ResourceDictionary themed
                && themed.TryGetValue(key, out object? value) && value is Color c)
            {
                return c;
            }
        }

        return null;
    }

    private static Color BackgroundColor(FrameworkElement scope) =>
        AnnotationBrushes.Get("SolidBackgroundFillColorBaseBrush", scope, false) is SolidColorBrush b ? b.Color
        : ThemeColor("TplHexBackgroundColor", AnnotationBrushes.ThemeOf(scope, false)) ?? default;
}
