using HexEditor.Core.Engine;

namespace HexEditor.Core.Search;

/// <summary>検索したスナップショットより後の編集に対する、一致の状態 (FIND-03 の仕様 3)。</summary>
public enum MatchStatus
{
    /// <summary>一致の範囲は変わっていない (位置だけずれることがある)。印なし。</summary>
    Unchanged,

    /// <summary>一致の範囲の中が変更・一部削除された (「変更あり」)。</summary>
    Modified,

    /// <summary>一致の範囲がすべて削除された (「削除済み」)。位置は削除された位置。</summary>
    Deleted,

    /// <summary>
    /// 元データが切り替わったため対応が分からない (保存で元データが新しいファイルになった後など)。位置は検索した時点のまま。
    /// UI は「古い結果」として扱う (FIND-03 の「エラー」)。
    /// </summary>
    Stale,
}

/// <summary>編集に合わせて補正した一致の位置と状態。</summary>
public readonly record struct TrackedMatch(long Offset, long Length, MatchStatus Status);

/// <summary>
/// 検索したスナップショット (<see cref="From"/>) 上の一致を、別の時点のスナップショット (<see cref="To"/>。通常は現在の状態) 上の
/// 位置に対応させる (FIND-03 の仕様 3・4)。
/// <para>
/// ピースツリーのピースは不変で、各バイトは「どのデータのどの位置か」(元データ・追加バッファ上の位置、乱数列の位置) で
/// 一意に識別できる。そこで、一致の範囲のバイトが <see cref="To"/> のどこにあるかをピースの識別で探す。編集の記録を持たないため、
/// Undo / Redo で状態が戻れば、印も自然に外れる (仕様 4)。
/// </para>
/// <para>
/// パターンのピース (ENG-03) は位置を持たない繰り返しのため、同じ繰り返しの同じ位相のバイトを「同じ」とみなす。
/// 1 つの <see cref="MatchTracker"/> は多数の一致に使い回す (作成時に <see cref="To"/> の索引を作る)。スレッドセーフ。
/// </para>
/// </summary>
public sealed class MatchTracker
{
    private const int MaxCandidates = 16;

    private readonly Entry[] _entries;
    private readonly long[] _prefixMaxEnd;
    private readonly Dictionary<(long Offset, int PatternLength), List<(long DocOffset, long Length, long Phase)>> _patterns = [];
    private readonly bool _sameTree;
    private readonly bool _sameStorage;
    private (long, Piece)[]? _fromPieces;

    public MatchTracker(DocumentSnapshot from, DocumentSnapshot to)
    {
        From = from;
        To = to;
        _sameTree = ReferenceEquals(from.Tree, to.Tree);
        _sameStorage = ReferenceEquals(from.Storage, to.Storage);
        var entries = new List<Entry>();
        if (!_sameTree && _sameStorage)
        {
            foreach ((long doc, Piece piece) in to.Tree.EnumerateAll())
            {
                if (piece.Kind == PieceKind.Pattern)
                {
                    var key = (piece.Offset, piece.PatternLength);
                    if (!_patterns.TryGetValue(key, out var list))
                    {
                        _patterns[key] = list = [];
                    }

                    list.Add((doc, piece.Length, piece.Phase));
                }
                else
                {
                    entries.Add(new Entry(piece.Kind, piece.Seed, piece.Offset, piece.Length, doc));
                }
            }

            entries.Sort(static (a, b) => Compare(a.Kind, a.Seed, a.IdStart, b.Kind, b.Seed, b.IdStart));
        }

        _entries = [.. entries];
        _prefixMaxEnd = new long[_entries.Length];
        for (int i = 0; i < _entries.Length; i++)
        {
            bool groupStart = i == 0 || _entries[i].Kind != _entries[i - 1].Kind || _entries[i].Seed != _entries[i - 1].Seed;
            _prefixMaxEnd[i] = groupStart ? _entries[i].IdEnd : Math.Max(_prefixMaxEnd[i - 1], _entries[i].IdEnd);
        }
    }

