using System.Text;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Search;

/// <summary>
/// 検索条件 (検索バーの種類・オプションをまとめたもの。FIND-04 の仕様 4)。検索バーと複数ファイル検索 (FIND-30 の仕様 1「検索バーと同じ種類・
/// オプション」) が <see cref="SearchQueryBuilder"/> で同じ作り方のパターンにする。
/// </summary>
public sealed record SearchQuery
{
    public SearchKind Kind { get; init; } = SearchKind.Hex;

    public string Text { get; init; } = string.Empty;

    /// <summary>テキスト・正規表現 (テキスト) の文字コード (表示の文字コードの一覧の名前)。</summary>
    public string EncodingId { get; init; } = "ascii";

    /// <summary>複数の文字コード (FIND-08)。空でなければ、テキストはこれらで同時に探す。</summary>
    public IReadOnlyList<string> Encodings { get; init; } = [];

    public bool CaseSensitive { get; init; }

    public bool WholeWord { get; init; }

    public bool UseEscapes { get; init; }

    public bool AlignToCharacters { get; init; }

    public int IntegerBits { get; init; } = 32;

    public IntegerSign Sign { get; init; } = IntegerSign.Either;

    public SearchEndian Endian { get; init; } = SearchEndian.Little;

    public FloatFormat FloatFormat { get; init; } = FloatFormat.Single;

    public ToleranceKind Tolerance { get; init; } = ToleranceKind.None;

    /// <summary>許容誤差の値の入力 (小数点は `.`。空なら 0)。</summary>
    public string ToleranceText { get; init; } = string.Empty;

    /// <summary>範囲の検索の「除外」(FIND-15 の仕様 5)。</summary>
    public bool RangeExclude { get; init; }

    public bool RegexIgnoreCase { get; init; }

    public bool RegexMultiline { get; init; }

    /// <summary>正規表現の `s` (null なら既定: テキストはオフ、バイト列はオン。FIND-19 の仕様 3)。</summary>
    public bool? RegexSingleline { get; init; }

    /// <summary>ビットマスクの検索 (FIND-16)。</summary>
    public bool Mask { get; init; }

    /// <summary>ビットマスクの入力のしかた: false なら値とマスク、true ならビットパターン。</summary>
    public bool MaskBits { get; init; }

    /// <summary>マスク (値とマスクのとき)。</summary>
    public string MaskText { get; init; } = string.Empty;

    /// <summary>一致しない箇所の検索 (FIND-25)。</summary>
    public bool Mismatch { get; init; }

    public bool MismatchAligned { get; init; } = true;

    /// <summary>位置の条件 (FIND-17)。null なら条件なし。</summary>
    public PositionCondition? Position { get; init; }

    /// <summary>複数の語 (FIND-26)。null でなければ、ほかの検索語の代わりにこれらをまとめて探す。</summary>
    public IReadOnlyList<SearchTerm>? Terms { get; init; }

    /// <summary>複数の語の結果の「検索語」の列の名前 (行ごと)。</summary>
    public IReadOnlyList<string>? TermLabels { get; init; }

    /// <summary>範囲の検索か (FIND-15)。</summary>
    public bool IsRange => Terms is null && Kind is SearchKind.Integer or SearchKind.Float && NumericRange.IsRange(Text);

    /// <summary>この条件で置換できるか (一致しない箇所・複数語は置換できない)。</summary>
    public bool CanReplace => Terms is null && !(Kind == SearchKind.Hex && Mismatch);
}

/// <summary>パターンを作るときの環境 (設定と文脈)。</summary>
public sealed record SearchQueryEnvironment
{
    /// <summary>「一致の最大長」(FIND-01 の仕様 3)。</summary>
    public int MaxMatchLength { get; init; } = SearchPattern.DefaultMaxMatchLength;

    /// <summary>正規表現の時間の上限 (FIND-18 の仕様 6)。</summary>
    public TimeSpan RegexTimeLimit { get; init; } = RegexSearch.DefaultTimeLimit;

    /// <summary>整数の入力式の文脈 (null なら名前を使えない)。</summary>
    public IExpressionContext? Context { get; init; }

    /// <summary>文字コードの名前から文字コードを得る。</summary>
    public Func<string, Encoding?> Encodings { get; init; } = TextEncodings.FromCatalogId;

    /// <summary>文字コードの表示名 (複数の文字コードの種類の名前)。</summary>
    public Func<string, string> EncodingName { get; init; } = id => id;
}

/// <summary>パターンを作った結果 (パターンと、複数の文字コードの補足)。</summary>
public sealed record BuiltSearchPattern(SearchPattern Pattern, IReadOnlyList<Encoding>? VariantEncodings, IReadOnlyList<string> ExcludedEncodings);

/// <summary>検索条件からパターンを作る (検索バーと複数ファイル検索で共通)。誤りは <see cref="PatternException"/>。</summary>
public static class SearchQueryBuilder
{
    public static BuiltSearchPattern Build(SearchQuery query, SearchQueryEnvironment environment)
    {
        BuiltSearchPattern built = BuildCore(query, environment);
        return query.Position is { } position ? built with { Pattern = built.Pattern.WithPosition(position) } : built;
    }

