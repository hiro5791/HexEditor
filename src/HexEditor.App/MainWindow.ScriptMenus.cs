using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>スクリプトのメタデータ <c>@menu</c> / <c>@context</c> による、メニューへの登録 (AUTO-26)。</summary>
public sealed partial class MainWindow
{
    private const string ScriptMenuTag = "ScriptMenu";

    /// <summary>ツール > スクリプトの下に、<c>@menu</c> を持つスクリプトを足す (パスの <c>/</c> でサブメニューにする)。</summary>
    internal void RebuildScriptMenuItems()
    {
        for (int i = ScriptsMenu.Items.Count - 1; i >= 0; i--)
        {
            if (ScriptsMenu.Items[i].Tag as string == ScriptMenuTag)
            {
                ScriptsMenu.Items.RemoveAt(i);
            }
        }

        var scripts = ScriptWorkspace.Library.CommandScripts.Where(n => !string.IsNullOrWhiteSpace(n.Menu)).OrderBy(n => n.Menu, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (scripts.Count == 0)
        {
            return;
        }

        var separator = new MenuFlyoutSeparator { Tag = ScriptMenuTag };
        ScriptsMenu.Items.Add(separator);
        var submenus = new Dictionary<string, MenuFlyoutSubItem>(StringComparer.CurrentCultureIgnoreCase);
        foreach (ScriptNode node in scripts)
        {
            string[] parts = node.Menu!.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            IList<MenuFlyoutItemBase> items = ScriptsMenu.Items;
            string key = string.Empty;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key += "/" + parts[i];
                if (!submenus.TryGetValue(key, out MenuFlyoutSubItem? sub))
                {
                    sub = new MenuFlyoutSubItem { Text = parts[i], Tag = i == 0 ? ScriptMenuTag : null };
                    AutomationProperties.SetAutomationId(sub, "ScriptMenu_" + key);
                    items.Add(sub);
                    submenus[key] = sub;
                }

                items = sub.Items;
            }

            items.Add(ScriptMenuItem(node, parts[^1], "ScriptMenu_Item_"));
        }
    }

    private MenuFlyoutItem ScriptMenuItem(ScriptNode node, string text, string idPrefix)
    {
        string id = ScriptCommandIds.For(node);
        var item = new MenuFlyoutItem { Text = text, Tag = ScriptMenuTag };
        AutomationProperties.SetAutomationId(item, idPrefix + id);
        item.Click += (_, _) => _ = Commands.ExecuteAsync(id);
        return item;
    }

    /// <summary>Hex ビューの右クリックメニューの「スクリプト」: <c>@context always</c> と、選択があるときの <c>@context selection</c> (メニューを開くたびに作り直す)。</summary>
    private void ExtendHexViewScriptMenu(MenuFlyout menu)
    {
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i].Tag as string == ScriptMenuTag)
            {
                menu.Items.RemoveAt(i);
            }
        }

        bool hasSelection = Vm.Selected?.Editor.HasSelection == true;
        var scripts = ScriptWorkspace.Library.CommandScripts
            .Where(n => n.ContextMenu == "always" || n.ContextMenu == "selection" && hasSelection).ToList();
        if (scripts.Count == 0)
        {
            return;
        }

        var sub = new MenuFlyoutSubItem { Text = Loc.Get("ScriptContext_Menu"), Tag = ScriptMenuTag };
        AutomationProperties.SetAutomationId(sub, "HexViewMenu_Script");
        foreach (ScriptNode node in scripts)
        {
            sub.Items.Add(ScriptMenuItem(node, node.Name, "HexViewMenu_Script_"));
        }

        menu.Items.Add(new MenuFlyoutSeparator { Tag = ScriptMenuTag });
        menu.Items.Add(sub);
        // 区切りと「スクリプト」を同じ印で消せるよう、サブメニューにも印を付けておく。
    }
}
