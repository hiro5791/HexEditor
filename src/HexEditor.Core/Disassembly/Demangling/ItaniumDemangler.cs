using System.Globalization;

namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>
/// Itanium C++ ABI (GCC、Clang) のデマングラ (ANA-30)。名前 (入れ子・局所・テンプレート)、置換 (<c>S_</c>・<c>St</c> など)、
/// テンプレート引数とテンプレートの仮引数、組み込み型・修飾・ポインタ・参照・関数型・配列・メンバへのポインタ、コンストラクター・
/// デストラクター・演算子、特別な名前 (vtable など)、ラムダ・無名の型、ABI タグ、整数のリテラルと簡単な式を解釈する。
/// 出力は LLVM の形 (<c>char const*</c>、<c>void (*)(int)</c>)。
/// </summary>
internal sealed class ItaniumDemangler
{
    private readonly string _s;
    private readonly DemangleOptions _options;
    private readonly List<ItNode> _subs = [];
    private List<ItNode> _templateParams = [];
    private int _p;
    private int _depth;

    public ItaniumDemangler(string s, DemangleOptions options)
    {
        _s = s;
        _options = options;
    }

    public string? Run()
    {
        if (!_s.StartsWith("_Z", StringComparison.Ordinal))
        {
            return null;
        }

        _p = 2;
        ItNode encoding = Encoding();
        string suffix = string.Empty;
        if (_p < _s.Length && _s[_p] == '.')
        {
            // クローンの接尾辞 (.cold、.constprop.0 など)。
            suffix = " (" + _s[_p..] + ")";
            _p = _s.Length;
        }

        if (_p != _s.Length)
        {
            return null;
        }

        var o = new Out { Short = _options.ShortNames };
        encoding.Print(o);
        return o + suffix;
    }

    // ---- 読み取りの補助 ----

    private char Look(int ahead = 0) => _p + ahead < _s.Length ? _s[_p + ahead] : '\0';

    private bool Consume(char c)
    {
        if (Look() == c)
        {
            _p++;
            return true;
        }

        return false;
    }

    private bool Consume(string text)
    {
        if (string.CompareOrdinal(_s, _p, text, 0, text.Length) == 0)
        {
            _p += text.Length;
            return true;
        }

        return false;
    }

    private void Expect(char c)
    {
        if (!Consume(c))
        {
            throw new DemangleException();
        }
    }

    private void Enter()
    {
        if (++_depth > Demangler.MaxDepth)
        {
            throw new DemangleException();
        }
    }

    private void Leave() => _depth--;

    /// <summary>10 進の数 (<c>n</c> で負)。</summary>
    private string Number(bool allowNegative = true)
    {
        int start = _p;
        if (allowNegative)
        {
            Consume('n');
        }

        if (!char.IsAsciiDigit(Look()))
        {
            throw new DemangleException();
        }

        while (char.IsAsciiDigit(Look()))
        {
            _p++;
        }

        string text = _s[start.._p];
        return text.StartsWith('n') ? "-" + text[1..] : text;
    }

    private int PositiveNumber()
    {
        string text = Number(allowNegative: false);
        return text.Length > 9 ? throw new DemangleException() : int.Parse(text, CultureInfo.InvariantCulture);
    }

    // ---- エンコーディング ----

    private ItNode Encoding()
    {
        Enter();
        try
        {
            if (Look() is 'T' or 'G')
            {
                return SpecialName();
            }

            var state = new NameState();
            ItNode name = Name(state, tagTemplates: true);
            if (_p >= _s.Length || Look() is 'E' or '.')
            {
                return name;
            }

            ItNode? ret = state.EndsWithTemplateArgs && !state.CtorDtorConversion ? Type() : null;
            var parameters = new List<ItNode>();
            if (Look() == 'v' && (_p + 1 >= _s.Length || Look(1) is 'E' or '.'))
            {
                _p++;
            }
            else
            {
                do
                {
                    parameters.Add(Type());
                }
                while (_p < _s.Length && Look() is not ('E' or '.'));
            }

            return new ItEncoding(ret, name, parameters, state.Qualifiers);
        }
        finally
        {
            Leave();
        }
    }

