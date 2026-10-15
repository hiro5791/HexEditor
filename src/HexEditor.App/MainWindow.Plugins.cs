using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Services.Plugins;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Plugins;
using HexEditor.Core.View;
using HexEditor.Sdk.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// プラグイン (08 の AUTO-29〜AUTO-35) のウィンドウ側: プラグインマネージャのページ、コマンド・メニュー (ツール &gt; プラグイン、右クリックの
/// 「プラグイン」、ファイル &gt; プラグインのデータソースを開く、データ &gt; プラグインのデータ演算、形式を選択してコピー)、信頼の確認のダイアログ、
/// エラーが続いたときの InfoBar、セーフモードの InfoBar、前回の異常終了の確認。
/// </summary>
public sealed partial class MainWindow : IPluginManagerHost
{
    private const string PluginMenuTag = "PluginMenu";
    private const string PluginManagerPageId = "plugins";

    private PluginManagerPage? _pluginManagerPage;
    private PluginManagerViewModel? _pluginManagerVm;

    /// <summary>プラグインのコマンドを登録する (<see cref="RegisterCommandHandlers"/> から呼ぶ)。</summary>
    private void RegisterPluginCommands()
    {
        Commands.Register("plugins.manage", OpenPluginManager);
        AttachPluginCommands();
        EventHandler commands = (_, _) =>
        {
            AttachPluginCommands();
            RebuildPluginMenus();
        };
        EventHandler changed = (_, _) =>
        {
            _pluginManagerVm?.Refresh();
            RebuildPluginMenus();
            _inspectorVm.Refresh();
        };
        EventHandler<PluginRecord> suspended = (_, p) => ShowPluginSuspended(p);
        PluginService.CommandsChanged += commands;
        PluginService.Changed += changed;
        PluginService.Suspended += suspended;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                PluginService.CommandsChanged -= commands;
                PluginService.Changed -= changed;
                PluginService.Suspended -= suspended;
            }
        };
    }

    // ---- プラグインマネージャ (AUTO-32) ----

    /// <summary>「プラグイン: 管理」: プラグインマネージャのページを開き、一覧にフォーカスを置く。</summary>
    public void OpenPluginManager()
    {
        _pluginManagerVm ??= new PluginManagerViewModel(this);
        _pluginManagerPage ??= new PluginManagerPage(_pluginManagerVm);
        _pluginManagerVm.Refresh();
        ShowToolPage(PluginManagerPageId, _pluginManagerPage, Loc.Get("ToolPage_Plugins"), "");
        DispatcherQueue.TryEnqueue(() => _pluginManagerPage.FocusList());
    }

    async Task<string?> IPluginManagerHost.PickPackageAsync()
    {
        const string settingsIdentifier = "HexEditor.Plugin";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add(PluginPackage.Extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    async Task<string?> IPluginManagerHost.PickFolderAsync()
    {
        const string settingsIdentifier = "HexEditor.PluginDevFolder";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FolderPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    void IPluginManagerHost.OpenFolder(string folder)
    {
        PluginService.LastOpenedFolder = folder;
        if (TestHooks.Active)
        {
            // テスト用のビルドでは Explorer を開かない (前面に出るため)。開こうとしたフォルダは LastOpenedFolder で確かめる。
            return;
        }

        ShowInFolder(folder);
    }

    Task IPluginManagerHost.RestartAsync() => RestartAsync();

    async Task<bool> IPluginManagerHost.ConfirmRemoveAsync(string name)
    {
        ContentDialog dialog = NewDialog(Loc.Get("PluginManager_RemoveTitle"), Loc.Format("PluginManager_RemoveBody", name));
        AutomationProperties.SetAutomationId(dialog, "PluginRemoveDialog");
        dialog.PrimaryButtonText = Loc.Get("PluginManager_Remove");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    void IPluginManagerHost.ShowPluginSettings(PluginRecord plugin)
    {
        if (PluginPanels.Settings(plugin) is not { } content)
        {
            return;
        }

        ContentDialog dialog = NewDialog(Loc.Format("PluginManager_SettingsTitle", plugin.NameFor(PluginService.UiLanguage)), new ScrollViewer { Content = content, MaxHeight = 480 });
        AutomationProperties.SetAutomationId(dialog, "PluginSettingsDialog");
        dialog.CloseButtonText = Loc.Get("Common_Close");
        _ = dialog.ShowQueuedAsync();
    }

    // ---- 信頼の確認 (AUTO-34 の仕様 2・8、AUTO-35) ----

    /// <summary>
    /// プラグインの信頼の確認: 名前・パス・発行者・署名の状態、完全信頼のコードである旨、インターネットから取得した旨、管理者として実行中の警告。
    /// ボタンは「有効にする」「有効にしない」。署名が有効なら「この発行者を常に信頼する」を選べる。
    /// </summary>
    internal async Task<PluginTrustAnswer> ConfirmPluginTrustAsync(PluginTrustPrompt prompt)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        void Line(string text, string id, bool ltr = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            if (ltr)
            {
                block.FlowDirection = FlowDirection.LeftToRight;
            }

            AutomationProperties.SetAutomationId(block, id);
            panel.Children.Add(block);
        }

        if (prompt.Elevated)
        {
            Line(Loc.Get("PluginTrust_Elevated"), "PluginTrust_Elevated");
        }

        Line(Loc.Format(prompt.Update ? "PluginTrust_NameUpdate" : "PluginTrust_Name", prompt.Name, prompt.Version), "PluginTrust_Name");
        Line(prompt.Folder, "PluginTrust_Path", ltr: true);
        if (prompt.Publisher.Length > 0)
        {
            Line(Loc.Format("PluginTrust_Publisher", prompt.Publisher), "PluginTrust_Publisher");
        }

        Line(SignatureLine(prompt.Signature), "PluginTrust_Signature");
        if (prompt.Development)
        {
            Line(Loc.Get("PluginTrust_Development"), "PluginTrust_Development");
        }

        Line(Loc.Get("PluginTrust_FullTrust"), "PluginTrust_Description");
        if (prompt.Permissions.Count > 0)
        {
            Line(Loc.Format("PluginTrust_Permissions", string.Join(", ", prompt.Permissions)), "PluginTrust_Permissions");
        }

        if (prompt.Signature.State == PluginSignatureState.Unsigned)
        {
            var unsigned = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Message = Loc.Get("PluginTrust_UnsignedWarning") };
            AutomationProperties.SetAutomationId(unsigned, "PluginTrust_Unsigned");
            panel.Children.Add(unsigned);
        }

        if (prompt.FromInternet)
        {
            var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Message = Loc.Get("ScriptTrust_FromInternet") };
            AutomationProperties.SetAutomationId(warning, "PluginTrust_FromInternet");
            panel.Children.Add(warning);
        }

        CheckBox? trustPublisher = null;
        if (prompt.Signature is { State: PluginSignatureState.Valid, Thumbprint: not null } && !prompt.Elevated)
        {
            trustPublisher = new CheckBox { Content = Loc.Format("PluginTrust_AlwaysTrustPublisher", prompt.Signature.Publisher ?? string.Empty) };
            AutomationProperties.SetAutomationId(trustPublisher, "PluginTrust_TrustPublisher");
            panel.Children.Add(trustPublisher);
        }

        ContentDialog dialog = NewDialog(Loc.Format("PluginTrust_Title", prompt.Name), new ScrollViewer { Content = panel, MaxHeight = 480 });
        AutomationProperties.SetAutomationId(dialog, "PluginTrustDialog");
        dialog.PrimaryButtonText = Loc.Get("PluginTrust_Enable");
        dialog.CloseButtonText = Loc.Get("PluginTrust_DontEnable");
        dialog.DefaultButton = prompt.Elevated ? ContentDialogButton.Close : ContentDialogButton.Primary;
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return PluginTrustAnswer.DontEnable;
        }

        return trustPublisher?.IsChecked == true ? PluginTrustAnswer.EnableAndTrustPublisher : PluginTrustAnswer.Enable;
    }

    private static string SignatureLine(PluginSignatureInfo signature) => signature.State switch
    {
        PluginSignatureState.Valid when signature.RevocationUnchecked => Loc.Format("PluginManager_SignedRevocationUnchecked", signature.Publisher ?? string.Empty),
        PluginSignatureState.Valid => Loc.Format("PluginManager_Signed", signature.Publisher ?? string.Empty),
        PluginSignatureState.Invalid => Loc.Get("PluginManager_SignatureInvalid"),
        _ => Loc.Get("PluginManager_Unsigned"),
    };

    /// <summary>
    /// 管理者として実行中の起動時の確認 (AUTO-34 の仕様 8): 読み込むプラグインの一覧をまとめて 1 回。ボタンは「読み込む」「読み込まない」で、
    /// 既定は「読み込まない」。信頼されていないものは同じダイアログで信頼の確認を兼ねる。
    /// </summary>
    internal async Task<bool> ConfirmElevatedPluginsAsync(IReadOnlyList<PluginTrustPrompt> plugins)
    {
        var panel = new StackPanel { Spacing = 8, MaxWidth = 520 };
        var intro = new TextBlock { Text = Loc.Get("PluginTrust_ElevatedBatch"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(intro, "PluginTrust_Elevated");
        panel.Children.Add(intro);
        foreach (PluginTrustPrompt p in plugins)
        {
            string line = Loc.Format("PluginTrust_ElevatedItem", p.Name, p.Version, SignatureLine(p.Signature))
                + (p.Trusted ? string.Empty : " " + Loc.Get("PluginTrust_ElevatedUntrusted"));
            var item = new TextBlock { Text = "• " + line, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(item, "PluginTrust_Item_" + p.Id);
            panel.Children.Add(item);
            var path = new TextBlock { Text = p.Folder, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(16, 0, 0, 0) };
            panel.Children.Add(path);
        }

        if (plugins.Any(p => !p.Trusted))
        {
            panel.Children.Add(new TextBlock { Text = Loc.Get("PluginTrust_FullTrust"), TextWrapping = TextWrapping.Wrap });
        }

        ContentDialog dialog = NewDialog(Loc.Get("PluginTrust_ElevatedTitle"), new ScrollViewer { Content = panel, MaxHeight = 480 });
        AutomationProperties.SetAutomationId(dialog, "PluginElevatedDialog");
        dialog.PrimaryButtonText = Loc.Get("PluginTrust_Load");
        dialog.CloseButtonText = Loc.Get("PluginTrust_DontLoad");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    // ---- エラーが続いたとき・セーフモード・前回の異常終了 (AUTO-30 の仕様 7・8) ----

    /// <summary>InfoBar「プラグイン「名前」でエラーが続いたため停止しました」(ボタン: 「プラグインを無効にする」「再起動時にもう一度試す」)。</summary>
    private void ShowPluginSuspended(PluginRecord plugin)
    {
        if (WindowManager.Current != this)
        {
            return;
        }

        string name = plugin.NameFor(PluginService.UiLanguage);
        Core.Notifications.Notification? notice = null;
        notice = ShowNotice(Loc.Format("Plugin_Suspended", name), InfoBarSeverity.Warning, actions:
        [
            new Core.Notifications.NotificationAction(Loc.Get("Plugin_DisableButton"), () =>
            {
                PluginService.Manager?.SetEnabled(plugin.Id, false);
                _pluginManagerVm?.Refresh();
                ShowNotice(Loc.Format("Plugin_DisabledRestart", name), InfoBarSeverity.Informational);
            }),
            new Core.Notifications.NotificationAction(Loc.Get("Plugin_RetryButton"), () => { }),
        ]);
    }

    /// <summary>起動の後の案内: セーフモードの InfoBar (09 の UI-30 の仕様 4) と、前回の異常終了の原因と考えられるプラグインの確認。</summary>
    internal void ShowPluginStartupNotices()
    {
        if (PluginService.SafeMode)
        {
            ShowNotice(Loc.Get("Plugin_SafeModeNotice"), InfoBarSeverity.Informational, actions:
            [
                new Core.Notifications.NotificationAction(Loc.Get("Plugin_RestartNormally"), () => _ = RestartNormallyAsync()),
            ]);
        }
    }

    /// <summary>「通常どおり再起動」: <c>--safe-mode</c> を外して起動し直す。</summary>
    private async Task RestartNormallyAsync()
    {
        AppRestart.DropSafeMode = true;
        await RestartAsync();
        AppRestart.DropSafeMode = false;
    }

    /// <summary>前回の異常終了の呼び出し履歴にプラグインがあった: 無効にするか確認する (読み込みが終わってから)。</summary>
    internal async Task AskAboutCrashedPluginAsync()
    {
        if (PluginService.CrashSuspect is not { } id || PluginService.Manager?.Find(id) is not { } plugin)
        {
            return;
        }

        string name = plugin.NameFor(PluginService.UiLanguage);
        ContentDialog dialog = NewDialog(Loc.Get("Plugin_CrashTitle"), Loc.Format("Plugin_CrashBody", name));
        AutomationProperties.SetAutomationId(dialog, "PluginCrashDialog");
        dialog.PrimaryButtonText = Loc.Get("Plugin_DisableButton");
        dialog.CloseButtonText = Loc.Get("Plugin_KeepEnabled");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary)
        {
            PluginService.Manager.SetEnabled(id, false);
            ShowNotice(Loc.Format("Plugin_DisabledRestart", name), InfoBarSeverity.Informational);
        }
    }

    // ---- コマンド ----

    /// <summary>登録されたプラグインのコマンドに、このウィンドウの処理をつなぐ。</summary>
    private void AttachPluginCommands()
    {
        foreach (string id in PluginService.Commands.Keys)
        {
            if (!Commands.HasHandler(id))
            {
                string key = id;
                Commands.Register(key, () => RunPluginCommandAsync(key), () => PluginCommandState(key));
            }
        }
    }

    private CommandState PluginCommandState(string id)
    {
        if (PluginService.CommandOf(id) is not { } registration || PluginService.Manager is not { } m || !registration.IsUsable)
        {
            return CommandState.Unavailable(Loc.Get("Plugin_CommandUnavailable"));
        }

        return registration.Extension switch
        {
            IByteSourceProvider => CommandState.Available,
            IExportFormat or IDataTransform when Vm.Selected is null => CommandState.Unavailable(Loc.Get("Command_NoDocument")),
            IDataTransform when Vm.Selected is { } doc && doc.Editor.ReadOnly => CommandState.Unavailable(Loc.Get("Command_ReadOnly")),
            ICommand => PluginCommands.CanExecute(m, registration, PluginDocumentOf(Vm.Selected))
                ? CommandState.Available
                : CommandState.Unavailable(registration.Guard.IsStopped ? registration.Guard.Stopped() : Loc.Get("Plugin_CommandUnavailable")),
            _ => CommandState.Available,
        };
    }

    private async Task RunPluginCommandAsync(string id)
    {
        if (PluginService.CommandOf(id) is not { } registration || PluginService.Manager is not { } m)
        {
            return;
        }

        switch (registration.Extension)
        {
            case IByteSourceProvider provider when id.EndsWith(".open", StringComparison.Ordinal):
                await OpenPluginSourceAsync(registration, provider);
                break;
            case IExportFormat format when id.EndsWith(".copyAs", StringComparison.Ordinal):
                await CopyAsPluginFormatAsync(registration, format);
                break;
            case IDataTransform transform when id.EndsWith(".apply", StringComparison.Ordinal):
                await ApplyPluginTransformAsync(registration, transform);
                break;
            case ICommand:
                if (!PluginCommands.Execute(m, registration, PluginDocumentOf(Vm.Selected)))
                {
                    ShowNotice(Loc.Format("Plugin_CommandFailed", registration.DisplayName, registration.Plugin.NameFor(PluginService.UiLanguage)), InfoBarSeverity.Error);
                }

                break;
        }
    }

    private static PluginDocument? PluginDocumentOf(DocumentViewModel? doc) => doc is null
        ? null
        : new PluginDocument(doc.Document, doc.DisplayName, doc.FilePath, () => doc.Editor.Cursor, () => (doc.Editor.SelectionStart, doc.Editor.SelectionLength));

    /// <summary>プラグインのデータソースを開く (引数を尋ねるものは入力ダイアログを出す)。</summary>
    private async Task OpenPluginSourceAsync(RegisteredExtension registration, IByteSourceProvider provider)
    {
        string? label = registration.Guard.Invoke("ArgumentLabel", () => provider.ArgumentLabel, null);
        string? argument = null;
        if (label is not null)
        {
            TextBox box = DialogParts.Field("PluginSource_Argument", label, string.Empty, monospace: false);
            ContentDialog dialog = DialogParts.Dialog(Root, "PluginSourceDialog", Loc.Format("Plugin_OpenSourceCommand", registration.DisplayName), box, Loc.Get("Common_Ok"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            argument = box.Text.Trim();
        }

        try
        {
            PluginByteSource source = PluginByteSource.Open(registration, provider, argument);
            Vm.OpenPluginSource(source);
            UpdateTitle();
        }
        catch (PluginCallException ex)
        {
            ShowNotice(Loc.Format("Plugin_OpenSourceFailed", registration.DisplayName, ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>形式を選択してコピー (03 の EDIT-25) のプラグインの形式: 選択範囲 (なければ全体) をテキストとしてコピーする。</summary>
    private async Task CopyAsPluginFormatAsync(RegisteredExtension registration, IExportFormat format)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        IReadOnlyList<(long Offset, long Length)> ranges = editor.HasSelection ? SelectionRangeList(editor) : [(0, doc.Document.Length)];
        long total = ranges.Sum(r => r.Length);
        if (total > ClipboardService.SystemLimit)
        {
            ShowNotice(Loc.Format("CopyAs_TooLargeNote", StatusFormat.ShortSize(ClipboardService.SystemLimit, Culture) ?? string.Empty), InfoBarSeverity.Warning, doc);
            return;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        var source = new ExportSource { Read = (o, d) => snapshot.Read(o, d), Length = snapshot.Length, FileName = doc.DisplayName };
        try
        {
            string text = await Task.Run(() =>
            {
                using var output = new MemoryStream();
                PluginIntegration.Export(registration, format, source, ranges, output, CancellationToken.None, null);
                return Encoding.UTF8.GetString(output.ToArray());
            });
            _clipboard.CopyFormatted(text);
            ShowStatusMessage(Loc.Format("Plugin_Copied", registration.DisplayName));
        }
        catch (PluginCallException ex)
        {
            ShowNotice(ex.Message, InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>プラグインのデータ演算 (03): 選択範囲 (なければ全体) をチャンクごとに変換し、1 つの編集グループで反映する。</summary>
    private async Task ApplyPluginTransformAsync(RegisteredExtension registration, IDataTransform transform)
    {
        if (Vm.Selected is not { } doc || doc.Editor.ReadOnly)
        {
            return;
        }

        IReadOnlyList<TargetRange> ranges = SelectionRangesOf(doc);
        if (ranges.Count == 0)
        {
            ranges = [new TargetRange(0, doc.Document.Length)];
        }

        DataOperationRunner runner = DataOperationRunner.For(doc.Document, TestHooks.Volumes ?? Core.Saving.SystemVolumeInfoProvider.Instance);
        DocumentSnapshot snapshot = doc.Document.Current;
        string name = Loc.Format("Plugin_DataTransformCommand", registration.DisplayName);
        DataOperationResult? result;
        try
        {
            result = await RunTransformAsync(doc, name, DataOperationRunner.TotalBytes(ranges), DataOperationRunner.IsLongRunning(ranges),
                op => runner.RunTransform(snapshot, ranges, (data, offset) =>
                {
                    if (registration.Guard.IsStopped)
                    {
                        throw new PluginCallException(registration.Guard.Stopped());
                    }

                    try
                    {
                        transform.Apply(offset, data, new PluginOperation(op?.CancellationToken ?? CancellationToken.None));
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PluginCallException))
                    {
                        registration.Guard.Fault("Apply", ex);
                        throw new PluginCallException(ex.Message, ex);
                    }
                }, op));
        }
        catch (PluginCallException ex)
        {
            ShowNotice(Loc.Format("Plugin_DataTransformFailed", registration.DisplayName, ex.Message), InfoBarSeverity.Error, doc);
            return;
        }

        if (result is null)
        {
            return;
        }

        if (!ApplyEdit(doc, () => TransformApplier.Apply(doc.Document, result.Replacements, name)))
        {
            TransformApplier.DisposeAll(result.Replacements);
        }
    }

    // ---- メニュー ----

    /// <summary>
    /// ツール &gt; プラグイン (メニューの項目 IMenuContribution と「プラグインの管理...」)、ファイル &gt; プラグインのデータソースを開く、
    /// データ &gt; プラグインのデータ演算 を作り直す。
    /// </summary>
    internal void RebuildPluginMenus()
    {
        if (PluginService.Manager is not { } m)
        {
            return;
        }

        RemoveTagged(PluginsMenu.Items);
        var contributions = m.Registry.Usable<IMenuContribution>()
            .Where(x => x.Registration.Guard.Invoke("Location", () => x.Extension.Location, MenuLocation.ContextMenu) == MenuLocation.ToolsPlugins).ToList();
        if (contributions.Count > 0)
        {
            PluginsMenu.Items.Add(new MenuFlyoutSeparator { Tag = PluginMenuTag });
            foreach ((RegisteredExtension registration, IMenuContribution menu) in contributions)
            {
                if (MenuItemFor(registration, menu) is { } item)
                {
                    PluginsMenu.Items.Add(item);
                }
            }
        }

        RemoveTagged(PluginSourcesMenu.Items);
        foreach ((RegisteredExtension registration, IByteSourceProvider _) in m.Registry.Usable<IByteSourceProvider>())
        {
            PluginSourcesMenu.Items.Add(CommandItem(PluginService.CommandIdOf(registration.Id) + ".open", registration.DisplayName, registration));
        }

        PluginSourcesMenu.Visibility = PluginSourcesMenu.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RemoveTagged(PluginDataMenu.Items);
        foreach ((RegisteredExtension registration, IDataTransform _) in m.Registry.Usable<IDataTransform>())
        {
            PluginDataMenu.Items.Add(CommandItem(PluginService.CommandIdOf(registration.Id) + ".apply", registration.DisplayName, registration));
        }

        PluginDataMenu.Visibility = PluginDataMenu.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void RemoveTagged(IList<MenuFlyoutItemBase> items)
    {
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Tag as string == PluginMenuTag)
            {
                items.RemoveAt(i);
            }
        }
    }

    private MenuFlyoutItem? MenuItemFor(RegisteredExtension registration, IMenuContribution menu)
    {
        string commandId = registration.Guard.Invoke("CommandId", () => menu.CommandId, string.Empty);
        if (PluginService.Manager?.Registry.Find(commandId) is not { Extension: ICommand } command || command.Plugin != registration.Plugin)
        {
            return null;
        }

        return CommandItem(PluginService.CommandIdOf(commandId), registration.DisplayName, registration);
    }

    private MenuFlyoutItem CommandItem(string commandId, string text, RegisteredExtension registration)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = PluginMenuTag };
        AutomationProperties.SetAutomationId(item, "PluginMenu_" + commandId);

        // 本体の機能と同じ場所に出し、ツールチップにプラグイン名を出す (AUTO-31 の仕様 2)。
        ToolTipService.SetToolTip(item, Loc.Format("Plugin_ToolTip", registration.Plugin.NameFor(PluginService.UiLanguage)));
        item.Click += (_, _) => _ = Commands.ExecuteAsync(commandId);
        return item;
    }

    /// <summary>Hex ビューの右クリックメニュー: 「プラグイン」(IMenuContribution の ContextMenu) と、「形式を選択してコピー」のプラグインの形式。</summary>
    private void ExtendHexViewPluginMenu(MenuFlyout menu)
    {
        RemoveTagged(menu.Items);
        if (PluginService.Manager is not { } m)
        {
            return;
        }

        var contributions = m.Registry.Usable<IMenuContribution>()
            .Where(x => x.Registration.Guard.Invoke("Location", () => x.Extension.Location, MenuLocation.ToolsPlugins) == MenuLocation.ContextMenu).ToList();
        if (contributions.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = Loc.Get("HexView_Menu_Plugins"), Tag = PluginMenuTag };
            AutomationProperties.SetAutomationId(sub, "HexViewMenu_Plugins");
            foreach ((RegisteredExtension registration, IMenuContribution contribution) in contributions)
            {
                if (MenuItemFor(registration, contribution) is { } item)
                {
                    item.IsEnabled = Commands.StateOf((string)AutomationProperties.GetAutomationId(item)["PluginMenu_".Length..]).Enabled;
                    sub.Items.Add(item);
                }
            }

            menu.Items.Add(sub);
        }

        if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == CopyAsMenuId) is MenuFlyoutSubItem copyAs)
        {
            RemoveTagged(copyAs.Items);
            var formats = m.Registry.Usable<IExportFormat>().Where(x => x.Registration.Guard.Invoke("IsText", () => x.Extension.IsText, false)).ToList();
            if (formats.Count > 0)
            {
                copyAs.Items.Add(new MenuFlyoutSeparator { Tag = PluginMenuTag });
                foreach ((RegisteredExtension registration, IExportFormat _) in formats)
                {
                    copyAs.Items.Add(CommandItem(PluginService.CommandIdOf(registration.Id) + ".copyAs", registration.DisplayName, registration));
                }
            }
        }
    }
}
