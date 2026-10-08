using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.I18nTool;

/// <summary>機械翻訳の提供元 (09 の UI-49 の仕様 2。提供元は未決定: DeepL API または Azure AI Translator)。</summary>
public interface IMachineTranslator
{
    /// <summary>英語の文を <paramref name="language"/> (リソースのフォルダ名) に訳す。<paramref name="glossary"/> の用語を渡す。</summary>
    Task<IReadOnlyList<string>> TranslateAsync(string language, IReadOnlyList<string> texts, Glossary glossary, CancellationToken cancellationToken);
}

/// <summary>
/// DeepL API (<c>POST /v2/translate</c>) の実装。訳さない用語とプレースホルダー (<c>{0}</c> など) は XML のタグで囲んで訳させない。
/// 用語集の訳語は、訳さない用語の保護だけを行い、訳語の強制はしない (DeepL の用語集は言語の組み合わせに制限があるため。未決定)。
/// API キーは環境変数 (GitHub の Secrets) で渡す。
/// </summary>
public sealed partial class DeepLTranslator(HttpClient http, Uri endpoint, string apiKey) : IMachineTranslator
{
    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"</?x>")]
    private static partial Regex KeepTag();

    /// <summary>リソースのフォルダ名を DeepL の言語コードに直す。</summary>
    public static string TargetCode(string language) => language switch
    {
        "zh-Hans" => "ZH-HANS",
        "zh-Hant" => "ZH-HANT",
        "pt" => "PT-BR",
        _ => language.ToUpperInvariant(),
    };

    public async Task<IReadOnlyList<string>> TranslateAsync(string language, IReadOnlyList<string> texts, Glossary glossary, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        foreach (string[] batch in texts.Chunk(50))
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("source_lang", "EN"),
                new("target_lang", TargetCode(language)),
                new("tag_handling", "xml"),
                new("ignore_tags", "x"),
            };
            form.AddRange(batch.Select(t => new KeyValuePair<string, string>("text", Protect(t, glossary))));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v2/translate")) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            JsonArray translations = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["translations"] as JsonArray
                ?? throw new FormatException("The translation API returned no translations.");
            results.AddRange(translations.Select(t => KeepTag().Replace(t?["text"]?.GetValue<string>() ?? string.Empty, string.Empty)));
        }

        return results;
    }

    /// <summary>XML の特別な文字を置き換え、訳さない部分を &lt;x&gt; で囲む。</summary>
    public static string Protect(string text, Glossary glossary)
    {
        string escaped = text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        escaped = Placeholder().Replace(escaped, m => $"<x>{m.Value}</x>");
        foreach (GlossaryTerm term in glossary.Terms.Where(t => t.DoNotTranslate).OrderByDescending(t => t.Term.Length))
        {
            escaped = Regex.Replace(escaped, $"(?<![A-Za-z0-9<]){Regex.Escape(term.Term)}(?![A-Za-z0-9])", "<x>$0</x>", RegexOptions.CultureInvariant);
        }

        return escaped;
    }
}

/// <summary>機械翻訳の実行の結果。</summary>
public sealed record TranslationRunResult(IReadOnlyDictionary<string, int> Translated, IReadOnlyDictionary<string, int> MarkedStale, IReadOnlyList<string> Warnings)
{
    public bool Changed => Translated.Values.Any(v => v > 0) || MarkedStale.Values.Any(v => v > 0);
}

