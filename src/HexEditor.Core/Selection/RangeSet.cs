using System.Collections;

namespace HexEditor.Core.Selection;

/// <summary>
/// 重ならない範囲をオフセット順に並べた集合 (マルチ選択の要素。EDIT-07 の仕様 1)。追加した範囲が既存の範囲と重なる、または隣り合う場合は
/// 1 つに結合する。
/// </summary>
/// <remarks>
/// 要素は 2 段の B 木 (最大 <see cref="ChunkSize"/> 個の要素を持つ葉を、先頭の順に並べたもの) で持つ。追加・削除・位置の検索は
/// O(log 要素数 + 葉の大きさ) で、100 万要素でも 1 回あたり数マイクロ秒で済む (EDIT-07 の「巨大ファイル・長時間処理」)。
/// オフセット順に追加する場合 (検索結果の変換など) は末尾への追記になり、O(1)。スレッド セーフではない (UI のスレッドで使う。
/// バックグラウンドの処理には <see cref="Clone"/> を渡す)。
/// </remarks>
public sealed class RangeSet : IReadOnlyCollection<ByteRange>
{
    /// <summary>葉 1 つの要素数の上限。超えたら半分に分ける。</summary>
    public const int ChunkSize = 1024;

    private readonly List<List<ByteRange>> _chunks = [];
    private int _count;
    private long _total;

    public RangeSet()
    {
    }

    public RangeSet(IEnumerable<ByteRange> ranges)
    {
        foreach (ByteRange r in ranges)
        {
            Add(r);
        }
    }

    /// <summary>要素数。</summary>
    public int Count => _count;

    /// <summary>要素の合計バイト数。</summary>
    public long TotalLength => _total;

    public bool IsEmpty => _count == 0;

    /// <summary>最初の要素 (空なら例外)。</summary>
    public ByteRange First => _count > 0 ? _chunks[0][0] : throw new InvalidOperationException("要素がありません。");

    /// <summary>最後の要素 (空なら例外)。</summary>
    public ByteRange Last => _count > 0 ? _chunks[^1][^1] : throw new InvalidOperationException("要素がありません。");

    /// <summary>すべての要素を含む最小の範囲 (空なら長さ 0)。</summary>
    public ByteRange Bounds => _count > 0 ? ByteRange.FromBounds(First.Start, Last.End) : default;

    /// <summary>
    /// 範囲を加える。重なる・隣り合う要素と結合し、結合した後の要素を返す。長さ 0 以下の範囲は加えない (そのまま返す)。
    /// </summary>
    public ByteRange Add(ByteRange range)
    {
        if (range.Length <= 0)
        {
            return range;
        }

        if (_count == 0 || range.Start > Last.End)
        {
            Append(range);
            return range;
        }

        (int c, int i) = LowerBound(range.Start, inclusive: true);
        long start = range.Start, end = range.End;
        while (c < _chunks.Count)
        {
            List<ByteRange> chunk = _chunks[c];
            if (i >= chunk.Count)
            {
                c++;
                i = 0;
                continue;
            }

            if (chunk[i].Start > end)
            {
                break;
            }

            // この葉の中で結合する要素 [i, j) をまとめて取り除く。
            int j = i;
            while (j < chunk.Count && chunk[j].Start <= end)
            {
                start = Math.Min(start, chunk[j].Start);
                end = Math.Max(end, chunk[j].End);
                _total -= chunk[j].Length;
                j++;
            }

            chunk.RemoveRange(i, j - i);
            _count -= j - i;
            if (chunk.Count == 0)
            {
                _chunks.RemoveAt(c);
                i = 0;
            }
            else if (i < chunk.Count)
            {
                break;
            }
        }

        var merged = ByteRange.FromBounds(start, end);
        InsertAt(c, i, merged);
        return merged;
    }

    /// <summary>オフセット順に並んだ範囲をまとめて加える (結合の規則は <see cref="Add(ByteRange)"/> と同じ)。</summary>
    public void AddRange(IEnumerable<ByteRange> ranges)
    {
        foreach (ByteRange r in ranges)
        {
            Add(r);
        }
    }

