using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Settings;
using HexEditor.Platform.Migration;
using HexEditor.Platform.Network;
using HexEditor.Platform.Shell;
using HexEditor.Platform.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 配布形態に関わる設定画面の専用の区画: Explorer 連携の項目ごとの登録の状態と登録・解除、「プログラムから開く」の拡張子、既定のアプリ
/// (09 の UI-54 の仕様 5・7、UI-56)、ネットワークを使う機能の一覧と各スイッチ (UI-58 の仕様 1・2)、更新の情報と「今すぐ最新の安定版に戻す」
/// (10 の PKG-19 の仕様 3、PKG-21 の仕様 4、PKG-22 の仕様 3)、詳細の「他の版から設定を取り込む」(PKG-31)、MSIX 版のアンインストールの
/// 注意 (PKG-09 の仕様 3)、ポータブル版の「この PC から登録を解除」(PKG-09 の仕様 4)。
/// </summary>
public static class PackagingSections
{
    /// <summary>Windows の「既定のアプリ」の設定画面 (UI-56 の仕様 3)。</summary>
    public static readonly Uri DefaultAppsUri = new("ms-settings:defaultapps");

    public static void Register()
    {
        Platform.Distribution distribution = Hosting.Program.Environment.Distribution;
        SettingsSections.Register(new SettingsSection(SettingCategories.Explorer, "explorer", "SetSection_Explorer", Explorer)
        {
            SearchKeys = ["SetSearch_Explorer", "Set_shell_openWith_extensions", "Shell_DefaultApps"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Privacy, "network", "SetSection_Network", Network)
        {
            SearchKeys = ["SetSearch_Network", "Set_network_downloads_enabled", "Set_network_translationReport_enabled"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Update, "updateInfo", "SetSection_UpdateInfo", UpdateInfo)
        {
            SearchKeys = ["SetSearch_UpdateInfo", "Update_ReturnToStable"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Advanced, "importOther", "SetSection_ImportOther", ImportFromOther, 20)
        {
            SearchKeys = ["SetSearch_ImportOther"],
        });

        // MSIX 版だけ: アンインストールすると設定も消える旨と「設定をエクスポート」(10 の PKG-09 の仕様 3)。
        if (distribution == Platform.Distribution.Msix)
        {
            SettingsSections.Register(new SettingsSection(SettingCategories.Advanced, "msixUninstall", "SetSection_MsixUninstall", MsixUninstall, 30)
            {
                SearchKeys = ["SetSearch_MsixUninstall"],
            });
        }

        // ポータブル版だけ: 「この PC から登録を解除」(10 の PKG-09 の仕様 4)。
        if (distribution == Platform.Distribution.Portable)
        {
            SettingsSections.Register(new SettingsSection(SettingCategories.Advanced, "unregisterPc", "SetSection_UnregisterPc", UnregisterFromThisPc)
            {
                SearchKeys = ["SetSearch_UnregisterPc"],
            });
        }
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static TextBlock Secondary(string text = "") => new()
    {
        Text = text,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    private static Button NamedButton(string text, string automationId)
    {
        var button = new Button { Content = text };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    /// <summary>区画を表示している間だけ、アプリ全体の設定の変更を購読する (閉じた設定画面の部品を残さない)。</summary>
    private static void WhileLoaded(FrameworkElement element, Action refresh)
    {
        Action<IReadOnlyCollection<string>> changed = _ => element.DispatcherQueue.TryEnqueue(() => refresh());
        element.Loaded += (_, _) =>
        {
            App.Settings.Changed -= changed;
            App.Settings.Changed += changed;
        };
        element.Unloaded += (_, _) => App.Settings.Changed -= changed;
    }

    // ---- Explorer 連携 (UI-54、UI-56) ----

    /// <summary>
    /// Explorer 連携: 右クリックメニューとファイルの関連付けを別の行にし、行ごとに状態と「登録する」「登録を解除する」を置く (UI-54 の仕様 5、
    /// UI-56)。関連付けの行には「プログラムから開く」の候補の拡張子 (チェックボックス) と「既定のアプリを設定」を置く。登録・解除の失敗は
    /// この区画に表示言語の理由で出す (UI-54・UI-56 の「エラー」)。MSIX 版は「Windows の設定で管理されます」。
    /// </summary>
    private static FrameworkElement Explorer(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 12 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        AutomationProperties.SetAutomationId(error, "Settings_ExplorerError");
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        var managed = Secondary();
        AutomationProperties.SetAutomationId(managed, "Settings_ExplorerStatus");
        var other = Secondary(Loc.Get("Shell_OtherDistribution"));
        other.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(other, "Settings_ExplorerOther");

        FrameworkElement extensions = OpenWithExtensions();
        var rows = new Dictionary<string, (TextBlock Status, Button Register, Button Unregister, StackPanel Buttons)>();
        StackPanel Row(string id, string titleKey)
        {
            string name = id == ShellRegistration.ContextMenuId ? "ContextMenu" : "FileAssociations";
            var row = new StackPanel { Spacing = 6 };
            AutomationProperties.SetAutomationId(row, "Settings_Explorer" + name);
            TextBlock heading = Heading(Loc.Get(titleKey));
            AutomationProperties.SetAutomationId(heading, $"Settings_Explorer{name}Title");
            row.Children.Add(heading);
            var status = Secondary();
            AutomationProperties.SetAutomationId(status, $"Settings_Explorer{name}Status");
            AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            Button register = NamedButton(Loc.Get("Shell_Register"), $"Settings_Explorer{name}Register");
            Button unregister = NamedButton(Loc.Get("Shell_Unregister"), $"Settings_Explorer{name}Unregister");
            AutomationProperties.SetName(register, $"{Loc.Get("Shell_Register")} ({Loc.Get(titleKey)})");
            AutomationProperties.SetName(unregister, $"{Loc.Get("Shell_Unregister")} ({Loc.Get(titleKey)})");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(register);
            buttons.Children.Add(unregister);
            row.Children.Add(status);
            row.Children.Add(buttons);
            rows[id] = (status, register, unregister, buttons);
            HashSet<string> only = [id];
            register.Click += (_, _) => Apply(ExplorerIntegration.Shell.Register(only));
            unregister.Click += (_, _) => Apply(ExplorerIntegration.Shell.Unregister(only));
            return row;
        }

        void Apply(IReadOnlyList<string> failures)
        {
            if (failures.Count > 0)
            {
                AppLog.Warning("Explorer registration failed: " + string.Join(", ", failures));
            }

            error.Text = failures.Count > 0 ? Loc.Format("Shell_RegistrationFailed", ExplorerIntegration.DescribeFailures(failures)) : string.Empty;
            error.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
        }

        StackPanel menuRow = Row(ShellRegistration.ContextMenuId, "Shell_Item_ContextMenu");
        StackPanel assocRow = Row(ShellRegistration.FileAssociationsId, "Shell_Item_FileAssociations");
        assocRow.Children.Add(extensions);
        Button defaultApps = NamedButton(Loc.Get("Shell_DefaultApps"), "Settings_ExplorerDefaultApps");
        defaultApps.Click += async (_, _) => await window.OpenSystemPageAsync(DefaultAppsUri);
        assocRow.Children.Add(defaultApps);

        panel.Children.Add(managed);
        panel.Children.Add(menuRow);
        panel.Children.Add(assocRow);
        panel.Children.Add(other);
        panel.Children.Add(error);

        void Refresh()
        {
            ShellIntegration shell = ExplorerIntegration.Shell;
            ShellIntegrationState state;
            IReadOnlyList<ShellItemState> items;
            try
            {
                state = shell.State();
                items = shell.Items();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Warning($"Explorer registration state unavailable: {ex.GetType().Name}");
                managed.Text = Loc.Get("Shell_StateUnavailable");
                managed.Visibility = Visibility.Visible;
                foreach ((_, _, _, StackPanel buttons) in rows.Values)
                {
                    buttons.Visibility = Visibility.Collapsed;
                }

                return;
            }

            // MSIX 版・開発中の実行: 登録はアプリから変えない (MSIX はマニフェストで登録。一覧の編集もできない。UI-56 の仕様 4)。
            managed.Visibility = state.Supported ? Visibility.Collapsed : Visibility.Visible;
            managed.Text = state.Supported ? string.Empty
                : Loc.Get(Hosting.Program.Environment.Distribution == Platform.Distribution.Msix ? "Shell_ManagedByWindows" : "Shell_NotAvailable");
            extensions.Visibility = state.Supported ? Visibility.Visible : Visibility.Collapsed;
            foreach ((string id, (TextBlock status, Button register, Button unregister, StackPanel buttons)) in rows)
            {
                ShellItemState? item = items.FirstOrDefault(i => i.Id == id);
                buttons.Visibility = state.Supported ? Visibility.Visible : Visibility.Collapsed;
                status.Visibility = state.Supported ? Visibility.Visible : Visibility.Collapsed;
                if (item is null)
                {
                    continue;
                }

                bool menu = id == ShellRegistration.ContextMenuId;
                status.Text = item switch
                {
                    { Ours: true } => Loc.Get(menu ? "Shell_Registered" : "Shell_ItemRegistered"),
                    { Stale: true } => Loc.Format("Shell_ItemStale", item.RegisteredExe ?? string.Empty),
                    { OtherDistribution: true } => Loc.Format("Shell_ItemOther", item.RegisteredExe ?? string.Empty),
                    _ => Loc.Get(menu ? "Shell_NotRegistered" : "Shell_ItemNotRegistered"),
                };

                // ポータブル版はインストーラ版の登録を消さない (「この PC から登録を解除」と同じ)。
                bool installerOwned = Hosting.Program.Environment.Distribution == Platform.Distribution.Portable && item.OtherDistribution;
                unregister.IsEnabled = item.Registered && !installerOwned;
                register.Content = Loc.Get(item.Stale ? "Shell_UpdateRegistration" : "Shell_Register");
            }

            other.Visibility = state.OtherDistributionRegistered ? Visibility.Visible : Visibility.Collapsed;
        }

        Refresh();
        return panel;
    }

    /// <summary>
    /// 「プログラムから開く」の候補にする拡張子 (UI-56 の仕様 2。設定 shell.openWith.extensions)。既定の 7 つと設定にある拡張子を
    /// チェックボックスで並べ、その他の拡張子を追加できる。変更は次に関連付けを登録したときに反映する。
    /// </summary>
    private static FrameworkElement OpenWithExtensions()
    {
        var panel = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(panel, "Settings_OpenWithExtensions");
        panel.Children.Add(new TextBlock { Text = Loc.Get("Set_shell_openWith_extensions"), TextWrapping = TextWrapping.Wrap });
        var boxes = new Controls.WrapPanel { HorizontalSpacing = 12, VerticalSpacing = 4 };
        AutomationProperties.SetName(boxes, Loc.Get("Set_shell_openWith_extensions"));
        panel.Children.Add(boxes);
        var add = new TextBox { PlaceholderText = Loc.Get("Shell_OpenWithAddPlaceholder"), MinWidth = 160 };
        AutomationProperties.SetAutomationId(add, "Settings_OpenWithAddText");
        AutomationProperties.SetName(add, Loc.Get("Shell_OpenWithAddPlaceholder"));
        Button addButton = NamedButton(Loc.Get("Shell_OpenWithAdd"), "Settings_OpenWithAdd");
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(add);
        addRow.Children.Add(addButton);
        panel.Children.Add(addRow);
        panel.Children.Add(Secondary(Loc.Get("Shell_OpenWithNote")));

        bool updating = false;
        IReadOnlyList<string> Current() =>
            ShellRegistration.ParseExtensions(App.Settings.GetString(ShellRegistration.OpenWithExtensionsKey, string.Empty));

        void Save()
        {
            if (updating)
            {
                return;
            }

            IEnumerable<string> chosen = boxes.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag);
            App.Settings.SetString(ShellRegistration.OpenWithExtensionsKey, ShellRegistration.FormatExtensions(chosen), string.Empty);
        }

        // 並びにない拡張子のチェックボックスを足し、チェックの状態を設定に合わせる (作り直さない: 操作中のフォーカスを失わないため)。
        void Fill(IEnumerable<string> extra)
        {
            updating = true;
            try
            {
                IReadOnlyList<string> current = Current();
                foreach (string extension in ShellRegistration.DefaultOpenWithExtensions.Union(current, StringComparer.OrdinalIgnoreCase)
                    .Union(extra, StringComparer.OrdinalIgnoreCase))
                {
                    if (boxes.Children.OfType<CheckBox>().Any(b => string.Equals((string)b.Tag, extension, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var box = new CheckBox { Content = extension, Tag = extension };
                    AutomationProperties.SetAutomationId(box, "Settings_OpenWith_" + extension.TrimStart('.'));
                    box.Checked += (_, _) => Save();
                    box.Unchecked += (_, _) => Save();
                    boxes.Children.Add(box);
                }

                foreach (CheckBox box in boxes.Children.OfType<CheckBox>())
                {
                    bool on = current.Contains((string)box.Tag, StringComparer.OrdinalIgnoreCase);
                    if (box.IsChecked != on)
                    {
                        box.IsChecked = on;
                    }
                }
            }
            finally
            {
                updating = false;
            }
        }

        addButton.Click += (_, _) =>
        {
            IReadOnlyList<string> parsed = ShellRegistration.ParseExtensions(add.Text + ";");
            if (parsed.Count == 0)
            {
                return;
            }

            IEnumerable<string> chosen = boxes.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).Concat(parsed);
            App.Settings.SetString(ShellRegistration.OpenWithExtensionsKey, ShellRegistration.FormatExtensions(chosen), string.Empty);
            add.Text = string.Empty;
            Fill(parsed);
        };
        Fill([]);
        // 外部の編集・既定に戻す: チェックの状態を設定に合わせる (外した拡張子も並びに残す)。
        WhileLoaded(panel, () => Fill([]));
        return panel;
    }

    // ---- プライバシー (UI-58) ----

    /// <summary>
    /// ネットワークを使う機能の一覧 (UI-58 の仕様 1) と各機能のスイッチ、対象外の機能の説明 (仕様 2)。<c>network.offline</c> のスイッチは
    /// 汎用の項目。同じ設定を使う機能 (テンプレートのダウンロードと追加コンポーネント) のスイッチは連動する。
    /// </summary>
    private static FrameworkElement Network(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        var list = new StackPanel { Spacing = 10 };
        AutomationProperties.SetAutomationId(list, "Settings_NetworkFeatures");
        AutomationProperties.SetName(list, Loc.Get("SetSection_Network"));
        var switches = new List<(ToggleSwitch Switch, string Key, bool Default)>();
        foreach ((NetworkFeature feature, string key, bool defaultValue) in NetworkPolicy.Features)
        {
            var row = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(row, "Settings_NetworkFeature_" + feature);
            AutomationProperties.SetName(row, Loc.Get("Network_Feature_" + feature));
            row.Children.Add(Heading(Loc.Get("Network_Feature_" + feature)));
            row.Children.Add(Secondary(Loc.Format("Network_FeatureDetail", Loc.Get("Network_Destination_" + feature),
                Loc.Get("Set_" + Core.Commands.CommandDefinition.KeyPart(key)))));
            var toggle = new ToggleSwitch { IsOn = App.Settings.GetBool(key, defaultValue) };
            AutomationProperties.SetAutomationId(toggle, "Settings_NetworkSwitch_" + feature);
            AutomationProperties.SetName(toggle, Loc.Get("Network_Feature_" + feature));
            toggle.Toggled += (_, _) =>
            {
                if (App.Settings.GetBool(key, defaultValue) != toggle.IsOn)
                {
                    App.Settings.SetBool(key, toggle.IsOn, defaultValue);
                }
            };
            switches.Add((toggle, key, defaultValue));
            row.Children.Add(toggle);
            list.Children.Add(row);
        }

        panel.Children.Add(list);
        TextBlock note = Secondary(Loc.Get("Network_UserDestinations"));
        AutomationProperties.SetAutomationId(note, "Settings_NetworkUserDestinations");
        panel.Children.Add(note);
        WhileLoaded(panel, () =>
        {
            foreach ((ToggleSwitch toggle, string key, bool defaultValue) in switches)
            {
                bool on = App.Settings.GetBool(key, defaultValue);
                if (toggle.IsOn != on)
                {
                    toggle.IsOn = on;
                }
            }
        });
        return panel;
    }

    // ---- 更新 (PKG-19、PKG-21、PKG-22) ----

    /// <summary>
    /// 更新の情報 (PKG-22 の仕様 3): 現在の版、チャネル、最後に確認した日時。MSIX 版は Store の自動更新の説明 (PKG-19 の仕様 3)。
    /// プレビュー版で stable のチャネルに戻したときは「今すぐ最新の安定版に戻す」(PKG-21 の仕様 4)。
    /// </summary>
    private static FrameworkElement UpdateInfo(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 6 };
        var version = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetAutomationId(version, "Settings_UpdateVersion");
        var channel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(channel, "Settings_UpdateChannel");
        var lastChecked = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(lastChecked, "Settings_UpdateLastChecked");
        panel.Children.Add(version);
        panel.Children.Add(channel);
        panel.Children.Add(lastChecked);
        if (Hosting.Program.Environment.Distribution == Platform.Distribution.Msix)
        {
            TextBlock store = Secondary(Loc.Get("Update_StoreNote"));
            AutomationProperties.SetAutomationId(store, "Settings_UpdateStoreNote");
            panel.Children.Add(store);
        }

        Button back = NamedButton(Loc.Get("Update_ReturnToStable"), "Settings_UpdateReturnToStable");
        TextBlock backNote = Secondary(Loc.Get("Update_ReturnToStableNote"));
        panel.Children.Add(backNote);
        panel.Children.Add(back);
        back.Click += async (_, _) =>
        {
            back.IsEnabled = false;
            try
            {
                await window.ReturnToStableAsync();
            }
            finally
            {
                back.IsEnabled = true;
                Refresh();
            }
        };

        void Refresh()
        {
            UpdateService service = AppUpdates.Service;
            version.Text = Loc.Format("Update_CurrentVersion", Hosting.Program.Environment.AppVersion);
            string channelValue = App.Settings.GetString(UpdatePreferences.ChannelKey, "stable");
            channel.Text = Loc.Format("Update_ChannelInfo", Loc.Get("SetOpt_update_channel_" + (channelValue == "preview" ? "preview" : "stable")));
            channel.Visibility = Hosting.Program.Environment.Distribution == Platform.Distribution.Msix ? Visibility.Collapsed : Visibility.Visible;
            lastChecked.Text = service.LastChecked is { } t
                ? Loc.Format("Update_LastChecked", t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
                : Loc.Get("Update_NeverChecked");
            Visibility visible = service.CanReturnToStable ? Visibility.Visible : Visibility.Collapsed;
            back.Visibility = visible;
            backNote.Visibility = visible;
        }

        EventHandler serviceChanged = (_, _) => panel.DispatcherQueue.TryEnqueue(Refresh);
        panel.Loaded += (_, _) =>
        {
            AppUpdates.Service.Changed -= serviceChanged;
            AppUpdates.Service.Changed += serviceChanged;
        };
        panel.Unloaded += (_, _) => AppUpdates.Service.Changed -= serviceChanged;
        WhileLoaded(panel, Refresh);
        Refresh();
        return panel;
    }

    // ---- 詳細 (PKG-09、PKG-31) ----

    /// <summary>「他の版から設定を取り込む」(PKG-31): この PC にある他の配布形態の設定ごとにボタンを置き、区分を選んで取り込む。</summary>
    private static FrameworkElement ImportFromOther(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        IReadOnlyList<OtherDistributionData> others = MainWindow.OtherDistributionSettings();
        TextBlock description = Secondary(Loc.Get(others.Count > 0 ? "Migration_Description" : "Migration_NoneFound"));
        AutomationProperties.SetAutomationId(description, "Settings_ImportOtherStatus");
        panel.Children.Add(description);
        foreach (OtherDistributionData other in others)
        {
            Button button = NamedButton(Loc.Format("Migration_Import", Loc.Get("Distribution_" + other.Distribution)), "Settings_ImportFrom_" + other.Distribution);
            button.Click += async (_, _) => await window.ImportSettingsFromAsync(other);
            panel.Children.Add(button);
        }

        return panel;
    }

    /// <summary>MSIX 版: 「アンインストールすると設定も削除されます。必要な場合は設定をエクスポートしてください」と「設定をエクスポート」(PKG-09 の仕様 3)。</summary>
    private static FrameworkElement MsixUninstall(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        var text = new TextBlock { Text = Loc.Get("Settings_MsixUninstallNote"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(text, "Settings_MsixUninstallNote");
        panel.Children.Add(text);
        Button export = NamedButton(Loc.Get("Settings_ExportTitle"), "Settings_MsixExport");
        export.Click += async (_, _) => await window.Commands.ExecuteAsync("settings.export");
        panel.Children.Add(export);
        return panel;
    }

    /// <summary>
    /// 「この PC から登録を解除」: レジストリの登録、ジャンプリスト、トースト通知の登録、一時フォルダを消し、
    /// 「フォルダを削除すればアンインストールは完了です」を出す。失敗した項目は一覧で出す (PKG-09 の「エラー」)。
    /// </summary>
    private static FrameworkElement UnregisterFromThisPc(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Shell_UnregisterPcDescription"), TextWrapping = TextWrapping.Wrap });
        Button button = NamedButton(Loc.Get("Shell_UnregisterPcButton"), "Settings_UnregisterPc");
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(result, "Settings_UnregisterPcResult");
        AutomationProperties.SetLiveSetting(result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        button.Click += (_, _) =>
        {
            (_, string message) = ExplorerIntegration.UnregisterFromThisPc();
            result.Text = message;
            result.Visibility = Visibility.Visible;
        };
        panel.Children.Add(button);
        panel.Children.Add(result);
        return panel;
    }
}