    private ItNode SpecialName()
    {
        if (Consume("TV"))
        {
            return new ItSpecial("vtable for ", Type());
        }

        if (Consume("TT"))
        {
            return new ItSpecial("VTT for ", Type());
        }

        if (Consume("TI"))
        {
            return new ItSpecial("typeinfo for ", Type());
        }

        if (Consume("TS"))
        {
            return new ItSpecial("typeinfo name for ", Type());
        }

        if (Consume("Th"))
        {
            Number();
            Expect('_');
            return new ItSpecial("non-virtual thunk to ", Encoding());
        }

        if (Consume("Tv"))
        {
            Number();
            Expect('_');
            Number();
            Expect('_');
            return new ItSpecial("virtual thunk to ", Encoding());
        }

        if (Consume("Tc"))
        {
            CallOffset();
            CallOffset();
            return new ItSpecial("covariant return thunk to ", Encoding());
        }

        if (Consume("TW"))
        {
            return new ItSpecial("thread-local wrapper routine for ", Name(new NameState(), false));
        }

        if (Consume("TH"))
        {
            return new ItSpecial("thread-local initialization routine for ", Name(new NameState(), false));
        }

        if (Consume("GV"))
        {
            return new ItSpecial("guard variable for ", Name(new NameState(), false));
        }

        if (Consume("GR"))
        {
            ItNode name = Name(new NameState(), false);
            int index = 0;
            if (Look() != '_')
            {
                index = SeqId() + 1;
            }

            Consume('_');
            return new ItSpecial($"reference temporary #{index} for ", name);
        }

        throw new DemangleException();
    }

    private void CallOffset()
    {
        if (Consume('h'))
        {
            Number();
            Expect('_');
        }
        else if (Consume('v'))
        {
            Number();
            Expect('_');
            Number();
            Expect('_');
        }
        else
        {
            throw new DemangleException();
        }
    }

    // ---- 名前 ----

    private sealed class NameState
    {
        public bool EndsWithTemplateArgs;
        public bool CtorDtorConversion;
        public string Qualifiers = string.Empty;
    }

    private ItNode Name(NameState state, bool tagTemplates)
    {
        Enter();
        try
        {
            switch (Look())
            {
                case 'N':
                    return Nested(state, tagTemplates);
                case 'Z':
                    return Local(state);
            }

            ItNode name;
            if (Look() == 'S' && Look(1) != 't')
            {
                name = Substitution();
                if (Look() != 'I')
                {
                    throw new DemangleException();
                }
            }
            else
            {
                bool std = Consume("St");
                name = Unqualified(state);
                if (std)
                {
                    name = new ItNested(new ItName("std"), name);
                }

                if (Look() == 'I')
                {
                    _subs.Add(name);
                }
            }

            if (Look() == 'I')
            {
                ItTemplateArgs args = TemplateArgs(tagTemplates);
                state.EndsWithTemplateArgs = true;
                name = new ItWithTemplateArgs(name, args);
            }

            return name;
        }
        finally
        {
            Leave();
        }
    }

    private ItNode Local(NameState state)
    {
        Expect('Z');
        ItNode encoding = Encoding();
        Expect('E');
        if (Consume('s'))
        {
            Discriminator();
            return new ItNested(encoding, new ItName("string literal"));
        }

        if (Consume('d'))
        {
            if (Look() != '_')
            {
                Number();
            }

            Expect('_');
            ItNode inner = Name(state, false);
            return new ItNested(encoding, inner);
        }

        ItNode entity = Name(state, false);
        Discriminator();
        return new ItNested(encoding, entity);
    }

    private void Discriminator()
    {
        if (Look() == '_' && char.IsAsciiDigit(Look(1)))
        {
            _p += 2;
        }
        else if (Look() == '_' && Look(1) == '_')
        {
            _p += 2;
            Number(false);
            Expect('_');
        }
    }

    private ItNode Nested(NameState state, bool tagTemplates)
    {
        Expect('N');
        string qualifiers = CvQualifiers();
        if (Consume('R'))
        {
            qualifiers += " &";
        }
        else if (Consume('O'))
        {
            qualifiers += " &&";
        }

        state.Qualifiers = qualifiers;
        ItNode? soFar = null;
        while (!Consume('E'))
        {
            if (_p >= _s.Length)
            {
                throw new DemangleException();
            }

            state.EndsWithTemplateArgs = false;
            if (Look() == 'T')
            {
                soFar = Combine(soFar, TemplateParam());
            }
            else if (Look() == 'I')
            {
                if (soFar is null)
                {
                    throw new DemangleException();
                }

                ItTemplateArgs args = TemplateArgs(tagTemplates);
                soFar = new ItWithTemplateArgs(soFar, args);
                state.EndsWithTemplateArgs = true;
            }
            else if (Look() == 'D' && Look(1) is 't' or 'T')
            {
                soFar = Combine(soFar, Decltype());
            }
            else if (Look() == 'S' && Look(1) != 't')
            {
                if (soFar is not null)
                {
                    throw new DemangleException();
                }

                soFar = Substitution();
                continue;
            }
            else if (Look() == 'S' && Look(1) == 't')
            {
                _p += 2;
                soFar = Combine(soFar, new ItName("std"));
                continue;
            }
            else if (Look() == 'C' || Look() == 'D' && Look(1) != 'C')
            {
                if (soFar is null)
                {
                    throw new DemangleException();
                }

                soFar = Combine(soFar, CtorDtor(soFar));
                state.CtorDtorConversion = true;
                soFar = Tags(soFar);
            }
            else
            {
                soFar = Combine(soFar, Unqualified(state));
            }

            if (Look() != 'E')
            {
                _subs.Add(soFar);
            }
        }

        return soFar ?? throw new DemangleException();
    }

