using System.Globalization;
using System.Text;
using HexEditor.Core.Templates.Kaitai;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Loading;

/// <summary>テンプレートの読み込みの設定 (TPL-12 の仕様 3 の検索パス)。</summary>
public sealed class TemplateLoadOptions
{
    public static TemplateLoadOptions Default { get; } = new();

    /// <summary>
    /// 検索パスの (2) 利用者のテンプレートフォルダと (3) オンラインリポジトリから入れたテンプレートのフォルダ (この順)。
    /// (1) インポートする側のフォルダと (4) 同梱の標準ライブラリは自動で加わる。
    /// </summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = [];

    public ITemplateFileSystem FileSystem { get; init; } = PhysicalTemplateFileSystem.Instance;

    /// <summary>アプリの版 (<c>min_app_version</c> の判定に使う)。</summary>
    public Version AppVersion { get; init; } = typeof(TemplateLoader).Assembly.GetName().Version ?? new Version(1, 0);
}

/// <summary>読み込んだテンプレート (構文木、型・関数の表、メタ情報)。</summary>
public sealed class TemplateProgram
{
    internal TemplateProgram(string path)
    {
        Path = path;
    }

    /// <summary>メインのファイルのパス (文字列から読み込んだ場合はその名前)。</summary>
    public string Path { get; }

    public TemplateMetadata Metadata { get; } = new();

    public IReadOnlyList<TemplateDiagnostic> Diagnostics => DiagnosticsList;

    public bool HasErrors => DiagnosticsList.Any(d => d.Severity == TemplateSeverity.Error);

    /// <summary>読み込んだファイル (メイン、インポート、インクルード)。</summary>
    public IReadOnlyList<string> Files => FilesList;

    /// <summary>テンプレートで定義した型の完全な名前 (追従・インスペクタで型を選ぶ。TPL-26、TPL-29)。</summary>
    public IEnumerable<string> TypeNames => Types.Keys;

    /// <summary>インスペクタに登録する型 (属性 <c>inspector("表示名")</c>。TPL-29)。名前 → 表示名。</summary>
    public IReadOnlyDictionary<string, string> InspectorTypes => InspectorTypesMap;

    internal List<TemplateDiagnostic> DiagnosticsList { get; } = [];

    internal List<string> FilesList { get; } = [];

    internal List<Stmt> TopLevel { get; } = [];

