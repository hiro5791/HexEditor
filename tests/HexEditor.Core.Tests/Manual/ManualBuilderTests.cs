using System.Text.RegularExpressions;
using HexEditor.Core.Help;
using HexEditor.ManualBuilder;

namespace HexEditor.Core.Tests.Manual;

/// <summary>
/// 利用者向けマニュアルの HTML を作るツール (tools/ManualBuilder。09 の UI-59): リポジトリの原稿の検査、壊れたリンクの検出、
/// HTML の作り (アクセシビリティ・テーマ・外部の参照なし)、翻訳のないページの扱い。
/// </summary>
public sealed class ManualBuilderTests : IDisposable
{
    private const string TC = "TC";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditor.ManualTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    internal static string RepositoryManual => Path.GetDirectoryName(Engine.SourceTests.FindRepoFile("docs/manual/manual.json"))!;

    private static ManualBuildResult BuildRepository() => SiteBuilder.Build(RepositoryManual);

    // ---- 小さな原稿を作る ----

    private const string Config = """
        {
          "defaultLanguage": "en",
          "languages": [ { "code": "en", "name": "English" }, { "code": "ja", "name": "日本語" }, { "code": "ar", "name": "العربية", "dir": "rtl" } ],
          "pages": [ "index", "editing" ],
          "strings": {
            "en": { "siteTitle": "Manual", "skipToContent": "Skip", "contents": "Contents", "onThisPage": "On this page", "language": "Language",
                    "previous": "Previous", "next": "Next", "notTranslated": "Not in {language} yet.", "chooseLanguage": "Choose" },
            "ja": { "siteTitle": "マニュアル" }
          }
        }
        """;

