using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Search.Yara;

/// <summary>
/// 文字列の照合の部品 1 つ。ブロック <c>block</c> の中の一致の開始位置と長さを順に返す (重なる一致も返す)。
/// </summary>
internal abstract class YaraMatcher
{
    /// <summary>ワイド (UTF-16LE の ASCII) の形か (fullword の境界の確かめ方)。</summary>
    public bool Wide { get; init; }

    /// <summary>一致の最大の長さ (ブロックの重なり幅を超えるものは、ブロックをまたぐと見つからないことがある)。</summary>
    public abstract int MaxLength { get; }

    /// <param name="atDataStart">ブロックの先頭がスキャンの対象の先頭か (正規表現の ^)。</param>
    /// <param name="atDataEnd">ブロックの終わりがスキャンの対象の終わりか (正規表現の $)。</param>
    public abstract void FindAll(ReadOnlySpan<byte> block, bool atDataStart, bool atDataEnd, YaraLimits limits, Func<int, int, bool> found);
}

/// <summary>固定のバイト列 (テキスト文字列の ascii・wide・xor・base64 の形)。nocase は ASCII の英字の大文字・小文字を区別しない。</summary>
internal sealed class YaraLiteralMatcher(byte[] bytes, bool nocase) : YaraMatcher
{
    public byte[] Bytes { get; } = bytes;

    public override int MaxLength => Bytes.Length;

    public override void FindAll(ReadOnlySpan<byte> block, bool atDataStart, bool atDataEnd, YaraLimits limits, Func<int, int, bool> found)
    {
        if (Bytes.Length == 0 || Bytes.Length > block.Length)
        {
            return;
        }

        ReadOnlySpan<byte> pattern = Bytes;
        int pos = 0;
        if (!nocase || !HasLetter(pattern))
        {
            while (pos <= block.Length - pattern.Length)
            {
                int i = block[pos..].IndexOf(pattern);
                if (i < 0)
                {
                    return;
                }

                if (!found(pos + i, pattern.Length))
                {
                    return;
                }

                pos += i + 1;
            }

            return;
        }

        byte first = pattern[0];
        byte lower = Lower(first), upper = Upper(first);
        int checks = 0;
        while (pos <= block.Length - pattern.Length)
        {
            int i = block[pos..(block.Length - pattern.Length + 1)].IndexOfAny(lower, upper);
            if (i < 0)
            {
                return;
            }

            int at = pos + i;
            if (EqualsIgnoreCase(block.Slice(at, pattern.Length), pattern) && !found(at, pattern.Length))
            {
                return;
            }

            if ((++checks & 0xFFFF) == 0)
            {
                limits.Check();
            }

            pos = at + 1;
        }
    }

    private static bool HasLetter(ReadOnlySpan<byte> s)
    {
        foreach (byte b in s)
        {
            if (char.IsAsciiLetter((char)b))
            {
                return true;
            }
        }

        return false;
    }

    internal static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    internal static byte Upper(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && Lower(a[i]) != Lower(b[i]))
            {
                return false;
            }
        }

        return true;
    }
}

// ---- 16 進文字列 ----

internal abstract record HexNode;

/// <summary>1 バイト (<c>XX</c>、<c>?X</c>、<c>X?</c>、<c>??</c>、<c>~XX</c>)。<see cref="Mask"/> のビットだけを比べる。</summary>
internal sealed record HexByte(byte Value, byte Mask, bool Negate) : HexNode
{
    public bool Test(byte b) => ((b & Mask) == Value) != Negate;

    public bool IsExact => Mask == 0xFF && !Negate;
}

/// <summary>[n-m] (<see cref="Max"/> が -1 なら上限なし)。</summary>
internal sealed record HexJump(int Min, int Max) : HexNode;

/// <summary>( a | b | … )。</summary>
internal sealed record HexAlternation(IReadOnlyList<IReadOnlyList<HexNode>> Options) : HexNode;

