using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// LDM (動的ディスク) と LVM2 のテスト用イメージを直接書く生成ツール (TD-FOR-LDM-STRIPE-*・TD-FOR-LVM-LINEAR-*)。LDM は Linux の ldm.c と同じ
/// 配置 (PRIVHEAD は LBA 6 とデータベースの 6・1856・2047 セクタ目、TOCBLOCK は 1・2、VMDB は 17、VBLK は 128 バイト) で、VBLK の可変長の欄は
/// ldm.c の読み方 (ldm_relative) と同じ位置に置く。LVM2 はラベル (セクタ 1)、PV ヘッダー、メタデータ領域 (4 KiB 目から) とテキストのメタデータ、
/// calc_crc のチェックサムを書く。アプリの解析の実装は使わない。
/// </summary>
public static class LogicalVolumeImages
{
    public const long Sectors64M = 64L * 1024 * 1024 / 512;
    public const long LdmLogicalStart = 63;
    public const long LdmPartitionSectors = 61440;
    public const long LdmStripeSectors = 128;
    public const long LdmConfigStart = Sectors64M - 2048;

    public static readonly Guid LdmDiskA = new("4C444D41-0000-4000-8000-0000000000A1");
    public static readonly Guid LdmDiskB = new("4C444D42-0000-4000-8000-0000000000B2");
    public static readonly Guid LdmGroup = new("4C444D47-0000-4000-8000-000000000060");
    public const string LdmGroupName = "HEXEDITOR-Dg0";

    // ---- LDM ----

    /// <summary>VBLK の可変長の欄を ldm.c と同じ位置に置く書き込み (位置は 0x18 からの相対)。</summary>
    private sealed class VblkData
    {
        public readonly byte[] B = new byte[128];

        public int Vnum(int at, ulong v)
        {
            int length = 1;
            while (length < 8 && v >> (8 * length) != 0)
            {
                length++;
            }

            B[at] = (byte)length;
            for (int i = 0; i < length; i++)
            {
                B[at + 1 + i] = (byte)(v >> (8 * (length - 1 - i)));
            }

            return 1 + length;
        }

        public int Vstr(int at, string s)
        {
            B[at] = (byte)s.Length;
            Ascii(B, at + 1, s);
            return 1 + s.Length;
        }
    }

    private static byte[] Vblk(uint seq, uint group, byte type, byte flags, Func<VblkData, int> fill)
    {
        var d = new VblkData();
        Ascii(d.B, 0, "VBLK");
        B32(d.B, 4, seq);
        B32(d.B, 8, group);
        B16(d.B, 0x0C, 0);
        B16(d.B, 0x0E, 1);
        d.B[0x12] = flags;
        d.B[0x13] = type;
        int size = fill(d);
        B32(d.B, 0x14, size);
        return d.B;
    }

    private static byte[] Disk4(uint seq, ulong id, string name, Guid guid) => Vblk(seq, seq, 0x44, 0, d =>
    {
        int rObj = d.Vnum(0x18, id);
        int rName = rObj + d.Vstr(0x18 + rObj, name);
        guid.ToByteArray(bigEndian: true).CopyTo(d.B, 0x18 + rName);
        return rName + 45;
    });

    private static byte[] Group3(uint seq, ulong id, string name, Guid guid) => Vblk(seq, seq, 0x35, 0, d =>
    {
        int rObj = d.Vnum(0x18, id);
        int rName = rObj + d.Vstr(0x18 + rObj, name);
        int rDisk = rName + d.Vstr(0x18 + rName, guid.ToString("D"));
        return rDisk + 32;
    });

