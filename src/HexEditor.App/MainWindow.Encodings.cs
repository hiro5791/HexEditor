using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace HexEditor.App;

/// <summary>
/// テキスト列の文字コードの選択 (VIEW-21): 表示 > 文字コードの最近使ったもの (仕様 8) と「その他…」の一覧 (絞り込み欄付きの
/// フライアウト。仕様 3・4)。ステータスバーの文字コード表示とコマンド <c>view.encoding.select</c> (引数なしで一覧、引数に名前を
/// 渡すとその文字コードにする) からも同じ一覧を開く。
/// </summary>
public sealed partial class MainWindow
{
    private const string EncodingSelectCommand = "view.encoding.select";

    /// <summary>最近使った文字コード (state.json。名前をカンマ区切り)。</summary>
    private const string RecentEncodingsKey = "encoding.recent";

    private readonly List<MenuFlyoutItemBase> _recentEncodingItems = [];
    private MenuFlyoutSubItem? _encodingMenu;
    private Flyout? _encodingFlyout;
    private TextBox? _encodingFilter;
    private ListView? _encodingList;
    private string _recentEncodingKey = string.Empty;

    // 閉じる途中 (Closing から Closed まで) に開き直すと ShowAt が無視されるため、閉じ終わってから開く。
    private bool _encodingFlyoutClosing;
    private (bool Pending, FrameworkElement? Anchor) _encodingFlyoutReopen;

    /// <summary>文字コードのメニューに「その他…」を加え、コマンドを登録する (InitializeViewMenu から 1 回呼ぶ)。</summary>
    private void InitializeEncodingList(MenuFlyoutSubItem encoding)
    {
        _encodingMenu = encoding;
        Commands.Register(EncodingSelectCommand, new CommandHandler(argument =>
        {
            if (argument is { Length: > 0 } id)
            {
                SetEncoding(id);
            }
            else
            {
                ShowEncodingList(null);
            }

            return Task.CompletedTask;
        }, NeedsDocument));

        encoding.Items.Add(new MenuFlyoutSeparator());
        const string moreKey = "Menu_View_EncodingMore";
        var more = new MenuFlyoutItem { Text = Loc.Get(moreKey + "/Text"), AccessKey = Loc.Get(moreKey + "/AccessKey") };
        CommandUi.SetId(more, EncodingSelectCommand);
        AutomationProperties.SetAutomationId(more, "Command_EncodingMore");
        encoding.Items.Add(more);
        _viewItems["Command_EncodingMore"] = more;
    }

    /// <summary>文字コードを変え、最近使った文字コードに加える (仕様 8)。状態を持つ文字コードは選べない (仕様 4)。</summary>
    private bool SetEncoding(string id)
    {
        if (Editor is not { } editor || EncodingCatalog.Find(id) is not { Selectable: true } entry)
        {
            return false;
        }

        editor.TextEncoding = TextEncoding.FromId(entry.Id);
        IReadOnlyList<string> recent = EncodingCatalog.PushRecent(RecentEncodings(), entry.Id);
        AppState.SetString(RecentEncodingsKey, string.Join(',', recent));
        UpdateEncodingMenu();
        QueueStatusBarLayout();
        return true;
    }

    private static IReadOnlyList<string> RecentEncodings() =>
        [.. AppState.GetString(RecentEncodingsKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => EncodingCatalog.Find(id) is { Selectable: true }).Take(EncodingCatalog.RecentLimit)];

    /// <summary>一覧の表示名 (例: <c>932 Japanese (Shift-JIS)</c>。ANSI・OEM は実際のコードページ番号を入れる)。</summary>
    internal static string EncodingDisplayText(EncodingEntry entry)
    {
        string name = EncodingName(entry);
        return entry.Group is EncodingGroup.Basic or EncodingGroup.Unicode && !entry.Stateful ? name : entry.Label + " " + name;
    }

    private static string EncodingName(EncodingEntry entry)
    {
        // 文字表 (tbl:ファイル名) はリソースに名前がない (ID にリソース名に使えない文字が入る)。
        if (entry.Id.StartsWith(TableEncodings.IdPrefix, StringComparison.Ordinal))
        {
            return entry.EnglishName;
        }

        string key = "Encoding_Name_" + entry.Id;
        string text = Loc.Get(key);
        if (string.IsNullOrEmpty(text) || text == key)
        {
            text = entry.EnglishName;
        }

        return entry.NameHasCodePage
            ? string.Format(CultureInfo.CurrentCulture, text, entry.Id == "oem" ? TextEncoding.Oem.CodePage : TextEncoding.Ansi.CodePage)
            : text;
    }

