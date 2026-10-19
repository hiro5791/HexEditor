using System.Buffers.Binary;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Fat;
using HexEditor.Core.FileSystems.Ntfs;

namespace HexEditor.Core.Wiping;

/// <summary>ファイルシステムの不整合の種類 (FOR-31 の仕様 4)。</summary>
public enum FsInconsistency
{
    None,

    /// <summary>NTFS の $Volume の dirty フラグ、FAT のブートセクタ・FAT[1] の dirty ビット、exFAT の VolumeDirty。</summary>
    DirtyFlag,

    /// <summary>FAT の 2 つのコピーが違う。</summary>
    FatCopiesDiffer,

    /// <summary>BPB の値が矛盾している。</summary>
    BpbInconsistent,

    /// <summary>$MFT と $MFTMirr が違う。</summary>
    MftMirrorMismatch,

    /// <summary>割り当て情報が読めない。</summary>
    AllocationUnreadable,

    /// <summary>exFAT のブート領域のチェックサムが合わない、メディアの障害のフラグ。</summary>
    BootChecksum,
}

/// <summary>スラックの消去の対象から外したファイルの数 (FOR-31 の仕様 2・6)。</summary>
public sealed record SlackSkipCounts(int Compressed, int Sparse, int Resident, int Inconsistent)
{
    public int Total => Compressed + Sparse + Resident + Inconsistent;
}

/// <summary>スラックの消去の計画: 上書きする範囲 (ドキュメント上の位置) と対象外にした数。</summary>
public sealed record SlackWipePlan(IReadOnlyList<WipeRange> Ranges, int Files, SlackSkipCounts Skipped)
{
    public long TotalBytes => Ranges.Sum(r => r.Length);
}

/// <summary>
/// ファイルシステムを理解した直接書き込みの消去 (FOR-31 の空き領域の「直接書き込み」とスラックの消去)。割り当て情報 (FOR-16) から
/// 未割り当てクラスタ・スラックを求める。対応するのは NTFS、FAT12/16/32、exFAT だけ。書き込む前に不整合 (dirty フラグなど) を確かめる。
/// </summary>
public static class FileSystemWipe
{
    /// <summary>対応しているファイルシステムか (仕様 1・2)。</summary>
    public static bool IsSupported(IFileSystem fs) => fs.Kind is FileSystemKind.Ntfs or FileSystemKind.Fat12 or FileSystemKind.Fat16
        or FileSystemKind.Fat32 or FileSystemKind.ExFat;

    /// <summary>
    /// 不整合がないかを確かめる (仕様 4: 直接書き込みの前に。不整合があれば実行しない)。dirty フラグ、FAT のコピーの違い、BPB の矛盾、
    /// $MFTMirr の不一致、割り当て情報が読めないことを不整合とする。
    /// </summary>
    public static FsInconsistency CheckConsistency(IFileSystem fs, CancellationToken cancellationToken = default)
    {
        if (fs.Allocation is null || fs.Issues.Any(i => i.Kind == FsIssueKind.AllocationUnreadable))
        {
            return FsInconsistency.AllocationUnreadable;
        }

        if (IsDirty(fs))
        {
            return FsInconsistency.DirtyFlag;
        }

        foreach (FsIssue issue in fs.Issues)
        {
            switch (issue.Kind)
            {
                case FsIssueKind.FatCopiesDiffer:
                    return FsInconsistency.FatCopiesDiffer;
                case FsIssueKind.BpbInconsistent:
                    return FsInconsistency.BpbInconsistent;
                case FsIssueKind.MftMirrorMismatch:
                    return FsInconsistency.MftMirrorMismatch;
                case FsIssueKind.ExFatBootChecksum:
                    return FsInconsistency.BootChecksum;
            }
        }

        // FAT のコピーの違いは解析では一部しか比べないため、ここで全体を比べる (FAT の大きさだけを読む)。
        if (fs is FatFileSystem fat && fat.FatCount > 1 && fat.CountFatDifferences(null, cancellationToken) > 0)
        {
            return FsInconsistency.FatCopiesDiffer;
        }

        return FsInconsistency.None;
    }

