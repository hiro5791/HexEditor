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
}

/// <summary>検索語の誤り。<see cref="Position"/> は誤りのある文字の位置 (1 から数えた文字数。FIND-04 の仕様 6)。</summary>
public sealed class PatternException : Exception
{
    public PatternException(PatternError error, string detail = "")
        : this(error, detail, null)
    {
    }

    public PatternException(PatternError error, string detail, int? position)
        : base(position is int p ? $"{error}: {detail} ({p})" : $"{error}: {detail}")
    {
        Error = error;
        Detail = detail;
        Position = position;
    }

    public PatternError Error { get; }

    /// <summary>表せない文字など、説明文に入れる語。</summary>
    public string Detail { get; }

    /// <summary>
    /// 誤りのある文字が検索語の何文字目か (1 から数える。サロゲートペアは 1 文字)。位置を示せない誤り (空、長すぎる) は null。
    /// </summary>
    public int? Position { get; }
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
}

/// <summary>
/// 検索するバイト列。次の 3 種類がある。
/// <list type="bullet">
/// <item>リテラル: <see cref="Bytes"/> そのもの。</item>
/// <item>ワイルドカード: <see cref="Mask"/> のビットが 1 の部分だけを比べる (FIND-06)。</item>
/// <item>文字ごとの候補: 大文字・小文字を区別しないテキスト (FIND-10)。文字ごとに、変種を符号化したバイト列のどれかに一致する。</item>
/// </list>
/// </summary>
public sealed class SearchPattern
{
    /// <summary>検索語の最大の長さ (FIND-05 の仕様 3)。</summary>
    public const int MaxLength = 1024 * 1024;

    /// <summary>文字ごとの候補 (候補がなければ null)。各要素は 1 文字 (またはエスケープの 1 バイト) の候補のバイト列。</summary>
    private readonly byte[][][]? _slots;

    /// <summary>先頭のバイトの候補 (文字ごとの候補のときだけ。FIND-01 の仕様 6)。</summary>
    private readonly SearchValues<byte>? _firstBytes;

    private SearchPattern(byte[] bytes, byte[]? mask, byte[][][]? slots, int alignment)
    {
        Bytes = bytes;
        Mask = mask;
        _slots = slots;
        Alignment = Math.Max(1, alignment);
        (AnchorOffset, AnchorLength) = FindAnchor(mask, bytes.Length);
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

    /// <summary>
    /// 検索語のバイト列。文字ごとの候補のときは、入力したとおりの文字を符号化したもの (変換結果の表示用。FIND-04 の仕様 7)。
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>比べるビット。null ならすべて比べる (リテラル)。</summary>
    public byte[]? Mask { get; }

    /// <summary>検索語のバイト数 (<see cref="Bytes"/> の長さ)。一致の長さは <see cref="MinMatchLength"/>〜<see cref="MaxMatchLength"/>。</summary>
    public int Length => Bytes.Length;

    /// <summary>ワイルドカードも文字ごとの候補もない (SIMD の IndexOf でそのまま探せる。FIND-01 の仕様 5)。</summary>
    public bool IsLiteral => Mask is null && _slots is null;

    /// <summary>大文字・小文字を区別しない (文字ごとの候補を持つ)。</summary>
    public bool IsCaseInsensitive => _slots is not null;

    /// <summary>一致の最短の長さ。</summary>
    public int MinMatchLength { get; }

    /// <summary>一致の最長の長さ。チャンクの重なり幅は これ − 1 (FIND-01 の仕様 3)。</summary>
    public int MaxMatchLength { get; }

    /// <summary>一致の開始オフセットが満たすべき倍数 (文字の境界に揃える。FIND-07 の仕様 5)。1 なら制限なし。</summary>
    public int Alignment { get; }

    /// <summary>ワイルドカードを含まない最も長い区間 (候補の絞り込みに使う)。</summary>
    internal int AnchorOffset { get; }

    internal int AnchorLength { get; }

    public static SearchPattern Literal(byte[] bytes)
    {
        CheckLength(bytes.Length);
        return new SearchPattern(bytes, null, null, 1);
    }

    /// <summary>
    /// Hex 文字列から作る (00-overview 6.3、FIND-05、FIND-06)。空白・カンマ・改行・`0x`・`\x` は区切り。
    /// `??` は任意の 1 バイト、`?` は任意の 1 ニブル。
    /// </summary>
    public static SearchPattern FromHex(string text)
    {
        var bytes = new List<byte>();
        var mask = new List<byte>();
        var digits = new List<char>();
        int lastDigitPosition = 0;
        int i = 0;
        int position = 0; // 1 から数えた文字の位置 (サロゲートペアは 1 文字)
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

            if (!char.IsAsciiHexDigit(c) && c != '?')
            {
                string bad = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? text.Substring(i, 2)
                    : c.ToString();
                throw new PatternException(PatternError.InvalidCharacter, bad, position);
            }

            digits.Add(c);
            lastDigitPosition = position;
            i++;
        }

        if (digits.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (digits.Count % 2 != 0)
        {
            // 対にならない最後の桁の位置を示す。
            throw new PatternException(PatternError.OddDigits, digits[^1].ToString(), lastDigitPosition);
        }

        bool anyWildcard = false;
        for (int d = 0; d < digits.Count; d += 2)
        {
            (int hi, int hiMask) = Nibble(digits[d]);
            (int lo, int loMask) = Nibble(digits[d + 1]);
            bytes.Add((byte)((hi << 4) | lo));
            mask.Add((byte)((hiMask << 4) | loMask));
            anyWildcard |= hiMask == 0 || loMask == 0;
        }

        CheckLength(bytes.Count);
        return new SearchPattern([.. bytes], anyWildcard ? [.. mask] : null, null, 1);
    }

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
            return new SearchPattern(bytes, null, null, alignment);
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
        return new SearchPattern(bytes, null, anyAlternative ? slots : null, alignment);
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
    public int MatchLength(ReadOnlySpan<byte> data)
    {
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
    /// バッファの中で最初の一致の位置。一致はバッファの中にすっかり収まるものだけ。<paramref name="baseOffset"/> は
    /// バッファの先頭のドキュメント上の位置 (文字の境界の判定に使う)。
    /// </summary>
    internal int IndexOf(ReadOnlySpan<byte> data, long baseOffset, out int length)
    {
        int from = 0;
        while (true)
        {
            int found = IndexOfUnaligned(data, from, out length);
            if (found < 0 || Alignment == 1 || (baseOffset + found) % Alignment == 0)
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
            int found = LastIndexOfUnaligned(data, end, out length);
            if (found < 0 || Alignment == 1 || (baseOffset + found) % Alignment == 0)
            {
                return found;
            }

            end = found;
        }
    }

    /// <summary>開始が <paramref name="from"/> 以上の最初の一致。</summary>
    private int IndexOfUnaligned(ReadOnlySpan<byte> data, int from, out int length)
    {
        length = 0;
        if (data.Length - from < MinMatchLength)
        {
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
            // すべてワイルドカード (例: ?? ??): 先頭から順に照合する。
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
    private int LastIndexOfUnaligned(ReadOnlySpan<byte> data, int end, out int length)
    {
        length = 0;
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

    /// <summary>表せない文字で例外を投げる文字コード。BOM は GetBytes では付かない。</summary>
    private static Encoding StrictEncoding(Encoding encoding)
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
