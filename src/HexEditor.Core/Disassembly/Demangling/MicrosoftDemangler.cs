using System.Globalization;
using System.Text;

namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>
/// Microsoft Visual C++ のデマングラ (ANA-30)。修飾名 (名前の後方参照・テンプレート・演算子・コンストラクター・デストラクター・
/// 無名の名前空間)、関数 (アクセス・static / virtual・this の修飾・呼び出し規約・戻り値・引数と型の後方参照)、データ、vftable を解釈する。
/// 出力は LLVM の llvm-undname の形 (<c>public: void __cdecl foo::bar(int)</c>、<c>char const *</c>)。<c>__ptr64</c> は表示しない。
/// </summary>
internal sealed class MicrosoftDemangler
{
    private readonly string _s;
    private readonly DemangleOptions _options;
    private int _p;
    private int _depth;
    private List<string> _names = [];
    private List<string> _types = [];

    public MicrosoftDemangler(string s, DemangleOptions options)
    {
        _s = s;
        _options = options;
    }

    private char Look(int ahead = 0) => _p + ahead < _s.Length ? _s[_p + ahead] : '\0';

    private char Next() => _p < _s.Length ? _s[_p++] : throw new DemangleException();

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

    private enum Special
    {
        None,
        Constructor,
        Destructor,
        Conversion,
        VfTable,
        VbTable,
    }

    public string? Run()
    {
        Expect('?');
        (string name, Special special) = QualifiedName(true);
        string result = Encoding(name, special);
        return _p == _s.Length ? result : null;
    }

    // ---- 名前 ----

