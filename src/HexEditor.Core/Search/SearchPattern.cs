using System.Buffers;
using System.Text;

namespace HexEditor.Core.Search;

/// <summary>検索語の誤り (FIND-04 の仕様 6)。UI はこれを見て説明文を出す。</summary>
public enum PatternError
{
    Empty,
    OddDigits,
    InvalidCharacter,
    TooLong,
    NotEncodable,

    /// <summary>エスケープ表記の誤り (`\q`、桁の足りない `\x4` など。FIND-07 の仕様 4)。</summary>
    InvalidEscape,

    /// <summary>ワイルドカードだけの検索語 (FIND-06 の仕様 5)。</summary>
    WildcardsOnly,

    /// <summary>可変長のワイルドカード `*{m,n}` の範囲の誤り (m &gt; n、または一致の最大長を超える。FIND-06 の仕様 3)。</summary>
    InvalidWildcardRange,

    /// <summary>
    /// 整数がサイズの範囲を超える (FIND-13 の仕様 3)。<see cref="PatternException.Arguments"/> は
    /// ビット数、符号ありの最小・最大、符号なしの最小・最大 (地域設定で書式化したもの)。
    /// </summary>
    IntegerOutOfRange,

    /// <summary>符号ありの範囲を超える。引数はビット数、最小、最大。</summary>
    SignedOutOfRange,

    /// <summary>符号なしの範囲を超える。引数はビット数、最小、最大。</summary>
    UnsignedOutOfRange,

    /// <summary>数値として読めない (FIND-13 の入力式の誤り、FIND-14 の `1,5` など)。<see cref="PatternException.Detail"/> は入力。</summary>
    InvalidNumber,

    /// <summary>値が浮動小数点の形式の範囲を超え、無限大になる (FIND-14 の「エラー」)。引数は形式の名前。</summary>
    FloatOverflow,

    /// <summary>許容誤差が負、または ULP が 0〜1,000,000 の整数でない (FIND-14 の仕様 3)。</summary>
    InvalidTolerance,

    /// <summary>置換語の `?` (ニブルのワイルドカード) と `*` は使えない (`??` だけが使える。FIND-22 の仕様 2)。</summary>
    InvalidReplacementWildcard,

    /// <summary>位置の条件の周期 x が 1〜2^32 でない (FIND-17 の仕様 1)。引数は上限。</summary>
    PositionModulus,

    /// <summary>位置の条件の余り y が 0〜x − 1 でない (FIND-17 の「エラー」)。引数は上限 (x − 1)。</summary>
    PositionRemainder,

    /// <summary>値とマスクの長さが違う (FIND-16 の「エラー」)。引数は値とマスクのバイト数。</summary>
    MaskLengthMismatch,

    /// <summary>マスクが長すぎる (1〜256 バイト。FIND-16 の仕様 2)。引数は上限。</summary>
    MaskTooLong,

    /// <summary>マスクがすべて 0 (FIND-16 の仕様 3)。</summary>
    MaskAllZero,

    /// <summary>ビットパターンの文字数が 8 の倍数でない (FIND-16 の「エラー」)。引数は文字数。</summary>
    BitPatternLength,

    /// <summary>ビットパターンに `0` `1` `x` 以外の文字がある。</summary>
    InvalidBit,

    /// <summary>範囲の最小が最大より大きい (FIND-15 の仕様 2)。引数は最小と最大。</summary>
    RangeOrder,

    /// <summary>正規表現の構文の誤り (FIND-18 の「エラー」)。<see cref="PatternException.Detail"/> は理由 (RegexParseError の名前)。</summary>
    RegexSyntax,

    /// <summary>バイト列の正規表現で Unicode プロパティ `\p{...}` を使った (FIND-19 の仕様 5)。</summary>
    RegexUnicodeProperty,

    /// <summary>バイト列の正規表現で `\u` を使った (FIND-19 の仕様 5)。</summary>
    RegexUnicodeEscape,

    /// <summary>バイト列の正規表現で U+00FF を超える文字を使った (バイトに当たらない。FIND-19 の仕様 2)。</summary>
    RegexNonByteCharacter,

    /// <summary>複数の文字コードを 1 つも選んでいない、またはどれでも符号化できない (FIND-08 の「エラー」)。</summary>
    NoEncoding,

    /// <summary>語が多すぎる (FIND-26 の仕様 2)。引数は上限。</summary>
    TooManyTerms,
}

/// <summary>検索語の警告 (検索はできる)。</summary>
[Flags]
public enum PatternWarnings
{
    None = 0,

    /// <summary>先頭と末尾のワイルドカードは照合に使わない (一致の範囲には含める。FIND-06 の仕様 4)。</summary>
    EdgeWildcardsIgnored = 1,
}

/// <summary>検索語の誤り。<see cref="Position"/> は誤りのある文字の位置 (1 から数えた文字数。FIND-04 の仕様 6)。</summary>
public sealed class PatternException : Exception
{
    public PatternException(PatternError error, string detail = "")
        : this(error, detail, null)
    {
    }

    public PatternException(PatternError error, string detail, int? position, IReadOnlyList<string>? arguments = null)
        : base(position is int p ? $"{error}: {detail} ({p})" : $"{error}: {detail}")
    {
        Error = error;
        Detail = detail;
        Position = position;
        Arguments = arguments ?? [];
    }

    public PatternError Error { get; }

    /// <summary>表せない文字など、説明文に入れる語。</summary>
    public string Detail { get; }

    /// <summary>
    /// 誤りのある文字が検索語の何文字目か (1 から数える。サロゲートペアは 1 文字)。位置を示せない誤り (空、長すぎる) は null。
    /// </summary>
    public int? Position { get; }

    /// <summary>説明文に入れる値 (範囲の数値など。地域設定で書式化済み)。空なら <see cref="Detail"/> を使う。</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>ビットマスクの検索 (FIND-16) で、誤りが検索欄ではなくマスクの入力欄にある。</summary>
    public bool InMask { get; init; }
}

/// <summary>テキストの検索語の解釈のしかた (FIND-07、FIND-10)。</summary>
public sealed record TextSearchOptions
{
    /// <summary>大文字と小文字を区別する (FIND-10 の仕様 1。UI の既定はオフ)。</summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>エスケープ表記 `\n \r \t \0 \\ \xHH \uHHHH` を使う (FIND-07 の仕様 4)。</summary>
    public bool UseEscapes { get; init; }

