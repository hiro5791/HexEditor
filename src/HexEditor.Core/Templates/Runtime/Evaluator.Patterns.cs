using System.Globalization;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

internal sealed partial class Evaluator
{
    // ───────────── パターンの宣言 (TPL-04、TPL-07〜TPL-10) ─────────────

    private void DeclarePatterns(PatternDeclStmt s, Frame f)
    {
        if (f.IsFunction)
        {
            throw ErrorAt(s.Location, TemplateMessages.PatternInFunction);
        }

        foreach (Declarator d in s.Declarators)
        {
            DeclareOne(s, d, f);
        }
    }

    private void AttachChild(Frame f, TemplateNode node)
    {
        if (f.Children is not null)
        {
            lock (_result.SyncRoot)
            {
                f.Children.Add(node);
            }
        }
    }

    private void RegisterMember(Frame f, TemplateNode node)
    {
        if (!f.Members.TryGetValue(node.Name, out List<TemplateNode>? list))
        {
            f.Members[node.Name] = [node];
            return;
        }

        list.Add(node);

        // ループの中で宣言したパターンは配列のように name[0]、name[1] と表示する (TPL-11 の仕様 2)
        if (list.Count == 2)
        {
            list[0].BaseName = list[0].Name;
            list[0].Name = list[0].Name + "[0]";
        }

        node.BaseName = node.Name;
        node.Name = $"{node.Name}[{list.Count - 1}]";
    }

    private void DeclareOne(PatternDeclStmt s, Declarator d, Frame f)
    {
        _loc = d.Location;
        CheckUnknownAttributes(d.Attributes);
        RtType type;
        try
        {
            type = ResolveType(s.Type, f);
        }
        catch (TemplateError e) when (d.Placement is not null && !e.IsLimit)
        {
            // 配置したパターンの型のエラー (.ksy の対応していない機能など) は、そのパターンだけのエラーにする
            var errorNode = new TemplateNode(_result, TemplateNodeKind.Error, d.Name ?? "padding", s.Type.Text, _pos, 0)
            {
                Owner = f.Node,
                Declaration = d.Location,
                Section = _space.Section,
            };
            CountPattern();
            AttachChild(f, errorNode);
            RegisterMember(f, errorNode);
            RecordError(e, errorNode);
            return;
        }

        CheckUnknownAttributes(type.Attributes);
        List<AttributeNode> attrs = type.Attributes.Count == 0 ? d.Attributes : [.. type.Attributes, .. d.Attributes];
        bool big = s.BigEndianKeyword ?? EndianAttr(d.Attributes, f) ?? EndianAttr(type.Attributes, f) ?? f.BigEndian;
        string name = d.Name ?? "padding";

        if (d.Placement is not null)
        {
            // 配置 (@): 現在位置は変えない (TPL-09 の仕様 2・3)。失敗はそのパターンだけのエラー (TPL-27)
            long offset = 0;
            Space space = _space;
            TemplateNode? sectionNode = null;
            long savePos = _pos;
            Space saveSpace = _space;
            long saveBits = f.BitPos;
            Space saveFrameSpace = f.Space;
            try
            {
                offset = EvalLong(d.Placement, f);
                if (d.PlacementSection is not null)
                {
                    TemplateSection section = SectionByName(d.PlacementSection, f, d.Location);
                    space = SpaceOf(section);
                    sectionNode = SectionNode(section);
                    sectionNode.Size = section.Length;
                }

                if (offset < 0 || offset > space.Length || (offset == space.Length && !IsZeroSized(type, d)))
                {
                    throw ErrorAt(d.Location, TemplateMessages.PlacementOutOfRange, offset.ToString("X", CultureInfo.InvariantCulture)) is var e
                        ? new TemplateError(e.Diagnostic with { DataOffset = offset })
                        : null!;
                }

                _pos = offset;
                _space = space;
                f.BitPos = -1;
                f.Space = space;
                TemplateNode node = CreatePattern(s, d, type, attrs, f, offset, space, big, n =>
                {
                    if (sectionNode is not null)
                    {
                        n.DisplayParentOverride = sectionNode;
                        lock (_result.SyncRoot)
                        {
                            ((ListChildren)sectionNode.Children0!).Items.Add(n);
                        }
                    }
                    else
                    {
                        AttachChild(f, n);
                    }

                    RegisterMember(f, n);
                });
                AfterDeclare(node, d, f);
            }
            catch (TemplateError e) when (!e.IsLimit)
            {
                var errorNode = new TemplateNode(_result, TemplateNodeKind.Error, name, type.Name, offset, 0)
                {
                    Owner = f.Node,
                    Declaration = d.Location,
                    Section = space.Section,
                };
                CountPattern();
                if (sectionNode is not null)
                {
                    errorNode.DisplayParentOverride = sectionNode;
                }

                AttachChild(f, errorNode);
                RegisterMember(f, errorNode);
                TemplateDiagnostic diag = e.Diagnostic with { Path = errorNode.Path, Location = e.Diagnostic.Location ?? d.Location };
                e.Diagnostic = diag;
                Reported.Add(e);
                Report(diag, errorNode);
            }
            finally
            {
                _pos = savePos;
                _space = saveSpace;
                f.BitPos = saveBits;
                f.Space = saveFrameSpace;
            }

            return;
        }

        // C 風のビットフィールド (u16 a : 4, b : 12;)
        if (d.BitWidth is not null)
        {
            DeclareInlineBits(s, d, type, attrs, f, big);
            return;
        }

        CloseBitGroup(f);

        // ビット単位の整数 (.ksy の b1〜b64) が続くときは、バイト境界にそろえずにビットの位置から読み続ける (Kaitai Struct と同じ。
        // バイト単位の読み込みの前にだけそろえる)
        if (type is not PrimType { Kind: PrimKind.Bits })
        {
            AlignToByte(f);
        }

        if (Attr(d.Attributes, "align") is { Args.Count: > 0 } align)
        {
            long n = EvalLong(align.Args[0], f);
            if (n > 1)
            {
                _pos = (_pos + n - 1) / n * n;
            }
        }

        long start = _pos;
        TemplateNode? created = null;
        try
        {
            TemplateNode node = CreatePattern(s, d, type, attrs, f, start, _space, big, n =>
            {
                created = n;
                AttachChild(f, n);
                RegisterMember(f, n);
            });
            bool advance = Attr(d.Attributes, "no_advance") is null;
            if (type is PrimType { Kind: PrimKind.Bits } && d.Array == ArrayKind.None)
            {
                // ビット単位のパターンはビットの位置を進める
            }
            else if (advance)
            {
                _pos = node.End;
                f.MaxEnd = f.HasPattern ? Math.Max(f.MaxEnd, node.End) : node.End;
                f.HasPattern = true;
            }
            else
            {
                _pos = start;
            }

            if (f.IsUnion)
            {
                _pos = f.Start;
            }

            AfterDeclare(node, d, f);
        }
        catch (TemplateError e) when (!e.IsLimit && !Reported.Contains(e))
        {
            // エラーのパターンを結果ツリーにエラーのノードとして残す (TPL-27 の仕様 2)
            TemplateNode errorNode = created ?? new TemplateNode(_result, TemplateNodeKind.Error, name, type.Name, start, 0)
            {
                Owner = f.Node,
                Declaration = d.Location,
                Section = _space.Section,
            };
            if (created is null)
            {
                CountPattern();
                AttachChild(f, errorNode);
                RegisterMember(f, errorNode);
            }

            RecordError(e, errorNode);
            throw;
        }
    }

