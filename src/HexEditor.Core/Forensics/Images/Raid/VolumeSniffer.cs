using System.Buffers.Binary;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Raid;

/// <summary>先頭 1 MiB の判定の結果 (FOR-26 の仕様 5)。</summary>
public sealed record SniffResult(string? PartitionTable, string? FileSystem, int PartitionCount)
{
    /// <summary>パーティション表もファイルシステムの署名もない。</summary>
    public bool Nothing => PartitionTable is null && FileSystem is null;
}

/// <summary>
/// RAID の構成の確認を助ける判定 (FOR-26 の仕様 5): 先頭 1 MiB のパーティション表 (GPT・MBR) と、ファイルシステムの署名 (先頭、または
/// 最初のパーティションの先頭が 1 MiB 以内ならそこ)。FOR-06・FOR-08 の判定と同じ署名を使う最小限のもの。
/// </summary>
public static class VolumeSniffer
{
    public const int Window = 1024 * 1024;

    public static SniffResult Sniff(IByteSource source, int sectorSize = 512)
    {
        byte[] head = new byte[(int)Math.Min(Window, source.Length)];
        source.Read(0, head);
        string? table = null;
        int partitions = 0;
        long firstPartition = -1;
        if (head.Length >= sectorSize * 2 && head[510] == 0x55 && head[511] == 0xAA)
        {
            if (head.AsSpan(sectorSize, 8).SequenceEqual("EFI PART"u8))
            {
                table = "GPT";
                long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(sectorSize + 72));
                long count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(sectorSize + 80)), 128u);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(sectorSize + 84));
                for (long i = 0; i < count && size >= 128; i++)
                {
                    long at = entriesLba * sectorSize + i * size;
                    if (at < 0 || at + 48 > head.Length)
                    {
                        break;
                    }

                    if (head.AsSpan((int)at, 16).ContainsAnyExcept((byte)0))
                    {
                        partitions++;
                        long first = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan((int)at + 32));
                        firstPartition = firstPartition < 0 ? first * sectorSize : Math.Min(firstPartition, first * sectorSize);
                    }
                }
            }
            else if (FileSystemOf(head) is null && IsMbr(head))
            {
                table = "MBR";
                for (int i = 0; i < 4; i++)
                {
                    ReadOnlySpan<byte> e = head.AsSpan(446 + i * 16, 16);
                    if (e[4] != 0)
                    {
                        partitions++;
                        long first = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * (long)sectorSize;
                        firstPartition = firstPartition < 0 ? first : Math.Min(firstPartition, first);
                    }
                }
            }
        }

        string? fs = FileSystemOf(head);
        if (fs is null && firstPartition > 0 && firstPartition + 4096 <= source.Length)
        {
            byte[] volume = new byte[(int)Math.Min(64 * 1024, source.Length - firstPartition)];
            source.Read(firstPartition, volume);
            fs = FileSystemOf(volume);
        }

        return new SniffResult(table, fs, partitions);
    }

    private static bool IsMbr(byte[] head)
    {
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<byte> e = head.AsSpan(446 + i * 16, 16);
            if (e[0] is not (0x00 or 0x80))
            {
                return false;
            }

            any |= e[4] != 0;
        }

        return any;
    }

    /// <summary>先頭のバイト列からファイルシステムの種類 (FOR-08 の表の署名)。</summary>
    public static string? FileSystemOf(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 512)
        {
            if (d.Slice(3, 8).SequenceEqual("NTFS    "u8))
            {
                return "NTFS";
            }

            if (d.Slice(3, 8).SequenceEqual("EXFAT   "u8))
            {
                return "exFAT";
            }

            if (d.Slice(3, 8).SequenceEqual("-FVE-FS-"u8))
            {
                return "BitLocker";
            }

            if (d[510] == 0x55 && d[511] == 0xAA)
            {
                if (d.Slice(82, 5).SequenceEqual("FAT32"u8))
                {
                    return "FAT32";
                }

                if (d.Slice(54, 5).SequenceEqual("FAT16"u8))
                {
                    return "FAT16";
                }

                if (d.Slice(54, 5).SequenceEqual("FAT12"u8))
                {
                    return "FAT12";
                }
            }

            if (d[..4].SequenceEqual("XFSB"u8))
            {
                return "XFS";
            }

            if (d[..6].SequenceEqual("LUKS\xBA\xBE"u8))
            {
                return "LUKS";
            }

            if (d.Length >= 64 && d.Slice(32, 4).SequenceEqual("NXSB"u8))
            {
                return "APFS";
            }
        }

        if (d.Length >= 1082 && BinaryPrimitives.ReadUInt16LittleEndian(d[1080..]) == 0xEF53)
        {
            return "ext";
        }

        if (d.Length >= 1026 && (d.Slice(1024, 2).SequenceEqual("H+"u8) || d.Slice(1024, 2).SequenceEqual("HX"u8)))
        {
            return "HFS+";
        }

        for (int s = 0; s < 4 && (s + 1) * 512 <= d.Length; s++)
        {
            if (d.Slice(s * 512, 8).SequenceEqual("LABELONE"u8))
            {
                return "LVM2";
            }
        }

        if (d.Length >= 32774 && d.Slice(32769, 5).SequenceEqual("CD001"u8))
        {
            return "ISO 9660";
        }

        return null;
    }
}
