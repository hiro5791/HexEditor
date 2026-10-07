using HexEditor.Core.Settings;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace HexEditor.App.Services;

/// <summary>
/// 外観の設定の反映: テーマ (UI-26)、アクセントカラーと背景素材 (UI-27)。ハイコントラストのときは Windows の設定に従い、
/// アクセントカラーの指定と背景素材は使わない。
/// </summary>
public static class Appearance
{
    public const string ThemeKey = "ui.theme";
    public const string AccentKey = "ui.accentColor";
    public const string BackdropKey = "ui.backdrop";

    public const string ThemeDefault = "system";
    public const string AccentDefault = "system";
    public const string BackdropDefault = "mica";

    private static readonly AccessibilitySettings Accessibility = new();

    public static bool IsHighContrast => Accessibility.HighContrast;

    public static ElementTheme ThemeOf(SettingsStore settings) => settings.GetString(ThemeKey, ThemeDefault) switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>ウィンドウにテーマと背景素材を反映する。設定が変わったとき・コントラストテーマが変わったときにも呼ぶ。</summary>
    public static void Apply(Window window, FrameworkElement root, SettingsStore settings)
    {
        ElementTheme theme = ThemeOf(settings);
        root.RequestedTheme = theme;
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        // 背景素材 (UI-27 の仕様 2・5)。Mica が使えない環境では WinUI が不透明の色にする (仕様 4)。
        string backdrop = settings.GetString(BackdropKey, BackdropDefault);
        window.SystemBackdrop = IsHighContrast || backdrop == "none" ? null
            : new MicaBackdrop { Kind = backdrop == "micaAlt" ? MicaKind.BaseAlt : MicaKind.Base };

        // 設定した背景素材をログに残す (UI-27 の確認用。変わったときだけ)。
        string applied = window.SystemBackdrop is MicaBackdrop mica ? "Mica" + (mica.Kind == MicaKind.BaseAlt ? "Alt" : string.Empty) : "None";
        if (applied != _lastBackdrop)
        {
            _lastBackdrop = applied;
            AppLog.Info($"Backdrop: {applied}");
        }
    }

    private static string? _lastBackdrop;

    /// <summary>ウィンドウに設定した背景素材 ("Mica"、"MicaAlt"、"None")。</summary>
    public static string? AppliedBackdrop => _lastBackdrop;

    /// <summary>
    /// アクセントカラーの指定 (UI-27 の仕様 1)。<c>SystemAccentColor</c> と派生色をアプリ内で上書きする。リソースの参照が
    /// 作られる前 (最初のウィンドウを作る前) に呼ぶ。
    /// </summary>
    public static void ApplyAccent(SettingsStore settings)
    {
        string value = settings.GetString(AccentKey, AccentDefault);
        if (IsHighContrast || !TryParseColor(value, out Color accent))
        {
            return;
        }

        ResourceDictionary resources = Application.Current.Resources;
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Lighten(accent, 0.25);
        resources["SystemAccentColorLight2"] = Lighten(accent, 0.5);
        resources["SystemAccentColorLight3"] = Lighten(accent, 0.75);
        resources["SystemAccentColorDark1"] = Darken(accent, 0.25);
        resources["SystemAccentColorDark2"] = Darken(accent, 0.5);
        resources["SystemAccentColorDark3"] = Darken(accent, 0.75);
    }

    /// <summary><c>#RRGGBB</c> を読む。</summary>
    public static bool TryParseColor(string text, out Color color)
    {
        color = default;
        if (text.Length != 7 || text[0] != '#' || !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return false;
        }

        color = Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static readonly UISettings UiSettings = new();

    /// <summary>
    /// アプリモード・コントラストテーマの切り替えを知らせる (UI スレッド以外から呼ばれる)。デスクトップアプリでは
    /// AccessibilitySettings.HighContrastChanged を購読できないため、色の設定の変更で代用する。
    /// </summary>
    public static event Action? SystemColorsChanged
    {
        add => UiSettings.ColorValuesChanged += (_, _) => value?.Invoke();
        remove { }
    }

    /// <summary>白に向けて t の割合だけ明るくする (派生色の計算。UI の色の直書きではない)。</summary>
    private static Color Lighten(Color c, double t) => Color.FromArgb(
        c.A, (byte)Math.Round(c.R + ((255 - c.R) * t)), (byte)Math.Round(c.G + ((255 - c.G) * t)), (byte)Math.Round(c.B + ((255 - c.B) * t)));

    /// <summary>黒に向けて t の割合だけ暗くする。</summary>
    private static Color Darken(Color c, double t) => Color.FromArgb(
        c.A, (byte)Math.Round(c.R * (1 - t)), (byte)Math.Round(c.G * (1 - t)), (byte)Math.Round(c.B * (1 - t)));
}