    private (string Name, Special Special) QualifiedName(bool first)
    {
        Enter();
        try
        {
            Special special = Special.None;
            string unqualified;
            if (first && Look() == '?' && Look(1) != '$')
            {
                _p++;
                (unqualified, special) = OperatorName();
            }
            else
            {
                unqualified = Fragment();
            }

            var scopes = new List<string>();
            while (!Consume('@'))
            {
                if (_p >= _s.Length)
                {
                    throw new DemangleException();
                }

                scopes.Add(Fragment());
            }

            scopes.Reverse();
            string scope = string.Join("::", scopes);
            if (special is Special.Constructor or Special.Destructor)
            {
                string cls = scopes.Count > 0 ? scopes[^1] : throw new DemangleException();
                int lt = cls.IndexOf('<', StringComparison.Ordinal);
                unqualified = (special == Special.Destructor ? "~" : string.Empty) + (lt > 0 ? cls[..lt] : cls);
            }
            else if (special is Special.VfTable)
            {
                unqualified = "`vftable'";
            }
            else if (special is Special.VbTable)
            {
                unqualified = "`vbtable'";
            }

            return (scope.Length == 0 ? unqualified : scope + "::" + unqualified, special);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>名前の断片 1 つ (単純な名前・後方参照・テンプレート・無名の名前空間)。</summary>
    private string Fragment()
    {
        char c = Look();
        if (char.IsAsciiDigit(c))
        {
            _p++;
            int index = c - '0';
            return index < _names.Count ? _names[index] : throw new DemangleException();
        }

        if (c == '?' && Look(1) == '$')
        {
            _p += 2;
            string template = TemplateName();
            Memorize(template);
            return template;
        }

        if (c == '?' && Look(1) == 'A' && Look(2) == '0' && Look(3) == 'x')
        {
            int end = _s.IndexOf('@', _p);
            if (end < 0)
            {
                throw new DemangleException();
            }

            _p = end + 1;
            const string anonymous = "`anonymous namespace'";
            Memorize(anonymous);
            return anonymous;
        }

        if (c == '?')
        {
            // 局所の名前 (?1??func@@...) は解釈しない。
            throw new DemangleException();
        }

        string name = SimpleName();
        Memorize(name);
        return name;
    }

    private void Memorize(string name)
    {
        if (_names.Count < 10 && !_names.Contains(name))
        {
            _names.Add(name);
        }
    }

    private string SimpleName()
    {
        int end = _s.IndexOf('@', _p);
        if (end <= _p)
        {
            throw new DemangleException();
        }

        string name = _s[_p..end];
        _p = end + 1;
        return name;
    }

    private string TemplateName()
    {
        // テンプレートの引数には、別の後方参照の表を使う。
        List<string> savedNames = _names;
        List<string> savedTypes = _types;
        _names = [];
        _types = [];
        try
        {
            string name;
            if (Look() == '?')
            {
                _p++;
                (name, _) = OperatorName();
            }
            else
            {
                name = SimpleName();
                Memorize(name);
            }

            var args = new List<string>();
            while (!Consume('@'))
            {
                if (_p >= _s.Length)
                {
                    throw new DemangleException();
                }

                if (Consume("$$V") || Consume("$$Z") || Consume("$S"))
                {
                    continue;
                }

                if (Consume("$0"))
                {
                    args.Add(Number().ToString(CultureInfo.InvariantCulture));
                }
                else if (Consume("$1"))
                {
                    if (Look() == '?')
                    {
                        int start = _p;
                        var inner = new MicrosoftDemangler(_s[start..], _options);
                        string? text = inner.RunPrefix(out int used);
                        _p = start + used;
                        args.Add("&" + (text ?? throw new DemangleException()));
                    }
                    else
                    {
                        throw new DemangleException();
                    }
                }
                else
                {
                    args.Add(Type());
                }
            }

            return name + "<" + string.Join(", ", args) + ">";
        }
        finally
        {
            _names = savedNames;
            _types = savedTypes;
        }
    }

    /// <summary>テンプレート引数の中のシンボル (<c>$1?x@@3HA</c>) を解釈し、使った文字数を返す。</summary>
    private string? RunPrefix(out int used)
    {
        Expect('?');
        (string name, Special special) = QualifiedName(true);
        string result = Encoding(name, special);
        used = _p;
        return result;
    }

    private static readonly Dictionary<char, string> Operators = new()
    {
        ['2'] = "operator new", ['3'] = "operator delete", ['4'] = "operator=", ['5'] = "operator>>", ['6'] = "operator<<", ['7'] = "operator!",
        ['8'] = "operator==", ['9'] = "operator!=", ['A'] = "operator[]", ['C'] = "operator->", ['D'] = "operator*", ['E'] = "operator++",
        ['F'] = "operator--", ['G'] = "operator-", ['H'] = "operator+", ['I'] = "operator&", ['J'] = "operator->*", ['K'] = "operator/",
        ['L'] = "operator%", ['M'] = "operator<", ['N'] = "operator<=", ['O'] = "operator>", ['P'] = "operator>=", ['Q'] = "operator,",
        ['R'] = "operator()", ['S'] = "operator~", ['T'] = "operator^", ['U'] = "operator|", ['V'] = "operator&&", ['W'] = "operator||",
        ['X'] = "operator*=", ['Y'] = "operator+=", ['Z'] = "operator-=",
    };

    private static readonly Dictionary<char, string> UnderscoreOperators = new()
    {
        ['0'] = "operator/=", ['1'] = "operator%=", ['2'] = "operator>>=", ['3'] = "operator<<=", ['4'] = "operator&=", ['5'] = "operator|=",
        ['6'] = "operator^=", ['9'] = "`vcall'", ['A'] = "`typeof'", ['B'] = "`local static guard'", ['D'] = "`vbase destructor'",
        ['E'] = "`vector deleting destructor'", ['F'] = "`default constructor closure'", ['G'] = "`scalar deleting destructor'",
        ['H'] = "`vector constructor iterator'", ['I'] = "`vector destructor iterator'", ['U'] = "operator new[]", ['V'] = "operator delete[]",
    };

    private (string Name, Special Special) OperatorName()
    {
        char c = Next();
        switch (c)
        {
            case '0':
                return (string.Empty, Special.Constructor);
            case '1':
                return (string.Empty, Special.Destructor);
            case 'B':
                return ("operator", Special.Conversion);
            case '_':
            {
                char d = Next();
                if (d == '7')
                {
                    return (string.Empty, Special.VfTable);
                }

                if (d == '8')
                {
                    return (string.Empty, Special.VbTable);
                }

                if (d == '_' && Look() == 'K')
                {
                    _p++;
                    return ("operator \"\" " + SimpleName(), Special.None);
                }

                return UnderscoreOperators.TryGetValue(d, out string? op) ? (op, Special.None) : throw new DemangleException();
            }

            default:
                return Operators.TryGetValue(c, out string? name) ? (name, Special.None) : throw new DemangleException();
        }
    }

    private long Number()
    {
        bool negative = Consume('?');
        long value;
        char c = Look();
        if (char.IsAsciiDigit(c))
        {
            _p++;
            value = c - '0' + 1;
        }
        else
        {
            value = 0;
            int digits = 0;
            while (!Consume('@'))
            {
                char h = Next();
                if (h is < 'A' or > 'P' || ++digits > 16)
                {
                    throw new DemangleException();
                }

                value = (value << 4) | (long)(h - 'A');
            }
        }

        return negative ? -value : value;
    }

    // ---- エンコーディング ----

    private string Encoding(string name, Special special)
    {
        char c = Next();
        if (c is >= '0' and <= '4')
        {
            string access = c switch { '0' => "private: static ", '1' => "protected: static ", '2' => "public: static ", _ => string.Empty };
            string type = Type();
            string cv = StorageClass();
            return Join(access + Prefix(cv) + type, name);
        }

        if (c == '6' || c == '7')
        {
            string cv = StorageClass();
            if (Look() != '@')
            {
                // どの基底クラスの表か ({for `X'})。
                (string forName, _) = QualifiedName(false);
                Consume('@');
                return Prefix(cv) + name + "{for `" + forName + "'}";
            }

            Expect('@');
            return Prefix(cv) + name;
        }

        if (c == '8')
        {
            return name;
        }

        string accessText;
        bool member;
        bool isStatic = false;
        bool isVirtual = false;
        switch (c)
        {
            case 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H':
                accessText = "private: ";
                break;
            case 'I' or 'J' or 'K' or 'L' or 'M' or 'N' or 'O' or 'P':
                accessText = "protected: ";
                break;
            case 'Q' or 'R' or 'S' or 'T' or 'U' or 'V' or 'W' or 'X':
                accessText = "public: ";
                break;
            case 'Y' or 'Z':
                accessText = string.Empty;
                break;
            default:
                throw new DemangleException();
        }

        int kind = c is 'Y' or 'Z' ? -1 : (c - 'A') % 8;
        member = kind >= 0;
        if (kind is 2 or 3)
        {
            isStatic = true;
        }
        else if (kind is 4 or 5)
        {
            isVirtual = true;
        }
        else if (kind is 6 or 7)
        {
            // thunk (調整付き)。調整量を読み飛ばす。
            isVirtual = true;
            Number();
        }

        string thisQualifier = string.Empty;
        if (member && !isStatic)
        {
            while (Consume('E') || Consume('I') || Consume('F'))
            {
            }

            thisQualifier = Next() switch
            {
                'A' => string.Empty,
                'B' => " const",
                'C' => " volatile",
                'D' => " const volatile",
                _ => throw new DemangleException(),
            };
        }

        string convention = CallingConvention();
        string ret;
        if (Consume('@'))
        {
            ret = string.Empty;
        }
        else
        {
            if (Consume('?'))
            {
                StorageClass();
            }

            ret = Type();
        }

        string parameters = Parameters();
        Throw();
        if (_options.ShortNames)
        {
            return name;
        }

        if (special == Special.Conversion)
        {
            name += " " + ret;
            ret = string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(accessText);
        if (isStatic)
        {
            sb.Append("static ");
        }

        if (isVirtual)
        {
            sb.Append("virtual ");
        }

        if (ret.Length > 0)
        {
            sb.Append(ret).Append(' ');
        }

        sb.Append(convention).Append(' ').Append(name).Append(parameters).Append(thisQualifier);
        return sb.ToString();
    }

    private static string Prefix(string cv) => cv.Length == 0 ? string.Empty : cv + " ";

    private static string Join(string type, string name)
    {
        // 関数へのポインタの変数は名前を (*) の中に入れる。
        int star = type.IndexOf("(__", StringComparison.Ordinal);
        int close = star >= 0 ? type.IndexOf(')', star) : -1;
        return close > 0 ? type[..close] + name + type[close..] : type.EndsWith('*') || type.EndsWith('&') ? type + name : type + " " + name;
    }

    private string StorageClass()
    {
        while (Consume('E') || Consume('I') || Consume('F'))
        {
        }

        return Next() switch
        {
            'A' or 'Q' => string.Empty,
            'B' or 'R' => "const",
            'C' or 'S' => "volatile",
            'D' or 'T' => "const volatile",
            _ => throw new DemangleException(),
        };
    }

    private string CallingConvention() => Next() switch
    {
        'A' or 'B' => "__cdecl",
        'C' or 'D' => "__pascal",
        'E' or 'F' => "__thiscall",
        'G' or 'H' => "__stdcall",
        'I' or 'J' => "__fastcall",
        'M' or 'N' => "__clrcall",
        'O' or 'P' => "__eabi",
        'Q' => "__vectorcall",
        'S' => "__swift_1",
        'W' => "__regcall",
        _ => throw new DemangleException(),
    };

    private string Parameters()
    {
        if (Consume('X'))
        {
            return "(void)";
        }

        var list = new List<string>();
        while (true)
        {
            if (Consume('@'))
            {
                break;
            }

            if (Consume('Z'))
            {
                list.Add("...");
                break;
            }

            if (_p >= _s.Length)
            {
                throw new DemangleException();
            }

            if (char.IsAsciiDigit(Look()))
            {
                int index = Next() - '0';
                list.Add(index < _types.Count ? _types[index] : throw new DemangleException());
                continue;
            }

            int start = _p;
            string type = Type();
            if (_p - start > 1 && _types.Count < 10)
            {
                _types.Add(type);
            }

            list.Add(type);
        }

        return "(" + string.Join(", ", list) + ")";
    }

    private void Throw()
    {
        // 例外の指定 (Z はなし、_E は noexcept)。
        if (Consume("_E"))
        {
            return;
        }

        Expect('Z');
    }

    // ---- 型 ----

    private string Type()
    {
        Enter();
        try
        {
            char c = Next();
            switch (c)
            {
                case 'C': return "signed char";
                case 'D': return "char";
                case 'E': return "unsigned char";
                case 'F': return "short";
                case 'G': return "unsigned short";
                case 'H': return "int";
                case 'I': return "unsigned int";
                case 'J': return "long";
                case 'K': return "unsigned long";
                case 'M': return "float";
                case 'N': return "double";
                case 'O': return "long double";
                case 'X': return "void";
                case 'Z': return "...";
                case '_':
                    return Next() switch
                    {
                        'D' => "__int8",
                        'E' => "unsigned __int8",
                        'F' => "__int16",
                        'G' => "unsigned __int16",
                        'H' => "__int32",
                        'I' => "unsigned __int32",
                        'J' => "__int64",
                        'K' => "unsigned __int64",
                        'L' => "__int128",
                        'M' => "unsigned __int128",
                        'N' => "bool",
                        'Q' => "char8_t",
                        'S' => "char16_t",
                        'U' => "char32_t",
                        'W' => "wchar_t",
                        _ => throw new DemangleException(),
                    };
                case 'T':
                    return "union " + QualifiedName(false).Name;
                case 'U':
                    return "struct " + QualifiedName(false).Name;
                case 'V':
                    return "class " + QualifiedName(false).Name;
                case 'W':
                    Next();
                    return "enum " + QualifiedName(false).Name;
                case 'P' or 'Q' or 'R' or 'S':
                    return Pointer(c switch { 'Q' => "*const", 'R' => "*volatile", 'S' => "*const volatile", _ => "*" });
                case 'A':
                    return Pointer("&");
                case 'B':
                    return Pointer("&volatile");
                case '?':
                {
                    string cv = StorageClass();
                    return Prefix(cv) + Type();
                }

                case '$':
                    if (Consume("$Q"))
                    {
                        return Pointer("&&");
                    }

                    if (Consume("$T"))
                    {
                        return "std::nullptr_t";
                    }

                    if (Consume("$A6"))
                    {
                        return FunctionType("");
                    }

                    if (Consume("$C"))
                    {
                        string cv = StorageClass();
                        return Prefix(cv) + Type();
                    }

                    throw new DemangleException();
                default:
                    if (char.IsAsciiDigit(c))
                    {
                        int index = c - '0';
                        return index < _types.Count ? _types[index] : throw new DemangleException();
                    }

                    throw new DemangleException();
            }
        }
        finally
        {
            _depth--;
        }
    }

    private string Pointer(string symbol)
    {
        while (Consume('E') || Consume('I') || Consume('F'))
        {
        }

        if (Consume('6'))
        {
            return FunctionType(symbol);
        }

        string cv = StorageClass();
        string pointee = Type();
        string space = symbol.StartsWith('&') || symbol.StartsWith('*') ? " " : string.Empty;
        return (cv.Length > 0 ? pointee + " " + cv : pointee) + space + symbol;
    }

    private string FunctionType(string symbol)
    {
        string convention = CallingConvention();
        string ret = Consume('@') ? string.Empty : Type();
        string parameters = Parameters();
        Throw();
        return symbol.Length == 0 ? $"{ret} {convention}{parameters}" : $"{ret} ({convention} {symbol}){parameters}";
    }
}
