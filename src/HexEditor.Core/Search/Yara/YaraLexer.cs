using System.Globalization;
using System.Text;

namespace HexEditor.Core.Search.Yara;

/// <summary>YARA のルールの字句の種類。</summary>
internal enum YaraTokenKind
{
    End,
    Identifier,
    StringIdentifier,   // $a、$a*、$
    StringCount,        // #a
    StringOffset,       // @a
    StringLength,       // !a
    Integer,
    Float,
    Text,               // "…"
    Regex,              // /…/is
    HexString,          // { … } (strings の中だけ)
    Keyword,
    Symbol,
}

/// <summary>字句 1 つ。<see cref="Text"/> は識別子・キーワード・記号の綴り、<see cref="Bytes"/> は文字列の内容 (UTF-8 のバイト)。</summary>
internal sealed record YaraToken(YaraTokenKind Kind, string Text, int Line, int Column)
{
    public long Integer { get; init; }

    public double Float { get; init; }

    public byte[] Bytes { get; init; } = [];

    /// <summary>正規表現の修飾子 (i、s)。</summary>
    public string Flags { get; init; } = string.Empty;

    public bool Is(string keywordOrSymbol) => (Kind is YaraTokenKind.Keyword or YaraTokenKind.Symbol) && Text == keywordOrSymbol;

    public override string ToString() => Kind switch
    {
        YaraTokenKind.End => "end of file",
        YaraTokenKind.Text => "\"" + Encoding.UTF8.GetString(Bytes) + "\"",
        _ => Text,
    };
}

/// <summary>ルールのコンパイルの誤り (ファイル名・行番号・メッセージ。FIND-33 の「エラー」)。</summary>
public sealed class YaraCompileException(string message, string? file, int line) : Exception(message)
{
    public string? File { get; } = file;

    public int Line { get; } = line;
}

/// <summary>
/// YARA のルールの字句解析。16 進文字列 (<c>{ … }</c>) と正規表現 (<c>/…/</c>) は文脈で決まるため、構文解析から <see cref="ReadHexString"/>・
/// 次の字句を正規表現として読むかを指示する。
/// </summary>
internal sealed class YaraLexer
{
    private static readonly HashSet<string> Keywords =
    [
        "all", "and", "any", "ascii", "at", "base64", "base64wide", "condition", "contains", "defined", "endswith", "entrypoint", "false",
        "filesize", "for", "fullword", "global", "icontains", "iendswith", "iequals", "import", "in", "include", "int16", "int16be", "int32",
        "int32be", "int8", "int8be", "istartswith", "matches", "meta", "nocase", "none", "not", "of", "or", "private", "rule", "startswith",
        "strings", "them", "true", "uint16", "uint16be", "uint32", "uint32be", "uint8", "uint8be", "wide", "xor",
    ];

    private static readonly string[] Symbols = ["..", "==", "!=", "<=", ">=", "<<", ">>", "<", ">", "(", ")", "[", "]", "{", "}", ",", ":", "=", ".", "+", "-", "*", "\\", "%", "&", "|", "^", "~"];

    private readonly string _source;
    private readonly string? _file;
    private int _pos;
    private int _line = 1;
    private int _lineStart;

    public YaraLexer(string source, string? file)
    {
        _source = source;
        _file = file;
    }

    public string? File => _file;

    public YaraCompileException Error(string message, int line) => new(message, _file, line);

    private YaraCompileException Error(string message) => new(message, _file, _line);

