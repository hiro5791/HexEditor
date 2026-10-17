namespace HexEditor.Core.Templates.Syntax;

// HXT の構文木。.ksy (TPL-33) も同じ構文木に変換して評価する。

internal abstract class Node
{
    public TemplateSourceLocation Location { get; init; }
}

// ───────────── 式 ─────────────

internal abstract class Expr : Node
{
}

internal sealed class LiteralExpr : Expr
{
    public required object? Value { get; init; } // Int128 / double / string / bool / null
}

internal sealed class ByteStringExpr : Expr
{
    public required byte[] Bytes { get; init; }

    public required byte[] Mask { get; init; }
}

/// <summary>名前 (<c>a</c>、<c>std::mem::find</c>、<c>Machine::AMD64</c>)。</summary>
internal sealed class NameExpr : Expr
{
    public required string[] Parts { get; init; }

    public string Text => string.Join("::", Parts);

    public string Last => Parts[^1];
}

internal enum KeywordKind
{
    Dollar,
    This,
    Parent,
    Last,
    Eof,
}

internal sealed class KeywordExpr : Expr
{
    public required KeywordKind Keyword { get; init; }
}

internal sealed class UnaryExpr : Expr
{
    public required TokenKind Op { get; init; }

    public required Expr Operand { get; init; }
}

internal sealed class BinaryExpr : Expr
{
    public required TokenKind Op { get; init; }

    public required Expr Left { get; init; }

    public required Expr Right { get; init; }
}

internal sealed class TernaryExpr : Expr
{
    public required Expr Condition { get; init; }

    public required Expr Then { get; init; }

    public required Expr Else { get; init; }
}

internal sealed class AssignExpr : Expr
{
    public required TokenKind Op { get; init; }

    public required Expr Target { get; init; }

    public required Expr Value { get; init; }
}

internal sealed class MemberExpr : Expr
{
    public required Expr Target { get; init; }

    public required string Name { get; init; }
}

internal sealed class IndexExpr : Expr
{
    public required Expr Target { get; init; }

    public required Expr Index { get; init; }
}

internal sealed class CallExpr : Expr
{
    public required Expr Callee { get; init; }

    public required List<Expr> Args { get; init; }

    /// <summary>型引数 (<c>std::mem::read&lt;u32&gt;(…)</c> など。.ksy の変換でも使う)。</summary>
    public List<TypeArg>? TypeArgs { get; init; }
}

internal enum IntrinsicKind
{
    Sizeof,
    Addressof,
    Endof,
    Typeof,
}

internal sealed class IntrinsicExpr : Expr
{
    public required IntrinsicKind Kind { get; init; }

    /// <summary>式の引数 (型の引数の場合は null)。</summary>
    public Expr? Operand { get; init; }

    /// <summary>型の引数 (<c>sizeof(u32)</c>)。名前だけの場合は、評価時に式か型かを決める。</summary>
    public TypeRef? TypeOperand { get; init; }
}

internal sealed class LambdaExpr : Expr
{
    public required List<string> Params { get; init; }

    public required Expr Body { get; init; }
}

// ───────────── 型 ─────────────

internal sealed class TypeArg
{
    public TypeRef? Type { get; init; }

    public Expr? Value { get; init; }
}

/// <summary>型の参照 (<c>Pair&lt;u32&gt;</c>、<c>Record(hdr.version)</c>、無名の構造体)。</summary>
internal sealed class TypeRef : Node
{
    public string[] Parts { get; init; } = [];

    public List<TypeArg> TypeArgs { get; init; } = [];

    public List<Expr> Args { get; init; } = [];

    /// <summary>無名の構造体・共用体・ビットフィールド (<c>struct { … } pair;</c>)。</summary>
    public TypeDecl? Inline { get; init; }

    public string Text => Inline is not null ? Inline.Name : string.Join("::", Parts);

    public bool IsAuto => Parts.Length == 1 && Parts[0] == "auto";
}

// ───────────── 属性 ─────────────

internal sealed class AttributeNode : Node
{
    public required string Name { get; init; }

    public List<Expr> Args { get; init; } = [];
}

// ───────────── 文 ─────────────

internal abstract class Stmt : Node
{
}

internal enum ArrayKind
{
    None,
    Count,
    While,
    Until,
    Terminated,
    Eof,
}

internal sealed class Declarator : Node
{
    /// <summary>パターンの名前 (<c>padding[2];</c> のように名前がない場合は null)。</summary>
    public string? Name { get; init; }

    public bool IsPointer { get; init; }

    /// <summary><c>Entry (*table)[n] : u32</c> (配列を 1 つ指すポインタ)。</summary>
    public bool PointerToArray { get; init; }

    public TypeRef? PointerType { get; init; }

    public ArrayKind Array { get; init; }

    public Expr? ArrayExpr { get; init; }

    /// <summary>構造体の中の C 風のビットフィールド (<c>u16 a : 4, b : 12;</c>) のビット数。</summary>
    public Expr? BitWidth { get; init; }

    public Expr? Placement { get; init; }

    public string? PlacementSection { get; init; }

    /// <summary>ポインタの指す先のセクション (<c>T *p : u32 in s;</c>)。</summary>
    public string? PointerSection { get; init; }

    public List<AttributeNode> Attributes { get; init; } = [];
}

internal sealed class PatternDeclStmt : Stmt
{
    /// <summary><c>le</c> / <c>be</c> のキーワード (なければ null)。</summary>
    public bool? BigEndianKeyword { get; init; }

    public required TypeRef Type { get; init; }

    public required List<Declarator> Declarators { get; init; }

    public string? DocComment { get; init; }
}

internal sealed class LocalDeclStmt : Stmt
{
    public bool IsConst { get; init; }

