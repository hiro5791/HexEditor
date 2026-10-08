using HexEditor.App.Services;
using HexEditor.Core.Expressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>編集のダイアログ (EDIT-04・14・15・25・26・29・30) で共通に使う部品。</summary>
internal static class DialogParts
{
    public static FontFamily Mono { get; } = new("Cascadia Mono, Consolas");

    /// <summary>見出し付きの入力欄。</summary>
    public static TextBox Field(string automationId, string header, string text = "", bool monospace = true)
    {
        var box = new TextBox
        {
            Header = header,
            Text = text,
            FlowDirection = FlowDirection.LeftToRight,
            FontFamily = monospace ? Mono : FontFamily.XamlAutoFontFamily,
            MinWidth = 220,
        };
        AutomationProperties.SetAutomationId(box, automationId);
        AutomationProperties.SetName(box, header);
        return box;
    }

    /// <summary>入力欄の下の小さな説明 (解釈結果・エラー)。</summary>
    public static TextBlock Caption(string automationId, bool monospace = false)
    {
        var text = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
        };
        if (monospace)
        {
            text.FontFamily = Mono;
        }

        AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    public static CheckBox Check(string automationId, string content, bool isChecked)
    {
        var box = new CheckBox { Content = content, IsChecked = isChecked };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    public static RadioButton Radio(string automationId, string content, string group, bool isChecked)
    {
        var radio = new RadioButton { Content = content, GroupName = group, IsChecked = isChecked };
        AutomationProperties.SetAutomationId(radio, automationId);
        return radio;
    }

    public static ComboBox Combo(string automationId, string header, IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { Header = header, MinWidth = 220 };
        foreach (string item in items)
        {
            combo.Items.Add(item);
        }

        combo.SelectedIndex = selected;
        AutomationProperties.SetAutomationId(combo, automationId);
        AutomationProperties.SetName(combo, header);
        return combo;
    }

    /// <summary>入力欄を赤枠にする / 戻す (色は ThemeResource のブラシ)。</summary>
    public static void MarkInvalid(Control box, bool invalid)
    {
        if (invalid)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
        else
        {
            box.ClearValue(Control.BorderBrushProperty);
        }
    }

    /// <summary>入力式の誤りの説明 (移動バーと同じ文言)。</summary>
    public static string ExpressionError(ExpressionException error) =>
        Loc.Format("GoTo_Error_" + error.Error, error.Detail) + " " + Loc.Format("Dialog_ErrorAt", error.Position + 1);

    /// <summary>値の解釈結果「= 1,048,560 (0xFFFF0)」(00-overview 9 章)。</summary>
    public static string Interpretation(long value) =>
        $"= {Core.View.StatusFormat.Number(value, System.Globalization.CultureInfo.CurrentCulture)} ({Core.View.StatusFormat.Hex(value)})";

    /// <summary>Hex のプレビュー (`DE AD BE EF …`)。</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Core.Clipboard.HexText.Format(bytes);

    /// <summary>入力式を評価する (空欄は null、誤りは例外の内容を返す)。</summary>
    public static bool TryEvaluate(string text, IExpressionContext context, out long value, out ExpressionException? error,
        DefaultRadix radix = DefaultRadix.Hexadecimal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            error = new ExpressionException(Core.Expressions.ExpressionError.Empty, 0);
            return false;
        }

        return ExpressionEvaluator.TryEvaluate(text, context, out value, out error, radix);
    }

    /// <summary>アプリの他のダイアログと同じ設定の ContentDialog。</summary>
    public static ContentDialog Dialog(FrameworkElement root, string automationId, string title, object content, string primary,
        string? secondary = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            RequestedTheme = root.ActualTheme,
            FlowDirection = root.FlowDirection,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (secondary is not null)
        {
            dialog.SecondaryButtonText = secondary;
        }

        AutomationProperties.SetAutomationId(dialog, automationId);
        return dialog;
    }
}