    /// <summary>次の字句。<paramref name="regexAllowed"/> なら <c>/</c> で始まる正規表現を読む。</summary>
    public YaraToken Next(bool regexAllowed = false)
    {
        SkipSpace();
        int line = _line, column = _pos - _lineStart + 1;
        if (_pos >= _source.Length)
        {
            return new YaraToken(YaraTokenKind.End, string.Empty, line, column);
        }

        char c = _source[_pos];
        if (char.IsAsciiLetter(c) || c == '_')
        {
            string word = ReadWord();
            return new YaraToken(Keywords.Contains(word) ? YaraTokenKind.Keyword : YaraTokenKind.Identifier, word, line, column);
        }

        if (c is '$' or '#' or '@' || (c == '!' && _pos + 1 < _source.Length && (char.IsAsciiLetterOrDigit(_source[_pos + 1]) || _source[_pos + 1] == '_')))
        {
            _pos++;
            string name = _pos < _source.Length && (char.IsAsciiLetterOrDigit(_source[_pos]) || _source[_pos] == '_') ? ReadWord() : string.Empty;
            if (c == '$' && _pos < _source.Length && _source[_pos] == '*')
            {
                _pos++;
                name += "*";
            }

            YaraTokenKind kind = c switch
            {
                '$' => YaraTokenKind.StringIdentifier,
                '#' => YaraTokenKind.StringCount,
                '@' => YaraTokenKind.StringOffset,
                _ => YaraTokenKind.StringLength,
            };
            return new YaraToken(kind, c + name, line, column);
        }

        if (char.IsAsciiDigit(c))
        {
            return ReadNumber(line, column);
        }

        if (c == '"')
        {
            return new YaraToken(YaraTokenKind.Text, "\"", line, column) { Bytes = ReadText() };
        }

        if (c == '/' && regexAllowed)
        {
            return ReadRegex(line, column);
        }

        foreach (string symbol in Symbols)
        {
            if (string.CompareOrdinal(_source, _pos, symbol, 0, symbol.Length) == 0)
            {
                _pos += symbol.Length;
                return new YaraToken(YaraTokenKind.Symbol, symbol, line, column);
            }
        }

        throw Error($"unexpected character '{c}'");
    }

    /// <summary>位置を保存・復元する (先読み)。</summary>
    public (int Pos, int Line, int LineStart) Mark() => (_pos, _line, _lineStart);

    public void Reset((int Pos, int Line, int LineStart) mark) => (_pos, _line, _lineStart) = mark;

