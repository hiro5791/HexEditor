using System.Text.RegularExpressions;

namespace HexEditor.I18nTool;

/// <summary>用語集の 1 行 (09 の UI-48 の仕様 2)。</summary>
public sealed record GlossaryTerm(string Term, string Description, string PartOfSpeech, bool DoNotTranslate, IReadOnlyDictionary<string, string> Translations);

/// <summary>
/// 用語集 <c>docs/i18n/glossary.csv</c> (UTF-8、BOM なし、カンマ区切り。09 の UI-48)。列は <c>term</c>、<c>description</c>、
/// <c>part_of_speech</c>、<c>do_not_translate</c> と、22 言語 (英語以外) の訳語。
/// </summary>
public sealed class Glossary
{
    public static readonly string[] FixedColumns = ["term", "description", "part_of_speech", "do_not_translate"];

    public Glossary(IReadOnlyList<string> header, IReadOnlyList<GlossaryTerm> terms)
    {
        Header = header;
        Terms = terms;
    }

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<GlossaryTerm> Terms { get; }

    /// <summary>訳語の列 (22 言語。日本語を先頭に、その後は 00-overview 5.1 の順。UI-48 の仕様 2)。</summary>
    public static IReadOnlyList<string> LanguageColumns => ["ja", .. Languages.All.Select(l => l.Tag).Where(t => t is not ("en" or "ja"))];

    public static Glossary Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new FormatException($"{path} has a BOM (UTF-8 without BOM is required).");
        }

        List<string[]> rows = Csv.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        if (rows.Count == 0)
        {
            throw new FormatException($"{path} is empty.");
        }

        string[] header = rows[0];
        var terms = new List<GlossaryTerm>();
        foreach (string[] row in rows.Skip(1))
        {
            string Cell(string column) => Array.IndexOf(header, column) is var i and >= 0 && i < row.Length ? row[i].Trim() : string.Empty;
            var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string language in LanguageColumns)
            {
                translations[language] = Cell(language);
            }

            terms.Add(new GlossaryTerm(Cell("term"), Cell("description"), Cell("part_of_speech"),
                Cell("do_not_translate").Equals("true", StringComparison.OrdinalIgnoreCase), translations));
        }

        return new Glossary(header, terms);
    }

    /// <summary>形式の誤り (見出しの列、空の訳語)。</summary>
    public IEnumerable<string> FormatErrors()
    {
        string[] expected = [.. FixedColumns, .. LanguageColumns];
        if (!Header.SequenceEqual(expected))
        {
            yield return $"glossary.csv: the header must be {string.Join(",", expected)}.";
        }

        foreach (GlossaryTerm term in Terms)
        {
            if (term.Term.Length == 0)
            {
                yield return "glossary.csv: a row has no term.";
            }

            if (!term.DoNotTranslate)
            {
                foreach ((string language, string value) in term.Translations.Where(t => t.Value.Length == 0))
                {
                    yield return $"glossary.csv: '{term.Term}' has no {language} translation.";
                }
            }
        }
    }

    /// <summary>単語として含むか (英数字の途中では一致させない。例: Hex は HexEditor に一致しない)。</summary>
    public static bool ContainsWord(string text, string term) =>
        Regex.IsMatch(text, $"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])", RegexOptions.CultureInvariant);

    /// <summary>
    /// 訳さない用語の検査 (UI-48 の仕様 5): 英語の原文に <c>do_not_translate</c> の用語があるのに、訳文にその用語が原文のまま
    /// 残っていないものを返す (警告にする)。
    /// </summary>
    public IEnumerable<(string Language, string Key, string Term)> DoNotTranslateViolations(
        IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> translations)
    {
        foreach (GlossaryTerm term in Terms.Where(t => t.DoNotTranslate))
        {
            foreach ((string key, string source) in english.Where(e => ContainsWord(e.Value, term.Term)))
            {
                foreach ((string language, IReadOnlyDictionary<string, string> values) in translations.OrderBy(t => t.Key, StringComparer.Ordinal))
                {
                    if (values.TryGetValue(key, out string? value) && !ContainsWord(value, term.Term))
                    {
                        yield return (language, key, term.Term);
                    }
                }
            }
        }
    }
}
