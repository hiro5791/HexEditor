using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Recovery;
using HexEditor.Core.Saving;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>異常終了の後始末: 復旧の画面 (ENG-27 の仕様 6・7) とクラッシュ情報の通知 (PKG-30 の仕様 2)。</summary>
public sealed partial class MainWindow
{
    private bool _recoveryErrorShown;

    /// <summary>ウィンドウの内容が読み込まれたら、復旧の提案とクラッシュ情報の通知を出す。</summary>
    public void ShowStartupNoticesWhenLoaded()
    {
        if (Root.XamlRoot is not null)
        {
            _ = ShowStartupNoticesAsync();
            return;
        }

        Root.Loaded += OnLoaded;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Root.Loaded -= OnLoaded;
            _ = ShowStartupNoticesAsync();
        }
    }

    /// <summary>復旧用データを書けない: 1 回だけ知らせる (ENG-27 の「エラー」)。</summary>
    public void ShowRecoveryWriteError(Exception error)
    {
        AppLog.Warning($"Recovery write failed: {error.GetType().Name}");
        if (_recoveryErrorShown)
        {
            return;
        }

        _recoveryErrorShown = true;
        ShowNotice(Loc.Format("Recovery_WriteFailed", error.Message), InfoBarSeverity.Warning);
    }

    private async Task ShowStartupNoticesAsync()
    {
        // 復旧の提案を先に出す (PKG-30 の仕様 2)。
        IReadOnlyList<RecoveryEntry> entries = RecoveryStore.Scan(Vm.RecoveryRoot);
        IReadOnlyList<string> journals = InPlaceSaver.FindJournals(Vm.RecoveryRoot);
        if (entries.Count > 0 || journals.Count > 0)
        {
            await ShowRecoveryDialogAsync(entries, journals);
        }

        ShowAdminDropNoticeOnce();
        if (CrashReporter.FindUnseen() is { } crash)
        {
            CrashReporter.MarkSeen(crash);
            ShowCrashNotice(crash);
        }
    }

    private void ShowCrashNotice(string crashFile)
    {
        ShowNotice(Loc.Get("Crash_Message"), InfoBarSeverity.Warning, actions:
        [
            new NotificationAction(Loc.Get("Crash_Open"), () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(crashFile))),
            new NotificationAction(Loc.Get("Crash_Report"), () => _ = OpenUriAsync(AboutInfo.IssueUrl(Program.Environment, Loc.Get("Crash_ReportNote")))),
        ]);
    }

    /// <summary>既定のブラウザで開く。開けなければ URL をクリップボードにコピーして知らせる (UI-40 の「エラー」)。</summary>
    private async Task OpenUriAsync(Uri uri)
    {
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
        {
            var package = new DataPackage();
            package.SetText(uri.ToString());
            Clipboard.SetContent(package);
            ShowNotice(Loc.Get("Notice_UrlCopied"), InfoBarSeverity.Informational);
        }
    }

    /// <summary>
    /// 復旧の画面。項目ごとに「復旧する」「破棄」を選べ、閉じる (「あとで」) と残りは次回の起動でもう一度出す。
    /// </summary>
    private async Task ShowRecoveryDialogAsync(IReadOnlyList<RecoveryEntry> entries, IReadOnlyList<string> journals)
    {
        var list = new StackPanel { Spacing = 12 };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Loc.Get("Recovery_Title"),
            CloseButtonText = Loc.Get("Recovery_Later"),
            DefaultButton = ContentDialogButton.None,
            FlowDirection = Root.FlowDirection,
        };
        AutomationProperties.SetAutomationId(dialog, "RecoveryDialog");
        int remaining = entries.Count + journals.Count;

        void Done(FrameworkElement row)
        {
            list.Children.Remove(row);
            if (--remaining == 0)
            {
                dialog.Hide();
            }
        }

        foreach (RecoveryEntry entry in entries)
        {
            RecoveryRecord record = entry.Record;
            string details = Loc.Format("Recovery_Details", record.SavedAtUtc.ToLocalTime().ToString("g"), record.ChangedBytes.ToString("N0"));
            FrameworkElement? row = null;
            row = RecoveryRow(
                record.Path is null ? record.DisplayName : Path.GetFileName(record.Path),
                record.Path ?? Loc.Get("Recovery_Untitled"),
                details,
                Loc.Get("Recovery_Restore"),
                () =>
                {
                    RestoreEntry(entry);
                    Done(row!);
                },
                () =>
                {
                    RecoveryStore.Discard(entry);
                    Done(row!);
                });
            list.Children.Add(row);
        }

        foreach (string journal in journals)
        {
            string target;
            try
            {
                target = InPlaceSaver.ReadJournalTarget(journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }

            FrameworkElement? row = null;
            row = RecoveryRow(
                Path.GetFileName(target),
                target,
                Loc.Get("Recovery_JournalDetails"),
                Loc.Get("Recovery_JournalRestore"),
                () =>
                {
                    try
                    {
                        InPlaceSaver.Rollback(journal);
                        Done(row!);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        ShowNotice(Loc.Format("Recovery_Failed", ex.Message), InfoBarSeverity.Error);
                    }
                },
                () =>
                {
                    File.Delete(journal);
                    Done(row!);
                });
            list.Children.Add(row);
        }

        if (remaining == 0)
        {
            return;
        }

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Recovery_Body"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        dialog.Content = body;
        await dialog.ShowAsync();
    }

    private static FrameworkElement RecoveryRow(string name, string location, string details, string restoreText, Action restore, Action discard)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock
        {
            Text = location,
            FlowDirection = FlowDirection.LeftToRight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        text.Children.Add(new TextBlock { Text = details, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var restoreButton = new Button { Content = restoreText, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(restoreButton, "Recovery_Restore");
        AutomationProperties.SetName(restoreButton, $"{restoreText}: {name}");
        restoreButton.Click += (_, _) => restore();
        var discardButton = new Button { Content = Loc.Get("Recovery_Discard") };
        AutomationProperties.SetAutomationId(discardButton, "Recovery_Discard");
        AutomationProperties.SetName(discardButton, $"{Loc.Get("Recovery_Discard")}: {name}");
        discardButton.Click += (_, _) => discard();
        buttons.Children.Add(restoreButton);
        buttons.Children.Add(discardButton);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    private void RestoreEntry(RecoveryEntry entry)
    {
        RestoredDocument restored;
        try
        {
            restored = RecoveryStore.Restore(entry, Vm.DocumentOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 失敗したら理由を示し、復旧用データは残す (「破棄」で消せる)。
            ShowNotice(Loc.Format("Recovery_Failed", ex.Message), InfoBarSeverity.Error);
            return;
        }

        DocumentViewModel vm = Vm.AddRestored(restored);
        AppLog.Info("Recovered a document");
        if (restored.SourceChanged)
        {
            ShowNotice(Loc.Get("Recovery_SourceChanged"), InfoBarSeverity.Warning, vm);
        }

        UpdateTitle();
    }
}
