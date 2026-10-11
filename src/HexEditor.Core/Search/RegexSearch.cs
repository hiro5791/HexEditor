using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Search;

/// <summary>正規表現の検索の条件 (FIND-18、FIND-19)。</summary>
public sealed record RegexSearchOptions
{
    /// <summary>`i`: 大文字・小文字を区別しない (FIND-10 と同じ Invariant の対応)。</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>`m`: `^` `$` を行頭・行末に一致させる。</summary>
    public bool Multiline { get; init; }

    /// <summary>`s`: `.` を改行に一致させる (テキストの既定はオフ、バイト列の既定はオン。FIND-19 の仕様 3)。</summary>
    public bool Singleline { get; init; }

    /// <summary>一致の最大長 (FIND-01 の仕様 3。既定 4,096 バイト)。これより長い一致は報告しない。</summary>
    public int MaxMatchLength { get; init; } = SearchPattern.DefaultMaxMatchLength;

    /// <summary>後戻りする方式の、チャンクごとの時間の上限 (FIND-18 の仕様 6。既定 2 秒、0.1〜60 秒)。</summary>
    public TimeSpan TimeLimit { get; init; } = RegexSearch.DefaultTimeLimit;

    /// <summary>UTF-16 / UTF-32 で、一致の開始を文字の境界 (2 / 4 の倍数のオフセット) に限る (FIND-07 の仕様 5 と同じ)。</summary>
    public bool AlignToCharacters { get; init; }
}

/// <summary>
/// 正規表現の検索 (FIND-18、FIND-19)。構文は ECMAScript に準じ (.NET の正規表現で照合する)、後方参照と先読み・後読みを使わなければ
/// 後戻りしない方式 (<see cref="RegexOptions.NonBacktracking"/>) で照合する (FIND-18 の仕様 6)。
/// <para>
/// 巨大なデータは、チャンクをさらに 256 KiB ごとの区間に分けて復号・照合し、メモリ使用量を一定に保つ。区間の後ろには
/// 「一致の最大長」(L) と文脈 (C) の分を重ねて読み、区間の前には C の分の文脈 (後読み・<c>\b</c>・<c>^</c> の判定用) を読む。
/// 報告するのは長さが 1〜L の一致だけで、L を超える一致は報告せず、その一致の後ろから探し続ける (FIND-01 の仕様 3)。
/// </para>
/// </summary>
public static class RegexSearch
{
    /// <summary>後戻りする方式の時間の上限の既定値 (FIND-18 の仕様 6)。</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(2);

    /// <summary>時間の上限の設定の範囲 (0.1〜60 秒)。</summary>
    public static readonly TimeSpan MinTimeLimit = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan MaxTimeLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 一致の前後に読む文脈の上限 (バイト)。後読み・先読み・<c>\b</c>・<c>^</c>・<c>$</c> は、一致の前後この長さまでのデータで判定する
    /// (一致の最大長がこれより短ければ一致の最大長まで)。
    /// </summary>
    public const int MaxContext = 1024;

