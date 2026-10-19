using System.Buffers.Binary;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>パーティション表の 1 エントリ (バイトの範囲。ディスクの先頭から)。</summary>
public sealed record ImagePartition(long Offset, long Length, string Scheme, string Type);

/// <summary>パーティション表の読み取りの結果。</summary>
public sealed record ImagePartitionTable(string? Scheme, IReadOnlyList<ImagePartition> Partitions, IReadOnlyList<(long Offset, long Length)> TableRanges);

/// <summary>
/// スケルトン・スパースイメージと RAID の判定のための、パーティション表の最小限の読み取り (MBR・拡張パーティション・GPT)。
/// パーティションの詳しい解析と表示は FOR-06 が担当する。入力は信頼できないデータとして扱い、EBR の連鎖は 256 個・循環で止める。
/// </summary>
public static class ImagePartitions
{
    private const int MaxLogical = 256;

    public static ImagePartitionTable Read(IByteSource disk, int sectorSize)
    {
        byte[] mbr = new byte[sectorSize];
        if (disk.Length < sectorSize * 2L || !disk.Read(0, mbr).IsComplete || mbr[510] != 0x55 || mbr[511] != 0xAA)
        {
            return new ImagePartitionTable(null, [], []);
        }

        if (TryGpt(disk, sectorSize, out ImagePartitionTable? gpt))
        {
            return gpt!;
        }

        var partitions = new List<ImagePartition>();
        var tables = new List<(long, long)> { (0, sectorSize) };
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<byte> e = mbr.AsSpan(446 + i * 16, 16);
            byte type = e[4];
            long start = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            long count = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (e[0] is not (0x00 or 0x80))
            {
                // 状態のバイトが 00 / 80 以外: MBR ではない (ブートセクタなど)。
                return new ImagePartitionTable(null, [], []);
            }

            // 保護 MBR (EE) は GPT の印: GPT を読めなかった場合はパーティションなしとする。
            if (type is 0 or 0xEE || count == 0)
            {
                continue;
            }

            any = true;
            if (type is 0x05 or 0x0F or 0x85)
            {
                ReadExtended(disk, sectorSize, start, partitions, tables);
            }
            else
            {
                Add(partitions, disk, sectorSize, start, count, "MBR", type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return any ? new ImagePartitionTable("MBR", partitions, tables) : new ImagePartitionTable(null, [], []);
    }

    private static void Add(List<ImagePartition> partitions, IByteSource disk, int sectorSize, long startLba, long count, string scheme, string type)
    {
        long offset = startLba * sectorSize;
        long length = Math.Min(count * sectorSize, disk.Length - offset);
        if (startLba > 0 && offset < disk.Length && length > 0)
        {
            partitions.Add(new ImagePartition(offset, length, scheme, type));
        }
    }

    private static void ReadExtended(IByteSource disk, int sectorSize, long extendedStart, List<ImagePartition> partitions, List<(long, long)> tables)
    {
        var seen = new HashSet<long>();
        long ebr = extendedStart;
        byte[] sector = new byte[sectorSize];
        for (int n = 0; n < MaxLogical && seen.Add(ebr); n++)
        {
            long at = ebr * sectorSize;
            if (at + sectorSize > disk.Length || !disk.Read(at, sector).IsComplete || sector[510] != 0x55 || sector[511] != 0xAA)
            {
                return;
            }

            tables.Add((at, sectorSize));
            ReadOnlySpan<byte> first = sector.AsSpan(446, 16);
            if (first[4] != 0)
            {
                Add(partitions, disk, sectorSize, ebr + BinaryPrimitives.ReadUInt32LittleEndian(first[8..]), BinaryPrimitives.ReadUInt32LittleEndian(first[12..]),
                    "EBR", first[4].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            ReadOnlySpan<byte> second = sector.AsSpan(462, 16);
            long next = BinaryPrimitives.ReadUInt32LittleEndian(second[8..]);
            if (second[4] == 0 || next == 0)
            {
                return;
            }

            ebr = extendedStart + next;
        }
    }

    private static bool TryGpt(IByteSource disk, int sectorSize, out ImagePartitionTable? table)
    {
        table = null;
        byte[] header = new byte[sectorSize];
        if (!disk.Read(sectorSize, header).IsComplete || !header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            return false;
        }

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        long firstUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
        long lastUsable = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48));
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72));
        long count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80));
        long entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));
        long sectors = disk.Length / sectorSize;
        if (headerSize is < 92 or > 4096 || entrySize is < 128 or > 4096 || count is <= 0 or > 1024 || entriesLba < 2 || entriesLba >= sectors
            || firstUsable < 2 || lastUsable < firstUsable || lastUsable >= sectors)
        {
            return false;
        }

        byte[] entries = new byte[count * entrySize];
        if (entriesLba * sectorSize + entries.Length > disk.Length)
        {
            return false;
        }

        disk.Read(entriesLba * sectorSize, entries);
        var partitions = new List<ImagePartition>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = entries.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty)
            {
                continue;
            }

            long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
            long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
            if (first < firstUsable || last < first || last > lastUsable)
            {
                continue;
            }

            Add(partitions, disk, sectorSize, first, last - first + 1, "GPT", type.ToString("D"));
        }

        // パーティション表: 先頭から最初の使える LBA まで (保護 MBR・主ヘッダー・エントリ配列) と、最後の使える LBA の後 (バックアップ)。
        var tables = new List<(long, long)> { (0, firstUsable * sectorSize) };
        long backup = (lastUsable + 1) * sectorSize;
        if (backup < disk.Length)
        {
            tables.Add((backup, disk.Length - backup));
        }

        table = new ImagePartitionTable("GPT", partitions, tables);
        return true;
    }
}

/// <summary>データソースの一部 [offset, offset + length) を先頭 0 として読むビュー。</summary>
public sealed class SubRangeSource(IByteSource inner, long offset, long length) : ByteSourceBase
{
    public override string DisplayName => inner.DisplayName;

    public override string Identity => inner.Identity + "#" + offset + "+" + length;

    public override long Length => length;

    public override int LogicalSectorSize => inner.LogicalSectorSize;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public long Offset => offset;

    public override ReadResult Read(long at, Span<byte> buffer)
    {
        int count = ClampToLength(at, buffer.Length);
        ReadResult r = inner.Read(offset + at, buffer[..count]);
        return r.IsComplete ? new ReadResult(count) : new ReadResult(count, [.. r.Unreadable.Select(u => u with { Offset = u.Offset - offset })]);
    }
}
