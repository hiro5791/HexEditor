using System.Globalization;
using HexEditor.Core.Forensics.Reports;

namespace HexEditor.Core.Devices.Info;

/// <summary>
/// レポート (FOR-33) の「デバイス情報」の節: このセッションで取得したデバイス情報・HPA / DCO・SMART (FOR-27 の仕様 6、FOR-28 の仕様 4)。
/// 取得した結果は <see cref="Add"/> で渡す (同じデバイスは新しいもので置き換える)。項目名は <paramref name="localize"/> (UI のリソース) で翻訳し、
/// なければ英語。
/// </summary>
public sealed class DeviceInfoReportContributor(Func<string, string?>? localize = null) : IReportContributor
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Captured"] = "Captured",
        ["Device"] = "Device",
        ["Model"] = "Model",
        ["Serial"] = "Serial number",
        ["Firmware"] = "Firmware",
        ["Capacity"] = "Capacity (bytes)",
        ["SectorSize"] = "Sector size (logical / physical)",
        ["Bus"] = "Bus",
        ["Hpa"] = "HPA",
        ["Dco"] = "DCO",
        ["MaxLba"] = "Max LBA (28-bit / 48-bit / native / DCO)",
        ["NotSupported"] = "Not available for this device",
        ["None"] = "None",
        ["Present"] = "Present ({0} sectors)",
        ["NotApplicable"] = "Not applicable (NVMe)",
        ["SmartId"] = "ID",
        ["SmartName"] = "Name",
        ["SmartCurrent"] = "Current",
        ["SmartWorst"] = "Worst",
        ["SmartThreshold"] = "Threshold",
        ["SmartRaw"] = "Raw",
        ["SmartStatus"] = "Status",
        ["Warning"] = "Warning",
        ["Ok"] = "OK",
    };

    private readonly object _gate = new();
    private readonly List<DeviceInfoReport> _reports = [];

    public string SectionId => ReportSections.Device;

    /// <summary>取得した結果を加える (同じデバイスの前の結果は置き換える)。</summary>
    public void Add(DeviceInfoReport report)
    {
        lock (_gate)
        {
            _reports.RemoveAll(r => string.Equals(r.DevicePath, report.DevicePath, StringComparison.OrdinalIgnoreCase));
            _reports.Add(report);
        }
    }

    public IReadOnlyList<DeviceInfoReport> Reports
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    private string T(string key) => localize?.Invoke(key) ?? English[key];

    public ReportSection? Build(ReportContext context)
    {
        IReadOnlyList<DeviceInfoReport> reports = Reports;
        if (reports.Count == 0)
        {
            return null;
        }

        var blocks = new List<ReportBlock>();
        foreach (DeviceInfoReport r in reports)
        {
            var items = new List<KeyValuePair<string, string>>
            {
                new(T("Device"), r.DevicePath),
                new(T("Captured"), r.CapturedText),
            };
            void Add(string key, object? value)
            {
                if (value is not null)
                {
                    items.Add(new(T(key), Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                }
            }

            DeviceIdentity id = r.Identity;
            Add("Model", id.Model);
            Add("Serial", id.Serial);
            Add("Firmware", id.Firmware);
            Add("Capacity", id.CapacityBytes);
            Add("SectorSize", id.LogicalSectorSize is { } l ? $"{l} / {id.PhysicalSectorSize ?? l}" : null);
            Add("Bus", id.Bus);
            if (r.HpaDco is { } h)
            {
                if (h.IsNvme)
                {
                    Add("Hpa", T("NotApplicable"));
                }
                else if (h.Availability != DeviceInfoAvailability.Available && h.HpaPresent is null)
                {
                    Add("Hpa", T("NotSupported"));
                }
                else
                {
                    Add("MaxLba", $"{h.MaxLba28?.ToString(CultureInfo.InvariantCulture) ?? "-"} / {h.MaxLba48?.ToString(CultureInfo.InvariantCulture) ?? "-"} / "
                        + $"{h.NativeMaxLba?.ToString(CultureInfo.InvariantCulture) ?? "-"} / {h.DcoMaxLba?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                    Add("Hpa", h.HpaPresent is true ? string.Format(CultureInfo.InvariantCulture, T("Present"), h.HpaSectors) : h.HpaPresent is false ? T("None") : T("NotSupported"));
                    Add("Dco", h.DcoPresent is true ? string.Format(CultureInfo.InvariantCulture, T("Present"), h.DcoSectors) : h.DcoPresent is false ? T("None") : T("NotSupported"));
                }
            }

            blocks.Add(new ReportKeyValues(items));
            if (r.Smart?.Ata is { } smart)
            {
                blocks.Add(new ReportTable(
                    [T("SmartId"), T("SmartName"), T("SmartCurrent"), T("SmartWorst"), T("SmartThreshold"), T("SmartRaw"), T("SmartStatus")],
                    smart.Attributes.Select(a => (IReadOnlyList<string>)[a.Id.ToString(CultureInfo.InvariantCulture), AtaStructures.AttributeName(a.Id) ?? "Unknown",
                        a.Current.ToString(CultureInfo.InvariantCulture), a.Worst.ToString(CultureInfo.InvariantCulture), a.Threshold?.ToString(CultureInfo.InvariantCulture) ?? "-",
                        $"0x{a.RawHex} ({a.Raw.ToString(CultureInfo.InvariantCulture)})", a.BelowThreshold ? T("Warning") : T("Ok")]).ToList()));
            }
            else if (r.Smart?.Nvme is { } n)
            {
                blocks.Add(new ReportParagraph(r.ToText().Split("[SMART]")[^1].Trim()));
            }
        }

        return new ReportSection(ReportSections.Device, context.T("Section_device"), blocks);
    }
}
