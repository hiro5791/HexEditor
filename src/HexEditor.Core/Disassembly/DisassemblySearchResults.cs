using HexEditor.Core.Engine;
using HexEditor.Core.Search;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// 相互参照 (ANA-25 の仕様 7) と命令パターン検索 (ANA-28 の仕様 6) の結果を、共通の結果一覧 (04 の FIND-20) に出す結果にする。
/// 一覧の「命令」の列は <paramref name="describe"/> の文字列。見つかった順に加え、キャンセルしたら見つかった分を残す。
/// </summary>
public static class DisassemblySearchResults
{
    public static SearchResults References(DisassemblyListing listing, DocumentSnapshot snapshot, ulong target, IReadOnlyList<(long Offset, long Length)> ranges,
        List<CrossReference> found, Func<CrossReference, string> describe, Func<IReadOnlyList<CrossReference>?>? cached = null,
        Action<IReadOnlyList<CrossReference>>? completed = null)
    {
        var names = new List<string>();
        return new SearchResults(snapshot, SearchPattern.Literal([0]), new SearchOptions { MaxMatches = 1_000_000 })
        {
            VariantNames = names,
            VariantColumnOverride = VariantColumn.Instruction,
            CustomFindAll = (results, _, operation, token) =>
            {
                IReadOnlyList<CrossReference>? known = cached?.Invoke();
                var refs = new List<CrossReference>();
                bool cancelled = false;
                if (known is not null)
                {
                    refs.AddRange(known);
                }
                else
                {
                    try
                    {
                        foreach (CrossReference x in DisassemblySearch.FindReferences(listing, snapshot, target, ranges, token, p => operation?.Report(p)))
                        {
                            refs.Add(x);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }
                }

                lock (names)
                {
                    found.AddRange(refs);
                    names.AddRange(refs.Select(describe));
                }

                results.AddMatches([.. refs.Select((x, i) => new SearchMatch(x.Offset, x.Length, i))]);
                if (!cancelled && known is null)
                {
                    completed?.Invoke(refs);
                }

                return false;
            },
        };
    }

    public static SearchResults Pattern(DisassemblyListing listing, DocumentSnapshot snapshot, InstructionPattern pattern, bool regex,
        IReadOnlyList<(long Offset, long Length)> ranges, List<PatternMatch> found, Func<PatternMatch, string> describe, Action<PatternMatch>? jumped = null)
    {
        var names = new List<string>();
        return new SearchResults(snapshot, SearchPattern.Literal([0]), new SearchOptions { MaxMatches = 1_000_000 })
        {
            VariantNames = names,
            VariantColumnOverride = VariantColumn.Instruction,
            CustomFindAll = (results, _, operation, token) =>
            {
                try
                {
                    foreach (PatternMatch m in DisassemblySearch.FindPattern(listing, snapshot, pattern, regex, ranges, token, p => operation?.Report(p)))
                    {
                        int index;
                        lock (names)
                        {
                            index = names.Count;
                            found.Add(m);
                            names.Add(describe(m));
                        }

                        results.AddMatches([new SearchMatch(m.Offset, m.Length, index)]);
                    }
                }
                catch (OperationCanceledException)
                {
                    // キャンセルしたら見つかった分を残す (ANA-28 の「巨大ファイル」)。
                }

                return false;
            },
            MatchJumped = m =>
            {
                PatternMatch? match;
                lock (names)
                {
                    match = m.Variant >= 0 && m.Variant < found.Count ? found[m.Variant] : null;
                }

                if (match is not null)
                {
                    jumped?.Invoke(match);
                }
            },
        };
    }
}
