using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.I18nTool;

/// <summary>訳文の状態 (09 の UI-49 の仕様 1)。</summary>
public enum TranslationState
{
    /// <summary>機械翻訳のまま (人の確認待ち)。原文が変わったら機械翻訳し直す。</summary>
    Machine,

    /// <summary>人が確認した。機械翻訳で上書きしない。</summary>
    Reviewed,

    /// <summary>確認した後に原文が変わった (要再確認)。訳はそのまま表示に使い、機械翻訳で上書きしない。</summary>
    Stale,
}

/// <summary>1 つの訳の状態と、訳したときの英語の原文のハッシュ。</summary>
public sealed record TranslationEntry(TranslationState State, string SourceHash);

/// <summary>
/// 訳文の状態ファイル <c>docs/i18n/translation-status.json</c> (09 の UI-49 の仕様 1)。言語と文字列のキーごとに、状態と、訳したときの
/// 英語の原文のハッシュ (SHA-256 の先頭 16 桁) を持つ。状態ファイルにない文字列は未翻訳とする。
/// 形式: <c>{"version": 1, "languages": {"de": {"Menu_File.Title": "machine:0123456789abcdef", ...}, ...}}</c>。
/// </summary>
public sealed class TranslationStatus
{
    public const int FormatVersion = 1;

    private static readonly string[] StateNames = ["machine", "reviewed", "stale"];

    public Dictionary<string, Dictionary<string, TranslationEntry>> Languages { get; } = new(StringComparer.Ordinal);

    /// <summary>原文のハッシュ (UTF-8 の SHA-256 の先頭 16 桁、小文字)。</summary>
    public static string Hash(string source) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];

    public Dictionary<string, TranslationEntry> For(string language)
    {
        if (!Languages.TryGetValue(language, out Dictionary<string, TranslationEntry>? entries))
        {
            Languages[language] = entries = new(StringComparer.Ordinal);
        }

        return entries;
    }

    public TranslationEntry? Get(string language, string key) =>
        Languages.TryGetValue(language, out Dictionary<string, TranslationEntry>? e) && e.TryGetValue(key, out TranslationEntry? entry) ? entry : null;

    /// <summary>読む。形式が違えば <see cref="FormatException"/> (CI を失敗にする。UI-49 の「エラー」)。</summary>
    public static TranslationStatus Load(string path)
    {
        var status = new TranslationStatus();
        if (!File.Exists(path))
        {
            return status;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{path}: {ex.Message}", ex);
        }

        if (root?["version"] is not JsonValue v || !v.TryGetValue(out int version) || version != FormatVersion)
        {
            throw new FormatException($"{path}: \"version\" must be {FormatVersion}.");
        }

        if (root["languages"] is not JsonObject languages)
        {
            throw new FormatException($"{path}: \"languages\" must be an object.");
        }

        foreach ((string language, JsonNode? node) in languages)
        {
            if (node is not JsonObject keys)
            {
                throw new FormatException($"{path}: languages.{language} must be an object.");
            }

            Dictionary<string, TranslationEntry> entries = status.For(language);
            foreach ((string key, JsonNode? value) in keys)
            {
                string text = value is JsonValue jv && jv.TryGetValue(out string? s) ? s : string.Empty;
                string[] parts = text.Split(':');
                int state = parts.Length == 2 ? Array.IndexOf(StateNames, parts[0]) : -1;
                if (state < 0 || parts[1].Length != 16 || !parts[1].All(Uri.IsHexDigit))
                {
                    throw new FormatException($"{path}: languages.{language}.{key} must be \"machine|reviewed|stale:<16 hex digits>\" (was \"{text}\").");
                }

                entries[key] = new TranslationEntry((TranslationState)state, parts[1].ToLowerInvariant());
            }
        }

        return status;
    }

    public void Save(string path)
    {
        var languages = new JsonObject();
        foreach ((string tag, _) in global::HexEditor.I18nTool.Languages.All)
        {
            if (!Languages.TryGetValue(tag, out Dictionary<string, TranslationEntry>? entries) || entries.Count == 0)
            {
                continue;
            }

            var keys = new JsonObject();
            foreach ((string key, TranslationEntry entry) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                keys[key] = $"{StateNames[(int)entry.State]}:{entry.SourceHash}";
            }

            languages[tag] = keys;
        }

        var root = new JsonObject
        {
            ["$comment"] = "UI-49: machine = machine translation / reviewed = reviewed by a person (never overwritten) / stale = reviewed, but the English source changed. The value is <state>:<first 16 hex digits of the SHA-256 of the English source>.",
            ["version"] = FormatVersion,
            ["languages"] = languages,
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n",
            new UTF8Encoding(false));
    }

    /// <summary>
    /// 状態ファイルと .resw の食い違い (CI を失敗にする。UI-49 の「エラー」): 状態があるのに訳がない、英語にないキーの状態がある。
    /// </summary>
    public IEnumerable<string> Mismatches(IReadOnlyDictionary<string, string> english, Func<string, IReadOnlyDictionary<string, string>> translations)
    {
        foreach ((string language, Dictionary<string, TranslationEntry> entries) in Languages.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            IReadOnlyDictionary<string, string> values = translations(language);
            foreach (string key in entries.Keys.Order(StringComparer.Ordinal))
            {
                if (!english.ContainsKey(key))
                {
                    yield return $"{language}: {key} is in translation-status.json but not in the English resources.";
                }
                else if (!values.ContainsKey(key))
                {
                    yield return $"{language}: {key} has a state but no translation in Resources.resw.";
                }
            }
        }
    }
}
