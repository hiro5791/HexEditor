namespace HexEditor.Core.Templates.Syntax;

/// <summary>字句の種類 (TPL-02)。</summary>
internal enum TokenKind
{
    End,
    Identifier,
    Integer,
    Float,
    String,
    Char,
    ByteString,

    // 区切り
    LParen,
    RParen,
    LBracket,
    RBracket,
    LBrace,
    RBrace,
    AttrOpen,   // [[
    AttrClose,  // ]]
    Comma,
    Semicolon,
    Colon,
    ColonColon,
    Dot,
    Ellipsis,   // ...
    Question,
    Arrow,      // ->
    FatArrow,   // =>
    At,
    Dollar,

    // 演算子
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Amp,
    Pipe,
    Caret,
    Tilde,
    Bang,
    Less,
    Greater,
    LessEq,
    GreaterEq,
    EqEq,
    NotEq,
    AmpAmp,
    PipePipe,
    Shl,
    Shr,
    Assign,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    AmpAssign,
    PipeAssign,
    CaretAssign,
    ShlAssign,
    ShrAssign,

    /// <summary><c>#pragma</c> の行 (値は行の残り)。</summary>
    Pragma,

    /// <summary><c>#include "…"</c> の行 (値はパス)。</summary>
    Include,
}

/// <summary>テンプレートの中の位置 (1 から数える行・列)。</summary>
public readonly record struct TemplateSourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

internal readonly record struct Token(TokenKind Kind, string Text, int Line, int Column, int Position)
{
    /// <summary>整数リテラルの値。</summary>
    public Int128 IntValue { get; init; }

    public double FloatValue { get; init; }

    /// <summary>文字列・バイト列リテラルの値 (文字列は復号後の文字列)。</summary>
    public string? StringValue { get; init; }

    /// <summary>バイト列リテラルの値とマスク (ワイルドカードは 0)。</summary>
    public byte[]? Bytes { get; init; }

    public byte[]? Mask { get; init; }

    /// <summary>直前の <c>///</c> コメント (宣言の説明)。</summary>
    public string? DocComment { get; init; }

    public bool Is(TokenKind kind) => Kind == kind;

    public bool IsIdent(string text) => Kind == TokenKind.Identifier && Text == text;

    public override string ToString() => Kind == TokenKind.End ? "<end>" : Text;
}
