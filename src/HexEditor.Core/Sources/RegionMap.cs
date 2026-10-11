namespace HexEditor.Core.Sources;

/// <summary>領域の読み取りの可否 (ENG-01 の仕様 7、ENG-33 の仕様 7)。</summary>
public enum RegionAccess
{
    /// <summary>読める。</summary>
    Readable,

    /// <summary>未割り当て (空き・予約のみ)。</summary>
    Unallocated,

    /// <summary>アクセス不可 (アクセス不可の保護属性・ガードページ)。</summary>
    NoAccess,
}

/// <summary>
/// 領域マップの 1 項目 (ENG-01 の仕様 7)。<see cref="Offset"/> はデータソース上の位置 (アドレスではない)。
/// <see cref="Label"/> はモジュール名・マップしたファイル名などの付加情報。
/// </summary>
public sealed record SourceRegion(long Offset, long Length, RegionAccess Access, string? Label = null)
{
    public long End => Offset + Length;
}

/// <summary>
/// 読めない範囲を持つデータソース (<see cref="SourceCapabilities.HasGaps"/>) の領域マップ (ENG-01 の仕様 7)。
/// 表示 (02) は「未割り当て」「アクセス不可」を読み込みエラーと区別して示し、移動 (ENG-33 の仕様 5) は読める領域を飛び移る。
/// </summary>
public interface IRegionMapSource
{
    /// <summary>今の領域の一覧 (オフセットの昇順。重ならない)。読める範囲・読めない範囲の両方を含む。</summary>
    IReadOnlyList<SourceRegion> Regions { get; }

    /// <summary>領域の一覧が変わった (再取得した)。</summary>
    event EventHandler? RegionsChanged;
}

/// <summary>領域マップの探索 (ENG-33 の仕様 4・5)。</summary>
public static class RegionMaps
{
    /// <summary><paramref name="offset"/> を含む領域。なければ null。</summary>
    public static SourceRegion? At(IReadOnlyList<SourceRegion> regions, long offset)
    {
        int index = IndexAt(regions, offset);
        return index >= 0 ? regions[index] : null;
    }

    /// <summary><paramref name="offset"/> を含む領域の番号。なければ −1。二分探索。</summary>
    public static int IndexAt(IReadOnlyList<SourceRegion> regions, long offset)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            SourceRegion r = regions[mid];
            if (offset < r.Offset)
            {
                hi = mid - 1;
            }
            else if (offset >= r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>
    /// 次の読める領域の先頭 (ENG-33 の仕様 5)。<paramref name="offset"/> より後ろで始まる、読める領域のうち最初のもの。
    /// 隣り合う読める領域は別の領域として数える。なければ null。
    /// </summary>
    public static long? NextReadable(IReadOnlyList<SourceRegion> regions, long offset)
    {
        int lo = 0, hi = regions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (regions[mid].Offset <= offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < regions.Count; i++)
        {
            if (regions[i].Access == RegionAccess.Readable && regions[i].Length > 0)
            {
                return regions[i].Offset;
            }
        }

        return null;
    }

    /// <summary>
    /// 前の読める領域の先頭 (ENG-33 の仕様 5)。<paramref name="offset"/> が読める領域の途中なら、その領域の先頭。なければ null。
    /// </summary>
    public static long? PreviousReadable(IReadOnlyList<SourceRegion> regions, long offset)
    {
        int lo = 0, hi = regions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (regions[mid].Offset < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo - 1; i >= 0; i--)
        {
            if (regions[i].Access == RegionAccess.Readable && regions[i].Length > 0)
            {
                return regions[i].Offset;
            }
        }

        return null;
    }
}
