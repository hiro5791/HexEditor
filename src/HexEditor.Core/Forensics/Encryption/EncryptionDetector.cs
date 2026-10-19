using System.Buffers.Binary;
using HexEditor.Core.Forensics.Carving;

namespace HexEditor.Core.Forensics.Encryption;

/// <summary>
/// 暗号化されたボリュームの検出 (12 の FOR-29)。パーティション解析 (FOR-06)・ファイルシステムの判定 (FOR-09) が範囲ごとに
/// <see cref="Detect"/> を呼び、暗号化されていればファイルシステムブラウザを開かない (<see cref="FileSystemBlockReason"/>。仕様 4)。
/// コマンド「暗号化の情報を表示」はディスク・イメージ全体を <see cref="Scan"/> で調べる。読むのはメタデータの数セクタと、
/// 署名のない範囲の先頭 64 KiB だけ (「巨大ファイル・長時間処理」)。
/// </summary>
public static class EncryptionDetector
{
    /// <summary>エントロピーを測る長さ (仕様 2 の表の VeraCrypt / TrueCrypt)。</summary>
    public const int EntropyWindow = 64 * 1024;

    /// <summary>「暗号化または圧縮されたデータの可能性」とする下限 (ビット / バイト)。</summary>
    public const double EntropyThreshold = 7.9;

    /// <summary>Apple Core Storage の GPT の種類 GUID。</summary>
    public static readonly Guid CoreStorageType = new("53746F72-6167-11AA-AA11-00306543ECAC");

    /// <summary>パーティション表の項目の上限 (GPT の項目の数が壊れていても大量に読まない)。</summary>
    public const int MaxPartitions = 256;

    /// <summary>
    /// 範囲を調べる。<paramref name="partitionType"/> は GPT の種類 GUID (分かれば)。暗号化と判定できなければ null
    /// (ファイルシステムの署名がある、またはエントロピーが低い)。
    /// </summary>
    public static EncryptionInfo? Detect(ICarveData data, long offset, long length, Guid? partitionType = null)
    {
        if (offset < 0 || length <= 0 || offset > data.Length)
        {
            return null;
        }

        length = Math.Min(length, data.Length - offset);
        if (partitionType == CoreStorageType)
        {
            return new EncryptionInfo { Kind = EncryptionKind.CoreStorage, Offset = offset, Length = length };
        }

        byte[]? head = EncryptionRead.Bytes(data, offset, (int)Math.Min(4096, length));
        if (head is null)
        {
            return null;
        }

        if (head.Length >= 512 && BitLockerParser.Kind(head) is { } toGo)
        {
            BitLockerInfo info = BitLockerParser.Parse(data, offset, length) ?? new BitLockerInfo { ToGo = toGo, Damaged = true };
            var warnings = new List<string>();
            if (info.MetadataMismatch)
            {
                warnings.Add("metadataMismatch");
            }

            if (info.Damaged)
            {
                warnings.Add("metadataDamaged");
            }

            return new EncryptionInfo
            {
                Kind = toGo ? EncryptionKind.BitLockerToGo : EncryptionKind.BitLocker,
                Offset = offset,
                Length = length,
                BitLocker = info,
                Warnings = warnings,
            };
        }

        if (LuksParser.IsLuks(head) && LuksParser.Parse(data, offset) is { } luks)
        {
            return new EncryptionInfo { Kind = luks.Version == 1 ? EncryptionKind.Luks1 : EncryptionKind.Luks2, Offset = offset, Length = length, Luks = luks };
        }

        if (ApfsEncryptionProbe.IsContainer(head))
        {
            IReadOnlyList<ApfsVolumeEncryption>? volumes = ApfsEncryptionProbe.Probe(data, offset);
            return volumes is { Count: > 0 } && volumes.Any(v => v.Encrypted)
                ? new EncryptionInfo { Kind = EncryptionKind.ApfsEncrypted, Offset = offset, Length = length, ApfsVolumes = volumes }
                : null;
        }

        if (HasFileSystemSignature(data, offset, length, head))
        {
            return null;
        }

        // 署名がない: 先頭 64 KiB のエントロピーが高ければ「暗号化または圧縮されたデータの可能性」(VeraCrypt / TrueCrypt は判定できない)。
        byte[]? window = EncryptionRead.Bytes(data, offset, (int)Math.Min(EntropyWindow, length));
        if (window is null || window.Length < EntropyWindow)
        {
            return null;
        }

        double entropy = Entropy(window);
        return entropy >= EntropyThreshold
            ? new EncryptionInfo { Kind = EncryptionKind.PossiblyEncrypted, Offset = offset, Length = length, Entropy = entropy }
            : null;
    }

    /// <summary>
    /// ファイルシステムブラウザ (FOR-09) を開かない理由 (仕様 4)。暗号化されていなければ null。FOR-09 の担当部品は、ブラウザを開く前にこれを呼ぶ。
    /// </summary>
    public static EncryptionInfo? FileSystemBlockReason(ICarveData data, long offset, long length, Guid? partitionType = null) =>
        Detect(data, offset, length, partitionType) is { BlocksFileSystem: true } info ? info : null;

    /// <summary>シャノンのエントロピー (ビット / バイト)。</summary>
    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (byte b in data)
        {
            counts[b]++;
        }

