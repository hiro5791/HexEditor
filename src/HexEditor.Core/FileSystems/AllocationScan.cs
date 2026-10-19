namespace HexEditor.Core.FileSystems;

/// <summary>
/// 割り当ての情報から空きの連続区間を求める共通の処理 (HFS+ の割り当てファイル、UDF の空き領域のビットマップ、APFS の空き領域マネージャ)。
/// 8 クラスタがまとめて空き・使用中のバイトは 1 回で進めるので、大きなボリュームでも 1 クラスタずつ調べない。
/// </summary>
internal static class AllocationScan
{
    /// <param name="isAllocated">1 クラスタが使用中か。</param>
    /// <param name="wholeByte">8 の倍数の位置から 8 クラスタがすべて空きなら 0、すべて使用中なら 1、混ざっている・分からなければ -1。</param>
    public static IEnumerable<(long Start, long Count)> FreeRuns(long start, long end, Func<long, bool> isAllocated, Func<long, int> wholeByte,
        CancellationToken cancellationToken)
    {
        long runStart = -1;
        long c = start;
        while (c < end)
        {
            if ((c & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int whole = (c & 7) == 0 && c + 8 <= end ? wholeByte(c) : -1;
            bool used;
            int step = 1;
            if (whole >= 0)
            {
                used = whole == 1;
                step = 8;
            }
            else
            {
                used = isAllocated(c);
            }

            if (!used && runStart < 0)
            {
                runStart = c;
            }
            else if (used && runStart >= 0)
            {
                yield return (runStart, c - runStart);
                runStart = -1;
            }

            c += step;
        }

        if (runStart >= 0)
        {
            yield return (runStart, end - runStart);
        }
    }
}
