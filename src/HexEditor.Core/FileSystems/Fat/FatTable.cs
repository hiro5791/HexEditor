using System.Buffers.Binary;
using System.Collections;

namespace HexEditor.Core.FileSystems.Fat;

/// <summary>FAT のエントリの幅。</summary>
public enum FatWidth
{
    Fat12 = 12,
    Fat16 = 16,
    Fat32 = 32,
}

/// <summary>
/// FAT 1 つの読み込み (FOR-11 の仕様 3・5)。全体を読み込まず、64 KB 単位でキャッシュして必要なエントリだけ読む。
/// クラスタ番号は 2 始まり (0・1 は予約)。
/// </summary>
public sealed class FatTable
{
    private readonly IVolumeReader _fat;

    public FatTable(IVolumeReader volume, long offset, long length, FatWidth width, long clusterCount)
    {
        _fat = new CachedVolumeReader(new SubVolumeReader(volume, offset, length), 64 * 1024, 64);
        Width = width;
        ClusterCount = clusterCount;
        Offset = offset;
        Length = length;
    }

    public FatWidth Width { get; }

    /// <summary>データ領域のクラスタ数 (番号は 2〜ClusterCount + 1)。</summary>
    public long ClusterCount { get; }

    public long Offset { get; }

    public long Length { get; }

    /// <summary>最大のクラスタ番号 + 1。</summary>
    public long ClusterLimit => ClusterCount + 2;

    /// <summary>エントリの値 (FAT32 は上位 4 ビットを除く)。</summary>
    public uint Get(long cluster)
    {
        if (cluster < 0 || cluster >= ClusterLimit)
        {
            return 0;
        }

        switch (Width)
        {
            case FatWidth.Fat12:
                {
                    long at = cluster * 3 / 2;
                    ushort v = _fat.U16(at);
                    return (uint)((cluster & 1) == 0 ? v & 0xFFF : v >> 4);
                }

            case FatWidth.Fat16:
                return _fat.U16(cluster * 2);
            default:
                return _fat.U32(cluster * 4) & 0x0FFFFFFF;
        }
    }

    /// <summary>エントリが使用中のチェーンの終わりか。</summary>
    public bool IsEnd(uint value) => Width switch
    {
        FatWidth.Fat12 => value >= 0xFF8,
        FatWidth.Fat16 => value >= 0xFFF8,
        _ => value >= 0x0FFFFFF8,
    };

    /// <summary>不良クラスタの印か。</summary>
    public bool IsBad(uint value) => Width switch
    {
        FatWidth.Fat12 => value == 0xFF7,
        FatWidth.Fat16 => value == 0xFFF7,
        _ => value == 0x0FFFFFF7,
    };

    /// <summary>エントリのバイト位置 (FAT の先頭から。FAT12 は 1.5 バイトの先頭)。</summary>
    public long EntryOffset(long cluster) => Width switch
    {
        FatWidth.Fat12 => cluster * 3 / 2,
        FatWidth.Fat16 => cluster * 2,
        _ => cluster * 4,
    };

    /// <summary>チェーンをたどった結果。</summary>
    public sealed record Chain(IReadOnlyList<long> Clusters, FsCorruptionKind Problem, long ProblemCluster);

