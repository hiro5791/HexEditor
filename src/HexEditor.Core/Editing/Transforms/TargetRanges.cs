using HexEditor.Core.Engine;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>演算・変換の対象の 1 つの範囲 (マルチ選択の要素、矩形選択の行)。</summary>
public readonly record struct TargetRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>
/// 演算・変換の対象範囲 (EDIT-31 の仕様 1・9、EDIT-38 の仕様 9)。マルチ選択 (F2-12)・矩形選択 (F2-15) の担当が実装して渡す。
/// 単一の選択範囲は <see cref="SelectionRanges.Single"/>。
/// </summary>
public interface ISelectionRanges
{
    /// <summary>オフセット順の、重ならない範囲 (長さ 0 の範囲は含めない)。</summary>
    IReadOnlyList<TargetRange> Ranges { get; }
}

/// <summary><see cref="ISelectionRanges"/> の簡単な実装。</summary>
public sealed class SelectionRanges(IReadOnlyList<TargetRange> ranges) : ISelectionRanges
{
    public IReadOnlyList<TargetRange> Ranges { get; } = Normalize(ranges);

    public static SelectionRanges Single(long offset, long length) => new([new TargetRange(offset, length)]);

    /// <summary>オフセット順に並べ、空の範囲を除き、重なりをまとめる。</summary>
    public static IReadOnlyList<TargetRange> Normalize(IEnumerable<TargetRange> ranges)
    {
        var result = new List<TargetRange>();
        foreach (TargetRange r in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
        {
            if (result.Count > 0 && result[^1].End > r.Offset)
            {
                TargetRange last = result[^1];
                result[^1] = last with { Length = Math.Max(last.End, r.End) - last.Offset };
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }
}

/// <summary>範囲の内容を置き換える内容 (長さは変わってもよい)。</summary>
public sealed record RangeReplacement(TargetRange Range, EditContent Content);

/// <summary>
/// 作り終えた内容をドキュメントに反映する (EDIT-31 の「巨大ファイル」4: 作り終えてから 1 回で反映する)。すべての範囲を 1 つの編集グループにする
/// (EDIT-31 の仕様 12)。長さが同じ範囲は上書き、違う範囲は削除と挿入で、後ろの範囲から行う (前の範囲のオフセットが変わらないように)。
/// </summary>
public static class TransformApplier
{
    /// <summary>反映する。長さが変わる範囲があり、長さを変えられないドキュメントなら <see cref="FixedLengthException"/>。</summary>
    public static void Apply(Document document, IReadOnlyList<RangeReplacement> parts, string description)
    {
        if (parts.Count == 0)
        {
            return;
        }

        if (!document.CanResize && parts.Any(p => p.Content.Length != p.Range.Length))
        {
            foreach (RangeReplacement p in parts)
            {
                p.Content.Dispose();
            }

            throw new FixedLengthException();
        }

        using (document.BeginGroup(description))
        {
            foreach (RangeReplacement part in parts.OrderByDescending(p => p.Range.Offset))
            {
                if (part.Content.Length == part.Range.Length)
                {
                    document.OverwriteContent(part.Range.Offset, part.Content, description);
                    continue;
                }

                document.Delete(part.Range.Offset, part.Range.Length, description);
                document.InsertContent(part.Range.Offset, part.Content, description);
            }
        }
    }

    /// <summary>反映しなかった内容の一時ファイルを消す (キャンセル・失敗)。</summary>
    public static void DisposeAll(IEnumerable<RangeReplacement> parts)
    {
        foreach (RangeReplacement p in parts)
        {
            p.Content.Dispose();
        }
    }

    /// <summary>反映した後の範囲 (選択し直すため)。長さが変わった範囲より後ろの範囲はずらす。</summary>
    public static IReadOnlyList<TargetRange> ResultRanges(IReadOnlyList<RangeReplacement> parts)
    {
        var result = new List<TargetRange>();
        long shift = 0;
        foreach (RangeReplacement p in parts.OrderBy(p => p.Range.Offset))
        {
            result.Add(new TargetRange(p.Range.Offset + shift, p.Content.Length));
            shift += p.Content.Length - p.Range.Length;
        }

        return result;
    }
}
