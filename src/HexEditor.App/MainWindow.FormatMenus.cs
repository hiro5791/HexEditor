using HexEditor.App.Commands;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 右クリックメニューの項目: 選択範囲がある場合の「新しいタブで開く」(ENG-39)、「選択範囲をエクスポート...」(TOOL-04)、「選択範囲をファイルに保存...」
/// (TOOL-16)。
/// </summary>
public sealed partial class MainWindow
{
    private static readonly (string Id, string TextKey)[] SelectionMenuCommands =
    [
        ("file.openSelectionInNewTab", "Menu_Selection_OpenInNewTab"),
        ("file.exportSelection", "Menu_Selection_Export"),
        ("file.saveSelection", "Menu_Selection_SaveToFile"),
    ];

    /// <summary>Hex ビューの右クリックメニューに、選択範囲の項目を足す (選択範囲があるときだけ表示する)。</summary>
    private void ExtendHexViewFilesMenu(MenuFlyout menu)
    {
        bool selected = Vm.Selected?.Editor.HasSelection == true;
        string separatorId = "HexViewMenu_SelectionSeparator";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == separatorId))
        {
            var separator = new MenuFlyoutSeparator();
            AutomationProperties.SetAutomationId(separator, separatorId);
            menu.Items.Add(separator);
            foreach ((string id, string textKey) in SelectionMenuCommands)
            {
                var item = new MenuFlyoutItem { Text = Loc.Get(textKey), Tag = "Command" };
                AutomationProperties.SetAutomationId(item, "HexViewMenu_" + id);
                item.Click += (_, _) => _ = Commands.ExecuteAsync(id);
                menu.Items.Add(item);
            }
        }

        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            string automationId = AutomationProperties.GetAutomationId(item);
            if (automationId == separatorId)
            {
                item.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (SelectionMenuCommands.FirstOrDefault(c => "HexViewMenu_" + c.Id == automationId) is { Id: { } id } && item is MenuFlyoutItem m)
            {
                m.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                m.IsEnabled = Commands.StateOf(id).Enabled;
                m.KeyboardAcceleratorTextOverride = CommandService.ShortcutText(id);
            }
        }
    }
}
