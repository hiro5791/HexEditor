using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace HexEditor.App.Controls;

/// <summary>
/// アプリのテーマ (ライト・ダーク・ハイコントラスト) を Monaco のテーマにする (08 の AUTO-20 の仕様 2、07 の TPL-31 の仕様 3)。
/// 色は ThemeResource (ハイコントラストではシステムの色) から取り、直接は書かない。構文の色は TplSyntax*Color (テンプレートと共通)。
/// </summary>
internal static class MonacoTheme
{
    /// <summary>Monaco の字句の名前と、色のリソースの名前 (TplSyntax{名前}Color)。</summary>
    private static readonly (string Token, string Resource)[] TokenColors =
    [
        ("keyword", "Keyword"), ("type", "Type"), ("type.identifier", "Type"), ("number", "Number"), ("string", "String"),
        ("comment", "Comment"), ("meta", "Pragma"), ("attribute", "Attribute"), ("annotation", "Attribute"), ("function", "Function"),
        ("regexp", "String"),
    ];

    public static bool IsHighContrast => new AccessibilitySettings().HighContrast;

    /// <summary>WebView2 の既定の背景 (読み込み中のちらつきを抑える)。</summary>
    public static Color Background(FrameworkElement scope) =>
        IsHighContrast ? new UISettings().UIElementColor(UIElementType.Window) : BrushColor("SolidBackgroundFillColorBaseBrush", scope);

    public static JsonObject Build(FrameworkElement scope)
    {
        if (IsHighContrast)
        {
            return HighContrast();
        }

        string theme = AnnotationBrushes.ThemeOf(scope, false);
        bool dark = theme == "Dark";
        Color background = BrushColor("SolidBackgroundFillColorBaseBrush", scope);
        Color text = BrushColor("TextFillColorPrimaryBrush", scope);
        Color secondary = BrushColor("TextFillColorSecondaryBrush", scope);
        Color accent = BrushColor("AccentFillColorDefaultBrush", scope);
        Color widget = BrushColor("SolidBackgroundFillColorTertiaryBrush", scope);
        Color stroke = BrushColor("ControlStrokeColorDefaultBrush", scope);
        Color critical = BrushColor("SystemFillColorCriticalBrush", scope);
        Color caution = BrushColor("SystemFillColorCautionBrush", scope);
        Color line = BrushColor("SubtleFillColorSecondaryBrush", scope);
        Color selection = WithAlpha(accent, 0x55);
        var colors = new JsonObject
        {
            ["editor.background"] = Hex(background),
            ["editor.foreground"] = Hex(text),
            ["editorGutter.background"] = Hex(background),
            ["editorLineNumber.foreground"] = Hex(secondary),
            ["editorLineNumber.activeForeground"] = Hex(text),
            ["editorCursor.foreground"] = Hex(text),
            ["editor.selectionBackground"] = Hex(selection),
            ["editor.inactiveSelectionBackground"] = Hex(WithAlpha(accent, 0x2A)),
            ["editor.lineHighlightBackground"] = Hex(line),
            ["editor.lineHighlightBorder"] = Hex(WithAlpha(line, 0)),
            ["editorWidget.background"] = Hex(widget),
            ["editorWidget.foreground"] = Hex(text),
            ["editorWidget.border"] = Hex(stroke),
            ["editorSuggestWidget.background"] = Hex(widget),
            ["editorSuggestWidget.border"] = Hex(stroke),
            ["editorSuggestWidget.foreground"] = Hex(text),
            ["editorSuggestWidget.selectedBackground"] = Hex(selection),
            ["editorHoverWidget.background"] = Hex(widget),
            ["editorHoverWidget.border"] = Hex(stroke),
            ["editorError.foreground"] = Hex(critical),
            ["editorWarning.foreground"] = Hex(caution),
            ["editorBracketMatch.border"] = Hex(accent),
            ["focusBorder"] = Hex(accent),
        };
        var rules = new JsonArray();
        foreach ((string token, string resource) in TokenColors)
        {
            if (TemplateBrushes.ThemeColor("TplSyntax" + resource + "Color", theme) is { } c)
            {
                rules.Add(new JsonObject { ["token"] = token, ["foreground"] = Hex(c)[1..7] });
            }
        }

        return new JsonObject
        {
            ["base"] = dark ? "vs-dark" : "vs",
            ["colors"] = colors,
            ["rules"] = rules,
            ["breakpoint"] = Hex(critical),
            ["breakpointText"] = Hex(BrushColor("TextOnAccentFillColorPrimaryBrush", scope)),
            ["current"] = Hex(accent),
            ["currentLine"] = TemplateBrushes.ThemeColor("TplSyntaxDebugLineColor", theme) is { } d ? Hex(WithAlpha(d, 0x99)) : Hex(WithAlpha(accent, 0x38)),
            ["error"] = Hex(critical),
            ["warning"] = Hex(caution),
        };
    }

