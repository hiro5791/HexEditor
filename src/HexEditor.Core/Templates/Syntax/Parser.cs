using System.Runtime.CompilerServices;

namespace HexEditor.Core.Templates.Syntax;

/// <summary><c>#include "…"</c> の位置 (読み込み時に、そのファイルの文に置き換える。TPL-12 の仕様 7)。</summary>
internal sealed class IncludeStmt : Stmt
{
    public required string Path { get; init; }
}

/// <summary>
/// HXT の構文解析 (TPL-01〜TPL-14)。構文エラーは位置を付けて集め (最大 100 件)、文の単位で回復して解析を続ける
/// (TPL-27 の仕様 1)。入れ子の深さを制限し、.NET のスタックを使い切らない (TPL-37 の仕様 6)。
/// </summary>
internal sealed class Parser
{
    /// <summary>式・ブロックの入れ子の上限。</summary>
    public const int MaxNesting = 256;

    private static readonly HashSet<string> Reserved =
    [
        "struct", "union", "enum", "flags", "bitfield", "fn", "return", "if", "else", "switch", "case", "default", "for", "while",
        "do", "break", "continue", "local", "const", "import", "namespace", "using", "le", "be", "in", "section", "true", "false",
        "null", "this", "parent", "last", "sizeof", "addressof", "endof", "typeof", "until", "eof", "extern",
    ];

    private readonly List<Token> _tokens;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;
    private bool _pendingGreater;
    private int _speculating;
    private int _depth;

    private Parser(List<Token> tokens, string file, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _file = file;
        _diagnostics = diagnostics;
    }

    public static FileSyntax Parse(string text, string file, DiagnosticBag diagnostics)
    {
        List<Token> tokens = Lexer.Tokenize(text, file, diagnostics);
        var parser = new Parser(tokens, file, diagnostics);
        return parser.ParseFile();
    }

    /// <summary>1 つの式だけを解析する (テスト・デバッガの式の評価に使う)。</summary>
    public static Expr? ParseExpression(string text, string file, DiagnosticBag diagnostics)
    {
        List<Token> tokens = Lexer.Tokenize(text, file, diagnostics);
        var parser = new Parser(tokens, file, diagnostics);
        try
        {
            Expr e = parser.Expression();
            if (!parser.At(TokenKind.End))
            {
                parser.Fail(TemplateMessages.UnexpectedToken, parser.Current, parser.Current.Text);
            }

            return e;
        }
        catch (SyntaxError ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return null;
        }
        catch (FatalSyntaxError ex)
        {
            diagnostics.Add(ex.Diagnostic);
            return null;
        }
    }

    private sealed class SyntaxError(TemplateDiagnostic diagnostic) : Exception
    {
        public TemplateDiagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed class FatalSyntaxError(TemplateDiagnostic diagnostic) : Exception
    {
        public TemplateDiagnostic Diagnostic { get; } = diagnostic;
    }

    // ───────────── トークンの操作 ─────────────

    private Token Current => _pendingGreater ? _tokens[_pos] with { Kind = TokenKind.Greater, Text = ">" } : _tokens[_pos];

    private Token PeekToken(int ahead) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];

    private bool At(TokenKind kind) => Current.Kind == kind;

    private bool AtIdent(string text) => Current.Kind == TokenKind.Identifier && Current.Text == text;

    private TemplateSourceLocation Loc(Token t) => new(_file, t.Line, t.Column);

    private Token Advance()
    {
        Token t = Current;
        if (_pendingGreater)
        {
            _pendingGreater = false;
            _pos++;
            return t;
        }

        if (_pos < _tokens.Count - 1)
        {
            _pos++;
        }

        return t;
    }

