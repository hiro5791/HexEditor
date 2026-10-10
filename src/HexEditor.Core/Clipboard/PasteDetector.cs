using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HexEditor.Core.Clipboard;

/// <summary>
/// 「形式を選択して貼り付け」の判別部品 (EDIT-26)。クリップボードのテキストを各形式で解釈し、解釈できた形式を確からしさの順に返す。
/// 判別はテキストの先頭 64 KiB で行い (<see cref="Detect"/>)、選んだ形式での全体の変換は貼り付けのときに <see cref="Parse"/> で行う。
/// </summary>
public static class PasteDetector
{
    /// <summary>判別に使うテキストの長さ (先頭 64 KiB)。</summary>
    public const int DetectionChars = 64 * 1024;

    /// <summary>解釈するテキストの上限 (512 MiB。仕様 7)。</summary>
    public const long MaxTextChars = 512L * 1024 * 1024;

    /// <summary>「カーソル位置に貼る」で作るバイト列の上限 (4 GiB。ENG-38 と同じ)。</summary>
    public const long MaxAddressedSpan = 4L * 1024 * 1024 * 1024;

    /// <summary>判別の順に並べた形式 (同じ順位の中ではこの順)。</summary>
    private static readonly PasteFormat[] TextFormats =
    [
        PasteFormat.IntelHex, PasteFormat.SRecord, PasteFormat.UUEncode, PasteFormat.XXEncode, PasteFormat.Json, PasteFormat.ScreenDump,
        PasteFormat.Array, PasteFormat.Escape, PasteFormat.BinaryNumbers, PasteFormat.HexString,
        PasteFormat.Ascii85, PasteFormat.Base32, PasteFormat.Base32Hex, PasteFormat.Base64, PasteFormat.Z85, PasteFormat.QuotedPrintable,
        PasteFormat.UrlEncoded, PasteFormat.Decimal, PasteFormat.Octal, PasteFormat.Text,
    ];

    /// <summary>
    /// 確からしさの順位 (仕様 3): (0) バイナリ・ファイル、(1) 構造のある形式、(2) 配列表記とエスケープ文字列、8 桁の 2 進の数値列、
    /// (3) Hex 文字列、(4) 文字集合だけで判別する形式、(5) 10 進・8 進の数値列、(6) テキスト。
    /// </summary>
    public static int RankOf(PasteFormat format) => format switch
    {
        PasteFormat.Binary or PasteFormat.FileContent => 0,
        PasteFormat.IntelHex or PasteFormat.SRecord or PasteFormat.UUEncode or PasteFormat.XXEncode or PasteFormat.Json
            or PasteFormat.ScreenDump => 1,
        PasteFormat.Array or PasteFormat.Escape or PasteFormat.BinaryNumbers => 2,
        PasteFormat.HexString => 3,
        PasteFormat.Decimal or PasteFormat.Octal => 5,
        PasteFormat.Text => 6,
        _ => 4,
    };

    /// <summary>
    /// テキストを判別する。解釈できた形式と、その形式らしいが解釈できなかった形式 (理由付き、選べない) を確からしさの順に返す。
    /// テキストは常に候補にする。<paramref name="binary"/> はクリップボードのバイナリ形式の内容 (あれば最上位)。
    /// </summary>
    public static IReadOnlyList<PasteCandidate> Detect(string? text, PasteOptions? options = null, byte[]? binary = null)
    {
        options ??= new PasteOptions();
        var list = new List<PasteCandidate>();
        if (binary is not null)
        {
            list.Add(new PasteCandidate(PasteFormat.Binary, binary, null));
        }

        if (!string.IsNullOrEmpty(text))
        {
            string sample = Sample(text);
            foreach (PasteFormat format in TextFormats)
            {
                if (TryParse(format, sample, options, detecting: true) is { } candidate)
                {
                    list.Add(candidate);
                }
            }
        }

        // 同じ順位の中では、前回使った形式を先にする (仕様 6)。並べ替えは安定。
        return [.. list.OrderBy(c => c.Rank).ThenBy(c => c.Format == options.Preferred ? 0 : 1)];
    }

    /// <summary>最上位の (選べる) 候補。</summary>
    public static PasteCandidate? Best(IReadOnlyList<PasteCandidate> candidates) => candidates.FirstOrDefault(c => c.IsValid);

    /// <summary>選んだ形式でテキスト全体を変換する (貼り付けのとき)。</summary>
    public static PasteCandidate Parse(PasteFormat format, string text, PasteOptions? options = null) =>
        TryParse(format, text, options ?? new PasteOptions(), detecting: false)
        ?? new PasteCandidate(format, null, new PasteError(1, 1, "format"));

    /// <summary>判別に使う先頭部分 (64 KiB。行の途中で切らない)。</summary>
    private static string Sample(string text)
    {
        if (text.Length <= DetectionChars)
        {
            return text;
        }

        int cut = text.LastIndexOf('\n', DetectionChars);
        return text[..(cut > 0 ? cut : DetectionChars)];
    }

