using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.FileSystems;

/// <summary>ファイルシステム・ボリュームの種類 (FOR-08 の仕様 2 の表、FOR-09 の仕様 1)。</summary>
public enum FileSystemKind
{
    Unknown,
    Ntfs,
    Fat12,
    Fat16,
    Fat32,
    ExFat,
    Ext,
    HfsPlus,
    Hfsx,
    Apfs,
    Iso9660,
    Udf,
    Lvm2,
    Ldm,
    BitLocker,
}

/// <summary>署名の判定の結果。<see cref="EstimatedLength"/> は BPB・スーパーブロックから求めた大きさ (分からなければ null)。</summary>
public sealed record FileSystemSignature(FileSystemKind Kind, long? EstimatedLength, string? Label = null, int? SectorSize = null)
{
    /// <summary>予備 (バックアップのブートセクタなど) も一致したか (FOR-08 の確度「高」)。予備を持たない形式は null。</summary>
    public bool? BackupMatches { get; init; }
}

/// <summary>
/// 先頭のセクタの署名からファイルシステムの種類を判定する (FOR-06 の「推定ファイルシステム」、FOR-08 の表、FOR-09 の仕様 1 で共通)。
/// 読むのは先頭の 64 KiB 程度だけで、ボリュームの大きさに依存しない。
/// </summary>
public static class FileSystemDetector
{
    /// <summary>判定に読む範囲 (ISO 9660 / UDF の 32 KiB 以降の記述子を含む)。</summary>
    public const int ProbeLength = 40 * 1024;

    public static string DisplayName(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.Fat12 => "FAT12",
        FileSystemKind.Fat16 => "FAT16",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Ext => "ext2/3/4",
        FileSystemKind.HfsPlus => "HFS+",
        FileSystemKind.Hfsx => "HFSX",
        FileSystemKind.Apfs => "APFS",
        FileSystemKind.Iso9660 => "ISO 9660",
        FileSystemKind.Udf => "UDF",
        FileSystemKind.Lvm2 => "LVM2",
        FileSystemKind.Ldm => "LDM",
        FileSystemKind.BitLocker => "BitLocker",
        _ => string.Empty,
    };

