using System.Globalization;
using System.Text;

namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>
/// D 言語のデマングラ (ANA-30)。修飾名 (後方参照 <c>Q</c>・テンプレートのインスタンス <c>__T</c>)、関数型 (引数・可変長引数・戻り値)、
/// 基本型・配列・連想配列・ポインタ・const / immutable / shared、クラス・構造体などの名前を解釈する。関数は <c>void foo.bar(int)</c> の形
/// (属性 pure などは表示しない)、変数は名前だけ。
/// </summary>
internal sealed class DDemangler
{
    private readonly string _s;
    private readonly DemangleOptions _options;
    private int _p;
    private int _depth;

    public DDemangler(string s, DemangleOptions options)
    {
        _s = s;
        _options = options;
    }

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

    private void Enter()
    {
        if (++_depth > Demangler.MaxDepth)
        {
            throw new DemangleException();
        }
    }

    public string? Run()
    {
        _p = 2;
        string name = QualifiedName();
        if (_p >= _s.Length)
        {
            return name;
        }

        // メンバ関数の this の修飾 (M の後の x / y / O / Ng)。
        string thisQualifier = string.Empty;
        if (Consume('M'))
        {
            thisQualifier = TypeModifiers();
        }

        if (Look() is 'F' or 'U' or 'W' or 'V' or 'R')
        {
            (string ret, string parameters) = Function();
            if (_p != _s.Length)
            {
                return null;
            }

            return _options.ShortNames ? name : ret + " " + name + parameters + thisQualifier;
        }

        // 変数: 型は表示しない。
        Type();
        return _p == _s.Length ? name : null;
    }

