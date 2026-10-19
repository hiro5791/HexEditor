using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.FileSystems.Partitions;

/// <summary>走査の間隔 (FOR-08 の仕様 1。複数選べる)。</summary>
[Flags]
public enum ScanIntervals
{
    None = 0,
    MiB = 1,
    Cylinder = 2,
    EverySector = 4,
    Default = MiB | Cylinder,
}

/// <summary>候補の種類 (FOR-08 の仕様 2 の表の行)。</summary>
public enum LostPartitionKind
{
    Ntfs,
    Fat,
    ExFat,
    Ext,
    Hfs,
    Apfs,
    IsoUdf,
    LvmLdm,
    BitLocker,
    GptEbr,
}

/// <summary>確度 (仕様 3: 高 = 主と予備の両方が一致、中 = 主だけ、低 = 署名だけ)。</summary>
public enum ScanConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>見つかった候補 (仕様 3)。<see cref="SectorCount"/> は推定 (分からなければ null)。</summary>
public sealed record LostPartitionCandidate(long StartLba, long? SectorCount, FileSystemKind Kind, string KindText, ScanConfidence Confidence,
    IReadOnlyList<int> Overlaps, int SectorSize)
{
    public long StartOffset => StartLba * SectorSize;
    public long? Size => SectorCount * SectorSize;
}

/// <summary>スキャンの条件 (仕様 1・2)。</summary>
public sealed record LostPartitionScanOptions
{
    public long StartLba { get; init; }

    /// <summary>範囲の終わり (このセクタを含まない)。null はディスクの末尾。</summary>
    public long? EndLba { get; init; }

    public ScanIntervals Intervals { get; init; } = ScanIntervals.Default;

    /// <summary>探す種類 (空ならすべて)。</summary>
    public IReadOnlySet<LostPartitionKind> Kinds { get; init; } = new HashSet<LostPartitionKind>();
}

/// <summary>スキャンの結果。読めなかった範囲は末尾にまとめて示す (仕様の「エラー」)。</summary>
public sealed record LostPartitionScanResult(IReadOnlyList<LostPartitionCandidate> Candidates, IReadOnlyList<(long Lba, long Count)> Unreadable,
    bool Cancelled);

/// <summary>
/// 失われたパーティションのスキャン (FOR-08)。指定した間隔の位置でファイルシステムの先頭とパーティション表の痕跡を探す。見つかるたびに
/// <c>found</c> に知らせ、キャンセルしてもそれまでの結果を返す。読めないセクタは飛ばして続ける。
/// </summary>
public static class LostPartitionScanner
{
    /// <summary>目安の時間の計算に使う読み込み速度 (仕様の「巨大ファイル」: 200 MB/s)。</summary>
    public const double AssumedBytesPerSecond = 200_000_000;

    public static TimeSpan EstimateFullScan(long bytes) => TimeSpan.FromSeconds(bytes / AssumedBytesPerSecond);

    private const int HeadLength = 2048;