    /// <summary>
    /// 正規表現によるテキスト検索 (FIND-18)。データを <paramref name="encoding"/> で復号し (復号できないバイトは U+FFFD 1 文字)、
    /// 文字の位置とバイトの位置の対応を保ちながら照合する。UTF-16 / UTF-32 は、文字の境界に揃えない限り、すべての位相で照合する。
    /// </summary>
    public static SearchPattern Text(string pattern, Encoding encoding, RegexSearchOptions options)
    {
        if (pattern.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        _ = TextEncodings.FromCatalogId("ascii"); // コードページの文字コードを使えるようにする。
        var decoder = ByteTextDecoder.For(encoding);
        var (regex, view, nonBacktracking) = Compile(pattern, null, options, bytes: false);
        var matcher = new RegexMatcher(regex, view, nonBacktracking, decoder, encoding, options, pattern);
        return SearchPattern.FromRegex(matcher, options.MaxMatchLength);
    }

    /// <summary>
    /// 正規表現によるバイト列の検索 (FIND-19)。各バイトを 1 文字 (U+0000〜U+00FF) とみなして照合する。`\d` `\w` `\s` `\b` は ASCII の
    /// 範囲だけで判定し、`i` は ASCII の英字だけを対応させる (仕様 4)。`\p{...}` と `\u` は使えない (仕様 5)。
    /// </summary>
    public static SearchPattern Bytes(string pattern, RegexSearchOptions options)
    {
        if (pattern.Length == 0)
        {
            throw new PatternException(PatternError.Empty);
        }

        string translated = TranslateBytePattern(pattern, out int[] map);
        var (regex, view, nonBacktracking) = Compile(translated, map, options, bytes: true);
        var matcher = new RegexMatcher(regex, view, nonBacktracking, null, null, options, pattern);
        return SearchPattern.FromRegex(matcher, options.MaxMatchLength);
    }

    /// <summary>後戻りしない方式で照合するか (後方参照・先読み・後読みを使っていない。FIND-18 の仕様 6)。</summary>
    public static bool IsNonBacktracking(SearchPattern pattern) => pattern.Regex?.NonBacktracking ?? false;

    /// <summary>
    /// バイト列の正規表現の置換語 (FIND-19 の仕様 7): Hex のバイトと、グループの参照 `$1` `$&lt;name&gt;` `$&amp;` `$$` を並べたもの。
    /// 構文を確かめる (誤りは <see cref="PatternException"/>)。
    /// </summary>
    public static void ValidateByteReplacement(string text) => _ = ParseByteReplacement(text);

    /// <summary>テキストの正規表現の置換語を .NET の置換の書き方にする (`$&lt;name&gt;` → `${name}`)。</summary>
    internal static string ToDotNetReplacement(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '$')
            {
                sb.Append("$$");
                i++;
                continue;
            }

            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '<')
            {
                int close = text.IndexOf('>', i + 2);
                if (close > i + 2)
                {
                    sb.Append("${").Append(text, i + 2, close - i - 2).Append('}');
                    i = close;
                    continue;
                }
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }

    /// <summary>バイト列の置換語の部品: バイト列、またはグループの参照。</summary>
    internal readonly record struct ByteReplacementPart(byte[]? Bytes, string? Group);

    internal static List<ByteReplacementPart> ParseByteReplacement(string text)
    {
        var parts = new List<ByteReplacementPart>();
        var digits = new StringBuilder();
        int position = 0;
        int i = 0;

        void Flush()
        {
            if (digits.Length % 2 != 0)
            {
                throw new PatternException(PatternError.OddDigits, digits[^1].ToString(), position);
            }

            if (digits.Length > 0)
            {
                parts.Add(new ByteReplacementPart(Convert.FromHexString(digits.ToString()), null));
                digits.Clear();
            }
        }

        while (i < text.Length)
        {
            char c = text[i];
            position = i + 1;
            if (char.IsWhiteSpace(c) || c == ',')
            {
                Flush();
                i++;
                continue;
            }

            if ((c == '0' || c == '\\') && i + 1 < text.Length && text[i + 1] is 'x' or 'X' && digits.Length % 2 == 0)
            {
                Flush();
                i += 2;
                continue;
            }

            if (c == '$')
            {
                Flush();
                if (i + 1 >= text.Length)
                {
                    throw new PatternException(PatternError.InvalidCharacter, "$", position);
                }

                char n = text[i + 1];
                if (n == '$')
                {
                    parts.Add(new ByteReplacementPart([(byte)'$'], null));
                    i += 2;
                }
                else if (n == '&')
                {
                    parts.Add(new ByteReplacementPart(null, "0"));
                    i += 2;
                }
                else if (n == '<')
                {
                    int close = text.IndexOf('>', i + 2);
                    if (close <= i + 2)
                    {
                        throw new PatternException(PatternError.InvalidCharacter, "$<", position);
                    }

                    parts.Add(new ByteReplacementPart(null, text[(i + 2)..close]));
                    i = close + 1;
                }
                else if (char.IsAsciiDigit(n))
                {
                    int j = i + 1;
                    while (j < text.Length && char.IsAsciiDigit(text[j]))
                    {
                        j++;
                    }

                    parts.Add(new ByteReplacementPart(null, text[(i + 1)..j]));
                    i = j;
                }
                else
                {
                    throw new PatternException(PatternError.InvalidCharacter, "$" + n, position);
                }

                continue;
            }

            if (!char.IsAsciiHexDigit(c))
            {
                throw new PatternException(PatternError.InvalidCharacter, c.ToString(), position);
            }

            digits.Append(c);
            i++;
        }

        Flush();
        return parts;
    }

