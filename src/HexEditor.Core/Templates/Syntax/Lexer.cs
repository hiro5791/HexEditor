using System.Globalization;
using System.Text;

namespace HexEditor.Core.Templates.Syntax;

/// <summary>
/// HXT の字句解析 (TPL-02)。改行は CRLF・LF のどちらも 1 つの改行として数える (TPL-01 の仕様 1)。
/// 接頭辞のない数値は常に 10 進 (TPL-02 の仕様 4)。
/// </summary>
internal sealed class Lexer
{
    private readonly string _text;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private bool _atLineStart = true;
    private StringBuilder? _doc;

    public Lexer(string text, string file, DiagnosticBag diagnostics)
    {
        _text = text;
        _file = file;
        _diagnostics = diagnostics;
    }

    public static List<Token> Tokenize(string text, string file, DiagnosticBag diagnostics)
    {
        var lexer = new Lexer(text, file, diagnostics);
        var tokens = new List<Token>();
        while (true)
        {
            Token t = lexer.Next();
            tokens.Add(t);
            if (t.Kind == TokenKind.End)
            {
                return tokens;
            }
        }
    }

    private char Peek(int ahead = 0) => _pos + ahead < _text.Length ? _text[_pos + ahead] : '\0';

    private int Column => _pos - _lineStart + 1;

    private TemplateSourceLocation Here => new(_file, _line, Column);

    private void NewLine()
    {
        _line++;
        _lineStart = _pos;
        _atLineStart = true;
    }

    private void SkipTrivia()
    {
        while (_pos < _text.Length)
        {
            char c = _text[_pos];
            if (c == '\r')
            {
                _pos++;
                if (Peek() == '\n')
                {
                    _pos++;
                }

                NewLine();
            }
            else if (c == '\n')
            {
                _pos++;
                NewLine();
            }
            else if (c is ' ' or '\t' or '\f' or '\v' or '﻿')
            {
                _pos++;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                bool doc = Peek(2) == '/' && Peek(3) != '/';
                int start = _pos + (doc ? 3 : 2);
                while (_pos < _text.Length && _text[_pos] is not ('\r' or '\n'))
                {
                    _pos++;
                }

                if (doc)
                {
                    _doc ??= new StringBuilder();
                    if (_doc.Length > 0)
                    {
                        _doc.Append('\n');
                    }

                    _doc.Append(_text.AsSpan(start, _pos - start).Trim());
                }
                else
                {
                    // 普通のコメントはドキュメントコメントを打ち切る (TC-TPL-02-03)
                    _doc = null;
                }
            }
            else if (c == '/' && Peek(1) == '*')
            {
                TemplateSourceLocation at = Here;
                _pos += 2;
                bool closed = false;
                while (_pos < _text.Length)
                {
                    if (_text[_pos] == '*' && Peek(1) == '/')
                    {
                        _pos += 2;
                        closed = true;
                        break;
                    }

                    if (_text[_pos] == '\r')
                    {
                        _pos++;
                        if (Peek() == '\n')
                        {
                            _pos++;
                        }

                        NewLine();
                    }
                    else if (_text[_pos] == '\n')
                    {
                        _pos++;
                        NewLine();
                    }
                    else
                    {
                        _pos++;
                    }
                }

                if (!closed)
                {
                    _diagnostics.Error(TemplateMessages.UnterminatedComment, at);
                }
            }
            else
            {
                return;
            }
        }
    }

    public Token Next()
    {
        SkipTrivia();
        int line = _line;
        int col = Column;
        int start = _pos;
        string? doc = _doc?.ToString();
        _doc = null;
        bool lineStart = _atLineStart;
        _atLineStart = false;
        if (_pos >= _text.Length)
        {
            return new Token(TokenKind.End, string.Empty, line, col, start);
        }

        char c = _text[_pos];
        Token Make(TokenKind kind, int length) => new(kind, _text.Substring(start, length), line, col, start) { DocComment = doc };

        if (c == '#' && lineStart)
        {
            return Directive(line, col, start);
        }

        if (c == 'x' && Peek(1) == '"')
        {
            _pos++;
            Token s = StringLiteral(line, col, start);
            return ByteString(s, doc);
        }

        if (IsIdentStart(c, out int width))
        {
            _pos += width;
            while (_pos < _text.Length && IsIdentPart(_text[_pos], out int w))
            {
                _pos += w;
            }

            return Make(TokenKind.Identifier, _pos - start);
        }

        if (char.IsAsciiDigit(c))
        {
            return Number(line, col, start, doc);
        }

        if (c == '"')
        {
            return StringLiteral(line, col, start) with { DocComment = doc };
        }

        if (c == '\'')
        {
            return CharLiteral(line, col, start, doc);
        }

        (TokenKind kind, int len) = Operator();
        if (len == 0)
        {
            _diagnostics.Error(TemplateMessages.UnexpectedCharacter, new TemplateSourceLocation(_file, line, col), c.ToString());
            _pos++;
            return Next();
        }

        _pos += len;
        return Make(kind, len);
    }

