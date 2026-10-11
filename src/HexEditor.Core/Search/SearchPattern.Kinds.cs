using System.Buffers;
using System.Globalization;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Search;

/// <summary>結果一覧の「種類」の列の見出し (どの種類で一致したか)。</summary>
public enum VariantColumn
{
    /// <summary>エンディアン (FIND-13 の「両方」)。</summary>
    Endian,

    /// <summary>文字コード (FIND-08 の仕様 5、FIND-32 の仕様 3)。</summary>
    Encoding,

    /// <summary>検索語 (FIND-26 の仕様 7)。</summary>
    Term,

    /// <summary>値の解釈 (範囲の検索の符号とエンディアン。FIND-15 の仕様 3)。</summary>
    Interpretation,
}

/// <summary>
/// 位置の条件「オフセット mod x = y」(FIND-17)。<see cref="BaseAddress"/> は基準が「表示上のアドレス」のときのベースアドレス
/// (ドキュメントのオフセットなら 0)。x は 1〜2^32、y は 0〜x − 1。x = 1 は「条件なし」。
/// </summary>
public sealed record PositionCondition
{
    /// <summary>周期の上限 (FIND-17 の仕様 1)。</summary>
    public const long MaxModulus = 1L << 32;

    private PositionCondition(long modulus, long remainder, long baseAddress)
    {
        Modulus = modulus;
        Remainder = remainder;
        BaseAddress = baseAddress;
    }

    /// <summary>条件なし (x = 1)。</summary>
    public static PositionCondition None { get; } = new(1, 0, 0);

    public long Modulus { get; }

    public long Remainder { get; }

    public long BaseAddress { get; }

    public bool IsNone => Modulus <= 1;