    private static (Regex Search, Regex View, bool NonBacktracking) Compile(string pattern, int[]? map, RegexSearchOptions options, bool bytes)
    {
        RegexOptions flags = RegexOptions.CultureInvariant;
        if (options.IgnoreCase)
        {
            flags |= RegexOptions.IgnoreCase;
        }

        if (options.Multiline)
        {
            flags |= RegexOptions.Multiline;
        }

        if (options.Singleline)
        {
            flags |= RegexOptions.Singleline;
        }

        TimeSpan limit = options.TimeLimit < MinTimeLimit ? MinTimeLimit : options.TimeLimit > MaxTimeLimit ? MaxTimeLimit : options.TimeLimit;
        TimeSpan viewLimit = TimeSpan.FromMilliseconds(30);
        try
        {
            try
            {
                var fast = new Regex(pattern, flags | RegexOptions.NonBacktracking, limit);
                return (fast, new Regex(pattern, flags | RegexOptions.NonBacktracking, viewLimit), true);
            }
            catch (NotSupportedException)
            {
                // 後方参照・先読み・後読みなど: 後戻りする方式で、時間の上限を付けて照合する (仕様 6)。
                return (new Regex(pattern, flags, limit), new Regex(pattern, flags, viewLimit), false);
            }
        }
        catch (RegexParseException ex)
        {
            int offset = Math.Clamp(ex.Offset, 0, pattern.Length);
            int original = map is null ? offset : map[Math.Min(offset, map.Length - 1)];
            throw new PatternException(PatternError.RegexSyntax, ex.Error.ToString(), Math.Max(1, map is null ? offset : original));
        }
        catch (ArgumentException ex)
        {
            throw new PatternException(PatternError.RegexSyntax, ex.Message);
        }
    }

    /// <summary>
    /// バイト列の正規表現を、データの写し方 (00〜7F はそのまま、80〜FF は U+E080〜U+E0FF の私用領域) に合わせて書き換える。
    /// 私用領域の文字は単語・数字・空白のどれでもないため、<c>\d</c> <c>\w</c> <c>\s</c> <c>\b</c> は ASCII の範囲だけで判定され、
    /// <c>i</c> は ASCII の英字だけを対応させる (FIND-19 の仕様 4)。<paramref name="map"/> は書き換え後の各文字の、元の何文字目 (1 から) か。
    /// </summary>
    internal static string TranslateBytePattern(string pattern, out int[] map)
    {
        var sb = new StringBuilder(pattern.Length + 16);
        var positions = new List<int>(pattern.Length + 16);
        bool inClass = false;
        int i = 0;

        void Emit(string text, int position)
        {
            sb.Append(text);
            for (int k = 0; k < text.Length; k++)
            {
                positions.Add(position);
            }
        }

        while (i < pattern.Length)
        {
            int position = i + 1;
            char c = pattern[i];
            if (inClass)
            {
                // 文字クラスの中: 項目 (1 文字、範囲、クラスの略記) ごとに書き換える。
                if (c == ']')
                {
                    inClass = false;
                    Emit("]", position);
                    i++;
                    continue;
                }

                (int? lo, string loText, int next) = ClassAtom(pattern, i);
                if (lo is int a && next < pattern.Length - 1 && pattern[next] == '-' && pattern[next + 1] != ']')
                {
                    (int? hi, string hiText, int after) = ClassAtom(pattern, next + 1);
                    if (hi is int b)
                    {
                        Emit(MapRange(a, b), position);
                    }
                    else
                    {
                        Emit(loText + "-" + hiText, position);
                    }

                    i = after;
                    continue;
                }

                Emit(lo is int v ? MapChar(v) : loText, position);
                i = next;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
                Emit("[", position);
                i++;
                if (i < pattern.Length && pattern[i] == '^')
                {
                    Emit("^", i + 1);
                    i++;
                }

                // 先頭の ']' は文字として扱う (.NET と同じ)。
                if (i < pattern.Length && pattern[i] == ']')
                {
                    Emit("\\]", i + 1);
                    i++;
                }

                continue;
            }

            if (c == '\\')
            {
                (int? value, string text, int next) = Escape(pattern, i);
                Emit(value is int v ? MapChar(v) : text, position);
                i = next;
                continue;
            }

            CheckByteChar(c, position);
            Emit(c < 0x80 ? c.ToString() : MapChar(c), position);
            i++;
        }

        positions.Add(pattern.Length);
        map = [.. positions];
        return sb.ToString();
    }

