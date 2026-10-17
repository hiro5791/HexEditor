using System.Globalization;
using System.Text;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Runtime;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Editing;

/// <summary>構文の色分けの種類 (TPL-31 の仕様 3)。</summary>
public enum TemplateTokenClass
{
    Default,
    Keyword,
    Type,
    Number,
    String,
    Comment,
    Pragma,
    Attribute,
    Function,
}

/// <summary>色分けの範囲 (文字の位置と長さ)。</summary>
public readonly record struct TemplateClassifiedSpan(int Start, int Length, TemplateTokenClass Class);

/// <summary>補完の候補の種類。</summary>
public enum TemplateCompletionKind
{
    Keyword,
    BuiltinType,
    Type,
    Function,
    Member,
    Pattern,
    Attribute,
    Pragma,
    Constant,
}

/// <summary>補完の候補 (TPL-31 の仕様 3。<see cref="Detail"/> は候補の説明: 型・引数など)。</summary>
public sealed record TemplateCompletion(string Label, TemplateCompletionKind Kind, string Detail);

/// <summary>補完の結果: 置き換える範囲 (入力中の語) と候補。</summary>
public sealed record TemplateCompletionResult(int ReplaceStart, int ReplaceLength, IReadOnlyList<TemplateCompletion> Items);

/// <summary>字句の一覧 (エディタの部品の色分けに渡す: 予約語・組み込みの型・属性。TPL-31 の仕様 3)。</summary>
public sealed record TemplateSyntaxLists(IReadOnlyList<string> Keywords, IReadOnlyList<string> Types, IReadOnlyList<string> Attributes);

/// <summary>エディタに出すエラー・警告 (波線と行の印。TPL-31 の仕様 3)。行・列は 1 から。<see cref="Length"/> は波線の長さ。</summary>
public sealed record TemplateEditorDiagnostic(TemplateDiagnostic Diagnostic, int Line, int Column, int Length);

/// <summary>
/// テンプレートエディタの言語の機能 (TPL-31 の仕様 3): 構文の色分け、構文チェック、補完、定義へ移動、参照の検索、書式の整形、
/// コメントの切り替え。画面に依存しないので、エディタの部品 (08 のコードエディタ) が変わってもそのまま使える。
/// </summary>
public static class TemplateLanguageService
{
    /// <summary>標準ライブラリの組み込み関数 (補完に出す。TPL-18)。</summary>
    public static IReadOnlyList<string> StdFunctions { get; } =
    [
        "std::bits::align", "std::bits::offset", "std::bits::popcount", "std::bits::read", "std::bits::reverse",
        "std::compress::brotli", "std::compress::bzip2", "std::compress::gzip", "std::compress::inflate", "std::compress::lz4", "std::compress::lzma",
        "std::compress::lzss", "std::compress::xz", "std::compress::zlib", "std::compress::zstd",
        "std::core::array_index", "std::core::assert", "std::core::base_offset", "std::core::eof", "std::core::get_endian", "std::core::len",
        "std::core::print", "std::core::raw", "std::core::set_endian", "std::core::size", "std::core::template_version", "std::core::warn",
        "std::encoding::base64", "std::encoding::convert", "std::encoding::hex", "std::encoding::rol", "std::encoding::ror", "std::encoding::xor",
        "std::format::format", "std::format::hex", "std::format::size",
        "std::hash::adler32", "std::hash::crc", "std::hash::crc16", "std::hash::crc32", "std::hash::md5", "std::hash::sha1", "std::hash::sha256",
        "std::math::abs", "std::math::align_down", "std::math::align_up", "std::math::ceil", "std::math::clamp", "std::math::floor", "std::math::is_nan",
        "std::math::log2", "std::math::max", "std::math::min", "std::math::pow", "std::math::sqrt",
        "std::mem::compare", "std::mem::find", "std::mem::find_all", "std::mem::read_bytes", "std::mem::read_f32", "std::mem::read_f64",
        "std::mem::read_s16", "std::mem::read_s24", "std::mem::read_s32", "std::mem::read_s64", "std::mem::read_s8", "std::mem::read_string",
        "std::mem::read_u16", "std::mem::read_u24", "std::mem::read_u32", "std::mem::read_u64", "std::mem::read_u8",
        "std::ptr::null_if", "std::ptr::relative_to",
        "std::section::append", "std::section::copy", "std::section::create", "std::section::open_in_tab", "std::section::size",
        "std::string::ends_with", "std::string::find", "std::string::from_bytes", "std::string::length", "std::string::parse_float",
        "std::string::parse_int", "std::string::split", "std::string::starts_with", "std::string::substr", "std::string::to_lower",
        "std::string::to_string", "std::string::to_upper", "std::string::trim",
        "std::sys::demangle", "std::sys::detected_mime", "std::sys::file_extension", "std::sys::file_name",
        "std::time::from_dos", "std::time::from_filetime", "std::time::from_unix", "std::time::to_string",
        "std::format", "std::eof",
    ];

