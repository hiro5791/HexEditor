namespace HexEditor.Core.Selection;

/// <summary>
/// 矩形選択 (EDIT-06 の仕様 1): 表示上の行 <see cref="FirstRow"/>〜<see cref="LastRow"/> と、列 <see cref="FirstColumn"/>〜<see cref="LastColumn"/>
/// (両端を含む) で囲まれた範囲。作ったときの 1 行のバイト数 <see cref="BytesPerRow"/> と行の先頭のずれ <see cref="RowShift"/> (VIEW-20) を持つ。
/// 4 つの数値だけで表すので、行数に関係なく一定の大きさで、100 GB のファイル全体の行にわたる矩形も作れる。
/// </summary>
public readonly record struct RectSelection(long FirstRow, long LastRow, int FirstColumn, int LastColumn, int BytesPerRow, int RowShift = 0)
{
    /// <summary>行数。</summary>
    public long RowCount => LastRow - FirstRow + 1;

    /// <summary>1 行で選ぶバイト数 (列の数)。</summary>
    public int Width => LastColumn - FirstColumn + 1;

    /// <summary>
    /// 2 つの位置を対角とする矩形 (Alt+ドラッグ・Alt+Shift+矢印・範囲を選択の「矩形として選択」。EDIT-06 の仕様 2、EDIT-04 の仕様 5)。
    /// 左右・上下は入れ替えて揃える。
    /// </summary>
    public static RectSelection FromCorners(long a, long b, int bytesPerRow, int rowShift = 0)
    {
        long ra = RowOf(a, bytesPerRow, rowShift), rb = RowOf(b, bytesPerRow, rowShift);
        int ca = ColumnOf(a, bytesPerRow, rowShift), cb = ColumnOf(b, bytesPerRow, rowShift);
        return new RectSelection(Math.Min(ra, rb), Math.Max(ra, rb), Math.Min(ca, cb), Math.Max(ca, cb), bytesPerRow, rowShift);
    }

    /// <summary>行 <paramref name="row"/> の選ぶ範囲 (ファイルの外にはみ出す分は除く。仕様 3)。空なら長さ 0。</summary>
    public ByteRange RowRange(long row, long documentLength)
    {
        long rowStart = row * BytesPerRow - RowShift;
        long start = Math.Max(0, rowStart + FirstColumn);
        long end = Math.Min(documentLength, rowStart + LastColumn + 1);
        return ByteRange.FromBounds(start, Math.Max(start, end));
    }

    /// <summary>選んでいる全体のバイト数 (ステータスバーの「計 N バイト」。仕様 4)。行数に関係なく一定時間で求める。</summary>
    public long ByteCount(long documentLength)
    {
        // 全部の列がファイルの中にある行 [full0, full1] は Width バイトずつ。それ以外 (先頭のずれで欠ける行 0 と末尾の行) は個別に数える。
        long full0 = RowShift - FirstColumn > 0 ? 1 : 0;
        long numerator = documentLength + RowShift - LastColumn - 1;
        long full1 = numerator >= 0 ? numerator / BytesPerRow : -1;
        long from = Math.Max(FirstRow, full0), to = Math.Min(LastRow, full1);
        long total = from <= to ? (to - from + 1) * Width : 0;

        // 一部だけがファイルの中にある行は、先頭のずれで欠ける行 0 と、末尾を含む行 full1 + 1 だけ (どちらも [full0, full1] の外)。
        long partialEnd = full1 + 1;
        if (full0 == 1 && partialEnd != 0 && FirstRow == 0)
        {
            total += RowRange(0, documentLength).Length;
        }

        if (partialEnd >= FirstRow && partialEnd <= LastRow)
        {
            total += RowRange(partialEnd, documentLength).Length;
        }

        return total;
    }

    /// <summary>空でない行の範囲 (上から順)。行ごとの処理の上限 (EDIT-17 の仕様 6) は呼び出し側で確かめる。</summary>
    public IEnumerable<ByteRange> Ranges(long documentLength)
    {
        long lastRow = Math.Min(LastRow, RowOf(Math.Max(0, documentLength - 1), BytesPerRow, RowShift));
        for (long row = FirstRow; row <= lastRow; row++)
        {
            ByteRange r = RowRange(row, documentLength);
            if (r.Length > 0)
            {
                yield return r;
            }
        }
    }

    /// <summary>[start, start + length) と重なる行の範囲 (描画では見えている行だけ尋ねる)。</summary>
    public IEnumerable<ByteRange> RangesIn(long start, long length, long documentLength)
    {
        if (length <= 0)
        {
            yield break;
        }

        long first = Math.Max(FirstRow, RowOf(start, BytesPerRow, RowShift));
        long last = Math.Min(LastRow, RowOf(start + length - 1, BytesPerRow, RowShift));
        for (long row = first; row <= last; row++)
        {
            ByteRange r = RowRange(row, documentLength);
            if (r.Length > 0)
            {
                yield return r;
            }
        }
    }

    /// <summary>オフセットのバイトが矩形に含まれるか。</summary>
    public bool Contains(long offset, long documentLength)
    {
        if (offset < 0 || offset >= documentLength)
        {
            return false;
        }

        long row = RowOf(offset, BytesPerRow, RowShift);
        int column = ColumnOf(offset, BytesPerRow, RowShift);
        return row >= FirstRow && row <= LastRow && column >= FirstColumn && column <= LastColumn;
    }

    /// <summary>行の先頭 (左端の列) のオフセット (矩形の入力・挿入の位置。EDIT-17 の仕様 4・5)。ファイルの先頭より前なら 0。</summary>
    public long RowLeft(long row) => Math.Max(0, row * BytesPerRow - RowShift + FirstColumn);

    private static long RowOf(long offset, int bytesPerRow, int rowShift) => (long)(((ulong)Math.Max(0, offset) + (ulong)rowShift) / (ulong)bytesPerRow);

    private static int ColumnOf(long offset, int bytesPerRow, int rowShift) => (int)(((ulong)Math.Max(0, offset) + (ulong)rowShift) % (ulong)bytesPerRow);
}
