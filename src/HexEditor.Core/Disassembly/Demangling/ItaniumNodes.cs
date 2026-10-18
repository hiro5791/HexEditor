using System.Text;

namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>
/// Itanium C++ ABI のデマングルの構文木の節 (LLVM の ItaniumDemangle と同じく、型を左側と右側に分けて書く。
/// 例: 関数へのポインタ <c>void (*)(int)</c> は左側 <c>void (*</c>、右側 <c>)(int)</c>)。
/// </summary>
internal abstract class ItNode
{
    public virtual bool HasRight => false;

    public virtual bool IsFunction => false;

    public virtual bool IsArray => false;

    public abstract void Left(Out o);

    public virtual void Right(Out o)
    {
    }

    public void Print(Out o)
    {
        o.Enter();
        Left(o);
        Right(o);
        o.Leave();
    }

    /// <summary>コンストラクター・デストラクターの名前に使う基本名。</summary>
    public virtual string BaseName => ToText();

    public string ToText()
    {
        var o = new Out();
        Print(o);
        return o.ToString();
    }
}

/// <summary>出力 (長さと深さの上限を見る)。</summary>
internal sealed class Out
{
    private readonly StringBuilder _sb = new();
    private int _depth;

    public bool Short { get; init; }

    public Out Add(string s)
    {
        _sb.Append(s);
        if (_sb.Length > Demangler.MaxOutput)
        {
            throw new DemangleException();
        }

        return this;
    }

    public char Last => _sb.Length == 0 ? '\0' : _sb[^1];

    public void Enter()
    {
        if (++_depth > Demangler.MaxDepth * 4)
        {
            throw new DemangleException();
        }
    }

    public void Leave() => _depth--;

    public override string ToString() => _sb.ToString();
}

internal sealed class ItName(string text) : ItNode
{
    public string Text { get; } = text;

    public override void Left(Out o) => o.Add(Text);
}

internal sealed class ItNested(ItNode qualifier, ItNode name) : ItNode
{
    public ItNode Qualifier { get; } = qualifier;

    public ItNode Name { get; } = name;

    public override string BaseName => Name.BaseName;

    public override void Left(Out o)
    {
        Qualifier.Print(o);
        o.Add("::");
        Name.Print(o);
    }
}

internal sealed class ItWithTemplateArgs(ItNode name, ItTemplateArgs args) : ItNode
{
    public ItNode Name { get; } = name;

    public ItTemplateArgs Args { get; } = args;

    public override string BaseName => Name.BaseName;

    public override void Left(Out o)
    {
        Name.Print(o);
        Args.Print(o);
    }
}

internal sealed class ItTemplateArgs(List<ItNode> args) : ItNode
{
    public List<ItNode> Args { get; } = args;

    public override void Left(Out o)
    {
        o.Add("<");
        for (int i = 0; i < Args.Count; i++)
        {
            if (i > 0)
            {
                o.Add(", ");
            }

            Args[i].Print(o);
        }

        o.Add(">");
    }
}

/// <summary>パラメータパックの展開 (テンプレート引数の <c>J…E</c>)。中身をそのまま並べる。</summary>
internal sealed class ItPack(List<ItNode> items) : ItNode
{
    public override void Left(Out o)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                o.Add(", ");
            }

            items[i].Print(o);
        }
    }
}

internal sealed class ItCtorDtor(ItNode basis, bool destructor) : ItNode
{
    public override void Left(Out o) => o.Add(destructor ? "~" : string.Empty).Add(basis.BaseName);
}

internal sealed class ItSpecial(string prefix, ItNode child) : ItNode
{
    public override void Left(Out o)
    {
        o.Add(prefix);
        child.Print(o);
    }
}

internal sealed class ItAbiTag(ItNode basis, string tag) : ItNode
{
    public override string BaseName => basis.BaseName;

    public override void Left(Out o)
    {
        basis.Print(o);
        o.Add("[abi:").Add(tag).Add("]");
    }
}

internal sealed class ItQual(ItNode child, string qualifiers) : ItNode
{
    public ItNode Child { get; } = child;

    public override bool HasRight => Child.HasRight;

