using HexEditor.Core.Engine;

namespace HexEditor.Core.Search.Yara;

/// <summary>結果一覧の 1 行の YARA の情報 (FIND-33 の仕様 8)。</summary>
public sealed record YaraRow(YaraRule Rule, string? Identifier)
{
    public string Tags => string.Join(" ", Rule.Tags);

    public string Meta => string.Join(", ", Rule.Meta.Select(m => m.Key + "=" + m.Value));

    /// <summary>文字列の一致 (なければ条件だけのルールの行で、オフセットを持たない)。</summary>
    public bool HasOffset => Identifier is not null;
}

/// <summary>
/// YARA のスキャンを、共通の結果一覧 (FIND-20) の結果として作る (FIND-33 の仕様 8)。一致した文字列ごとに 1 行、文字列の一致のないルールは
/// オフセットなしの行。行の <see cref="SearchMatch.Variant"/> は <see cref="Rows"/> の番号。
/// </summary>
public sealed class YaraSearch
{
    private readonly List<YaraRow> _rows = [];

    private YaraSearch()
    {
    }

    /// <summary>行の情報 (結果の <see cref="SearchMatch.Variant"/> の番号の順)。</summary>
    public IReadOnlyList<YaraRow> Rows => _rows;

    /// <summary>スキャンした範囲の先頭 (ドキュメント上の位置)。</summary>
    public long ScanOffset { get; private set; }

    /// <summary>スキャンの結果 (終わるまでは null)。</summary>
    public YaraScanResult? Result { get; private set; }

    /// <summary>結果を作る。<see cref="SearchEngine.FindAll(SearchResults, Operations.LongRunningOperation?, CancellationToken)"/> でスキャンする。</summary>
    /// <param name="offset">スキャンする範囲の先頭 (ドキュメント上の位置)。</param>
    /// <param name="headers">追加の列 (ルール名・名前空間・タグ・メタ情報・文字列の識別子) の見出しのリソースのキー。</param>
    public static (YaraSearch Search, SearchResults Results) Create(DocumentSnapshot snapshot, long offset, long length, YaraRuleSet rules,
        YaraScanOptions scanOptions, SearchOptions searchOptions, IReadOnlyList<string> headers, Action<SearchMatch>? jumped = null)
    {
        var search = new YaraSearch { ScanOffset = offset };
        var results = new SearchResults(snapshot, SearchPattern.Literal([0]), searchOptions with { Scope = SearchScope.Of(offset, length) })
        {
            HighlightFromResults = true,
            MatchJumped = jumped,
            IsWithoutOffset = m => !search.RowOf(m).HasOffset,
            ExtraColumns =
            [
                new SearchResultColumn(headers[0], 140, m => search.RowOf(m).Rule.Name),
                new SearchResultColumn(headers[1], 90, m => search.RowOf(m).Rule.Namespace),
                new SearchResultColumn(headers[2], 120, m => search.RowOf(m).Tags),
                new SearchResultColumn(headers[3], 200, m => search.RowOf(m).Meta),
                new SearchResultColumn(headers[4], 90, m => search.RowOf(m).Identifier ?? string.Empty),
            ],
            // 「続ける」(上限の 2 倍まで続きを探す) では、スキャンし直さない (一致はスキャンの終わりにまとめて加える)。
            CustomFindAll = (r, from, operation, token) => from == long.MinValue && search.Run(r, snapshot, offset, length, rules, scanOptions, operation, token),
        };
        return (search, results);
    }

    public YaraRow RowOf(SearchMatch match)
    {
        lock (_rows)
        {
            return _rows[match.Variant];
        }
    }

    private bool Run(SearchResults results, DocumentSnapshot snapshot, long offset, long length, YaraRuleSet rules, YaraScanOptions options,
        Operations.LongRunningOperation? operation, CancellationToken token)
    {
        YaraScanResult result = YaraScanner.Scan(rules, new YaraSnapshotData(snapshot, offset, length), options, token, done => operation?.Report(done));
        Result = result;
        var matches = new List<SearchMatch>();
        lock (_rows)
        {
            foreach (YaraRuleResult rule in result.Rules)
            {
                if (rule.Strings.Count == 0)
                {
                    _rows.Add(new YaraRow(rule.Rule, null));
                    matches.Add(new SearchMatch(offset, 0, _rows.Count - 1));
                    continue;
                }

                foreach (YaraStringResult s in rule.Strings)
                {
                    _rows.Add(new YaraRow(rule.Rule, s.Identifier));
                    matches.Add(new SearchMatch(offset + s.Offset, s.Length, _rows.Count - 1));
                }
            }
        }

        // 結果は開始位置の昇順に加える (条件だけのルールの行は範囲の先頭)。
        matches.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Variant.CompareTo(b.Variant));
        long limit = results.Limit;
        bool full = matches.Count > limit;
        results.AddMatches(full ? matches.GetRange(0, (int)Math.Min(int.MaxValue, limit)) : matches);
        operation?.ReportMatches(Math.Min(matches.Count, limit));
        if (result.Cancelled)
        {
            token.ThrowIfCancellationRequested();
        }

        return full;
    }
}