    /// <summary>条件を作る。x が 1〜2^32 でない、y が 0〜x − 1 でない場合はエラー (FIND-17 の「エラー」)。</summary>
    public static PositionCondition Create(long modulus, long remainder, long baseAddress = 0)
    {
        if (modulus < 1 || modulus > MaxModulus)
        {
            throw new PatternException(PatternError.PositionModulus, modulus.ToString(CultureInfo.InvariantCulture), null,
                [MaxModulus.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        if (remainder < 0 || remainder >= modulus)
        {
            throw new PatternException(PatternError.PositionRemainder, remainder.ToString(CultureInfo.InvariantCulture), null,
                [(modulus - 1).ToString("N0", CultureInfo.CurrentCulture)]);
        }

        return modulus == 1 && baseAddress == 0 ? None : new PositionCondition(modulus, remainder, Math.Max(0, baseAddress));
    }

    /// <summary>ドキュメント上の位置 <paramref name="offset"/> が条件を満たすか。</summary>
    public bool Accepts(long offset) => IsNone || Mod(offset) == Remainder;

    /// <summary><paramref name="offset"/> 以上で条件を満たす最初の位置。</summary>
    public long NextAccepted(long offset) => IsNone ? offset : offset + ((Remainder - Mod(offset) + Modulus) % Modulus);

    /// <summary><paramref name="offset"/> 以下で条件を満たす最後の位置 (負になることがある)。</summary>
    public long PreviousAccepted(long offset) => IsNone ? offset : offset - ((Mod(offset) - Remainder + Modulus) % Modulus);

    private long Mod(long offset) => (long)((((ulong)offset % (ulong)Modulus) + ((ulong)BaseAddress % (ulong)Modulus)) % (ulong)Modulus);
}

/// <summary>ビットマスクの入力方法 (FIND-16 の仕様 1)。</summary>
public enum MaskInputMode
{
    /// <summary>値とマスクを Hex で入力する。</summary>
    ValueAndMask,

    /// <summary>`0` `1` `x` を 8 文字で 1 バイトとして並べる。</summary>
    BitPattern,
}

/// <summary>1 つのバッファでの一致 (開始はバッファの先頭からの位置)。</summary>
internal readonly record struct RawMatch(int Start, int Length, int Variant);

/// <summary>一致を受け取った側の判断。</summary>
internal enum MatchDecision
{
    /// <summary>一致として採った (重ならない一致なら、末尾の次から探す)。</summary>
    Accept,

    /// <summary>採らなかった (単語の境界でないなど)。次の位置から探す。</summary>
    Reject,

    /// <summary>探すのをやめる。</summary>
    Stop,
}

/// <summary>
/// 照合するバッファの周りの事情。正規表現 (FIND-18、FIND-19) は、バッファの前の文脈 (後読み・<c>\b</c>) をドキュメントから読み、
/// バッファの端が範囲の本当の端か (<c>$</c>) を知る必要がある。<see cref="CheckCancel"/> はキャンセルの確認 (要求されていれば
/// <see cref="OperationCanceledException"/> を投げる)。チャンクの中の区間ごと・正規表現の照合ごとに呼び、キャンセルの要求から
/// 200 ms 以内に止める (FIND-02 の仕様 3)。
/// </summary>
internal readonly record struct ScanContext(DocumentSnapshot? Snapshot, long RangeStart, long RangeEnd, bool StartIsBoundary, bool EndIsBoundary,
    bool ForView = false, System.Diagnostics.Stopwatch? Clock = null, Action? CheckCancel = null);

/// <summary>
/// 複数の一致をまとめて求める照合 (複数語・複数の文字コード・正規表現)。<see cref="Gather"/> は、開始が [from, coreEnd) の一致を
/// すべて (重なるものも) 返す。<paramref name="overlapping"/> が false なら、重ならない一致だけを返してもよい。
/// </summary>
internal abstract class MultiMatcher
{
    public abstract void Gather(ReadOnlySpan<byte> data, long baseOffset, int from, int coreEnd, bool overlapping, in ScanContext context,
        List<RawMatch> sink);

    /// <summary>先頭 (ドキュメント上の位置 <paramref name="baseOffset"/>) で一致する長さと種類。一致しなければ −1。</summary>
    public abstract int MatchLength(ReadOnlySpan<byte> data, long baseOffset, out int variant);

    public virtual int IndexOf(ReadOnlySpan<byte> data, int from, long baseOffset, out int length)
    {
        var list = new List<RawMatch>();
        Gather(data, baseOffset, from, data.Length, true, new ScanContext(null, baseOffset, baseOffset + data.Length, true, true), list);
        RawMatch? best = null;
        foreach (RawMatch m in list)
        {
            if (best is null || m.Start < best.Value.Start || (m.Start == best.Value.Start && m.Variant < best.Value.Variant))
            {
                best = m;
            }
        }

        length = best?.Length ?? 0;
        return best?.Start ?? -1;
    }

    public virtual int LastIndexOf(ReadOnlySpan<byte> data, int end, long baseOffset, out int length)
    {
        var list = new List<RawMatch>();
        Gather(data, baseOffset, 0, Math.Min(end, data.Length), true, new ScanContext(null, baseOffset, baseOffset + data.Length, true, true), list);
        RawMatch? best = null;
        foreach (RawMatch m in list)
        {
            if (best is null || m.Start > best.Value.Start || (m.Start == best.Value.Start && m.Variant < best.Value.Variant))
            {
                best = m;
            }
        }

        length = best?.Length ?? 0;
        return best?.Start ?? -1;
    }
}

public sealed partial class SearchPattern
{
    /// <summary>複数の一致をまとめて求める照合 (複数語・複数の文字コード・正規表現)。</summary>
    private MultiMatcher? _multi;

    /// <summary>一致しない箇所の検索 (FIND-25)。</summary>
    private MismatchMatcher? _mismatch;

    /// <summary>まとめて求める照合の 1 回の単位 (開始位置の数)。メモリを一定に保つため、チャンクをさらに分けて照合する。</summary>
    internal const int SubWindow = 256 * 1024;

    /// <summary>位置の条件 (FIND-17)。null なら条件なし。</summary>
    public PositionCondition? Position { get; private set; }

    /// <summary>結果一覧の種類の列の見出し。</summary>
    public VariantColumn VariantColumn { get; private set; } = VariantColumn.Endian;

    /// <summary>複数語・複数の文字コードの、語 (文字コード) ごとのパターン (<see cref="Variants"/> と同じ順)。それ以外は空。</summary>
    public IReadOnlyList<SearchPattern> Parts { get; private set; } = [];

    /// <summary>複数の一致をまとめて求める照合か (複数語・複数の文字コード・正規表現)。</summary>
    internal bool IsMulti => _multi is not null;

    /// <summary>正規表現か (FIND-18、FIND-19)。</summary>
    public bool IsRegex => _multi is RegexMatcher;

    /// <summary>正規表現の照合 (正規表現でなければ null)。</summary>
    internal RegexMatcher? Regex => _multi as RegexMatcher;

    /// <summary>一致しない箇所の検索か (FIND-25)。</summary>
    public bool IsMismatch => _mismatch is not null;

    /// <summary>一致しない箇所の検索の繰り返しのパターンの長さ (一致しない箇所の検索でなければ 0)。</summary>
    internal MismatchMatcher? MismatchSpec => _mismatch;

    /// <summary>
    /// 一致の前に読む文脈のバイト数 (正規表現の後読み・<c>\b</c>・<c>^</c> の判定)。0 なら読まない。
    /// </summary>
    internal int LeadingContext => _multi is RegexMatcher regex ? regex.LeadingContext : 0;

    /// <summary>位置の条件を付けた写し (FIND-17)。条件なしなら条件を外す。</summary>
    public SearchPattern WithPosition(PositionCondition? condition)
    {
        var copy = (SearchPattern)MemberwiseClone();
        copy.Position = condition is { IsNone: true } ? null : condition;
        return copy;
    }

    /// <summary>
    /// 検索の開始位置に合わせた写し。一致しない箇所の検索で「起点を P の長さの倍数のオフセットに揃える」がオフなら、繰り返しの起点を
    /// 開始位置にする (FIND-25 の仕様 2)。それ以外は自分自身。
    /// </summary>
    public SearchPattern ForStart(long start)
    {
        if (_mismatch is not { Aligned: false } mismatch || mismatch.Origin == start)
        {
            return this;
        }

        var copy = (SearchPattern)MemberwiseClone();
        copy._mismatch = mismatch.WithOrigin(start);
        return copy;
    }

    /// <summary>種類 <paramref name="variant"/> の単語の境界 (複数語・複数の文字コードでは語ごと)。null なら調べない。</summary>
    public WordBoundary? WordFor(int variant) =>
        _multi is UnionMatcher && variant >= 0 && variant < Parts.Count ? Parts[variant].Word : Word;

    // ---- ビットマスク (FIND-16) ----

    /// <summary>マスクの長さの上限 (FIND-16 の仕様 2)。</summary>
    public const int MaxMaskLength = 256;

    /// <summary>
    /// 値とマスク (どちらも Hex、同じ長さ) から作る (FIND-16 の仕様 1)。各バイトで <c>(データ &amp; M) == (V &amp; M)</c> のとき一致。
    /// マスクの誤りは <see cref="PatternException.InMask"/> を付けて投げる。
    /// </summary>
    public static SearchPattern FromValueAndMask(string value, string mask)
    {
        byte[] v = ParsePlainHex(value, inMask: false);
        byte[] m = ParsePlainHex(mask, inMask: true);
        if (m.Length > MaxMaskLength)
        {
            throw new PatternException(PatternError.MaskTooLong, string.Empty, null, [MaxMaskLength.ToString(CultureInfo.CurrentCulture)]) { InMask = true };
        }

        if (v.Length != m.Length)
        {
            throw new PatternException(PatternError.MaskLengthMismatch, string.Empty, null,
                [v.Length.ToString(CultureInfo.CurrentCulture), m.Length.ToString(CultureInfo.CurrentCulture)]) { InMask = true };
        }

        return FromMasked(v, m);
    }

    /// <summary>
    /// ビットパターン (<c>0</c>、<c>1</c>、<c>x</c> を 8 文字で 1 バイト。上位ビットから下位ビットの順。空白は区切り) から作る
    /// (FIND-16 の仕様 1・4)。
    /// </summary>
    public static SearchPattern FromBitPattern(string text)
    {
        var bits = new List<(char Bit, int Position)>();
        int position = 0;
        foreach (char c in text)
        {
            position++;
            if (char.IsWhiteSpace(c) || c is '_' or ',')
            {
                continue;
            }

            if (c is not ('0' or '1' or 'x' or 'X' or '?'))
            {
                throw new PatternException(PatternError.InvalidBit, c.ToString(), position);
            }

            bits.Add((c, position));
        }

        if (bits.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (bits.Count % 8 != 0)
        {
            throw new PatternException(PatternError.BitPatternLength, string.Empty, null, [bits.Count.ToString(CultureInfo.CurrentCulture)]);
        }

        int n = bits.Count / 8;
        if (n > MaxMaskLength)
        {
            throw new PatternException(PatternError.MaskTooLong, string.Empty, null, [MaxMaskLength.ToString(CultureInfo.CurrentCulture)]);
        }

        byte[] value = new byte[n];
        byte[] mask = new byte[n];
        for (int i = 0; i < bits.Count; i++)
        {
            int bit = 7 - (i % 8);
            char c = bits[i].Bit;
            if (c is '0' or '1')
            {
                mask[i / 8] |= (byte)(1 << bit);
                if (c == '1')
                {
                    value[i / 8] |= (byte)(1 << bit);
                }
            }
        }

        return FromMasked(value, mask);
    }

    private static SearchPattern FromMasked(byte[] value, byte[] mask)
    {
        if (mask.All(b => b == 0))
        {
            throw new PatternException(PatternError.MaskAllZero) { InMask = true };
        }

        byte[] bytes = new byte[value.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(value[i] & mask[i]);
        }

        // マスクがすべて FF ならリテラル (SIMD で探す)。
        return new SearchPattern(bytes, mask.All(b => b == 0xFF) ? null : mask, null, 1);
    }

    /// <summary>ワイルドカードなしの Hex 文字列 (00-overview 6.3) をバイト列にする。</summary>
    private static byte[] ParsePlainHex(string text, bool inMask)
    {
        try
        {
            List<HexItem> items = ParseHexItems(text, DefaultMaxMatchLength, allowWildcards: false);
            if (items.Count == 0)
            {
                throw new PatternException(PatternError.Empty);
            }

            return [.. items.Select(i => i.Value)];
        }
        catch (PatternException ex) when (inMask)
        {
            throw new PatternException(ex.Error, ex.Detail, ex.Position, ex.Arguments) { InMask = true };
        }
    }

    // ---- 複数語・複数の文字コード (FIND-08、FIND-26) ----

    /// <summary>同時に検索できる語の上限 (FIND-26 の仕様 2)。</summary>
    public const int MaxTerms = 10_000;

    /// <summary>複数の文字コードの上限 (FIND-08 の仕様 1)。</summary>
    public const int MaxEncodings = 8;

    /// <summary>
    /// いくつかのパターンを 1 回の読み込みで同時に探すパターン (FIND-26)。<paramref name="labels"/> は結果の「検索語」の列。
    /// 同じ位置で複数の語が一致した場合は、それぞれ別の結果になる (仕様 5)。リテラルの語は Aho-Corasick 法でまとめて照合し (仕様 4)、
    /// それ以外は語ごとに候補を絞り込んで照合する。正規表現・一致しない箇所の検索は入れられない (仕様 3)。
    /// </summary>
    public static SearchPattern Union(IReadOnlyList<SearchPattern> parts, IReadOnlyList<string> labels, VariantColumn column = VariantColumn.Term)
    {
        if (parts.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (parts.Count > MaxTerms)
        {
            throw new PatternException(PatternError.TooManyTerms, string.Empty, null, [MaxTerms.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        if (parts.Any(p => p.IsMulti || p.IsMismatch))
        {
            throw new ArgumentException("正規表現・複数語・一致しない箇所の検索は語にできません。", nameof(parts));
        }

        var union = new SearchPattern(parts[0].Bytes, null, null, 1)
        {
            Variants = [.. labels],
        };
        union._multi = new UnionMatcher(parts);
        union.Parts = [.. parts];
        union.VariantColumn = column;
        union._minMatchLength = parts.Min(p => p.MinMatchLength);
        union._maxMatchLength = parts.Max(p => p.MaxMatchLength);
        return union;
    }

    /// <summary>
    /// 同じ文字列を複数の文字コードで同時に探す (FIND-08)。符号化の結果が同じになる文字コードは 1 つにまとめ、名前を「 / 」でつなぐ
    /// (仕様 3)。符号化できない文字コードは除き、<paramref name="excluded"/> に名前を返す (仕様 6)。どれでも符号化できなければエラー。
    /// </summary>
    public static SearchPattern FromTextEncodings(string text, IReadOnlyList<(string Name, System.Text.Encoding Encoding)> encodings,
        TextSearchOptions options, out IReadOnlyList<string> excluded)
    {
        if (encodings.Count == 0)
        {
            throw new PatternException(PatternError.NoEncoding);
        }

        var parts = new List<SearchPattern>();
        var names = new List<string>();
        var keys = new List<string>();
        var skipped = new List<string>();
        PatternException? first = null;
        foreach ((string name, System.Text.Encoding encoding) in encodings)
        {
            SearchPattern part;
            try
            {
                part = FromText(text, encoding, options);
            }
            catch (PatternException ex) when (ex.Error == PatternError.NotEncodable)
            {
                first ??= ex;
                skipped.Add(name);
                continue;
            }

            string key = part.EncodingKey();
            int same = keys.IndexOf(key);
            if (same >= 0)
            {
                names[same] += " / " + name;
                continue;
            }

            keys.Add(key);
            parts.Add(part);
            names.Add(name);
        }

        excluded = skipped;
        if (parts.Count == 0)
        {
            throw first ?? new PatternException(PatternError.NoEncoding);
        }

        SearchPattern union = Union(parts, names, VariantColumn.Encoding);
        return union;
    }

    /// <summary>符号化の結果の比較の鍵 (候補のバイト列、文字の境界、単語の文字コード)。</summary>
    private string EncodingKey()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Convert.ToHexString(Bytes)).Append('|').Append(Alignment).Append('|');
        if (_slots is not null)
        {
            foreach (byte[][] slot in _slots)
            {
                sb.Append('[').AppendJoin(',', slot.Select(Convert.ToHexString)).Append(']');
            }
        }

        // 単語単位では、前後の文字の復号に文字コードを使う。1 バイトの文字コードどうしでも、英字の判定が同じとは限らないので、
        // UTF-8 と ASCII のように ASCII の範囲で同じものだけまとめる。
        sb.Append('|').Append(Word is null ? "-" : CharacterUnit(System.Text.Encoding.ASCII).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ---- 一致しない箇所 (FIND-25) ----

    /// <summary>
    /// 一致しない箇所の検索 (FIND-25)。<paramref name="repeat"/> (Hex、1〜256 バイト、ワイルドカード可) を繰り返したものとデータを比べ、
    /// 違うバイトを 1 バイトの一致として報告する。<paramref name="aligned"/> なら繰り返しの起点を「オフセット mod P の長さ = 0」の位置にし
    /// (仕様 2 の既定)、そうでなければ検索の開始位置 (<see cref="ForStart"/>) にする。
    /// </summary>
    public static SearchPattern Mismatch(string repeat, bool aligned)
    {
        List<HexItem> items = ParseHexItems(repeat, DefaultMaxMatchLength, allowWildcards: true);
        if (items.Count == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        if (items.Any(i => i.IsGap))
        {
            HexItem gap = items.First(i => i.IsGap);
            throw new PatternException(PatternError.InvalidReplacementWildcard, "*", gap.Position);
        }

        if (items.Count > MaxMaskLength)
        {
            throw new PatternException(PatternError.MaskTooLong, string.Empty, null, [MaxMaskLength.ToString(CultureInfo.CurrentCulture)]);
        }

        byte[] bytes = [.. items.Select(i => i.Value)];
        byte[] mask = [.. items.Select(i => i.Mask)];
        if (mask.All(m => m == 0))
        {
            throw new PatternException(PatternError.WildcardsOnly);
        }

        var pattern = new SearchPattern(bytes, null, null, 1)
        {
            _mismatch = new MismatchMatcher(bytes, mask, aligned, 0),
        };
        pattern._minMatchLength = pattern._maxMatchLength = 1;
        return pattern;
    }
}

/// <summary>
/// 一致しない箇所の照合 (FIND-25)。位置 q で比べるバイトは P[(q − 起点) mod P の長さ]。ワイルドカード (マスクが 0 のビット) は比べない。
/// P が 1 バイトでマスクがすべて FF なら <see cref="MemoryExtensions.IndexOfAnyExcept{T}(ReadOnlySpan{T}, T)"/> (SIMD) で探す。
/// </summary>
internal sealed class MismatchMatcher
{
    private readonly byte[] _pattern;
    private readonly byte[] _mask;
    private readonly bool _fullMask;
    private readonly SearchValues<byte>? _single;
    private byte[]? _repeated;

    public MismatchMatcher(byte[] pattern, byte[] mask, bool aligned, long origin)
    {
        _pattern = pattern;
        _mask = mask;
        Aligned = aligned;
        Origin = aligned ? 0 : origin;
        _fullMask = mask.All(m => m == 0xFF);
        if (pattern.Length == 1 && !_fullMask)
        {
            var values = new List<byte>();
            for (int b = 0; b < 256; b++)
            {
                if ((b & mask[0]) == (pattern[0] & mask[0]))
                {
                    values.Add((byte)b);
                }
            }

            _single = SearchValues.Create([.. values]);
        }
    }

    /// <summary>繰り返しのパターン P の長さ。</summary>
    public int Length => _pattern.Length;

    /// <summary>起点を P の長さの倍数のオフセットに揃える (FIND-25 の仕様 2。既定オン)。</summary>
    public bool Aligned { get; }

    /// <summary>繰り返しの起点 (揃える場合は 0)。</summary>
    public long Origin { get; }

    public MismatchMatcher WithOrigin(long origin) => new(_pattern, _mask, Aligned, origin);

    /// <summary>ドキュメント上の位置 <paramref name="offset"/> のバイトが P の繰り返しと同じか。</summary>
    public bool Matches(byte value, long offset)
    {
        int i = Phase(offset);
        return (value & _mask[i]) == (_pattern[i] & _mask[i]);
    }

    /// <summary>開始が <paramref name="from"/> 以上で、P の繰り返しと違う最初のバイトの位置 (なければ −1)。</summary>
    public int IndexOf(ReadOnlySpan<byte> data, int from, long baseOffset)
    {
        if (from >= data.Length)
        {
            return -1;
        }

        if (_pattern.Length == 1)
        {
            int found = _single is null ? data[from..].IndexOfAnyExcept(_pattern[0]) : data[from..].IndexOfAnyExcept(_single);
            return found < 0 ? -1 : from + found;
        }

        if (_fullMask)
        {
            // P を繰り返したバッファと比べる (共通の先頭部分の長さは SIMD で求める)。
            byte[] repeated = Repeated();
            int i = from;
            while (i < data.Length)
            {
                int phase = Phase(baseOffset + i);
                int n = Math.Min(data.Length - i, repeated.Length - phase);
                int common = data.Slice(i, n).CommonPrefixLength(repeated.AsSpan(phase, n));
                if (common < n)
                {
                    return i + common;
                }

                i += n;
            }

            return -1;
        }

        for (int i = from; i < data.Length; i++)
        {
            if (!Matches(data[i], baseOffset + i))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>開始が <paramref name="end"/> 未満で、P の繰り返しと違う最後のバイトの位置 (なければ −1)。</summary>
    public int LastIndexOf(ReadOnlySpan<byte> data, int end, long baseOffset)
    {
        end = Math.Min(end, data.Length);
        if (end <= 0)
        {
            return -1;
        }

        if (_pattern.Length == 1)
        {
            return _single is null ? data[..end].LastIndexOfAnyExcept(_pattern[0]) : data[..end].LastIndexOfAnyExcept(_single);
        }

        for (int i = end - 1; i >= 0; i--)
        {
            if (!Matches(data[i], baseOffset + i))
            {
                return i;
            }
        }

        return -1;
    }

    private int Phase(long offset)
    {
        long n = _pattern.Length;
        return (int)(((offset - Origin) % n + n) % n);
    }

    /// <summary>P を 4 KiB 以上になるまで繰り返したもの (P の長さの倍数 + P の長さ)。</summary>
    private byte[] Repeated()
    {
        if (_repeated is null)
        {
            int n = _pattern.Length;
            int count = (4096 / n) + 2;
            byte[] buffer = new byte[count * n];
            for (int k = 0; k < count; k++)
            {
                _pattern.CopyTo(buffer, k * n);
            }

            _repeated = buffer;
        }

        return _repeated;
    }
}

/// <summary>
/// 複数語・複数の文字コードの照合 (FIND-08 の仕様 3、FIND-26 の仕様 4・5)。リテラルの語は Aho-Corasick 法でまとめて照合し、
/// それ以外の語は語ごとに候補を絞り込む。語の位置の条件 (文字の境界) はここで調べ、単語の境界は呼び出し側が語ごとに調べる。
/// </summary>
internal sealed class UnionMatcher : MultiMatcher
{
    private readonly SearchPattern[] _parts;
    private readonly AhoCorasick? _literals;
    private readonly int[] _literalIds;
    private readonly int[] _others;
    private readonly int _maxLiteral;

    public UnionMatcher(IReadOnlyList<SearchPattern> parts)
    {
        _parts = [.. parts];
        var literal = new List<int>();
        var others = new List<int>();
        for (int i = 0; i < _parts.Length; i++)
        {
            (_parts[i].IsLiteral ? literal : others).Add(i);
        }

        _literalIds = [.. literal];
        _others = [.. others];
        if (literal.Count > 0)
        {
            _literals = new AhoCorasick([.. literal.Select(i => _parts[i].Bytes)]);
            _maxLiteral = literal.Max(i => _parts[i].Bytes.Length);
        }
    }

    public override void Gather(ReadOnlySpan<byte> data, long baseOffset, int from, int coreEnd, bool overlapping, in ScanContext context,
        List<RawMatch> sink)
    {
        coreEnd = Math.Min(coreEnd, data.Length);
        if (from >= coreEnd)
        {
            return;
        }

        if (_literals is not null)
        {
            int end = (int)Math.Min(data.Length, (long)coreEnd + _maxLiteral - 1);
            var hits = new List<(int End, int Id)>();
            _literals.Search(data[..end], from, hits);
            foreach ((int hitEnd, int id) in hits)
            {
                int variant = _literalIds[id];
                SearchPattern part = _parts[variant];
                int start = hitEnd - part.Bytes.Length;
                if (start >= from && start < coreEnd && part.Accepts(baseOffset + start))
                {
                    sink.Add(new RawMatch(start, part.Bytes.Length, variant));
                }
            }
        }

        foreach (int variant in _others)
        {
            SearchPattern part = _parts[variant];
            int window = (int)Math.Min(data.Length, (long)coreEnd + part.MaxMatchLength - 1);
            int pos = from;
            while (pos < coreEnd)
            {
                int found = part.IndexOf(data[pos..window], baseOffset + pos, out int len);
                if (found < 0)
                {
                    break;
                }

                int start = pos + found;
                if (start >= coreEnd)
                {
                    break;
                }

                sink.Add(new RawMatch(start, len, variant));
                pos = start + 1;
            }
        }
    }

    public override int MatchLength(ReadOnlySpan<byte> data, long baseOffset, out int variant)
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            int len = _parts[i].MatchLengthAt(data, baseOffset);
            if (len >= 0)
            {
                variant = i;
                return len;
            }
        }

        variant = 0;
        return -1;
    }
}

/// <summary>
/// バイト列の Aho-Corasick 法 (FIND-26 の仕様 4)。語の数に関係なく、データを 1 回なめるだけで、すべての語のすべての出現を見つける。
/// 根の状態では、語の先頭のバイトの候補 (<see cref="SearchValues{T}"/>) まで SIMD で飛ばす。
/// </summary>
internal sealed class AhoCorasick
{
    private readonly int[] _rootNext = new int[256];
    private readonly byte[][] _keys;
    private readonly int[][] _targets;
    private readonly int[] _fail;
    private readonly int[][] _outputs;
    private readonly int[] _outputLink;
    private readonly SearchValues<byte> _firstBytes;

    public AhoCorasick(IReadOnlyList<byte[]> patterns)
    {
        // 木を作る。
        var children = new List<Dictionary<byte, int>> { new() };
        var outputs = new List<List<int>?> { null };
        for (int id = 0; id < patterns.Count; id++)
        {
            int state = 0;
            foreach (byte b in patterns[id])
            {
                if (!children[state].TryGetValue(b, out int next))
                {
                    next = children.Count;
                    children.Add([]);
                    outputs.Add(null);
                    children[state][b] = next;
                }

                state = next;
            }

            (outputs[state] ??= []).Add(id);
        }

        int count = children.Count;
        _keys = new byte[count][];
        _targets = new int[count][];
        for (int s = 0; s < count; s++)
        {
            KeyValuePair<byte, int>[] sorted = [.. children[s].OrderBy(kv => kv.Key)];
            _keys[s] = [.. sorted.Select(kv => kv.Key)];
            _targets[s] = [.. sorted.Select(kv => kv.Value)];
        }

        // 失敗の遷移を幅優先で求める。
        _fail = new int[count];
        _outputLink = new int[count];
        Array.Fill(_outputLink, -1);
        var queue = new Queue<int>();
        Array.Fill(_rootNext, 0);
        for (int k = 0; k < _keys[0].Length; k++)
        {
            int child = _targets[0][k];
            _rootNext[_keys[0][k]] = child;
            _fail[child] = 0;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            int s = queue.Dequeue();
            for (int k = 0; k < _keys[s].Length; k++)
            {
                byte b = _keys[s][k];
                int child = _targets[s][k];
                int f = _fail[s];
                int next;
                while ((next = Child(f, b)) < 0 && f != 0)
                {
                    f = _fail[f];
                }

                _fail[child] = next >= 0 && next != child ? next : 0;
                int link = _fail[child];
                _outputLink[child] = outputs[link] is not null ? link : _outputLink[link];
                queue.Enqueue(child);
            }
        }

        _outputs = [.. outputs.Select(o => o is null ? Array.Empty<int>() : o.ToArray())];
        _firstBytes = SearchValues.Create([.. _keys[0]]);
    }

    /// <summary>[from, data の末尾) を照合し、出現ごとに (末尾の次の位置, 語の番号) を加える。</summary>
    public void Search(ReadOnlySpan<byte> data, int from, List<(int End, int Id)> sink)
    {
        int state = 0;
        int i = from;
        while (i < data.Length)
        {
            if (state == 0)
            {
                int skip = data[i..].IndexOfAny(_firstBytes);
                if (skip < 0)
                {
                    return;
                }

                i += skip;
                state = _rootNext[data[i]];
                i++;
            }
            else
            {
                byte b = data[i];
                int next;
                while ((next = Child(state, b)) < 0 && state != 0)
                {
                    state = _fail[state];
                }

                state = next >= 0 ? next : 0;
                i++;
            }

            for (int s = state; s > 0; s = _outputLink[s])
            {
                foreach (int id in _outputs[s])
                {
                    sink.Add((i, id));
                }
            }
        }
    }

    private int Child(int state, byte b)
    {
        if (state == 0)
        {
            int r = _rootNext[b];
            return r == 0 ? -1 : r;
        }

        byte[] keys = _keys[state];
        if (keys.Length <= 8)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                if (keys[k] == b)
                {
                    return _targets[state][k];
                }
            }

            return -1;
        }

        int index = Array.BinarySearch(keys, b);
        return index >= 0 ? _targets[state][index] : -1;
    }
}