    public required TypeRef Type { get; init; }

    public required string Name { get; init; }

    public Expr? Init { get; init; }

    public List<AttributeNode> Attributes { get; init; } = [];

    public string? DocComment { get; init; }
}

internal sealed class SectionDeclStmt : Stmt
{
    public required string Name { get; init; }
}

internal sealed class ExprStmt : Stmt
{
    public required Expr Expr { get; init; }
}

internal sealed class BlockStmt : Stmt
{
    public required List<Stmt> Body { get; init; }
}

internal sealed class IfStmt : Stmt
{
    public required Expr Condition { get; init; }

    public required Stmt Then { get; init; }

    public Stmt? Else { get; init; }
}

internal sealed class CaseLabel
{
    public required Expr Value { get; init; }

    public Expr? RangeEnd { get; init; }
}

internal sealed class SwitchCase
{
    public List<CaseLabel> Labels { get; init; } = [];

    public bool IsDefault { get; init; }

    public List<Stmt> Body { get; init; } = [];
}

internal sealed class SwitchStmt : Stmt
{
    public required Expr Value { get; init; }

    public required List<SwitchCase> Cases { get; init; }
}

internal sealed class ForStmt : Stmt
{
    public Stmt? Init { get; init; }

    public Expr? Condition { get; init; }

    public Expr? Update { get; init; }

    public required Stmt Body { get; init; }
}

internal sealed class WhileStmt : Stmt
{
    public required Expr Condition { get; init; }

    public required Stmt Body { get; init; }

    public bool IsDoWhile { get; init; }
}

internal sealed class BreakStmt : Stmt
{
}

internal sealed class ContinueStmt : Stmt
{
}

internal sealed class ReturnStmt : Stmt
{
    public Expr? Value { get; init; }
}

internal sealed class EmptyStmt : Stmt
{
}

/// <summary>型・関数の定義の文 (名前空間に登録する。評価時は何もしない)。</summary>
internal sealed class DeclStmt : Stmt
{
    public required Decl Decl { get; init; }
}

internal sealed class ImportStmt : Stmt
{
    /// <summary><c>import a.b.c;</c> の部分 (文字列の場合は null)。</summary>
    public string[]? Dotted { get; init; }

    public string? Path { get; init; }
}

internal sealed class NamespaceStmt : Stmt
{
    public required string[] Name { get; init; }

    public required List<Stmt> Body { get; init; }
}

internal sealed class UsingNamespaceStmt : Stmt
{
    public required string[] Name { get; init; }
}

// ───────────── 定義 ─────────────

internal abstract class Decl : Node
{
    public required string Name { get; init; }

    public string? DocComment { get; init; }

    public List<AttributeNode> Attributes { get; set; } = [];

    /// <summary>定義した名前空間 (読み込み時に設定する)。</summary>
    public string[] Namespace { get; set; } = [];

    public string QualifiedName => Namespace.Length == 0 ? Name : string.Join("::", Namespace) + "::" + Name;
}

internal abstract class TypeDecl : Decl
{
}

internal sealed class TypeParam
{
    public required string Name { get; init; }

    /// <summary><c>auto N</c> (数値の型引数)。</summary>
    public bool IsValue { get; init; }
}

internal sealed class Param
{
    public required TypeRef Type { get; init; }

    public required string Name { get; init; }

    public bool IsRef { get; init; }
}

internal sealed class StructDecl : TypeDecl
{
    public bool IsUnion { get; init; }

    public List<TypeParam> TypeParams { get; init; } = [];

    public List<Param> Params { get; init; } = [];

    public TypeRef? Base { get; init; }

    public required List<Stmt> Body { get; init; }

    /// <summary>.ksy の instances (遅延評価のパターン。TPL-33)。</summary>
    public List<PatternDeclStmt> Instances { get; init; } = [];
}

internal sealed class EnumMember : Node
{
    public required string Name { get; init; }

    public Expr? Value { get; init; }

    public Expr? RangeEnd { get; init; }

    public string? DocComment { get; init; }
}

internal sealed class EnumDecl : TypeDecl
{
    public bool IsFlags { get; init; }

    public TypeRef? BaseType { get; init; }

    public required List<EnumMember> Members { get; init; }
}

internal sealed class BitfieldMember : Node
{
    public TypeRef? Type { get; init; }

    /// <summary>名前 (<c>padding</c> は null)。</summary>
    public string? Name { get; init; }

    public required Expr Width { get; init; }

    public List<AttributeNode> Attributes { get; init; } = [];

    public string? DocComment { get; init; }
}

internal sealed class BitfieldDecl : TypeDecl
{
    public TypeRef? BaseType { get; init; }

    public required List<BitfieldMember> Members { get; init; }
}

internal sealed class AliasDecl : TypeDecl
{
    public required TypeRef Target { get; init; }
}

internal sealed class FuncDecl : Decl
{
    public required List<Param> Params { get; init; }

    public TypeRef? ReturnType { get; init; }

    public BlockStmt? Body { get; init; }

    public bool IsExtern { get; init; }
}

internal sealed class ConstDecl : Decl
{
    public required LocalDeclStmt Statement { get; init; }
}

internal sealed class PragmaNode : Node
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

/// <summary>1 つのファイルの構文木。</summary>
internal sealed class FileSyntax
{
    public required string Path { get; init; }

    public List<Stmt> Statements { get; init; } = [];

    public List<PragmaNode> Pragmas { get; init; } = [];

    /// <summary><c>#include</c> の位置と、展開したファイル (読み込み時に設定する)。</summary>
    public List<(int Index, string Path, TemplateSourceLocation At)> Includes { get; init; } = [];
}
