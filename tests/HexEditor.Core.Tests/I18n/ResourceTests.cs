using HexEditor.Core.Tests.Engine;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.I18n;

/// <summary>
/// 23 言語の文字列リソースの検査 (UI-42、UI-49)、メニューのアクセスキー (UI-03)、色の直書き (UI-26)。
/// </summary>
public sealed class ResourceTests
{
    private static readonly string[] Languages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    private static string AppFolder => Path.GetDirectoryName(SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;

    private static Dictionary<string, string> Load(string language) =>
        ResourceChecker.LoadResw(Path.Combine(AppFolder, "Strings", language, "Resources.resw"));

    private static IEnumerable<string> AppFiles(string pattern) =>
        Directory.EnumerateFiles(AppFolder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    public static TheoryData<string> AllLanguages() => [.. Languages];

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryLanguageHasSameKeysAndPlaceholdersAsEnglish(string language)
    {
        var errors = ResourceChecker.CompareWithEnglish(language, Load("en"), Load(language)).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void EveryKeyIsUsed()
    {
        var errors = ResourceChecker.UnusedKeys(
            Load("en").Keys,
            AppFiles("*.cs").Select(File.ReadAllText),
            AppFiles("*.xaml").Select(File.ReadAllText)).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void XamlHasNoLiteralStrings()
    {
        var errors = AppFiles("*.xaml").SelectMany(f => ResourceChecker.XamlLiterals(Path.GetFileName(f), File.ReadAllText(f))).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    [Trait(TC, "TC-UI-03-04")]
    public void AccessKeysAreUniqueWithinEachMenu(string language)
    {
        string xaml = File.ReadAllText(Path.Combine(AppFolder, "MainWindow.xaml"));
        Dictionary<string, string> strings = Load("en");
        foreach ((string key, string value) in Load(language))
        {
            strings[key] = value;
        }

        var errors = ResourceChecker.DuplicateAccessKeys(language, strings, ResourceChecker.Menus(xaml)).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    [Trait(TC, "TC-UI-26-04")]
    public void NoHardCodedColors()
    {
        // アクセントカラーの派生色の計算 (Appearance.cs) は色の直書きではないので除く。
        var errors = AppFiles("*.xaml").SelectMany(f => ResourceChecker.HardCodedColors(Path.GetFileName(f), File.ReadAllText(f), isXaml: true))
            .Concat(AppFiles("*.cs").Where(f => Path.GetFileName(f) != "Appearance.cs")
                .SelectMany(f => ResourceChecker.HardCodedColors(Path.GetFileName(f), File.ReadAllText(f), isXaml: false)))
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>検査が誤りを見つけられること (わざと誤ったデータを渡す)。</summary>
    [Fact]
    [Trait(TC, "TC-UI-42-01")]
    public void CheckerDetectsErrors()
    {
        var english = new Dictionary<string, string> { ["A_One"] = "Open {0}", ["A_Two"] = "Close", ["A_Unused"] = "x" };
        var german = new Dictionary<string, string> { ["A_One"] = "Öffnen", ["A_Old"] = "Alt" };
        var compare = ResourceChecker.CompareWithEnglish("de", english, german).ToList();
        Assert.Contains(compare, e => e.Contains("A_Two") && e.Contains("ありません"));
        Assert.Contains(compare, e => e.Contains("A_Old"));
        Assert.Contains(compare, e => e.Contains("A_One") && e.Contains("プレースホルダー"));

        Assert.Equal(
            ["キー A_Unused はどこからも参照されていません。"],
            ResourceChecker.UnusedKeys(english.Keys, ["Loc.Format(\"A_One\", x); Loc.Get(\"A_Two\");"], []));

        const string Xaml = """<Grid xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"><Button Content="Save" /><TextBlock Text="{x:Bind Name}" /></Grid>""";
        Assert.Single(ResourceChecker.XamlLiterals("Test.xaml", Xaml));

        var strings = new Dictionary<string, string> { ["M_A.AccessKey"] = "F", ["M_B.AccessKey"] = "f" };
        Assert.Single(ResourceChecker.DuplicateAccessKeys("en", strings, [("M", ["M_A", "M_B"])]));

        Assert.Single(ResourceChecker.HardCodedColors("a.xaml", "<Border Background=\"#FF0000\" />", isXaml: true));
        Assert.Single(ResourceChecker.HardCodedColors("a.cs", "var c = Colors.Red;", isXaml: false));
    }
}
