using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>パーティション表のテスト用イメージ (TD-FOR-MBR-*、TD-FOR-GPT*、TD-FOR-APM、TD-FOR-BSD)。定義は docs/test/cases/12-forensics.md。</summary>
public static class PartitionImages
{
    public const long MiB = 1024 * 1024;

    /// <summary>MBR のエントリ 1 つ (16 バイト)。</summary>
    public static byte[] MbrEntry(byte type, long start, long count, bool boot = false)
    {
        byte[] e = new byte[16];
        e[0] = boot ? (byte)0x80 : (byte)0;
        e[1] = 0xFE; e[2] = 0xFF; e[3] = 0xFF;
        e[4] = type;
        e[5] = 0xFE; e[6] = 0xFF; e[7] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)start);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)count);
        return e;
    }

    /// <summary>MBR (LBA 0) を書く。</summary>
    public static void WriteMbr(SparseImage image, uint signature, params byte[][] entries)
    {
        byte[] s = new byte[512];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x1B8), signature);
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].CopyTo(s, 0x1BE + i * 16);
        }

        s[510] = 0x55;
        s[511] = 0xAA;
        image.Write(0, s);
    }

    /// <summary>TD-FOR-MBR-EXT の値 (基本 3 つ + 拡張、論理 5 つ)。</summary>
    public static readonly (byte Type, long Start, long Count)[] MbrExtPrimary =
        [(0x07, 2048, 32768), (0x0B, 34816, 32768), (0x83, 67584, 32768), (0x0F, 100352, 409600)];

    public static readonly long[] MbrExtLogicalStarts = [102400, 167936, 233472, 299008, 364544];

    /// <summary>TD-FOR-MBR-EXT。<paramref name="loop"/> なら k = 2 の EBR の次の EBR を 1 つ目の EBR にする (TD-FOR-MBR-EBRLOOP)。</summary>
    public static SparseImage MbrExtended(bool loop = false)
    {
        var image = new SparseImage(256 * MiB);
        WriteMbr(image, 0x12345678, [.. MbrExtPrimary.Select(p => MbrEntry(p.Type, p.Start, p.Count))]);
        const long ExtStart = 100352;
        for (int k = 0; k < 5; k++)
        {
            long ebr = ExtStart + k * 65536L;
            byte[] s = new byte[512];
            MbrEntry(0x07, 2048, 32768).CopyTo(s, 0x1BE);
            if (k < 4)
            {
                long next = loop && k == 2 ? 0 : (k + 1) * 65536L;
                MbrEntry(0x05, next, 65536).CopyTo(s, 0x1BE + 16);
            }

            s[510] = 0x55;
            s[511] = 0xAA;
            image.Write(ebr * 512, s);
        }

        return image;
    }

    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid LinuxFs = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    public static Guid Numbered(int n) => new($"00000000-0000-0000-0000-{n:X12}");

    /// <summary>GPT のパーティション 1 つ。</summary>
    public sealed record GptPart(Guid Type, Guid Unique, long First, long Last, string Name, ulong Attributes = 0);

    /// <summary>保護 MBR と主・バックアップの GPT を書く (エントリ 128 個 × 128 バイト)。</summary>
    public static void WriteGpt(SparseImage image, int sectorSize, Guid disk, IReadOnlyList<GptPart> parts)
    {
        long sectors = image.Length / sectorSize;
        byte[] mbr = new byte[512];
        MbrEntry(0xEE, 1, Math.Min(sectors - 1, uint.MaxValue)).CopyTo(mbr, 0x1BE);
        mbr[0x1BE + 1] = 0; mbr[0x1BE + 2] = 2; mbr[0x1BE + 3] = 0;
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        image.Write(0, mbr);

        byte[] entries = new byte[128 * 128];
        for (int i = 0; i < parts.Count; i++)
        {
            Span<byte> e = entries.AsSpan(i * 128, 128);
            parts[i].Type.TryWriteBytes(e);
            parts[i].Unique.TryWriteBytes(e[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], parts[i].First);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], parts[i].Last);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], parts[i].Attributes);
            Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e[56..]);
        }

        int entrySectors = entries.Length / sectorSize;
        long backupLba = sectors - 1;
        long backupEntries = backupLba - entrySectors;
        long firstUsable = 2 + entrySectors, lastUsable = backupEntries - 1;
        uint crc = Crc32.HashToUInt32(entries);
        image.Write(2L * sectorSize, entries);
        image.Write(backupEntries * sectorSize, entries);
        image.Write(sectorSize, Header(sectorSize, 1, backupLba, firstUsable, lastUsable, disk, 2, crc));
        image.Write(backupLba * sectorSize, Header(sectorSize, backupLba, 1, firstUsable, lastUsable, disk, backupEntries, crc));
    }

    private static byte[] Header(int sectorSize, long current, long backup, long first, long last, Guid disk, long entriesLba, uint crc)
    {
        byte[] h = new byte[sectorSize];
        "EFI PART"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), current);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), first);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), last);
        disk.TryWriteBytes(h.AsSpan(56));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.HashToUInt32(h.AsSpan(0, 92)));
        return h;
    }

    /// <summary>TD-FOR-GPT のパーティション。</summary>
    public static readonly GptPart[] GptParts =
    [
        new(EfiSystem, Numbered(1), 2048, 4095, "EFI"),
        new(BasicData, Numbered(2), 4096, 36863, "Data"),
        new(LinuxFs, Numbered(3), 36864, 69631, "Linux"),
    ];

    public static readonly Guid GptDisk = new("00000000-0000-0000-0000-0000000000D0");

    /// <summary>TD-FOR-GPT と、その変形 (<c>BADCRC</c>: エントリ配列を 1 バイト変える、<c>BADBACKUP</c>: バックアップヘッダーの先頭を X にする)。</summary>
    public static SparseImage Gpt(string variant = "")
    {
        var image = new SparseImage(64 * MiB);
        WriteGpt(image, 512, GptDisk, GptParts);
        if (variant == "BADCRC")
        {
            image.Xor(2 * 512 + 0x38, 0x01);
        }
        else if (variant == "BADBACKUP")
        {
            image.Write(131071L * 512, "X"u8);
        }

        return image;
    }

    public static readonly GptPart[] Gpt4KParts =
    [
        new(BasicData, Numbered(1), 256, 4351, string.Empty),
        new(LinuxFs, Numbered(2), 4352, 8447, string.Empty),
    ];

    /// <summary>TD-FOR-GPT-4K (セクタ 4,096 バイト)。</summary>
    public static SparseImage Gpt4K()
    {
        var image = new SparseImage(64 * MiB);
        WriteGpt(image, 4096, GptDisk, Gpt4KParts);
        return image;
    }

    /// <summary>TD-FOR-APM の PM エントリ (名前、種類、開始、ブロック数)。</summary>
    public static readonly (string Name, string Type, long Start, long Count)[] ApmEntries =
    [
        ("Apple", "Apple_partition_map", 1, 63),
        ("Data1", "Apple_HFS", 64, 32768),
        ("Extra", "Apple_Free", 32832, 32704),
    ];

    public static SparseImage Apm()
    {
        var image = new SparseImage(32 * MiB);
        byte[] er = new byte[512];
        er[0] = (byte)'E';
        er[1] = (byte)'R';
        BinaryPrimitives.WriteUInt16BigEndian(er.AsSpan(2), 512);
        BinaryPrimitives.WriteUInt32BigEndian(er.AsSpan(4), 65536);
        image.Write(0, er);
        for (int i = 0; i < ApmEntries.Length; i++)
        {
            byte[] pm = new byte[512];
            pm[0] = (byte)'P';
            pm[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32BigEndian(pm.AsSpan(4), (uint)ApmEntries.Length);
            BinaryPrimitives.WriteUInt32BigEndian(pm.AsSpan(8), (uint)ApmEntries[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(pm.AsSpan(12), (uint)ApmEntries[i].Count);
            Encoding.ASCII.GetBytes(ApmEntries[i].Name).CopyTo(pm, 16);
            Encoding.ASCII.GetBytes(ApmEntries[i].Type).CopyTo(pm, 48);
            BinaryPrimitives.WriteUInt32BigEndian(pm.AsSpan(88), 0x37);
            image.Write((i + 1) * 512L, pm);
        }

        return image;
    }

    /// <summary>TD-FOR-BSD のパーティション (欄の番号、オフセット、セクタ数、fstype)。</summary>
    public static readonly (int Slot, long Offset, long Size, byte FsType)[] BsdPartitions =
    [
        (0, 16, 16384, 7), (1, 16400, 8192, 1), (3, 24592, 16384, 7), (4, 40976, 24560, 7),
    ];

    public static SparseImage Bsd()
    {
        var image = new SparseImage(32 * MiB);
        byte[] l = new byte[512];
        const uint Magic = 0x82564557;
        BinaryPrimitives.WriteUInt32LittleEndian(l, Magic);
        Encoding.ASCII.GetBytes("TESTDISK").CopyTo(l, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(40), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(44), 63);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(48), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(52), 65);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(56), 1008);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(60), 65536);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(132), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(l.AsSpan(138), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(140), 8192);
        BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(144), 65536);
        foreach ((int slot, long offset, long size, byte type) in BsdPartitions)
        {
            int e = 148 + slot * 16;
            BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(e), (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(l.AsSpan(e + 4), (uint)offset);
            l[e + 12] = type;
        }

        // チェックサム: 16 ビットごとの XOR (パーティションの欄まで)。
        ushort sum = 0;
        for (int i = 0; i < 148 + 8 * 16; i += 2)
        {
            sum ^= BinaryPrimitives.ReadUInt16LittleEndian(l.AsSpan(i));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(l.AsSpan(136), sum);
        image.Write(512, l);
        return image;
    }
}
