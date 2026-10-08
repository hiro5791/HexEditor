using HexEditor.App.Services;
using HexEditor.Core.Settings;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面「外観」の専用の区画: Hex 表示のフォント (UI-29 の仕様 2。インストールされているフォントの一覧から選ぶ) と配色
/// (UI-28。組み込みの配色と themes フォルダの配色から選ぶ)。値は settings.json の <c>view.font.family</c>・<c>view.colorScheme</c>。
/// </summary>
public static class ViewSections
{
    public static void Register() =>
        SettingsSections.Register(new SettingsSection(SettingCategories.Appearance, "hexFont", "SetSection_HexFont", HexFont, 5)
        {
            SearchKeys = ["SetSearch_HexFont", "Set_view_font_family", "Set_view_colorScheme"],
        });

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
                fonts.Items.Add(new ComboBoxItem { Content = name, Tag = name });
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

        // 配色
        var schemes = new ComboBox { Header = Loc.Get("Set_view_colorScheme"), MinWidth = 280 };
        AutomationProperties.SetAutomationId(schemes, "SettingControl_view.colorScheme");
        AutomationProperties.SetName(schemes, Loc.Get("Set_view_colorScheme"));
        string scheme = App.Settings.GetString(ViewOptions.ColorSchemeKey, ColorScheme.DefaultName);
        foreach (ColorScheme s in new ColorSchemeStore(App.Settings.Folder).All())
        {
            var item = new ComboBoxItem { Content = s.BuiltIn ? Loc.Get("Scheme_" + s.Name) : s.Name, Tag = s.Name };
            schemes.Items.Add(item);
            if (string.Equals(s.Name, scheme, StringComparison.OrdinalIgnoreCase))
            {
                schemes.SelectedItem = item;
            }
        }

        schemes.SelectionChanged += (_, _) =>
        {
            if (schemes.SelectedItem is ComboBoxItem { Tag: string name })
            {
                _ = window.Commands.ExecuteAsync("view.colorScheme", name);
            }
        };
        panel.Children.Add(schemes);
        return panel;
    }
}
