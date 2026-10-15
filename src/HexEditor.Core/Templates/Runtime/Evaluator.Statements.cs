using System.Runtime.CompilerServices;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    // ───────────── 文 (TPL-11) ─────────────

    private Flow ExecStatement(Stmt s, Frame f)
    {
        Step();
        _loc = s.Location;
        if (Debugging)
        {
            DebugBefore(s, f);
        }

        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw Error(TemplateMessages.RecursionTooDeep, Limits.Recursion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        switch (s)
        {
            case PatternDeclStmt p when !ReferenceEquals(p, _resolvingInstance) && p.Declarators.Count == 1 && Attr(p.Declarators[0].Attributes, "instance") is not null:
                DeclareInstance(p, p.Declarators[0].Name ?? "instance", f);
                return Flow.Normal;
            case LocalDeclStmt l when !ReferenceEquals(l, _resolvingInstance) && Attr(l.Attributes, "instance") is not null:
                DeclareInstance(l, l.Name, f);
                return Flow.Normal;
            case PatternDeclStmt p when Debugging:
            {
                int before = f.Children?.Count ?? 0;
                DeclarePatterns(p, f);
                DebugAfterPatterns(p, f, before);
                return Flow.Normal;
            }

            case PatternDeclStmt p:
                DeclarePatterns(p, f);
                return Flow.Normal;
            case LocalDeclStmt l:
                DeclareLocal(l, f);
                return Flow.Normal;
            case ExprStmt e:
                _touched = -1;
                Eval(e.Expr, f);
                return Flow.Normal;
            case BlockStmt b:
                return ExecBlock(b.Body, f);
            case IfStmt i:
                if (Truthy(Eval(i.Condition, f)))
                {
                    return ExecStatement(i.Then, f);
                }

                return i.Else is null ? Flow.Normal : ExecStatement(i.Else, f);
            case SwitchStmt sw:
                return ExecSwitch(sw, f);
            case ForStmt fs:
                return ExecFor(fs, f);
            case WhileStmt w:
                return ExecWhile(w, f);
            case BreakStmt:
                return Flow.Break;
            case ContinueStmt:
                return Flow.Continue;
            case ReturnStmt r:
                _returnValue = r.Value is null ? Val.Void : Eval(r.Value, f);
                return Flow.Return;
            case SectionDeclStmt sd:
                DeclareSection(sd, f);
                return Flow.Normal;
            case DeclStmt { Decl: ConstDecl c }:
                if (!ReferenceEquals(f, _root) || f.IsFunction)
                {
                    DeclareLocal(c.Statement, f);
                }

                return Flow.Normal;
            case DeclStmt:
            case EmptyStmt:
            case ImportStmt:
            case NamespaceStmt:
            case UsingNamespaceStmt:
                return Flow.Normal;
            default:
                return Flow.Normal;
        }
    }

    private Flow ExecBlock(List<Stmt> body, Frame f)
    {
        foreach (Stmt s in body)
        {
            Flow flow = ExecStatement(s, f);
            if (flow != Flow.Normal)
            {
                return flow;
            }

            if (f.StopWhen is { } stop && stop())
            {
                return Flow.Break;
            }
        }

        return Flow.Normal;
    }

    private Flow ExecSwitch(SwitchStmt sw, Frame f)
    {
        Val value = Eval(sw.Value, f);
        SwitchCase? chosen = null;
        foreach (SwitchCase c in sw.Cases)
        {
            if (c.IsDefault)
            {
                continue;
            }

            foreach (CaseLabel label in c.Labels)
            {
                Val v = Eval(label.Value, f);
                bool match;
                if (label.RangeEnd is not null)
                {
                    Val hi = Eval(label.RangeEnd, f);
                    match = Compare(value, v, label.Value) >= 0 && Compare(value, hi, label.Value) <= 0;
                }
                else
                {
                    match = ValuesEqual(value, v, label.Value);
                }

                if (match)
                {
                    chosen = c;
                    break;
                }
            }

            if (chosen is not null)
            {
                break;
            }
        }

        chosen ??= sw.Cases.FirstOrDefault(c => c.IsDefault);
        if (chosen is null)
        {
            return Flow.Normal;
        }

        // C と違い、次の case へは続けない (TPL-11 の仕様 1)
        Flow flow = ExecBlock(chosen.Body, f);
        return flow == Flow.Break ? Flow.Normal : flow;
    }

    private Flow ExecFor(ForStmt fs, Frame f)
    {
        if (fs.Init is not null)
        {
            ExecStatement(fs.Init, f);
        }

        while (true)
        {
            Step();
            if (fs.Condition is not null && !Truthy(Eval(fs.Condition, f)))
            {
                return Flow.Normal;
            }

            Flow flow = ExecStatement(fs.Body, f);
            if (flow == Flow.Break)
            {
                return Flow.Normal;
            }

            if (flow == Flow.Return)
            {
                return flow;
            }

            if (fs.Update is not null)
            {
                Eval(fs.Update, f);
            }
        }
    }

    private Flow ExecWhile(WhileStmt w, Frame f)
    {
        if (w.IsDoWhile)
        {
            while (true)
            {
                Flow flow = ExecStatement(w.Body, f);
                if (flow == Flow.Break)
                {
                    return Flow.Normal;
                }

                if (flow == Flow.Return)
                {
                    return flow;
                }

                Step();
                if (!Truthy(Eval(w.Condition, f)))
                {
                    return Flow.Normal;
                }
            }
        }

        while (true)
        {
            Step();
            if (!Truthy(Eval(w.Condition, f)))
            {
                return Flow.Normal;
            }

            Flow flow = ExecStatement(w.Body, f);
            if (flow == Flow.Break)
            {
                return Flow.Normal;
            }

            if (flow == Flow.Return)
            {
                return flow;
            }
        }
    }

    // ───────────── ローカル変数・定数 (TPL-11 の仕様 3・4) ─────────────

    private void DeclareLocal(LocalDeclStmt l, Frame f)
    {
        RtType? type = l.Type.IsAuto ? null : ResolveType(l.Type, f);
        Val value = l.Init is null ? DefaultValue(type) : Eval(l.Init, f);
        value = Coerce(value, type, l.Location);
        TrackLocalMemory(null, value);
        var v = new Variable(value, l.IsConst, type);
        f.Locals.Define(l.Name, v);
        CheckUnknownAttributes(l.Attributes);
        if (l.Attributes.Any(a => a.Name == "show") && !f.IsFunction)
        {
            // 計算値として結果ツリーに斜体で表示する
            var node = new TemplateNode(_result, TemplateNodeKind.Computed, l.Name, type?.Name ?? "auto", _pos, 0)
            {
                Owner = f.Node,
                Declaration = l.Location,
                DocComment = l.DocComment,
                ComputedVariable = v,
                Context = f,
                AttributeNodes = l.Attributes,
                Section = _space.Section,
            };
            v.ShowNode = node;
            CountPattern();
            AttachChild(f, node);
        }
    }

    private void TrackLocalMemory(Val? old, Val value)
    {
        if (old is { Kind: VK.Str } o)
        {
            _localBytes -= o.AsString.Length * 2L;
        }

        if (value.Kind == VK.Str)
        {
            _localBytes += value.AsString.Length * 2L;
        }
    }

    private static Val DefaultValue(RtType? type) => type switch
    {
        PrimType { Kind: PrimKind.CString or PrimKind.WString or PrimKind.PString } => Val.Str(string.Empty),
        PrimType { Kind: PrimKind.Bool } => Val.False,
        PrimType p when p.IsFloat => Val.Float(0),
        _ => Val.Int(0),
    };

    /// <summary>値を型に合わせる (整数は代入先の型の幅で切り捨てる。TPL-02 の仕様 8)。</summary>
    private Val Coerce(Val value, RtType? type, TemplateSourceLocation? at)
    {
        if (type is null)
        {
            return Deref(value);
        }

        if (type is EnumType et)
        {
            type = et.Base;
        }

        if (type is not PrimType p)
        {
            return value;
        }

        Val v = Deref(value);
        switch (p.Kind)
        {
            case PrimKind.CString:
            case PrimKind.WString:
            case PrimKind.PString:
                if (v.Kind is VK.Str)
                {
                    return v;
                }

                if (v.Kind is VK.Null)
                {
                    return Val.Str(string.Empty);
                }

                throw ErrorAt(at, TemplateMessages.TypeMismatch, $"{KindName(v)} → {p.Name}");
            case PrimKind.Bool:
                return Val.Bool(Truthy(v));
        }

        if (p.IsFloat)
        {
            return v.Kind switch
            {
                VK.Float => p.Size == 4 ? Val.Float((float)v.F) : v,
                VK.Int or VK.Bool => Val.Float((double)v.I),
                _ => throw ErrorAt(at, TemplateMessages.TypeMismatch, $"{KindName(v)} → {p.Name}"),
            };
        }

        if (p.IsInteger || p.Size > 0)
        {
            Int128 i = v.Kind switch
            {
                VK.Int or VK.Bool or VK.Endian => v.I,
                VK.Float => double.IsFinite(v.F) ? (Int128)Math.Truncate(v.F) : 0,
                VK.Null => 0,
                VK.DateTime => v.I,
                _ => throw ErrorAt(at, TemplateMessages.TypeMismatch, $"{KindName(v)} → {p.Name}"),
            };
            int bits = p.Kind is PrimKind.Bits ? p.FracBits : p.Size * 8;
            return bits is > 0 and < 128 ? Val.Int(Truncate(i, bits, p.IsSigned)) : Val.Int(i);
        }

        return v;
    }

    internal static Int128 Truncate(Int128 v, int bits, bool signed)
    {
        if (bits >= 128)
        {
            return v;
        }

        UInt128 mask = (UInt128.One << bits) - 1;
        UInt128 u = (UInt128)v & mask;
        if (signed && (u >> (bits - 1)) != 0)
        {
            return (Int128)(u | ~mask);
        }

        return (Int128)u;
    }

    // ───────────── セクション (TPL-15) ─────────────

    private void DeclareSection(SectionDeclStmt sd, Frame f)
    {
        var section = new TemplateSection(sd.Name, _options.TempDirectory) { Index = Sections.Count };
        lock (_result.SyncRoot)
        {
            Sections.Add(section);
        }

        f.Locals.Define(sd.Name, new Variable(Val.Section(section), isConst: true, null));
    }

    /// <summary>セクションの見出しのノード (最初にパターンを置いたときに、適用のノードの下に作る)。</summary>
    private TemplateNode SectionNode(TemplateSection section)
    {
        if (section.DisplayNode is { } existing)
        {
            return existing;
        }

        int sameName = Sections.Count(s => s.Name == section.Name && s.DisplayNode is not null);
        string name = sameName == 0 ? section.Name : $"{section.Name}[{sameName}]";
        var node = new TemplateNode(_result, TemplateNodeKind.Section, name, "section", 0, section.Length)
        {
            Owner = _result.Root,
            Section = section,
        };
        node.Children0 = new ListChildren();
        section.DisplayNode = node;
        CountPattern();
        lock (_result.SyncRoot)
        {
            ((ListChildren)_result.Root.Children0!).Items.Add(node);
        }

        return node;
    }

    private Space SpaceOf(TemplateSection section)
    {
        ProduceSection(section);
        string key = section.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!_sectionSpaces.TryGetValue(key, out Space? space))
        {
            space = new Space(section);
            _sectionSpaces[key] = space;
        }

        return space;
    }

    /// <summary>セクションの中身を作る (展開は必要になったときに行う。TPL-15 の仕様 7)。</summary>
    private void ProduceSection(TemplateSection section)
    {
        if (section.PendingProducers.Count == 0)
        {
            return;
        }

        List<Action> pending = [.. section.PendingProducers];
        section.PendingProducers.Clear();
        foreach (Action produce in pending)
        {
            produce();
        }

        foreach (Space s in _sectionSpaces.Values)
        {
            if (s.Section == section)
            {
                s.Invalidate();
            }
        }

        if (section.DisplayNode is { } node)
        {
            node.Size = section.Length;
        }
    }
}
