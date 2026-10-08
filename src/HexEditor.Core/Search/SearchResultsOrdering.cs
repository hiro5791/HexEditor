namespace HexEditor.Core.Search;

/// <summary>結果一覧の並べ替えの列 (00-overview 9 章の「結果一覧」、FIND-20 の仕様 9)。</summary>
public enum SearchResultSortKey
{
    /// <summary>見つかった順 (開始オフセットの順。既定)。</summary>
    Number,
    Offset,
    Length,
    Hex,
    Text,
    Status,
}

/// <summary>
/// 結果一覧の並べ替えと絞り込み (00-overview 9 章の「結果一覧」、FIND-20 の仕様 9)。一覧の行の番号 (表示の順) から結果の番号
/// (見つかった順。複数のドキュメントのときはドキュメントの順につないだ番号) への対応を作る。データの列で並べ替える・絞り込むときは、
/// 結果ごとに行の内容を作るため時間がかかる (バックグラウンドで呼ぶ)。
/// </summary>
public static class SearchResultsOrdering
{
    /// <summary>並べ替え・絞り込みができる結果の数の上限 (対応表をメモリに置くため)。</summary>
    public const long MaxResults = 10_000_000;

    /// <summary>
    /// 並べ替え・絞り込みをした順の結果の番号を返す。<paramref name="filter"/> は Hex の列 (空白を無視) とテキストの列に含まれる語
    /// (大文字・小文字を区別しない)。同じ値どうしは見つかった順にする (安定)。上限を超える場合は null。
    /// </summary>
    public static long[]? Build(IReadOnlyList<SearchResultRowFactory> groups, SearchResultSortKey key, bool descending, string? filter,
        CancellationToken cancellationToken = default)
    {
        long total = groups.Sum(g => g.Results.LongCount);
        if (total > MaxResults)
        {
            return null;
        }

        string query = (filter ?? string.Empty).Trim();
        string hexQuery = new([.. query.Where(c => !char.IsWhiteSpace(c))]);
        bool needsRow = query.Length > 0 || key is SearchResultSortKey.Hex or SearchResultSortKey.Text;
        bool needsTrack = key is SearchResultSortKey.Offset or SearchResultSortKey.Status;
        var indices = new List<long>((int)Math.Min(total, int.MaxValue));
        var keys = new List<IComparable>();
        long start = 0;
        foreach (SearchResultRowFactory factory in groups)
        {
            long count = factory.Results.LongCount;
            for (long local = 0; local < count; local++)
            {
                if ((local & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                SearchResultRow? row = needsRow ? factory.Row(local) : null;
                TrackedMatch tracked = row is not null ? new TrackedMatch(row.Offset, row.Length, row.Status)
                    : needsTrack ? factory.Track(factory.Results[local]) : default;
                if (query.Length > 0 && !Matches(row!, query, hexQuery))
                {
                    continue;
                }

                indices.Add(start + local);
                keys.Add(key switch
                {
                    SearchResultSortKey.Offset => tracked.Offset,
                    SearchResultSortKey.Length => row?.Length ?? factory.Results[local].Length,
                    SearchResultSortKey.Hex => row!.Hex,
                    SearchResultSortKey.Text => row!.Text,
                    SearchResultSortKey.Status => (int)tracked.Status,
                    _ => start + local,
                });
            }

            start += count;
        }

        long[] order = [.. indices];
        if (key != SearchResultSortKey.Number)
        {
            IComparable[] sortKeys = [.. keys];
            int[] positions = [.. Enumerable.Range(0, order.Length)];
            Comparison<int> compare = (a, b) =>
            {
                int c = sortKeys[a] is string sa && sortKeys[b] is string sb
                    ? StringComparer.OrdinalIgnoreCase.Compare(sa, sb)
                    : sortKeys[a].CompareTo(sortKeys[b]);
                return c != 0 ? c : order[a].CompareTo(order[b]);
            };
            Array.Sort(positions, compare);
            long[] source = order;
            order = [.. positions.Select(p => source[p])];
        }

        if (descending)
        {
            Array.Reverse(order);
        }

        return order;
    }

    private static bool Matches(SearchResultRow row, string query, string hexQuery) =>
        row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (hexQuery.Length > 0 && row.Hex.Replace(" ", string.Empty, StringComparison.Ordinal).Contains(hexQuery, StringComparison.OrdinalIgnoreCase));
}