    private static ItNode Combine(ItNode? qualifier, ItNode name) => qualifier is null ? name : new ItNested(qualifier, name);

    private ItNode CtorDtor(ItNode soFar)
    {
        ItNode basis = soFar is ItNested n ? n.Name : soFar;
        if (Consume('C'))
        {
            bool inheriting = Consume('I');
            if (Look() is not ('1' or '2' or '3' or '4' or '5'))
            {
                throw new DemangleException();
            }

            _p++;
            if (inheriting)
            {
                Type();
            }

            return new ItCtorDtor(basis, false);
        }

        Expect('D');
        if (Look() is not ('0' or '1' or '2' or '4' or '5'))
        {
            throw new DemangleException();
        }

        _p++;
        return new ItCtorDtor(basis, true);
    }

    private string CvQualifiers()
    {
        string q = string.Empty;
        if (Consume('r'))
        {
            q += " restrict";
        }

        if (Consume('V'))
        {
            q += " volatile";
        }

        if (Consume('K'))
        {
            q = " const" + q;
        }

        return q;
    }

    private ItNode Unqualified(NameState state)
    {
        ItNode result;
        Consume('L');
        char c = Look();
        if (char.IsAsciiDigit(c))
        {
            result = SourceName();
        }
        else if (c == 'U')
        {
            result = UnnamedType();
        }
        else if (char.IsAsciiLetterLower(c))
        {
            result = OperatorName(state);
        }
        else
        {
            throw new DemangleException();
        }

        return Tags(result);
    }

    private ItNode Tags(ItNode result)
    {
        while (Look() == 'B')
        {
            _p++;
            result = new ItAbiTag(result, SourceIdentifier());
        }

        return result;
    }

    private string SourceIdentifier()
    {
        int length = PositiveNumber();
        if (length <= 0 || _p + length > _s.Length)
        {
            throw new DemangleException();
        }

        string id = _s.Substring(_p, length);
        _p += length;
        return id;
    }

    private ItNode SourceName()
    {
        string id = SourceIdentifier();
        return new ItName(id.StartsWith("_GLOBAL__N", StringComparison.Ordinal) ? "(anonymous namespace)" : id);
    }

    private ItNode UnnamedType()
    {
        if (Consume("Ut"))
        {
            int n = Look() == '_' ? 1 : PositiveNumber() + 2;
            Expect('_');
            return new ItName($"{{unnamed type#{n.ToString(CultureInfo.InvariantCulture)}}}");
        }

        if (Consume("Ul"))
        {
            var parameters = new List<ItNode>();
            if (Look() == 'v' && Look(1) == 'E')
            {
                _p++;
            }
            else
            {
                while (Look() != 'E')
                {
                    parameters.Add(Type());
                }
            }

            Expect('E');
            int n = Look() == '_' ? 1 : PositiveNumber() + 2;
            Expect('_');
            var o = new Out();
            ItParams.Print(o, parameters);
            return new ItName($"{{lambda{o}#{n.ToString(CultureInfo.InvariantCulture)}}}");
        }

        throw new DemangleException();
    }