    internal Dictionary<string, TypeDecl> Types { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, FuncDecl> Functions { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, ConstDecl> Consts { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);

    internal List<string[]> Usings { get; } = [];

    internal Dictionary<string, string> InspectorTypesMap { get; } = new(StringComparer.Ordinal);

    internal string? KaitaiRootType { get; set; }

    /// <summary>HXT に変換した .ksy のテキスト (「.ksy を HXT に変換」。TPL-33 の仕様 4)。</summary>
    public string? ConvertedHxt { get; internal set; }

    internal TypeDecl? FindType(string[] parts, string[] context)
    {
        string name = string.Join("::", parts);
        for (int n = context.Length; n >= 0; n--)
        {
            string key = n == 0 ? name : string.Join("::", context.Take(n)) + "::" + name;
            if (Types.TryGetValue(key, out TypeDecl? t))
            {
                return t;
            }
        }

        foreach (string[] u in Usings)
        {
            if (Types.TryGetValue(string.Join("::", u) + "::" + name, out TypeDecl? t))
            {
                return t;
            }
        }

        return null;
    }

    internal FuncDecl? FindFunction(string[] parts, string[] context)
    {
        string name = string.Join("::", parts);
        for (int n = context.Length; n >= 0; n--)
        {
            string key = n == 0 ? name : string.Join("::", context.Take(n)) + "::" + name;
            if (Functions.TryGetValue(key, out FuncDecl? f))
            {
                return f;
            }
        }

        foreach (string[] u in Usings)
        {
            if (Functions.TryGetValue(string.Join("::", u) + "::" + name, out FuncDecl? f))
            {
                return f;
            }
        }

        return null;
    }

    internal ConstDecl? FindConst(string[] parts, string[] context)
    {
        string name = string.Join("::", parts);
        for (int n = context.Length; n >= 0; n--)
        {
            string key = n == 0 ? name : string.Join("::", context.Take(n)) + "::" + name;
            if (Consts.TryGetValue(key, out ConstDecl? c))
            {
                return c;
            }
        }

        foreach (string[] u in Usings)
        {
            if (Consts.TryGetValue(string.Join("::", u) + "::" + name, out ConstDecl? c))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>候補の名前 (「もしかして」の表示に使う)。</summary>
    internal IEnumerable<string> AllNames => Types.Keys.Concat(Functions.Keys).Concat(Consts.Keys);
}

/// <summary>
/// テンプレートの読み込み (TPL-01、TPL-12、TPL-17、TPL-37)。ファイル全体を読んでから名前を解決するため、型・関数は宣言より
/// 前で使える。インポートは検索パスの中の <c>.hxt</c> / <c>.ksy</c> だけを読み、同じファイルは 1 回だけ読む。
/// </summary>
public static class TemplateLoader
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>ファイルからテンプレートを読み込む。メインのファイルは利用者が選んだものなので検索パスの制限を受けない。</summary>
    public static TemplateProgram LoadFile(string path, TemplateLoadOptions? options = null)
    {
        options ??= TemplateLoadOptions.Default;
        string full = System.IO.Path.GetFullPath(path);
        var program = new TemplateProgram(full);
        byte[] bytes;
        try
        {
            bytes = options.FileSystem.ReadAllBytes(full);
        }
        catch (TemplateFileTooLargeException)
        {
            program.DiagnosticsList.Add(Error(TemplateMessages.TemplateTooLarge, new TemplateSourceLocation(full, 1, 1)));
            return program;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            program.DiagnosticsList.Add(Error(TemplateMessages.ImportNotFound, new TemplateSourceLocation(full, 1, 1), path, ex.Message));
            return program;
        }

        return Load(program, bytes, full, System.IO.Path.GetDirectoryName(full), options);
    }

    /// <summary>文字列からテンプレートを読み込む (テスト、テンプレートエディタの未保存の内容)。</summary>
    public static TemplateProgram LoadText(string text, string fileName = "template.hxt", string? directory = null, TemplateLoadOptions? options = null)
    {
        options ??= TemplateLoadOptions.Default;
        string path = directory is null ? fileName : System.IO.Path.Combine(directory, fileName);
        var program = new TemplateProgram(path);
        return Load(program, Encoding.UTF8.GetBytes(text), path, directory, options);
    }

    /// <summary>UTF-8 のバイト列からテンプレートを読み込む (BOM の有無は問わない)。</summary>
    public static TemplateProgram LoadBytes(byte[] utf8, string fileName = "template.hxt", string? directory = null, TemplateLoadOptions? options = null)
    {
        options ??= TemplateLoadOptions.Default;
        string path = directory is null ? fileName : System.IO.Path.Combine(directory, fileName);
        var program = new TemplateProgram(path);
        return Load(program, utf8, path, directory, options);
    }

    private static TemplateProgram Load(TemplateProgram program, byte[] bytes, string path, string? directory, TemplateLoadOptions options)
    {
        var ctx = new LoadContext(program, options, directory);
        var diags = new DiagnosticBag();
        try
        {
            FileSyntax? file = ctx.ParseFile(bytes, path, diags, isMain: true);
            if (file is not null)
            {
                ApplyPragmas(program, file, diags, options);
                List<Stmt> statements = ctx.ExpandIncludes(file.Statements, path, directory, diags, 0);
                ctx.Register(statements, [], path, directory, diags, topLevel: true);
                program.TopLevel.AddRange(statements);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            diags.Add(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.Internal, [ex.Message], new TemplateSourceLocation(path, 1, 1))
            {
                InternalDetail = ex.ToString(),
            });
        }

        // 字句のエラーと構文のエラーを、ファイルの中の位置の順に並べる
        program.DiagnosticsList.AddRange(diags.Items
            .Select((d, i) => (d, i))
            .OrderBy(x => x.d.Location is { } at ? Math.Max(0, program.FilesList.IndexOf(at.File)) : int.MaxValue)
            .ThenBy(x => x.d.Location?.Line ?? int.MaxValue)
            .ThenBy(x => x.d.Location?.Column ?? int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.d));
        if (diags.ErrorCount >= DiagnosticBag.MaxErrors)
        {
            program.DiagnosticsList.Add(new TemplateDiagnostic(TemplateSeverity.Info, TemplateMessages.TooManyErrors, ["100"]));
        }

        return program;
    }

    private static TemplateDiagnostic Error(string code, TemplateSourceLocation at, params string[] args) =>
        new(TemplateSeverity.Error, code, args, at);

    /// <summary>UTF-8 として復号する (不正なバイト列は位置付きのエラー。TPL-01 の「エラー」)。</summary>
    internal static string? DecodeUtf8(byte[] bytes, string path, DiagnosticBag diags)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return strict.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException ex)
        {
            int bad = start + Math.Max(0, ex.Index);
            // 不正なバイトまでの行・列を数える
            int line = 1;
            int col = 1;
            string prefix = Encoding.UTF8.GetString(bytes, start, Math.Max(0, Math.Min(bad, bytes.Length) - start));
            for (int i = 0; i < prefix.Length; i++)
            {
                if (prefix[i] == '\n' || (prefix[i] == '\r' && (i + 1 >= prefix.Length || prefix[i + 1] != '\n')))
                {
                    line++;
                    col = 1;
                }
                else if (prefix[i] != '\r')
                {
                    col++;
                }
            }

            diags.Error(TemplateMessages.InvalidUtf8, new TemplateSourceLocation(path, line, col));
            return null;
        }
    }

    private static readonly HashSet<string> KnownPragmas =
    [
        "name", "description", "author", "version", "license", "min_app_version", "endian", "bitfield_order", "encoding", "magic",
        "magic_all", "mime", "extensions", "priority", "auto_apply",
    ];

    private static void ApplyPragmas(TemplateProgram program, FileSyntax file, DiagnosticBag diags, TemplateLoadOptions options)
    {
        TemplateMetadata meta = program.Metadata;
        foreach (PragmaNode p in file.Pragmas)
        {
            string name = p.Name;
            string raw = p.Value;
            string value = Unquote(raw);
            void Invalid() => diags.Error(TemplateMessages.InvalidPragma, p.Location, name, raw);
            if (name.StartsWith("name.", StringComparison.Ordinal))
            {
                meta.LocalizedNames[name[5..]] = value;
                continue;
            }

            if (name.StartsWith("description.", StringComparison.Ordinal))
            {
                meta.LocalizedDescriptions[name[12..]] = value;
                continue;
            }

            if (name.StartsWith("limit.", StringComparison.Ordinal))
            {
                string key = name[6..];
                if (key is not ("steps" or "recursion" or "array" or "patterns" or "memory" or "section" or "section_total" or "time"))
                {
                    diags.Warning(TemplateMessages.UnknownPragma, p.Location, name);
                    continue;
                }

                string num = value.Replace("_", string.Empty);
                if (Lexer.TryParseInteger(num, out Int128 iv) && iv >= 0)
                {
                    meta.RequestedLimits[key] = ((double)iv, p.Location);
                }
                else if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) && dv >= 0)
                {
                    meta.RequestedLimits[key] = (dv, p.Location);
                }
                else
                {
                    Invalid();
                }

                continue;
            }

            if (!KnownPragmas.Contains(name))
            {
                diags.Warning(TemplateMessages.UnknownPragma, p.Location, name);
                continue;
            }

            switch (name)
            {
                case "name": meta.Name = value; break;
                case "description": meta.Description = value; break;
                case "author": meta.Author = value; break;
                case "version": meta.Version = value; break;
                case "license": meta.License = value; break;
                case "min_app_version":
                    if (System.Version.TryParse(value.Contains('.') ? value : value + ".0", out Version? v))
                    {
                        meta.MinAppVersion = v;
                        if (options.AppVersion < v)
                        {
                            diags.Error(TemplateMessages.AppVersionRequired, p.Location, value);
                        }
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
                case "endian":
                    if (value is "big" or "little")
                    {
                        meta.BigEndian = value == "big";
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
                case "bitfield_order":
                    if (value is "msb_first" or "lsb_first")
                    {
                        meta.MsbFirst = value == "msb_first";
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
                case "encoding": meta.Encoding = value; break;
                case "magic":
                case "magic_all":
                {
                    int sp = raw.IndexOfAny([' ', '\t']);
                    if (sp > 0 && long.TryParse(raw[..sp], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long off)
                        && HexPattern.TryParse(Unquote(raw[(sp + 1)..].Trim()), out byte[] bytes, out byte[] mask) && bytes.Length > 0)
                    {
                        (name == "magic" ? meta.Magic : meta.MagicAll).Add(new TemplateMagic(off, bytes, mask));
                    }
                    else if (sp > 0 && raw[..sp].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        && Lexer.TryParseInteger(raw[..sp], out Int128 hexOff)
                        && HexPattern.TryParse(Unquote(raw[(sp + 1)..].Trim()), out byte[] b2, out byte[] m2) && b2.Length > 0)
                    {
                        (name == "magic" ? meta.Magic : meta.MagicAll).Add(new TemplateMagic((long)hexOff, b2, m2));
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
                }

                case "mime":
                    meta.Mimes.Add(value);
                    break;
                case "extensions":
                    meta.Extensions.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(e => e.TrimStart('.')));
                    break;
                case "priority":
                    if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int pr))
                    {
                        meta.Priority = pr;
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
                case "auto_apply":
                    if (value is "true" or "false")
                    {
                        meta.AutoApply = value == "true";
                    }
                    else
                    {
                        Invalid();
                    }

                    break;
            }
        }
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            var sb = new StringBuilder();
            for (int i = 1; i < s.Length - 1; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length - 1)
                {
                    i++;
                    sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                }
                else
                {
                    sb.Append(s[i]);
                }
            }

            return sb.ToString();
        }

