using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Elevation;

namespace HexEditor.Core.Devices.Info;

/// <summary>
/// 読み取り専用のデバイス情報を取れるハンドル (12 の FOR-27、FOR-28)。実装は 3 つ: Win32 (ATA パススルー・<c>IOCTL_STORAGE_QUERY_PROPERTY</c>)、
/// 補助プロセス経由 (<c>QueryDeviceInfo</c>)、偽のデバイス。戻り値は Win32 のエラーコード (0 = 成功)。応答の形は <see cref="DeviceInfoPayloads"/>。
/// </summary>
public interface IDeviceInfoSource
{
    int QueryDeviceInfo(DeviceInfoKind kind, out byte[] data);
}

/// <summary>
/// <see cref="IDeviceInfoSource"/> の応答の形:
/// <list type="bullet">
/// <item>ATA の種類 (1〜5): [ステータス u8][エラー u8][予約 6][データ 512 バイト]。最大 LBA (2) のデータは [LBA i64]。(<see cref="AtaCommands.EncodeResult"/>)</item>
/// <item>NVMe (6〜8): ログページ・Identify のデータそのもの。</item>
/// <item>ストレージの記述子 (9〜11): <c>STORAGE_DEVICE_DESCRIPTOR</c> などの構造体そのもの。</item>
/// </list>
/// </summary>
public static class DeviceInfoPayloads
{
    public static bool IsAta(DeviceInfoKind kind) => kind is >= DeviceInfoKind.IdentifyDevice and <= DeviceInfoKind.SmartReadThresholds;
}

/// <summary>取得できたか。</summary>
public enum DeviceInfoAvailability
{
    Available,

    /// <summary>このデバイスでは確認できない (USB 変換・仮想ディスクなどでコマンドが通らない。FOR-27 の仕様 5)。</summary>
    NotSupported,

    /// <summary>管理者権限が必要。</summary>
    AccessDenied,

    /// <summary>コマンドが失敗した (ATA のステータスとエラーレジスタを示す)。</summary>
    CommandFailed,

    /// <summary>SMART が無効にされている。</summary>
    Disabled,
}

/// <summary>HPA / DCO の確認の結果 (FOR-27 の仕様 2・4)。LBA は最大の番号 (含む)。</summary>
public sealed record HpaDcoResult
{
    public DeviceInfoAvailability Availability { get; init; }

    public bool IsNvme { get; init; }

    public long? MaxLba28 { get; init; }

    public long? MaxLba48 { get; init; }

    public long? NativeMaxLba { get; init; }

    public long? DcoMaxLba { get; init; }

    public bool HpaSupported { get; init; }

    public bool DcoSupported { get; init; }

    /// <summary>HPA の有無 (分からなければ null)。</summary>
    public bool? HpaPresent { get; init; }

    public long HpaSectors { get; init; }

    public bool? DcoPresent { get; init; }

    public long DcoSectors { get; init; }

    public int SectorSize { get; init; } = 512;

    // NVMe (仕様 4)。
    public ulong? NamespaceSize { get; init; }

    public ulong? NamespaceCapacity { get; init; }

    public int? NamespaceBlockSize { get; init; }

    /// <summary>失敗したコマンドの名前と、ATA のステータス・エラーレジスタ (エラーの表示)。</summary>
    public string? FailedCommand { get; init; }

    public byte? AtaStatus { get; init; }

    public byte? AtaError { get; init; }

    public int ErrorCode { get; init; }

    /// <summary>InfoBar で知らせる (HPA か DCO がある)。</summary>
    public bool HasHiddenArea => HpaPresent == true || DcoPresent == true;
}

/// <summary>SMART の取得の結果 (FOR-28 の仕様 1・2)。</summary>
public sealed record SmartResult
{
    public DeviceInfoAvailability Availability { get; init; }

    public SmartData? Ata { get; init; }

    public NvmeHealthData? Nvme { get; init; }

    public byte? AtaStatus { get; init; }

    public byte? AtaError { get; init; }

    public int ErrorCode { get; init; }
}

/// <summary>デバイス情報 (FOR-28 の仕様 3)。分からない項目は null。</summary>
public sealed record DeviceIdentity
{
    public string? Model { get; init; }

    public string? Serial { get; init; }

