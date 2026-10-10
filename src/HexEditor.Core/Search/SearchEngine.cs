using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>
/// 見つかった一致。<see cref="Wrapped"/> は折り返して見つけたか (FIND-09 の仕様 4)。<see cref="Variant"/> は一致した種類
/// (<see cref="SearchPattern.Variants"/> の添字。一致した文字コード・語。FIND-08 の仕様 4、FIND-26 の仕様 6)。
/// </summary>
public readonly record struct SearchHit(long Offset, long Length, bool Wrapped, int Variant = 0);

/// <summary>
/// スナップショットに対する検索 (FIND-01〜03、FIND-09、FIND-11、FIND-12)。チャンク単位で読み、前のチャンクの末尾を
/// 「一致の最長 − 1」バイト重ねて読むため、チャンクの境界をまたぐ一致も見つかる。リテラルは SIMD 化された
/// <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> を使う。
/// 読めないバイト (不良セクタなど) を含む一致は報告しない。読めない範囲は <see cref="SearchOptions.OnUnreadable"/> に知らせる。
/// </summary>
public static class SearchEngine
{
    /// <summary>チャンクの既定のサイズ (FIND-01 の仕様 2)。</summary>
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>件数を自動で数える範囲の上限 (FIND-12 の仕様 2)。これ以下なら自動で数える。</summary>
    public const long AutoCountMaxBytes = 1L << 30;

    /// <summary>数え上げを止める件数 (FIND-12 の仕様 3)。これを超えたら「100 万件以上」。</summary>
    public const int CountLimit = 1_000_000;

    /// <summary>この大きさ以上のチャンクでは、次のチャンクを別のスレッドで先読みする (FIND-01 の仕様 7)。</summary>
    private const int PrefetchThreshold = 64 * 1024;

    /// <summary>すべて検索の既定の並列数: 論理プロセッサ数 − 1 (最小 1、最大 8。FIND-01 の仕様 7)。</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    /// <summary>範囲の大きさから、件数を自動で数えるかを決める (FIND-12 の仕様 2)。</summary>
    public static bool CountsAutomatically(long scopeBytes) => scopeBytes <= AutoCountMaxBytes;

    // ---- 次 / 前を検索 ----

    /// <summary>
    /// <paramref name="start"/> から次 (前方) または前 (後方) の一致を、ドキュメント全体から探す。前方は start 以上で始まる一致、
    /// 後方は start 未満で始まる一致を探す。<paramref name="wrap"/> なら反対側から開始位置まで続ける。
    /// </summary>
    public static SearchHit? Find(DocumentSnapshot snapshot, SearchPattern pattern, long start, bool forward, bool wrap,
        LongRunningOperation? operation = null, int chunkSize = DefaultChunkSize) =>
        Find(snapshot, pattern, start, forward, wrap, new SearchOptions { ChunkSize = chunkSize }, operation);

