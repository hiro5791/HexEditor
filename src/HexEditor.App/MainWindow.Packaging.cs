using System.Collections.Specialized;
using System.Globalization;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Operations;
using HexEditor.Platform.Localization;
using HexEditor.Platform.Migration;
using HexEditor.Platform.Network;
using HexEditor.Platform.Shell;
using HexEditor.Platform.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 配布形態に関わる機能の画面側: 更新の確認と InfoBar (10 の PKG-17〜PKG-22)、ネットワークを使う機能の管理 (09 の UI-58)、
/// 翻訳の誤りの報告 (UI-41)、表示言語の選択 (UI-43。設定画面ができるまでの仮の画面)、ジャンプリスト (UI-35)、
/// Explorer 連携の古い登録の案内 (UI-54 の仕様 6)、他の配布形態の設定の取り込み (PKG-31)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>更新の自動の確認の時刻を見る間隔。</summary>
    private static readonly TimeSpan UpdateTimerInterval = TimeSpan.FromSeconds(5);

    private readonly SessionRecentFiles _sessionRecent = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
    private JumpListService? _jumpList;
    private bool _firstRun;
    private string _language = "en";

    /// <summary>最近使ったファイルの一覧 (ジャンプリストが読む)。「最近使ったファイル」の機能 (UI-32) ができたらそちらに置き換える。</summary>
    public IRecentFilesSource RecentFiles { get; set; } = null!;

    /// <summary>App.OnLaunched から、ウィンドウを作った後に呼ぶ。</summary>
    public void StartPackagingFeatures()
    {
        IAppEnvironment env = Program.Environment;

        // 初回起動の判定は、この起動で state.json を書く前に行う (PKG-31 の仕様 1、UI-38)。
        _firstRun = !File.Exists(Path.Combine(env.Locations.Settings, Platform.StateFile.FileName));
        _language = Localization.CurrentLanguage;
        RecentFiles ??= _sessionRecent;

        StartUpdates();
        StartJumpList(env);
        StringKeyTips.Apply(this, MainMenu, Root);
        UpdatePackagingMenu();
        App.Settings.Changed += _ => DispatcherQueue.TryEnqueue(UpdatePackagingMenu);
        CheckExplorerRegistration();
        OfferSettingsImport(env);
    }

    // ---- 更新 (PKG-17〜PKG-22) ----

    private void StartUpdates()
    {
        UpdateService service = AppUpdates.Service;
        service.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshRestartButton);
        Vm.Operations.Changed += (_, _) => DispatcherQueue.TryEnqueue(RefreshRestartButton);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => service.RecordRun();

        // 更新後の初回起動: 「版 X に更新しました」。適用に失敗していたら「更新に失敗しました」とログの場所 (PKG-18 の仕様 6・「エラー」)。
        if (service.StartupNotice() is { } notice)
        {
            ShowUpdateMessage(notice.Updated
                ? UpdatePresentation.Updated(notice.Version)
                : UpdatePresentation.ApplyFailed(notice.Version, Program.Environment.Locations.Logs));
        }

        // 自動の確認 (起動から 30 秒後、その後 24 時間ごと。PKG-17 の仕様 1)。時刻になったかを 5 秒ごとに見る。
        _updateTimer = DispatcherQueue.CreateTimer();
        _updateTimer.Interval = UpdateTimerInterval;
        _updateTimer.Tick += async (_, _) =>
        {
            if (AppUpdates.Service.IsAutomaticCheckDue())
            {
                await CheckForUpdatesAsync(manual: false);
            }
        };
        _updateTimer.Start();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <summary>更新の確認 (PKG-17)。手動なら結果を必ず表示し、自動なら新しい版があるときだけ表示する (仕様 6)。</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateService service = AppUpdates.Service;
        UpdateCheckResult result = await service.CheckAsync(manual);
        AppLog.Info($"Update check ({(manual ? "manual" : "automatic")}): {result.Outcome} {result.Offer?.Version.SemVer} {result.Failure}");
        if (UpdatePresentation.ForCheck(result, service.Current, service.Kind) is { } message)
        {
            ShowUpdateMessage(message);
        }

        // インストーラ版: 見つかったら裏でダウンロードする (PKG-18 の仕様 1)。
        if (result.Outcome == UpdateCheckOutcome.Available && service.Phase == UpdatePhase.Available && service.ShouldDownloadAutomatically)
        {
            await DownloadUpdateAsync(manual);
        }
    }

    /// <summary>ダウンロードを長時間処理として実行し (処理センターに出る。キャンセルできる)、終わったら「準備ができました」を出す。</summary>
    private async Task DownloadUpdateAsync(bool manual)
    {
        UpdateService service = AppUpdates.Service;
        if (service.Offer is not { } offer)
        {
            return;
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Operation_UpdateDownload", offer.Version.SemVer), OperationKind.ReadOnly, null, 100, async op =>
            {
                EventHandler progress = (_, _) => op.Report(service.DownloadProgress);
                service.Changed += progress;
                try
                {
                    await service.DownloadAsync(op.CancellationToken);
                }
                finally
                {
                    service.Changed -= progress;
                }

                return true;
            });
            ShowUpdateMessage(UpdatePresentation.Ready(offer.Version));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Update download cancelled.");
        }
        catch (Exception ex)
        {
            // 自動のときはログだけ (次の周期で再試行)。手動で始めたときは理由を表示する (PKG-18 の「エラー」)。
            AppLog.Warning($"Update download failed: {ex.GetType().Name}");
            if (manual)
            {
                ShowUpdateMessage(UpdatePresentation.DownloadFailed(UpdateService.Classify(ex)));
            }
        }
    }

    private void ShowUpdateMessage(UpdateMessage message)
    {
        AppLog.Info($"Update notice: {message.MessageKey} {string.Join(",", message.Buttons)}");
        UpdateBar.Show(message, OnUpdateButton);
        RefreshRestartButton();
    }

    private async void OnUpdateButton(UpdateButton button)
    {
        UpdateService service = AppUpdates.Service;
        UpdateOffer? offer = service.Offer;
        switch (button)
        {
            case UpdateButton.Download:
                UpdateBar.Hide();
                await DownloadUpdateAsync(manual: true);
                break;
            case UpdateButton.OpenStore or UpdateButton.OpenDownloadPage when offer?.DownloadPage is { } page:
                await OpenUriAsync(page);
                break;
            case UpdateButton.ReleaseNotes:
                await OpenUriAsync(offer?.ReleaseNotes ?? UpdateSource.Resolve(AppUpdates.Preferences).ReleasePage(service.Current));
                break;
            case UpdateButton.Skip:
                service.Skip();
                UpdateBar.Hide();
                break;
            case UpdateButton.Later:
                service.ApplyOnExit();
                UpdateBar.Hide();
                break;
            case UpdateButton.RestartToUpdate:
                await RestartToUpdateAsync();
                break;
        }
    }

    /// <summary>「再起動して更新」(PKG-18 の仕様 3): 未保存の変更を確認してから適用し、開いていたファイルを渡して再起動する。</summary>
    private async Task RestartToUpdateAsync()
    {
        if (!UpdateService.CanRestartNow(Vm.Operations.Active.Count))
        {
            return;
        }

        List<string> files = [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()];
        if (!await CloseAsync(Vm.Documents.ToList()))
        {
            return;
        }

        App.Settings.Flush();
        AppUpdates.Service.ApplyAndRestart(AppRestart.Arguments(Environment.GetCommandLineArgs().Skip(1).ToList(), files));
    }

    /// <summary>実行中の長時間処理 (保存を含む) があるときは「再起動して更新」を押せない (PKG-18 の仕様 5)。</summary>
    private void RefreshRestartButton() =>
        UpdateBar.SetRestartEnabled(UpdateService.CanRestartNow(Vm.Operations.Active.Count), Loc.Get("Update_RestartBusy"));

    // ---- ネットワークを使う機能の管理 (UI-58) ----

    /// <summary>
    /// オフラインモードでは「更新の確認」を無効にして理由を表示する (UI-58 の仕様 3)。理由は項目の右側 (ショートカットの欄) と
    /// UI オートメーションの説明に出す (無効の項目のツールチップは表示されないため)。
    /// </summary>
    private void UpdatePackagingMenu()
    {
        if (FindMenuItem("Command_CheckForUpdates") is not { } item)
        {
            return;
        }

        bool offline = AppUpdates.Policy.Offline;
        item.IsEnabled = !offline;
        item.KeyboardAcceleratorTextOverride = offline ? Loc.Get("Network_OfflineReason") : string.Empty;
        AutomationProperties.SetHelpText(item, offline ? Loc.Get("Network_OfflineReason") : string.Empty);
    }

    private MenuFlyoutItem? FindMenuItem(string automationId)
    {
        static MenuFlyoutItem? Find(IEnumerable<MenuFlyoutItemBase> items, string id)
        {
            foreach (MenuFlyoutItemBase item in items)
            {
                if (item is MenuFlyoutItem m && AutomationProperties.GetAutomationId(m) == id)
                {
                    return m;
                }

                if (item is MenuFlyoutSubItem sub && Find(sub.Items, id) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return MainMenu.Items.Select(m => Find(m.Items, automationId)).FirstOrDefault(m => m is not null);
    }

    /// <summary>
    /// ブラウザで開く前の確認 (UI-58 の仕様 3、UI-41 の仕様 6)。オフラインモード (翻訳の報告では、翻訳の報告を無効にしたとき) は、
    /// URL を表示して開くかどうかを選ばせる。開いてよければ true。
    /// </summary>
    private async Task<bool> ConfirmOpenWebPageAsync(Uri uri, bool translationReport)
    {
        if (uri.Scheme is not ("http" or "https") || !AppUpdates.Policy.ConfirmBeforeOpening(translationReport))
        {
            return true;
        }

        var url = new TextBox { Text = uri.AbsoluteUri, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160 };
        AutomationProperties.SetAutomationId(url, "OpenUrlDialog_Url");
        AutomationProperties.SetName(url, Loc.Get("OpenUrl_UrlLabel"));
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Loc.Get("OpenUrl_Body"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(url);
        ContentDialog dialog = NewDialog(Loc.Get("OpenUrl_Title"), body);
        AutomationProperties.SetAutomationId(dialog, "OpenUrlDialog");
        dialog.PrimaryButtonText = Loc.Get("OpenUrl_Open");
        dialog.SecondaryButtonText = Loc.Get("OpenUrl_Copy");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        AppLog.Info("Open URL dialog shown (offline mode)");
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            var package = new DataPackage();
            package.SetText(uri.AbsoluteUri);
            SystemClipboard.SetContent(package);
        }

        return result == ContentDialogResult.Primary;
    }

    // ---- 翻訳の誤りを報告 (UI-41) ----

    private async void ReportTranslation_Click(object sender, RoutedEventArgs e) => await ReportTranslationAsync();

    /// <summary>
    /// 報告する文字列を選ぶダイアログ (UI-41 の仕様 1)。現在の表示言語の訳文・英語の原文・リソースキーで検索し、1 つ選ぶか「選ばずに報告」。
    /// その後、翻訳の報告用の Issue フォームを、欄を入力済みにしてブラウザで開く (仕様 2)。
    /// </summary>
    private async Task ReportTranslationAsync()
    {
        IReadOnlyList<ResourceString> strings = ResourceStrings.All(_language);
        var search = new TextBox { PlaceholderText = Loc.Get("TranslationReport_SearchPlaceholder") };
        AutomationProperties.SetAutomationId(search, "TranslationReport_Search");
        AutomationProperties.SetName(search, Loc.Get("TranslationReport_SearchPlaceholder"));
        var list = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "TranslationReport_List");
        AutomationProperties.SetName(list, Loc.Get("TranslationReport_ListName"));

        void Fill()
        {
            list.Items.Clear();
            foreach (ResourceString s in TranslationReport.Filter(strings, search.Text, s => s.Key, s => s.English, s => s.Current).Take(500))
            {
                var row = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock { Text = s.Key, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], FlowDirection = FlowDirection.LeftToRight });
                row.Children.Add(new TextBlock { Text = s.English, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight });
                if (s.Current != s.English)
                {
                    row.Children.Add(new TextBlock { Text = s.Current, TextWrapping = TextWrapping.Wrap });
                }

                var item = new ListViewItem { Content = row, Tag = s };
                AutomationProperties.SetName(item, s.Key);
                list.Items.Add(item);
            }
        }

        Fill();
        search.TextChanged += (_, _) => Fill();
        var body = new StackPanel { Spacing = 8, MinWidth = 480 };
        body.Children.Add(new TextBlock { Text = Loc.Get("TranslationReport_Body"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(search);
        body.Children.Add(list);
        ContentDialog dialog = NewDialog(Loc.Get("TranslationReport_Title"), body);
        AutomationProperties.SetAutomationId(dialog, "TranslationReportDialog");
        dialog.PrimaryButtonText = Loc.Get("TranslationReport_Report");
        dialog.SecondaryButtonText = Loc.Get("TranslationReport_ReportWithout");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.IsPrimaryButtonEnabled = false;
        dialog.DefaultButton = ContentDialogButton.Primary;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is ListViewItem;
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        ResourceString? chosen = result == ContentDialogResult.Primary ? (list.SelectedItem as ListViewItem)?.Tag as ResourceString : null;
        Uri url = TranslationReport.IssueUrl(_language, chosen?.Key, chosen?.Current, Program.Environment.AppVersion);
        await OpenUriAsync(url, translationReport: true);
    }

    // ---- 表示言語 (UI-43。設定画面「言語」ができるまでの仮の画面: 表示 > 表示言語) ----

    private async void DisplayLanguage_Click(object sender, RoutedEventArgs e) => await ChooseDisplayLanguageAsync();

    /// <summary>表示言語の一覧の項目 (UI-43 の仕様 2): 先頭は「Windows の設定に従う (現在: 言語名)」、その後に 23 言語。</summary>
    public IReadOnlyList<(string Tag, string Text)> DisplayLanguageItems()
    {
        IReadOnlyDictionary<string, LanguageCoverage> coverage = TranslationCoverage.Load(AppContext.BaseDirectory);
        string system = DisplayLanguages.Resolve(Localization.WindowsLanguages());
        var items = new List<(string, string)>
        {
            (DisplayLanguages.System, Loc.Format("Language_System", DisplayLanguages.Find(system)!.NativeName)),
        };
        foreach (DisplayLanguage language in DisplayLanguages.All)
        {
            string local = LocalName(language.Tag);
            int reviewed = coverage.TryGetValue(language.Tag, out LanguageCoverage? c) ? c.Reviewed : 0;
            items.Add((language.Tag, Loc.Format("Language_Item", language.NativeName, local, reviewed)));
        }

        return items;
    }

    /// <summary>今の表示言語での言語名 (例: 日本語表示で「ドイツ語」)。</summary>
    private static string LocalName(string tag)
    {
        try
        {
            return new CultureInfo(tag).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    private async Task ChooseDisplayLanguageAsync()
    {
        IReadOnlyList<(string Tag, string Text)> items = DisplayLanguageItems();
        var combo = new ComboBox { MinWidth = 360, Header = Loc.Get("Language_Header") };
        AutomationProperties.SetAutomationId(combo, "Settings_Language");
        foreach ((string tag, string text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            AutomationProperties.SetName(item, text);
            combo.Items.Add(item);
        }

        string current = App.Settings.GetString(DisplayLanguages.SettingKey, DisplayLanguages.System);
        combo.SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => i.Tag.Equals(current, StringComparison.OrdinalIgnoreCase)));
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(combo);
        body.Children.Add(new TextBlock { Text = Loc.Get("Language_RestartNote"), TextWrapping = TextWrapping.Wrap });
        ContentDialog dialog = NewDialog(Loc.Get("Language_Title"), body);
        AutomationProperties.SetAutomationId(dialog, "LanguageDialog");
        dialog.PrimaryButtonText = Loc.Get("Language_Apply");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && combo.SelectedItem is ComboBoxItem { Tag: string chosen })
        {
            SetDisplayLanguage(chosen);
        }
    }

    /// <summary>
    /// 表示言語を変える (UI-43 の仕様 5): 設定 <c>ui.language</c> に書き、「再起動後に反映されます」と「今すぐ再起動」を出す。
    /// 設定画面 (UI-22) の「言語」からもこれを呼ぶ。
    /// </summary>
    public void SetDisplayLanguage(string tag)
    {
        App.Settings.SetString(DisplayLanguages.SettingKey, tag, DisplayLanguages.System);
        ShowNotice(Loc.Get("Language_RestartNotice"), InfoBarSeverity.Informational, actions:
        [
            new Core.Notifications.NotificationAction(Loc.Get("Language_RestartNow"), () => _ = RestartForLanguageAsync()),
        ]);
    }

    /// <summary>「今すぐ再起動」: 未保存の変更を確認してから再起動し、開いていたファイルを開き直す (UI-43 の仕様 5)。</summary>
    private async Task RestartForLanguageAsync()
    {
        List<string> files = [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()];
        if (!await CloseAsync(Vm.Documents.ToList()))
        {
            return;
        }

        if (!AppRestart.Restart(files))
        {
            ShowNotice(Loc.Get("Language_RestartFailed"), InfoBarSeverity.Warning);
        }
    }

    // ---- ジャンプリスト (UI-35) ----

    private void StartJumpList(IAppEnvironment env)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HexEditor.exe");
        _jumpList = new JumpListService(RecentFiles, env.AppUserModelId, exe, DispatcherQueue);
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            AddRecent(doc);
        }

        Vm.Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace && e.NewItems is { } added)
            {
                foreach (DocumentViewModel doc in added.OfType<DocumentViewModel>())
                {
                    AddRecent(doc);
                }
            }
        };
        App.Settings.Changed += keys =>
        {
            if (keys.Contains(JumpListPlan.EnabledKey))
            {
                DispatcherQueue.TryEnqueue(() => _jumpList?.Schedule());
            }
        };
        _jumpList.Schedule();
    }

    private void AddRecent(DocumentViewModel doc)
    {
        if (doc.FilePath is { } path && ReferenceEquals(RecentFiles, _sessionRecent))
        {
            _sessionRecent.Add(path);
        }
    }

    // ---- Explorer 連携 (UI-54 の仕様 1・6) ----

    private void CheckExplorerRegistration()
    {
        try
        {
            ShellIntegration shell = ExplorerIntegration.Shell;
            if (!shell.Supported)
            {
                return;
            }

            // 表示言語を変えたら、メニューの名前を登録し直す (UI-54 の仕様 1)。
            if (shell.RefreshLabels())
            {
                AppLog.Info("Explorer menu name updated for the display language.");
            }

            // ポータブル版のフォルダを移動した: 「右クリックメニューの登録が古い場所を指しています」(仕様 6)。
            if (shell.State().Stale)
            {
                ShowNotice(Loc.Get("Shell_StaleRegistration"), InfoBarSeverity.Warning, actions:
                [
                    new Core.Notifications.NotificationAction(Loc.Get("Shell_UpdateRegistration"), () => ReportShellFailures(shell.Register())),
                    new Core.Notifications.NotificationAction(Loc.Get("Shell_Unregister"), () => ReportShellFailures(shell.Unregister())),
                ]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warning($"Explorer registration check failed: {ex.GetType().Name}");
        }
    }

    private void ReportShellFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count > 0)
        {
            AppLog.Warning("Explorer registration failed: " + string.Join(", ", failures));
            ShowNotice(Loc.Format("Shell_RegistrationFailed", string.Join(", ", failures)), InfoBarSeverity.Error);
        }
    }

    // ---- 他の配布形態の設定の取り込み (PKG-31) ----

    /// <summary>
    /// 初回起動時に、他の配布形態の設定があれば、スタートページに「<配布形態> の HexEditor の設定を取り込む」を出す (PKG-31 の仕様 1)。
    /// スタートページの「はじめに」(UI-38) ができたらそこに移す。
    /// </summary>
    private void OfferSettingsImport(IAppEnvironment env)
    {
        if (!_firstRun || StartPage.Content is not StackPanel page)
        {
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (OtherDistributionData other in SettingsMigration.Find(env.Distribution, env.Locations.Settings, localAppData))
        {
            var button = new Button { Content = Loc.Format("Migration_Import", Loc.Get("Distribution_" + other.Distribution)) };
            AutomationProperties.SetAutomationId(button, "Start_ImportFrom_" + other.Distribution);
            button.Click += (_, _) =>
            {
                ImportSettingsFrom(other, MigrationCategories.All);
                button.IsEnabled = false;
            };
            page.Children.Add(button);
        }
    }

    /// <summary>取り込む (UI-25 のインポートと同じ区分。元のフォルダは変えない)。失敗した区分は一覧で示す (PKG-31 の「エラー」)。</summary>
    public MigrationResult ImportSettingsFrom(OtherDistributionData other, MigrationCategories categories)
    {
        MigrationResult result = SettingsMigration.Import(other.Folder, Program.Environment.Locations.Settings, categories, (key, value) =>
        {
            if (value is System.Text.Json.Nodes.JsonValue v)
            {
                if (v.TryGetValue(out bool b))
                {
                    App.Settings.SetBool(key, b, !b);
                }
                else if (v.TryGetValue(out int i))
                {
                    App.Settings.SetInt(key, i, i == 0 ? 1 : 0);
                }
                else if (v.TryGetValue(out string? s))
                {
                    App.Settings.SetString(key, s, s.Length == 0 ? "-" : string.Empty);
                }
            }
        });
        App.Settings.Flush();
        ApplyAppearance();
        ApplyEditorSettings();
        AppLog.Info($"Imported settings from {other.Distribution}: {result.Imported}");
        if (result.Failed.Count > 0)
        {
            ShowNotice(Loc.Format("Migration_Failed", string.Join(", ", result.Failed.Select(f => f.Category))), InfoBarSeverity.Warning);
        }
        else
        {
            ShowNotice(Loc.Get("Migration_Done"), InfoBarSeverity.Success);
        }

        return result;
    }
}
