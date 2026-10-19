using System.Buffers.Binary;
using HexEditor.Core.Devices;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Elevation;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Encryption;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.MemoryDumps;
using HexEditor.Core.Processes;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// フェーズ 5 のデバイス情報 (FOR-27・28)、暗号化の検出 (FOR-29)、メモリダンプ (ENG-36) の、信頼できない入力 (デバイスの応答、ディスクの内容、
/// ダンプのファイル) を読む部品のファジング。正しいデータを変異させたものと乱数のデータを与え、想定外の例外・無限ループ・大量のメモリがないことを確かめる。
/// </summary>
public sealed class DevicesProtectFuzzTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-fuzz-devices").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Identify() => AtaStructures.BuildIdentify(new AtaIdentifyData
    {
        Model = "Fuzz SSD",
        Serial = "FZ0001",
        Firmware = "1.0",
        Supports48Bit = true,
        HpaSupported = true,
        DcoSupported = true,
        SmartSupported = true,
        SmartEnabled = true,
        LogicalSectorSize = 4096,
        PhysicalSectorSize = 4096,
        RotationRate = 7200,
        TrimSupported = true,
        IsSata = true,
    }, 1_000_000);

    [Fact]
    public void Ata_structures_and_pass_through_buffers_never_throw()
    {
        SmartAttribute[] attributes = [new(5, 100, 100, 10, 0, 10), new(194, 30, 20, 0, 0x1234_5678, 0)];
        byte[][] seeds =
        [
            Identify(), AtaStructures.BuildDco(2_000_000), AtaStructures.BuildSmartData(attributes), AtaStructures.BuildSmartThresholds(attributes),
            AtaCommands.BuildPassThrough(AtaCommands.IdentifyDevice, true), AtaCommands.BuildPassThrough(AtaCommands.ReadNativeMaxAddressExt, false),
            AtaCommands.EncodeNativeMax(12345), AtaCommands.EncodeData(Identify()),
        ];
        Fuzz.Run(Fuzz.Cases(2701, seeds, mutations: 1500, randoms: 300, maxRandomLength: 1024), data =>
        {
            _ = AtaStructures.ParseIdentify(data);
            _ = AtaStructures.ParseDco(data);
            _ = AtaStructures.ParseSmart(data, data);
            _ = AtaStructures.ParseSmart(data, []);
            foreach (bool is64 in new[] { true, false })
            {
                _ = AtaCommands.ParsePassThrough(data, AtaCommands.IdentifyDevice, is64);
                _ = AtaCommands.ParsePassThrough(data, AtaCommands.ReadNativeMaxAddressExt, is64);
            }

            _ = AtaCommands.DecodeResult(data);
            _ = AtaCommands.NativeMaxFromRegisters(data, data, true);
            _ = AtaStructures.AtaString(data, 0, Math.Min(64, data.Length / 2 + 1));
        }, timeoutMs: 20_000, maxAllocatedBytes: 16L * 1024 * 1024);
    }

    [Fact]
    public void Nvme_and_storage_descriptor_structures_never_throw()
    {
        byte[][] seeds =
        [
            NvmeStructures.BuildHealth(new NvmeHealthData { TemperatureKelvin = 300, DataUnitsRead = 5 }),
            NvmeStructures.BuildController("SN", "Model", "FW", 1_000_000, true),
            NvmeStructures.BuildNamespace(1000, 1000, 4096),
            NvmeStructures.BuildStorageDescriptor("V", "P", "R", "S", 17, true),
            NvmeStructures.BuildFlagDescriptor(true),
            NvmeStructures.BuildQuery(NvmeStructures.StorageDeviceProtocolSpecificProperty, NvmeStructures.NvmeDataTypeLogPage, 2, 0, 512),
        ];
        Fuzz.Run(Fuzz.Cases(2702, seeds, mutations: 1500, randoms: 300, maxRandomLength: 8192, maxLength: 1 << 14), data =>
        {
            _ = NvmeStructures.ParseHealth(data);
            _ = NvmeStructures.ParseController(data);
            _ = NvmeStructures.ParseNamespace(data);
            _ = NvmeStructures.ParseStorageDescriptor(data);
            _ = NvmeStructures.ParseFlagDescriptor(data);
            _ = NvmeStructures.ProtocolData(data);
        }, timeoutMs: 20_000, maxAllocatedBytes: 32L * 1024 * 1024);
    }

    /// <summary>偽の応答をそのまま返すデバイス (コレクタが壊れた応答に耐えるか)。</summary>
    private sealed class ScriptedSource(byte[] payload) : IDeviceInfoSource
    {
        public int QueryDeviceInfo(DeviceInfoKind kind, out byte[] data)
        {
            data = payload;
            return 0;
        }
    }

    [Fact]
    public void The_collector_survives_arbitrary_device_answers()
    {
        byte[][] seeds = [AtaCommands.EncodeData(Identify()), AtaCommands.EncodeNativeMax(-5), AtaCommands.EncodeData(AtaStructures.BuildDco(1)), new byte[8]];
        var sata = new DiskInfo { Number = 1, Bus = DiskBusType.Sata };
        var nvme = new DiskInfo { Number = 1, Bus = DiskBusType.Nvme };
        Fuzz.Run(Fuzz.Cases(2703, seeds, mutations: 800, randoms: 200, maxRandomLength: 1024), data =>
        {
            var source = new ScriptedSource(data);
            foreach (DiskInfo disk in new[] { sata, nvme })
            {
                DeviceInfoReport report = DeviceInfoCollector.Collect(source, @"\\.\PhysicalDrive1", disk, new DeviceGeometry(1 << 20, 512, 512), DateTime.UtcNow);
                _ = report.ToJson();
                _ = report.ToText();
            }
        }, timeoutMs: 20_000, maxAllocatedBytes: 64L * 1024 * 1024);
    }

    [Fact]
    public void The_helper_rejects_or_answers_malformed_device_info_requests()
    {
        using var devices = new FakeDeviceAccess(new FakeDeviceSpec { Elevated = true, Disks = [new FakeDiskSpec { Number = 0, Bus = DiskBusType.Sata, Size = 1 << 20 }] });
        var operations = new PrivilegedOperations(devices, new FakeProcessAccess(new FakeProcessListSpec()), null, "1", new FakeUsbWriteProtectStore());
        (_, byte[] opened) = operations.Execute((ushort)HelperCommand.OpenDevice, 0, new BodyWriter().Text(DevicePath.PhysicalDrive(0)).ToArray());
        uint handle = new BodyReader(opened).U32();
        byte[][] seeds = [new BodyWriter().U32(handle).U16(1).ToArray(), new BodyWriter().U32(handle).U16(6).ToArray(), new BodyWriter().U32(1).ToArray()];
        Fuzz.Run(Fuzz.Cases(2704, seeds, mutations: 600, randoms: 100, maxRandomLength: 64), body =>
        {
            foreach (HelperCommand command in new[] { HelperCommand.QueryDeviceInfo, HelperCommand.GetUsbWriteProtect, HelperCommand.SetUsbWriteProtect })
            {
                (uint status, byte[] response) = operations.Execute((ushort)command, 0, body);
                Assert.True(status != 0 || response.Length <= 64 * 1024);
            }
        }, allowed: ex => ex is HelperProtocolException, maxAllocatedBytes: 16L * 1024 * 1024);
    }

    private static byte[] BitLockerSample()
    {
        IReadOnlyList<(long Offset, byte[] Data)> parts = BitLockerParser.BuildVolume(64 * 1024, 0x8004, Guid.NewGuid(), DateTime.UtcNow, "FUZZ C: 2026/10/07",
            [new BitLockerParser.ProtectorSpec(0x2000, Guid.NewGuid(), DateTime.UtcNow), new BitLockerParser.ProtectorSpec(0x0800, Guid.NewGuid(), DateTime.UtcNow)],
            [0x1000, 0x2000, 0x3000]);
        byte[] volume = new byte[0x4000];
        foreach ((long offset, byte[] data) in parts)
        {
            data.CopyTo(volume, offset);
        }

        return volume;
    }

    private static byte[] GptSample()
    {
        byte[] disk = new byte[64 * 1024];
        disk[446 + 4] = 0xEE;
        disk[510] = 0x55;
        disk[511] = 0xAA;
        "EFI PART"u8.CopyTo(disk.AsSpan(512));
        BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(512 + 72), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(512 + 80), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(512 + 84), 128);
        EncryptionDetector.CoreStorageType.TryWriteBytes(disk.AsSpan(1024));
        BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(1024 + 32), 40);
        BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(1024 + 40), 100);
        LuksParser.BuildLuks2("11111111-2222-3333-4444-555555555555", 1, headerSize: 16384).AsSpan(0, 16384).CopyTo(disk.AsSpan(40 * 512));
        return disk;
    }

    [Fact]
    public void Encryption_parsers_and_the_partition_scan_never_throw()
    {
        byte[] luks1 = new byte[2048];
        LuksParser.Magic.CopyTo(luks1, 0);
        luks1[7] = 1;
        byte[][] seeds =
        [
            BitLockerSample(), LuksParser.BuildLuks2("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", 2, headerSize: 16384), luks1,
            ApfsEncryptionProbe.BuildContainer([("A", true), ("B", false)]), GptSample(),
        ];
        Fuzz.Run(Fuzz.Cases(2905, seeds, mutations: 800, randoms: 200, maxRandomLength: 4096, maxLength: 1 << 17), data =>
        {
            var carve = new MemoryCarveData(data);
            _ = EncryptionDetector.Detect(carve, 0, data.Length);
            _ = EncryptionDetector.Detect(carve, 0, data.Length, EncryptionDetector.CoreStorageType);
            _ = EncryptionDetector.Scan(carve);
            _ = BitLockerParser.Parse(carve, 0, data.Length);
            _ = LuksParser.Parse(carve, 0);
            _ = ApfsEncryptionProbe.Probe(carve, 0);
        }, timeoutMs: 30_000, maxAllocatedBytes: 64L * 1024 * 1024);

        // LUKS2 の JSON の領域だけを文字単位で変異させる。
        var random = new Random(2906);
        string json = System.Text.Encoding.ASCII.GetString(LuksParser.BuildLuks2("u", 3, headerSize: 16384).AsSpan(4096, 12288)).TrimEnd('\0');
        Fuzz.Run(Enumerable.Range(0, 1000).Select(_ => Fuzz.MutateText(random, json, "{}[]\":,0123456789abc \\")), text =>
        {
            _ = LuksParser.ParseJson(System.Text.Encoding.UTF8.GetBytes(text));
        });
    }

    private byte[] MinidumpSample()
    {
        const int pid = 777;
        var access = new FakeProcessAccess(new FakeProcessListSpec
        {
            Elevated = true,
            Processes =
            [
                new FakeProcessSpec
                {
                    Pid = pid, Name = "fuzz.exe", AddressLimit = 0x100000,
                    Regions = [new FakeRegionSpec { Base = 0x10000, Size = 0x2000, Data = "AB" }, new FakeRegionSpec { Base = 0x40000, Size = 0x1000, Data = "CD", Type = RegionType.Image, Name = "fuzz.exe" }],
                    Modules = [new ProcessModule("fuzz.exe", 0x40000, 0x1000, @"C:\fuzz.exe")],
                },
            ],
        });
        using var source = new ProcessMemoryByteSource(access.Open(pid, false), access, new ProcessOpenInfo { DisplayName = "fuzz.exe" });
        string path = Path.Combine(_dir, "seed.dmp");
        MinidumpWriter.Write(source, path, new ProcessCaptureOptions());
        return File.ReadAllBytes(path);
    }

    [Fact]
    public void Memory_dump_headers_and_minidumps_are_rejected_cleanly_when_broken()
    {
        byte[] full = [.. CrashDumpHeader.BuildFull64([new PhysicalRun(1, 2), new PhysicalRun(16, 1)], DateTime.UtcNow), .. new byte[3 * 4096]];
        byte[] bitmap = new byte[4];
        bitmap[0] = 0b1010_0001;
        byte[] kernel = [.. CrashDumpHeader.BuildBitmap64(MemoryDumpFormat.DumpTypeBitmapKernel, bitmap, 32, DateTime.UtcNow), .. new byte[3 * 4096]];
        byte[] mini = MinidumpSample();
        byte[][] seeds = [full, kernel, mini];
        Fuzz.Run(Fuzz.Cases(3601, seeds, mutations: 700, randoms: 200, maxRandomLength: 0x3000, maxLength: 1 << 17), data =>
        {
            _ = MemoryDumpFormat.Detect(data);
            try
            {
                _ = CrashDumpHeader.Parse(data, data.Length, (offset, count) =>
                    offset >= 0 && count >= 0 && offset <= data.Length - count ? data.AsSpan((int)offset, count).ToArray() : null);
            }
            catch (MemoryDumpFormatException)
            {
            }

            using var stream = new MemoryStream(data, writable: false);
            SnapshotMetadata metadata = MinidumpReader.Read(stream, "fuzz");
            Assert.All(metadata.Regions, r => Assert.True(r.Size > 0 && r.FileOffset >= 0 && r.FileOffset <= data.Length - r.Size));
        }, allowed: ex => ex is MemoryDumpFormatException, timeoutMs: 30_000, maxAllocatedBytes: 64L * 1024 * 1024);

        // 開いて読むところまで (ファイル経由。壊れた範囲の一覧で範囲外を読まない)。
        string path = Path.Combine(_dir, "fuzz.dmp");
        Fuzz.Run(Fuzz.Cases(3602, [full, kernel], mutations: 150), data =>
        {
            File.WriteAllBytes(path, data);
            try
            {
                using CrashDumpByteSource source = CrashDumpByteSource.Open(path);
                byte[] buffer = new byte[8192];
                _ = source.Read(0, buffer);
                _ = source.Read(Math.Max(0, source.Length - 4096), buffer);
            }
            catch (MemoryDumpFormatException)
            {
            }
        }, timeoutMs: 30_000);
    }
}