/// <summary>
/// 16 進文字列の照合。固定の長さの先頭部分にある最も長い固定のバイト列 (アトム) を IndexOf で探し、そこから全体を照合する
/// (アトムがなければすべての位置を照合する)。ジャンプと選択肢は、開始位置ごとの手数の上限の中で順に試す (最初に見つかった、短い一致を返す)。
/// </summary>
internal sealed class YaraHexMatcher : YaraMatcher
{
    /// <summary>上限のないジャンプ ([n-]、[-]) の長さの上限 (ブロックの重なり幅)。</summary>
    public const int UnboundedJump = 64 * 1024;

    private const int StepBudget = 1 << 16;

    /// <summary>
    /// 選択肢の入れ子の深さの上限 (解釈と照合は入れ子 1 段ごとに再帰するため、スタックオーバーフローにしない。ルールは信頼できない入力)。
    /// </summary>
    public const int MaxNesting = 32;

    /// <summary>ジャンプと選択肢の数の上限 (照合はジャンプ・選択肢ごとに再帰するため、再帰の深さをこの数で抑える)。</summary>
    public const int MaxBranches = 256;

    private readonly IReadOnlyList<HexNode> _nodes;
    private readonly byte[] _atom;
    private readonly int _atomOffset;

    // 照合の状態はスレッドごとに持つ (コンパイルしたルールを複数のスキャンで同時に使っても混ざらない)。
    [ThreadStatic]
    private static int t_steps;

    [ThreadStatic]
    private static YaraLimits? t_limits;

    public YaraHexMatcher(IReadOnlyList<HexNode> nodes)
    {
        _nodes = nodes;
        (_atom, _atomOffset) = FindAtom(nodes);
        MaxLength = (int)Math.Min(int.MaxValue, MaxSpan(nodes));
    }

    public override int MaxLength { get; }

    public override void FindAll(ReadOnlySpan<byte> block, bool atDataStart, bool atDataEnd, YaraLimits limits, Func<int, int, bool> found)
    {
        int checks = 0;
        t_limits = limits;
        if (_atom.Length > 0)
        {
            int pos = _atomOffset;
            while (pos <= block.Length - _atom.Length)
            {
                int i = block[pos..].IndexOf(_atom);
                if (i < 0)
                {
                    return;
                }

                int start = pos + i - _atomOffset;
                if (TryMatch(block, start) is int end && !found(start, end - start))
                {
                    return;
                }

                if ((++checks & 0x3FFF) == 0)
                {
                    limits.Check();
                }

                pos += i + 1;
            }

            return;
        }

        for (int start = 0; start < block.Length; start++)
        {
            if (TryMatch(block, start) is int end && !found(start, end - start))
            {
                return;
            }

            if ((++checks & 0x3FFF) == 0)
            {
                limits.Check();
            }
        }
    }

    private int? TryMatch(ReadOnlySpan<byte> block, int start)
    {
        t_steps = 0;
        int end = Match(block, _nodes, 0, start, null);
        return end >= 0 ? end : null;
    }

    /// <summary>続き (<paramref name="next"/>) を持つ照合。一致すれば終わりの位置、なければ -1。</summary>
    private int Match(ReadOnlySpan<byte> block, IReadOnlyList<HexNode> nodes, int index, int pos, Continuation? next)
    {
        while (index < nodes.Count)
        {
            if (++t_steps > StepBudget)
            {
                return -1;
            }

            // ジャンプが続くと 1 か所の照合でも手間がかかるため、照合の途中でも時間の上限とキャンセルを確かめる。
            if ((t_steps & 0xFFF) == 0)
            {
                t_limits?.Check();
            }

            switch (nodes[index])
            {
                case HexByte b:
                    if (pos >= block.Length || !b.Test(block[pos]))
                    {
                        return -1;
                    }

                    pos++;
                    index++;
                    continue;
                case HexJump j:
                {
                    int max = j.Max < 0 ? UnboundedJump : j.Max;
                    for (int k = j.Min; k <= max && pos + k <= block.Length; k++)
                    {
                        int r = Match(block, nodes, index + 1, pos + k, next);
                        if (r >= 0)
                        {
                            return r;
                        }

                        if (t_steps > StepBudget)
                        {
                            return -1;
                        }
                    }

                    return -1;
                }

                case HexAlternation alt:
                {
                    var rest = new Continuation(nodes, index + 1, next);
                    foreach (IReadOnlyList<HexNode> option in alt.Options)
                    {
                        int r = Match(block, option, 0, pos, rest);
                        if (r >= 0)
                        {
                            return r;
                        }
                    }

                    return -1;
                }
            }
        }

        return next is null ? pos : Match(block, next.Nodes, next.Index, pos, next.Next);
    }