    /// <summary>UTF-16 / UTF-32 で、一致の開始オフセットを 2 / 4 の倍数に限る (FIND-07 の仕様 5)。ほかの文字コードでは無視する。</summary>
    public bool AlignToCharacters { get; init; }

    /// <summary>単語単位で探す (FIND-10 の仕様 4。既定オフ)。</summary>
    public bool WholeWord { get; init; }
}

/// <summary>Hex の検索語の解釈のしかた (FIND-06)。</summary>
public sealed record HexSearchOptions
{
    /// <summary>可変長のワイルドカード `*` の最大長 (「一致の最大長」。FIND-01 の仕様 3。既定 4,096 バイト)。</summary>
    public int MaxWildcardLength { get; init; } = SearchPattern.DefaultMaxMatchLength;
}

/// <summary>
/// 検索するバイト列。次の種類がある。
/// <list type="bullet">
/// <item>リテラル: <see cref="Bytes"/> そのもの。</item>
/// <item>ワイルドカード: <see cref="Mask"/> のビットが 1 の部分だけを比べる (FIND-06)。</item>
/// <item>文字ごとの候補: 大文字・小文字を区別しないテキスト (FIND-10)、エンディアン「両方」の数値 (FIND-13)。文字ごとに、候補のバイト列のどれかに一致する。</item>
/// <item>可変長のワイルドカード `*` で区切った部分の並び (FIND-06 の仕様 3)。間の長さは最短で一致させる。</item>
/// <item>値の比較: 各位置で値を復号して比べる (許容誤差のある浮動小数点、NaN。FIND-14)。</item>
/// </list>
/// </summary>
public sealed partial class SearchPattern
{
    /// <summary>検索語の最大の長さ (FIND-05 の仕様 3)。</summary>
    public const int MaxLength = 1024 * 1024;

    /// <summary>「一致の最大長」の既定値 (FIND-01 の仕様 3)。</summary>
    public const int DefaultMaxMatchLength = 4096;

    /// <summary>「一致の最大長」の設定の上限 (FIND-01 の仕様 3)。</summary>
    public const int MaxMaxMatchLength = 1024 * 1024;

    /// <summary>文字ごとの候補 (候補がなければ null)。各要素は 1 文字 (またはエスケープの 1 バイト) の候補のバイト列。</summary>
    private readonly byte[][][]? _slots;

    /// <summary>先頭のバイトの候補 (文字ごとの候補のときだけ。FIND-01 の仕様 6)。</summary>
    private readonly SearchValues<byte>? _firstBytes;

    /// <summary>可変長のワイルドカードで区切った部分 (可変長でなければ null)。</summary>
    private readonly SearchPattern[]? _parts;

    /// <summary><see cref="_parts"/> の間の長さの範囲 (要素数は部分の数 − 1)。</summary>
    private readonly (int Min, int Max)[]? _gaps;

    /// <summary>値の比較 (なければ null)。</summary>
    private readonly ValueMatcher? _matcher;

    private readonly bool _caseInsensitive;

    /// <summary>
    /// ワイルドカード・ビットマスクで、ワイルドカードでないバイトがないときに候補を絞り込む位置と、その位置で一致しうるバイトの値
    /// (FIND-16 の「巨大ファイル」: 先頭バイトのマスク結果で候補を絞り込む。ニブルのワイルドカードも同じ。FIND-16 の仕様 5)。
    /// </summary>
    private readonly int _probeOffset = -1;

    private readonly SearchValues<byte>? _probeValues;

    private SearchPattern(byte[] bytes, byte[]? mask, byte[][][]? slots, int alignment, bool caseInsensitive = false)
    {
        Bytes = bytes;
        Mask = mask;
        _slots = slots;
        _caseInsensitive = caseInsensitive;
        Alignment = Math.Max(1, alignment);
        (AnchorOffset, AnchorLength) = FindAnchor(mask, bytes.Length);
        if (mask is not null && AnchorLength == 0)
        {
            (_probeOffset, _probeValues) = FindProbe(bytes, mask);
        }

        if (slots is null)
        {
            MinMatchLength = MaxMatchLength = bytes.Length;
        }
        else
        {
            MinMatchLength = slots.Sum(s => s.Min(a => a.Length));
            MaxMatchLength = slots.Sum(s => s.Max(a => a.Length));
            _firstBytes = SearchValues.Create([.. slots[0].Select(a => a[0]).Distinct()]);
        }
    }

    private SearchPattern(SearchPattern[] parts, (int Min, int Max)[] gaps, byte[] bytes, byte[]? mask)
    {
        _parts = parts;
        _gaps = gaps;
        Bytes = bytes;
        Mask = mask;
        Alignment = 1;
        MinMatchLength = parts.Sum(p => p.MinMatchLength) + gaps.Sum(g => g.Min);
        MaxMatchLength = parts.Sum(p => p.MaxMatchLength) + gaps.Sum(g => g.Max);
    }

    private SearchPattern(ValueMatcher matcher, byte[] bytes)
    {
        _matcher = matcher;
        Bytes = bytes;
        Alignment = 1;
        MinMatchLength = MaxMatchLength = matcher.Length;
    }

    /// <summary>
    /// 検索語のバイト列。文字ごとの候補のときは、入力したとおりの文字を符号化したもの (変換結果の表示用。FIND-04 の仕様 7)。
    /// 可変長のワイルドカードのときは、部分をつなげたもの。値の比較のときは、値をそのまま符号化したもの。
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>比べるビット。null ならすべて比べる (リテラル)。</summary>
    public byte[]? Mask { get; }

    /// <summary>検索語のバイト数 (<see cref="Bytes"/> の長さ)。一致の長さは <see cref="MinMatchLength"/>〜<see cref="MaxMatchLength"/>。</summary>
    public int Length => Bytes.Length;

    /// <summary>ワイルドカードも文字ごとの候補もない (SIMD の IndexOf でそのまま探せる。FIND-01 の仕様 5)。</summary>
    public bool IsLiteral => Mask is null && _slots is null && _parts is null && _matcher is null && _multi is null && _mismatch is null;

    /// <summary>大文字・小文字を区別しない (文字ごとの候補を持つ)。</summary>
    public bool IsCaseInsensitive => _caseInsensitive;

    /// <summary>可変長のワイルドカード `*` を含む。</summary>
    public bool HasVariableGap => _parts is not null;

    /// <summary>一致の最短の長さ。</summary>
    public int MinMatchLength { get => _minMatchLength; private set => _minMatchLength = value; }

    /// <summary>一致の最長の長さ。チャンクの重なり幅は これ − 1 (FIND-01 の仕様 3)。</summary>
    public int MaxMatchLength { get => _maxMatchLength; private set => _maxMatchLength = value; }

