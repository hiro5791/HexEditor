using System.Globalization;
using System.Text;

namespace HexEditor.Core.Search.Yara;

/// <summary>コンパイルの誤り 1 件 (ファイル名・行番号・メッセージ。FIND-33 の「エラー」)。</summary>
public sealed record YaraCompileError(string? File, int Line, string Message)
{
    public override string ToString() => $"{(File is null ? string.Empty : Path.GetFileName(File) + "(")}{Line}{(File is null ? string.Empty : ")")}: {Message}";
}

/// <summary>コンパイルできなかった (誤りの一覧)。</summary>
public sealed class YaraCompileErrorsException(IReadOnlyList<YaraCompileError> errors)
    : Exception(string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<YaraCompileError> Errors { get; } = errors;
}

/// <summary>コンパイルしたルールセット。</summary>
public sealed class YaraRuleSet
{
    internal YaraRuleSet(IReadOnlyList<YaraRule> rules, IReadOnlyCollection<string> modules, IReadOnlyDictionary<string, YValue> externals)
    {
        Rules = rules;
        Modules = modules;
        Externals = externals;
    }

    public IReadOnlyList<YaraRule> Rules { get; }

    /// <summary>import したモジュール。</summary>
    public IReadOnlyCollection<string> Modules { get; }

    internal IReadOnlyDictionary<string, YValue> Externals { get; }

    /// <summary>文字列の一致の最大の長さ (ブロックの重なり幅を超える文字列があれば、ブロックをまたぐ一致が見つからないことがある)。</summary>
    public int MaxMatchLength => Rules.SelectMany(r => r.Strings).SelectMany(s => s.Matchers).Select(m => m.MaxLength).DefaultIfEmpty(0).Max();
}

/// <summary>
/// YARA のルールのコンパイル (FIND-33 の仕様 1・5)。ルールは利用者が用意する信頼できない入力として扱う: 入れ子の深さ・include の深さ・繰り返しの
/// 大きさに上限を設け、正規表現は入力の長さに比例する時間で照合する形にする。include は含めるファイルからの相対パスで解決する。
/// yarac で作ったコンパイル済みのファイルは、形式が libyara の版ごとに異なり公開されていないため読み込まない (誤りとして示す)。
/// </summary>
public sealed class YaraCompiler
{
    private const int MaxIncludeDepth = 16;
    private const int MaxExpressionDepth = 256;

    private readonly List<YaraRule> _rules = [];
    private readonly Dictionary<string, YaraRule> _byName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, YValue> _externals = new(StringComparer.Ordinal);
    private readonly List<YaraCompileError> _errors = [];

    public IReadOnlyList<YaraCompileError> Errors => _errors;

    /// <summary>外部変数を定義する (整数・浮動小数点・真偽・文字列。-d 相当)。ルールを加える前に呼ぶ。</summary>
    public void DefineExternal(string name, object value)
    {
        _externals[name] = value switch
        {
            bool b => YValue.Bool(b),
            int i => YValue.Int(i),
            long l => YValue.Int(l),
            double d => YValue.Float(d),
            float f => YValue.Float(f),
            string s => YValue.Str(s),
            _ => throw new ArgumentException("外部変数の値は整数・浮動小数点・真偽・文字列です。", nameof(value)),
        };
    }