    private sealed record Continuation(IReadOnlyList<HexNode> Nodes, int Index, Continuation? Next);

    /// <summary>固定の長さの先頭部分の、最も長い固定のバイト列とその位置。</summary>
    private static (byte[] Atom, int Offset) FindAtom(IReadOnlyList<HexNode> nodes)
    {
        var best = new List<byte>();
        int bestOffset = 0;
        var run = new List<byte>();
        int runOffset = 0, offset = 0;
        foreach (HexNode node in nodes)
        {
            if (node is HexByte { IsExact: true } b)
            {
                if (run.Count == 0)
                {
                    runOffset = offset;
                }

                run.Add(b.Value);
                offset++;
                if (run.Count > best.Count)
                {
                    best = [.. run];
                    bestOffset = runOffset;
                }

                continue;
            }

            run.Clear();
            if (node is HexByte)
            {
                offset++;
                continue;
            }

            if (node is HexAlternation alt && FixedWidth(alt) is int w)
            {
                offset += w;
                continue;
            }

            break;
        }

        return ([.. best], bestOffset);
    }

    private static int? FixedWidth(HexAlternation alt)
    {
        int? width = null;
        foreach (IReadOnlyList<HexNode> option in alt.Options)
        {
            int w = 0;
            foreach (HexNode n in option)
            {
                switch (n)
                {
                    case HexByte:
                        w++;
                        break;
                    case HexAlternation inner when FixedWidth(inner) is int iw:
                        w += iw;
                        break;
                    default:
                        return null;
                }
            }

            if (width is int prev && prev != w)
            {
                return null;
            }

            width = w;
        }

        return width;
    }

    private static long MaxSpan(IReadOnlyList<HexNode> nodes) => nodes.Sum(n => n switch
    {
        HexByte => 1L,
        HexJump j => j.Max < 0 ? UnboundedJump : j.Max,
        HexAlternation a => a.Options.Max(MaxSpan),
        _ => 0L,
    });

    /// <summary>16 進文字列の中身を読む。誤りは <see cref="FormatException"/> (メッセージは英語の YARA 風)。</summary>
    public static IReadOnlyList<HexNode> Parse(string body)
    {
        int pos = 0, branches = 0;
        List<HexNode> nodes = ParseSequence(body, ref pos, topLevel: true, depth: 0, ref branches);
        if (nodes.Count == 0)
        {
            throw new FormatException("empty hex string");
        }

        if (nodes[0] is HexJump || nodes[^1] is HexJump)
        {
            throw new FormatException("hex string can't start or end with a jump");
        }

        return nodes;
    }

    private static List<HexNode> ParseSequence(string s, ref int pos, bool topLevel, int depth, ref int branches)
    {
        if (depth > MaxNesting)
        {
            throw new FormatException($"hex string alternatives are nested too deeply (maximum {MaxNesting})");
        }

        var nodes = new List<HexNode>();
        while (true)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }

            if (pos >= s.Length)
            {
                if (!topLevel)
                {
                    throw new FormatException("unterminated alternative in hex string");
                }

                return nodes;
            }

            char c = s[pos];
            if (c is '|' or ')')
            {
                if (topLevel)
                {
                    throw new FormatException($"unexpected '{c}' in hex string");
                }

                return nodes;
            }

            if (c == '(')
            {
                pos++;
                CountBranch(ref branches);
                var options = new List<IReadOnlyList<HexNode>>();
                while (true)
                {
                    List<HexNode> option = ParseSequence(s, ref pos, topLevel: false, depth + 1, ref branches);
                    if (option.Count == 0)
                    {
                        throw new FormatException("empty alternative in hex string");
                    }

                    options.Add(option);
                    if (s[pos] == ')')
                    {
                        pos++;
                        break;
                    }

                    pos++; // '|'
                }

                nodes.Add(new HexAlternation(options));
                continue;
            }

