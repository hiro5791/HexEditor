using HexEditor.Core.Help;

namespace HexEditor.Core.Tests.Manual;

/// <summary>ヘルプ > ドキュメントなどで開くマニュアルのページの選び方 (09 の UI-40 の仕様 1、UI-59)。</summary>
public sealed class ManualLinksTests : IDisposable
{
    private const string TC = "TC";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HexEditor.ManualLinks", Guid.NewGuid().ToString("N"), "manual");

    public ManualLinksTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ManualLinks.IndexFile), """{ "defaultLanguage": "en", "languages": ["en", "ja", "pt"], "pages": ["index"] }""");
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);

    private static bool Exists(string path) => true;

    [Fact]
    [Trait(TC, "TC-UI-59-05")]
    public void OnlineOpensThePageOfTheDisplayLanguage()
    {
        ManualLink en = ManualLinks.Choose(ManualTopic.Home, "en", online: true, _root, Exists);
        Assert.False(en.IsLocal);
        Assert.Equal("https://hiro5791.github.io/HexEditor/en/index.html", en.Uri.AbsoluteUri);

        ManualLink ja = ManualLinks.Choose(ManualTopic.Home, "ja", online: true, _root, Exists);
        Assert.Equal("https://hiro5791.github.io/HexEditor/ja/index.html", ja.Uri.AbsoluteUri);
        Assert.Equal("ja", ja.Language);

        ManualLink deep = ManualLinks.Choose(ManualTopic.CSharpScriptDebugging, "ja", online: true, _root, Exists);
        Assert.Equal("https://hiro5791.github.io/HexEditor/ja/scripting.html#debugging-csharp-scripts", deep.Uri.AbsoluteUri);
        Assert.Equal("https://hiro5791.github.io/HexEditor/en/template-error-codes.html#hxt1004",
            ManualLinks.Choose(ManualTopic.TemplateError("HXT1004"), "en", online: true, _root, Exists).Uri.AbsoluteUri);
    }

    [Fact]
    [Trait(TC, "TC-UI-59-05")]
    public void OfflineOpensTheBundledPage()
    {
        ManualLink link = ManualLinks.Choose(ManualTopic.CSharpScriptDebugging, "ja", online: false, _root, Exists);
        Assert.True(link.IsLocal);
        Assert.True(link.Uri.IsFile);
        Assert.Equal(Path.Combine(_root, "ja", "scripting.html"), link.Uri.LocalPath);
        Assert.Equal("#debugging-csharp-scripts", link.Uri.Fragment);
    }

    [Fact]
    [Trait(TC, "TC-UI-59-05")]
    public void OfflineWithoutTheBundledPageOpensTheOnlinePage()
    {
        // 同梱のマニュアルのないビルド (-p:HexBuildManual=false): オンラインのページ (開く前に URL を確かめる。UI-58 の仕様 3)。
        ManualLink link = ManualLinks.Choose(ManualTopic.Home, "en", online: false, _root, _ => false);
        Assert.False(link.IsLocal);
        Assert.Equal("https://hiro5791.github.io/HexEditor/en/index.html", link.Uri.AbsoluteUri);
    }

    [Theory]
    [Trait(TC, "TC-UI-59-04")]
    [InlineData("de", "en")]
    [InlineData("zh-Hans", "en")]
    [InlineData("ja", "ja")]
    [InlineData("JA", "ja")]
    [InlineData("pt-BR", "pt")]
    [InlineData("qps-ploc", "en")]
    public void LanguagesWithoutAManualFallBackToEnglish(string uiLanguage, string expected)
    {
        Assert.Equal(expected, ManualLinks.ResolveLanguage(uiLanguage, ManualLinks.AvailableLanguages(_root)));
        Assert.EndsWith($"/{expected}/index.html", ManualLinks.Choose(ManualTopic.Home, uiLanguage, online: true, _root, Exists).Uri.AbsoluteUri);
        Assert.EndsWith(Path.Combine(expected, "index.html"), ManualLinks.Choose(ManualTopic.Home, uiLanguage, online: false, _root, Exists).Uri.LocalPath);
    }

    [Fact]
    [Trait(TC, "TC-UI-59-04")]
    public void WithoutTheLanguageListOnlyEnglishIsUsed()
    {
        string empty = Path.Combine(Path.GetDirectoryName(_root)!, "none");
        Assert.Equal(["en"], ManualLinks.AvailableLanguages(empty));
        File.WriteAllText(Path.Combine(_root, ManualLinks.IndexFile), "{ broken");
        Assert.Equal(["en"], ManualLinks.AvailableLanguages(_root));
        Assert.Equal("en", ManualLinks.Choose(ManualTopic.Home, "ja", online: true, _root, Exists).Language);
    }
}