    public string? Firmware { get; init; }

    public long? CapacityBytes { get; init; }

    public int? LogicalSectorSize { get; init; }

    public int? PhysicalSectorSize { get; init; }

    public DiskBusType Bus { get; init; }

    /// <summary>回転数 (rpm)。SSD は 1 (= 回転しない)。</summary>
    public int? RotationRate { get; init; }

    public bool? Trim { get; init; }
}

/// <summary>デバイス情報パネルの内容と書き出し (FOR-28 の仕様 4: 取得日時 (UTC) と一緒に、テキスト・JSON に書き出せる)。</summary>
public sealed record DeviceInfoReport
{
    public required DateTime CapturedUtc { get; init; }

    public required string DevicePath { get; init; }

    public required DeviceIdentity Identity { get; init; }

    public HpaDcoResult? HpaDco { get; init; }

    public SmartResult? Smart { get; init; }

    /// <summary>取得日時の形式 (FOR-05: <c>2026-10-07T03:15:42.1234567Z</c>)。</summary>
    public string CapturedText => DateTime.SpecifyKind(CapturedUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("capturedUtc", CapturedText);
            w.WriteString("device", DevicePath);
            w.WriteStartObject("identity");
            WriteOpt(w, "model", Identity.Model);
            WriteOpt(w, "serialNumber", Identity.Serial);
            WriteOpt(w, "firmware", Identity.Firmware);
            WriteOpt(w, "capacityBytes", Identity.CapacityBytes);
            WriteOpt(w, "logicalSectorSize", Identity.LogicalSectorSize);
            WriteOpt(w, "physicalSectorSize", Identity.PhysicalSectorSize);
            w.WriteString("bus", Identity.Bus.ToString());
            WriteOpt(w, "rotationRate", Identity.RotationRate);
            if (Identity.Trim is { } trim)
            {
                w.WriteBoolean("trim", trim);
            }

            w.WriteEndObject();
            if (HpaDco is { } h)
            {
                w.WriteStartObject("hpaDco");
                w.WriteString("availability", h.Availability.ToString());
                w.WriteBoolean("nvme", h.IsNvme);
                WriteOpt(w, "maxLba28", h.MaxLba28);
                WriteOpt(w, "maxLba48", h.MaxLba48);
                WriteOpt(w, "nativeMaxLba", h.NativeMaxLba);
                WriteOpt(w, "dcoMaxLba", h.DcoMaxLba);
                if (h.HpaPresent is { } hpa)
                {
                    w.WriteBoolean("hpa", hpa);
                    w.WriteNumber("hpaSectors", h.HpaSectors);
                }

                if (h.DcoPresent is { } dco)
                {
                    w.WriteBoolean("dco", dco);
                    w.WriteNumber("dcoSectors", h.DcoSectors);
                }

                WriteOpt(w, "namespaceSize", h.NamespaceSize);
                WriteOpt(w, "namespaceCapacity", h.NamespaceCapacity);
                WriteOpt(w, "namespaceBlockSize", h.NamespaceBlockSize);
                WriteAtaError(w, h.FailedCommand, h.AtaStatus, h.AtaError, h.ErrorCode);
                w.WriteEndObject();
            }

            if (Smart is { } s)
            {
                w.WriteStartObject("smart");
                w.WriteString("availability", s.Availability.ToString());
                if (s.Ata is { } ata)
                {
                    w.WriteBoolean("checksumMismatch", ata.ChecksumMismatch);
                    w.WriteStartArray("attributes");
                    foreach (SmartAttribute a in ata.Attributes)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("id", a.Id);
                        w.WriteString("name", AtaStructures.AttributeName(a.Id) ?? "Unknown");
                        w.WriteNumber("current", a.Current);
                        w.WriteNumber("worst", a.Worst);
                        WriteOpt(w, "threshold", a.Threshold is { } t ? t : null);
                        w.WriteString("rawHex", a.RawHex);
                        w.WriteNumber("raw", a.Raw);
                        w.WriteString("status", a.BelowThreshold ? "warning" : "ok");
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                if (s.Nvme is { } n)
                {
                    w.WriteStartObject("nvme");
                    w.WriteNumber("criticalWarning", n.CriticalWarning);
                    w.WriteNumber("temperatureCelsius", n.TemperatureCelsius);
                    w.WriteNumber("availableSparePercent", n.AvailableSparePercent);
                    w.WriteNumber("availableSpareThresholdPercent", n.AvailableSpareThresholdPercent);
                    w.WriteNumber("percentageUsed", n.PercentageUsed);
                    w.WriteString("dataUnitsRead", n.DataUnitsRead.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("dataUnitsWritten", n.DataUnitsWritten.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("bytesRead", n.BytesRead.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("bytesWritten", n.BytesWritten.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("powerCycles", n.PowerCycles.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("powerOnHours", n.PowerOnHours.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("unsafeShutdowns", n.UnsafeShutdowns.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("mediaErrors", n.MediaErrors.ToString(CultureInfo.InvariantCulture));
                    w.WriteString("errorLogEntries", n.ErrorLogEntries.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }

                WriteAtaError(w, null, s.AtaStatus, s.AtaError, s.ErrorCode);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAtaError(Utf8JsonWriter w, string? command, byte? status, byte? error, int code)
    {
        if (command is not null)
        {
            w.WriteString("failedCommand", command);
        }

        if (status is { } st)
        {
            w.WriteString("ataStatus", "0x" + st.ToString("X2", CultureInfo.InvariantCulture));
        }

        if (error is { } er)
        {
            w.WriteString("ataError", "0x" + er.ToString("X2", CultureInfo.InvariantCulture));
        }

        if (code != 0)
        {
            w.WriteNumber("errorCode", code);
        }
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, ulong? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
    }

    /// <summary>テキストの書き出し (英語の固定の項目名。報告書に貼れる形)。</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        void Line(string name, object? value)
        {
            if (value is not null)
            {
                sb.Append(name.PadRight(28)).Append(": ").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");
            }
        }

        Line("Captured (UTC)", CapturedText);
        Line("Device", DevicePath);
        Line("Model", Identity.Model);
        Line("Serial number", Identity.Serial);
        Line("Firmware", Identity.Firmware);
        Line("Capacity (bytes)", Identity.CapacityBytes);
        Line("Logical sector size", Identity.LogicalSectorSize);
        Line("Physical sector size", Identity.PhysicalSectorSize);
        Line("Bus", Identity.Bus);
        Line("Rotation rate", Identity.RotationRate is 1 ? "Non-rotating (SSD)" : Identity.RotationRate);
        Line("TRIM", Identity.Trim is { } t ? (t ? "Supported" : "Not supported") : null);
        if (HpaDco is { } h)
        {
            sb.Append("\r\n[HPA / DCO]\r\n");
            Line("Availability", h.Availability);
            Line("Max LBA (28-bit)", h.MaxLba28);
            Line("Max LBA (48-bit)", h.MaxLba48);
            Line("Native max LBA", h.NativeMaxLba);
            Line("DCO max LBA", h.DcoMaxLba);
            Line("HPA", h.HpaPresent is { } hpa ? (hpa ? $"Present ({h.HpaSectors} sectors)" : "None") : null);
            Line("DCO", h.DcoPresent is { } dco ? (dco ? $"Present ({h.DcoSectors} sectors)" : "None") : null);
            Line("Namespace size (blocks)", h.NamespaceSize);
            Line("Namespace capacity (blocks)", h.NamespaceCapacity);
            Line("Failed command", h.FailedCommand);
            Line("ATA status", h.AtaStatus is { } st ? $"0x{st:X2}" : null);
            Line("ATA error", h.AtaError is { } er ? $"0x{er:X2}" : null);
        }

        if (Smart is { } s)
        {
            sb.Append("\r\n[SMART]\r\n");
            Line("Availability", s.Availability);
            if (s.Ata is { } ata)
            {
                sb.Append("ID  Name                               Cur Wst Thr Raw (hex)    Raw            Status\r\n");
                foreach (SmartAttribute a in ata.Attributes)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{a.Id,3} {(AtaStructures.AttributeName(a.Id) ?? "Unknown"),-34} {a.Current,3} {a.Worst,3} {(a.Threshold?.ToString(CultureInfo.InvariantCulture) ?? "-"),3} {a.RawHex} {a.Raw,-14} {(a.BelowThreshold ? "Warning" : "OK")}\r\n");
                }
            }

            if (s.Nvme is { } n)
            {
                Line("Critical warning", $"0x{n.CriticalWarning:X2}");
                Line("Temperature (C)", n.TemperatureCelsius);
                Line("Available spare (%)", n.AvailableSparePercent);
                Line("Spare threshold (%)", n.AvailableSpareThresholdPercent);
                Line("Percentage used (%)", n.PercentageUsed);
                Line("Data read (bytes)", n.BytesRead);
                Line("Data written (bytes)", n.BytesWritten);
                Line("Power cycles", n.PowerCycles);
                Line("Power-on hours", n.PowerOnHours);
                Line("Unsafe shutdowns", n.UnsafeShutdowns);
                Line("Media errors", n.MediaErrors);
                Line("Error log entries", n.ErrorLogEntries);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// デバイス情報を集める (FOR-27、FOR-28)。送るのは読み取りだけのコマンド (<see cref="AtaCommands"/> の許可リスト) と記述子の取得だけ。
/// コマンドが通らない (USB 変換・仮想ディスク) ときも例外にせず、結果の <see cref="DeviceInfoAvailability"/> で返す。
/// </summary>
public static class DeviceInfoCollector
{
    private static DeviceInfoAvailability FromError(int error) => error == Win32Errors.AccessDenied
        ? DeviceInfoAvailability.AccessDenied
        : DeviceInfoAvailability.NotSupported;

    private static bool IsNvme(DiskInfo? disk) => disk?.Bus == DiskBusType.Nvme;

    /// <summary>ATA のコマンドを送って結果を読む。通らなければ (Win32 のエラー, null)。</summary>
    private static (int Error, byte Status, byte AtaError, byte[]? Payload) Ata(IDeviceInfoSource source, DeviceInfoKind kind)
    {
        int error;
        byte[] data;
        try
        {
            error = source.QueryDeviceInfo(kind, out data);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            return (ex is UnauthorizedAccessException ? Win32Errors.AccessDenied : Win32Errors.NotSupported, 0, 0, null);
        }

        if (error != 0)
        {
            return (error, 0, 0, null);
        }

        if (AtaCommands.DecodeResult(data) is not { } decoded)
        {
            return (Win32Errors.NotSupported, 0, 0, null);
        }

        return (0, decoded.Status, decoded.Error, (decoded.Status & 0x01) != 0 ? null : decoded.Payload);
    }

    private static int Query(IDeviceInfoSource source, DeviceInfoKind kind, out byte[] data)
    {
        try
        {
            return source.QueryDeviceInfo(kind, out data);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            data = [];
            return ex is UnauthorizedAccessException ? Win32Errors.AccessDenied : Win32Errors.NotSupported;
        }
    }

    /// <summary>HPA / DCO を確かめる (FOR-27)。</summary>
    public static HpaDcoResult CheckHpaDco(IDeviceInfoSource source, DiskInfo? disk)
    {
        if (IsNvme(disk))
        {
            // NVMe は HPA / DCO を持たない (仕様 4)。名前空間のサイズと容量を示す。
            int error = Query(source, DeviceInfoKind.NvmeIdentifyNamespace, out byte[] ns);
            NvmeNamespaceData? parsed = error == 0 ? NvmeStructures.ParseNamespace(ns) : null;
            return new HpaDcoResult
            {
                IsNvme = true,
                Availability = parsed is null ? FromError(error == 0 ? Win32Errors.NotSupported : error) : DeviceInfoAvailability.Available,
                NamespaceSize = parsed?.Size,
                NamespaceCapacity = parsed?.Capacity,
                NamespaceBlockSize = parsed?.BlockSize,
                ErrorCode = error,
            };
        }

        (int idError, byte idStatus, byte idAtaError, byte[]? idData) = Ata(source, DeviceInfoKind.IdentifyDevice);
        if (idError != 0)
        {
            return new HpaDcoResult { Availability = FromError(idError), ErrorCode = idError };
        }

        AtaIdentifyData? identify = idData is null ? null : AtaStructures.ParseIdentify(idData);
        if (identify is null)
        {
            return new HpaDcoResult
            {
                Availability = DeviceInfoAvailability.CommandFailed,
                FailedCommand = "IDENTIFY DEVICE",
                AtaStatus = idStatus,
                AtaError = idAtaError,
            };
        }

        long? user = identify.UserMaxLba;
        var result = new HpaDcoResult
        {
            Availability = DeviceInfoAvailability.Available,
            MaxLba28 = identify.MaxLba28,
            MaxLba48 = identify.MaxLba48,
            HpaSupported = identify.HpaSupported,
            DcoSupported = identify.DcoSupported,
            SectorSize = identify.LogicalSectorSize,
        };

        long? native = null;
        if (identify.HpaSupported)
        {
            (int nError, byte nStatus, byte nAtaError, byte[]? nData) = Ata(source, DeviceInfoKind.ReadNativeMaxAddress);
            if (nError == 0 && nData is { Length: >= 8 } && BinaryPrimitives.ReadInt64LittleEndian(nData) is var lba and >= 0)
            {
                native = lba;
            }
            else if (nError == 0)
            {
                result = result with { Availability = DeviceInfoAvailability.CommandFailed, FailedCommand = "READ NATIVE MAX ADDRESS", AtaStatus = nStatus, AtaError = nAtaError };
            }
            else
            {
                result = result with { ErrorCode = nError };
            }
        }
        else
        {
            // HPA の機能がなければ、本来の最大 LBA は利用者がアクセスできる最大 LBA と同じ。
            native = user;
        }

        long? dco = null;
        if (identify.DcoSupported)
        {
            (int dError, byte dStatus, byte dAtaError, byte[]? dData) = Ata(source, DeviceInfoKind.DeviceConfigurationIdentify);
            if (dError == 0 && dData is not null && AtaStructures.ParseDco(dData) is { } parsedDco)
            {
                dco = parsedDco.MaxLba;
            }
            else if (dError == 0 && result.FailedCommand is null)
            {
                result = result with { Availability = DeviceInfoAvailability.CommandFailed, FailedCommand = "DEVICE CONFIGURATION IDENTIFY", AtaStatus = dStatus, AtaError = dAtaError };
            }
        }

        bool? hpaPresent = native is { } n && user is { } u ? n > u : null;
        long? dcoBase = native ?? user;
        bool? dcoPresent = dco is { } d && dcoBase is { } b ? d > b : identify.DcoSupported ? null : false;
        return result with
        {
            NativeMaxLba = native,
            DcoMaxLba = dco,
            HpaPresent = hpaPresent,
            HpaSectors = hpaPresent == true ? native!.Value - user!.Value : 0,
            DcoPresent = dcoPresent,
            DcoSectors = dcoPresent == true ? dco!.Value - dcoBase!.Value : 0,
        };
    }

    /// <summary>SMART を読む (FOR-28 の仕様 1・2)。</summary>
    public static SmartResult ReadSmart(IDeviceInfoSource source, DiskInfo? disk)
    {
        if (IsNvme(disk))
        {
            int error = Query(source, DeviceInfoKind.NvmeSmartHealth, out byte[] log);
            NvmeHealthData? health = error == 0 ? NvmeStructures.ParseHealth(log) : null;
            return new SmartResult
            {
                Availability = health is null ? FromError(error == 0 ? Win32Errors.NotSupported : error) : DeviceInfoAvailability.Available,
                Nvme = health,
                ErrorCode = error,
            };
        }

        (int idError, _, _, byte[]? idData) = Ata(source, DeviceInfoKind.IdentifyDevice);
        if (idError != 0)
        {
            return new SmartResult { Availability = FromError(idError), ErrorCode = idError };
        }

        if (idData is not null && AtaStructures.ParseIdentify(idData) is { } identify && identify.SmartSupported && !identify.SmartEnabled)
        {
            return new SmartResult { Availability = DeviceInfoAvailability.Disabled };
        }

        (int error2, byte status, byte ataError, byte[]? data) = Ata(source, DeviceInfoKind.SmartReadData);
        if (error2 != 0)
        {
            return new SmartResult { Availability = FromError(error2), ErrorCode = error2 };
        }

        if (data is null)
        {
            return new SmartResult { Availability = DeviceInfoAvailability.CommandFailed, AtaStatus = status, AtaError = ataError };
        }

        (int error3, _, _, byte[]? thresholds) = Ata(source, DeviceInfoKind.SmartReadThresholds);
        SmartData? smart = AtaStructures.ParseSmart(data, error3 == 0 && thresholds is not null ? thresholds : []);
        return new SmartResult
        {
            Availability = smart is null ? DeviceInfoAvailability.CommandFailed : DeviceInfoAvailability.Available,
            Ata = smart,
        };
    }

    /// <summary>デバイス情報 (FOR-28 の仕様 3)。カタログの情報を基に、デバイスから読めた値で補う。</summary>
    public static DeviceIdentity ReadIdentity(IDeviceInfoSource source, DiskInfo? disk, DeviceGeometry? geometry)
    {
        var identity = new DeviceIdentity
        {
            Model = disk?.Model,
            Serial = disk?.SerialNumber,
            CapacityBytes = disk?.Size ?? geometry?.Length,
            LogicalSectorSize = disk?.LogicalSectorSize ?? geometry?.LogicalSectorSize,
            PhysicalSectorSize = disk?.PhysicalSectorSize ?? geometry?.PhysicalSectorSize,
            Bus = disk?.Bus ?? DiskBusType.Unknown,
        };

        if (Query(source, DeviceInfoKind.StorageDescriptor, out byte[] descriptor) == 0 && NvmeStructures.ParseStorageDescriptor(descriptor) is { } d)
        {
            identity = identity with
            {
                Model = identity.Model ?? NonEmpty($"{d.Vendor} {d.Product}".Trim()),
                Serial = identity.Serial ?? NonEmpty(d.Serial),
                Firmware = NonEmpty(d.Revision),
            };
        }

        if (Query(source, DeviceInfoKind.TrimSupport, out byte[] trim) == 0 && NvmeStructures.ParseFlagDescriptor(trim) is { } trimEnabled)
        {
            identity = identity with { Trim = trimEnabled };
        }

        if (Query(source, DeviceInfoKind.SeekPenalty, out byte[] seek) == 0 && NvmeStructures.ParseFlagDescriptor(seek) is false)
        {
            identity = identity with { RotationRate = 1 };
        }

        if (IsNvme(disk))
        {
            if (Query(source, DeviceInfoKind.NvmeIdentifyController, out byte[] ctrl) == 0 && NvmeStructures.ParseController(ctrl) is { } c)
            {
                identity = identity with
                {
                    Model = NonEmpty(c.Model) ?? identity.Model,
                    Serial = NonEmpty(c.Serial) ?? identity.Serial,
                    Firmware = NonEmpty(c.Firmware) ?? identity.Firmware,
                    Trim = identity.Trim ?? c.DatasetManagement,
                    RotationRate = 1,
                };
            }

            return identity;
        }

        (int error, _, _, byte[]? data) = Ata(source, DeviceInfoKind.IdentifyDevice);
        if (error == 0 && data is not null && AtaStructures.ParseIdentify(data) is { } a)
        {
            long? sectors = a.UserMaxLba is { } max ? max + 1 : null;
            identity = identity with
            {
                Model = NonEmpty(a.Model) ?? identity.Model,
                Serial = NonEmpty(a.Serial) ?? identity.Serial,
                Firmware = NonEmpty(a.Firmware) ?? identity.Firmware,
                CapacityBytes = identity.CapacityBytes ?? (sectors * a.LogicalSectorSize),
                LogicalSectorSize = identity.LogicalSectorSize ?? a.LogicalSectorSize,
                PhysicalSectorSize = a.PhysicalSectorSize,
                RotationRate = a.RotationRate ?? identity.RotationRate,
                Trim = a.TrimSupported || identity.Trim == true,
            };
        }

        return identity;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>まとめて取得する。</summary>
    public static DeviceInfoReport Collect(IDeviceInfoSource source, string devicePath, DiskInfo? disk, DeviceGeometry? geometry, DateTime capturedUtc,
        bool hpaDco = true, bool smart = true) => new()
        {
            CapturedUtc = capturedUtc,
            DevicePath = devicePath,
            Identity = ReadIdentity(source, disk, geometry),
            HpaDco = hpaDco ? CheckHpaDco(source, disk) : null,
            Smart = smart ? ReadSmart(source, disk) : null,
        };
}