/// <summary>
/// 未翻訳の文字列の機械翻訳 (09 の UI-49 の仕様 2・3)。
/// <list type="bullet">
/// <item>日本語は開発者が書くため「確認済み」にする (訳がある文字列だけ)。</item>
/// <item>残り 21 言語の、状態ファイルにない文字列 (未翻訳) と、原文が変わった「機械翻訳」の文字列を訳して .resw に書き、「機械翻訳」にする。</item>
/// <item>「確認済み」の文字列は上書きしない。原文が変わっていたら「要再確認」にする。「要再確認」も上書きしない。</item>
/// <item>英語にないキーは .resw と状態ファイルから消す。</item>
/// </list>
/// 提供元の失敗は警告にし、その言語の文字列は未翻訳のまま (次回に再試行) にする。
/// </summary>
public static class MachineTranslation
{
    public static async Task<TranslationRunResult> RunAsync(string stringsFolder, string statusPath, Glossary glossary, IMachineTranslator translator,
        IReadOnlyList<string>? onlyLanguages = null, CancellationToken cancellationToken = default)
    {
        string englishPath = Resw.PathFor(stringsFolder, "en");
        List<KeyValuePair<string, string>> englishList = Resw.Load(englishPath);
        Dictionary<string, string> english = englishList.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        TranslationStatus status = TranslationStatus.Load(statusPath);
        var translated = new Dictionary<string, int>();
        var stale = new Dictionary<string, int>();
        var warnings = new List<string>();

        // 日本語: 訳がある文字列を「確認済み」にする。
        Dictionary<string, string> japanese = Resw.LoadMap(Resw.PathFor(stringsFolder, "ja"));
        Dictionary<string, TranslationEntry> ja = status.For("ja");
        ja.Clear();
        foreach ((string key, string source) in english.Where(e => japanese.ContainsKey(e.Key)))
        {
            ja[key] = new TranslationEntry(TranslationState.Reviewed, TranslationStatus.Hash(source));
        }

        foreach (string language in onlyLanguages ?? Languages.MachineTranslated)
        {
            string path = Resw.PathFor(stringsFolder, language);
            Dictionary<string, string> values = Resw.LoadMap(path);
            Dictionary<string, TranslationEntry> entries = status.For(language);

            // 英語にないキーを消す。
            foreach (string old in values.Keys.Where(k => !english.ContainsKey(k)).ToList())
            {
                values.Remove(old);
            }

            foreach (string old in entries.Keys.Where(k => !english.ContainsKey(k)).ToList())
            {
                entries.Remove(old);
            }

            var todo = new List<string>();
            int markedStale = 0;
            foreach ((string key, string source) in englishList)
            {
                string hash = TranslationStatus.Hash(source);
                TranslationEntry? entry = entries.GetValueOrDefault(key);
                switch (entry?.State)
                {
                    case TranslationState.Reviewed when entry.SourceHash != hash:
                        entries[key] = entry with { State = TranslationState.Stale };
                        markedStale++;
                        break;
                    case TranslationState.Reviewed or TranslationState.Stale:
                        break;
                    case TranslationState.Machine when entry.SourceHash == hash && values.ContainsKey(key):
                        break;
                    default:
                        todo.Add(key);
                        break;
                }
            }

            int done = 0;
            if (todo.Count > 0)
            {
                try
                {
                    IReadOnlyList<string> results = await translator.TranslateAsync(language, [.. todo.Select(k => english[k])], glossary, cancellationToken);
                    if (results.Count != todo.Count)
                    {
                        throw new FormatException($"expected {todo.Count} translations, got {results.Count}");
                    }

                    for (int i = 0; i < todo.Count; i++)
                    {
                        values[todo[i]] = results[i];
                        entries[todo[i]] = new TranslationEntry(TranslationState.Machine, TranslationStatus.Hash(english[todo[i]]));
                    }

                    done = todo.Count;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException)
                {
                    // 提供元の失敗: 警告にして、この言語の未翻訳の文字列はそのまま (次回に再試行)。
                    warnings.Add($"{language}: machine translation failed ({ex.Message}); {todo.Count} string(s) stay untranslated.");
                    foreach (string key in todo.Where(k => entries.GetValueOrDefault(k)?.State != TranslationState.Machine || !values.ContainsKey(k)))
                    {
                        entries.Remove(key);
                    }
                }
            }

            translated[language] = done;
            stale[language] = markedStale;
            Resw.Save(path, englishList.Select(p => p.Key), values, englishPath);
        }

        status.Save(statusPath);
        return new TranslationRunResult(translated, stale, warnings);
    }
}