    private readonly Dictionary<StructDecl, bool> _hasPointers = [];

    private bool HasPointers(StructDecl decl)
    {
        if (_hasPointers.TryGetValue(decl, out bool has))
        {
            return has;
        }

        static bool Scan(IEnumerable<Stmt> body) => body.Any(s => s switch
        {
            PatternDeclStmt p => p.Declarators.Any(d => d.IsPointer),
            BlockStmt b => Scan(b.Body),
            IfStmt i => Scan([i.Then]) || (i.Else is not null && Scan([i.Else])),
            SwitchStmt sw => sw.Cases.Any(c => Scan(c.Body)),
            ForStmt f => Scan([f.Body]),
            WhileStmt w => Scan([w.Body]),
            _ => false,
        });

        has = Scan(decl.Body);
        _hasPointers[decl] = has;
        return has;
    }

    private static bool IsZeroSized(RtType type, Declarator d) =>
        d.Array is ArrayKind.Eof or ArrayKind.While || (d.Array == ArrayKind.Count && d.ArrayExpr is LiteralExpr { Value: Int128 n } && n == 0);

    /// <summary>宣言の後の処理 (expect / require の確認、last の更新。TPL-13)。</summary>
    private void AfterDeclare(TemplateNode node, Declarator d, Frame f)
    {
        f.Last = node;
        Interlocked.Exchange(ref ProgressOffset, node.Offset);
        foreach (AttributeNode a in d.Attributes)
        {
            if (a.Name is not ("expect" or "require") || a.Args.Count == 0)
            {
                continue;
            }

            TemplateNode? saveThis = _thisNode;
            _thisNode = node;
            bool ok;
            string message;
            try
            {
                ok = Truthy(Eval(a.Args[0], f));
                message = a.Args.Count > 1 ? ToDisplayString(Deref(Eval(a.Args[1], f))) : a.Name;
            }
            finally
            {
                _thisNode = saveThis;
            }

            if (ok)
            {
                continue;
            }

            if (a.Name == "expect")
            {
                var diag = new TemplateDiagnostic(TemplateSeverity.Warning, TemplateMessages.ExpectFailed, [message], a.Location, node.Offset, node.Path,
                    [.. _callStack]);
                Report(diag, node);
            }
            else
            {
                throw ErrorAt(a.Location, TemplateMessages.RequireFailed, message);
            }
        }
    }

    private TemplateSection SectionByName(string name, Frame f, TemplateSourceLocation at)
    {
        for (Frame? fr = f; fr is not null; fr = fr.Parent)
        {
            if (fr.Locals.Find(name) is { Value.Kind: VK.Section } v)
            {
                return (TemplateSection)v.Value.O!;
            }
        }

        throw ErrorAt(at, TemplateMessages.Undefined, name, string.Empty);
    }

    /// <summary>1 つの宣言子のパターンを作る。<paramref name="attach"/> は作った直後 (中身の評価の前) に呼ぶ。</summary>
    private TemplateNode CreatePattern(PatternDeclStmt s, Declarator d, RtType type, List<AttributeNode> attrs, Frame f, long offset, Space space,
        bool big, Action<TemplateNode> attach)
    {
        string name = d.Name ?? "padding";
        if (d.IsPointer)
        {
            return CreatePointerDecl(s, d, type, attrs, f, offset, space, big, attach);
        }

        if (d.Array != ArrayKind.None)
        {
            return CreateArray(s, d, type, attrs, f, offset, space, big, attach, name);
        }

        Val[] args = EvalArgs(s.Type.Args, f, null);
        TemplateNode node = CreateSingle(type, name, offset, space, f, big, attrs, args, attach, -1, d.Location, s.DocComment);
        return node;
    }