    private (TokenKind, int) Operator()
    {
        char c = Peek();
        char d = Peek(1);
        char e = Peek(2);
        return c switch
        {
            '(' => (TokenKind.LParen, 1),
            ')' => (TokenKind.RParen, 1),
            '[' => (TokenKind.LBracket, 1),
            ']' => (TokenKind.RBracket, 1),
            '{' => (TokenKind.LBrace, 1),
            '}' => (TokenKind.RBrace, 1),
            ',' => (TokenKind.Comma, 1),
            ';' => (TokenKind.Semicolon, 1),
            ':' => d == ':' ? (TokenKind.ColonColon, 2) : (TokenKind.Colon, 1),
            '.' => d == '.' && e == '.' ? (TokenKind.Ellipsis, 3) : (TokenKind.Dot, 1),
            '?' => (TokenKind.Question, 1),
            '@' => (TokenKind.At, 1),
            '$' => (TokenKind.Dollar, 1),
            '~' => (TokenKind.Tilde, 1),
            '+' => d == '=' ? (TokenKind.PlusAssign, 2) : (TokenKind.Plus, 1),
            '-' => d == '>' ? (TokenKind.Arrow, 2) : d == '=' ? (TokenKind.MinusAssign, 2) : (TokenKind.Minus, 1),
            '*' => d == '=' ? (TokenKind.StarAssign, 2) : (TokenKind.Star, 1),
            '/' => d == '=' ? (TokenKind.SlashAssign, 2) : (TokenKind.Slash, 1),
            '%' => d == '=' ? (TokenKind.PercentAssign, 2) : (TokenKind.Percent, 1),
            '^' => d == '=' ? (TokenKind.CaretAssign, 2) : (TokenKind.Caret, 1),
            '!' => d == '=' ? (TokenKind.NotEq, 2) : (TokenKind.Bang, 1),
            '=' => d == '=' ? (TokenKind.EqEq, 2) : d == '>' ? (TokenKind.FatArrow, 2) : (TokenKind.Assign, 1),
            '&' => d == '&' ? (TokenKind.AmpAmp, 2) : d == '=' ? (TokenKind.AmpAssign, 2) : (TokenKind.Amp, 1),
            '|' => d == '|' ? (TokenKind.PipePipe, 2) : d == '=' ? (TokenKind.PipeAssign, 2) : (TokenKind.Pipe, 1),
            '<' => d == '<' ? (e == '=' ? (TokenKind.ShlAssign, 3) : (TokenKind.Shl, 2)) : d == '=' ? (TokenKind.LessEq, 2) : (TokenKind.Less, 1),
            '>' => d == '>' ? (e == '=' ? (TokenKind.ShrAssign, 3) : (TokenKind.Shr, 2)) : d == '=' ? (TokenKind.GreaterEq, 2) : (TokenKind.Greater, 1),
            _ => (TokenKind.End, 0),
        };
    }

    private Token Directive(int line, int col, int start)
    {
        int end = _pos;
        while (end < _text.Length && _text[end] is not ('\r' or '\n'))
        {
            end++;
        }

        string lineText = _text[_pos..end];
        _pos = end;
        var at = new TemplateSourceLocation(_file, line, col);
        string body = lineText[1..].TrimStart();
        if (body.StartsWith("pragma", StringComparison.Ordinal) && (body.Length == 6 || char.IsWhiteSpace(body[6])))
        {
            return new Token(TokenKind.Pragma, lineText, line, col, start) { StringValue = StripLineComment(body[6..]).Trim() };
        }

        if (body.StartsWith("include", StringComparison.Ordinal))
        {
            string rest = StripLineComment(body[7..]).Trim();
            if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
            {
                return new Token(TokenKind.Include, lineText, line, col, start) { StringValue = rest[1..^1] };
            }

            _diagnostics.Error(TemplateMessages.Expected, at, "\"");
            return Next();
        }

        _diagnostics.Error(TemplateMessages.UnexpectedToken, at, lineText.Split(' ')[0]);
        return Next();
    }

