using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Files;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Sftp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// リモートのデータソース (ENG-40〜ENG-42) の共通部分: コマンドの登録、切断の通知と再接続、接続状態の表示の更新、セッションのリモートのタブの
/// 「接続」ボタン。通信は利用者が接続の操作をしたときと、その接続を使っている間だけ行う (00 の 11.4。起動時・セッションの復元では接続しない)。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterRemoteCommands()
    {
        Commands.Register("file.saveProcessSnapshot", () => SaveProcessCaptureAsync(minidump: false),
            () => NeedsDocument(d => d.IsProcessMemory ? null : Loc.Get("Command_ProcessOnly")));
        Commands.Register("file.saveMinidump", () => SaveProcessCaptureAsync(minidump: true),
            () => NeedsDocument(d => d.IsProcessMemory ? null : Loc.Get("Command_ProcessOnly")));
        Commands.Register("file.connectGdb", () => ConnectGdbAsync(null));
        Commands.Register("file.openSftp", () => OpenSftpAsync(null, null));
        Commands.Register("file.receiveUdp", ReceiveUdpAsync);
        Commands.Register("file.reconnect", () => Vm.Selected is { } doc ? ReconnectRemoteAsync(doc) : Task.CompletedTask,
            () => NeedsDocument(d => d.IsRemote ? d.RemoteState == RemoteConnectionState.Disconnected ? null : Loc.Get("Remote_AlreadyConnected")
                : Loc.Get("Remote_NotRemote")));
        HookUdpPanel();
    }

    private readonly HashSet<(DocumentViewModel, object)> _watchedRemote = [];

    /// <summary>接続状態の変化で、ステータスバーとコマンドの状態を更新する (任意のスレッドから呼ばれる)。同じ接続には 1 回だけ。</summary>
    private void WatchRemoteState(DocumentViewModel doc)
    {
        object? key = doc.Document.Source switch
        {
            GdbByteSource g => g,
            SftpByteSource s => s.Connection,
            _ => null,
        };
        if (key is null || !_watchedRemote.Add((doc, key)))
        {
            return;
        }

        void Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!doc.Document.IsDisposed)
            {
                doc.NotifyStatusChanged();
                QueueStatusBarLayout();
                RefreshCommandUi();
            }
        });

        switch (doc.Document.Source)
        {
            case GdbByteSource gdb:
                gdb.StateChanged += Changed;
                break;
            case SftpByteSource sftp:
                sftp.Connection.StateChanged += Changed;
                break;
        }
    }

    /// <summary>切断の通知 (ENG-40 の仕様 8、ENG-41 の仕様 7): 「再接続」ボタンを出す。未保存の変更は残る。表示した文を返す。</summary>
    private string ShowRemoteDisconnected(DocumentViewModel doc)
    {
        string message = doc.Gdb is { } gdb
            ? gdb.LastError is GdbProtocolException ? Loc.Get("Gdb_ProtocolError") : Loc.Format("Remote_Disconnected", gdb.Endpoint.Display)
            : Loc.Format("Remote_Disconnected", doc.Sftp?.Connection.Endpoint.Display ?? doc.DisplayName);
        ShowNotice(message, InfoBarSeverity.Warning, doc,
            actions: [new NotificationAction(Loc.Get("Remote_Reconnect"), () => _ = ReconnectRemoteAsync(doc))]);
        doc.NotifyStatusChanged();
        QueueStatusBarLayout();
        return message;
    }

    /// <summary>「再接続」(ENG-40 の仕様 8、ENG-41 の仕様 7): 接続し直し、キャッシュを捨てて表示中の範囲を読み直す。</summary>
    internal async Task ReconnectRemoteAsync(DocumentViewModel doc)
    {
        try
        {
            switch (doc.Document.Source)
            {
                case GdbByteSource gdb:
                    await gdb.ReconnectAsync();
                    break;
                case SftpByteSource sftp:
                    await sftp.ReconnectAsync();
                    break;
                default:
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("Remote_ReconnectFailed", ex.Message), InfoBarSeverity.Error, doc,
                actions: [new NotificationAction(Loc.Get("Remote_Reconnect"), () => _ = ReconnectRemoteAsync(doc))]);
            return;
        }

        if (!doc.Document.IsDisposed)
        {
            doc.Document.RefreshFromSource();
            Vm.Notifications.DismissOwnedBy(doc);
            doc.NotifyStatusChanged();
            ShowStatusMessage(Loc.Get("Remote_Reconnected"));
        }

        QueueStatusBarLayout();
        RefreshCommandUi();
    }

    /// <summary>ステータスバーの接続の項目: 切断していれば再接続、UDP ならパケットのパネルを出す。</summary>
    private void StatusRemote_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.StatusDocument is not { } doc)
        {
            return;
        }

        if (doc.Udp is not null)
        {
            ShowPanel(PacketsPanelId, focus: true);
        }
        else if (doc.RemoteState == RemoteConnectionState.Disconnected)
        {
            _ = ReconnectRemoteAsync(doc);
        }
    }

    /// <summary>
    /// セッションのリモートのタブの「接続」(ENG-40・ENG-41 の仕様 9): 記録した接続先を入れた接続ダイアログを出す (自動では接続しない)。
    /// 接続できたら同じ位置のタブにする。
    /// </summary>
    private void MissingConnectRemote_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { IsMissingRemote: true } doc)
        {
            _ = ConnectRestoredRemoteAsync(doc);
        }
    }

    internal async Task ConnectRestoredRemoteAsync(DocumentViewModel missing)
    {
        if (missing.MissingRecord is not { Path: { } path } record)
        {
            return;
        }

        DocumentViewModel? opened = SftpUri.TryParse(path) is { } uri
            ? await OpenSftpAsync(uri.Endpoint, uri.Path)
            : GdbEndpoint.TryParseUri(path) is { } gdb ? await ConnectGdbAsync(gdb) : null;
        if (opened is null)
        {
            return;
        }

        int index = Vm.Documents.IndexOf(missing);
        if (index >= 0)
        {
            Vm.Close(missing);
            Vm.MoveDocument(opened, Math.Min(index, Vm.Documents.Count - 1));
        }

        opened.RestorePosition(record.Cursor, record.SelectionStart, record.SelectionLength, record.TopRow);
        Vm.Selected = opened;
        UpdateTitle();
    }
}
