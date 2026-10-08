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
/// 用語集 (UI-48 の仕様 4) の訳語は、言語ごとに DeepL の用語集 (<c>POST /v2/glossaries</c>) を作って <c>glossary_id</c> で渡し、
/// 訳し終えたら消す。DeepL が用語集に対応していない言語の組み合わせ (<c>GET /v2/glossary-language-pairs</c> にない言語) と、
/// 用語集を付けた要求を DeepL が受け付けない場合 (400) は、用語集なしで訳す (訳さない用語の保護は続ける)。
/// API キーは環境変数 (GitHub の Secrets) で渡す。
/// </summary>
public sealed partial class DeepLTranslator(HttpClient http, Uri endpoint, string apiKey) : IMachineTranslator
{
    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"</?x>")]
    private static partial Regex KeepTag();

    /// <summary>用語集に対応している訳先の言語 (DeepL の用語集の言語コード)。最初に使うときに 1 回だけ問い合わせる。</summary>
    private HashSet<string>? _glossaryTargets;

    /// <summary>用語集を使わなかった言語と理由 (ログ用)。</summary>
    public List<string> Notes { get; } = [];

    /// <summary>リソースのフォルダ名を DeepL の言語コードに直す。</summary>
    public static string TargetCode(string language) => language switch
    {
        "zh-Hans" => "ZH-HANS",
        "zh-Hant" => "ZH-HANT",
        "pt" => "PT-BR",
        _ => language.ToUpperInvariant(),
    };

    /// <summary>リソースのフォルダ名を DeepL の用語集の言語コード (小文字、地域なし) に直す。</summary>
    public static string GlossaryCode(string language) => language switch
    {
        "zh-Hans" or "zh-Hant" => "zh",
        _ => language.ToLowerInvariant(),
    };

    /// <summary>
    /// 用語集の項目 (英語 → 訳語)。訳さない用語は &lt;x&gt; で保護するので入れない。訳語のない用語、タブ・改行を含む用語は除く。
    /// 小文字で始まる用語は、文頭の形 (先頭を大文字にした英語 → 先頭を大文字にした訳語) も入れる。
    /// </summary>
    public static IReadOnlyList<(string Source, string Target)> GlossaryEntries(Glossary glossary, string language)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (GlossaryTerm term in glossary.Terms.Where(t => !t.DoNotTranslate))
        {
            if (!term.Translations.TryGetValue(language, out string? target) || target.Length == 0 || term.Term.Length == 0
                || $"{term.Term}{target}".AsSpan().IndexOfAny("\t\r\n") >= 0)
            {
                continue;
            }

            entries.TryAdd(term.Term, target);
            if (char.IsLower(term.Term[0]))
            {
                entries.TryAdd(char.ToUpperInvariant(term.Term[0]) + term.Term[1..], char.ToUpperInvariant(target[0]) + target[1..]);
            }
        }

        return [.. entries.Select(e => (e.Key, e.Value))];
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(string language, IReadOnlyList<string> texts, Glossary glossary, CancellationToken cancellationToken)
    {
        string? glossaryId = await CreateGlossaryAsync(language, glossary, cancellationToken);
        try
        {
            var results = new List<string>();
            foreach (string[] batch in texts.Chunk(50))
            {
                using HttpResponseMessage response = await SendTranslateAsync(language, batch, glossary, glossaryId, cancellationToken);
                if (glossaryId is not null && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // 用語集を付けた要求を受け付けない (訳先の言語の変種など): 用語集なしで訳し直す。
                    Notes.Add($"{language}: the glossary was rejected by the translation API; translated without it.");
                    await DeleteGlossaryAsync(glossaryId, cancellationToken);
                    glossaryId = null;
                    using HttpResponseMessage retry = await SendTranslateAsync(language, batch, glossary, null, cancellationToken);
                    results.AddRange(await ReadTranslationsAsync(retry, cancellationToken));
                    continue;
                }

                results.AddRange(await ReadTranslationsAsync(response, cancellationToken));
            }

            return results;
        }
        finally
        {
            if (glossaryId is not null)
            {
                await DeleteGlossaryAsync(glossaryId, CancellationToken.None);
            }
        }
    }

    private async Task<HttpResponseMessage> SendTranslateAsync(string language, string[] batch, Glossary glossary, string? glossaryId, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("source_lang", "EN"),
            new("target_lang", TargetCode(language)),
            new("tag_handling", "xml"),
            new("ignore_tags", "x"),
        };
        if (glossaryId is not null)
        {
            form.Add(new("glossary_id", glossaryId));
        }

        form.AddRange(batch.Select(t => new KeyValuePair<string, string>("text", Protect(t, glossary))));
        return await SendAsync(HttpMethod.Post, "v2/translate", new FormUrlEncodedContent(form), cancellationToken);
    }

    private static async Task<IEnumerable<string>> ReadTranslationsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        JsonArray translations = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["translations"] as JsonArray
            ?? throw new FormatException("The translation API returned no translations.");
        return [.. translations.Select(t => KeepTag().Replace(t?["text"]?.GetValue<string>() ?? string.Empty, string.Empty))];
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// この言語の用語集を DeepL に作り、ID を返す。項目がない、または DeepL がこの言語の用語集に対応していなければ null。
    /// 作れなかった (通信の失敗・API の誤り) 場合は例外 (その言語は未翻訳のまま次回に再試行する)。
    /// </summary>
    private async Task<string?> CreateGlossaryAsync(string language, Glossary glossary, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Source, string Target)> entries = GlossaryEntries(glossary, language);
        if (entries.Count == 0)
        {
            return null;
        }

        if (_glossaryTargets is null)
        {
            using HttpResponseMessage pairs = await SendAsync(HttpMethod.Get, "v2/glossary-language-pairs", null, cancellationToken);
            pairs.EnsureSuccessStatusCode();
            JsonArray list = JsonNode.Parse(await pairs.Content.ReadAsStringAsync(cancellationToken))?["supported_languages"] as JsonArray
                ?? throw new FormatException("The translation API returned no glossary language pairs.");
            _glossaryTargets = [.. list
                .Where(p => string.Equals(p?["source_lang"]?.GetValue<string>(), "en", StringComparison.OrdinalIgnoreCase))
                .Select(p => p?["target_lang"]?.GetValue<string>()?.ToLowerInvariant() ?? string.Empty)];
        }

        string code = GlossaryCode(language);
        if (!_glossaryTargets.Contains(code))
        {
            Notes.Add($"{language}: the translation API has no glossaries for en → {code}; translated without the glossary.");
            return null;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("name", $"HexEditor {language}"),
            new("source_lang", "en"),
            new("target_lang", code),
            new("entries", string.Join("\n", entries.Select(e => e.Source + "\t" + e.Target))),
            new("entries_format", "tsv"),
        };
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "v2/glossaries", new FormUrlEncodedContent(form), cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["glossary_id"]?.GetValue<string>()
            ?? throw new FormatException("The translation API returned no glossary_id.");
    }

    /// <summary>訳し終えた用語集を消す (失敗しても訳の結果には影響しないので無視する)。</summary>
    private async Task DeleteGlossaryAsync(string glossaryId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, "v2/glossaries/" + Uri.EscapeDataString(glossaryId), null, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Notes.Add($"glossary {glossaryId} could not be deleted ({(int)response.StatusCode}).");
            }
        }
        catch (HttpRequestException ex)
        {
            Notes.Add($"glossary {glossaryId} could not be deleted ({ex.Message}).");
        }
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
        SyncJapanese(status, english, Resw.LoadMap(Resw.PathFor(stringsFolder, "ja")));

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

    /// <summary>
    /// 日本語の状態を .resw に合わせる (UI-49 の仕様 2 の 1。日本語は開発者が書くので「確認済み」)。機械翻訳の API キーがなくても
    /// 実行できる (I18nTool の sync-ja)。状態が変わったら true。
    /// </summary>
    public static bool SyncJapanese(string stringsFolder, string statusPath)
    {
        TranslationStatus status = TranslationStatus.Load(statusPath);
        bool changed = SyncJapanese(status, Resw.LoadMap(Resw.PathFor(stringsFolder, "en")), Resw.LoadMap(Resw.PathFor(stringsFolder, "ja")));
        if (changed)
        {
            status.Save(statusPath);
        }

        return changed;
    }

    private static bool SyncJapanese(TranslationStatus status, IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> japanese)
    {
        Dictionary<string, TranslationEntry> ja = status.For("ja");
        var wanted = english.Where(e => japanese.ContainsKey(e.Key))
            .ToDictionary(e => e.Key, e => new TranslationEntry(TranslationState.Reviewed, TranslationStatus.Hash(e.Value)), StringComparer.Ordinal);
        bool changed = wanted.Count != ja.Count || wanted.Any(w => !ja.TryGetValue(w.Key, out TranslationEntry? e) || e != w.Value);
        ja.Clear();
        foreach ((string key, TranslationEntry entry) in wanted)
        {
            ja[key] = entry;
        }

        return changed;
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

        return new JsonObject { ["languages"] = languages }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n";
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
