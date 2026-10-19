using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>
/// FOR-23 のテスト用のディスクイメージを、構造を直接書いて作る (TD-FOR-NTFS-10PCT・TD-FOR-NTFS-FAT32 の代わり。これらは OS のツールで作る定義だが、
/// この PC ではフォーマット・diskpart を使わないため、同じ配置の最小の NTFS / FAT32 をコードで書く)。ファイルはスパースファイルとして作る。
/// NTFS は境界の検証に要る構造 (ブートセクタ、$MFT、$MFTMirr、$LogFile、$Bitmap、$Boot、ルートの $INDEX_ALLOCATION、ファイルの $FILE_NAME と
/// 非常駐の $DATA) だけを持つ。
/// </summary>
internal static class SyntheticDisks
{
    public const long MiB = 1024 * 1024;

    /// <summary>ファイル 1 つ (名前、内容、NTFS ではディスク上の位置)。</summary>
    public sealed record FileSpec(string Name, byte[] Content);

    /// <summary>TD-FOR-NTFS-FAT32 の代わり: MBR で NTFS (LBA 2,048、262,144 セクタ) と FAT32 (LBA 264,192、196,608 セクタ、クラスタ 1 KiB)。</summary>
    public static string NtfsFat32(string path)
    {
        using SafeFileHandle file = Create(path, 256 * MiB);
        byte[] mbr = new byte[512];
        Entry(mbr, 0, 0x07, 2048, 262_144);
        Entry(mbr, 1, 0x0C, 264_192, 196_608);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        RandomAccess.Write(file, mbr, 0);
        WriteNtfs(file, 2048 * 512L, 262_144 * 512L, FileSet(), "NTFSPART");
        WriteFat32(file, 264_192 * 512L, 196_608 * 512L, FileSet());
        return path;
    }

    /// <summary>TD-FOR-NTFS-10PCT の代わり: 1 GiB、GPT で NTFS のパーティション 1 つ (LBA 2,048〜末尾 − 34)。種 1〜10 の乱数 10 MiB のファイル 10 個。</summary>
    public static string Ntfs10Pct(string path)
    {
        long size = 1024 * MiB;
        using SafeFileHandle file = Create(path, size);
        long sectors = size / 512;
        long last = sectors - 34;
        WriteGpt(file, sectors, 2048, last);
        var files = Enumerable.Range(1, 10).Select(i =>
        {
            byte[] data = new byte[10 * MiB];
            TestDataCatalog.Random((ulong)i, 0, data);
            return new FileSpec($"r{i:D2}.bin", data);
        }).ToList();
        WriteNtfs(file, 2048 * 512L, (last - 2048 + 1) * 512, files, "NTFS10");
        return path;
    }

