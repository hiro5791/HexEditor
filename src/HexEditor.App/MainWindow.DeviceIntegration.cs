using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Notifications;
using HexEditor.Core.Sources;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ディスク・プロセスのドキュメントと、ほかの機能のつなぎ: 切断の InfoBar と再接続 (ENG-29 の仕様 11、ENG-28 の仕様 7、ENG-32 の仕様 9)、
/// 変わりうるデータソースの自動更新 (ENG-18 の仕様 2)、タブの右クリックメニュー「即時書き込み」(ENG-34) と「自動更新」。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>自動更新の間隔の選択肢 (ENG-18 の仕様 2: 100 ms〜60 秒、既定 1 秒)。</summary>
    internal static readonly TimeSpan[] AutoRefreshChoices =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)];

    private readonly Dictionary<DocumentViewModel, DispatcherQueueTimer> _autoRefresh = [];

    /// <summary>ウィンドウを作るときに 1 回呼ぶ。</summary>
    private void HookDeviceIntegration()
    {
        Vm.SourceDisconnected += (_, doc) => ShowSourceDisconnected(doc);
        Vm.Documents.CollectionChanged += (_, _) =>
        {
            // 閉じたドキュメントの自動更新を止める。
            foreach (DocumentViewModel gone in _autoRefresh.Keys.Where(d => !Vm.Documents.Contains(d)).ToList())
            {
                SetAutoRefresh(gone, null);
            }
        };
        Closed += (_, _) =>
        {
            foreach (DocumentViewModel doc in _autoRefresh.Keys.ToList())
            {
                SetAutoRefresh(doc, null);
            }
        };
    }

    // ---- 切断と再接続 ----

    /// <summary>
    /// デバイスの取り外し・補助プロセスの終了・プロセスの終了を InfoBar で知らせる。デバイスは「再接続」で同じパスを開き直す
    /// (補助プロセス経由だった場合は UAC の確認が出る。押したときだけ)。未保存の変更は残る。
    /// </summary>
    internal void ShowSourceDisconnected(DocumentViewModel doc)
    {
        LastDisconnectNotice = null;
        switch (doc.Document.Source)
        {
            case DeviceByteSource device:
                string message = device.Route == DeviceRoute.Helper && device.DisconnectError == 0
                    ? Loc.Get("Device_HelperExited")
                    : Loc.Get("Device_Disconnected");
                LastDisconnectNotice = message;
                ShowNotice(message, InfoBarSeverity.Warning, doc,
                    actions: [new NotificationAction(Loc.Get("Device_Reconnect"), () => _ = ReconnectDeviceAsync(doc, device))]);
                break;
            default:
                LastDisconnectNotice = Loc.Get("Process_Exited");
                SetAutoRefresh(doc, null);
                ShowNotice(LastDisconnectNotice, InfoBarSeverity.Warning, doc);
                break;
        }
    }

    /// <summary>最後に出した切断の通知の文 (テスト用)。</summary>
    internal string? LastDisconnectNotice { get; private set; }

    /// <summary>同じパスのデバイスを開き直し、ドキュメントのデータソースのハンドルを付け替える (長さ・セクタサイズが違えば別のデバイス)。</summary>
    internal async Task<bool> ReconnectDeviceAsync(DocumentViewModel doc, DeviceByteSource device)
    {
        try
        {
            OpenRoute route = device.Route switch
            {
                DeviceRoute.Helper => OpenRoute.Helper,
                DeviceRoute.Elevated => OpenRoute.SameProcess,
                _ => OpenRoute.Direct,
            };
            IDeviceAccess access = await DeviceService.DeviceAccessForAsync(route);
            IDeviceHandle handle = access.Open(device.Path, device.Handle.Writable);
            device.ReplaceHandle(handle, access);
        }
        catch (Exception ex) when (ex is DeviceException or IOException or UnauthorizedAccessException or InvalidOperationException
            or OperationCanceledException)
        {
            ShowNotice(Loc.Format("Device_ReconnectFailed", ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }

        if (!doc.Document.IsDisposed)
        {
            // 読めなかった範囲の記録を捨てて読み直す (ENG-18 の仕様 1 と同じ)。
            doc.Document.RefreshFromSource();
        }

        Vm.Notifications.DismissOwnedBy(doc);
        ShowStatusMessage(Loc.Get("Device_Reconnected"));
        UpdateHelperShield();
        return true;
    }

    // ---- セッションのディスクのタブ (UI-31 の仕様 5) ----

    private void MissingOpenDisk_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (TabOf(sender) is { IsMissingDisk: true } doc)
        {
            _ = OpenRestoredDiskAsync(doc, confirm: false);
        }
    }

    /// <summary>
    /// セッションのディスクのタブを開き直す。管理者権限 (補助プロセス) が要り、まだ起動していなければ、<paramref name="confirm"/> なら先に確かめる
    /// (取り消したら「開けません」のタブのまま「開く」ボタンを出す)。開けたら同じ位置のタブにする。
    /// </summary>
    internal async Task OpenRestoredDiskAsync(DocumentViewModel missing, bool confirm)
    {
        if (missing.MissingRecord is not { Path: { } path } record)
        {
            return;
        }

        OpenRoute route = DeviceService.RouteForDisk(isRemovableUsbVolume: false);
        if (route == OpenRoute.GuidanceNeeded)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_Disk"));
            return;
        }

        if (confirm && route == OpenRoute.Helper && !DeviceService.IsHelperRunning
            && !await ConfirmAsync(Loc.Get("Session_DiskHelperTitle"), Loc.Format("Session_DiskHelperBody", record.DisplayName),
                Loc.Get("Session_DiskHelperOpen"), "SessionDiskHelperDialog"))
        {
            return;
        }

        try
        {
            var info = new DeviceOpenInfo { Path = path, DisplayName = record.DisplayName };
            DeviceByteSource source = await DeviceService.OpenDeviceAsync(info, writable: false, route);
            int index = Vm.Documents.IndexOf(missing);
            if (index < 0)
            {
                source.Dispose();
                return;
            }

            Vm.Close(missing);
            DocumentViewModel opened = Vm.OpenDevice(source);
            Vm.MoveDocument(opened, Math.Min(index, Vm.Documents.Count - 1));
            opened.RestorePosition(record.Cursor, record.SelectionStart, record.SelectionLength, record.TopRow);
            Vm.Selected = opened;
            RefreshHelperIndicator();
        }
        catch (HexEditor.Core.Elevation.HelperElevationDeclinedException)
        {
            ShowNotice(Loc.Get("AdminGuide_Declined"), InfoBarSeverity.Informational);
        }
        catch (DeviceException ex)
        {
            ShowNotice(DeviceErrorMessage(ex), InfoBarSeverity.Error);
        }

        UpdateTitle();
    }

    // ---- 自動更新 (ENG-18 の仕様 2) ----

    /// <summary>変わりうるデータソース (ディスク・プロセスメモリ) か。自動更新を選べる。</summary>
    private static bool IsVolatile(DocumentViewModel doc) =>
        !doc.IsPending && !doc.Document.IsDisposed && doc.Document.Source.Capabilities.HasFlag(SourceCapabilities.IsVolatile);

    /// <summary>自動更新を設定する (null はオフ)。表示中の範囲だけが読み直される (キャッシュを捨て、描くときに必要な範囲だけを読む)。</summary>
    internal void SetAutoRefresh(DocumentViewModel doc, TimeSpan? interval)
    {
        if (_autoRefresh.Remove(doc, out DispatcherQueueTimer? old))
        {
            old.Stop();
        }

        doc.AutoRefreshInterval = interval;
        if (interval is not { } every || !IsVolatile(doc))
        {
            doc.AutoRefreshInterval = null;
            return;
        }

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = every;
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (doc.Document.IsDisposed || !Vm.Documents.Contains(doc))
            {
                SetAutoRefresh(doc, null);
                return;
            }

            // 書き込み中・保存中は読み直さない。
            if (!doc.Document.IsEditLocked)
            {
                doc.Document.RefreshFromSource();
            }
        };
        _autoRefresh[doc] = timer;
        timer.Start();
    }

    private static string AutoRefreshText(TimeSpan interval) => interval.TotalSeconds < 1
        ? Loc.Format("Tab_AutoRefreshMilliseconds", interval.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture))
        : Loc.Format("Tab_AutoRefreshSeconds", interval.TotalSeconds.ToString("0", CultureInfo.CurrentCulture));

    /// <summary>タブの右クリックメニューの、ディスク・プロセスの項目 (即時書き込み、自動更新)。</summary>
    private IEnumerable<TabMenuEntry?> DeviceTabMenuEntries(DocumentViewModel doc)
    {
        if (doc.IsProcessMemory)
        {
            yield return null;

            // 「スナップショットを作成」(ANA-09 の「呼び出し」)。
            yield return new TabMenuEntry("TabMenu_CreateSnapshot", Loc.Get("Tab_CreateSnapshot"), !doc.Document.IsDisposed, () =>
            {
                Vm.Selected = doc;
                _ = Commands.ExecuteAsync("compare.createSnapshot");
            });
            yield return new TabMenuEntry("TabMenu_ImmediateWrite", Loc.Get("Tab_ImmediateWrite"), !doc.Document.IsDisposed, () =>
            {
                Vm.Selected = doc;
                ToggleImmediateWrite();
            }, Checked: doc.ImmediateWrite);
        }

        if (IsVolatile(doc))
        {
            if (!doc.IsProcessMemory)
            {
                yield return null;
            }

            yield return new TabMenuEntry("TabMenu_AutoRefresh", Loc.Get("Tab_AutoRefresh"), true, null, Children:
            [
                new TabMenuEntry("TabMenu_AutoRefresh_Off", Loc.Get("Tab_AutoRefreshOff"), true, () => SetAutoRefresh(doc, null),
                    Checked: doc.AutoRefreshInterval is null),
                .. AutoRefreshChoices.Select(c => new TabMenuEntry("TabMenu_AutoRefresh_" + (long)c.TotalMilliseconds, AutoRefreshText(c), true,
                    () => SetAutoRefresh(doc, c), Checked: doc.AutoRefreshInterval == c)),
            ]);
        }
    }
}