    /// <summary>文字クラスの中の 1 項目。値がわかる文字 (リテラル・<c>\xHH</c> など) なら値を返す。</summary>
    private static (int? Value, string Text, int Next) ClassAtom(string pattern, int i)
    {
        char c = pattern[i];
        if (c == '\\')
        {
            return Escape(pattern, i);
        }

        CheckByteChar(c, i + 1);
        return (c, c.ToString(), i + 1);
    }

    /// <summary>
    /// エスケープ 1 つ。<c>\xHH</c> と 1 文字を表すエスケープは値を返す。<c>\u</c> と <c>\p</c> はエラー (FIND-19 の仕様 5)。
    /// それ以外 (クラスの略記・後方参照・アンカーなど) はそのまま写す。
    /// </summary>
    private static (int? Value, string Text, int Next) Escape(string pattern, int i)
    {
        if (i + 1 >= pattern.Length)
        {
            return (null, "\\", i + 1);
        }

        char e = pattern[i + 1];
        switch (e)
        {
            case 'x':
                if (IsHex(pattern, i + 2, 2))
                {
                    return (Convert.ToInt32(pattern.Substring(i + 2, 2), 16), pattern.Substring(i, 4), i + 4);
                }

                return (null, pattern.Substring(i, 2), i + 2);
            case 'u':
                throw new PatternException(PatternError.RegexUnicodeEscape, "\\u", i + 1);
            case 'p' or 'P':
                throw new PatternException(PatternError.RegexUnicodeProperty, "\\" + e, i + 1);
            case 'n':
                return (0x0A, "\\n", i + 2);
            case 'r':
                return (0x0D, "\\r", i + 2);
            case 't':
                return (0x09, "\\t", i + 2);
            case 'f':
                return (0x0C, "\\f", i + 2);
            case 'v':
                return (0x0B, "\\v", i + 2);
            case 'c' when i + 2 < pattern.Length:
                return (null, pattern.Substring(i, 3), i + 3);
            case 'k' when i + 2 < pattern.Length && pattern[i + 2] == '<':
            {
                int close = pattern.IndexOf('>', i + 3);
                int end = close < 0 ? pattern.Length : close + 1;
                return (null, pattern[i..end], end);
            }

            default:
                if (e >= 0x80)
                {
                    CheckByteChar(e, i + 2);
                    return (e, pattern.Substring(i, 2), i + 2);
                }

                return (null, pattern.Substring(i, 2), i + 2);
        }
    }