    /// <summary>
    /// 直接書き込み (空き領域・スラック) の前の検査 (仕様 1・2・4): 対応するファイルシステムで、不整合がないこと。拒否なら監査ログに「拒否」を
    /// 記録して <see cref="WipeRefusedException"/> (<see cref="WipeRefusal.Inconsistent"/> の詳細は <see cref="FsInconsistency"/> の名前)。
    /// </summary>
    public static void EnsureCanWriteDirect(IFileSystem fs, string targetName, WipeKind kind, IWipeForensicHooks? hooks = null,
        CancellationToken cancellationToken = default)
    {
        hooks ??= WipeForensics.Hooks;
        WipeRefusal refusal = WipeRefusal.None;
        string? detail = null;
        if (!IsSupported(fs))
        {
            refusal = WipeRefusal.UnsupportedFileSystem;
            detail = fs.Kind.ToString();
        }
        else if (CheckConsistency(fs, cancellationToken) is var inconsistency && inconsistency != FsInconsistency.None)
        {
            refusal = WipeRefusal.Inconsistent;
            detail = inconsistency.ToString();
        }

        if (refusal == WipeRefusal.None)
        {
            return;
        }

        hooks.Audit(WipeAuditOperations.Wipe, targetName, WipeAuditOutcome.Refused, new Dictionary<string, string>
        {
            ["kind"] = kind.ToString(),
            ["fileSystem"] = fs.Kind.ToString(),
            ["refusal"] = refusal.ToString(),
            ["detail"] = detail ?? string.Empty,
        });
        throw new WipeRefusedException(refusal, detail);
    }

