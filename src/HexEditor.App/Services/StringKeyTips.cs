using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using HexEditor.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Services;

/// <summary>
/// 翻訳者向け: 各文字列のツールチップにリソースキーを表示する (09 の UI-41 の仕様 5。設定 <c>i18n.showStringKeys</c>、再起動後に反映)。
/// <list type="bullet">
/// <item>XAML の要素: x:Uid は実行時に読めないため、ビルドで埋め込んだ XAML (メインウィンドウ・設定画面・コントロール) から
/// AutomationId / x:Name と x:Uid の対応を読み、その x:Uid のキー (<c>Menu_File_Open.Text</c> など) を出す。</item>
/// <item>コードで作る要素 (メニュー・ダイアログ・設定画面の項目など): 表示している文字列と同じ訳文のキーを出す (同じ訳文のキーが
/// 複数あれば候補を並べる)。</item>
/// <item>後から作られる要素 (フライアウト・ダイアログ・設定画面・パネル): 設定が有効な間、ウィンドウと開いているポップアップを
/// 定期的に見直して付ける。</item>
/// <item>元からツールチップがある要素は、元のツールチップの後ろにキーを足す (上書きしない)。</item>
/// </list>
/// </summary>
public static class StringKeyTips
{
    public const string SettingKey = "i18n.showStringKeys";

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>見直しの間隔。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>キーを足した要素の、元のツールチップと付けたツールチップ。</summary>
    private static readonly ConditionalWeakTable<DependencyObject, TipRecord> Records = new();

    /// <summary>キーを付けているウィンドウ (同じウィンドウに 2 回付けない)。</summary>
    private static readonly ConditionalWeakTable<Window, object> Windows = new();

    private static readonly Lazy<XamlUids> s_uids = new(ReadUids);
    private static readonly Lazy<ResourceIndex> s_resources = new(() => ResourceIndex.Create(Hosting.Localization.CurrentLanguage));

    private sealed class TipRecord
    {
        public object? Original { get; set; }

        public string Applied { get; set; } = string.Empty;
    }

    /// <summary>
    /// 設定が有効なら、メニューと画面の要素のツールチップにキーを出し、ウィンドウを閉じるまで新しい要素にも付け続ける。
    /// 最初に付けた要素の数を返す。
    /// </summary>
    public static int Apply(Window window, MenuBar menu, FrameworkElement root)
    {
        if (!App.Settings.GetBool(SettingKey, false))
        {
            return 0;
        }

        if (Windows.TryGetValue(window, out _))
        {
            return Refresh(menu, root);
        }

        Windows.Add(window, new object());
        int count = Refresh(menu, root);
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = RefreshInterval;
        timer.Tick += (_, _) => Refresh(menu, root);
        timer.Start();
        window.Closed += (_, _) => timer.Stop();
        AppLog.Info($"Resource keys shown in {count} tooltip(s) ({window.GetType().Name}).");
        return count;
    }

