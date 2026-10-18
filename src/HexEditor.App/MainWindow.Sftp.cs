using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Saving;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// SFTP で開く (ENG-41): 接続ダイアログ (ホスト・ポート・ユーザー・認証方式・「保存する」)、ホスト鍵の確認 (初回は「信頼して接続」、鍵が変わったら
/// 警告して既定では接続しない)、リモートのファイルの選択画面、保存 (変更範囲だけ / 一時ファイルと名前の変更)、外部変更の確認 (アクティブ化時と
/// 30 秒ごと)。パスワード・パスフレーズは「保存する」を選んだ場合だけ資格情報マネージャーに保存し、ログ・設定ファイルには書かない。
/// </summary>
public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sftpCheckTimer;

    /// <summary>
    /// 接続ダイアログを出し、接続してファイルを選んで開く。<paramref name="preset"/> と <paramref name="presetPath"/> は最近使ったファイル・セッションの
    /// 接続先 (パスがあればファイルの選択画面を出さずに開く)。
    /// </summary>
    private async Task<DocumentViewModel?> OpenSftpAsync(SftpEndpoint? preset, string? presetPath)
    {
        SftpLoginInput? login = await AskSftpLoginAsync(preset, null);
        while (login is not null)
        {
            (SftpConnection? connection, string? error) = await ConnectSftpAsync(login.Endpoint, login.Credentials);
            if (connection is null)
            {
                if (error is null)
                {
                    return null;
                }

                // 認証の失敗・接続の失敗: 理由を示してダイアログを出し直す。
                login = await AskSftpLoginAsync(login.Endpoint, error);
                continue;
            }

            SftpLogin.RememberSecret(RemoteServices.Credentials, login.Endpoint, login.Credentials, login.Save);
            string? path = presetPath ?? await PickRemoteFileAsync(connection);
            if (path is null)
            {
                connection.Dispose();
                return null;
            }

            return await OpenSftpFileAsync(connection, path);
        }

        return null;
    }

    /// <summary>最近使ったファイルの <c>sftp://…</c> を開く (接続ダイアログを接続先を入れた状態で出す)。</summary>
    private async Task OpenSftpUriAsync(string text)
    {
        if (SftpUri.TryParse(text) is { } uri)
        {
            await OpenSftpAsync(uri.Endpoint, uri.Path);
        }
    }

    private sealed record SftpLoginInput(SftpEndpoint Endpoint, SftpCredentials Credentials, bool Save);

    /// <summary>接続ダイアログ (ENG-41 の仕様 1)。</summary>
    private async Task<SftpLoginInput?> AskSftpLoginAsync(SftpEndpoint? preset, string? error)
    {
        var host = DialogParts.Field("Sftp_Host", Loc.Get("Sftp_Host"), preset?.Host ?? string.Empty, monospace: false);
        var port = DialogParts.Field("Sftp_Port", Loc.Get("Sftp_Port"), (preset?.Port ?? 22).ToString(CultureInfo.InvariantCulture));
        var user = DialogParts.Field("Sftp_User", Loc.Get("Sftp_User"), preset?.User ?? Environment.UserName, monospace: false);
        var method = DialogParts.Combo("Sftp_Method", Loc.Get("Sftp_Method"),
            [Loc.Get("Sftp_MethodPassword"), Loc.Get("Sftp_MethodKeyFile"), Loc.Get("Sftp_MethodAgent")], 0);
        var password = new PasswordBox { Header = Loc.Get("Sftp_Password"), MinWidth = 220 };
        AutomationProperties.SetAutomationId(password, "Sftp_Password");
        AutomationProperties.SetName(password, Loc.Get("Sftp_Password"));
        var keyFile = DialogParts.Field("Sftp_KeyFile", Loc.Get("Sftp_KeyFile"), string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("Capture_Browse"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(browse, "Sftp_KeyBrowse");
        var passphrase = new PasswordBox { Header = Loc.Get("Sftp_Passphrase"), MinWidth = 220 };
        AutomationProperties.SetAutomationId(passphrase, "Sftp_Passphrase");
        AutomationProperties.SetName(passphrase, Loc.Get("Sftp_Passphrase"));
        var save = DialogParts.Check("Sftp_Save", Loc.Get("Sftp_Save"), false);
        var message = DialogParts.Caption("Sftp_Error");
        message.Text = error ?? string.Empty;
        message.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;

        if (preset is not null && SftpLogin.SavedSecret(RemoteServices.Credentials, preset) is { Length: > 0 } saved)
        {
            password.Password = saved;
            save.IsChecked = true;
        }

        var keyRow = new Grid { ColumnSpacing = 8 };
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyRow.Children.Add(keyFile);
        Grid.SetColumn(browse, 1);
        keyRow.Children.Add(browse);
        browse.Click += async (_, _) =>
        {
            IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.SshKey");
            if (paths is null)
            {
                var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.SshKey" };
                picker.FileTypeFilter.Add("*");
                paths = await picker.PickSingleFileAsync() is { } file ? [file.Path] : [];
            }

            if (paths.Count > 0)
            {
                keyFile.Text = paths[0];
            }
        };

        void UpdateMethod()
        {
            password.Visibility = method.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            keyRow.Visibility = passphrase.Visibility = method.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            save.Visibility = method.SelectedIndex == 2 ? Visibility.Collapsed : Visibility.Visible;
        }

        method.SelectionChanged += (_, _) => UpdateMethod();
        UpdateMethod();
        var panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        foreach (UIElement e in new UIElement[] { message, host, port, user, method, password, keyRow, passphrase, save })
        {
            panel.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "SftpConnectDialog", Loc.Get("Sftp_Title"), new ScrollViewer { Content = panel },
            Loc.Get("Sftp_Connect"));
        int portValue = 22;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            bool hostOk = host.Text.Trim().Length > 0;
            bool portOk = int.TryParse(port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out portValue) && portValue is >= 1 and <= 65535;
            bool userOk = user.Text.Trim().Length > 0;
            bool keyOk = method.SelectedIndex != 1 || keyFile.Text.Trim().Length > 0;
            DialogParts.MarkInvalid(host, !hostOk);
            DialogParts.MarkInvalid(port, !portOk);
            DialogParts.MarkInvalid(user, !userOk);
            DialogParts.MarkInvalid(keyFile, !keyOk);
            e.Cancel = !(hostOk && portOk && userOk && keyOk);
        };
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var endpoint = new SftpEndpoint { Host = host.Text.Trim(), Port = portValue, User = user.Text.Trim() };
        var credentials = new SftpCredentials
        {
            Method = (SftpAuthMethod)method.SelectedIndex,
            Password = method.SelectedIndex == 0 ? password.Password : null,
            KeyFile = method.SelectedIndex == 1 ? keyFile.Text.Trim() : null,
            Passphrase = method.SelectedIndex == 1 && passphrase.Password.Length > 0 ? passphrase.Password : null,
        };
        return new SftpLoginInput(endpoint, credentials, save.IsChecked == true && method.SelectedIndex != 2);
    }

    /// <summary>
    /// 接続する。ホスト鍵を信頼していなければ確認・警告のダイアログを出し (ENG-41 の仕様 2)、信頼したら表に加えて接続し直す。
    /// 失敗したら (null, 理由)。利用者が取り消したら (null, null)。
    /// </summary>
    internal async Task<(SftpConnection? Connection, string? Error)> ConnectSftpAsync(SftpEndpoint endpoint, SftpCredentials credentials)
    {
        ShowStatusMessage(Loc.Format("Sftp_Connecting", endpoint.Display));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                SftpConnection connection = await SftpConnection.ConnectAsync(endpoint, credentials, RemoteServices.Connector, RemoteServices.KnownHosts);
                AppLog.Info($"SFTP connected: {endpoint.Display} ({credentials.Method})");
                return (connection, null);
            }
            catch (HostKeyRejectedException ex)
            {
                if (!await ConfirmHostKeyAsync(ex))
                {
                    return (null, null);
                }

                RemoteServices.KnownHosts.Trust(endpoint.Host, endpoint.Port, ex.Key, DateTime.UtcNow);
            }
            catch (SftpException ex)
            {
                string text = ex.Failure switch
                {
                    SftpFailure.Authentication => Loc.Format("Sftp_AuthFailed", endpoint.Display),
                    SftpFailure.Permission => Loc.Format("Sftp_PermissionDenied", ex.Message),
                    _ => Loc.Format("Sftp_ConnectFailed", endpoint.Display, ex.Message),
                };
                LastSftpError = text;
                return (null, text);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
            {
                LastSftpError = Loc.Format("Sftp_ConnectFailed", endpoint.Display, ex.Message);
                return (null, LastSftpError);
            }
        }

        return (null, null);
    }

    /// <summary>最後の接続の失敗の文 (テスト用)。</summary>
    internal string? LastSftpError { get; private set; }

    /// <summary>
    /// ホスト鍵の確認 (ENG-41 の仕様 2): 初めてのホストは鍵の種類と SHA-256 のフィンガープリントを示して「信頼して接続」「キャンセル」を選ばせる。
    /// 以前と違う鍵は警告し、既定のボタン (キャンセル) では接続しない。信頼するなら true。
    /// </summary>
    private async Task<bool> ConfirmHostKeyAsync(HostKeyRejectedException ex)
    {
        bool changed = ex.Status == HostKeyStatus.Changed;
        var text = new TextBlock
        {
            Text = changed
                ? Loc.Format("Sftp_HostKeyChangedBody", ex.Endpoint.Display, ex.Key.Algorithm, ex.Key.Fingerprint,
                    ex.Previous?.Algorithm ?? string.Empty, ex.Previous?.Fingerprint ?? string.Empty)
                : Loc.Format("Sftp_HostKeyUnknownBody", ex.Endpoint.Display, ex.Key.Algorithm, ex.Key.Fingerprint),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AutomationProperties.SetAutomationId(text, "Sftp_HostKeyMessage");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get(changed ? "Sftp_HostKeyChangedTitle" : "Sftp_HostKeyUnknownTitle"),
            Content = text,
            PrimaryButtonText = Loc.Get(changed ? "Sftp_HostKeyReplace" : "Sftp_HostKeyTrust"),
            CloseButtonText = Loc.Get("Common_Cancel"),

            // 鍵が変わった場合は既定では接続しない (Enter で「キャンセル」)。初めてのホストも、確かめずに Enter で信頼しないよう「キャンセル」。
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, changed ? "SftpHostKeyChangedDialog" : "SftpHostKeyDialog");
        LastHostKeyDialog = (dialog.Title?.ToString(), text.Text, dialog.DefaultButton.ToString());
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>最後に出したホスト鍵のダイアログ (見出し・本文・既定のボタン。テスト用)。</summary>
    internal (string? Title, string Message, string DefaultButton)? LastHostKeyDialog { get; private set; }

    /// <summary>リモートのファイルの選択画面 (ENG-41 の仕様 1): フォルダの一覧。選んだファイルのパスを返す。</summary>
    private async Task<string?> PickRemoteFileAsync(SftpConnection connection)
    {
        string folder = connection.Session.HomeDirectory is { Length: > 0 } home ? home : "/";
        var pathBox = DialogParts.Field("SftpPicker_Path", Loc.Get("SftpPicker_Path"), folder, monospace: false);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Height = 300, MinWidth = 420 };
        AutomationProperties.SetAutomationId(list, "SftpPicker_List");
        AutomationProperties.SetName(list, Loc.Get("SftpPicker_List"));
        var status = DialogParts.Caption("SftpPicker_Status");
        var up = new Button { Content = Loc.Get("SftpPicker_Up") };
        AutomationProperties.SetAutomationId(up, "SftpPicker_Up");
        IReadOnlyList<SftpFileInfo> entries = [];
        string? chosen = null;
        ContentDialog? dialog = null;

        async Task LoadAsync(string path)
        {
            try
            {
                entries = [.. (await connection.Session.ListAsync(path)).OrderBy(e => !e.IsDirectory).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)];
                folder = path;
                pathBox.Text = path;
                list.Items.Clear();
                foreach (SftpFileInfo e in entries)
                {
                    string name = e.Path[(e.Path.TrimEnd('/').LastIndexOf('/') + 1)..];
                    list.Items.Add(e.IsDirectory ? name + "/" : $"{name}    {RemoteSize(e.Length)}");
                }

                status.Text = string.Empty;
            }
            catch (SftpException ex)
            {
                status.Text = Loc.Format("SftpPicker_ListFailed", ex.Message);
            }
        }

        void Activate(int index)
        {
            if (index < 0 || index >= entries.Count)
            {
                return;
            }

            if (entries[index].IsDirectory)
            {
                _ = LoadAsync(entries[index].Path);
            }
            else
            {
                chosen = entries[index].Path;
                dialog?.Hide();
            }
        }

        list.DoubleTapped += (_, _) => Activate(list.SelectedIndex);
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Activate(list.SelectedIndex);
                e.Handled = true;
            }
        };
        up.Click += (_, _) =>
        {
            string trimmed = folder.TrimEnd('/');
            _ = LoadAsync(trimmed.LastIndexOf('/') is var slash and > 0 ? trimmed[..slash] : "/");
        };
        pathBox.KeyDown += (sender, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _ = LoadAsync(pathBox.Text.Trim());
                e.Handled = true;
            }
        };
        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(pathBox);
        up.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(up, 1);
        bar.Children.Add(up);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(bar);
        panel.Children.Add(list);
        panel.Children.Add(status);
        dialog = DialogParts.Dialog(Root, "SftpPickerDialog", Loc.Format("SftpPicker_Title", connection.Endpoint.Display), panel, Loc.Get("SftpPicker_Open"));
        dialog.PrimaryButtonClick += (sender, e) =>
        {
            int index = list.SelectedIndex;
            if (index >= 0 && index < entries.Count && !entries[index].IsDirectory)
            {
                chosen = entries[index].Path;
            }
            else if (index >= 0 && index < entries.Count)
            {
                e.Cancel = true;
                _ = LoadAsync(entries[index].Path);
            }
            else if (pathBox.Text.Trim() is { Length: > 0 } typed && typed != folder)
            {
                // パスを直接入力した。
                chosen = typed;
            }
            else
            {
                e.Cancel = true;
            }
        };
        await LoadAsync(folder);
        ActiveSftpPicker = (list, pathBox);
        ContentDialogResult result = await dialog.ShowQueuedAsync();
        ActiveSftpPicker = null;
        return result == ContentDialogResult.Primary || chosen is not null ? chosen : null;
    }

    /// <summary>開いているリモートのファイルの選択画面 (テスト用)。</summary>
    internal (ListView List, TextBox Path)? ActiveSftpPicker { get; private set; }

    private static string RemoteSize(long length) =>
        Core.View.StatusFormat.ShortSize(length, CultureInfo.CurrentCulture) ?? Core.View.StatusFormat.Number(length, CultureInfo.CurrentCulture);

    /// <summary>リモートのファイルを開く (開く処理はファイルサイズに依存しない。必要な範囲だけを読む)。テスト用の命令からも呼ぶ。</summary>
    internal async Task<DocumentViewModel?> OpenSftpFileAsync(SftpConnection connection, string path)
    {
        SftpByteSource source;
        try
        {
            source = await SftpByteSource.OpenAsync(connection, path);
        }
        catch (SftpException ex)
        {
            LastSftpError = ex.Failure switch
            {
                SftpFailure.Permission => Loc.Format("Sftp_PermissionDenied", path),
                SftpFailure.NotFound => Loc.Format("Sftp_NotFound", path),
                _ => Loc.Format("Sftp_OpenFailed", path, ex.Message),
            };
            ShowNotice(LastSftpError, InfoBarSeverity.Error);
            connection.Dispose();
            return null;
        }

        DocumentViewModel doc = Vm.OpenSftp(source);
        WatchRemoteState(doc);
        EnsureSftpExternalCheck();
        UpdateTitle();
        QueueStatusBarLayout();
        return doc;
    }

    // ---- 保存 (ENG-41 の仕様 5) ----

    /// <summary>
    /// 保存: 長さが変わらず、データがずれていなければ変更範囲だけを書き込む (途中で切れたら一部だけ書き換わることを確認する。設定で省略できる)。
    /// それ以外は同じフォルダの一時ファイルに全体を書き出し、名前の変更で置き換える (長時間処理)。
    /// </summary>
    private async Task<bool> SaveSftpAsync(DocumentViewModel doc)
    {
        if (doc.Sftp is not { } source)
        {
            return false;
        }

        if (!doc.Document.IsModified)
        {
            return true;
        }

        if (source.Connection.State != RemoteConnectionState.Connected)
        {
            ShowNotice(Loc.Format("Remote_Disconnected", source.Connection.Endpoint.Display), InfoBarSeverity.Warning, doc,
                actions: [new NotificationAction(Loc.Get("Remote_Reconnect"), () => _ = ReconnectRemoteAsync(doc))]);
            return false;
        }

        SftpSavePlan plan = SftpSaver.Plan(doc.Document, source);
        if (plan.Method == SftpSaveMethod.ChangedRanges && App.Settings.GetBool(RemoteSettings.SftpConfirmPartialWriteKey, true))
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = Loc.Get("Sftp_PartialWriteBody"), TextWrapping = TextWrapping.Wrap });
            CheckBox skip = DialogParts.Check("Sftp_PartialWriteSkip", Loc.Get("Sftp_PartialWriteSkip"), false);
            body.Children.Add(skip);
            if (!await ConfirmAsync(Loc.Get("Sftp_SaveTitle"), body, Loc.Get("Sftp_SaveWrite"), "SftpPartialWriteDialog"))
            {
                return false;
            }

            if (skip.IsChecked == true)
            {
                App.Settings.SetBool(RemoteSettings.SftpConfirmPartialWriteKey, false, true);
            }
        }

        string name = Loc.Format("Sftp_Saving", doc.DisplayName);
        try
        {
            if (plan.Method == SftpSaveMethod.ChangedRanges)
            {
                IReadOnlyList<SavedRange> written = await Vm.Operations.RunAsync(name, Core.Operations.OperationKind.WritesExternal, doc.Document,
                    plan.WriteBytes, op => SftpSaver.WriteChangedRangesAsync(doc.Document, source, plan, op, op.CancellationToken),
                    locked => doc.Document.SetEditLock(locked));
                doc.Document.CompleteTargetWrite(source, written, allWritten: true);
            }
            else
            {
                SftpByteSource saved = await Vm.Operations.RunAsync(name, Core.Operations.OperationKind.WritesExternal, doc.Document, plan.Length,
                    op => SftpSaver.ReplaceAsync(doc.Document, source, op, op.CancellationToken), locked => doc.Document.SetEditLock(locked));
                doc.Document.CompleteSave(saved);
                Vm.WatchRemote(doc, saved);
            }

            doc.OnSaved();
            UpdateTitle();
            ShowStatusMessage(Loc.Format("Sftp_Saved", doc.DisplayName));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SftpException ex)
        {
            string text = ex.Failure switch
            {
                SftpFailure.Permission => Loc.Format("Sftp_SavePermission", source.RemotePath),
                SftpFailure.Disconnected when plan.Method == SftpSaveMethod.ChangedRanges => Loc.Get("Sftp_SavePartial"),
                SftpFailure.Disconnected => Loc.Get("Sftp_SaveDisconnected"),
                _ => Loc.Format("Sftp_SaveFailed", ex.Message),
            };
            ShowNotice(text, InfoBarSeverity.Error, doc);
            return false;
        }
        catch (UnreadableDataException)
        {
            ShowNotice(Loc.Get("Error_Unreadable"), InfoBarSeverity.Error, doc);
            return false;
        }
    }

    // ---- 外部変更の確認 (ENG-41 の仕様 6) ----

    private readonly HashSet<DocumentViewModel> _sftpChangeNotified = [];

    /// <summary>SFTP のタブがある間、30 秒ごとと、ウィンドウのアクティブ化時に外部変更を確かめる。</summary>
    private void EnsureSftpExternalCheck()
    {
        if (_sftpCheckTimer is not null)
        {
            return;
        }

        _sftpCheckTimer = DispatcherQueue.CreateTimer();
        _sftpCheckTimer.Interval = RemoteSettings.SftpExternalCheckInterval;
        _sftpCheckTimer.Tick += (_, _) => _ = CheckSftpExternalChangesAsync();
        _sftpCheckTimer.Start();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _ = CheckSftpExternalChangesAsync();
            }
        };
    }

    /// <summary>
    /// SFTP のタブの外部変更を確かめる (ENG-19 と同じ扱い: 未編集なら読み直すかを、編集していれば変更を捨てて読み直すかを InfoBar で示す)。
    /// 接続していないタブは確かめない (通信は接続を使っている間だけ)。テスト用の命令からも呼ぶ。
    /// </summary>
    internal async Task CheckSftpExternalChangesAsync()
    {
        List<DocumentViewModel> docs = [.. Vm.Documents.Where(d => d.Sftp is { Connection.State: RemoteConnectionState.Connected })];
        if (docs.Count == 0)
        {
            return;
        }

        foreach (DocumentViewModel doc in docs)
        {
            if (doc.Sftp is not { } source || _sftpChangeNotified.Contains(doc) || !await source.CheckExternalChangeAsync())
            {
                continue;
            }

            _sftpChangeNotified.Add(doc);
            ShowNotice(Loc.Format(doc.Document.IsModified ? "Sftp_ChangedModified" : "Sftp_Changed", source.RemotePath), InfoBarSeverity.Warning, doc,
                actions: [new NotificationAction(Loc.Get("Sftp_Reload"), () => _ = ReloadSftpAsync(doc))]);
        }
    }

    /// <summary>リモートのファイルを開き直して元データを差し替える (変更は捨てる)。</summary>
    private async Task ReloadSftpAsync(DocumentViewModel doc)
    {
        _sftpChangeNotified.Remove(doc);
        if (doc.Sftp is not { } source)
        {
            return;
        }

        try
        {
            SftpByteSource reopened = await SftpByteSource.OpenAsync(source.Connection, source.RemotePath);
            doc.Document.ReplaceSource(reopened);
            Vm.WatchRemote(doc, reopened);
            UpdateTitle();
        }
        catch (Exception ex) when (ex is SftpException or Core.Engine.DocumentLockedException)
        {
            ShowNotice(Loc.Format("Sftp_OpenFailed", source.RemotePath, ex.Message), InfoBarSeverity.Error, doc);
        }
    }
}
