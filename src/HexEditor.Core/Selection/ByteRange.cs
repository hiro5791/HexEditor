namespace HexEditor.Core.Selection;

/// <summary>
/// ドキュメント上の連続した範囲 [<see cref="Start"/>, <see cref="End"/>)。選択の要素 (03-editing.md の用語) の 1 つ。
/// </summary>
public readonly record struct ByteRange(long Start, long Length)
{
    /// <summary>範囲の直後の位置 (このバイトを含まない)。</summary>
    public long End => Start + Length;

    /// <summary>範囲の最後のバイト (入力式の <c>sel.last</c>)。長さ 0 なら <see cref="Start"/> − 1。</summary>
    public long Last => Start + Length - 1;

    public bool IsEmpty => Length <= 0;

    public bool Contains(long offset) => offset >= Start && offset < End;

    /// <summary>[start, end) から作る。</summary>
    public static ByteRange FromBounds(long start, long end) => new(start, Math.Max(0, end - start));

    /// <summary>重なる、または隣り合う (結合できる) か。</summary>
    public bool Touches(ByteRange other) => other.Start <= End && Start <= other.End;

    public override string ToString() => $"0x{Start:X}+0x{Length:X}";
}