    private string Source(params (string Path, string Text)[] files)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "manual.json"), Config);
        foreach (string lang in new[] { "en", "ja", "ar" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, lang));
        }

        var all = new Dictionary<string, string>
        {
            ["en/index.md"] = "# Home\n\nSee [undo](editing.md#undo).\n",
            ["en/editing.md"] = "# Editing\n\n## Undo {#undo}\n\nPress <kbd>Ctrl</kbd>+<kbd>Z</kbd>.\n\n## Redo {#redo}\n\nText.\n",
            ["ja/index.md"] = "# ホーム\n\n[元に戻す](editing.md#undo) を見てください。\n",
            ["ja/editing.md"] = "# 編集\n\n## 元に戻す {#undo}\n\n本文。\n\n## やり直し {#redo}\n\n本文。\n",
        };
        foreach ((string path, string text) in files)
        {
            all[path] = text;
        }

        foreach ((string path, string text) in all)
        {
            string full = Path.Combine(_dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        return _dir;
    }

    private static string Problems(ManualBuildResult result) => string.Join("\n", result.Problems);

    // ---- リポジトリの原稿 ----

    [Fact]
    [Trait(TC, "TC-UI-59-01")]
    public void RepositoryManualBuildsWithoutBrokenLinksInEnglishAndJapanese()
    {
        ManualBuildResult result = BuildRepository();
        Assert.True(result.Succeeded, Problems(result));

        // 英語と日本語はすべてのページを書く (英語の代わりに出すページがない)。
        string[] languages = [.. result.Pages.Select(p => p.Language).Distinct()];
        Assert.Contains("en", languages);
        Assert.Contains("ja", languages);
        Assert.Empty(result.Pages.Where(p => p.Language is "en" or "ja" && p.IsFallback).Select(p => $"{p.Language}/{p.Id}"));
        Assert.Equal(result.Pages.Count(p => p.Language == "en"), result.Pages.Count(p => p.Language == "ja"));

        // 出力の HTML のページ間のリンクは、すべて存在するページと見出しを指す (書き換えの後でも)。
        foreach ((string path, byte[] _) in result.Files.Where(f => f.Key.EndsWith(".html", StringComparison.Ordinal)))
        {
            string html = result.Text(path);
            string folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            foreach (Match m in Regex.Matches(html, "(?:href|src)=\"([^\"]+)\""))
            {
                string url = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (url.StartsWith("https://", StringComparison.Ordinal))
                {
                    continue;
                }

                string target = url.Split('#')[0];
                string anchor = url.Contains('#') ? Uri.UnescapeDataString(url[(url.IndexOf('#') + 1)..]) : string.Empty;
                string resolved = target.Length == 0 ? path : Normalize(folder, target);
                Assert.True(result.Files.ContainsKey(resolved), $"{path}: {url} -> {resolved}");
                if (anchor.Length > 0)
                {
                    Assert.Contains($"id=\"{System.Net.WebUtility.HtmlEncode(anchor)}\"", result.Text(resolved), StringComparison.Ordinal);
                }
            }
        }
    }

    private static string Normalize(string folder, string relative)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (string part in relative.Split('/'))
        {
            if (part == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    [Fact]
    [Trait(TC, "TC-UI-59-01")]
    public void BuildIsDeterministic()
    {
        ManualBuildResult a = BuildRepository();
        ManualBuildResult b = BuildRepository();
        Assert.Equal(a.Files.Keys, b.Files.Keys);
        foreach (string key in a.Files.Keys)
        {
            Assert.Equal(a.Files[key], b.Files[key]);
        }
    }

    // ---- 壊れたリンクの検出 ----

    [Theory]
    [Trait(TC, "TC-UI-59-02")]
    [InlineData("[x](missing.md)", "does not exist")]
    [InlineData("[x](editing.md#nowhere)", "'#nowhere' does not exist")]
    [InlineData("[x](#nowhere)", "'#nowhere' does not exist")]
    [InlineData("[x](../ja/editing.md)", "same language")]
    [InlineData("[x](../../spec/00-overview.md)", "same language")]
    [InlineData("[x](editing.html)", "same language")]
    [InlineData("[x](http://example.com/)", "https")]
    [InlineData("![shot](images/missing.png)", "does not exist")]
    [InlineData("![](images/shot.png)", "alternative text")]
    [InlineData("![shot](https://example.com/a.png)", "Images must be files")]
    [InlineData("<script>alert(1)</script>", "not allowed")]
    [InlineData("Text <span>x", "not allowed")]
    public void BrokenLinksImagesAndHtmlFailTheBuild(string markdown, string message)
    {
        string source = Source(("en/index.md", "# Home\n\n" + markdown + "\n"));
        Directory.CreateDirectory(Path.Combine(source, "en", "images"));
        File.WriteAllBytes(Path.Combine(source, "en", "images", "shot.png"), [0x89, 0x50, 0x4E, 0x47]);
        ManualBuildResult result = SiteBuilder.Build(source);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Files);
        ManualProblem problem = Assert.Single(result.Problems);
        Assert.Equal("en/index.md", problem.File);
        Assert.Equal(3, problem.Line);
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TC, "TC-UI-59-02")]
    [InlineData("Text without a title.\n", "must start with a level-1 heading")]
    [InlineData("# A\n\n# B\n", "only one level-1 heading")]
    [InlineData("# A\n\n### Skipped\n", "skips a level")]
    [InlineData("# A\n\n## X {#same}\n\n## Y {#same}\n", "used twice")]
    public void HeadingProblemsFailTheBuild(string markdown, string message)
    {
        ManualBuildResult result = SiteBuilder.Build(Source(("ja/editing.md", markdown)));
        Assert.Contains(result.Problems, p => p.File == "ja/editing.md" && p.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-UI-59-02")]
    public void PagesMissingFromTheConfigAndUnknownFoldersFailTheBuild()
    {
        string source = Source(("en/orphan.md", "# Orphan\n"));
        ManualBuildResult result = SiteBuilder.Build(source);
        Assert.Contains(result.Problems, p => p.File == "en/orphan.md" && p.Message.Contains("not listed", StringComparison.Ordinal));

        File.Delete(Path.Combine(source, "en", "orphan.md"));
        Directory.CreateDirectory(Path.Combine(source, "xx"));
        result = SiteBuilder.Build(source);
        Assert.Contains(result.Problems, p => p.File == "xx" && p.Message.Contains("not a language", StringComparison.Ordinal));

        // 既定の言語にないページ。
        Directory.Delete(Path.Combine(source, "xx"));
        File.Delete(Path.Combine(source, "en", "editing.md"));
        result = SiteBuilder.Build(source);
        Assert.Contains(result.Problems, p => p.Message.Contains("'editing' is listed in manual.json but does not exist", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TC, "TC-UI-59-02")]
    public void ValidLinksAreRewrittenToHtml()
    {
        ManualBuildResult result = SiteBuilder.Build(Source());
        Assert.True(result.Succeeded, Problems(result));
        Assert.Contains("<a href=\"editing.html#undo\">undo</a>", result.Text("en/index.html"), StringComparison.Ordinal);
        Assert.Contains("<a href=\"editing.html#undo\">元に戻す</a>", result.Text("ja/index.html"), StringComparison.Ordinal);
        // 言語によらない見出しの ID ({#undo})。
        Assert.Contains("<h2 id=\"undo\">元に戻す</h2>", result.Text("ja/editing.html"), StringComparison.Ordinal);
        Assert.Contains("<kbd>Ctrl</kbd>+<kbd>Z</kbd>", result.Text("en/editing.html"), StringComparison.Ordinal);
    }

    // ---- HTML の作り ----

    [Fact]
    [Trait(TC, "TC-UI-59-03")]
    public void PagesHaveLanguageLandmarksAndNavigation()
    {
        ManualBuildResult result = SiteBuilder.Build(Source());
        Assert.True(result.Succeeded, Problems(result));
        string html = result.Text("ja/editing.html");
        Assert.Contains("<html lang=\"ja\" dir=\"ltr\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>編集 - マニュアル</title>", html, StringComparison.Ordinal);
        Assert.Contains("<a class=\"skip-link\" href=\"#main\">", html, StringComparison.Ordinal);
        Assert.Contains("<header class=\"site-header\">", html, StringComparison.Ordinal);
        Assert.Contains("<main id=\"main\"", html, StringComparison.Ordinal);
        Assert.Contains("<footer", html, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(html, "<h1[ >]"));
        // サイドバー (現在のページの印)、このページの内容、前へ。
        Assert.Contains("<a href=\"editing.html\" aria-current=\"page\">編集</a>", html, StringComparison.Ordinal);
        Assert.Contains("<li class=\"toc-h2\"><a href=\"#redo\">やり直し</a></li>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"index.html\" rel=\"prev\"", html, StringComparison.Ordinal);
        // 言語の切り替え: 同じページのほかの言語。
        Assert.Contains("<a href=\"../en/editing.html\" lang=\"en\" hreflang=\"en\">English</a>", html, StringComparison.Ordinal);
        Assert.Contains("<a href=\"editing.html\" lang=\"ja\" aria-current=\"true\">日本語</a>", html, StringComparison.Ordinal);
        // 右から左に書く言語。
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", result.Text("ar/editing.html"), StringComparison.Ordinal);
        // 入口: 言語を選ぶページ。
        Assert.Contains("<a href=\"ja/index.html\" lang=\"ja\"", result.Text("index.html"), StringComparison.Ordinal);
        Assert.Contains(".nojekyll", result.Files.Keys);
    }

    [Fact]
    [Trait(TC, "TC-UI-59-03")]
    public void StylesheetFollowsTheThemeAndUsesNoExternalResources()
    {
        ManualBuildResult result = BuildRepository();
        string css = result.Text(SiteBuilder.StylesheetPath);
        Assert.Contains("@media (prefers-color-scheme: dark)", css, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@import", css, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", css, StringComparison.Ordinal);
        foreach ((string path, byte[] _) in result.Files.Where(f => f.Key.EndsWith(".html", StringComparison.Ordinal)))
        {
            string html = result.Text(path);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch("<link [^>]*href=\"(https?:)?//", html);
            Assert.DoesNotMatch("<img [^>]*src=\"(https?:)?//", html);
        }
    }

    // ---- 翻訳のないページ ----

    [Fact]
    [Trait(TC, "TC-UI-59-04")]
    public void PagesWithoutATranslationAreShownInEnglishWithANotice()
    {
        // ar には原稿がない (フォルダだけ)。ja も editing だけ英語にする。
        string source = Source();
        File.Delete(Path.Combine(source, "ja", "editing.md"));
        ManualBuildResult result = SiteBuilder.Build(source);
        Assert.True(result.Succeeded, Problems(result));
        Assert.True(result.Pages.Single(p => p.Language == "ja" && p.Id == "editing").IsFallback);
        Assert.False(result.Pages.Single(p => p.Language == "ja" && p.Id == "index").IsFallback);
        string ja = result.Text("ja/editing.html");
        Assert.Contains("<html lang=\"ja\"", ja, StringComparison.Ordinal);
        Assert.Contains("<p class=\"notice\" role=\"note\">Not in 日本語 yet.</p>", ja, StringComparison.Ordinal);
        Assert.Contains("<article lang=\"en\">", ja, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"undo\">Undo</h2>", ja, StringComparison.Ordinal);
        // 画面の部品の文言は、その言語の訳 (なければ英語)。
        Assert.Contains("マニュアル", ja, StringComparison.Ordinal);
        Assert.Contains(">Contents<", ja, StringComparison.Ordinal);
        Assert.Contains("<a href=\"editing.html#undo\">", result.Text("ar/index.html"), StringComparison.Ordinal);
    }

    // ---- API リファレンス (docs/api の生成物) ----

    [Fact]
    [Trait(TC, "TC-UI-59-10")]
    public void ApiReferenceAndPluginSdkAreGeneratedPagesOfTheSite()
    {
        ManualBuildResult result = BuildRepository();
        Assert.True(result.Succeeded, Problems(result));
        foreach ((string page, string file) in new[] { ("api-reference", "reference"), ("api-plugins", "plugins") })
        {
            // 手で書いた原稿はない (docs/api の生成物だけが元)。
            Assert.False(File.Exists(Path.Combine(RepositoryManual, "en", page + ".md")));
            foreach (string language in new[] { "en", "ja" })
            {
                string source = File.ReadAllText(Engine.SourceTests.FindRepoFile($"docs/api/{file}.{language}.md")).ReplaceLineEndings("\n");
                string title = source.Split('\n')[0][2..];
                string html = result.Text($"{language}/{page}.html");
                Assert.Contains(System.Net.WebUtility.HtmlEncode(title), html, StringComparison.Ordinal);
                Assert.Contains(language == "en" ? "generated from the HexEditor SDK" : "自動生成されたもの", html, StringComparison.Ordinal);
                Assert.False(result.Pages.Single(p => p.Language == language && p.Id == page).IsFallback);
            }
        }

        // 利用者向けのページと目次からリンクする。docs/api どうしのリンクはサイトのページへのリンクになる。
        foreach (string language in new[] { "en", "ja" })
        {
            Assert.Contains("href=\"api-reference.html\"", result.Text($"{language}/scripting.html"), StringComparison.Ordinal);
            Assert.Contains("href=\"api-plugins.html\"", result.Text($"{language}/plugins.html"), StringComparison.Ordinal);
            Assert.Contains("href=\"api-reference.html\"", result.Text($"{language}/api-plugins.html"), StringComparison.Ordinal);
            string index = result.Text($"{language}/index.html");
            Assert.Contains("href=\"api-reference.html\"", index, StringComparison.Ordinal);
            Assert.Contains("href=\"api-plugins.html\"", index, StringComparison.Ordinal);
        }
    }

    // ---- 機能から開く見出し ----

    [Fact]
    [Trait(TC, "TC-UI-59-07")]
    public void EveryTopicTheAppOpensExistsInEveryLanguage()
    {
        ManualBuildResult result = BuildRepository();
        Assert.True(result.Succeeded, Problems(result));
        string source = File.ReadAllText(Engine.SourceTests.FindRepoFile("src/HexEditor.Core/Templates/TemplateDiagnostics.cs"));
        string[] codes = [.. Regex.Matches(source, "= \"(HXT[0-9]{4})\";").Select(m => m.Groups[1].Value).Distinct()];
        Assert.NotEmpty(codes);
        IEnumerable<ManualTopic> topics = ManualTopic.Linked.Concat(codes.Select(ManualTopic.TemplateError));
        foreach (string language in result.Pages.Select(p => p.Language).Distinct())
        {
            foreach (ManualTopic topic in topics)
            {
                ManualPage? page = result.Pages.SingleOrDefault(p => p.Language == language && p.Id == topic.Page);
                Assert.True(page is not null, $"{language}/{topic.Page}");
                Assert.True(topic.Anchor is null || page.Anchors.Contains(topic.Anchor), $"{language}/{topic.Page}#{topic.Anchor}");
                Assert.True(result.Files.ContainsKey(ManualLinks.RelativePath(topic, language).Split('#')[0]));
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-59-07")]
    public void CSharpDebuggingPageStatesTheLimitationInBothLanguages()
    {
        // 08 の AUTO-22 の受け入れ基準 3 (TC-AUTO-22-03 の手順 3 と同じ内容をマニュアルの原稿で確かめる)。
        ManualBuildResult result = BuildRepository();
        string en = result.Text("en/scripting.html");
        string ja = result.Text("ja/scripting.html");
        Assert.Contains("id=\"debugging-csharp-scripts\"", en, StringComparison.Ordinal);
        Assert.Contains("C# scripts cannot be stepped through in the app", en, StringComparison.Ordinal);
        Assert.Contains("id=\"debugging-csharp-scripts\"", ja, StringComparison.Ordinal);
        Assert.Contains("C# スクリプトは、アプリ内でステップ実行できません", ja, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualIndexListsTheLanguagesForTheApp()
    {
        string source = Source();
        ManualBuildResult result = SiteBuilder.Build(source);
        string output = Path.Combine(_dir, "out");
        SiteBuilder.Write(result, output);
        Assert.Equal(["en", "ja", "ar"], ManualLinks.AvailableLanguages(output));
        Assert.True(File.Exists(Path.Combine(output, "ja", "editing.html")));

        // 書き直すと前の出力は消える。
        File.WriteAllText(Path.Combine(output, "stale.html"), "x");
        SiteBuilder.Write(result, output);
        Assert.False(File.Exists(Path.Combine(output, "stale.html")));
    }
}
