using HexEditor.Core.Elevation;

namespace HexEditor.Core.Devices.Info;

/// <summary>偽のディスクの SMART の属性 (テスト用)。</summary>
public sealed record FakeSmartAttributeSpec(byte Id, byte Current, byte Worst, byte Threshold, long Raw, ushort Flags = 0x0033);

/// <summary>
/// 偽のディスクの ATA の応答 (FOR-27・FOR-28 のテスト)。<see cref="HpaSectors"/> は HPA で隠したセクタ数 (READ NATIVE MAX ADDRESS が
/// 利用者の最大 LBA + この数を返す)、<see cref="DcoSectors"/> は DCO で隠したセクタ数。
/// </summary>
public sealed record FakeAtaSpec
{
    public long HpaSectors { get; init; }

    public long DcoSectors { get; init; }

    public bool HpaSupported { get; init; } = true;

    public bool DcoSupported { get; init; } = true;

    public bool SmartEnabled { get; init; } = true;

    public string Firmware { get; init; } = "FW1.0";

    /// <summary>回転数 (1 = SSD)。</summary>
    public int RotationRate { get; init; } = 1;

    public bool Trim { get; init; } = true;

    /// <summary>ATA のコマンドがエラーになる (ステータスの ERR とエラーレジスタの ABRT)。</summary>
    public bool Abort { get; init; }

    public List<FakeSmartAttributeSpec> Smart { get; init; } =
    [
        new(5, 100, 100, 10, 0),
        new(9, 99, 99, 0, 1234),
        new(12, 99, 99, 0, 56),
        new(194, 70, 60, 0, 30),
        new(197, 100, 100, 0, 0),
    ];
}

/// <summary>偽のディスクの NVMe の応答。</summary>
public sealed record FakeNvmeSpec
{
    public string Firmware { get; init; } = "NV1.0";

    public int TemperatureKelvin { get; init; } = 310;

    public int AvailableSpare { get; init; } = 100;

    public int SpareThreshold { get; init; } = 10;

    public int PercentageUsed { get; init; } = 3;

    public long DataUnitsRead { get; init; } = 123_456;

    public long DataUnitsWritten { get; init; } = 65_432;

    public long PowerCycles { get; init; } = 42;

    public long PowerOnHours { get; init; } = 1_000;

    public long UnsafeShutdowns { get; init; } = 2;

    public long MediaErrors { get; init; }

    public int BlockSize { get; init; } = 512;
}