    /// <summary>
    /// 先頭クラスタからチェーンをたどる (仕様 5)。循環・範囲外・途中の空きクラスタを検出したらそこで止める。たどる数は
    /// <paramref name="limit"/> (既定はクラスタ数) まで。訪れたクラスタの記録は、短いチェーンは集合、長いチェーンはビットマップで持つ
    /// (メモリはクラスタ数 / 8 バイトまで)。
    /// </summary>
    public Chain Walk(long first, long? limit = null, CancellationToken cancellationToken = default)
    {
        var clusters = new List<long>();
        if (first < 2 || first >= ClusterLimit)
        {
            return new Chain(clusters, first is 0 ? FsCorruptionKind.None : FsCorruptionKind.ClusterOutOfRange, first);
        }

        long max = Math.Min(limit ?? ClusterCount, ClusterCount);
        HashSet<long>? small = [];
        BitArray? large = null;
        long current = first;
        while (true)
        {
            if ((clusters.Count & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool seen;
            if (large is not null)
            {
                seen = large[(int)(current - 2)];
                large[(int)(current - 2)] = true;
            }
            else
            {
                seen = !small!.Add(current);
                if (small.Count > 65536 && ClusterCount <= int.MaxValue)
                {
                    large = new BitArray((int)ClusterCount);
                    foreach (long c in small)
                    {
                        large[(int)(c - 2)] = true;
                    }

                    small = null;
                }
            }

            if (seen)
            {
                return new Chain(clusters, FsCorruptionKind.ChainLoop, current);
            }

            clusters.Add(current);
            if (clusters.Count >= max)
            {
                return new Chain(clusters, FsCorruptionKind.None, 0);
            }

            uint next = Get(current);
            if (IsEnd(next))
            {
                return new Chain(clusters, FsCorruptionKind.None, 0);
            }

            if (next == 0)
            {
                return new Chain(clusters, FsCorruptionKind.ChainFreeCluster, current);
            }

            if (next < 2 || next >= ClusterLimit || IsBad(next))
            {
                return new Chain(clusters, FsCorruptionKind.ClusterOutOfRange, current);
            }

            current = next;
        }
    }

    /// <summary>2 つの FAT の違いのあるエントリの数 (長時間処理。仕様 3)。</summary>
    public static long CountDifferences(FatTable a, FatTable b, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        long diff = 0;
        long limit = a.ClusterLimit;
        const int Step = 16384;
        for (long c = 2; c < limit; c += Step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long end = Math.Min(limit, c + Step);
            for (long k = c; k < end; k++)
            {
                if (a.Get(k) != b.Get(k))
                {
                    diff++;
                }
            }

            progress?.Report((double)end / limit);
        }

        return diff;
    }
}

/// <summary>FAT の空きエントリから求める割り当て情報 (FOR-11 の仕様 8)。</summary>
public sealed class FatAllocationMap(FatTable table) : IAllocationMap
{
    public long ClusterCount => table.ClusterCount;

    public bool IsAllocated(long cluster) => cluster >= 2 && cluster < table.ClusterLimit && table.Get(cluster) != 0;

    public IEnumerable<(long Start, long Count)> FreeRuns(long first, long count, CancellationToken cancellationToken = default)
    {
        long start = Math.Max(2, first);
        long end = Math.Min(table.ClusterLimit, first + count);
        long run = -1;
        for (long c = start; c < end; c++)
        {
            if ((c & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool free = table.Get(c) == 0;
            if (free && run < 0)
            {
                run = c;
            }
            else if (!free && run >= 0)
            {
                yield return (run, c - run);
                run = -1;
            }
        }

        if (run >= 0)
        {
            yield return (run, end - run);
        }
    }

    public long CountFree(CancellationToken cancellationToken = default)
    {
        long free = 0;
        foreach ((_, long n) in FreeRuns(2, table.ClusterCount, cancellationToken))
        {
            free += n;
        }

        return free;
    }
}

/// <summary>FAT のディレクトリエントリの日時 (FOR-11 の仕様 4、FOR-05: タイムゾーンを持たない)。</summary>
public static class FatTime
{
    /// <summary>日付と時刻 (2 秒単位) と 10 ミリ秒単位の補正から日時を作る。0 なら空。範囲外は不正な日時。</summary>
    public static FsTimestamp Decode(ushort date, ushort time, int tenMs, TimeSpan localOffset, bool hasTime = true)
    {
        if (date == 0 && time == 0)
        {
            return default;
        }

        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59 || tenMs is < 0 or > 199)
        {
            return new FsTimestamp(null, Invalid: true, Raw: ((long)date << 16) | time);
        }

        var local = new DateTime(year, month, day, hasTime ? hour : 0, hasTime ? minute : 0, hasTime ? second : 0, DateTimeKind.Unspecified);
        local = local.AddMilliseconds(tenMs * 10);
        return new FsTimestamp(DateTime.SpecifyKind(local - localOffset, DateTimeKind.Utc), ZoneUnknown: true, Raw: ((long)date << 16) | time)
        {
            Recorded = local,
        };
    }

    /// <summary>exFAT の日時 (32 ビット + 10 ミリ秒 + UTC オフセット。FOR-11 の仕様 7)。オフセットが無効ならケースのタイムゾーンを使う。</summary>
    public static FsTimestamp DecodeExFat(uint value, int tenMs, byte utcOffset, TimeSpan localOffset)
    {
        if (value == 0)
        {
            return default;
        }

        FsTimestamp t = Decode((ushort)(value >> 16), (ushort)value, tenMs, TimeSpan.Zero);
        if (t.Invalid || t.Recorded is not DateTime local)
        {
            return t;
        }

        if ((utcOffset & 0x80) != 0)
        {
            int quarters = (sbyte)(byte)((utcOffset & 0x7F) << 1) >> 1;
            var offset = TimeSpan.FromMinutes(quarters * 15);
            return new FsTimestamp(DateTime.SpecifyKind(local - offset, DateTimeKind.Utc), Raw: value) { Recorded = local };
        }

        return new FsTimestamp(DateTime.SpecifyKind(local - localOffset, DateTimeKind.Utc), ZoneUnknown: true, Raw: value) { Recorded = local };
    }

    /// <summary>LFN の短い名前のチェックサム。</summary>
    public static byte LfnChecksum(ReadOnlySpan<byte> shortName)
    {
        byte sum = 0;
        for (int i = 0; i < 11; i++)
        {
            sum = (byte)((((sum & 1) << 7) | (sum >> 1)) + shortName[i]);
        }

        return sum;
    }

    public static ushort ReadU16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
}
