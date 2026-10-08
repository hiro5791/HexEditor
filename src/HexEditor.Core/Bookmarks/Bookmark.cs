using System.Globalization;

namespace HexEditor.Core.Bookmarks;

/// <summary>
/// ブックマークの色 (INSP-24 の仕様 4): 色の一覧の番号 (1〜8。表示の色はテーマごとの ThemeResource で決める) か、任意の色 (RGB)。
/// </summary>
public readonly record struct BookmarkColor
{
    public const int PaletteSize = 8;

    private BookmarkColor(int index, uint rgb)
    {
        PaletteIndex = index;
        Rgb = rgb;
    }

    /// <summary>色の一覧の番号 (1〜8)。任意の色なら 0。</summary>
    public int PaletteIndex { get; }

    /// <summary>任意の色の 0xRRGGBB。</summary>
    public uint Rgb { get; }

    public bool IsCustom => PaletteIndex == 0;

    public static BookmarkColor Palette(int index) =>
        index is >= 1 and <= PaletteSize ? new BookmarkColor(index, 0) : throw new ArgumentOutOfRangeException(nameof(index));

    public static BookmarkColor Custom(uint rgb) => new(0, rgb & 0xFFFFFF);

    /// <summary>既定の色 (色の一覧の 1 番目。INSP-23 の仕様 2)。</summary>
    public static BookmarkColor Default => Palette(1);

    /// <summary>設定「ブックマークの既定の色」のキー (INSP-23 の仕様 2。値は色の一覧の番号 1〜8)。</summary>
    public const string DefaultColorKey = "bookmarks.defaultColor";

    /// <summary>設定の値から既定の色を求める (不正な値は色の一覧の 1 番目)。</summary>
    public static BookmarkColor FromSetting(string? value) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= PaletteSize
            ? Palette(index)
            : Default;

    /// <summary>保存の書式: 一覧の色は番号、任意の色は <c>#RRGGBB</c>。</summary>
    public override string ToString() => IsCustom ? "#" + Rgb.ToString("X6", CultureInfo.InvariantCulture) : PaletteIndex.ToString(CultureInfo.InvariantCulture);

    public static BookmarkColor Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        text = text.Trim();
        if (text.StartsWith('#') && text.Length == 7 && uint.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return Custom(rgb);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= PaletteSize
            ? Palette(index)
            : Default;
    }
}

/// <summary>
/// ブックマーク 1 件 (INSP-23 の仕様 1)。位置 (開始・長さ) は属する <see cref="BookmarkCollection"/> が持ち、ドキュメントの編集に
/// 合わせて調整する。名前・色などの変更は <see cref="BookmarkCollection"/> の操作で行う (変更の通知のため)。
/// </summary>
public sealed class Bookmark
{
    internal Bookmark(long id, string name, BookmarkColor color, DateTime created)
    {
        Id = id;
        Name = name;
        Color = color;
        Created = created;
        Updated = created;
    }

    /// <summary>ドキュメントの中で一意の番号 (作った順。<c>bm.名前</c> の重複は小さい方を選ぶ)。</summary>
    public long Id { get; }

    public string Name { get; internal set; }

    public BookmarkColor Color { get; internal set; }

    /// <summary>コメント (Markdown。最大 64 KB。INSP-24 の仕様 5)。</summary>
    public string Comment { get; internal set; } = string.Empty;

    /// <summary>番号 (1〜9。なければ 0。INSP-25)。</summary>
    public int Number { get; internal set; }

    /// <summary>グループ (INSP-27。フェーズ 2)。保存・読み込みだけ行う。</summary>
    public string? Group { get; internal set; }

    public DateTime Created { get; internal set; }

    public DateTime Updated { get; internal set; }

    /// <summary>範囲がすべて削除された (長さ 0 にして削除位置に置いた。INSP-23 の仕様 4)。</summary>
    public bool RangeDeleted { get; internal set; }

    /// <summary>番号の設定で自動的に作られた (INSP-25 の仕様 1)。</summary>
    public bool CreatedForNumber { get; internal set; }

    /// <summary>作った後に、利用者が名前・色・範囲・コメントを変えた。</summary>
    public bool EditedByUser { get; internal set; }

    /// <summary>名前を変えた・コメントを書いた (削除したときに「元に戻す」付きの通知を出す。INSP-23 の仕様 2)。</summary>
    public bool IsCustomized { get; internal set; }

    public long Start => Owner?.StartOf(this) ?? _detachedStart;

    public long Length { get; internal set; }

    /// <summary>最後のバイトの次の位置。</summary>
    public long End => Start + Length;

    internal BookmarkCollection? Owner { get; set; }

    internal long _detachedStart;

    // ---- 区間木のノードとしての値 (BookmarkTree) ----
    // ノードを別のオブジェクトにせず、ブックマーク自体に持つ (100 万件でオブジェクトの数を減らし、GC の手間を減らす。VIEW-04 の受け入れ基準 4)。

    /// <summary>木に入っている (<see cref="Owner"/> の一覧にある)。</summary>
    internal bool InTree;

    /// <summary>開始位置 (祖先の <see cref="Lazy"/> を足す前の値)。</summary>
    internal long TreeStart;

    /// <summary>部分木の終了位置 (開始 + 長さ) の最大値。</summary>
    internal long MaxEnd;

    /// <summary>子の部分木にまだ伝えていないずらし。</summary>
    internal long Lazy;

    internal int Priority;

    internal int Size;

    internal Bookmark? Left;

    internal Bookmark? Right;

    internal Bookmark? Parent;

    /// <summary>作業用の番号 (保存の写し取りで、位置の配列の添字を覚える。UI スレッドだけで使う)。</summary>
    internal int Scratch;

    /// <summary>入力式の <c>bm.名前</c> で使える名前か (英字・数字・<c>_</c> からなり、英字か <c>_</c> で始まる。INSP-24 の仕様 2)。</summary>
    public static bool IsExpressionName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public override string ToString() => $"{Name} @0x{Start:X} +{Length}";
}