    /// <summary>1 つのパターン (配列・ポインタ以外)。</summary>
    private TemplateNode CreateSingle(RtType type, string name, long offset, Space space, Frame f, bool big, List<AttributeNode> attrs, Val[] args,
        Action<TemplateNode>? attach, long arrayIndex, TemplateSourceLocation? declaredAt, string? doc, TemplateNode? owner = null)
    {
        TemplateNode node = new(_result, KindOf(type), name, type.Name, offset, 0)
        {
            Owner = owner ?? f.Node,
            Type = type,
            Section = space.Section,
            BigEndian = big,
            ArrayIndex = arrayIndex,
            Declaration = declaredAt,
            TypeDeclaration = type.Location,
            DocComment = doc ?? (type as StructType)?.Decl.DocComment ?? (type as EnumType)?.Decl.DocComment,
            Context = f,
            AttributeNodes = attrs,
        };
        node.Attributes = BuildAttributes(attrs, f);
        node.DisplayNameAttr = Attr(attrs, "name") is { Args.Count: > 0 } na ? ToDisplayString(Deref(Eval(na.Args[0], f))) : null;
        if (node.Attributes.Docs is null && type.Docs is not null)
        {
            if (node.Attributes == TemplateNodeAttributes.None)
            {
                node.Attributes = new TemplateNodeAttributes();
            }

            node.Attributes.Docs = type.Docs;
        }

        CountPattern();
        switch (type)
        {
            case PrimType p:
                node.Size = PrimSize(p, offset, space, f, node);
                CheckRange(node, space);
                if (p.Kind == PrimKind.Magic)
                {
                    byte[] data = new byte[p.Size];
                    space.Read(offset, data, this, name);
                    if (!HexPattern.Matches(data, p.MagicBytes!, p.MagicMask!))
                    {
                        attach?.Invoke(node);
                        throw ErrorAt(declaredAt, TemplateMessages.MagicMismatch, Convert.ToHexString(p.MagicBytes!));
                    }
                }

                attach?.Invoke(node);
                return node;
            case EnumType e:
                node.Size = e.Base.Size;
                CheckRange(node, space);
                attach?.Invoke(node);
                return node;
            case BitfieldType b:
            {
                bool msb = BitOrderAttr(attrs, f) ?? f.MsbFirst;
                long size = BitfieldBytes(b) ?? throw ErrorAt(declaredAt, TemplateMessages.SizeofVariable, b.Name);
                node.Size = size;
                CheckRange(node, space);
                node.Children0 = new LazyChildren(new StructThunk
                {
                    Type = b,
                    Args = [],
                    Offset = offset,
                    Space = space,
                    Parent = f,
                    BigEndian = big,
                    MsbFirst = msb,
                    Depth = f.Depth,
                    WindowEnd = f.WindowEnd,
                    IoBase = f.IoBase,
                });
                attach?.Invoke(node);
                return node;
            }

            case StructType st:
            {
                bool msb = BitOrderAttr(attrs, f) ?? f.MsbFirst;
                string encoding = node.Attributes.Encoding ?? f.Encoding;
                long windowEnd = f.WindowEnd;
                long ioBase = f.IoBase;
                if (Attr(attrs, "substream") is { Args.Count: > 0 } sub)
                {
                    long len = EvalLong(sub.Args[0], f);
                    windowEnd = Math.Min(f.WindowEnd, offset + len);
                    ioBase = offset;
                    if (offset + len > Math.Min(space.Length, f.WindowEnd))
                    {
                        throw new TemplateError(ErrorAt(declaredAt, TemplateMessages.PatternOutOfRange, name,
                            offset.ToString("X", CultureInfo.InvariantCulture), len.ToString(CultureInfo.InvariantCulture)).Diagnostic)
                        {
                            IsOutOfRange = true,
                        };
                    }
                }

                var lc = new LazyChildren(new StructThunk
                {
                    Type = st,
                    Args = args,
                    Offset = offset,
                    Space = space,
                    Parent = f,
                    BigEndian = big,
                    MsbFirst = msb,
                    Encoding = encoding,
                    ArrayIndex = arrayIndex,
                    Depth = f.Depth,
                    WindowEnd = windowEnd,
                    IoBase = ioBase,
                });
                node.Children0 = lc;
                node.Kind = st.Decl.IsUnion ? TemplateNodeKind.Union : TemplateNodeKind.Struct;
                if (HasPointers(st.Decl))
                {
                    // ポインタでつながる構造の型だけを記録する (同じ位置・同じ型を指すポインタの共有。TPL-08 の仕様 4)
                    _seen.TryAdd((space, offset, st.Identity), node);
                }
                long? fixedSize = st.Decl.Params.Count == 0 || !UsesParams(st.Decl) ? StaticSize(st) : null;
                if (Attr(attrs, "substream") is not null)
                {
                    fixedSize = windowEnd - offset;
                }

                if (fixedSize is { } fs)
                {
                    node.Size = fs;
                    CheckRange(node, space);
                    attach?.Invoke(node);
                    return node; // 中身は開いたとき、または参照したときに評価する
                }

                attach?.Invoke(node);
                if (node.Attributes.Lazy && Attr(st.Attributes, "size") is not null)
                {
                    SizeOnly(node);
                    return node;
                }

                Materialize(node);
                return node;
            }
        }

        attach?.Invoke(node);
        return node;
    }

    private static TemplateNodeKind KindOf(RtType type) => type switch
    {
        EnumType e => e.Decl.IsFlags ? TemplateNodeKind.Flags : TemplateNodeKind.Enum,
        BitfieldType => TemplateNodeKind.Bitfield,
        StructType s => s.Decl.IsUnion ? TemplateNodeKind.Union : TemplateNodeKind.Struct,
        PrimType p => p.Kind switch
        {
            PrimKind.Bool => TemplateNodeKind.Bool,
            PrimKind.Char8 or PrimKind.Char16 or PrimKind.Char32 => TemplateNodeKind.Char,
            PrimKind.CString or PrimKind.WString or PrimKind.PString => TemplateNodeKind.String,
            PrimKind.Padding => TemplateNodeKind.Padding,
            PrimKind.Guid or PrimKind.Uuid => TemplateNodeKind.Guid,
            PrimKind.Ipv4 or PrimKind.Ipv6 => TemplateNodeKind.IpAddress,
            PrimKind.Mac => TemplateNodeKind.MacAddress,
            PrimKind.Magic or PrimKind.RawBytes => TemplateNodeKind.Bytes,
            _ when p.IsDate => TemplateNodeKind.DateTime,
            _ when p.IsColor => TemplateNodeKind.Color,
            _ when p.IsFloat => TemplateNodeKind.Float,
            _ => TemplateNodeKind.Integer,
        },
        _ => TemplateNodeKind.Integer,
    };

    private void CheckRange(TemplateNode node, Space space)
    {
        if (node.Offset < 0 || node.Size < 0 || node.Offset > space.Length - node.Size)
        {
            var d = ErrorAt(node.Declaration ?? _loc, TemplateMessages.PatternOutOfRange, node.Name,
                node.Offset.ToString("X", CultureInfo.InvariantCulture), node.Size.ToString(CultureInfo.InvariantCulture)).Diagnostic;
            throw new TemplateError(d with { DataOffset = node.Offset }) { IsOutOfRange = true };
        }
    }

    /// <summary>組み込み型のサイズ (可変長の型はデータを読んで決める)。</summary>
    private long PrimSize(PrimType p, long offset, Space space, Frame f, TemplateNode node)
    {
        switch (p.Kind)
        {
            case PrimKind.Bits:
            {
                // ビット単位の整数: ビットの位置から読み、位置を進める
                if (f.BitPos < 0)
                {
                    f.BitPos = offset * 8;
                }

                long bit = f.BitPos;
                node.Offset = bit / 8;
                node.BitOffset = (int)(bit % 8);
                node.BitSize = p.FracBits;
                node.BitValue = ReadBits(space, bit, p.FracBits, f.MsbFirst);
                node.Kind = TemplateNodeKind.BitfieldMember;
                f.BitPos = bit + p.FracBits;
                _pos = f.BitPos / 8;
                long end = (f.BitPos + 7) / 8;
                f.MaxEnd = f.HasPattern ? Math.Max(f.MaxEnd, end) : end;
                f.HasPattern = true;
                return Math.Max(1, end - node.Offset);
            }

            case PrimKind.CString:
                return ScanTerminator(space, offset, 1, node, f);
            case PrimKind.WString:
                return ScanTerminator(space, offset, 2, node, f);
            case PrimKind.PString:
            {
                ulong len = (ulong)ReadUnsigned(space, offset, p.Inner!.Size, node.BigEndian);
                return p.Inner.Size + (long)Math.Min(len, (ulong)(space.Length - offset));
            }

            case PrimKind.ULeb:
            case PrimKind.SLeb:
            case PrimKind.Vlq:
            {
                for (int i = 0; i < 10; i++)
                {
                    if (offset + i >= space.Length)
                    {
                        throw OutOfRange(p.Name, offset + i);
                    }

                    byte b = ReadByte(space, offset + i);
                    if ((b & 0x80) == 0)
                    {
                        return i + 1;
                    }
                }

                throw ErrorAt(node.Declaration, TemplateMessages.LebTooLong, offset.ToString("X", CultureInfo.InvariantCulture));
            }

            case PrimKind.SqliteVarint:
            {
                for (int i = 0; i < 9; i++)
                {
                    if (i == 8 || (ReadByte(space, offset + i) & 0x80) == 0)
                    {
                        return i + 1;
                    }
                }

                return 9;
            }
        }

        return p.Size;
    }

