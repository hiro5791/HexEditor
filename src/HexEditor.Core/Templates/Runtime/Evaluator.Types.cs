using System.Globalization;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    private readonly Dictionary<string, long?> _staticSizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sizing = new(StringComparer.Ordinal);

    // ───────────── 型の解決 ─────────────

    private RtType ResolveType(TypeRef t, Frame f)
    {
        if (t.Inline is StructDecl sd)
        {
            return new StructType
            {
                Name = sd.Name.Length > 0 ? sd.Name : sd.IsUnion ? "union" : "struct",
                Decl = sd,
                Attributes = sd.Attributes,
                Location = sd.Location,
                Docs = DocsOf(sd),
            };
        }

        if (t.Inline is BitfieldDecl bd)
        {
            return MakeBitfield(bd, f);
        }

        string name = string.Join("::", t.Parts);
        if (t.Parts.Length == 1)
        {
            for (Frame? fr = f; fr is not null; fr = fr.Parent)
            {
                if (fr.TypeArgs is not null && fr.TypeArgs.TryGetValue(name, out RtType? bound))
                {
                    return bound;
                }
            }
        }

        switch (name)
        {
            case "pstring":
            {
                PrimType inner = TypeArgPrim(t, 0, f);
                return new PrimType { Name = $"pstring<{inner.Name}>", Kind = PrimKind.PString, Inner = inner };
            }

            case "fixed":
            case "ufixed":
            {
                int i = (int)TypeArgValue(t, 0, f);
                int fr = (int)TypeArgValue(t, 1, f);
                if (i + fr is not (8 or 16 or 32 or 64))
                {
                    throw ErrorAt(t.Location, TemplateMessages.InvalidArgument, name, "I + F must be 8, 16, 32 or 64");
                }

                return new PrimType
                {
                    Name = $"{name}<{i}, {fr}>",
                    Kind = name == "fixed" ? PrimKind.Fixed : PrimKind.UFixed,
                    Size = (i + fr) / 8,
                    FracBits = fr,
                };
            }

            case "bits":
            {
                int n = (int)TypeArgValue(t, 0, f);
                if (n is < 1 or > 64)
                {
                    throw ErrorAt(t.Location, TemplateMessages.BitfieldTooWide, n.ToString(CultureInfo.InvariantCulture));
                }

                return new PrimType { Name = $"bits<{n}>", Kind = PrimKind.Bits, FracBits = n };
            }

            case "magic":
            case "std::type::magic":
            {
                if (t.TypeArgs.Count != 1 || t.TypeArgs[0].Value is not LiteralExpr { Value: string hex }
                    || !HexPattern.TryParse(hex, out byte[] bytes, out byte[] mask) || bytes.Length == 0)
                {
                    throw ErrorAt(t.Location, TemplateMessages.InvalidByteString, name);
                }

                return new PrimType { Name = $"magic<\"{hex}\">", Kind = PrimKind.Magic, Size = bytes.Length, MagicBytes = bytes, MagicMask = mask };
            }

            case "__unsupported":
                throw ErrorAt(t.Location, TemplateMessages.UnsupportedKaitai,
                    t.TypeArgs.Count > 0 && t.TypeArgs[0].Value is LiteralExpr { Value: string feature } ? feature : "?");
            case "std::type::Size":
            case "std::type::Bitmask":
            {
                PrimType inner = TypeArgPrim(t, 0, f);
                return new PrimType
                {
                    Name = $"{t.Parts[^1]}<{inner.Name}>",
                    Kind = name.EndsWith("Size", StringComparison.Ordinal) ? PrimKind.SizeValue : PrimKind.Bitmask,
                    Size = inner.Size,
                    Inner = inner,
                };
            }
        }

        if (BuiltinTypes.Find(name) is { } prim && _program.FindType(t.Parts, f.Namespace) is null)
        {
            return prim;
        }

        TypeDecl? decl = _program.FindType(t.Parts, f.Namespace);
        switch (decl)
        {
            case StructDecl s:
            {
                Dictionary<string, RtType>? typeArgs = null;
                Dictionary<string, Val>? valueArgs = null;
                var shown = new List<string>();
                for (int i = 0; i < s.TypeParams.Count; i++)
                {
                    TypeParam tp = s.TypeParams[i];
                    if (i >= t.TypeArgs.Count)
                    {
                        throw ErrorAt(t.Location, TemplateMessages.ArgumentCount, s.Name, s.TypeParams.Count.ToString(CultureInfo.InvariantCulture));
                    }

                    TypeArg arg = t.TypeArgs[i];
                    if (tp.IsValue)
                    {
                        Val v = arg.Value is not null ? Eval(arg.Value, f) : ResolveName(new NameExpr { Parts = arg.Type!.Parts, Location = t.Location }, f);
                        valueArgs ??= new Dictionary<string, Val>(StringComparer.Ordinal);
                        valueArgs[tp.Name] = Deref(v);
                        shown.Add(Deref(v).ToString());
                    }
                    else
                    {
                        if (arg.Type is null)
                        {
                            throw ErrorAt(t.Location, TemplateMessages.TypeMismatch, "type argument");
                        }

                        RtType at = ResolveType(arg.Type, f);
                        typeArgs ??= new Dictionary<string, RtType>(StringComparer.Ordinal);
                        typeArgs[tp.Name] = at;
                        shown.Add(at.Name);
                    }
                }

                string display = shown.Count == 0 ? s.Name : $"{s.Name}<{string.Join(", ", shown)}>";
                return new StructType
                {
                    Name = display,
                    Decl = s,
                    TypeArgs = typeArgs,
                    ValueArgs = valueArgs,
                    Attributes = s.Attributes,
                    Location = s.Location,
                    Docs = DocsOf(s),
                };
            }

            case EnumDecl e:
            {
                PrimType baseType = e.BaseType is null ? BuiltinTypes.Find("u32")! : ResolveType(e.BaseType, f) as PrimType
                    ?? throw ErrorAt(e.Location, TemplateMessages.TypeMismatch, "enum base type");
                return new EnumType { Name = e.Name, Decl = e, Base = baseType, Attributes = e.Attributes, Location = e.Location, Docs = DocsOf(e) };
            }

            case BitfieldDecl b:
                return MakeBitfield(b, f);
            case AliasDecl a:
            {
                if (!_sizing.Add("alias:" + a.QualifiedName))
                {
                    throw ErrorAt(a.Location, TemplateMessages.RecursionTooDeep, a.Name);
                }

                try
                {
                    var frame = new Frame { Node = f.Node, Locals = f.Locals, Space = f.Space, Namespace = a.Namespace, Parent = f };
                    RtType target = ResolveType(a.Target, frame);
                    return target.WithAttributes(a.Attributes, a.Name);
                }
                finally
                {
                    _sizing.Remove("alias:" + a.QualifiedName);
                }
            }
        }

        throw Undefined(name, t.Location, f);
    }

    private static string? DocsOf(Decl d) =>
        d.Attributes.LastOrDefault(a => a.Name == "docs")?.Args.FirstOrDefault() is LiteralExpr { Value: string s } ? s : d.DocComment;

    private BitfieldType MakeBitfield(BitfieldDecl b, Frame f)
    {
        PrimType? baseType = null;
        if (b.BaseType is not null)
        {
            baseType = ResolveType(b.BaseType, f) as PrimType;
            if (baseType is null || !baseType.IsInteger)
            {
                throw ErrorAt(b.Location, TemplateMessages.TypeMismatch, "bitfield base type");
            }
        }

        return new BitfieldType
        {
            Name = b.Name.Length > 0 ? b.Name : "bitfield",
            Decl = b,
            Base = baseType,
            Attributes = b.Attributes,
            Location = b.Location,
            Docs = DocsOf(b),
        };
    }

    private PrimType TypeArgPrim(TypeRef t, int index, Frame f)
    {
        if (index >= t.TypeArgs.Count || t.TypeArgs[index].Type is null)
        {
            throw ErrorAt(t.Location, TemplateMessages.ArgumentCount, string.Join("::", t.Parts), (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        RtType inner = ResolveType(t.TypeArgs[index].Type!, f);
        if (inner is EnumType et)
        {
            inner = et.Base;
        }

        if (inner is not PrimType p || !p.IsInteger)
        {
            throw ErrorAt(t.Location, TemplateMessages.TypeMismatch, "integer type");
        }

        return p;
    }

    private long TypeArgValue(TypeRef t, int index, Frame f)
    {
        if (index >= t.TypeArgs.Count)
        {
            throw ErrorAt(t.Location, TemplateMessages.ArgumentCount, string.Join("::", t.Parts), (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        TypeArg a = t.TypeArgs[index];
        Expr e = a.Value ?? new NameExpr { Parts = a.Type!.Parts, Location = t.Location };
        return EvalLong(e, f);
    }

    // ───────────── 固定長の判定 (TPL-28 の仕様 1) ─────────────

    /// <summary>
    /// 型のサイズを評価の前に決められるなら、そのサイズ (中身に可変長の配列・条件分岐・<c>$</c> への代入・ポインタ以外の
    /// <c>@</c> を含まない、または <c>[[size(…)]]</c> が定数)。
    /// </summary>
    internal long? StaticSize(RtType type)
    {
        switch (type)
        {
            case PrimType p:
                return p.Kind == PrimKind.Bits ? null : p.Size > 0 ? p.Size : null;
            case EnumType e:
                return e.Base.Size;
            case BitfieldType b:
                return BitfieldBytes(b);
            case StructType s:
            {
                string key = StructKey(s);
                if (_staticSizes.TryGetValue(key, out long? cached))
                {
                    return cached;
                }

                if (!_sizing.Add(key))
                {
                    return null; // 再帰する型は可変長
                }

                long? size;
                try
                {
                    size = ComputeStaticSize(s);
                }
                catch (TemplateError)
                {
                    size = null;
                }
                finally
                {
                    _sizing.Remove(key);
                }

                _staticSizes[key] = size;
                return size;
            }
        }

        return null;
    }

    private static string StructKey(StructType s)
    {
        string key = s.Decl.QualifiedName + "#" + s.Decl.Location;
        if (s.TypeArgs is not null)
        {
            key += "<" + string.Join(",", s.TypeArgs.Select(kv => kv.Key + "=" + kv.Value.Name)) + ">";
        }

        if (s.ValueArgs is not null)
        {
            key += "<" + string.Join(",", s.ValueArgs.Select(kv => kv.Key + "=" + kv.Value)) + ">";
        }

        if (s.Attributes.Count != s.Decl.Attributes.Count)
        {
            key += "@" + s.Attributes.Count;
        }

        return key;
    }

    private Frame TypeFrame(StructType s) => new()
    {
        Node = _result.Root,
        Locals = new Scope(null),
        Space = _docSpace,
        Namespace = s.Decl.Namespace,
        TypeArgs = s.TypeArgs,
        ValueArgs = s.ValueArgs,
    };

    private long? ComputeStaticSize(StructType s)
    {
        StructDecl d = s.Decl;
        Frame frame = TypeFrame(s);
        if (Attr(s.Attributes, "size") is { } sizeAttr && sizeAttr.Args.Count == 1)
        {
            return TryConst(sizeAttr.Args[0], frame) is { } c && c >= 0 ? (long)c : null;
        }

        if (d.Params.Count > 0 && d.Body.Count > 0)
        {
            // 引数で中身が変わる型は、中身の評価なしにはサイズが決まらないとみなす
            if (UsesParams(d))
            {
                return null;
            }
        }

        long total = 0;
        if (d.Base is not null)
        {
            if (ResolveType(d.Base, frame) is not StructType bs || StaticSize(bs) is not { } bsize)
            {
                return null;
            }

            total = bsize;
        }

        long max = 0;
        foreach (Stmt st in d.Body)
        {
            switch (st)
            {
                case PatternDeclStmt p:
                    foreach (Declarator decl in p.Declarators)
                    {
                        if (decl.Placement is not null || decl.BitWidth is not null
                            || decl.Attributes.Any(a => a.Name is "align" or "no_advance" or "size"))
                        {
                            return null;
                        }

                        long elem;
                        if (decl.IsPointer)
                        {
                            if (decl.PointerType is null || ResolveType(decl.PointerType, frame) is not PrimType pt || pt.Size == 0)
                            {
                                return null;
                            }

                            elem = pt.Size;
                            if (decl.PointerToArray)
                            {
                                if (decl.Array != ArrayKind.None)
                                {
                                    elem = pt.Size;
                                }

                                total = d.IsUnion ? total : total + elem;
                                max = Math.Max(max, elem);
                                continue;
                            }
                        }
                        else
                        {
                            RtType et = ResolveType(p.Type, frame);
                            if (StaticSize(et) is not { } es)
                            {
                                return null;
                            }

                            elem = es;
                        }

                        long count = 1;
                        if (decl.Array == ArrayKind.Count)
                        {
                            if (TryConst(decl.ArrayExpr!, frame) is not { } n || n < 0)
                            {
                                return null;
                            }

                            count = (long)n;
                        }
                        else if (decl.Array != ArrayKind.None)
                        {
                            return null;
                        }

                        long size = checked(elem * count);
                        if (d.IsUnion)
                        {
                            max = Math.Max(max, size);
                        }
                        else
                        {
                            total = checked(total + size);
                        }
                    }

                    break;
                case LocalDeclStmt:
                case DeclStmt:
                case EmptyStmt:
                case SectionDeclStmt:
                    break;
                case ExprStmt { Expr: CallExpr { Callee: NameExpr n } } when n.Text is "std::core::set_endian" or "std::core::print"
                    or "std::core::warn" or "std::print" or "print":
                    break;
                default:
                    return null;
            }
        }

        if (d.Instances.Count > 0)
        {
            // .ksy の instances は遅延評価なのでサイズに影響しない
        }

        return d.IsUnion ? Math.Max(total, max) : total;
    }

    private static bool UsesParams(StructDecl d)
    {
        var names = d.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        bool found = false;
        void Visit(Expr? e)
        {
            switch (e)
            {
                case null:
                    return;
                case NameExpr n when n.Parts.Length == 1 && names.Contains(n.Parts[0]):
                    found = true;
                    return;
                case BinaryExpr b:
                    Visit(b.Left);
                    Visit(b.Right);
                    return;
                case UnaryExpr u:
                    Visit(u.Operand);
                    return;
                case TernaryExpr t:
                    Visit(t.Condition);
                    Visit(t.Then);
                    Visit(t.Else);
                    return;
                case MemberExpr m:
                    Visit(m.Target);
                    return;
                case IndexExpr ix:
                    Visit(ix.Target);
                    Visit(ix.Index);
                    return;
                case CallExpr c:
                    c.Args.ForEach(Visit);
                    return;
            }
        }

        foreach (Stmt s in d.Body)
        {
            if (s is PatternDeclStmt p)
            {
                foreach (Declarator decl in p.Declarators)
                {
                    Visit(decl.ArrayExpr);
                    Visit(decl.Placement);
                }

                p.Type.Args.ForEach(Visit);
            }
        }

        return found;
    }

    /// <summary>定数として評価できる式の値 (できなければ null)。</summary>
    private Int128? TryConst(Expr e, Frame f)
    {
        switch (e)
        {
            case LiteralExpr { Value: Int128 i }:
                return i;
            case LiteralExpr { Value: bool b }:
                return b ? 1 : 0;
            case NameExpr n:
                if (n.Parts.Length == 1 && f.ValueArgs is not null && f.ValueArgs.TryGetValue(n.Parts[0], out Val v) && v.Kind == VK.Int)
                {
                    return v.I;
                }

                if (_program.FindConst(n.Parts, f.Namespace) is { } c)
                {
                    Val cv = ConstValue(c, n.Location);
                    return cv.Kind == VK.Int ? cv.I : null;
                }

                if (n.Parts.Length > 1 && _program.FindType(n.Parts[..^1], f.Namespace) is EnumDecl)
                {
                    Val ev = ResolveName(n, f);
                    return ev.Kind == VK.Int ? ev.I : null;
                }

                return null;
            case UnaryExpr u when TryConst(u.Operand, f) is { } x:
                return u.Op switch { TokenKind.Minus => -x, TokenKind.Tilde => ~x, TokenKind.Plus => x, TokenKind.Bang => x == 0 ? 1 : 0, _ => null };
            case BinaryExpr b when TryConst(b.Left, f) is { } l && TryConst(b.Right, f) is { } r:
                return b.Op switch
                {
                    TokenKind.Plus => l + r,
                    TokenKind.Minus => l - r,
                    TokenKind.Star => l * r,
                    TokenKind.Slash => r == 0 ? null : l / r,
                    TokenKind.Percent => r == 0 ? null : l % r,
                    TokenKind.Shl => r >= 0 && r < 128 ? l << (int)r : 0,
                    TokenKind.Shr => r >= 0 && r < 128 ? l >> (int)r : 0,
                    TokenKind.Amp => l & r,
                    TokenKind.Pipe => l | r,
                    TokenKind.Caret => l ^ r,
                    _ => null,
                };
            case IntrinsicExpr { Kind: IntrinsicKind.Sizeof } ie:
            {
                RtType? t = ie.TypeOperand is not null ? ResolveType(ie.TypeOperand, f)
                    : ie.Operand is NameExpr nn ? TryResolveTypeName(nn.Parts, f) : null;
                return t is null ? null : StaticSize(t);
            }
        }

        return null;
    }

    private long? BitfieldBytes(BitfieldType b)
    {
        if (b.Base is not null)
        {
            return b.Base.Size;
        }

        long bits = 0;
        Frame frame = new() { Node = _result.Root, Locals = new Scope(null), Space = _docSpace, Namespace = b.Decl.Namespace };
        foreach (BitfieldMember m in b.Decl.Members)
        {
            if (TryConst(m.Width, frame) is not { } w)
            {
                return null;
            }

            bits += (long)w;
        }

        return (bits + 7) / 8;
    }

    // ───────────── 列挙型 (TPL-05) ─────────────

    private EnumInfo EnumInfoOf(EnumDecl e)
    {
        if (_enumCache.TryGetValue(e, out EnumInfo? info))
        {
            return info;
        }

        info = new EnumInfo();
        _enumCache[e] = info;
        var frame = new Frame { Node = _result.Root, Locals = new Scope(null), Space = _docSpace, Namespace = e.Namespace };
        PrimType baseType = e.BaseType is null ? BuiltinTypes.Find("u32")! : (ResolveType(e.BaseType, frame) as PrimType ?? BuiltinTypes.Find("u32")!);
        Int128 next = 0;
        int bits = baseType.Size * 8;
        Int128 min = baseType.IsSigned ? -(Int128.One << (bits - 1)) : 0;
        Int128 max = baseType.IsSigned ? (Int128.One << (bits - 1)) - 1 : bits >= 127 ? Int128.MaxValue : (Int128.One << bits) - 1;
        foreach (EnumMember m in e.Members)
        {
            Int128 low = m.Value is null ? next : EvalInt(m.Value, frame);
            Int128 high = m.RangeEnd is null ? low : EvalInt(m.RangeEnd, frame);
            foreach (Int128 v in new[] { low, high })
            {
                if (v < min || v > max)
                {
                    throw ErrorAt(m.Location, TemplateMessages.EnumValueOverflow, "0x" + v.ToString("X", CultureInfo.InvariantCulture), baseType.Name);
                }
            }

            if (low == high)
            {
                if (!info.Exact.TryAdd(low, m.Name))
                {
                    Report(new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.DuplicateEnumValue,
                        ["0x" + low.ToString("X", CultureInfo.InvariantCulture), info.Exact[low]], m.Location), null);
                }
            }

            info.Members.Add((m.Name, low, high, m.DocComment));
            next = high + 1;
        }

        return info;
    }

    // ───────────── 属性 (TPL-13) ─────────────

    internal static readonly HashSet<string> KnownAttributes =
    [
        "color", "no_color", "name", "comment", "format", "format_entries", "read", "write", "hidden", "highlight_hidden", "inline",
        "sealed", "readonly", "lazy", "size", "align", "no_advance", "endian", "bit_order", "encoding", "base", "relative",
        "pointer_map", "allow_null", "expect", "require", "show", "show_all", "visualize", "export", "bookmark", "inspector", "docs",

        // 構造を理解した比較 (06 の ANA-31) の配列のキー。比較以外の動作は変えない
        "diff_key",

        // .ksy の変換で使う (TPL-33)
        "substream", "ksy_enum", "instance", "ksy_root_io",
    ];

    internal static AttributeNode? Attr(List<AttributeNode> list, string name)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Name == name)
            {
                return list[i];
            }
        }

        return null;
    }

    private void CheckUnknownAttributes(List<AttributeNode> list)
    {
        foreach (AttributeNode a in list)
        {
            if (!KnownAttributes.Contains(a.Name) && _warnedAttributes.Add(a))
            {
                Report(new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.UnknownAttribute, [a.Name], a.Location), null);
            }
        }
    }

    /// <summary>引数が文字列の属性 (<c>color("…")</c> など)。引数の型が違えばエラー。</summary>
    private string? StringAttr(List<AttributeNode> list, string name, Frame f)
    {
        if (Attr(list, name) is not { } a)
        {
            return null;
        }

        if (a.Args.Count == 0)
        {
            return string.Empty;
        }

        Val v = Deref(Eval(a.Args[0], f));
        if (v.Kind != VK.Str)
        {
            throw ErrorAt(a.Location, TemplateMessages.TypeMismatch, $"'{name}' takes a string");
        }

        return v.AsString;
    }

    private TemplateNodeAttributes BuildAttributes(List<AttributeNode> attrs, Frame f)
    {
        if (attrs.Count == 0)
        {
            return TemplateNodeAttributes.None;
        }

        var a = new TemplateNodeAttributes
        {
            Color = StringAttr(attrs, "color", f),
            NoColor = Attr(attrs, "no_color") is not null,
            Hidden = Attr(attrs, "hidden") is not null || Attr(attrs, "highlight_hidden") is not null,
            HighlightHidden = Attr(attrs, "highlight_hidden") is not null,
            Inline = Attr(attrs, "inline") is not null,
            Sealed = Attr(attrs, "sealed") is not null,
            ReadOnly = Attr(attrs, "readonly") is not null,
            Lazy = Attr(attrs, "lazy") is not null,
            ShowAll = Attr(attrs, "show_all") is not null,
            Bookmark = StringAttr(attrs, "bookmark", f),
            Docs = StringAttr(attrs, "docs", f),
            Encoding = StringAttr(attrs, "encoding", f),
        };
        if (Attr(attrs, "export") is { Args.Count: > 0 } ex)
        {
            a.Export = Truthy(Eval(ex.Args[0], f));
        }

        if (Attr(attrs, "visualize") is { } vis && vis.Args.Count > 0)
        {
            a.VisualizeKind = ToDisplayString(Deref(Eval(vis.Args[0], f)));
            a.VisualizeArguments = [.. vis.Args.Skip(1).Select(x => ToDisplayString(Deref(Eval(x, f))))];
        }

        return a;
    }

    private bool? EndianAttr(List<AttributeNode> attrs, Frame f)
    {
        if (Attr(attrs, "endian") is not { Args.Count: > 0 } a)
        {
            return null;
        }

        Val v = Eval(a.Args[0], f);
        return v.Kind switch
        {
            VK.Endian => v.I != 0,
            VK.Str => v.AsString == "big",
            _ => throw ErrorAt(a.Location, TemplateMessages.TypeMismatch, "'endian' takes little or big"),
        };
    }

    private bool? BitOrderAttr(List<AttributeNode> attrs, Frame f)
    {
        if (Attr(attrs, "bit_order") is not { Args.Count: > 0 } a)
        {
            return null;
        }

        Val v = Eval(a.Args[0], f);
        return v.Kind switch
        {
            VK.BitOrder => v.I != 0,
            VK.Str => v.AsString == "msb_first",
            _ => throw ErrorAt(a.Location, TemplateMessages.TypeMismatch, "'bit_order' takes lsb_first or msb_first"),
        };
    }
}
