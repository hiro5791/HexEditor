using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Processes;
using HexEditor.Core.Remote.Gdb;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// GDB サーバーに接続 (ENG-40): 接続ダイアログ (ホスト・ポート・アドレスの幅・読み取り専用で開く)、対象のメモリのタブ、保存 (ENG-34 と同じ確認と
/// 書き込み)、エラーの表示。実行の停止・再開などデバッガとしての操作はしない (仕様 7)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>接続ダイアログ (ENG-40 の仕様 1)。<paramref name="preset"/> は前回の接続先 (セッションのタブ)。接続できたらタブを返す。</summary>
    private async Task<DocumentViewModel?> ConnectGdbAsync((GdbEndpoint Endpoint, bool ReadOnly)? preset)
    {
        GdbEndpoint initial = preset?.Endpoint ?? new GdbEndpoint();
        var host = DialogParts.Field("Gdb_Host", Loc.Get("Gdb_Host"), initial.Host, monospace: false);
        var port = DialogParts.Field("Gdb_Port", Loc.Get("Gdb_Port"), initial.Port.ToString(CultureInfo.InvariantCulture));
        var width = DialogParts.Combo("Gdb_AddressWidth", Loc.Get("Gdb_AddressWidth"), [Loc.Get("Gdb_Width32"), Loc.Get("Gdb_Width64")],
            initial.AddressBits == 32 ? 0 : 1);
        var readOnly = DialogParts.Check("Gdb_ReadOnly", Loc.Get("Gdb_ReadOnly"), preset?.ReadOnly ?? true);
        var note = new TextBlock { Text = Loc.Get("Gdb_Note"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Opacity = 0.8 };
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        panel.Children.Add(host);
        panel.Children.Add(port);
        panel.Children.Add(width);
        panel.Children.Add(readOnly);
        panel.Children.Add(note);
        ContentDialog dialog = DialogParts.Dialog(Root, "GdbConnectDialog", Loc.Get("Gdb_Title"), panel, Loc.Get("Gdb_Connect"));
        int portValue = 0;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            bool hostOk = host.Text.Trim().Length > 0;
            bool portOk = int.TryParse(port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out portValue) && portValue is >= 1 and <= 65535;
            DialogParts.MarkInvalid(host, !hostOk);
            DialogParts.MarkInvalid(port, !portOk);
            e.Cancel = !hostOk || !portOk;
        };
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var endpoint = new GdbEndpoint
        {
            Host = host.Text.Trim(),
            Port = portValue,
            AddressBits = width.SelectedIndex == 0 ? 32 : 64,
            Timeout = RemoteServices.GdbTimeout,
        };
        return await ConnectGdbCoreAsync(endpoint, readOnly.IsChecked == true);
    }

    /// <summary>接続してタブを開く (テスト用の命令からも呼ぶ)。接続できなければ理由を示して null。</summary>
    internal async Task<DocumentViewModel?> ConnectGdbCoreAsync(GdbEndpoint endpoint, bool readOnly)
    {
        ShowStatusMessage(Loc.Format("Gdb_Connecting", endpoint.Display));
        GdbByteSource source;
        try
        {
            source = await GdbByteSource.ConnectAsync(endpoint, writable: !readOnly);
        }
        catch (GdbProtocolException)
        {
            ShowNotice(Loc.Get("Gdb_ProtocolError"), InfoBarSeverity.Error);
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            LastGdbConnectError = Loc.Format("Gdb_ConnectFailed", endpoint.Display, ex.Message);
            ShowNotice(LastGdbConnectError, InfoBarSeverity.Error);
            return null;
        }

        AppLog.Info($"GDB connected: {endpoint.Display} ({endpoint.AddressBits} bit)");
        DocumentViewModel doc = Vm.OpenGdb(source, readOnly);
        WatchRemoteState(doc);
        UpdateTitle();
        QueueStatusBarLayout();
        return doc;
    }

    /// <summary>最後の接続の失敗の文 (テスト用)。</summary>
    internal string? LastGdbConnectError { get; private set; }

    /// <summary>保存 (ENG-40 の仕様 6): ENG-34 と同じく、初回だけ確認してから変更範囲を書き込む。</summary>
    private async Task<bool> SaveGdbAsync(DocumentViewModel doc)
    {
        if (doc.Gdb is not { } gdb)
        {
            return false;
        }

        if (!doc.Document.DataSnapshot.EnumerateModifiedRanges().Any() && doc.Document.LastDeviceWriteRanges.Count == 0)
        {
            return true;
        }

        if (gdb.State != Core.Remote.RemoteConnectionState.Connected)
        {
            ShowNotice(Loc.Format("Remote_Disconnected", gdb.Endpoint.Display), InfoBarSeverity.Warning, doc,
                actions: [new NotificationAction(Loc.Get("Remote_Reconnect"), () => _ = ReconnectRemoteAsync(doc))]);
            return false;
        }

        if (!doc.RemoteWriteConfirmed)
        {
            long bytes = doc.Document.DataSnapshot.EnumerateModifiedRanges().Sum(r => r.Length);
            if (!await ConfirmAsync(Loc.Get("ProcessWrite_Title"), Loc.Format("Gdb_WriteBody", gdb.Endpoint.Display, bytes),
                Loc.Get("ProcessWrite_Write"), "GdbWriteConfirmDialog"))
            {
                return false;
            }

            doc.RemoteWriteConfirmed = true;
        }

        return await WriteGdbAsync(doc, gdb);
    }

    /// <summary>変更範囲を対象に書き込み、ドキュメントに反映する。失敗した範囲は一覧で示す (ENG-34 の「エラー」と同じ)。</summary>
    private async Task<bool> WriteGdbAsync(DocumentViewModel doc, GdbByteSource gdb)
    {
        ProcessWriteResult result = await GdbWrite.ExecuteAsync(doc.Document, gdb);
        if (doc.Document.IsDisposed)
        {
            return false;
        }

        doc.Document.CompleteTargetWrite(gdb, result.Written, result.AllWritten);
        doc.OnSaved();
        if (!result.AllWritten)
        {
            string text = result.Failed.Any(f => f.Reason == ProcessWriteReason.ProcessExited)
                ? Loc.Format("Gdb_WriteDisconnected", gdb.Endpoint.Display)
                : Loc.Format("ProcessWrite_Failed", result.Failed.Count) + Environment.NewLine + string.Join(Environment.NewLine,
                    result.Failed.Take(10).Select(f => Loc.Format("ProcessWrite_FailedRange", "0x" + f.Offset.ToString("X", CultureInfo.InvariantCulture),
                        f.Length.ToString("N0", CultureInfo.CurrentCulture), Loc.Get("Gdb_WriteRejected"))));
            ShowNotice(text, InfoBarSeverity.Warning, doc);
        }

        UpdateTitle();
        return result.AllWritten;
    }
}
