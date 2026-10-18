using System.Globalization;
using System.Net;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Panels;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Udp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// UDP を受信 (ENG-42): 開始ダイアログ (アドレス・ポート・保持する上限)、受信したデータグラムを末尾に追記する読み取り専用のタブ、下部パネル
/// 「パケット」、上限に達したときの InfoBar (「古いデータを捨てて続ける」)、「名前を付けて保存」(受信したデータを 1 つのファイルとして)。
/// 受信だけを行い、送信はしない。待ち受けは利用者が開始したときだけで、起動時・セッションの復元では再開しない (仕様 8)。
/// </summary>
public sealed partial class MainWindow
{
    private const string PacketsPanelId = "packets";

    /// <summary>パネルの一覧に登録する (起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterPacketsPanel()
    {
        if (PanelRegistry.Find(PacketsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(PacketsPanelId, "Panel_Packets_Title", PanelDock.Bottom, ctx =>
            {
                var panel = new PacketsPanel(ctx);
                AutomationProperties.SetAutomationId(panel, "PacketsPanel");
                return panel;
            }));
        }
    }

    private void HookUdpPanel() => Vm.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(MainViewModel.Selected))
        {
            FindPanelContent<PacketsPanel>(PacketsPanelId)?.Refresh();
        }
    };

    /// <summary>開始ダイアログ (ENG-42 の仕様 1・7)。</summary>
    private async Task ReceiveUdpAsync()
    {
        string[] addresses = ["127.0.0.1", "0.0.0.0", "::"];
        var address = DialogParts.Combo("Udp_Address", Loc.Get("Udp_Address"), addresses, 0);
        var port = DialogParts.Field("Udp_Port", Loc.Get("Udp_Port"), string.Empty);
        port.PlaceholderText = Loc.Get("Udp_PortPlaceholder");
        long engineMiB = Math.Max(1, Vm.Memory.Limit / (1024 * 1024));
        var limit = DialogParts.Field("Udp_Limit", Loc.Format("Udp_LimitMiB", engineMiB),
            (UdpCaptureSettings.DefaultLimit / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        var firewall = DialogParts.Caption("Udp_FirewallNote");
        firewall.Text = Loc.Get("Udp_FirewallNote");
        address.SelectionChanged += (_, _) => firewall.Visibility = address.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        firewall.Visibility = Visibility.Collapsed;
        var note = new TextBlock { Text = Loc.Get("Udp_ReceiveOnly"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        foreach (UIElement e in new UIElement[] { address, firewall, port, limit, note })
        {
            panel.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "UdpStartDialog", Loc.Get("Udp_Title"), panel, Loc.Get("Udp_Start"));
        int portValue = 0;
        long limitBytes = 0;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            bool portOk = int.TryParse(port.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out portValue);
            bool limitOk = long.TryParse(limit.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mib);
            limitBytes = limitOk ? mib * 1024 * 1024 : 0;
            string? invalid = UdpCaptureSettings.Validate(portOk ? portValue : 0, limitOk ? limitBytes : 0, Vm.Memory.Limit);
            DialogParts.MarkInvalid(port, invalid == "port");
            DialogParts.MarkInvalid(limit, invalid == "limit");
            e.Cancel = invalid is not null;
        };
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        StartUdpCore(IPAddress.Parse(addresses[address.SelectedIndex]), portValue, limitBytes);
    }

    /// <summary>受信を始めてタブを開く (テスト用の命令からも呼ぶ)。始められなければ理由を示して null。</summary>
    internal DocumentViewModel? StartUdpCore(IPAddress address, int port, long limit)
    {
        UdpCapture capture;
        try
        {
            capture = UdpCapture.Start(new UdpCaptureSettings
            {
                Address = address,
                Port = port,
                Limit = limit,
                MemoryLimit = Math.Min(limit, Math.Max(UdpCaptureSettings.MinimumLimit, Vm.Memory.Limit / 4)),
                TempDirectory = Path.Combine(App.TempRoot, "udp"),
            });
        }
        catch (UdpPortInUseException ex)
        {
            LastUdpError = Loc.Format("Udp_PortInUse", ex.Port);
            ShowNotice(LastUdpError, InfoBarSeverity.Error);
            return null;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            LastUdpError = Loc.Format("Udp_StartFailed", ex.Message);
            ShowNotice(LastUdpError, InfoBarSeverity.Error);
            return null;
        }

        var source = new UdpByteSource(capture, Loc.Format("Udp_TabName", capture.LocalEndPoint.ToString()));
        DocumentViewModel doc = Vm.OpenUdp(source);
        AppLog.Info($"UDP receiving on {capture.LocalEndPoint} (limit {limit} bytes)");
        bool queued = false;
        capture.DataReceived += (_, _) =>
        {
            // 記録のたびに UI を更新しない (100 ms に 1 回までまとめる)。
            if (!queued)
            {
                queued = true;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
                {
                    await Task.Delay(100);
                    queued = false;
                    UpdateUdpDocument(doc);
                });
            }
        };
        capture.StateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => UpdateUdpDocument(doc));
        capture.LimitReached += (_, _) => DispatcherQueue.TryEnqueue(() => ShowUdpLimitReached(doc));
        ShowPanel(PacketsPanelId, focus: false);
        UpdateTitle();
        QueueStatusBarLayout();
        return doc;
    }

    /// <summary>最後の開始の失敗の文 (テスト用)。</summary>
    internal string? LastUdpError { get; private set; }

    /// <summary>
    /// 受信した分をドキュメントに反映する: 伸びただけなら末尾に加え、先頭を捨てた・消去した場合は元データを差し替える (カーソルは同じバイトを指す
    /// ように動かす)。カーソルが末尾にあれば末尾に追従する (仕様 3。設定でオフにできる)。
    /// </summary>
    internal void UpdateUdpDocument(DocumentViewModel doc)
    {
        if (doc.Document.IsDisposed || doc.Udp is not { } source)
        {
            return;
        }

        long oldBase = source.BaseAddress;
        long oldLength = doc.Document.Length;
        long cursor = doc.Editor.Cursor;
        bool atEnd = cursor >= oldLength;
        bool shifted = source.Publish();
        try
        {
            if (shifted || source.Length < oldLength)
            {
                doc.Document.ReplaceSource(source);
                long moved = cursor - (source.BaseAddress - oldBase);
                doc.Editor.GoTo(Math.Clamp(moved, 0, doc.Document.Length), extendSelection: false);
            }
            else
            {
                doc.Document.SyncAppendedSource();
            }
        }
        catch (Core.Engine.DocumentLockedException)
        {
            // 処理中 (検索など): 次の受信で反映する。
        }

        if (atEnd && App.Settings.GetBool(RemoteSettings.UdpFollowEndKey, true) && doc.Document.Length > oldLength)
        {
            doc.Editor.GoTo(doc.Document.Length, extendSelection: false);
        }

        doc.NotifyStatusChanged();
        if (ReferenceEquals(Vm.Selected, doc))
        {
            FindPanelContent<PacketsPanel>(PacketsPanelId)?.Refresh();
        }

        QueueStatusBarLayout();
    }

    /// <summary>上限に達した (ENG-42 の仕様 4): 記録を止め、「古いデータを捨てて続ける」を示す。</summary>
    private void ShowUdpLimitReached(DocumentViewModel doc)
    {
        if (doc.Udp is not { } source)
        {
            return;
        }

        UpdateUdpDocument(doc);
        // 上限は MiB 単位で決めるので、「64 MiB」のように整数で示す。
        long bytes = source.Capture.Settings.Limit;
        string limit = bytes % (1024 * 1024) == 0 ? $"{(bytes / (1024 * 1024)).ToString("N0", CultureInfo.CurrentCulture)} MiB"
            : Core.View.StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) ?? bytes.ToString("N0", CultureInfo.CurrentCulture);
        LastUdpLimitNotice = Loc.Format("Udp_LimitReached", limit);
        ShowNotice(LastUdpLimitNotice, InfoBarSeverity.Warning, doc, actions:
        [
            new NotificationAction(Loc.Get("Udp_DiscardOld"), () =>
            {
                source.Capture.DiscardOldData();
                UpdateUdpDocument(doc);
            }),
        ]);
    }

    /// <summary>最後の上限の InfoBar の文 (テスト用)。</summary>
    internal string? LastUdpLimitNotice { get; private set; }

    /// <summary>「名前を付けて保存」(ENG-42 の仕様 6): 受信したデータ (保持している分) を 1 つのファイルに書く。ドキュメントは受信を続ける。</summary>
    private async Task<bool> SaveUdpAsAsync(DocumentViewModel doc)
    {
        string suggested = SafeFileName(doc.DisplayName) + ".bin";
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.SaveUdp" };
            picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [".bin"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return false;
        }

        Core.Engine.DocumentSnapshot snapshot = doc.Document.DataSnapshot;
        try
        {
            await Task.Run(() =>
            {
                string temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    for (long pos = 0; pos < snapshot.Length; pos += buffer.Length)
                    {
                        int n = (int)Math.Min(buffer.Length, snapshot.Length - pos);
                        snapshot.Read(pos, buffer.AsSpan(0, n));
                        stream.Write(buffer, 0, n);
                    }
                }

                File.Move(temp, path, overwrite: true);
            });
            ShowStatusMessage(Loc.Format("Udp_Saved", Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Udp_SaveFailed", ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }
    }

    /// <summary>パケットの一覧のエクスポート (結果一覧のエクスポートと同じ CSV / JSON。ENG-42 の仕様 6)。</summary>
    internal async Task SavePacketListAsync(string text, string extension)
    {
        string suggested = "packets" + extension;
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.ExportPackets" };
            picker.FileTypeChoices.Add(extension == ".csv" ? "CSV" : "JSON", [extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
            ShowStatusMessage(Loc.Format("Packets_Exported", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Udp_SaveFailed", ex.Message), InfoBarSeverity.Error);
        }
    }
}