    /// <summary>
    /// <paramref name="start"/> から次 (前方) または前 (後方) の一致を、<see cref="SearchOptions.Scope"/> の中で探す (FIND-09、FIND-11)。
    /// 前方は start 以上で始まる一致、後方は start 未満で始まる一致。<paramref name="wrap"/> なら範囲の反対側から開始位置まで続ける。
    /// 1 スレッドで照合し、別のスレッドで次のチャンクを先読みする (FIND-01 の仕様 7)。
    /// </summary>
    public static SearchHit? Find(DocumentSnapshot snapshot, SearchPattern pattern, long start, bool forward, bool wrap,
        SearchOptions options, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SearchRange> ranges = options.Scope.Resolve(snapshot.Length);
        start = Math.Clamp(start, 0, snapshot.Length);
        pattern = pattern.ForStart(start); // 一致しない箇所の検索の繰り返しの起点 (FIND-25 の仕様 2)
        var scan = new Scan(snapshot, pattern, options, operation, cancellationToken, options.Scope.TotalLength(snapshot.Length));
        int maxLen = pattern.MaxMatchLength;
        if (forward)
        {
            foreach (SearchRange r in ranges)
            {
                long lo = Math.Max(r.Offset, start);
                if (lo < r.End && scan.ForwardIn(lo, r.End, long.MaxValue, r) is (long at, int len, int variant))
                {
                    return new SearchHit(at, len, false, variant);
                }
            }

            if (wrap)
            {
                foreach (SearchRange r in ranges)
                {
                    if (r.Offset < start && scan.ForwardIn(r.Offset, Math.Min(r.End, start + maxLen - 1), start, r) is (long at, int len, int variant))
                    {
                        return new SearchHit(at, len, true, variant);
                    }
                }
            }
        }
        else
        {
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                SearchRange r = ranges[i];
                if (r.Offset < start && scan.BackwardIn(r.Offset, Math.Min(r.End, start - 1 + maxLen), start, r) is (long at, int len, int variant))
                {
                    return new SearchHit(at, len, false, variant);
                }
            }

            if (wrap)
            {
                for (int i = ranges.Count - 1; i >= 0; i--)
                {
                    SearchRange r = ranges[i];
                    long lo = Math.Max(r.Offset, start);
                    if (lo < r.End && scan.BackwardIn(lo, r.End, long.MaxValue, r) is (long at, int len, int variant))
                    {
                        return new SearchHit(at, len, true, variant);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 単語単位の検索 (FIND-10 の仕様 4) で、[at, at + length) の前後が単語の境界か。範囲 <paramref name="range"/> の端は境界とみなす。
    /// 単語単位でなければ常に true。読めないバイトは境界とみなす。
    /// </summary>
    internal static bool AcceptsWord(DocumentSnapshot snapshot, SearchPattern pattern, SearchRange range, long at, long length, int variant = 0)
    {
        if (pattern.WordFor(variant) is not { } word)
        {
            return true;
        }

        Span<byte> before = stackalloc byte[WordBoundary.MaxCharBytes];
        Span<byte> after = stackalloc byte[WordBoundary.MaxCharBytes];
        int beforeLength = (int)Math.Clamp(at - range.Offset, 0, WordBoundary.MaxCharBytes);
        long end = at + length;
        int afterLength = (int)Math.Clamp(range.End - end, 0, WordBoundary.MaxCharBytes);
        if (beforeLength > 0 && !snapshot.Read(at - beforeLength, before[..beforeLength]).IsComplete)
        {
            beforeLength = 0;
        }

        if (afterLength > 0 && !snapshot.Read(end, after[..afterLength]).IsComplete)
        {
            afterLength = 0;
        }

        return word.Accept(before[..beforeLength], after[..afterLength]);
    }

    // ---- すべて検索・件数の数え上げ ----

    /// <summary>
    /// 範囲のすべての一致を探す (FIND-12 の数え上げ、FIND-20 の基盤)。チャンクを並列に処理し、結果は開始オフセットの昇順に
    /// <see cref="SearchResults"/> へ追加していく。キャンセルされた場合は、それまでの結果を残して <see cref="OperationCanceledException"/> を投げる。
    /// </summary>
    public static SearchResults FindAll(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions? options = null,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        var results = new SearchResults(snapshot, pattern, options ?? SearchOptions.Default);
        FindAll(results, operation, cancellationToken);
        return results;
    }

    /// <summary>
    /// <paramref name="results"/> の条件ですべての一致を探し、追加していく。呼び出し側は先に
    /// <see cref="SearchResults.MatchesAdded"/> を購読しておける (ストリーミング)。
    /// </summary>
    public static void FindAll(SearchResults results, LongRunningOperation? operation = null, CancellationToken cancellationToken = default) =>
        FindAllFrom(results, long.MinValue, operation, cancellationToken);

    /// <summary>
    /// 件数の上限で止めたすべて検索を続ける (FIND-20 の仕様 6)。上限を 2 倍にし、一覧の最後の一致の続き (重なる一致を含めない
    /// 場合は一致の末尾、含める場合は開始 + 1) から探す。重複も取りこぼしもない。
    /// </summary>
    public static void ContinueFindAll(SearchResults results, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        long? from = results.PrepareContinue();
        FindAllFrom(results, from ?? long.MinValue, operation, cancellationToken);
    }

    internal static void FindAllFrom(SearchResults results, long startFrom, LongRunningOperation? operation, CancellationToken cancellationToken)
    {
        var scan = new Scan(results.Snapshot, results.Pattern, results.Options, operation, cancellationToken, results.TotalBytes);
        try
        {
            bool limited = results.CustomFindAll is { } custom
                ? custom(results, startFrom, operation, cancellationToken)
                : scan.CollectAll(results, startFrom);
            results.SetState(limited ? SearchResultsState.LimitReached : SearchResultsState.Completed);
        }
        catch (OperationCanceledException)
        {
            results.SetState(SearchResultsState.Cancelled);
            throw;
        }
        catch
        {
            results.SetState(SearchResultsState.Failed);
            throw;
        }
    }

    /// <summary>
    /// 一致の件数を数える (FIND-12)。次を検索と番号を合わせるため、重なる一致も数える。<see cref="CountLimit"/> 件を超えたら止め、
    /// <see cref="SearchResults.LimitReached"/> にする。
    /// </summary>
    public static SearchResults Count(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions? options = null,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default) =>
        FindAll(snapshot, pattern, (options ?? SearchOptions.Default) with { IncludeOverlapping = true, MaxMatches = CountLimit },
            operation, cancellationToken);

    // ---- 表示中の範囲の強調 ----

    /// <summary>
    /// [offset, offset + length) と重なる一致 (表示中の範囲の強調。FIND-04 の仕様 9)。表示用の読み込みを使い、ブロックしない。
    /// まだ読み込み中のバイトがあれば <paramref name="complete"/> を false にする (読み込みの完了後に呼び直す)。
    /// 読めないバイト・読み込み中のバイトを含む一致は返さない。範囲 (<paramref name="scope"/>) の中に収まる一致だけを返す。
    /// </summary>
    public static IReadOnlyList<SearchMatch> FindInView(DocumentSnapshot snapshot, SearchPattern pattern, long offset, long length,
        SearchScope? scope, out bool complete)
    {
        complete = true;
        var result = new List<SearchMatch>();
        int maxLen = pattern.MaxMatchLength;
        foreach (SearchRange r in (scope ?? SearchScope.WholeDocument).Resolve(snapshot.Length))
        {
            long from = Math.Max(r.Offset, offset - maxLen + 1);
            long to = Math.Min(r.End, offset + length + maxLen - 1);
            if (to - from < pattern.MinMatchLength)
            {
                continue;
            }

            byte[] bytes = new byte[to - from];
            var states = new ByteState[bytes.Length];
            int n = snapshot.ReadForDisplay(from, bytes, states);
            int segStart = 0;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && states[i] == ByteState.Valid)
                {
                    continue;
                }

                if (i < n && states[i] == ByteState.Loading)
                {
                    complete = false;
                }

                SearchRange range = r;
                var context = new ScanContext(snapshot, r.Offset, r.End, segStart > 0 || from == r.Offset, i < n || from + i == r.End, ForView: true);
                try
                {
                    Collect(pattern, bytes.AsSpan(segStart, i - segStart), from + segStart, 0, offset + length, true, result,
                        m => m.End > offset, HasWords(pattern) ? m => AcceptsWord(snapshot, pattern, range, m.Offset, m.Length, m.Variant) : null, context);
                }
                catch (RegexChunkTimeoutException)
                {
                    // 表示中の範囲の強調は、時間がかかる場合はあきらめる (UI を止めない)。
                    complete = true;
                }

                segStart = i + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="data"/> の中で、開始が [minStart, maxStart) の一致を昇順に集める。<paramref name="overlapping"/> が false なら
    /// 一致の末尾の次から探す。<paramref name="accept"/> が false を返す一致 (単語の境界でないもの) は数えず、次の位置から探し直す。
    /// </summary>
    private static void Collect(SearchPattern pattern, ReadOnlySpan<byte> data, long baseOffset, long minStart, long maxStart,
        bool overlapping, List<SearchMatch> sink, Func<SearchMatch, bool>? filter, Func<SearchMatch, bool>? accept, in ScanContext context)
    {
        if (pattern.IsMulti)
        {
            pattern.ScanMulti(data, baseOffset, minStart, maxStart, overlapping, context, m =>
            {
                if (accept is not null && !accept(m))
                {
                    return MatchDecision.Reject;
                }

                if (filter is null || filter(m))
                {
                    sink.Add(m);
                }

                return MatchDecision.Accept;
            });
            return;
        }

        int from = (int)Math.Clamp(minStart - baseOffset, 0, data.Length);
        while (from < data.Length)
        {
            int found = pattern.IndexOf(data[from..], baseOffset + from, out int len);
            if (found < 0)
            {
                return;
            }

            long at = baseOffset + from + found;
            if (at >= maxStart)
            {
                return;
            }

            var match = new SearchMatch(at, len, pattern.VariantAt(data[(from + found)..]));
            if (accept is not null && !accept(match))
            {
                from += found + 1;
                continue;
            }

            if (filter is null || filter(match))
            {
                sink.Add(match);
            }

            from += found + (overlapping ? 1 : Math.Max(1, len));
        }
    }

    /// <summary>単語単位の判定が要るか (語ごとに違う場合も含む)。</summary>
    private static bool HasWords(SearchPattern pattern) => pattern.Word is not null || pattern.Parts.Any(p => p.Word is not null);

    /// <summary>1 回の検索の状態 (進捗、キャンセル、読めない範囲の報告)。</summary>
    private sealed class Scan
    {
        private readonly DocumentSnapshot _snapshot;
        private readonly SearchPattern _pattern;
        private readonly SearchOptions _options;
        private readonly LongRunningOperation? _operation;
        private readonly CancellationToken _token;
        private readonly long _total;
        private readonly int _chunkSize;
        private readonly int _overlap;
        private readonly object _reportLock = new();

        /// <summary>知らせ済みの読めない範囲 (同じバイトを 2 回知らせないため。昇順で重ならない)。</summary>
        private readonly List<SearchRange> _reported = [];
        private long _done;

        /// <summary>次 / 前を検索で今調べている範囲 (単語の境界の判定に使う)。</summary>
        private SearchRange _range;

        public Scan(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options, LongRunningOperation? operation,
            CancellationToken token, long total)
        {
            _snapshot = snapshot;
            _pattern = pattern;
            _options = options;
            _operation = operation;
            _token = token;
            _total = total;
            _chunkSize = Math.Max(1, options.ChunkSize);
            _overlap = pattern.MaxMatchLength - 1;
        }

        /// <summary>[from, to) にすっかり収まり、開始が maxStart 未満の最初の一致。<paramref name="range"/> は単語の境界の判定に使う範囲。</summary>
        public (long, int, int)? ForwardIn(long from, long to, long maxStart, SearchRange range)
        {
            _range = range;
            IEnumerable<(long, int)> Plan()
            {
                for (long pos = from; pos < maxStart && to - pos >= _pattern.MinMatchLength; pos += _chunkSize)
                {
                    yield return (pos, (int)Math.Min((long)_chunkSize + _overlap, to - pos));
                }
            }

            using var reader = new ChunkReader(_snapshot, Plan(), _chunkSize >= PrefetchThreshold);
            while (reader.TryNext(out Chunk chunk))
            {
                Check();
                ReadOnlySpan<byte> data = chunk.Buffer.AsSpan(0, chunk.Count);
                int cursor = 0;
                Clock?.Restart();
                try
                {
                    List<(int Start, int End)> gaps = Gaps(chunk);
                    foreach ((int gapStart, int gapEnd) in gaps)
                    {
                        if (FirstIn(data[cursor..gapStart], chunk.Offset + cursor, Context(chunk, cursor, gapStart)) is { } hit)
                        {
                            return hit.Item1 < maxStart ? hit : null;
                        }

                        Report(chunk.Offset + gapStart, gapEnd - gapStart, chunk);
                        cursor = gapEnd;
                    }

                    if (FirstIn(data[cursor..], chunk.Offset + cursor, Context(chunk, cursor, chunk.Count)) is { } last)
                    {
                        return last.Item1 < maxStart ? last : null;
                    }
                }
                catch (RegexChunkTimeoutException)
                {
                    TimedOut(chunk.Offset, Math.Min(_chunkSize, chunk.Count), null);
                }

                Advance(Math.Min(_chunkSize, chunk.Count));
            }

            return null;
        }

        /// <summary>[from, to) にすっかり収まり、開始が maxStart 未満の最後の一致。チャンクを末尾側から読む (FIND-01 の仕様 8)。</summary>
        public (long, int, int)? BackwardIn(long from, long to, long maxStart, SearchRange range)
        {
            _range = range;
            IEnumerable<(long, int)> Plan()
            {
                for (long end = to; end - from >= _pattern.MinMatchLength; end -= _chunkSize)
                {
                    long pos = Math.Max(from, end - ((long)_chunkSize + _overlap));
                    if (pos >= maxStart)
                    {
                        continue;
                    }

                    yield return (pos, (int)(end - pos));
                }
            }

            using var reader = new ChunkReader(_snapshot, Plan(), _chunkSize >= PrefetchThreshold);
            while (reader.TryNext(out Chunk chunk))
            {
                Check();
                ReadOnlySpan<byte> data = chunk.Buffer.AsSpan(0, chunk.Count);
                int limit = (int)Math.Clamp(maxStart - chunk.Offset, 0, chunk.Count);
                List<(int Start, int End)> gaps = Gaps(chunk);
                int cursor = chunk.Count;
                Clock?.Restart();
                try
                {
                    for (int g = gaps.Count - 1; g >= -1; g--)
                    {
                        int segStart = g >= 0 ? gaps[g].End : 0;
                        ReadOnlySpan<byte> segment = data[segStart..cursor];
                        int segLimit = Math.Max(0, limit - segStart);
                        if (_pattern.IsMulti)
                        {
                            // まとめて求める照合: 区間のすべての一致 (重なるものも) から、開始が上限未満の最後のものを選ぶ。
                            SearchMatch? lastFound = null;
                            _pattern.ScanMulti(segment, chunk.Offset + segStart, chunk.Offset + segStart, chunk.Offset + segStart + segLimit, true,
                                Context(chunk, segStart, cursor), m =>
                                {
                                    if (!AcceptsWord(_snapshot, _pattern, _range, m.Offset, m.Length, m.Variant))
                                    {
                                        return MatchDecision.Reject;
                                    }

                                    if (lastFound is null || m.Offset > lastFound.Value.Offset)
                                    {
                                        lastFound = m;
                                    }

                                    return MatchDecision.Accept;
                                });
                            if (lastFound is { } lf)
                            {
                                return (lf.Offset, (int)lf.Length, lf.Variant);
                            }
                        }
                        else
                        {
                            while (true)
                            {
                                int found = _pattern.LastIndexOf(segment, chunk.Offset + segStart, segLimit, out int len);
                                if (found < 0)
                                {
                                    break;
                                }

                                long at = chunk.Offset + segStart + found;
                                if (AcceptsWord(_snapshot, _pattern, _range, at, len))
                                {
                                    return (at, len, _pattern.VariantAt(segment[found..]));
                                }

                                segLimit = found;
                            }
                        }

                        if (g >= 0)
                        {
                            Report(chunk.Offset + gaps[g].Start, gaps[g].End - gaps[g].Start, chunk);
                            cursor = gaps[g].Start;
                        }
                    }
                }
                catch (RegexChunkTimeoutException)
                {
                    TimedOut(chunk.Offset, Math.Min(_chunkSize, chunk.Count), null);
                }

                Advance(Math.Min(_chunkSize, chunk.Count));
            }

            return null;
        }

        /// <summary>
        /// 範囲のすべての一致を集める。開始が <paramref name="startFrom"/> 以上の一致だけを集める (「続ける」)。上限で止めたら true。
        /// </summary>
        public bool CollectAll(SearchResults results, long startFrom = long.MinValue)
        {
            IReadOnlyList<SearchRange> ranges = _options.Scope.Resolve(_snapshot.Length);
            long skipped = ranges.Sum(r => Math.Clamp(startFrom - r.Offset, 0, r.Length));
            if (skipped > 0)
            {
                Advance(skipped);
            }

            IEnumerable<ChunkPlan> Plan()
            {
                foreach (SearchRange r in ranges)
                {
                    long first = Math.Max(r.Offset, startFrom);
                    for (long pos = first; pos < r.End; pos += _chunkSize)
                    {
                        long coreEnd = Math.Min(r.End, pos + _chunkSize);
                        yield return new ChunkPlan(pos, coreEnd, Math.Min(r.End, coreEnd + _overlap), r);
                    }
                }
            }

            int parallelism = Math.Max(1, _options.MaxDegreeOfParallelism);
            bool overlapping = _options.IncludeOverlapping;
            using IEnumerator<ChunkPlan> plan = Plan().GetEnumerator(); // 計画は少しずつ作る (チャンクの数に比例したメモリを使わない)
            var batchPlans = new ChunkPlan[parallelism];
            byte[][] buffers = new byte[parallelism][];
            var chunkResults = new ChunkResult[parallelism];
            long carriedEnd = startFrom; // 重ならない一致: 直前に採った一致の末尾
            long count = results.LongCount;
            long limit = results.Limit;
            if (count >= limit)
            {
                return true;
            }

            while (true)
            {
                Check();
                int n = 0;
                while (n < parallelism && plan.MoveNext())
                {
                    batchPlans[n++] = plan.Current;
                }

                if (n == 0)
                {
                    break;
                }

                if (n == 1)
                {
                    chunkResults[0] = CollectChunk(batchPlans[0], ref buffers[0], batchPlans[0].Offset, overlapping);
                }
                else
                {
                    try
                    {
                        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, k =>
                        {
                            chunkResults[k] = CollectChunk(batchPlans[k], ref buffers[k], batchPlans[k].Offset, overlapping);
                        });
                    }
                    catch (AggregateException ex)
                    {
                        Exception inner = ex.InnerExceptions.FirstOrDefault(e => e is OperationCanceledException) ?? ex.InnerExceptions[0];
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                    }
                }

                // 結果は、それより前のチャンクの結果をすべて追加してから追加する (FIND-20 の仕様 2)。
                for (int k = 0; k < n; k++)
                {
                    ChunkPlan p = batchPlans[k];
                    ChunkResult cr = chunkResults[k];
                    if (!overlapping && cr.Matches.Count > 0 && cr.Matches[0].Offset < carriedEnd)
                    {
                        // 前のチャンクの一致がこのチャンクにはみ出した: その末尾から探し直す。
                        cr = CollectChunk(p, ref buffers[k], carriedEnd, overlapping);
                    }

                    if (cr.TimedOut is { } timedOut)
                    {
                        // 位置の順に、正規表現の時間の上限に達したチャンクを知らせる (飛ばす・中止する)。
                        TimedOut(timedOut.Offset, timedOut.Length, results);
                    }

                    var accepted = new List<SearchMatch>(cr.Matches.Count);
                    int gapIndex = 0;
                    foreach (SearchMatch m in cr.Matches)
                    {
                        for (; gapIndex < cr.Gaps.Count && cr.Gaps[gapIndex].Offset < m.Offset; gapIndex++)
                        {
                            Flush(results, accepted);
                            ReportOrdered(results, cr.Gaps[gapIndex]);
                        }

                        if (count >= limit)
                        {
                            Flush(results, accepted);
                            return true;
                        }

                        accepted.Add(m);
                        count++;
                        carriedEnd = m.End;
                    }

                    Flush(results, accepted);
                    if (results.SpillFailed)
                    {
                        // 一時ファイルを作れなかった: メモリ上の件数で止める (FIND-20 の「エラー」)。
                        return true;
                    }

                    for (; gapIndex < cr.Gaps.Count; gapIndex++)
                    {
                        ReportOrdered(results, cr.Gaps[gapIndex]);
                    }

                    Advance(p.CoreEnd - p.Offset);
                }
            }

            return false;
        }

        private static void Flush(SearchResults results, List<SearchMatch> accepted)
        {
            if (accepted.Count > 0)
            {
                results.Add([.. accepted]);
                accepted.Clear();
            }
        }

        /// <summary>1 つのチャンクの一致 (開始が [startFrom, CoreEnd)) と、読めない範囲 (CoreEnd までに切り詰めたもの) を集める。</summary>
        private ChunkResult CollectChunk(ChunkPlan p, ref byte[] buffer, long startFrom, bool overlapping)
        {
            Check();
            int count = (int)(p.ReadEnd - p.Offset);
            if (buffer is null || buffer.Length < count)
            {
                buffer = GC.AllocateUninitializedArray<byte>(count);
            }

            ReadResult read = _snapshot.Read(p.Offset, buffer.AsSpan(0, count));
            var chunk = new Chunk(p.Offset, buffer, count, read);
            var matches = new List<SearchMatch>();
            var gaps = new List<UnreadableRange>();
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            int cursor = 0;
            long from = startFrom;
            System.Diagnostics.Stopwatch? clock = _pattern.IsRegex ? System.Diagnostics.Stopwatch.StartNew() : null;
            try
            {
                foreach ((int gapStart, int gapEnd) in Gaps(chunk))
                {
                    Collect(_pattern, data[cursor..gapStart], p.Offset + cursor, from, p.CoreEnd, overlapping, matches, null, Accept(p.Range),
                        Context(chunk, cursor, gapStart, p.Range, clock));
                    if (!overlapping && matches.Count > 0)
                    {
                        from = Math.Max(from, matches[^1].End);
                    }

                    long gs = p.Offset + gapStart;
                    long ge = Math.Min(p.CoreEnd, p.Offset + gapEnd);
                    if (ge > gs)
                    {
                        gaps.Add(ReasonAt(read, gs) with { Offset = gs, Length = ge - gs });
                    }

                    cursor = gapEnd;
                }

                Collect(_pattern, data[cursor..], p.Offset + cursor, from, p.CoreEnd, overlapping, matches, null, Accept(p.Range),
                    Context(chunk, cursor, count, p.Range, clock));
            }
            catch (RegexChunkTimeoutException)
            {
                // 正規表現の時間の上限 (FIND-18 の仕様 6): このチャンクの結果は捨て、飛ばすか中止するかを後で (位置の順に) 尋ねる。
                return new ChunkResult([], gaps, new SearchRange(p.Offset, p.CoreEnd - p.Offset));
            }

            return new ChunkResult(matches, gaps, null);
        }

        /// <summary>単語の境界の判定 (単語単位でなければ null)。</summary>
        private Func<SearchMatch, bool>? Accept(SearchRange range) =>
            !HasWords(_pattern) ? null : m => AcceptsWord(_snapshot, _pattern, range, m.Offset, m.Length, m.Variant);

        /// <summary>正規表現の時間を測る時計 (次 / 前を検索のチャンクごとに測り直す)。</summary>
        private System.Diagnostics.Stopwatch? Clock => _clock ??= _pattern.IsRegex ? new System.Diagnostics.Stopwatch() : null;

        private System.Diagnostics.Stopwatch? _clock;

        /// <summary>チャンクの中の区間 [segStart, segEnd) の照合の事情 (前後が範囲の端・読めない範囲か)。</summary>
        private ScanContext Context(Chunk chunk, int segStart, int segEnd, SearchRange? range = null, System.Diagnostics.Stopwatch? clock = null)
        {
            SearchRange r = range ?? _range;
            bool startBoundary = segStart > 0 || chunk.Offset <= r.Offset;
            bool endBoundary = segEnd < chunk.Count || chunk.Offset + chunk.Count >= r.End;
            return new ScanContext(_snapshot, r.Offset, r.End, startBoundary, endBoundary, false, clock ?? Clock);
        }

        /// <summary>
        /// 正規表現の時間の上限に達したチャンクを知らせる (FIND-18 の「エラー」)。飛ばすなら <paramref name="results"/> に記録し、中止するなら
        /// <see cref="SearchTimedOutException"/> を投げる。
        /// </summary>
        private void TimedOut(long offset, long length, SearchResults? results)
        {
            var range = new SearchRange(offset, length);
            UnreadableAction action = _options.OnTimeout?.Invoke(range) ?? UnreadableAction.Skip;
            if (action == UnreadableAction.Abort)
            {
                throw new SearchTimedOutException(range);
            }

            results?.AddTimedOut(range);
        }

        /// <summary>すべて検索の読めない範囲を、位置の順に知らせて記録する。</summary>
        private void ReportOrdered(SearchResults results, UnreadableRange range)
        {
            UnreadableAction action = _options.OnUnreadable?.Invoke(range) ?? UnreadableAction.Skip;
            if (action == UnreadableAction.Abort)
            {
                throw new SearchAbortedException(range);
            }

            results.AddSkipped(range);
        }

        /// <summary><paramref name="data"/> の中の最初の一致 (単語単位なら境界を満たすもの)。</summary>
        private (long, int, int)? FirstIn(ReadOnlySpan<byte> data, long baseOffset, in ScanContext context)
        {
            if (_pattern.IsMulti)
            {
                (long, int, int)? first = null;
                _pattern.ScanMulti(data, baseOffset, baseOffset, baseOffset + data.Length, true, context, m =>
                {
                    if (!AcceptsWord(_snapshot, _pattern, _range, m.Offset, m.Length, m.Variant))
                    {
                        return MatchDecision.Reject;
                    }

                    first = (m.Offset, (int)m.Length, m.Variant);
                    return MatchDecision.Stop;
                });
                return first;
            }

            int from = 0;
            while (from <= data.Length)
            {
                int found = _pattern.IndexOf(data[from..], baseOffset + from, out int len);
                if (found < 0)
                {
                    return null;
                }

                long at = baseOffset + from + found;
                if (AcceptsWord(_snapshot, _pattern, _range, at, len))
                {
                    return (at, len, _pattern.VariantAt(data[(from + found)..]));
                }

                from += found + 1;
            }

            return null;
        }

        /// <summary>次 / 前を検索で出会った読めない範囲を知らせる。知らせ済みの部分は除く。</summary>
        private void Report(long offset, long length, Chunk chunk)
        {
            lock (_reportLock)
            {
                foreach ((long from, long len) in Unreported(offset, offset + length))
                {
                    UnreadableRange range = ReasonAt(chunk.Result, from) with { Offset = from, Length = len };
                    MarkReported(from, from + len);
                    if ((_options.OnUnreadable?.Invoke(range) ?? UnreadableAction.Skip) == UnreadableAction.Abort)
                    {
                        throw new SearchAbortedException(range);
                    }
                }
            }
        }

        private IEnumerable<(long, long)> Unreported(long from, long to)
        {
            var result = new List<(long, long)>();
            long cursor = from;
            foreach (SearchRange r in _reported)
            {
                if (r.End <= cursor)
                {
                    continue;
                }

                if (r.Offset >= to)
                {
                    break;
                }

                if (r.Offset > cursor)
                {
                    result.Add((cursor, r.Offset - cursor));
                }

                cursor = Math.Max(cursor, r.End);
            }

            if (cursor < to)
            {
                result.Add((cursor, to - cursor));
            }

            return result;
        }

        private void MarkReported(long from, long to)
        {
            int i = 0;
            while (i < _reported.Count && _reported[i].End < from)
            {
                i++;
            }

            long start = from;
            long end = to;
            while (i < _reported.Count && _reported[i].Offset <= end)
            {
                start = Math.Min(start, _reported[i].Offset);
                end = Math.Max(end, _reported[i].End);
                _reported.RemoveAt(i);
            }

            _reported.Insert(i, new SearchRange(start, end - start));
        }

        /// <summary>位置 <paramref name="offset"/> を含む読めない範囲の理由 (なければ IoError)。</summary>
        private static UnreadableRange ReasonAt(ReadResult read, long offset)
        {
            foreach (UnreadableRange u in read.Unreadable)
            {
                if (u.Offset <= offset && offset < u.End)
                {
                    return u;
                }
            }

            return new UnreadableRange(offset, 0, UnreadableReason.IoError);
        }

        /// <summary>チャンクの中の読めない範囲 (チャンクの先頭からの位置。昇順で重ならない)。</summary>
        private static List<(int Start, int End)> Gaps(Chunk chunk)
        {
            var gaps = new List<(int Start, int End)>();
            if (chunk.Result.IsComplete)
            {
                return gaps;
            }

            foreach (UnreadableRange u in chunk.Result.Unreadable.OrderBy(u => u.Offset))
            {
                int s = (int)Math.Clamp(u.Offset - chunk.Offset, 0, chunk.Count);
                int e = (int)Math.Clamp(u.End - chunk.Offset, 0, chunk.Count);
                if (e <= s)
                {
                    continue;
                }

                if (gaps.Count > 0 && s <= gaps[^1].End)
                {
                    gaps[^1] = (gaps[^1].Start, Math.Max(gaps[^1].End, e));
                }
                else
                {
                    gaps.Add((s, e));
                }
            }

            return gaps;
        }

        /// <summary>キャンセルの確認 (ENG-09)。</summary>
        private void Check()
        {
            _token.ThrowIfCancellationRequested();
            _operation?.CancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>進捗を報告する。</summary>
        private void Advance(long bytes)
        {
            long done = Math.Min(_total, Interlocked.Add(ref _done, bytes));
            _operation?.Report(done);
        }
    }

    private readonly record struct ChunkPlan(long Offset, long CoreEnd, long ReadEnd, SearchRange Range);

    private sealed record ChunkResult(List<SearchMatch> Matches, List<UnreadableRange> Gaps, SearchRange? TimedOut);

    /// <summary>読み込んだチャンク。<see cref="Buffer"/> の先頭 <see cref="Count"/> バイトが [Offset, Offset + Count) の内容。</summary>
    private readonly record struct Chunk(long Offset, byte[] Buffer, int Count, ReadResult Result);

    /// <summary>
    /// 計画した順にチャンクを読む。<paramref name="prefetch"/> なら、渡したチャンクを照合している間に次のチャンクを別のスレッドで
    /// 読んでおく (FIND-01 の仕様 7)。バッファは 2 つを交互に使い、渡したチャンクは次の <see cref="TryNext"/> まで有効。
    /// </summary>
    private sealed class ChunkReader : IDisposable
    {
        private readonly DocumentSnapshot _snapshot;
        private readonly IEnumerator<(long Offset, int Count)> _plan;
        private readonly bool _prefetch;
        private readonly byte[]?[] _buffers = new byte[2][];
        private Task<Chunk>? _pending;
        private int _next;
        private bool _started;

        public ChunkReader(DocumentSnapshot snapshot, IEnumerable<(long, int)> plan, bool prefetch)
        {
            _snapshot = snapshot;
            _plan = plan.GetEnumerator();
            _prefetch = prefetch;
        }

        public bool TryNext(out Chunk chunk)
        {
            if (!_prefetch)
            {
                if (!_plan.MoveNext())
                {
                    chunk = default;
                    return false;
                }

                chunk = Read(_plan.Current, 0);
                return true;
            }

            if (!_started)
            {
                _started = true;
                StartNext();
            }

            if (_pending is null)
            {
                chunk = default;
                return false;
            }

            chunk = _pending.GetAwaiter().GetResult();
            _pending = null;
            StartNext();
            return true;
        }

        public void Dispose()
        {
            // 先読み中の読み込みを待つ (バッファの再利用と、例外の取りこぼしを防ぐ)。
            try
            {
                _pending?.Wait();
            }
            catch (AggregateException)
            {
            }

            _plan.Dispose();
        }

        private void StartNext()
        {
            if (!_plan.MoveNext())
            {
                return;
            }

            (long, int) item = _plan.Current;
            int index = _next;
            _next ^= 1;
            _pending = Task.Run(() => Read(item, index));
        }

        private Chunk Read((long Offset, int Count) item, int index)
        {
            byte[]? buffer = _buffers[index];
            if (buffer is null || buffer.Length < item.Count)
            {
                buffer = _buffers[index] = GC.AllocateUninitializedArray<byte>(item.Count);
            }

            ReadResult result = _snapshot.Read(item.Offset, buffer.AsSpan(0, item.Count));
            return new Chunk(item.Offset, buffer, result.BytesReturned, result);
        }
    }
}
