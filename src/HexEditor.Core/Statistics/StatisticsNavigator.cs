using HexEditor.Core.Engine;

namespace HexEditor.Core.Statistics;

/// <summary>統計の結果からドキュメントの位置へ移動する (ANA-10 の仕様 8: 値 (ビン) の次の出現位置)。</summary>
public static class StatisticsNavigator
{
    /// <summary>
    /// ドキュメント上のオフセット <paramref name="after"/> より後ろで、値が <paramref name="match"/> を満たす最初の要素の位置を返す。
    /// 要素の位置は対象の先頭からストライドごと。<paramref name="wrap"/> なら末尾まで見つからなければ先頭から探し直す。
    /// 見つからなければ null。
    /// </summary>
    public static long? FindNext(DocumentSnapshot snapshot, LogicalRanges ranges, ElementSpec spec, long after, Func<double, bool> match,
        bool wrap = false, CancellationToken cancellationToken = default)
    {
        long from = (ranges.ToLogical(after) is long l ? l + 1 : after < ranges.Start ? 0 : ranges.Length);
        if (Scan(snapshot, ranges, spec, from, ranges.Length, match, cancellationToken) is long found)
        {
            return found;
        }

        return wrap ? Scan(snapshot, ranges, spec, 0, Math.Min(from, ranges.Length), match, cancellationToken) : null;
    }

    private static long? Scan(DocumentSnapshot snapshot, LogicalRanges ranges, ElementSpec spec, long from, long to, Func<double, bool> match,
        CancellationToken token)
    {
        int stride = spec.EffectiveStride;
        long first = (from + stride - 1) / stride * stride;
        if (first >= to)
        {
            return null;
        }

        var extractor = new ElementExtractor(spec.Size, stride);
        var scanner = new RangeScanner(snapshot, ranges, 1024 * 1024, token);
        long? hit = null;
        foreach (ScanChunk chunk in scanner.Read(first, Math.Min(ranges.Length, to + spec.Size - 1)))
        {
            extractor.Feed(chunk.Logical - first, chunk.Span, chunk.Bad, (bytes, index) =>
            {
                if (hit is null && first + (index * stride) < to && match(ElementTypes.ReadDouble(spec.Type, bytes, spec.BigEndian)))
                {
                    hit = first + (index * stride);
                }
            });
            if (hit is not null)
            {
                break;
            }
        }

        return hit is long logical ? ranges.ToDocument(logical) : null;
    }
}