/// <summary>
/// 言語ごとの翻訳済み・確認済みの割合 (09 の UI-49 の仕様 4)。<c>translation-coverage.json</c> にしてアプリに同梱し、リリースノートに使う。
/// 英語は原文なので 100% / 100%。翻訳済みは訳がある文字列 (英語にあるキー) の割合、確認済みは状態が「確認済み」の割合
/// (「要再確認」は含めない)。
/// </summary>
public static class Coverage
{
    public static IReadOnlyList<(string Language, int Translated, int Reviewed)> Compute(string stringsFolder, TranslationStatus status)
    {
        Dictionary<string, string> english = Resw.LoadMap(Resw.PathFor(stringsFolder, "en"));
        var result = new List<(string, int, int)>();
        foreach ((string tag, _) in Languages.All)
        {
            if (tag == "en" || english.Count == 0)
            {
                result.Add((tag, 100, 100));
                continue;
            }

            Dictionary<string, string> values = Resw.LoadMap(Resw.PathFor(stringsFolder, tag));
            int translatedCount = english.Keys.Count(values.ContainsKey);
            int reviewedCount = english.Keys.Count(k => values.ContainsKey(k) && status.Get(tag, k)?.State == TranslationState.Reviewed);
            result.Add((tag, Percent(translatedCount, english.Count), Percent(reviewedCount, english.Count)));
        }

        return result;
    }

    /// <summary>切り捨ての百分率 (すべてそろったときだけ 100)。</summary>
    public static int Percent(int part, int total) => total == 0 ? 100 : (int)Math.Floor(part * 100.0 / total);

    public static string ToJson(IReadOnlyList<(string Language, int Translated, int Reviewed)> coverage)
    {
        var languages = new JsonObject();
        foreach ((string language, int t, int r) in coverage)
        {
            languages[language] = new JsonObject { ["translated"] = t, ["reviewed"] = r };
        }

        return new JsonObject { ["languages"] = languages }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static Dictionary<string, (int Translated, int Reviewed)> FromJson(string json)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        if (JsonNode.Parse(json)?["languages"] is JsonObject languages)
        {
            foreach ((string language, JsonNode? node) in languages)
            {
                result[language] = (node?["translated"]?.GetValue<int>() ?? 0, node?["reviewed"]?.GetValue<int>() ?? 0);
            }
        }

        return result;
    }
}

/// <summary>
/// リリースノートの翻訳の進捗の表 (10 の PKG-29 の仕様 3・4)。言語、翻訳済み、確認済み、前回からの差 (確認済みの差)。
/// 確認済みが 50% 未満の言語には「機械翻訳が中心です」と注記する。表の下に翻訳の修正を提案する方法を書く。
/// </summary>
public static class ReleaseTable
{
    public const string MachineNote = "Mostly machine translation";

    public static string Build(IReadOnlyDictionary<string, (int Translated, int Reviewed)> current, IReadOnlyDictionary<string, (int Translated, int Reviewed)>? previous,
        string repository = "https://github.com/hiro5791/HexEditor")
    {
        var lines = new List<string>
        {
            "## Translation progress",
            string.Empty,
            "| Language | Translated | Reviewed | Change |",
            "| --- | --- | --- | --- |",
        };
        foreach ((string tag, string name) in Languages.All)
        {
            (int t, int r) = current.TryGetValue(tag, out var c) ? c : (0, 0);
            string change = previous is not null && previous.TryGetValue(tag, out var p) ? Signed(r - p.Reviewed) : "new";
            string reviewed = r < 50 ? $"{r}% ({MachineNote})" : $"{r}%";
            lines.Add($"| {name} ({tag}) | {t}% | {reviewed} | {change} |");
        }

        lines.Add(string.Empty);
        lines.Add($"Found a wrong translation? Choose **Help > Report a translation error** in HexEditor, or [open a translation issue]({repository}/issues/new?template=translation.yml). " +
            $"To fix a `.resw` file with a pull request, see [CONTRIBUTING.md]({repository}/blob/main/CONTRIBUTING.md).");
        return string.Join("\n", lines) + "\n";
    }

    private static string Signed(int d) => d switch
    {
        > 0 => $"+{d}",
        < 0 => $"{d}",
        _ => "±0",
    };
}
