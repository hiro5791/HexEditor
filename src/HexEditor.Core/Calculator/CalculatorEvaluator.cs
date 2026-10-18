using System.Globalization;
using System.Numerics;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Calculator;

/// <summary>電卓の式の中の名前・読み取り関数に値を与える (TOOL-22 の仕様 2)。</summary>
public interface ICalculatorContext : IExpressionContext
{
    /// <summary>アクティブなドキュメントがあるか。なければ、名前や読み取り関数を使うとエラーにする。</summary>
    bool HasDocument { get; }

    /// <summary>ドキュメントのエンディアン (VIEW-11)。<c>u32(x)</c> のようにエンディアンを省いた読み取り関数が使う。</summary>
    bool BigEndian { get; }
}

/// <summary>ドキュメントがないときの文脈 (名前と読み取り関数はエラー)。</summary>
public sealed class NoDocumentCalculatorContext : ICalculatorContext
{
    public static readonly NoDocumentCalculatorContext Instance = new();

    public bool HasDocument => false;

    public bool BigEndian => false;

    public long Cursor => 0;

    public long Length => 0;

    public long SelectionStart => 0;

    public long SelectionLength => 0;

    public int SectorSize => 512;

    public long? ClusterSize => null;

    public long? RecordLength => null;

    public long? Bookmark(string name) => null;

    public bool TryRead(long offset, Span<byte> destination) => false;
}

/// <summary>
/// 計算の条件。プログラマモード (TOOL-23) ではビット幅 (8 / 16 / 32 / 64) と符号を指定し、計算はそのビット幅で折り返す。
/// 通常の電卓は 64 bit・符号付き。
/// </summary>
public sealed record CalculatorOptions
{
    public int BitWidth { get; init; } = 64;

    public bool Signed { get; init; } = true;

    /// <summary>接頭辞のない数値の基数。電卓は 10 進。</summary>
    public DefaultRadix Radix { get; init; } = DefaultRadix.Decimal;

    /// <summary>直前の結果 (<c>ans</c>)。なければ 0。</summary>
    public CalcValue Answer { get; init; } = CalcValue.Zero;
}

/// <summary>
/// 計算の値。整数は符号付き / なしの解釈を済ませた 128 bit の整数 (範囲は <see cref="CalculatorOptions"/> のビット幅と符号)、
/// 小数を含む式は倍精度の浮動小数点数。
/// </summary>
public readonly record struct CalcValue(Int128 Integer, double Double, bool IsFloat)
{
    public static readonly CalcValue Zero = new(0, 0, false);

    public static CalcValue FromInteger(Int128 value) => new(value, 0, false);

    public static CalcValue FromDouble(double value) => new(0, value, true);

    /// <summary>浮動小数点数として見た値。</summary>
    public double AsDouble => IsFloat ? Double : (double)Integer;
}

/// <summary>計算の結果。<see cref="Overflow"/> は 64 bit (またはビット幅) に収まらず、2 の補数で折り返したとき。</summary>
public sealed record CalcResult(CalcValue Value, bool Overflow);

/// <summary>
/// 電卓の式の評価 (TOOL-22・TOOL-23)。書き方は 00-overview の 6 章の入力式と同じ (数値・単位・演算子・名前・読み取り関数は
/// <see cref="ExpressionEvaluator"/> を再利用する) で、電卓だけの追加がある: <c>ans</c>、<c>min</c>・<c>max</c>・<c>abs</c>・<c>align</c>、
/// <c>popcnt</c>・<c>clz</c>・<c>ctz</c>、<c>rol</c>・<c>ror</c>・<c>bswap</c>、比較 (結果は 1 / 0)、エンディアンを省いた読み取り関数 (<c>u32(x)</c>)、
/// 小数点を含む数値 (倍精度で計算する)。他の入力欄ではこれらを使えない (<see cref="ExpressionEvaluator"/> が誤りにする)。
/// </summary>
public sealed class CalculatorEvaluator
{
    private readonly string _text;
    private readonly ICalculatorContext _context;
    private readonly CalculatorOptions _options;
    private readonly Int128 _min;
    private readonly Int128 _max;
    private int _pos;
    private bool _overflow;