    private string TypeModifiers()
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (Consume('x'))
            {
                sb.Append(" const");
            }
            else if (Consume('y'))
            {
                sb.Append(" immutable");
            }
            else if (Consume('O'))
            {
                sb.Append(" shared");
            }
            else if (Look() == 'N' && Look(1) == 'g')
            {
                _p += 2;
                sb.Append(" inout");
            }
            else
            {
                return sb.ToString();
            }
        }
    }

    private int Length()
    {
        int start = _p;
        while (char.IsAsciiDigit(Look()))
        {
            _p++;
        }

        if (_p == start || _p - start > 9)
        {
            throw new DemangleException();
        }

        return int.Parse(_s.AsSpan(start, _p - start), CultureInfo.InvariantCulture);
    }

    /// <summary>後方参照の位置 (大文字で続き、小文字で終わる 26 進)。</summary>
    private int BackrefPosition()
    {
        int at = _p - 1;
        int value = 0;
        for (int i = 0; i < 8; i++)
        {
            char c = Look();
            _p++;
            if (c is >= 'A' and <= 'Z')
            {
                value = value * 26 + (c - 'A');
            }
            else if (c is >= 'a' and <= 'z')
            {
                value = value * 26 + (c - 'a');
                if (value <= 0 || value > at)
                {
                    throw new DemangleException();
                }

                return at - value;
            }
            else
            {
                throw new DemangleException();
            }
        }

        throw new DemangleException();
    }

    private string QualifiedName()
    {
        Enter();
        try
        {
            var parts = new List<string>();
            do
            {
                parts.Add(SymbolName());
            }
            while (char.IsAsciiDigit(Look()) || Look() == 'Q' && parts.Count > 0 && LooksLikeName(BackrefTargetPeek()));
            return string.Join(".", parts);
        }
        finally
        {
            _depth--;
        }
    }

    private int BackrefTargetPeek()
    {
        int saved = _p;
        try
        {
            _p++;
            return BackrefPosition();
        }
        catch (DemangleException)
        {
            return -1;
        }
        finally
        {
            _p = saved;
        }
    }

    private bool LooksLikeName(int position) => position >= 0 && position < _s.Length && char.IsAsciiDigit(_s[position]);

    private string SymbolName()
    {
        if (Consume('Q'))
        {
            int target = BackrefPosition();
            int saved = _p;
            _p = target;
            string name = SymbolName();
            _p = saved;
            return name;
        }

        int length = Length();
        if (length == 0 || _p + length > _s.Length)
        {
            throw new DemangleException();
        }

        if (string.CompareOrdinal(_s, _p, "__T", 0, 3) == 0)
        {
            int end = _p + length;
            _p += 3;
            string template = TemplateInstance();
            _p = end;
            return template;
        }

        if (string.CompareOrdinal(_s, _p, "__S", 0, 3) == 0)
        {
            _p += length;
            return SymbolName();
        }

        string id = _s.Substring(_p, length);
        _p += length;
        return id;
    }

    private string TemplateInstance()
    {
        int length = Length();
        if (_p + length > _s.Length)
        {
            throw new DemangleException();
        }

        string name = _s.Substring(_p, length);
        _p += length;
        var args = new List<string>();
        while (!Consume('Z'))
        {
            char c = _p < _s.Length ? _s[_p++] : throw new DemangleException();
            switch (c)
            {
                case 'T':
                    args.Add(Type());
                    break;
                case 'V':
                    Type();
                    args.Add(Value());
                    break;
                case 'S':
                    args.Add(QualifiedName());
                    break;
                default:
                    throw new DemangleException();
            }
        }

        return name + "!(" + string.Join(", ", args) + ")";
    }

    private string Value()
    {
        if (Consume('i'))
        {
            return Length().ToString(CultureInfo.InvariantCulture);
        }

        if (Consume('N'))
        {
            return "-" + Length().ToString(CultureInfo.InvariantCulture);
        }

        if (Consume('n'))
        {
            return "null";
        }

        throw new DemangleException();
    }

    private (string Ret, string Parameters) Function()
    {
        _p++;

        // 関数の属性 (pure、nothrow など) は読み飛ばす。
        while (Look() == 'N' && Look(1) is 'a' or 'b' or 'c' or 'd' or 'e' or 'f' or 'i' or 'j' or 'l' or 'm')
        {
            _p += 2;
        }

        var parameters = new List<string>();
        string variadic = string.Empty;
        while (true)
        {
            if (Consume('Z'))
            {
                break;
            }

            if (Consume('X'))
            {
                variadic = "...";
                break;
            }

            if (Consume('Y'))
            {
                variadic = parameters.Count > 0 ? ", ..." : "...";
                break;
            }

            if (_p >= _s.Length)
            {
                throw new DemangleException();
            }

            string storage = string.Empty;
            while (true)
            {
                if (Consume('J'))
                {
                    storage += "out ";
                }
                else if (Consume('K'))
                {
                    storage += "ref ";
                }
                else if (Consume('L'))
                {
                    storage += "lazy ";
                }
                else if (Consume('M'))
                {
                    storage += "scope ";
                }
                else if (Look() == 'N' && Look(1) == 'k')
                {
                    _p += 2;
                    storage += "return ";
                }
                else
                {
                    break;
                }
            }

            parameters.Add(storage + Type());
        }

        string ret = Type();
        string list = string.Join(", ", parameters);
        return (ret, "(" + list + variadic + ")");
    }

    private string Type()
    {
        Enter();
        try
        {
            char c = _p < _s.Length ? _s[_p++] : throw new DemangleException();
            switch (c)
            {
                case 'v': return "void";
                case 'g': return "byte";
                case 'h': return "ubyte";
                case 's': return "short";
                case 't': return "ushort";
                case 'i': return "int";
                case 'k': return "uint";
                case 'l': return "long";
                case 'm': return "ulong";
                case 'f': return "float";
                case 'd': return "double";
                case 'e': return "real";
                case 'o': return "ifloat";
                case 'p': return "idouble";
                case 'j': return "ireal";
                case 'q': return "cfloat";
                case 'r': return "cdouble";
                case 'c': return "creal";
                case 'b': return "bool";
                case 'a': return "char";
                case 'u': return "wchar";
                case 'w': return "dchar";
                case 'n': return "typeof(null)";
                case 'z':
                    return Consume('i') ? "cent" : Consume('k') ? "ucent" : throw new DemangleException();
                case 'A':
                    return Type() + "[]";
                case 'G':
                {
                    int n = Length();
                    return Type() + "[" + n.ToString(CultureInfo.InvariantCulture) + "]";
                }

                case 'H':
                {
                    string key = Type();
                    return Type() + "[" + key + "]";
                }

                case 'P':
                    return Type() + "*";
                case 'x':
                    return "const(" + Type() + ")";
                case 'y':
                    return "immutable(" + Type() + ")";
                case 'O':
                    return "shared(" + Type() + ")";
                case 'N' when Look() == 'g':
                    _p++;
                    return "inout(" + Type() + ")";
                case 'C' or 'S' or 'E' or 'T' or 'I':
                    return QualifiedName();
                case 'F' or 'U' or 'W' or 'V' or 'R':
                {
                    _p--;
                    (string ret, string parameters) = Function();
                    return ret + " function" + parameters;
                }

                case 'D':
                {
                    (string ret, string parameters) = Function();
                    return ret + " delegate" + parameters;
                }

                case 'Q':
                {
                    int target = BackrefPosition();
                    int saved = _p;
                    _p = target;
                    string type = Type();
                    _p = saved;
                    return type;
                }

                default:
                    throw new DemangleException();
            }
        }
        finally
        {
            _depth--;
        }
    }
}