    /// <summary>プラグマの行の末尾の <c>//</c> コメントを除く (引用符の中は除かない)。</summary>
    private static string StripLineComment(string s)
    {
        bool quoted = false;
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '"' && (i == 0 || s[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (!quoted && s[i] == '/' && s[i + 1] == '/')
            {
                return s[..i];
            }
        }

        return s;
    }

    private static bool IsIdentStart(char c, out int width)
    {
        width = 1;
        if (c == '_' || char.IsAsciiLetter(c))
        {
            return true;
        }

        if (c < 0x80)
        {
            return false;
        }

        if (char.IsHighSurrogate(c))
        {
            width = 2;
            return true;
        }

        return char.IsLetter(c);
    }

    private static bool IsIdentPart(char c, out int width)
    {
        width = 1;
        if (c == '_' || char.IsAsciiLetterOrDigit(c))
        {
            return true;
        }

        if (c < 0x80)
        {
            return false;
        }

        if (char.IsHighSurrogate(c))
        {
            width = 2;
            return true;
        }

        UnicodeCategory cat = char.GetUnicodeCategory(c);
        return char.IsLetterOrDigit(c) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation;
    }

    private Token Number(int line, int col, int start, string? doc)
    {
        // 数字・英字・_ の連続を 1 つの数値とみなし、後で検査する (0xZZ のような誤りを 1 つのエラーにする)
        var at = new TemplateSourceLocation(_file, line, col);
        bool isFloat = false;
        if (Peek() == '0' && Peek(1) is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            _pos += 2;
            while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }
        }
        else
        {
            while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }

            if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isFloat = true;
                _pos++;
                while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '_'))
                {
                    _pos++;
                }
            }

            if (Peek() is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                // 16 進の h 接尾辞 (1E5h など) と区別する: 指数の後に英字が続く場合は 16 進として扱う
                int save = _pos;
                _pos += Peek(1) is '+' or '-' ? 2 : 1;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
                {
                    _pos++;
                }

                if (_pos < _text.Length && (char.IsAsciiLetter(_text[_pos]) || _text[_pos] == '_'))
                {
                    _pos = save;
                }
                else
                {
                    isFloat = true;
                }
            }

            while (_pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
            {
                _pos++;
            }
        }

        string text = _text[start.._pos];
        if (isFloat)
        {
            if (double.TryParse(text.Replace("_", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
            {
                return new Token(TokenKind.Float, text, line, col, start) { FloatValue = f, DocComment = doc };
            }
        }

        if (TryParseInteger(text, out Int128 value))
        {
            return new Token(TokenKind.Integer, text, line, col, start) { IntValue = value, DocComment = doc };
        }

        _diagnostics.Error(TemplateMessages.InvalidNumber, at, text);
        return new Token(TokenKind.Integer, text, line, col, start) { IntValue = 0, DocComment = doc };
    }

    /// <summary>数値リテラル (00 の 6.1 の書き方。接頭辞なしは 10 進)。</summary>
    public static bool TryParseInteger(string text, out Int128 value)
    {
        value = 0;
        string t = text.Replace("_", string.Empty);
        if (t.Length == 0 || text.StartsWith('_') || text.EndsWith('_'))
        {
            return false;
        }

        int radix = 10;
        string digits = t;
        if (t.Length > 2 && t[0] == '0' && t[1] is 'x' or 'X')
        {
            radix = 16;
            digits = t[2..];
        }
        else if (t.Length > 2 && t[0] == '0' && t[1] is 'b' or 'B')
        {
            radix = 2;
            digits = t[2..];
        }
        else if (t.Length > 2 && t[0] == '0' && t[1] is 'o' or 'O')
        {
            radix = 8;
            digits = t[2..];
        }
        else if (t.Length > 1 && t[^1] is 'h' or 'H' && char.IsAsciiDigit(t[0]))
        {
            radix = 16;
            digits = t[..^1];
        }

        if (digits.Length == 0)
        {
            return false;
        }

        UInt128 acc = 0;
        foreach (char ch in digits)
        {
            int d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => ch - 'a' + 10,
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => 99,
            };
            if (d >= radix)
            {
                return false;
            }

            if (acc > (UInt128.MaxValue - (UInt128)d) / (UInt128)radix)
            {
                return false;
            }

            acc = acc * (UInt128)radix + (UInt128)d;
        }

        value = (Int128)acc;
        return true;
    }

    private Token StringLiteral(int line, int col, int start)
    {
        var at = new TemplateSourceLocation(_file, line, col);
        _pos++; // "
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length || _text[_pos] is '\r' or '\n')
            {
                _diagnostics.Error(TemplateMessages.UnterminatedString, at);
                break;
            }

            char c = _text[_pos];
            if (c == '"')
            {
                _pos++;
                break;
            }

            if (c == '\\')
            {
                ReadEscape(sb);
                continue;
            }

            sb.Append(c);
            _pos++;
        }

        return new Token(TokenKind.String, _text[start.._pos], line, col, start) { StringValue = sb.ToString() };
    }

    private void ReadEscape(StringBuilder sb)
    {
        var at = Here;
        _pos++; // \
        char e = Peek();
        _pos++;
        switch (e)
        {
            case 'n': sb.Append('\n'); return;
            case 'r': sb.Append('\r'); return;
            case 't': sb.Append('\t'); return;
            case '0': sb.Append('\0'); return;
            case '\\': sb.Append('\\'); return;
            case '"': sb.Append('"'); return;
            case '\'': sb.Append('\''); return;
            case 'x':
                if (Uri.IsHexDigit(Peek()) && Uri.IsHexDigit(Peek(1)))
                {
                    sb.Append((char)Convert.ToInt32(_text.Substring(_pos, 2), 16));
                    _pos += 2;
                    return;
                }

                break;
            case 'u':
                if (Peek() == '{')
                {
                    int close = _text.IndexOf('}', _pos);
                    if (close > _pos + 1 && close - _pos <= 9
                        && int.TryParse(_text.AsSpan(_pos + 1, close - _pos - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
                        && Rune.IsValid(cp))
                    {
                        sb.Append(new Rune(cp).ToString());
                        _pos = close + 1;
                        return;
                    }
                }

                break;
        }

        _diagnostics.Error(TemplateMessages.InvalidEscape, at, "\\" + e);
    }

    private Token CharLiteral(int line, int col, int start, string? doc)
    {
        var at = new TemplateSourceLocation(_file, line, col);
        _pos++;
        var sb = new StringBuilder();
        while (_pos < _text.Length && _text[_pos] != '\'' && _text[_pos] is not ('\r' or '\n'))
        {
            if (_text[_pos] == '\\')
            {
                ReadEscape(sb);
            }
            else
            {
                sb.Append(_text[_pos]);
                _pos++;
            }
        }

        if (Peek() == '\'')
        {
            _pos++;
        }
        else
        {
            _diagnostics.Error(TemplateMessages.UnterminatedString, at);
        }

        string s = sb.ToString();
        int value = s.Length == 1 ? s[0] : 0;
        if (s.Length != 1)
        {
            _diagnostics.Error(TemplateMessages.UnexpectedToken, at, _text[start.._pos]);
        }

        return new Token(TokenKind.Char, _text[start.._pos], line, col, start) { IntValue = value, DocComment = doc };
    }

    private Token ByteString(Token s, string? doc)
    {
        var at = new TemplateSourceLocation(_file, s.Line, s.Column);
        if (!HexPattern.TryParse(s.StringValue ?? string.Empty, out byte[] bytes, out byte[] mask))
        {
            _diagnostics.Error(TemplateMessages.InvalidByteString, at, s.StringValue ?? string.Empty);
            bytes = [];
            mask = [];
        }

        return new Token(TokenKind.ByteString, s.Text, s.Line, s.Column, s.Position)
        {
            Bytes = bytes,
            Mask = mask,
            DocComment = doc,
        };
    }
}

/// <summary>
/// ワイルドカード付きの Hex のバイト列 (00 の 6.3)。<c>??</c> はバイト全体、<c>?</c> は 1 桁 (4 bit) のワイルドカード。
/// </summary>
public static class HexPattern
{
    public static bool TryParse(string text, out byte[] bytes, out byte[] mask)
    {
        var b = new List<byte>();
        var m = new List<byte>();
        var digits = new List<char>();
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == ',')
            {
                continue;
            }

            if (!Uri.IsHexDigit(c) && c != '?')
            {
                bytes = [];
                mask = [];
                return false;
            }

            digits.Add(c);
        }

        if (digits.Count % 2 != 0)
        {
            bytes = [];
            mask = [];
            return false;
        }

        for (int i = 0; i < digits.Count; i += 2)
        {
            int hi = digits[i] == '?' ? -1 : Convert.ToInt32(digits[i].ToString(), 16);
            int lo = digits[i + 1] == '?' ? -1 : Convert.ToInt32(digits[i + 1].ToString(), 16);
            b.Add((byte)(((hi < 0 ? 0 : hi) << 4) | (lo < 0 ? 0 : lo)));
            m.Add((byte)((hi < 0 ? 0 : 0xF0) | (lo < 0 ? 0 : 0x0F)));
        }

        bytes = [.. b];
        mask = [.. m];
        return true;
    }

    public static bool Matches(ReadOnlySpan<byte> data, byte[] bytes, byte[] mask)
    {
        if (data.Length < bytes.Length)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            if ((data[i] & mask[i]) != (bytes[i] & mask[i]))
            {
                return false;
            }
        }

        return true;
    }
}
