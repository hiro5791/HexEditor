using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 言語ごとのスクリーンショットの撮影 (UI-47)。毎晩の CI (ci.yml の screenshots。言語を 5 つの組に分けて並列に実行する) で実行する
/// (25 言語 × 画面 × テーマ × 大きさ × 表示倍率で時間がかかるため、UI テストの分類 (Category=UI) には入れない)。
/// 表示倍率 200% は、モニターの表示倍率を変えずに再現する (テスト方針 7.2): 画面全体のズーム (UI-08 の仕様 3、設定 view.zoom.ui) を
/// 200% にし、ウィンドウを物理ピクセルで 2 倍の大きさにする (論理的な大きさと文字・画像の解像度が 200% のモニターと同じになる)。
/// 画面より大きいウィンドウにできない場合は、その大きさの画面を「失敗」と記録する。
/// 出力: 環境変数 HEXEDITOR_SCREENSHOTS のフォルダ (既定は TestResults/screenshots) の下に言語ごとのフォルダを作り、PNG と一覧
/// (index.html)、切れた文字列の一覧 (trimmed.txt) を置く。環境変数 HEXEDITOR_SCREENSHOTS_BASELINE に前回の出力があれば
/// 切れた文字列の数を比べ、増えていれば警告にする (失敗にはしない。仕様 4)。HEXEDITOR_SCREENSHOT_LANGUAGES (カンマ区切り) で
/// 言語を絞れる (手元での確認用)。
/// </summary>
[Trait(UiTest.Category, "Screenshots")]
public sealed class ScreenshotTests
{
    /// <summary>23 言語 + 疑似翻訳 2 つ (UI-47 の仕様 2)。</summary>
    public static readonly string[] Languages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa", "qps-ploc", "qps-plocm",
    ];

    private static readonly string[] Themes = ["light", "dark"];

    private static readonly (int Width, int Height)[] Sizes = [(1024, 768), (1920, 1080)];

    /// <summary>表示倍率 (200% は画面全体のズームで再現する)。</summary>
    private static readonly int[] Scales = [100, 200];

    public static string OutputRoot =>
        Environment.GetEnvironmentVariable("HEXEDITOR_SCREENSHOTS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppLocator.RepositoryRoot, "TestResults", "screenshots");

    [Fact]
    [Trait(UiTest.TC, "TC-UI-47-01")]
    public async Task Screenshots_for_all_languages()
    {
        string[] languages = Environment.GetEnvironmentVariable("HEXEDITOR_SCREENSHOT_LANGUAGES") is { Length: > 0 } only
            ? only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Languages;
        if (Directory.Exists(OutputRoot))
        {
            Directory.Delete(OutputRoot, recursive: true);
        }

        var warnings = new List<string>();
        foreach (string language in languages)
        {
            Shots shots = await CaptureLanguageAsync(language);
            warnings.AddRange(shots.Compare());
        }

        // 期待結果: 言語ごとのフォルダに、画面 × 表示倍率 2 × テーマ 2 × 大きさ 2 の数の PNG (失敗したものは一覧に「失敗」) と一覧の HTML。
        string[] folders = [.. Directory.GetDirectories(OutputRoot).Select(Path.GetFileName)!];
        Assert.Equal(languages.Order(), folders.Order());
        int expected = MainScreens.AllNames.Length * Scales.Length * Themes.Length * Sizes.Length;
        foreach (string folder in Directory.GetDirectories(OutputRoot))
        {
            string index = Path.Combine(folder, "index.html");
            Assert.True(File.Exists(index), $"{folder}: no index.html");
            int pngs = Directory.GetFiles(folder, "*.png").Length;
            int failed = Regex.Count(await File.ReadAllTextAsync(index), "<td class=\"failed\">");
            Assert.True(pngs + failed == expected, $"{Path.GetFileName(folder)}: {pngs} PNG + {failed} failed, expected {expected}");
            Assert.True(pngs >= expected / 2, $"{Path.GetFileName(folder)}: only {pngs} PNG");
        }

        // 切れた文字列が前回より増えた場合は警告 (失敗にはしない)。
        if (warnings.Count > 0)
        {
            string text = "Clipped text increased:\n" + string.Join("\n", warnings);
            await File.WriteAllTextAsync(Path.Combine(OutputRoot, "warnings.txt"), text);
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            {
                await File.AppendAllTextAsync(summary, "### ⚠ UI-47: " + text.Replace("\n", "\n- ", StringComparison.Ordinal) + "\n");
            }
        }
    }

    private sealed class Shots(string language, string folder)
    {
        public List<(string Screen, string Theme, string Size, int Scale, string? File, string? Error)> Rows { get; } = [];

        public SortedSet<string> Clipped { get; } = new(StringComparer.Ordinal);

        /// <summary>前回の出力 (HEXEDITOR_SCREENSHOTS_BASELINE) と比べ、増えた切れの行を返す。</summary>
        public IEnumerable<string> Compare()
        {
            string? baseline = Environment.GetEnvironmentVariable("HEXEDITOR_SCREENSHOTS_BASELINE");
            string previousFile = baseline is null ? string.Empty : Path.Combine(baseline, language, "trimmed.txt");
            string[] previous = File.Exists(previousFile) ? File.ReadAllLines(previousFile) : [];
            return TrimReport.Compare(previous, Clipped) == TrimVerdict.Warning && baseline is not null
                ? Clipped.Except(previous).Select(l => language + ": " + l)
                : [];
        }

        public void Write()
        {
            File.WriteAllLines(Path.Combine(folder, "trimmed.txt"), Clipped);
            var html = new StringBuilder();
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(language))
                .Append(" screenshots</title><style>body{font-family:sans-serif}td{padding:4px 8px;border-bottom:1px solid #ccc}")
                .Append("img{max-width:480px}.failed{color:#b00}</style></head><body><h1>").Append(WebUtility.HtmlEncode(language)).Append("</h1>");
            html.Append("<h2>Clipped or overlapping text (").Append(Clipped.Count).Append(")</h2><ul>");
            foreach (string line in Clipped)
            {
                html.Append("<li>").Append(WebUtility.HtmlEncode(line)).Append("</li>");
            }

            html.Append("</ul><table><tr><th>Screen</th><th>Theme</th><th>Size</th><th>Scale</th><th>Image</th></tr>");
            foreach ((string screen, string theme, string size, int scale, string? file, string? error) in Rows)
            {
                html.Append("<tr><td>").Append(screen).Append("</td><td>").Append(theme).Append("</td><td>").Append(size)
                    .Append("</td><td>").Append(scale).Append("%</td>");
                html.Append(file is not null
                    ? $"<td><a href=\"{file}\"><img src=\"{file}\" alt=\"{screen}\"></a></td>"
                    : $"<td class=\"failed\">失敗: {WebUtility.HtmlEncode(error)}</td>");
                html.Append("</tr>");
            }

            html.Append("</table></body></html>");
            File.WriteAllText(Path.Combine(folder, "index.html"), html.ToString());
        }
    }

    private static async Task<Shots> CaptureLanguageAsync(string language)
    {
        string folder = Directory.CreateDirectory(Path.Combine(OutputRoot, language)).FullName;
        var shots = new Shots(language, folder);
        bool pseudo = language.StartsWith("qps-", StringComparison.Ordinal);
        IReadOnlyList<(string, Regex)> keys = TrimReport.Keys(language);
        foreach (int scale in Scales)
        {
            foreach (string theme in Themes)
            {
                foreach ((int width, int height) in Sizes)
                {
                    string size = $"{width}x{height}";
                    await using UiTestContext ctx = UiTestContext.Create($"shots-{language}-{theme}-{size}-{scale}");
                    string profile = ctx.NewProfile();
                    await File.WriteAllTextAsync(Path.Combine(profile, "settings.json"),
                        $"{{\"$schemaVersion\": 1, \"ui.theme\": \"{theme}\", \"view.zoom.ui\": {scale}}}");
                    var done = new HashSet<string>();
                    try
                    {
                        AppSession app = await ctx.StartAsync(new AppOptions
                        {
                            Profile = profile,
                            UiLanguage = pseudo ? null : language,
                            ExtraArgs = pseudo ? ["--pseudo-locale", language] : [],
                        });
                        await foreach ((string screen, string? error) in MainScreens.ShowAllAsync(app, ctx, width, height, zoom: scale / 100.0))
                        {
                            done.Add(screen);
                            if (error is not null)
                            {
                                // 撮影に失敗した画面は一覧に「失敗」と記録し、続ける (UI-47 の「エラー」)。
                                shots.Rows.Add((screen, theme, size, scale, null, error));
                                continue;
                            }

                            string file = $"{screen}-{theme}-{size}-{scale}.png";
                            app.Screenshot().SavePng(Path.Combine(folder, file));
                            shots.Rows.Add((screen, theme, size, scale, file, null));
                            foreach (string line in TrimReport.Describe(await app.TextCheckAsync(), screen, size, keys))
                            {
                                shots.Clipped.Add($"{theme} {scale}% {line}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
                    {
                        // 起動できない・アプリが止まった: 残りの画面を「失敗」と記録し、続ける。
                        foreach (string screen in MainScreens.AllNames.Where(s => !done.Contains(s)))
                        {
                            shots.Rows.Add((screen, theme, size, scale, null, ex.Message));
                        }
                    }
                }
            }
        }

        shots.Write();
        return shots;
    }
}
