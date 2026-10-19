using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.FileSystems.Partitions;

/// <summary>パーティション表に書く 1 件 (開始 LBA、セクタ数、種類)。</summary>
public sealed record PartitionTableEntry(long StartLba, long SectorCount, FileSystemKind Kind, string Name = "");

/// <summary>ドキュメントに加える上書き 1 件。</summary>
public sealed record PartitionTableWrite(long Offset, byte[] Data);

/// <summary>作れなかった理由。</summary>
public enum PartitionTableError
{
    None,

    /// <summary>MBR の空きのエントリが足りない。</summary>
    NoFreeSlot,

    /// <summary>MBR で表せない (開始・セクタ数が 32 ビットを超える)。</summary>
    TooLargeForMbr,

    /// <summary>GPT の空きのエントリが足りない。</summary>
    NoFreeGptEntry,

    /// <summary>ディスクが小さすぎて GPT を置けない。</summary>
    DiskTooSmall,
}

/// <summary>
/// 「パーティション表のエントリを作成」(FOR-08 の仕様 4) の書き込む内容を作る。ここでは書き込まず、呼び出し側がデータエンジン上の編集として
/// ドキュメントに加える (保存するまでディスクには書かない)。
/// </summary>
public static class PartitionTableWriter
{
    /// <summary>MBR の種類 ID。</summary>
    public static byte MbrType(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs or FileSystemKind.ExFat or FileSystemKind.BitLocker => 0x07,
        FileSystemKind.Fat32 => 0x0C,
        FileSystemKind.Fat16 => 0x0E,
        FileSystemKind.Fat12 => 0x01,
        FileSystemKind.Ext => 0x83,
        FileSystemKind.HfsPlus or FileSystemKind.Hfsx => 0xAF,
        FileSystemKind.Lvm2 => 0x8E,
        FileSystemKind.Ldm => 0x42,
        _ => 0x83,
    };

    /// <summary>GPT の種類 GUID。</summary>
    public static Guid GptType(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs or FileSystemKind.ExFat or FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32
            or FileSystemKind.BitLocker => PartitionTypes.WindowsBasicData,
        FileSystemKind.Ext => PartitionTypes.LinuxFileSystem,
        FileSystemKind.HfsPlus or FileSystemKind.Hfsx => PartitionTypes.AppleHfsPlus,
        FileSystemKind.Apfs => PartitionTypes.AppleApfs,
        FileSystemKind.Lvm2 => PartitionTypes.LinuxLvm,
        FileSystemKind.Ldm => PartitionTypes.LdmData,
        _ => PartitionTypes.LinuxFileSystem,
    };