    private static byte[] Volume5(uint seq, ulong id, string name, ulong children, ulong sectors, Guid guid) => Vblk(seq, seq, 0x51, 0, d =>
    {
        int rObj = d.Vnum(0x18, id);
        int rName = rObj + d.Vstr(0x18 + rObj, name);
        int rType = rName + d.Vstr(0x18 + rName, "gen");
        int rDisable = rType + d.Vstr(0x18 + rType, string.Empty);
        Ascii(d.B, 0x18 + rDisable, "ACTIVE");
        int rChild = rDisable + d.Vnum(0x2D + rDisable, children);
        int rSize = rChild + d.Vnum(0x3D + rChild, sectors);
        d.B[0x41 + rSize] = 0x07;
        guid.ToByteArray(bigEndian: true).CopyTo(d.B, 0x42 + rSize);
        return rSize + 58;
    });

    private static byte[] Component3(uint seq, ulong id, string name, byte type, ulong children, ulong parent, ulong stripe, ulong columns) =>
        Vblk(seq, seq, 0x32, (byte)(stripe > 0 ? 0x10 : 0), d =>
        {
            int rObj = d.Vnum(0x18, id);
            int rName = rObj + d.Vstr(0x18 + rObj, name);
            int rState = rName + d.Vstr(0x18 + rName, "ACTIVE");
            d.B[0x18 + rState] = type;
            int rChild = rState + d.Vnum(0x1D + rState, children);
            int rParent = rChild + d.Vnum(0x2D + rChild, parent);
            if (stripe == 0)
            {
                return rParent + 22;
            }

            int rStripe = rParent + d.Vnum(0x2E + rParent, stripe);
            int rCols = rStripe + d.Vnum(0x2E + rStripe, columns);
            return rCols + 22;
        });

    private static byte[] Partition3(uint seq, ulong id, string name, ulong start, ulong volumeOffset, ulong sectors, ulong parent, ulong disk, int? index) =>
        Vblk(seq, seq, 0x33, (byte)(index is null ? 0 : 0x08), d =>
        {
            int rObj = d.Vnum(0x18, id);
            int rName = rObj + d.Vstr(0x18 + rObj, name);
            B64(d.B, 0x24 + rName, (long)start);
            B64(d.B, 0x2C + rName, (long)volumeOffset);
            int rSize = rName + d.Vnum(0x34 + rName, sectors);
            int rParent = rSize + d.Vnum(0x34 + rSize, parent);
            int rDisk = rParent + d.Vnum(0x34 + rParent, disk);
            if (index is int i)
            {
                return rDisk + d.Vnum(0x34 + rDisk, (ulong)i) + 28;
            }

            return rDisk + 28;
        });

    /// <summary>LDM のボリュームの定義 (テストで種類を変える)。</summary>
    public sealed record LdmVolumeSpec(string Name, byte ComponentType, ulong StripeSectors, IReadOnlyList<(int Disk, ulong Start, ulong VolumeOffset, ulong Sectors, int? Index)> Parts);

    private static List<byte[]> LdmRecords(IReadOnlyList<Guid> disks, IReadOnlyList<LdmVolumeSpec> volumes)
    {
        var records = new List<byte[]>();
        uint seq = 4;
        ulong next = 1;
        records.Add(Group3(seq++, next++, LdmGroupName, LdmGroup));
        var diskIds = new List<ulong>();
        for (int i = 0; i < disks.Count; i++)
        {
            diskIds.Add(next);
            records.Add(Disk4(seq++, next++, $"Disk{i + 1}", disks[i]));
        }

        foreach (LdmVolumeSpec v in volumes)
        {
            ulong volumeId = next++, componentId = next++;
            ulong sectors = v.ComponentType == 1 ? v.Parts.Aggregate(0ul, (a, p) => a + p.Sectors) : v.Parts.Max(p => p.VolumeOffset + p.Sectors);
            records.Add(Volume5(seq++, volumeId, v.Name, 1, sectors, new Guid($"4C444D56-0000-4000-8000-{volumeId:X12}")));
            records.Add(Component3(seq++, componentId, v.Name + "-01", v.ComponentType, (ulong)v.Parts.Count, volumeId, v.StripeSectors,
                v.StripeSectors > 0 ? (ulong)v.Parts.Count : 0));
            int n = 1;
            foreach ((int disk, ulong start, ulong offset, ulong count, int? index) in v.Parts)
            {
                records.Add(Partition3(seq++, next++, $"Disk{disk + 1}-{n++:D2}", start, offset, count, componentId, diskIds[disk], index));
            }
        }

        return records;
    }