            if (c == '[')
            {
                int end = s.IndexOf(']', pos);
                if (end < 0)
                {
                    throw new FormatException("unterminated jump in hex string");
                }

                string inner = s[(pos + 1)..end].Replace(" ", string.Empty, StringComparison.Ordinal);
                pos = end + 1;
                int dash = inner.IndexOf('-', StringComparison.Ordinal);
                int min, max;
                if (dash < 0)
                {
                    min = max = ParseInt(inner);
                }
                else
                {
                    min = dash == 0 ? 0 : ParseInt(inner[..dash]);
                    max = dash == inner.Length - 1 ? -1 : ParseInt(inner[(dash + 1)..]);
                }

                if (max >= 0 && max < min)
                {
                    throw new FormatException("invalid jump range in hex string");
                }

                if (max > UnboundedJump)
                {
                    throw new FormatException($"jump is too large (maximum {UnboundedJump})");
                }

                if (!topLevel && max < 0)
                {
                    throw new FormatException("unbounded jumps not allowed inside alternation");
                }

                CountBranch(ref branches);
                nodes.Add(new HexJump(min, max));
                continue;
            }

            bool negate = false;
            if (c == '~')
            {
                negate = true;
                pos++;
            }

            if (pos + 2 > s.Length)
            {
                throw new FormatException("invalid hex string");
            }

            char hi = s[pos], lo = s[pos + 1];
            pos += 2;
            if (!(char.IsAsciiHexDigit(hi) || hi == '?') || !(char.IsAsciiHexDigit(lo) || lo == '?'))
            {
                throw new FormatException($"invalid character in hex string '{hi}{lo}'");
            }

            if (negate && hi == '?' && lo == '?')
            {
                throw new FormatException("~?? is not allowed");
            }

            byte value = (byte)((hi == '?' ? 0 : Convert.ToInt32(hi.ToString(), 16) << 4) | (lo == '?' ? 0 : Convert.ToInt32(lo.ToString(), 16)));
            byte mask = (byte)((hi == '?' ? 0 : 0xF0) | (lo == '?' ? 0 : 0x0F));
            nodes.Add(new HexByte(value, mask, negate));
        }
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : throw new FormatException("invalid jump in hex string");

    private static void CountBranch(ref int branches)
    {
        if (++branches > MaxBranches)
        {
            throw new FormatException($"too many jumps and alternatives in hex string (maximum {MaxBranches})");
        }
    }
}

// ---- 正規表現 ----

/// <summary>
/// 正規表現の照合。YARA の正規表現を .NET の NonBacktracking の正規表現 (入力の長さに比例する時間で照合する) に書き換え、ブロックを 1 MiB の窓に
/// 分けて (重なりは <see cref="YaraRegex.MaxMatchLength"/>) 照合する。バイトは ISO 8859-1 の文字として扱う。
/// </summary>
internal sealed class YaraRegexMatcher(Regex regex) : YaraMatcher
{
    private const int Window = 1024 * 1024;

    public override int MaxLength => YaraRegex.MaxMatchLength;

