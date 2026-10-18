using System.Globalization;
using System.Text;

namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>
/// Rust の旧方式 (Itanium の入れ子の名前の形で、最後が <c>17h&lt;16 桁&gt;E</c> のハッシュ) のデマングラ (ANA-30)。
/// <c>$LT$</c> などのエスケープと <c>..</c> (<c>::</c>) を戻す。ハッシュは既定で表示しない (仕様 5)。
/// </summary>
internal static class RustLegacyDemangler
{
    public static bool Looks(string name)
    {
        string s = StripSuffix(name.StartsWith("__Z", StringComparison.Ordinal) ? name[1..] : name, out _);
        if (!s.StartsWith("_ZN", StringComparison.Ordinal) || !s.EndsWith('E') || s.Length < 3 + 20)
        {
            return false;
        }

        string tail = s[^20..^1];
        return tail.StartsWith("17h", StringComparison.Ordinal) && tail[3..].All(char.IsAsciiHexDigit);
    }

    /// <summary>ハッシュの後ろの接尾辞 (<c>.llvm.1234</c> など) を外す (名前の中の <c>..</c> は <c>::</c> なので外さない)。</summary>
    private static string StripSuffix(string s, out string suffix)
    {
        suffix = string.Empty;
        int at = s.LastIndexOf("17h", StringComparison.Ordinal);
        if (at > 0 && at + 20 <= s.Length && s[at + 19] == 'E' && at + 20 < s.Length && s[at + 20] == '.')
        {
            suffix = s[(at + 20)..];
            return s[..(at + 20)];
        }

        return s;
    }

    public static string? Demangle(string name, DemangleOptions options)
    {
        string s = StripSuffix(name.StartsWith("__Z", StringComparison.Ordinal) ? name[1..] : name, out string suffix);

        int p = 3;
        var parts = new List<string>();
        while (p < s.Length && s[p] != 'E')
        {
            int start = p;
            while (p < s.Length && char.IsAsciiDigit(s[p]))
            {
                p++;
            }

            if (p == start || p - start > 9)
            {
                return null;
            }

            int length = int.Parse(s.AsSpan(start, p - start), CultureInfo.InvariantCulture);
            if (length == 0 || p + length > s.Length)
            {
                return null;
            }

            parts.Add(s.Substring(p, length));
            p += length;
        }

        if (p != s.Length - 1 || parts.Count < 2)
        {
            return null;
        }

        string hash = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("::");
            }

            if (Unescape(parts[i]) is not { } part)
            {
                return null;
            }

