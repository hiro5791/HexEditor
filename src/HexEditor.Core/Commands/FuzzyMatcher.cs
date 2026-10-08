using System.Globalization;
using System.Text;

namespace HexEditor.Core.Commands;

/// <summary>
/// 検索用に正規化した文字列。大文字・小文字、全角・半角、アクセント記号、カタカナ・ひらがなの違いをなくす
/// (Invariant で比較し、地域設定で結果が変わらない。00-overview.md 5.4、UI-17 の仕様 4)。
/// 正規化した文字ごとに元の文字の位置を持ち、一致した文字を元の文字列で強調できる。
/// </summary>
public sealed class SearchText
{
    public SearchText(string original)
    {
        Original = original;
        var text = new StringBuilder(original.Length);
        var map = new List<int>(original.Length);
        for (int i = 0; i < original.Length; i++)
        {
            string unit = char.IsHighSurrogate(original[i]) && i + 1 < original.Length
                ? original.Substring(i, 2)
                : original[i].ToString();
            foreach (char c in Normalize(unit))
            {
                text.Append(c);
                map.Add(i);
            }

            i += unit.Length - 1;
        }

        Normalized = text.ToString();
        Map = map;
    }

    public string Original { get; }

    public string Normalized { get; }

    /// <summary>正規化した文字の位置 → 元の文字列の位置。</summary>
    public IReadOnlyList<int> Map { get; }

    /// <summary>入力の正規化 (検索語にも同じ規則を使う)。</summary>
    public static string Normalize(string text)
    {
        string compat = text.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(compat.Length);
        foreach (char c in compat)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // カタカナはひらがなとして比べる (読みの別名をどちらで入力しても一致させる)。
            char ch = c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;
            result.Append(char.ToLowerInvariant(ch));
        }

        return result.ToString();
    }
}

/// <summary>あいまい一致の種類。値が大きいほど上に並べる (UI-17 の仕様 3)。</summary>
public enum MatchKind
{
    None = 0,

    /// <summary>飛び飛びの一致 (入力の文字が順に含まれる)。</summary>
    Scattered = 1,

    /// <summary>連続した一致 (部分文字列)。</summary>
    Contiguous = 2,

    /// <summary>単語の先頭からの一致。</summary>
    WordStart = 3,

    /// <summary>先頭からの一致。</summary>
    Prefix = 4,
}

/// <summary>一致の結果。<see cref="Positions"/> は元の文字列で一致した文字の位置。</summary>
public readonly record struct FuzzyMatch(MatchKind Kind, int Score, IReadOnlyList<int> Positions)
{
    public static readonly FuzzyMatch None = new(MatchKind.None, 0, []);

    public bool Success => Kind != MatchKind.None;
}

/// <summary>あいまい一致 (UI-17 の仕様 3): 先頭一致 &gt; 単語の先頭一致 &gt; 連続一致 &gt; 飛び飛びの一致。</summary>
public static class FuzzyMatcher
{
    /// <summary><paramref name="query"/> は <see cref="SearchText.Normalize"/> したもの。</summary>
    public static FuzzyMatch Match(SearchText text, string query)
    {
        if (query.Length == 0)
        {
            return new FuzzyMatch(MatchKind.Prefix, 0, []);
        }

        string s = text.Normalized;
        int index = s.IndexOf(query, StringComparison.Ordinal);
        if (index >= 0)
        {
            // 単語の先頭から一致する箇所があればそれを選ぶ。
            int wordStart = index;
            while (wordStart >= 0 && !IsWordStart(s, wordStart))
            {
                wordStart = wordStart + 1 < s.Length ? s.IndexOf(query, wordStart + 1, StringComparison.Ordinal) : -1;
            }

            int at = wordStart >= 0 ? wordStart : index;
            MatchKind kind = at == 0 ? MatchKind.Prefix : wordStart >= 0 ? MatchKind.WordStart : MatchKind.Contiguous;
            return new FuzzyMatch(kind, Score(kind, at, s.Length), Positions(text, Enumerable.Range(at, query.Length)));
        }

        // 飛び飛び: 単語の先頭を優先して前から順に拾う。
        var picked = new List<int>(query.Length);
        int from = 0;
        foreach (char q in query)
        {
            int found = -1;
            for (int i = from; i < s.Length; i++)
            {
                if (s[i] == q)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return FuzzyMatch.None;
            }

            picked.Add(found);
            from = found + 1;
        }

        int gaps = picked[^1] - picked[0] - (picked.Count - 1);
        return new FuzzyMatch(MatchKind.Scattered, Score(MatchKind.Scattered, picked[0], s.Length) - gaps, Positions(text, picked));
    }

    private static int Score(MatchKind kind, int at, int length) => ((int)kind * 100_000) - (at * 100) - Math.Min(length, 99);

    private static bool IsWordStart(string s, int i) => i == 0 || !char.IsLetterOrDigit(s[i - 1]) || (char.IsLower(s[i - 1]) && char.IsUpper(s[i]));

    private static IReadOnlyList<int> Positions(SearchText text, IEnumerable<int> normalized) =>
        normalized.Select(i => text.Map[i]).Distinct().ToList();
}