    /// <summary>0 で終わる文字列の長さ (終端を含む)。終端がなければ警告し、末尾までとする (TPL-03 の「エラー」)。</summary>
    private long ScanTerminator(Space space, long offset, int unit, TemplateNode node, Frame f)
    {
        long end = Math.Min(space.Length, f.WindowEnd);
        byte[] buf = new byte[4096];
        long pos = offset;
        while (pos + unit <= end)
        {
            int n = (int)Math.Min(buf.Length, end - pos);
            n -= n % unit;
            if (n <= 0)
            {
                break;
            }

            space.Read(pos, buf.AsSpan(0, n), this, node.Name);
            for (int i = 0; i + unit <= n; i += unit)
            {
                if (buf[i] == 0 && (unit == 1 || buf[i + 1] == 0))
                {
                    return pos + i + unit - offset;
                }
            }

            pos += n;
            ConsumeSteps(n / 1024);
        }

        Warn(node, TemplateMessages.StringNotTerminated);
        return end - offset;
    }

    // ───────────── 配列 (TPL-07) ─────────────

    private TemplateNode CreateArray(PatternDeclStmt s, Declarator d, RtType elem, List<AttributeNode> attrs, Frame f, long offset, Space space, bool big,
        Action<TemplateNode> attach, string name)
    {
        var node = new TemplateNode(_result, TemplateNodeKind.Array, name, elem.Name + "[]", offset, 0)
        {
            Owner = f.Node,
            Type = elem,
            Section = space.Section,
            BigEndian = big,
            Declaration = d.Location,
            TypeDeclaration = elem.Location,
            DocComment = s.DocComment,
            Context = f,
            AttributeNodes = attrs,
        };
        node.Attributes = BuildAttributes(attrs, f);
        node.DisplayNameAttr = Attr(attrs, "name") is { Args.Count: > 0 } na ? ToDisplayString(Deref(Eval(na.Args[0], f))) : null;
        if (elem is PrimType { Kind: PrimKind.Padding })
        {
            node.Kind = TemplateNodeKind.Padding;
            node.TypeName = "padding";
        }

        CountPattern();
        Val[] args = EvalArgs(s.Type.Args, f, null);
        List<AttributeNode> elemAttrs = elem.Attributes;
        if (Attr(attrs, "format_entries") is { } fe)
        {
            elemAttrs = [.. elem.Attributes, new AttributeNode { Name = "format", Args = fe.Args, Location = fe.Location }];
        }

        long? elemSize = StaticSize(elem);
        long limit = Limits.Array;
        long end = Math.Min(space.Length, f.WindowEnd);
        if (elemSize is { } es && d.Array is ArrayKind.Count or ArrayKind.Eof && elem is not PrimType { Kind: PrimKind.Bits })
        {
            long count;
            if (d.Array == ArrayKind.Count)
            {
                Int128 n = EvalInt(d.ArrayExpr!, f);
                if (n < 0)
                {
                    throw ErrorAt(d.Location, TemplateMessages.IndexOutOfRange, n.ToString(CultureInfo.InvariantCulture), "0");
                }

                if (n > limit)
                {
                    Warn(node, TemplateMessages.ArrayTruncated, limit.ToString(CultureInfo.InvariantCulture));
                    n = limit;
                }

                count = (long)n;
            }
            else
            {
                if (es == 0)
                {
                    throw ErrorAt(d.Location, TemplateMessages.ZeroSizeElements);
                }

                long avail = Math.Max(0, end - offset);
                count = avail / es;
                long rest = avail % es;
                if (count > limit)
                {
                    Warn(node, TemplateMessages.ArrayTruncated, limit.ToString(CultureInfo.InvariantCulture));
                    count = limit;
                }
                else if (rest != 0)
                {
                    Warn(node, TemplateMessages.PartialElement, rest.ToString(CultureInfo.InvariantCulture));
                }
            }

            Int128 total = (Int128)count * es;
            if (total > long.MaxValue)
            {
                throw ErrorAt(d.Location, TemplateMessages.PatternOutOfRange, name, offset.ToString("X", CultureInfo.InvariantCulture), total.ToString(CultureInfo.InvariantCulture));
            }

            node.Size = (long)total;
            node.TypeName = $"{elem.Name}[{count}]";
            CheckRange(node, space);
            bool lazy = node.Attributes.Lazy;
            node.Children0 = new OnDemandChildren
            {
                Count = count,
                Factory = i => CreateElementLazy(elem, node, i, offset + (i * es), space, f, big, elemAttrs, args),
                Evicted = ReleaseElements,
            };
            attach(node);
            return node;
        }

        // 可変長の要素、while / until / 終端値の配列: 先頭から順に評価する (TPL-07 の仕様 4)。ビット単位の要素は位置がビットで進むため
        // 作り直さず、すべて持つ
        bool bitElements = elem is PrimType { Kind: PrimKind.Bits };
        var seq = new SequenceChildren
        {
            Recreate = bitElements ? null : (i, start) => CreateElementLazy(elem, node, i, start, space, f, big, elemAttrs, args),
            Evicted = ReleaseElements,
        };
        node.Children0 = seq;
        attach(node);
        long fixedCount = -1;
        if (d.Array == ArrayKind.Count)
        {
            Int128 n = EvalInt(d.ArrayExpr!, f);
            if (n < 0)
            {
                throw ErrorAt(d.Location, TemplateMessages.IndexOutOfRange, n.ToString(CultureInfo.InvariantCulture), "0");
            }

            if (n > limit)
            {
                Warn(node, TemplateMessages.ArrayTruncated, limit.ToString(CultureInfo.InvariantCulture));
                n = limit;
            }

            fixedCount = (long)n;
        }

        long pos = offset;
        int zeroRun = 0;
        TemplateNode? saveLast = f.Last;
        long saveBits = f.BitPos;
        try
        {
            for (long i = 0; ; i++)
            {
                Step();
                if (fixedCount >= 0 && i >= fixedCount)
                {
                    break;
                }

                if (i >= limit)
                {
                    Warn(node, TemplateMessages.ArrayTruncated, limit.ToString(CultureInfo.InvariantCulture));
                    break;
                }

                bool isBits = elem is PrimType { Kind: PrimKind.Bits };
                if (d.Array == ArrayKind.While)
                {
                    _pos = pos;
                    if (!Truthy(Eval(d.ArrayExpr!, f)))
                    {
                        break;
                    }
                }

                if (pos >= end && !isBits && d.Array != ArrayKind.Count)
                {
                    if (d.Array != ArrayKind.Eof)
                    {
                        Warn(node, TemplateMessages.ArrayReachedEnd);
                    }

                    break;
                }

                _pos = pos;
                TemplateNode el;
                try
                {
                    el = CreateSingle(elem, $"[{i}]", pos, space, f, big, elemAttrs, args, null, i, d.Location, null, node);
                }
                catch (TemplateError e) when (d.Array == ArrayKind.Eof && e.IsOutOfRange && !e.IsLimit)
                {
                    Warn(node, TemplateMessages.PartialElement, (end - pos).ToString(CultureInfo.InvariantCulture));
                    break;
                }

                lock (_result.SyncRoot)
                {
                    seq.Add(el, pos);
                }

                long next = isBits ? (f.BitPos + 7) / 8 : el.End;
                if (isBits)
                {
                    pos = f.BitPos / 8;
                }
                else
                {
                    pos = next;
                }

                if (!isBits && el.Size == 0)
                {
                    if (++zeroRun >= 1000)
                    {
                        node.Size = pos - offset;
                        throw ErrorAt(d.Location, TemplateMessages.ZeroSizeElements);
                    }
                }
                else
                {
                    zeroRun = 0;
                }

                node.Size = Math.Max(next, pos) - offset;
                if (d.Array == ArrayKind.Until)
                {
                    f.Last = el;
                    _pos = pos;
                    if (Truthy(Eval(d.ArrayExpr!, f)))
                    {
                        break;
                    }
                }
                else if (d.Array == ArrayKind.Terminated && IsAllZero(el))
                {
                    break;
                }
            }
        }
        finally
        {
            f.Last = saveLast;
            if (elem is not PrimType { Kind: PrimKind.Bits })
            {
                f.BitPos = saveBits;
            }
        }

        node.TypeName = $"{elem.Name}[{seq.Count}]";
        return node;
    }