    private int _minMatchLength;
    private int _maxMatchLength;

    /// <summary>一致の開始オフセットが満たすべき倍数 (文字の境界に揃える。FIND-07 の仕様 5)。1 なら制限なし。</summary>
    public int Alignment { get; }

    /// <summary>ワイルドカードを含まない最も長い区間 (候補の絞り込みに使う)。</summary>
    internal int AnchorOffset { get; }

    internal int AnchorLength { get; }

    /// <summary>検索語の警告 (先頭と末尾のワイルドカードなど)。</summary>
    public PatternWarnings Warnings { get; private set; }

    /// <summary>
    /// 一致の種類の名前 (エンディアン「両方」の「LE」「BE」など。FIND-13 の仕様 4)。空なら種類はない。
    /// <see cref="VariantAt"/> の値はこの添字。
    /// </summary>
    public IReadOnlyList<string> Variants { get; private init; } = [];

    /// <summary>単語単位で探す (FIND-10 の仕様 4)。null なら単語の境界を調べない。</summary>
    public WordBoundary? Word { get; private init; }

    /// <summary>数値の検索の条件 (結果一覧の「値」列と置換語の符号化に使う。FIND-13、FIND-14)。数値でなければ null。</summary>
    public NumericSearchInfo? Numeric { get; private set; }