    public static LostPartitionScanResult Scan(IVolumeReader reader, int sectorSize, LostPartitionScanOptions options, PartitionAnalysis? existing,
        Action<long>? progressLba = null, Action<LostPartitionCandidate>? found = null, CancellationToken cancellationToken = default)
    {
        long total = reader.Length / sectorSize;
        long start = Math.Clamp(options.StartLba, 0, total);
        long end = Math.Clamp(options.EndLba ?? total, start, total);
        var results = new List<LostPartitionCandidate>();
        var keys = new HashSet<(long, FileSystemKind)>();
        var unreadable = new List<(long, long)>();
        bool cancelled = false;
        IReadOnlySet<LostPartitionKind> kinds = options.Kinds;
        bool want(LostPartitionKind k) => kinds.Count == 0 || kinds.Contains(k);

        void Add(LostPartitionCandidate c)
        {
            if (keys.Add((c.StartLba, c.Kind)))
            {
                results.Add(c);
                found?.Invoke(c);
            }
        }

        void Check(long lba, ReadOnlySpan<byte> head)
        {
            foreach (LostPartitionCandidate c in Probe(reader, sectorSize, lba, head, existing, want))
            {
                Add(c);
            }
        }

        try
        {
            if (options.Intervals.HasFlag(ScanIntervals.EverySector))
            {
                // 全セクタ: 1 MiB ずつ読み、各セクタの先頭を調べる (ISO / UDF は 32 KiB 先を読む)。
                int perChunk = Math.Max(1, (1 << 20) / sectorSize);
                byte[] buffer = new byte[(perChunk * sectorSize) + HeadLength];
                for (long lba = start; lba < end; lba += perChunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progressLba?.Invoke(lba);
                    int sectors = (int)Math.Min(perChunk, end - lba);
                    var r = reader.Read(lba * sectorSize, buffer.AsSpan(0, Math.Min(buffer.Length, (int)Math.Min(int.MaxValue, reader.Length - lba * sectorSize))));
                    if (!r.IsComplete)
                    {
                        foreach (var u in r.Unreadable)
                        {
                            AddUnreadable(unreadable, u.Offset / sectorSize, Math.Max(1, u.Length / sectorSize));
                        }
                    }

                    for (int s = 0; s < sectors; s++)
                    {
                        if ((s & 0x3FF) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        int at = s * sectorSize;
                        if (QuickReject(buffer.AsSpan(at, Math.Min(HeadLength, buffer.Length - at))))
                        {
                            continue;
                        }

                        Check(lba + s, buffer.AsSpan(at, Math.Min(HeadLength, buffer.Length - at)));
                    }

                    if (want(LostPartitionKind.IsoUdf))
                    {
                        // ISO / UDF: 各セクタから 32768 バイト目の記述子。2048 バイトの境界だけ調べる。
                        for (long s = lba; s < lba + sectors; s++)
                        {
                            if (s * sectorSize % 2048 == 0)
                            {
                                CheckIso(reader, sectorSize, s, existing, Add);
                            }
                        }
                    }
                }
            }
            else
            {
                byte[] head = new byte[HeadLength];
                foreach (long lba in Positions(start, end, sectorSize, options.Intervals))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((lba & 0xFF) == 0 || options.Intervals == ScanIntervals.MiB)
                    {
                        progressLba?.Invoke(lba);
                    }

                    var r = reader.Read(lba * sectorSize, head);
                    if (!r.IsComplete)
                    {
                        foreach (var u in r.Unreadable)
                        {
                            AddUnreadable(unreadable, u.Offset / sectorSize, Math.Max(1, (u.Length + sectorSize - 1) / sectorSize));
                        }

                        if (r.Unreadable.Any(u => u.Offset == lba * sectorSize))
                        {
                            continue;
                        }
                    }

                    if (r.BytesReturned < 512)
                    {
                        continue;
                    }

                    if (!QuickReject(head))
                    {
                        Check(lba, head.AsSpan(0, r.BytesReturned));
                    }

                    if (want(LostPartitionKind.IsoUdf))
                    {
                        CheckIso(reader, sectorSize, lba, existing, Add);
                    }
                }
            }

            progressLba?.Invoke(end);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        return new LostPartitionScanResult(results, unreadable, cancelled);
    }

    private static void AddUnreadable(List<(long Lba, long Count)> list, long lba, long count)
    {
        if (list.Count > 0 && list[^1].Lba + list[^1].Count >= lba)
        {
            (long l, long c) = list[^1];
            list[^1] = (l, Math.Max(c, lba + count - l));
        }
        else if (list.Count < 100_000)
        {
            list.Add((lba, count));
        }
    }

    /// <summary>1 MiB 境界とシリンダ境界 (63 セクタ) の位置を小さい順に重ねずに返す。</summary>
    private static IEnumerable<long> Positions(long start, long end, int sectorSize, ScanIntervals intervals)
    {
        long mib = Math.Max(1, (1 << 20) / sectorSize);
        long nextMib = intervals.HasFlag(ScanIntervals.MiB) ? (start + mib - 1) / mib * mib : long.MaxValue;
        long nextCyl = intervals.HasFlag(ScanIntervals.Cylinder) ? (start + 62) / 63 * 63 : long.MaxValue;
        while (true)
        {
            long next = Math.Min(nextMib, nextCyl);
            if (next >= end || next == long.MaxValue)
            {
                yield break;
            }

            yield return next;
            if (nextMib == next)
            {
                nextMib += mib;
            }

            if (nextCyl == next)
            {
                nextCyl += 63;
            }
        }
    }

    /// <summary>どの署名にも当たらないセクタを早く捨てる (全セクタの走査を速くする)。</summary>
    private static bool QuickReject(ReadOnlySpan<byte> h)
    {
        if (h.Length < 512)
        {
            return true;
        }

        bool bootSig = h[510] == 0x55 && h[511] == 0xAA;
        if (bootSig || h[3] == (byte)'N' || h[3] == (byte)'E' || h[3] == (byte)'-' || h[0] == (byte)'P' || h[0] == (byte)'E' || h[0] == (byte)'L')
        {
            return false;
        }

        if (h.Length >= 1082 && (h[1080] == 0x53 && h[1081] == 0xEF || h[1024] == (byte)'H'))
        {
            return false;
        }

        if (h.Length >= 36 && h[32] == (byte)'N' && h[33] == (byte)'X')
        {
            return false;
        }

        for (int s = 1; s < 4 && (s + 1) * 512 <= h.Length; s++)
        {
            if (h[s * 512] == (byte)'L')
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<int> Overlaps(PartitionAnalysis? existing, long lba, long? count)
    {
        if (existing is null)
        {
            return [];
        }

        long last = lba + Math.Max(1, count ?? 1) - 1;
        return [.. existing.Partitions.Where(r => !r.IsContainer && r.SectorCount > 0 && r.StartLba <= last && lba <= r.EndLba).Select(r => r.Number)];
    }

    private static LostPartitionKind KindGroup(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Ntfs => LostPartitionKind.Ntfs,
        FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32 => LostPartitionKind.Fat,
        FileSystemKind.ExFat => LostPartitionKind.ExFat,
        FileSystemKind.Ext => LostPartitionKind.Ext,
        FileSystemKind.HfsPlus or FileSystemKind.Hfsx => LostPartitionKind.Hfs,
        FileSystemKind.Apfs => LostPartitionKind.Apfs,
        FileSystemKind.Iso9660 or FileSystemKind.Udf => LostPartitionKind.IsoUdf,
        FileSystemKind.Lvm2 or FileSystemKind.Ldm => LostPartitionKind.LvmLdm,
        FileSystemKind.BitLocker => LostPartitionKind.BitLocker,
        _ => LostPartitionKind.GptEbr,
    };

    private static IEnumerable<LostPartitionCandidate> Probe(IVolumeReader reader, int sectorSize, long lba, ReadOnlySpan<byte> head,
        PartitionAnalysis? existing, Func<LostPartitionKind, bool> want)
    {
        var list = new List<LostPartitionCandidate>();
        long offset = lba * sectorSize;
        FileSystemSignature? sig = FileSystemDetector.DetectHead(head, reader.Length - offset);
        if (sig is not null && sig.Kind is not (FileSystemKind.Iso9660 or FileSystemKind.Udf) && want(KindGroup(sig.Kind)))
        {
            long? count = sig.EstimatedLength is long len ? (len + sectorSize - 1) / sectorSize : null;
            ScanConfidence confidence = ScanConfidence.Medium;
            long startLba = lba;
            switch (sig.Kind)
            {
                case FileSystemKind.Ntfs:
                    {
                        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(head[0x28..]);
                        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head[11..]);
                        byte[] sector = head[..512].ToArray();
                        if (Same(reader, offset + totalSectors * bps, sector))
                        {
                            confidence = ScanConfidence.High;
                        }
                        else if (offset - totalSectors * bps >= 0 && Same(reader, offset - totalSectors * bps, sector))
                        {
                            // 末尾のバックアップブートセクタから逆算する (仕様 2)。
                            startLba = (offset - totalSectors * bps) / sectorSize;
                            confidence = ScanConfidence.High;
                        }

                        break;
                    }

                case FileSystemKind.Fat32:
                    {
                        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head[11..]);
                        int backup = BinaryPrimitives.ReadUInt16LittleEndian(head[50..]);
                        byte[] sector = head[..512].ToArray();
                        if (backup is > 0 and < 64 && Same(reader, offset + (long)backup * bps, sector))
                        {
                            confidence = ScanConfidence.High;
                        }
                        else if (backup is > 0 and < 64 && offset - (long)backup * bps >= 0 && Same(reader, offset - (long)backup * bps, sector))
                        {
                            startLba = (offset - (long)backup * bps) / sectorSize;
                            confidence = ScanConfidence.High;
                        }

                        break;
                    }

                case FileSystemKind.ExFat:
                    {
                        int bps = 1 << head[108];
                        byte[] main = reader.ReadBytes(offset, 12 * bps);
                        byte[] backup = reader.ReadBytes(offset + 12L * bps, 12 * bps);
                        bool mainOk = FileSystemDetector.ExFatBootChecksum(main, bps).Matches;
                        confidence = mainOk && main.AsSpan().SequenceEqual(backup) ? ScanConfidence.High : mainOk ? ScanConfidence.Medium : ScanConfidence.Low;
                        break;
                    }

                case FileSystemKind.HfsPlus or FileSystemKind.Hfsx:
                    {
                        // 末尾から 1024 バイト目の予備のボリュームヘッダー。
                        if (sig.EstimatedLength is long size && size > 2048)
                        {
                            byte[] alt = reader.ReadBytes(offset + size - 1024, 2);
                            confidence = alt.AsSpan().SequenceEqual(head.Slice(1024, 2)) ? ScanConfidence.High : ScanConfidence.Medium;
                        }

                        break;
                    }

                case FileSystemKind.BitLocker or FileSystemKind.Lvm2 or FileSystemKind.Ldm:
                    confidence = ScanConfidence.Low;
                    break;
            }

            list.Add(new LostPartitionCandidate(startLba, count, sig.Kind, FileSystemDetector.DisplayName(sig.Kind), confidence,
                Overlaps(existing, startLba, count), sectorSize));
        }

        if (!want(LostPartitionKind.GptEbr) || sig is not null)
        {
            return list;
        }

        // GPT ヘッダー (EFI PART) と EBR (55 AA と妥当なエントリ)。
        if (head.Length >= 92 && head[..8].SequenceEqual("EFI PART"u8))
        {
            int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            bool crc = false;
            if (headerSize is >= 92 && headerSize <= head.Length)
            {
                byte[] copy = head[..headerSize].ToArray();
                uint stored = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(16));
                copy.AsSpan(16, 4).Clear();
                crc = Crc32Ieee.Compute(copy) == stored;
            }

            long current = BinaryPrimitives.ReadInt64LittleEndian(head[24..]);
            long backup = BinaryPrimitives.ReadInt64LittleEndian(head[32..]);
            long diskStart = lba - current;
            long? count = backup > current ? backup + 1 : current + 1;
            list.Add(new LostPartitionCandidate(Math.Max(0, diskStart), count, FileSystemKind.Unknown, "GPT", crc ? ScanConfidence.Medium : ScanConfidence.Low,
                Overlaps(existing, Math.Max(0, diskStart), count), sectorSize));
        }
        else if (lba > 0 && head[510] == 0x55 && head[511] == 0xAA)
        {
            ReadOnlySpan<byte> e = head.Slice(0x1BE, 16);
            byte status = e[0], type = e[4];
            uint relStart = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            bool othersEmpty = head.Slice(0x1BE + 32, 32).IndexOfAnyExcept((byte)0) < 0;
            if (status is 0 or 0x80 && type != 0 && relStart > 0 && count > 0 && othersEmpty)
            {
                list.Add(new LostPartitionCandidate(lba + relStart, count, FileSystemKind.Unknown, "EBR", ScanConfidence.Low,
                    Overlaps(existing, lba + relStart, count), sectorSize));
            }
        }

        return list;
    }

