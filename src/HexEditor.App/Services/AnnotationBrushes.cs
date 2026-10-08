using HexEditor.Core.Bookmarks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Services;

/// <summary>
/// ブックマークとインスペクタの強調の色 (Themes/AnnotationPalette.xaml の ThemeResource)。要素の今のテーマ (ライト・ダーク・
/// ハイコントラスト) の辞書から取る。任意の色 (INSP-24 の仕様 4) は利用者のデータなので、テーマに合わせて明度を調整して作る。
/// </summary>
public static class AnnotationBrushes
{
    private static readonly Dictionary<(string Theme, uint Rgb, bool Mark), SolidColorBrush> Custom = [];

    /// <summary>テーマの辞書の名前 (ハイコントラストを優先)。</summary>
    public static string ThemeOf(FrameworkElement scope, bool highContrast) =>
        highContrast ? "HighContrast" : scope.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";

    /// <summary>ThemeResource の色を、要素のテーマで取る。</summary>
    public static Brush Get(string key, FrameworkElement scope, bool highContrast)
    {
        string theme = ThemeOf(scope, highContrast);
        foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out object? found) && found is ResourceDictionary themed
                && themed.TryGetValue(key, out object? value) && value is Brush brush)
            {
                return brush;
            }
        }

        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>Hex 表示の背景 (文字が読めるよう淡い色)。</summary>
    public static Brush Background(BookmarkColor color, FrameworkElement scope, bool highContrast) =>
        color.IsCustom && !highContrast ? Adjusted(color.Rgb, ThemeOf(scope, false), mark: false)
            : Get($"BookmarkBackground{Index(color)}Brush", scope, highContrast);

    /// <summary>色見本・目印・ハイコントラストの枠線の色。</summary>
    public static Brush Mark(BookmarkColor color, FrameworkElement scope, bool highContrast) =>
        color.IsCustom && !highContrast ? Adjusted(color.Rgb, ThemeOf(scope, false), mark: true)
            : Get($"BookmarkMark{Index(color)}Brush", scope, highContrast);

    /// <summary>ハイコントラストの枠線の形: 1〜4 番目は実線、5〜8 番目は破線 (色と形の組み合わせで 8 色を区別する)。</summary>
    public static IReadOnlyList<double>? Dash(BookmarkColor color) => Index(color) > 4 ? [3, 2] : null;

    /// <summary>任意の色は、色の一覧の 1 番目と同じ形で描く。</summary>
    private static int Index(BookmarkColor color) => color.IsCustom ? 1 : color.PaletteIndex;

    /// <summary>
    /// 任意の色の明度を調整する: 背景はライトでは白に、ダークでは黒に寄せて文字を読めるようにする。見本はダークでは少し明るくする。
    /// </summary>
    private static SolidColorBrush Adjusted(uint rgb, string theme, bool mark)
    {
        if (Custom.TryGetValue((theme, rgb, mark), out SolidColorBrush? cached))
        {
            return cached;
        }

        byte r = (byte)(rgb >> 16);
        byte g = (byte)(rgb >> 8);
        byte b = (byte)rgb;
        bool dark = theme == "Dark";
        (double weight, double target) = (mark, dark) switch
        {
            (false, false) => (0.35, 255.0),
            (false, true) => (0.4, 0.0),
            (true, true) => (0.8, 255.0),
            _ => (1.0, 0.0),
        };
        byte Mix(byte c) => (byte)Math.Round(c * weight + target * (1 - weight));
        var brush = new SolidColorBrush(new Windows.UI.Color { A = 255, R = Mix(r), G = Mix(g), B = Mix(b) });
        Custom[(theme, rgb, mark)] = brush;
        return brush;
    }

    /// <summary>ColorPicker の色を保存用の RGB にする。</summary>
    public static uint ToRgb(Windows.UI.Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);

    public static Windows.UI.Color FromRgb(uint rgb) => new() { A = 255, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb };
}