    private TemplateNode CreateElementLazy(RtType elem, TemplateNode array, long index, long offset, Space space, Frame f, bool big,
        List<AttributeNode> attrs, Val[] args)
    {
        long savePos = _pos;
        Space saveSpace = _space;
        try
        {
            _pos = offset;
            _space = space;
            return CreateSingle(elem, $"[{index}]", offset, space, f, big, attrs, args, null, index, array.Declaration, null, array);
        }
        finally
        {
            _pos = savePos;
            _space = saveSpace;
        }
    }

    private bool IsAllZero(TemplateNode el)
    {
        if (el.Children0 is null && el.Size <= 16 && el.Type is PrimType)
        {
            Val v = NodeValue(el, converted: false);
            return v.Kind switch
            {
                VK.Int or VK.Bool => v.I == 0,
                VK.Float => v.F == 0,
                VK.Str => v.AsString.Length == 0,
                _ => false,
            };
        }

        Space space = SpaceOfNode(el);
        byte[] buf = new byte[Math.Min(el.Size, 65536)];
        for (long p = 0; p < el.Size; p += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, el.Size - p);
            space.Read(el.Offset + p, buf.AsSpan(0, n), this, el.Name);
            if (buf.AsSpan(0, n).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }

        return true;
    }

    internal Space SpaceOfNode(TemplateNode n) => n.Section is null ? _docSpace : SpaceOf(n.Section);

    // ───────────── 構造体の評価 (TPL-04、TPL-28) ─────────────

    /// <summary>構造体・共用体・ビットフィールドの中身を評価する (遅延評価の実体化)。</summary>
    internal void Materialize(TemplateNode node)
    {
        if (node.Children0 is not LazyChildren lc || lc.Items is not null)
        {
            return;
        }

        StructThunk t = lc.Thunk;
        if (lc.Error is not null)
        {
            lc.Items = [];
            return;
        }

        long savePos = _pos;
        Space saveSpace = _space;
        long patternsBefore = PatternCounter;
        var items = new List<TemplateNode>();
        lc.Items = items;
        lc.Pinned = true;
        bool entered = false;
        Frame? frame = null;
        try
        {
            if (t.Type is BitfieldType bt)
            {
                BuildBitfield(node, bt, t);
                return;
            }

            var st = (StructType)t.Type;
            EnterDepth(st.Name);
            entered = true;
            lock (_result.SyncRoot)
            {
                TypeEvaluations[st.Decl.Name.Length > 0 ? st.Decl.Name : st.Name] =
                    TypeEvaluations.GetValueOrDefault(st.Decl.Name.Length > 0 ? st.Decl.Name : st.Name) + 1;
            }

            frame = new Frame
            {
                Node = node,
                Parent = t.Parent,
                Locals = new Scope(null),
                Children = items,
                Start = t.Offset,
                Space = t.Space,
                BigEndian = t.BigEndian,
                MsbFirst = t.MsbFirst,
                Encoding = t.Encoding ?? t.Parent.Encoding,
                MaxEnd = t.Offset,
                IsUnion = st.Decl.IsUnion,
                Namespace = st.Decl.Namespace,
                TypeArgs = st.TypeArgs,
                ValueArgs = st.ValueArgs,
                ArrayIndex = t.ArrayIndex,
                Depth = _depth,
                Decl = st.Decl,
                WindowEnd = t.WindowEnd,
                IoBase = t.IoBase,
            };
            _pos = t.Offset;
            _space = t.Space;
            ExecStruct(st, t.Args, frame);
            CloseBitGroup(frame);
            AlignToByte(frame);
            long size = frame.HasPattern ? Math.Max(0, frame.MaxEnd - t.Offset) : 0;
            if (Attr(st.Attributes, "size") is { Args.Count: > 0 } sa)
            {
                long fixedSize = EvalLong(sa.Args[0], frame);
                if (size > fixedSize)
                {
                    Warn(node, TemplateMessages.StructTooLarge, size.ToString(CultureInfo.InvariantCulture), fixedSize.ToString(CultureInfo.InvariantCulture));
                }

                size = fixedSize;
            }

            if (Attr(node.AttributeNodes, "substream") is not null)
            {
                size = t.WindowEnd - t.Offset;
            }

            if (node.Size == 0 || node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union && StaticSize(st) is null)
            {
                node.Size = size;
            }
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            if (frame is not null && node.Size == 0)
            {
                node.Size = frame.HasPattern ? Math.Max(0, frame.MaxEnd - t.Offset) : 0;
            }

            lc.Error = e.Diagnostic;
            RecordError(e, node);
            throw;
        }
        finally
        {
            if (entered)
            {
                LeaveDepth();
            }

            _pos = savePos;
            _space = saveSpace;
            lc.Pinned = false;
            lc.Cost = (PatternCounter - patternsBefore) * NodeCost;
            if (ReferenceEquals(lc.Items, items) && lc.LruEntry is null && node.Owner is not null)
            {
                lc.LruEntry = _lru.AddLast(node);
            }
        }
    }