    private static readonly HashSet<string> Keywords = [.. Parser.Reserved];

    /// <summary>色分けに使う字句の一覧 (<see cref="Classify"/> と同じ分類。Monaco の字句解析 (Monarch) に渡す)。</summary>
    public static TemplateSyntaxLists SyntaxLists() => new(
        [.. Keywords.Order(StringComparer.Ordinal)],
        [.. BuiltinTypes.Names.Concat(["pstring", "fixed", "ufixed", "bits", "std::type::magic", "magic", "std::type::Size", "std::type::Bitmask"])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        [.. Evaluator.KnownAttributes.Order(StringComparer.Ordinal)]);

    // ───────────── 色分け ─────────────

    /// <summary>構文の色分け (テキスト全体。コメント・文字列・数値・予約語・型・属性・プラグマ)。</summary>
    public static IReadOnlyList<TemplateClassifiedSpan> Classify(string text)
    {
        var spans = new List<TemplateClassifiedSpan>();
        int i = 0;
        bool inAttribute = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = LineEnd(text, i);
                spans.Add(new(i, end - i, TemplateTokenClass.Comment));
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                spans.Add(new(i, end - i, TemplateTokenClass.Comment));
                i = end;
            }
            else if (c == '#' && IsLineStart(text, i))
            {
                int end = LineEnd(text, i);
                spans.Add(new(i, end - i, TemplateTokenClass.Pragma));
                i = end;
            }
            else if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < text.Length && text[j] != c && text[j] != '\n')
                {
                    j += text[j] == '\\' ? 2 : 1;
                }

