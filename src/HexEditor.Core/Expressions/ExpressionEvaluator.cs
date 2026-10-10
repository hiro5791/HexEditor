using System.Buffers.Binary;
using System.Globalization;

namespace HexEditor.Core.Expressions;

/// <summary>入力式の誤りの種類。UI はこれを見て説明文を出す (00-overview 6 章)。</summary>
public enum ExpressionError
{
    Empty,
    Syntax,
    UnclosedParenthesis,
    UnknownName,
    UnknownBookmark,
    UnknownFunction,
    DivideByZero,
    Overflow,
    InvalidNumber,
    Unreadable,
    NotAvailable,
}

public sealed class ExpressionException(ExpressionError error, int position, string detail = "")
    : Exception($"{error} at {position}: {detail}")
{
    public ExpressionError Error { get; } = error;

    /// <summary>誤りのある位置 (入力の文字位置)。</summary>
    public int Position { get; } = position;

    /// <summary>名前・関数名など、説明文に入れる語。</summary>
    public string Detail { get; } = detail;
}

/// <summary>入力式の中の名前と読み取り関数に値を与える (00-overview 6.2)。</summary>
public interface IExpressionContext
{
    long Cursor { get; }

    long Length { get; }

    long SelectionStart { get; }

    long SelectionLength { get; }

    int SectorSize { get; }

    /// <summary>クラスタサイズ。ファイルシステムを解析していなければ null。</summary>
    long? ClusterSize { get; }

    /// <summary>レコード長 (VIEW-18)。レコード表示がオフのときは最後に設定した値。使えなければ null。</summary>
    long? RecordLength { get; }

    /// <summary>最初のレコードの開始オフセット (VIEW-18 の仕様 8。<c>recno</c> の計算に使う)。</summary>
    long RecordStart => 0;

    /// <summary>名前付きブックマークの位置。なければ null。</summary>
    long? Bookmark(string name);

    /// <summary><paramref name="offset"/> から <paramref name="destination"/> の長さ分を読む。読めなければ false。</summary>
    bool TryRead(long offset, Span<byte> destination);
}

/// <summary>接頭辞のない数値をどの基数で読むか (00-overview 6.1 の※)。</summary>
public enum DefaultRadix
{
    Hexadecimal,
    Decimal,
}

