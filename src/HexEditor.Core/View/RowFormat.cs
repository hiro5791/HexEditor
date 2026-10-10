namespace HexEditor.Core.View;

/// <summary>
/// 1 行の文字列の中での各列の位置 (内容の領域の左端からの文字数)。グループ化 (VIEW-09)、中央区切り (VIEW-09 の仕様 3)、
/// 列の表示・非表示 (VIEW-16)、セルの表示形式 (VIEW-10)、グループ内の逆順表示 (VIEW-11)、複数のテキスト列 (VIEW-24) で決まる。
/// Hex 列とテキスト列の間、テキスト列どうしの間は 2 文字あける (VIEW-01 の仕様 1)。
/// <para>
/// Hex 列は「セル」の並び。Hex 形式ではセルは 1 バイト (2 文字) で、グループの間に 1 文字あける。ほかの形式ではセルは単位のバイト数
/// (VIEW-10 の仕様 1) で、セルごとに 1 文字あける。<paramref name="Reverse"/> (Hex 形式のみ) はグループの中のバイトを逆順の位置に置く。
/// 位置の計算で <c>valid</c> を受け取るものは、その行でデータのあるバイト数 (末尾の行だけ 1 行のバイト数より少ない)。末尾の不完全な
/// グループは逆順にせず (VIEW-11 の仕様 7)、単位に満たない端数のバイトは 1 バイトずつ Hex の 2 文字で置く (VIEW-10 の仕様 5)。
/// </para>
/// </summary>
public readonly record struct RowFormat(int BytesPerRow, int GroupSize = 1, bool MiddleSeparator = false, bool ShowHex = true,
    bool ShowText = true, CellFormat CellFormat = CellFormat.Hex, bool Reverse = false, int TextColumns = 1, bool BigEndian = false)
{
    /// <summary>Hex 列の 1 バイトの文字数 (VIEW-10 の Hex 形式)。</summary>
    public const int HexCellChars = 2;

    /// <summary>列と列の間の空白の文字数。</summary>
    public const int ColumnGap = 2;

    public static RowFormat For(ViewSettings settings, int bytesPerRow) =>
        new(bytesPerRow, Math.Max(1, settings.GroupSize), settings.EffectiveMiddleSeparator(bytesPerRow), settings.ShowHexColumn,
            settings.ShowTextColumn || !settings.ShowHexColumn, settings.CellFormat,
            settings.ReverseGroups && settings.CellFormat == CellFormat.Hex && settings.GroupSize > 1,
            Math.Clamp(settings.TextColumnCount, 1, ViewSettings.MaxTextColumns), settings.BigEndian);

    /// <summary>Hex 形式 (1 バイト = 1 セル = 2 文字)。</summary>
    public bool IsHexBytes => CellFormat == CellFormat.Hex;

    /// <summary>セルの単位のバイト数 (VIEW-10)。</summary>
    public int Unit => CellFormatter.Unit(CellFormat);

    /// <summary>1 セルの文字数。</summary>
    public int CellChars => CellFormatter.Chars(CellFormat);

    /// <summary>行の中のセルの数 (端数の単位も 1 セルと数える)。</summary>
    public int CellsPerRow => (BytesPerRow + Unit - 1) / Unit;

    /// <summary>グループ (Hex 形式はグループ化のバイト数、ほかの形式は 1 セル)。</summary>
    private int Group => IsHexBytes ? Math.Max(1, GroupSize) : Unit;

    /// <summary>セル k (表示の並び順) の先頭の文字位置。グループの間に 1 文字、中央区切りなら 8 バイトごとにもう 1 文字。</summary>
    public int CellStart(int k)
    {
        int groupCells = Math.Max(1, Group / Unit);
        return CellChars * k + k / groupCells + (MiddleSeparator ? k * Unit / 8 : 0);
    }

    /// <summary>
    /// c 番目のバイトの Hex 列の先頭の文字位置 (行のバイトがすべてある場合)。2 バイト以上の Hex 以外の形式では、そのバイトを含むセルの先頭。
    /// </summary>
    public int HexIndex(int c) => ByteSpan(c, BytesPerRow).Start;

    /// <summary>c 番目のバイトの Hex 列の文字の範囲 (先頭と文字数)。</summary>
    public (int Start, int Length) ByteSpan(int c, int valid)
    {
        if (IsHexBytes)
        {
            return (CellStart(SlotOf(c, valid)), HexCellChars);
        }

        int unit = Unit;
        int k = c / unit;
        int i = c % unit;
        int start = CellStart(k);
        if (!IsCompleteCell(k, valid))
        {
            // 単位に満たない端数のバイト: 1 バイトずつ Hex の 2 文字 (VIEW-10 の仕様 5)。
            return (start + i * HexCellChars, HexCellChars);
        }

        if (CellFormatter.IsHexLike(CellFormat))
        {
            // 16 進の値ではバイトごとに 2 文字の位置がある (リトルエンディアンでは上位のバイトが左)。
            int at = BigEndian ? i : unit - 1 - i;
            return (start + at * HexCellChars, HexCellChars);
        }

        return (start, CellChars);
    }

    /// <summary>セル k がすべてのバイトを持つか (単位に満たない端数でないか)。</summary>
    public bool IsCompleteCell(int k, int valid) => (k + 1) * Unit <= Math.Min(valid, BytesPerRow);

    /// <summary>
    /// [c0, c1] のバイトを Hex 列で強調する文字の範囲を <paramref name="output"/> に書き、書いた数を返す。並びが続く範囲はまとめ、
    /// グループの間・中央区切りの空白 (2 文字まで) もつなげる。逆順表示では範囲が分かれることがある (最大 8)。
    /// </summary>
    public int HexSpans(int c0, int c1, int valid, Span<(int Start, int Length)> output)
    {
        if (c0 > c1 || output.IsEmpty)
        {
            return 0;
        }

        if (IsHexBytes && !Reverse)
        {
            int s = CellStart(c0);
            output[0] = (s, CellStart(c1) + HexCellChars - s);
            return 1;
        }

        // 表示の位置で並べ替えてつなげる。
        Span<(int Start, int End)> spans = c1 - c0 + 1 <= 256 ? stackalloc (int, int)[c1 - c0 + 1] : new (int, int)[c1 - c0 + 1];
        for (int c = c0; c <= c1; c++)
        {
            (int start, int length) = ByteSpan(c, valid);
            spans[c - c0] = (start, start + length);
        }

        spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        int n = 0;
        (int curStart, int curEnd) = spans[0];
        for (int i = 1; i < spans.Length; i++)
        {
            (int s, int e) = spans[i];
            if (s - curEnd <= 2)
            {
                curEnd = Math.Max(curEnd, e);
                continue;
            }

            if (n < output.Length - 1)
            {
                output[n++] = (curStart, curEnd - curStart);
                (curStart, curEnd) = (s, e);
            }
            else
            {
                curEnd = Math.Max(curEnd, e);
            }
        }

        output[n++] = (curStart, curEnd - curStart);
        return n;
    }

    /// <summary>Hex 形式で、c 番目のバイトを置く表示の位置 (セルの番号)。逆順表示ではグループの中で逆に置く (VIEW-11 の仕様 4・7)。</summary>
    public int SlotOf(int c, int valid)
    {
        if (!Reverse || !IsHexBytes)
        {
            return c;
        }

        int g = Math.Max(1, GroupSize);
        int groupStart = c / g * g;
        int size = Math.Min(g, BytesPerRow - groupStart);
        if (groupStart + size > valid)
        {
            // 末尾の不完全なグループは逆順にしない。
            return c;
        }

        return groupStart + (size - 1 - (c - groupStart));
    }

    /// <summary>Hex 形式の表示の位置 <paramref name="slot"/> にあるバイト (<see cref="SlotOf"/> の逆。逆順は自分自身の逆写像)。</summary>
    public int ByteOfSlot(int slot, int valid) => SlotOf(slot, valid);

    /// <summary>この位置のグループが末尾で不完全なため、逆順にせず点線の枠を付けるか (VIEW-11 の仕様 7)。</summary>
    public bool IsIncompleteReversedGroup(int c, int valid)
    {
        if (!Reverse || !IsHexBytes || c >= valid)
        {
            return false;
        }

        int g = Math.Max(1, GroupSize);
        int groupStart = c / g * g;
        return groupStart + Math.Min(g, BytesPerRow - groupStart) > valid;
    }

    /// <summary>Hex 列の幅 (文字数)。表示しないときは 0。</summary>
    public int HexWidth => ShowHex ? CellStart(CellsPerRow - 1) + CellChars : 0;

    /// <summary>テキスト列 1 の先頭の文字位置。</summary>
    public int TextStart => TextColumnStart(0);

    /// <summary>テキスト列 <paramref name="column"/> (0 始まり) の先頭の文字位置。</summary>
    public int TextColumnStart(int column) => (ShowHex ? HexWidth + ColumnGap : 0) + column * (BytesPerRow + ColumnGap);

    /// <summary>テキスト列 1 の c 番目の文字位置 (テキスト列はグループ化の影響を受けない。VIEW-09 の仕様 6)。</summary>
    public int TextIndex(int c) => TextStart + c;

    /// <summary>テキスト列 <paramref name="column"/> の c 番目の文字位置。</summary>
    public int TextIndex(int column, int c) => TextColumnStart(column) + c;

    /// <summary>表示しているテキスト列の数 (テキスト列を表示しないときは 0)。</summary>
    public int ShownTextColumns => ShowText ? Math.Max(1, TextColumns) : 0;

    /// <summary>行全体の文字数。</summary>
    public int LineLength => ShowText ? TextColumnStart(ShownTextColumns - 1) + BytesPerRow : HexWidth;

    /// <summary>c 番目のバイトがグループの先頭か。</summary>
    public bool IsGroupStart(int c) => c % Group == 0;

    /// <summary>c 番目のバイトのグループの番号 (交互色の判定。VIEW-14 の仕様 2・3)。</summary>
    public int GroupOf(int c) => c / Group;

    /// <summary>グループの文字数。</summary>
    public int GroupWidth(int c)
    {
        int first = c / Group * Group;
        int last = Math.Min(BytesPerRow - 1, first + Group - 1);
        int s = CellStart(first / Unit);
        return CellStart(last / Unit) + CellChars - s;
    }

    /// <summary>
    /// Hex 列の文字位置から、その位置を含むバイト (または左隣のバイト) の行内の位置。行のバイトがすべてある場合の位置で求める。
    /// </summary>
    public int ByteAtHexIndex(int index) => ByteAtHexIndex(index, BytesPerRow);

    /// <summary>Hex 列の文字位置を含むセル (または左隣のセル) の表示の位置。</summary>
    public int CellAt(int index)
    {
        int lo = 0;
        int hi = CellsPerRow - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (CellStart(mid) <= index)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>
    /// マウスの位置 (Hex 列の文字位置) のバイト。セルの前の空白は右のセルに含める (VIEW-25 の仕様 6)。
    /// </summary>
    public int ByteAtPointer(int index, int valid)
    {
        int k = CellAt(index + 1);
        return ByteAtHexIndex(Math.Max(index, CellStart(k)), valid);
    }

    /// <summary>Hex 列の文字位置から、その位置を含むバイト (または左隣のバイト) の行内の位置。</summary>
    public int ByteAtHexIndex(int index, int valid)
    {
        int lo = CellAt(index);

        if (IsHexBytes)
        {
            return Math.Min(BytesPerRow - 1, ByteOfSlot(lo, valid));
        }

        int unit = Unit;
        int first = lo * unit;
        int within = Math.Clamp((index - CellStart(lo)) / HexCellChars, 0, unit - 1);
        if (!IsCompleteCell(lo, valid))
        {
            return Math.Min(BytesPerRow - 1, first + within);
        }

        if (CellFormatter.IsHexLike(CellFormat))
        {
            return Math.Min(BytesPerRow - 1, first + (BigEndian ? within : unit - 1 - within));
        }

        return first;
    }
}
