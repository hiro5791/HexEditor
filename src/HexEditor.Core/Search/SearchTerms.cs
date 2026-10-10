using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Search;

/// <summary>複数語の検索 (FIND-26) の 1 行: 種類、検索語、文字コード・サイズなどのオプション、有効 / 無効。</summary>
public sealed record SearchTerm
{
    /// <summary>種類 (Hex / テキスト / 整数 / 浮動小数点。正規表現は入れられない。FIND-26 の仕様 3)。</summary>
    public SearchKind Kind { get; init; } = SearchKind.Hex;

    public string Text { get; init; } = string.Empty;

    /// <summary>テキストの文字コード (表示の文字コードの一覧の名前。<c>ascii</c>、<c>utf-16le</c> など)。</summary>
    public string Encoding { get; init; } = "ascii";

    public int IntegerBits { get; init; } = 32;

    public IntegerSign Sign { get; init; } = IntegerSign.Either;

    public SearchEndian Endian { get; init; } = SearchEndian.Little;

    public FloatFormat FloatFormat { get; init; } = FloatFormat.Single;

    /// <summary>有効 (無効にした行は検索しない。不正でも検索を妨げない)。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>複数語の検索の語の一覧 (FIND-26)。テキストファイルからの読み込み (仕様 2)、JSON での保存・読み込み (仕様 8)、パターンの作成。</summary>
public static class SearchTerms
{
    /// <summary>
    /// テキストファイルの内容 (UTF-8、1 行に 1 語) から語を作る (FIND-26 の仕様 2)。空の行は飛ばす。種類などは <paramref name="template"/> のもの。
    /// 上限 (10,000 語) を超えたら <see cref="PatternError.TooManyTerms"/>。
    /// </summary>
    public static IReadOnlyList<SearchTerm> FromLines(string text, SearchTerm template)
    {
        var terms = new List<SearchTerm>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (terms.Count >= SearchPattern.MaxTerms)
            {
                throw new PatternException(PatternError.TooManyTerms, string.Empty, null,
                    [SearchPattern.MaxTerms.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)]);
            }

            terms.Add(template with { Text = line });
        }

        return terms;
    }

    /// <summary>テキストファイル (UTF-8。BOM は無視) から読み込む。</summary>
    public static IReadOnlyList<SearchTerm> FromFile(string path, SearchTerm template)
    {
        string text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return FromLines(text.TrimStart('﻿'), template);
    }

    /// <summary>語の一覧を JSON にする (名前を付けて保存。FIND-26 の仕様 8)。</summary>
    public static string ToJson(IReadOnlyList<SearchTerm> terms)
    {
        var array = new JsonArray();
        foreach (SearchTerm t in terms)
        {
            array.Add(new JsonObject
            {
                ["kind"] = t.Kind.ToString(),
                ["text"] = t.Text,
                ["encoding"] = t.Encoding,
                ["bits"] = t.IntegerBits,
                ["sign"] = t.Sign.ToString(),
                ["endian"] = t.Endian.ToString(),
                ["float"] = t.FloatFormat.ToString(),
                ["enabled"] = t.Enabled,
            });
        }

        return new JsonObject { ["terms"] = array }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>JSON から読み込む。形式が違えば <see cref="JsonException"/>。</summary>
    public static IReadOnlyList<SearchTerm> FromJson(string json)
    {
        JsonNode? root = JsonNode.Parse(json);
        if (root?["terms"] is not JsonArray array)
        {
            throw new JsonException("terms がありません。");
        }

        var terms = new List<SearchTerm>();
        foreach (JsonObject o in array.OfType<JsonObject>())
        {
            terms.Add(new SearchTerm
            {
                Kind = Enum<SearchKind>(o["kind"], SearchKind.Hex) is var k && k is SearchKind.Hex or SearchKind.Text or SearchKind.Integer or SearchKind.Float ? k : SearchKind.Hex,
                Text = o["text"]?.GetValue<string>() ?? string.Empty,
                Encoding = o["encoding"]?.GetValue<string>() ?? "ascii",
                IntegerBits = o["bits"] is JsonValue b && b.TryGetValue(out int bits) && NumericSearch.IntegerSizes.Contains(bits) ? bits : 32,
                Sign = Enum<IntegerSign>(o["sign"], IntegerSign.Either),
                Endian = Enum<SearchEndian>(o["endian"], SearchEndian.Little),
                FloatFormat = Enum<FloatFormat>(o["float"], FloatFormat.Single),
                Enabled = o["enabled"] is not JsonValue e || !e.TryGetValue(out bool enabled) || enabled,
            });
        }

        if (terms.Count > SearchPattern.MaxTerms)
        {
            throw new PatternException(PatternError.TooManyTerms);
        }

        return terms;
    }

    /// <summary>1 つの語のパターン (大文字・小文字の区別、単語単位は全体で共通。FIND-26 の仕様 3)。</summary>
    public static SearchPattern Pattern(SearchTerm term, TextSearchOptions common, Func<string, Encoding?> encodings, IExpressionContext? context = null) =>
        term.Kind switch
        {
            SearchKind.Text => SearchPattern.FromText(term.Text, encodings(term.Encoding) ?? System.Text.Encoding.ASCII, common),
            SearchKind.Integer => NumericSearch.Integer(term.Text, new IntegerSearchOptions { Bits = term.IntegerBits, Sign = term.Sign, Endian = term.Endian }, context),
            SearchKind.Float => NumericSearch.Float(term.Text, new FloatSearchOptions { Format = term.FloatFormat, Endian = term.Endian }),
            _ => SearchPattern.FromHex(term.Text),
        };

    /// <summary>
    /// 有効な語をまとめたパターンを作る (FIND-26)。不正な語は <paramref name="errors"/> に行の番号 (0 から) と誤りを返し、パターンは null
    /// (不正な語がある間は検索できない。「エラー」)。<paramref name="labels"/> は結果の「検索語」の列に出す名前 (行ごと)。
    /// </summary>
    public static SearchPattern? Build(IReadOnlyList<SearchTerm> terms, IReadOnlyList<string> labels, TextSearchOptions common,
        Func<string, Encoding?> encodings, out IReadOnlyList<(int Row, PatternException Error)> errors, IExpressionContext? context = null)
    {
        var parts = new List<SearchPattern>();
        var names = new List<string>();
        var problems = new List<(int, PatternException)>();
        for (int i = 0; i < terms.Count; i++)
        {
            SearchTerm term = terms[i];
            if (!term.Enabled)
            {
                continue;
            }

            try
            {
                parts.Add(Pattern(term, common, encodings, context));
                names.Add(i < labels.Count ? labels[i] : term.Text);
            }
            catch (PatternException ex)
            {
                problems.Add((i, ex));
            }
        }

        errors = problems;
        if (problems.Count > 0)
        {
            return null;
        }

        if (parts.Count == 0)
        {
            errors = [(-1, new PatternException(PatternError.Empty))];
            return null;
        }

        return SearchPattern.Union(parts, names, VariantColumn.Term);
    }

    private static T Enum<T>(JsonNode? node, T fallback)
        where T : struct, System.Enum =>
        node is JsonValue v && v.TryGetValue(out string? s) && System.Enum.TryParse(s, out T value) ? value : fallback;
}
