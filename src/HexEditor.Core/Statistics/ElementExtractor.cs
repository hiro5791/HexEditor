namespace HexEditor.Core.Statistics;

/// <summary>1 つの要素のバイト列を受け取る。</summary>
internal delegate void ElementHandler(ReadOnlySpan<byte> bytes, long index);

/// <summary>
/// 順に届くデータから、論理位置 k × ストライドに始まる要素を取り出す (ANA-10 の仕様 2)。読み込みの区切りをまたぐ要素は
/// 前の読み込みの末尾と合わせて組み立てる。読めないバイトを含む要素は数えない。要素どうしが重なる (ストライド &lt; サイズ) 場合も扱う。
/// </summary>
internal sealed class ElementExtractor
{
    private readonly int _size;
    private readonly long _stride;
    private readonly byte[] _history;
    private readonly bool[] _historyBad;
    private int _historyLength;
    private long _next;

    public ElementExtractor(int size, int stride)
    {
        _size = size;
        _stride = Math.Max(1, stride);
        _history = new byte[Math.Max(1, size - 1)];
        _historyBad = new bool[_history.Length];
    }

    /// <summary>取り出した要素の数 (読めなかったものを含む)。</summary>
    public long Visited { get; private set; }

    /// <summary>読めないバイトを含むため数えなかった要素の数。</summary>
    public long Skipped { get; private set; }

    /// <summary>直前の要素が読めなかった (系列相関の組を切るために使う)。</summary>
    public bool LastSkipped { get; private set; }

    /// <summary>
    /// 論理位置 <paramref name="logical"/> から続くデータを渡す (前回の続きであること)。<paramref name="bad"/> は読めないバイトの位置。
    /// </summary>
    public void Feed(long logical, ReadOnlySpan<byte> data, IReadOnlyList<(int Start, int Length)> bad, ElementHandler handler)
    {
        long end = logical + data.Length;
        Span<byte> assembled = stackalloc byte[_size];
        while (_next + _size <= end)
        {
            long s = _next;
            bool isBad;
            scoped ReadOnlySpan<byte> bytes;
            if (s >= logical)
            {
                int at = (int)(s - logical);
                bytes = data.Slice(at, _size);
                isBad = bad.Count > 0 && Overlaps(bad, at, _size);
            }
            else
            {
                // 前の読み込みの末尾 (履歴) と今回の先頭を合わせる。
                int fromHistory = (int)(logical - s);
                int h0 = _historyLength - fromHistory;
                isBad = false;
                for (int i = 0; i < fromHistory; i++)
                {
                    assembled[i] = _history[h0 + i];
                    isBad |= _historyBad[h0 + i];
                }

                data[..(_size - fromHistory)].CopyTo(assembled[fromHistory..]);
                isBad |= bad.Count > 0 && Overlaps(bad, 0, _size - fromHistory);
                bytes = assembled;
            }

            Visited++;
            LastSkipped = isBad;
            if (isBad)
            {
                Skipped++;
            }
            else
            {
                handler(bytes, Visited - 1);
            }

            _next += _stride;
        }

        Remember(data, bad);
    }

    /// <summary>次の要素の先頭 (端数の計算に使う)。</summary>
    public long NextStart => _next;

    /// <summary>末尾のバイトを履歴として残す (次の読み込みとまたぐ要素のため)。</summary>
    private void Remember(ReadOnlySpan<byte> data, IReadOnlyList<(int Start, int Length)> bad)
    {
        int keep = _history.Length;
        if (_size <= 1)
        {
            return;
        }

        if (data.Length >= keep)
        {
            data[^keep..].CopyTo(_history);
            for (int i = 0; i < keep; i++)
            {
                _historyBad[i] = bad.Count > 0 && Overlaps(bad, data.Length - keep + i, 1);
            }

            _historyLength = keep;
            return;
        }

        // 今回のデータが短い: 古い履歴の後ろに足して、末尾の keep バイトを残す。
        int total = Math.Min(keep, _historyLength + data.Length);
        int fromOld = total - data.Length;
        byte[] merged = new byte[total];
        bool[] mergedBad = new bool[total];
        for (int i = 0; i < fromOld; i++)
        {
            merged[i] = _history[_historyLength - fromOld + i];
            mergedBad[i] = _historyBad[_historyLength - fromOld + i];
        }

        for (int i = 0; i < data.Length; i++)
        {
            merged[fromOld + i] = data[i];
            mergedBad[fromOld + i] = bad.Count > 0 && Overlaps(bad, i, 1);
        }

        merged.CopyTo(_history, 0);
        mergedBad.CopyTo(_historyBad, 0);
        _historyLength = total;
    }

    private static bool Overlaps(IReadOnlyList<(int Start, int Length)> bad, int at, int length)
    {
        foreach ((int s, int l) in bad)
        {
            if (s < at + length && at < s + l)
            {
                return true;
            }

            if (s >= at + length)
            {
                break;
            }
        }

        return false;
    }
}