    /// <summary>メニューバーの項目、ウィンドウの木、開いているポップアップ (フライアウト・ダイアログ) にキーを付ける。付けた数を返す。</summary>
    public static int Refresh(MenuBar menu, FrameworkElement root)
    {
        XamlUids uids = s_uids.Value;
        int count = 0;
        for (int i = 0; i < menu.Items.Count; i++)
        {
            // メニューバーの項目は AutomationId がないものがあるので、XAML の MenuBarItem の順で x:Uid を対応させる。
            MenuBarItem top = menu.Items[i];
            count += Tag(top, uids, i < uids.MenuTitles.Count ? uids.MenuTitles[i] : null) ? 1 : 0;
            count += TagMenuItems(top.Items, uids);
        }

        count += TagTree(root, uids, insideControl: false);
        if (root.XamlRoot is { } xamlRoot)
        {
            foreach (Microsoft.UI.Xaml.Controls.Primitives.Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (popup.Child is { } child and not ToolTip)
                {
                    count += TagTree(child, uids, insideControl: false);
                }
            }
        }

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

    /// <summary>
    /// 要素に出すキーの文字列。x:Uid が分かればその x:Uid のキー (アクセスキーを除き、<see cref="KeyFor"/> の形を先頭に)。
    /// 分からなければ、表示している文字列と同じ訳文のキー (最大 3 つ)。どちらもなければ null。
    /// </summary>
    internal static string? KeysFor(DependencyObject element, XamlUids uids, ResourceIndex resources, string? uid = null)
    {
        if (uid is null && element is FrameworkElement or MenuFlyoutItemBase or MenuBarItem)
        {
            string id = AutomationProperties.GetAutomationId(element);
            if (id.Length > 0)
            {
                uids.ById.TryGetValue(id, out uid);
            }

            if (uid is null && element is FrameworkElement { Name.Length: > 0 } named)
            {
                uids.ByName.TryGetValue(named.Name, out uid);
            }
        }

        if (uid is not null)
        {
            string main = KeyFor(element, uid);
            List<string> keys = [.. resources.KeysWithPrefix(uid + ".").Where(k => !k.EndsWith(".AccessKey", StringComparison.Ordinal))
                .OrderBy(k => k == main ? 0 : 1).ThenBy(k => k, StringComparer.Ordinal)];
            return keys.Count > 0 ? string.Join(", ", keys) : main;
        }

        foreach (string text in ShownTexts(element))
        {
            IReadOnlyList<string> keys = resources.KeysOf(text);
            if (keys.Count > 0)
            {
                return string.Join(", ", keys.Take(3)) + (keys.Count > 3 ? ", …" : string.Empty);
            }
        }

        return null;
    }

    /// <summary>要素が表示している文字列 (主なものから順に)。</summary>
    private static IEnumerable<string> ShownTexts(DependencyObject element)
    {
        switch (element)
        {
            case MenuFlyoutItem item:
                yield return item.Text;
                break;
            case MenuFlyoutSubItem sub:
                yield return sub.Text;
                break;
            case MenuBarItem bar:
                yield return bar.Title;
                break;
            case TextBlock text:
                yield return text.Text;
                break;
            case ToggleSwitch toggle:
                if (toggle.Header is string toggleHeader)
                {
                    yield return toggleHeader;
                }

                break;
            case TextBox box:
                if (box.Header is string boxHeader)
                {
                    yield return boxHeader;
                }

                yield return box.PlaceholderText;
                break;
            case ComboBox combo:
                if (combo.Header is string comboHeader)
                {
                    yield return comboHeader;
                }

                yield return combo.PlaceholderText;
                break;
            case ContentControl { Content: string content }:
                yield return content;
                break;
        }

        if (element is UIElement or MenuFlyoutItemBase)
        {
            yield return AutomationProperties.GetName(element);
        }
    }

    private static int TagMenuItems(IList<MenuFlyoutItemBase> items, XamlUids uids)
    {
        int count = 0;
        foreach (MenuFlyoutItemBase item in items)
        {
            count += Tag(item, uids) ? 1 : 0;
            if (item is MenuFlyoutSubItem sub)
            {
                count += TagMenuItems(sub.Items, uids);
            }
        }

        return count;
    }

    /// <summary>
    /// 木の要素に付ける。Hex ビュー (データの表示)・入力欄の中身・ツールチップは除く。キーを付けた操作できる要素の中の文字列
    /// (ボタンの中の TextBlock など) には付けない (外側のツールチップを隠さないため)。
    /// </summary>
    private static int TagTree(DependencyObject element, XamlUids uids, bool insideControl)
    {
        if (element is HexView or ToolTip or UIElement { Visibility: Visibility.Collapsed })
        {
            return 0;
        }

        int count = 0;
        bool tagged = false;
        if ((element is Control || element is TextBlock) && !(insideControl && element is TextBlock))
        {
            tagged = Tag(element, uids);
            count += tagged ? 1 : 0;
        }

        if (element is TextBox or RichEditBox or PasswordBox)
        {
            return count;
        }

        bool inside = insideControl || (tagged && element is Control);
        int children = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < children; i++)
        {
            count += TagTree(VisualTreeHelper.GetChild(element, i), uids, inside);
        }

        return count;
    }

