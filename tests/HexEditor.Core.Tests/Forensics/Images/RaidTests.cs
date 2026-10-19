using System.Security.Cryptography;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Forensics.Images.Raid;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Forensics.Images.ImageTestSupport;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>FOR-26 RAID の再構成。</summary>
public sealed class RaidTests
{
    private static RaidConfig Raid5(bool missingM1 = false) => new()
    {
        Level = RaidLevel.Raid5,
        StripeSize = 64 * 1024,
        Layout = Raid5Layout.LeftSymmetric,
        Members =
        [
            new RaidMember { Source = TestDataCatalog.Get("TD-FOR-RAID5-M0") },
            new RaidMember { Source = TestDataCatalog.Get("TD-FOR-RAID5-M1"), Missing = missingM1 },
            new RaidMember { Source = TestDataCatalog.Get("TD-FOR-RAID5-M2") },
        ],
    };

    private static RaidByteSource Open(RaidConfig config) =>
        new(config, [.. config.Members.Select(m => m.Missing ? null : (IByteSource)FileByteSource.Open(m.Source))], ownsMembers: true);

    [Fact]
    [Trait("TC", "TC-FOR-26-01")]
    public void Raid5_logical_disk_matches_the_source_also_with_one_member_missing()
    {
        using FileByteSource expected = FileByteSource.Open(TestDataCatalog.Get("TD-FOR-RAID-SRC"));
        byte[] sha = Hash(expected, HashAlgorithmName.SHA256);
        foreach (bool missing in new[] { false, true })
        {
            using RaidByteSource raid = Open(Raid5(missing));
            Assert.Equal(128 * MiB, raid.Length);
            Assert.Equal(sha, Hash(raid, HashAlgorithmName.SHA256));

            // 論理ディスクは読み取り専用。
            Assert.False(raid.Capabilities.HasFlag(SourceCapabilities.CanWrite));
            Assert.Throws<NotSupportedException>(() => raid.Write(0, [1]));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-26-02")]
    public void Raid0_detection_changes_from_gpt_to_no_partition_table_when_the_order_is_swapped()
    {
        string m0 = TestDataCatalog.Get("TD-FOR-RAID0-M0");
        string m1 = TestDataCatalog.Get("TD-FOR-RAID0-M1");
        RaidConfig config = new() { Level = RaidLevel.Raid0, StripeSize = 64 * 1024, Members = [new() { Source = m0 }, new() { Source = m1 }] };
        using (RaidByteSource raid = Open(config))
        {
            SniffResult r = VolumeSniffer.Sniff(raid);
            Assert.Equal("GPT", r.PartitionTable);
            Assert.Equal(1, r.PartitionCount);
            using FileByteSource expected = FileByteSource.Open(TestDataCatalog.Get("TD-FOR-RAID-SRC"));
            Assert.Equal(Hash(expected, HashAlgorithmName.SHA256), Hash(raid, HashAlgorithmName.SHA256));
        }

        using RaidByteSource swapped = Open(config with { Members = [new() { Source = m1 }, new() { Source = m0 }] });
        SniffResult s = VolumeSniffer.Sniff(swapped);
        Assert.Null(s.PartitionTable);
        Assert.True(s.Nothing);
    }

    [Theory]
    [InlineData(Raid5Layout.LeftSymmetric)]
    [InlineData(Raid5Layout.LeftAsymmetric)]
    [InlineData(Raid5Layout.RightSymmetric)]
    [InlineData(Raid5Layout.RightAsymmetric)]
    public void Every_raid5_layout_reconstructs_any_single_missing_member(Raid5Layout layout)
    {
        const int Disks = 4;
        const int Stripe = 4096;
        byte[] source = new byte[Stripe * (Disks - 1) * 12];
        new Random(5).NextBytes(source);
        byte[][] members = [.. Enumerable.Range(0, Disks).Select(_ => new byte[Stripe * 12])];
        for (long pos = 0; pos < source.Length; pos += Stripe)
        {
            (int data, int parity, long at) = RaidByteSource.Map(pos, Disks, Stripe, layout);
            Assert.NotEqual(data, parity);
            source.AsSpan((int)pos, Stripe).CopyTo(members[data].AsSpan((int)at));
            for (int i = 0; i < Stripe; i++)
            {
                members[parity][at + i] ^= source[pos + i];
            }
        }

        for (int missing = -1; missing < Disks; missing++)
        {
            var config = new RaidConfig
            {
                Level = RaidLevel.Raid5,
                Layout = layout,
                StripeSize = Stripe,
                Members = [.. Enumerable.Range(0, Disks).Select(i => new RaidMember { Source = "m" + i, Missing = i == missing })],
            };
            using var raid = new RaidByteSource(config, [.. members.Select((m, i) => i == missing ? null : (IByteSource)new MemoryByteSource(m))]);
            Assert.Equal(source.Length, raid.Length);
            Assert.Equal(source, ReadAll(raid, 0, source.Length));
        }
    }

    [Fact]
    public void Jbod_raid1_and_data_offsets_and_size_mismatch()
    {
        byte[] a = [.. Enumerable.Range(0, 1024).Select(i => (byte)i)];
        byte[] b = [.. Enumerable.Range(0, 2048).Select(i => (byte)(i * 3))];
        var jbod = new RaidConfig { Level = RaidLevel.Jbod, Members = [new() { Source = "a" }, new() { Source = "b", DataOffset = 512 }] };
        using (var raid = new RaidByteSource(jbod, [new MemoryByteSource(a), new MemoryByteSource(b)]))
        {
            Assert.Equal(1024 + 1536, raid.Length);
            Assert.Equal(a.Concat(b.Skip(512)).ToArray(), ReadAll(raid, 0, (int)raid.Length));
        }

        var mirror = new RaidConfig { Level = RaidLevel.Raid1, UseMember = 1, Members = [new() { Source = "a" }, new() { Source = "b" }] };
        using (var raid = new RaidByteSource(mirror, [new MemoryByteSource(a), new MemoryByteSource(b)]))
        {
            Assert.Equal(b, ReadAll(raid, 0, b.Length));
        }

        var stripe = new RaidConfig { Level = RaidLevel.Raid0, StripeSize = 512, Members = [new() { Source = "a" }, new() { Source = "b" }] };
        using (var raid = new RaidByteSource(stripe, [new MemoryByteSource(a), new MemoryByteSource(b)]))
        {
            Assert.True(raid.SizesDiffer);
            Assert.Equal(2048, raid.Length);
            Assert.Equal(b.AsSpan(0, 512).ToArray(), ReadAll(raid, 512, 512));
        }
    }

    [Fact]
    public void Two_missing_raid5_members_and_invalid_parameters_are_refused()
    {
        var two = new RaidConfig
        {
            Level = RaidLevel.Raid5,
            Members = [new() { Source = "a", Missing = true }, new() { Source = "b", Missing = true }, new() { Source = "c" }],
        };
        Assert.Equal(RaidProblem.TooManyMissing, two.Validate());
        Assert.Equal(RaidProblem.TooManyMissing,
            Assert.Throws<RaidConfigException>(() => new RaidByteSource(two, [null, null, new MemoryByteSource(new byte[4096])])).Problem);
        Assert.Equal(RaidProblem.InvalidStripeSize, (two with { Members = [new() { Source = "a" }, new() { Source = "b" }, new() { Source = "c" }], StripeSize = 3000 }).Validate());
        Assert.Equal(RaidProblem.InvalidStripeSize, (two with { StripeSize = 32L * 1024 * 1024 }).Validate());
        Assert.Equal(RaidProblem.TooFewMembers, new RaidConfig { Level = RaidLevel.Raid5, Members = [new() { Source = "a" }, new() { Source = "b" }] }.Validate());
    }

    [Fact]
    public void Configuration_round_trips_through_json_and_bad_json_is_rejected()
    {
        RaidConfig config = Raid5(missingM1: true) with { Layout = Raid5Layout.RightAsymmetric };
        RaidConfig copy = RaidConfig.Parse(config.ToJson());
        Assert.Equal(config.Level, copy.Level);
        Assert.Equal(config.Layout, copy.Layout);
        Assert.Equal(config.Members, copy.Members);
        Assert.Contains("\"level\": \"Raid5\"", config.ToJson());
        Assert.Throws<InvalidDataException>(() => RaidConfig.Parse("{"));
        Assert.Throws<InvalidDataException>(() => RaidConfig.Parse("null"));
        Assert.Throws<InvalidDataException>(() => RaidConfig.Parse("{\"level\":\"Raid5\",\"members\":[{\"source\":\"a\"}]}"));
    }
}