    public override bool IsFunction => Child.IsFunction;

    public override bool IsArray => Child.IsArray;

    public override void Left(Out o)
    {
        Child.Left(o);
        o.Add(qualifiers);
    }

    public override void Right(Out o) => Child.Right(o);
}

internal sealed class ItPointer(ItNode pointee, string symbol) : ItNode
{
    public override bool HasRight => pointee.HasRight;

    public override void Left(Out o)
    {
        pointee.Left(o);
        if (pointee.IsArray)
        {
            o.Add(" ");
        }

        if (pointee.IsArray || pointee.IsFunction)
        {
            o.Add("(");
        }

        o.Add(symbol);
    }

    public override void Right(Out o)
    {
        if (pointee.IsArray || pointee.IsFunction)
        {
            o.Add(")");
        }

        pointee.Right(o);
    }
}

internal sealed class ItPointerToMember(ItNode cls, ItNode member) : ItNode
{
    public override bool HasRight => member.HasRight;

    public override void Left(Out o)
    {
        member.Left(o);
        o.Add(member.IsArray || member.IsFunction ? "(" : " ");
        cls.Print(o);
        o.Add("::*");
    }

    public override void Right(Out o)
    {
        if (member.IsArray || member.IsFunction)
        {
            o.Add(")");
        }

        member.Right(o);
    }
}

internal sealed class ItArray(ItNode element, string dimension) : ItNode
{
    public override bool HasRight => true;

    public override bool IsArray => true;

    public override void Left(Out o) => element.Left(o);

    public override void Right(Out o)
    {
        if (o.Last != ']')
        {
            o.Add(" ");
        }

        o.Add("[").Add(dimension).Add("]");
        element.Right(o);
    }
}

internal sealed class ItFunctionType(ItNode ret, List<ItNode> parameters, string qualifiers) : ItNode
{
    public override bool HasRight => true;

    public override bool IsFunction => true;

    public override void Left(Out o)
    {
        ret.Left(o);
        o.Add(" ");
    }

    public override void Right(Out o)
    {
        ItParams.Print(o, parameters);
        ret.Right(o);
        o.Add(qualifiers);
    }
}

internal static class ItParams
{
    public static void Print(Out o, List<ItNode> parameters)
    {
        o.Add("(");
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                o.Add(", ");
            }

            parameters[i].Print(o);
        }

        o.Add(")");
    }
}

internal sealed class ItEncoding(ItNode? ret, ItNode name, List<ItNode> parameters, string qualifiers) : ItNode
{
    public override void Left(Out o)
    {
        if (o.Short)
        {
            name.Print(o);
            return;
        }

        if (ret is not null)
        {
            ret.Left(o);
            if (!ret.HasRight)
            {
                o.Add(" ");
            }
        }

        name.Print(o);
        ItParams.Print(o, parameters);
        ret?.Right(o);
        o.Add(qualifiers);
    }
}

internal sealed class ItConversion(ItNode type) : ItNode
{
    public override void Left(Out o)
    {
        o.Add("operator ");
        type.Print(o);
    }
}

internal sealed class ItVector(ItNode element, string dimension) : ItNode
{
    public override void Left(Out o)
    {
        element.Print(o);
        o.Add(" vector[").Add(dimension).Add("]");
    }
}

internal sealed class ItPackExpansion(ItNode child) : ItNode
{
    public override void Left(Out o)
    {
        child.Print(o);
        o.Add("...");
    }
}

/// <summary>式 (テンプレート引数の <c>X…E</c> と <c>L…E</c>)。</summary>
internal sealed class ItExpr(string text) : ItNode
{
    public override void Left(Out o) => o.Add(text);
}

internal sealed class ItBinary(ItNode left, string op, ItNode right) : ItNode
{
    public override void Left(Out o)
    {
        o.Add("(");
        left.Print(o);
        o.Add(") ").Add(op).Add(" (");
        right.Print(o);
        o.Add(")");
    }
}

internal sealed class ItPrefix(string op, ItNode child) : ItNode
{
    public override void Left(Out o)
    {
        o.Add(op).Add("(");
        child.Print(o);
        o.Add(")");
    }
}