    private static bool IsHex(string s, int start, int count)
    {
        if (start + count > s.Length)
        {
            return false;
        }

        for (int k = 0; k < count; k++)
        {
            if (!char.IsAsciiHexDigit(s[start + k]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>バイト列の正規表現には U+00FF を超える文字は書けない (どのバイトにも当たらない)。</summary>
    private static void CheckByteChar(char c, int position)
    {
        if (c > 0xFF)
        {
            throw new PatternException(PatternError.RegexNonByteCharacter, c.ToString(), position);
        }
    }

    /// <summary>バイトの値 0〜FF を、照合する文字の書き方にする。</summary>
    private static string MapChar(int value) => value < 0x80
        ? "\\x" + value.ToString("X2", CultureInfo.InvariantCulture)
        : "\\u" + (0xE000 + value).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>バイトの範囲 a〜b を、照合する文字の範囲にする (7F と 80 の間で分ける)。</summary>
    private static string MapRange(int a, int b)
    {
        if (a > b || b < 0x80 || a >= 0x80)
        {
            return MapChar(a) + "-" + MapChar(b);
        }

        return MapChar(a) + "-\\x7F" + MapChar(0x80) + "-" + MapChar(b);
    }

    /// <summary>データのバイトを照合する文字にする (00〜7F はそのまま、80〜FF は U+E080〜U+E0FF)。</summary>
    internal static char ByteChar(byte b) => b < 0x80 ? (char)b : (char)(0xE000 + b);

    /// <summary><see cref="ByteChar"/> の逆。</summary>
    internal static byte CharByte(char c) => c < 0x80 ? (byte)c : (byte)(c - 0xE000);
}

/// <summary>正規表現の照合が、チャンクの時間の上限に達した (FIND-18 の仕様 6)。</summary>
internal sealed class RegexChunkTimeoutException : Exception
{
}

/// <summary>正規表現の時間の上限に達したため検索を中止した (FIND-18 の「エラー」で「中止する」を選んだ)。</summary>
public sealed class SearchTimedOutException(SearchRange range)
    : OperationCanceledException($"正規表現の時間の上限に達したため検索を中止しました: 0x{range.Offset:X}〜 ({range.Length} バイト)")
{
    /// <summary>上限に達したチャンクの範囲。</summary>
    public SearchRange Range { get; } = range;
}

/// <summary>正規表現の照合 (FIND-18、FIND-19)。</summary>
internal sealed class RegexMatcher : MultiMatcher
{
    private readonly Regex _regex;
    private readonly Regex _viewRegex;

    /// <summary>後戻りする方式で、まず試す短い時間の上限 (<see cref="SliceTimeout"/>) の正規表現 (後戻りしない方式なら null)。</summary>
    private readonly Regex? _sliceRegex;
    private readonly ByteTextDecoder? _decoder;
    private readonly Encoding? _encoding;
    private readonly int _maxLength;
    private readonly bool _aligned;

    public RegexMatcher(Regex regex, Regex viewRegex, bool nonBacktracking, ByteTextDecoder? decoder, Encoding? encoding, RegexSearchOptions options,
        string source)
    {
        _regex = regex;
        _viewRegex = viewRegex;
        NonBacktracking = nonBacktracking;
        _decoder = decoder;
        _encoding = encoding;
        _maxLength = Math.Clamp(options.MaxMatchLength, 1, SearchPattern.MaxMaxMatchLength);
        _aligned = options.AlignToCharacters;
        TimeLimit = options.TimeLimit < RegexSearch.MinTimeLimit ? RegexSearch.MinTimeLimit
            : options.TimeLimit > RegexSearch.MaxTimeLimit ? RegexSearch.MaxTimeLimit : options.TimeLimit;
        LeadingContext = Math.Min(RegexSearch.MaxContext, _maxLength);
        Source = source;
        if (!nonBacktracking)
        {
            _sliceRegex = new Regex(regex.ToString(), regex.Options, SliceTimeout);
        }
    }

    /// <summary>
    /// 後戻りする方式で、1 回の照合をそのスレッドで続ける時間。これを超えた照合は別のスレッドに移し、キャンセルとチャンクの時間の上限を
    /// 確かめながら待つ (.NET の照合は途中で止められないため。FIND-02 の仕様 3)。
    /// </summary>
    internal static readonly TimeSpan SliceTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>別のスレッドに移した照合を待つ間に、キャンセルを確かめる間隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 表示中の範囲の強調 (UI スレッド) で、1 回の描画の照合に使う時間の合計の上限。これを超えたら残りの強調はあきらめる
    /// (1 回の照合の上限 30 ms と合わせて、1 回の描画で最大でも約 80 ms)。
    /// </summary>
    internal static readonly TimeSpan ViewBudget = TimeSpan.FromMilliseconds(50);

    /// <summary>テスト用: 別のスレッドに移した照合を待ち始めたときに呼ぶ。</summary>
    internal Action? SlowMatchStartedForTest { get; set; }

    /// <summary>入力した正規表現。</summary>
    public string Source { get; }

    /// <summary>後戻りしない方式で照合するか。</summary>
    public bool NonBacktracking { get; }

    /// <summary>チャンクごとの時間の上限 (後戻りする方式だけ)。</summary>
    public TimeSpan TimeLimit { get; }

    /// <summary>一致の最大長 (L)。</summary>
    public int MaxLength => _maxLength;

    /// <summary>一致の前後に読む文脈 (C)。</summary>
    public int LeadingContext { get; }

    /// <summary>バイト列の正規表現か (テキストなら false)。</summary>
    public bool IsBytes => _decoder is null;

    /// <summary>テキストの正規表現の文字コード。</summary>
    public Encoding? Encoding => _encoding;

    public override void Gather(ReadOnlySpan<byte> data, long baseOffset, int from, int coreEnd, bool overlapping, in ScanContext context,
        List<RawMatch> sink)
    {
        coreEnd = Math.Min(coreEnd, data.Length);
        if (from >= coreEnd)
        {
            return;
        }

        // 前の文脈: バッファの中にあればそれを、なければドキュメントから読む (範囲の先頭・読めない範囲の直後なら読まない)。
        int leadInData = Math.Min(from, LeadingContext);
        byte[]? extra = null;
        if (leadInData < LeadingContext && from - leadInData == 0 && !context.StartIsBoundary && context.Snapshot is { } snapshot)
        {
            long want = Math.Min(LeadingContext - leadInData, baseOffset - context.RangeStart);
            if (want > 0)
            {
                byte[] buffer = new byte[want];
                if (snapshot.Read(baseOffset - want, buffer).IsComplete)
                {
                    extra = buffer;
                }
            }
        }

        int extraLength = extra?.Length ?? 0;
        int start = from - leadInData;
        int length = extraLength + (data.Length - start);
        byte[] combined = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            extra?.CopyTo(combined, 0);
            data[start..].CopyTo(combined.AsSpan(extraLength));
            long combinedBase = baseOffset + start - extraLength;
            int fromInCombined = extraLength + leadInData;
            int coreEndInCombined = extraLength + (coreEnd - start);
            Regex regex = context.ForView ? _viewRegex : _regex;
            Stopwatch? clock = context.Clock;
            int units = _decoder is null || _aligned ? 1 : _decoder.Unit;
            for (int phase = 0; phase < units; phase++)
            {
                GatherPhase(regex, combined.AsSpan(0, length), combinedBase, fromInCombined, coreEndInCombined, phase, overlapping, context, clock,
                    m => sink.Add(new RawMatch(m.Start - extraLength + start, m.Length, 0)));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(combined);
        }
    }

    /// <summary>1 つの位相 (UTF-16 なら偶数・奇数の位置から始めた復号) で照合する。</summary>
    private void GatherPhase(Regex regex, ReadOnlySpan<byte> bytes, long baseOffset, int from, int coreEnd, int phase, bool overlapping,
        in ScanContext context, Stopwatch? clock, Action<RawMatch> add)
    {
        int unit = _decoder?.Unit ?? 1;
        int skip = 0;
        if (unit > 1)
        {
            // 復号の開始を、ドキュメント上の位置が unit を法として phase になる位置に揃える。
            long mod = ((baseOffset % unit) + unit) % unit;
            skip = (int)(((phase - mod) % unit + unit) % unit);
            if (_aligned)
            {
                skip = (int)((unit - mod) % unit);
            }
        }

        if (skip >= bytes.Length)
        {
            return;
        }

        ReadOnlySpan<byte> part = bytes[skip..];
        string input;
        int[]? offsets = null;
        if (_decoder is null)
        {
            input = string.Create(part.Length, part.ToArray(), static (span, src) =>
            {
                for (int k = 0; k < src.Length; k++)
                {
                    span[k] = RegexSearch.ByteChar(src[k]);
                }
            });
        }
        else
        {
            char[] chars = System.Buffers.ArrayPool<char>.Shared.Rent(part.Length + 1);
            offsets = new int[part.Length + 2];
            int n = _decoder.Decode(part, chars, offsets);
            input = new string(chars, 0, n);
            System.Buffers.ArrayPool<char>.Shared.Return(chars);
        }

        int ByteAt(int charIndex) => skip + (offsets is null ? charIndex : offsets[charIndex]);
        int CharAt(int byteIndex)
        {
            int target = byteIndex - skip;
            if (offsets is null)
            {
                return Math.Clamp(target, 0, input.Length);
            }

            // 最初に target 以上の位置から始まる文字。
            int lo = 0;
            int hi = input.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (offsets[mid] < target)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        int startAt = CharAt(from);
        while (startAt <= input.Length)
        {
            context.CheckCancel?.Invoke();
            if (context.ForView)
            {
                if (clock is not null && clock.Elapsed > ViewBudget)
                {
                    // 表示中の範囲の強調に使う時間の合計の上限: 残りはあきらめる (UI を止めない)。
                    return;
                }
            }
            else if (clock is not null && !NonBacktracking && clock.Elapsed > TimeLimit)
            {
                throw new RegexChunkTimeoutException();
            }

            Match m;
            try
            {
                m = context.ForView || _sliceRegex is null ? regex.Match(input, startAt) : MatchBacktracking(input, startAt, context, clock);
            }
            catch (RegexMatchTimeoutException)
            {
                if (context.ForView)
                {
                    return;
                }

                throw new RegexChunkTimeoutException();
            }

            if (!m.Success)
            {
                return;
            }

            int byteStart = ByteAt(m.Index);
            if (byteStart >= coreEnd)
            {
                return;
            }

            int byteEnd = ByteAt(m.Index + m.Length);
            int len = byteEnd - byteStart;
            if (m.Length == 0 || len <= 0)
            {
                // 長さ 0 の一致は報告しない (FIND-18 の仕様 7)。
                startAt = m.Index + 1;
                continue;
            }

            if (len <= _maxLength && byteStart >= from)
            {
                add(new RawMatch(byteStart, len, 0));
                startAt = overlapping ? m.Index + 1 : m.Index + m.Length;
            }
            else
            {
                // 一致の最大長を超える一致は報告せず、その後ろから探す (FIND-01 の仕様 3)。
                startAt = m.Index + m.Length;
            }
        }
    }

    /// <summary>
    /// 後戻りする方式の 1 回の照合。まず短い時間の上限 (<see cref="SliceTimeout"/>) でこのスレッドで照合し、終わらなければ別のスレッドで
    /// 照合し直して、キャンセルとチャンクの時間の上限 (<see cref="TimeLimit"/>、<paramref name="clock"/> で測る) を確かめながら待つ。
    /// キャンセルなら <see cref="OperationCanceledException"/>、時間の上限なら <see cref="RegexChunkTimeoutException"/> を投げ、
    /// 別のスレッドの照合は待たずに手放す (その照合は自分の時間の上限で終わる)。
    /// </summary>
    private Match MatchBacktracking(string input, int startAt, in ScanContext context, Stopwatch? clock)
    {
        try
        {
            return _sliceRegex!.Match(input, startAt);
        }
        catch (RegexMatchTimeoutException)
        {
        }

        Stopwatch own = clock ?? Stopwatch.StartNew();
        if (own.Elapsed > TimeLimit)
        {
            throw new RegexChunkTimeoutException();
        }

        Regex regex = _regex;
        Task<Match?> slow = Task.Factory.StartNew<Match?>(() =>
        {
            try
            {
                return regex.Match(input, startAt);
            }
            catch (RegexMatchTimeoutException)
            {
                return null; // 手放した後に時間の上限に達しても、観測されない例外を残さない。
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        SlowMatchStartedForTest?.Invoke();
        while (true)
        {
            if (slow.Wait(PollInterval))
            {
                return slow.Result ?? throw new RegexChunkTimeoutException();
            }

            context.CheckCancel?.Invoke();
            if (own.Elapsed > TimeLimit)
            {
                throw new RegexChunkTimeoutException();
            }
        }
    }

    public override int MatchLength(ReadOnlySpan<byte> data, long baseOffset, out int variant)
    {
        variant = 0;
        var list = new List<RawMatch>();
        Gather(data, baseOffset, 0, 1, true, new ScanContext(null, baseOffset, baseOffset + data.Length, true, true), list);
        return list.Count > 0 && list[0].Start == 0 ? list[0].Length : -1;
    }

    /// <summary>
    /// 今の状態の <paramref name="offset"/> で始まる一致の長さ (範囲 [rangeStart, rangeEnd) の中で照合する。置換の前の確認)。一致しなければ −1。
    /// </summary>
    public int LengthAt(DocumentSnapshot snapshot, long offset, long rangeStart, long rangeEnd)
    {
        long readStart = Math.Max(rangeStart, offset - LeadingContext);
        long readEnd = Math.Min(rangeEnd, offset + (long)_maxLength + LeadingContext);
        if (offset < readStart || offset >= readEnd)
        {
            return -1;
        }

        byte[] buffer = new byte[readEnd - readStart];
        if (!snapshot.Read(readStart, buffer).IsComplete)
        {
            return -1;
        }

        int from = (int)(offset - readStart);
        var list = new List<RawMatch>();
        var context = new ScanContext(snapshot, rangeStart, rangeEnd, readStart == rangeStart, readEnd == rangeEnd);
        try
        {
            Gather(buffer, readStart, from, from + 1, true, context, list);
        }
        catch (RegexChunkTimeoutException)
        {
            return -1;
        }

        return list.FirstOrDefault(m => m.Start == from) is { Length: > 0 } hit ? hit.Length : -1;
    }

    /// <summary>
    /// 今の状態の <paramref name="offset"/> から長さ <paramref name="length"/> の一致を照合し直し、一致すればその Match を返す
    /// (置換の確認とグループの参照。FIND-03 の仕様 5、FIND-18 の仕様 8)。
    /// </summary>
    public Match? MatchAt(DocumentSnapshot snapshot, long offset, long length, out Func<string, string>? toText)
    {
        toText = null;
        if (offset < 0 || length <= 0 || offset + length > snapshot.Length)
        {
            return null;
        }

        long lead = Math.Min(LeadingContext, offset);
        long trail = Math.Min(LeadingContext, snapshot.Length - offset - length);
        byte[] buffer = new byte[lead + length + trail];
        if (!snapshot.Read(offset - lead, buffer).IsComplete)
        {
            return null;
        }

        int unit = _decoder?.Unit ?? 1;
        int skip = (int)(lead % unit); // 一致の開始が文字の始まりになるように復号を始める。
        ReadOnlySpan<byte> part = buffer.AsSpan(skip);
        string input;
        int[]? offsets = null;
        if (_decoder is null)
        {
            char[] chars = new char[part.Length];
            for (int k = 0; k < part.Length; k++)
            {
                chars[k] = RegexSearch.ByteChar(part[k]);
            }

            input = new string(chars);
        }
        else
        {
            char[] chars = new char[part.Length + 1];
            offsets = new int[part.Length + 2];
            int n = _decoder.Decode(part, chars, offsets);
            input = new string(chars, 0, n);
        }

        int startByte = (int)lead - skip;
        int startChar = offsets is null ? startByte : Array.IndexOf(offsets, startByte, 0, input.Length + 1);
        if (startChar < 0)
        {
            return null;
        }

        Match m;
        try
        {
            m = _regex.Match(input, startChar);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!m.Success || m.Index != startChar)
        {
            return null;
        }

        int endByte = offsets is null ? m.Index + m.Length : offsets[m.Index + m.Length];
        if (endByte - startByte != length)
        {
            return null;
        }

        return m;
    }

    /// <summary>一致に置換語を当てはめたバイト列 (FIND-18 の仕様 8、FIND-19 の仕様 7)。</summary>
    public byte[] Replace(Match match, string replacement)
    {
        if (_decoder is null)
        {
            var bytes = new List<byte>();
            foreach (RegexSearch.ByteReplacementPart part in RegexSearch.ParseByteReplacement(replacement))
            {
                if (part.Bytes is { } literal)
                {
                    bytes.AddRange(literal);
                    continue;
                }

                Group g = int.TryParse(part.Group, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                    ? match.Groups[number]
                    : match.Groups[part.Group!];
                if (!g.Success && !match.Groups.ContainsKey(part.Group!) && number >= match.Groups.Count)
                {
                    throw new PatternException(PatternError.InvalidCharacter, "$" + part.Group);
                }

                foreach (char c in g.Value)
                {
                    bytes.Add(RegexSearch.CharByte(c));
                }
            }

            return [.. bytes];
        }

        string text = match.Result(RegexSearch.ToDotNetReplacement(replacement));
        return SearchPattern.EncodeText(text, _encoding!, useEscapes: false);
    }
}