    /// <summary><paramref name="offset"/> を含む要素を取り除く。取り除いた要素を返す (なければ null)。</summary>
    public ByteRange? RemoveAt(long offset)
    {
        (int c, int i) = LowerBound(offset, inclusive: false);
        if (c >= _chunks.Count || i >= _chunks[c].Count || _chunks[c][i].Start > offset)
        {
            return null;
        }

        ByteRange removed = _chunks[c][i];
        RemoveElement(c, i);
        return removed;
    }

    /// <summary>要素 <paramref name="range"/> (と同じ開始の要素) を取り除く。</summary>
    public bool Remove(ByteRange range) => Find(range.Start) == range && RemoveAt(range.Start) is not null;

    /// <summary><paramref name="offset"/> を含む要素。なければ null。</summary>
    public ByteRange? Find(long offset)
    {
        (int c, int i) = LowerBound(offset, inclusive: false);
        return c < _chunks.Count && i < _chunks[c].Count && _chunks[c][i].Start <= offset ? _chunks[c][i] : null;
    }

    public bool Contains(long offset) => Find(offset) is not null;

    /// <summary>[start, start + length) と重なる要素 (オフセット順)。描画では見えている範囲だけを尋ねる。</summary>
    public IEnumerable<ByteRange> Overlapping(long start, long length)
    {
        long end = start + length;
        (int c, int i) = LowerBound(start, inclusive: false);
        for (; c < _chunks.Count; c++, i = 0)
        {
            List<ByteRange> chunk = _chunks[c];
            for (; i < chunk.Count; i++)
            {
                if (chunk[i].Start >= end)
                {
                    yield break;
                }

                yield return chunk[i];
            }
        }
    }

    /// <summary>[start, start + length) と重なる、または隣り合う要素 (結合の対象)。</summary>
    public IEnumerable<ByteRange> Touching(long start, long length)
    {
        long end = start + length;
        (int c, int i) = LowerBound(start, inclusive: true);
        for (; c < _chunks.Count; c++, i = 0)
        {
            List<ByteRange> chunk = _chunks[c];
            for (; i < chunk.Count; i++)
            {
                if (chunk[i].Start > end)
                {
                    yield break;
                }

                yield return chunk[i];
            }
        }
    }

    /// <summary>開始が <paramref name="offset"/> より後ろの最初の要素 (「次の要素へ」。EDIT-07 の仕様 6)。なければ null。</summary>
    public ByteRange? NextAfter(long offset)
    {
        (int c, int i) = LowerBound(offset, inclusive: false);
        if (c < _chunks.Count && i < _chunks[c].Count && _chunks[c][i].Start <= offset)
        {
            (c, i) = Advance(c, i);
        }

        return c < _chunks.Count && i < _chunks[c].Count ? _chunks[c][i] : null;
    }

    /// <summary>開始が <paramref name="offset"/> より前の最後の要素 (「前の要素へ」)。なければ null。</summary>
    public ByteRange? PreviousBefore(long offset)
    {
        (int c, int i) = LowerBound(offset, inclusive: false);
        if (c < _chunks.Count && i < _chunks[c].Count && _chunks[c][i].Start < offset)
        {
            return _chunks[c][i];
        }

        (c, i) = Retreat(c, i);
        return c >= 0 ? _chunks[c][i] : null;
    }

    /// <summary>ドキュメント [0, documentLength) のうち、どの要素にも含まれない範囲 (「選択を反転」。EDIT-07 の仕様 4)。</summary>
    public RangeSet Invert(long documentLength)
    {
        var result = new RangeSet();
        long position = 0;
        foreach (ByteRange r in this)
        {
            if (r.Start >= documentLength)
            {
                break;
            }

            if (r.Start > position)
            {
                result.Append(ByteRange.FromBounds(position, r.Start));
            }

            position = Math.Max(position, r.End);
        }

        if (position < documentLength)
        {
            result.Append(ByteRange.FromBounds(position, documentLength));
        }

        return result;
    }

    /// <summary>すべての要素を <paramref name="delta"/> バイトずらした集合 (「ずらす」。EDIT-05 の仕様 6)。</summary>
    public RangeSet Shifted(long delta)
    {
        var result = new RangeSet();
        foreach (ByteRange r in this)
        {
            result.Append(r with { Start = r.Start + delta });
        }

        return result;
    }