    /// <summary>LDM の動的ディスク (MBR) 1 台。ボリュームの中身は <paramref name="write"/> で書く。</summary>
    public static SparseImage LdmDisk(Guid diskGuid, IReadOnlyList<Guid> allDisks, IReadOnlyList<LdmVolumeSpec> volumes, ulong committedSequence = 5)
    {
        var image = new SparseImage(Sectors64M * 512);
        PartitionImages.WriteMbr(image, 0x4C444D00 | (uint)allDisks.ToList().IndexOf(diskGuid), PartitionImages.MbrEntry(0x42, LdmLogicalStart, Sectors64M - LdmLogicalStart));

        byte[] ph = new byte[512];
        Ascii(ph, 0, "PRIVHEAD");
        B32(ph, 8, 0x2FB0);
        B16(ph, 0x0C, 2);
        B16(ph, 0x0E, 12);
        B64(ph, 0x10, 0x01DC0000_00000000);
        Ascii(ph, 0x30, diskGuid.ToString("D"));
        Ascii(ph, 0x70, "4C444D48-0000-4000-8000-000000000001");
        Ascii(ph, 0xB0, LdmGroup.ToString("D"));
        Ascii(ph, 0xF0, LdmGroupName);
        B64(ph, 0x11B, LdmLogicalStart);
        B64(ph, 0x123, LdmConfigStart - LdmLogicalStart);
        B64(ph, 0x12B, LdmConfigStart);
        B64(ph, 0x133, 2048);
        B64(ph, 0x13B, 2);
        B64(ph, 0x143, 2);
        B32(ph, 0x14B, 1);
        B32(ph, 0x14F, 1);
        B64(ph, 0x153, 1740);
        B64(ph, 0x15B, 96);
        image.Write(6 * 512, ph);
        foreach (long off in new long[] { 6, 1856, 2047 })
        {
            image.Write((LdmConfigStart + off) * 512, ph);
        }

        byte[] toc = new byte[512];
        Ascii(toc, 0, "TOCBLOCK");
        Ascii(toc, 0x24, "config");
        B64(toc, 0x2E, 17);
        B64(toc, 0x36, 1740);
        Ascii(toc, 0x46, "log");
        B64(toc, 0x50, 1757);
        B64(toc, 0x58, 96);
        image.Write((LdmConfigStart + 1) * 512, toc);
        image.Write((LdmConfigStart + 2) * 512, toc);

        List<byte[]> records = LdmRecords(allDisks, volumes);
        byte[] vmdb = new byte[512];
        Ascii(vmdb, 0, "VMDB");
        B32(vmdb, 4, 4 + records.Count);
        B32(vmdb, 8, 128);
        B32(vmdb, 0x0C, 512);
        B16(vmdb, 0x10, 1);
        B16(vmdb, 0x12, 4);
        B16(vmdb, 0x14, 10);
        Ascii(vmdb, 0x16, LdmGroupName);
        Ascii(vmdb, 0x35, LdmGroup.ToString("D"));
        B64(vmdb, 0x75, (long)committedSequence);
        B64(vmdb, 0x7D, (long)committedSequence);
        long vmdbAt = (LdmConfigStart + 17) * 512;
        image.Write(vmdbAt, vmdb);
        for (int i = 0; i < records.Count; i++)
        {
            image.Write(vmdbAt + (4 + i) * 128L, records[i]);
        }

        return image;
    }