        double entropy = 0;
        foreach (int c in counts)
        {
            if (c > 0)
            {
                double p = (double)c / data.Length;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy;
    }

    /// <summary>よく使われるファイルシステム・ボリュームの署名があるか (あれば暗号化の「可能性」の判定をしない)。</summary>
    public static bool HasFileSystemSignature(ICarveData data, long offset, long length, ReadOnlySpan<byte> head)
    {
        if (head.Length >= 512)
        {
            ReadOnlySpan<byte> oem = head.Slice(3, 8);
            if (oem.SequenceEqual("NTFS    "u8) || oem.SequenceEqual("EXFAT   "u8) || head.Slice(3, 4).SequenceEqual("ReFS"u8)
                || head.Slice(0x36, 5).SequenceEqual("FAT12"u8) || head.Slice(0x36, 5).SequenceEqual("FAT16"u8) || head.Slice(0x52, 5).SequenceEqual("FAT32"u8)
                || head[..4].SequenceEqual("XFSB"u8))
            {
                return true;
            }
        }

        if (head.Length >= 1024 + 64)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(head[(1024 + 56)..]) == 0xEF53 || head.Slice(1024, 2).SequenceEqual("H+"u8) || head.Slice(1024, 2).SequenceEqual("HX"u8))
            {
                return true;
            }
        }

        if (head.Length >= 4096 && head.Slice(4086, 10).SequenceEqual("SWAPSPACE2"u8))
        {
            return true;
        }

        // ISO 9660 / UDF (32 KiB の後の記述子) と Btrfs (64 KiB + 64)。
        if (EncryptionRead.Bytes(data, offset + 32769, 5) is { } iso && (iso.AsSpan().SequenceEqual("CD001"u8) || iso.AsSpan().SequenceEqual("BEA01"u8)))
        {
            return true;
        }

        return length > 0x10040 + 8 && EncryptionRead.Bytes(data, offset + 0x10040, 8) is { } btrfs && btrfs.AsSpan().SequenceEqual("_BHRfS_M"u8);
    }

    /// <summary>
    /// ディスク・イメージ全体を調べる。MBR / GPT のパーティションごとに <see cref="Detect"/> し、パーティション表がなければ全体を 1 つの範囲として調べる。
    /// </summary>
    public static IReadOnlyList<EncryptionScanEntry> Scan(ICarveData data, int sectorSize = 512)
    {
        var entries = new List<EncryptionScanEntry>();
        foreach ((long offset, long length, Guid? type, byte? mbr) in Partitions(data, sectorSize))
        {
            entries.Add(new EncryptionScanEntry(entries.Count + 1, offset, length, type, mbr, Detect(data, offset, length, type)));
        }

        if (entries.Count == 0)
        {
            entries.Add(new EncryptionScanEntry(0, 0, data.Length, null, null, Detect(data, 0, data.Length)));
        }

        return entries;
    }

    /// <summary>MBR と GPT のパーティション (拡張パーティションの中は読まない)。範囲外・重複した項目は除く。</summary>
    public static IEnumerable<(long Offset, long Length, Guid? Type, byte? MbrType)> Partitions(ICarveData data, int sectorSize = 512)
    {
        byte[]? mbr = EncryptionRead.Bytes(data, 0, 512);
        if (mbr is null || mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            yield break;
        }

        // 先頭がボリュームそのもの (パーティション表のないフロッピー形式) なら、パーティション表とみなさない。
        if (BitLockerParser.Kind(mbr) is not null || mbr.AsSpan(3, 8).SequenceEqual("NTFS    "u8) || mbr.AsSpan(3, 8).SequenceEqual("EXFAT   "u8)
            || mbr.AsSpan(0x52, 5).SequenceEqual("FAT32"u8) || mbr.AsSpan(0x36, 3).SequenceEqual("FAT"u8))
        {
            yield break;
        }

        bool protective = false;
        var list = new List<(long, long, Guid?, byte?)>();
        for (int i = 0; i < 4; i++)
        {
            int at = 446 + i * 16;
            byte type = mbr[at + 4];
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + 8));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(at + 12));
            if (type == 0xEE)
            {
                protective = true;
                continue;
            }

            if (type is 0 or 0x05 or 0x0F or 0x85 || count == 0)
            {
                continue;
            }

            long offset = (long)first * sectorSize;
            long length = (long)count * sectorSize;
            if (offset > 0 && offset < data.Length)
            {
                list.Add((offset, Math.Min(length, data.Length - offset), null, type));
            }
        }

        if (protective && Gpt(data, sectorSize) is { Count: > 0 } gpt)
        {
            foreach (var p in gpt)
            {
                yield return p;
            }

            yield break;
        }

        foreach (var p in list)
        {
            yield return p;
        }
    }

    private static List<(long, long, Guid?, byte?)>? Gpt(ICarveData data, int sectorSize)
    {
        byte[]? header = EncryptionRead.Bytes(data, sectorSize, 92);
        if (header is null || !header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            return null;
        }

        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        uint count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)), MaxPartitions);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        if (size is < 128 or > 1024 || entriesLba <= 0)
        {
            return null;
        }

        var list = new List<(long, long, Guid?, byte?)>();
        byte[]? table = EncryptionRead.Bytes(data, entriesLba * sectorSize, (int)(count * size));
        if (table is null)
        {
            return list;
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * (int)size, 128);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty)
            {
                continue;
            }

            long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
            long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
            if (first <= 0 || last < first || first > data.Length / sectorSize)
            {
                continue;
            }

            long offset = first * sectorSize;
            long length = Math.Min((last - first + 1) * sectorSize, data.Length - offset);
            list.Add((offset, length, type, null));
        }

        return list;
    }
}
