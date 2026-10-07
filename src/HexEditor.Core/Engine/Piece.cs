namespace HexEditor.Core.Engine;

/// <summary>ピースが指すデータの種類 (ENG-02 の仕様 1)。</summary>
public enum PieceKind : byte
{
    /// <summary>データソースの範囲。<see cref="Piece.Offset"/> はデータソース上の位置。</summary>
    Original,

    /// <summary>追加バッファの範囲。<see cref="Piece.Offset"/> は追加バッファ上の位置。</summary>
    Added,

    /// <summary>パターンの繰り返し。<see cref="Piece.Offset"/> は追加バッファ上のパターンの位置。</summary>
    Pattern,

    /// <summary>カウンタ方式の乱数。<see cref="Piece.Offset"/> は乱数列上の位置。</summary>
    Random,

    /// <summary>
    /// 別のドキュメント (または保存前の版) の範囲の参照 (EDIT-24)。<see cref="Piece.Seed"/> は元データの外部参照の表の番号、
    /// <see cref="Piece.Offset"/> はその参照の中の位置。
    /// </summary>
    External,
}

/// <summary>
/// ドキュメントの一部分を表す不変の値。生成データ (ENG-03) は分割しても内容が変わらないよう、
/// パターンは位相、乱数は乱数列上の位置を持つ。
/// </summary>
public readonly record struct Piece
{
    private Piece(PieceKind kind, long length, long offset, long phase, int patternLength, ulong seed)
    {
        Kind = kind;
        Length = length;
        Offset = offset;
        Phase = phase;
        PatternLength = patternLength;
        Seed = seed;
    }

    public PieceKind Kind { get; }

    public long Length { get; }

    public long Offset { get; }

    /// <summary>パターンの何バイト目から始まるか (0 以上 <see cref="PatternLength"/> 未満)。</summary>
    public long Phase { get; }

    public int PatternLength { get; }

    public ulong Seed { get; }

    public static Piece Original(long sourceOffset, long length) =>
        new(PieceKind.Original, Positive(length), sourceOffset, 0, 0, 0);

    public static Piece Added(long bufferOffset, long length) =>
        new(PieceKind.Added, Positive(length), bufferOffset, 0, 0, 0);

    public static Piece Pattern(long patternBufferOffset, int patternLength, long length, long phase = 0)
    {
        if (patternLength is < 1 or > GeneratedData.MaxPatternLength)
        {
            throw new ArgumentOutOfRangeException(nameof(patternLength), "パターンの長さは 1〜4,096 バイトです。");
        }

        return new(PieceKind.Pattern, Positive(length), patternBufferOffset, phase % patternLength, patternLength, 0);
    }

    public static Piece Random(ulong seed, long streamPosition, long length) =>
        new(PieceKind.Random, Positive(length), streamPosition, 0, 0, seed);

    public static Piece External(int index, long offset, long length) =>
        new(PieceKind.External, Positive(length), offset, 0, 0, (ulong)index);

    /// <summary>外部参照の表の番号 (<see cref="PieceKind.External"/> のとき)。</summary>
    public int ExternalIndex => (int)Seed;

    /// <summary>先頭から <paramref name="at"/> バイトの位置で 2 つに分ける。内容は分割前と変わらない。</summary>
    public (Piece Left, Piece Right) Split(long at)
    {
        if (at <= 0 || at >= Length)
        {
            throw new ArgumentOutOfRangeException(nameof(at));
        }

        return (WithRange(0, at), WithRange(at, Length - at));
    }

    /// <summary>このピースの [start, start + length) の部分を表すピース。</summary>
    public Piece WithRange(long start, long length)
    {
        if (start < 0 || length <= 0 || start + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        return Kind switch
        {
            PieceKind.Pattern => new(Kind, length, Offset, (Phase + start) % PatternLength, PatternLength, 0),
            _ => new(Kind, length, Offset + start, 0, PatternLength, Seed),
        };
    }

    /// <summary>このピースの直後に <paramref name="next"/> が続くとき、1 つにまとめられるなら返す (ENG-02 の仕様 4)。</summary>
    public bool TryAppend(Piece next, out Piece merged)
    {
        merged = default;
        if (next.Kind != Kind || Length > long.MaxValue - next.Length)
        {
            return false;
        }

        bool contiguous = Kind switch
        {
            PieceKind.Original or PieceKind.Added => Offset + Length == next.Offset,
            PieceKind.Pattern => next.Offset == Offset && next.PatternLength == PatternLength
                && (Phase + Length) % PatternLength == next.Phase,
            PieceKind.Random or PieceKind.External => next.Seed == Seed && Offset + Length == next.Offset,
            _ => false,
        };
        if (!contiguous)
        {
            return false;
        }

        merged = new Piece(Kind, Length + next.Length, Offset, Phase, PatternLength, Seed);
        return true;
    }

    private static long Positive(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return length;
    }
}
