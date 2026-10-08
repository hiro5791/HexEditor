using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Commands;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 形式を選択してコピー (EDIT-25) の「呼び出し」: 形式ごとのコマンド (コマンドパレット「形式を選択してコピー: &lt;形式名&gt;」) と、
/// Hex ビューの右クリックメニューの「形式を選択してコピー」のサブメニュー (よく使う形式)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>右クリックメニューのサブメニューに出す、よく使う形式。</summary>
    private static readonly CopyFormat[] FrequentCopyFormats =
    [
        CopyFormat.HexSpaced, CopyFormat.HexPlain, CopyFormat.HexCommaPrefixed, CopyFormat.ArrayC, CopyFormat.ArrayCSharp,
        CopyFormat.ArrayPython, CopyFormat.Base64, CopyFormat.ScreenDump, CopyFormat.Text, CopyFormat.Position,
    ];

    private const string CopyAsMenuId = "HexViewMenu_CopyAsMenu";

    /// <summary>形式ごとのコピーのコマンドを登録する (RegisterEditCommands から呼ぶ)。</summary>
    private void RegisterCopyAsFormatCommands()
    {
        foreach (CopyFormat format in Enum.GetValues<CopyFormat>())
        {
            CopyFormat f = format;
            Commands.Register(BuiltInCommands.CopyAsFormatId(f), () => CopyInFormatAsync(f), NeedsDocument);
        }
    }

    /// <summary>右クリックメニューの「形式を選択してコピー」のサブメニューを足し、項目の状態を更新する (メニューを開くたびに呼ぶ)。</summary>
    private void ExtendHexViewCopyAsMenu(MenuFlyout menu)
    {
        if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == CopyAsMenuId) is not MenuFlyoutSubItem sub)
        {
            sub = new MenuFlyoutSubItem { Text = Loc.Get("HexView_Menu_CopyAs"), Tag = "Command" };
            AutomationProperties.SetAutomationId(sub, CopyAsMenuId);
            sub.Items.Add(Item("edit.copyAs", Loc.Get("HexView_Menu_CopyAsChoose")));
            sub.Items.Add(Item("edit.copyAsLast", Loc.Get("Cmd_edit_copyAsLast")));
            sub.Items.Add(new MenuFlyoutSeparator());
            foreach (CopyFormat format in FrequentCopyFormats)
            {
                sub.Items.Add(Item(BuiltInCommands.CopyAsFormatId(format), CopyFormatMenuText(format)));
            }

            int index = menu.Items.ToList().FindIndex(i => AutomationProperties.GetAutomationId(i) == "HexViewMenu_Copy");
            menu.Items.Insert(index < 0 ? menu.Items.Count : index + 1, sub);
        }

        bool any = false;
        foreach (MenuFlyoutItem item in sub.Items.OfType<MenuFlyoutItem>())
        {
            string id = (string)item.Tag;
            item.IsEnabled = Commands.StateOf(id).Enabled;
            item.KeyboardAcceleratorTextOverride = CommandService.ShortcutText(id);
            any |= item.IsEnabled;
        }

        sub.IsEnabled = any;

        MenuFlyoutItem Item(string id, string text)
        {
            var item = new MenuFlyoutItem { Text = text, Tag = id };
            AutomationProperties.SetAutomationId(item, "HexViewMenu_" + id);
            item.Click += (_, _) => _ = Commands.ExecuteAsync(id);
            return item;
        }
    }

    /// <summary>サブメニューの形式の名前 (Hex 文字列・数値列・配列は分類を添える。例: 「配列 (C)」)。</summary>
    private static string CopyFormatMenuText(CopyFormat format)
    {
        CopyFormatCategory category = CopyFormatter.CategoryOf(format);
        string name = Loc.Get("CopyAs_Format_" + format);
        return category is CopyFormatCategory.HexString or CopyFormatCategory.Numbers or CopyFormatCategory.Array
            ? Loc.Format("HexView_Menu_CopyAsFormat", Loc.Get("CopyAs_Category_" + category), name)
            : name;
    }
}