    private static readonly Dictionary<string, (string Name, int Arity)> Operators = new(StringComparer.Ordinal)
    {
        ["nw"] = ("new", 0), ["na"] = ("new[]", 0), ["dl"] = ("delete", 1), ["da"] = ("delete[]", 1), ["ps"] = ("+", 1), ["ng"] = ("-", 1),
        ["ad"] = ("&", 1), ["de"] = ("*", 1), ["co"] = ("~", 1), ["pl"] = ("+", 2), ["mi"] = ("-", 2), ["ml"] = ("*", 2), ["dv"] = ("/", 2),
        ["rm"] = ("%", 2), ["an"] = ("&", 2), ["or"] = ("|", 2), ["eo"] = ("^", 2), ["aS"] = ("=", 2), ["pL"] = ("+=", 2), ["mI"] = ("-=", 2),
        ["mL"] = ("*=", 2), ["dV"] = ("/=", 2), ["rM"] = ("%=", 2), ["aN"] = ("&=", 2), ["oR"] = ("|=", 2), ["eO"] = ("^=", 2), ["ls"] = ("<<", 2),
        ["rs"] = (">>", 2), ["lS"] = ("<<=", 2), ["rS"] = (">>=", 2), ["eq"] = ("==", 2), ["ne"] = ("!=", 2), ["lt"] = ("<", 2), ["gt"] = (">", 2),
        ["le"] = ("<=", 2), ["ge"] = (">=", 2), ["ss"] = ("<=>", 2), ["nt"] = ("!", 1), ["aa"] = ("&&", 2), ["oo"] = ("||", 2), ["pp"] = ("++", 1),
        ["mm"] = ("--", 1), ["cm"] = (",", 2), ["pm"] = ("->*", 2), ["pt"] = ("->", 2), ["cl"] = ("()", 2), ["ix"] = ("[]", 2), ["qu"] = ("?", 3),
        ["aw"] = ("co_await", 1),
    };

    private ItNode OperatorName(NameState state)
    {
        if (Consume("cv"))
        {
            state.CtorDtorConversion = true;
            return new ItConversion(Type());
        }

        if (Consume("li"))
        {
            return new ItName("operator\"\" " + SourceIdentifier());
        }

        if (Look() == 'v' && char.IsAsciiDigit(Look(1)))
        {
            _p += 2;
            return new ItName("operator " + SourceIdentifier());
        }

        string code = _s.Substring(_p, Math.Min(2, _s.Length - _p));
        if (!Operators.TryGetValue(code, out (string Name, int Arity) op))
        {
            throw new DemangleException();
        }

        _p += 2;
        bool word = char.IsAsciiLetter(op.Name[0]);
        return new ItName("operator" + (word ? " " : string.Empty) + op.Name);
    }

    // ---- 置換とテンプレート ----

    private int SeqId()
    {
        int value = 0;
        int digits = 0;
        while (Look() is >= '0' and <= '9' or >= 'A' and <= 'Z')
        {
            char c = _s[_p++];
            value = checked(value * 36 + (c <= '9' ? c - '0' : c - 'A' + 10));
            if (++digits > 6)
            {
                throw new DemangleException();
            }
        }

        return digits == 0 ? throw new DemangleException() : value;
    }

    private ItNode Substitution()
    {
        Expect('S');
        string? special = Look() switch
        {
            'a' => "std::allocator",
            'b' => "std::basic_string",
            's' => "std::string",
            'i' => "std::istream",
            'o' => "std::ostream",
            'd' => "std::iostream",
            _ => null,
        };
        if (special is not null)
        {
            _p++;
            string basis = special switch
            {
                "std::string" => "basic_string",
                "std::istream" => "basic_istream",
                "std::ostream" => "basic_ostream",
                "std::iostream" => "basic_iostream",
                _ => special[5..],
            };
            return new ItNested(new ItName("std"), new BaseNamed(special[5..], basis));
        }

        int index = Look() == '_' ? 0 : SeqId() + 1;
        Expect('_');
        return index < _subs.Count ? _subs[index] : throw new DemangleException();
    }

    /// <summary>標準の置換 (<c>Ss</c> など) の名前 (コンストラクターの名前は basic_string など)。</summary>
    private sealed class BaseNamed(string text, string basis) : ItNode
    {
        public override string BaseName => basis;

        public override void Left(Out o) => o.Add(text);
    }

    private ItTemplateArgs TemplateArgs(bool tag)
    {
        Expect('I');
        var args = new List<ItNode>();
        List<ItNode> saved = _templateParams;
        while (!Consume('E'))
        {
            if (_p >= _s.Length)
            {
                throw new DemangleException();
            }

            args.Add(TemplateArg());
        }

        _templateParams = tag ? args : saved;
        return new ItTemplateArgs(args);
    }