            sb.Append(part);
        }

        if (options.ShowRustHash)
        {
            sb.Append("::").Append(hash);
        }

        return sb + suffix;
    }

    private static string? Unescape(string part)
    {
        // 先頭の "_$" は "$" の前に付けたもの。
        if (part.StartsWith("_$", StringComparison.Ordinal))
        {
            part = part[1..];
        }

        var sb = new StringBuilder();
        int i = 0;
        while (i < part.Length)
        {
            if (part[i] == '$')
            {
                int end = part.IndexOf('$', i + 1);
                if (end < 0)
                {
                    return null;
                }

                string code = part[(i + 1)..end];
                string? text = code switch
                {
                    "SP" => "@",
                    "BP" => "*",
                    "RF" => "&",
                    "LT" => "<",
                    "GT" => ">",
                    "LP" => "(",
                    "RP" => ")",
                    "C" => ",",
                    _ when code.StartsWith('u') && code.Length > 1 && int.TryParse(code.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u)
                        && u <= 0x10FFFF && (u < 0xD800 || u > 0xDFFF) => char.ConvertFromUtf32(u),
                    _ => null,
                };
                if (text is null)
                {
                    return null;
                }

                sb.Append(text);
                i = end + 1;
            }
            else if (part[i] == '.' && i + 1 < part.Length && part[i + 1] == '.')
            {
                sb.Append("::");
                i += 2;
            }
            else
            {
                sb.Append(part[i]);
                i++;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Rust v0 (<c>_R</c>) のデマングラ (ANA-30)。パス (クレートの根・入れ子・impl・ジェネリクス・後方参照)、型 (基本型・参照・ポインタ・
/// 配列・スライス・タプル・関数・dyn)、定数を解釈する。クレートの曖昧さ回避の値 (<c>Cs1234_</c>) は表示しない (LLVM の既定と同じ)。
/// </summary>
internal sealed class RustV0Demangler
{
    private readonly string _s;
    private int _p;
    private int _depth;
    private readonly StringBuilder _out = new();

    /// <summary>表示オプションは v0 の出力に影響しない (ハッシュを持たない)。</summary>
    public RustV0Demangler(string s, DemangleOptions options)
    {
        _s = s;
        _ = options;
    }

    public string? Run()
    {
        _p = 2;
        if (char.IsAsciiDigit(Look()))
        {
            // エンコーディングの版 (0 以外は対象外)。
            return null;
        }

        Path(inValue: true);
        if (_p < _s.Length && char.IsAsciiLetterUpper(Look()))
        {
            // インスタンス化したクレート (表示しない)。
            var saved = _out.Length;
            Path(inValue: false);
            _out.Length = saved;
        }

        string suffix = _p < _s.Length && Look() == '.' ? " (" + _s[_p..] + ")" : string.Empty;
        if (suffix.Length > 0)
        {
            _p = _s.Length;
        }

        return _p == _s.Length ? _out + suffix : null;
    }

    private char Look() => _p < _s.Length ? _s[_p] : '\0';

    private bool Consume(char c)
    {
        if (Look() == c)
        {
            _p++;
            return true;
        }

        return false;
    }

    private void Add(string text)
    {
        _out.Append(text);
        if (_out.Length > Demangler.MaxOutput)
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

    /// <summary>base-62 の数 (<c>_</c> で終わる。<c>_</c> だけなら 0、そうでなければ値 + 1)。</summary>
    private ulong Base62()
    {
        if (Consume('_'))
        {
            return 0;
        }

        ulong value = 0;
        int digits = 0;
        while (!Consume('_'))
        {
            char c = Look();
            int d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 36,
                _ => throw new DemangleException(),
            };
            _p++;
            value = checked(value * 62 + (ulong)d);
            if (++digits > 12)
            {
                throw new DemangleException();
            }
        }

        return checked(value + 1);
    }

    private ulong Disambiguator() => Consume('s') ? Base62() + 1 : 0;

    private string Identifier()
    {
        Disambiguator();
        bool punycode = Consume('u');
        int start = _p;
        while (char.IsAsciiDigit(Look()))
        {
            _p++;
        }

        if (_p == start || _p - start > 9)
        {
            throw new DemangleException();
        }

        int length = int.Parse(_s.AsSpan(start, _p - start), CultureInfo.InvariantCulture);
        Consume('_');
        if (_p + length > _s.Length)
        {
            throw new DemangleException();
        }

        string id = _s.Substring(_p, length);
        _p += length;
        if (punycode)
        {
            // Punycode の識別子は変換せずに表示する。
            return "punycode{" + id + "}";
        }

        return id;
    }

    private void Backref(Action<bool> parse, bool flag)
    {
        // 後方参照の位置は "_R" の後ろからの位置。
        int at = _p - 1;
        ulong target = Base62() + 2;
        if (target >= (ulong)at)
        {
            throw new DemangleException();
        }

        int saved = _p;
        _p = (int)target;
        parse(flag);
        _p = saved;
    }

    private void Path(bool inValue)
    {
        Enter();
        try
        {
            char c = Look();
            _p++;
            switch (c)
            {
                case 'C':
                    Add(Identifier());
                    break;
                case 'N':
                {
                    char ns = Look();
                    if (!char.IsAsciiLetter(ns))
                    {
                        throw new DemangleException();
                    }

                    _p++;
                    Path(inValue);
                    ulong dis = Disambiguator();
                    string name = Identifier();
                    if (char.IsAsciiLetterUpper(ns))
                    {
                        string kind = ns switch { 'C' => "closure", 'S' => "shim", _ => ns.ToString() };
                        Add("::{" + kind + (name.Length > 0 ? ":" + name : string.Empty) + "#" + dis.ToString(CultureInfo.InvariantCulture) + "}");
                    }
                    else if (name.Length > 0)
                    {
                        Add("::" + name);
                    }

                    break;
                }

                case 'M':
                    Disambiguator();
                    SkipPath();
                    Add("<");
                    Type();
                    Add(">");
                    break;
                case 'X':
                    Disambiguator();
                    SkipPath();
                    Add("<");
                    Type();
                    Add(" as ");
                    Path(inValue: false);
                    Add(">");
                    break;
                case 'Y':
                    Add("<");
                    Type();
                    Add(" as ");
                    Path(inValue: false);
                    Add(">");
                    break;
                case 'I':
                {
                    Path(inValue);
                    if (inValue)
                    {
                        Add("::");
                    }

                    Add("<");
                    int n = 0;
                    while (!Consume('E'))
                    {
                        if (_p >= _s.Length)
                        {
                            throw new DemangleException();
                        }

                        if (n++ > 0)
                        {
                            Add(", ");
                        }

                        GenericArg();
                    }

                    Add(">");
                    break;
                }

                case 'B':
                    Backref(Path, inValue);
                    break;
                default:
                    throw new DemangleException();
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>impl のパス (表示しない) を読み飛ばす。</summary>
    private void SkipPath()
    {
        int saved = _out.Length;
        Path(inValue: false);
        _out.Length = saved;
    }

    private void GenericArg()
    {
        if (Consume('L'))
        {
            ulong lifetime = Base62();
            Add(lifetime == 0 ? "'_" : "'_" + lifetime.ToString(CultureInfo.InvariantCulture));
        }
        else if (Consume('K'))
        {
            Const();
        }
        else
        {
            Type();
        }
    }

    private static string? Basic(char c) => c switch
    {
        'a' => "i8", 'b' => "bool", 'c' => "char", 'd' => "f64", 'e' => "str", 'f' => "f32", 'h' => "u8", 'i' => "isize", 'j' => "usize",
        'l' => "i32", 'm' => "u32", 'n' => "i128", 'o' => "u128", 's' => "i16", 't' => "u16", 'u' => "()", 'v' => "...", 'x' => "i64",
        'y' => "u64", 'z' => "!", 'p' => "_",
        _ => null,
    };

    private void Type(bool unused = false)
    {
        Enter();
        try
        {
            char c = Look();
            if (Basic(c) is { } basic)
            {
                _p++;
                Add(basic);
                return;
            }

            _p++;
            switch (c)
            {
                case 'R' or 'Q':
                    Add("&");
                    if (Consume('L'))
                    {
                        ulong lifetime = Base62();
                        if (lifetime != 0)
                        {
                            Add("'_" + lifetime.ToString(CultureInfo.InvariantCulture) + " ");
                        }
                    }

                    if (c == 'Q')
                    {
                        Add("mut ");
                    }

                    Type();
                    break;
                case 'P':
                    Add("*const ");
                    Type();
                    break;
                case 'O':
                    Add("*mut ");
                    Type();
                    break;
                case 'A':
                    Add("[");
                    Type();
                    Add("; ");
                    Const();
                    Add("]");
                    break;
                case 'S':
                    Add("[");
                    Type();
                    Add("]");
                    break;
                case 'T':
                {
                    Add("(");
                    int n = 0;
                    while (!Consume('E'))
                    {
                        if (_p >= _s.Length)
                        {
                            throw new DemangleException();
                        }

                        if (n++ > 0)
                        {
                            Add(", ");
                        }

                        Type();
                    }

                    if (n == 1)
                    {
                        Add(",");
                    }

                    Add(")");
                    break;
                }

                case 'F':
                {
                    if (Consume('G'))
                    {
                        Base62();
                    }

                    if (Consume('U'))
                    {
                        Add("unsafe ");
                    }

                    if (Consume('K'))
                    {
                        Add("extern \"");
                        if (Consume('C'))
                        {
                            Add("C");
                        }
                        else
                        {
                            Add(Identifier().Replace('_', '-'));
                        }

                        Add("\" ");
                    }

                    Add("fn(");
                    int n = 0;
                    while (!Consume('E'))
                    {
                        if (_p >= _s.Length)
                        {
                            throw new DemangleException();
                        }

                        if (n++ > 0)
                        {
                            Add(", ");
                        }

                        Type();
                    }

                    Add(")");
                    if (Look() == 'u')
                    {
                        _p++;
                    }
                    else
                    {
                        Add(" -> ");
                        Type();
                    }

                    break;
                }

                case 'D':
                {
                    Add("dyn ");
                    if (Consume('G'))
                    {
                        Base62();
                    }

                    int n = 0;
                    while (!Consume('E'))
                    {
                        if (_p >= _s.Length)
                        {
                            throw new DemangleException();
                        }

                        if (n++ > 0)
                        {
                            Add(" + ");
                        }

                        Path(inValue: false);
                        while (Consume('p'))
                        {
                            Identifier();
                            Type();
                        }
                    }

                    if (!Consume('L'))
                    {
                        throw new DemangleException();
                    }

                    Base62();
                    break;
                }

                case 'B':
                    Backref(Type, false);
                    break;
                default:
                    _p--;
                    Path(inValue: false);
                    break;
            }
        }
        finally
        {
            _depth--;
        }
    }

    private void Const()
    {
        if (Consume('p'))
        {
            Add("_");
            return;
        }

        if (Consume('B'))
        {
            int at = _p - 1;
            ulong target = Base62() + 2;
            if (target >= (ulong)at)
            {
                throw new DemangleException();
            }

            int saved = _p;
            _p = (int)target;
            Const();
            _p = saved;
            return;
        }

        char type = Look();
        if (Basic(type) is null)
        {
            throw new DemangleException();
        }

        _p++;
        bool negative = Consume('n');
        int start = _p;
        while (char.IsAsciiHexDigit(Look()))
        {
            _p++;
        }

        string hex = _s[start.._p];
        if (!Consume('_'))
        {
            throw new DemangleException();
        }

        if (type == 'b')
        {
            Add(hex == "1" ? "true" : "false");
            return;
        }

        if (type == 'c')
        {
            int cp = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Add("'" + char.ConvertFromUtf32(cp) + "'");
            return;
        }

        System.Numerics.BigInteger value = hex.Length == 0 ? 0 : System.Numerics.BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Add((negative ? "-" : string.Empty) + value.ToString(CultureInfo.InvariantCulture));
    }
}
