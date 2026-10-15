using System.Globalization;
using System.Text;

namespace HexEditor.Core.Templates.Kaitai;

/// <summary>YAML のノード (.ksy の読み込みに使う部分だけ)。</summary>
internal abstract class YamlNode
{
    public int Line { get; init; }

    public int Column { get; init; }
}

internal sealed class YamlScalar : YamlNode
{
    public required string Value { get; init; }

    /// <summary>引用符で囲んだ (文字列として扱う)。</summary>
    public bool Quoted { get; init; }

    public override string ToString() => Value;
}

internal sealed class YamlSequence : YamlNode
{
    public List<YamlNode> Items { get; } = [];
}

internal sealed class YamlMapping : YamlNode
{
    public List<(YamlScalar Key, YamlNode Value)> Entries { get; } = [];

    public YamlNode? this[string key] => Entries.FirstOrDefault(e => e.Key.Value == key).Value;

    public bool Has(string key) => Entries.Any(e => e.Key.Value == key);
}

internal sealed class YamlException(string code, int line, int column, params string[] args) : Exception(code)
{
    public string Code { get; } = code;

    public int Line { get; } = line;

    public int Column { get; } = column;

    public string[] Args { get; } = args;
}

/// <summary>
/// 安全な YAML の読み込み (TPL-33 の仕様 6)。型の指定 (<c>!!python/object</c> など) によるオブジェクトの生成はせずエラーにし、
/// 別名の展開と入れ子の深さに上限を設ける (「billion laughs」と深い入れ子の対策)。.ksy で使う範囲 (ブロックの対応付け・列、
/// フロー形式、引用符、ブロックスカラー、コメント、アンカーと別名) を扱う。
/// </summary>
internal sealed class YamlParser
{
    public const int MaxDepth = 256;
    public const int MaxAliasExpansions = 100_000;

    private readonly List<string> _lines;
    private readonly Dictionary<string, YamlNode> _anchors = new(StringComparer.Ordinal);
    private int _line;
    private int _expanded;
    private int _depth;

    private YamlParser(string text)
    {
        _lines = [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')];
    }

    public static YamlNode Parse(string text)
    {
        var p = new YamlParser(text);
        p.SkipBlank();
        if (p._line < p._lines.Count && p._lines[p._line].TrimEnd() == "---")
        {
            p._line++;
        }

        p.SkipBlank();
        if (p._line >= p._lines.Count)
        {
            return new YamlMapping { Line = 1, Column = 1 };
        }

        YamlNode node = p.ParseBlock(p.Indent(p._lines[p._line]));
        p.SkipBlank();
        if (p._line < p._lines.Count && p._lines[p._line].TrimEnd() is not ("..." or "---"))
        {
            throw new YamlException(TemplateMessages.YamlSyntax, p._line + 1, p.Indent(p._lines[p._line]) + 1, "unexpected indentation");
        }

        return node;
    }

    private int Indent(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        if (i < line.Length && line[i] == '\t')
        {
            throw new YamlException(TemplateMessages.YamlTab, _line + 1, i + 1);
        }

        return i;
    }

    private static bool IsBlank(string line)
    {
        string t = line.TrimStart(' ', '\t');
        return t.Length == 0 || t[0] == '#';
    }

    private void SkipBlank()
    {
        while (_line < _lines.Count && IsBlank(_lines[_line]))
        {
            _line++;
        }
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw new YamlException(TemplateMessages.YamlTooDeep, _line + 1, 1);
        }
    }

    /// <summary>インデント <paramref name="indent"/> で始まるブロックのノード。</summary>
    private YamlNode ParseBlock(int indent)
    {
        Enter();
        try
        {
            SkipBlank();
            string line = _lines[_line];
            string content = line[indent..];
            if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-")
            {
                return ParseSequence(indent);
            }

            if (FindMappingColon(content) >= 0)
            {
                return ParseMapping(indent);
            }

            // 1 行のスカラー (またはフロー形式)
            int col = indent;
            YamlNode value = ParseInlineValue(StripComment(content), _line, col, indent);
            _line++;
            return value;
        }
        finally
        {
            _depth--;
        }
    }