    private ItNode TemplateArg()
    {
        Enter();
        try
        {
            switch (Look())
            {
                case 'X':
                {
                    _p++;
                    ItNode e = Expression();
                    Expect('E');
                    return e;
                }

                case 'L':
                    return ExprPrimary();
                case 'J':
                {
                    _p++;
                    var items = new List<ItNode>();
                    while (!Consume('E'))
                    {
                        if (_p >= _s.Length)
                        {
                            throw new DemangleException();
                        }

                        items.Add(TemplateArg());
                    }

                    return new ItPack(items);
                }

                default:
                    return Type();
            }
        }
        finally
        {
            Leave();
        }
    }

    private ItNode TemplateParam()
    {
        Expect('T');
        int index = Look() == '_' ? 0 : PositiveNumber() + 1;
        Expect('_');
        ItNode node = index < _templateParams.Count ? _templateParams[index] : new ItName(index == 0 ? "auto" : $"auto:{index + 1}");
        return node;
    }

    private ItNode Decltype()
    {
        Expect('D');
        if (Look() is not ('t' or 'T'))
        {
            throw new DemangleException();
        }

        _p++;
        ItNode e = Expression();
        Expect('E');
        return new ItPrefix("decltype", e);
    }

    // ---- 式 (テンプレート引数) ----

    private ItNode ExprPrimary()
    {
        Expect('L');
        if (Consume("_Z"))
        {
            ItNode encoding = Encoding();
            Expect('E');
            return encoding;
        }

        if (Consume("DnE"))
        {
            return new ItExpr("nullptr");
        }

        char t = Look();
        string suffix;
        string? cast = null;
        switch (t)
        {
            case 'b':
            {
                _p++;
                string v = Number();
                Expect('E');
                return new ItExpr(v == "0" ? "false" : "true");
            }

            case 'i': suffix = string.Empty; _p++; break;
            case 'j': suffix = "u"; _p++; break;
            case 'l': suffix = "l"; _p++; break;
            case 'm': suffix = "ul"; _p++; break;
            case 'x': suffix = "ll"; _p++; break;
            case 'y': suffix = "ull"; _p++; break;
            default:
            {
                cast = Type().ToText();
                suffix = string.Empty;
                break;
            }
        }

        string value = Number();
        Expect('E');
        return new ItExpr(cast is null ? value + suffix : "(" + cast + ")" + value);
    }

    private ItNode Expression()
    {
        Enter();
        try
        {
            switch (Look())
            {
                case 'L':
                    return ExprPrimary();
                case 'T':
                    return TemplateParam();
                case 'f' when Look(1) == 'p':
                {
                    _p += 2;
                    CvQualifiers();
                    string n = Look() == '_' ? string.Empty : (PositiveNumber() + 2).ToString(CultureInfo.InvariantCulture);
                    Expect('_');
                    return new ItExpr("fp" + n);
                }

                case 's' when Look(1) == 't':
                    _p += 2;
                    return new ItPrefix("sizeof ", Type());
                case 's' when Look(1) == 'z':
                    _p += 2;
                    return new ItPrefix("sizeof ", Expression());
                case 'a' when Look(1) == 't':
                    _p += 2;
                    return new ItPrefix("alignof ", Type());
                case 's' when Look(1) == 'r':
                {
                    _p += 2;
                    ItNode type = Type();
                    ItNode name = Unqualified(new NameState());
                    if (Look() == 'I')
                    {
                        name = new ItWithTemplateArgs(name, TemplateArgs(false));
                    }

                    return new ItNested(type, name);
                }
            }

            string code = _s.Substring(_p, Math.Min(2, _s.Length - _p));
            if (Operators.TryGetValue(code, out (string Name, int Arity) op) && op.Arity is 1 or 2)
            {
                _p += 2;
                ItNode first = Expression();
                return op.Arity == 1 ? new ItPrefix(op.Name, first) : new ItBinary(first, op.Name, Expression());
            }

            throw new DemangleException();
        }
        finally
        {
            Leave();
        }
    }

    // ---- 型 ----

    private static string? Builtin(char c) => c switch
    {
        'v' => "void",
        'w' => "wchar_t",
        'b' => "bool",
        'c' => "char",
        'a' => "signed char",
        'h' => "unsigned char",
        's' => "short",
        't' => "unsigned short",
        'i' => "int",
        'j' => "unsigned int",
        'l' => "long",
        'm' => "unsigned long",
        'x' => "long long",
        'y' => "unsigned long long",
        'n' => "__int128",
        'o' => "unsigned __int128",
        'f' => "float",
        'd' => "double",
        'e' => "long double",
        'g' => "__float128",
        'z' => "...",
        _ => null,
    };

