using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>組み込みの専用の区画: キーボード (UI-18〜UI-21)、ツールバー (UI-04)、詳細 (UI-22 の仕様 8、UI-23〜UI-25)。</summary>
public static class BuiltInSections
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        SettingsSections.Register(new SettingsSection(SettingCategories.Keyboard, "keyboard", "SetSection_Keyboard", w => new KeyboardSettingsSection(w))
        {
            SearchKeys = ["SetSearch_Keyboard"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.General, "toolbar", "SetSection_Toolbar", Toolbar, 10)
        {
            SearchKeys = ["SetSearch_Toolbar"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Advanced, "data", "SetSection_Data", Advanced)
        {
            SearchKeys = ["SetSearch_Data"],
        });
        PackagingSections.Register();
    }

    private static Button CommandButton(MainWindow window, string id)
    {
        var button = new Button { Content = CommandService.DisplayName(id) };
        AutomationProperties.SetAutomationId(button, "SettingsAction_" + id);
        button.Click += async (_, _) => await window.Commands.ExecuteAsync(id);
        return button;
    }

    /// <summary>ツールバーの構成。設定にあるが存在しないコマンドの数を出す (UI-04 の「エラー」)。</summary>
    private static FrameworkElement Toolbar(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        (_, int missing) = ToolbarItems.Resolve(window.ToolbarItemIds, CommandService.Catalog);
        if (missing > 0)
        {
            var text = new TextBlock { Text = Loc.Format("Toolbar_Missing", missing), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(text, "Toolbar_MissingCount");
            panel.Children.Add(text);
        }

        panel.Children.Add(CommandButton(window, "view.customizeToolbar"));
        return panel;
    }

    /// <summary>詳細: データフォルダ・設定ファイル・すべての設定を既定に戻す・インポート / エクスポート。</summary>
    private static FrameworkElement Advanced(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        var path = new TextBlock { Text = App.Settings.Folder, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(path, "Settings_DataFolderPath");
        panel.Children.Add(path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (string id in new[] { "settings.openDataFolder", "settings.openFile", "settings.export", "settings.import", "settings.resetAll" })
        {
            buttons.Children.Add(CommandButton(window, id));
        }

        panel.Children.Add(new ScrollViewer { Content = buttons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled });
        return panel;
    }
}