    /// <summary>
    /// ハイコントラスト: システムの色だけを使う (背景・文字・選択・リンク・無効の文字)。予約語・文字列・コメント・数値は互いに違う色にし、
    /// 太字・斜体も付ける (色だけで区別しない。TC-AUTO-20-03)。
    /// </summary>
    private static JsonObject HighContrast()
    {
        var ui = new UISettings();
        Color window = ui.UIElementColor(UIElementType.Window);
        Color text = ui.UIElementColor(UIElementType.WindowText);
        Color highlight = ui.UIElementColor(UIElementType.Highlight);
        Color highlightText = ui.UIElementColor(UIElementType.HighlightText);
        Color hotlight = ui.UIElementColor(UIElementType.Hotlight);
        Color gray = ui.UIElementColor(UIElementType.GrayText);
        Color face = ui.UIElementColor(UIElementType.ButtonFace);
        Color faceText = ui.UIElementColor(UIElementType.ButtonText);
        bool dark = Luminance(window) < 0.5;
        var colors = new JsonObject
        {
            ["editor.background"] = Hex(window),
            ["editor.foreground"] = Hex(text),
            ["editorGutter.background"] = Hex(window),
            ["editorLineNumber.foreground"] = Hex(text),
            ["editorLineNumber.activeForeground"] = Hex(hotlight),
            ["editorCursor.foreground"] = Hex(text),
            ["editor.selectionBackground"] = Hex(highlight),
            ["editor.selectionForeground"] = Hex(highlightText),
            ["editor.inactiveSelectionBackground"] = Hex(highlight),
            ["editor.lineHighlightBorder"] = Hex(hotlight),
            ["editorWidget.background"] = Hex(face),
            ["editorWidget.foreground"] = Hex(faceText),
            ["editorWidget.border"] = Hex(text),
            ["editorSuggestWidget.background"] = Hex(face),
            ["editorSuggestWidget.foreground"] = Hex(faceText),
            ["editorSuggestWidget.selectedBackground"] = Hex(highlight),
            ["editorSuggestWidget.selectedForeground"] = Hex(highlightText),
            ["editorHoverWidget.background"] = Hex(face),
            ["editorHoverWidget.foreground"] = Hex(faceText),
            ["editorHoverWidget.border"] = Hex(text),
            ["editorError.foreground"] = Hex(text),
            ["editorError.border"] = Hex(text),
            ["editorWarning.foreground"] = Hex(hotlight),
            ["editorBracketMatch.border"] = Hex(hotlight),
            ["contrastBorder"] = Hex(text),
            ["contrastActiveBorder"] = Hex(hotlight),
            ["focusBorder"] = Hex(hotlight),
        };
        var rules = new JsonArray
        {
            Rule("keyword", hotlight, "bold"),
            Rule("type", hotlight, null),
            Rule("type.identifier", hotlight, null),
            Rule("string", highlight, null),
            Rule("regexp", highlight, null),
            Rule("number", text, "underline"),
            Rule("comment", gray, "italic"),
            Rule("meta", highlight, "bold"),
            Rule("attribute", highlight, null),
            Rule("function", text, "bold"),
        };
        return new JsonObject
        {
            ["base"] = dark ? "hc-black" : "hc-light",
            ["colors"] = colors,
            ["rules"] = rules,
            ["breakpoint"] = Hex(text),
            ["breakpointText"] = Hex(window),
            ["current"] = Hex(hotlight),
            ["currentLine"] = Hex(WithAlpha(highlight, 0x55)),
            ["error"] = Hex(text),
            ["warning"] = Hex(hotlight),
        };
    }

    private static JsonObject Rule(string token, Color color, string? style)
    {
        var rule = new JsonObject { ["token"] = token, ["foreground"] = Hex(color)[1..7] };
        if (style is not null)
        {
            rule["fontStyle"] = style;
        }

        return rule;
    }

    private static Color BrushColor(string key, FrameworkElement scope) =>
        AnnotationBrushes.Get(key, scope, false) is SolidColorBrush b ? b.Color : default;

    private static Color WithAlpha(Color c, byte alpha) => Microsoft.UI.ColorHelper.FromArgb(alpha, c.R, c.G, c.B);

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    /// <summary><c>#RRGGBBAA</c> (Monaco の色の書式)。</summary>
    internal static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");
}