        return s;
    }

    /// <summary>インポートの読み込みの状態。</summary>
    private sealed class LoadContext(TemplateProgram program, TemplateLoadOptions options, string? mainDirectory)
    {
        private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private long _totalBytes;

        public FileSyntax? ParseFile(byte[] bytes, string path, DiagnosticBag diags, bool isMain)
        {
            if (bytes.Length > TemplateLimits.MaxFileBytes)
            {
                diags.Error(TemplateMessages.TemplateTooLarge, new TemplateSourceLocation(path, 1, 1));
                return null;
            }

            _totalBytes += bytes.Length;
            if (_totalBytes > TemplateLimits.MaxTotalBytes)
            {
                diags.Error(TemplateMessages.TemplatesTooLarge, new TemplateSourceLocation(path, 1, 1));
                return null;
            }

            program.FilesList.Add(path);
            if (path.EndsWith(".ksy", StringComparison.OrdinalIgnoreCase))
            {
                string? hxt = KsyConverter.Convert(bytes, path, diags, out string? rootType);
                if (hxt is null)
                {
                    return null;
                }

                if (isMain)
                {
                    program.Metadata.IsKaitai = true;
                    program.ConvertedHxt = hxt;
                    program.KaitaiRootType = rootType;
                }

                return Parser.Parse(hxt, path, diags);
            }

            string? text = DecodeUtf8(bytes, path, diags);
            return text is null ? null : Parser.Parse(text, path, diags);
        }

        /// <summary><c>#include</c> を、そのファイルの文に置き換える (TPL-12 の仕様 7)。</summary>
        public List<Stmt> ExpandIncludes(List<Stmt> statements, string file, string? directory, DiagnosticBag diags, int depth)
        {
            if (!statements.Any(s => s is IncludeStmt))
            {
                return statements;
            }

            var result = new List<Stmt>();
            foreach (Stmt s in statements)
            {
                if (s is not IncludeStmt inc)
                {
                    result.Add(s);
                    continue;
                }

                if (depth >= 16)
                {
                    diags.Error(TemplateMessages.NestingTooDeep, inc.Location, "16");
                    continue;
                }

                if (!TryResolveRelative(inc.Path, directory, inc.Location, diags, out string full))
                {
                    continue;
                }

                if (!options.FileSystem.FileExists(full))
                {
                    diags.Error(TemplateMessages.ImportNotFound, inc.Location, inc.Path, directory ?? string.Empty);
                    continue;
                }

                FileSyntax? included = ReadAndParse(full, inc.Location, diags);
                if (included is not null)
                {
                    result.AddRange(ExpandIncludes(included.Statements, full, System.IO.Path.GetDirectoryName(full), diags, depth + 1));
                }
            }

            return result;
        }

        private FileSyntax? ReadAndParse(string full, TemplateSourceLocation at, DiagnosticBag diags)
        {
            byte[] bytes;
            try
            {
                if (full.StartsWith(BundledTemplates.Root, StringComparison.Ordinal))
                {
                    BundledTemplates.TryGet(full[BundledTemplates.Root.Length..], out bytes);
                }
                else
                {
                    bytes = options.FileSystem.ReadAllBytes(full);
                }
            }
            catch (TemplateFileTooLargeException)
            {
                diags.Error(TemplateMessages.TemplateTooLarge, at);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diags.Error(TemplateMessages.ImportNotFound, at, full, ex.Message);
                return null;
            }

            return ParseFile(bytes, full, diags, isMain: false);
        }

        /// <summary>型・関数・定数を名前空間に登録し、インポートを読み込む。</summary>
        public void Register(List<Stmt> statements, string[] ns, string file, string? directory, DiagnosticBag diags, bool topLevel)
        {
            foreach (Stmt s in statements)
            {
                switch (s)
                {
                    case DeclStmt d:
                        RegisterDecl(d.Decl, ns, diags);
                        break;
                    case BlockStmt b when topLevel:
                        // struct S { … } s; の定義
                        foreach (Stmt inner in b.Body)
                        {
                            if (inner is DeclStmt id)
                            {
                                RegisterDecl(id.Decl, ns, diags);
                            }
                        }

                        break;
                    case NamespaceStmt n:
                    {
                        string[] inner = [.. ns, .. n.Name];
                        for (int i = 1; i <= inner.Length; i++)
                        {
                            program.Namespaces.Add(string.Join("::", inner.Take(i)));
                        }

                        List<Stmt> body = ExpandIncludes(n.Body, file, directory, diags, 0);
                        Register(body, inner, file, directory, diags, topLevel: true);
                        break;
                    }

                    case UsingNamespaceStmt u:
                        program.Usings.Add(u.Name);
                        break;
                    case ImportStmt i:
                        Import(i, file, directory, diags);
                        break;
                    case PatternDeclStmt p:
                        foreach (Declarator _ in p.Declarators)
                        {
                            if (p.Type.Inline is TypeDecl inline)
                            {
                                RegisterNested(inline, ns, diags);
                            }
                        }

                        break;
                }
            }
        }

        private void RegisterDecl(Decl decl, string[] ns, DiagnosticBag diags)
        {
            decl.Namespace = ns;
            string key = decl.QualifiedName;
            switch (decl)
            {
                case FuncDecl f:
                    if (program.Functions.TryGetValue(key, out FuncDecl? existingF) && !ReferenceEquals(existingF, f))
                    {
                        diags.Error(TemplateMessages.DuplicateDefinition, f.Location, key, existingF.Location.ToString());
                        return;
                    }

                    program.Functions[key] = f;
                    if (f.IsExtern)
                    {
                        program.Metadata.UsesExtensions = true;
                    }

                    break;
                case ConstDecl c:
                    if (program.Consts.TryGetValue(key, out ConstDecl? existingC) && !ReferenceEquals(existingC, c))
                    {
                        diags.Error(TemplateMessages.DuplicateDefinition, c.Location, key, existingC.Location.ToString());
                        return;
                    }

                    program.Consts[key] = c;
                    break;
                case TypeDecl t:
                    if (program.Types.TryGetValue(key, out TypeDecl? existingT) && !ReferenceEquals(existingT, t))
                    {
                        diags.Error(TemplateMessages.DuplicateDefinition, t.Location, key, existingT.Location.ToString());
                        return;
                    }

                    program.Types[key] = t;
                    if (t.Attributes.FirstOrDefault(a => a.Name == "inspector") is { } insp
                        && insp.Args.FirstOrDefault() is LiteralExpr { Value: string label })
                    {
                        program.InspectorTypesMap[key] = label;
                    }

                    RegisterNested(t, ns, diags);
                    break;
            }
        }

        /// <summary>構造体の本体の中で定義した型も登録する。</summary>
        private void RegisterNested(TypeDecl t, string[] ns, DiagnosticBag diags)
        {
            if (t is StructDecl s)
            {
                foreach (Stmt inner in s.Body)
                {
                    if (inner is DeclStmt d)
                    {
                        RegisterDecl(d.Decl, ns, diags);
                    }
                }
            }
        }

        private void Import(ImportStmt i, string file, string? directory, DiagnosticBag diags)
        {
            string? full = ResolveImport(i, directory, diags);
            if (full is null)
            {
                return;
            }

            if (!_loaded.Add(full))
            {
                return; // 同じファイルは 1 回だけ読む (循環も許す)
            }

            FileSyntax? syntax = ReadAndParse(full, i.Location, diags);
            if (syntax is null)
            {
                return;
            }

            // インポートしたファイルのトップレベルの文は実行しない (TPL-12 の仕様 1)。プラグマも無視する (TPL-17 の仕様 1)。
            string? dir = full.StartsWith(BundledTemplates.Root, StringComparison.Ordinal)
                ? BundledTemplates.Root + (System.IO.Path.GetDirectoryName(full[BundledTemplates.Root.Length..]) ?? string.Empty)
                : System.IO.Path.GetDirectoryName(full);
            List<Stmt> statements = ExpandIncludes(syntax.Statements, full, dir, diags, 0);
            Register(statements, [], full, dir, diags, topLevel: true);
        }

        private string? ResolveImport(ImportStmt i, string? directory, DiagnosticBag diags)
        {
            if (i.Path is not null)
            {
                if (directory is not null && directory.StartsWith(BundledTemplates.Root, StringComparison.Ordinal))
                {
                    string rel = System.IO.Path.Combine(directory[BundledTemplates.Root.Length..], i.Path).Replace('\\', '/');
                    if (BundledTemplates.TryGet(rel, out _))
                    {
                        return BundledTemplates.Root + rel;
                    }

                    diags.Error(TemplateMessages.ImportNotFound, i.Location, i.Path, "bundled");
                    return null;
                }

                if (!TryResolveRelative(i.Path, directory, i.Location, diags, out string full))
                {
                    return null;
                }

                if (!options.FileSystem.FileExists(full))
                {
                    diags.Error(TemplateMessages.ImportNotFound, i.Location, i.Path, directory ?? string.Empty);
                    return null;
                }

                return full;
            }

            string[] parts = i.Dotted!;
            var searched = new List<string>();
            foreach (string ext in new[] { ".hxt", ".ksy" })
            {
                string rel = System.IO.Path.Combine(parts) + ext;
                foreach (string root in SearchRoots(directory))
                {
                    string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
                    if (ext == ".hxt")
                    {
                        searched.Add(root);
                    }

                    if (!IsInside(candidate, root) || !LinksStayInside(candidate, root))
                    {
                        continue;
                    }

                    if (options.FileSystem.FileExists(candidate))
                    {
                        return candidate;
                    }
                }

                string bundled = string.Join('/', parts) + ext;
                if (BundledTemplates.TryGet(bundled, out _))
                {
                    return BundledTemplates.Root + bundled;
                }
            }

            searched.Add("bundled");
            diags.Error(TemplateMessages.ImportNotFound, i.Location, string.Join('.', parts), string.Join(", ", searched.Distinct()));
            return null;
        }

        private IEnumerable<string> SearchRoots(string? directory)
        {
            if (directory is not null && !directory.StartsWith(BundledTemplates.Root, StringComparison.Ordinal))
            {
                yield return directory;
            }

            foreach (string p in options.SearchPaths)
            {
                yield return System.IO.Path.GetFullPath(p);
            }
        }

        /// <summary>
        /// 文字列で指定したインポート・インクルードのパスを解決する (TPL-12 の仕様 2、TPL-37 の仕様 2)。絶対パス、UNC、URL、
        /// ドライブ、デバイス名、代替データストリーム、検索パスの外に出るパス、<c>.hxt</c> / <c>.ksy</c> 以外は拒否し、ファイルを開かない。
        /// </summary>
        public bool TryResolveRelative(string spec, string? directory, TemplateSourceLocation at, DiagnosticBag diags, out string full)
        {
            full = string.Empty;
            string? reason = Check(spec);
            if (reason is null && directory is null)
            {
                reason = "no base folder";
            }

            if (reason is null)
            {
                string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory!, spec.Replace('/', '\\')));
                var roots = SearchRoots(directory).Append(mainDirectory).OfType<string>().Select(System.IO.Path.GetFullPath).ToList();
                string? root = roots.FirstOrDefault(r => IsInside(candidate, r));
                if (root is null)
                {
                    reason = $"'{spec}' is outside the template search paths";
                }
                else if (!LinksStayInside(candidate, root, roots))
                {
                    reason = $"'{spec}' goes through a link that points outside the template search paths";
                }
                else
                {
                    full = candidate;
                    return true;
                }
            }

            diags.Error(TemplateMessages.NotPermitted, at, reason);
            return false;
        }

        private static string? Check(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                return "empty path";
            }

            if (spec.Contains('\0') || spec.Contains(':') || spec.StartsWith('/') || spec.StartsWith('\\') || System.IO.Path.IsPathRooted(spec))
            {
                return $"'{spec}' is an absolute path, a URL, a drive or a data stream";
            }

            if (spec.Any(c => c < 0x20 || c is '*' or '?' or '"' or '<' or '>' or '|'))
            {
                return $"'{spec}' contains characters that cannot be used";
            }

            foreach (string part in spec.Split('/', '\\'))
            {
                string stem = part.Split('.')[0].TrimEnd(' ');
                if (DeviceNames.Contains(stem))
                {
                    return $"'{part}' is a device name";
                }

                if (part.Length > 0 && part != "." && part != ".." && (part.EndsWith(' ') || part.EndsWith('.')))
                {
                    return $"'{part}' is not a valid file name";
                }
            }

            string ext = System.IO.Path.GetExtension(spec);
            if (!ext.Equals(".hxt", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ksy", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{spec}' is not a template file (.hxt or .ksy)";
            }

            return null;
        }

        private static bool IsInside(string path, string root)
        {
            string r = root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        private bool LinksStayInside(string candidate, string root, IReadOnlyList<string>? roots = null)
        {
            roots ??= [root];
            string r = root.TrimEnd('\\', '/');
            string rest = candidate[r.Length..].TrimStart('\\', '/');
            string current = r;
            foreach (string part in rest.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, part);
                string? target = options.FileSystem.ResolveLink(current);
                if (target is not null && !roots.Any(x => IsInside(System.IO.Path.GetFullPath(target), x)
                    || string.Equals(System.IO.Path.GetFullPath(target).TrimEnd('\\'), x.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
