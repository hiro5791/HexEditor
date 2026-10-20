using System.Text;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Encryption;
using HexEditor.Core.Forensics.Images.Raid;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Core.Wiping;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>フォレンジックの領域をつなぐ部品 (LDM の RAID → FOR-26、パーティションごとの暗号化の検出 FOR-06 → FOR-29、FAT の日時 FOR-05、書き込み保護 FOR-01)。</summary>
[Collection(Support.SerialCollection.Name)]
public sealed class ForensicsIntegrationTests
{
    [Fact]
    public void An_ldm_mirror_opens_as_a_raid1_disk_of_the_member_ranges()
    {
        byte[] disk = new byte[1024 * 1024];
        byte[] payload = Encoding.ASCII.GetBytes("MIRROR-PAYLOAD");
        payload.CopyTo(disk, 0x10000);
        var request = new LogicalVolumeRaidRequest(LogicalVolumeKind.Mirror, "Volume1", 0x40000, 0, [("A", 0x10000, 0x40000), ("B", 0x10000, 0x40000)], "Mirror");
        var readers = new Dictionary<string, IVolumeReader> { ["A"] = new ArrayVolumeReader(disk) };

        RaidConfig config = LogicalVolumeRaid.ConfigOf(request, readers);
        Assert.Equal(RaidLevel.Raid1, config.Level);
        Assert.False(config.Members[0].Missing);
        Assert.True(config.Members[1].Missing);
        Assert.Equal(0, config.UseMember);

        using RaidByteSource source = LogicalVolumeRaid.Open(request, readers);
        Assert.Equal(0x40000, source.Length);
        byte[] head = new byte[payload.Length];
        source.Read(0, head);
        Assert.Equal(payload, head);
    }

    [Fact]
    public void An_ldm_raid5_request_maps_to_a_left_asymmetric_raid5_configuration()
    {
        var request = new LogicalVolumeRaidRequest(LogicalVolumeKind.Raid5, "R5", 0x80000, 0x10000,
            [("A", 0, 0x40000), ("B", 0, 0x40000), ("C", 0, 0x40000)], "LeftAsymmetric");
        RaidConfig config = LogicalVolumeRaid.ConfigOf(request, new Dictionary<string, IVolumeReader>
        {
            ["A"] = new ArrayVolumeReader(new byte[0x40000]), ["B"] = new ArrayVolumeReader(new byte[0x40000]), ["C"] = new ArrayVolumeReader(new byte[0x40000]),
        });
        Assert.Equal(RaidLevel.Raid5, config.Level);
        Assert.Equal(Raid5Layout.LeftAsymmetric, config.Layout);
        Assert.Equal(0x10000, config.StripeSize);
        Assert.Null(config.Validate());
    }

    [Fact]
    public void Partition_analysis_marks_a_luks2_partition_as_encrypted()
    {
        byte[] luks = LuksParser.BuildLuks2("12345678-1234-1234-1234-123456789abc", 1);
        var analysis = new PartitionAnalysis
        {
            SectorSize = 512,
            DiskLength = 4 * 1024 * 1024,
            Rows =
            [
                new PartitionRow { Number = 1, Scheme = PartitionScheme.Gpt, StartLba = 2048, SectorCount = 4096, SectorSize = 512 },
                new PartitionRow { Number = 2, Scheme = PartitionScheme.Gpt, StartLba = 6144, SectorCount = 1024, SectorSize = 512 },
            ],
        };
        byte[] disk = new byte[analysis.DiskLength];
        luks.CopyTo(disk, 2048 * 512);

        PartitionAnalysis annotated = PartitionEncryption.Annotate(analysis, new MemoryCarveData(disk));
        Assert.Equal(EncryptionKind.Luks2, annotated.Rows[0].Encryption?.Kind);
        Assert.Null(annotated.Rows[1].Encryption);
    }

    [Fact]
    public void Fat_times_without_a_zone_are_read_in_the_case_time_zone_with_the_estimate_mark()
    {
        var recorded = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Unspecified);
        var fat = new FsTimestamp(new DateTime(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc), ZoneUnknown: true, Raw: 0x5C2F5000) { Recorded = recorded };
        TimeZoneInfo tokyo = TimeZoneInfo.CreateCustomTimeZone("Test Tokyo", TimeSpan.FromHours(9), "Test Tokyo", "Test Tokyo");

        Core.Forensics.Time.ForensicTimestamp inTokyo = FsTimeConversion.ToForensic(fat, tokyo)!.Value;
        Assert.True(inTokyo.ZoneEstimated);
        Assert.Equal(new DateTime(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc), inTokyo.Utc);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), FsTimeConversion.ToForensic(fat, TimeZoneInfo.Utc)!.Value.Utc);
        Assert.Null(FsTimeConversion.ToForensic(default, tokyo));
        Assert.False(FsTimeConversion.ToForensic(new FsTimestamp(null, Invalid: true, Raw: 0x7FFF), tokyo)!.Value.IsValid);
    }

    [Fact]
    public void Wipes_are_refused_while_write_protection_is_on_even_without_the_app_hooks()
    {
        var policy = new WriteBlockPolicy();
        policy.SetEnabled(true);
        using (WriteBlockGate.Use(policy))
        {
            Assert.Equal(WipeRefusal.WriteBlock, WipeRunner.Check("file:x", "x.bin", "x.bin", out _, NullWipeForensicHooks.Instance));
            Assert.Throws<WriteBlockedException>(() => policy.EnsureOperation(BlockedOperation.Clone));
        }

        Assert.Equal(WipeRefusal.None, WipeRunner.Check("file:x", "x.bin", "x.bin", out _, NullWipeForensicHooks.Instance));
    }
}
