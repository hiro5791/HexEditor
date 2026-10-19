using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Sources;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>FOR-07 LDM 動的ディスクと LVM2。イメージはテストデータの生成ツール (LogicalVolumeImages) がコードで書いたもの。</summary>
public sealed class LogicalVolumeTests
{
    private static IReadOnlyList<LogicalVolumeMember> Discover(string id) => Discover(FsTestImages.Reader(id), id);

    private static IReadOnlyList<LogicalVolumeMember> Discover(IVolumeReader reader, string name) =>
        LogicalVolumeAnalyzer.Discover(reader, name, PartitionAnalyzer.Analyze(reader, 512));

    private static byte[] ReadAll(IByteSource source)
    {
        byte[] b = new byte[source.Length];
        source.Read(0, b);
        return b;
    }

    [Fact]
    [Trait("TC", "TC-FOR-07-01")]
    public void Ldm_striped_volume_over_two_disks_opens_as_one_document()
    {
        PartitionAnalysis a = PartitionAnalyzer.Analyze(FsTestImages.Reader("TD-FOR-LDM-STRIPE-A"), 512);
        Assert.Contains(a.Rows, r => r.FileSystem == FileSystemKind.Ldm);

        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. Discover("TD-FOR-LDM-STRIPE-A"), .. Discover("TD-FOR-LDM-STRIPE-B")]);
        Assert.Empty(scan.Issues);
        LogicalVolumeInfo v = Assert.Single(scan.Volumes);
        Assert.Equal(LogicalVolumeKind.Striped, v.Kind);
        Assert.Equal(2, v.Members.Distinct().Count());
        Assert.Equal(LogicalVolumeScheme.Ldm, v.Scheme);
        Assert.Empty(scan.MissingMembers(v));

        LogicalVolumeByteSource source = LogicalVolumeAnalyzer.Open(v, scan);
        SparseImage expected = FileSystemImageSet.Image("TD-FOR-LDM-STRIPE-VOL");
        Assert.Equal(expected.Length, source.Length);
        Assert.Equal(SourceCapabilities.None, source.Capabilities & SourceCapabilities.CanWrite);
        Assert.Equal(FsTestImages.Sha256(FsTestImages.ReadAll(FsTestImages.Reader("TD-FOR-LDM-STRIPE-VOL"))), FsTestImages.Sha256(ReadAll(source)));

        // 組み立てたボリュームはファイルシステムとして読める。
        IFileSystem fs = FileSystemRegistry.Open(new ByteSourceVolumeReader(source));
        Assert.Equal("STRIPE", fs.Summary.Label);
        Assert.Equal(FileSet.Readme, FsTestImages.ReadAll(fs.OpenContent(FsTestImages.Child(fs, fs.Root, "readme.txt"))));
    }

    [Fact]
    [Trait("TC", "TC-FOR-07-02")]
    public void Lvm_linear_volume_spanning_two_pvs_opens()
    {
        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. Discover("TD-FOR-LVM-LINEAR-A"), .. Discover("TD-FOR-LVM-LINEAR-B")]);
        Assert.Empty(scan.Issues);
        LogicalVolumeInfo lv = Assert.Single(scan.Volumes);
        Assert.Equal("vg0", lv.Group);
        Assert.Equal("lv0", lv.Name);
        Assert.Equal(LogicalVolumeKind.Linear, lv.Kind);
        Assert.Equal(2, lv.Pieces.Count);
        LogicalVolumeByteSource source = LogicalVolumeAnalyzer.Open(lv, scan);
        byte[] expected = FileSystemImageSet.LvmLinearVolume();
        Assert.Equal(expected.Length, source.Length);
        Assert.Equal(FsTestImages.Sha256(expected), FsTestImages.Sha256(ReadAll(source)));
        Assert.Equal((Lvm2PhysicalVolumeLabel.Normalize(LogicalVolumeImages.LvmPvB), LogicalVolumeImages.LvmPeStart * 512),
            source.Map(expected.Length / 2));
    }

    [Fact]
    public void Missing_members_are_identified()
    {
        LogicalVolumeScan ldm = LogicalVolumeAnalyzer.Analyze(Discover("TD-FOR-LDM-STRIPE-A"));
        LogicalVolumeInfo stripe = Assert.Single(ldm.Volumes);
        Assert.Equal([LogicalVolumeImages.LdmDiskB.ToString("D")], ldm.MissingMembers(stripe));
        Assert.Throws<InvalidOperationException>(() => LogicalVolumeAnalyzer.Open(stripe, ldm));

        LogicalVolumeScan lvm = LogicalVolumeAnalyzer.Analyze(Discover("TD-FOR-LVM-LINEAR-A"));
        Assert.Equal([LogicalVolumeImages.LvmUuidText(LogicalVolumeImages.LvmPvB)], lvm.MissingMembers(Assert.Single(lvm.Volumes)));

        // 割り当てたファイルで開ける (見つからないディスクをファイルで指定。FOR-07 の仕様 3)。
        IVolumeReader b = FsTestImages.Reader("TD-FOR-LDM-STRIPE-B");
        LogicalVolumeByteSource opened = LogicalVolumeAnalyzer.Open(stripe, ldm,
            new Dictionary<string, IVolumeReader> { [LogicalVolumeImages.LdmDiskB.ToString("D")] = b });
        Assert.Equal(FileSystemImageSet.Image("TD-FOR-LDM-STRIPE-VOL").Length, opened.Length);
    }

    [Fact]
    public void Newest_metadata_wins_and_sequence_mismatch_is_warned()
    {
        SparseImage oldA = LogicalVolumeImages.LdmStripeDisk(0, FileSystemImageSet.LdmStripeVolume(), committedSequence: 4);
        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. Discover(new SparseImageReader(oldA), "A"), .. Discover("TD-FOR-LDM-STRIPE-B")]);
        Assert.Single(scan.Issues);
        Assert.Single(scan.Volumes);

        SparseImage oldLvm = LogicalVolumeImages.LvmLinearPv(0, FileSystemImageSet.LvmLinearVolume(), seqno: 2);
        LogicalVolumeScan lvm = LogicalVolumeAnalyzer.Analyze([.. Discover(new SparseImageReader(oldLvm), "A"), .. Discover("TD-FOR-LVM-LINEAR-B")]);
        Assert.Contains(lvm.Issues, i => i.Group == LogicalVolumeImages.LvmVgId && i.Detail.Contains("3", StringComparison.Ordinal));
    }

    [Fact]
    public void Ldm_simple_spanned_and_mirror_volumes()
    {
        Guid[] disks = [LogicalVolumeImages.LdmDiskA, LogicalVolumeImages.LdmDiskB];
        LogicalVolumeImages.LdmVolumeSpec simple = new("Simple", 2, 0, [(0, 0, 0, 2048, null)]);
        LogicalVolumeImages.LdmVolumeSpec spanned = new("Span", 2, 0, [(0, 4096, 0, 2048, null), (1, 0, 2048, 4096, null)]);
        SparseImage a = LogicalVolumeImages.LdmDisk(disks[0], disks, [simple, spanned]);
        SparseImage b = LogicalVolumeImages.LdmDisk(disks[1], disks, [simple, spanned]);
        a.Write(LogicalVolumeImages.LdmLogicalStart * 512, "SIMPLE"u8);
        a.Write((LogicalVolumeImages.LdmLogicalStart + 4096) * 512, "SPAN-1"u8);
        b.Write(LogicalVolumeImages.LdmLogicalStart * 512, "SPAN-2"u8);
        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. Discover(new SparseImageReader(a), "A"), .. Discover(new SparseImageReader(b), "B")]);
        LogicalVolumeInfo s = scan.Volumes.Single(v => v.Name == "Simple");
        LogicalVolumeInfo sp = scan.Volumes.Single(v => v.Name == "Span");
        Assert.Equal(LogicalVolumeKind.Simple, s.Kind);
        Assert.Equal(LogicalVolumeKind.Spanned, sp.Kind);
        Assert.Equal(6144L * 512, sp.Size);
        LogicalVolumeByteSource span = LogicalVolumeAnalyzer.Open(sp, scan);
        byte[] head = new byte[6];
        span.Read(0, head);
        Assert.Equal("SPAN-1"u8.ToArray(), head);
        span.Read(2048 * 512, head);
        Assert.Equal("SPAN-2"u8.ToArray(), head);
        span.Read(0, head);
        LogicalVolumeByteSource simpleSource = LogicalVolumeAnalyzer.Open(s, scan);
        simpleSource.Read(0, head);
        Assert.Equal("SIMPLE"u8.ToArray(), head);

        // RAID-5 は FOR-26 に渡す設定だけを作る。
        LogicalVolumeImages.LdmVolumeSpec raid = new("Raid", 3, 128, [(0, 0, 0, 2048, 0), (1, 0, 0, 2048, 1)]);
        SparseImage r = LogicalVolumeImages.LdmDisk(disks[0], disks, [raid]);
        LogicalVolumeInfo raid5 = Assert.Single(LogicalVolumeAnalyzer.Analyze(Discover(new SparseImageReader(r), "R")).Volumes);
        Assert.Equal(LogicalVolumeKind.Raid5, raid5.Kind);
        Assert.False(raid5.CanOpenDirectly);
        Assert.Equal(2, raid5.Raid!.Members.Count);
        Assert.Equal(64 * 1024, raid5.Raid.StripeSize);
    }

    [Fact]
    public void Lvm_striped_and_unsupported_volumes()
    {
        string striped = LogicalVolumeImages.LvmMetadata(1, $$"""
            segment1 {
            start_extent = 0
            extent_count = 2
            type = "striped"
            stripe_count = 2
            stripe_size = 128
            stripes = [
            "pv0", 0,
            "pv1", 0
            ]
            }
            """, 1);
        SparseImage a = LogicalVolumeImages.LvmPv(LogicalVolumeImages.LvmPvA, striped);
        SparseImage b = LogicalVolumeImages.LvmPv(LogicalVolumeImages.LvmPvB, striped);
        a.Write(LogicalVolumeImages.LvmPeStart * 512, "STRIPE-A"u8);
        b.Write(LogicalVolumeImages.LvmPeStart * 512, "STRIPE-B"u8);
        LogicalVolumeScan scan = LogicalVolumeAnalyzer.Analyze([.. Discover(new SparseImageReader(a), "A"), .. Discover(new SparseImageReader(b), "B")]);
        LogicalVolumeInfo lv = Assert.Single(scan.Volumes);
        Assert.Equal(LogicalVolumeKind.Striped, lv.Kind);
        LogicalVolumeByteSource source = LogicalVolumeAnalyzer.Open(lv, scan);
        byte[] head = new byte[8];
        source.Read(64 * 1024, head);
        Assert.Equal("STRIPE-B"u8.ToArray(), head);

        string thin = LogicalVolumeImages.LvmMetadata(1, """
            segment1 {
            start_extent = 0
            extent_count = 1
            type = "thin"
            thin_pool = "pool"
            transaction_id = 0
            device_id = 1
            }
            """, 1, "thin");
        LogicalVolumeInfo unsupported = Assert.Single(LogicalVolumeAnalyzer.Analyze(
            Discover(new SparseImageReader(LogicalVolumeImages.LvmPv(LogicalVolumeImages.LvmPvA, thin)), "T")).Volumes);
        Assert.Equal(LogicalVolumeKind.Unsupported, unsupported.Kind);
        Assert.Equal("thin", unsupported.TypeText);
        Assert.False(unsupported.CanOpenDirectly);
    }

    [Fact]
    public void Lvm_text_parser_handles_nesting_comments_and_escapes()
    {
        LvmText.Section root = LvmText.Parse("""
            # comment
            vg { id = "a\"b" n = -5 list = [ "x", 1, "y", 2 ] inner { deep { v = 1 } } }
            """);
        LvmText.Section vg = root.Child("vg")!;
        Assert.Equal("a\"b", vg.String("id"));
        Assert.Equal(-5, vg.Number("n"));
        Assert.Equal(4, vg.List("list").Count);
        Assert.Equal(1, vg.Child("inner")!.Child("deep")!.Number("v"));

        // 深すぎる入れ子は読み飛ばす。
        string deep = string.Concat(Enumerable.Repeat("a { ", 200)) + string.Concat(Enumerable.Repeat("} ", 200));
        _ = LvmText.Parse(deep);
    }
}