    /// <summary>TD-FOR-LDM-STRIPE-VOL: 2 台のストライプのボリューム (NTFS、ラベル STRIPE、readme.txt)。</summary>
    public static SparseImage LdmStripeVolume()
    {
        var b = new NtfsImageBuilder(2 * LdmPartitionSectors * 512, "STRIPE");
        b.AddFile(b.Root, "readme.txt", FileSet.Readme, FileSet.ReadmeModifiedUtc);
        var image = new SparseImage(2 * LdmPartitionSectors * 512);
        b.Build(new SparseImageTarget(image));
        return image;
    }

    public static readonly LdmVolumeSpec StripeSpec = new("Volume1", 1, LdmStripeSectors,
        [(0, 0, 0, LdmPartitionSectors, 0), (1, 0, 0, LdmPartitionSectors, 1)]);

    /// <summary>TD-FOR-LDM-STRIPE-A / B (<paramref name="disk"/> は 0 か 1)。ボリュームの中身を 64 KiB ずつ交互に置く。</summary>
    public static SparseImage LdmStripeDisk(int disk, SparseImage volume, ulong committedSequence = 5)
    {
        Guid[] disks = [LdmDiskA, LdmDiskB];
        SparseImage image = LdmDisk(disks[disk], disks, [StripeSpec], committedSequence);
        long stripe = LdmStripeSectors * 512;
        long data = LdmLogicalStart * 512;
        for (long chunk = 0; chunk * stripe < volume.Length; chunk++)
        {
            if (chunk % 2 != disk)
            {
                continue;
            }

            byte[] part = volume.Read(chunk * stripe, (int)stripe);
            if (part.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                image.Write(data + chunk / 2 * stripe, part);
            }
        }

        return image;
    }

    // ---- LVM2 ----

    public const string LvmPvA = "Abcdef0123456789Abcdef0123456789";
    public const string LvmPvB = "Bcdefg1234567890Bcdefg1234567890";
    public const string LvmVgId = "Vgvgvg-0000-1111-2222-3333-4444-555555";
    public const long LvmExtentSectors = 8192;
    public const long LvmPeStart = 2048;
    public const long LvmPvSectors = 32L * 1024 * 1024 / 512;
    public const long LvmExtentsPerPv = (LvmPvSectors - LvmPeStart) / LvmExtentSectors;

    /// <summary>LVM2 の calc_crc。</summary>
    public static uint LvmCrc(ReadOnlySpan<byte> data) => Crc32Raw(0xF597A6CF, data);

    /// <summary>LVM2 の PV 1 つ (ラベル・PV ヘッダー・メタデータ領域)。<paramref name="metadata"/> はテキストのメタデータ。</summary>
    public static SparseImage LvmPv(string uuid, string metadata, long sectors = LvmPvSectors)
    {
        var image = new SparseImage(sectors * 512);
        byte[] label = new byte[512];
        Ascii(label, 0, "LABELONE");
        U64(label, 8, 1);
        U32(label, 20, 32);
        Ascii(label, 24, "LVM2 001");
        Ascii(label, 32, uuid);
        U64(label, 64, sectors * 512);
        U64(label, 72, LvmPeStart * 512);
        U64(label, 80, 0);
        U64(label, 104, 4096);
        U64(label, 112, LvmPeStart * 512 - 4096);
        U32(label, 16, LvmCrc(label.AsSpan(20)));
        image.Write(512, label);

        byte[] text = [.. Encoding.ASCII.GetBytes(metadata), 0];
        byte[] mda = new byte[512];
        Ascii(mda, 4, " LVM2 x[5A%r0N*>");
        U32(mda, 20, 1);
        U64(mda, 24, 4096);
        U64(mda, 32, LvmPeStart * 512 - 4096);
        U64(mda, 40, 512);
        U64(mda, 48, text.Length - 1);
        U32(mda, 56, LvmCrc(text.AsSpan(0, text.Length - 1)));
        U32(mda, 0, LvmCrc(mda.AsSpan(4)));
        image.Write(4096, mda);
        image.Write(4096 + 512, text);
        return image;
    }

