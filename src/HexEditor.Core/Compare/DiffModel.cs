namespace HexEditor.Core.Compare;

/// <summary>差分の種類 (ANA-02 の仕様 2・4、ANA-03 の仕様 1、ANA-08 の仕様 4)。</summary>
public enum DiffKind
{
    /// <summary>変更 (左右の両方に長さがある。挿入・削除を考慮した比較では左右の長さが異なってよい)。</summary>
    Changed,

    /// <summary>挿入 (右のみ。左の長さは 0)。単純比較では、右の方が長い場合の残りの部分。</summary>
    Inserted,

    /// <summary>削除 (左のみ。右の長さは 0)。単純比較では、左の方が長い場合の残りの部分。</summary>
    Deleted,

    /// <summary>読み込み不可 (どちらかが読めなかった範囲。ANA-02 の「エラー」)。</summary>
    Unreadable,
}

/// <summary>差分 1 件。オフセットは左右それぞれのデータの絶対位置 (開始オフセットを足した位置。ANA-02 の仕様 7)。</summary>
public readonly record struct DiffRange(DiffKind Kind, long LeftOffset, long LeftLength, long RightOffset, long RightLength)
{
    public long LeftEnd => LeftOffset + LeftLength;

    public long RightEnd => RightOffset + RightLength;

    /// <summary>片側の開始位置。</summary>
    public long Start(bool right) => right ? RightOffset : LeftOffset;

    /// <summary>片側の長さ。</summary>
    public long Length(bool right) => right ? RightLength : LeftLength;

    /// <summary>左右の長い方 (一覧の「長さ」での並べ替えと絞り込みに使う)。</summary>
    public long MaxLength => Math.Max(LeftLength, RightLength);

    /// <summary>左右の長さから種類を決める (変更・挿入・削除)。</summary>
    public static DiffKind KindFor(long leftLength, long rightLength) =>
        leftLength > 0 && rightLength > 0 ? DiffKind.Changed : leftLength > 0 ? DiffKind.Deleted : DiffKind.Inserted;
}

/// <summary>比較方式 (ANA-01 の仕様 3)。</summary>
public enum CompareMethod
{
    /// <summary>単純比較 (ANA-02)。</summary>
    Simple,

    /// <summary>挿入・削除を考慮した比較 (ANA-03)。</summary>
    InsertDelete,
}