    /// <summary>
    /// 変換結果の表示 (FIND-04 の仕様 7)。先頭 <paramref name="maxBytes"/> バイトを Hex で書き、ワイルドカードは `??` / `?`、
    /// 可変長のワイルドカードは `*` (範囲を指定したものは `*{m,n}`) のまま書く。
    /// </summary>
    public string Preview(int maxBytes = 32)
    {
        var parts = new List<string>();
        int shown = 0;
        void AddBytes(SearchPattern p)
        {
            for (int i = 0; i < p.Bytes.Length && shown < maxBytes; i++, shown++)
            {
                byte m = p.Mask?[i] ?? 0xFF;
                char hi = (m & 0xF0) == 0 ? '?' : "0123456789ABCDEF"[p.Bytes[i] >> 4];
                char lo = (m & 0x0F) == 0 ? '?' : "0123456789ABCDEF"[p.Bytes[i] & 0xF];
                parts.Add(string.Concat(hi, lo));
            }
        }

        if (_parts is null)
        {
            AddBytes(this);
        }
        else
        {
            for (int k = 0; k < _parts.Length && shown < maxBytes; k++)
            {
                if (k > 0)
                {
                    (int min, int max) = _gaps![k - 1];
                    parts.Add(min == 0 && max == _defaultGapMax ? "*" : $"*{{{min},{max}}}");
                }

                AddBytes(_parts[k]);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>可変長のワイルドカードの既定の最大長 (表示で `*` と書くかどうかの判定に使う)。</summary>
    private int _defaultGapMax = DefaultMaxMatchLength;

    public static SearchPattern Literal(byte[] bytes)
    {
        CheckLength(bytes.Length);
        return new SearchPattern(bytes, null, null, 1);
    }

    /// <summary>
    /// Hex 文字列から作る (00-overview 6.3、FIND-05、FIND-06)。空白・カンマ・改行・`0x`・`\x` は区切り。
    /// `??` は任意の 1 バイト、`?` は任意の 1 ニブル、`*` は 0〜「一致の最大長」バイトの任意の並び (`*{m,n}` で範囲を指定)。
    /// </summary>
    public static SearchPattern FromHex(string text) => FromHex(text, new HexSearchOptions());

    /// <inheritdoc cref="FromHex(string)"/>
    public static SearchPattern FromHex(string text, HexSearchOptions options)
    {
        List<HexItem> items = ParseHexItems(text, options.MaxWildcardLength, allowWildcards: true);
        if (items.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        // ワイルドカードだけの検索語はエラー (仕様 5)。
        if (!items.Any(i => !i.IsGap && i.Mask != 0))
        {
            throw new PatternException(PatternError.WildcardsOnly);
        }

        // 先頭と末尾の `??` / `*` は照合に使わない (仕様 4)。`??` は一致の範囲に含め、`*` は最短 (0 バイト) なので取り除く。
        bool edge = (!items[0].IsGap && items[0].Mask == 0) || (!items[^1].IsGap && items[^1].Mask == 0)
            || items[0].IsGap || items[^1].IsGap;
        while (items[0].IsGap)
        {
            items.RemoveAt(0);
        }

        while (items[^1].IsGap)
        {
            items.RemoveAt(items.Count - 1);
        }

        // `*` で部分に分ける。続けて書いた `*` は 1 つにまとめる。
        var parts = new List<List<HexItem>> { new() };
        var gaps = new List<(int Min, int Max)>();
        foreach (HexItem item in items)
        {
            if (item.IsGap)
            {
                if (parts[^1].Count == 0)
                {
                    gaps[^1] = (gaps[^1].Min + item.GapMin, Math.Min(options.MaxWildcardLength, gaps[^1].Max + item.GapMax));
                }
                else
                {
                    gaps.Add((item.GapMin, item.GapMax));
                    parts.Add([]);
                }
            }
            else
            {
                parts[^1].Add(item);
            }
        }

        SearchPattern[] built = [.. parts.Select(BuildFixed)];
        CheckLength(built.Sum(p => p.Length));
        PatternWarnings warnings = edge ? PatternWarnings.EdgeWildcardsIgnored : PatternWarnings.None;
        if (built.Length == 1)
        {
            return built[0].With(warnings);
        }

        byte[] bytes = [.. built.SelectMany(p => p.Bytes)];
        byte[]? mask = built.Any(p => p.Mask is not null) ? [.. built.SelectMany(p => p.Mask ?? Enumerable.Repeat((byte)0xFF, p.Length))] : null;
        return new SearchPattern(built, [.. gaps], bytes, mask) { Warnings = warnings, _defaultGapMax = options.MaxWildcardLength };
    }

    /// <summary>
    /// 置換語の Hex 文字列 (FIND-22 の仕様 2)。`??` の位置は元のバイトを残す (戻り値の <c>Keep</c> が true)。
    /// `?` (ニブル) と `*` は使えない。空 (空白だけ) なら長さ 0 (一致を削除する。FIND-22 の仕様 8)。
    /// </summary>
    public static (byte Value, bool Keep)[] ParseReplacementHex(string text)
    {
        List<HexItem> items = ParseHexItems(text, DefaultMaxMatchLength, allowWildcards: true);
        var result = new (byte, bool)[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            HexItem item = items[i];
            if (item.IsGap || item.Mask is not (0x00 or 0xFF))
            {
                throw new PatternException(PatternError.InvalidReplacementWildcard, item.IsGap ? "*" : "?", item.Position);
            }

            result[i] = (item.Value, item.Mask == 0);
        }

        if (result.Length > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        return result;
    }

    /// <summary>候補のバイト列のどれかに一致するパターン (エンディアン「両方」など)。同じバイト列はまとめ、名前を「/」でつなぐ。</summary>
    internal static SearchPattern FromAlternatives(IReadOnlyList<byte[]> alternatives, IReadOnlyList<string> labels)
    {
        var distinct = new List<byte[]>();
        var names = new List<string>();
        for (int i = 0; i < alternatives.Count; i++)
        {
            int same = distinct.FindIndex(a => a.AsSpan().SequenceEqual(alternatives[i]));
            if (same >= 0)
            {
                if (!names[same].Split('/').Contains(labels[i]))
                {
                    names[same] += "/" + labels[i];
                }
            }
            else
            {
                distinct.Add(alternatives[i]);
                names.Add(labels[i]);
            }
        }

        foreach (byte[] a in distinct)
        {
            CheckLength(a.Length);
        }

        if (distinct.Count == 1)
        {
            return new SearchPattern(distinct[0], null, null, 1) { Variants = names };
        }

        return new SearchPattern(distinct[0], null, [[.. distinct]], 1) { Variants = names };
    }

    /// <summary>各位置で値を比べるパターン (許容誤差のある浮動小数点など)。<paramref name="bytes"/> は変換結果の表示用。</summary>
    internal static SearchPattern FromMatcher(ValueMatcher matcher, byte[] bytes, IReadOnlyList<string> labels) =>
        new(matcher, bytes) { Variants = labels };

    /// <summary>文字列を文字コードで符号化して作る (FIND-07)。BOM は付けない。表せない文字はエラーにする。大文字・小文字を区別する。</summary>
    public static SearchPattern FromText(string text, Encoding encoding) => FromText(text, encoding, new TextSearchOptions());

    /// <summary>
    /// 文字列を文字コードで符号化して作る (FIND-07、FIND-10)。BOM は付けない。表せない文字は、その文字と位置を示してエラーにする
    /// (置換文字に置き換えない)。正規化はしない。
    /// </summary>
    public static SearchPattern FromText(string text, Encoding encoding, TextSearchOptions options)
    {
        if (text.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        Encoding strict = StrictEncoding(encoding);
        List<TextToken> tokens = Tokenize(text, options.UseEscapes);
        if (tokens.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        int alignment = options.AlignToCharacters ? CharacterUnit(encoding) : 1;
        WordBoundary? word = options.WholeWord ? new WordBoundary(encoding) : null;

        // 入力したとおりの符号化 (変換結果の表示と、区別する検索のバイト列)。
        var typed = new List<byte[]>(tokens.Count);
        foreach (TextToken token in tokens)
        {
            typed.Add(token.IsByte ? [token.Byte] : Encode(strict, token.Rune, token.Position));
        }

        byte[] bytes = Concat(typed);
        CheckLength(bytes.Length);
        if (options.CaseSensitive)
        {
            return new SearchPattern(bytes, null, null, alignment) { Word = word };
        }

        // 区別しない: 文字ごとに変種を作り、符号化できる変種だけを候補にする (FIND-10 の仕様 2)。
        var slots = new byte[tokens.Count][][];
        bool anyAlternative = false;
        for (int t = 0; t < tokens.Count; t++)
        {
            TextToken token = tokens[t];
            var alternatives = new List<byte[]> { typed[t] };
            if (!token.IsByte)
            {
                foreach (Rune variant in CaseVariants(token.Rune))
                {
                    if (TryEncode(strict, variant) is { } encoded && !alternatives.Any(a => a.AsSpan().SequenceEqual(encoded)))
                    {
                        alternatives.Add(encoded);
                    }
                }
            }

            anyAlternative |= alternatives.Count > 1;
            slots[t] = [.. alternatives];
        }

        if (slots.Sum(s => s.Max(a => a.Length)) > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        // 大文字・小文字のない文字だけなら、リテラルとして SIMD で探す。
        return new SearchPattern(bytes, null, anyAlternative ? slots : null, alignment, anyAlternative) { Word = word };
    }

    /// <summary>
    /// 文字の大文字・小文字の変種 (自分自身を含まない)。Unicode の単純な対応 (1 文字対 1 文字、Invariant) だけを使う。
    /// トルコ語の `İ` (U+0130) と `ı` (U+0131) は自分自身とだけ一致させる (FIND-10 の仕様 2)。
    /// </summary>
    public static IReadOnlyList<Rune> CaseVariants(Rune rune)
    {
        const int DottedCapitalI = 0x130;
        const int DotlessSmallI = 0x131;
        if (rune.Value is DottedCapitalI or DotlessSmallI)
        {
            return [];
        }

        var result = new List<Rune>(3);
        Rune upper = Rune.ToUpperInvariant(rune);
        Rune lower = Rune.ToLowerInvariant(rune);
        foreach (Rune v in new[] { upper, lower, Rune.ToLowerInvariant(upper), Rune.ToUpperInvariant(lower) })
        {
            if (v != rune && v.Value is not (DottedCapitalI or DotlessSmallI) && !result.Contains(v))
            {
                result.Add(v);
            }
        }

        return result;
    }

    /// <summary>文字の境界の単位 (UTF-16 は 2、UTF-32 は 4、ほかは 1)。</summary>
    public static int CharacterUnit(Encoding encoding) => encoding.CodePage switch
    {
        1200 or 1201 => 2,
        12000 or 12001 => 4,
        _ => 1,
    };

    /// <summary><paramref name="data"/> の先頭がこのパターンに一致するか。</summary>
    public bool MatchesAt(ReadOnlySpan<byte> data) => MatchLength(data) >= 0;

    /// <summary><paramref name="data"/> の先頭で一致する長さ。一致しなければ −1。</summary>
    public int MatchLength(ReadOnlySpan<byte> data) => MatchLengthAt(data, 0);

    /// <summary>
    /// <paramref name="data"/> の先頭 (ドキュメント上の位置 <paramref name="baseOffset"/>) で一致する長さ。一致しなければ −1。
    /// 一致しない箇所の検索 (FIND-25) は位置で比べるバイトが決まるため、位置を渡す。位置の条件は調べない。
    /// </summary>
    public int MatchLengthAt(ReadOnlySpan<byte> data, long baseOffset)
    {
        if (_mismatch is { } mismatch)
        {
            return data.Length > 0 && mismatch.IndexOf(data[..1], 0, baseOffset) == 0 ? 1 : -1;
        }

        if (_multi is not null)
        {
            return _multi.MatchLength(data, baseOffset, out _);
        }

        if (_parts is not null)
        {
            int end = MatchParts(data, 0, 0);
            return end;
        }

        if (_matcher is not null)
        {
            return data.Length >= _matcher.Length && _matcher.Match(data) >= 0 ? _matcher.Length : -1;
        }

        if (_slots is not null)
        {
            return MatchSlots(data);
        }

        if (data.Length < Bytes.Length)
        {
            return -1;
        }

        if (Mask is null)
        {
            return data.StartsWith(Bytes) ? Bytes.Length : -1;
        }

        for (int i = 0; i < Bytes.Length; i++)
        {
            if ((data[i] & Mask[i]) != (Bytes[i] & Mask[i]))
            {
                return -1;
            }
        }

        return Bytes.Length;
    }

    /// <summary>
    /// <paramref name="data"/> の先頭で一致した種類 (<see cref="Variants"/> の添字)。種類がない・一致しない場合は 0。
    /// </summary>
    public int VariantAt(ReadOnlySpan<byte> data)
    {
        if (Variants.Count <= 1)
        {
            return 0;
        }

        if (_multi is not null)
        {
            return _multi.MatchLength(data, 0, out int variant) >= 0 ? variant : 0;
        }

        if (_matcher is not null)
        {
            return data.Length >= _matcher.Length ? Math.Max(0, _matcher.Match(data)) : 0;
        }

        if (_slots is { Length: 1 })
        {
            byte[][] alternatives = _slots[0];
            for (int i = 0; i < alternatives.Length; i++)
            {
                if (data.StartsWith(alternatives[i]))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// バッファの中で最初の一致の位置。一致はバッファの中にすっかり収まるものだけ。<paramref name="baseOffset"/> は
    /// バッファの先頭のドキュメント上の位置 (文字の境界の判定に使う)。
    /// </summary>
    internal int IndexOf(ReadOnlySpan<byte> data, long baseOffset, out int length)
    {
        int from = 0;
        while (true)
        {
            if (Position is { IsNone: false } position)
            {
                // 位置の条件を満たさない位置は一致になりえないので、満たす位置まで飛ばす (FIND-17)。
                long next = position.NextAccepted(baseOffset + from) - baseOffset;
                if (next > data.Length)
                {
                    length = 0;
                    return -1;
                }

                from = (int)next;
            }

            int found = IndexOfUnaligned(data, from, baseOffset, out length);
            if (found < 0 || Accepts(baseOffset + found))
            {
                return found;
            }

            from = found + 1;
        }
    }

    /// <summary>
    /// バッファの中で、開始が <paramref name="startLimit"/> 未満の最後の一致の位置 (後方検索。FIND-01 の仕様 8)。
    /// </summary>
    internal int LastIndexOf(ReadOnlySpan<byte> data, long baseOffset, int startLimit, out int length)
    {
        int end = Math.Min(startLimit, data.Length); // 一致の開始は end 未満
        while (true)
        {
            if (Position is { IsNone: false } position && end > 0)
            {
                long previous = position.PreviousAccepted(baseOffset + end - 1) - baseOffset;
                if (previous < 0)
                {
                    length = 0;
                    return -1;
                }

                end = (int)previous + 1;
            }

            int found = LastIndexOfUnaligned(data, end, baseOffset, out length);
            if (found < 0 || Accepts(baseOffset + found))
            {
                return found;
            }

            end = found;
        }
    }

    /// <summary>
    /// ドキュメント上の位置 <paramref name="offset"/> を一致の開始として認めるか (文字の境界に揃える (FIND-07 の仕様 5) と
    /// 位置の条件 (FIND-17))。
    /// </summary>
    public bool Accepts(long offset) =>
        (Alignment == 1 || offset % Alignment == 0) && (Position is null || Position.Accepts(offset));

    /// <summary>開始が <paramref name="from"/> 以上の最初の一致。</summary>
    private int IndexOfUnaligned(ReadOnlySpan<byte> data, int from, long baseOffset, out int length)
    {
        length = 0;
        if (_mismatch is { } mismatch)
        {
            length = 1;
            return mismatch.IndexOf(data, from, baseOffset);
        }

        if (_multi is not null)
        {
            return _multi.IndexOf(data, from, baseOffset, out length);
        }

        if (data.Length - from < MinMatchLength)
        {
            return -1;
        }

        if (_parts is not null)
        {
            // 最初の部分で候補を探し、残りの部分を最短で照合する (FIND-06 の仕様 3)。
            SearchPattern first = _parts[0];
            while (true)
            {
                int candidate = first.IndexOfUnaligned(data, from, baseOffset, out _);
                if (candidate < 0)
                {
                    return -1;
                }

                int end = MatchParts(data, 0, candidate);
                if (end >= 0)
                {
                    length = end - candidate;
                    return candidate;
                }

                from = candidate + 1;
            }
        }

        if (_matcher is not null)
        {
            // 位置の条件があれば、条件を満たす位置だけを復号する (FIND-17 の「巨大ファイル」)。
            int n = _matcher.Length;
            long step = Position is { IsNone: false } p ? p.Modulus : 1;
            long first = step > 1 ? Position!.NextAccepted(baseOffset + from) - baseOffset : from;
            for (long i = first; i + n <= data.Length; i += step)
            {
                if (_matcher.Match(data[(int)i..]) >= 0)
                {
                    length = n;
                    return (int)i;
                }
            }

            return -1;
        }

        if (_slots is not null)
        {
            // 先頭の文字の候補のバイトで絞り込み、候補の位置だけを照合する (FIND-01 の仕様 6)。
            int limit = data.Length - MinMatchLength + 1;
            while (from < limit)
            {
                int found = data[from..limit].IndexOfAny(_firstBytes!);
                if (found < 0)
                {
                    return -1;
                }

                int candidate = from + found;
                length = MatchSlots(data[candidate..]);
                if (length >= 0)
                {
                    return candidate;
                }

                from = candidate + 1;
            }

            return -1;
        }

        length = Bytes.Length;
        if (Mask is null)
        {
            int found = data[from..].IndexOf(Bytes);
            return found < 0 ? -1 : from + found;
        }

        if (AnchorLength == 0)
        {
            if (_probeValues is not null)
            {
                // ワイルドカードでないバイトがない (ニブル・ビットマスク): 最も絞り込める位置のバイトの値の候補で探す (FIND-16)。
                int probeFrom = from + _probeOffset;
                int probeLimit = data.Length - Bytes.Length + _probeOffset + 1; // 候補の位置の終わり (この位置を含まない)
                while (probeFrom < probeLimit)
                {
                    int found = data[probeFrom..probeLimit].IndexOfAny(_probeValues);
                    if (found < 0)
                    {
                        return -1;
                    }

                    int candidate = probeFrom + found - _probeOffset;
                    if (MatchLength(data[candidate..]) >= 0)
                    {
                        return candidate;
                    }

                    probeFrom += found + 1;
                }

                return -1;
            }

            // すべてワイルドカード (可変長の間の `??` など): 先頭から順に照合する。
            for (int i = from; i + Bytes.Length <= data.Length; i++)
            {
                if (MatchLength(data[i..]) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        // ワイルドカードのない最も長い区間で候補を絞り込む (FIND-06 の「巨大ファイル」)。
        ReadOnlySpan<byte> anchor = Bytes.AsSpan(AnchorOffset, AnchorLength);
        int searchFrom = from + AnchorOffset;
        int anchorLimit = data.Length - Bytes.Length + AnchorOffset + AnchorLength;
        while (searchFrom + AnchorLength <= anchorLimit)
        {
            int found = data[searchFrom..anchorLimit].IndexOf(anchor);
            if (found < 0)
            {
                return -1;
            }

            int candidate = searchFrom + found - AnchorOffset;
            if (MatchLength(data[candidate..]) >= 0)
            {
                return candidate;
            }

            searchFrom += found + 1;
        }

        return -1;
    }

    /// <summary>開始が <paramref name="end"/> 未満の最後の一致。</summary>
    private int LastIndexOfUnaligned(ReadOnlySpan<byte> data, int end, long baseOffset, out int length)
    {
        length = 0;
        if (_mismatch is { } mismatch)
        {
            length = 1;
            return mismatch.LastIndexOf(data, end, baseOffset);
        }

        if (_multi is not null)
        {
            return _multi.LastIndexOf(data, end, baseOffset, out length);
        }

        if (_parts is not null)
        {
            SearchPattern first = _parts[0];
            int limit = Math.Min(end, data.Length);
            while (limit > 0)
            {
                int candidate = first.LastIndexOfUnaligned(data, limit, baseOffset, out _);
                if (candidate < 0)
                {
                    return -1;
                }

                int matchEnd = MatchParts(data, 0, candidate);
                if (matchEnd >= 0)
                {
                    length = matchEnd - candidate;
                    return candidate;
                }

                limit = candidate;
            }

            return -1;
        }

        if (_matcher is not null)
        {
            int n = _matcher.Length;
            long step = Position is { IsNone: false } p ? p.Modulus : 1;
            long last = Math.Min(end - 1, data.Length - n);
            if (step > 1 && last >= 0)
            {
                last = Position!.PreviousAccepted(baseOffset + last) - baseOffset;
            }

            for (long i = last; i >= 0; i -= step)
            {
                if (_matcher.Match(data[(int)i..]) >= 0)
                {
                    length = n;
                    return (int)i;
                }
            }

            return -1;
        }

        if (_slots is not null)
        {
            int limit = Math.Min(end, data.Length - MinMatchLength + 1);
            while (limit > 0)
            {
                int candidate = data[..limit].LastIndexOfAny(_firstBytes!);
                if (candidate < 0)
                {
                    return -1;
                }

                length = MatchSlots(data[candidate..]);
                if (length >= 0)
                {
                    return candidate;
                }

                limit = candidate;
            }

            return -1;
        }

        length = Bytes.Length;
        int lastStart = Math.Min(end - 1, data.Length - Bytes.Length);
        if (lastStart < 0)
        {
            return -1;
        }

        if (Mask is null)
        {
            return data[..(lastStart + Bytes.Length)].LastIndexOf(Bytes);
        }

        if (AnchorLength == 0)
        {
            if (_probeValues is not null)
            {
                int probeEnd = lastStart + _probeOffset + 1; // 候補の位置の終わり (この位置を含まない)
                while (probeEnd > _probeOffset)
                {
                    int found = data[_probeOffset..probeEnd].LastIndexOfAny(_probeValues);
                    if (found < 0)
                    {
                        return -1;
                    }

                    if (MatchLength(data[found..]) >= 0)
                    {
                        return found;
                    }

                    probeEnd = _probeOffset + found;
                }

                return -1;
            }

            for (int i = lastStart; i >= 0; i--)
            {
                if (MatchLength(data[i..]) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        ReadOnlySpan<byte> anchor = Bytes.AsSpan(AnchorOffset, AnchorLength);
        int anchorEnd = lastStart + AnchorOffset + AnchorLength; // 候補の区間の終わり (この位置を含まない)
        while (anchorEnd - AnchorLength >= AnchorOffset)
        {
            int found = data[AnchorOffset..anchorEnd].LastIndexOf(anchor);
            if (found < 0)
            {
                return -1;
            }

            int candidate = found; // data[AnchorOffset + found] が区間の先頭なので、一致の開始は found
            if (MatchLength(data[candidate..]) >= 0)
            {
                return candidate;
            }

            anchorEnd = AnchorOffset + found + AnchorLength - 1;
        }

        return -1;
    }

    /// <summary>
    /// 部分 <paramref name="index"/> が <paramref name="at"/> から始まるとして、残りの部分を照合する。一致すれば一致の末尾
    /// (データの先頭からの位置)、しなければ −1。間の長さは短いものから試す (最短一致)。
    /// </summary>
    private int MatchParts(ReadOnlySpan<byte> data, int index, int at)
    {
        SearchPattern part = _parts![index];
        int len = part.MatchLength(data[at..]);
        if (len < 0)
        {
            return -1;
        }

        int end = at + len;
        if (index == _parts.Length - 1)
        {
            return end;
        }

        (int min, int max) = _gaps![index];
        SearchPattern next = _parts[index + 1];
        long lo = (long)end + min;
        long hi = Math.Min((long)end + max, data.Length - next.MinMatchLength);
        if (lo > hi)
        {
            return -1;
        }

        // 次の部分の開始が hi 以下になるよう、探す範囲を切る。
        ReadOnlySpan<byte> window = data[..(int)Math.Min(data.Length, hi + next.MaxMatchLength)];
        int from = (int)lo;
        while (from <= hi)
        {
            int candidate = next.IndexOfUnaligned(window, from, 0, out _);
            if (candidate < 0 || candidate > hi)
            {
                return -1;
            }

            int result = MatchParts(data, index + 1, candidate);
            if (result >= 0)
            {
                return result;
            }

            from = candidate + 1;
        }

        return -1;
    }

    /// <summary>
    /// 文字ごとの候補を先頭から照合する。各文字の候補は、文字コードの性質 (UTF-8・UTF-16・2 バイト文字コードは、ある文字の
    /// 符号化が別の文字の符号化の先頭部分にならない) から高々 1 つしか一致しないため、後戻りしない。
    /// </summary>
    private int MatchSlots(ReadOnlySpan<byte> data)
    {
        int at = 0;
        foreach (byte[][] alternatives in _slots!)
        {
            int matched = -1;
            foreach (byte[] alternative in alternatives)
            {
                if (data[at..].StartsWith(alternative))
                {
                    matched = alternative.Length;
                    break;
                }
            }

            if (matched < 0)
            {
                return -1;
            }

            at += matched;
        }

        return at;
    }

    private SearchPattern With(PatternWarnings warnings)
    {
        if (warnings == PatternWarnings.None)
        {
            return this;
        }

        var copy = (SearchPattern)MemberwiseClone();
        copy.Warnings = warnings;
        return copy;
    }

    /// <summary>数値の検索の条件を付けた写し。</summary>
    internal SearchPattern WithNumeric(NumericSearchInfo info)
    {
        var copy = (SearchPattern)MemberwiseClone();
        copy.Numeric = info;
        return copy;
    }

    private static SearchPattern BuildFixed(List<HexItem> items)
    {
        byte[] bytes = [.. items.Select(i => i.Value)];
        bool anyWildcard = items.Any(i => i.Mask != 0xFF);
        return new SearchPattern(bytes, anyWildcard ? [.. items.Select(i => i.Mask)] : null, null, 1);
    }

    private static void CheckLength(int length)
    {
        if (length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (length > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }
    }

    /// <summary>Hex の検索語の 1 単位: 1 バイト (値とマスク)、または可変長のワイルドカード。</summary>
    private readonly record struct HexItem(byte Value, byte Mask, bool IsGap, int GapMin, int GapMax, int Position);

    /// <summary>Hex 文字列をバイトと可変長のワイルドカードに分ける。誤りは位置を付けて投げる。</summary>
    private static List<HexItem> ParseHexItems(string text, int maxGap, bool allowWildcards)
    {
        var items = new List<HexItem>();
        var digits = new List<(char Digit, int Position)>();
        int i = 0;
        int position = 0; // 1 から数えた文字の位置 (サロゲートペアは 1 文字)

        void FlushDigits()
        {
            if (digits.Count % 2 != 0)
            {
                // 対にならない最後の桁の位置を示す。
                throw new PatternException(PatternError.OddDigits, digits[^1].Digit.ToString(), digits[^1].Position);
            }

            for (int d = 0; d < digits.Count; d += 2)
            {
                (int hi, int hiMask) = Nibble(digits[d].Digit);
                (int lo, int loMask) = Nibble(digits[d + 1].Digit);
                items.Add(new HexItem((byte)((hi << 4) | lo), (byte)((hiMask << 4) | loMask), false, 0, 0, digits[d].Position));
            }

            digits.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];
            position++;
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
                continue;
            }

            if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' && (digits.Count % 2 == 0))
            {
                i += 2;
                position++;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
            {
                i += 2;
                position++;
                continue;
            }

            if (c == '*' && allowWildcards)
            {
                int starPosition = position;
                if (digits.Count % 2 != 0)
                {
                    throw new PatternException(PatternError.OddDigits, digits[^1].Digit.ToString(), digits[^1].Position);
                }

                FlushDigits();
                i++;
                int min = 0;
                int max = maxGap;
                if (i < text.Length && text[i] == '{')
                {
                    int close = text.IndexOf('}', i);
                    if (close < 0)
                    {
                        throw new PatternException(PatternError.InvalidWildcardRange, text[(i - 1)..], starPosition,
                            [maxGap.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)]);
                    }

                    string range = text[(i + 1)..close];
                    string[] bounds = range.Split(',');
                    bool ok = bounds.Length is 1 or 2
                        && int.TryParse(bounds[0].Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out min);
                    if (ok && bounds.Length == 2)
                    {
                        string upper = bounds[1].Trim();
                        ok = upper.Length == 0 || int.TryParse(upper, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out max);
                    }
                    else if (ok)
                    {
                        max = min;
                    }

                    if (!ok || min > max || max > maxGap)
                    {
                        throw new PatternException(PatternError.InvalidWildcardRange, text[(i - 1)..(close + 1)], starPosition,
                            [maxGap.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)]);
                    }

                    position += close - i + 1;
                    i = close + 1;
                }

                items.Add(new HexItem(0, 0, true, min, max, starPosition));
                continue;
            }

            if (!char.IsAsciiHexDigit(c) && !(c == '?' && allowWildcards))
            {
                string bad = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? text.Substring(i, 2)
                    : c.ToString();
                throw new PatternException(PatternError.InvalidCharacter, bad, position);
            }

            digits.Add((c, position));
            i++;
        }

        FlushDigits();
        if (items.Count(it => !it.IsGap) > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        return items;
    }

    /// <summary>表せない文字で例外を投げる文字コード。BOM は GetBytes では付かない。</summary>
    internal static Encoding StrictEncoding(Encoding encoding)
    {
        var strict = (Encoding)encoding.Clone();
        strict.EncoderFallback = EncoderFallback.ExceptionFallback;
        return strict;
    }

    private static byte[] Encode(Encoding strict, Rune rune, int position) =>
        TryEncode(strict, rune) ?? throw new PatternException(PatternError.NotEncodable, rune.ToString(), position);

    private static byte[]? TryEncode(Encoding strict, Rune rune)
    {
        Span<char> chars = stackalloc char[2];
        int n = rune.EncodeToUtf16(chars);
        try
        {
            byte[] bytes = strict.GetBytes(chars[..n].ToArray());
            return bytes.Length == 0 ? null : bytes;
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static byte[] Concat(List<byte[]> parts)
    {
        long total = parts.Sum(p => (long)p.Length);
        if (total > MaxLength)
        {
            throw new PatternException(PatternError.TooLong);
        }

        byte[] result = new byte[total];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    /// <summary>
    /// テキストの検索語をエスケープ表記ごと符号化する (置換語の符号化。FIND-22 の仕様 2)。表せない文字はエラーにする。
    /// 空なら長さ 0。
    /// </summary>
    public static byte[] EncodeText(string text, Encoding encoding, bool useEscapes)
    {
        if (text.Length == 0)
        {
            return [];
        }

        Encoding strict = StrictEncoding(encoding);
        var parts = new List<byte[]>();
        foreach (TextToken token in Tokenize(text, useEscapes))
        {
            parts.Add(token.IsByte ? [token.Byte] : Encode(strict, token.Rune, token.Position));
        }

        return Concat(parts);
    }

    /// <summary>検索語の 1 単位: 1 文字、またはエスケープ `\xHH` の 1 バイト。<see cref="Position"/> は入力の何文字目か。</summary>
    private readonly record struct TextToken(Rune Rune, byte Byte, bool IsByte, int Position);

    /// <summary>入力を文字 (とエスケープのバイト) に分ける。対にならないサロゲートは表せない文字の誤りにする。</summary>
    private static List<TextToken> Tokenize(string text, bool useEscapes)
    {
        var tokens = new List<TextToken>(text.Length);
        int i = 0;
        int position = 0;
        while (i < text.Length)
        {
            position++;
            int tokenPosition = position;
            if (useEscapes && text[i] == '\\')
            {
                if (i + 1 >= text.Length)
                {
                    throw new PatternException(PatternError.InvalidEscape, "\\", tokenPosition);
                }

                char e = text[i + 1];
                switch (e)
                {
                    case 'n': tokens.Add(Char('\n')); i += 2; position++; continue;
                    case 'r': tokens.Add(Char('\r')); i += 2; position++; continue;
                    case 't': tokens.Add(Char('\t')); i += 2; position++; continue;
                    case '0': tokens.Add(Char('\0')); i += 2; position++; continue;
                    case '\\': tokens.Add(Char('\\')); i += 2; position++; continue;
                    case 'x' or 'X':
                        if (!TryHex(text, i + 2, 2, out int b))
                        {
                            throw new PatternException(PatternError.InvalidEscape, Snippet(text, i, 4), tokenPosition);
                        }

                        tokens.Add(new TextToken(default, (byte)b, true, tokenPosition));
                        i += 4;
                        position += 3;
                        continue;
                    case 'u' or 'U':
                        if (!TryHex(text, i + 2, 4, out int u))
                        {
                            throw new PatternException(PatternError.InvalidEscape, Snippet(text, i, 6), tokenPosition);
                        }

                        i += 6;
                        position += 5;
                        if (char.IsHighSurrogate((char)u) && i + 1 < text.Length && text[i] == '\\' && text[i + 1] is 'u' or 'U'
                            && TryHex(text, i + 2, 4, out int low) && char.IsLowSurrogate((char)low))
                        {
                            // 上位と下位のサロゲートを続けて書いたもの (U+1F600 なら D83D と DE00) は 1 文字にまとめる。
                            tokens.Add(new TextToken(new Rune((char)u, (char)low), 0, false, tokenPosition));
                            i += 6;
                            position += 6;
                            continue;
                        }

                        if (!Rune.IsValid(u))
                        {
                            throw new PatternException(PatternError.NotEncodable, Snippet(text, i - 6, 6), tokenPosition);
                        }

                        tokens.Add(new TextToken(new Rune(u), 0, false, tokenPosition));
                        continue;
                    default:
                        throw new PatternException(PatternError.InvalidEscape, Snippet(text, i, 2), tokenPosition);
                }
            }

            if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune rune, out int consumed) != OperationStatus.Done)
            {
                throw new PatternException(PatternError.NotEncodable, text[i].ToString(), tokenPosition);
            }

            tokens.Add(new TextToken(rune, 0, false, tokenPosition));
            i += consumed;

            TextToken Char(char c) => new(new Rune(c), 0, false, tokenPosition);
        }

        return tokens;
    }

    private static bool TryHex(string text, int start, int digits, out int value)
    {
        value = 0;
        if (start + digits > text.Length)
        {
            return false;
        }

        for (int k = 0; k < digits; k++)
        {
            char c = text[start + k];
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            value = (value << 4) | Convert.ToInt32(c.ToString(), 16);
        }

        return true;
    }

    private static string Snippet(string text, int start, int length) => text.Substring(start, Math.Min(length, text.Length - start));

    private static (int Value, int Mask) Nibble(char c) => c == '?' ? (0, 0) : (Convert.ToInt32(c.ToString(), 16), 0xF);

    /// <summary>
    /// 候補の絞り込みに使う位置: マスクが 0 でないバイトのうち、一致しうる値の数 (2^(8 − マスクのビット数)) が最も少ないもの。
    /// </summary>
    private static (int Offset, SearchValues<byte>? Values) FindProbe(byte[] bytes, byte[] mask)
    {
        int best = -1;
        int bestBits = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            int bits = System.Numerics.BitOperations.PopCount(mask[i]);
            if (bits > bestBits)
            {
                best = i;
                bestBits = bits;
            }
        }

        if (best < 0)
        {
            return (-1, null);
        }

        byte m = mask[best];
        byte v = (byte)(bytes[best] & m);
        var values = new List<byte>(256 >> bestBits);
        for (int b = 0; b < 256; b++)
        {
            if ((b & m) == v)
            {
                values.Add((byte)b);
            }
        }

        return (best, SearchValues.Create([.. values]));
    }

    private static (int Offset, int Length) FindAnchor(byte[]? mask, int length)
    {
        if (mask is null)
        {
            return (0, length);
        }

        int bestStart = 0;
        int bestLength = 0;
        int runStart = 0;
        for (int i = 0; i <= length; i++)
        {
            if (i == length || mask[i] != 0xFF)
            {
                if (i - runStart > bestLength)
                {
                    bestStart = runStart;
                    bestLength = i - runStart;
                }

                runStart = i + 1;
            }
        }

        return (bestStart, bestLength);
    }
}

/// <summary>
/// 各位置で値を復号して比べる照合 (許容誤差のある浮動小数点、NaN など。FIND-14 の仕様 8)。<see cref="Match"/> は
/// <paramref name="data"/> の先頭 <see cref="Length"/> バイトが一致すれば種類の番号 (0 から)、しなければ −1 を返す。
/// </summary>
internal abstract class ValueMatcher(int length)
{
    public int Length { get; } = length;

    public abstract int Match(ReadOnlySpan<byte> data);
}