    /// <summary>構造体の本体を実行する (継承元の本体を先に実行する。TPL-04 の仕様 5)。</summary>
    private void ExecStruct(StructType st, Val[] args, Frame frame)
    {
        StructDecl decl = st.Decl;
        if (decl.Base is not null)
        {
            if (ResolveType(decl.Base, frame) is StructType baseType)
            {
                ExecStruct(baseType, [], frame);
            }
        }

        if (decl.Params.Count != args.Length)
        {
            throw ErrorAt(decl.Location, TemplateMessages.ArgumentCount, decl.Name, decl.Params.Count.ToString(CultureInfo.InvariantCulture));
        }

        for (int i = 0; i < args.Length; i++)
        {
            Param p = decl.Params[i];
            Val v = args[i];
            if (!p.IsRef && !p.Type.IsAuto && TryResolveTypeName(p.Type.Parts, frame) is PrimType or EnumType)
            {
                v = Coerce(v, TryResolveTypeName(p.Type.Parts, frame), decl.Location);
            }

            frame.Locals.Define(p.Name, new Variable(v, false, null));
        }

        ExecBlock(decl.Body, frame);
    }

    /// <summary>
    /// <c>[[lazy]]</c> の要素で、型に <c>[[size(式)]]</c> がある場合: サイズの式を評価できるところまでだけ本体を実行し、中身は開いたときに
    /// 評価する (TPL-07 の仕様 5)。
    /// </summary>
    private void SizeOnly(TemplateNode node)
    {
        var lc = (LazyChildren)node.Children0!;
        StructThunk t = lc.Thunk;
        var st = (StructType)t.Type;
        Expr sizeExpr = Attr(st.Attributes, "size")!.Args[0];
        var scratch = new List<TemplateNode>();
        var shadow = new TemplateNode(_result, node.Kind, node.Name, node.TypeName, node.Offset, 0) { Owner = node.Owner };
        long savePos = _pos;
        Space saveSpace = _space;
        long patternsBefore = PatternCounter;
        var frame = new Frame
        {
            Node = shadow,
            Parent = t.Parent,
            Locals = new Scope(null),
            Children = scratch,
            Start = t.Offset,
            Space = t.Space,
            BigEndian = t.BigEndian,
            MsbFirst = t.MsbFirst,
            Encoding = t.Encoding ?? t.Parent.Encoding,
            MaxEnd = t.Offset,
            Namespace = st.Decl.Namespace,
            TypeArgs = st.TypeArgs,
            ValueArgs = st.ValueArgs,
            Depth = _depth,
        };
        long? size = null;
        frame.StopWhen = () =>
        {
            try
            {
                size = EvalLong(sizeExpr, frame);
                return true;
            }
            catch (TemplateError e) when (e.Diagnostic.Code is TemplateMessages.Undefined or TemplateMessages.UnknownMember)
            {
                return false;
            }
        };
        try
        {
            _pos = t.Offset;
            _space = t.Space;
            if (!frame.StopWhen())
            {
                ExecBlock(st.Decl.Body, frame);
            }

            node.Size = size ?? (frame.HasPattern ? frame.MaxEnd - t.Offset : 0);
        }
        finally
        {
            _pos = savePos;
            _space = saveSpace;
            PatternCounter = patternsBefore;
            MemoryCounter -= scratch.Count * (long)NodeCost;
        }
    }

    /// <summary>部分木のサイズを確定させる (遅延評価のノードで、サイズが未確定のもの)。</summary>
    private void EnsureSize(TemplateNode node)
    {
        if (node.Children0 is LazyChildren { Items: null } && node.Size == 0 && node.Type is StructType st && StaticSize(st) is null)
        {
            Materialize(node);
        }
    }

    // ───────────── ビットフィールド (TPL-06) ─────────────

    private void BuildBitfield(TemplateNode node, BitfieldType bt, StructThunk t)
    {
        var lc = (LazyChildren)node.Children0!;
        Frame f = t.Parent;
        int nbytes = (int)node.Size;
        if (nbytes > 8)
        {
            throw ErrorAt(bt.Location, TemplateMessages.BitfieldOverflow, (nbytes * 8).ToString(CultureInfo.InvariantCulture), "u64");
        }

        bool bigEndian = bt.Base is not null ? t.BigEndian : t.MsbFirst;
        ulong value = (ulong)ReadUnsigned(t.Space, t.Offset, nbytes, bigEndian);
        int totalBits = nbytes * 8;
        int used = 0;
        var frame = new Frame { Node = node, Locals = new Scope(null), Space = t.Space, Namespace = bt.Decl.Namespace, Parent = f };
        int declared = 0;
        foreach (BitfieldMember m in bt.Decl.Members)
        {
            declared += (int)EvalLong(m.Width, frame);
        }

        if (bt.Base is not null && declared > totalBits)
        {
            throw ErrorAt(bt.Location, TemplateMessages.BitfieldOverflow, declared.ToString(CultureInfo.InvariantCulture), bt.Base.Name);
        }

        foreach (BitfieldMember m in bt.Decl.Members)
        {
            int w = (int)EvalLong(m.Width, frame);
            if (w is < 1 or > 64)
            {
                throw ErrorAt(m.Location, TemplateMessages.BitfieldTooWide, w.ToString(CultureInfo.InvariantCulture));
            }

            int shift = t.MsbFirst ? totalBits - used - w : used;
            used += w;
            if (m.Name is null)
            {
                continue;
            }

            ulong v = (value >> shift) & (w == 64 ? ulong.MaxValue : (1UL << w) - 1);
            TemplateNode member = MakeBitMember(m.Name, m.Type, m.Attributes, m.DocComment, m.Location, frame, node, t.Offset, nbytes,
                bigEndian, shift, w, v, t.Space);
            items(lc).Add(member);
            RegisterMember(frame, member);
        }

        static List<TemplateNode> items(LazyChildren l) => l.Items!;
    }

    private TemplateNode MakeBitMember(string name, TypeRef? typeRef, List<AttributeNode> attrs, string? doc, TemplateSourceLocation at, Frame f,
        TemplateNode owner, long unitOffset, int nbytes, bool bigEndian, int shift, int width, ulong value, Space space)
    {
        RtType? type = typeRef is null ? null : ResolveType(typeRef, f);
        // 値のビット位置からバイトの位置へ (TPL-06 の仕様 6)
        int byteIndex = bigEndian ? nbytes - 1 - (shift / 8) : shift / 8;
        int bit = shift % 8;
        int spanBytes = Math.Max(1, (bit + width + 7) / 8);
        long offset = bigEndian ? unitOffset + Math.Max(0, byteIndex - spanBytes + 1) : unitOffset + byteIndex;
        var node = new TemplateNode(_result, TemplateNodeKind.BitfieldMember, name, type?.Name ?? $"{width} bit", offset, spanBytes)
        {
            Owner = owner,
            Type = type,
            Section = space.Section,
            BigEndian = bigEndian,
            BitOffset = bit,
            BitSize = width,
            BitValue = value,
            Declaration = at,
            DocComment = doc,
            Context = f,
            AttributeNodes = attrs,
        };
        if (type is EnumType et)
        {
            node.Kind = et.Decl.IsFlags ? TemplateNodeKind.Flags : TemplateNodeKind.Enum;
        }
        else if (type is PrimType { Kind: PrimKind.Bool })
        {
            node.Kind = TemplateNodeKind.Bool;
        }

        node.Attributes = BuildAttributes(attrs, f);
        CountPattern();
        return node;
    }