    private YamlSequence ParseSequence(int indent)
    {
        var seq = new YamlSequence { Line = _line + 1, Column = indent + 1 };
        while (true)
        {
            SkipBlank();
            if (_line >= _lines.Count)
            {
                break;
            }

            string line = _lines[_line];
            int ind = Indent(line);
            if (ind < indent)
            {
                break;
            }

            if (ind > indent)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, _line + 1, ind + 1, "bad indentation of a sequence entry");
            }

            string content = line[indent..];
            if (!(content.StartsWith("- ", StringComparison.Ordinal) || content == "-"))
            {
                break;
            }

            string rest = content.Length > 1 ? content[2..] : string.Empty;
            int restCol = indent + 2;
            int lead = rest.Length - rest.TrimStart(' ').Length;
            rest = rest.TrimStart(' ');
            restCol += lead;
            if (rest.Length == 0 || rest.StartsWith('#'))
            {
                _line++;
                SkipBlank();
                if (_line >= _lines.Count || Indent(_lines[_line]) <= indent)
                {
                    seq.Items.Add(new YamlScalar { Value = string.Empty, Line = _line, Column = restCol + 1 });
                    continue;
                }

                seq.Items.Add(ParseBlock(Indent(_lines[_line])));
                continue;
            }

            if (FindMappingColon(rest) >= 0 && !rest.StartsWith('{') && !rest.StartsWith('['))
            {
                // "- key: value" で始まる対応付け: 残りの行は restCol のインデントで続く
                _lines[_line] = new string(' ', restCol) + rest;
                seq.Items.Add(ParseMapping(restCol));
                continue;
            }

            if (rest.StartsWith("- ", StringComparison.Ordinal))
            {
                _lines[_line] = new string(' ', restCol) + rest;
                seq.Items.Add(ParseSequence(restCol));
                continue;
            }

            int startLine = _line;
            YamlNode value = ParseInlineValue(StripComment(rest), _line, restCol, indent);
            if (_line == startLine)
            {
                _line++;
            }

