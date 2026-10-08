using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using HexEditor.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 最近使ったファイル (UI-32): ファイル > 最近使ったファイル のサブメニュー、「すべて表示…」の一覧、一覧の消去と元に戻す。
/// 閉じたタブを開き直す (UI-12)。読み取り専用で開く (ENG-14)。
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<MenuFlyoutItemBase> _recentMenuItems = [];

    /// <summary>
    /// ファイルの記録のコマンド (UI-16): 読み取り専用で開く、再読み込み (ENG-18)、閉じたタブを開き直す (UI-12)、最近使ったファイル (UI-32)、
    /// 前回のセッションの復元 (UI-30)。キー (Ctrl+R、Ctrl+Shift+T) は KeyDispatcher が振り分ける。
    /// </summary>
    private void RegisterFilesCommands()
    {
        var e = new RoutedEventArgs();
        Commands.Register("file.openReadOnly", () => OpenReadOnly_Click(this, e));
        Commands.Register("file.reload", () => Reload_Click(this, e), () => NeedsDocument(NeedsFile));
        Commands.Register("file.discardReload", () => DiscardReload_Click(this, e), () => NeedsDocument(NeedsFile));
        Commands.Register("file.reopenClosed", ReopenClosedTab,
            () => Vm.ClosedTabs.Count > 0 ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoClosedTabs")));
        Commands.Register("file.recent.clear", () => ClearRecent_Click(this, e),
            () => Vm.Recent.Items.Any(i => !i.Pinned) ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoRecentFiles")));
        Commands.Register("file.recent.showAll", () => ShowAllRecent_Click(this, e));
        Commands.Register("window.restoreSession", RestorePreviousSession,
            () => _startupSession.HasRestorableTabs ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoSession")));
    }

    /// <summary>ファイルから開いた文書だけが対象のコマンドの、使えない理由。</summary>
    private static string? NeedsFile(DocumentViewModel doc) =>
        doc.FilePath is null || doc.IsMissing ? Loc.Get("Command_NoFile") : null;

    /// <summary>サブメニュー: ピン留めした項目と最近の 10 件 (UI-32 の仕様 3)。各項目はファイル名と短縮したパス、ツールチップに完全なパス。</summary>
    private void RebuildRecentMenu()
    {
        foreach (MenuFlyoutItemBase old in _recentMenuItems)
        {
            RecentMenu.Items.Remove(old);
        }

        _recentMenuItems.Clear();
        int at = 0;
        bool pinnedDone = false;
        foreach (RecentItem item in Vm.Recent.MenuEntries())
        {
            if (!item.Pinned && !pinnedDone && at > 0)
            {
                // ピン留めした項目と最近の項目の間の区切り。
                var separator = new MenuFlyoutSeparator();
                RecentMenu.Items.Insert(at++, separator);
                _recentMenuItems.Add(separator);
            }

            pinnedDone |= !item.Pinned;
            var entry = new RecentEntryViewModel(item);
            var menuItem = new MenuFlyoutItem
            {
                Text = Loc.Format("Recent_MenuItem", item.DisplayName, entry.ShortFolder),
                Tag = item,
            };
            if (item.Pinned)
            {
                menuItem.Icon = new FontIcon { Glyph = "" };
            }

            ToolTipService.SetToolTip(menuItem, item.Path);
            AutomationProperties.SetAutomationId(menuItem, "Recent_Item");
            AutomationProperties.SetName(menuItem, item.DisplayName);
            menuItem.Click += (_, _) => OpenRecent(item);
            RecentMenu.Items.Insert(at++, menuItem);
            _recentMenuItems.Add(menuItem);

            // 見つからないファイルは薄く表示し、「見つかりません」と付記する (存在の確認はバックグラウンドで。ネットワークは確かめない)。
            entry.CheckExistsInBackground(action => DispatcherQueue.TryEnqueue(() =>
            {
                action();
                if (entry.IsMissing)
                {
                    menuItem.Text = Loc.Format("Recent_MenuItemMissing", item.DisplayName, entry.ShortFolder);
                    menuItem.Opacity = 0.5;
                }
            }));
        }

        RecentMenuSeparator.Visibility = at > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 最近使ったファイルを開く。見つからなければ「&lt;パス&gt; が見つかりません」と「一覧から削除」を示す (UI-32 の仕様 6)。
    /// ネットワークのパスは確かめずに開く。
    /// </summary>
    private async void OpenRecent(RecentItem item)
    {
        // ドライブの種類と存在の確認は、応答しないドライブで UI を止めないようにバックグラウンドで行う。
        bool missing = !RecentFileList.IsNetworkPathSyntax(item.Path) && await Task.Run(() => !item.IsNetworkPath && !File.Exists(item.Path));
        if (missing)
        {
            ShowNotice(Loc.Format("Recent_Missing", item.Path), InfoBarSeverity.Warning, actions:
            [
                new NotificationAction(Loc.Get("Recent_Remove"), () => Vm.Recent.Remove(item.Path)),
            ]);
            return;
        }

        TryOpen(item.Path);
        UpdateTitle();
    }

    /// <summary>「読み取り専用で開く」(ENG-14 の仕様 1)。</summary>
    private async void OpenReadOnly_Click(object sender, RoutedEventArgs e)
    {
        const string settingsIdentifier = "HexEditor.Open";
        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult(settingsIdentifier);
        if (paths is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
            picker.FileTypeFilter.Add("*");
            paths = [.. (await picker.PickMultipleFilesAsync()).Select(r => r.Path)];
        }

        foreach (string path in paths)
        {
            TryOpen(path, readOnly: true);
        }

        UpdateTitle();
    }

    /// <summary>閉じたタブを開き直す (UI-12)。見つからないファイルは「&lt;名前&gt; は見つかりませんでした」と示して次を開く。</summary>
    public void ReopenClosedTab()
    {
        Vm.ReopenClosedTab(
            name => ShowNotice(Loc.Format("Reopen_NotFound", name), InfoBarSeverity.Warning),
            (path, index, readOnly) => TryOpen(path, index, readOnly, restorePosition: false));
        UpdateTitle();
    }

    /// <summary>「一覧を消去」: ピン留めしていない項目を消し、InfoBar に「元に戻す」を出す (UI-32 の仕様 5)。</summary>
    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        RecentClearUndo undo = Vm.Recent.ClearUnpinned();
        ShowNotice(Loc.Get("Recent_Cleared"), InfoBarSeverity.Success,
            undo: new NotificationAction(Loc.Get("Common_Undo"), () => Vm.Recent.Undo(undo)));
    }

    /// <summary>
    /// 「すべて表示…」: 全件を検索欄付きの一覧で示す (UI-32 の仕様 4)。列はファイル名、フォルダ、サイズ、最後に開いた日時。
    /// 行の右クリックで「ピン留め / ピン留めを外す」「一覧から削除」「パスをコピー」「エクスプローラーで表示」。
    /// </summary>
    private async void ShowAllRecent_Click(object sender, RoutedEventArgs e)
    {
        var search = new TextBox { PlaceholderText = Loc.Get("Recent_Search") };
        AutomationProperties.SetAutomationId(search, "RecentAll_Search");
        AutomationProperties.SetName(search, Loc.Get("Recent_Search"));
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            MaxHeight = 420,
            ItemTemplate = (DataTemplate)Root.Resources["RecentAllItemTemplate"],
        };
        AutomationProperties.SetAutomationId(list, "RecentAll_List");
        AutomationProperties.SetName(list, Loc.Get("Recent_AllTitle"));
        var body = new StackPanel { Spacing = 12, MinWidth = 560 };
        body.Children.Add(search);
        body.Children.Add(list);
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("Recent_AllTitle"),
            Content = body,
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        AutomationProperties.SetAutomationId(dialog, "RecentAllDialog");

        var entries = new List<RecentEntryViewModel>();
        void Fill()
        {
            entries = [.. Vm.Recent.Items.Select(i => new RecentEntryViewModel(i))];
            foreach (RecentEntryViewModel entry in entries)
            {
                entry.CheckExistsInBackground(action => DispatcherQueue.TryEnqueue(() => action()));
            }

            Filter();
        }

        void Filter()
        {
            string text = search.Text.Trim();
            list.ItemsSource = text.Length == 0
                ? entries
                : entries.Where(en => en.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || en.Folder.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        search.TextChanged += (_, _) => Filter();
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is RecentEntryViewModel entry)
            {
                dialog.Hide();
                OpenRecent(entry.Item);
            }
        };
        list.ContextRequested += (_, args) =>
        {
            if ((args.OriginalSource as FrameworkElement)?.DataContext is not RecentEntryViewModel entry)
            {
                return;
            }

            args.Handled = true;
            MenuFlyout menu = RecentContextMenu(entry, Fill);
            if (args.TryGetPosition(list, out Windows.Foundation.Point point))
            {
                menu.ShowAt(list, point);
            }
            else
            {
                menu.ShowAt((FrameworkElement)args.OriginalSource);
            }
        };

        Fill();
        _recentDialog = (dialog, list, Fill);
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _recentDialog = null;
        }
    }

    /// <summary>表示中の「すべて表示…」(テスト用の命令が中身を読む)。</summary>
    private (ContentDialog Dialog, ListView List, Action Refill)? _recentDialog;

    /// <summary>一覧の行の右クリックメニュー (UI-32 の仕様 4)。</summary>
    private MenuFlyout RecentContextMenu(RecentEntryViewModel entry, Action refill)
    {
        var menu = new MenuFlyout();
        void Add(string key, string id, Action action)
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key) };
            AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) =>
            {
                action();
                refill();
            };
            menu.Items.Add(item);
        }

        if (entry.IsPinned)
        {
            Add("Recent_Unpin", "Recent_Unpin", () => Vm.Recent.Unpin(entry.Path));
        }
        else
        {
            Add("Recent_Pin", "Recent_Pin", () => Vm.Recent.Pin(entry.Path));
        }

        Add("Recent_Remove", "Recent_RemoveItem", () => Vm.Recent.Remove(entry.Path));
        Add("Recent_CopyPath", "Recent_CopyPath", () =>
        {
            var package = new DataPackage();
            package.SetText(entry.Path);
            SystemClipboard.SetContent(package);
        });
        Add("Recent_ShowInExplorer", "Recent_ShowInExplorer", () => ShowInExplorer(entry.Path));
        return menu;
    }

    /// <summary>エクスプローラーでファイルを選んだ状態で開く (テスト中は起動せずにログに残す)。</summary>
    private static void ShowInExplorer(string path)
    {
        if (TestHooks.InterceptLaunch(new Uri(path)))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Explorer could not be started: {ex.Message}");
        }
    }
}