#if HEX_TEST_HOOKS
/// <summary>偽のデバイスの <see cref="IDeviceInfoSource"/> の応答を作る。ATA に対応しない接続 (USB・仮想ディスク) は <see cref="Win32Errors.NotSupported"/>。</summary>
public static class FakeDeviceInfo
{
    public static int Query(FakeDiskSpec disk, bool isVolume, DeviceInfoKind kind, out byte[] data)
    {
        data = [];
        long sectors = disk.Size / disk.SectorSize;
        if (kind == DeviceInfoKind.StorageDescriptor)
        {
            data = NvmeStructures.BuildStorageDescriptor(string.Empty, disk.Model, (disk.Nvme?.Firmware ?? disk.Ata?.Firmware) ?? "1.0", disk.Serial,
                (int)disk.Bus, disk.Removable);
            return 0;
        }

        bool ata = disk.Ata is not null || disk.Bus is DiskBusType.Sata or DiskBusType.Ata;
        bool nvme = disk.Nvme is not null || disk.Bus == DiskBusType.Nvme;
        if (kind is DeviceInfoKind.TrimSupport or DeviceInfoKind.SeekPenalty)
        {
            if (!ata && !nvme)
            {
                return Win32Errors.NotSupported;
            }

            bool trim = disk.Ata?.Trim ?? true;
            bool ssd = (disk.Ata?.RotationRate ?? 1) == 1;
            data = NvmeStructures.BuildFlagDescriptor(kind == DeviceInfoKind.TrimSupport ? trim : !ssd);
            return 0;
        }

        if (isVolume)
        {
            return Win32Errors.NotSupported;
        }

        if (DeviceInfoPayloads.IsAta(kind))
        {
            if (!ata || nvme)
            {
                return Win32Errors.NotSupported;
            }

            FakeAtaSpec spec = disk.Ata ?? new FakeAtaSpec();
            if (spec.Abort)
            {
                data = AtaCommands.EncodeData([], status: 0x51, error: 0x04);
                return 0;
            }

            switch (kind)
            {
                case DeviceInfoKind.IdentifyDevice:
                    data = AtaCommands.EncodeData(AtaStructures.BuildIdentify(new AtaIdentifyData
                    {
                        Model = disk.Model,
                        Serial = disk.Serial,
                        Firmware = spec.Firmware,
                        Supports48Bit = true,
                        HpaSupported = spec.HpaSupported,
                        HpaEnabled = spec.HpaSupported,
                        DcoSupported = spec.DcoSupported,
                        SmartSupported = true,
                        SmartEnabled = spec.SmartEnabled,
                        LogicalSectorSize = disk.SectorSize,
                        PhysicalSectorSize = disk.PhysicalSectorSize > 0 ? disk.PhysicalSectorSize : disk.SectorSize,
                        RotationRate = spec.RotationRate,
                        TrimSupported = spec.Trim,
                        IsSata = true,
                    }, sectors));
                    return 0;
                case DeviceInfoKind.ReadNativeMaxAddress:
                    if (!spec.HpaSupported)
                    {
                        data = AtaCommands.EncodeData([], status: 0x51, error: 0x04);
                        return 0;
                    }

                    data = AtaCommands.EncodeNativeMax(sectors - 1 + spec.HpaSectors);
                    return 0;
                case DeviceInfoKind.DeviceConfigurationIdentify:
                    if (!spec.DcoSupported)
                    {
                        data = AtaCommands.EncodeData([], status: 0x51, error: 0x04);
                        return 0;
                    }

                    data = AtaCommands.EncodeData(AtaStructures.BuildDco(sectors - 1 + spec.HpaSectors + spec.DcoSectors));
                    return 0;
                case DeviceInfoKind.SmartReadData:
                    data = AtaCommands.EncodeData(AtaStructures.BuildSmartData(spec.Smart.Select(Attribute)));
                    return 0;
                case DeviceInfoKind.SmartReadThresholds:
                    data = AtaCommands.EncodeData(AtaStructures.BuildSmartThresholds(spec.Smart.Select(Attribute)));
                    return 0;
            }
        }

        if (!nvme)
        {
            return Win32Errors.NotSupported;
        }

        FakeNvmeSpec n = disk.Nvme ?? new FakeNvmeSpec();
        switch (kind)
        {
            case DeviceInfoKind.NvmeSmartHealth:
                data = NvmeStructures.BuildHealth(new NvmeHealthData
                {
                    TemperatureKelvin = n.TemperatureKelvin,
                    AvailableSparePercent = n.AvailableSpare,
                    AvailableSpareThresholdPercent = n.SpareThreshold,
                    PercentageUsed = n.PercentageUsed,
                    DataUnitsRead = (UInt128)n.DataUnitsRead,
                    DataUnitsWritten = (UInt128)n.DataUnitsWritten,
                    PowerCycles = (UInt128)n.PowerCycles,
                    PowerOnHours = (UInt128)n.PowerOnHours,
                    UnsafeShutdowns = (UInt128)n.UnsafeShutdowns,
                    MediaErrors = (UInt128)n.MediaErrors,
                });
                return 0;
            case DeviceInfoKind.NvmeIdentifyController:
                data = NvmeStructures.BuildController(disk.Serial, disk.Model, n.Firmware, (UInt128)disk.Size, dsm: true);
                return 0;
            case DeviceInfoKind.NvmeIdentifyNamespace:
                ulong blocks = (ulong)(disk.Size / n.BlockSize);
                data = NvmeStructures.BuildNamespace(blocks, blocks, n.BlockSize);
                return 0;
            default:
                return Win32Errors.InvalidParameter;
        }
    }

    private static SmartAttribute Attribute(FakeSmartAttributeSpec s) => new(s.Id, s.Flags, s.Current, s.Worst, s.Raw, s.Threshold);
}
#endif
