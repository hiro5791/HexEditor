using System.Collections.Specialized;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Reports;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// デバイス情報 (12 の FOR-27 HPA / DCO の検出、FOR-28 SMART 情報)。開いている物理ディスクのドキュメントのハンドルから、読み取り専用のデバイス情報を
/// 取る (補助プロセス経由で開いたディスクは補助プロセスの <c>QueryDeviceInfo</c>、管理者として実行しているときは同じプロセス)。結果はデバイス情報の
/// パネルに出し、監査ログに記録し、レポートの「デバイス情報」の節に入れる。ディスクを開いたときに HPA / DCO を自動で確認する (設定。既定オン)。
/// </summary>
public sealed partial class MainWindow
{
    public const string DeviceInfoPanelId = "deviceInfo";

    /// <summary>設定「ディスクを開いたときに HPA / DCO を確認する」(既定オン)。</summary>
    internal const string AutoHpaCheckKey = "forensics.device.autoHpaCheck";

    /// <summary>レポートの節 (アプリ全体で 1 つ)。</summary>
    internal static DeviceInfoReportContributor DeviceReports { get; } = CreateDeviceReports();

    private DeviceInfoPanel? _deviceInfoPanel;

    /// <summary>このウィンドウで最後に取得したデバイス情報 (パネルに出す)。</summary>
    internal DeviceInfoReport? LastDeviceInfo { get; private set; }

    private static DeviceInfoReportContributor CreateDeviceReports()
    {
        var contributor = new DeviceInfoReportContributor(key => Loc.TryGet("DeviceReport_" + key));
        ReportRegistry.Shared.Register(contributor);
        return contributor;
    }

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterDeviceInfoPanel()
    {
        if (PanelRegistry.Find(DeviceInfoPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(DeviceInfoPanelId, "Panel_DeviceInfo_Title", Core.Panels.PanelDock.Right,
                ctx => ((MainWindow)ctx.Window)._deviceInfoPanel = new DeviceInfoPanel((MainWindow)ctx.Window)) { RequiresDocument = false, CanFloat = false });
        }
    }

    private void InitializeDeviceInfo()
    {
        _ = DeviceReports;
        Commands.Register("forensics.device.hpaDco", () => RunDeviceInfoAsync(hpaDco: true, smart: false, show: true), DeviceInfoState);
        Commands.Register("forensics.device.smart", () => RunDeviceInfoAsync(hpaDco: false, smart: true, show: true), DeviceInfoState);
        Vm.Documents.CollectionChanged += DeviceInfoDocumentsChanged;
        Closed += (_, _) => Vm.Documents.CollectionChanged -= DeviceInfoDocumentsChanged;
    }

    /// <summary>物理ディスクのドキュメントか (HPA / DCO・SMART はディスク全体の情報)。</summary>
    private static bool IsPhysicalDiskDocument(DocumentViewModel d) => d.Device is { } device && DevicePath.DiskNumber(device.Path) is not null;

    private CommandState DeviceInfoState() =>
        NeedsDocument(d => IsPhysicalDiskDocument(d) ? null : Loc.Get("DeviceInfo_DiskOnly"));