    /// <summary><paramref name="offset"/> から始まるファイルシステムを判定する。判定できなければ null。</summary>
    public static FileSystemSignature? Detect(IVolumeReader reader, long offset = 0, bool checkBackup = true)
    {
        byte[] head = reader.ReadBytes(offset, ProbeLength);
        FileSystemSignature? sig = DetectHead(head, reader.Length - offset);
        if (sig is null || !checkBackup)
        {
            return sig;
        }

        return sig.Kind switch
        {
            FileSystemKind.Ntfs => sig with { BackupMatches = NtfsBackupMatches(reader, offset, head) },
            FileSystemKind.Fat32 => sig with { BackupMatches = BackupSectorMatches(reader, offset, head, 6 * BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(11))) },
            FileSystemKind.ExFat => sig with { BackupMatches = ExFatBackupMatches(reader, offset, head) },
            _ => sig,
        };
    }

    /// <summary>先頭のバイト列だけで判定する (<paramref name="available"/> はその位置から末尾までの長さ)。</summary>
    public static FileSystemSignature? DetectHead(ReadOnlySpan<byte> head, long available)
    {
        if (head.Length >= 512)
        {
            ReadOnlySpan<byte> oem = head.Slice(3, 8);
            if (oem.SequenceEqual("NTFS    "u8) && TryNtfs(head) is { } ntfs)
            {
                return ntfs;
            }

            if (oem.SequenceEqual("EXFAT   "u8) && TryExFat(head) is { } exfat)
            {
                return exfat;
            }

            if (oem.SequenceEqual("-FVE-FS-"u8))
            {
                return new FileSystemSignature(FileSystemKind.BitLocker, null);
            }

            if (TryFat(head) is { } fat)
            {
                return fat;
            }

            if (head.Slice(0, 8).SequenceEqual("PRIVHEAD"u8))
            {
                return new FileSystemSignature(FileSystemKind.Ldm, null);
            }
        }

        // LVM2: 先頭 4 セクタのどれかに LABELONE。
        for (int s = 0; s < 4 && (s + 1) * 512 <= head.Length; s++)
        {
            if (head.Slice(s * 512, 8).SequenceEqual("LABELONE"u8))
            {
                return new FileSystemSignature(FileSystemKind.Lvm2, null);
            }
        }

        if (head.Length >= 1024 + 512)
        {
            // ext: 1080 バイト目 (スーパーブロックの 0x38) にマジック 0xEF53。
            if (BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(1080)) == 0xEF53)
            {
                uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(1024 + 4));
                uint logBlock = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(1024 + 24));
                uint inodes = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(1024));
                if (logBlock <= 6 && blocks > 0 && inodes > 0)
                {
                    string label = Encoding.UTF8.GetString(head.Slice(1024 + 120, 16)).TrimEnd('\0');
                    return new FileSystemSignature(FileSystemKind.Ext, (long)blocks << (int)(10 + logBlock), label);
                }
            }

            if (head.Slice(1024, 2).SequenceEqual("H+"u8) || head.Slice(1024, 2).SequenceEqual("HX"u8))
            {
                uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(1024 + 40));
                uint total = BinaryPrimitives.ReadUInt32BigEndian(head.Slice(1024 + 44));
                long? size = blockSize is >= 512 and <= 1 << 20 ? (long)blockSize * total : null;
                return new FileSystemSignature(head[1025] == (byte)'+' ? FileSystemKind.HfsPlus : FileSystemKind.Hfsx, size);
            }
        }

        if (head.Length >= 64 && head.Slice(32, 4).SequenceEqual("NXSB"u8))
        {
            uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(36));
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(head.Slice(40));
            long? size = blockSize is >= 4096 and <= 65536 && count < (ulong)(long.MaxValue / blockSize) ? (long)blockSize * (long)count : null;
            return new FileSystemSignature(FileSystemKind.Apfs, size);
        }

        // ISO 9660 / UDF: 32768 バイト目からのボリューム記述子 (2048 バイトごと)。
        bool iso = false, udf = false;
        for (int at = 32768; at + 6 <= head.Length; at += 2048)
        {
            ReadOnlySpan<byte> id = head.Slice(at + 1, 5);
            if (id.SequenceEqual("CD001"u8))
            {
                iso = true;
            }
            else if (id.SequenceEqual("BEA01"u8) || id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
            {
                udf |= !id.SequenceEqual("BEA01"u8);
            }
        }

        if (udf)
        {
            return new FileSystemSignature(FileSystemKind.Udf, null);
        }

        if (iso)
        {
            // 基本ボリューム記述子 (32768) の論理ブロック数 × ブロックサイズ。
            long? size = null;
            if (head.Length >= 32768 + 132 && head[32768] == 1)
            {
                uint blocks = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(32768 + 80));
                ushort blockSize = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(32768 + 128));
                size = (long)blocks * blockSize;
            }

            return new FileSystemSignature(FileSystemKind.Iso9660, size);
        }

        return null;
    }

    private static bool IsPowerOfTwo(long v) => v > 0 && (v & (v - 1)) == 0;

    private static FileSystemSignature? TryNtfs(ReadOnlySpan<byte> b)
    {
        ushort bps = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(11));
        int spc = NtfsSectorsPerCluster(b[13]);
        long total = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(0x28));
        if (bps is < 256 or > 4096 || !IsPowerOfTwo(bps) || spc <= 0 || !IsPowerOfTwo(spc) || total <= 0)
        {
            return null;
        }

        // ボリュームの大きさは総セクタ数 + 1 (末尾の予備のブートセクタを含む)。
        return new FileSystemSignature(FileSystemKind.Ntfs, (total + 1) * bps, null, bps);
    }

    /// <summary>NTFS のクラスタあたりのセクタ数 (128 を超える場合は負の値で 2 の累乗を表す)。</summary>
    public static int NtfsSectorsPerCluster(byte raw) => raw <= 0x80 ? raw : raw >= 0xF4 ? 1 << (256 - raw) : 0;

    private static FileSystemSignature? TryExFat(ReadOnlySpan<byte> b)
    {
        long length = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(72));
        int bpsShift = b[108], spcShift = b[109];
        if (bpsShift is < 9 or > 12 || spcShift > 25 - bpsShift || length <= 0)
        {
            return null;
        }

        return new FileSystemSignature(FileSystemKind.ExFat, length << bpsShift, null, 1 << bpsShift);
    }

    private static FileSystemSignature? TryFat(ReadOnlySpan<byte> b)
    {
        // ジャンプ命令 (EB xx 90 または E9 xx xx)、BPB の整合性、末尾の 55 AA。
        if (!(b[0] == 0xEB && b[2] == 0x90 || b[0] == 0xE9) || b[510] != 0x55 || b[511] != 0xAA)
        {
            return null;
        }

        if (FatGeometry.TryParse(b, out FatGeometry g))
        {
            return new FileSystemSignature(g.Kind, g.TotalSectors * g.BytesPerSector, g.Label, g.BytesPerSector);
        }

        return null;
    }

    private static bool NtfsBackupMatches(IVolumeReader reader, long offset, byte[] head)
    {
        ushort bps = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(11));
        long total = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(0x28));
        long at = offset + total * bps;
        if (at < 0 || at + 512 > reader.Length)
        {
            return false;
        }

        byte[] backup = reader.ReadBytes(at, 512);
        return backup.AsSpan(0, 512).SequenceEqual(head.AsSpan(0, 512));
    }

    private static bool BackupSectorMatches(IVolumeReader reader, long offset, byte[] head, long backupOffset)
    {
        if (backupOffset <= 0)
        {
            return false;
        }

        byte[] backup = reader.ReadBytes(offset + backupOffset, 512);
        return backup.AsSpan(0, 512).SequenceEqual(head.AsSpan(0, 512));
    }

    private static bool ExFatBackupMatches(IVolumeReader reader, long offset, byte[] head)
    {
        int bps = 1 << head[108];
        if (12 * bps > 1 << 20)
        {
            return false;
        }

        byte[] main = reader.ReadBytes(offset, 12 * bps);
        byte[] backup = reader.ReadBytes(offset + 12L * bps, 12 * bps);
        return ExFatBootChecksum(main, bps) is var (ok, sum) && ok && main.AsSpan().SequenceEqual(backup) && sum != 0;
    }

    /// <summary>exFAT のブート領域のチェックサム (11 セクタ分、VolumeFlags と PercentInUse を除く)。12 番目のセクタの値と一致するか。</summary>
    public static (bool Matches, uint Sum) ExFatBootChecksum(ReadOnlySpan<byte> region, int bytesPerSector)
    {
        if (region.Length < 12 * bytesPerSector)
        {
            return (false, 0);
        }

        uint sum = 0;
        int n = 11 * bytesPerSector;
        for (int i = 0; i < n; i++)
        {
            if (i is 106 or 107 or 112)
            {
                continue;
            }

            sum = ((sum & 1) != 0 ? 0x80000000u : 0) + (sum >> 1) + region[i];
        }

        ReadOnlySpan<byte> sector12 = region.Slice(11 * bytesPerSector, bytesPerSector);
        for (int i = 0; i + 4 <= sector12.Length; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(sector12.Slice(i)) != sum)
            {
                return (false, sum);
            }
        }

        return (true, sum);
    }
}