    /// <summary>
    /// その形式として解釈する。その形式らしくない (判別の候補に出さない) 場合は null。判別のとき (<paramref name="detecting"/>)
    /// は、その形式の特徴があるのに解釈できない場合だけ誤りの候補を返す。
    /// </summary>
    private static PasteCandidate? TryParse(PasteFormat format, string text, PasteOptions options, bool detecting)
    {
        var p = new Parser(text, options) { Detecting = detecting };
        if (!detecting && string.IsNullOrWhiteSpace(text) && format is not (PasteFormat.Text or PasteFormat.IntelHex or PasteFormat.SRecord))
        {
            // 長さ 0 の出力 (空の Base64 など) は長さ 0 のバイト列に戻す。
            return new PasteCandidate(format, [], null);
        }

        try
        {
            return format switch
            {
                PasteFormat.HexString => p.HexString(),
                PasteFormat.Array => p.ArrayNotation(),
                PasteFormat.Escape => p.Escape(),
                PasteFormat.BinaryNumbers => p.Numbers(PasteFormat.BinaryNumbers),
                PasteFormat.Decimal => p.Numbers(PasteFormat.Decimal),
                PasteFormat.Octal => p.Numbers(PasteFormat.Octal),
                PasteFormat.Base64 => p.Base64(),
                PasteFormat.Base32 => p.Base32(hex: false),
                PasteFormat.Base32Hex => p.Base32(hex: true),
                PasteFormat.Ascii85 => p.Ascii85(),
                PasteFormat.Z85 => p.Z85(),
                PasteFormat.QuotedPrintable => p.QuotedPrintable(),
                PasteFormat.UrlEncoded => p.UrlEncoded(),
                PasteFormat.UUEncode => p.Uu(xx: false),
                PasteFormat.XXEncode => p.Uu(xx: true),
                PasteFormat.IntelHex => p.IntelHex(),
                PasteFormat.SRecord => p.SRecord(),
                PasteFormat.Json => p.Json(),
                PasteFormat.ScreenDump => p.ScreenDump(),
                PasteFormat.Text => p.Text(),
                _ => null,
            };
        }
        catch (PasteParseException ex)
        {
            return ex.Plausible || !detecting ? new PasteCandidate(format, null, p.ErrorAt(ex.Index, ex.Reason)) : null;
        }
    }

    /// <summary>
    /// アドレスを持つ形式の範囲を、最小のアドレスを先頭にした連続したバイト列にする (「カーソル位置に貼る」)。隙間は詰めずに
    /// <paramref name="fill"/> で埋める。長さは「最大のアドレス + 1 − 最小のアドレス」(EDIT-26 の仕様 5)。
    /// </summary>
    public static byte[] Contiguous(IReadOnlyList<AddressedSegment> segments, byte fill)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        long min = segments.Min(s => s.Address);
        long max = segments.Max(s => s.Address + s.Data.Length);
        if (max - min > MaxAddressedSpan || max - min > Array.MaxLength)
        {
            throw new InvalidDataException("レコードのアドレスの範囲が上限 (4 GiB) を超えます。");
        }

        byte[] result = new byte[max - min];
        result.AsSpan().Fill(fill);
        foreach (AddressedSegment s in segments)
        {
            s.Data.CopyTo(result.AsSpan((int)(s.Address - min)));
        }