    private static void CheckIso(IVolumeReader reader, int sectorSize, long lba, PartitionAnalysis? existing, Action<LostPartitionCandidate> add)
    {
        long at = lba * sectorSize + 32768;
        if (at + 6 > reader.Length)
        {
            return;
        }

        byte[] d = reader.ReadBytes(at, 6);
        ReadOnlySpan<byte> id = d.AsSpan(1, 5);
        if (id.SequenceEqual("CD001"u8) || id.SequenceEqual("BEA01"u8) || id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
        {
            byte[] probe = reader.ReadBytes(lba * sectorSize, FileSystemDetector.ProbeLength);
            FileSystemSignature? sig = FileSystemDetector.DetectHead(probe, reader.Length - lba * sectorSize);
            if (sig is { Kind: FileSystemKind.Iso9660 or FileSystemKind.Udf })
            {
                long? count = sig.EstimatedLength is long len ? (len + sectorSize - 1) / sectorSize : null;
                add(new LostPartitionCandidate(lba, count, sig.Kind, FileSystemDetector.DisplayName(sig.Kind), ScanConfidence.Medium,
                    Overlaps(existing, lba, count), sectorSize));
            }
        }
    }

    private static bool Same(IVolumeReader reader, long offset, byte[] sector)
    {
        if (offset < 0 || offset + sector.Length > reader.Length)
        {
            return false;
        }

        byte[] other = reader.ReadBytes(offset, sector.Length, out bool complete);
        return complete && other.AsSpan().SequenceEqual(sector);
    }

    // ---- エクスポート (仕様 5) ----

    public static string ToCsv(IEnumerable<LostPartitionCandidate> candidates)
    {
        var s = new StringBuilder("StartLba,SectorCount,Size,Type,Confidence,Overlaps\r\n");
        foreach (LostPartitionCandidate c in candidates)
        {
            s.Append(c.StartLba).Append(',').Append(c.SectorCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(c.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty).Append(',').Append(c.KindText).Append(',')
                .Append(c.Confidence).Append(',').Append('"').Append(string.Join(' ', c.Overlaps)).Append('"').Append("\r\n");
        }

        return s.ToString();
    }

    public static string ToJson(IEnumerable<LostPartitionCandidate> candidates) =>
        JsonSerializer.Serialize(candidates.Select(c => new
        {
            startLba = c.StartLba, sectorCount = c.SectorCount, size = c.Size, type = c.KindText, confidence = c.Confidence.ToString(),
            overlaps = c.Overlaps,
        }), new JsonSerializerOptions { WriteIndented = true });
}