/// <summary>FAT12/16/32 の BPB から求めた配置 (FOR-11 の仕様 1・2)。</summary>
public readonly record struct FatGeometry
{
    public int BytesPerSector { get; init; }
    public int SectorsPerCluster { get; init; }
    public int ReservedSectors { get; init; }
    public int FatCount { get; init; }
    public int RootEntries { get; init; }
    public long TotalSectors { get; init; }
    public long SectorsPerFat { get; init; }
    public long RootCluster { get; init; }
    public int FsInfoSector { get; init; }
    public int BackupBootSector { get; init; }
    public uint VolumeId { get; init; }
    public string Label { get; init; }
    public long RootDirSectors { get; init; }
    public long FirstDataSector { get; init; }
    public long ClusterCount { get; init; }
    public FileSystemKind Kind { get; init; }

    public long ClusterSize => (long)BytesPerSector * SectorsPerCluster;

    /// <summary>BPB の値として成り立つか (クラスタサイズが 2 の累乗、FAT の領域がボリュームの内側) を調べて配置を求める。</summary>
    public static bool TryParse(ReadOnlySpan<byte> b, out FatGeometry geometry, int? overrideSectorsPerCluster = null)
    {
        geometry = default;
        if (b.Length < 512)
        {
            return false;
        }

        int bps = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(11));
        int spc = overrideSectorsPerCluster ?? b[13];
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(14));
        int fats = b[16];
        int rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(17));
        long total = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(19));
        if (total == 0)
        {
            total = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(32));
        }

        long fatSize = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(22));
        bool fat32Layout = fatSize == 0;
        if (fat32Layout)
        {
            fatSize = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(36));
        }

        if (bps is < 512 or > 4096 || (bps & (bps - 1)) != 0 || spc <= 0 || (spc & (spc - 1)) != 0 || spc > 128 || reserved == 0 || fats is 0 or > 4
            || total == 0 || fatSize == 0)
        {
            return false;
        }

        long rootDirSectors = ((rootEntries * 32L) + (bps - 1)) / bps;
        long firstData = reserved + fats * fatSize + rootDirSectors;
        if (firstData >= total)
        {
            return false;
        }

        long clusters = (total - firstData) / spc;
        FileSystemKind kind = clusters < 4085 ? FileSystemKind.Fat12 : clusters < 65525 ? FileSystemKind.Fat16 : FileSystemKind.Fat32;

        // FAT の領域が、クラスタ数を表すのに足りるか (足りなければ BPB の矛盾)。
        long neededFatBytes = kind switch
        {
            FileSystemKind.Fat12 => (clusters + 2) * 3 / 2,
            FileSystemKind.Fat16 => (clusters + 2) * 2,
            _ => (clusters + 2) * 4,
        };
        if (fatSize * bps < neededFatBytes || kind == FileSystemKind.Fat32 && (rootEntries != 0 || !fat32Layout))
        {
            return false;
        }

        int labelAt = fat32Layout ? 71 : 43;
        uint volumeId = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(fat32Layout ? 67 : 39));
        string label = Encoding.ASCII.GetString(b.Slice(labelAt, 11)).TrimEnd(' ', '\0');
        geometry = new FatGeometry
        {
            BytesPerSector = bps,
            SectorsPerCluster = spc,
            ReservedSectors = reserved,
            FatCount = fats,
            RootEntries = rootEntries,
            TotalSectors = total,
            SectorsPerFat = fatSize,
            RootCluster = fat32Layout ? BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(44)) : 0,
            FsInfoSector = fat32Layout ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(48)) : 0,
            BackupBootSector = fat32Layout ? BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(50)) : 0,
            VolumeId = volumeId,
            Label = label,
            RootDirSectors = rootDirSectors,
            FirstDataSector = firstData,
            ClusterCount = clusters,
            Kind = kind,
        };
        return true;
    }
}