        return result;
    }

    // ---- 解釈の本体 ----

    private sealed class PasteParseException(int index, string reason, bool plausible) : Exception(reason)
    {
        public int Index { get; } = index;

        public string Reason { get; } = reason;

        /// <summary>その形式の特徴があり、誤りとして一覧に出すべきか。</summary>
        public bool Plausible { get; } = plausible;
    }

    private sealed class Parser(string text, PasteOptions options)
    {
        /// <summary>判別中か (偽なら、形式を指定した変換。特徴の有無を問わない)。</summary>
        public bool Detecting { get; init; }

        private static PasteParseException Fail(int index, string reason, bool plausible = false) => new(index, reason, plausible);

        public PasteError ErrorAt(int index, string reason)
        {
            int line = 1, column = 1;
            for (int i = 0; i < Math.Min(index, text.Length); i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else if (text[i] != '\r')
                {
                    column++;
                }
            }

            return new PasteError(line, column, reason);
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        private static bool IsHex(char c) => HexValue(c) >= 0;

        // ---- Hex 文字列 (00-overview 6.3。区切りで分けた各まとまりの桁数が偶数) ----

        public PasteCandidate HexString()
        {
            var bytes = new List<byte>();
            int i = 0;
            bool any = false;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c is ',' or ':' or '-' or ';')
                {
                    i++;
                    continue;
                }

                if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' && i + 2 < text.Length && IsHex(text[i + 2]))
                {
                    i += 2;
                }
                else if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
                {
                    i += 2;
                }

                int start = i;
                while (i < text.Length && IsHex(text[i]))
                {
                    i++;
                }

                if (i == start || i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or ':' or '-' or ';' or '\\'))
                {
                    throw Fail(i, "char", plausible: false);
                }

                if ((i - start) % 2 != 0)
                {
                    // 奇数桁 (最後のニブルを補完しない)。位置は対になる桁のない最後の桁。
                    throw Fail(i - 1, "odd", plausible: true);
                }

                for (int k = start; k < i; k += 2)
                {
                    bytes.Add((byte)(HexValue(text[k]) << 4 | HexValue(text[k + 1])));
                }

                any = true;
            }

            if (!any)
            {
                throw Fail(0, "empty");
            }

            return new PasteCandidate(PasteFormat.HexString, [.. bytes], null);
        }

        // ---- 数値列 (10 進・8 進・8 桁の 2 進) ----

        public PasteCandidate Numbers(PasteFormat format)
        {
            var bytes = new List<byte>();
            int i = 0;
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]) || text[i] == ',')
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ',')
                {
                    i++;
                }

                string token = text[start..i];
                int value;
                switch (format)
                {
                    case PasteFormat.BinaryNumbers:
                        if (token.Length != 8 || token.Any(c => c is not ('0' or '1')))
                        {
                            throw Fail(start, "binary");
                        }

                        value = Convert.ToInt32(token, 2);
                        break;
                    case PasteFormat.Octal:
                        if (token.Length is 0 or > 3 || token.Any(c => c is < '0' or > '7') || (value = Convert.ToInt32(token, 8)) > 255)
                        {
                            throw Fail(start, "octal");
                        }

                        break;
                    default:
                        if (token.Length is 0 or > 3 || token.Any(c => !char.IsAsciiDigit(c))
                            || (value = int.Parse(token, CultureInfo.InvariantCulture)) > 255)
                        {
                            throw Fail(start, "decimal");
                        }

                        break;
                }

                bytes.Add((byte)value);
            }

            if (bytes.Count == 0)
            {
                throw Fail(0, "empty");
            }

            return new PasteCandidate(format, [.. bytes], null);
        }

        // ---- 配列表記 ----

        private sealed record NumberToken(int Index, ulong Value, bool Prefixed);

        private sealed class Group(char open, int index)
        {
            public char Open { get; } = open;

            public int Index { get; } = index;

            public List<NumberToken> Numbers { get; } = [];
        }

        private static readonly HashSet<string> Directives = new(StringComparer.OrdinalIgnoreCase)
        {
            "db", "dw", "dd", "dq", "byte", "short", "long", "quad", "data", "datasection", "bytes", "uint8_t", "char",
        };

        public PasteCandidate ArrayNotation()
        {
            var groups = new List<Group>();
            var stack = new Stack<Group>();
            var top = new Group('\0', 0);
            bool keyword = false, anyPrefixed = false;

            // 誤りを読み飛ばして集める (ソースコードの配列のインポート。数値として解釈できない部分をすべて一覧にする)。
            bool collecting = options.CollectErrors && !Detecting;
            var skipped = new List<PasteError>();

            // 解釈できない数値: 誤りを集めて、その語の終わりまで読み飛ばす。
            bool TryNumber(ref int at, out NumberToken token)
            {
                int start = at;
                try
                {
                    token = ReadNumber(ref at);
                    return true;
                }
                catch (PasteParseException ex) when (collecting)
                {
                    skipped.Add(ErrorAt(ex.Index, ex.Reason));
                    at = Math.Max(at, start + 1);
                    while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] is '_' or '.'))
                    {
                        at++;
                    }

                    token = null!;
                    return false;
                }
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/' || c == '#' || c == ';' && stack.Count == 0)
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                    continue;
                }

                if (c is '"' or '\'' && IsSourceLiteral(i, stack.Count > 0))
                {
                    // 文字リテラル 'A' と文字列リテラル "..." (エスケープを解釈) を要素として取り出す (ソースコードの配列のインポートと共通。
                    // TOOL-09 の仕様 4)。Python の bytes リテラル (b"…") はエスケープ文字列の形式で扱う。
                    var literal = new List<byte>();
                    int j = i + 1;
                    while (j < text.Length && text[j] != c && text[j] != '\n')
                    {
                        j = text[j] > 0x7F ? AppendUtf8(j, literal) : Unescape(j, literal, quoted: true);
                    }

                    if (j >= text.Length || text[j] != c)
                    {
                        if (collecting)
                        {
                            skipped.Add(ErrorAt(i, "unclosed"));
                            i = j;
                            continue;
                        }

                        throw Fail(i, "unclosed", plausible: true);
                    }

                    Group target = stack.Count > 0 ? stack.Peek() : top;
                    if (c == '\'')
                    {
                        ulong value = 0;
                        foreach (byte b in literal)
                        {
                            value = value << 8 | b;
                        }

                        target.Numbers.Add(new NumberToken(i, value, Prefixed: true));
                    }
                    else
                    {
                        foreach (byte b in literal)
                        {
                            target.Numbers.Add(new NumberToken(i, b, Prefixed: true));
                        }
                    }

                    anyPrefixed = true;
                    i = j + 1;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    // 文字列は無視する (エスケープ文字列の形式で扱う)。
                    int end = i + 1;
                    while (end < text.Length && text[end] != c)
                    {
                        end += text[end] == '\\' ? 2 : 1;
                    }

                    i = Math.Min(text.Length, end + 1);
                    continue;
                }

                if (c is '{' or '[' or '(')
                {
                    var g = new Group(c, i);
                    groups.Add(g);
                    stack.Push(g);
                    i++;
                    continue;
                }

                if (c is '}' or ']' or ')')
                {
                    if (stack.Count == 0 || stack.Pop().Open != (c == '}' ? '{' : c == ']' ? '[' : '('))
                    {
                        throw Fail(i, "bracket");
                    }

                    i++;
                    continue;
                }

                if (char.IsAsciiLetter(c) || c is '_' or '.')
                {
                    // 識別子・言語の語句 (byte、data、Data.b など) は読み飛ばす。&H は VB の 16 進。
                    if (c is 'h' or 'H' && i > 0 && text[i - 1] == '&')
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or ':'))
                    {
                        i++;
                    }

                    string word = text[start..i].TrimEnd(':').TrimStart('.');
                    keyword |= Directives.Contains(word) || word.StartsWith("Data.", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (c == '-' && options.SourceLiterals && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    // 負の 10 進 (Java の符号付きの配列など。TOOL-09 の仕様 4)。要素の大きさの 2 の補数にする。
                    i++;
                    if (!TryNumber(ref i, out NumberToken positive))
                    {
                        continue;
                    }

                    int bits = (options.ElementSize is 1 or 2 or 4 or 8 ? options.ElementSize : 1) * 8;
                    ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                    ulong negated = unchecked((ulong)-(long)positive.Value) & mask;
                    (stack.Count > 0 ? stack.Peek() : top).Numbers.Add(positive with { Value = negated });
                    continue;
                }

                if (char.IsAsciiDigit(c) || c == '$' || c == '&' && i + 1 < text.Length && text[i + 1] is 'H' or 'h')
                {
                    if (!TryNumber(ref i, out NumberToken token))
                    {
                        continue;
                    }

                    anyPrefixed |= token.Prefixed;
                    (stack.Count > 0 ? stack.Peek() : top).Numbers.Add(token);
                    continue;
                }

                if (char.IsWhiteSpace(c) || c is ',' or '=' or ';' or ':' or '<' or '>' or '*' or '-' or '+' or '!')
                {
                    i++;
                    continue;
                }

                if (collecting)
                {
                    skipped.Add(ErrorAt(i, "char"));
                    i++;
                    continue;
                }

                throw Fail(i, "char");
            }

            if (stack.Count > 0)
            {
                throw Fail(stack.Peek().Index, "bracket", plausible: true);
            }

            // 値は、数値を直接含む最後の括弧の組 (C の data[4] = { … } の { … })。括弧がなければ全体 (アセンブリ・PureBasic)。
            Group? chosen = groups.LastOrDefault(g => g.Numbers.Count > 0);
            bool bracketed = chosen is not null;
            chosen ??= top;
            if (chosen.Numbers.Count == 0 || !bracketed && !anyPrefixed && !keyword)
            {
                throw Fail(0, "array");
            }

            int size = options.ElementSize is 1 or 2 or 4 or 8 ? options.ElementSize : 1;
            ulong max = size == 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
            var bytes = new List<byte>(chosen.Numbers.Count * size);
            foreach (NumberToken n in chosen.Numbers)
            {
                if (n.Value > max)
                {
                    if (collecting)
                    {
                        skipped.Add(ErrorAt(n.Index, "range"));
                        continue;
                    }

                    throw Fail(n.Index, "range", plausible: true);
                }

                for (int k = 0; k < size; k++)
                {
                    int shift = options.BigEndian ? (size - 1 - k) * 8 : k * 8;
                    bytes.Add((byte)(n.Value >> shift));
                }
            }

            return new PasteCandidate(PasteFormat.Array, [.. bytes], null) { SkippedErrors = skipped };
        }

        /// <summary>0x / $ / &amp;H / h 接尾辞 / 10 進の数値。型の接尾辞 (u、L、UL、n、US など) は無視する。</summary>
        private NumberToken ReadNumber(ref int i)
        {
            int start = i;
            bool hex = false, prefixed = false;
            if (text[i] == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
            {
                i += 2;
                hex = prefixed = true;
            }
            else if (text[i] == '$')
            {
                i++;
                hex = prefixed = true;
            }
            else if (text[i] == '&')
            {
                i += 2;
                hex = prefixed = true;
            }

            int digits = i;
            while (i < text.Length && IsHex(text[i]))
            {
                i++;
            }

            string body = text[digits..i];
            if (!hex && i < text.Length && text[i] is 'h' or 'H')
            {
                // MASM の 0DEh。
                i++;
                hex = prefixed = true;
            }
            else if (!hex && body.Any(c => !char.IsAsciiDigit(c)))
            {
                // 10 進の後の英字 (1e5 など) は型の接尾辞として扱わない。
                throw Fail(start, "number");
            }

            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            if (body.Length == 0 || body.Length > 16)
            {
                throw Fail(start, "number", plausible: prefixed);
            }

            if (!hex && options.SourceLiterals && body.Length > 1 && body[0] == '0' && body.All(c => c is >= '0' and <= '7'))
            {
                // C などの 8 進のリテラル (0177。TOOL-09 の仕様 4)。
                return new NumberToken(start, Convert.ToUInt64(body, 8), true);
            }

            ulong value = hex ? ulong.Parse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out ulong d) ? d : throw Fail(start, "number");
            return new NumberToken(start, value, prefixed);
        }

        /// <summary>
        /// <paramref name="i"/> の引用符を配列の要素のリテラルとして読むか: 括弧の中、または代入 (<c>=</c>) の後にあり、Python の bytes リテラル
        /// (<c>b"…"</c>) でないもの。ソースコードのインポート (<see cref="PasteOptions.SourceLiterals"/>) では常に読む。
        /// </summary>
        private bool IsSourceLiteral(int i, bool bracketed)
        {
            if (i > 0 && text[i - 1] is 'b' or 'B' && (i == 1 || !char.IsAsciiLetterOrDigit(text[i - 2])))
            {
                return false;
            }

            return options.SourceLiterals || bracketed || text.LastIndexOf('=', i) >= 0;
        }

        /// <summary>ASCII 以外の文字 1 つを UTF-8 で加える (文字列リテラルの中の日本語など)。</summary>
        private int AppendUtf8(int i, List<byte> bytes)
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, length)));
            return i + length;
        }

        // ---- エスケープ文字列 ----

        public PasteCandidate Escape()
        {
            var bytes = new List<byte>();
            bool python = false;
            int literalStart = -1;
            // Python の bytes リテラル (b"…" / b'…') が並んでいれば、それだけをつなぐ。
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (text[i] is 'b' or 'B' && text[i + 1] is '"' or '\'' && (i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1])))
                {
                    python = true;
                    char quote = text[i + 1];
                    int j = i + 2;
                    literalStart = j;
                    while (j < text.Length && text[j] != quote)
                    {
                        j = Unescape(j, bytes, quoted: true);
                    }

                    if (j >= text.Length)
                    {
                        throw Fail(literalStart, "unclosed", plausible: true);
                    }

                    i = j;
                }
            }

            if (python)
            {
                return new PasteCandidate(PasteFormat.Escape, [.. bytes], null);
            }

            if (!text.Contains('\\'))
            {
                throw Fail(0, "escape");
            }

            string body = text.Trim();
            int from = 0, to = body.Length;
            if (body.Length >= 2 && body[0] is '"' or '\'' && body[^1] == body[0])
            {
                from = 1;
                to = body.Length - 1;
            }

            int offset = text.IndexOf(body, StringComparison.Ordinal);
            var p = new Parser(body[from..to], options);
            for (int k = 0; k < to - from;)
            {
                try
                {
                    k = p.Unescape(k, bytes, quoted: from == 1);
                }
                catch (PasteParseException ex)
                {
                    throw Fail(offset + from + ex.Index, ex.Reason, ex.Plausible);
                }
            }

            return new PasteCandidate(PasteFormat.Escape, [.. bytes], null);
        }

        /// <summary>1 文字 (またはエスケープ 1 つ) を読み、次の位置を返す。引用符の外では空白・改行を無視する。</summary>
        private int Unescape(int i, List<byte> bytes, bool quoted)
        {
            char c = text[i];
            if (c != '\\')
            {
                if (!quoted && char.IsWhiteSpace(c))
                {
                    return i + 1;
                }

                if (c > 0x7F)
                {
                    throw Fail(i, "char");
                }

                bytes.Add((byte)c);
                return i + 1;
            }

            if (i + 1 >= text.Length)
            {
                throw Fail(i, "escape", plausible: true);
            }

            char e = text[i + 1];
            switch (e)
            {
                case 'x' or 'X':
                {
                    int j = i + 2, value = 0, n = 0;
                    while (j < text.Length && n < 2 && IsHex(text[j]))
                    {
                        value = value * 16 + HexValue(text[j]);
                        j++;
                        n++;
                    }

                    if (n == 0)
                    {
                        throw Fail(i, "escape", plausible: true);
                    }

                    bytes.Add((byte)value);
                    return j;
                }

                case >= '0' and <= '7':
                {
                    int j = i + 1, value = 0, n = 0;
                    while (j < text.Length && n < 3 && text[j] is >= '0' and <= '7')
                    {
                        value = value * 8 + (text[j] - '0');
                        j++;
                        n++;
                    }

                    if (value > 255)
                    {
                        throw Fail(i, "escape", plausible: true);
                    }

                    bytes.Add((byte)value);
                    return j;
                }

                default:
                    int b = e switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'a' => 7,
                        'b' => 8,
                        'f' => 12,
                        'v' => 11,
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        '?' => '?',
                        _ => -1,
                    };
                    if (b < 0)
                    {
                        throw Fail(i, "escape", plausible: true);
                    }

                    bytes.Add((byte)b);
                    return i + 2;
            }
        }

        // ---- 文字集合で判別するエンコード ----

        /// <summary>改行だけを取り除く (空白・タブは使えない文字として扱う)。位置の対応表も返す。</summary>
        private (string Body, int[] Map) WithoutLineBreaks()
        {
            var sb = new StringBuilder(text.Length);
            var map = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is not ('\r' or '\n'))
                {
                    sb.Append(text[i]);
                    map.Add(i);
                }
            }

            // 前後の空白は許す。
            string body = sb.ToString();
            int lead = body.Length - body.TrimStart().Length;
            string trimmed = body.Trim();
            return (trimmed, [.. map.Skip(lead).Take(trimmed.Length)]);
        }

        public PasteCandidate Base64()
        {
            (string body, int[] map) = WithoutLineBreaks();
            if (body.Length == 0)
            {
                throw Fail(0, "empty");
            }

            bool url = body.Contains('-') || body.Contains('_');
            int pad = body.Length - body.TrimEnd('=').Length;
            string data = body[..^pad];
            for (int i = 0; i < data.Length; i++)
            {
                char c = data[i];
                bool ok = char.IsAsciiLetterOrDigit(c) || (url ? c is '-' or '_' : c is '+' or '/');
                if (!ok)
                {
                    throw Fail(map[i], "char");
                }
            }

            if (pad > 2 || pad > 0 && body.Length % 4 != 0 || data.Length % 4 == 1)
            {
                throw Fail(map[^1], "length");
            }

            const string std = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            var bytes = new List<byte>(data.Length * 3 / 4);
            int bits = 0, acc = 0;
            foreach (char ch in data)
            {
                char c = url ? ch switch { '-' => '+', '_' => '/', _ => ch } : ch;
                acc = acc << 6 | std.IndexOf(c);
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)(acc >> bits));
                    acc &= (1 << bits) - 1;
                }
            }

            // 余りのビットが 0 でなければ、正しい Base64 の出力ではない (別の形式の可能性が高い)。
            if (acc != 0)
            {
                throw Fail(map[^1], "bits");
            }

            return new PasteCandidate(PasteFormat.Base64, [.. bytes], null);
        }

        public PasteCandidate Base32(bool hex)
        {
            (string body, int[] map) = WithoutLineBreaks();
            string alphabet = hex ? Base32Writer.HexAlphabet : Base32Writer.Standard;
            if (body.Length == 0)
            {
                throw Fail(0, "empty");
            }

            int pad = body.Length - body.TrimEnd('=').Length;
            string data = body[..^pad];
            for (int i = 0; i < data.Length; i++)
            {
                if (alphabet.IndexOf(data[i]) < 0)
                {
                    throw Fail(map[i], "char");
                }
            }

            if (pad > 0 && body.Length % 8 != 0 || (data.Length % 8) is 1 or 3 or 6)
            {
                throw Fail(map[^1], "length");
            }

            var bytes = new List<byte>(data.Length * 5 / 8);
            int bits = 0;
            long acc = 0;
            foreach (char c in data)
            {
                acc = acc << 5 | (long)alphabet.IndexOf(c);
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)(acc >> bits));
                    acc &= (1L << bits) - 1;
                }
            }

            if (acc != 0)
            {
                throw Fail(map[^1], "bits");
            }

            return new PasteCandidate(hex ? PasteFormat.Base32Hex : PasteFormat.Base32, [.. bytes], null);
        }

        public PasteCandidate Ascii85()
        {
            string body = text.Trim();
            if (!body.StartsWith("<~", StringComparison.Ordinal))
            {
                throw Fail(0, "ascii85");
            }

            int offset = text.IndexOf("<~", StringComparison.Ordinal);
            if (!body.EndsWith("~>", StringComparison.Ordinal))
            {
                throw Fail(text.Length, "unclosed", plausible: true);
            }

            var bytes = new List<byte>();
            uint acc = 0;
            int n = 0;
            for (int i = 2; i < body.Length - 2; i++)
            {
                char c = body[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (c == 'z' && n == 0)
                {
                    bytes.AddRange([0, 0, 0, 0]);
                    continue;
                }

                if (c is < '!' or > 'u')
                {
                    throw Fail(offset + i, "char", plausible: true);
                }

                acc = unchecked(acc * 85 + (uint)(c - 33));
                if (++n == 5)
                {
                    bytes.AddRange([(byte)(acc >> 24), (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc]);
                    acc = 0;
                    n = 0;
                }
            }

            if (n == 1)
            {
                throw Fail(offset + body.Length - 2, "length", plausible: true);
            }

            if (n > 0)
            {
                for (int k = n; k < 5; k++)
                {
                    acc = unchecked(acc * 85 + 84);
                }

                for (int k = 0; k < n - 1; k++)
                {
                    bytes.Add((byte)(acc >> (24 - k * 8)));
                }
            }

            return new PasteCandidate(PasteFormat.Ascii85, [.. bytes], null);
        }

        public PasteCandidate Z85()
        {
            (string body, int[] map) = WithoutLineBreaks();
            if (body.Length == 0 || body.Length % 5 != 0)
            {
                throw Fail(0, "length");
            }

            var bytes = new List<byte>(body.Length / 5 * 4);
            for (int i = 0; i < body.Length; i += 5)
            {
                ulong acc = 0;
                for (int k = 0; k < 5; k++)
                {
                    int d = Ascii85Writer.Z85Alphabet.IndexOf(body[i + k]);
                    if (d < 0)
                    {
                        throw Fail(map[i + k], "char");
                    }

                    acc = acc * 85 + (ulong)d;
                }

                if (acc > uint.MaxValue)
                {
                    throw Fail(map[i], "range");
                }

                bytes.AddRange([(byte)(acc >> 24), (byte)(acc >> 16), (byte)(acc >> 8), (byte)acc]);
            }

            return new PasteCandidate(PasteFormat.Z85, [.. bytes], null);
        }

        public PasteCandidate QuotedPrintable()
        {
            var bytes = new List<byte>();
            bool escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '=')
                {
                    // ソフト改行 (= の直後の改行) は取り除く。
                    if (i + 1 < text.Length && text[i + 1] is '\r' or '\n')
                    {
                        i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
                        escaped = true;
                        continue;
                    }

                    if (i + 1 == text.Length)
                    {
                        escaped = true;
                        continue;
                    }

                    if (i + 2 >= text.Length || !IsHex(text[i + 1]) || !IsHex(text[i + 2]))
                    {
                        throw Fail(i, "escape");
                    }

                    bytes.Add((byte)(HexValue(text[i + 1]) << 4 | HexValue(text[i + 2])));
                    i += 2;
                    escaped = true;
                    continue;
                }

                if (c is '\r' or '\n')
                {
                    bytes.Add((byte)c);
                    continue;
                }

                if (c > 0x7E || c < 0x20 && c != '\t')
                {
                    throw Fail(i, "char");
                }

                bytes.Add((byte)c);
            }

            if (!escaped && Detecting)
            {
                throw Fail(0, "qp");
            }

            return new PasteCandidate(PasteFormat.QuotedPrintable, [.. bytes], null);
        }

        public PasteCandidate UrlEncoded()
        {
            var bytes = new List<byte>();
            bool escaped = false;
            string body = text.Trim();
            int offset = text.IndexOf(body, StringComparison.Ordinal);
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '%')
                {
                    if (i + 2 >= body.Length || !IsHex(body[i + 1]) || !IsHex(body[i + 2]))
                    {
                        throw Fail(offset + i, "escape", plausible: escaped);
                    }

                    bytes.Add((byte)(HexValue(body[i + 1]) << 4 | HexValue(body[i + 2])));
                    i += 2;
                    escaped = true;
                }
                else if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '+')
                {
                    bytes.Add(c == '+' ? (byte)' ' : (byte)c);
                }
                else
                {
                    throw Fail(offset + i, "char", plausible: escaped);
                }
            }

            if (!escaped && Detecting)
            {
                throw Fail(0, "url");
            }

            return new PasteCandidate(PasteFormat.UrlEncoded, [.. bytes], null);
        }

        // ---- 構造のある形式 ----

        /// <summary>行に分ける (位置は行の先頭の文字位置)。</summary>
        private List<(int Index, string Line)> Lines()
        {
            var lines = new List<(int, string)>();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    string line = text[start..i].TrimEnd('\r');
                    lines.Add((start, line));
                    start = i + 1;
                }
            }

            return lines;
        }

        public PasteCandidate Uu(bool xx)
        {
            List<(int Index, string Line)> lines = Lines();
            int b = lines.FindIndex(l => l.Line.StartsWith("begin ", StringComparison.Ordinal));
            if (b < 0)
            {
                throw Fail(0, "begin");
            }

            var bytes = new List<byte>();
            int value(char c, int index)
            {
                int v = xx ? UuWriter.XxAlphabet.IndexOf(c) : c == '`' ? 0 : c is >= ' ' and <= '_' ? c - 32 : -1;
                return v >= 0 ? v : throw Fail(index, "char", plausible: true);
            }

            for (int k = b + 1; k < lines.Count; k++)
            {
                (int index, string line) = lines[k];
                if (line == "end")
                {
                    return new PasteCandidate(xx ? PasteFormat.XXEncode : PasteFormat.UUEncode, [.. bytes], null);
                }

                if (line.Length == 0)
                {
                    continue;
                }

                int n = value(line[0], index);
                if (n == 0)
                {
                    continue;
                }

                int needed = (n + 2) / 3 * 4;
                if (line.Length - 1 < needed)
                {
                    throw Fail(index + line.Length, "length", plausible: true);
                }

                for (int g = 0, written = 0; g < needed; g += 4)
                {
                    int c0 = value(line[1 + g], index + 1 + g), c1 = value(line[2 + g], index + 2 + g);
                    int c2 = value(line[3 + g], index + 3 + g), c3 = value(line[4 + g], index + 4 + g);
                    int[] triple = [c0 << 2 | c1 >> 4, (c1 & 15) << 4 | c2 >> 2, (c2 & 3) << 6 | c3];
                    for (int t = 0; t < 3 && written < n; t++, written++)
                    {
                        bytes.Add((byte)triple[t]);
                    }
                }
            }

            throw Fail(text.Length, "end", plausible: true);
        }

        public PasteCandidate IntelHex()
        {
            List<(int Index, string Line)> lines = Lines().Where(l => l.Line.Trim().Length > 0).ToList();
            if (lines.Count == 0 || lines.Any(l => !l.Line.TrimStart().StartsWith(':')))
            {
                throw Fail(0, "ihex", plausible: lines.Count > 0 && lines[0].Line.TrimStart().StartsWith(':'));
            }

            var segments = new List<AddressedSegment>();
            long upper = 0;
            int checksumErrors = 0;
            foreach ((int index, string raw) in lines)
            {
                string line = raw.Trim();
                int at = index + raw.IndexOf(':');
                byte[] record = RecordBytes(line, 1, at);
                if (record.Length < 5 || record[0] + 5 != record.Length)
                {
                    throw Fail(at, "length", plausible: true);
                }

                if ((byte)record.Sum(x => x) != 0)
                {
                    checksumErrors++;
                }

                int count = record[0];
                int address = record[1] << 8 | record[2];
                switch (record[3])
                {
                    case 0:
                        AddSegment(segments, upper + address, record.AsSpan(4, count));
                        break;
                    case 1:
                        return Result(PasteFormat.IntelHex, segments, checksumErrors);
                    case 2:
                        upper = (long)(record[4] << 8 | record[5]) << 4;
                        break;
                    case 4:
                        upper = (long)(record[4] << 8 | record[5]) << 16;
                        break;
                    case 3 or 5:
                        break;
                    default:
                        throw Fail(at + 7, "type", plausible: true);
                }
            }

            return Result(PasteFormat.IntelHex, segments, checksumErrors);
        }

        public PasteCandidate SRecord()
        {
            List<(int Index, string Line)> lines = Lines().Where(l => l.Line.Trim().Length > 0).ToList();
            bool looks = lines.Count > 0 && lines.All(l => l.Line.TrimStart() is ['S', >= '0' and <= '9', ..]);
            if (!looks)
            {
                throw Fail(0, "srec");
            }

            var segments = new List<AddressedSegment>();
            int checksumErrors = 0;
            foreach ((int index, string raw) in lines)
            {
                string line = raw.Trim();
                int at = index + raw.IndexOf('S');
                byte[] record = RecordBytes(line, 2, at);
                if (record.Length < 1 || record[0] + 1 != record.Length)
                {
                    throw Fail(at, "length", plausible: true);
                }

                if ((byte)~record.Take(record.Length - 1).Sum(x => x) != record[^1])
                {
                    checksumErrors++;
                }

                int type = line[1] - '0';
                int addressBytes = type switch { 1 or 9 => 2, 2 or 8 => 3, 3 or 7 => 4, _ => 0 };
                if (type is 1 or 2 or 3)
                {
                    long address = 0;
                    for (int k = 0; k < addressBytes; k++)
                    {
                        address = address << 8 | record[1 + k];
                    }

                    AddSegment(segments, address, record.AsSpan(1 + addressBytes, record.Length - 2 - addressBytes));
                }
                else if (type is 7 or 8 or 9)
                {
                    break;
                }
                else if (type is not (0 or 5 or 6))
                {
                    throw Fail(at + 1, "type", plausible: true);
                }
            }

            return Result(PasteFormat.SRecord, segments, checksumErrors);
        }

        private PasteCandidate Result(PasteFormat format, List<AddressedSegment> segments, int checksumErrors)
        {
            if (segments.Count == 0)
            {
                throw Fail(0, "empty", plausible: true);
            }

            byte[] bytes;
            try
            {
                bytes = Contiguous(segments, options.GapFill);
            }
            catch (InvalidDataException)
            {
                throw Fail(0, "span", plausible: true);
            }

            return new PasteCandidate(format, bytes, null, segments, checksumErrors);
        }

        /// <summary>隣り合うレコードは 1 つの範囲にまとめる。</summary>
        private static void AddSegment(List<AddressedSegment> segments, long address, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            if (segments.Count > 0 && segments[^1] is { } last && last.Address + last.Data.Length == address)
            {
                segments[^1] = new AddressedSegment(last.Address, [.. last.Data, .. data]);
            }
            else
            {
                segments.Add(new AddressedSegment(address, data.ToArray()));
            }
        }

        private byte[] RecordBytes(string line, int start, int at)
        {
            if ((line.Length - start) % 2 != 0)
            {
                throw Fail(at + line.Length - 1, "odd", plausible: true);
            }

            byte[] bytes = new byte[(line.Length - start) / 2];
            for (int k = 0; k < bytes.Length; k++)
            {
                int hi = HexValue(line[start + k * 2]), lo = HexValue(line[start + k * 2 + 1]);
                if (hi < 0 || lo < 0)
                {
                    throw Fail(at + start + k * 2 + (hi < 0 ? 0 : 1), "char", plausible: true);
                }

                bytes[k] = (byte)(hi << 4 | lo);
            }

            return bytes;
        }

        public PasteCandidate Json()
        {
            string body = text.Trim();
            if (body.Length == 0 || body[0] is not ('{' or '['))
            {
                throw Fail(0, "json");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                // 配列表記 ([0xDE, …]) なども [ で始まるため、構文の誤りは候補に出さない。
                throw Fail((int)(ex.BytePositionInLine ?? 0), "json");
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                JsonElement data = root;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (!root.TryGetProperty("data", out data))
                    {
                        throw Fail(0, "data", plausible: true);
                    }
                }

                byte[] bytes;
                if (data.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<byte>();
                    foreach (JsonElement e in data.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Number || !e.TryGetByte(out byte b))
                        {
                            throw Fail(0, "range", plausible: root.ValueKind == JsonValueKind.Object);
                        }

                        list.Add(b);
                    }

                    bytes = [.. list];
                }
                else if (data.ValueKind == JsonValueKind.String && root.ValueKind == JsonValueKind.Object)
                {
                    string s = data.GetString() ?? string.Empty;
                    // Hex 文字列 (偶数桁の 16 進数字だけ) なら Hex、それ以外は Base64。
                    if (s.Length % 2 == 0 && s.All(IsHex))
                    {
                        bytes = Convert.FromHexString(s);
                    }
                    else
                    {
                        try
                        {
                            bytes = Convert.FromBase64String(s);
                        }
                        catch (FormatException)
                        {
                            throw Fail(0, "data", plausible: true);
                        }
                    }
                }
                else
                {
                    throw Fail(0, "data", plausible: root.ValueKind == JsonValueKind.Object);
                }

                return new PasteCandidate(PasteFormat.Json, bytes, null);
            }
        }

        /// <summary>
        /// 画面表示どおり・一般的なダンプ (オフセット・Hex・テキストの列)。オフセットとテキストの列を除き、Hex の部分だけを使う。
        /// 2 行以上ある場合は、オフセットの差が前の行のバイト数と一致することを確かめる。
        /// </summary>
        public PasteCandidate ScreenDump()
        {
            var bytes = new List<byte>();
            long? expectedOffset = null;
            int rows = 0;
            foreach ((int index, string raw) in Lines())
            {
                string line = raw.TrimEnd();
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("```", StringComparison.Ordinal)
                    || trimmed is "\\begin{verbatim}" or "\\end{verbatim}")
                {
                    continue;
                }

                int i = 0;
                while (i < line.Length && line[i] == ' ')
                {
                    i++;
                }

                int start = i;
                while (i < line.Length && IsHex(line[i]))
                {
                    i++;
                }

                int digits = i - start;
                if (i < line.Length && line[i] == ':')
                {
                    i++;
                }

                if (digits is < 4 or > 16 || i >= line.Length || line[i] != ' ')
                {
                    throw Fail(index + i, "offset", plausible: rows > 0);
                }

                long offset = long.Parse(line.AsSpan(start, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (expectedOffset is long expected && offset != expected)
                {
                    throw Fail(index + start, "offset", plausible: true);
                }

                List<byte> row = DumpRow(line, i);
                if (row.Count == 0)
                {
                    throw Fail(index + i, "hex", plausible: rows > 0);
                }

                bytes.AddRange(row);
                expectedOffset = offset + row.Count;
                rows++;
            }

            if (rows == 0)
            {
                throw Fail(0, "dump");
            }

            return new PasteCandidate(PasteFormat.ScreenDump, [.. bytes], null);
        }

        /// <summary>
        /// 1 行の Hex の列を読む。2 桁 (または xxd の 4 桁) の 16 進のまとまりを並び順に読み、残り (テキストの列) が読んだバイト数と同じ
        /// 長さ (|…| で囲む場合は 2 文字多い) になる区切りを探す。テキストの列が 16 進に見える場合にも誤らないため。
        /// </summary>
        private static List<byte> DumpRow(string line, int from)
        {
            var tokens = new List<(int Start, int End)>();
            int i = from;
            while (i < line.Length)
            {
                while (i < line.Length && line[i] == ' ')
                {
                    i++;
                }

                int s = i;
                while (i < line.Length && line[i] != ' ')
                {
                    i++;
                }

                if (i > s)
                {
                    tokens.Add((s, i));
                }
            }

            int hexCount = 0;
            while (hexCount < tokens.Count && IsByteToken(line, tokens[hexCount]))
            {
                hexCount++;
            }

            // 後ろから区切りを試す: k 個のまとまりを Hex とし、残りをテキストとみなせるか。
            for (int k = Math.Min(hexCount, tokens.Count - 1); k >= 1; k--)
            {
                int bytes = tokens.Take(k).Sum(t => (t.End - t.Start) / 2);
                string rest = line[tokens[k].Start..].TrimEnd();
                if (rest.Length == bytes || rest.Length == bytes + 2 && rest[0] == '|' && rest[^1] == '|')
                {
                    // テキストの列の前は 2 文字以上の空白。
                    if (tokens[k].Start - tokens[k - 1].End >= 2)
                    {
                        return ToBytes(line, tokens.Take(k));
                    }
                }
            }

            // テキストの列の長さが合わない場合 (文字コードで幅が違うなど) は、16 進に見える部分をすべて使う。
            return ToBytes(line, tokens.Take(hexCount));
        }

        private static bool IsByteToken(string line, (int Start, int End) t) =>
            (t.End - t.Start) is 2 or 4 && Enumerable.Range(t.Start, t.End - t.Start).All(k => IsHex(line[k]));

        private static List<byte> ToBytes(string line, IEnumerable<(int Start, int End)> tokens)
        {
            var bytes = new List<byte>();
            foreach ((int s, int e) in tokens)
            {
                for (int k = s; k < e; k += 2)
                {
                    bytes.Add((byte)(HexValue(line[k]) << 4 | HexValue(line[k + 1])));
                }
            }

            return bytes;
        }

        // ---- テキスト ----

        public PasteCandidate Text()
        {
            string body = options.NewLines switch
            {
                NewLineConversion.CrLf => text.ReplaceLineEndings("\r\n"),
                NewLineConversion.Lf => text.ReplaceLineEndings("\n"),
                _ => text,
            };
            if (!options.Encoding.TryEncode(body, out byte[] bytes))
            {
                string? bad = options.Encoding.FirstUnencodable(body);
                throw Fail(bad is null ? 0 : Math.Max(0, text.IndexOf(bad, StringComparison.Ordinal)), "encoding", plausible: true);
            }

            if (options.AppendNul)
            {
                bytes = [.. bytes, 0];
            }

            return new PasteCandidate(PasteFormat.Text, bytes, null);
        }
    }
}
