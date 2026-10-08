using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Tests.Engine;
using HexEditor.I18nTool;

namespace HexEditor.Core.Tests.I18n;

/// <summary>
/// 翻訳の運用 (09 の UI-48 用語集、UI-49 機械翻訳と翻訳の確認、10 の PKG-29 リリースノートの翻訳の進捗)。
/// 機械翻訳の提供元は偽のサーバー (<see cref="FakeDeepL"/>。DeepL API と同じ形で応答する) に置き換え、実際には接続しない。
/// </summary>
public sealed class TranslationWorkflowTests : IDisposable
{
    private const string TC = "TC";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "HexEditorI18nTests", Guid.NewGuid().ToString("N"));

    public TranslationWorkflowTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepoFile(string relative) => SourceTests.FindRepoFile(relative);

    private string Strings => Path.Combine(_temp, "Strings");

    /// <summary>アプリの Strings フォルダ。</summary>
    private static string RepoStrings => Path.GetDirectoryName(Path.GetDirectoryName(RepoFile("src/HexEditor.App/Strings/en/Resources.resw")))!;

    private string StatusPath => Path.Combine(_temp, "translation-status.json");

    private void WriteResw(string language, params (string Key, string Value)[] values)
    {
        string path = Resw.PathFor(Strings, language);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\n");
        foreach ((string key, string value) in values)
        {
            text.Append($"  <data name=\"{key}\" xml:space=\"preserve\"><value>{value}</value></data>\n");
        }

        File.WriteAllText(path, text.Append("</root>\n").ToString(), new UTF8Encoding(false));
    }

    private static Glossary EmptyGlossary() => new([.. Glossary.FixedColumns, .. Glossary.LanguageColumns], []);

    // ---- UI-48 用語集 ----

    /// <summary>UI-48 の仕様 3 の用語 (00-overview 3 章の用語、7 章のメニュー名、その他)。</summary>
    private static readonly string[] RequiredTerms =
    [
        "document", "data source", "offset", "address", "cursor", "selection", "hex column", "text column", "overwrite mode", "insert mode",
        "long-running operation",
        "File", "Edit", "Search", "Go", "View", "Data", "Analysis", "Tools", "Forensics", "Help",
        "byte", "nibble", "endianness", "little-endian", "big-endian", "bookmark", "template", "inspector", "panel", "tab", "command palette",
    ];

    [Fact]
    [Trait(TC, "TC-UI-48-01")]
    public void Glossary_has_the_required_terms_with_every_language()
    {
        string path = RepoFile("docs/i18n/glossary.csv");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB, "glossary.csv must not have a BOM");
        Glossary glossary = Glossary.Load(path);
        Assert.Equal(["term", "description", "part_of_speech", "do_not_translate", "ja", "zh-Hans", "zh-Hant", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
            "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa"], glossary.Header);
        Assert.Empty(glossary.FormatErrors());
        foreach (string term in RequiredTerms)
        {
            GlossaryTerm row = Assert.Single(glossary.Terms, t => t.Term == term);
            Assert.False(row.DoNotTranslate);
            Assert.All(Glossary.LanguageColumns, l => Assert.False(string.IsNullOrWhiteSpace(row.Translations[l]), $"{term}: {l} is empty"));
        }

        Assert.Contains(glossary.Terms, t => t.Term == "UTF-8" && t.DoNotTranslate);
        Assert.Contains(glossary.Terms, t => t.Term == "Hex" && t.DoNotTranslate);
    }

    [Fact]
    [Trait(TC, "TC-UI-48-02")]
    public async Task Glossary_check_warns_when_a_do_not_translate_term_is_changed()
    {
        // TD-UI-RESW-GLOSSARY: UTF-8 は訳さない用語。fr の訳文で UTF8 に変わっている。
        string glossary = Path.Combine(_temp, "glossary.csv");
        File.WriteAllText(glossary, string.Join(',', [.. Glossary.FixedColumns, .. Glossary.LanguageColumns]) + "\n"
            + "UTF-8,character encoding name,proper noun,true" + new string(',', Glossary.LanguageColumns.Count) + "\n", new UTF8Encoding(false));
        WriteResw("en", ("Encoding_Utf8", "UTF-8 encoding"));
        WriteResw("fr", ("Encoding_Utf8", "Encodage UTF8"));
        WriteResw("de", ("Encoding_Utf8", "UTF-8-Kodierung"));

        var output = new StringWriter();
        TextWriter original = Console.Out;
        Console.SetOut(output);
        int exit;
        try
        {
            exit = await Cli.RunAsync(["check-glossary", "--strings", Strings, "--glossary", glossary]);
        }
        finally
        {
            Console.SetOut(original);
        }

        // 警告で、失敗にはしない。言語 fr、キー、用語 UTF-8 を報告する。
        Assert.Equal(0, exit);
        string text = output.ToString();
        Assert.Contains("::warning::fr: Encoding_Utf8: the term 'UTF-8'", text);
        Assert.DoesNotContain("de: Encoding_Utf8", text);
    }

    [Fact]
    public void Glossary_words_match_whole_words_only()
    {
        Assert.True(Glossary.ContainsWord("Hex column", "Hex"));
        Assert.True(Glossary.ContainsWord("Hex 列", "Hex"));
        Assert.False(Glossary.ContainsWord("HexEditor", "Hex"));
        Assert.Equal("Save as <x>UTF-8</x> (<x>{0}</x>) &amp; more", DeepLTranslator.Protect("Save as UTF-8 ({0}) & more",
            new Glossary([.. Glossary.FixedColumns], [new GlossaryTerm("UTF-8", "", "", true, new Dictionary<string, string>())])));
    }

    // ---- UI-49 機械翻訳と翻訳の確認 ----

    [Fact]
    [Trait(TC, "TC-UI-49-01")]
    public async Task New_string_is_machine_translated_into_21_languages_and_japanese_is_reviewed()
    {
        // テスト用のリポジトリの代わりに、本物のリソースをコピーして Test_NewString を en と ja に足す。
        foreach (string dir in Directory.GetDirectories(RepoStrings))
        {
            string language = Path.GetFileName(dir);
            Directory.CreateDirectory(Path.Combine(Strings, language));
            File.Copy(Path.Combine(dir, "Resources.resw"), Resw.PathFor(Strings, language));
        }

        File.Copy(RepoFile("docs/i18n/translation-status.json"), StatusPath);
        foreach ((string language, string value) in new[] { ("en", "A new string"), ("ja", "新しい文字列") })
        {
            string path = Resw.PathFor(Strings, language);
            string text = File.ReadAllText(path).Replace("</root>", $"  <data name=\"Test_NewString\" xml:space=\"preserve\"><value>{value}</value></data>\n</root>");
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        using var server = new FakeDeepL();
        using var http = new HttpClient(server);
        TranslationRunResult result = await MachineTranslation.RunAsync(Strings, StatusPath, Glossary.Load(RepoFile("docs/i18n/glossary.csv")),
            new DeepLTranslator(http, new Uri("https://deepl.test/"), "test-key"));
        Assert.Empty(result.Warnings);
        Assert.True(result.Changed);

        TranslationStatus status = TranslationStatus.Load(StatusPath);
        Assert.Equal(21, Languages.MachineTranslated.Count);
        foreach (string language in Languages.MachineTranslated)
        {
            Assert.Equal($"[{DeepLTranslator.TargetCode(language)}] A new string", Resw.LoadMap(Resw.PathFor(Strings, language))["Test_NewString"]);
            Assert.Equal(TranslationState.Machine, status.Get(language, "Test_NewString")!.State);
            Assert.Equal(TranslationStatus.Hash("A new string"), status.Get(language, "Test_NewString")!.SourceHash);
        }

        Assert.Equal(TranslationState.Reviewed, status.Get("ja", "Test_NewString")!.State);
        Assert.All(server.Requests, r => Assert.Equal("DeepL-Auth-Key test-key", r));

        // 既に訳がある文字列は訳し直さない (この新しい文字列だけを送った)。
        Assert.Equal(21, server.Texts.Count);

        // ワークフロー: 毎晩と手動の起動で、変更があれば 1 つの Pull Request を作る (UI-49 の仕様 2 の 3)。
        string workflow = File.ReadAllText(RepoFile(".github/workflows/translate.yml"));
        Assert.Contains("schedule:", workflow);
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("tools/I18nTool -- translate", workflow);
        Assert.Contains("HEX_TRANSLATOR_KEY: ${{ secrets.", workflow);
        Assert.Contains("gh pr create", workflow);
    }

    [Fact]
    [Trait(TC, "TC-UI-49-02")]
    public async Task Reviewed_translations_are_never_overwritten_and_become_stale_when_the_source_changes()
    {
        // TD-UI-I18N-STATUS: de の Test_Key は確認済み、fr は機械翻訳。どちらも原文 "Open file" のハッシュ。
        WriteResw("en", ("Test_Key", "Open the file"));
        WriteResw("ja", ("Test_Key", "ファイルを開く"));
        WriteResw("de", ("Test_Key", "Datei öffnen"));
        WriteResw("fr", ("Test_Key", "Ouvrir le fichier"));
        string old = TranslationStatus.Hash("Open file");
        File.WriteAllText(StatusPath, new JsonObject
        {
            ["version"] = 1,
            ["languages"] = new JsonObject
            {
                ["de"] = new JsonObject { ["Test_Key"] = "reviewed:" + old },
                ["fr"] = new JsonObject { ["Test_Key"] = "machine:" + old },
            },
        }.ToJsonString());

        using var server = new FakeDeepL();
        using var http = new HttpClient(server);
        await MachineTranslation.RunAsync(Strings, StatusPath, EmptyGlossary(), new DeepLTranslator(http, new Uri("https://deepl.test/"), "k"), ["de", "fr"]);

        TranslationStatus status = TranslationStatus.Load(StatusPath);
        Assert.Equal("Datei öffnen", Resw.LoadMap(Resw.PathFor(Strings, "de"))["Test_Key"]);
        Assert.Equal(new TranslationEntry(TranslationState.Stale, old), status.Get("de", "Test_Key"));
        Assert.Equal("[FR] Open the file", Resw.LoadMap(Resw.PathFor(Strings, "fr"))["Test_Key"]);
        Assert.Equal(new TranslationEntry(TranslationState.Machine, TranslationStatus.Hash("Open the file")), status.Get("fr", "Test_Key"));

        // 「要再確認」も上書きしない (2 回目の実行)。
        await MachineTranslation.RunAsync(Strings, StatusPath, EmptyGlossary(), new DeepLTranslator(http, new Uri("https://deepl.test/"), "k"), ["de", "fr"]);
        Assert.Equal("Datei öffnen", Resw.LoadMap(Resw.PathFor(Strings, "de"))["Test_Key"]);
        Assert.Equal(TranslationState.Stale, TranslationStatus.Load(StatusPath).Get("de", "Test_Key")!.State);
    }

    [Fact]
    public async Task A_failing_translation_api_is_a_warning_and_leaves_the_string_untranslated()
    {
        WriteResw("en", ("A", "One"), ("B", "Two"));
        WriteResw("de", ("A", "Eins"));
        File.WriteAllText(StatusPath, """{ "version": 1, "languages": { "de": { "A": "reviewed:%H%" } } }""".Replace("%H%", TranslationStatus.Hash("One")));
        using var server = new FakeDeepL { Fail = true };
        using var http = new HttpClient(server);
        TranslationRunResult result = await MachineTranslation.RunAsync(Strings, StatusPath, EmptyGlossary(), new DeepLTranslator(http, new Uri("https://deepl.test/"), "k"), ["de"]);
        Assert.Single(result.Warnings);
        Assert.False(Resw.LoadMap(Resw.PathFor(Strings, "de")).ContainsKey("B"));
        Assert.Null(TranslationStatus.Load(StatusPath).Get("de", "B"));
    }

    [Fact]
    public void Status_file_errors_fail_the_check()
    {
        File.WriteAllText(StatusPath, """{ "version": 1, "languages": { "de": { "A": "approved:0000" } } }""");
        Assert.Throws<FormatException>(() => TranslationStatus.Load(StatusPath));

        WriteResw("en", ("A", "One"));
        WriteResw("de");
        File.WriteAllText(StatusPath, """{ "version": 1, "languages": { "de": { "A": "machine:0123456789abcdef", "Gone": "machine:0123456789abcdef" } } }""");
        TranslationStatus status = TranslationStatus.Load(StatusPath);
        List<string> errors = [.. status.Mismatches(Resw.LoadMap(Resw.PathFor(Strings, "en")), l => Resw.LoadMap(Resw.PathFor(Strings, l)))];
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Repository_status_file_matches_the_resources()
    {
        string strings = Path.GetDirectoryName(Path.GetDirectoryName(RepoFile("src/HexEditor.App/Strings/en/Resources.resw")))!;
        TranslationStatus status = TranslationStatus.Load(RepoFile("docs/i18n/translation-status.json"));
        Assert.Empty(status.Mismatches(Resw.LoadMap(Resw.PathFor(strings, "en")), l => Resw.LoadMap(Resw.PathFor(strings, l))));
    }

    [Fact]
    [Trait(TC, "TC-UI-49-03")]
    public void Coverage_file_made_by_the_build_has_23_languages()
    {
        // アプリのビルドと同じターゲット (build/TranslationCoverage.targets) を、本物の Strings と状態ファイルで実行する。
        string project = Path.Combine(_temp, "coverage.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup><IntermediateOutputPath>obj\</IntermediateOutputPath><HexStringsFolder>{RepoStrings}</HexStringsFolder></PropertyGroup>
              <Import Project="{RepoFile("build/TranslationCoverage.targets")}" />
              <Target Name="Build" DependsOnTargets="HexTranslationCoverage" />
            </Project>
            """);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[] { "msbuild", project, "-nologo", "-t:Build" })
        {
            start.ArgumentList.Add(a);
        }

        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            string log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)) && process.ExitCode == 0, log);
        }

        string built = Path.Combine(_temp, "obj", TranslationCoverageFileName);
        Dictionary<string, (int Translated, int Reviewed)> coverage = Coverage.FromJson(File.ReadAllText(built));
        Assert.Equal(Languages.All.Select(l => l.Tag).Order(), coverage.Keys.Order());
        Assert.All(coverage.Values, v =>
        {
            Assert.InRange(v.Translated, 0, 100);
            Assert.InRange(v.Reviewed, 0, 100);
        });
        Assert.Equal((100, 100), coverage["en"]);

        // ビルドの計算とツールの計算 (Coverage.Compute) が同じ。
        IReadOnlyList<(string Language, int Translated, int Reviewed)> computed = Coverage.Compute(RepoStrings, TranslationStatus.Load(RepoFile("docs/i18n/translation-status.json")));
        Assert.All(computed, c => Assert.Equal((c.Translated, c.Reviewed), coverage[c.Language]));

        // アプリはこのファイルを同梱する。
        Assert.Contains("TranslationCoverage.targets", File.ReadAllText(RepoFile("src/HexEditor.App/HexEditor.App.csproj")));
    }

    private const string TranslationCoverageFileName = "translation-coverage.json";

    // ---- PKG-29 リリースノート ----

    [Fact]
    [Trait(TC, "TC-PKG-29-01")]
    public void Release_notes_have_the_translation_table_for_23_languages()
    {
        var current = Languages.All.ToDictionary(l => l.Tag, l => l.Tag switch { "en" or "ja" => (100, 100), "de" => (100, 87), _ => (100, 10) });
        var previous = new Dictionary<string, (int, int)> { ["de"] = (100, 82), ["ja"] = (100, 100) };
        string table = ReleaseTable.Build(current, previous);
        string[] rows = [.. table.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Language", StringComparison.Ordinal) && !l.StartsWith("| ---", StringComparison.Ordinal))];
        Assert.Equal(23, rows.Length);
        Assert.Contains("| Deutsch (de) | 100% | 87% | +5 |", rows);
        Assert.Contains("| 日本語 (ja) | 100% | 100% | ±0 |", rows);
        Assert.Contains($"| Français (fr) | 100% | 10% ({ReleaseTable.MachineNote}) | new |", rows);
        Assert.Contains("issues/new?template=translation.yml", table);
        Assert.Contains("CONTRIBUTING.md", table);

        // リリースのワークフローが表を作ってリリースノートに入れる。
        string release = File.ReadAllText(RepoFile(".github/workflows/release.yml"));
        Assert.Contains("tools/I18nTool -- release-table", release);
        Assert.Contains("-TranslationTable", release);
        Assert.Contains("{{TRANSLATIONS}}", File.ReadAllText(RepoFile("build/release/release-notes-template.md")));
    }

    /// <summary>DeepL API の偽物: 受けた text を「[言語コード] 原文」にして返す。</summary>
    private sealed class FakeDeepL : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> Texts { get; } = [];

        public bool Fail { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Assert.Equal("/v2/translate", request.RequestUri!.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&').Select(p => p.Split('=', 2)).Select(p => (Key: WebUtility.UrlDecode(p[0]), Value: WebUtility.UrlDecode(p[1]))).ToList();
            string target = form.Single(p => p.Key == "target_lang").Value;
            var translations = new JsonArray();
            foreach ((_, string text) in form.Where(p => p.Key == "text"))
            {
                Texts.Add(text);
                translations.Add(new JsonObject { ["text"] = $"[{target}] {text}" });
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["translations"] = translations }.ToJsonString()) };
        }
    }
}