    /// <summary>一致を見つけたスナップショット。</summary>
    public DocumentSnapshot From { get; }

    /// <summary>対応させる先のスナップショット。</summary>
    public DocumentSnapshot To { get; }

    public TrackedMatch Track(SearchMatch match) => Track(match.Offset, match.Length);

    /// <summary><see cref="From"/> 上の [offset, offset + length) を <see cref="To"/> 上に対応させる。</summary>
    public TrackedMatch Track(long offset, long length)
    {
        if (_sameTree)
        {
            return new TrackedMatch(offset, length, MatchStatus.Unchanged);
        }

        if (!_sameStorage)
        {
            return new TrackedMatch(offset, length, MatchStatus.Stale);
        }

        length = Math.Max(0, Math.Min(length, From.Length - offset));
        if (length == 0)
        {
            long at = MapPoint(offset);
            return new TrackedMatch(at, 0, MatchStatus.Unchanged);
        }

        var segments = From.Tree.Enumerate(offset, length).ToList();

        // 範囲がそのまま残っているか: 候補のずれを集めて、ピースの識別を 1 バイトずつ照らし合わせる。
        foreach (long delta in Candidates(segments, offset))
        {
            long b = offset + delta;
            if (b >= 0 && b + length <= To.Length && SameBytes(offset, b, length))
            {
                return new TrackedMatch(b, length, MatchStatus.Unchanged);
            }
        }

        long start = MapPoint(offset);
        long end = MapPoint(offset + length);
        bool survives = segments.Any(s => Survives(s.Piece));
        return survives
            ? new TrackedMatch(start, Math.Max(0, end - start), MatchStatus.Modified)
            : new TrackedMatch(start, 0, MatchStatus.Deleted);
    }

    /// <summary>
    /// <see cref="From"/> 上の位置 <paramref name="x"/> (バイト x の直前の境界) に当たる <see cref="To"/> 上の位置。
    /// バイト x が残っていればその位置、なければ、その前で残っている最後のバイトの直後 (削除された位置)。
    /// </summary>
    public long MapPoint(long x)
    {
        if (_sameTree)
        {
            return x;
        }

        x = Math.Clamp(x, 0, From.Length);
        if (x < From.Length)
        {
            (long doc, Piece piece) = From.Tree.Enumerate(x, 1).First();
            if (piece.Kind != PieceKind.Pattern)
            {
                foreach ((long idStart, long idEnd, long toDoc) in Query(piece.Kind, piece.Seed, piece.Offset, piece.Offset + 1))
                {
                    return toDoc + (piece.Offset - idStart) + (x - doc);
                }
            }
        }

        // 前に向かって、残っている最後のバイトを探す。窓を倍々に広げる。
        long windowEnd = x;
        for (long window = 4096; windowEnd > 0; window = Math.Min(window * 2, 1L << 40))
        {
            long windowStart = Math.Max(0, windowEnd - window);
            var segments = From.Tree.Enumerate(windowStart, windowEnd - windowStart).ToList();
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                (long doc, Piece piece) = segments[i];
                if (piece.Kind == PieceKind.Pattern)
                {
                    continue;
                }

                long best = -1;
                long bestTo = 0;
                foreach ((long idStart, long idEnd, long toDoc) in Query(piece.Kind, piece.Seed, piece.Offset, piece.Offset + piece.Length))
                {
                    long lastId = Math.Min(idEnd, piece.Offset + piece.Length) - 1;
                    long fromPos = doc + (lastId - piece.Offset);
                    if (fromPos > best)
                    {
                        best = fromPos;
                        bestTo = toDoc + (lastId - idStart);
                    }
                }

                if (best >= 0)
                {
                    return bestTo + 1;
                }
            }

            windowEnd = windowStart;
        }

