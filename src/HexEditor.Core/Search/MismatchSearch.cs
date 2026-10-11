using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Search;

/// <summary>
/// 一致しない箇所のすべて検索 (FIND-25 の仕様 4)。P の繰り返しで「ない」範囲を、開始の昇順に一覧にする。各範囲は最初に違うバイトから、
/// 次に P の繰り返しが「P の長さ × N」バイト以上続く位置まで (範囲の最後は違うバイト)。範囲の端・読めない範囲でも区切る。
/// 位置の条件 (FIND-17) があれば、範囲は条件を満たす位置の違うバイトから始める (範囲の途中の違うバイトは位置を問わない)。
/// 先頭から順に 1 スレッドで読み、違うバイトは SIMD で探す。メモリ使用量はチャンク 1 つ分。
/// </summary>
public static class MismatchSearch
{
    /// <summary>最小の繰り返し回数 N の既定値 (FIND-25 の仕様 4)。</summary>
    public const int DefaultMinRepeat = 16;

    /// <summary>N の設定の範囲 (1〜65,536)。</summary>
    public const int MaxMinRepeat = 65_536;

    /// <summary>設定のキー (N)。</summary>
    public const string MinRepeatKey = "search.mismatch.minRepeat";

    /// <summary>
    /// すべて検索の結果を作る。<paramref name="pattern"/> は <see cref="SearchPattern.Mismatch"/> で作ったもの。結果一覧の「続ける」
    /// (FIND-20 の仕様 6) も同じ処理で続きを探す。
    /// </summary>
    public static SearchResults CreateResults(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options, int minRepeat = DefaultMinRepeat)
    {
        if (!pattern.IsMismatch)
        {
            throw new ArgumentException("一致しない箇所の検索のパターンではありません。", nameof(pattern));
        }

        int n = Math.Clamp(minRepeat, 1, MaxMinRepeat);
        return new SearchResults(snapshot, pattern, options)
        {
            HighlightFromResults = true,
            CustomFindAll = (results, from, operation, token) => Run(results, n, from, operation, token),
        };
    }