    /// <summary>構造体の中の C 風のビットフィールド (基底型の大きさの単位でまとめて読む。TPL-06 の仕様 2)。</summary>
    private void DeclareInlineBits(PatternDeclStmt s, Declarator d, RtType type, List<AttributeNode> attrs, Frame f, bool big)
    {
        PrimType? baseType = type as PrimType ?? (type as EnumType)?.Base;
        if (baseType is null || !baseType.IsInteger || baseType.Size == 0)
        {
            throw ErrorAt(d.Location, TemplateMessages.TypeMismatch, "bit field type");
        }

        int w = (int)EvalLong(d.BitWidth!, f);
        int bits = baseType.Size * 8;
        if (w is < 1 or > 64 || w > bits)
        {
            throw ErrorAt(d.Location, TemplateMessages.BitfieldOverflow, w.ToString(CultureInfo.InvariantCulture), baseType.Name);
        }

        BitGroup? g = f.OpenGroup;
        if (g is null || g.Base.Size != baseType.Size || g.Used + w > bits || g.BigEndian != big)
        {
            CloseBitGroup(f);
            AlignToByte(f);
            long start = _pos;
            ulong value = (ulong)ReadUnsigned(_space, start, baseType.Size, big);
            g = new BitGroup { Start = start, Base = baseType, Value = value, BigEndian = big };
            f.OpenGroup = g;
        }

        bool msb = BitOrderAttr(attrs, f) ?? f.MsbFirst;
        int shift = msb ? bits - g.Used - w : g.Used;
        g.Used += w;
        if (d.Name is null || d.Name == "padding")
        {
            return;
        }

        ulong v = (g.Value >> shift) & (w == 64 ? ulong.MaxValue : (1UL << w) - 1);
        TemplateNode node = MakeBitMember(d.Name, type is EnumType or PrimType { Kind: PrimKind.Bool } ? s.Type : null, attrs, s.DocComment,
            d.Location, f, f.Node, g.Start, baseType.Size, big, shift, w, v, _space);
        AttachChild(f, node);
        RegisterMember(f, node);
        f.Last = node;
    }

    private void CloseBitGroup(Frame f)
    {
        if (f.OpenGroup is { } g)
        {
            f.OpenGroup = null;
            _pos = g.Start + g.Base.Size;
            f.MaxEnd = f.HasPattern ? Math.Max(f.MaxEnd, _pos) : _pos;
            f.HasPattern = true;
            if (f.IsUnion)
            {
                _pos = f.Start;
            }
        }
    }

    /// <summary>ビット単位の読み込みの後、次のバイト境界に揃える。</summary>
    private void AlignToByte(Frame f)
    {
        if (f.BitPos >= 0)
        {
            _pos = (f.BitPos + 7) / 8;
            f.BitPos = -1;
        }
    }

    private ulong ReadBits(Space space, long bitPos, int count, bool msbFirst)
    {
        long firstByte = bitPos / 8;
        long lastByte = (bitPos + count - 1) / 8;
        int n = (int)(lastByte - firstByte + 1);
        Span<byte> buf = stackalloc byte[16];
        space.Read(firstByte, buf[..n], this, "bits");
        int skip = (int)(bitPos % 8);
        ulong result = 0;
        for (int i = 0; i < count; i++)
        {
            int b = skip + i;
            int byteIndex = b / 8;
            int bitInByte = b % 8;
            int bitValue = msbFirst ? (buf[byteIndex] >> (7 - bitInByte)) & 1 : (buf[byteIndex] >> bitInByte) & 1;
            result = msbFirst ? (result << 1) | (uint)bitValue : result | ((ulong)bitValue << i);
        }

        return result;
    }

    // ───────────── ポインタ (TPL-08) ─────────────

    private TemplateNode CreatePointerDecl(PatternDeclStmt s, Declarator d, RtType target, List<AttributeNode> attrs, Frame f, long offset, Space space,
        bool big, Action<TemplateNode> attach)
    {
        string name = d.Name!;
        RtType? ptrType0 = d.PointerType is null ? null : ResolveType(d.PointerType, f);
        if (ptrType0 is EnumType pe)
        {
            ptrType0 = pe.Base;
        }

        if (ptrType0 is not PrimType ptrType || !ptrType.IsInteger || ptrType.Size == 0)
        {
            throw ErrorAt(d.Location, TemplateMessages.PointerNotInteger);
        }

        Val[] args = EvalArgs(s.Type.Args, f, null);
        if (d.Array != ArrayKind.None && !d.PointerToArray)
        {
            // ポインタの配列 (Entry *table[n] : u32)
            long count;
            if (d.Array == ArrayKind.Count)
            {
                count = (long)Int128.Clamp(EvalInt(d.ArrayExpr!, f), 0, Limits.Array);
            }
            else if (d.Array == ArrayKind.Eof)
            {
                count = Math.Min(Limits.Array, Math.Max(0, Math.Min(space.Length, f.WindowEnd) - offset) / ptrType.Size);
            }
            else
            {
                throw ErrorAt(d.Location, TemplateMessages.TypeMismatch, "pointer arrays need a count");
            }

            var array = new TemplateNode(_result, TemplateNodeKind.Array, name, $"{target.Name}*[{count}]", offset, count * ptrType.Size)
            {
                Owner = f.Node,
                Section = space.Section,
                BigEndian = big,
                Declaration = d.Location,
                Context = f,
                AttributeNodes = attrs,
            };
            array.Attributes = BuildAttributes(attrs, f);
            CountPattern();
            CheckRange(array, space);
            array.Children0 = new OnDemandChildren
            {
                Count = count,
                Factory = i => MakePointer($"[{i}]", target, ptrType, d, attrs, f, offset + (i * ptrType.Size), space, big, args, array, i),
                Evicted = ReleaseElements,
            };
            attach(array);
            return array;
        }

        TemplateNode node = MakePointer(name, target, ptrType, d, attrs, f, offset, space, big, args, null, -1);
        attach(node);
        return node;
    }

    private TemplateNode MakePointer(string name, RtType target, PrimType ptrType, Declarator d, List<AttributeNode> attrs, Frame f, long offset,
        Space space, bool big, Val[] args, TemplateNode? owner, long index)
    {
        var node = new TemplateNode(_result, TemplateNodeKind.Pointer, name, target.Name + "*", offset, ptrType.Size)
        {
            Owner = owner ?? f.Node,
            Type = ptrType,
            Section = space.Section,
            BigEndian = big,
            Declaration = d.Location,
            TypeDeclaration = target.Location,
            Context = f,
            AttributeNodes = attrs,
            ArrayIndex = index,
        };
        node.Attributes = BuildAttributes(attrs, f);
        CountPattern();
        CheckRange(node, space);
        Int128 raw = ReadIntegerValue(space, offset, ptrType, big);
        bool allowNull = Attr(attrs, "allow_null") is not null;
        node.Pointer = new TemplatePointerInfo { TargetType = target.Name, IsNull = raw == 0 && !allowNull };
        node.ValueCache = Val.Int(raw);
        node.ValueCached = true;
        node.Children0 = new PointerChildren(() => ResolvePointer(node, target, d, attrs, f, raw, big, args));
        return node;
    }