            seq.Items.Add(value);
        }

        return seq;
    }

    private YamlMapping ParseMapping(int indent)
    {
        var map = new YamlMapping { Line = _line + 1, Column = indent + 1 };
        while (true)
        {
            SkipBlank();
            if (_line >= _lines.Count)
            {
                break;
            }

            string line = _lines[_line];
            int ind = Indent(line);
            if (ind < indent)
            {
                break;
            }

            if (ind > indent)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, _line + 1, ind + 1, "bad indentation of a mapping entry");
            }

            string content = line[indent..];
            if (content.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            int colon = FindMappingColon(content);
            if (colon < 0)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, _line + 1, indent + 1, "':' expected");
            }

            string keyText = content[..colon].TrimEnd();
            var key = new YamlScalar { Value = UnquoteKey(keyText, _line, indent), Line = _line + 1, Column = indent + 1, Quoted = keyText.StartsWith('"') || keyText.StartsWith('\'') };
            if (map.Has(key.Value))
            {
                throw new YamlException(TemplateMessages.YamlDuplicateKey, _line + 1, indent + 1, key.Value);
            }

            string rest = content[(colon + 1)..];
            int restCol = indent + colon + 1;
            int lead = rest.Length - rest.TrimStart(' ').Length;
            rest = StripComment(rest.TrimStart(' '));
            restCol += lead;
            YamlNode value;
            if (rest.Length == 0)
            {
                _line++;
                SkipBlank();
                if (_line < _lines.Count && Indent(_lines[_line]) > indent)
                {
                    value = ParseBlock(Indent(_lines[_line]));
                }
                else if (_line < _lines.Count && Indent(_lines[_line]) == indent && _lines[_line][indent..].StartsWith("- ", StringComparison.Ordinal))
                {
                    // 対応付けの値の列は同じインデントでもよい
                    value = ParseSequence(indent);
                }
                else
                {
                    value = new YamlScalar { Value = string.Empty, Line = key.Line, Column = restCol + 1 };
                }
            }
            else if (rest.StartsWith("&", StringComparison.Ordinal) && !rest.Contains(' ', StringComparison.Ordinal))
            {
                // アンカーだけの行: 値は次の行のブロック
                string anchor = rest[1..];
                _line++;
                SkipBlank();
                value = _line < _lines.Count && Indent(_lines[_line]) > indent ? ParseBlock(Indent(_lines[_line]))
                    : new YamlScalar { Value = string.Empty, Line = key.Line, Column = restCol + 1 };
                _anchors[anchor] = value;
            }
            else
            {
                int startLine = _line;
                value = ParseInlineValue(rest, _line, restCol, indent);
                if (_line == startLine)
                {
                    _line++;
                }
            }

            map.Entries.Add((key, value));
        }

        return map;
    }

    private string UnquoteKey(string key, int line, int col)
    {
        if (key.Length >= 2 && (key[0] == '"' || key[0] == '\''))
        {
            int pos = 0;
            return ReadQuoted(key, ref pos, line, col);
        }

        return key;
    }

    /// <summary>行の中の対応付けの ':' の位置 (引用符・括弧の中は除く。なければ -1)。</summary>
    private static int FindMappingColon(string s)
    {
        if (s.StartsWith('[') || s.StartsWith('{') || s.StartsWith('|') || s.StartsWith('>'))
        {
            return -1;
        }

        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    if (quote == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    quote = '\0';
                }
                else if (c == '\\' && quote == '"')
                {
                    i++;
                }

                continue;
            }

            if ((c == '"' || c == '\'') && i == 0)
            {
                quote = c;
                continue;
            }

            if (c == '#' && i > 0 && s[i - 1] == ' ')
            {
                return -1;
            }

            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripComment(string s)
    {
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"')
                {
                    i++;
                }

                continue;
            }

            if (c is '"' or '\'' && (i == 0 || s[i - 1] is ' ' or '[' or '{' or ',' or ':'))
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (c == '#' && (i == 0 || s[i - 1] == ' '))
            {
                return s[..i].TrimEnd();
            }
        }

        return s.TrimEnd();
    }

    /// <summary>行の中の値 (スカラー、フロー形式、ブロックスカラー、別名)。</summary>
    private YamlNode ParseInlineValue(string text, int line, int col, int parentIndent)
    {
        if (text.StartsWith("!", StringComparison.Ordinal))
        {
            throw new YamlException(TemplateMessages.YamlTag, line + 1, col + 1, text.Split(' ')[0]);
        }

        string? anchor = null;
        if (text.StartsWith('&'))
        {
            int sp = text.IndexOf(' ');
            anchor = sp < 0 ? text[1..] : text[1..sp];
            text = sp < 0 ? string.Empty : text[(sp + 1)..].TrimStart();
            col += sp + 1;
            if (text.StartsWith("!", StringComparison.Ordinal))
            {
                throw new YamlException(TemplateMessages.YamlTag, line + 1, col + 1, text.Split(' ')[0]);
            }
        }

        YamlNode node;
        if (text.StartsWith('*'))
        {
            string name = text[1..].Trim();
            if (!_anchors.TryGetValue(name, out YamlNode? target))
            {
                throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + 1, $"unknown alias '{name}'");
            }

            CountExpansion(target, line, col);
            node = target;
        }
        else if (text.StartsWith('|') || text.StartsWith('>'))
        {
            node = BlockScalar(text, line, col, parentIndent);
        }
        else if (text.StartsWith('[') || text.StartsWith('{'))
        {
            string flow = text;
            // フロー形式が複数の行にまたがる場合は、括弧が閉じるまで連結する
            int startLine = _line;
            while (!Balanced(flow) && _line + 1 < _lines.Count)
            {
                _line++;
                flow += " " + StripComment(_lines[_line].Trim());
            }

            if (_line != startLine)
            {
                _line++;
            }

            int pos = 0;
            node = ParseFlow(flow, ref pos, line, col);
            SkipSpaces(flow, ref pos);
            if (pos < flow.Length)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "unexpected characters after a flow collection");
            }
        }
        else if (text.StartsWith('"') || text.StartsWith('\''))
        {
            int pos = 0;
            string s = ReadQuoted(text, ref pos, line, col);
            if (text[pos..].Trim().Length > 0)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "unexpected characters after a quoted string");
            }

            node = new YamlScalar { Value = s, Quoted = true, Line = line + 1, Column = col + 1 };
        }
        else
        {
            node = new YamlScalar { Value = text.Trim(), Line = line + 1, Column = col + 1 };
        }

        if (anchor is not null)
        {
            _anchors[anchor] = node;
        }

        return node;
    }

    private void CountExpansion(YamlNode target, int line, int col)
    {
        _expanded += Size(target, 0);
        if (_expanded > MaxAliasExpansions)
        {
            throw new YamlException(TemplateMessages.YamlAliasLimit, line + 1, col + 1);
        }
    }

    private static int Size(YamlNode n, int depth)
    {
        if (depth > 64)
        {
            return MaxAliasExpansions + 1;
        }

        long total = 1;
        switch (n)
        {
            case YamlSequence s:
                foreach (YamlNode i in s.Items)
                {
                    total += Size(i, depth + 1);
                    if (total > MaxAliasExpansions)
                    {
                        return MaxAliasExpansions + 1;
                    }
                }

                break;
            case YamlMapping m:
                foreach ((_, YamlNode v) in m.Entries)
                {
                    total += Size(v, depth + 1);
                    if (total > MaxAliasExpansions)
                    {
                        return MaxAliasExpansions + 1;
                    }
                }

                break;
        }

        return (int)Math.Min(total, MaxAliasExpansions + 1);
    }

    private static bool Balanced(string s)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else if (c == '\\' && quote == '"')
                {
                    i++;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
        }

        return depth <= 0 && quote == '\0';
    }

    private YamlScalar BlockScalar(string header, int line, int col, int parentIndent)
    {
        bool folded = header[0] == '>';
        char chomp = header.Length > 1 ? header[1] : ' ';
        var lines = new List<string>();
        _line++;
        int? indent = null;
        while (_line < _lines.Count)
        {
            string l = _lines[_line];
            if (l.Trim().Length == 0)
            {
                lines.Add(string.Empty);
                _line++;
                continue;
            }

            int ind = Indent(l);
            if (ind <= parentIndent)
            {
                break;
            }

            indent ??= ind;
            if (ind < indent)
            {
                break;
            }

            lines.Add(l[indent.Value..]);
            _line++;
        }

        while (lines.Count > 0 && lines[^1].Length == 0 && chomp != '+')
        {
            lines.RemoveAt(lines.Count - 1);
        }

        string text = folded ? string.Join(" ", lines).Replace("  ", "\n", StringComparison.Ordinal) : string.Join("\n", lines);
        if (chomp != '-')
        {
            text += "\n";
        }

        _line--; // 呼び出し側が 1 行進める
        _line++;
        return new YamlScalar { Value = text, Quoted = true, Line = line + 1, Column = col + 1 };
    }

    private YamlNode ParseFlow(string s, ref int pos, int line, int col)
    {
        Enter();
        try
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "unexpected end of a flow collection");
            }

            char c = s[pos];
            if (c == '[')
            {
                var seq = new YamlSequence { Line = line + 1, Column = col + pos + 1 };
                pos++;
                while (true)
                {
                    SkipSpaces(s, ref pos);
                    if (pos >= s.Length)
                    {
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "']' expected");
                    }

                    if (s[pos] == ']')
                    {
                        pos++;
                        return seq;
                    }

                    seq.Items.Add(ParseFlow(s, ref pos, line, col));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',')
                    {
                        pos++;
                    }
                    else if (pos < s.Length && s[pos] != ']')
                    {
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "',' or ']' expected");
                    }
                }
            }

            if (c == '{')
            {
                var map = new YamlMapping { Line = line + 1, Column = col + pos + 1 };
                pos++;
                while (true)
                {
                    SkipSpaces(s, ref pos);
                    if (pos >= s.Length)
                    {
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "'}' expected");
                    }

                    if (s[pos] == '}')
                    {
                        pos++;
                        return map;
                    }

                    int keyCol = col + pos;
                    string key = s[pos] is '"' or '\'' ? ReadQuoted(s, ref pos, line, col) : ReadPlain(s, ref pos, inKey: true);
                    SkipSpaces(s, ref pos);
                    if (pos >= s.Length || s[pos] != ':')
                    {
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "':' expected");
                    }

                    pos++;
                    if (map.Has(key))
                    {
                        throw new YamlException(TemplateMessages.YamlDuplicateKey, line + 1, keyCol + 1, key);
                    }

                    YamlNode value = ParseFlow(s, ref pos, line, col);
                    map.Entries.Add((new YamlScalar { Value = key, Line = line + 1, Column = keyCol + 1 }, value));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',')
                    {
                        pos++;
                    }
                    else if (pos < s.Length && s[pos] != '}')
                    {
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos + 1, "',' or '}' expected");
                    }
                }
            }

            int at = col + pos;
            if (c is '"' or '\'')
            {
                return new YamlScalar { Value = ReadQuoted(s, ref pos, line, col), Quoted = true, Line = line + 1, Column = at + 1 };
            }

            if (c == '!')
            {
                throw new YamlException(TemplateMessages.YamlTag, line + 1, at + 1, ReadPlain(s, ref pos, inKey: false));
            }

            if (c == '*')
            {
                pos++;
                string name = ReadPlain(s, ref pos, inKey: false);
                if (!_anchors.TryGetValue(name, out YamlNode? target))
                {
                    throw new YamlException(TemplateMessages.YamlSyntax, line + 1, at + 1, $"unknown alias '{name}'");
                }

                CountExpansion(target, line, at);
                return target;
            }

            if (c == '&')
            {
                pos++;
                string anchor = ReadPlain(s, ref pos, inKey: false, stopAtSpace: true);
                YamlNode value = ParseFlow(s, ref pos, line, col);
                _anchors[anchor] = value;
                return value;
            }

            return new YamlScalar { Value = ReadPlain(s, ref pos, inKey: false), Line = line + 1, Column = at + 1 };
        }
        finally
        {
            _depth--;
        }
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && s[pos] == ' ')
        {
            pos++;
        }
    }

    private static string ReadPlain(string s, ref int pos, bool inKey, bool stopAtSpace = false)
    {
        int start = pos;
        while (pos < s.Length && s[pos] is not (',' or ']' or '}') && !(inKey && s[pos] == ':') && !(stopAtSpace && s[pos] == ' ')
            && !(s[pos] == ':' && pos + 1 < s.Length && s[pos + 1] == ' ' && !inKey))
        {
            pos++;
        }

        return s[start..pos].Trim();
    }

    private static string ReadQuoted(string s, ref int pos, int line, int col)
    {
        char q = s[pos];
        int start = pos;
        pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (pos >= s.Length)
            {
                throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + start + 1, "unterminated quoted string");
            }

            char c = s[pos];
            if (c == q)
            {
                if (q == '\'' && pos + 1 < s.Length && s[pos + 1] == '\'')
                {
                    sb.Append('\'');
                    pos += 2;
                    continue;
                }

                pos++;
                return sb.ToString();
            }

            if (c == '\\' && q == '"' && pos + 1 < s.Length)
            {
                char e = s[pos + 1];
                pos += 2;
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'x' when pos + 2 <= s.Length:
                        sb.Append((char)int.Parse(s.AsSpan(pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 2;
                        break;
                    case 'u' when pos + 4 <= s.Length:
                        sb.Append((char)int.Parse(s.AsSpan(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default:
                        throw new YamlException(TemplateMessages.YamlSyntax, line + 1, col + pos, $"invalid escape '\\{e}'");
                }

                continue;
            }

            sb.Append(c);
            pos++;
        }
    }
}
