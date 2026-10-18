using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Templates.Online;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// オンラインのテンプレート (07 の TPL-36) のつなぎ込み: コマンド (「テンプレート: オンラインで探す」「テンプレート: 更新を確認」)、
/// インストール (拡張関数を使うものは確認する。仕様 9)、起動時の更新の確認 (1 日 1 回。新しい版があれば InfoBar で知らせ、自動では更新
/// しない。設定で自動更新にできる。仕様 5)。
/// </summary>
public sealed partial class MainWindow
{
    private static int _onlineUpdateCheckStarted;

    private void RegisterOnlineTemplateCommands()
    {
        Commands.Register("templates.online.browse", OpenOnlineTemplates);
        Commands.Register("templates.online.checkUpdates", () => CheckOnlineTemplateUpdatesAsync(scheduled: false));
    }

    /// <summary>テンプレートの管理画面の「オンライン」を開く。</summary>
    private void OpenOnlineTemplates()
    {
        OpenSettingsPage(Views.TemplateSettingsSection.Category);
        DispatcherQueue.TryEnqueue(() => _settingsPage?.BringIntoView("TplOnline"));
    }

    /// <summary>起動時の更新の確認 (アプリ全体で 1 回。最初のウィンドウを表示した後にバックグラウンドで)。</summary>
    internal void StartOnlineTemplateUpdateCheck()
    {
        if (Interlocked.Exchange(ref _onlineUpdateCheckStarted, 1) == 0)
        {
            _ = CheckOnlineTemplateUpdatesAsync(scheduled: true);
        }
    }

    /// <summary>
    /// 新しい版を確認する。<paramref name="scheduled"/> は起動時の確認 (1 日 1 回。通信がオフなら何もしない)。手動の確認は結果を必ず知らせる。
    /// </summary>
    internal async Task CheckOnlineTemplateUpdatesAsync(bool scheduled)
    {
        if (!scheduled && !OnlineTemplateService.DownloadsAllowed)
        {
            ShowNotice(Loc.Get("TplOnline_Disabled"), InfoBarSeverity.Informational, null,
                actions: [new NotificationAction(Loc.Get("TplOnline_OpenSettings"), () => OpenSettingsPage(Core.Settings.SettingCategories.Privacy, Platform.Network.NetworkPolicy.OfflineKey))]);
            return;
        }

        IReadOnlyList<OnlineTemplateUpdate>? updates;
        try
        {
            updates = await Task.Run(() => OnlineTemplateService.FindUpdatesAsync(scheduled, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Warning($"Online templates: update check failed: {ex.GetType().Name}");
            return;
        }

        if (updates is null)
        {
            if (!scheduled)
            {
                ShowNotice(Loc.Get("TplOnline_NoUpdates"), InfoBarSeverity.Informational, null);
            }

            return;
        }

        if (updates.Count == 0)
        {
            if (!scheduled)
            {
                ShowNotice(Loc.Get("TplOnline_NoUpdates"), InfoBarSeverity.Informational, null);
            }

            return;
        }

        if (App.Settings.GetBool(OnlineTemplateService.AutoUpdateKey, false))
        {
            await UpdateOnlineTemplatesAsync(updates);
            return;
        }

        CultureInfo ui = CultureInfo.CurrentUICulture;
        string names = string.Join(", ", updates.Select(u => $"{u.Latest.DisplayName(ui)} {u.Latest.Version}"));
        ShowNotice(Loc.Format("TplOnline_UpdatesAvailable", names), InfoBarSeverity.Informational, null,
            actions: [new NotificationAction(Loc.Get("TplOnline_Update"), () => _ = UpdateOnlineTemplatesAsync(updates))]);
    }

    private async Task UpdateOnlineTemplatesAsync(IReadOnlyList<OnlineTemplateUpdate> updates)
    {
        foreach (OnlineTemplateUpdate u in updates)
        {
            await InstallOnlineTemplateAsync(u.Listing, u.Latest);
        }

        Views.TemplateSettingsSection.RefreshAll();
    }

    /// <summary>
    /// インストールする (長時間処理としてキャンセルできる)。拡張関数を使うテンプレートは「スクリプトの機能を使います」と確認する (仕様 9)。
    /// 照合に失敗したらインストールせず「ファイルが改ざんされている可能性があります」と知らせる。
    /// </summary>
    internal async Task<bool> InstallOnlineTemplateAsync(TemplateRepositoryListing listing, OnlineTemplateEntry entry)
    {
        string name = entry.DisplayName(CultureInfo.CurrentUICulture);
        if (entry.UsesExtensions && !await ConfirmOnlineTemplateExtensionsAsync(name))
        {
            return false;
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Format("TplOnline_Downloading", name), OperationKind.ReadOnly, null, null,
                async op => await OnlineTemplateService.InstallAsync(listing, entry, op.CancellationToken));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TemplateTamperedException)
        {
            ShowNotice(Loc.Format("TplOnline_FileTamperedNamed", name), InfoBarSeverity.Error, null);
            return false;
        }
        catch (Exception ex) when (ex is TemplateFeedUnavailableException or TemplateFeedTooLargeException or IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("TplOnline_Unavailable", ex.Message), InfoBarSeverity.Error, null);
            return false;
        }

        ShowNotice(Loc.Format("TplOnline_Installed", name, entry.Version), InfoBarSeverity.Success, null);
        return true;
    }

    private async Task<bool> ConfirmOnlineTemplateExtensionsAsync(string name)
    {
        ContentDialog dialog = NewDialog(Loc.Format("TplOnline_ExtensionsTitle", name), Loc.Get("TplOnline_ExtensionsText"));
        AutomationProperties.SetAutomationId(dialog, "TplOnline_ExtensionsDialog");
        dialog.PrimaryButtonText = Loc.Get("TplOnline_Install");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>「ソースの表示」(仕様 4): テンプレートの本文を読み取り専用で見せる。</summary>
    internal async Task ShowOnlineTemplateSourceAsync(string name, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Controls.DialogParts.Mono,
            MinWidth = 480,
            MaxHeight = 420,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(box, "TplOnline_SourceText");
        AutomationProperties.SetName(box, name);
        ContentDialog dialog = NewDialog(Loc.Format("TplOnline_SourceTitle", name), box);
        AutomationProperties.SetAutomationId(dialog, "TplOnline_SourceDialog");
        dialog.CloseButtonText = Loc.Get("Common_Close");
        await dialog.ShowQueuedAsync();
    }
}