    /// <summary>LVM2 の PV の UUID のテキストの書式 (6-4-4-4-4-4-6)。</summary>
    public static string LvmUuidText(string uuid) => string.Join('-', uuid[..6], uuid[6..10], uuid[10..14], uuid[14..18], uuid[18..22], uuid[22..26], uuid[26..]);

    /// <summary>VG vg0 のメタデータ (<paramref name="segments"/> は LV lv0 のセグメントのテキスト)。</summary>
    public static string LvmMetadata(long seqno, string segments, int segmentCount, string type = "striped") => $$"""
        vg0 {
        id = "{{LvmVgId}}"
        seqno = {{seqno}}
        format = "lvm2"
        status = ["RESIZEABLE", "READ", "WRITE"]
        flags = []
        extent_size = {{LvmExtentSectors}}
        max_lv = 0
        max_pv = 0
        metadata_copies = 0

        physical_volumes {

        pv0 {
        id = "{{LvmUuidText(LvmPvA)}}"
        device = "/dev/loop0"

        status = ["ALLOCATABLE"]
        flags = []
        dev_size = {{LvmPvSectors}}
        pe_start = {{LvmPeStart}}
        pe_count = {{LvmExtentsPerPv}}
        }

        pv1 {
        id = "{{LvmUuidText(LvmPvB)}}"
        device = "/dev/loop1"

        status = ["ALLOCATABLE"]
        flags = []
        dev_size = {{LvmPvSectors}}
        pe_start = {{LvmPeStart}}
        pe_count = {{LvmExtentsPerPv}}
        }
        }

        logical_volumes {

        lv0 {
        id = "Lvlvlv-0000-1111-2222-3333-4444-555555"
        status = ["READ", "WRITE", "VISIBLE"]
        flags = []
        creation_time = 1767225600 # 2026-01-01 00:00:00 +0000
        creation_host = "hexeditor"
        segment_count = {{segmentCount}}
        {{segments}}
        }
        }

        }
        # Generated by the HexEditor test data generator ({{type}})

        contents = "Text Format Volume Group"
        version = 1

        description = ""

        creation_host = "hexeditor"
        creation_time = 1767225600 # 2026-01-01 00:00:00 +0000
        """;

    /// <summary>2 つの PV にまたがる linear の LV (セグメント 2 つ)。</summary>
    public static string LvmLinearMetadata(long seqno = 3) => LvmMetadata(seqno, $$"""
        segment1 {
        start_extent = 0
        extent_count = {{LvmExtentsPerPv}}

        type = "striped"
        stripe_count = 1 # linear

        stripes = [
        "pv0", 0
        ]
        }
        segment2 {
        start_extent = {{LvmExtentsPerPv}}
        extent_count = {{LvmExtentsPerPv}}

        type = "striped"
        stripe_count = 1 # linear

        stripes = [
        "pv1", 0
        ]
        }
        """, 2);

    /// <summary>TD-FOR-LVM-LINEAR-LV の内容 (種 0x1F の乱数、PV 2 つ分のエクステント)。</summary>
    public static byte[] LvmLinearVolume()
    {
        byte[] b = new byte[2 * LvmExtentsPerPv * LvmExtentSectors * 512];
        TestDataCatalog.Random(0x1F, 0, b);
        return b;
    }

    /// <summary>TD-FOR-LVM-LINEAR-A / B。</summary>
    public static SparseImage LvmLinearPv(int pv, byte[] volume, long seqno = 3)
    {
        SparseImage image = LvmPv(pv == 0 ? LvmPvA : LvmPvB, LvmLinearMetadata(seqno));
        long length = LvmExtentsPerPv * LvmExtentSectors * 512;
        image.Write(LvmPeStart * 512, volume.AsSpan((int)(pv * length), (int)length));
        return image;
    }
}