        return 0;
    }

    /// <summary>範囲が丸ごと残っている場合のずれ (To の位置 − From の位置) の候補。</summary>
    private List<long> Candidates(List<(long DocumentOffset, Piece Piece)> segments, long offset)
    {
        var result = new List<long>();
        foreach ((long doc, Piece piece) in segments)
        {
            if (piece.Kind == PieceKind.Pattern)
            {
                continue;
            }

            foreach ((long idStart, long idEnd, long toDoc) in Query(piece.Kind, piece.Seed, piece.Offset, piece.Offset + piece.Length))
            {
                long id = Math.Max(idStart, piece.Offset);
                long delta = (toDoc + (id - idStart)) - (doc + (id - piece.Offset));
                if (!result.Contains(delta))
                {
                    result.Add(delta);
                }

                if (result.Count >= MaxCandidates)
                {
                    break;
                }
            }

            // 最初の追跡できるピースで候補が出れば十分 (残っているならこのピースも同じずれで残っている)。
            if (result.Count > 0)
            {
                result.Sort((x, y) => Math.Abs(x).CompareTo(Math.Abs(y)));
                return result;
            }
        }

        // 範囲がすべてパターンのバイト: まず、From のパターンのピース全体が To にそのまま (同じ長さ・位相で) あれば、そのずれ。
        // なければ、同じ繰り返しの、位相が合う最も近い位置を候補にする (同じ内容なので、どれを選んでも一致は成り立つ)。
        (long firstDoc, Piece first) = segments[0];
        if (first.Kind == PieceKind.Pattern && _patterns.TryGetValue((first.Offset, first.PatternLength), out var places))
        {
            (long wholeStart, Piece whole) = PieceAt(firstDoc);
            foreach ((long y0, long len, long phase) in places)
            {
                if (len == whole.Length && phase == whole.Phase && !result.Contains(y0 - wholeStart))
                {
                    result.Add(y0 - wholeStart);
                }
            }

            int pl = first.PatternLength;
            var nearest = new List<long>();
            foreach ((long y0, long len, long phase) in places)
            {
                long shift = ((first.Phase - phase) % pl + pl) % pl;
                long y = y0 + shift;
                if (y >= y0 + len)
                {
                    continue;
                }

                // offset に最も近い、位相の合う位置。
                long k = Math.Max(0, (offset - y) / pl);
                long[] options = [y + k * pl, y + (k + 1) * pl];
                foreach (long candidate in options)
                {
                    if (candidate < y0 + len && !result.Contains(candidate - firstDoc) && !nearest.Contains(candidate - firstDoc))
                    {
                        nearest.Add(candidate - firstDoc);
                    }
                }

                if (nearest.Count >= MaxCandidates)
                {
                    break;
                }
            }

            result.AddRange(nearest.OrderBy(Math.Abs));
        }

        return result;
    }

    /// <summary>From のドキュメント上の位置 <paramref name="doc"/> を含むピース (切り詰めない全体) とその開始位置。</summary>
    private (long Start, Piece Piece) PieceAt(long doc)
    {
        (long, Piece)[] pieces = _fromPieces ??= [.. From.Tree.EnumerateAll()];
        int left = 0;
        int right = pieces.Length - 1;
        while (left < right)
        {
            int mid = (left + right + 1) >>> 1;
            if (pieces[mid].Item1 <= doc)
            {
                left = mid;
            }
            else
            {
                right = mid - 1;
            }
        }

        return pieces[left];
    }

    /// <summary>From の [a, a + length) と To の [b, b + length) が、同じ識別のバイトの並びか。</summary>
    private bool SameBytes(long a, long b, long length)
    {
        using IEnumerator<(long DocumentOffset, Piece Piece)> left = From.Tree.Enumerate(a, length).GetEnumerator();
        using IEnumerator<(long DocumentOffset, Piece Piece)> right = To.Tree.Enumerate(b, length).GetEnumerator();
        Piece p = default;
        Piece q = default;
        long pUsed = 0;
        long qUsed = 0;
        bool pValid = false;
        bool qValid = false;
        long done = 0;
        while (done < length)
        {
            if (!pValid || pUsed == p.Length)
            {
                if (!left.MoveNext())
                {
                    return false;
                }

                p = left.Current.Piece;
                pUsed = 0;
                pValid = true;
            }

            if (!qValid || qUsed == q.Length)
            {
                if (!right.MoveNext())
                {
                    return false;
                }

                q = right.Current.Piece;
                qUsed = 0;
                qValid = true;
            }

            long n = Math.Min(p.Length - pUsed, q.Length - qUsed);
            if (!SameIdentity(p.WithRange(pUsed, n), q.WithRange(qUsed, n)))
            {
                return false;
            }

            pUsed += n;
            qUsed += n;
            done += n;
        }

        return true;
    }

    private static bool SameIdentity(Piece p, Piece q) =>
        p.Kind == q.Kind && p.Length == q.Length && p.Kind switch
        {
            PieceKind.Pattern => p.Offset == q.Offset && p.PatternLength == q.PatternLength && p.Phase == q.Phase,
            PieceKind.Random => p.Seed == q.Seed && p.Offset == q.Offset,
            _ => p.Offset == q.Offset,
        };

    /// <summary>ピースのバイトのどれかが To に残っているか。</summary>
    private bool Survives(Piece piece) => piece.Kind == PieceKind.Pattern
        ? _patterns.ContainsKey((piece.Offset, piece.PatternLength))
        : Query(piece.Kind, piece.Seed, piece.Offset, piece.Offset + piece.Length).Any();

    /// <summary>To のピースのうち、識別の範囲が [a, b) と重なるもの (識別の開始、終わり、To 上の位置)。</summary>
    private IEnumerable<(long IdStart, long IdEnd, long DocOffset)> Query(PieceKind kind, ulong seed, long a, long b)
    {
        // 同じ種類・種のまとまりの範囲。
        int lo = LowerBound(kind, seed, long.MinValue);
        int hi = UpperBoundOfGroup(lo, kind, seed);

        // 終わりの累積の最大値は単調に増えるので、a を越える最初の位置から調べる。
        int left = lo;
        int right = hi;
        while (left < right)
        {
            int mid = (left + right) >>> 1;
            if (_prefixMaxEnd[mid] > a)
            {
                right = mid;
            }
            else
            {
                left = mid + 1;
            }
        }

        for (int i = left; i < hi && _entries[i].IdStart < b; i++)
        {
            Entry e = _entries[i];
            if (e.IdEnd > a)
            {
                yield return (e.IdStart, e.IdEnd, e.DocOffset);
            }
        }
    }

    private int UpperBoundOfGroup(int lo, PieceKind kind, ulong seed)
    {
        int left = lo;
        int right = _entries.Length;
        while (left < right)
        {
            int mid = (left + right) >>> 1;
            if (_entries[mid].Kind == kind && _entries[mid].Seed == seed)
            {
                left = mid + 1;
            }
            else
            {
                right = mid;
            }
        }

        return left;
    }

    private int LowerBound(PieceKind kind, ulong seed, long idStart)
    {
        int left = 0;
        int right = _entries.Length;
        while (left < right)
        {
            int mid = (left + right) >>> 1;
            Entry e = _entries[mid];
            if (Compare(e.Kind, e.Seed, e.IdStart, kind, seed, idStart) < 0)
            {
                left = mid + 1;
            }
            else
            {
                right = mid;
            }
        }

        return left;
    }

    private static int Compare(PieceKind k1, ulong s1, long i1, PieceKind k2, ulong s2, long i2)
    {
        int c = k1.CompareTo(k2);
        if (c != 0)
        {
            return c;
        }

        c = s1.CompareTo(s2);
        return c != 0 ? c : i1.CompareTo(i2);
    }

    /// <summary>To のパターン以外のピース 1 つ (識別の範囲と To 上の位置)。</summary>
    private readonly record struct Entry(PieceKind Kind, ulong Seed, long IdStart, long Length, long DocOffset)
    {
        public long IdEnd => IdStart + Length;
    }
}