    /// <summary>TD-FOR-FILESET の一部 (readme.txt、data.bin、small100.bin)。</summary>
    public static List<FileSpec> FileSet()
    {
        byte[] data = new byte[MiB];
        TestDataCatalog.Random(TestDataCatalog.RandomSeed, 0, data);
        byte[] readme = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("This is the readme file of the test volume.\r\n", 100)))[..4096];
        return [new FileSpec("readme.txt", readme), new FileSpec("data.bin", data), new FileSpec("small100.bin", [.. Enumerable.Repeat((byte)0x41, 100)])];
    }

    private static SafeFileHandle Create(string path, long size)
    {
        SafeFileHandle file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        SparseFiles.TryMakeSparse(file);
        RandomAccess.SetLength(file, size);
        return file;
    }

    private static void Entry(byte[] mbr, int index, byte type, uint start, uint count)
    {
        Span<byte> e = mbr.AsSpan(446 + index * 16, 16);
        e[4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], start);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], count);
    }

    private static void WriteGpt(SafeFileHandle file, long sectors, long first, long last)
    {
        byte[] mbr = new byte[512];
        Entry(mbr, 0, 0xEE, 1, (uint)Math.Min(sectors - 1, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        RandomAccess.Write(file, mbr, 0);

        byte[] entries = new byte[128 * 128];
        new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").TryWriteBytes(entries);
        new Guid("00000000-0000-0000-0000-000000000001").TryWriteBytes(entries.AsSpan(16));
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32), first);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40), last);
        Encoding.Unicode.GetBytes("Data").CopyTo(entries, 56);
        uint entriesCrc = ImageChecksums.Crc32(entries);
        foreach ((long lba, long backup, long entriesLba) in new[] { (1L, sectors - 1, 2L), (sectors - 1, 1L, sectors - 33) })
        {
            byte[] h = new byte[512];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), lba);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), backup);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), sectors - 34);
            new Guid("00000000-0000-0000-0000-0000000000D0").TryWriteBytes(h.AsSpan(56));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), ImageChecksums.Crc32(h.AsSpan(0, 92)));
            RandomAccess.Write(file, h, lba * 512);
            RandomAccess.Write(file, entries, entriesLba * 512);
        }
    }

    // ---- NTFS (クラスタ 4 KiB、MFT レコード 1 KiB) ----

    private const int Cluster = 4096;
    private const int RecordSize = 1024;

    /// <summary>NTFS のファイルの、ボリュームの中のデータの位置 (テストの期待値: スケルトンに含めないこと)。</summary>
    public static Dictionary<string, (long Offset, long Length)> NtfsDataRanges { get; } = new(StringComparer.Ordinal);

    private static void WriteNtfs(SafeFileHandle file, long volumeStart, long volumeLength, IReadOnlyList<FileSpec> files, string label)
    {
        long totalClusters = volumeLength / Cluster;
        const long MftLcn = 4;
        const int MftRecords = 64;
        const long MirrLcn = MftLcn + MftRecords * RecordSize / Cluster;
        const long LogLcn = MirrLcn + 1;
        const long LogClusters = 16;
        const long BitmapLcn = LogLcn + LogClusters;
        long bitmapBytes = (totalClusters + 7) / 8;
        long bitmapClusters = (bitmapBytes + Cluster - 1) / Cluster;
        long indexLcn = BitmapLcn + bitmapClusters;
        long next = indexLcn + 1;

        var used = new List<(long Lcn, long Count)>
        {
            (0, 2), (MftLcn, MftRecords * RecordSize / Cluster), (MirrLcn, 1), (LogLcn, LogClusters), (BitmapLcn, bitmapClusters), (indexLcn, 1),
            (totalClusters - 1, 1),
        };
        var records = new byte[MftRecords][];
        records[0] = Record(0, "$MFT", 5, false, (MftLcn, MftRecords * RecordSize / Cluster), MftRecords * RecordSize);
        records[1] = Record(1, "$MFTMirr", 5, false, (MirrLcn, 1), 4 * RecordSize);
        records[2] = Record(2, "$LogFile", 5, false, (LogLcn, LogClusters), LogClusters * Cluster);
        records[3] = Record(3, "$Volume", 5, false, null, 0);
        records[5] = Record(5, ".", 5, true, null, 0, indexAllocation: (indexLcn, 1));
        records[6] = Record(6, "$Bitmap", 5, false, (BitmapLcn, bitmapClusters), bitmapBytes);
        records[7] = Record(7, "$Boot", 5, false, (0, 2), 8192);
        NtfsDataRanges.Clear();
        for (int i = 0; i < files.Count; i++)
        {
            long clusters = Math.Max(1, (files[i].Content.Length + Cluster - 1) / Cluster);
            records[16 + i] = Record(16 + i, files[i].Name, 5, false, (next, clusters), files[i].Content.Length);
            RandomAccess.Write(file, files[i].Content, volumeStart + next * Cluster);
            NtfsDataRanges[files[i].Name] = (next * Cluster, files[i].Content.Length);
            used.Add((next, clusters));
            next += clusters;
        }

        byte[] mft = new byte[MftRecords * RecordSize];
        for (int i = 0; i < MftRecords; i++)
        {
            byte[] r = records[i] ?? Record(i, null, 5, false, null, 0, inUse: false);
            Fixup(r);
            r.CopyTo(mft, i * RecordSize);
        }

        RandomAccess.Write(file, mft, volumeStart + MftLcn * Cluster);
        RandomAccess.Write(file, mft.AsSpan(0, 4 * RecordSize), volumeStart + MirrLcn * Cluster);
        byte[] indx = new byte[Cluster];
        "INDX"u8.CopyTo(indx);
        RandomAccess.Write(file, indx, volumeStart + indexLcn * Cluster);
        byte[] log = new byte[Cluster];
        "RSTR"u8.CopyTo(log);
        RandomAccess.Write(file, log, volumeStart + LogLcn * Cluster);

        byte[] bitmap = new byte[bitmapBytes];
        foreach ((long lcn, long count) in used)
        {
            for (long c = lcn; c < lcn + count && c < totalClusters; c++)
            {
                bitmap[c / 8] |= (byte)(1 << (int)(c % 8));
            }
        }

        RandomAccess.Write(file, bitmap, volumeStart + BitmapLcn * Cluster);

        byte[] boot = new byte[512];
        boot[0] = 0xEB;
        boot[1] = 0x52;
        boot[2] = 0x90;
        "NTFS    "u8.CopyTo(boot.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(0x0B), 512);
        boot[0x0D] = Cluster / 512;
        boot[0x15] = 0xF8;
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x28), volumeLength / 512 - 1);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x30), MftLcn);
        BinaryPrimitives.WriteInt64LittleEndian(boot.AsSpan(0x38), MirrLcn);
        boot[0x40] = 0xF6; // -10: 1 KiB
        boot[0x44] = 0x01;
        Encoding.ASCII.GetBytes(label).CopyTo(boot, 0x48);
        boot[510] = 0x55;
        boot[511] = 0xAA;
        RandomAccess.Write(file, boot, volumeStart);
        RandomAccess.Write(file, boot, volumeStart + volumeLength - 512);
    }

    private static byte[] Record(int index, string? name, long parent, bool directory, (long Lcn, long Count)? data, long dataSize,
        (long Lcn, long Count)? indexAllocation = null, bool inUse = true)
    {
        byte[] r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 0x30);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x1C), RecordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x2C), (uint)index);
        int at = 0x38;
        if (name is not null)
        {
            byte[] n = Encoding.Unicode.GetBytes(name);
            int valueLength = 66 + n.Length;
            int length = (24 + valueLength + 7) / 8 * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 16), (uint)valueLength);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(at + 20), 24);
            Span<byte> v = r.AsSpan(at + 24);
            BinaryPrimitives.WriteInt64LittleEndian(v, parent | (1L << 48));
            BinaryPrimitives.WriteInt64LittleEndian(v[48..], dataSize);
            v[64] = (byte)name.Length;
            v[65] = 1; // Win32
            n.CopyTo(v[66..]);
            at += length;
        }

        if (data is { } d)
        {
            at = NonResident(r, at, 0x80, d, dataSize);
        }

        if (indexAllocation is { } ia)
        {
            at = NonResident(r, at, 0xA0, ia, ia.Count * Cluster);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)(at + 8));
        return r;
    }

    private static int NonResident(byte[] r, int at, int type, (long Lcn, long Count) run, long size)
    {
        byte[] runlist = [0x44, .. BitConverter.GetBytes((uint)run.Count), .. BitConverter.GetBytes((uint)run.Lcn), 0];
        int length = (0x40 + runlist.Length + 7) / 8 * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), (uint)length);
        r[at + 8] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 0x18), run.Count - 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(at + 0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 0x28), run.Count * Cluster);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(at + 0x38), size);
        runlist.CopyTo(r, at + 0x40);
        return at + length;
    }

    private static void Fixup(byte[] r)
    {
        const ushort Usn = 0x0001;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), Usn);
        for (int i = 1; i <= 2; i++)
        {
            int end = i * 512 - 2;
            r[0x30 + i * 2] = r[end];
            r[0x30 + i * 2 + 1] = r[end + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), Usn);
        }
    }

    // ---- FAT32 (セクタ 512、クラスタ 1 KiB) ----

    /// <summary>FAT32 のファイルの、ボリュームの中のデータの位置。</summary>
    public static Dictionary<string, (long Offset, long Length)> FatDataRanges { get; } = new(StringComparer.Ordinal);

    private static void WriteFat32(SafeFileHandle file, long volumeStart, long volumeLength, IReadOnlyList<FileSpec> files)
    {
        const int Spc = 2;
        const int ClusterBytes = Spc * 512;
        const int Reserved = 32;
        long totalSectors = volumeLength / 512;
        long fatSectors = ((totalSectors - Reserved) / Spc * 4 + 511) / 512 + 8;
        long dataSector = Reserved + 2 * fatSectors;
        long clusters = (totalSectors - dataSector) / Spc;
        uint[] fat = new uint[clusters + 2];
        fat[0] = 0x0FFFFFF8;
        fat[1] = 0x0FFFFFFF;
        long nextCluster = 2;
        long Alloc(long count)
        {
            long first = nextCluster;
            for (long c = first; c < first + count; c++)
            {
                fat[c] = c == first + count - 1 ? 0x0FFFFFFF : (uint)(c + 1);
            }

            nextCluster += count;
            return first;
        }

        long Offset(long c) => volumeStart + (dataSector + (c - 2) * Spc) * 512;

        long root = Alloc(1);
        long sub = Alloc(1);
        byte[] rootDir = new byte[ClusterBytes];
        byte[] subDir = new byte[ClusterBytes];
        void Dirent(byte[] dir, int index, string shortName, byte attr, long cluster, long size)
        {
            Span<byte> e = dir.AsSpan(index * 32, 32);
            Encoding.ASCII.GetBytes(shortName.PadRight(11)).CopyTo(e);
            e[11] = attr;
            BinaryPrimitives.WriteUInt16LittleEndian(e[20..], (ushort)(cluster >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(e[26..], (ushort)cluster);
            BinaryPrimitives.WriteUInt32LittleEndian(e[28..], (uint)size);
        }

        Dirent(rootDir, 0, "FAT32PART", 0x08, 0, 0);
        Dirent(rootDir, 1, "DIR1", 0x10, sub, 0);
        Dirent(subDir, 0, ".", 0x10, sub, 0);
        Dirent(subDir, 1, "..", 0x10, 0, 0);
        FatDataRanges.Clear();
        for (int i = 0; i < files.Count; i++)
        {
            long count = Math.Max(1, (files[i].Content.Length + ClusterBytes - 1) / ClusterBytes);
            long first = Alloc(count);
            RandomAccess.Write(file, files[i].Content, Offset(first));
            FatDataRanges[files[i].Name] = (Offset(first) - volumeStart, files[i].Content.Length);
            string name = Path.GetFileNameWithoutExtension(files[i].Name).ToUpperInvariant();
            string ext = Path.GetExtension(files[i].Name).TrimStart('.').ToUpperInvariant();
            Dirent(rootDir, 2 + i, name.PadRight(8)[..8] + ext.PadRight(3)[..3], 0x20, first, files[i].Content.Length);
        }

        RandomAccess.Write(file, rootDir, Offset(root));
        RandomAccess.Write(file, subDir, Offset(sub));
        byte[] fatBytes = new byte[fatSectors * 512];
        for (int i = 0; i < fat.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fat[i]);
        }

        RandomAccess.Write(file, fatBytes, volumeStart + Reserved * 512L);
        RandomAccess.Write(file, fatBytes, volumeStart + (Reserved + fatSectors) * 512);

        byte[] boot = new byte[512];
        boot[0] = 0xEB;
        boot[1] = 0x58;
        boot[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(boot.AsSpan(3));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), 512);
        boot[13] = Spc;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), Reserved);
        boot[16] = 2;
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), (uint)totalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), (uint)fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), (uint)root);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);
        boot[66] = 0x29;
        "FAT32PART  "u8.CopyTo(boot.AsSpan(71));
        "FAT32   "u8.CopyTo(boot.AsSpan(82));
        boot[510] = 0x55;
        boot[511] = 0xAA;
        RandomAccess.Write(file, boot, volumeStart);
        RandomAccess.Write(file, boot, volumeStart + 6 * 512);
    }
}