    private bool Accept(TokenKind kind)
    {
        if (At(kind))
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool AcceptIdent(string text)
    {
        if (AtIdent(text))
        {
            Advance();
            return true;
        }

        return false;
    }

    private Token Expect(TokenKind kind, string text)
    {
        if (kind == TokenKind.Greater && !_pendingGreater && Current.Kind == TokenKind.Shr)
        {
            _pendingGreater = true;
            return _tokens[_pos];
        }

        if (kind == TokenKind.Greater && !_pendingGreater && Current.Kind == TokenKind.GreaterEq)
        {
            Fail(TemplateMessages.Expected, Current, text);
        }

        if (!At(kind))
        {
            Fail(TemplateMessages.Expected, Current, text);
        }

        return Advance();
    }

    private void ExpectSemicolon()
    {
        if (Accept(TokenKind.Semicolon))
        {
            return;
        }

        // ';' がない: 直前のトークンの末尾の位置で報告し、続きを解析する (TC-TPL-27-01)
        Token prev = _pos > 0 ? _tokens[_pos - 1] : Current;
        var at = new TemplateSourceLocation(_file, prev.Line, prev.Column + Math.Max(1, prev.Text.Length));
        if (_speculating > 0)
        {
            throw new SyntaxError(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.Expected, [";"], at));
        }

        _diagnostics.Add(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.Expected, [";"], at));
    }

    private string ExpectIdent()
    {
        if (Current.Kind != TokenKind.Identifier)
        {
            Fail(TemplateMessages.Expected, Current, "identifier");
        }

        return Advance().Text;
    }

    private string ExpectName()
    {
        if (Current.Kind != TokenKind.Identifier || Reserved.Contains(Current.Text))
        {
            Fail(TemplateMessages.Expected, Current, "identifier");
        }

        return Advance().Text;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Fail(string code, Token at, params string[] args) =>
        throw new SyntaxError(new TemplateDiagnostic(TemplateSeverity.Error, code, args, Loc(at)));

    private void Enter(Token at)
    {
        if (++_depth > MaxNesting || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new FatalSyntaxError(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.NestingTooDeep,
                [MaxNesting.ToString(System.Globalization.CultureInfo.InvariantCulture)], Loc(at)));
        }
    }

    private void Leave() => _depth--;

    /// <summary>失敗したら位置を戻す試しの解析。</summary>
    private bool TrySpeculate<T>(Func<T> parse, out T result)
    {
        int save = _pos;
        bool pending = _pendingGreater;
        int depth = _depth;
        _speculating++;
        try
        {
            result = parse();
            return true;
        }
        catch (SyntaxError)
        {
            _pos = save;
            _pendingGreater = pending;
            _depth = depth;
            result = default!;
            return false;
        }
        finally
        {
            _speculating--;
        }
    }

    // ───────────── ファイル・文 ─────────────

    private FileSyntax ParseFile()
    {
        var file = new FileSyntax { Path = _file };
        try
        {
            while (!At(TokenKind.End))
            {
                if (_diagnostics.Full)
                {
                    break;
                }

                if (At(TokenKind.Pragma))
                {
                    Token p = Advance();
                    string body = p.StringValue ?? string.Empty;
                    int space = body.IndexOfAny([' ', '\t']);
                    string name = space < 0 ? body : body[..space];
                    string value = space < 0 ? string.Empty : body[(space + 1)..].Trim();
                    file.Pragmas.Add(new PragmaNode { Name = name, Value = value, Location = Loc(p) });
                    continue;
                }

                if (At(TokenKind.RBrace))
                {
                    Token t = Advance();
                    _diagnostics.Error(TemplateMessages.UnexpectedToken, Loc(t), "}");
                    continue;
                }

                Stmt? s = StatementWithRecovery(topLevel: true);
                if (s is not null)
                {
                    file.Statements.Add(s);
                }
            }
        }
        catch (FatalSyntaxError ex)
        {
            _diagnostics.Add(ex.Diagnostic);
        }

        if (_diagnostics.ErrorCount >= DiagnosticBag.MaxErrors)
        {
            // 101 件目以降は捨てる (TC-TPL-27-01)
        }

        return file;
    }

    private Stmt? StatementWithRecovery(bool topLevel)
    {
        int start = _pos;
        try
        {
            return Statement(topLevel);
        }
        catch (SyntaxError ex)
        {
            _diagnostics.Add(ex.Diagnostic);
            Synchronize(ex.Diagnostic.Location?.Line ?? Current.Line, start);
            return null;
        }
    }

    /// <summary>エラーの後、次の文の始まりまで読み飛ばす。</summary>
    private void Synchronize(int errorLine, int start)
    {
        _pendingGreater = false;
        if (_pos == start)
        {
            Advance();
        }

        while (!At(TokenKind.End))
        {
            if (At(TokenKind.Semicolon))
            {
                Advance();
                return;
            }

            if (At(TokenKind.RBrace) || At(TokenKind.Pragma) || At(TokenKind.Include))
            {
                return;
            }

            if (Current.Line > errorLine)
            {
                return;
            }

            Advance();
        }
    }

    private List<Stmt> BlockBody()
    {
        Token open = Expect(TokenKind.LBrace, "{");
        Enter(open);
        var body = new List<Stmt>();
        while (!At(TokenKind.RBrace) && !At(TokenKind.End))
        {
            if (_diagnostics.Full)
            {
                break;
            }

            if (At(TokenKind.Pragma))
            {
                Advance();
                continue;
            }

            Stmt? s = StatementWithRecovery(topLevel: false);
            if (s is not null)
            {
                body.Add(s);
            }
        }

        Expect(TokenKind.RBrace, "}");
        Leave();
        return body;
    }

    private Stmt Statement(bool topLevel)
    {
        Token t = Current;
        TemplateSourceLocation at = Loc(t);
        switch (t.Kind)
        {
            case TokenKind.Include:
                Advance();
                return new IncludeStmt { Path = t.StringValue ?? string.Empty, Location = at };
            case TokenKind.Semicolon:
                Advance();
                return new EmptyStmt { Location = at };
            case TokenKind.LBrace:
                return new BlockStmt { Body = BlockBody(), Location = at };
            case TokenKind.Identifier:
                break;
            default:
                return ExpressionStatement();
        }

        switch (t.Text)
        {
            case "import":
                return Import();
            case "namespace":
            {
                Advance();
                string[] name = QualifiedName();
                return new NamespaceStmt { Name = name, Body = BlockBody(), Location = at };
            }

            case "using":
                return Using();
            case "struct":
            case "union":
                return StructStatement();
            case "enum":
            case "flags":
                return new DeclStmt { Decl = EnumDeclaration(), Location = at };
            case "bitfield":
                return BitfieldStatement();
            case "fn":
                if (PeekToken(1).Kind == TokenKind.Identifier)
                {
                    return new DeclStmt { Decl = Function(isExtern: false), Location = at };
                }

                return ExpressionStatement();
            case "extern":
                Advance();
                if (!AtIdent("fn"))
                {
                    Fail(TemplateMessages.Expected, Current, "fn");
                }

                return new DeclStmt { Decl = Function(isExtern: true), Location = at };
            case "const":
            {
                Advance();
                LocalDeclStmt local = LocalRest(at, isConst: true, t.DocComment);
                return new DeclStmt { Decl = new ConstDecl { Name = local.Name, Statement = local, Location = at }, Location = at };
            }

            case "local":
                Advance();
                return LocalRest(at, isConst: false, t.DocComment);
            case "section":
            {
                Advance();
                string name = ExpectName();
                ExpectSemicolon();
                return new SectionDeclStmt { Name = name, Location = at };
            }

            case "if":
                return If();
            case "switch":
                return Switch();
            case "for":
                return For();
            case "while":
            {
                Advance();
                Expect(TokenKind.LParen, "(");
                Expr cond = Expression();
                Expect(TokenKind.RParen, ")");
                Stmt body = SubStatement();
                return new WhileStmt { Condition = cond, Body = body, Location = at };
            }

            case "do":
            {
                Advance();
                Stmt body = SubStatement();
                if (!AcceptIdent("while"))
                {
                    Fail(TemplateMessages.Expected, Current, "while");
                }

                Expect(TokenKind.LParen, "(");
                Expr cond = Expression();
                Expect(TokenKind.RParen, ")");
                ExpectSemicolon();
                return new WhileStmt { Condition = cond, Body = body, IsDoWhile = true, Location = at };
            }

            case "break":
                Advance();
                ExpectSemicolon();
                return new BreakStmt { Location = at };
            case "continue":
                Advance();
                ExpectSemicolon();
                return new ContinueStmt { Location = at };
            case "return":
            {
                Advance();
                Expr? value = At(TokenKind.Semicolon) ? null : Expression();
                ExpectSemicolon();
                return new ReturnStmt { Value = value, Location = at };
            }

            case "le":
            case "be":
            {
                Advance();
                TypeRef type = TypeReference();
                return PatternRest(at, type, t.Text == "be", t.DocComment);
            }

            case "padding":
                if (PeekToken(1).Kind == TokenKind.LBracket)
                {
                    TypeRef type = new() { Parts = ["padding"], Location = at };
                    Advance();
                    Expect(TokenKind.LBracket, "[");
                    Expr count = Expression();
                    Expect(TokenKind.RBracket, "]");
                    List<AttributeNode> attrs = Attributes();
                    ExpectSemicolon();
                    return new PatternDeclStmt
                    {
                        Type = type,
                        Declarators = [new Declarator { Name = null, Array = ArrayKind.Count, ArrayExpr = count, Attributes = attrs, Location = at }],
                        Location = at,
                        DocComment = t.DocComment,
                    };
                }

                break;
        }

        // 宣言か式か: 型の参照の後に宣言子 (名前、*、(*) が続けば宣言
        if (TrySpeculate(() =>
            {
                TypeRef type = TypeReference();
                if (!(Current.Kind == TokenKind.Identifier && Current.Text != "in")
                    && !(At(TokenKind.Star) && IsPointerDeclarator())
                    && !(At(TokenKind.LParen) && PeekToken(1).Kind == TokenKind.Star))
                {
                    Fail(TemplateMessages.UnexpectedToken, Current, Current.Text);
                }

                return type;
            }, out TypeRef declType))
        {
            return PatternRest(at, declType, null, t.DocComment);
        }

        return ExpressionStatement();
    }

    /// <summary><c>* 名前 [配列] :</c> の形か (掛け算の式と区別する)。</summary>
    private bool IsPointerDeclarator()
    {
        int i = 1;
        if (PeekToken(i).Kind != TokenKind.Identifier)
        {
            return false;
        }

        i++;
        int depth = 0;
        while (true)
        {
            Token t = PeekToken(i);
            if (t.Kind == TokenKind.End)
            {
                return false;
            }

            if (t.Kind == TokenKind.LBracket)
            {
                depth++;
            }
            else if (t.Kind == TokenKind.RBracket)
            {
                depth--;
            }
            else if (depth == 0)
            {
                return t.Kind == TokenKind.Colon;
            }

            i++;
        }
    }

    private Stmt SubStatement()
    {
        Enter(Current);
        try
        {
            return Statement(topLevel: false);
        }
        finally
        {
            Leave();
        }
    }

    private Stmt ExpressionStatement()
    {
        Token t = Current;
        if (t.Kind is TokenKind.RParen or TokenKind.RBracket or TokenKind.Comma or TokenKind.Colon or TokenKind.At
            or TokenKind.Semicolon or TokenKind.End)
        {
            Fail(TemplateMessages.UnexpectedToken, t, t.Text);
        }

        Expr e = Expression();
        ExpectSemicolon();
        return new ExprStmt { Expr = e, Location = Loc(t) };
    }

    private Stmt Import()
    {
        Token t = Advance();
        if (At(TokenKind.String))
        {
            string path = Advance().StringValue ?? string.Empty;
            ExpectSemicolon();
            return new ImportStmt { Path = path, Location = Loc(t) };
        }

        var parts = new List<string> { ExpectIdent() };
        while (Accept(TokenKind.Dot))
        {
            parts.Add(ExpectIdent());
        }

        ExpectSemicolon();
        return new ImportStmt { Dotted = [.. parts], Location = Loc(t) };
    }

    private Stmt Using()
    {
        Token t = Advance();
        if (AcceptIdent("namespace"))
        {
            string[] ns = QualifiedName();
            ExpectSemicolon();
            return new UsingNamespaceStmt { Name = ns, Location = Loc(t) };
        }

        string name = ExpectName();
        Expect(TokenKind.Assign, "=");
        TypeRef target = TypeReference();
        List<AttributeNode> attrs = Attributes();
        ExpectSemicolon();
        return new DeclStmt
        {
            Decl = new AliasDecl { Name = name, Target = target, Attributes = attrs, Location = Loc(t), DocComment = t.DocComment },
            Location = Loc(t),
        };
    }

    private string[] QualifiedName()
    {
        var parts = new List<string> { ExpectIdent() };
        while (Accept(TokenKind.ColonColon))
        {
            parts.Add(ExpectIdent());
        }

        return [.. parts];
    }

    // ───────────── 型の定義 ─────────────

    private Stmt StructStatement()
    {
        Token t = Current;
        StructDecl decl = StructDeclaration();
        if (decl.Name.Length == 0 || Current.Kind == TokenKind.Identifier)
        {
            // 無名の構造体のパターン、または struct S { … } s;
            var type = new TypeRef { Inline = decl, Location = Loc(t), Parts = decl.Name.Length > 0 ? [decl.Name] : [] };
            if (decl.Name.Length > 0)
            {
                // 名前付きの定義と宣言を同時に行う
                return new BlockStmt
                {
                    Body =
                    [
                        new DeclStmt { Decl = decl, Location = Loc(t) },
                        PatternRest(Loc(t), new TypeRef { Parts = [decl.Name], Location = Loc(t) }, null, null),
                    ],
                    Location = Loc(t),
                };
            }

            return PatternRest(Loc(t), type, null, t.DocComment);
        }

        ExpectSemicolon();
        return new DeclStmt { Decl = decl, Location = Loc(t) };
    }

    private StructDecl StructDeclaration()
    {
        Token t = Advance();
        bool isUnion = t.Text == "union";
        string name = Current.Kind == TokenKind.Identifier && !Reserved.Contains(Current.Text) ? Advance().Text : string.Empty;
        var typeParams = new List<TypeParam>();
        if (Accept(TokenKind.Less))
        {
            do
            {
                bool isValue = AcceptIdent("auto");
                typeParams.Add(new TypeParam { Name = ExpectName(), IsValue = isValue });
            }
            while (Accept(TokenKind.Comma));
            Expect(TokenKind.Greater, ">");
        }

        List<Param> parameters = At(TokenKind.LParen) ? Parameters() : [];
        TypeRef? baseType = null;
        if (Accept(TokenKind.Colon))
        {
            baseType = TypeReference();
        }

        List<Stmt> body = BlockBody();
        List<AttributeNode> attrs = Attributes();
        return new StructDecl
        {
            Name = name,
            IsUnion = isUnion,
            TypeParams = typeParams,
            Params = parameters,
            Base = baseType,
            Body = body,
            Attributes = attrs,
            Location = Loc(t),
            DocComment = t.DocComment,
        };
    }

    private List<Param> Parameters()
    {
        Expect(TokenKind.LParen, "(");
        var list = new List<Param>();
        if (!At(TokenKind.RParen))
        {
            do
            {
                bool isRef = AcceptIdent("ref");
                TypeRef type = TypeReference();
                string name = ExpectName();
                list.Add(new Param { Type = type, Name = name, IsRef = isRef });
            }
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.RParen, ")");
        return list;
    }

    private EnumDecl EnumDeclaration()
    {
        Token t = Advance();
        bool isFlags = t.Text == "flags";
        string name = ExpectName();
        TypeRef? baseType = Accept(TokenKind.Colon) ? TypeReference() : null;
        Expect(TokenKind.LBrace, "{");
        var members = new List<EnumMember>();
        while (!At(TokenKind.RBrace) && !At(TokenKind.End))
        {
            Token m = Current;
            string memberName = ExpectIdent();
            Expr? value = null;
            Expr? rangeEnd = null;
            if (Accept(TokenKind.Assign))
            {
                value = Expression(Prec.Ternary);
                if (Accept(TokenKind.Ellipsis))
                {
                    rangeEnd = Expression(Prec.Ternary);
                }
            }

            members.Add(new EnumMember { Name = memberName, Value = value, RangeEnd = rangeEnd, DocComment = m.DocComment, Location = Loc(m) });
            if (!Accept(TokenKind.Comma))
            {
                break;
            }
        }

        Expect(TokenKind.RBrace, "}");
        List<AttributeNode> attrs = Attributes();
        ExpectSemicolon();
        return new EnumDecl
        {
            Name = name,
            IsFlags = isFlags,
            BaseType = baseType,
            Members = members,
            Attributes = attrs,
            Location = Loc(t),
            DocComment = t.DocComment,
        };
    }

    private Stmt BitfieldStatement()
    {
        Token t = Current;
        BitfieldDecl decl = BitfieldDeclaration();
        if (decl.Name.Length == 0)
        {
            return PatternRest(Loc(t), new TypeRef { Inline = decl, Location = Loc(t) }, null, t.DocComment);
        }

        ExpectSemicolon();
        return new DeclStmt { Decl = decl, Location = Loc(t) };
    }

    private BitfieldDecl BitfieldDeclaration()
    {
        Token t = Advance();
        string name = Current.Kind == TokenKind.Identifier && !Reserved.Contains(Current.Text) ? Advance().Text : string.Empty;
        TypeRef? baseType = Accept(TokenKind.Colon) ? TypeReference() : null;
        Token open = Expect(TokenKind.LBrace, "{");
        Enter(open);
        var members = new List<BitfieldMember>();
        while (!At(TokenKind.RBrace) && !At(TokenKind.End))
        {
            Token m = Current;
            try
            {
                TypeRef? type = null;
                string? memberName;
                if (PeekToken(1).Kind == TokenKind.Colon)
                {
                    memberName = ExpectIdent();
                }
                else
                {
                    type = TypeReference();
                    memberName = ExpectIdent();
                }

                if (memberName == "padding")
                {
                    memberName = null;
                }

                Expect(TokenKind.Colon, ":");
                Expr width = Expression(Prec.Ternary);
                List<AttributeNode> attrs = Attributes();
                ExpectSemicolon();
                members.Add(new BitfieldMember { Type = type, Name = memberName, Width = width, Attributes = attrs, DocComment = m.DocComment, Location = Loc(m) });
            }
            catch (SyntaxError ex)
            {
                _diagnostics.Add(ex.Diagnostic);
                Synchronize(ex.Diagnostic.Location?.Line ?? Current.Line, _pos);
            }
        }

        Expect(TokenKind.RBrace, "}");
        Leave();
        List<AttributeNode> declAttrs = Attributes();
        return new BitfieldDecl
        {
            Name = name,
            BaseType = baseType,
            Members = members,
            Attributes = declAttrs,
            Location = Loc(t),
            DocComment = t.DocComment,
        };
    }

    private FuncDecl Function(bool isExtern)
    {
        Token t = Advance(); // fn
        string name = ExpectName();
        List<Param> parameters = Parameters();
        TypeRef? ret = Accept(TokenKind.Arrow) ? TypeReference() : null;
        BlockStmt? body = null;
        if (isExtern)
        {
            ExpectSemicolon();
        }
        else
        {
            Token b = Current;
            body = new BlockStmt { Body = BlockBody(), Location = Loc(b) };
        }

        return new FuncDecl
        {
            Name = name,
            Params = parameters,
            ReturnType = ret,
            Body = body,
            IsExtern = isExtern,
            Location = Loc(t),
            DocComment = t.DocComment,
        };
    }

    // ───────────── 宣言 ─────────────

    private LocalDeclStmt LocalRest(TemplateSourceLocation at, bool isConst, string? doc)
    {
        if (AtIdent("local"))
        {
            Advance();
        }

        TypeRef type = TypeReference();
        string name = ExpectName();
        Expr? init = null;
        if (Accept(TokenKind.Assign))
        {
            init = Expression(Prec.Ternary);
        }

        List<AttributeNode> attrs = Attributes();
        ExpectSemicolon();
        return new LocalDeclStmt { IsConst = isConst, Type = type, Name = name, Init = init, Attributes = attrs, Location = at, DocComment = doc };
    }

    private PatternDeclStmt PatternRest(TemplateSourceLocation at, TypeRef type, bool? bigEndian, string? doc)
    {
        var declarators = new List<Declarator>();
        do
        {
            declarators.Add(Declarator());
        }
        while (Accept(TokenKind.Comma));
        ExpectSemicolon();
        return new PatternDeclStmt { BigEndianKeyword = bigEndian, Type = type, Declarators = declarators, Location = at, DocComment = doc };
    }

    private Declarator Declarator()
    {
        Token t = Current;
        bool isPointer = false;
        bool pointerToArray = false;
        string? name;
        if (Accept(TokenKind.Star))
        {
            isPointer = true;
            name = ExpectName();
        }
        else if (At(TokenKind.LParen) && PeekToken(1).Kind == TokenKind.Star)
        {
            Advance();
            Advance();
            isPointer = true;
            pointerToArray = true;
            name = ExpectName();
            Expect(TokenKind.RParen, ")");
        }
        else
        {
            name = ExpectIdent();
        }

        ArrayKind array = ArrayKind.None;
        Expr? arrayExpr = null;
        if (At(TokenKind.LBracket) && PeekToken(1).Kind != TokenKind.LBracket)
        {
            Advance();
            if (Accept(TokenKind.RBracket))
            {
                array = ArrayKind.Terminated;
            }
            else
            {
                if (AtIdent("eof") && PeekToken(1).Kind == TokenKind.RBracket)
                {
                    Advance();
                    array = ArrayKind.Eof;
                }
                else if ((AtIdent("while") || AtIdent("until")) && PeekToken(1).Kind == TokenKind.LParen)
                {
                    array = Advance().Text == "while" ? ArrayKind.While : ArrayKind.Until;
                    Expect(TokenKind.LParen, "(");
                    arrayExpr = Expression();
                    Expect(TokenKind.RParen, ")");
                }
                else
                {
                    array = ArrayKind.Count;
                    arrayExpr = Expression();
                }

                Expect(TokenKind.RBracket, "]");
            }
        }

        TypeRef? pointerType = null;
        Expr? bitWidth = null;
        string? pointerSection = null;
        if (Accept(TokenKind.Colon))
        {
            if (isPointer)
            {
                pointerType = TypeReference();
                if (AcceptIdent("in"))
                {
                    pointerSection = ExpectName();
                }
            }
            else
            {
                bitWidth = Expression(Prec.Ternary);
            }
        }
        else if (isPointer)
        {
            Fail(TemplateMessages.Expected, Current, ":");
        }

        Expr? placement = null;
        string? section = null;
        if (Accept(TokenKind.At))
        {
            placement = Expression(Prec.Ternary);
            if (AcceptIdent("in"))
            {
                section = ExpectName();
            }
        }

        List<AttributeNode> attrs = Attributes();
        return new Declarator
        {
            Name = name,
            IsPointer = isPointer,
            PointerToArray = pointerToArray,
            PointerType = pointerType,
            PointerSection = pointerSection,
            Array = array,
            ArrayExpr = arrayExpr,
            BitWidth = bitWidth,
            Placement = placement,
            PlacementSection = section,
            Attributes = attrs,
            Location = Loc(t),
        };
    }

    private List<AttributeNode> Attributes()
    {
        var list = new List<AttributeNode>();
        while (At(TokenKind.LBracket) && PeekToken(1).Kind == TokenKind.LBracket)
        {
            Advance();
            Advance();
            if (!At(TokenKind.RBracket))
            {
                do
                {
                    Token n = Current;
                    string name = ExpectIdent();
                    var args = new List<Expr>();
                    if (Accept(TokenKind.LParen))
                    {
                        if (!At(TokenKind.RParen))
                        {
                            do
                            {
                                args.Add(Expression(Prec.Ternary));
                            }
                            while (Accept(TokenKind.Comma));
                        }

                        Expect(TokenKind.RParen, ")");
                    }

                    list.Add(new AttributeNode { Name = name, Args = args, Location = Loc(n) });
                }
                while (Accept(TokenKind.Comma));
            }

            Expect(TokenKind.RBracket, "]]");
            Expect(TokenKind.RBracket, "]]");
        }

        return list;
    }

    // ───────────── 型の参照 ─────────────

    private TypeRef TypeReference()
    {
        Token t = Current;
        if (AtIdent("struct") || AtIdent("union"))
        {
            StructDecl inline = StructDeclaration();
            return new TypeRef { Inline = inline, Location = Loc(t), Parts = inline.Name.Length > 0 ? [inline.Name] : [] };
        }

        if (AtIdent("bitfield"))
        {
            BitfieldDecl inline = BitfieldDeclaration();
            return new TypeRef { Inline = inline, Location = Loc(t) };
        }

        if (Current.Kind != TokenKind.Identifier || (Reserved.Contains(Current.Text) && Current.Text is not ("eof")))
        {
            Fail(TemplateMessages.Expected, Current, "type");
        }

        string[] parts = QualifiedName();
        var typeArgs = new List<TypeArg>();
        if (At(TokenKind.Less))
        {
            Advance();
            Enter(t);
            do
            {
                typeArgs.Add(TypeArgument());
            }
            while (Accept(TokenKind.Comma));
            Expect(TokenKind.Greater, ">");
            Leave();
        }

        var args = new List<Expr>();
        if (At(TokenKind.LParen) && PeekToken(1).Kind != TokenKind.Star)
        {
            Advance();
            if (!At(TokenKind.RParen))
            {
                do
                {
                    args.Add(Expression(Prec.Ternary));
                }
                while (Accept(TokenKind.Comma));
            }

            Expect(TokenKind.RParen, ")");
        }

        return new TypeRef { Parts = parts, TypeArgs = typeArgs, Args = args, Location = Loc(t) };
    }

    private TypeArg TypeArgument()
    {
        if (Current.Kind == TokenKind.Identifier && !Reserved.Contains(Current.Text)
            && TrySpeculate(() =>
            {
                TypeRef type = TypeReference();
                if (!At(TokenKind.Comma) && !At(TokenKind.Greater) && !At(TokenKind.Shr))
                {
                    Fail(TemplateMessages.UnexpectedToken, Current, Current.Text);
                }

                return type;
            }, out TypeRef typeArg))
        {
            return new TypeArg { Type = typeArg };
        }

        return new TypeArg { Value = Expression(Prec.Shift) };
    }

    // ───────────── 制御構文 ─────────────

    private Stmt If()
    {
        Token t = Advance();
        Expect(TokenKind.LParen, "(");
        Expr cond = Expression();
        Expect(TokenKind.RParen, ")");
        Stmt then = SubStatement();
        Stmt? @else = null;
        if (AcceptIdent("else"))
        {
            @else = SubStatement();
        }

        return new IfStmt { Condition = cond, Then = then, Else = @else, Location = Loc(t) };
    }

    private Stmt Switch()
    {
        Token t = Advance();
        Expect(TokenKind.LParen, "(");
        Expr value = Expression();
        Expect(TokenKind.RParen, ")");
        Token open = Expect(TokenKind.LBrace, "{");
        Enter(open);
        var cases = new List<SwitchCase>();
        while (!At(TokenKind.RBrace) && !At(TokenKind.End))
        {
            var labels = new List<CaseLabel>();
            bool isDefault = false;
            if (AcceptIdent("default"))
            {
                isDefault = true;
                Expect(TokenKind.Colon, ":");
            }
            else if (AcceptIdent("case"))
            {
                do
                {
                    Expr v = Expression(Prec.Ternary);
                    Expr? end = Accept(TokenKind.Ellipsis) ? Expression(Prec.Ternary) : null;
                    labels.Add(new CaseLabel { Value = v, RangeEnd = end });
                }
                while (Accept(TokenKind.Comma));
                Expect(TokenKind.Colon, ":");
            }
            else
            {
                Fail(TemplateMessages.Expected, Current, "case");
            }

            var body = new List<Stmt>();
            while (!AtIdent("case") && !AtIdent("default") && !At(TokenKind.RBrace) && !At(TokenKind.End))
            {
                Stmt? s = StatementWithRecovery(topLevel: false);
                if (s is not null)
                {
                    body.Add(s);
                }
            }

            cases.Add(new SwitchCase { Labels = labels, IsDefault = isDefault, Body = body });
        }

        Expect(TokenKind.RBrace, "}");
        Leave();
        return new SwitchStmt { Value = value, Cases = cases, Location = Loc(t) };
    }

    private Stmt For()
    {
        Token t = Advance();
        Expect(TokenKind.LParen, "(");
        Stmt? init = null;
        if (!Accept(TokenKind.Semicolon))
        {
            if (AtIdent("local"))
            {
                Token l = Advance();
                init = LocalRest(Loc(l), isConst: false, null);
            }
            else
            {
                init = ExpressionStatement();
            }
        }

        Expr? cond = At(TokenKind.Semicolon) ? null : Expression();
        ExpectSemicolon();
        Expr? update = At(TokenKind.RParen) ? null : Expression();
        Expect(TokenKind.RParen, ")");
        Stmt body = SubStatement();
        return new ForStmt { Init = init, Condition = cond, Update = update, Body = body, Location = Loc(t) };
    }

    // ───────────── 式 ─────────────

    private enum Prec
    {
        Assign = 1,
        Ternary = 2,
        Or = 3,
        And = 4,
        BitOr = 5,
        BitXor = 6,
        BitAnd = 7,
        Equality = 8,
        Relational = 9,
        Shift = 10,
        Additive = 11,
        Multiplicative = 12,
    }

    private static Prec? Binary(TokenKind k) => k switch
    {
        TokenKind.PipePipe => Prec.Or,
        TokenKind.AmpAmp => Prec.And,
        TokenKind.Pipe => Prec.BitOr,
        TokenKind.Caret => Prec.BitXor,
        TokenKind.Amp => Prec.BitAnd,
        TokenKind.EqEq or TokenKind.NotEq => Prec.Equality,
        TokenKind.Less or TokenKind.LessEq or TokenKind.Greater or TokenKind.GreaterEq => Prec.Relational,
        TokenKind.Shl or TokenKind.Shr => Prec.Shift,
        TokenKind.Plus or TokenKind.Minus => Prec.Additive,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => Prec.Multiplicative,
        _ => null,
    };

    private static bool IsAssign(TokenKind k) => k is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign
        or TokenKind.StarAssign or TokenKind.SlashAssign or TokenKind.PercentAssign or TokenKind.AmpAssign or TokenKind.PipeAssign
        or TokenKind.CaretAssign or TokenKind.ShlAssign or TokenKind.ShrAssign;

    private Expr Expression() => Expression(Prec.Assign);

    private Expr Expression(Prec min)
    {
        Token start = Current;
        Enter(start);
        try
        {
            Expr left = Unary();
            while (true)
            {
                TokenKind k = Current.Kind;
                if (min <= Prec.Assign && IsAssign(k))
                {
                    Advance();
                    Expr value = Expression(Prec.Assign);
                    left = new AssignExpr { Op = k, Target = left, Value = value, Location = left.Location };
                    continue;
                }

                if (min <= Prec.Ternary && k == TokenKind.Question)
                {
                    Advance();
                    Expr then = Expression(Prec.Ternary);
                    Expect(TokenKind.Colon, ":");
                    Expr @else = Expression(Prec.Ternary);
                    left = new TernaryExpr { Condition = left, Then = then, Else = @else, Location = left.Location };
                    continue;
                }

                Prec? p = Binary(k);
                if (p is null || p < min)
                {
                    return left;
                }

                Advance();
                Expr right = Expression(p.Value + 1);
                left = new BinaryExpr { Op = k, Left = left, Right = right, Location = left.Location };
            }
        }
        finally
        {
            Leave();
        }
    }

    private Expr Unary()
    {
        Token t = Current;
        if (t.Kind is TokenKind.Bang or TokenKind.Tilde or TokenKind.Minus or TokenKind.Plus)
        {
            Advance();
            Enter(t);
            Expr operand = Unary();
            Leave();
            return new UnaryExpr { Op = t.Kind, Operand = operand, Location = Loc(t) };
        }

        if (t.Kind == TokenKind.Identifier && t.Text is "sizeof" or "addressof" or "endof" or "typeof")
        {
            Advance();
            IntrinsicKind kind = t.Text switch
            {
                "sizeof" => IntrinsicKind.Sizeof,
                "addressof" => IntrinsicKind.Addressof,
                "endof" => IntrinsicKind.Endof,
                _ => IntrinsicKind.Typeof,
            };
            Expect(TokenKind.LParen, "(");
            Expr? operand = null;
            TypeRef? typeOperand = null;
            if (kind == IntrinsicKind.Sizeof && TrySpeculate(() =>
                {
                    TypeRef type = TypeReference();
                    if (!At(TokenKind.RParen) || (type.TypeArgs.Count == 0 && type.Args.Count == 0 && type.Inline is null))
                    {
                        // 名前だけの場合は式として扱い、評価時に型かどうかを決める
                        Fail(TemplateMessages.UnexpectedToken, Current, Current.Text);
                    }

                    return type;
                }, out TypeRef ty))
            {
                typeOperand = ty;
            }
            else
            {
                operand = Expression();
            }

            Expect(TokenKind.RParen, ")");
            return Postfix(new IntrinsicExpr { Kind = kind, Operand = operand, TypeOperand = typeOperand, Location = Loc(t) });
        }

        return Postfix(Primary());
    }

    private Expr Primary()
    {
        Token t = Current;
        TemplateSourceLocation at = Loc(t);
        switch (t.Kind)
        {
            case TokenKind.Integer:
            case TokenKind.Char:
                Advance();
                return new LiteralExpr { Value = t.IntValue, Location = at };
            case TokenKind.Float:
                Advance();
                return new LiteralExpr { Value = t.FloatValue, Location = at };
            case TokenKind.String:
                Advance();
                return new LiteralExpr { Value = t.StringValue ?? string.Empty, Location = at };
            case TokenKind.ByteString:
                Advance();
                return new ByteStringExpr { Bytes = t.Bytes ?? [], Mask = t.Mask ?? [], Location = at };
            case TokenKind.Dollar:
                Advance();
                return new KeywordExpr { Keyword = KeywordKind.Dollar, Location = at };
            case TokenKind.LParen:
            {
                Advance();
                Expr e = Expression();
                Expect(TokenKind.RParen, ")");
                return e;
            }

            case TokenKind.Identifier:
                break;
            default:
                Fail(TemplateMessages.UnexpectedToken, t, t.Text);
                break;
        }

        switch (t.Text)
        {
            case "true":
                Advance();
                return new LiteralExpr { Value = true, Location = at };
            case "false":
                Advance();
                return new LiteralExpr { Value = false, Location = at };
            case "null":
                Advance();
                return new LiteralExpr { Value = null, Location = at };
            case "this":
                Advance();
                return new KeywordExpr { Keyword = KeywordKind.This, Location = at };
            case "parent":
                Advance();
                return new KeywordExpr { Keyword = KeywordKind.Parent, Location = at };
            case "last":
                Advance();
                return new KeywordExpr { Keyword = KeywordKind.Last, Location = at };
            case "eof":
                Advance();
                return new KeywordExpr { Keyword = KeywordKind.Eof, Location = at };
            case "fn":
            {
                Advance();
                Expect(TokenKind.LParen, "(");
                var ps = new List<string>();
                if (!At(TokenKind.RParen))
                {
                    do
                    {
                        // 型を書いた引数 (fn(u32 v) => …) も受け付ける
                        string first = ExpectIdent();
                        ps.Add(Current.Kind == TokenKind.Identifier ? ExpectIdent() : first);
                    }
                    while (Accept(TokenKind.Comma));
                }

                Expect(TokenKind.RParen, ")");
                Expect(TokenKind.FatArrow, "=>");
                Expr body = Expression(Prec.Ternary);
                return new LambdaExpr { Params = ps, Body = body, Location = at };
            }
        }

        if (Reserved.Contains(t.Text) && t.Text is not ("in"))
        {
            Fail(TemplateMessages.UnexpectedToken, t, t.Text);
        }

        Advance();
        var parts = new List<string> { t.Text };
        while (At(TokenKind.ColonColon) && PeekToken(1).Kind == TokenKind.Identifier)
        {
            Advance();
            parts.Add(Advance().Text);
        }

        return new NameExpr { Parts = [.. parts], Location = at };
    }

    private Expr Postfix(Expr e)
    {
        int count = 0;
        while (true)
        {
            Token t = Current;
            if (++count > MaxNesting)
            {
                throw new FatalSyntaxError(new TemplateDiagnostic(TemplateSeverity.Error, TemplateMessages.NestingTooDeep,
                    [MaxNesting.ToString(System.Globalization.CultureInfo.InvariantCulture)], Loc(t)));
            }

            if (t.Kind == TokenKind.Dot)
            {
                Advance();
                string name = ExpectIdent();
                e = new MemberExpr { Target = e, Name = name, Location = e.Location };
            }
            else if (t.Kind == TokenKind.LBracket && PeekToken(1).Kind != TokenKind.LBracket)
            {
                Advance();
                Expr index = Expression();
                Expect(TokenKind.RBracket, "]");
                e = new IndexExpr { Target = e, Index = index, Location = e.Location };
            }
            else if (t.Kind == TokenKind.LParen)
            {
                Advance();
                var args = new List<Expr>();
                if (!At(TokenKind.RParen))
                {
                    do
                    {
                        args.Add(Expression(Prec.Ternary));
                    }
                    while (Accept(TokenKind.Comma));
                }

                Expect(TokenKind.RParen, ")");
                e = new CallExpr { Callee = e, Args = args, Location = e.Location };
            }
            else
            {
                return e;
            }
        }
    }
}