    /// <summary>ディスクを開いたら HPA / DCO を自動で確認する (FOR-27 の「呼び出し」。設定で既定オン)。</summary>
    private void DeviceInfoDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!App.Settings.GetBool(AutoHpaCheckKey, true))
        {
            return;
        }

        foreach (DocumentViewModel doc in (e.NewItems ?? Array.Empty<object>()).OfType<DocumentViewModel>().Where(IsPhysicalDiskDocument))
        {
            DocumentViewModel target = doc;
            DispatcherQueue.TryEnqueue(() => _ = RunDeviceInfoAsync(hpaDco: true, smart: false, show: false, target));
        }
    }

    /// <summary>
    /// 取得してパネルに出す。<paramref name="show"/> なら結果のタブでパネルを開く。HPA・DCO があれば InfoBar で知らせる (FOR-27 の仕様 2)。
    /// コマンドが通らない・失敗した場合も例外にせず、パネルに理由を示す (仕様 5、「エラー」)。
    /// </summary>
    internal async Task<DeviceInfoReport?> RunDeviceInfoAsync(bool hpaDco, bool smart, bool show, DocumentViewModel? document = null)
    {
        DocumentViewModel? doc = document ?? Vm.Selected;
        if (doc?.Device is not { } device || !IsPhysicalDiskDocument(doc))
        {
            return null;
        }

        if (device.Handle is not IDeviceInfoSource source)
        {
            ShowNotice(Loc.Get("DeviceInfo_NotSupported"), InfoBarSeverity.Warning, doc);
            return null;
        }

        DiskInfo? disk = device.Info.Disk;
        DeviceGeometry geometry = device.Handle.Geometry;
        DateTime now = ForensicsService.Clock.GetUtcNow().UtcDateTime;
        DeviceInfoReport report;
        try
        {
            report = await Task.Run(() => DeviceInfoCollector.Collect(source, device.Path, disk, geometry, now, hpaDco, smart));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Device information failed: {ex.GetType().Name}");
            ShowNotice(Loc.Format("DeviceInfo_Failed", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }

        // 同じデバイスの前の結果と合わせる (HPA / DCO と SMART を別々に取得しても両方を残す)。
        if (LastDeviceInfo is { } previous && string.Equals(previous.DevicePath, report.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            report = report with { HpaDco = report.HpaDco ?? previous.HpaDco, Smart = report.Smart ?? previous.Smart };
        }

        LastDeviceInfo = report;
        DeviceReports.Add(report);
        RecordDeviceInfo(report, hpaDco, smart);
        if (hpaDco && report.HpaDco is { HasHiddenArea: true } h)
        {
            ShowNotice(Loc.Format("DeviceInfo_HiddenAreaNotice", doc.DisplayName,
                h.HpaPresent == true ? h.HpaSectors.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "0",
                h.DcoPresent == true ? h.DcoSectors.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "0"), InfoBarSeverity.Warning, doc,
                actions: [new Core.Notifications.NotificationAction(Loc.Get("DeviceInfo_ShowPanel"), () => ShowDeviceInfoPanel("hpa"))]);
        }

        if (show)
        {
            ShowDeviceInfoPanel(smart ? "smart" : "hpa");
        }
        else
        {
            _deviceInfoPanel?.Refresh();
        }

        return report;
    }

    private void ShowDeviceInfoPanel(string tab)
    {
        ShowPanel(DeviceInfoPanelId, focus: false);
        _deviceInfoPanel?.Refresh();
        _deviceInfoPanel?.Select(tab);
    }

    /// <summary>結果を監査ログに記録する (FOR-27 の仕様 6、FOR-28 の仕様 4)。</summary>
    private static void RecordDeviceInfo(DeviceInfoReport report, bool hpaDco, bool smart)
    {
        var args = new List<(string, string)> { ("serial", report.Identity.Serial ?? string.Empty), ("model", report.Identity.Model ?? string.Empty) };
        if (hpaDco && report.HpaDco is { } h)
        {
            args.Add(("hpaDco", h.Availability.ToString()));
            args.Add(("hpa", h.HpaPresent is { } hpa ? (hpa ? h.HpaSectors.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none") : "unknown"));
            args.Add(("dco", h.DcoPresent is { } dco ? (dco ? h.DcoSectors.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none") : "unknown"));
        }

        if (smart && report.Smart is { } s)
        {
            args.Add(("smart", s.Availability.ToString()));
            args.Add(("warnings", (s.Ata?.Attributes.Count(a => a.BelowThreshold) ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        ForensicsService.Audit.Record(new AuditRecord(DeviceInfoAuditOperation)
        {
            Path = report.DevicePath,
            Message = hpaDco ? "HPA / DCO were checked." : "SMART information was read.",
            Parameters = AuditRecord.Args([.. args]),
        });
    }

    /// <summary>監査ログの操作名 (デバイス情報の取得)。</summary>
    internal const string DeviceInfoAuditOperation = "device.info";

    /// <summary>テキスト・JSON に書き出す (FOR-28 の仕様 4: 取得日時 (UTC) 付き)。</summary>
    internal async Task ExportDeviceInfoAsync(bool json)
    {
        if (LastDeviceInfo is not { } report)
        {
            ShowNotice(Loc.Get("DeviceInfo_Empty"), InfoBarSeverity.Informational);
            return;
        }

        string? target = await PickForensicsSavePathAsync(json ? "device-info.json" : "device-info.txt", "HexEditor.DeviceInfoExport");
        if (target is null)
        {
            return;
        }

        try
        {
            string content = json ? report.ToJson() : report.ToText();
            await Task.Run(() => Core.Saving.SafeFileWriter.Write(target, stream =>
            {
                byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
            }));
            ForensicsService.Audit.Record(new AuditRecord(AuditOperations.FileExport) { Path = target, Message = "Device information was exported." });
            ShowNotice(Loc.Format("DeviceInfo_Exported", target), InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("DeviceInfo_ExportFailed", ex.Message), InfoBarSeverity.Error);
        }
    }
}