/// <summary>
/// 入力式 (00-overview 6 章) を評価する。数値の書き方、単位 K/M/G/T、演算子、名前、読み取り関数を受け付ける。
/// 計算は 64 bit の符号付き整数で行い、桁あふれは誤りにする (<see cref="EvaluateWide"/> は 128 bit)。
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly string _text;
    private readonly IExpressionContext _context;
    private readonly DefaultRadix _radix;

    /// <summary>128 bit で計算する (<see cref="EvaluateWide"/>)。false なら途中の値もすべて 64 bit の符号付き整数に収める。</summary>
    private readonly bool _wide;
    private int _pos;

    private ExpressionEvaluator(string text, IExpressionContext context, DefaultRadix radix, bool wide)
    {
        _text = text;
        _context = context;
        _radix = radix;
        _wide = wide;
    }

    public static long Evaluate(string text, IExpressionContext context, DefaultRadix radix = DefaultRadix.Hexadecimal) =>
        (long)new ExpressionEvaluator(text, context, radix, wide: false).Run();

    /// <summary>
    /// 128 bit の符号付き整数で評価する。数値は符号なし 64 bit の最大値 (0xFFFFFFFFFFFFFFFF) まで書け、u64le() などの読み取り関数も
    /// 2^63 以上の値を返せる (データ演算のオペランド。EDIT-31 の仕様 5)。結果が要素の型に収まるかは呼び出し側で調べる。
    /// </summary>
    public static Int128 EvaluateWide(string text, IExpressionContext context, DefaultRadix radix = DefaultRadix.Hexadecimal) =>
        new ExpressionEvaluator(text, context, radix, wide: true).Run();

    /// <summary><see cref="EvaluateWide"/> で評価できれば値を返し、できなければ誤りを返す。</summary>
    public static bool TryEvaluateWide(string text, IExpressionContext context, out Int128 value, out ExpressionException? error,
        DefaultRadix radix = DefaultRadix.Hexadecimal)
    {
        try
        {
            value = EvaluateWide(text, context, radix);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            value = 0;
            error = ex;
            return false;
        }
    }

    private Int128 Run()
    {
        SkipSpaces();
        if (AtEnd)
        {
            throw new ExpressionException(ExpressionError.Empty, 0);
        }

        Int128 value = ParseBinary(0);
        SkipSpaces();
        if (!AtEnd)
        {
            throw new ExpressionException(ExpressionError.Syntax, _pos);
        }

        return value;
    }

    /// <summary>評価できれば値を返し、できなければ誤りを返す。</summary>
    public static bool TryEvaluate(string text, IExpressionContext context, out long value, out ExpressionException? error,
        DefaultRadix radix = DefaultRadix.Hexadecimal)
    {
        try
        {
            value = Evaluate(text, context, radix);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            value = 0;
            error = ex;
            return false;
        }
    }

    private bool AtEnd => _pos >= _text.Length;

    private char Peek(int ahead = 0) => _pos + ahead < _text.Length ? _text[_pos + ahead] : '\0';

    private void SkipSpaces()
    {
        while (!AtEnd && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }

    // ---- 二項演算子 (優先順位の低い順) ----

    private static readonly (string Op, int Precedence)[] Operators =
    [
        ("|", 1), ("^", 2), ("&", 3), ("<<", 4), (">>", 4), ("+", 5), ("-", 5), ("*", 6), ("/", 6), ("%", 6),
    ];

    private Int128 ParseBinary(int minPrecedence)
    {
        Int128 left = ParseUnary();
        while (true)
        {
            SkipSpaces();
            (string Op, int Precedence)? match = null;
            foreach ((string op, int precedence) in Operators)
            {
                if (string.CompareOrdinal(_text, _pos, op, 0, op.Length) == 0 && precedence >= minPrecedence)
                {
                    match = (op, precedence);
                    break;
                }
            }

            if (match is null)
            {
                return left;
            }

            int at = _pos;
            _pos += match.Value.Op.Length;
            Int128 right = ParseBinary(match.Value.Precedence + 1);
            left = Apply(match.Value.Op, left, right, at);
        }
    }

    private Int128 Apply(string op, Int128 a, Int128 b, int at) => _wide ? ApplyWide(op, a, b, at) : ApplyLong(op, (long)a, (long)b, at);

    /// <summary>128 bit の計算 (桁あふれは誤り)。</summary>
    private static Int128 ApplyWide(string op, Int128 a, Int128 b, int at)
    {
        try
        {
            return op switch
            {
                "+" => checked(a + b),
                "-" => checked(a - b),
                "*" => checked(a * b),
                "/" => b == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : a / b,
                "%" => b == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : a % b,
                "&" => a & b,
                "|" => a | b,
                "^" => a ^ b,
                "<<" => b < 0 || b > 127 || ((a << (int)b) >> (int)b) != a
                    ? throw new ExpressionException(ExpressionError.Overflow, at) : a << (int)b,
                ">>" => b < 0 || b > 127 ? throw new ExpressionException(ExpressionError.Overflow, at) : a >> (int)b,
                _ => throw new ExpressionException(ExpressionError.Syntax, at),
            };
        }
        catch (OverflowException)
        {
            throw new ExpressionException(ExpressionError.Overflow, at);
        }
    }

    private static long ApplyLong(string op, long a, long b, int at)
    {
        try
        {
            return op switch
            {
                "+" => checked(a + b),
                "-" => checked(a - b),
                "*" => checked(a * b),
                "/" => b == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : a / b,
                "%" => b == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : a % b,
                "&" => a & b,
                "|" => a | b,
                "^" => a ^ b,
                "<<" => b is < 0 or > 63 ? throw new ExpressionException(ExpressionError.Overflow, at) : a << (int)b,
                ">>" => b is < 0 or > 63 ? throw new ExpressionException(ExpressionError.Overflow, at) : a >> (int)b,
                _ => throw new ExpressionException(ExpressionError.Syntax, at),
            };
        }
        catch (OverflowException)
        {
            throw new ExpressionException(ExpressionError.Overflow, at);
        }
    }

    private Int128 ParseUnary()
    {
        SkipSpaces();
        int at = _pos;
        switch (Peek())
        {
            case '-':
                _pos++;
                Int128 v = ParseUnary();
                return v == (_wide ? Int128.MinValue : long.MinValue) ? throw new ExpressionException(ExpressionError.Overflow, at) : -v;
            case '+':
                _pos++;
                return ParseUnary();
            case '~':
                _pos++;
                return ~ParseUnary();
            default:
                return ParsePrimary();
        }
    }

    private Int128 ParsePrimary()
    {
        SkipSpaces();
        int at = _pos;
        char c = Peek();
        if (c == '(')
        {
            _pos++;
            Int128 value = ParseBinary(0);
            SkipSpaces();
            if (Peek() != ')')
            {
                throw new ExpressionException(ExpressionError.UnclosedParenthesis, at);
            }

            _pos++;
            return value;
        }

        if (c == '$' || char.IsAsciiDigit(c))
        {
            return ParseNumber();
        }

        if (char.IsAsciiLetter(c) || c == '_')
        {
            // 16 進の英字で始まり h で終わる数値 (例: FFh) は名前より先に数値として読む。
            int save = _pos;
            if (TryParseHexWithSuffix(out Int128 hex))
            {
                return hex;
            }

            // 既定が 16 進のときは、16 進の数字だけの語 (例: FF) を数値として読む。名前 (cur、end など) は
            // 16 進の数字だけではできていないため区別できる。
            _pos = save;
            string word = ReadToken(allowDot: false);
            if (_radix == DefaultRadix.Hexadecimal && word.Replace("_", string.Empty).All(char.IsAsciiHexDigit) && Peek() != '(' && Peek() != '.')
            {
                return ParseInteger(word.Replace("_", string.Empty), save);
            }

            _pos = save;
            return ParseName();
        }

        throw new ExpressionException(ExpressionError.Syntax, at);
    }

    // ---- 数値 (00-overview 6.1) ----

    private Int128 ParseNumber()
    {
        int at = _pos;
        string token = ReadToken(allowDot: true);
        if (token.Length == 0)
        {
            throw new ExpressionException(ExpressionError.InvalidNumber, at);
        }

        // 単位 K/M/G/T (1024 の累乗)。小数も受け付ける (例: 1.5G)。K・M・G・T は 16 進の数字ではないので、接頭辞の付いた数値
        // (0x10G、$10G、0b1K、0o7K) の後ろにも付けられる。接頭辞のない数値は、既定の基数の設定によらず常に 10 進とする
        // (00-overview 6.1 の注。10G、1.5G)。
        long multiplier = 1;
        bool decimalDigits = false;
        char last = char.ToUpperInvariant(token[^1]);
        if (last is 'K' or 'M' or 'G' or 'T')
        {
            multiplier = last switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, _ => 1L << 40 };
            token = token[..^1];
            bool prefixed = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || token.StartsWith('$')
                || token.StartsWith("0b", StringComparison.OrdinalIgnoreCase) || token.StartsWith("0o", StringComparison.OrdinalIgnoreCase);
            decimalDigits = !prefixed;
            if (token.Contains('.'))
            {
                if (prefixed || !decimal.TryParse(token.Replace("_", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d))
                {
                    throw new ExpressionException(ExpressionError.InvalidNumber, at);
                }

                decimal scaled = d * multiplier;
                return scaled > MaxLiteral ? throw new ExpressionException(ExpressionError.Overflow, at) : (Int128)decimal.Floor(scaled);
            }
        }

        Int128 value = decimalDigits
            ? ParseInteger(token.Replace("_", string.Empty), at, forceRadix: 10)
            : ParseInteger(token.Replace("_", string.Empty), at);

        // 値は符号なし 64 bit 以下、単位は 2^40 以下なので、積は 128 bit であふれない。
        Int128 scaledValue = value * multiplier;
        return scaledValue > MaxLiteral ? throw new ExpressionException(ExpressionError.Overflow, at) : scaledValue;
    }

    /// <summary>数値として書ける最大値 (64 bit の計算では符号付きの最大値、128 bit の計算では符号なし 64 bit の最大値)。</summary>
    private ulong MaxLiteral => _wide ? ulong.MaxValue : long.MaxValue;

    private Int128 ParseInteger(string token, int at, int? forceRadix = null)
    {
        (string digits, int radix) = forceRadix is { } forced ? (token, forced) : token switch
        {
            _ when token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) => (token[2..], 16),
            _ when token.StartsWith('$') => (token[1..], 16),
            _ when token.StartsWith("0b", StringComparison.OrdinalIgnoreCase) => (token[2..], 2),
            _ when token.StartsWith("0o", StringComparison.OrdinalIgnoreCase) => (token[2..], 8),
            _ when token.EndsWith('h') || token.EndsWith('H') => (token[..^1], 16),
            _ when (token.EndsWith('d') || token.EndsWith('D')) && token[..^1].All(char.IsAsciiDigit) => (token[..^1], 10),
            _ => (token, _radix == DefaultRadix.Hexadecimal ? 16 : 10),
        };

        if (digits.Length == 0)
        {
            throw new ExpressionException(ExpressionError.InvalidNumber, at);
        }

        ulong result = 0;
        foreach (char ch in digits)
        {
            int d = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetter(ch) ? char.ToUpperInvariant(ch) - 'A' + 10 : 99;
            if (d >= radix)
            {
                throw new ExpressionException(ExpressionError.InvalidNumber, at);
            }

            if (result > (ulong.MaxValue - (ulong)d) / (ulong)radix)
            {
                throw new ExpressionException(ExpressionError.Overflow, at);
            }

            result = result * (ulong)radix + (ulong)d;
        }

        return result > MaxLiteral ? throw new ExpressionException(ExpressionError.Overflow, at) : result;
    }

    private bool TryParseHexWithSuffix(out Int128 value)
    {
        value = 0;
        int at = _pos;
        string token = ReadToken(allowDot: false);
        if (token.Length < 2 || !(token.EndsWith('h') || token.EndsWith('H')) || !token[..^1].Replace("_", string.Empty).All(char.IsAsciiHexDigit))
        {
            return false;
        }

        value = ParseInteger(token.Replace("_", string.Empty), at);
        return true;
    }

    private string ReadToken(bool allowDot)
    {
        int start = _pos;
        if (Peek() == '$')
        {
            _pos++;
        }

        while (!AtEnd && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] == '_' || (allowDot && _text[_pos] == '.')))
        {
            _pos++;
        }

        return _text[start.._pos];
    }

    // ---- 名前と読み取り関数 (00-overview 6.2) ----

    private Int128 ParseName()
    {
        int at = _pos;
        int start = _pos;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '.'))
        {
            _pos++;
        }

        string name = _text[start.._pos];
        SkipSpaces();
        if (Peek() == '(')
        {
            return ParseFunction(name, at);
        }

        string lower = name.ToLowerInvariant();
        if (lower.StartsWith("bm.", StringComparison.Ordinal))
        {
            string bookmark = name[3..];
            return _context.Bookmark(bookmark) ?? throw new ExpressionException(ExpressionError.UnknownBookmark, at, bookmark);
        }

        return lower switch
        {
            "cur" => _context.Cursor,
            "start" => 0,
            "end" => _context.Length,
            "sel.start" => _context.SelectionStart,
            "sel.end" => _context.SelectionStart + _context.SelectionLength,
            "sel.last" => _context.SelectionLength > 0 ? _context.SelectionStart + _context.SelectionLength - 1 : _context.SelectionStart,
            "sel.len" => _context.SelectionLength,
            "sector" => _context.SectorSize,
            "cluster" => _context.ClusterSize ?? throw new ExpressionException(ExpressionError.NotAvailable, at, name),
            "rec" => _context.RecordLength ?? throw new ExpressionException(ExpressionError.NotAvailable, at, name),
            // レコード番号は開始オフセットから数える。開始オフセットより前は入力エラー (VIEW-18 の仕様 8)。
            "recno" => _context.RecordLength is long r && r > 0 && _context.Cursor >= _context.RecordStart
                ? (_context.Cursor - _context.RecordStart) / r
                : throw new ExpressionException(ExpressionError.NotAvailable, at, name),
            _ => throw new ExpressionException(ExpressionError.UnknownName, at, name),
        };
    }

    private Int128 ParseFunction(string name, int at)
    {
        _pos++; // '('
        Int128 argument = ParseBinary(0);
        SkipSpaces();
        if (Peek() != ')')
        {
            throw new ExpressionException(ExpressionError.UnclosedParenthesis, at);
        }

        _pos++;
        (int size, bool signed, bool bigEndian, bool isFloat) = ParseFunctionName(name.ToLowerInvariant(), at, name);
        Span<byte> bytes = stackalloc byte[8];
        Span<byte> target = bytes[..size];
        if (argument < 0 || argument > long.MaxValue || !_context.TryRead((long)argument, target))
        {
            throw new ExpressionException(ExpressionError.Unreadable, at, name);
        }

        if (bigEndian)
        {
            target.Reverse();
        }

        if (isFloat)
        {
            double d = size == 4 ? BitConverter.ToSingle(target) : BitConverter.ToDouble(target);
            if (double.IsNaN(d) || d >= long.MaxValue || d <= long.MinValue)
            {
                throw new ExpressionException(ExpressionError.Overflow, at, name);
            }

            return (long)d;
        }

        ulong raw = 0;
        for (int i = size - 1; i >= 0; i--)
        {
            raw = (raw << 8) | target[i];
        }

        if (signed && size < 8 && (raw & (1UL << (size * 8 - 1))) != 0)
        {
            raw |= ulong.MaxValue << (size * 8);
        }

        if (!signed && size == 8 && raw > long.MaxValue)
        {
            if (_wide)
            {
                return raw;
            }

            throw new ExpressionException(ExpressionError.Overflow, at, name);
        }

        return (long)raw;
    }

    /// <summary>u8 / s8、u16le / s32be / f64le などの関数名を解釈する。</summary>
    private static (int Size, bool Signed, bool BigEndian, bool IsFloat) ParseFunctionName(string name, int at, string original)
    {
        if (name is "u8" or "s8")
        {
            return (1, name[0] == 's', false, false);
        }

        if (name.Length >= 5 && name[0] is 'u' or 's' or 'f' && (name.EndsWith("le", StringComparison.Ordinal) || name.EndsWith("be", StringComparison.Ordinal)))
        {
            string bits = name[1..^2];
            bool bigEndian = name.EndsWith("be", StringComparison.Ordinal);
            if (name[0] == 'f' && bits is "32" or "64")
            {
                return (bits == "32" ? 4 : 8, true, bigEndian, true);
            }

            if (name[0] != 'f' && bits is "16" or "24" or "32" or "64")
            {
                return (int.Parse(bits, CultureInfo.InvariantCulture) / 8, name[0] == 's', bigEndian, false);
            }
        }

        throw new ExpressionException(ExpressionError.UnknownFunction, at, original);
    }
}
