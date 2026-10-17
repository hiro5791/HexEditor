using System.Globalization;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal enum VK : byte
{
    Void,
    Null,
    Bool,
    Int,
    Float,
    Str,
    Bytes,
    Mask,
    Node,
    Func,
    List,
    Section,
    Endian,
    DateTime,
    BitOrder,
}

/// <summary>評価中の値。整数は 128 bit 符号付きで扱う (TPL-02 の仕様 8)。</summary>
internal readonly struct Val
{
    public readonly VK Kind;
    public readonly Int128 I;
    public readonly double F;
    public readonly object? O;

    private Val(VK kind, Int128 i = default, double f = 0, object? o = null)
    {
        Kind = kind;
        I = i;
        F = f;
        O = o;
    }

    public static readonly Val Void = new(VK.Void);
    public static readonly Val Null = new(VK.Null);
    public static readonly Val True = new(VK.Bool, 1);
    public static readonly Val False = new(VK.Bool, 0);

    public static Val Int(Int128 v) => new(VK.Int, v);

    public static Val Float(double v) => new(VK.Float, f: v);

    public static Val Bool(bool v) => v ? True : False;

    public static Val Str(string s) => new(VK.Str, o: s);

    public static Val Bytes(byte[] b) => new(VK.Bytes, o: b);

    public static Val Mask(byte[] bytes, byte[] mask) => new(VK.Mask, o: (bytes, mask));

    public static Val Node(TemplateNode n) => new(VK.Node, o: n);

    public static Val Func(Callable f) => new(VK.Func, o: f);

    public static Val List(List<Val> items) => new(VK.List, o: items);

    public static Val Section(TemplateSection s) => new(VK.Section, o: s);

    public static Val Endian(bool big) => new(VK.Endian, big ? 1 : 0);

    public static Val BitOrder(bool msbFirst) => new(VK.BitOrder, msbFirst ? 1 : 0);

    public static Val DateTime(long ticksUtc) => new(VK.DateTime, ticksUtc);

    public bool IsNumeric => Kind is VK.Int or VK.Float or VK.Bool;

    public string AsString => (string)O!;

    public TemplateNode AsNode => (TemplateNode)O!;

    public override string ToString() => Kind switch
    {
        VK.Void => "void",
        VK.Null => "null",
        VK.Bool => I != 0 ? "true" : "false",
        VK.Int => I.ToString(CultureInfo.InvariantCulture),
        VK.Float => F.ToString("R", CultureInfo.InvariantCulture),
        VK.Str => AsString,
        VK.Bytes => Convert.ToHexString((byte[])O!),
        VK.Node => AsNode.Path,
        VK.Endian => I != 0 ? "big" : "little",
        VK.DateTime => new DateTime((long)I, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        _ => Kind.ToString(),
    };
}

/// <summary>呼び出せるもの (利用者の関数、無名関数、組み込み関数、拡張関数)。</summary>
internal abstract class Callable
{
    public abstract string Name { get; }
}

internal sealed class UserFunction(FuncDecl decl) : Callable
{
    public FuncDecl Decl { get; } = decl;

    public override string Name => Decl.QualifiedName;
}

internal sealed class LambdaFunction(LambdaExpr expr, Scope scope, Frame frame) : Callable
{
    public LambdaExpr Expr { get; } = expr;

    public Scope Scope { get; } = scope;

    public Frame Frame { get; } = frame;

    public override string Name => "fn";
}

internal sealed class BuiltinFunction(string name) : Callable
{
    public override string Name { get; } = name;
}

/// <summary>組み込み関数が作る関数 (std::ptr::relative_to など)。</summary>
internal sealed class NativeFunction(string name, Func<Val[], Val> invoke) : Callable
{
    public override string Name { get; } = name;

    public Func<Val[], Val> Invoke { get; } = invoke;
}

/// <summary>ローカル変数・引数・定数。</summary>
internal sealed class Variable(Val value, bool isConst, RtType? type)
{
    public Val Value { get; set; } = value;

    public bool IsConst { get; } = isConst;

    public RtType? Type { get; } = type;

    /// <summary><c>[[show]]</c> の計算値のノード (値を代入したら表示も変える)。</summary>
    public TemplateNode? ShowNode { get; set; }
}

/// <summary>ローカル変数のスコープ。</summary>
internal sealed class Scope(Scope? parent)
{
    private Dictionary<string, Variable>? _vars;

    public Scope? Parent { get; } = parent;

    public Variable? Find(string name)
    {
        for (Scope? s = this; s is not null; s = s.Parent)
        {
            if (s._vars is not null && s._vars.TryGetValue(name, out Variable? v))
            {
                return v;
            }
        }

        return null;
    }

    public void Define(string name, Variable v)
    {
        _vars ??= new Dictionary<string, Variable>(StringComparer.Ordinal);
        _vars[name] = v;
    }

    public IEnumerable<string> Names()
    {
        for (Scope? s = this; s is not null; s = s.Parent)
        {
            if (s._vars is not null)
            {
                foreach (string k in s._vars.Keys)
                {
                    yield return k;
                }
            }
        }
    }
}

/// <summary>評価を止めるエラー (または部分木だけのエラー)。</summary>
internal sealed class TemplateError(TemplateDiagnostic diagnostic, bool isLimit = false) : Exception(diagnostic.Message)
{
    public TemplateDiagnostic Diagnostic { get; set; } = diagnostic;

    /// <summary>上限 (ステップ、パターン数、時間、メモリ、キャンセル) によるもの。部分木では捕まえず、評価全体を止める。</summary>
    public bool IsLimit { get; } = isLimit;

    /// <summary>データの範囲外の読み込み (配列 [eof] の末尾の端数の判定に使う)。</summary>
    public bool IsOutOfRange { get; init; }
}

/// <summary>文の実行結果 (break / continue / return は例外を使わずに伝える)。</summary>
internal enum Flow : byte
{
    Normal,
    Break,
    Continue,
    Return,
}