    private void SkipSpace()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\n')
            {
                _pos++;
                _line++;
                _lineStart = _pos;
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '/')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                {
                    _pos++;
                }
            }
            else if (c == '/' && _pos + 1 < _source.Length && _source[_pos + 1] == '*')
            {
                int end = _source.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Error("unterminated comment");
                }

                for (int i = _pos; i < end; i++)
                {
                    if (_source[i] == '\n')
                    {
                        _line++;
                        _lineStart = i + 1;
                    }
                }

                _pos = end + 2;
            }
            else
            {
                break;
            }
        }
    }

    private string ReadWord()
    {
        int start = _pos;
        while (_pos < _source.Length && (char.IsAsciiLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
        {
            _pos++;
        }

        return _source[start.._pos];
    }

    private YaraToken ReadNumber(int line, int column)
    {
        int start = _pos;
        if (_source[_pos] == '0' && _pos + 1 < _source.Length && _source[_pos + 1] is 'x' or 'X' or 'o' or 'O')
        {
            bool hex = _source[_pos + 1] is 'x' or 'X';
            _pos += 2;
            int digits = _pos;
            while (_pos < _source.Length && (hex ? char.IsAsciiHexDigit(_source[_pos]) : _source[_pos] is >= '0' and <= '7'))
            {
                _pos++;
            }

            if (_pos == digits)
            {
                throw Error("invalid number");
            }

            ulong value = hex
                ? ulong.Parse(_source.AsSpan(digits, _pos - digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : Convert.ToUInt64(_source[digits.._pos], 8);
            return new YaraToken(YaraTokenKind.Integer, _source[start.._pos], line, column) { Integer = unchecked((long)value) };
        }

        while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
        {
            _pos++;
        }

        if (_pos + 1 < _source.Length && _source[_pos] == '.' && char.IsAsciiDigit(_source[_pos + 1]))
        {
            _pos++;
            while (_pos < _source.Length && char.IsAsciiDigit(_source[_pos]))
            {
                _pos++;
            }

            return new YaraToken(YaraTokenKind.Float, _source[start.._pos], line, column)
            {
                Float = double.Parse(_source.AsSpan(start, _pos - start), CultureInfo.InvariantCulture),
            };
        }

        if (!long.TryParse(_source.AsSpan(start, _pos - start), NumberStyles.None, CultureInfo.InvariantCulture, out long n))
        {
            throw Error("integer is too large");
        }

        if (_pos + 1 < _source.Length && _source[_pos + 1] == 'B' && _source[_pos] is 'K' or 'M')
        {
            n *= _source[_pos] == 'K' ? 1024 : 1024 * 1024;
            _pos += 2;
        }

        return new YaraToken(YaraTokenKind.Integer, _source[start.._pos], line, column) { Integer = n };
    }

    /// <summary>"…" (エスケープ \" \\ \t \n \r \xHH)。中身はソースの UTF-8 のバイト。</summary>
    private byte[] ReadText()
    {
        _pos++;
        var bytes = new List<byte>();
        Span<byte> utf8 = stackalloc byte[4];
        while (true)
        {
            if (_pos >= _source.Length || _source[_pos] == '\n')
            {
                throw Error("unterminated string");
            }

            char c = _source[_pos++];
            if (c == '"')
            {
                return [.. bytes];
            }

            if (c == '\\')
            {
                if (_pos >= _source.Length)
                {
                    throw Error("unterminated string");
                }

                char e = _source[_pos++];
                switch (e)
                {
                    case '"': bytes.Add((byte)'"'); break;
                    case '\\': bytes.Add((byte)'\\'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 'x':
                        if (_pos + 2 > _source.Length || !char.IsAsciiHexDigit(_source[_pos]) || !char.IsAsciiHexDigit(_source[_pos + 1]))
                        {
                            throw Error("illegal escape sequence");
                        }

                        bytes.Add(byte.Parse(_source.AsSpan(_pos, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 2;
                        break;
                    default:
                        throw Error("illegal escape sequence");
                }

                continue;
            }

            if (char.IsHighSurrogate(c) && _pos < _source.Length)
            {
                int n = Encoding.UTF8.GetBytes([c, _source[_pos++]], utf8);
                bytes.AddRange(utf8[..n]);
            }
            else
            {
                int n = Encoding.UTF8.GetBytes([c], utf8);
                bytes.AddRange(utf8[..n]);
            }
        }
    }

    /// <summary>/…/is (中身はそのまま。エスケープの解釈は <see cref="YaraRegex"/>)。</summary>
    private YaraToken ReadRegex(int line, int column)
    {
        _pos++;
        var sb = new StringBuilder();
        bool inClass = false;
        while (true)
        {
            if (_pos >= _source.Length || _source[_pos] == '\n')
            {
                throw Error("unterminated regular expression");
            }

            char c = _source[_pos++];
            if (c == '\\' && _pos < _source.Length)
            {
                sb.Append(c).Append(_source[_pos++]);
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                break;
            }

            sb.Append(c);
        }

        int flagStart = _pos;
        while (_pos < _source.Length && _source[_pos] is 'i' or 's')
        {
            _pos++;
        }

        if (sb.Length == 0)
        {
            throw Error("empty regular expression");
        }

        return new YaraToken(YaraTokenKind.Regex, sb.ToString(), line, column) { Flags = _source[flagStart.._pos] };
    }

    /// <summary>16 進文字列 <c>{ … }</c> の中身を読む (開きの <c>{</c> は読んだ後)。閉じの <c>}</c> まで。</summary>
    public (string Body, int Line) ReadHexString()
    {
        int line = _line;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _source.Length)
            {
                throw Error("unterminated hex string");
            }

            char c = _source[_pos++];
            if (c == '}')
            {
                return (sb.ToString(), line);
            }

            if (c == '/' && _pos < _source.Length && _source[_pos] is '/' or '*')
            {
                _pos--;
                SkipSpace();
                continue;
            }

            if (c == '\n')
            {
                _line++;
                _lineStart = _pos;
            }

            sb.Append(c);
        }
    }
}
