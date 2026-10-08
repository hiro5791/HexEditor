namespace HexEditor.Core.View;

/// <summary>
/// 1 行の文字列の中での各列の位置 (内容の領域の左端からの文字数)。グループ化 (VIEW-09)、中央区切り (VIEW-09 の仕様 3)、
/// 列の表示・非表示 (VIEW-16) で決まる。Hex 列とテキスト列の間は 2 文字あける (VIEW-01 の仕様 1)。
/// </summary>
public readonly record struct RowFormat(int BytesPerRow, int GroupSize = 1, bool MiddleSeparator = false, bool ShowHex = true,
    bool ShowText = true)
{
    /// <summary>Hex 列の 1 セルの文字数 (VIEW-10 の Hex 形式)。</summary>
    public const int HexCellChars = 2;

    /// <summary>列と列の間の空白の文字数。</summary>
    public const int ColumnGap = 2;

    public static RowFormat For(ViewSettings settings, int bytesPerRow) =>
        new(bytesPerRow, Math.Max(1, settings.GroupSize), settings.EffectiveMiddleSeparator(bytesPerRow), settings.ShowHexColumn,
            settings.ShowTextColumn || !settings.ShowHexColumn);

    private int Group => Math.Max(1, GroupSize);

    /// <summary>c 番目のバイトの Hex 列の先頭の文字位置。グループの間に 1 文字、中央区切りなら 8 バイトごとにもう 1 文字。</summary>
    public int HexIndex(int c) => HexCellChars * c + c / Group + (MiddleSeparator ? c / 8 : 0);

    /// <summary>Hex 列の幅 (文字数)。表示しないときは 0。</summary>
    public int HexWidth => ShowHex ? HexIndex(BytesPerRow - 1) + HexCellChars : 0;

    /// <summary>テキスト列の先頭の文字位置。</summary>
    public int TextStart => ShowHex ? HexWidth + ColumnGap : 0;

    /// <summary>テキスト列の c 番目の文字位置 (テキスト列はグループ化の影響を受けない。VIEW-09 の仕様 6)。</summary>
    public int TextIndex(int c) => TextStart + c;

    /// <summary>行全体の文字数。</summary>
    public int LineLength => ShowText ? TextIndex(BytesPerRow) : HexWidth;

    /// <summary>c 番目のバイトがグループの先頭か。</summary>
    public bool IsGroupStart(int c) => c % Group == 0;

    /// <summary>c 番目のバイトのグループの番号 (交互色の判定。VIEW-14 の仕様 2・3)。</summary>
    public int GroupOf(int c) => c / Group;

    /// <summary>グループの文字数。</summary>
    public int GroupWidth(int c)
    {
        int first = c / Group * Group;
        int last = Math.Min(BytesPerRow - 1, first + Group - 1);
        return HexIndex(last) + HexCellChars - HexIndex(first);
    }

    /// <summary>Hex 列の文字位置から、その位置を含むバイト (または左隣のバイト) の行内の位置。</summary>
    public int ByteAtHexIndex(int index)
    {
        int lo = 0;
        int hi = BytesPerRow - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (HexIndex(mid) <= index)
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
}