    private TemplateNode? ResolvePointer(TemplateNode node, RtType target, Declarator d, List<AttributeNode> attrs, Frame f, Int128 raw, bool big,
        Val[] args)
    {
        TemplatePointerInfo info = node.Pointer!;
        info.Resolved = true;
        if (info.IsNull)
        {
            return null;
        }

        try
        {
            Int128 off = raw;
            TemplateNode? saveThis = _thisNode;
            _thisNode = node;
            try
            {
                if (Attr(attrs, "base") is { Args.Count: > 0 } b)
                {
                    off = raw + EvalInt(b.Args[0], f);
                }

                if (Attr(attrs, "relative") is not null)
                {
                    off = raw + node.Offset;
                }

                if (Attr(attrs, "pointer_map") is { Args.Count: > 0 } pm)
                {
                    Val fn = Eval(pm.Args[0], f);
                    if (fn.Kind != VK.Func)
                    {
                        throw ErrorAt(pm.Location, TemplateMessages.NotCallable, "pointer_map");
                    }

                    off = ToInt(Invoke((Callable)fn.O!, [Val.Int(off)], null, f), pm.Location);
                }
            }
            finally
            {
                _thisNode = saveThis;
            }

            Space space = d.PointerSection is not null ? SpaceOf(SectionByName(d.PointerSection, f, d.Location)) : SpaceOfNode(node);
            info.TargetSection = space.Section;
            info.TargetOffset = off > long.MaxValue ? long.MaxValue : off < long.MinValue ? long.MinValue : (long)off;
            if (off < 0 || off >= space.Length)
            {
                info.OutOfRange = true;
                Warn(node, TemplateMessages.PointerOutOfRange, info.TargetOffset.ToString("X", CultureInfo.InvariantCulture));
                return null;
            }

            long at = (long)off;
            if (target is StructType st && _seen.TryGetValue((space, at, st.Identity), out TemplateNode? existing))
            {
                info.DuplicateOf = existing.Path;
                return null;
            }

            long savePos = _pos;
            Space saveSpace = _space;
            try
            {
                _pos = at;
                _space = space;
                if (d.PointerToArray)
                {
                    var arrDecl = new Declarator
                    {
                        Name = "*" + node.Name,
                        Array = d.Array,
                        ArrayExpr = d.ArrayExpr,
                        Location = d.Location,
                    };
                    var stmt = new PatternDeclStmt { Type = new TypeRef { Parts = [target.Name] }, Declarators = [arrDecl], Location = d.Location };
                    return CreateArray(stmt, arrDecl, target, target.Attributes, f, at, space, big, _ => { }, "*" + node.Name);
                }

                TemplateNode t = CreateSingle(target, "*" + node.Name, at, space, f, big, target.Attributes, args, null, -1, d.Location, null, node);
                return t;
            }
            finally
            {
                _pos = savePos;
                _space = saveSpace;
            }
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            info.Error = e.Diagnostic;
            RecordError(e, node);
            return null;
        }
    }

    // ───────────── .ksy の instances (TPL-33) ─────────────

    /// <summary>instances: 結果ツリーで開くか、式で参照したときに評価するパターン。</summary>
    private void DeclareInstance(Stmt inst, string name, Frame f)
    {
        var node = new TemplateNode(_result, TemplateNodeKind.Struct, name, "instance", f.Start, 0)
        {
            Owner = f.Node,
            Declaration = inst.Location,
            Context = f,
        };
        node.Children0 = new PointerChildren(() => ResolveInstance(node, inst, f));
        node.Pointer = null;
        node.Kind = TemplateNodeKind.Struct;
        node.Attributes = new TemplateNodeAttributes { Inline = false };
        _instanceNodes.Add(node);
        CountPattern();
        AttachChild(f, node);
        RegisterMember(f, node);
    }

    private readonly HashSet<TemplateNode> _instanceNodes = [];

    private Stmt? _resolvingInstance;

    private TemplateNode? ResolveInstance(TemplateNode holder, Stmt inst, Frame f)
    {
        long savePos = _pos;
        Space saveSpace = _space;
        List<TemplateNode>? children = f.Children;
        var scratch = new List<TemplateNode>();
        var frame = new Frame
        {
            Node = f.Node,
            Parent = f.Parent,
            Locals = f.Locals,
            Children = scratch,
            Start = f.Start,
            Space = f.Space,
            BigEndian = f.BigEndian,
            MsbFirst = f.MsbFirst,
            Encoding = f.Encoding,
            MaxEnd = f.Start,
            Namespace = f.Namespace,
            TypeArgs = f.TypeArgs,
            ValueArgs = f.ValueArgs,
            Depth = f.Depth,
            WindowEnd = f.WindowEnd,
            IoBase = f.IoBase,
        };
        foreach (var kv in f.Members)
        {
            if (!ReferenceEquals(kv.Value[^1], holder))
            {
                frame.Members[kv.Key] = kv.Value;
            }
        }

        try
        {
            // pos のない instances は、Kaitai Struct と同じくストリームの今の位置 (その型でそこまで読んだ末尾。seq を読み終えていれば
            // seq の末尾) から読む
            _pos = f.HasPattern ? f.MaxEnd : f.Start;
            _space = f.Space;
            lock (_result.SyncRoot)
            {
                TypeEvaluations["instance:" + holder.Name] = TypeEvaluations.GetValueOrDefault("instance:" + holder.Name) + 1;
            }

            Stmt? saveInst = _resolvingInstance;
            _resolvingInstance = inst;
            try
            {
                ExecStatement(inst, frame);
            }
            finally
            {
                _resolvingInstance = saveInst;
            }

            TemplateNode? result = scratch.LastOrDefault();
            if (result is not null)
            {
                result.Owner = holder.Owner;
                holder.Offset = result.Offset;
                holder.Size = result.Size;
                holder.TypeName = result.TypeName;
                holder.Kind = result.Kind;
                holder.Type = result.Type;
                holder.Section = result.Section;
                holder.BigEndian = result.BigEndian;
                holder.ComputedVariable = result.ComputedVariable;
                holder.InstanceTarget = result;
            }

            return result;
        }
        catch (TemplateError e) when (!e.IsLimit)
        {
            RecordError(e, holder);
            holder.Kind = TemplateNodeKind.Error;
            throw;
        }
        finally
        {
            _pos = savePos;
            _space = saveSpace;
        }
    }
}
