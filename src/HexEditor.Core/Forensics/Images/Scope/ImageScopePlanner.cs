using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Scope;

/// <summary>スパース・スケルトンの範囲の計算の結果 (FOR-23 の仕様 1)。</summary>
public sealed record ImageScopePlan
{
    /// <summary>含める範囲 (入力元の上のバイトの範囲。昇順で重ならない)。</summary>
    public required IReadOnlyList<(long Offset, long Length)> Included { get; init; }

    /// <summary>ファイルシステムを解析できず、全体を含めたパーティション (「このパーティションは全体を含めます」。FOR-23 の「エラー」)。</summary>
    public IReadOnlyList<ImagePartition> WholePartitions { get; init; } = [];

    /// <summary>解析したパーティションとファイルシステムの名前。</summary>
    public IReadOnlyList<(ImagePartition Partition, string FileSystem)> Analyzed { get; init; } = [];

    public long IncludedBytes => Included.Sum(r => r.Length);
}

/// <summary>
/// スパース・スケルトンイメージに含める範囲を求める (FOR-23 の仕様 1)。範囲の計算 (ビットマップの読み込み) も長時間処理に含める。
/// <list type="bullet">
/// <item>スパース: ファイルシステムを解析できるパーティションは割り当て済みの領域だけ、それ以外 (解析できないパーティション、パーティションの外) は全体。</item>
/// <item>スケルトン: パーティション表と、ファイルシステムのメタデータだけ。解析できないパーティションは全体。加えて利用者が選んだ範囲
/// (ファイルの内容、ブックマーク・マルチ選択の範囲)。</item>
/// </list>
/// </summary>
public static class ImageScopePlanner
{
    public static ImageScopePlan Plan(IByteSource disk, int sectorSize, AcquisitionScope scope, IEnumerable<(long Offset, long Length)>? extra = null,
        CancellationToken cancellationToken = default)
    {
        if (scope == AcquisitionScope.Full)
        {
            return new ImageScopePlan { Included = [(0, disk.Length)] };
        }

        ImagePartitionTable table = ImagePartitions.Read(disk, sectorSize);
        IReadOnlyList<ImagePartition> partitions = table.Partitions.Count > 0
            ? table.Partitions
            : [new ImagePartition(0, disk.Length, "none", "volume")];
        var included = new List<(long, long)>();
        var whole = new List<ImagePartition>();
        var analyzed = new List<(ImagePartition, string)>();
        if (scope == AcquisitionScope.Skeleton)
        {
            included.AddRange(table.TableRanges);
        }
        else
        {
            // スパース: パーティションの外は全体を含める。
            long at = 0;
            foreach (ImagePartition p in partitions.OrderBy(p => p.Offset))
            {
                if (p.Offset > at)
                {
                    included.Add((at, p.Offset - at));
                }

                at = Math.Max(at, p.Offset + p.Length);
            }

            if (at < disk.Length)
            {
                included.Add((at, disk.Length - at));
            }
        }

        foreach (ImagePartition p in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volume = new SubRangeSource(disk, p.Offset, p.Length);
            FileSystemRegions? regions = FileSystemRegionProviders.Analyze(volume, cancellationToken);
            if (regions is null)
            {
                whole.Add(p);
                included.Add((p.Offset, p.Length));
                continue;
            }

            analyzed.Add((p, regions.FileSystem));
            IReadOnlyList<(long Offset, long Length)> ranges = scope == AcquisitionScope.Sparse ? regions.Allocated : regions.Metadata;
            included.AddRange(ranges.Select(r => (p.Offset + r.Offset, Math.Min(r.Length, p.Length - r.Offset))).Where(r => r.Item2 > 0));
        }

        if (extra is not null)
        {
            included.AddRange(extra);
        }

        var clipped = included.Select(r => (Math.Max(0, r.Item1), Math.Min(disk.Length, r.Item1 + r.Item2) - Math.Max(0, r.Item1)))
            .Where(r => r.Item2 > 0);
        return new ImageScopePlan
        {
            Included = FileSystemRegionProviders.Merge(clipped.Select(r => (r.Item1 / sectorSize * sectorSize,
                (r.Item1 + r.Item2 + sectorSize - 1) / sectorSize * sectorSize - r.Item1 / sectorSize * sectorSize))).Select(r => (r.Offset, Math.Min(r.Length, disk.Length - r.Offset))).ToList(),
            WholePartitions = whole,
            Analyzed = analyzed,
        };
    }
}