    /// <summary>1 つの要素のツールチップにキーを付ける (元のツールチップがあれば後ろに足す)。付けた (付いている) なら true。</summary>
    private static bool Tag(DependencyObject element, XamlUids uids, string? uid = null)
    {
        string? keys = KeysFor(element, uids, s_resources.Value, uid);
        if (keys is null)
        {
            return false;
        }

        object? current = ToolTipService.GetToolTip(element);
        TipRecord record = Records.GetOrCreateValue(element);
        if (current is not string s || s != record.Applied)
        {
            // まだ付けていない、またはアプリが後からツールチップを変えた: 今のツールチップを元のツールチップとする。
            record.Original = current;
        }

        string? original = record.Original switch
        {
            string text => text,
            ToolTip { Content: string text } => text,
            null => null,
            _ => string.Empty,
        };
        if (original == string.Empty)
        {
            // 文字列でないツールチップ (画像など) は置き換えない。
            return false;
        }

        string tip = original is null ? keys : original + "\n" + keys;
        if (current as string != tip)
        {
            ToolTipService.SetToolTip(element, tip);
        }

        record.Applied = tip;
        return true;
    }

    /// <summary>埋め込んだ XAML の AutomationId → x:Uid、x:Name → x:Uid (複数の XAML で食い違うものは除く)、MenuBarItem の x:Uid。</summary>
    internal sealed record XamlUids(Dictionary<string, string> ById, Dictionary<string, string> ByName, List<string> MenuTitles);

    internal static XamlUids ReadUids()
    {
        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string?>(StringComparer.Ordinal);
        var titles = new List<string>();
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("HexEditor.App.Xaml.", StringComparison.Ordinal)))
        {
            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
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
                    byId[id] = byId.TryGetValue(id, out string? old) && old != uid ? null : uid;
                }

                if ((e.Attribute(XName.Get("Name", XamlNamespace)) ?? e.Attribute("Name"))?.Value is { } name)
                {
                    byName[name] = byName.TryGetValue(name, out string? old) && old != uid ? null : uid;
                }
            }
        }

        static Dictionary<string, string> Unique(Dictionary<string, string?> map) =>
            map.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);
        return new XamlUids(Unique(byId), Unique(byName), titles);
    }

    /// <summary>今の表示言語の文字列リソース: キーの一覧と、訳文からキーを引く表。</summary>
    internal sealed class ResourceIndex
    {
        private readonly SortedSet<string> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _byText = new(StringComparer.Ordinal);

        public static ResourceIndex Create(string language)
        {
            var index = new ResourceIndex();
            foreach (ResourceString s in ResourceStrings.All(language))
            {
                index.Add(s.Key, s.Current);
            }

            return index;
        }

        public void Add(string key, string text)
        {
            _keys.Add(key);

            // アクセスキー (1 文字) や空の文字列からは引かない。
            if (text.Trim().Length < 2 || key.EndsWith(".AccessKey", StringComparison.Ordinal))
            {
                return;
            }

            if (!_byText.TryGetValue(text, out List<string>? keys))
            {
                _byText[text] = keys = [];
            }

            keys.Add(key);
        }

        public IReadOnlyList<string> KeysOf(string? text) =>
            text is { Length: > 0 } && _byText.TryGetValue(text, out List<string>? keys) ? keys : [];

        public IEnumerable<string> KeysWithPrefix(string prefix) =>
            _keys.GetViewBetween(prefix, prefix + '￿');
    }
}