    /// <summary>メニューの上部の「最近使った文字コード」(仕様 8) を作り直す。</summary>
    private void UpdateRecentEncodingItems()
    {
        if (_encodingMenu is null)
        {
            return;
        }

        IReadOnlyList<string> recent = RecentEncodings();
        string current = Editor?.TextEncoding.Id ?? "ascii";
        string key = string.Join(',', recent) + "|" + current;
        if (key == _recentEncodingKey)
        {
            return;
        }

        _recentEncodingKey = key;
        foreach (MenuFlyoutItemBase item in _recentEncodingItems)
        {
            _encodingMenu.Items.Remove(item);
        }

        _recentEncodingItems.Clear();
        for (int i = 0; i < recent.Count; i++)
        {
            EncodingEntry entry = EncodingCatalog.Find(recent[i])!;
            var item = new RadioMenuFlyoutItem
            {
                Text = EncodingDisplayText(entry),
                GroupName = "RecentEncoding",
                IsChecked = string.Equals(entry.Id, current, StringComparison.OrdinalIgnoreCase),
                Tag = entry.Id,
            };
            AutomationProperties.SetAutomationId(item, "Command_EncodingRecent_" + i);
            string id = entry.Id;
            item.Click += (_, _) => _ = Commands.ExecuteAsync(EncodingSelectCommand, id);
            _recentEncodingItems.Add(item);
        }

        if (_recentEncodingItems.Count > 0)
        {
            _recentEncodingItems.Add(new MenuFlyoutSeparator());
        }

        for (int i = 0; i < _recentEncodingItems.Count; i++)
        {
            _encodingMenu.Items.Insert(i, _recentEncodingItems[i]);
        }
    }

    // ---- 「その他…」の一覧 (仕様 3・4) ----

