namespace HexEditor.Core.Annotations;

/// <summary>
/// 表示範囲を問い合わせるたびに、その範囲の注釈だけを作る出どころ (INSP-32 の「巨大ファイル・長時間処理」)。すべて検索の結果
/// (FIND-20。100 万件を超えて一時ファイルに書き出したものを含む) のように件数の多い結果を、注釈の配列にせずに注釈の層に載せる。
/// <paramref name="ranges"/> は [start, end) と重なる範囲 (開始・長さ) を返す。UI スレッドから表示のたびに呼ぶので、ブロックしないこと。
/// </summary>
public sealed class RangeAnnotationSource(string id, AnnotationOrigin origin, Func<long, long, IEnumerable<(long Offset, long Length)>> ranges,
    Func<string> label, string displayName = "") : IAnnotationSource
{
    public string Id { get; } = id;

    public AnnotationOrigin Origin { get; } = origin;

    public string DisplayName { get; } = displayName;

    public event EventHandler? Changed;

    public void Query(long start, long end, List<Annotation> output)
    {
        if (end <= start)
        {
            return;
        }

        string? text = null;
        foreach ((long offset, long length) in ranges(start, end))
        {
            var annotation = new Annotation(offset, length, text ??= label());
            if (annotation.Overlaps(start, end))
            {
                output.Add(annotation);
            }
        }
    }

    /// <summary>結果が変わった (描き直す)。</summary>
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