    /// <summary>dirty フラグが立っているか (NTFS の $VOLUME_INFORMATION、FAT のブートセクタと FAT[1]、exFAT の VolumeFlags)。</summary>
    public static bool IsDirty(IFileSystem fs)
    {
        switch (fs)
        {
            case NtfsFileSystem ntfs:
            {
                // $Volume (MFT 3) の $VOLUME_INFORMATION の 10 バイト目からのフラグ。0x0001 = dirty。
                NtfsAttribute? info = ntfs.Load(3)?.Attributes.FirstOrDefault(a => a.Type == NtfsAttributeType.VolumeInformation && !a.NonResident);
                if (info is null || info.Value.Length < 12)
                {
                    // 読めない: 不整合として扱う (確かめられないまま書かない)。
                    return true;
                }

                return (BinaryPrimitives.ReadUInt16LittleEndian(info.Value.AsSpan(10)) & 0x0001) != 0;
            }

            case FatFileSystem fat:
            {
                // ブートセクタの予約バイト (FAT12/16 は 0x25、FAT32 は 0x41) の 0x01 は Windows の dirty の印。
                byte reserved = fat.Volume.ReadBytes(fat.Kind == FileSystemKind.Fat32 ? 0x41 : 0x25, 1)[0];
                if ((reserved & 0x01) != 0)
                {
                    return true;
                }

                // FAT[1] の「正常に切り離した」ビット (FAT16 は 0x8000、FAT32 は 0x08000000)。0 なら dirty。FAT12 にはない。
                uint entry = fat.Fat.Get(1);
                return fat.Kind switch
                {
                    FileSystemKind.Fat16 => (entry & 0x8000) == 0,
                    FileSystemKind.Fat32 => (entry & 0x08000000) == 0,
                    _ => false,
                };
            }

            case ExFatFileSystem exfat:
            {
                // VolumeFlags (ブートセクタの 106): bit 1 = VolumeDirty、bit 2 = MediaFailure。
                ushort flags = exfat.Volume.U16(106);
                return (flags & 0x0006) != 0;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 未割り当てクラスタの範囲 (ドキュメント上の位置。<paramref name="volumeOffset"/> はファイルシステムの先頭)。呼ぶたびに割り当て情報から
    /// 列挙し直す (範囲の一覧をメモリに持たない)。メタデータの範囲に重なる部分は除く (割り当て情報が正しくない場合の備え)。
    /// </summary>
    public static IEnumerable<WipeRange> FreeRanges(IFileSystem fs, long volumeOffset, CancellationToken cancellationToken = default)
    {
        FsRegion[] metadata = [.. fs.MetadataRegions.OrderBy(r => r.Offset)];
        foreach (ExtractMapRow row in FileSystemExtraction.UnallocatedRuns(fs, volumeOffset, cancellationToken))
        {
            long start = row.DiskOffset - volumeOffset;
            foreach ((long s, long l) in Subtract(start, row.Length, metadata))
            {
                yield return new WipeRange(volumeOffset + s, l);
            }
        }
    }

    /// <summary>空き領域の大きさ (確認ダイアログ。割り当て情報を 1 回走査する)。</summary>
    public static long FreeBytes(IFileSystem fs, long volumeOffset, CancellationToken cancellationToken = default) =>
        FreeRanges(fs, volumeOffset, cancellationToken).Sum(r => r.Length);

    /// <summary>
    /// スラックの消去の計画 (仕様 2): 各ファイル (名前付きストリームを含む) の最後の断片の、データの終わりから割り当てたクラスタの終わりまで
    /// (RAM スラックとドライブスラック)。圧縮・スパース・常駐のファイルは対象外にして数える。割り当て情報で未割り当てのクラスタ・メタデータの範囲に
    /// 重なるもの (解析の誤りの備え) も対象外にする。同じ範囲 (ハードリンク) は 1 回だけにする。
    /// </summary>
    public static SlackWipePlan PlanSlack(IFileSystem fs, long volumeOffset, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var ranges = new List<WipeRange>();
        var seen = new HashSet<long>();
        int files = 0, compressed = 0, sparse = 0, resident = 0, inconsistent = 0;
        long clusterSize = fs.ClusterSize;
        IAllocationMap? allocation = fs.Allocation;
        FsRegion[] metadata = [.. fs.MetadataRegions];
        foreach (FsEntry e in fs.EnumerateAll(progress, cancellationToken))
        {
            if (e.IsDirectory && !e.Flags.HasFlag(FsEntryFlags.Stream) || e.Flags.HasFlag(FsEntryFlags.Virtual) || e.Flags.HasFlag(FsEntryFlags.Metadata)
                || e.State != FsEntryState.Normal || e.Size <= 0)
            {
                continue;
            }

            // NTFS のシステムファイル ($MFT・$Bitmap など。レコード 0〜23) は対象にしない。
            if (e.Tag is NtfsTag { Record: < 24 })
            {
                continue;
            }

            if (e.Flags.HasFlag(FsEntryFlags.Compressed))
            {
                compressed++;
                continue;
            }

            if (e.Flags.HasFlag(FsEntryFlags.Sparse))
            {
                sparse++;
                continue;
            }

            if (e.Flags.HasFlag(FsEntryFlags.Corrupt))
            {
                inconsistent++;
                continue;
            }

            IReadOnlyList<FsExtent> extents = fs.GetExtents(e);
            if (e.Flags.HasFlag(FsEntryFlags.Resident) || extents.Any(x => x.Resident))
            {
                resident++;
                continue;
            }

            if (extents.Any(x => x.Compressed))
            {
                compressed++;
                continue;
            }

            if (extents.Any(x => x.IsSparse))
            {
                sparse++;
                continue;
            }

            FsExtent? last = extents.LastOrDefault(x => x.ClusterCount > 0);
            if (last?.VolumeOffset is not long offset)
            {
                continue;
            }

            files++;
            long dataInLast = Math.Max(0, Math.Min(last.Length, e.Size - last.LogicalOffset));
            long allocated = last.ClusterCount * clusterSize;
            if (dataInLast >= allocated)
            {
                continue;
            }

            long slackStart = offset + dataInLast;
            long slackLength = allocated - dataInLast;
            if (!InsideAllocated(fs, allocation, slackStart, slackLength) || metadata.Any(m => m.Offset < slackStart + slackLength && slackStart < m.End))
            {
                inconsistent++;
                continue;
            }

            if (seen.Add(slackStart))
            {
                ranges.Add(new WipeRange(volumeOffset + slackStart, slackLength));
            }
        }

        return new SlackWipePlan(WipeEngine.Normalize(ranges), files, new SlackSkipCounts(compressed, sparse, resident, inconsistent));
    }

    /// <summary>範囲のクラスタがすべて割り当て済みでクラスタの領域の中か。</summary>
    internal static bool InsideAllocated(IFileSystem fs, IAllocationMap? allocation, long volumeOffset, long length)
    {
        if (length <= 0)
        {
            return true;
        }

        if (fs.ClusterAt(volumeOffset) is not long first || fs.ClusterAt(volumeOffset + length - 1) is not long last || last < first)
        {
            return false;
        }

        if (allocation is null)
        {
            return true;
        }

        for (long c = first; c <= last; c++)
        {
            if (!allocation.IsAllocated(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>[start, start + length) からメタデータの範囲を除いた部分。</summary>
    private static IEnumerable<(long Start, long Length)> Subtract(long start, long length, FsRegion[] sortedMetadata)
    {
        long position = start;
        long end = start + length;
        foreach (FsRegion m in sortedMetadata)
        {
            if (m.End <= position || m.Offset >= end)
            {
                continue;
            }

            if (m.Offset > position)
            {
                yield return (position, m.Offset - position);
            }

            position = Math.Max(position, m.End);
            if (position >= end)
            {
                yield break;
            }
        }

        if (position < end)
        {
            yield return (position, end - position);
        }
    }
}