    /// <summary>絞り込み欄付きの一覧を開く。<paramref name="anchor"/> がなければ Hex ビューの左上に出す。</summary>
    private void ShowEncodingList(FrameworkElement? anchor)
    {
        if (Editor is null)
        {
            return;
        }

        EnsureEncodingFlyout();
        if (_encodingFlyoutClosing)
        {
            _encodingFlyoutReopen = (true, anchor);
            return;
        }

        _encodingFilter!.Text = string.Empty;
        RefreshEncodingList();
        if (anchor is not null)
        {
            _encodingFlyout!.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Top });
        }
        else if (SelectedView() is { } view)
        {
            _encodingFlyout!.ShowAt(view, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                Position = new Windows.Foundation.Point(view.ContentLeft, 0),
            });
        }
        else
        {
            return;
        }

        _encodingFilter.Focus(FocusState.Programmatic);
    }

    private void EnsureEncodingFlyout()
    {
        if (_encodingFlyout is not null)
        {
            return;
        }

        var title = new TextBlock { Text = Loc.Get("EncodingList_Title"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        _encodingFilter = new TextBox { PlaceholderText = Loc.Get("EncodingList_Filter"), MinWidth = 320 };
        AutomationProperties.SetAutomationId(_encodingFilter, "EncodingList_Filter");
        AutomationProperties.SetName(_encodingFilter, Loc.Get("EncodingList_Filter"));
        _encodingFilter.TextChanged += (_, _) => RefreshEncodingList();
        _encodingFilter.KeyDown += EncodingFilter_KeyDown;

        _encodingList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 420,
            Width = 380,
        };
        AutomationProperties.SetAutomationId(_encodingList, "EncodingList_Items");
        AutomationProperties.SetName(_encodingList, Loc.Get("EncodingList_Title"));
        _encodingList.ItemClick += (_, e) => ChooseEncoding((e.ClickedItem as FrameworkElement)?.Tag as string);
        _encodingList.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && (_encodingList.SelectedItem as FrameworkElement)?.Tag is string id)
            {
                ChooseEncoding(id);
                e.Handled = true;
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(title);
        panel.Children.Add(_encodingFilter);
        panel.Children.Add(_encodingList);
        _encodingFlyout = new Flyout { Content = panel };
        _encodingFlyout.Closing += (_, _) => _encodingFlyoutClosing = true;
        _encodingFlyout.Closed += (_, _) =>
        {
            _encodingFlyoutClosing = false;
            if (_encodingFlyoutReopen.Pending)
            {
                FrameworkElement? anchor = _encodingFlyoutReopen.Anchor;
                _encodingFlyoutReopen = default;
                DispatcherQueue.TryEnqueue(() => ShowEncodingList(anchor));
                return;
            }

            FocusEditor();
        };
    }

    private void EncodingFilter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_encodingList is null)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Down && _encodingList.Items.Count > 0)
        {
            // 一覧に移る (最初の選べる項目を選ぶ)。
            int first = _encodingList.Items.Cast<ListViewItem>().ToList().FindIndex(i => i.IsEnabled);
            _encodingList.SelectedIndex = Math.Max(0, first);
            (_encodingList.ContainerFromIndex(_encodingList.SelectedIndex) as Control)?.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // 絞り込んだ結果の選択中の項目 (なければ最初の選べる項目) にする。
            ListViewItem? item = _encodingList.SelectedItem as ListViewItem
                ?? _encodingList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.IsEnabled);
            ChooseEncoding(item?.Tag as string);
            e.Handled = true;
        }
    }

    /// <summary>絞り込みに合う項目で一覧を作り直す。状態を持つ文字コードは無効にし、選べない理由を添える (仕様 4)。</summary>
    private void RefreshEncodingList()
    {
        if (_encodingList is null || _encodingFilter is null)
        {
            return;
        }

        string current = Editor?.TextEncoding.Id ?? "ascii";
        _encodingList.Items.Clear();
        foreach (EncodingEntry entry in EncodingCatalog.All)
        {
            string text = EncodingDisplayText(entry);
            if (!EncodingCatalog.Matches(entry, EncodingName(entry), _encodingFilter.Text))
            {
                continue;
            }

            var content = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
            content.Children.Add(new TextBlock { Text = text });
            string group = Loc.Get("EncodingList_Group_" + entry.Group);
            string? reason = entry.Selectable ? null : Loc.Get("EncodingList_Stateful");
            content.Children.Add(new TextBlock
            {
                Text = reason is null ? group : group + " — " + reason,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            var item = new ListViewItem { Content = content, Tag = entry.Id, IsEnabled = entry.Selectable };
            AutomationProperties.SetName(item, text);
            AutomationProperties.SetHelpText(item, reason ?? group);
            AutomationProperties.SetAutomationId(item, "EncodingList_" + entry.Id);
            _encodingList.Items.Add(item);
            if (string.Equals(entry.Id, current, StringComparison.OrdinalIgnoreCase))
            {
                _encodingList.SelectedItem = item;
            }
        }
    }

    /// <summary>一覧で選んだ文字コードにする。選べない項目 (状態を持つ文字コード) では何もしない (仕様 4)。</summary>
    private void ChooseEncoding(string? id)
    {
        if (id is null || EncodingCatalog.Find(id) is not { Selectable: true })
        {
            return;
        }

        _encodingFlyout?.Hide();
        _ = Commands.ExecuteAsync(EncodingSelectCommand, id);
    }

#if HEX_TEST_HOOKS
    /// <summary>
    /// 文字コードの一覧の状態 (テスト用)。<c>open</c> で開く (<c>anchor</c> が <c>status</c> ならステータスバーから)、<c>choose</c> で
    /// その名前の項目を Enter で選ぶのと同じ処理をする。
    /// </summary>
    private System.Text.Json.Nodes.JsonObject TestEncodingList(System.Text.Json.Nodes.JsonObject request)
    {
        if (request["open"]?.GetValue<bool>() == true)
        {
            ShowEncodingList(request["anchor"]?.GetValue<string>() == "status" ? StatusEncoding : null);
        }

        if (request["choose"]?.GetValue<string>() is { } id)
        {
            ChooseEncoding(id);
        }

        var items = new System.Text.Json.Nodes.JsonArray();
        foreach (ListViewItem item in _encodingList?.Items.Cast<ListViewItem>() ?? [])
        {
            items.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = item.Tag as string,
                ["text"] = AutomationProperties.GetName(item),
                ["enabled"] = item.IsEnabled,
                ["description"] = AutomationProperties.GetHelpText(item),
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["open"] = _encodingFlyout?.IsOpen ?? false,
            ["filter"] = _encodingFilter?.Text,
            ["items"] = items,
            ["encoding"] = Editor?.TextEncoding.Id,
            ["recent"] = new System.Text.Json.Nodes.JsonArray([.. RecentEncodings().Select(r => (System.Text.Json.Nodes.JsonNode?)r)]),
        };
    }
#endif
}