                j = Math.Min(text.Length, j + 1);
                spans.Add(new(i, j - i, TemplateTokenClass.String));
                i = j;
            }
            else if (c == 'x' && i + 1 < text.Length && text[i + 1] == '"' && (i == 0 || !IsIdent(text[i - 1])))
            {
                int j = text.IndexOf('"', i + 2);
                j = j < 0 ? text.Length : j + 1;
                spans.Add(new(i, j - i, TemplateTokenClass.String));
                i = j;
            }
            else if (char.IsAsciiDigit(c))
            {
                int j = i;
                while (j < text.Length && (char.IsAsciiLetterOrDigit(text[j]) || text[j] is '_' or '.'))
                {
                    j++;
                }

                spans.Add(new(i, j - i, TemplateTokenClass.Number));
                i = j;
            }
            else if (IsIdentStart(c))
            {
                int j = i;
                while (j < text.Length && IsIdent(text[j]))
                {
                    j++;
                }

                string word = text[i..j];
                TemplateTokenClass cls = inAttribute && Evaluator.KnownAttributes.Contains(word) ? TemplateTokenClass.Attribute
                    : Keywords.Contains(word) ? TemplateTokenClass.Keyword
                    : BuiltinTypes.IsBuiltinName(word) ? TemplateTokenClass.Type
                    : NextNonSpace(text, j) == '(' ? TemplateTokenClass.Function
                    : TemplateTokenClass.Default;
                if (cls != TemplateTokenClass.Default)
                {
                    spans.Add(new(i, j - i, cls));
                }

                i = j;
            }
            else
            {
                if (c == '[' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    inAttribute = true;
                    i += 2;
                    continue;
                }

                if (c == ']' && i + 1 < text.Length && text[i + 1] == ']')
                {
                    inAttribute = false;
                    i += 2;
                    continue;
                }

                i++;
            }
        }

        return spans;
    }

    // ───────────── 構文チェック ─────────────

    /// <summary>
    /// テキストを読み込んで、このファイルのエラー・警告を返す (入力中の構文チェック。300 ms ごとに呼ぶ。TPL-31 の仕様 3)。
    /// <paramref name="path"/> はファイルのパス (インポートの解決に使う)。
    /// </summary>
    public static IReadOnlyList<TemplateEditorDiagnostic> Diagnose(string text, string path, TemplateLoadOptions? options = null)
    {
        TemplateProgram program = Load(text, path, options);
        string fileName = Path.GetFileName(path);
        var lines = SplitLines(text);
        var result = new List<TemplateEditorDiagnostic>();
        foreach (TemplateDiagnostic d in program.Diagnostics)
        {
            if (d.Location is not { } at || !SameFile(at.File, path, fileName))
            {
                continue;
            }

            (int line, int column, int length) = Squiggle(lines, at.Line, at.Column, d.Code);
            result.Add(new TemplateEditorDiagnostic(d, line, column, length));
        }

        return result;
    }

    /// <summary>
    /// 波線の位置と長さ。「; がない」(次の語の位置で報告される) は、前の語の末尾に付ける (利用者が直す場所)。
    /// </summary>
    private static (int Line, int Column, int Length) Squiggle(IReadOnlyList<string> lines, int line, int column, string code)
    {
        if (line < 1 || line > lines.Count)
        {
            int last = Math.Max(1, lines.Count);
            return (last, Math.Max(1, (lines.Count == 0 ? 0 : lines[last - 1].Length)), 1);
        }

        if (code == TemplateMessages.Expected)
        {
            // 直前の空白・改行を飛ばし、前の語の末尾へ
            int l = line;
            int c = column - 1;
            while (l >= 1)
            {
                string s = lines[l - 1];
                int k = Math.Min(c, s.Length) - 1;
                while (k >= 0 && char.IsWhiteSpace(s[k]))
                {
                    k--;
                }

                if (k >= 0)
                {
                    return (l, k + 1, 1);
                }

                l--;
                c = l >= 1 ? lines[l - 1].Length : 0;
            }
        }

        string text = lines[line - 1];
        int start = Math.Clamp(column - 1, 0, Math.Max(0, text.Length - 1));
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && (end == start || IsIdent(text[end]) == IsIdent(text[start])))
        {
            end++;
        }

        return (line, start + 1, Math.Max(1, end - start));
    }

    // ───────────── 補完 ─────────────

    /// <summary>
    /// <paramref name="caret"/> (文字の位置) での補完の候補。<c>hdr.</c> の後ではパターン <c>hdr</c> の型のメンバー、<c>[[</c> の中では属性、
    /// <c>#pragma</c> の後ではプラグマ、それ以外は予約語・型・関数・それまでに宣言したパターン。
    /// </summary>
    public static TemplateCompletionResult Complete(string text, int caret, string path, TemplateLoadOptions? options = null)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int start = caret;
        while (start > 0 && IsIdent(text[start - 1]))
        {
            start--;
        }

        string prefix = text[start..caret];
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (start == 0)
        {
            lineStart = 0;
        }

        string linePrefix = text[lineStart..start];

        // 入力中の行を除いて読み込む (書きかけの行が構文エラーでも、それまでの宣言は使える)
        int lineEnd = LineEnd(text, caret);
        string withoutLine = text[..lineStart] + text[lineEnd..];
        TemplateProgram program = Load(withoutLine, path, options);
        var items = new List<TemplateCompletion>();
        if (linePrefix.TrimStart().StartsWith("#pragma", StringComparison.Ordinal))
        {
            items.AddRange(TemplateLoader.KnownPragmas.Order(StringComparer.Ordinal).Select(p => new TemplateCompletion(p, TemplateCompletionKind.Pragma, "#pragma")));
        }
        else if (start > 0 && text[start - 1] == '.' && MemberChain(text, start - 1) is { Count: > 0 } chain)
        {
            items.AddRange(Members(program, chain));
        }
        else if (InsideAttribute(text, start))
        {
            items.AddRange(Evaluator.KnownAttributes.Order(StringComparer.Ordinal).Select(a => new TemplateCompletion(a, TemplateCompletionKind.Attribute, "[[" + a + "]]")));
        }
        else
        {
            string qualifier = QualifierBefore(text, start);
            if (qualifier.Length > 0)
            {
                string full = qualifier + "::";
                items.AddRange(StdFunctions.Where(f => f.StartsWith(full, StringComparison.Ordinal))
                    .Select(f => f[full.Length..]).Where(f => !f.Contains("::", StringComparison.Ordinal))
                    .Select(f => new TemplateCompletion(f, TemplateCompletionKind.Function,
                        TemplateLibraryReference.Signatures.TryGetValue(full + f, out string? signature) ? signature : full + f + "(…)")));
                items.AddRange(program.Types.Keys.Where(t => t.StartsWith(full, StringComparison.Ordinal))
                    .Select(t => t[full.Length..]).Where(t => !t.Contains("::", StringComparison.Ordinal))
                    .Select(t => new TemplateCompletion(t, TemplateCompletionKind.Type, full + t)));
                items.AddRange(program.Functions.Keys.Where(t => t.StartsWith(full, StringComparison.Ordinal))
                    .Select(t => t[full.Length..]).Where(t => !t.Contains("::", StringComparison.Ordinal))
                    .Select(t => new TemplateCompletion(t, TemplateCompletionKind.Function, full + t + "(…)")));
                items.AddRange(program.Namespaces.Where(n => n.StartsWith(full, StringComparison.Ordinal))
                    .Select(n => n[full.Length..]).Where(n => n.Length > 0 && !n.Contains("::", StringComparison.Ordinal))
                    .Select(n => new TemplateCompletion(n, TemplateCompletionKind.Keyword, "namespace " + full + n)));
                if (qualifier == "std")
                {
                    items.AddRange(StdFunctions.Select(f => f.Split("::")).Where(p => p.Length == 3).Select(p => p[1]).Distinct()
                        .Select(n => new TemplateCompletion(n, TemplateCompletionKind.Keyword, "namespace std::" + n)));
                }
            }
            else
            {
                items.AddRange(Keywords.Order(StringComparer.Ordinal).Select(k => new TemplateCompletion(k, TemplateCompletionKind.Keyword, string.Empty)));
                items.AddRange(BuiltinTypes.Names.Order(StringComparer.Ordinal).Select(t => new TemplateCompletion(t, TemplateCompletionKind.BuiltinType,
                    BuiltinTypes.Find(t) is { } p ? p.Size.ToString(CultureInfo.InvariantCulture) + " byte" : string.Empty)));
                items.AddRange(program.Types.Values.Select(t => new TemplateCompletion(t.QualifiedName, TemplateCompletionKind.Type, TypeKeyword(t) + " " + t.QualifiedName)));
                items.AddRange(program.Functions.Values.Select(f => new TemplateCompletion(f.QualifiedName, TemplateCompletionKind.Function,
                    "fn " + f.QualifiedName + "(" + string.Join(", ", f.Params.Select(p => p.Type.Text + " " + p.Name)) + ")")));
                items.AddRange(program.Consts.Values.Select(c => new TemplateCompletion(c.QualifiedName, TemplateCompletionKind.Constant, "const " + c.Statement.Type.Text)));
                items.AddRange(Patterns(program.TopLevel).Select(p => new TemplateCompletion(p.Name, TemplateCompletionKind.Pattern, p.Type)));
                items.Add(new TemplateCompletion("std", TemplateCompletionKind.Keyword, "namespace std"));
            }
        }

        List<TemplateCompletion> filtered = [.. items
            .Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Label, StringComparer.Ordinal).Select(g => g.First())];
        return new TemplateCompletionResult(start, caret - start, filtered);
    }

    private static string TypeKeyword(TypeDecl t) => t switch
    {
        StructDecl { IsUnion: true } => "union",
        StructDecl => "struct",
        EnumDecl { IsFlags: true } => "flags",
        EnumDecl => "enum",
        BitfieldDecl => "bitfield",
        _ => "using",
    };

    /// <summary><c>a.b.</c> の <c>a</c>、<c>b</c> (<paramref name="dot"/> は最後の <c>.</c> の位置)。</summary>
    private static List<string> MemberChain(string text, int dot)
    {
        var parts = new List<string>();
        int i = dot;
        while (i >= 0 && text[i] == '.')
        {
            int end = i;
            int s = end;
            while (s > 0 && IsIdent(text[s - 1]))
            {
                s--;
            }

            if (s == end)
            {
                break;
            }

            parts.Insert(0, text[s..end]);
            i = s - 1;
        }

        return parts;
    }

    /// <summary>入力中の語の前の <c>a::b::</c> (なければ空)。</summary>
    private static string QualifierBefore(string text, int start)
    {
        var parts = new List<string>();
        int i = start;
        while (i >= 2 && text[i - 1] == ':' && text[i - 2] == ':')
        {
            int end = i - 2;
            int s = end;
            while (s > 0 && IsIdent(text[s - 1]))
            {
                s--;
            }

            if (s == end)
            {
                break;
            }

            parts.Insert(0, text[s..end]);
            i = s;
        }

        return string.Join("::", parts);
    }

    private static bool InsideAttribute(string text, int position)
    {
        int open = text.LastIndexOf("[[", Math.Max(0, position - 1), StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        int close = text.IndexOf("]]", open, StringComparison.Ordinal);
        return close < 0 || close >= position;
    }

    /// <summary><c>hdr.</c> のメンバー: <c>hdr</c> を宣言したパターンの型 (構造体) の中のパターン。</summary>
    private static IEnumerable<TemplateCompletion> Members(TemplateProgram program, List<string> chain)
    {
        IReadOnlyList<(string Name, TypeRef Type, string Text)> scope = PatternsWithTypes(program.TopLevel);
        StructDecl? current = null;
        foreach (string part in chain)
        {
            (string Name, TypeRef Type, string Text)? found = (current is null ? scope : PatternsWithTypes(current.Body)).Cast<(string, TypeRef, string)?>()
                .LastOrDefault(p => p!.Value.Item1 == part);
            if (found is null || program.FindType(found.Value.Type.Parts, []) is not StructDecl sd)
            {
                return [];
            }

            current = sd;
        }

        if (current is null)
        {
            return [];
        }

        return PatternsWithTypes(current.Body).Select(p => new TemplateCompletion(p.Name, TemplateCompletionKind.Member, p.Text));
    }

    private static IEnumerable<(string Name, string Type)> Patterns(IEnumerable<Stmt> body) =>
        PatternsWithTypes(body).Select(p => (p.Name, p.Text));

    /// <summary>文の並びの中で宣言したパターンとローカル変数 (入れ子の if・switch・ループの中も)。</summary>
    private static IReadOnlyList<(string Name, TypeRef Type, string Text)> PatternsWithTypes(IEnumerable<Stmt> body)
    {
        var result = new List<(string, TypeRef, string)>();
        void Walk(Stmt s)
        {
            switch (s)
            {
                case PatternDeclStmt p:
                    foreach (Declarator d in p.Declarators)
                    {
                        if (d.Name is { } n)
                        {
                            string type = p.Type.Text + (d.Array != ArrayKind.None ? "[]" : string.Empty) + (d.IsPointer ? "*" : string.Empty);
                            result.Add((n, p.Type, type));
                        }
                    }

                    break;
                case LocalDeclStmt l:
                    result.Add((l.Name, l.Type, "local " + l.Type.Text));
                    break;
                case BlockStmt b:
                    b.Body.ForEach(Walk);
                    break;
                case IfStmt i:
                    Walk(i.Then);
                    if (i.Else is not null)
                    {
                        Walk(i.Else);
                    }

                    break;
                case SwitchStmt sw:
                    foreach (SwitchCase c in sw.Cases)
                    {
                        c.Body.ForEach(Walk);
                    }

                    break;
                case ForStmt f:
                    Walk(f.Body);
                    break;
                case WhileStmt w:
                    Walk(w.Body);
                    break;
                case NamespaceStmt ns:
                    ns.Body.ForEach(Walk);
                    break;
            }
        }

        foreach (Stmt s in body)
        {
            Walk(s);
        }

        return result;
    }

    // ───────────── 定義へ移動・参照の検索 ─────────────

    /// <summary>
    /// <paramref name="caret"/> の名前 (型・関数・定数・パターン) を定義した位置 (TPL-31 の仕様 3。インポート先のファイルも開く)。
    /// 見つからなければ null。
    /// </summary>
    public static TemplateSourceLocation? FindDefinition(string text, int caret, string path, TemplateLoadOptions? options = null)
    {
        (string word, string[] parts) = QualifiedWordAt(text, caret);
        if (word.Length == 0)
        {
            return null;
        }

        TemplateProgram program = Load(text, path, options);
        if (program.FindType(parts, []) is { } type)
        {
            return Normalize(type.Location, path);
        }

        if (program.FindFunction(parts, []) is { } fn)
        {
            return Normalize(fn.Location, path);
        }

        if (program.FindConst(parts, []) is { } c)
        {
            return Normalize(c.Location, path);
        }

        // 名前空間の中で定義した型の短い名前
        string? key = program.Types.Keys.FirstOrDefault(k => k.EndsWith("::" + word, StringComparison.Ordinal));
        if (key is not null)
        {
            return Normalize(program.Types[key].Location, path);
        }

        // パターン・ローカル変数の宣言 (このファイルの中)
        foreach (Stmt s in program.TopLevel)
        {
            if (FindDeclaration(s, word) is { } at)
            {
                return Normalize(at, path);
            }
        }

        foreach (TypeDecl t in program.Types.Values.OfType<StructDecl>())
        {
            foreach (Stmt s in ((StructDecl)t).Body)
            {
                if (FindDeclaration(s, word) is { } at && SameFile(at.File, path, Path.GetFileName(path)))
                {
                    return Normalize(at, path);
                }
            }
        }

        return null;
    }

    private static TemplateSourceLocation? FindDeclaration(Stmt s, string name) => s switch
    {
        PatternDeclStmt p => p.Declarators.FirstOrDefault(d => d.Name == name) is { } d ? d.Location : null,
        LocalDeclStmt l when l.Name == name => l.Location,
        BlockStmt b => b.Body.Select(x => FindDeclaration(x, name)).FirstOrDefault(x => x is not null),
        IfStmt i => FindDeclaration(i.Then, name) ?? (i.Else is null ? null : FindDeclaration(i.Else, name)),
        SwitchStmt sw => sw.Cases.SelectMany(c => c.Body).Select(x => FindDeclaration(x, name)).FirstOrDefault(x => x is not null),
        ForStmt f => FindDeclaration(f.Body, name),
        WhileStmt w => FindDeclaration(w.Body, name),
        NamespaceStmt ns => ns.Body.Select(x => FindDeclaration(x, name)).FirstOrDefault(x => x is not null),
        _ => null,
    };

    /// <summary>このファイルの中で、名前 <paramref name="caret"/> の語と同じ語の位置 (参照の検索)。</summary>
    public static IReadOnlyList<(int Line, int Column)> FindReferences(string text, int caret)
    {
        (string word, _) = QualifiedWordAt(text, caret);
        var result = new List<(int, int)>();
        if (word.Length == 0)
        {
            return result;
        }

        var commentOrString = new bool[text.Length];
        foreach (TemplateClassifiedSpan span in Classify(text).Where(s => s.Class is TemplateTokenClass.Comment or TemplateTokenClass.String or TemplateTokenClass.Pragma))
        {
            Array.Fill(commentOrString, true, span.Start, Math.Min(span.Length, text.Length - span.Start));
        }

        int line = 1;
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
                continue;
            }

            if (!commentOrString[i] && (i == 0 || !IsIdent(text[i - 1])) && string.CompareOrdinal(text, i, word, 0, word.Length) == 0
                && (i + word.Length >= text.Length || !IsIdent(text[i + word.Length])))
            {
                result.Add((line, i - lineStart + 1));
            }
        }

        return result;
    }

    /// <summary>位置の語と、<c>a::b::C</c> の修飾を含めた名前の部分。</summary>
    private static (string Word, string[] Parts) QualifiedWordAt(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int s = caret;
        while (s > 0 && IsIdent(text[s - 1]))
        {
            s--;
        }

        int e = caret;
        while (e < text.Length && IsIdent(text[e]))
        {
            e++;
        }

        if (s == e)
        {
            return (string.Empty, []);
        }

        string word = text[s..e];
        string qualifier = QualifierBefore(text, s);
        return (word, qualifier.Length == 0 ? [word] : [.. qualifier.Split("::"), word]);
    }

    // ───────────── 書式の整形・コメントの切り替え ─────────────

    /// <summary>
    /// 書式の整形 (インデント 4 文字の空白。TPL-31 の仕様 3): 波かっこの深さで行頭のインデントを付け直し、行末の空白を消す。
    /// 文字列とコメントの中のかっこは数えない。
    /// </summary>
    public static string Format(string text)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder();
        int depth = 0;
        bool inBlockComment = false;
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            int opens = 0;
            int closes = 0;
            int leadingCloses = 0;
            bool leading = true;
            bool inString = false;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (c is '"' or '\'')
                {
                    inString = true;
                    quote = c;
                    leading = false;
                    continue;
                }

                if (c == '{')
                {
                    opens++;
                    leading = false;
                }
                else if (c == '}')
                {
                    if (leading)
                    {
                        leadingCloses++;
                    }
                    else
                    {
                        closes++;
                    }
                }
                else if (!char.IsWhiteSpace(c))
                {
                    leading = false;
                }
            }

            int indent = Math.Max(0, depth - leadingCloses);
            if (line.Length > 0)
            {
                sb.Append(' ', indent * 4).Append(line);
            }

            depth = Math.Max(0, depth - leadingCloses + opens - closes);
            if (n < lines.Length - 1)
            {
                sb.Append(newline);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 行 [<paramref name="firstLine"/>, <paramref name="lastLine"/>] (1 から) のコメントの切り替え: すべての行が <c>//</c> で始まっていれば外し、
    /// そうでなければ付ける。
    /// </summary>
    public static string ToggleComment(string text, int firstLine, int lastLine)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int a = Math.Clamp(firstLine, 1, lines.Length) - 1;
        int b = Math.Clamp(lastLine, 1, lines.Length) - 1;
        bool allCommented = Enumerable.Range(a, b - a + 1).Where(i => lines[i].Trim().Length > 0).All(i => lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal));
        for (int i = a; i <= b; i++)
        {
            string l = lines[i];
            if (l.Trim().Length == 0)
            {
                continue;
            }

            int ws = l.Length - l.TrimStart().Length;
            if (allCommented)
            {
                string rest = l[ws..][2..];
                lines[i] = l[..ws] + (rest.StartsWith(' ') ? rest[1..] : rest);
            }
            else
            {
                lines[i] = l[..ws] + "// " + l[ws..];
            }
        }

        return string.Join(newline, lines);
    }

    /// <summary>新規作成のひな形 (TPL-31 の仕様 5)。</summary>
    public static string NewTemplate(string name) => string.Join("\n",
        "#pragma name \"" + name + "\"",
        "#pragma description \"\"",
        "#pragma author \"\"",
        "#pragma version \"1.0.0\"",
        "#pragma endian little",
        string.Empty,
        "import std.core;",
        string.Empty,
        "struct Header {",
        "};",
        string.Empty,
        "Header header;",
        string.Empty);

    // ───────────── 補助 ─────────────

    private static TemplateProgram Load(string text, string path, TemplateLoadOptions? options)
    {
        string? dir = TemplateCatalog.IsStandardPath(path) ? null : Path.GetDirectoryName(path);
        TemplateLoadOptions o = options ?? (dir is null ? TemplateLoadOptions.Default : new TemplateLoadOptions { SearchPaths = [dir] });
        return TemplateLoader.LoadText(text, Path.GetFileName(path), string.IsNullOrEmpty(dir) ? null : dir, o);
    }

    private static TemplateSourceLocation Normalize(TemplateSourceLocation at, string path) =>
        at.File.Length == 0 || SameFile(at.File, path, Path.GetFileName(path)) ? at with { File = path } : at;

    private static bool SameFile(string file, string path, string fileName) =>
        string.Equals(file, path, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase)
        && (!Path.IsPathRooted(file) || string.Equals(Path.GetFullPath(file), SafeFull(path), StringComparison.OrdinalIgnoreCase));

    private static string SafeFull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static List<string> SplitLines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    private static int LineEnd(string text, int i)
    {
        int end = text.IndexOf('\n', i);
        return end < 0 ? text.Length : (end > 0 && text[end - 1] == '\r' ? end - 1 : end);
    }

    private static bool IsLineStart(string text, int i)
    {
        for (int k = i - 1; k >= 0; k--)
        {
            if (text[k] == '\n')
            {
                return true;
            }

            if (!char.IsWhiteSpace(text[k]))
            {
                return false;
            }
        }

        return true;
    }

    private static char NextNonSpace(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        return i < text.Length ? text[i] : '\0';
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdent(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
