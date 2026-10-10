using HexEditor.Core.Sources;

namespace HexEditor.Core.Compare;

/// <summary>領域ごとの比較で、片方にしかない領域 (ANA-09 の仕様 5)。</summary>
public enum RegionChange
{
    /// <summary>右にだけある領域 (領域の追加)。</summary>
    Added,

    /// <summary>左にだけある領域 (領域の削除)。</summary>
    Removed,
}

/// <summary>
/// プロセスメモリ・プロセスのスナップショットの比較 (ANA-09 の仕様 5・6)。読める領域のうち、ベースアドレスが同じ領域どうしを単純比較
/// (ANA-02) で比べる。片方にしかない領域は、領域全体を 1 つの差分 (左のみ = 領域の削除、右のみ = 領域の追加) にする。サイズが変わった
/// 領域は短い方の長さまで比べ、残りを「左のみ」「右のみ」にする。比べるのは領域だけなので、アドレス空間全体 (128 TB) は読まない。
/// 位置はアドレス (データ上の位置 = 仮想アドレス)。
/// </summary>
public static class RegionComparer
{
    /// <summary>左右とも領域の一覧を持つか (比較の範囲は指定できない: 全体を比べる場合だけ)。</summary>
    public static bool CanCompare(CompareResult result) =>
        CompareData.RegionsOf(result.Left.Data) is not null && CompareData.RegionsOf(result.Right.Data) is not null
        && result.Left.Start == 0 && result.Right.Start == 0;

    internal static void Run(CompareOptions options, CompareResult result, CancellationToken cancellationToken, Action<long>? progress)
    {
        IReadOnlyList<SourceRegion> left = Readable(CompareData.RegionsOf(result.Left.Data)!);
        IReadOnlyList<SourceRegion> right = Readable(CompareData.RegionsOf(result.Right.Data)!);
        var rightByBase = right.ToDictionary(r => r.Offset);
        var leftByBase = left.ToDictionary(r => r.Offset);
        result.ByRegion = true;
        var sink = new DiffSink(result, options.MergeGap);
        long compared = 0;
        long done = 0;
        try
        {
            foreach (long baseAddress in left.Select(r => r.Offset).Union(right.Select(r => r.Offset)).Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceRegion? l = leftByBase.GetValueOrDefault(baseAddress);
                SourceRegion? r = rightByBase.GetValueOrDefault(baseAddress);
                if (l is not null && r is not null)
                {
                    var subLeft = new CompareRange(result.Left.Data, l.Offset, l.Length);
                    var subRight = new CompareRange(result.Right.Data, r.Offset, r.Length);
                    long before = done;
                    SimpleComparer.RunRange(subLeft, subRight, options.Unit, sink, result, cancellationToken,
                        p => progress?.Invoke(before + p), (lp, _) => result.ReportPosition(l.Offset + lp, r.Offset + lp));
                    compared += Math.Max(l.Length, r.Length);
                    done += Math.Max(l.Length, r.Length);
                }
                else if (l is not null)
                {
                    // 左にだけある領域 (解放された): 領域の削除。
                    sink.Add(new DiffRange(DiffKind.Deleted, l.Offset, l.Length, l.Offset, 0), l.Length, mergeable: false);
                    compared += l.Length;
                }
                else if (r is not null)
                {
                    // 右にだけある領域 (確保された): 領域の追加。
                    sink.Add(new DiffRange(DiffKind.Inserted, r.Offset, 0, r.Offset, r.Length), r.Length, mergeable: false);
                    compared += r.Length;
                }

                progress?.Invoke(done);
            }

            result.ComparedOverride = compared;
            result.ReportPosition(result.Left.Length, result.Right.Length);
        }
        finally
        {
            sink.Flush();
        }
    }

    private static List<SourceRegion> Readable(IReadOnlyList<SourceRegion> regions) =>
        [.. regions.Where(r => r.Access == RegionAccess.Readable && r.Length > 0)];

    /// <summary>
    /// 差分が、片方にしかない領域全体を表すか (差分の一覧の「領域の追加」「領域の削除」。ANA-09 の仕様 5)。領域ごとの比較でなければ null。
    /// </summary>
    public static RegionChange? ChangeOf(CompareResult result, DiffRange diff)
    {
        if (!result.ByRegion || CompareData.RegionsOf(result.Left.Data) is not { } left || CompareData.RegionsOf(result.Right.Data) is not { } right)
        {
            return null;
        }

        if (diff.Kind == DiffKind.Inserted && diff.LeftLength == 0
            && RegionMaps.At(right, diff.RightOffset) is { Access: RegionAccess.Readable } added && added.Offset == diff.RightOffset
            && added.Length == diff.RightLength && !StartsReadable(left, diff.RightOffset))
        {
            return RegionChange.Added;
        }

        if (diff.Kind == DiffKind.Deleted && diff.RightLength == 0
            && RegionMaps.At(left, diff.LeftOffset) is { Access: RegionAccess.Readable } removed && removed.Offset == diff.LeftOffset
            && removed.Length == diff.LeftLength && !StartsReadable(right, diff.LeftOffset))
        {
            return RegionChange.Removed;
        }

        return null;
    }

    private static bool StartsReadable(IReadOnlyList<SourceRegion> regions, long offset) =>
        RegionMaps.At(regions, offset) is { Access: RegionAccess.Readable } r && r.Offset == offset;

    /// <summary>
    /// アドレスの領域名 (ANA-09 の仕様 7): モジュールの領域は「モジュール名+0x…」(領域の先頭からの位置)、それ以外は「private 0x…」(領域の先頭)。
    /// 領域がなければ空。
    /// </summary>
    public static string RegionName(IReadOnlyList<SourceRegion> regions, long address, IReadOnlyList<Processes.ProcessModule>? modules = null)
    {
        if (RegionMaps.At(regions, address) is not { Access: RegionAccess.Readable } region)
        {
            return string.Empty;
        }

        // モジュールの中なら、モジュールの先頭からの位置 (セクションごとの領域でも同じ基準になる)。
        if (modules?.FirstOrDefault(m => address >= m.BaseAddress && address < m.End) is { } m)
        {
            return $"{m.Name}+0x{address - m.BaseAddress:X}";
        }

        return region.Label is { Length: > 0 } module
            ? $"{module}+0x{address - region.Offset:X}"
            : $"private 0x{region.Offset:X}";
    }

    /// <summary>データのモジュールの一覧 (プロセスメモリ・スナップショット)。なければ null。</summary>
    public static IReadOnlyList<Processes.ProcessModule>? ModulesOf(ICompareData data) => CompareData.SourceOf(data) switch
    {
        Processes.SnapshotByteSource s => s.Modules,
        Processes.ProcessMemoryByteSource p => p.Modules,
        _ => null,
    };
}
