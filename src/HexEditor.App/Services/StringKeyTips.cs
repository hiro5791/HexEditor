using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Services;

/// <summary>
/// 翻訳者向け: 各文字列のツールチップにリソースキーを表示する (09 の UI-41 の仕様 5。設定 <c>i18n.showStringKeys</c>、再起動後に反映)。
/// x:Uid は実行時に読めないため、ビルドで埋め込んだ MainWindow.xaml から AutomationId と x:Uid の対応を読む。
/// </summary>
public static class StringKeyTips
{
    public const string SettingKey = "i18n.showStringKeys";

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>設定が有効なら、メニューと画面の要素のツールチップにキーを出す。出した要素の数を返す。</summary>
    public static int Apply(Window window, MenuBar menu, FrameworkElement root)
    {
        if (!App.Settings.GetBool(SettingKey, false))
        {
            return 0;
        }

        (Dictionary<string, string> byId, List<string> menuTitles) = ReadUids();
        int count = 0;
        for (int i = 0; i < menu.Items.Count && i < menuTitles.Count; i++)
        {
            ToolTipService.SetToolTip(menu.Items[i], menuTitles[i] + ".Title");
            count++;
            count += ApplyToItems(menu.Items[i].Items, byId);
        }

        count += ApplyToTree(root, byId);
        AppLog.Info($"Resource keys shown in {count} tooltip(s) ({window.GetType().Name}).");
        return count;
    }

    /// <summary>要素の x:Uid から、ツールチップに出すキー (メニューの項目は .Text、その他は x:Uid の後に .*)。</summary>
    public static string KeyFor(object element, string uid) => element switch
    {
        MenuFlyoutItem or MenuFlyoutSubItem => uid + ".Text",
        MenuBarItem => uid + ".Title",
        ContentControl => uid + ".Content",
        TextBlock => uid + ".Text",
        _ => uid + ".*",
    };

    private static int ApplyToItems(IList<MenuFlyoutItemBase> items, Dictionary<string, string> byId)
    {
        int count = 0;
        foreach (MenuFlyoutItemBase item in items)
        {
            if (byId.TryGetValue(AutomationProperties.GetAutomationId(item), out string? uid))
            {
                ToolTipService.SetToolTip(item, KeyFor(item, uid));
                count++;
            }

            if (item is MenuFlyoutSubItem sub)
            {
                count += ApplyToItems(sub.Items, byId);
            }
        }

        return count;
    }

    private static int ApplyToTree(DependencyObject element, Dictionary<string, string> byId)
    {
        int count = 0;
        if (element is FrameworkElement fe && element is not MenuBar && byId.TryGetValue(AutomationProperties.GetAutomationId(fe), out string? uid))
        {
            ToolTipService.SetToolTip(fe, KeyFor(fe, uid));
            count++;
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            count += ApplyToTree(VisualTreeHelper.GetChild(element, i), byId);
        }

        return count;
    }

    /// <summary>埋め込んだ MainWindow.xaml の AutomationId → x:Uid と、MenuBarItem の x:Uid (順番)。</summary>
    internal static (Dictionary<string, string> ById, List<string> MenuTitles) ReadUids()
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        var titles = new List<string>();
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HexEditor.App.MainWindow.xaml");
        if (stream is null)
        {
            return (byId, titles);
        }

        XDocument xaml = XDocument.Load(stream);
        foreach (XElement e in xaml.Descendants())
        {
            string? uid = e.Attribute(XName.Get("Uid", XamlNamespace))?.Value;
            if (uid is null)
            {
                continue;
            }

            if (e.Name.LocalName == "MenuBarItem")
            {
                titles.Add(uid);
            }

            if (e.Attribute("AutomationProperties.AutomationId")?.Value is { } id)
            {
                byId[id] = uid;
            }
        }

        return (byId, titles);
    }
}
