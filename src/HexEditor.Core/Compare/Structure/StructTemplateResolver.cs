using HexEditor.Core.Templates;

namespace HexEditor.Core.Compare.Structure;

/// <summary>構造の比較のテンプレートを決めるための、片側の情報 (ANA-31 の仕様 2)。</summary>
public sealed record StructResolveSide(TemplateDataSource Data, string? Path, long CompareStart, IReadOnlyList<TemplateApplicationInfo> Applications);

/// <summary>
/// 構造の比較に使うテンプレートを決める (ANA-31 の仕様 2)。上から順に調べ、最初に決まったものを使う:
/// (1) 利用者が選んだテンプレート、(2) 左右の同じテンプレートの固定の適用 (比較の開始オフセットからの位置が左右で同じもの)、
/// (3) 左右の比較の開始オフセットでの自動適用の判定で、両方の最上位の候補が同じテンプレートで、どちらの一致もマジックを含む。
/// </summary>
public static class StructTemplateResolver
{
    public static StructTemplateChoice Resolve(StructTemplateChoice? userChoice, StructResolveSide left, StructResolveSide right, TemplateCatalog catalog)
    {
        if (userChoice is { IsNone: false } chosen)
        {
            return chosen with { Reason = StructTemplateReason.UserChoice };
        }

        // (2) 同じテンプレートファイルの固定の適用で、開始オフセットから比較の開始オフセットを引いた値が左右で同じもの。複数あれば
        // 比較の開始オフセットに最も近いもの。
        StructTemplateChoice? best = null;
        long bestDistance = long.MaxValue;
        foreach (TemplateApplicationInfo la in left.Applications.Where(a => a.Kind == TemplateApplyKind.Fixed && a.TypeName is null))
        {
            foreach (TemplateApplicationInfo ra in right.Applications.Where(a => a.Kind == TemplateApplyKind.Fixed && a.TypeName is null))
            {
                if (!string.Equals(la.TemplatePath, ra.TemplatePath, StringComparison.OrdinalIgnoreCase)
                    || la.StartOffset - left.CompareStart != ra.StartOffset - right.CompareStart)
                {
                    continue;
                }

                long distance = Math.Abs(la.StartOffset - left.CompareStart);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new StructTemplateChoice(la.TemplatePath, la.StartOffset, ra.StartOffset, StructTemplateReason.SameApplication);
                }
            }
        }

        if (best is not null)
        {
            return best;
        }

        // (3) 自動適用の判定 (TPL-30 の 1、2)。拡張子・MIME だけの一致では選ばない。
        TemplateCandidate? l = catalog.Detect(InputAt(left)).FirstOrDefault();
        TemplateCandidate? r = catalog.Detect(InputAt(right)).FirstOrDefault();
        if (l is { Match.Magic: true } && r is { Match.Magic: true } && string.Equals(l.Entry.Path, r.Entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            return new StructTemplateChoice(l.Entry.Path, left.CompareStart, right.CompareStart, StructTemplateReason.MagicMatch);
        }

        return StructTemplateChoice.None;
    }

    /// <summary>比較の開始オフセットからのデータで判定する (先頭と末尾それぞれ 64 KB と拡張子。MIME は使わない)。</summary>
    public static TemplateMatchInput InputAt(StructResolveSide side)
    {
        long start = Math.Clamp(side.CompareStart, 0, side.Data.Length);
        long length = side.Data.Length - start;
        byte[] head = new byte[(int)Math.Min(length, TemplateMatchInput.WindowBytes)];
        byte[] tail = new byte[(int)Math.Min(length, TemplateMatchInput.WindowBytes)];
        Fill(side.Data, start, head);
        Fill(side.Data, side.Data.Length - tail.Length, tail);
        string? extension = side.Path is null ? null : System.IO.Path.GetExtension(side.Path).TrimStart('.');
        return new TemplateMatchInput(length, head, tail, string.IsNullOrEmpty(extension) ? null : extension, null);
    }

    private static void Fill(TemplateDataSource data, long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = data.Read(offset + done, destination[done..], out _);
            if (n <= 0)
            {
                break;
            }

            done += n;
        }
    }
}
