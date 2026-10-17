using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HexEditor.ManualBuilder;

/// <summary>検査の誤り 1 件。<see cref="File"/> は原稿のフォルダからの相対パス。</summary>
internal sealed record ManualProblem(string File, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{File}({Line}): {Message}" : $"{File}: {Message}";
}

/// <summary>作ったページ 1 つ。<see cref="Anchors"/> は見出しの ID (出現順)。</summary>
internal sealed record ManualPage(string Language, string Id, string Title, bool IsFallback, IReadOnlyList<string> Anchors);

/// <summary>作った結果。<see cref="Files"/> は出力のフォルダからの相対パス (区切りは /) と内容。</summary>
internal sealed record ManualBuildResult(
    IReadOnlyList<ManualProblem> Problems,
    IReadOnlyList<ManualPage> Pages,
    IReadOnlyDictionary<string, byte[]> Files)
{
    public bool Succeeded => Problems.Count == 0;

    public string Text(string path) => Encoding.UTF8.GetString(Files[path]);
}

/// <summary>
/// 原稿 (Markdown) から静的な HTML のサイトを作る (09 の UI-59)。出力は同梱 (アプリの <c>manual\</c>) と GitHub Pages で同じもの。
/// <list type="bullet">
/// <item>ページ間のリンク (<c>editing.md#undo</c>) は <c>.html</c> に直し、ページと見出しの ID があるかを検査する。</item>
/// <item>ほかの言語にないページは、既定の言語の内容に「まだ翻訳されていない」旨を付けて出す。</item>
/// <item>外部のスクリプト・フォント・スタイルシートは使わない。画像は同じ言語の <c>images/</c> (なければ既定の言語のもの) だけ。</item>
/// <item>生の HTML は <c>&lt;kbd&gt;</c> だけ使える。</item>
/// </list>
/// </summary>
internal static partial class SiteBuilder
{
    public const string ConfigFile = "manual.json";
    public const string IndexFile = "manual-index.json";
    public const string StylesheetPath = "assets/manual.css";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };

    private static MarkdownPipeline CreatePipeline() => new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .UsePipeTables()
        .UseAutoLinks()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        // {#id} で見出しに言語によらない ID を付ける (機能からのリンク先。STYLE.md)。最後に足す (Markdig の決まり)。
        .UseGenericAttributes()
        .Build();

    /// <summary>原稿のフォルダ <paramref name="sourceDirectory"/> (manual.json のあるフォルダ) からサイトを作る。書き出しはしない。</summary>
    public static ManualBuildResult Build(string sourceDirectory)
    {
        var problems = new List<ManualProblem>();
        string configPath = Path.Combine(sourceDirectory, ConfigFile);
        if (!File.Exists(configPath))
        {
            return new([new(ConfigFile, 0, "manual.json was not found.")], [], new Dictionary<string, byte[]>());
        }

        ManualConfig config;
        try
        {
            config = ManualConfig.Load(configPath);
        }
        catch (JsonException ex)
        {
            return new([new(ConfigFile, 0, "manual.json is not valid: " + ex.Message)], [], new Dictionary<string, byte[]>());
        }

        ValidateConfig(sourceDirectory, config, problems);
        if (problems.Count > 0)
        {
            return new(problems, [], new Dictionary<string, byte[]>());
        }

        MarkdownPipeline pipeline = CreatePipeline();

        // 1. すべてのページを読む (リンクの検査に、ほかのページの見出しの ID が要るため先に全部読む)。
        var parsed = new Dictionary<(string Lang, string Page), ParsedPage>();
        foreach (ManualLanguage language in config.Languages)
        {
            foreach (string page in config.Pages)
            {
                string own = Path.Combine(sourceDirectory, language.Code, page + ".md");
                bool fallback = !File.Exists(own);
                string sourceLanguage = fallback ? config.DefaultLanguage : language.Code;
                string path = fallback ? Path.Combine(sourceDirectory, config.DefaultLanguage, page + ".md") : own;
                if (!File.Exists(path))
                {
                    var missing = new ManualProblem(Relative(sourceDirectory, path), 0, $"The page '{page}' is listed in manual.json but does not exist.");
                    if (!problems.Contains(missing))
                    {
                        problems.Add(missing);
                    }

                    continue;
                }

                // 既定の言語のページは、言語ごとに 1 回ずつ読む (リンクの書き換えが言語で変わらないため、同じ結果になる)。
                string markdown = File.ReadAllText(path).ReplaceLineEndings("\n");
                MarkdownDocument document = Markdown.Parse(markdown, pipeline);
                parsed[(language.Code, page)] = new ParsedPage(language.Code, page, sourceLanguage, Relative(sourceDirectory, path), document, fallback);
            }
        }

        foreach (ParsedPage page in parsed.Values)
        {
            CollectHeadings(page, problems);
        }

        // 2. リンクと画像を検査して書き換える。
        var images = new SortedDictionary<string, string>(StringComparer.Ordinal); // 出力のパス → 原稿のパス
        foreach (ParsedPage page in parsed.Values)
        {
            CheckInlines(sourceDirectory, config, page, parsed, images, problems);
        }

        // 原稿のフォルダにあるのに manual.json にないページ (載せ忘れ)。
        foreach (ManualLanguage language in config.Languages)
        {
            string dir = Path.Combine(sourceDirectory, language.Code);
            foreach (string file in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (Path.GetDirectoryName(file) != dir || !config.Pages.Contains(id))
                {
                    problems.Add(new(Relative(sourceDirectory, file), 0, "This page is not listed in the pages of manual.json (pages are directly in the language folder)."));
                }
            }
        }

        var pages = parsed.Values
            .OrderBy(p => config.Languages.FindIndex(l => l.Code == p.Language))
            .ThenBy(p => config.Pages.IndexOf(p.Page))
            .Select(p => new ManualPage(p.Language, p.Page, p.Title, p.IsFallback, [.. p.Headings.Select(h => h.Id)]))
            .ToList();
        if (problems.Count > 0)
        {
            return new(problems, pages, new Dictionary<string, byte[]>());
        }

        // 3. HTML を作る。
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (ParsedPage page in parsed.Values)
        {
            files[$"{page.Language}/{page.Page}.html"] = Utf8(RenderPage(config, page, parsed));
        }

        foreach ((string output, string source) in images)
        {
            files[output] = File.ReadAllBytes(source);
        }

        files[StylesheetPath] = Utf8(Stylesheet());
        files["index.html"] = Utf8(RenderLanguageChooser(config, parsed));
        // GitHub Pages で Jekyll を通さない (そのまま公開する)。
        files[".nojekyll"] = [];
        files[IndexFile] = Utf8(JsonSerializer.Serialize(new
        {
            defaultLanguage = config.DefaultLanguage,
            languages = config.Languages.Select(l => l.Code).ToArray(),
            pages = config.Pages.ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n");
        return new(problems, pages, files);
    }

    /// <summary>作った結果を <paramref name="outputDirectory"/> に書き出す。前の出力は消す。</summary>
    public static void Write(ManualBuildResult result, string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        foreach ((string path, byte[] content) in result.Files)
        {
            string target = Path.Combine(outputDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }
    }

    // ---- 構成の検査 ----

    private static void ValidateConfig(string sourceDirectory, ManualConfig config, List<ManualProblem> problems)
    {
        if (config.Languages.Count == 0 || config.Pages.Count == 0)
        {
            problems.Add(new(ConfigFile, 0, "languages and pages must not be empty."));
            return;
        }

        if (!config.Languages.Any(l => l.Code == config.DefaultLanguage))
        {
            problems.Add(new(ConfigFile, 0, $"The default language '{config.DefaultLanguage}' is not in languages."));
        }

        foreach (IGrouping<string, string> dup in config.Pages.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add(new(ConfigFile, 0, $"The page '{dup.Key}' is listed more than once."));
        }

        foreach (string page in config.Pages.Where(p => !PageId().IsMatch(p)))
        {
            problems.Add(new(ConfigFile, 0, $"The page ID '{page}' must use lowercase letters, digits and '-' only."));
        }

        if (!config.Pages.Contains("index"))
        {
            problems.Add(new(ConfigFile, 0, "The pages must include 'index' (the start page of each language)."));
        }

        foreach (ManualLanguage language in config.Languages)
        {
            if (!Directory.Exists(Path.Combine(sourceDirectory, language.Code)))
            {
                problems.Add(new(ConfigFile, 0, $"The folder of the language '{language.Code}' does not exist."));
            }

            if (language.Direction is not ("ltr" or "rtl"))
            {
                problems.Add(new(ConfigFile, 0, $"The dir of '{language.Code}' must be ltr or rtl."));
            }
        }

        foreach (string dir in Directory.EnumerateDirectories(sourceDirectory))
        {
            string name = Path.GetFileName(dir);
            if (!config.Languages.Any(l => l.Code == name))
            {
                problems.Add(new(name, 0, "This folder is not a language listed in manual.json."));
            }
        }

        foreach (string key in ManualConfig.StringKeys)
        {
            if (config.Strings.GetValueOrDefault(config.DefaultLanguage)?.ContainsKey(key) != true)
            {
                problems.Add(new(ConfigFile, 0, $"The string '{key}' of the default language is missing."));
            }
        }
    }

    // ---- 見出し ----

    private static void CollectHeadings(ParsedPage page, List<ManualProblem> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int previous = 0;
        foreach (HeadingBlock heading in page.Document.Descendants<HeadingBlock>())
        {
            string text = PlainText(heading.Inline);
            string? id = heading.GetAttributes().Id;
            int line = heading.Line + 1;
            if (heading.Level == 1)
            {
                if (page.Title.Length > 0)
                {
                    page.Problem(problems, line, "A page has only one level-1 heading (# Title).");
                }

                page.Title = text;
            }
            else if (page.Title.Length == 0)
            {
                page.Problem(problems, line, "The page must start with a level-1 heading (# Title).");
            }

            if (previous > 0 && heading.Level > previous + 1)
            {
                page.Problem(problems, line, $"The heading skips a level (h{previous} to h{heading.Level}).");
            }

            previous = heading.Level;
            if (string.IsNullOrEmpty(id))
            {
                page.Problem(problems, line, "The heading has no ID.");
                continue;
            }

            if (!ids.Add(id))
            {
                page.Problem(problems, line, $"The heading ID '{id}' is used twice on this page.");
            }

            page.Headings.Add((heading.Level, id, text));
        }

        if (page.Title.Length == 0)
        {
            page.Problem(problems, 1, "The page must start with a level-1 heading (# Title).");
        }
    }

    // ---- リンク・画像・HTML ----

    private static void CheckInlines(string sourceDirectory, ManualConfig config, ParsedPage page,
        Dictionary<(string Lang, string Page), ParsedPage> parsed, SortedDictionary<string, string> images, List<ManualProblem> problems)
    {
        foreach (HtmlBlock block in page.Document.Descendants<HtmlBlock>())
        {
            page.Problem(problems, block.Line + 1, "HTML blocks are not allowed (only <kbd> inside text).");
        }

        foreach (HtmlInline html in page.Document.Descendants<HtmlInline>())
        {
            if (!AllowedHtml().IsMatch(html.Tag))
            {
                page.Problem(problems, html.Line + 1, $"The HTML '{html.Tag}' is not allowed (only <kbd> and </kbd>).");
            }
        }

        foreach (LinkInline link in page.Document.Descendants<LinkInline>())
        {
            int line = link.Line + 1;
            string url = link.Url ?? string.Empty;
            if (link.IsImage)
            {
                CheckImage(sourceDirectory, config, page, link, line, images, problems);
                continue;
            }

            if (url.Length == 0)
            {
                page.Problem(problems, line, "The link has no target.");
                continue;
            }

            if (IsExternal(url))
            {
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    page.Problem(problems, line, $"Use https for external links: {url}");
                }

                continue;
            }

            int hash = url.IndexOf('#');
            string path = hash >= 0 ? url[..hash] : url;
            string? anchor = hash >= 0 ? Uri.UnescapeDataString(url[(hash + 1)..]) : null;
            string targetPage = page.Page;
            if (path.Length > 0)
            {
                if (path.StartsWith("./", StringComparison.Ordinal))
                {
                    path = path[2..];
                }

                if (path.Contains('/') || path.Contains('\\') || !path.EndsWith(".md", StringComparison.Ordinal))
                {
                    page.Problem(problems, line, $"Links must point to a page of the manual in the same language (page.md or page.md#heading), not '{url}'. Use a full https URL for other documents.");
                    continue;
                }

                targetPage = path[..^3];
                if (!config.Pages.Contains(targetPage))
                {
                    page.Problem(problems, line, $"The linked page '{path}' does not exist.");
                    continue;
                }
            }

            if (anchor is not null)
            {
                if (anchor.Length == 0)
                {
                    page.Problem(problems, line, "The link has an empty '#'.");
                    continue;
                }

                if (!parsed.TryGetValue((page.Language, targetPage), out ParsedPage? target) || !target.Headings.Any(h => h.Id == anchor))
                {
                    // 既定の言語の原稿をほかの言語で使うページから、訳したページへのリンクなら、どの言語の訳かを書く
                    // (既定の言語どうしの誤りは 1 回だけ出す)。
                    string where = page.IsFallback && target is { IsFallback: false } ? $" ({page.Language})" : string.Empty;
                    page.Problem(problems, line, $"The heading '#{anchor}' does not exist in '{targetPage}.md'{where}. Link to headings with an explicit ID ({{#id}}) that every language has.");
                    continue;
                }
            }

            link.Url = (path.Length > 0 ? targetPage + ".html" : string.Empty) + (anchor is null ? string.Empty : "#" + Uri.EscapeDataString(anchor));
        }
    }

    private static void CheckImage(string sourceDirectory, ManualConfig config, ParsedPage page, LinkInline link, int line,
        SortedDictionary<string, string> images, List<ManualProblem> problems)
    {
        string url = link.Url ?? string.Empty;
        if (PlainText(link).Trim().Length == 0)
        {
            page.Problem(problems, line, $"The image '{url}' has no alternative text ![text](images/...).");
        }

        if (IsExternal(url) || url.StartsWith("//", StringComparison.Ordinal))
        {
            page.Problem(problems, line, $"Images must be files in images/ of the manual, not '{url}'.");
            return;
        }

        string name = url.StartsWith("images/", StringComparison.Ordinal) ? url["images/".Length..] : string.Empty;
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)
            || !ImageExtensions.Contains(Path.GetExtension(name)))
        {
            page.Problem(problems, line, $"Images must be images/<name>.png (or .jpg, .gif, .svg, .webp), not '{url}'.");
            return;
        }

        // 同じ言語の images/ になければ、既定の言語の images/ のもの (画面の写真がまだない言語)。
        string own = Path.Combine(sourceDirectory, page.Language, "images", name);
        string fallback = Path.Combine(sourceDirectory, config.DefaultLanguage, "images", name);
        string? source = File.Exists(own) ? own : File.Exists(fallback) ? fallback : null;
        if (source is null)
        {
            page.Problem(problems, line, $"The image '{url}' does not exist.");
            return;
        }

        images[$"{page.Language}/images/{name}"] = source;
    }

    private static bool IsExternal(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    // ---- HTML ----

    private static string RenderPage(ManualConfig config, ParsedPage page, Dictionary<(string Lang, string Page), ParsedPage> parsed)
    {
        ManualLanguage language = config.Languages.Single(l => l.Code == page.Language);
        string T(string key) => Html(config.Text(page.Language, key));
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append($"<html lang=\"{Html(page.Language)}\" dir=\"{language.Direction}\">\n");
        html.Append("<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"color-scheme\" content=\"light dark\">\n");
        html.Append($"<title>{Html(page.Title)} - {T("siteTitle")}</title>\n");
        html.Append("<link rel=\"stylesheet\" href=\"../").Append(StylesheetPath).Append("\">\n");
        foreach (ManualLanguage other in config.Languages.Where(l => l.Code != page.Language))
        {
            html.Append($"<link rel=\"alternate\" hreflang=\"{Html(other.Code)}\" href=\"../{Html(other.Code)}/{page.Page}.html\">\n");
        }

        html.Append("</head>\n<body>\n");
        html.Append($"<a class=\"skip-link\" href=\"#main\">{T("skipToContent")}</a>\n");

        // ヘッダ: サイトの名前と言語の切り替え。
        html.Append("<header class=\"site-header\">\n");
        html.Append($"<a class=\"site-title\" href=\"index.html\">{T("siteTitle")}</a>\n");
        if (config.Languages.Count > 1)
        {
            html.Append($"<nav class=\"languages\" aria-label=\"{T("language")}\">\n<ul>\n");
            foreach (ManualLanguage other in config.Languages)
            {
                html.Append(other.Code == page.Language
                    ? $"<li><a href=\"{page.Page}.html\" lang=\"{Html(other.Code)}\" aria-current=\"true\">{Html(other.Name)}</a></li>\n"
                    : $"<li><a href=\"../{Html(other.Code)}/{page.Page}.html\" lang=\"{Html(other.Code)}\" hreflang=\"{Html(other.Code)}\">{Html(other.Name)}</a></li>\n");
            }

            html.Append("</ul>\n</nav>\n");
        }

        html.Append("</header>\n<div class=\"layout\">\n");

        // サイドバー: すべてのページ。
        html.Append($"<nav class=\"sidebar\" aria-label=\"{T("contents")}\">\n<p class=\"sidebar-title\">{T("contents")}</p>\n<ol>\n");
        foreach (string id in config.Pages)
        {
            ParsedPage other = parsed[(page.Language, id)];
            html.Append(id == page.Page
                ? $"<li><a href=\"{id}.html\" aria-current=\"page\">{Html(other.Title)}</a></li>\n"
                : $"<li><a href=\"{id}.html\">{Html(other.Title)}</a></li>\n");
        }

        html.Append("</ol>\n</nav>\n");

        // 見出し 1 (ページの題) を先に出し、その後に「このページの内容」と本文を置く (読み上げの順)。
        string articleLang = page.IsFallback ? $" lang=\"{Html(page.SourceLanguage)}\"" : string.Empty;
        HeadingBlock? title = page.Document.OfType<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        html.Append("<main id=\"main\" tabindex=\"-1\">\n");
        if (title is not null)
        {
            page.Document.Remove(title);
            html.Append($"<h1 id=\"{Html(title.GetAttributes().Id ?? string.Empty)}\"{articleLang}>{Html(page.Title)}</h1>\n");
        }

        if (page.IsFallback)
        {
            html.Append($"<p class=\"notice\" role=\"note\">{Html(config.Text(page.Language, "notTranslated").Replace("{language}", language.Name, StringComparison.Ordinal))}</p>\n");
        }

        // このページの目次 (h2・h3)。
        var toc = page.Headings.Where(h => h.Level is 2 or 3).ToList();
        if (toc.Count > 1)
        {
            html.Append($"<nav class=\"toc\" aria-label=\"{T("onThisPage")}\">\n<p class=\"toc-title\">{T("onThisPage")}</p>\n<ul>\n");
            foreach ((int level, string id, string text) in toc)
            {
                html.Append($"<li class=\"toc-h{level}\"><a href=\"#{Html(id)}\">{Html(text)}</a></li>\n");
            }

            html.Append("</ul>\n</nav>\n");
        }

        html.Append($"<article{articleLang}>\n");
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        CreatePipeline().Setup(renderer);
        renderer.Render(page.Document);
        writer.Flush();
        html.Append(writer.ToString().ReplaceLineEndings("\n"));
        html.Append("</article>\n");

        // 前へ・次へ。
        int index = config.Pages.IndexOf(page.Page);
        html.Append("<nav class=\"pager\" aria-label=\"").Append(T("previous")).Append(" / ").Append(T("next")).Append("\">\n");
        if (index > 0)
        {
            string prev = config.Pages[index - 1];
            html.Append($"<a class=\"prev\" href=\"{prev}.html\" rel=\"prev\"><span>{T("previous")}</span> {Html(parsed[(page.Language, prev)].Title)}</a>\n");
        }

        if (index < config.Pages.Count - 1)
        {
            string next = config.Pages[index + 1];
            html.Append($"<a class=\"next\" href=\"{next}.html\" rel=\"next\"><span>{T("next")}</span> {Html(parsed[(page.Language, next)].Title)}</a>\n");
        }

        html.Append("</nav>\n</main>\n</div>\n");
        html.Append($"<footer class=\"site-footer\"><p>{T("siteTitle")}</p></footer>\n");
        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>サイトの入口 (index.html): 言語を選ぶページ。各言語の入口は &lt;言語&gt;/index.html。</summary>
    private static string RenderLanguageChooser(ManualConfig config, Dictionary<(string Lang, string Page), ParsedPage> parsed)
    {
        string lang = config.DefaultLanguage;
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append($"<html lang=\"{Html(lang)}\" dir=\"ltr\">\n");
        html.Append("<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<meta name=\"color-scheme\" content=\"light dark\">\n");
        html.Append($"<title>{Html(config.Text(lang, "siteTitle"))}</title>\n");
        html.Append("<link rel=\"stylesheet\" href=\"").Append(StylesheetPath).Append("\">\n</head>\n<body>\n");
        html.Append("<main id=\"main\" class=\"chooser\">\n");
        html.Append($"<h1>{Html(config.Text(lang, "siteTitle"))}</h1>\n");
        html.Append($"<nav aria-label=\"{Html(config.Text(lang, "chooseLanguage"))}\">\n<ul class=\"chooser-list\">\n");
        foreach (ManualLanguage language in config.Languages)
        {
            html.Append($"<li><a href=\"{Html(language.Code)}/index.html\" lang=\"{Html(language.Code)}\" hreflang=\"{Html(language.Code)}\" dir=\"{language.Direction}\">"
                + $"{Html(language.Name)}</a> <span lang=\"{Html(language.Code)}\">{Html(config.Text(language.Code, "siteTitle"))}</span></li>\n");
        }

        html.Append("</ul>\n</nav>\n</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private static string Stylesheet()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HexEditor.ManualBuilder.manual.css")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    // ---- 部品 ----

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>見出しやリンクの文字列 (書式を除いたもの)。</summary>
    internal static string PlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (Inline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case ContainerInline child:
                    text.Append(PlainText(child));
                    break;
            }
        }

        return text.ToString();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex PageId();

    [GeneratedRegex("^</?kbd>$", RegexOptions.IgnoreCase)]
    private static partial Regex AllowedHtml();

    private sealed class ParsedPage(string language, string page, string sourceLanguage, string file, MarkdownDocument document, bool isFallback)
    {
        public string Language { get; } = language;

        public string Page { get; } = page;

        /// <summary>原稿の言語 (翻訳がなければ既定の言語)。</summary>
        public string SourceLanguage { get; } = sourceLanguage;

        public string File { get; } = file;

        public MarkdownDocument Document { get; } = document;

        public bool IsFallback { get; } = isFallback;

        public string Title { get; set; } = string.Empty;

        public List<(int Level, string Id, string Text)> Headings { get; } = [];

        /// <summary>同じ原稿 (既定の言語のページを言語ごとに使う場合) の誤りは 1 回だけ出す。</summary>
        public void Problem(List<ManualProblem> problems, int line, string message)
        {
            var problem = new ManualProblem(File, line, message);
            if (!problems.Contains(problem))
            {
                problems.Add(problem);
            }
        }
    }
}