    /// <summary>探す。件数の上限で止めたら true。</summary>
    private static bool Run(SearchResults results, int minRepeat, long startFrom, LongRunningOperation? operation, CancellationToken token)
    {
        DocumentSnapshot snapshot = results.Snapshot;
        IReadOnlyList<SearchRange> ranges = results.Options.Scope.Resolve(snapshot.Length);
        if (ranges.Count == 0)
        {
            return false;
        }

        SearchPattern pattern = results.Pattern.ForStart(ranges[0].Offset);
        MismatchMatcher matcher = pattern.MismatchSpec!;
        long run = (long)matcher.Length * minRepeat;
        bool positional = pattern.Position is { IsNone: false } || pattern.Alignment > 1;
        int chunkSize = Math.Max(4096, results.Options.ChunkSize);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(chunkSize);
        var pending = new List<SearchMatch>();
        long done = ranges.Sum(r => Math.Clamp(startFrom - r.Offset, 0, r.Length));
        operation?.Report(done);

        // 範囲を報告する。上限に達したら true。
        bool Emit(long start, long end)
        {
            if (results.LongCount + pending.Count >= results.Limit)
            {
                return true;
            }

            pending.Add(new SearchMatch(start, end - start));
            if (pending.Count >= 4096)
            {
                results.AddMatches([.. pending]);
                pending.Clear();
            }

            return false;
        }

        try
        {
            foreach (SearchRange r in ranges)
            {
                long pos = Math.Max(r.Offset, startFrom);
                bool inRange = false;
                long rangeStart = 0;
                long lastMismatch = 0;
                while (pos < r.End)
                {
                    token.ThrowIfCancellationRequested();
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(chunkSize, r.End - pos);
                    ReadResult read = snapshot.Read(pos, buffer.AsSpan(0, count));
                    count = read.BytesReturned;
                    if (count <= 0)
                    {
                        break;
                    }

                    // 読めない範囲は区切りにする (飛ばすか中止するかを尋ねる。FIND-01 の「エラー」)。
                    var segments = new List<(int Start, int End)>();
                    int cursor = 0;
                    foreach (UnreadableRange u in read.Unreadable.OrderBy(u => u.Offset))
                    {
                        int s = (int)Math.Clamp(u.Offset - pos, 0, count);
                        int e = (int)Math.Clamp(u.End - pos, 0, count);
                        if (e <= s)
                        {
                            continue;
                        }

                        if (s > cursor)
                        {
                            segments.Add((cursor, s));
                        }

                        segments.Add((-s - 1, e)); // 読めない範囲の印
                        cursor = Math.Max(cursor, e);
                    }

                    if (cursor < count)
                    {
                        segments.Add((cursor, count));
                    }

                    foreach ((int segStart, int segEnd) in segments)
                    {
                        if (segStart < 0)
                        {
                            int gapStart = -segStart - 1;
                            if (inRange && Emit(rangeStart, lastMismatch + 1))
                            {
                                return true;
                            }

                            inRange = false;
                            var gap = new UnreadableRange(pos + gapStart, segEnd - gapStart, UnreadableReason.IoError);
                            foreach (UnreadableRange u in read.Unreadable)
                            {
                                if (u.Offset <= gap.Offset && gap.Offset < u.End)
                                {
                                    gap = u with { Offset = gap.Offset, Length = gap.Length };
                                    break;
                                }
                            }

                            Flush(results, pending);
                            if ((results.Options.OnUnreadable?.Invoke(gap) ?? UnreadableAction.Skip) == UnreadableAction.Abort)
                            {
                                throw new SearchAbortedException(gap);
                            }

                            results.AddSkipped(gap);
                            continue;
                        }

                        ReadOnlySpan<byte> data = buffer.AsSpan(segStart, segEnd - segStart);
                        long baseOffset = pos + segStart;
                        int i = 0;
                        while (i < data.Length)
                        {
                            int found = matcher.IndexOf(data, i, baseOffset);
                            if (!inRange)
                            {
                                if (found < 0)
                                {
                                    break;
                                }

                                if (positional && !pattern.Accepts(baseOffset + found))
                                {
                                    // 位置の条件 (FIND-17 の仕様 3): 範囲は、条件を満たす位置の違うバイトから始める (次を検索と同じ)。
                                    i = found + 1;
                                    continue;
                                }

                                inRange = true;
                                rangeStart = lastMismatch = baseOffset + found;
                                i = found + 1;
                                continue;
                            }

                            // 直前の違うバイトの後ろに、同じバイトが続いた長さ。
                            long sameEnd = found < 0 ? baseOffset + data.Length : baseOffset + found;
                            if (sameEnd - (lastMismatch + 1) >= run)
                            {
                                if (Emit(rangeStart, lastMismatch + 1))
                                {
                                    return true;
                                }

                                inRange = false;
                                if (found < 0)
                                {
                                    break;
                                }

                                i = found; // 次の違うバイトから新しい範囲
                                continue;
                            }

                            if (found < 0)
                            {
                                break;
                            }

                            lastMismatch = baseOffset + found;
                            i = found + 1;
                        }

                        // 区間の終わりが読めない範囲の手前なら、そこで区切る (次の区間の処理で区切る)。
                    }

                    pos += count;
                    done += count;
                    operation?.Report(done);
                }

                if (inRange && Emit(rangeStart, lastMismatch + 1))
                {
                    return true;
                }
            }
        }
        finally
        {
            Flush(results, pending);
        }

        return false;
    }

    private static void Flush(SearchResults results, List<SearchMatch> pending)
    {
        if (pending.Count > 0)
        {
            results.AddMatches([.. pending]);
            pending.Clear();
        }
    }
}