    public override void FindAll(ReadOnlySpan<byte> block, bool atDataStart, bool atDataEnd, YaraLimits limits, Func<int, int, bool> found)
    {
        char[] chars = new char[Window + YaraRegex.MaxMatchLength + 2];
        for (int start = 0; start < block.Length; start += Window)
        {
            limits.Check();
            int end = (int)Math.Min(block.Length, (long)start + Window + YaraRegex.MaxMatchLength);
            int owned = Math.Min(block.Length, start + Window);

            // 対象の先頭でない窓は前に、終わりでない窓は後ろに番兵を置き、^ と \z が窓の端で一致しないようにする。
            int lead = start == 0 && atDataStart ? 0 : 1;
            bool tail = !(end == block.Length && atDataEnd);
            if (lead == 1)
            {
                chars[0] = '￿';
            }

            ReadOnlySpan<byte> slice = block[start..end];
            for (int i = 0; i < slice.Length; i++)
            {
                chars[lead + i] = (char)slice[i];
            }

            int length = lead + slice.Length;
            if (tail)
            {
                chars[length++] = '￿';
            }

            ReadOnlySpan<char> text = chars.AsSpan(0, length);
            int from = lead;
            while (from < lead + (owned - start))
            {
                bool any = false;
                foreach (ValueMatch m in regex.EnumerateMatches(text, from))
                {
                    any = true;
                    int at = m.Index - lead;
                    if (at >= owned - start)
                    {
                        break;
                    }

                    if (m.Length > 0 && !found(start + at, m.Length))
                    {
                        return;
                    }

                    // 重なる一致も報告する (次の位置から探し直す)。
                    from = m.Index + 1;
                    break;
                }

                if (!any)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>YARA の正規表現を .NET の正規表現に書き換える。</summary>
internal static class YaraRegex
{
    /// <summary>正規表現の一致の最大の長さ (YARA の RE_SCAN_LIMIT と同じ)。</summary>
    public const int MaxMatchLength = 4096;

    /// <summary>{n,m} の上限。</summary>
    public const int MaxRepeat = 4096;

    private const string WordClass = "A-Za-z0-9_";

    /// <param name="wide">各文字の後ろに 00 を置く (wide 修飾子)。</param>
    public static Regex Compile(string pattern, bool nocase, bool dotAll, bool wide)
    {
        int pos = 0;
        var sb = new StringBuilder();
        TranslateAlternation(pattern, ref pos, sb, nocase, dotAll, wide, depth: 0);
        if (pos != pattern.Length)
        {
            throw new FormatException($"unexpected ')' in regular expression");
        }

        try
        {
            return new Regex(sb.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new FormatException("invalid regular expression: " + e.Message);
        }
    }

    /// <summary>matches 演算子の正規表現 (文字列 (バイト列) との照合)。</summary>
    public static Regex CompileForMatches(string pattern, string flags) =>
        Compile(pattern, flags.Contains('i'), flags.Contains('s'), wide: false);

    private static void TranslateAlternation(string p, ref int pos, StringBuilder sb, bool nocase, bool dotAll, bool wide, int depth)
    {
        if (depth > 64)
        {
            throw new FormatException("regular expression is too deeply nested");
        }

        TranslateSequence(p, ref pos, sb, nocase, dotAll, wide, depth);
        while (pos < p.Length && p[pos] == '|')
        {
            pos++;
            sb.Append('|');
            TranslateSequence(p, ref pos, sb, nocase, dotAll, wide, depth);
        }
    }

    private static void TranslateSequence(string p, ref int pos, StringBuilder sb, bool nocase, bool dotAll, bool wide, int depth)
    {
        while (pos < p.Length && p[pos] is not '|' and not ')')
        {
            var atom = new StringBuilder();
            bool widen = true;
            char c = p[pos];
            switch (c)
            {
                case '(':
                    pos++;
                    if (pos + 1 < p.Length && p[pos] == '?' && p[pos + 1] == ':')
                    {
                        pos += 2;
                    }

                    atom.Append("(?:");
                    TranslateAlternation(p, ref pos, atom, nocase, dotAll, wide, depth + 1);
                    if (pos >= p.Length || p[pos] != ')')
                    {
                        throw new FormatException("missing ')' in regular expression");
                    }

                    pos++;
                    atom.Append(')');
                    widen = false;
                    break;
                case '[':
                    atom.Append(TranslateClass(p, ref pos, nocase));
                    break;
                case '.':
                    pos++;
                    atom.Append(dotAll ? @"[\x00-\xFF]" : @"[\x00-\x09\x0B-\xFF]");
                    break;
                case '^':
                    pos++;
                    atom.Append('^');
                    widen = false;
                    break;
                case '$':
                    pos++;
                    atom.Append(@"\z");
                    widen = false;
                    break;
                case '\\':
                    (string text, bool isAnchor) = TranslateEscape(p, ref pos, nocase);
                    atom.Append(text);
                    widen = !isAnchor;
                    break;
                case '*' or '+' or '?' or '{':
                    throw new FormatException("nothing to repeat in regular expression");
                default:
                    pos++;
                    atom.Append(Literal(c, nocase));
                    break;
            }

            string quantifier = ReadQuantifier(p, ref pos);
            if (wide && widen)
            {
                sb.Append("(?:").Append(atom).Append(@"\x00)");
            }
            else if (quantifier.Length > 0 && !widen && atom[0] != '(')
            {
                throw new FormatException("nothing to repeat in regular expression");
            }
            else
            {
                sb.Append(atom);
            }

            sb.Append(quantifier);
        }
    }

    private static string ReadQuantifier(string p, ref int pos)
    {
        if (pos >= p.Length)
        {
            return string.Empty;
        }

        string q;
        if (p[pos] is '*' or '+' or '?')
        {
            q = p[pos].ToString();
            pos++;
        }
        else if (p[pos] == '{' && p.IndexOf('}', pos) is int end && end > pos
            && Regex.IsMatch(p[(pos + 1)..end], @"^\d*(,\d*)?$") && end > pos + 1)
        {
            string inner = p[(pos + 1)..end];
            pos = end + 1;
            string[] parts = inner.Split(',');
            int min = parts[0].Length == 0 ? 0 : int.Parse(parts[0], CultureInfo.InvariantCulture);
            int? max = parts.Length == 1 ? min : parts[1].Length == 0 ? null : int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (min > MaxRepeat || max > MaxRepeat || (max is int m && m < min))
            {
                throw new FormatException("bad repeat interval in regular expression");
            }

            q = max is int mx ? $"{{{min},{mx}}}" : $"{{{min},}}";
        }
        else
        {
            return string.Empty;
        }

        if (pos < p.Length && p[pos] == '?')
        {
            pos++;
            q += "?";
        }

        return q;
    }

    private static string Literal(char c, bool nocase)
    {
        if (c > 0x7F)
        {
            // ルールのソースの UTF-8 のバイトの並びにする。
            var sb = new StringBuilder("(?:");
            foreach (byte b in Encoding.UTF8.GetBytes(c.ToString()))
            {
                sb.Append($@"\x{b:X2}");
            }

            return sb.Append(')').ToString();
        }

        if (nocase && char.IsAsciiLetter(c))
        {
            return $"[{char.ToLowerInvariant(c)}{char.ToUpperInvariant(c)}]";
        }

        return $@"\x{(int)c:X2}";
    }

    private static (string Text, bool Anchor) TranslateEscape(string p, ref int pos, bool nocase)
    {
        pos++;
        if (pos >= p.Length)
        {
            throw new FormatException("illegal escape sequence in regular expression");
        }

        char e = p[pos++];
        switch (e)
        {
            case 'w': return ($"[{WordClass}]", false);
            case 'W': return (@"[\x00-\x2F\x3A-\x40\x5B-\x5E\x60\x7B-\xFF]", false);
            case 'd': return ("[0-9]", false);
            case 'D': return (@"[\x00-\x2F\x3A-\xFF]", false);
            case 's': return (@"[\t\n\v\f\r ]", false);
            case 'S': return (@"[\x00-\x08\x0E-\x1F\x21-\xFF]", false);
            case 'b': return (@"\b", true);
            case 'B': return (@"\B", true);
            case 't': return (@"\x09", false);
            case 'n': return (@"\x0A", false);
            case 'r': return (@"\x0D", false);
            case 'f': return (@"\x0C", false);
            case 'a': return (@"\x07", false);
            case 'x':
                if (pos + 2 > p.Length || !char.IsAsciiHexDigit(p[pos]) || !char.IsAsciiHexDigit(p[pos + 1]))
                {
                    throw new FormatException("illegal escape sequence in regular expression");
                }

                char value = (char)Convert.ToInt32(p.Substring(pos, 2), 16);
                pos += 2;
                return (nocase && char.IsAsciiLetter(value) ? Literal(value, true) : $@"\x{(int)value:X2}", false);
            default:
                if (char.IsAsciiLetterOrDigit(e))
                {
                    throw new FormatException($"unknown escape sequence '\\{e}' in regular expression");
                }

                return (Literal(e, nocase), false);
        }
    }

    private static string TranslateClass(string p, ref int pos, bool nocase)
    {
        pos++;
        var sb = new StringBuilder("[");
        if (pos < p.Length && p[pos] == '^')
        {
            sb.Append('^');
            pos++;
        }

        bool first = true;
        while (true)
        {
            if (pos >= p.Length)
            {
                throw new FormatException("missing ']' in regular expression");
            }

            char c = p[pos];
            if (c == ']' && !first)
            {
                pos++;
                break;
            }

            first = false;
            int? lo = ClassChar(p, ref pos, sb);
            if (lo is null)
            {
                continue;
            }

            int hi = lo.Value;
            if (pos + 1 < p.Length && p[pos] == '-' && p[pos + 1] != ']')
            {
                pos++;
                hi = ClassChar(p, ref pos, sb) ?? throw new FormatException("bad character range in regular expression");
                if (hi < lo)
                {
                    throw new FormatException("bad character range in regular expression");
                }
            }

            sb.Append($@"\x{lo:X2}");
            if (hi != lo)
            {
                sb.Append($@"-\x{hi:X2}");
            }

            if (nocase)
            {
                // 範囲の英字の大文字・小文字を足す。
                for (int x = lo.Value; x <= hi; x++)
                {
                    if (char.IsAsciiLetter((char)x))
                    {
                        sb.Append($@"\x{(int)(char.IsAsciiLetterLower((char)x) ? char.ToUpperInvariant((char)x) : char.ToLowerInvariant((char)x)):X2}");
                    }
                }
            }
        }

        return sb.Append(']').ToString();
    }

    /// <summary>文字クラスの 1 文字 (\w などのクラスは <paramref name="sb"/> に書いて null)。</summary>
    private static int? ClassChar(string p, ref int pos, StringBuilder sb)
    {
        char c = p[pos++];
        if (c != '\\')
        {
            return c > 0xFF ? throw new FormatException("non-ASCII character in a regular expression class") : c;
        }

        if (pos >= p.Length)
        {
            throw new FormatException("illegal escape sequence in regular expression");
        }

        char e = p[pos++];
        switch (e)
        {
            case 'w': sb.Append(WordClass); return null;
            case 'd': sb.Append("0-9"); return null;
            case 's': sb.Append(@"\t\n\v\f\r "); return null;
            case 'W': sb.Append(@"\x00-\x2F\x3A-\x40\x5B-\x5E\x60\x7B-\xFF"); return null;
            case 'D': sb.Append(@"\x00-\x2F\x3A-\xFF"); return null;
            case 'S': sb.Append(@"\x00-\x08\x0E-\x1F\x21-\xFF"); return null;
            case 't': return 9;
            case 'n': return 10;
            case 'r': return 13;
            case 'f': return 12;
            case 'a': return 7;
            case 'x':
                if (pos + 2 > p.Length)
                {
                    throw new FormatException("illegal escape sequence in regular expression");
                }

                int v = Convert.ToInt32(p.Substring(pos, 2), 16);
                pos += 2;
                return v;
            default:
                return e;
        }
    }
}

/// <summary>テキスト文字列の修飾子から照合の部品を作る (ascii・wide・nocase・xor・base64・base64wide)。</summary>
internal static class YaraTextVariants
{
    public const string StandardAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static byte[] Widen(byte[] s)
    {
        byte[] w = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            w[i * 2] = s[i];
        }

        return w;
    }

    /// <summary>base64 の 3 通りの並び (前に 0〜2 バイトがある場合)。前後の、周りのバイトで変わる文字は取り除く (YARA と同じ)。</summary>
    public static IEnumerable<byte[]> Base64(byte[] s, string alphabet)
    {
        for (int lead = 0; lead < 3; lead++)
        {
            byte[] data = [.. new byte[lead], .. s];
            string encoded = Encode(data, alphabet);
            int skip = lead switch { 0 => 0, 1 => 2, _ => 3 };
            int trim = (data.Length % 3) switch { 1 => 3, 2 => 2, _ => 0 };
            if (encoded.Length - skip - trim <= 0)
            {
                continue;
            }

            yield return Encoding.ASCII.GetBytes(encoded[skip..^trim]);
        }
    }

    private static string Encode(byte[] data, string alphabet)
    {
        string standard = Convert.ToBase64String(data);
        if (alphabet == StandardAlphabet)
        {
            return standard;
        }

        var sb = new StringBuilder(standard.Length);
        foreach (char c in standard)
        {
            sb.Append(c == '=' ? '=' : alphabet[StandardAlphabet.IndexOf(c, StringComparison.Ordinal)]);
        }

        return sb.ToString();
    }
}