    private CalculatorEvaluator(string text, ICalculatorContext context, CalculatorOptions options)
    {
        _text = text;
        _context = context;
        _options = options;
        int w = options.BitWidth;
        _max = options.Signed ? (Int128.One << (w - 1)) - 1 : (Int128.One << w) - 1;
        _min = options.Signed ? -(Int128.One << (w - 1)) : 0;
    }

    /// <summary>評価する。誤りは <see cref="ExpressionException"/>。</summary>
    public static CalcResult Evaluate(string text, ICalculatorContext context, CalculatorOptions? options = null)
    {
        options ??= new CalculatorOptions();
        if (options.BitWidth is not (8 or 16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "bit width");
        }

        var evaluator = new CalculatorEvaluator(text, context, options);
        evaluator.SkipSpaces();
        if (evaluator.AtEnd)
        {
            throw new ExpressionException(ExpressionError.Empty, 0);
        }

        CalcValue value = evaluator.ParseBinary(0);
        evaluator.SkipSpaces();
        if (!evaluator.AtEnd)
        {
            throw new ExpressionException(ExpressionError.Syntax, evaluator._pos);
        }

        return new CalcResult(value, evaluator._overflow);
    }

    public static bool TryEvaluate(string text, ICalculatorContext context, out CalcResult? result, out ExpressionException? error,
        CalculatorOptions? options = null)
    {
        try
        {
            result = Evaluate(text, context, options);
            error = null;
            return true;
        }
        catch (ExpressionException ex)
        {
            result = null;
            error = ex;
            return false;
        }
    }

    // ---- 値の折り返し ----

    /// <summary>ビット幅と符号に収める。収まらなければ 2 の補数で折り返す。<paramref name="flag"/> なら桁あふれの印を付ける。</summary>
    private Int128 Wrap(Int128 value, bool flag)
    {
        if (value >= _min && value <= _max)
        {
            return value;
        }

        if (flag)
        {
            _overflow = true;
        }

        Int128 modulus = Int128.One << _options.BitWidth;
        Int128 r = ((value % modulus) + modulus) % modulus;
        if (_options.Signed && r > _max)
        {
            r -= modulus;
        }

        return r;
    }

    private CalcValue Int(Int128 value, bool flag = false) => CalcValue.FromInteger(Wrap(value, flag));

    /// <summary>ビット列 (符号なし、ビット幅に収まる) を、符号に従った値にする。</summary>
    private Int128 FromBits(ulong bits)
    {
        Int128 v = bits;
        return _options.Signed && v > _max ? v - (Int128.One << _options.BitWidth) : v;
    }

    private ulong ToBits(Int128 value) => (ulong)(value & ((Int128.One << _options.BitWidth) - 1));

