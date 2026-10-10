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

    /// <summary>入力式を 128 bit で評価する (符号なし 64 bit の値も書ける。データ演算のオペランド)。</summary>
    public static bool TryEvaluateWide(string text, IExpressionContext context, out Int128 value, out ExpressionException? error,
        DefaultRadix radix = DefaultRadix.Hexadecimal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            error = new ExpressionException(Core.Expressions.ExpressionError.Empty, 0);
            return false;
        }

        return ExpressionEvaluator.TryEvaluateWide(text, context, out value, out error, radix);
    }

    // ---- 入力履歴 (EDIT-04 の仕様 10) ----

    /// <summary>最後に付けた入力履歴 (入力欄の AutomationId ごと。テスト用の読み出し)。</summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> s_histories = [];

    /// <summary>
    /// 入力履歴の候補を出す入力欄: 右に「履歴」ボタンを置き、押すか Alt+↓ で直近の入力の一覧を開く。選ぶと入力欄に入れる。
    /// 履歴はアプリの状態 (<paramref name="stateKey"/>) から読む。確定したときに <see cref="SaveHistory"/> で加える。
    /// </summary>
    public static FrameworkElement WithHistory(TextBox box, string stateKey)
    {
        IReadOnlyList<string> items = Core.Editing.InputHistory.Parse(AppState.GetString(stateKey, string.Empty));
        string id = AutomationProperties.GetAutomationId(box);
        s_histories[id] = items;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (string item in items)
        {
            var entry = new MenuFlyoutItem { Text = item, FontFamily = box.FontFamily, FlowDirection = FlowDirection.LeftToRight };
            entry.Click += (_, _) =>
            {
                box.Text = item;
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.Text.Length;
            };
            menu.Items.Add(entry);
        }

        string name = Loc.Format("Dialog_History", box.Header as string ?? string.Empty);
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = items.Count > 0,
            Flyout = menu,
        };
        AutomationProperties.SetAutomationId(button, id + "_History");
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name + " (Alt+↓)");
        box.KeyDown += (_, e) =>
        {
            bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == Windows.System.VirtualKey.Down && alt && items.Count > 0)
            {
                menu.ShowAt(box);
                e.Handled = true;
            }
        };
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    /// <summary>入力欄の今の文字を履歴の先頭に加える (確定したとき)。</summary>
    public static void SaveHistory(TextBox box, string stateKey) =>
        AppState.SetString(stateKey, Core.Editing.InputHistory.Serialize(
            Core.Editing.InputHistory.Push(Core.Editing.InputHistory.Parse(AppState.GetString(stateKey, string.Empty)), box.Text)));

    /// <summary>テスト用: 入力欄に最後に付けた履歴の候補。</summary>
    internal static IReadOnlyList<string> HistoryOf(string automationId) =>
        s_histories.TryGetValue(automationId, out IReadOnlyList<string>? items) ? items : [];

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
