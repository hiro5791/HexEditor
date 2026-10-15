using System.Globalization;
using System.Runtime.CompilerServices;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    // ───────────── 式 (TPL-02) ─────────────

    private Val Eval(Expr e, Frame f)
    {
        switch (e)
        {
            case LiteralExpr lit:
                return lit.Value switch
                {
                    Int128 i => Val.Int(i),
                    double d => Val.Float(d),
                    string s => Val.Str(s),
                    bool b => Val.Bool(b),
                    null => Val.Null,
                    _ => Val.Null,
                };
            case ByteStringExpr bs:
                return Val.Mask(bs.Bytes, bs.Mask);
            case NameExpr n:
                return ResolveName(n, f);
            case KeywordExpr k:
                return Keyword(k, f);
            case BinaryExpr b:
                return Binary(b, f);
            case UnaryExpr u:
                return Unary(u, f);
            case TernaryExpr t:
                return Truthy(Eval(t.Condition, f)) ? Eval(t.Then, f) : Eval(t.Else, f);
            case AssignExpr a:
                return Assign(a, f);
            case MemberExpr m:
                return Member(Eval(m.Target, f), m.Name, m);
            case IndexExpr ix:
                return Index(ix, f);
            case CallExpr c:
                return Call(c, f);
            case IntrinsicExpr ie:
                return Intrinsic(ie, f);
            case LambdaExpr l:
                return Val.Func(new LambdaFunction(l, f.Locals, f));
            case ValueExpr ve:
                return ve.Value;
            default:
                throw ErrorAt(e.Location, TemplateMessages.UnexpectedToken, e.GetType().Name);
        }
    }

    private Int128 EvalInt(Expr e, Frame f)
    {
        Val v = Deref(Eval(e, f));
        return v.Kind switch
        {
            VK.Int or VK.Bool or VK.Endian or VK.BitOrder => v.I,
            VK.Float when double.IsFinite(v.F) => (Int128)Math.Truncate(v.F),
            _ => throw ErrorAt(e.Location, TemplateMessages.TypeMismatch, KindName(v) + " → integer"),
        };
    }

    private long EvalLong(Expr e, Frame f)
    {
        Int128 v = EvalInt(e, f);
        if (v > long.MaxValue || v < long.MinValue)
        {
            throw ErrorAt(e.Location, TemplateMessages.ValueOverflow, v.ToString(CultureInfo.InvariantCulture), "s64");
        }

        return (long)v;
    }

    private Val Keyword(KeywordExpr k, Frame f)
    {
        switch (k.Keyword)
        {
            case KeywordKind.Dollar:
                return Val.Int(_pos);
            case KeywordKind.This:
                return Val.Node(_thisNode ?? f.Node);
            case KeywordKind.Parent:
            {
                Frame? p = _thisNode is not null ? f : f.Parent;
                while (p is not null && p.IsFunction)
                {
                    p = p.Parent;
                }

                if (p is null)
                {
                    throw ErrorAt(k.Location, TemplateMessages.Undefined, "parent", string.Empty);
                }

                return Val.Node(p.Node);
            }

            case KeywordKind.Last:
                if (f.Last is null)
                {
                    throw ErrorAt(k.Location, TemplateMessages.Undefined, "last", string.Empty);
                }

                return Val.Node(f.Last);
            case KeywordKind.Eof:
                return Val.Bool(_pos >= Math.Min(_space.Length, f.WindowEnd));
        }

        return Val.Null;
    }

    // ───────────── 名前 ─────────────

    private Val ResolveName(NameExpr n, Frame f)
    {
        if (n.Parts.Length == 1)
        {
            string name = n.Parts[0];
            for (Frame? fr = f; fr is not null; fr = fr.Parent)
            {
                if (fr.Locals.Find(name) is { } v)
                {
                    return v.Value;
                }

                if (fr.Members.TryGetValue(name, out List<TemplateNode>? list))
                {
                    TemplateNode node = list[^1];
                    _touched = node.Offset;
                    return Val.Node(node);
                }

                if (fr.ValueArgs is not null && fr.ValueArgs.TryGetValue(name, out Val tv))
                {
                    return tv;
                }

            }

            switch (name)
            {
                case "big":
                    return Val.Endian(true);
                case "little":
                    return Val.Endian(false);
                case "msb_first":
                    return Val.BitOrder(true);
                case "lsb_first":
                    return Val.BitOrder(false);
            }
        }

        string[] ns = f.Namespace;
        if (_program.FindConst(n.Parts, ns) is { } c)
        {
            return ConstValue(c, n.Location);
        }

        if (n.Parts.Length > 1 && _program.FindType(n.Parts[..^1], ns) is EnumDecl ed)
        {
            EnumInfo info = EnumInfoOf(ed);
            foreach ((string mname, Int128 low, _, _) in info.Members)
            {
                if (mname == n.Last)
                {
                    return Val.Int(low);
                }
            }

            throw ErrorAt(n.Location, TemplateMessages.UnknownMember, ed.Name, n.Last);
        }

        if (_program.FindFunction(n.Parts, ns) is { } fn)
        {
            return Val.Func(new UserFunction(fn));
        }

        if (IsBuiltinFunction(n.Text))
        {
            return Val.Func(new BuiltinFunction(CanonicalBuiltin(n.Text)));
        }

        throw Undefined(n.Text, n.Location, f);
    }

    private TemplateError Undefined(string name, TemplateSourceLocation at, Frame f)
    {
        var candidates = new List<string>();
        for (Frame? fr = f; fr is not null; fr = fr.Parent)
        {
            candidates.AddRange(fr.Locals.Names());
            candidates.AddRange(fr.Members.Keys);
        }

        candidates.AddRange(_program.AllNames);
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string c in candidates.Distinct())
        {
            int d = Levenshtein(name, c);
            if (d < bestDistance && d <= Math.Max(1, name.Length / 3))
            {
                bestDistance = d;
                best = c;
            }
        }

        return ErrorAt(at, TemplateMessages.Undefined, name, best ?? string.Empty);
    }

    private static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3)
        {
            return int.MaxValue;
        }

        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private Val ConstValue(ConstDecl c, TemplateSourceLocation at)
    {
        if (_constCache.TryGetValue(c, out Val v))
        {
            return v;
        }

        if (!_constEvaluating.Add(c))
        {
            throw ErrorAt(at, TemplateMessages.RecursionTooDeep, "const");
        }

        try
        {
            var frame = new Frame { Node = _result.Root, Locals = new Scope(null), Space = _docSpace, Namespace = c.Namespace, Parent = null };
            RtType? type = c.Statement.Type.IsAuto ? null : ResolveType(c.Statement.Type, frame);
            Val value = c.Statement.Init is null ? DefaultValue(type) : Eval(c.Statement.Init, frame);
            value = Coerce(value, type, c.Location);
            _constCache[c] = value;
            return value;
        }
        finally
        {
            _constEvaluating.Remove(c);
        }
    }

    // ───────────── 演算子 ─────────────

    private Val Unary(UnaryExpr u, Frame f)
    {
        Val v = Deref(Eval(u.Operand, f));
        switch (u.Op)
        {
            case TokenKind.Bang:
                return Val.Bool(!Truthy(v));
            case TokenKind.Tilde:
                return Val.Int(~ToInt(v, u.Operand.Location));
            case TokenKind.Minus:
                return v.Kind == VK.Float ? Val.Float(-v.F) : Val.Int(-ToInt(v, u.Operand.Location));
            case TokenKind.Plus:
                return v.Kind == VK.Float ? v : Val.Int(ToInt(v, u.Operand.Location));
        }

        return v;
    }

    private Val Binary(BinaryExpr b, Frame f)
    {
        if (b.Op == TokenKind.AmpAmp)
        {
            return Val.Bool(Truthy(Eval(b.Left, f)) && Truthy(Eval(b.Right, f)));
        }

        if (b.Op == TokenKind.PipePipe)
        {
            return Val.Bool(Truthy(Eval(b.Left, f)) || Truthy(Eval(b.Right, f)));
        }

        Val l = Eval(b.Left, f);
        Val r = Eval(b.Right, f);
        return Apply(b.Op, l, r, b);
    }

    private Val Apply(TokenKind op, Val l0, Val r0, Expr at)
    {
        switch (op)
        {
            case TokenKind.EqEq:
                return Val.Bool(ValuesEqual(l0, r0, at));
            case TokenKind.NotEq:
                return Val.Bool(!ValuesEqual(l0, r0, at));
            case TokenKind.Less:
                return Val.Bool(Compare(l0, r0, at) < 0);
            case TokenKind.LessEq:
                return Val.Bool(Compare(l0, r0, at) <= 0);
            case TokenKind.Greater:
                return Val.Bool(Compare(l0, r0, at) > 0);
            case TokenKind.GreaterEq:
                return Val.Bool(Compare(l0, r0, at) >= 0);
        }

        Val l = Deref(l0);
        Val r = Deref(r0);
        if (op == TokenKind.Plus && (l.Kind == VK.Str || r.Kind == VK.Str))
        {
            string ls = l.Kind == VK.Str ? l.AsString : ToDisplayString(l);
            string rs = r.Kind == VK.Str ? r.AsString : ToDisplayString(r);
            CheckStringMemory((long)ls.Length + rs.Length);
            return Val.Str(ls + rs);
        }

        if (l.Kind == VK.Float || r.Kind == VK.Float)
        {
            double x = ToDouble(l, at.Location);
            double y = ToDouble(r, at.Location);
            return op switch
            {
                TokenKind.Plus => Val.Float(x + y),
                TokenKind.Minus => Val.Float(x - y),
                TokenKind.Star => Val.Float(x * y),
                TokenKind.Slash => Val.Float(x / y),
                TokenKind.Percent => Val.Float(x % y),
                _ => throw ErrorAt(at.Location, TemplateMessages.TypeMismatch, "float " + op),
            };
        }

        Int128 a = ToInt(l, at.Location);
        Int128 c = ToInt(r, at.Location);
        switch (op)
        {
            case TokenKind.Plus: return Val.Int(a + c);
            case TokenKind.Minus: return Val.Int(a - c);
            case TokenKind.Star: return Val.Int(a * c);
            case TokenKind.Slash:
                if (c == 0)
                {
                    throw ErrorAt(at.Location, TemplateMessages.DivisionByZero);
                }

                return Val.Int(a / c);
            case TokenKind.Percent:
                if (c == 0)
                {
                    throw ErrorAt(at.Location, TemplateMessages.DivisionByZero);
                }

                return Val.Int(a % c);
            case TokenKind.Amp: return Val.Int(a & c);
            case TokenKind.Pipe: return Val.Int(a | c);
            case TokenKind.Caret: return Val.Int(a ^ c);
            case TokenKind.Shl:
                return Val.Int(c >= 128 || c < 0 ? 0 : a << (int)c);
            case TokenKind.Shr:
                return Val.Int(c >= 128 || c < 0 ? (a < 0 ? -1 : 0) : a >> (int)c);
        }

        throw ErrorAt(at.Location, TemplateMessages.TypeMismatch, op.ToString());
    }

    private Val Assign(AssignExpr a, Frame f)
    {
        Val value = Eval(a.Value, f);
        if (a.Op != TokenKind.Assign)
        {
            Val current = Eval(a.Target, f);
            TokenKind op = a.Op switch
            {
                TokenKind.PlusAssign => TokenKind.Plus,
                TokenKind.MinusAssign => TokenKind.Minus,
                TokenKind.StarAssign => TokenKind.Star,
                TokenKind.SlashAssign => TokenKind.Slash,
                TokenKind.PercentAssign => TokenKind.Percent,
                TokenKind.AmpAssign => TokenKind.Amp,
                TokenKind.PipeAssign => TokenKind.Pipe,
                TokenKind.CaretAssign => TokenKind.Caret,
                TokenKind.ShlAssign => TokenKind.Shl,
                _ => TokenKind.Shr,
            };
            value = Apply(op, current, value, a);
        }

        switch (a.Target)
        {
            case KeywordExpr { Keyword: KeywordKind.Dollar }:
            {
                long p = (long)ToInt(Deref(value), a.Location);
                if (p < 0)
                {
                    throw ErrorAt(a.Location, TemplateMessages.NegativePosition, p.ToString(CultureInfo.InvariantCulture));
                }

                CloseBitGroup(f);
                f.BitPos = -1;
                _pos = p;
                return Val.Int(p);
            }

            case NameExpr { Parts.Length: 1 } n:
                for (Frame? fr = f; fr is not null; fr = fr.Parent)
                {
                    if (fr.Locals.Find(n.Parts[0]) is { } v)
                    {
                        if (v.IsConst)
                        {
                            throw ErrorAt(a.Location, TemplateMessages.InvalidAssignment, n.Text);
                        }

                        Val coerced = Coerce(value, v.Type, a.Location);
                        TrackLocalMemory(v.Value, coerced);
                        v.Value = coerced;
                        return coerced;
                    }

                    if (fr.Members.ContainsKey(n.Parts[0]))
                    {
                        throw ErrorAt(a.Location, TemplateMessages.InvalidAssignment, n.Text);
                    }

                    if (fr.IsFunction)
                    {
                        break;
                    }
                }

                throw Undefined(n.Text, n.Location, f);
            default:
                throw ErrorAt(a.Location, TemplateMessages.InvalidAssignment, "expression");
        }
    }

    // ───────────── 値の比較と変換 ─────────────

    internal bool Truthy(Val v)
    {
        switch (v.Kind)
        {
            case VK.Bool:
            case VK.Int:
            case VK.Endian:
                return v.I != 0;
            case VK.Float:
                return v.F != 0;
            case VK.Str:
                return v.AsString.Length > 0;
            case VK.Null:
            case VK.Void:
                return false;
            case VK.Node:
            {
                TemplateNode n = v.AsNode;
                if (n.Kind == TemplateNodeKind.Pointer)
                {
                    return !(n.Pointer?.IsNull ?? true);
                }

                Val d = Deref(v);
                return d.Kind == VK.Node || Truthy(d);
            }

            default:
                return true;
        }
    }

    private bool ValuesEqual(Val a0, Val b0, Expr at)
    {
        if (a0.Kind == VK.Null || b0.Kind == VK.Null)
        {
            bool aNull = a0.Kind == VK.Null || (a0.Kind == VK.Node && a0.AsNode.Pointer?.IsNull == true);
            bool bNull = b0.Kind == VK.Null || (b0.Kind == VK.Node && b0.AsNode.Pointer?.IsNull == true);
            return aNull == bNull;
        }

        if (a0.Kind == VK.Mask || b0.Kind == VK.Mask)
        {
            (Val m, Val other) = a0.Kind == VK.Mask ? (a0, b0) : (b0, a0);
            (byte[] bytes, byte[] mask) = ((byte[], byte[]))m.O!;
            byte[] data = BytesOf(other, at.Location);
            return data.Length == bytes.Length && Syntax.HexPattern.Matches(data, bytes, mask);
        }

        if (a0.Kind == VK.Bytes || b0.Kind == VK.Bytes)
        {
            return BytesOf(a0, at.Location).AsSpan().SequenceEqual(BytesOf(b0, at.Location));
        }

        Val a = Deref(a0);
        Val b = Deref(b0);
        if (a.Kind == VK.Str || b.Kind == VK.Str)
        {
            if (a.Kind != VK.Str || b.Kind != VK.Str)
            {
                throw ErrorAt(at.Location, TemplateMessages.TypeMismatch, $"{KindName(a)} == {KindName(b)}");
            }

            return string.Equals(a.AsString, b.AsString, StringComparison.Ordinal);
        }

        if (a.Kind == VK.Node || b.Kind == VK.Node)
        {
            return a.Kind == VK.Node && b.Kind == VK.Node && ReferenceEquals(a.O, b.O);
        }

        if (a.Kind == VK.Float || b.Kind == VK.Float)
        {
            return ToDouble(a, at.Location) == ToDouble(b, at.Location);
        }

        return ToInt(a, at.Location) == ToInt(b, at.Location);
    }

    private int Compare(Val a0, Val b0, Expr at)
    {
        Val a = Deref(a0);
        Val b = Deref(b0);
        if (a.Kind == VK.Str && b.Kind == VK.Str)
        {
            return string.CompareOrdinal(a.AsString, b.AsString);
        }

        if (a.Kind == VK.Str || b.Kind == VK.Str || a.Kind == VK.Node || b.Kind == VK.Node)
        {
            throw ErrorAt(at.Location, TemplateMessages.TypeMismatch, $"{KindName(a)} < {KindName(b)}");
        }

        if (a.Kind == VK.Float || b.Kind == VK.Float)
        {
            return ToDouble(a, at.Location).CompareTo(ToDouble(b, at.Location));
        }

        return ToInt(a, at.Location).CompareTo(ToInt(b, at.Location));
    }

    internal static string KindName(Val v) => v.Kind switch
    {
        VK.Int => "integer",
        VK.Float => "float",
        VK.Str => "string",
        VK.Bool => "bool",
        VK.Node => "pattern " + v.AsNode.TypeName,
        VK.Bytes => "bytes",
        VK.Null => "null",
        _ => v.Kind.ToString().ToLowerInvariant(),
    };

    private Int128 ToInt(Val v, TemplateSourceLocation at)
    {
        v = Deref(v);
        return v.Kind switch
        {
            VK.Int or VK.Bool or VK.Endian or VK.BitOrder or VK.DateTime => v.I,
            VK.Float when double.IsFinite(v.F) => (Int128)Math.Truncate(v.F),
            VK.Null => 0,
            VK.Node when v.AsNode.Kind == TemplateNodeKind.Pointer => RawInt(v.AsNode),
            _ => throw ErrorAt(at, TemplateMessages.TypeMismatch, KindName(v) + " → integer"),
        };
    }

    private double ToDouble(Val v, TemplateSourceLocation at)
    {
        v = Deref(v);
        return v.Kind switch
        {
            VK.Float => v.F,
            VK.Int or VK.Bool => (double)v.I,
            _ => throw ErrorAt(at, TemplateMessages.TypeMismatch, KindName(v) + " → float"),
        };
    }

    private byte[] BytesOf(Val v, TemplateSourceLocation at)
    {
        switch (v.Kind)
        {
            case VK.Bytes:
                return (byte[])v.O!;
            case VK.Str:
                return System.Text.Encoding.UTF8.GetBytes(v.AsString);
            case VK.Node:
            {
                TemplateNode n = v.AsNode;
                if (n.Size > 64 * 1024 * 1024)
                {
                    throw ErrorAt(at, TemplateMessages.InvalidArgument, "bytes", "too long");
                }

                byte[] buf = new byte[n.Size];
                SpaceOfNode(n).Read(n.Offset, buf, this, n.Name);
                return buf;
            }

            default:
                throw ErrorAt(at, TemplateMessages.TypeMismatch, KindName(v) + " → bytes");
        }
    }

    internal string ToDisplayString(Val v) => v.Kind switch
    {
        VK.Float => FormatDouble(v.F),
        VK.Node => v.AsNode.Path,
        _ => v.ToString(),
    };

    internal static string FormatDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Inf" : "-Inf";
        }

        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>パターンを値として使う (整数・浮動小数点・文字列のパターンは値に、構造体などはパターンのまま)。</summary>
    internal Val Deref(Val v)
    {
        if (v.Kind != VK.Node)
        {
            return v;
        }

        return NodeValue(v.AsNode, converted: true);
    }

    // ───────────── メンバー・添字 ─────────────

    private Val Member(Val target, string name, Expr at)
    {
        if (target.Kind != VK.Node)
        {
            throw ErrorAt(at.Location, TemplateMessages.UnknownMember, KindName(target), name);
        }

        TemplateNode node = target.AsNode;
        if (node.Kind == TemplateNodeKind.Pointer)
        {
            TemplateNode? t = PointerTarget(node);
            if (t is null)
            {
                throw ErrorAt(at.Location, TemplateMessages.UnknownMember, node.Name, name);
            }

            node = t;
        }

        TemplateNode? child = MemberOf(node, name);
        if (child is null && name == "parent")
        {
            // parent.parent (TPL-04 の仕様 9)
            TemplateNode? owner = node.Owner;
            while (owner is { Kind: TemplateNodeKind.Array })
            {
                owner = owner.Owner;
            }

            if (owner is not null)
            {
                return Val.Node(owner);
            }
        }

        if (child is null)
        {
            throw ErrorAt(at.Location, TemplateMessages.UnknownMember, node.Path, name);
        }

        _touched = child.Offset;
        return Val.Node(child);
    }

    /// <summary>構造体のメンバー (評価していなければ評価する。<c>inline</c> の子も探す)。</summary>
    internal TemplateNode? MemberOf(TemplateNode node, string name)
    {
        if (node.Kind == TemplateNodeKind.Pointer)
        {
            TemplateNode? t = PointerTarget(node);
            return t is null ? null : MemberOf(t, name);
        }

        if (node.Children0 is LazyChildren lc)
        {
            Materialize(node);
            return FindIn(lc.Items, name);
        }

        if (node.Children0 is ListChildren list)
        {
            return FindIn(list.Items, name);
        }

        return null;
    }

    private TemplateNode? FindIn(List<TemplateNode>? items, string name)
    {
        if (items is null)
        {
            return null;
        }

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Name == name)
            {
                return items[i];
            }
        }

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].BaseName == name)
            {
                return items[i];
            }
        }

        foreach (TemplateNode n in items)
        {
            if (n.Attributes.Inline && MemberOf(n, name) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    private Val Index(IndexExpr ix, Frame f)
    {
        // ループで宣言した同じ名前のパターン (section[1] など。TPL-11 の仕様 2)
        if (ix.Target is NameExpr { Parts.Length: 1 } n)
        {
            for (Frame? fr = f; fr is not null; fr = fr.Parent)
            {
                if (fr.Locals.Find(n.Parts[0]) is not null)
                {
                    break;
                }

                if (fr.Members.TryGetValue(n.Parts[0], out List<TemplateNode>? list) && list.Count > 1)
                {
                    long i = EvalLong(ix.Index, f);
                    if (i < 0 || i >= list.Count)
                    {
                        throw ErrorAt(ix.Location, TemplateMessages.IndexOutOfRange, i.ToString(CultureInfo.InvariantCulture),
                            (list.Count - 1).ToString(CultureInfo.InvariantCulture));
                    }

                    return Val.Node(list[(int)i]);
                }
            }
        }

        Val target = Eval(ix.Target, f);
        long index = EvalLong(ix.Index, f);
        return IndexValue(target, index, ix.Location);
    }

    private Val IndexValue(Val target, long index, TemplateSourceLocation at)
    {
        switch (target.Kind)
        {
            case VK.Node:
            {
                TemplateNode node = target.AsNode;
                if (node.Kind == TemplateNodeKind.Pointer && PointerTarget(node) is { } t)
                {
                    node = t;
                }

                long count = ChildCount(node);
                if (index < 0 || index >= count)
                {
                    throw ErrorAt(at, TemplateMessages.IndexOutOfRange, index.ToString(CultureInfo.InvariantCulture),
                        (count - 1).ToString(CultureInfo.InvariantCulture));
                }

                TemplateNode child = GetChildRaw(node, index);
                _touched = child.Offset;
                return Val.Node(child);
            }

            case VK.Str:
            {
                string s = target.AsString;
                if (index < 0 || index >= s.Length)
                {
                    throw ErrorAt(at, TemplateMessages.IndexOutOfRange, index.ToString(CultureInfo.InvariantCulture),
                        (s.Length - 1).ToString(CultureInfo.InvariantCulture));
                }

                return Val.Int(s[(int)index]);
            }

            case VK.Bytes:
            {
                byte[] b = (byte[])target.O!;
                if (index < 0 || index >= b.Length)
                {
                    throw ErrorAt(at, TemplateMessages.IndexOutOfRange, index.ToString(CultureInfo.InvariantCulture),
                        (b.Length - 1).ToString(CultureInfo.InvariantCulture));
                }

                return Val.Int(b[index]);
            }

            case VK.List:
            {
                var list = (List<Val>)target.O!;
                if (index < 0 || index >= list.Count)
                {
                    throw ErrorAt(at, TemplateMessages.IndexOutOfRange, index.ToString(CultureInfo.InvariantCulture),
                        (list.Count - 1).ToString(CultureInfo.InvariantCulture));
                }

                return list[(int)index];
            }
        }

        throw ErrorAt(at, TemplateMessages.TypeMismatch, KindName(target) + "[]");
    }

    // ───────────── sizeof など ─────────────

    private Val Intrinsic(IntrinsicExpr ie, Frame f)
    {
        if (ie.Kind == IntrinsicKind.Sizeof)
        {
            RtType? type = null;
            if (ie.TypeOperand is not null)
            {
                type = ResolveType(ie.TypeOperand, f);
            }
            else if (ie.Operand is NameExpr n && !IsValueName(n, f) && TryResolveTypeName(n.Parts, f) is { } t)
            {
                type = t;
            }

            if (type is not null)
            {
                long? size = StaticSize(type);
                if (size is null)
                {
                    throw ErrorAt(ie.Location, TemplateMessages.SizeofVariable, type.Name);
                }

                return Val.Int(size.Value);
            }
        }

        Val v = Eval(ie.Operand!, f);
        if (v.Kind == VK.Str && ie.Kind == IntrinsicKind.Sizeof)
        {
            return Val.Int(System.Text.Encoding.UTF8.GetByteCount(v.AsString));
        }

        if (v.Kind == VK.Bytes && ie.Kind == IntrinsicKind.Sizeof)
        {
            return Val.Int(((byte[])v.O!).Length);
        }

        if (v.Kind == VK.Section && ie.Kind == IntrinsicKind.Sizeof)
        {
            var s = (TemplateSection)v.O!;
            ProduceSection(s);
            return Val.Int(s.Length);
        }

        if (v.Kind != VK.Node)
        {
            if (ie.Kind == IntrinsicKind.Typeof)
            {
                return Val.Str(KindName(v));
            }

            throw ErrorAt(ie.Location, TemplateMessages.NotAPattern, ie.Operand is NameExpr nn ? nn.Text : KindName(v));
        }

        TemplateNode node = v.AsNode;
        EnsureSize(node);
        return ie.Kind switch
        {
            IntrinsicKind.Sizeof => Val.Int(node.Size),
            IntrinsicKind.Addressof => Val.Int(node.Offset),
            IntrinsicKind.Endof => Val.Int(node.End),
            _ => Val.Str(node.TypeName),
        };
    }

    private bool IsValueName(NameExpr n, Frame f)
    {
        if (n.Parts.Length != 1)
        {
            return false;
        }

        for (Frame? fr = f; fr is not null; fr = fr.Parent)
        {
            if (fr.Locals.Find(n.Parts[0]) is not null || fr.Members.ContainsKey(n.Parts[0]))
            {
                return true;
            }
        }

        return false;
    }

    // ───────────── 呼び出し (TPL-11 の仕様 5、TPL-19) ─────────────

    private Val Call(CallExpr c, Frame f)
    {
        if (c.Callee is NameExpr n)
        {
            // 変数に入れた関数 (無名関数など)
            if (n.Parts.Length == 1 && FindVariable(n.Parts[0], f) is { Value.Kind: VK.Func } fv)
            {
                return Invoke((Callable)fv.Value.O!, EvalArgs(c.Args, f, null), c, f);
            }

            if (_program.FindFunction(n.Parts, f.Namespace) is { } fn)
            {
                return Invoke(new UserFunction(fn), EvalArgs(c.Args, f, fn), c, f);
            }

            string name = n.Text;
            if (IsBuiltinFunction(name))
            {
                return CallBuiltin(CanonicalBuiltin(name), c, f);
            }

            throw Undefined(name, n.Location, f);
        }

        Val callee = Eval(c.Callee, f);
        if (callee.Kind != VK.Func)
        {
            throw ErrorAt(c.Location, TemplateMessages.NotCallable, KindName(callee));
        }

        return Invoke((Callable)callee.O!, EvalArgs(c.Args, f, null), c, f);
    }

    private Variable? FindVariable(string name, Frame f)
    {
        for (Frame? fr = f; fr is not null; fr = fr.Parent)
        {
            if (fr.Locals.Find(name) is { } v)
            {
                return v;
            }

            if (fr.Members.ContainsKey(name))
            {
                return null;
            }
        }

        return null;
    }

    private Val[] EvalArgs(List<Expr> args, Frame f, FuncDecl? fn)
    {
        var values = new Val[args.Count];
        for (int i = 0; i < args.Count; i++)
        {
            values[i] = Eval(args[i], f);
        }

        return values;
    }

    /// <summary>関数を呼ぶ (書式・変換の関数からも使う)。</summary>
    internal Val Invoke(Callable fn, Val[] args, Expr? at, Frame f)
    {
        switch (fn)
        {
            case UserFunction uf when uf.Decl.IsExtern:
                return CallExtension(uf.Decl, args, at?.Location ?? _loc);
            case UserFunction uf:
                return CallUser(uf.Decl, args, at?.Location ?? _loc);
            case LambdaFunction lf:
            {
                EnterDepth("fn");
                try
                {
                    var scope = new Scope(lf.Scope);
                    for (int i = 0; i < lf.Expr.Params.Count; i++)
                    {
                        scope.Define(lf.Expr.Params[i], new Variable(i < args.Length ? args[i] : Val.Null, false, null));
                    }

                    var frame = new Frame
                    {
                        Node = lf.Frame.Node,
                        Parent = lf.Frame.Parent,
                        Locals = scope,
                        Space = lf.Frame.Space,
                        Namespace = lf.Frame.Namespace,
                        BigEndian = lf.Frame.BigEndian,
                        IsFunction = false,
                    };
                    foreach (var kv in lf.Frame.Members)
                    {
                        frame.Members[kv.Key] = kv.Value;
                    }

                    return Eval(lf.Expr.Body, frame);
                }
                finally
                {
                    LeaveDepth();
                }
            }

            case NativeFunction nf:
                return nf.Invoke(args);
            case BuiltinFunction bf:
            {
                var call = new CallExpr
                {
                    Callee = new NameExpr { Parts = bf.Name.Split("::"), Location = at?.Location ?? default },
                    Args = [.. args.Select(a => (Expr)new ValueExpr(a))],
                    Location = at?.Location ?? default,
                };
                return CallBuiltin(bf.Name, call, f);
            }
        }

        throw ErrorAt(at?.Location ?? _loc, TemplateMessages.NotCallable, fn.Name);
    }

    private Val CallUser(FuncDecl fn, Val[] args, TemplateSourceLocation? at)
    {
        if (args.Length != fn.Params.Count)
        {
            throw ErrorAt(at, TemplateMessages.ArgumentCount, fn.Name, fn.Params.Count.ToString(CultureInfo.InvariantCulture));
        }

        EnterDepth(fn.Name);
        try
        {
            var frame = new Frame
            {
                Node = _result.Root,
                Parent = _root,
                Locals = new Scope(null),
                Space = _space,
                Namespace = fn.Namespace,
                BigEndian = _root.BigEndian,
                IsFunction = true,
            };
            for (int i = 0; i < args.Length; i++)
            {
                Param p = fn.Params[i];
                Val v = args[i];
                if (!p.IsRef && !p.Type.IsAuto)
                {
                    RtType? pt = TryResolveTypeName(p.Type.Parts, frame);
                    if (pt is PrimType or EnumType)
                    {
                        v = Coerce(v, pt, at);
                    }
                }

                frame.Locals.Define(p.Name, new Variable(v, false, null));
            }

            _returnValue = Val.Void;
            TemplateSourceLocation? saveLoc = _loc;
            Flow flow = ExecBlock(fn.Body!.Body, frame);
            _loc = saveLoc;
            Val result = flow == Flow.Return ? _returnValue : Val.Void;
            if (fn.ReturnType is not null && !fn.ReturnType.IsAuto && result.Kind != VK.Void)
            {
                RtType? rt = TryResolveTypeName(fn.ReturnType.Parts, frame);
                if (rt is PrimType or EnumType)
                {
                    result = Coerce(result, rt, at);
                }
            }

            return result;
        }
        finally
        {
            LeaveDepth();
        }
    }

    /// <summary>
    /// 拡張関数の呼び出し (TPL-19 の仕様 2・4・6)。登録がなければエラー、許可がなければ失敗し、その部分だけが部分結果になる。
    /// </summary>
    private Val CallExtension(FuncDecl fn, Val[] args, TemplateSourceLocation? at)
    {
        var ext = _options.Extensions?.Find(fn.Name);
        if (ext is null)
        {
            throw ErrorAt(at, TemplateMessages.ExtensionNotRegistered, fn.Name);
        }

        if (!_options.Extensions!.IsPermitted(ext))
        {
            throw ErrorAt(at, TemplateMessages.ExtensionNotPermitted, fn.Name);
        }

        var values = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            values[i] = ToHost(args[i], fn.Params.ElementAtOrDefault(i)?.IsRef == true);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_token);
        cts.CancelAfter(_options.Extensions.CallTimeout);
        object? result;
        try
        {
            result = ext.Invoke(values, cts.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Print(_options.Extensions.Describe(ex));
            throw ErrorAt(at, TemplateMessages.ExtensionFailed, fn.Name, ex.Message);
        }

        CheckCancel();
        return FromHost(result, at);
    }

    private object? ToHost(Val v, bool asPattern)
    {
        if (v.Kind == VK.Node && asPattern)
        {
            return v.AsNode;
        }

        Val d = Deref(v);
        return d.Kind switch
        {
            VK.Int => d.I >= long.MinValue && d.I <= long.MaxValue ? (long)d.I : d.I,
            VK.Float => d.F,
            VK.Bool => d.I != 0,
            VK.Str => d.AsString,
            VK.Bytes => ((byte[])d.O!).Length <= 64 * 1024 * 1024 ? ((byte[])d.O!).Clone() : throw Error(TemplateMessages.InvalidArgument, "bytes", "64 MB"),
            VK.Node => d.AsNode,
            _ => null,
        };
    }

    private Val FromHost(object? value, TemplateSourceLocation? at) => value switch
    {
        null => Val.Null,
        bool b => Val.Bool(b),
        long l => Val.Int(l),
        int i => Val.Int(i),
        ulong u => Val.Int(u),
        uint u => Val.Int(u),
        Int128 i => Val.Int(i),
        double d => Val.Float(d),
        float fl => Val.Float(fl),
        decimal m => Val.Float((double)m),
        string s => Val.Str(s),
        byte[] b => Val.Bytes(b),
        _ => throw ErrorAt(at, TemplateMessages.TypeMismatch, value.GetType().Name),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private RtType? TryResolveTypeName(string[] parts, Frame f)
    {
        try
        {
            return ResolveType(new TypeRef { Parts = parts }, f);
        }
        catch (TemplateError)
        {
            return null;
        }
    }
}

/// <summary>評価済みの値を式として渡す (組み込み関数を関数の値として呼ぶとき)。</summary>
internal sealed class ValueExpr(Val value) : Expr
{
    public Val Value { get; } = value;
}
