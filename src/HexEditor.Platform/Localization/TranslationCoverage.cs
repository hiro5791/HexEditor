using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Platform.Localization;

/// <summary>1 言語の翻訳の割合 (0〜100)。<see cref="Translated"/> は訳がある割合、<see cref="Reviewed"/> は人が確認した割合。</summary>
public sealed record LanguageCoverage(string Language, int Translated, int Reviewed);

/// <summary>
/// ビルド時に作ってアプリに同梱する <c>translation-coverage.json</c> (09 の UI-49 の仕様 4)。表示言語の一覧 (UI-43 の仕様 2) と
/// リリースノート (10 の PKG-29) に使う。形式: <c>{"languages": {"de": {"translated": 100, "reviewed": 87}, ...}}</c>。
/// </summary>
public static class TranslationCoverage
{
    public const string FileName = "translation-coverage.json";

    public static IReadOnlyDictionary<string, LanguageCoverage> Parse(string json)
    {
        var result = new Dictionary<string, LanguageCoverage>(StringComparer.OrdinalIgnoreCase);
        if (JsonNode.Parse(json)?["languages"] is not JsonObject languages)
        {
            return result;
        }

        foreach ((string language, JsonNode? node) in languages)
        {
            int translated = node?["translated"] is JsonValue t && t.TryGetValue(out int a) ? a : 0;
            int reviewed = node?["reviewed"] is JsonValue r && r.TryGetValue(out int b) ? b : 0;
            result[language] = new LanguageCoverage(language, Math.Clamp(translated, 0, 100), Math.Clamp(reviewed, 0, 100));
        }

        return result;
    }

    /// <summary>アプリのフォルダの <c>translation-coverage.json</c> を読む。なければ空。</summary>
    public static IReadOnlyDictionary<string, LanguageCoverage> Load(string folder)
    {
        try
        {
            string path = Path.Combine(folder, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new Dictionary<string, LanguageCoverage>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, LanguageCoverage>();
        }
    }
}
