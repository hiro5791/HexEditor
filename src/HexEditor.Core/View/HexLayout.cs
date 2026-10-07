namespace HexEditor.Core.View;

/// <summary>
/// 行とオフセットの対応 (VIEW-01 の仕様 5〜7)。行番号・オフセットはすべて <see cref="long"/> で計算する。
/// </summary>
public readonly record struct HexLayout(int BytesPerRow, long Length, bool CanResize)
{
    /// <summary>カーソルを置ける最大のオフセット (VIEW-25 の仕様 1)。長さを変えられる場合は末尾の次の位置 L。</summary>
    public long MaxCursor => CanResize ? Length : Math.Max(0, Length - 1);

    /// <summary>全行数。末尾の次の位置を含む行まで。長さ 0 でも 1 行。</summary>
    public long TotalRows => MaxCursor / BytesPerRow + 1;

    public long RowOf(long offset) => offset / BytesPerRow;

    public long RowStart(long row) => row * BytesPerRow;

    public int ColumnOf(long offset) => (int)(offset % BytesPerRow);

    /// <summary>一番上に表示できる行の最大値 M (VIEW-02 の仕様 1)。</summary>
    public long MaxTopRow(int visibleRows) => Math.Max(0, TotalRows - Math.Max(1, visibleRows));
}

/// <summary>
/// 何十億行あっても縦スクロールバーで全体を移動できるようにする換算 (VIEW-02 の仕様 2)。
/// スクロールバーの内部値は 0〜S の整数で、S = min(M, 1,000,000)。
/// </summary>
public static class ScrollMapping
{
    public const long MaxScrollValue = 1_000_000;

    /// <summary>スクロールバーの内部値の最大値 S。</summary>
    public static long Scale(long maxTopRow) => Math.Min(maxTopRow, MaxScrollValue);

    /// <summary>内部値から一番上の行番号へ。</summary>
    public static long ToRow(long value, long maxTopRow)
    {
        long s = Scale(maxTopRow);
        if (s == 0)
        {
            return 0;
        }

        value = Math.Clamp(value, 0, s);
        if (maxTopRow <= MaxScrollValue)
        {
            return value;
        }

        return (long)RoundDiv((Int128)value * maxTopRow, s);
    }

    /// <summary>一番上の行番号から内部値へ (最も近い値)。</summary>
    public static long ToValue(long row, long maxTopRow)
    {
        long s = Scale(maxTopRow);
        if (s == 0)
        {
            return 0;
        }

        row = Math.Clamp(row, 0, maxTopRow);
        if (maxTopRow <= MaxScrollValue)
        {
            return row;
        }

        return (long)RoundDiv((Int128)row * s, maxTopRow);
    }

    private static Int128 RoundDiv(Int128 numerator, Int128 denominator) => (numerator + denominator / 2) / denominator;
}
