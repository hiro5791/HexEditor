using System.Text.RegularExpressions;
using System.Xml.Linq;
using HexEditor.Core.Tests.Engine;

namespace HexEditor.Core.Tests.I18n;

/// <summary>
/// 23 言語の文字列リソースの整合性 (UI-42、UI-49)。すべての言語が英語と同じキーを持ち、英語の {n} をすべて含むこと。
/// </summary>
public sealed partial class ResourceTests
{
    private static readonly string[] Languages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    private static Dictionary<string, string> Load(string language)
    {
        string path = SourceTests.FindRepoFile($"src/HexEditor.App/Strings/{language}/Resources.resw");
        return XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")!.Value);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    public static TheoryData<string> AllLanguages() => [.. Languages];

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryLanguageHasSameKeysAndPlaceholdersAsEnglish(string language)
    {
        Dictionary<string, string> english = Load("en");
        Dictionary<string, string> translated = Load(language);
        Assert.Equal(english.Keys.Order(), translated.Keys.Order());
        foreach ((string key, string source) in english)
        {
            var expected = Placeholder().Matches(source).Select(m => m.Value).Order().ToList();
            var actual = Placeholder().Matches(translated[key]).Select(m => m.Value).Order().ToList();
            Assert.True(expected.SequenceEqual(actual), $"{language}/{key}: {{n}} が英語と違います。");
            Assert.False(string.IsNullOrWhiteSpace(translated[key]), $"{language}/{key} が空です。");
        }
    }
}
