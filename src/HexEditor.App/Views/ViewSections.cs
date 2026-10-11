using HexEditor.App.Services;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面「外観」の専用の区画: Hex 表示のフォント (UI-29 の仕様 2。インストールされているフォントの一覧から選ぶ) と配色
/// (UI-28。組み込みの配色と themes フォルダの配色から選ぶ)。値は settings.json の <c>view.font.family</c>・<c>view.colorScheme</c>。
/// </summary>
public static class ViewSections
{
    public static void Register()
    {
        SettingsSections.Register(new SettingsSection(SettingCategories.Appearance, "hexFont", "SetSection_HexFont", HexFont, 5)
        {
            SearchKeys = ["SetSearch_HexFont", "Set_view_font_family", "Set_view_colorScheme"],
        });

        // 表示プリセットの管理 (VIEW-42 の仕様 6・7)。
        MainWindow.RegisterPresetSection();
    }

    private static FrameworkElement HexFont(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };

        // フォント: 既定は等幅フォントだけ。「すべてのフォントを表示」で等幅でないものも出す (UI-29 の仕様 2)。
        string current = App.Settings.GetString(ViewOptions.FontFamilyKey, string.Empty);
        var fonts = new ComboBox { Header = Loc.Get("Set_view_font_family"), MinWidth = 280 };
        AutomationProperties.SetAutomationId(fonts, "SettingControl_view.font.family");
        AutomationProperties.SetName(fonts, Loc.Get("Set_view_font_family"));
        var all = new CheckBox { Content = Loc.Get("Settings_ShowAllFonts") };
        AutomationProperties.SetAutomationId(all, "Settings_ShowAllFonts");
        var missing = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(missing, "Settings_FontMissing");
        bool filling = false;

        void FillFonts()
        {
            filling = true;
            fonts.Items.Clear();
            fonts.Items.Add(new ComboBoxItem { Content = Loc.Get("Settings_FontDefault"), Tag = string.Empty });
            foreach (string name in FontCatalog.ListForSettings(all.IsChecked == true))
            {
                // フォント名はそのフォントで表示する (UI-29 の「画面」)。
                var item = new ComboBoxItem { Content = name, Tag = name, FontFamily = new FontFamily(name) };
                AutomationProperties.SetName(item, name);
                fonts.Items.Add(item);
            }

            string selected = App.Settings.GetString(ViewOptions.FontFamilyKey, string.Empty);
            int index = fonts.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => string.Equals((string)i.Tag, selected, StringComparison.OrdinalIgnoreCase));
            fonts.SelectedIndex = Math.Max(0, index);
            bool notFound = selected.Length > 0 && FontCatalog.IsReady && !FontCatalog.IsInstalled(selected);
            missing.Text = notFound ? Loc.Format("Settings_FontMissing", selected) : string.Empty;
            missing.Visibility = notFound ? Visibility.Visible : Visibility.Collapsed;
            filling = false;
        }

        fonts.SelectionChanged += (_, _) =>
        {
            if (!filling && fonts.SelectedItem is ComboBoxItem { Tag: string name })
            {
                App.Settings.SetString(ViewOptions.FontFamilyKey, name, string.Empty);
                window.ApplyEditorSettings();
            }
        };
        all.Checked += (_, _) => FillFonts();
        all.Unchecked += (_, _) => FillFonts();
        all.IsChecked = current.Length > 0 && FontCatalog.IsReady && FontCatalog.Families().Any(f => f.Name == current && !f.Monospaced);
        FillFonts();
        panel.Children.Add(fonts);
        panel.Children.Add(all);
        panel.Children.Add(missing);

        // プレビュー (UI-29 の仕様 8、UI-28 の仕様 3): フォント・大きさ・行間・配色の変更を即座に映す。
        var previewHeader = new TextBlock { Text = Loc.Get("Settings_HexPreview") };
        var preview = new HexPreview { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(preview, "Settings_HexPreview");
        AutomationProperties.SetName(preview, Loc.Get("Settings_HexPreview"));
        panel.Children.Add(previewHeader);
        panel.Children.Add(preview);
        void ApplyFont()
        {
            string family = FontCatalog.Resolve(App.Settings.GetString(ViewOptions.FontFamilyKey, string.Empty) is { Length: > 0 } f ? f : null, out _);
            double points = Math.Clamp(Math.Round(Number(ViewOptions.FontSizeKey, ViewOptions.DefaultFontPoints) * 2) / 2, 6, 72);
            preview.SetFont(family, points, Math.Clamp(Number(ViewOptions.LineHeightKey, 1.2), 1.0, 2.0));
        }

        ApplyFont();

        // 配色 (UI-28)。
        ColorSchemeSection.Build(window, preview, panel);

        // フォントの設定は設定画面の別の項目 (大きさ・行間) や設定ファイルの編集でも変わるので、変わったらプレビューを描き直す。
        // 設定はアプリ全体で 1 つなので、設定画面から外れたら購読をやめる。
        Action<IReadOnlyCollection<string>> changed = keys =>
        {
            if (keys.Any(k => k.StartsWith("view.font.", StringComparison.Ordinal)))
            {
                panel.DispatcherQueue.TryEnqueue(ApplyFont);
            }
        };
        panel.Loaded += (_, _) =>
        {
            App.Settings.Changed -= changed;
            App.Settings.Changed += changed;
        };
        panel.Unloaded += (_, _) => App.Settings.Changed -= changed;
        return panel;
    }

    private static double Number(string key, double defaultValue) =>
        App.Settings.GetNode(key) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out double d) ? d : defaultValue;
}
