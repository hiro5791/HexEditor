using System.Globalization;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Forensics.Time;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// デバイス情報のパネル (12 の FOR-27・FOR-28 の「画面」)。タブで「HPA / DCO」「SMART」「デバイス情報」を切り替える。内容はウィンドウが最後に取得した
/// 結果 (<see cref="MainWindow.LastDeviceInfo"/>)。取得日時は UTC (設定によりローカルも) で示し、テキスト・JSON に書き出せる。
/// </summary>
public sealed class DeviceInfoPanel : UserControl, IPanelContent
{
    private readonly MainWindow _window;
    private readonly SelectorBar _tabs = new();
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 6, 12, 0) };
    private readonly StackPanel _hpa = new() { Spacing = 4, Padding = new Thickness(12, 6, 12, 6) };
    private readonly StackPanel _identity = new() { Spacing = 4, Padding = new Thickness(12, 6, 12, 6) };
    private readonly StackPanel _smartText = new() { Spacing = 4, Padding = new Thickness(12, 6, 12, 6) };
    private readonly VirtualTable _smartTable = new("DeviceInfo_SmartList");
    private readonly Grid _smart = new();
    private readonly Grid _content = new();
    private readonly ScrollViewer _hpaView = new();
    private readonly ScrollViewer _identityView = new();
    private IReadOnlyList<IReadOnlyList<string>> _smartRows = [];

    public DeviceInfoPanel(MainWindow window)
    {
        _window = window;
        AutomationProperties.SetAutomationId(_header, "DeviceInfo_Header");
        AutomationProperties.SetAutomationId(_tabs, "DeviceInfo_Tabs");
        AutomationProperties.SetAutomationId(_hpa, "DeviceInfo_HpaDco");
        AutomationProperties.SetAutomationId(_identity, "DeviceInfo_Identity");
        AutomationProperties.SetAutomationId(_smartText, "DeviceInfo_SmartText");
        foreach ((string id, string key) in new[] { ("hpa", "DeviceInfo_Tab_Hpa"), ("smart", "DeviceInfo_Tab_Smart"), ("identity", "DeviceInfo_Tab_Identity") })
        {
            var item = new SelectorBarItem { Text = Loc.Get(key), Tag = id };
            AutomationProperties.SetAutomationId(item, "DeviceInfo_Tab_" + id);
            _tabs.Items.Add(item);
        }

        _tabs.SelectedItem = _tabs.Items[0];
        _tabs.SelectionChanged += (_, _) => ShowTab();

        Button Make(string id, string key, Func<Task> run)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, id);
            b.Click += async (_, _) => await run();
            return b;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 6, 12, 0) };
        bar.Children.Add(Make("DeviceInfo_CheckHpa", "DeviceInfo_CheckHpa", () => window.Commands.ExecuteAsync("forensics.device.hpaDco")));
        bar.Children.Add(Make("DeviceInfo_ReadSmart", "DeviceInfo_ReadSmart", () => window.Commands.ExecuteAsync("forensics.device.smart")));
        bar.Children.Add(Make("DeviceInfo_ExportJson", "DeviceInfo_ExportJson", () => window.ExportDeviceInfoAsync(json: true)));
        bar.Children.Add(Make("DeviceInfo_ExportText", "DeviceInfo_ExportText", () => window.ExportDeviceInfoAsync(json: false)));

        _smart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _smart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_smartTable, 1);
        _smart.Children.Add(_smartText);
        _smart.Children.Add(_smartTable);
        _smartTable.RowProvider = i => i < _smartRows.Count ? _smartRows[(int)i] : [];
        _hpaView.Content = _hpa;
        _identityView.Content = _identity;

        var root = new Grid();
        for (int i = 0; i < 3; i++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_header, 1);
        Grid.SetRow(_tabs, 2);
        Grid.SetRow(_content, 3);
        root.Children.Add(bar);
        root.Children.Add(_header);
        root.Children.Add(_tabs);
        root.Children.Add(_content);
        Content = root;
        Refresh();
    }

    public bool FocusContent() => _tabs.Focus(FocusState.Programmatic);

    /// <summary>タブを選ぶ (<c>hpa</c>、<c>smart</c>、<c>identity</c>)。</summary>
    public void Select(string id)
    {
        _tabs.SelectedItem = _tabs.Items.FirstOrDefault(i => (string)i.Tag == id) ?? _tabs.Items[0];
        ShowTab();
    }

    internal string SelectedTab => (string?)_tabs.SelectedItem?.Tag ?? "hpa";

    /// <summary>テスト用: 各タブの文。</summary>
    internal IReadOnlyList<string> HpaLines => [.. _hpa.Children.OfType<TextBlock>().Select(t => t.Text)];

    internal IReadOnlyList<string> IdentityLines => [.. _identity.Children.OfType<TextBlock>().Select(t => t.Text)];

    internal IReadOnlyList<string> SmartLines => [.. _smartText.Children.OfType<TextBlock>().Select(t => t.Text)];

    internal IReadOnlyList<IReadOnlyList<string>> SmartRows => _smartRows;

    internal IReadOnlyList<string> SmartColumns { get; private set; } = [];

    internal string HeaderText => _header.Text;

    private void ShowTab()
    {
        _content.Children.Clear();
        _content.Children.Add(SelectedTab switch
        {
            "smart" => _smart,
            "identity" => _identityView,
            _ => _hpaView,
        });
    }

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    private static string N(long? value) => value is { } v ? v.ToString("N0", CultureInfo.CurrentCulture) : Loc.Get("Common_Unknown");

    /// <summary>ウィンドウが持つ最後の結果を表示し直す。</summary>
    public void Refresh()
    {
        DeviceInfoReport? report = _window.LastDeviceInfo;
        _hpa.Children.Clear();
        _identity.Children.Clear();
        _smartText.Children.Clear();
        _smartRows = [];
        if (report is null)
        {
            _header.Text = Loc.Get("DeviceInfo_Empty");
            _smartTable.SetColumns([]);
            _smartTable.Refresh(0);
            ShowTab();
            return;
        }

        string time = ForensicsService.TimeContext.Detail(ForensicTimestamp.FromUtc(DateTime.SpecifyKind(report.CapturedUtc, DateTimeKind.Utc), 1), Loc.Get("Time_Invalid"));
        _header.Text = Loc.Format("DeviceInfo_HeaderText", report.DevicePath, time);
        FillHpa(report.HpaDco);
        FillSmart(report.Smart);
        FillIdentity(report.Identity);
        ShowTab();
    }

    private void FillHpa(HpaDcoResult? h)
    {
        if (h is null)
        {
            _hpa.Children.Add(Line(Loc.Get("DeviceInfo_HpaNotChecked")));
            return;
        }

        if (h.IsNvme)
        {
            // FOR-27 の仕様 4: NVMe は HPA / DCO を持たない。
            _hpa.Children.Add(Line(Loc.Get("DeviceInfo_NvmeNoHpa")));
            if (h.NamespaceSize is { } size)
            {
                _hpa.Children.Add(Line(Loc.Format("DeviceInfo_NamespaceSize", N((long)size), N(h.NamespaceBlockSize))));
                _hpa.Children.Add(Line(Loc.Format("DeviceInfo_NamespaceCapacity", N((long?)h.NamespaceCapacity))));
            }

            return;
        }

        switch (h.Availability)
        {
            case DeviceInfoAvailability.NotSupported:
                _hpa.Children.Add(Line(Loc.Get("DeviceInfo_NotSupported")));
                return;
            case DeviceInfoAvailability.AccessDenied:
                _hpa.Children.Add(Line(Loc.Get("DeviceInfo_NeedsAdmin")));
                return;
            case DeviceInfoAvailability.CommandFailed when h.MaxLba28 is null && h.MaxLba48 is null:
                _hpa.Children.Add(Line(Loc.Format("DeviceInfo_CommandFailed", h.FailedCommand ?? string.Empty, $"0x{h.AtaStatus ?? 0:X2}", $"0x{h.AtaError ?? 0:X2}")));
                return;
        }

        _hpa.Children.Add(Line(Loc.Format("DeviceInfo_MaxLba28", N(h.MaxLba28))));
        _hpa.Children.Add(Line(Loc.Format("DeviceInfo_MaxLba48", N(h.MaxLba48))));
        _hpa.Children.Add(Line(Loc.Format("DeviceInfo_NativeMax", h.HpaSupported ? N(h.NativeMaxLba) : Loc.Get("DeviceInfo_HpaUnsupported"))));
        _hpa.Children.Add(Line(Loc.Format("DeviceInfo_DcoMax", h.DcoSupported ? N(h.DcoMaxLba) : Loc.Get("DeviceInfo_DcoUnsupported"))));
        _hpa.Children.Add(Line(h.HpaPresent switch
        {
            true => Loc.Format("DeviceInfo_HpaPresent", N(h.HpaSectors), StatusSize(h.HpaSectors * h.SectorSize)),
            false => Loc.Get("DeviceInfo_HpaAbsent"),
            null => Loc.Get("DeviceInfo_HpaUnknown"),
        }));
        _hpa.Children.Add(Line(h.DcoPresent switch
        {
            true => Loc.Format("DeviceInfo_DcoPresent", N(h.DcoSectors), StatusSize(h.DcoSectors * h.SectorSize)),
            false => Loc.Get("DeviceInfo_DcoAbsent"),
            null => Loc.Get("DeviceInfo_DcoUnknown"),
        }));
        if (h.FailedCommand is not null)
        {
            _hpa.Children.Add(Line(Loc.Format("DeviceInfo_CommandFailed", h.FailedCommand, $"0x{h.AtaStatus ?? 0:X2}", $"0x{h.AtaError ?? 0:X2}")));
        }
    }

    private static string StatusSize(long bytes) =>
        Core.View.StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) ?? bytes.ToString("N0", CultureInfo.CurrentCulture);

    private void FillSmart(SmartResult? s)
    {
        string[] columns = [Loc.Get("Smart_Col_Id"), Loc.Get("Smart_Col_Name"), Loc.Get("Smart_Col_Current"), Loc.Get("Smart_Col_Worst"), Loc.Get("Smart_Col_Threshold"),
            Loc.Get("Smart_Col_RawHex"), Loc.Get("Smart_Col_RawDec"), Loc.Get("Smart_Col_Status")];
        SmartColumns = columns;
        if (s is null)
        {
            _smartText.Children.Add(Line(Loc.Get("DeviceInfo_SmartNotRead")));
            _smartTable.SetColumns([]);
            _smartTable.Refresh(0);
            return;
        }

        switch (s.Availability)
        {
            case DeviceInfoAvailability.NotSupported:
                _smartText.Children.Add(Line(Loc.Get("DeviceInfo_SmartNotSupported")));
                break;
            case DeviceInfoAvailability.AccessDenied:
                _smartText.Children.Add(Line(Loc.Get("DeviceInfo_NeedsAdmin")));
                break;
            case DeviceInfoAvailability.Disabled:
                _smartText.Children.Add(Line(Loc.Get("DeviceInfo_SmartDisabled")));
                break;
            case DeviceInfoAvailability.CommandFailed:
                _smartText.Children.Add(Line(Loc.Format("DeviceInfo_CommandFailed", "SMART READ DATA", $"0x{s.AtaStatus ?? 0:X2}", $"0x{s.AtaError ?? 0:X2}")));
                break;
        }

        if (s.Ata is { } ata)
        {
            if (ata.ChecksumMismatch)
            {
                _smartText.Children.Add(Line(Loc.Get("DeviceInfo_SmartChecksum")));
            }

            _smartRows = [.. ata.Attributes.Select(a => (IReadOnlyList<string>)[
                a.Id.ToString(CultureInfo.InvariantCulture),
                Loc.TryGet("Smart_Attr_" + a.Id.ToString(CultureInfo.InvariantCulture)) ?? Loc.Get("Smart_Attr_Unknown"),
                a.Current.ToString(CultureInfo.InvariantCulture),
                a.Worst.ToString(CultureInfo.InvariantCulture),
                a.Threshold?.ToString(CultureInfo.InvariantCulture) ?? "-",
                a.RawHex,
                a.Raw.ToString(CultureInfo.InvariantCulture),
                a.BelowThreshold ? Loc.Get("Smart_Status_Warning") : Loc.Get("Smart_Status_Ok")])];
            _smartTable.SetColumns([new(columns[0], 50, true), new(columns[1], 240), new(columns[2], 70, true), new(columns[3], 70, true), new(columns[4], 70, true),
                new(columns[5], 130, true), new(columns[6], 150, true), new(columns[7], 80)]);
            _smartTable.Refresh(_smartRows.Count);
            return;
        }

        _smartTable.SetColumns([]);
        _smartTable.Refresh(0);
        if (s.Nvme is { } n)
        {
            // FOR-28 の仕様 2: NVMe の SMART / Health Information。
            _smartText.Children.Add(Line(Loc.Format("Nvme_CriticalWarning", $"0x{n.CriticalWarning:X2}")));
            _smartText.Children.Add(Line(Loc.Format("Nvme_Temperature", n.TemperatureCelsius)));
            _smartText.Children.Add(Line(Loc.Format("Nvme_AvailableSpare", n.AvailableSparePercent, n.AvailableSpareThresholdPercent)));
            _smartText.Children.Add(Line(Loc.Format("Nvme_PercentageUsed", n.PercentageUsed)));
            _smartText.Children.Add(Line(Loc.Format("Nvme_DataRead", n.BytesRead.ToString("N0", CultureInfo.CurrentCulture))));
            _smartText.Children.Add(Line(Loc.Format("Nvme_DataWritten", n.BytesWritten.ToString("N0", CultureInfo.CurrentCulture))));
            _smartText.Children.Add(Line(Loc.Format("Nvme_PowerCycles", n.PowerCycles.ToString("N0", CultureInfo.CurrentCulture))));
            _smartText.Children.Add(Line(Loc.Format("Nvme_PowerOnHours", n.PowerOnHours.ToString("N0", CultureInfo.CurrentCulture))));
            _smartText.Children.Add(Line(Loc.Format("Nvme_UnsafeShutdowns", n.UnsafeShutdowns.ToString("N0", CultureInfo.CurrentCulture))));
            _smartText.Children.Add(Line(Loc.Format("Nvme_MediaErrors", n.MediaErrors.ToString("N0", CultureInfo.CurrentCulture))));
        }
    }

    private void FillIdentity(DeviceIdentity id)
    {
        string unknown = Loc.Get("Common_Unknown");
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Model", id.Model ?? unknown)));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Serial", id.Serial ?? unknown)));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Firmware", id.Firmware ?? unknown)));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Capacity", id.CapacityBytes is { } c ? $"{StatusSize(c)} ({c.ToString("N0", CultureInfo.CurrentCulture)})" : unknown)));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_SectorSize", N(id.LogicalSectorSize), N(id.PhysicalSectorSize))));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Bus", id.Bus.ToString())));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Rotation", id.RotationRate switch
        {
            1 => Loc.Get("DeviceInfo_Rotation_Ssd"),
            { } rpm => rpm.ToString("N0", CultureInfo.CurrentCulture) + " rpm",
            null => unknown,
        })));
        _identity.Children.Add(Line(Loc.Format("DeviceInfo_Trim", id.Trim switch
        {
            true => Loc.Get("DeviceInfo_Trim_Yes"),
            false => Loc.Get("DeviceInfo_Trim_No"),
            null => unknown,
        })));
    }
}
