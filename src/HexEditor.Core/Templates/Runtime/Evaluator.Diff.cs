using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

// 構造を理解した比較 (06 の ANA-31) が評価器の型の情報を読むための部品。比較は評価器のスレッドで呼ぶ (TemplateResult.Run の中)。
internal sealed partial class Evaluator
{
    private readonly Dictionary<RtType, bool> _plainFixed = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 配列 (またはその要素の型) の属性 <c>diff_key("フィールド名")</c> (TPL-13)。配列のパターンの属性を優先し、なければ要素の型の属性。
    /// 引数が文字列でなければ null。
    /// </summary>
    internal string? DiffKeyOf(TemplateNode node)
    {
        AttributeNode? a = Attr(node.AttributeNodes, "diff_key") ?? (node.Type is { } t ? Attr(t.Attributes, "diff_key") : null);
        if (a is not { Args.Count: > 0 })
        {
            return null;
        }

        try
        {
            Val v = Deref(Eval(a.Args[0], node.Context ?? _root));
            return v.Kind == VK.Str ? v.AsString : null;
        }
        catch (TemplateError)
        {
            return null;
        }
    }

    /// <summary>パターンまたはその型に <c>read</c> の変換がある (値は変換後の値で比べる。ANA-31 の仕様 4)。</summary>
    internal static bool HasReadConversion(TemplateNode node) =>
        Attr(node.AttributeNodes, "read") is not null || (node.Type is { } t && Attr(t.Attributes, "read") is not null);

    /// <summary>
    /// 要素を 1 つずつ評価しなくてよい、固定長の葉の要素の配列の要素の大きさ (<c>u32 a[n]</c>、<c>u64 a[eof]</c> など)。要素の型が
    /// 固定長の基本型か列挙型で、要素に <c>read</c> の変換がない配列だけ。それ以外は null。
    /// </summary>
    internal long? LeafArrayElementSize(TemplateNode node)
    {
        if (node.Kind != TemplateNodeKind.Array || node.Children0 is not OnDemandChildren || node.IsByteOrCharArray)
        {
            return null;
        }

        return node.Type switch
        {
            PrimType { IsVariable: false, Kind: not (PrimKind.Bits or PrimKind.Padding) } p when p.Size > 0 && Attr(p.Attributes, "read") is null => p.Size,
            EnumType e when e.Base.Size > 0 && Attr(e.Attributes, "read") is null => e.Base.Size,
            _ => null,
        };
    }

    /// <summary>
    /// 型が、ポインタ・セクション・<c>$</c> への代入を含まない固定長の型か (TPL-28 の仕様 1。左右のバイト列が同じなら子を評価せずに一致とできる。
    /// ANA-31 の「巨大ファイル・長時間処理」)。
    /// </summary>
    internal bool IsPlainFixed(RtType? type)
    {
        if (type is null)
        {
            return false;
        }

        if (_plainFixed.TryGetValue(type, out bool known))
        {
            return known;
        }

        _plainFixed[type] = false; // 再帰する型は固定長ではない
        bool result = StaticSize(type) is not null && type switch
        {
            StructType s => PlainBody(s),
            _ => true,
        };
        _plainFixed[type] = result;
        return result;
    }

    private bool PlainBody(StructType s)
    {
        if (Attr(s.Attributes, "read") is not null)
        {
            return false;
        }

        Frame frame = TypeFrame(s);
        foreach (Stmt st in s.Decl.Body)
        {
            switch (st)
            {
                case SectionDeclStmt:
                    return false;
                case PatternDeclStmt p:
                    if (p.Declarators.Any(d => d.IsPointer || d.Placement is not null))
                    {
                        return false;
                    }

                    RtType et;
                    try
                    {
                        et = ResolveType(p.Type, frame);
                    }
                    catch (TemplateError)
                    {
                        return false;
                    }

                    if (!IsPlainFixed(et))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>ビットフィールドの要素の値 (ビットで比べる。ANA-31 の仕様 4)。</summary>
    internal static ulong BitValueOf(TemplateNode node) => node.BitValue;
}