    /// <summary>MBR の空きのエントリに加える (末尾の 55 AA も書く)。</summary>
    public static (IReadOnlyList<PartitionTableWrite> Writes, PartitionTableError Error) CreateMbr(IVolumeReader disk, int sectorSize,
        IReadOnlyList<PartitionTableEntry> entries)
    {
        byte[] lba0 = disk.ReadBytes(0, 512);
        var free = Enumerable.Range(0, 4).Where(i => lba0[0x1BE + i * 16 + 4] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(lba0.AsSpan(0x1BE + i * 16 + 12)) == 0)
            .ToList();
        if (free.Count < entries.Count)
        {
            return ([], PartitionTableError.NoFreeSlot);
        }

        var writes = new List<PartitionTableWrite>();
        foreach ((PartitionTableEntry entry, int slot) in entries.OrderBy(e => e.StartLba).Zip(free))
        {
            if (entry.StartLba > uint.MaxValue || entry.SectorCount > uint.MaxValue || entry.SectorCount <= 0)
            {
                return ([], PartitionTableError.TooLargeForMbr);
            }

            byte[] e = new byte[16];
            e[0] = 0x00;
            // CHS は LBA で表せない値の印 (FE FF FF) にする。
            e[1] = 0xFE; e[2] = 0xFF; e[3] = 0xFF;
            e[4] = MbrType(entry.Kind);
            e[5] = 0xFE; e[6] = 0xFF; e[7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)entry.StartLba);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)entry.SectorCount);
            writes.Add(new PartitionTableWrite(0x1BE + slot * 16, e));
        }

        writes.Add(new PartitionTableWrite(510, [0x55, 0xAA]));
        return (writes, PartitionTableError.None);
    }

    /// <summary>
    /// GPT のエントリを加える。GPT があれば空きのエントリに入れて、主とバックアップのエントリ配列・ヘッダーの CRC32 を書き直す。GPT がなければ
    /// 保護 MBR と主・バックアップの GPT (エントリ 128 個) を作る。
    /// </summary>
    public static (IReadOnlyList<PartitionTableWrite> Writes, PartitionTableError Error) CreateGpt(IVolumeReader disk, int sectorSize,
        IReadOnlyList<PartitionTableEntry> entries, Func<Guid>? newGuid = null)
    {
        newGuid ??= Guid.NewGuid;
        long sectors = disk.Length / sectorSize;
        const int EntrySize = 128;
        const int EntryCount = 128;
        int entrySectors = EntryCount * EntrySize / sectorSize;
        if (sectors < 2 + 2 * entrySectors + 2)
        {
            return ([], PartitionTableError.DiskTooSmall);
        }

        byte[] header = disk.ReadBytes(sectorSize, sectorSize);
        bool exists = header.AsSpan(0, 8).SequenceEqual("EFI PART"u8);
        var writes = new List<PartitionTableWrite>();
        byte[] array;
        long entriesLba, backupLba, firstUsable, lastUsable;
        Guid diskGuid;
        int count, size;
        if (exists)
        {
            entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
            count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)), 4096);
            size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
            backupLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
            firstUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
            lastUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48));
            diskGuid = new Guid(header.AsSpan(56, 16));
            if (size is < 128 or > 4096 || count <= 0)
            {
                return ([], PartitionTableError.NoFreeGptEntry);
            }

            array = disk.ReadBytes(entriesLba * sectorSize, count * size);
        }
        else
        {
            entriesLba = 2;
            count = EntryCount;
            size = EntrySize;
            backupLba = sectors - 1;
            firstUsable = 2 + entrySectors;
            lastUsable = sectors - 2 - entrySectors;
            diskGuid = newGuid();
            array = new byte[count * size];

            // 保護 MBR。
            byte[] mbr = disk.ReadBytes(0, 512);
            mbr.AsSpan(0x1BE, 64).Clear();
            mbr[0x1BE + 1] = 0x00; mbr[0x1BE + 2] = 0x02; mbr[0x1BE + 3] = 0x00;
            mbr[0x1BE + 4] = 0xEE;
            mbr[0x1BE + 5] = 0xFF; mbr[0x1BE + 6] = 0xFF; mbr[0x1BE + 7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(0x1BE + 12), (uint)Math.Min(sectors - 1, uint.MaxValue));
            mbr[510] = 0x55;
            mbr[511] = 0xAA;
            writes.Add(new PartitionTableWrite(0x1BE, mbr.AsSpan(0x1BE, 66).ToArray()));
        }

        var free = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (array.AsSpan(i * size, 16).IndexOfAnyExcept((byte)0) < 0)
            {
                free.Add(i);
            }
        }

        if (free.Count < entries.Count)
        {
            return ([], PartitionTableError.NoFreeGptEntry);
        }

        foreach ((PartitionTableEntry entry, int slot) in entries.OrderBy(e => e.StartLba).Zip(free))
        {
            Span<byte> e = array.AsSpan(slot * size, size);
            e.Clear();
            GptType(entry.Kind).TryWriteBytes(e);
            newGuid().TryWriteBytes(e[16..]);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], entry.StartLba);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], entry.StartLba + entry.SectorCount - 1);
            byte[] name = Encoding.Unicode.GetBytes(entry.Name.Length > 36 ? entry.Name[..36] : entry.Name);
            name.CopyTo(e[56..]);
        }

        uint arrayCrc = Crc32Ieee.Compute(array);
        long backupEntriesLba = backupLba - (count * size + sectorSize - 1) / sectorSize;
        writes.Add(new PartitionTableWrite(entriesLba * sectorSize, array));
        writes.Add(new PartitionTableWrite(backupEntriesLba * sectorSize, array));
        writes.Add(new PartitionTableWrite(sectorSize, Header(1, backupLba, firstUsable, lastUsable, diskGuid, entriesLba, count, size, arrayCrc, sectorSize)));
        writes.Add(new PartitionTableWrite(backupLba * sectorSize,
            Header(backupLba, 1, firstUsable, lastUsable, diskGuid, backupEntriesLba, count, size, arrayCrc, sectorSize)));
        return (writes, PartitionTableError.None);
    }

    private static byte[] Header(long current, long backup, long firstUsable, long lastUsable, Guid disk, long entriesLba, int count, int size,
        uint arrayCrc, int sectorSize)
    {
        byte[] h = new byte[sectorSize];
        "EFI PART"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), current);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), firstUsable);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), lastUsable);
        disk.TryWriteBytes(h.AsSpan(56));
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32Ieee.Compute(h.AsSpan(0, 92)));
        return h;
    }
}
