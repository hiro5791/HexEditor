namespace HexEditor.Core.Search;

public sealed partial class SearchPattern
{
    /// <summary>種類の列の見出しを変えた写し。</summary>
    internal SearchPattern WithVariantColumn(VariantColumn column)
    {
        var copy = (SearchPattern)MemberwiseClone();
        copy.VariantColumn = column;
        return copy;
    }

    /// <summary>正規表現のパターンを作る (FIND-18、FIND-19)。チャンクの重なり幅は「一致の最大長 + 文脈」。</summary>
    internal static SearchPattern FromRegex(RegexMatcher matcher, int maxMatchLength)
    {
        var pattern = new SearchPattern([], null, null, 1)
        {
            _multi = matcher,
        };
        pattern._minMatchLength = 1;
        pattern._maxMatchLength = Math.Clamp(maxMatchLength, 1, MaxMaxMatchLength) + matcher.LeadingContext;
        return pattern;
    }

    /// <summary>
    /// まとめて求める照合 (<see cref="IsMulti"/>) で、<paramref name="data"/> の中の開始が [minStart, maxStart) の一致を、開始の昇順 (同じ
    /// 開始なら種類の順) に <paramref name="onMatch"/> へ渡す。<paramref name="overlapping"/> が false なら、採った一致の末尾の次から探す
    /// (同じ位置の一致はすべて渡し、そのうち最も長いものの末尾の次から)。データは 256 KiB ごとの区間 (ドキュメントの位置で決まる区切り) に
    /// 分けて照合し、メモリ使用量を一定に保つ。
    /// </summary>
    internal void ScanMulti(ReadOnlySpan<byte> data, long baseOffset, long minStart, long maxStart, bool overlapping, in ScanContext context,
        Func<SearchMatch, MatchDecision> onMatch)
    {
        int from = (int)Math.Clamp(minStart - baseOffset, 0, data.Length);
        int end = (int)Math.Clamp(maxStart - baseOffset, 0, data.Length);
        var raw = new List<RawMatch>();
        long next = baseOffset + from; // 重ならない一致: 次に採れる開始位置
        bool positional = Position is { IsNone: false } || Alignment > 1;
        for (long window = (baseOffset + from) / SubWindow * SubWindow; window < baseOffset + end; window += SubWindow)
        {
            context.CheckCancel?.Invoke();
            int cs = (int)Math.Max(from, window - baseOffset);
            int ce = (int)Math.Min(end, window + SubWindow - baseOffset);
            int gatherFrom = (int)Math.Max(cs, next - baseOffset);
            if (gatherFrom >= ce)
            {
                continue;
            }

            int dataEnd = (int)Math.Min(data.Length, (long)ce + MaxMatchLength - 1);
            raw.Clear();

            // 位置の条件で除く一致があると、重ならない一致の数え方が変わるため、重なる一致も集めてからここで選ぶ。
            _multi!.Gather(data[..dataEnd], baseOffset, gatherFrom, ce, overlapping || positional, context, raw);
            raw.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Variant.CompareTo(b.Variant));
            int i = 0;
            while (i < raw.Count)
            {
                int start = raw[i].Start;
                int j = i;
                while (j < raw.Count && raw[j].Start == start)
                {
                    j++;
                }

                long at = baseOffset + start;
                if (at >= next && Accepts(at))
                {
                    bool accepted = false;
                    long groupEnd = at + 1;
                    int lastVariant = -1;
                    for (int k = i; k < j; k++)
                    {
                        RawMatch m = raw[k];
                        if (m.Variant == lastVariant)
                        {
                            // 同じ種類の同じ位置の一致は 1 回だけ (位相を変えた正規表現の照合など)。
                            continue;
                        }

                        lastVariant = m.Variant;
                        switch (onMatch(new SearchMatch(at, m.Length, m.Variant)))
                        {
                            case MatchDecision.Stop:
                                return;
                            case MatchDecision.Accept:
                                accepted = true;
                                groupEnd = Math.Max(groupEnd, at + m.Length);
                                break;
                        }
                    }

                    if (accepted && !overlapping)
                    {
                        next = groupEnd;
                    }
                }

                i = j;
            }
        }
    }
}