    /// <summary>外部変数を文字列から定義する (true/false は真偽、整数・小数として読めればその値、それ以外は文字列)。</summary>
    public void DefineExternalText(string name, string text)
    {
        if (bool.TryParse(text, out bool b))
        {
            DefineExternal(name, b);
        }
        else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        {
            DefineExternal(name, l);
        }
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            DefineExternal(name, d);
        }
        else
        {
            DefineExternal(name, text);
        }
    }

    /// <summary>ルールのファイルを加える (.yar / .yara)。誤りは <see cref="Errors"/> に入る。</summary>
    public void AddFile(string path)
    {
        string full = Path.GetFullPath(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _errors.Add(new YaraCompileError(full, 0, "can't open file: " + e.Message));
            return;
        }

        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("YARA"u8))
        {
            _errors.Add(new YaraCompileError(full, 0, "compiled rules (yarac) are not supported; add the rule source (.yar)"));
            return;
        }

        AddSource(DecodeSource(bytes), full);
    }

    /// <summary>ソースを加える (<paramref name="file"/> は誤りの表示と include の基準)。</summary>
    public void AddSource(string source, string? file = null)
    {
        try
        {
            var parser = new Parser(this, new YaraLexer(source, file), file, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            parser.ParseFile();
        }
        catch (YaraCompileException e)
        {
            _errors.Add(new YaraCompileError(e.File, e.Line, e.Message));
        }
    }

    /// <summary>コンパイルを終える。誤りがあれば <see cref="YaraCompileErrorsException"/> (スキャンを始めない)。</summary>
    public YaraRuleSet Build()
    {
        if (_errors.Count > 0)
        {
            throw new YaraCompileErrorsException([.. _errors]);
        }

        return new YaraRuleSet([.. _rules], [.. _modules], new Dictionary<string, YValue>(_externals, StringComparer.Ordinal));
    }

    private static string DecodeSource(byte[] bytes)
    {
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, false).GetString(bytes, skip, bytes.Length - skip);
    }

    // ---- 構文解析 ----

    private sealed class Parser(YaraCompiler compiler, YaraLexer lexer, string? file, int includeDepth, HashSet<string> including)
    {
        private readonly HashSet<string> _imports = new(StringComparer.Ordinal);
        private YaraToken? _current;
        private YaraToken? _previous;
        private YaraRule? _rule;
        private int _depth;
        private int _forOf;
        private bool _noOf;
        private readonly List<(string Name, object? Shape)> _variables = [];

        private YaraToken Peek(bool regex = false) => _current ??= lexer.Next(regex);

        private YaraToken Take(bool regex = false)
        {
            YaraToken t = Peek(regex);
            _previous = t;
            _current = null;
            return t;
        }

        private bool Accept(string keywordOrSymbol)
        {
            if (Peek().Is(keywordOrSymbol))
            {
                Take();
                return true;
            }

            return false;
        }

        private YaraToken Expect(string keywordOrSymbol)
        {
            YaraToken t = Peek();
            if (!t.Is(keywordOrSymbol))
            {
                throw Unexpected(t, $"'{keywordOrSymbol}'");
            }

            return Take();
        }

        private YaraToken Expect(YaraTokenKind kind, string what)
        {
            YaraToken t = Peek();
            if (t.Kind != kind)
            {
                throw Unexpected(t, what);
            }

            return Take();
        }

        /// <summary>構文の誤り。式が足りない場合などは、足りない直前の字句の行で示す (閉じかっこの行ではなく)。</summary>
        private YaraCompileException Unexpected(YaraToken t, string expecting)
        {
            int line = _previous is { } p && p.Line < t.Line ? p.Line : t.Line;
            string found = t.Kind == YaraTokenKind.End ? "end of file" : $"'{t.Text}'";
            return lexer.Error($"syntax error, unexpected {found}, expecting {expecting}", line);
        }

        private YaraCompileException Error(string message, int? line = null) => lexer.Error(message, line ?? Peek().Line);

        public void ParseFile()
        {
            while (Peek().Kind != YaraTokenKind.End)
            {
                if (Accept("import"))
                {
                    YaraToken name = Expect(YaraTokenKind.Text, "module name");
                    string module = Encoding.UTF8.GetString(name.Bytes);
                    if (!YaraModules.Names.Contains(module))
                    {
                        throw lexer.Error($"unknown module \"{module}\"", name.Line);
                    }

                    _imports.Add(module);
                    compiler._modules.Add(module);
                    continue;
                }

                if (Accept("include"))
                {
                    YaraToken name = Expect(YaraTokenKind.Text, "file name");
                    Include(Encoding.UTF8.GetString(name.Bytes), name.Line);
                    continue;
                }

                ParseRule();
            }
        }

        private void Include(string name, int line)
        {
            if (includeDepth >= MaxIncludeDepth)
            {
                throw lexer.Error("too many nested includes", line);
            }

            string baseDir = file is null ? Environment.CurrentDirectory : Path.GetDirectoryName(file) ?? Environment.CurrentDirectory;
            string path = Path.GetFullPath(Path.Combine(baseDir, name));
            if (!including.Add(path))
            {
                throw lexer.Error($"include cycle \"{name}\"", line);
            }

            try
            {
                string text;
                try
                {
                    text = DecodeSource(File.ReadAllBytes(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw lexer.Error($"can't open include file: {name}", line);
                }

                var nested = new Parser(compiler, new YaraLexer(text, path), path, includeDepth + 1, including);
                foreach (string m in _imports)
                {
                    nested._imports.Add(m);
                }

                nested.ParseFile();
                foreach (string m in nested._imports)
                {
                    _imports.Add(m);
                }
            }
            finally
            {
                including.Remove(path);
            }
        }

        private void ParseRule()
        {
            bool isPrivate = false, isGlobal = false;
            while (true)
            {
                if (Accept("private"))
                {
                    isPrivate = true;
                }
                else if (Accept("global"))
                {
                    isGlobal = true;
                }
                else
                {
                    break;
                }
            }

            int line = Expect("rule").Line;
            YaraToken name = Expect(YaraTokenKind.Identifier, "rule identifier");
            if (compiler._byName.ContainsKey(name.Text))
            {
                throw lexer.Error($"duplicated identifier \"{name.Text}\"", name.Line);
            }

            var tags = new List<string>();
            if (Accept(":"))
            {
                while (Peek().Kind == YaraTokenKind.Identifier)
                {
                    tags.Add(Take().Text);
                }

                if (tags.Count == 0)
                {
                    throw Unexpected(Peek(), "tag");
                }
            }

            Expect("{");
            var meta = new List<KeyValuePair<string, string>>();
            if (Accept("meta"))
            {
                Expect(":");
                while (Peek().Kind == YaraTokenKind.Identifier)
                {
                    string key = Take().Text;
                    Expect("=");
                    YaraToken v = Take();
                    string value = v.Kind switch
                    {
                        YaraTokenKind.Text => Encoding.UTF8.GetString(v.Bytes),
                        YaraTokenKind.Integer => v.Integer.ToString(CultureInfo.InvariantCulture),
                        YaraTokenKind.Float => v.Float.ToString(CultureInfo.InvariantCulture),
                        YaraTokenKind.Keyword when v.Text is "true" or "false" => v.Text,
                        YaraTokenKind.Symbol when v.Text == "-" && Peek().Kind == YaraTokenKind.Integer => "-" + Take().Integer.ToString(CultureInfo.InvariantCulture),
                        _ => throw Unexpected(v, "meta value"),
                    };
                    meta.Add(new(key, value));
                }
            }

            var rule = new YaraRule { Name = name.Text, Tags = tags, Meta = meta, IsPrivate = isPrivate, IsGlobal = isGlobal, File = file, Line = line };
            _rule = rule;
            if (Accept("strings"))
            {
                Expect(":");
                while (Peek().Kind == YaraTokenKind.StringIdentifier)
                {
                    ParseString(rule);
                }

                if (rule.Strings.Count == 0)
                {
                    throw Unexpected(Peek(), "string identifier");
                }
            }

            Expect("condition");
            Expect(":");
            rule.Condition = ParseExpression(0);
            Expect("}");
            foreach (YaraStringDef s in rule.Strings)
            {
                if (!s.Referenced && !s.Identifier.StartsWith("$_", StringComparison.Ordinal))
                {
                    throw lexer.Error($"unreferenced string \"{s.Identifier}\"", line);
                }
            }

            _rule = null;
            compiler._rules.Add(rule);
            compiler._byName[rule.Name] = rule;
        }

        private void ParseString(YaraRule rule)
        {
            YaraToken id = Take();
            if (id.Text is "$" || id.Text.EndsWith('*'))
            {
                throw lexer.Error($"invalid string identifier \"{id.Text}\"", id.Line);
            }

            if (rule.Strings.Any(s => s.Identifier == id.Text))
            {
                throw lexer.Error($"duplicated string identifier \"{id.Text}\"", id.Line);
            }

            Expect("=");
            YaraToken value = Peek(regex: true);
            YaraStringKind kind;
            byte[] text = [];
            string body = string.Empty, flags = string.Empty;
            if (value.Kind == YaraTokenKind.Text)
            {
                Take();
                kind = YaraStringKind.Text;
                text = value.Bytes;
                if (text.Length == 0)
                {
                    throw lexer.Error("empty string", value.Line);
                }
            }
            else if (value.Kind == YaraTokenKind.Regex)
            {
                Take();
                kind = YaraStringKind.Regex;
                body = value.Text;
                flags = value.Flags;
            }
            else if (value.Is("{"))
            {
                Take();
                kind = YaraStringKind.Hex;
                (body, _) = lexer.ReadHexString();
            }
            else
            {
                throw Unexpected(value, "string, hex string or regular expression");
            }

            // 修飾子。
            bool nocase = false, wide = false, ascii = false, fullword = false, isPrivate = false, xor = false, base64 = false, base64Wide = false;
            int xorMin = 0, xorMax = 255;
            string alphabet = YaraTextVariants.StandardAlphabet;
            while (Peek().Kind == YaraTokenKind.Keyword && Peek().Text is "nocase" or "wide" or "ascii" or "fullword" or "private" or "xor" or "base64" or "base64wide")
            {
                YaraToken m = Take();
                switch (m.Text)
                {
                    case "nocase": nocase = true; break;
                    case "wide": wide = true; break;
                    case "ascii": ascii = true; break;
                    case "fullword": fullword = true; break;
                    case "private": isPrivate = true; break;
                    case "xor":
                        xor = true;
                        if (Accept("("))
                        {
                            xorMin = (int)Expect(YaraTokenKind.Integer, "xor key").Integer;
                            xorMax = Accept("-") ? (int)Expect(YaraTokenKind.Integer, "xor key").Integer : xorMin;
                            Expect(")");
                            if (xorMin is < 0 or > 255 || xorMax is < 0 or > 255 || xorMax < xorMin)
                            {
                                throw lexer.Error("invalid xor range", m.Line);
                            }
                        }

                        break;
                    default:
                        if (m.Text == "base64")
                        {
                            base64 = true;
                        }
                        else
                        {
                            base64Wide = true;
                        }

                        if (Accept("("))
                        {
                            byte[] a = Expect(YaraTokenKind.Text, "base64 alphabet").Bytes;
                            Expect(")");
                            if (a.Length != 64)
                            {
                                throw lexer.Error("base64 alphabet must have 64 characters", m.Line);
                            }

                            alphabet = Encoding.Latin1.GetString(a);
                        }

                        break;
                }
            }

            if (kind != YaraStringKind.Text && (xor || base64 || base64Wide))
            {
                throw lexer.Error("invalid modifier combination for a hex string or regular expression", id.Line);
            }

            if (kind == YaraStringKind.Hex && (nocase || wide || ascii || fullword))
            {
                throw lexer.Error("invalid modifier for a hex string", id.Line);
            }

            if (xor && nocase)
            {
                throw lexer.Error("invalid modifier combination: xor nocase", id.Line);
            }

            if ((base64 || base64Wide) && (nocase || xor || fullword))
            {
                throw lexer.Error("invalid modifier combination with base64", id.Line);
            }

            var def = new YaraStringDef { Identifier = id.Text, Rule = rule, Kind = kind, IsPrivate = isPrivate, Fullword = fullword, Index = rule.Strings.Count };
            try
            {
                BuildMatchers(def, text, body, flags, nocase, wide, ascii, xor, xorMin, xorMax, base64, base64Wide, alphabet);
            }
            catch (FormatException e)
            {
                throw lexer.Error(e.Message, id.Line);
            }

            rule.Strings.Add(def);
        }

        private static void BuildMatchers(YaraStringDef def, byte[] text, string body, string flags, bool nocase, bool wide, bool ascii, bool xor,
            int xorMin, int xorMax, bool base64, bool base64Wide, string alphabet)
        {
            bool useAscii = ascii || !wide;
            switch (def.Kind)
            {
                case YaraStringKind.Hex:
                    def.Matchers.Add(new YaraHexMatcher(YaraHexMatcher.Parse(body)));
                    return;
                case YaraStringKind.Regex:
                    bool i = nocase || flags.Contains('i'), s = flags.Contains('s');
                    if (useAscii)
                    {
                        def.Matchers.Add(new YaraRegexMatcher(YaraRegex.Compile(body, i, s, wide: false)));
                    }

                    if (wide)
                    {
                        def.Matchers.Add(new YaraRegexMatcher(YaraRegex.Compile(body, i, s, wide: true)) { Wide = true });
                    }

                    return;
            }

            if (base64 || base64Wide)
            {
                foreach (byte[] encoded in YaraTextVariants.Base64(text, alphabet))
                {
                    if (base64)
                    {
                        def.Matchers.Add(new YaraLiteralMatcher(encoded, false));
                    }

                    if (base64Wide)
                    {
                        def.Matchers.Add(new YaraLiteralMatcher(YaraTextVariants.Widen(encoded), false) { Wide = true });
                    }
                }

                return;
            }

            var variants = new List<(byte[] Bytes, bool Wide)>();
            if (useAscii)
            {
                variants.Add((text, false));
            }

            if (wide)
            {
                variants.Add((YaraTextVariants.Widen(text), true));
            }

            foreach ((byte[] bytes, bool isWide) in variants)
            {
                if (!xor)
                {
                    def.Matchers.Add(new YaraLiteralMatcher(bytes, nocase) { Wide = isWide });
                    continue;
                }

                for (int key = xorMin; key <= xorMax; key++)
                {
                    byte[] x = [.. bytes.Select(b => (byte)(b ^ key))];
                    def.Matchers.Add(new YaraLiteralMatcher(x, false) { Wide = isWide });
                }
            }
        }

        // ---- 式 ----

        private static int Precedence(YaraToken t) => t.Kind is YaraTokenKind.Keyword or YaraTokenKind.Symbol ? t.Text switch
        {
            "or" => 1,
            "and" => 2,
            "==" or "!=" or "contains" or "icontains" or "startswith" or "istartswith" or "endswith" or "iendswith" or "iequals" or "matches" => 4,
            "<" or "<=" or ">" or ">=" => 5,
            "|" => 6,
            "^" => 7,
            "&" => 8,
            "<<" or ">>" => 9,
            "+" or "-" => 10,
            "*" or "\\" or "%" => 11,
            _ => 0,
        } : 0;

        private YaraExpr ParseExpression(int minPrecedence)
        {
            if (++_depth > MaxExpressionDepth)
            {
                throw Error("expression is too deeply nested");
            }

            try
            {
                YaraExpr left = ParsePrefix();
                while (true)
                {
                    YaraToken op = Peek();

                    // N% of …
                    if (op.Is("%") && IsPercentOf())
                    {
                        if (_noOf)
                        {
                            return left;
                        }

                        Take();
                        Expect("of");
                        left = ParseOfRest(new YaraQuantifier("percent", left));
                        continue;
                    }

                    if (!_noOf && op.Is("of"))
                    {
                        Take();
                        left = ParseOfRest(new YaraQuantifier("count", left));
                        continue;
                    }

                    int p = Precedence(op);
                    if (p == 0 || p < minPrecedence)
                    {
                        return left;
                    }

                    Take();
                    if (op.Text == "matches")
                    {
                        YaraToken r = Take(regex: true);
                        if (r.Kind != YaraTokenKind.Regex)
                        {
                            throw Unexpected(r, "regular expression");
                        }

                        try
                        {
                            left = new YaraMatchesRegex(left, YaraRegex.CompileForMatches(r.Text, r.Flags));
                        }
                        catch (FormatException e)
                        {
                            throw lexer.Error(e.Message, r.Line);
                        }

                        continue;
                    }

                    YaraExpr right = ParseExpression(p + 1);
                    left = new YaraBinary(op.Text, left, right);
                }
            }
            finally
            {
                _depth--;
            }
        }

        private bool IsPercentOf()
        {
            var mark = lexer.Mark();
            YaraToken? saved = _current;
            try
            {
                // 現在の字句 (%) の次の字句を先読みする。
                return lexer.Next().Is("of");
            }
            finally
            {
                lexer.Reset(mark);
                _current = saved;
            }
        }

        private YaraExpr ParsePrefix()
        {
            YaraToken t = Peek();
            switch (t.Kind)
            {
                case YaraTokenKind.Integer:
                    Take();
                    return new YaraConst(YValue.Int(t.Integer));
                case YaraTokenKind.Float:
                    Take();
                    return new YaraConst(YValue.Float(t.Float));
                case YaraTokenKind.Text:
                    Take();
                    return new YaraConst(YValue.Str(t.Bytes));
                case YaraTokenKind.StringIdentifier:
                    return ParseStringTest();
                case YaraTokenKind.StringCount:
                {
                    Take();
                    YaraStringDef? def = ResolveString(t);
                    (YaraExpr, YaraExpr)? range = Accept("in") ? ParseRange() : null;
                    return new YaraStringCount(def, range);
                }

                case YaraTokenKind.StringOffset:
                case YaraTokenKind.StringLength:
                {
                    Take();
                    YaraStringDef? def = ResolveString(t);
                    YaraExpr? index = null;
                    if (Accept("["))
                    {
                        index = ParseExpression(0);
                        Expect("]");
                    }

                    return new YaraStringOffset(def, index, t.Kind == YaraTokenKind.StringLength);
                }

                case YaraTokenKind.Identifier:
                    return ParsePostfix(ResolveIdentifier(Take()));
            }

            if (t.Kind == YaraTokenKind.Keyword)
            {
                switch (t.Text)
                {
                    case "true":
                        Take();
                        return new YaraConst(YValue.True);
                    case "false":
                        Take();
                        return new YaraConst(YValue.False);
                    case "filesize":
                        Take();
                        return new YaraFileSize();
                    case "entrypoint":
                        Take();
                        return new YaraEntryPoint();
                    case "not":
                        Take();
                        return new YaraUnary("not", ParseExpression(3));
                    case "defined":
                        Take();
                        return new YaraUnary("defined", ParseExpression(3));
                    case "all" or "any" or "none":
                        Take();
                        Expect("of");
                        return ParseOfRest(new YaraQuantifier(t.Text, null));
                    case "for":
                        Take();
                        return ParseFor();
                }

                if (t.Text.StartsWith("int", StringComparison.Ordinal) || t.Text.StartsWith("uint", StringComparison.Ordinal))
                {
                    Take();
                    string n = t.Text;
                    bool signed = !n.StartsWith('u');
                    bool be = n.EndsWith("be", StringComparison.Ordinal);
                    string digits = new([.. n.Where(char.IsAsciiDigit)]);
                    int size = int.Parse(digits, CultureInfo.InvariantCulture) / 8;
                    Expect("(");
                    YaraExpr offset = ParseExpression(0);
                    Expect(")");
                    return new YaraReadInt(size, signed, be, offset);
                }
            }

            if (t.Is("("))
            {
                Take();
                YaraExpr e = ParseExpression(0);
                Expect(")");
                return ParsePostfix(e);
            }

            if (t.Is("-"))
            {
                Take();
                return new YaraUnary("-", ParseExpression(12));
            }

            if (t.Is("~"))
            {
                Take();
                return new YaraUnary("~", ParseExpression(12));
            }

            throw Unexpected(t, "expression");
        }

        private YaraExpr ParseStringTest()
        {
            YaraToken t = Take();
            YaraStringDef? def = ResolveString(t);
            if (Accept("at"))
            {
                return new YaraStringTest(def, ParseExpression(6), null);
            }

            if (Accept("in"))
            {
                return new YaraStringTest(def, null, ParseRange());
            }

            return new YaraStringTest(def, null, null);
        }

        /// <summary>$a・#a・@a・!a の文字列 ($ だけなら for … of の中の文字列で null)。</summary>
        private YaraStringDef? ResolveString(YaraToken t)
        {
            string name = "$" + t.Text[1..];
            if (name == "$")
            {
                if (_forOf == 0)
                {
                    throw lexer.Error($"\"{t.Text}\" is only valid inside a for … of loop", t.Line);
                }

                return null;
            }

            YaraStringDef? def = _rule?.Strings.FirstOrDefault(s => s.Identifier == name);
            if (def is null)
            {
                throw lexer.Error($"undefined string identifier \"{name}\"", t.Line);
            }

            def.Referenced = true;
            return def;
        }

        private (YaraExpr, YaraExpr) ParseRange()
        {
            Expect("(");
            YaraExpr lo = ParseExpression(0);
            Expect("..");
            YaraExpr hi = ParseExpression(0);
            Expect(")");
            return (lo, hi);
        }

        /// <summary>of の後: them、($a, $b*)、(rule1, rule2*)、続く at・in。</summary>
        private YaraExpr ParseOfRest(YaraQuantifier quantifier)
        {
            if (Accept("them"))
            {
                List<YaraStringDef> all = _rule!.Strings;
                if (all.Count == 0)
                {
                    throw Error("no strings defined for \"them\"");
                }

                all.ForEach(s => s.Referenced = true);
                return OfWithPlacement(quantifier, all, null);
            }

            Expect("(");
            if (Peek().Kind == YaraTokenKind.StringIdentifier)
            {
                var strings = new List<YaraStringDef>();
                do
                {
                    strings.AddRange(StringSetItem(Expect(YaraTokenKind.StringIdentifier, "string identifier")));
                }
                while (Accept(","));
                Expect(")");
                return OfWithPlacement(quantifier, strings.Distinct().ToList(), null);
            }

            var rules = new List<YaraRule>();
            do
            {
                YaraToken name = Expect(YaraTokenKind.Identifier, "rule identifier or string identifier");
                if (Accept("*"))
                {
                    IEnumerable<YaraRule> matches = compiler._rules.Where(r => r.Name.StartsWith(name.Text, StringComparison.Ordinal));
                    if (!matches.Any())
                    {
                        throw lexer.Error($"undefined identifier \"{name.Text}*\"", name.Line);
                    }

                    rules.AddRange(matches);
                }
                else
                {
                    rules.Add(compiler._byName.TryGetValue(name.Text, out YaraRule? r) ? r : throw lexer.Error($"undefined identifier \"{name.Text}\"", name.Line));
                }
            }
            while (Accept(","));
            Expect(")");
            return new YaraOf(quantifier, null, rules.Distinct().ToList(), null, null);
        }

        private YaraExpr OfWithPlacement(YaraQuantifier quantifier, List<YaraStringDef> strings, object? _)
        {
            if (Accept("at"))
            {
                return new YaraOf(quantifier, strings, null, ParseExpression(6), null);
            }

            if (Accept("in"))
            {
                return new YaraOf(quantifier, strings, null, null, ParseRange());
            }

            return new YaraOf(quantifier, strings, null, null, null);
        }

        private IEnumerable<YaraStringDef> StringSetItem(YaraToken t)
        {
            if (t.Text.EndsWith('*'))
            {
                string prefix = t.Text[..^1];
                var matched = _rule!.Strings.Where(s => s.Identifier.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                if (matched.Count == 0)
                {
                    throw lexer.Error($"undefined string identifier \"{t.Text}\"", t.Line);
                }

                matched.ForEach(s => s.Referenced = true);
                return matched;
            }

            return [ResolveString(t) ?? throw lexer.Error("\"$\" is not valid here", t.Line)];
        }

        private YaraExpr ParseFor()
        {
            YaraQuantifier quantifier;
            YaraToken q = Peek();
            if (q.Kind == YaraTokenKind.Keyword && q.Text is "all" or "any" or "none")
            {
                Take();
                quantifier = new YaraQuantifier(q.Text, null);
            }
            else
            {
                _noOf = true;
                YaraExpr count;
                try
                {
                    count = ParseExpression(6);
                }
                finally
                {
                    _noOf = false;
                }

                quantifier = Accept("%") ? new YaraQuantifier("percent", count) : new YaraQuantifier("count", count);
            }

            if (Accept("of"))
            {
                List<YaraStringDef> strings;
                if (Accept("them"))
                {
                    strings = _rule!.Strings;
                    strings.ForEach(s => s.Referenced = true);
                }
                else
                {
                    Expect("(");
                    strings = [];
                    do
                    {
                        strings.AddRange(StringSetItem(Expect(YaraTokenKind.StringIdentifier, "string identifier")));
                    }
                    while (Accept(","));
                    Expect(")");
                }

                Expect(":");
                Expect("(");
                _forOf++;
                YaraExpr ofBody;
                try
                {
                    ofBody = ParseExpression(0);
                }
                finally
                {
                    _forOf--;
                }

                Expect(")");
                return new YaraForOf(quantifier, strings.Distinct().ToList(), ofBody);
            }

            var names = new List<string> { Expect(YaraTokenKind.Identifier, "identifier").Text };
            while (Accept(","))
            {
                names.Add(Expect(YaraTokenKind.Identifier, "identifier").Text);
            }

            Expect("in");
            YaraExpr? lo = null, hi = null, collection = null;
            YaraExpr[]? items = null;
            object? elementShape = null, valueShape = null;
            if (Accept("("))
            {
                YaraExpr first = ParseExpression(0);
                if (Accept(".."))
                {
                    lo = first;
                    hi = ParseExpression(0);
                }
                else
                {
                    var list = new List<YaraExpr> { first };
                    while (Accept(","))
                    {
                        list.Add(ParseExpression(0));
                    }

                    items = [.. list];
                }

                Expect(")");
            }
            else
            {
                collection = ParsePostfix(ResolveIdentifier(Expect(YaraTokenKind.Identifier, "identifier")));
                if (collection.Shape is YArray a)
                {
                    elementShape = a.Element;
                }
                else if (collection.Shape is YDictionary d)
                {
                    valueShape = d.Element;
                }
            }

            int expected = collection?.Shape is YDictionary ? 2 : 1;
            if (names.Count != expected)
            {
                throw Error($"wrong number of loop variables (expected {expected})");
            }

            Expect(":");
            Expect("(");
            int saved = _variables.Count;
            _variables.Add((names[0], expected == 2 ? null : elementShape));
            if (expected == 2)
            {
                _variables.Add((names[1], valueShape));
            }

            YaraExpr body;
            try
            {
                body = ParseExpression(0);
            }
            finally
            {
                _variables.RemoveRange(saved, _variables.Count - saved);
            }

            Expect(")");
            return new YaraForIn(quantifier, [.. names], lo, hi, items, collection, body);
        }

        private YaraExpr ResolveIdentifier(YaraToken t)
        {
            for (int i = _variables.Count - 1; i >= 0; i--)
            {
                if (_variables[i].Name == t.Text)
                {
                    return new YaraVariable(t.Text, _variables[i].Shape);
                }
            }

            if (compiler._byName.TryGetValue(t.Text, out YaraRule? rule))
            {
                return new YaraRuleRef(rule);
            }

            if (compiler._externals.ContainsKey(t.Text))
            {
                return new YaraExternalRef(t.Text);
            }

            if (_imports.Contains(t.Text))
            {
                return new YaraModuleRef(t.Text, YaraModules.Schema(t.Text));
            }

            throw lexer.Error($"undefined identifier \"{t.Text}\"", t.Line);
        }

        private YaraExpr ParsePostfix(YaraExpr e)
        {
            while (true)
            {
                if (Peek().Is("."))
                {
                    Take();
                    YaraToken name = Expect(YaraTokenKind.Identifier, "field name");
                    object? shape = null;
                    if (e.Shape is YStruct s)
                    {
                        if (!s.Fields.TryGetValue(name.Text, out YValue field))
                        {
                            throw lexer.Error($"invalid field name \"{name.Text}\"", name.Line);
                        }

                        shape = field.O;
                    }

                    e = new YaraMember(e, name.Text, shape);
                    continue;
                }

                if (Peek().Is("["))
                {
                    Take();
                    YaraExpr index = ParseExpression(0);
                    Expect("]");
                    object? shape = e.Shape switch
                    {
                        YArray a => a.Element,
                        YDictionary d => d.Element,
                        _ => null,
                    };
                    e = new YaraIndex(e, index, shape);
                    continue;
                }

                if (Peek().Is("("))
                {
                    int line = Take().Line;
                    var args = new List<YaraExpr>();
                    if (!Peek().Is(")"))
                    {
                        do
                        {
                            args.Add(ParseExpression(0));
                        }
                        while (Accept(","));
                    }

                    Expect(")");
                    if (e.Shape is YFunction f && !f.Arities.Contains(args.Count))
                    {
                        throw lexer.Error($"wrong arguments for function \"{f.Name}\"", line);
                    }

                    if (e.Shape is not YFunction && e.Shape is not null)
                    {
                        throw lexer.Error("this is not a function", line);
                    }

                    e = new YaraCall(e, [.. args]);
                    continue;
                }

                return e;
            }
        }
    }
}
