namespace HexEditor.Core.View;

/// <summary>区切りの長さの指定の誤り (VIEW-33 の仕様 2 と「エラー」)。</summary>
public enum SeparatorError
{
    /// <summary>1 行分未満、または 2^31 − 1 を超える。</summary>
    OutOfRange,

    /// <summary>1 行のバイト数の倍数でない。</summary>
    NotMultipleOfRow,

    /// <summary>行の先頭のずれ (VIEW-20) があって区切りの位置が行の境目と合わない。</summary>
    RowShift,
}

/// <summary>
/// 一定の長さの区切り (セクタ VIEW-32、区切り線とページ表示 VIEW-33)。区切りの番号は 0 始まり (オフセット ÷ 長さ、切り捨て)、
/// 区切りの数は ⌈長さ ÷ 区切りの長さ⌉ (末尾の端数も 1 つ)。末尾の次の位置は最後の区切りに属する (VIEW-32 の仕様 2)。
/// 区切りの位置は計算で求め、一覧は作らない (「巨大ファイル」)。
/// </summary>
public readonly record struct SectionLayout(long SectionLength, long DocumentLength)
{
    /// <summary>ページの長さ (VIEW-33 の仕様 1)。</summary>
    public const int PageSize = 4096;

    /// <summary>表示設定の区切り線の長さ。区切り線がなければ 0。</summary>
    public static long LengthFor(ViewSettings view, int sectorSize) => view.Separator switch
    {
        SeparatorKind.Sector => Math.Max(1, sectorSize),
        SeparatorKind.Page => PageSize,
        SeparatorKind.Custom => view.SeparatorLength,
        _ => 0,
    };

    /// <summary>
    /// 区切りの長さを選べるか (VIEW-33 の仕様 2): 1 行分〜2^31 − 1 で、1 行のバイト数の倍数、かつ行の先頭のずれがない (区切りの位置が
    /// 行の境目と合う)。問題がなければ null。
    /// </summary>
    public static SeparatorError? Validate(long length, int bytesPerRow, int rowShift)
    {
        if (length < bytesPerRow || length > int.MaxValue)
        {
            return SeparatorError.OutOfRange;
        }

        if (length % bytesPerRow != 0)
        {
            return SeparatorError.NotMultipleOfRow;
        }

        return rowShift % bytesPerRow != 0 ? SeparatorError.RowShift : null;
    }

    /// <summary>区切りの数 (長さ 0 では 1)。</summary>
    public long Count => SectionLength <= 0 ? 1 : Math.Max(1, (DocumentLength + SectionLength - 1) / SectionLength);

    /// <summary>オフセットの区切りの番号 (末尾の次の位置は最後の区切り)。</summary>
    public long IndexOf(long offset) => SectionLength <= 0 ? 0 : Math.Min(Math.Max(0, offset) / SectionLength, Count - 1);

    /// <summary>区切りの先頭のオフセット。</summary>
    public long StartOf(long index) => index * SectionLength;

    /// <summary>「次の区切り」(VIEW-32 の仕様 2): 次の区切りの先頭。最後の区切りでは null (動かない)。</summary>
    public long? Next(long cursor)
    {
        if (SectionLength <= 0 || DocumentLength == 0)
        {
            return null;
        }

        long index = IndexOf(cursor);
        return index + 1 < Count ? StartOf(index + 1) : null;
    }

    /// <summary>「前の区切り」(VIEW-32 の仕様 3): 区切りの先頭にいなければその先頭へ、先頭にいれば前の区切りの先頭へ。動けなければ null。</summary>
    public long? Previous(long cursor)
    {
        if (SectionLength <= 0 || DocumentLength == 0)
        {
            return null;
        }

        long start = StartOf(IndexOf(cursor));
        if (cursor != start)
        {
            return start;
        }

        return start > 0 ? StartOf(IndexOf(cursor) - 1) : null;
    }
}

/// <summary>
/// レコード表示の区切り (VIEW-18)。レコード番号 n は 開始オフセット + n × レコード長 から始まる。開始オフセットより前はレコードに
/// 属さない (ヘッダ)。区切りはデータに追従せず、いつも計算で求める (仕様 9)。
/// </summary>
public readonly record struct RecordLayout(long Start, long Length)
{
    public static RecordLayout For(ViewSettings view) => new(view.RecordStart, Math.Max(1, view.RecordLength));

    /// <summary>オフセットのレコード番号。開始オフセットより前は null。</summary>
    public long? IndexOf(long offset) => offset < Start ? null : (offset - Start) / Length;

    /// <summary>レコードの中の位置。開始オフセットより前は null。</summary>
    public long? WithinOf(long offset) => offset < Start ? null : (offset - Start) % Length;

    /// <summary>奇数番のレコードか (「レコードの交互色」で塗る。仕様 3)。</summary>
    public bool IsOdd(long offset) => offset >= Start && ((offset - Start) / Length & 1) == 1;

    /// <summary>オフセットがレコードの先頭か (ハイコントラストの境界線。仕様 3)。</summary>
    public bool IsBoundary(long offset) => offset >= Start && (offset - Start) % Length == 0;

    /// <summary>「次のレコード」(仕様 6): 同じレコード内の位置を保って次のレコードへ。移動先がなければ null。</summary>
    public long? Next(long cursor, long maxCursor)
    {
        if (cursor < Start)
        {
            return Start <= maxCursor && Start != cursor ? Start : null;
        }

        return cursor <= maxCursor - Length ? cursor + Length : null;
    }

    /// <summary>「前のレコード」(仕様 6)。移動先がなければ null。</summary>
    public long? Previous(long cursor) => cursor - Length >= Start ? cursor - Length : null;

    /// <summary>
    /// レコード内の位置の数字 (仕様 5・7): オフセットの基数 (10 進・8 進・16 進。セクタは 16 進) に従い、<paramref name="digits"/> 桁まで 0 で埋める。
    /// 接頭辞は付けない。
    /// </summary>
    public static string WithinDigits(long within, OffsetRadix radix, bool lowercase, int digits) => radix switch
    {
        OffsetRadix.Decimal => within.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0'),
        OffsetRadix.Octal => Convert.ToString(within, 8).PadLeft(digits, '0'),
        _ => within.ToString(lowercase ? "x" : "X", System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0'),
    };

    /// <summary>ステータスバーのレコード内の位置 (仕様 7): 10 進はそのまま、8 進は <c>0o</c>、16 進は <c>0x</c> と 2 桁以上。</summary>
    public static string WithinText(long within, OffsetRadix radix, bool lowercase) => radix switch
    {
        OffsetRadix.Decimal => WithinDigits(within, radix, lowercase, 1),
        OffsetRadix.Octal => "0o" + WithinDigits(within, radix, lowercase, 1),
        _ => "0x" + WithinDigits(within, radix, lowercase, 2),
    };
}
