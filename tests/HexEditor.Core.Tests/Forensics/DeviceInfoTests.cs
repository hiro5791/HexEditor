using System.Text.Json;
using HexEditor.Core.Devices;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Elevation;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.Processes;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>
/// FOR-27 HPA / DCO の検出、FOR-28 SMART 情報 (Core)。実機のディスクにはコマンドを送らず、偽のディスクの応答 (<see cref="FakeDeviceInfo"/>) と、
/// 構造体の組み立て・読み込みで確かめる。実機の SATA / NVMe での確認は手動のテストケース (TC-FOR-27-01、TC-FOR-28-01/02)。
/// </summary>
public sealed class DeviceInfoTests
{
    private static readonly DateTime Captured = new(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc);

    private static (FakeDeviceAccess Access, IDeviceInfoSource Source, DiskInfo Disk) Open(FakeDiskSpec disk)
    {
        var access = new FakeDeviceAccess(new FakeDeviceSpec { Elevated = true, Disks = [disk] });
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(disk.Number), writable: false);
        return (access, (IDeviceInfoSource)handle, access.Enumerate().Disks[0]);
    }

    private static FakeDiskSpec Sata(FakeAtaSpec? ata = null) => new()
    {
        Number = 1,
        Model = "HexTest SSD 512",
        Serial = "HXT0000SATA1",
        Size = 64L * 1024 * 1024,
        Bus = DiskBusType.Sata,
        Ata = ata ?? new FakeAtaSpec(),
    };

    [Fact]
    public void Hpa_and_dco_sizes_are_reported_from_the_three_max_lbas()
    {
        // HPA 1,048,576 セクタ (512 MiB 相当) と DCO 2,048 セクタを隠したディスク。
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(Sata(new FakeAtaSpec { HpaSectors = 1_048_576, DcoSectors = 2048 }));
        HpaDcoResult r = DeviceInfoCollector.CheckHpaDco(source, disk);
        long user = 64L * 1024 * 1024 / 512 - 1;
        Assert.Equal(DeviceInfoAvailability.Available, r.Availability);
        Assert.Equal(user, r.MaxLba48);
        Assert.Equal(user, r.MaxLba28);
        Assert.Equal(user + 1_048_576, r.NativeMaxLba);
        Assert.Equal(user + 1_048_576 + 2048, r.DcoMaxLba);
        Assert.True(r.HpaPresent);
        Assert.Equal(1_048_576, r.HpaSectors);
        Assert.True(r.DcoPresent);
        Assert.Equal(2048, r.DcoSectors);
        Assert.True(r.HasHiddenArea);

        // 隠し領域がなければ「なし」。
        (_, source, disk) = Open(Sata());
        r = DeviceInfoCollector.CheckHpaDco(source, disk);
        Assert.False(r.HpaPresent);
        Assert.False(r.DcoPresent);
        Assert.False(r.HasHiddenArea);
    }

    [Theory]
    [InlineData(DiskBusType.Virtual)]
    [InlineData(DiskBusType.Usb)]
    public void Disks_that_do_not_answer_ata_pass_through_report_not_supported_without_exceptions(DiskBusType bus)
    {
        // TC-FOR-27-02・TC-FOR-27-03 の Core の部分: 仮想ディスク・USB 変換は ATA パススルーに応答しない。
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(new FakeDiskSpec { Number = 1, Bus = bus, Size = 1024 * 1024 });
        Assert.Equal(DeviceInfoAvailability.NotSupported, DeviceInfoCollector.CheckHpaDco(source, disk).Availability);
        Assert.Equal(DeviceInfoAvailability.NotSupported, DeviceInfoCollector.ReadSmart(source, disk).Availability);
        DeviceIdentity identity = DeviceInfoCollector.ReadIdentity(source, disk, null);
        Assert.Equal(bus, identity.Bus);
    }

    [Fact]
    public void Ata_errors_report_the_status_and_error_registers()
    {
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(Sata(new FakeAtaSpec { Abort = true }));
        HpaDcoResult r = DeviceInfoCollector.CheckHpaDco(source, disk);
        Assert.Equal(DeviceInfoAvailability.CommandFailed, r.Availability);
        Assert.Equal("IDENTIFY DEVICE", r.FailedCommand);
        Assert.Equal((byte)0x51, r.AtaStatus);
        Assert.Equal((byte)0x04, r.AtaError);
        Assert.Contains("\"ataStatus\": \"0x51\"", new DeviceInfoReport { CapturedUtc = Captured, DevicePath = "x", Identity = new(), HpaDco = r }.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nvme_reports_namespace_size_and_health_information()
    {
        // FOR-27 の仕様 4、FOR-28 の仕様 2。
        var nvme = new FakeDiskSpec { Number = 3, Model = "HexTest NVMe 1TB", Serial = "NVME-0001", Size = 128L * 1024 * 1024, Bus = DiskBusType.Nvme, Nvme = new FakeNvmeSpec { MediaErrors = 5 } };
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(nvme);
        HpaDcoResult h = DeviceInfoCollector.CheckHpaDco(source, disk);
        Assert.True(h.IsNvme);
        Assert.Equal((ulong)(128L * 1024 * 1024 / 512), h.NamespaceSize);
        Assert.Equal(512, h.NamespaceBlockSize);
        Assert.Null(h.HpaPresent);

        SmartResult s = DeviceInfoCollector.ReadSmart(source, disk);
        Assert.Equal(DeviceInfoAvailability.Available, s.Availability);
        NvmeHealthData health = Assert.IsType<NvmeHealthData>(s.Nvme);
        Assert.Equal(37, health.TemperatureCelsius);
        Assert.Equal(100, health.AvailableSparePercent);
        Assert.Equal(3, health.PercentageUsed);
        Assert.Equal((UInt128)123_456 * 512_000, health.BytesRead);
        Assert.Equal((UInt128)42, health.PowerCycles);
        Assert.Equal((UInt128)1000, health.PowerOnHours);
        Assert.Equal((UInt128)2, health.UnsafeShutdowns);
        Assert.Equal((UInt128)5, health.MediaErrors);

        DeviceIdentity id = DeviceInfoCollector.ReadIdentity(source, disk, null);
        Assert.Equal("HexTest NVMe 1TB", id.Model);
        Assert.Equal("NVME-0001", id.Serial);
        Assert.Equal("NV1.0", id.Firmware);
        Assert.True(id.Trim);
        Assert.Equal(1, id.RotationRate);
    }

    [Fact]
    public void Smart_attributes_have_values_thresholds_raw_values_and_warnings()
    {
        var ata = new FakeAtaSpec
        {
            Smart =
            [
                new(5, 100, 100, 10, 0),
                new(9, 98, 98, 0, 0x0000_0001_0002),
                new(197, 5, 5, 10, 300),
                new(250, 100, 100, 0, 7),
            ],
        };
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(Sata(ata));
        SmartResult r = DeviceInfoCollector.ReadSmart(source, disk);
        Assert.Equal(DeviceInfoAvailability.Available, r.Availability);
        SmartData data = Assert.IsType<SmartData>(r.Ata);
        Assert.False(data.ChecksumMismatch);
        Assert.Equal([5, 9, 197, 250], data.Attributes.Select(a => (int)a.Id));
        SmartAttribute pending = data.Attributes.Single(a => a.Id == 197);
        Assert.Equal((byte)10, pending.Threshold);
        Assert.True(pending.BelowThreshold);
        Assert.Equal(300, pending.Raw);
        Assert.Equal("00000000012C", pending.RawHex);
        Assert.False(data.Attributes.Single(a => a.Id == 5).BelowThreshold);
        Assert.Equal(0x0000_0001_0002, data.Attributes.Single(a => a.Id == 9).Raw);
        Assert.Equal("Reallocated Sectors Count", AtaStructures.AttributeName(5));
        Assert.Null(AtaStructures.AttributeName(250));
    }

    [Fact]
    public void Smart_disabled_is_reported()
    {
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(Sata(new FakeAtaSpec { SmartEnabled = false }));
        Assert.Equal(DeviceInfoAvailability.Disabled, DeviceInfoCollector.ReadSmart(source, disk).Availability);
    }

    [Fact]
    public void Exported_json_contains_the_utc_capture_time_and_serial_number()
    {
        // FOR-28 の受け入れ基準 2 (TC-FOR-28-01 の手順 6 の Core の部分)。
        (_, IDeviceInfoSource source, DiskInfo disk) = Open(Sata());
        DeviceInfoReport report = DeviceInfoCollector.Collect(source, DevicePath.PhysicalDrive(1), disk, null, Captured.AddTicks(1234567));
        string json = report.ToJson();
        using JsonDocument doc = JsonDocument.Parse(json);
        string captured = doc.RootElement.GetProperty("capturedUtc").GetString()!;
        Assert.EndsWith("Z", captured, StringComparison.Ordinal);
        Assert.Equal("2026-10-07T03:15:42.1234567Z", captured);
        Assert.Equal("HXT0000SATA1", doc.RootElement.GetProperty("identity").GetProperty("serialNumber").GetString());
        Assert.Equal("FW1.0", doc.RootElement.GetProperty("identity").GetProperty("firmware").GetString());
        Assert.True(doc.RootElement.GetProperty("identity").GetProperty("trim").GetBoolean());
        Assert.Equal(5, doc.RootElement.GetProperty("smart").GetProperty("attributes").GetArrayLength());
        string text = report.ToText();
        Assert.Contains("2026-10-07T03:15:42.1234567Z", text, StringComparison.Ordinal);
        Assert.Contains("HXT0000SATA1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Identify_round_trips_strings_sizes_sector_sizes_rotation_and_features()
    {
        var info = new AtaIdentifyData
        {
            Model = "WDC WD40EFRX-68N32N0",
            Serial = "WD-WCC7K1234567",
            Firmware = "82.00A82",
            Supports48Bit = true,
            HpaSupported = true,
            HpaEnabled = true,
            DcoSupported = true,
            SmartSupported = true,
            SmartEnabled = true,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            RotationRate = 5400,
            TrimSupported = false,
            IsSata = true,
        };
        byte[] data = AtaStructures.BuildIdentify(info, 7_814_037_168);
        AtaIdentifyData parsed = AtaStructures.ParseIdentify(data)!;
        Assert.Equal(info.Model, parsed.Model);
        Assert.Equal(info.Serial, parsed.Serial);
        Assert.Equal(info.Firmware, parsed.Firmware);
        Assert.Equal(7_814_037_167, parsed.MaxLba48);
        Assert.Equal(0x0FFF_FFFE, parsed.MaxLba28);
        Assert.Equal(4096, parsed.PhysicalSectorSize);
        Assert.Equal(5400, parsed.RotationRate);
        Assert.False(parsed.TrimSupported);
        Assert.False(parsed.ChecksumMismatch);
        data[100] ^= 1;
        Assert.True(AtaStructures.ParseIdentify(data)!.ChecksumMismatch);
        Assert.Null(AtaStructures.ParseIdentify(new byte[100]));
    }

    [Fact]
    public void Only_read_only_ata_commands_are_built_and_none_sends_data()
    {
        // FOR-27 の仕様 3、FOR-28 の仕様 5: 設定を変えるコマンド (SET MAX ADDRESS 0xF9、DCO RESTORE、SMART のセルフテストなど) は作らない。
        foreach (DeviceInfoKind kind in new[] { DeviceInfoKind.IdentifyDevice, DeviceInfoKind.ReadNativeMaxAddress, DeviceInfoKind.DeviceConfigurationIdentify,
                     DeviceInfoKind.SmartReadData, DeviceInfoKind.SmartReadThresholds })
        {
            foreach (bool extended in new[] { true, false })
            {
                AtaTaskFile command = AtaCommands.For(kind, extended)!.Value;
                Assert.True(AtaCommands.IsAllowed(command));
                foreach (bool is64 in new[] { true, false })
                {
                    byte[] buffer = AtaCommands.BuildPassThrough(command, is64);
                    ushort flags = BitConverter.ToUInt16(buffer, 2);
                    Assert.Equal(0, flags & 0x04); // ATA_FLAGS_DATA_OUT は使わない
                    Assert.Equal(AtaCommands.PassThroughSize(is64) + command.DataLength, buffer.Length);
                }
            }
        }

        AtaTaskFile setMax = new(0, 0, 0, 0, 0, 0x40, 0xF9, false, false, 0);
        AtaTaskFile dcoRestore = AtaCommands.DeviceConfigurationIdentify with { Features = 0xC0 };
        AtaTaskFile selfTest = AtaCommands.SmartReadData with { Features = 0xD4 };
        foreach (AtaTaskFile bad in new[] { setMax, dcoRestore, selfTest })
        {
            Assert.False(AtaCommands.IsAllowed(bad));
            Assert.Throws<ArgumentException>(() => AtaCommands.BuildPassThrough(bad, true));
        }

        Assert.Null(AtaCommands.For(DeviceInfoKind.NvmeSmartHealth));
    }

    [Fact]
    public void Native_max_is_read_from_the_48_and_28_bit_registers()
    {
        byte[] cur = [0, 0, 0x78, 0x56, 0x34, 0x40, 0x50, 0];
        byte[] prev = [0, 0, 0x12, 0x00, 0x01, 0, 0, 0];
        Assert.Equal(0x0100_1234_5678L, AtaCommands.NativeMaxFromRegisters(cur, prev, extended: true));
        cur[5] = 0x4A;
        Assert.Equal(0x0A34_5678L, AtaCommands.NativeMaxFromRegisters(cur, prev, extended: false));
        byte[] encoded = AtaCommands.EncodeResult(new AtaResult(0x50, 0, [], cur, prev), DeviceInfoKind.ReadNativeMaxAddress, extended: false);
        (byte status, byte error, byte[] payload) = AtaCommands.DecodeResult(encoded)!.Value;
        Assert.Equal((0x50, 0), (status, error));
        Assert.Equal(0x0A34_5678L, BitConverter.ToInt64(payload));
    }

    [Fact]
    public void Storage_descriptor_and_nvme_query_buffers_are_read()
    {
        byte[] descriptor = NvmeStructures.BuildStorageDescriptor("ATA", "Samsung SSD 870", "SVT02B6Q", "S6PNNS0T123456", 11, false);
        StorageDescriptorData d = NvmeStructures.ParseStorageDescriptor(descriptor)!;
        Assert.Equal(("ATA", "Samsung SSD 870", "SVT02B6Q", "S6PNNS0T123456", 11), (d.Vendor, d.Product, d.Revision, d.Serial, d.BusType));

        byte[] query = NvmeStructures.BuildQuery(NvmeStructures.StorageDeviceProtocolSpecificProperty, NvmeStructures.NvmeDataTypeLogPage, 2, 0, 512);
        Assert.Equal(8 + 40 + 512, query.Length);
        Assert.Equal(40u, BitConverter.ToUInt32(query, 8 + 16));
        query[8 + 40] = 0xAB;
        Assert.Equal(0xAB, NvmeStructures.ProtocolData(query)![0]);
        BitConverter.GetBytes(4096u).CopyTo(query, 8 + 20);
        Assert.Null(NvmeStructures.ProtocolData(query));
    }

    [Fact]
    public async Task The_helper_answers_device_info_queries_through_the_pipe_even_in_write_protection_mode()
    {
        (Stream a, Stream b) = DuplexStreams.CreatePair();
        using var devices = new FakeDeviceAccess(new FakeDeviceSpec { Elevated = true, Disks = [Sata(new FakeAtaSpec { HpaSectors = 2048 })] });
        var operations = new PrivilegedOperations(devices, new FakeProcessAccess(new FakeProcessListSpec { Elevated = true }), null, "1.0.0", new FakeUsbWriteProtectStore());
        var server = new HelperServer(b, operations) { IdleTimeout = null };
        _ = server.RunAsync();
        await using var client = new HelperClient(a) { Timeout = TimeSpan.FromSeconds(10) };
        await client.SendAsync(HelperCommand.Hello, 0, new BodyWriter().U32(HelperProtocol.Version).Text("1.0.0").ToArray());
        await client.SendAsync(HelperCommand.SetWriteBlock, 0, new byte[] { 1 });
        var access = new HelperDeviceAccess(client);
        IDeviceHandle handle = await Task.Run(() => access.Open(DevicePath.PhysicalDrive(1), writable: false));
        HpaDcoResult r = await Task.Run(() => DeviceInfoCollector.CheckHpaDco((IDeviceInfoSource)handle, devices.Enumerate().Disks[0]));
        Assert.True(r.HpaPresent);
        Assert.Equal(2048, r.HpaSectors);
        Assert.Contains(devices.Calls, c => c.Contains("QueryDeviceInfo", StringComparison.Ordinal) && c.Contains("ReadNativeMaxAddress", StringComparison.Ordinal));
        handle.Dispose();
    }
}