    private static string? BuiltinD(char c) => c switch
    {
        'd' => "decimal64",
        'e' => "decimal128",
        'f' => "decimal32",
        'h' => "half",
        'i' => "char32_t",
        's' => "char16_t",
        'u' => "char8_t",
        'a' => "auto",
        'c' => "decltype(auto)",
        'n' => "std::nullptr_t",
        _ => null,
    };

    private ItNode Type()
    {
        Enter();
        try
        {
            char c = Look();
            if (Builtin(c) is { } builtin)
            {
                _p++;
                return new ItName(builtin);
            }

            ItNode result;
            switch (c)
            {
                case 'r' or 'V' or 'K':
                {
                    string q = CvQualifiers();
                    ItNode child = Type();
                    result = new ItQual(child, q);
                    break;
                }

                case 'P':
                    _p++;
                    result = new ItPointer(Type(), "*");
                    break;
                case 'R':
                    _p++;
                    result = new ItPointer(Type(), "&");
                    break;
                case 'O':
                    _p++;
                    result = new ItPointer(Type(), "&&");
                    break;
                case 'C':
                    _p++;
                    result = new ItSuffix(Type(), " _Complex");
                    break;
                case 'G':
                    _p++;
                    result = new ItSuffix(Type(), " _Imaginary");
                    break;
                case 'F':
                    result = FunctionType();
                    break;
                case 'A':
                    result = ArrayType();
                    break;
                case 'M':
                {
                    _p++;
                    ItNode cls = Type();
                    ItNode member = Type();
                    result = new ItPointerToMember(cls, member);
                    break;
                }

                case 'T':
                {
                    result = TemplateParam();
                    if (Look() == 'I')
                    {
                        _subs.Add(result);
                        result = new ItWithTemplateArgs(result, TemplateArgs(false));
                    }

                    break;
                }

                case 'S' when Look(1) != 't':
                {
                    ItNode sub = Substitution();
                    if (Look() != 'I')
                    {
                        return sub;
                    }

                    result = new ItWithTemplateArgs(sub, TemplateArgs(false));
                    break;
                }

                case 'D':
                {
                    char d = Look(1);
                    if (BuiltinD(d) is { } dn)
                    {
                        _p += 2;
                        return new ItName(dn);
                    }

                    if (d == 'p')
                    {
                        _p += 2;
                        result = new ItPackExpansion(Type());
                        break;
                    }

                    if (d is 't' or 'T')
                    {
                        result = Decltype();
                        break;
                    }

                    if (d == 'v')
                    {
                        _p += 2;
                        string dim = Number(false);
                        Expect('_');
                        result = new ItVector(Type(), dim);
                        break;
                    }

                    if (d == 'F' && char.IsAsciiDigit(Look(2)))
                    {
                        _p += 2;
                        string bits = Number(false);
                        Expect('_');
                        return new ItName("_Float" + bits);
                    }

                    throw new DemangleException();
                }

                case 'u':
                    _p++;
                    result = SourceName();
                    break;
                default:
                    result = Name(new NameState(), false);
                    break;
            }

            _subs.Add(result);
            return result;
        }
        finally
        {
            Leave();
        }
    }

    private sealed class ItSuffix(ItNode child, string suffix) : ItNode
    {
        public override void Left(Out o)
        {
            child.Print(o);
            o.Add(suffix);
        }
    }

    private ItNode FunctionType()
    {
        Expect('F');
        Consume('Y');
        ItNode ret = Type();
        var parameters = new List<ItNode>();
        string qualifiers = string.Empty;
        while (!Consume('E'))
        {
            if (_p >= _s.Length)
            {
                throw new DemangleException();
            }

            if (Look() == 'v' && Look(1) == 'E' && parameters.Count == 0)
            {
                _p++;
                continue;
            }

            if (Look() == 'R' && Look(1) == 'E')
            {
                _p++;
                qualifiers = " &";
                continue;
            }

            if (Look() == 'O' && Look(1) == 'E')
            {
                _p++;
                qualifiers = " &&";
                continue;
            }

            parameters.Add(Type());
        }

        return new ItFunctionType(ret, parameters, qualifiers);
    }

    private ItNode ArrayType()
    {
        Expect('A');
        string dimension = string.Empty;
        if (char.IsAsciiDigit(Look()))
        {
            dimension = Number(false);
        }
        else if (Look() != '_')
        {
            dimension = Expression().ToText();
        }

        Expect('_');
        return new ItArray(Type(), dimension);
    }
}