    private static double Finite(double value, int at)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ExpressionException(ExpressionError.Overflow, at);
        }

        return value;
    }

    // ---- 二項演算子 ----

    private static readonly (string Op, int Precedence)[] Operators =
    [
        ("|", 1), ("^", 2), ("&", 3), ("==", 4), ("!=", 4), ("<=", 5), (">=", 5), ("<<", 6), (">>", 6), ("<", 5), (">", 5), ("+", 7), ("-", 7),
        ("*", 8), ("/", 8), ("%", 8),
    ];

    private CalcValue ParseBinary(int minPrecedence)
    {
        CalcValue left = ParseUnary();
        while (true)
        {
            SkipSpaces();
            (string Op, int Precedence)? match = null;
            foreach ((string op, int precedence) in Operators)
            {
                // "|" が "||" の一部、"<" が "<<" の一部のように長い演算子を先に調べているため、表の順で最初に合うものを取る。
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
            CalcValue right = ParseBinary(match.Value.Precedence + 1);
            left = Apply(match.Value.Op, left, right, at);
        }
    }

    private CalcValue Apply(string op, CalcValue a, CalcValue b, int at)
    {
        if (op is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            bool result;
            if (a.IsFloat || b.IsFloat)
            {
                double x = a.AsDouble, y = b.AsDouble;
                result = op switch { "==" => x == y, "!=" => x != y, "<" => x < y, "<=" => x <= y, ">" => x > y, _ => x >= y };
            }
            else
            {
                Int128 x = a.Integer, y = b.Integer;
                result = op switch { "==" => x == y, "!=" => x != y, "<" => x < y, "<=" => x <= y, ">" => x > y, _ => x >= y };
            }

            return CalcValue.FromInteger(result ? 1 : 0);
        }

        if (a.IsFloat || b.IsFloat)
        {
            double x = a.AsDouble, y = b.AsDouble;
            switch (op)
            {
                case "+":
                    return CalcValue.FromDouble(Finite(x + y, at));
                case "-":
                    return CalcValue.FromDouble(Finite(x - y, at));
                case "*":
                    return CalcValue.FromDouble(Finite(x * y, at));
                case "/":
                    return y == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : CalcValue.FromDouble(Finite(x / y, at));
                case "%":
                    return y == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : CalcValue.FromDouble(Finite(x % y, at));
                default:
                    throw new ExpressionException(ExpressionError.NotInteger, at, op);
            }
        }

        Int128 l = a.Integer, r = b.Integer;
        switch (op)
        {
            case "+":
                return Int(l + r, flag: true);
            case "-":
                return Int(l - r, flag: true);
            case "*":
                return Int(l * r, flag: true);
            case "/":
                return r == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : Int(l / r, flag: true);
            case "%":
                return r == 0 ? throw new ExpressionException(ExpressionError.DivideByZero, at) : Int(l % r);
            case "&":
                return Int(l & r);
            case "|":
                return Int(l | r);
            case "^":
                return Int(l ^ r);
            case "<<":
                // シフトの量が 64 以上の場合はエラー (仕様 4)。ビット幅以上のシフトは全ビットが押し出される。
                return r < 0 || r > 63 ? throw new ExpressionException(ExpressionError.Overflow, at) : Int(l << (int)r);
            case ">>":
                return r < 0 || r > 63 ? throw new ExpressionException(ExpressionError.Overflow, at) : Int(l >> (int)r);
            default:
                throw new ExpressionException(ExpressionError.Syntax, at);
        }
    }

    private CalcValue ParseUnary()
    {
        SkipSpaces();
        int at = _pos;
        switch (Peek())
        {
            case '-':
                _pos++;
                CalcValue v = ParseUnary();
                return v.IsFloat ? CalcValue.FromDouble(-v.Double) : Int(-v.Integer, flag: true);
            case '+':
                _pos++;
                return ParseUnary();
            case '~':
                _pos++;
                CalcValue n = ParseUnary();
                return n.IsFloat ? throw new ExpressionException(ExpressionError.NotInteger, at, "~") : Int(~n.Integer);
            default:
                return ParsePrimary();
        }
    }

    private CalcValue ParsePrimary()
    {
        SkipSpaces();
        int at = _pos;
        char c = Peek();
        if (c == '(')
        {
            _pos++;
            CalcValue value = ParseBinary(0);
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
            return ParseWord();
        }

        throw new ExpressionException(ExpressionError.Syntax, at);
    }

    // ---- 数値 ----

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

    private CalcValue ParseNumber()
    {
        int at = _pos;
        string token = ReadToken(allowDot: true);
        bool prefixed = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || token.StartsWith('$')
            || token.StartsWith("0b", StringComparison.OrdinalIgnoreCase) || token.StartsWith("0o", StringComparison.OrdinalIgnoreCase);
        if (!prefixed && token.Contains('.', StringComparison.Ordinal))
        {
            return ParseDecimalNumber(token, at);
        }

        try
        {
            // 小数点のない数値は入力式の評価器に任せる (接頭辞、末尾の h / d、桁区切りの _、単位 K/M/G/T)。
            return Int(ExpressionEvaluator.EvaluateWide(token, _context, _options.Radix), flag: false);
        }
        catch (ExpressionException ex)
        {
            throw new ExpressionException(ex.Error, at + ex.Position, ex.Detail);
        }
    }

    /// <summary>
    /// 小数点を含む数値。単位 K/M/G/T 付きで整数になるもの (<c>1.5G</c>) は整数、それ以外は倍精度の小数 (TOOL-22 の仕様 4)。
    /// 小数点は常に <c>.</c> (地域設定によらない)。
    /// </summary>
    private CalcValue ParseDecimalNumber(string token, int at)
    {
        string digits = token.Replace("_", string.Empty, StringComparison.Ordinal);
        long multiplier = 1;
        char last = char.ToUpperInvariant(digits[^1]);
        if (last is 'K' or 'M' or 'G' or 'T')
        {
            multiplier = last switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, _ => 1L << 40 };
            digits = digits[..^1];
        }

        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d) || digits.EndsWith('.')
            || digits.StartsWith('.'))
        {
            throw new ExpressionException(ExpressionError.InvalidNumber, at);
        }

        decimal scaled = d * multiplier;
        if (multiplier != 1 && scaled == decimal.Truncate(scaled))
        {
            return Int((Int128)scaled);
        }

        return CalcValue.FromDouble((double)scaled);
    }

    // ---- 名前と関数 ----

    private static readonly HashSet<string> DocumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cur", "start", "end", "sel.start", "sel.end", "sel.last", "sel.len", "sector", "cluster", "rec", "recno",
    };

    private CalcValue ParseWord()
    {
        int at = _pos;
        int start = _pos;
        while (!AtEnd && (char.IsAsciiLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '.'))
        {
            _pos++;
        }

        string word = _text[start.._pos];
        SkipSpaces();
        if (Peek() == '(')
        {
            return ParseFunction(word, at);
        }

        _pos = start + word.Length;
        if (word.Equals("ans", StringComparison.OrdinalIgnoreCase))
        {
            return _options.Answer.IsFloat ? _options.Answer : Int(_options.Answer.Integer);
        }

        if ((DocumentNames.Contains(word) || word.StartsWith("bm.", StringComparison.OrdinalIgnoreCase)) && !_context.HasDocument)
        {
            throw new ExpressionException(ExpressionError.NotAvailable, at, word);
        }

        try
        {
            // 名前の値は入力式の評価器と同じ (16 進の語 FF や FFh もここで読まれる)。
            return Int(ExpressionEvaluator.EvaluateWide(word, _context, _options.Radix));
        }
        catch (ExpressionException ex)
        {
            throw new ExpressionException(ex.Error, at + ex.Position, ex.Detail);
        }
    }

    private CalcValue ParseFunction(string name, int at)
    {
        _pos++; // '('
        var args = new List<CalcValue>();
        SkipSpaces();
        if (Peek() == ')')
        {
            _pos++;
        }
        else
        {
            while (true)
            {
                args.Add(ParseBinary(0));
                SkipSpaces();
                if (Peek() == ',')
                {
                    _pos++;
                    continue;
                }

                if (Peek() != ')')
                {
                    throw new ExpressionException(ExpressionError.UnclosedParenthesis, at);
                }

                _pos++;
                break;
            }
        }

        string lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "min":
                Expect(args, 2, at, name);
                return args[0].AsDouble <= args[1].AsDouble ? args[0] : args[1];
            case "max":
                Expect(args, 2, at, name);
                return args[0].AsDouble >= args[1].AsDouble ? args[0] : args[1];
            case "abs":
                Expect(args, 1, at, name);
                return args[0].IsFloat ? CalcValue.FromDouble(Math.Abs(args[0].Double)) : Int(Int128.Abs(args[0].Integer), flag: true);
            case "align":
                {
                    Expect(args, 2, at, name);
                    Int128 value = IntegerArg(args[0], at, name);
                    Int128 n = IntegerArg(args[1], at, name);
                    if (n < 1)
                    {
                        throw new ExpressionException(ExpressionError.InvalidNumber, at, name);
                    }

                    Int128 rem = value % n;
                    if (rem < 0)
                    {
                        rem += n;
                    }

                    return Int(rem == 0 ? value : value + (n - rem), flag: true);
                }

            case "popcnt":
                Expect(args, 1, at, name);
                return CalcValue.FromInteger(BitOperations.PopCount(ToBits(IntegerArg(args[0], at, name))));
            case "clz":
                Expect(args, 1, at, name);
                return CalcValue.FromInteger(BitOperations.LeadingZeroCount(ToBits(IntegerArg(args[0], at, name))) - (64 - _options.BitWidth));
            case "ctz":
                {
                    Expect(args, 1, at, name);
                    ulong bits = ToBits(IntegerArg(args[0], at, name));
                    return CalcValue.FromInteger(bits == 0 ? _options.BitWidth : BitOperations.TrailingZeroCount(bits));
                }

            case "rol" or "ror":
                {
                    Expect(args, 2, at, name);
                    ulong bits = ToBits(IntegerArg(args[0], at, name));
                    int w = _options.BitWidth;
                    int n = (int)(((long)IntegerArg(args[1], at, name) % w + w) % w);
                    int shift = lower == "rol" ? n : (w - n) % w;
                    ulong mask = w == 64 ? ulong.MaxValue : (1UL << w) - 1;
                    ulong rotated = shift == 0 ? bits : ((bits << shift) | (bits >> (w - shift))) & mask;
                    return CalcValue.FromInteger(FromBits(rotated));
                }

            case "bswap":
                {
                    Expect(args, 1, at, name);
                    ulong bits = ToBits(IntegerArg(args[0], at, name));
                    int w = _options.BitWidth;
                    ulong swapped = w switch
                    {
                        8 => bits,
                        16 => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((ushort)bits),
                        32 => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)bits),
                        _ => System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(bits),
                    };
                    return CalcValue.FromInteger(FromBits(swapped));
                }
        }

        // 読み取り関数: エンディアンを省いたもの (u16〜u64、s16〜s64) はドキュメントのエンディアンで読む。
        string readName = lower;
        if (lower.Length >= 3 && lower[0] is 'u' or 's' && lower[1..].All(char.IsAsciiDigit) && lower is not ("u8" or "s8"))
        {
            readName = lower + (_context.BigEndian ? "be" : "le");
        }

        (int Size, bool Signed, bool BigEndian, bool IsFloat) read;
        try
        {
            read = ExpressionEvaluator.ParseFunctionName(readName, at, name);
        }
        catch (ExpressionException)
        {
            // 電卓では使えない関数名 (u8 / s8 / u16le などでもない) は、名前が分からないという誤りにする。
            throw new ExpressionException(ExpressionError.UnknownFunction, at, name);
        }

        Expect(args, 1, at, name);
        if (!_context.HasDocument)
        {
            throw new ExpressionException(ExpressionError.NotAvailable, at, name);
        }

        Int128 offset = IntegerArg(args[0], at, name);
        Span<byte> bytes = stackalloc byte[8];
        Span<byte> target = bytes[..read.Size];
        if (offset < 0 || offset > long.MaxValue || !_context.TryRead((long)offset, target))
        {
            throw new ExpressionException(ExpressionError.Unreadable, at, name);
        }

        if (read.BigEndian)
        {
            target.Reverse();
        }

        if (read.IsFloat)
        {
            return CalcValue.FromDouble(Finite(read.Size == 4 ? BitConverter.ToSingle(target) : BitConverter.ToDouble(target), at));
        }

        ulong raw = 0;
        for (int i = read.Size - 1; i >= 0; i--)
        {
            raw = (raw << 8) | target[i];
        }

        Int128 result = raw;
        if (read.Signed && read.Size < 8 && (raw & (1UL << (read.Size * 8 - 1))) != 0)
        {
            result = (long)(raw | (ulong.MaxValue << (read.Size * 8)));
        }
        else if (read.Signed && read.Size == 8)
        {
            result = (long)raw;
        }

        return Int(result);
    }

    private static void Expect(List<CalcValue> args, int count, int at, string name)
    {
        if (args.Count != count)
        {
            throw new ExpressionException(ExpressionError.Syntax, at, name);
        }
    }

    private static Int128 IntegerArg(CalcValue value, int at, string name) =>
        value.IsFloat ? throw new ExpressionException(ExpressionError.NotInteger, at, name) : value.Integer;

    // ---- 文字の読み取り ----

    private bool AtEnd => _pos >= _text.Length;

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

    private void SkipSpaces()
    {
        while (!AtEnd && char.IsWhiteSpace(_text[_pos]))
        {
            _pos++;
        }
    }
}