/// <summary>テスト用の最小のファイル一覧 (NTFS は MFT の $FILE_NAME と $DATA の大きさ、FAT32 はディレクトリのエントリ)。</summary>
internal static class TestFileLister
{
    public static List<(string Name, long Size)> Ntfs(IByteSource volume)
    {
        byte[] boot = new byte[512];
        volume.Read(0, boot);
        long cluster = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B)) * (long)boot[0x0D];
        long mft = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30)) * cluster;
        var result = new List<(string, long)>();
        for (int i = 16; i < 64; i++)
        {
            byte[] r = new byte[1024];
            volume.Read(mft + i * 1024L, r);
            if (!r.AsSpan(0, 4).SequenceEqual("FILE"u8) || (r[0x16] & 1) == 0)
            {
                continue;
            }

            int at = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(0x14));
            string? name = null;
            long size = 0;
            while (at + 8 < r.Length && BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at)) != 0xFFFFFFFF)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at));
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(at + 4));
                if (type == 0x30)
                {
                    int value = at + BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(at + 20));
                    name = Encoding.Unicode.GetString(r, value + 66, r[value + 64] * 2);
                }
                else if (type == 0x80)
                {
                    size = BinaryPrimitives.ReadInt64LittleEndian(r.AsSpan(at + 0x30));
                }

                if (length <= 0)
                {
                    break;
                }

                at += length;
            }

            if (name is not null)
            {
                result.Add((name, size));
            }
        }

        return result;
    }

    public static List<(string Name, long Size)> Fat32(IByteSource volume)
    {
        byte[] b = new byte[512];
        volume.Read(0, b);
        int spc = b[13];
        long reserved = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14));
        long fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
        long root = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(44));
        long data = (reserved + 2 * fatSectors) * 512;
        byte[] dir = new byte[spc * 512];
        volume.Read(data + (root - 2) * spc * 512, dir);
        var result = new List<(string, long)>();
        for (int i = 0; i < dir.Length / 32; i++)
        {
            ReadOnlySpan<byte> e = dir.AsSpan(i * 32, 32);
            if (e[0] == 0)
            {
                break;
            }

            if ((e[11] & 0x18) != 0)
            {
                continue;
            }

            string name = Encoding.ASCII.GetString(e[..8]).TrimEnd() + "." + Encoding.ASCII.GetString(e[8..11]).TrimEnd();
            result.Add((name, BinaryPrimitives.ReadUInt32LittleEndian(e[28..])));
        }

        return result;
    }
}