    /// <summary>
    /// [0, <paramref name="length"/>) に収まるように切り詰める (EDIT-09 の仕様 4)。途中で切った要素の数と、取り除いた要素の数を返す。
    /// </summary>
    public (int Truncated, int Removed) ClipTo(long length)
    {
        int truncated = 0, removed = 0;
        while (_count > 0 && Last.End > length)
        {
            ByteRange last = Last;
            RemoveElement(_chunks.Count - 1, _chunks[^1].Count - 1);
            if (last.Start < length)
            {
                Append(ByteRange.FromBounds(last.Start, length));
                truncated++;
                break;
            }

            removed++;
        }

        return (truncated, removed);
    }

    public void Clear()
    {
        _chunks.Clear();
        _count = 0;
        _total = 0;
    }

    /// <summary>同じ要素を持つ別の集合 (バックグラウンドの処理に渡す)。</summary>
    public RangeSet Clone()
    {
        var copy = new RangeSet();
        foreach (List<ByteRange> chunk in _chunks)
        {
            copy._chunks.Add([.. chunk]);
        }

        copy._count = _count;
        copy._total = _total;
        return copy;
    }

    public IEnumerator<ByteRange> GetEnumerator()
    {
        foreach (List<ByteRange> chunk in _chunks)
        {
            foreach (ByteRange r in chunk)
            {
                yield return r;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>後ろから順の要素 (長さが変わる編集はオフセットの大きい要素から行う。EDIT-07 の仕様 8)。</summary>
    public IEnumerable<ByteRange> Reversed()
    {
        for (int c = _chunks.Count - 1; c >= 0; c--)
        {
            List<ByteRange> chunk = _chunks[c];
            for (int i = chunk.Count - 1; i >= 0; i--)
            {
                yield return chunk[i];
            }
        }
    }

    // ---- 内部 ----

    /// <summary>
    /// 終わりが <paramref name="x"/> 以上 (<paramref name="inclusive"/>) / より大きい最初の要素の位置。要素は重ならないので、
    /// 終わりの順と開始の順は同じ。なければ (葉の数, 0)。
    /// </summary>
    private (int Chunk, int Index) LowerBound(long x, bool inclusive)
    {
        int lo = 0, hi = _chunks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Satisfies(_chunks[mid][^1]))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (lo == _chunks.Count)
        {
            return (lo, 0);
        }

        List<ByteRange> chunk = _chunks[lo];
        int a = 0, b = chunk.Count;
        while (a < b)
        {
            int mid = (a + b) >>> 1;
            if (Satisfies(chunk[mid]))
            {
                b = mid;
            }
            else
            {
                a = mid + 1;
            }
        }

        return (lo, a);

        bool Satisfies(ByteRange r) => inclusive ? r.End >= x : r.End > x;
    }

    private (int, int) Advance(int c, int i) =>
        i + 1 < _chunks[c].Count ? (c, i + 1) : (c + 1, 0);

    private (int, int) Retreat(int c, int i)
    {
        if (c < _chunks.Count && i > 0)
        {
            return (c, i - 1);
        }

        int prev = Math.Min(c, _chunks.Count) - 1;
        return prev >= 0 ? (prev, _chunks[prev].Count - 1) : (-1, -1);
    }

    private void Append(ByteRange range)
    {
        if (_chunks.Count == 0 || _chunks[^1].Count >= ChunkSize)
        {
            _chunks.Add(new List<ByteRange>(Math.Min(ChunkSize, 16)));
        }

        _chunks[^1].Add(range);
        _count++;
        _total += range.Length;
    }

    private void InsertAt(int c, int i, ByteRange range)
    {
        if (_chunks.Count == 0)
        {
            Append(range);
            return;
        }

        if (c >= _chunks.Count)
        {
            c = _chunks.Count - 1;
            i = _chunks[c].Count;
        }

        List<ByteRange> chunk = _chunks[c];
        chunk.Insert(i, range);
        _count++;
        _total += range.Length;
        if (chunk.Count > ChunkSize)
        {
            int half = chunk.Count / 2;
            var right = chunk.GetRange(half, chunk.Count - half);
            chunk.RemoveRange(half, chunk.Count - half);
            _chunks.Insert(c + 1, right);
        }
    }

    private void RemoveElement(int c, int i)
    {
        List<ByteRange> chunk = _chunks[c];
        _total -= chunk[i].Length;
        _count--;
        chunk.RemoveAt(i);
        if (chunk.Count == 0)
        {
            _chunks.RemoveAt(c);
        }
    }
}