    /// <summary>位置の条件を付ける前のパターン。</summary>
    private static BuiltSearchPattern BuildCore(SearchQuery q, SearchQueryEnvironment env)
    {
        if (q.Terms is { } terms)
        {
            SearchPattern? pattern = SearchTerms.Build(terms, q.TermLabels ?? [.. terms.Select(t => t.Text)], TextOptions(q), env.Encodings,
                out IReadOnlyList<(int Row, PatternException Error)> errors, env.Context);
            return pattern is null ? throw errors.FirstOrDefault().Error ?? new PatternException(PatternError.Empty) : Plain(pattern);
        }

        Encoding encoding = env.Encodings(q.EncodingId) ?? Encoding.ASCII;
        int maxLength = Math.Clamp(env.MaxMatchLength, 1, SearchPattern.MaxMaxMatchLength);
        switch (q.Kind)
        {
            case SearchKind.RegexText:
                return Plain(RegexSearch.Text(q.Text, encoding, RegexOptions(q, env, singlelineDefault: false)));
            case SearchKind.RegexBytes:
                return Plain(RegexSearch.Bytes(q.Text, RegexOptions(q, env, singlelineDefault: true)));
            case SearchKind.Hex when q.Mismatch:
                return Plain(SearchPattern.Mismatch(q.Text, q.MismatchAligned));
            case SearchKind.Hex when q.Mask:
                return Plain(q.MaskBits ? SearchPattern.FromBitPattern(q.Text) : SearchPattern.FromValueAndMask(q.Text, q.MaskText));
            case SearchKind.Integer:
                return Plain(q.IsRange
                    ? NumericRange.Integer(q.Text, IntegerOptions(q), q.RangeExclude, env.Context)
                    : NumericSearch.Integer(q.Text, IntegerOptions(q), env.Context));
            case SearchKind.Float:
                return Plain(q.IsRange
                    ? NumericRange.Float(q.Text, new FloatSearchOptions { Format = q.FloatFormat, Endian = q.Endian }, q.RangeExclude)
                    : NumericSearch.Float(q.Text, new FloatSearchOptions
                    {
                        Format = q.FloatFormat,
                        Endian = q.Endian,
                        Tolerance = q.Tolerance,
                        ToleranceValue = ParseTolerance(q),
                    }));
            case SearchKind.Text when q.Encodings.Count > 0:
                var encodings = new List<(string Name, Encoding Encoding)>();
                foreach (string id in q.Encodings.Take(SearchPattern.MaxEncodings))
                {
                    if (env.Encodings(id) is { } e)
                    {
                        encodings.Add((env.EncodingName(id), e));
                    }
                }

                SearchPattern multi = SearchPattern.FromTextEncodings(q.Text, encodings, TextOptions(q), out IReadOnlyList<string> excluded);

                // 種類 (まとめた名前の最初の文字コード) ごとの文字コード (置換語の符号化に使う)。
                Encoding[] variants = [.. multi.Variants.Select(v => encodings.First(e => v.StartsWith(e.Name, StringComparison.Ordinal)).Encoding)];
                return new BuiltSearchPattern(multi, variants, excluded);
            case SearchKind.Text:
                return Plain(SearchPattern.FromText(q.Text, encoding, TextOptions(q)));
            default:
                return Plain(SearchPattern.FromHex(q.Text, new HexSearchOptions { MaxWildcardLength = maxLength }));
        }
    }

    /// <summary>置換語 (検索バーと同じ作り方。FIND-22)。</summary>
    public static ReplacementTemplate BuildTemplate(SearchQuery q, BuiltSearchPattern built, string replacement, SearchQueryEnvironment env)
    {
        SearchPattern pattern = built.Pattern;
        if (pattern.IsRegex)
        {
            return ReplacementTemplate.FromRegex(pattern, replacement);
        }

        if (built.VariantEncodings is { } encodings && q.Kind == SearchKind.Text)
        {
            return ReplacementTemplate.PerVariant([.. encodings.Select(e => SearchPattern.EncodeText(replacement, e, q.UseEscapes))]);
        }

        return q.Kind switch
        {
            SearchKind.Text => ReplacementTemplate.FromText(replacement, env.Encodings(q.EncodingId) ?? Encoding.ASCII, q.UseEscapes),
            SearchKind.Integer or SearchKind.Float when pattern.Numeric is { } numeric => ReplacementTemplate.FromNumeric(replacement, numeric, env.Context),
            _ => ReplacementTemplate.FromHex(replacement),
        };
    }

    private static BuiltSearchPattern Plain(SearchPattern pattern) => new(pattern, null, []);

    private static TextSearchOptions TextOptions(SearchQuery q) => new()
    {
        CaseSensitive = q.CaseSensitive,
        UseEscapes = q.UseEscapes,
        AlignToCharacters = q.AlignToCharacters,
        WholeWord = q.WholeWord,
    };

    private static IntegerSearchOptions IntegerOptions(SearchQuery q) => new() { Bits = q.IntegerBits, Sign = q.Sign, Endian = q.Endian };

    private static RegexSearchOptions RegexOptions(SearchQuery q, SearchQueryEnvironment env, bool singlelineDefault) => new()
    {
        IgnoreCase = q.RegexIgnoreCase,
        Multiline = q.RegexMultiline,
        Singleline = q.RegexSingleline ?? singlelineDefault,
        MaxMatchLength = Math.Clamp(env.MaxMatchLength, 1, SearchPattern.MaxMaxMatchLength),
        TimeLimit = env.RegexTimeLimit < RegexSearch.MinTimeLimit ? RegexSearch.MinTimeLimit
            : env.RegexTimeLimit > RegexSearch.MaxTimeLimit ? RegexSearch.MaxTimeLimit : env.RegexTimeLimit,
        AlignToCharacters = q.AlignToCharacters && q.Kind == SearchKind.RegexText,
    };

    /// <summary>許容誤差の入力 (小数点は `.`。空なら 0)。読めない値は誤りにする。</summary>
    private static double ParseTolerance(SearchQuery q)
    {
        if (q.Tolerance == ToleranceKind.None)
        {
            return 0;
        }

        string t = q.ToleranceText.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        if (!double.TryParse(t, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent
            | System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            throw new PatternException(PatternError.InvalidTolerance, t);
        }

        return v;
    }
}
