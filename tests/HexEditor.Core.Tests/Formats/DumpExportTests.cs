using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Formats;
using HexEditor.Core.Tests.Clipboard;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>ダンプのエクスポート (TOOL-10)。</summary>
public sealed partial class DumpExportTests
{
    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    /// <summary>テキストの列の期待値 (表示できない文字は '.')。</summary>
    private static IEnumerable<string> ExpectedText(byte[] data)
    {
        for (int row = 0; row < data.Length; row += 16)
        {
            yield return new string([.. data.Skip(row).Take(16).Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.')]);
        }
    }

    private static DumpOptions Colored() => new()
    {
        Highlights =
        [
            new DumpHighlight(0x20, 1, DumpHighlightKind.Modified),
            new DumpHighlight(0x40, 4, DumpHighlightKind.Bookmark, "bm1"),
        ],
    };

    private static string Dump(string format, byte[] data, DumpOptions? options = null) =>
        Export(data, new ExportOptions { Format = format, Dump = options ?? new DumpOptions() });

    /// <summary>HTML の pre の各行のテキスト (タグを除き、文字参照を戻したもの)。</summary>
    private static string[] HtmlRows(string html)
    {
        string pre = PreBody().Match(html).Groups[1].Value;
        string text = WebUtility.HtmlDecode(Tag().Replace(pre, string.Empty));
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex("<pre[^>]*>(.*)</pre>", RegexOptions.Singleline)]
    private static partial Regex PreBody();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [Fact]
    [Trait(TC, "TC-TOOL-10-01")]
    public void Html_dump_is_a_complete_document_with_aligned_columns()
    {
        string html = Dump(FormatIds.Html, Bytes256, Colored());
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>", html);

        // 等幅フォントで、すべての行の同じ列 (Hex の 1 文字目・テキストの列の縦線) が同じ文字位置にある。
        Assert.Contains("monospace", html);
        string[] rows = HtmlRows(html);
        Assert.Equal(16, rows.Length);
        Assert.Single(rows.Select(r => r.IndexOf('|')).Distinct());
        Assert.Single(rows.Select(r => r.LastIndexOf(" | ", StringComparison.Ordinal)).Distinct());

        // 0x20 は変更バイトの強調 (色に加えて下線)、0x40 はブックマークの名前のツールチップ。
        Assert.Matches("<span class=\"mod\">20</span>", html);
        Assert.Matches(@"\.mod \{[^}]*text-decoration: underline", html);
        Assert.Matches("<span class=\"mark\" title=\"bm1\">40</span>", html);
        Assert.Equal(ExpectedText(Bytes256), rows.Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]));
    }

    [Fact]
    public void Html_dark_scheme_changes_the_colors()
    {
        string light = Dump(FormatIds.Html, Bytes256, Colored());
        string dark = Dump(FormatIds.Html, Bytes256, Colored() with { DarkScheme = true });
        Assert.Contains("background: #FFFFFF", light);
        Assert.Contains("background: #1E1E1E", dark);
    }

    /// <summary>RTF の最小の読み取り: フォントテーブルの最初のフォントと、本文の行 (\par で区切る)。</summary>
    private static (string Font, string[] Rows) ParseRtf(string rtf)
    {
        Match font = RtfFont().Match(rtf);
        int body = rtf.IndexOf("\\fs", StringComparison.Ordinal);
        body = rtf.IndexOf(' ', body) + 1;
        var sb = new StringBuilder();
        for (int i = body; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c is '{' or '}' or '\r' or '\n')
            {
                continue;
            }

            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            char next = rtf[i + 1];
            if (next is '\\' or '{' or '}')
            {
                sb.Append(next);
                i++;
                continue;
            }

            Match word = RtfWord().Match(rtf, i);
            string name = word.Groups[1].Value;
            if (name == "par")
            {
                sb.Append('\n');
            }
            else if (name == "u")
            {
                sb.Append((char)short.Parse(word.Groups[2].Value));
                i += word.Length; // '?' を飛ばす
                continue;
            }

            i += word.Length - 1;
        }

        return (font.Groups[1].Value, sb.ToString().Split('\n'));
    }

    [GeneratedRegex(@"\\fonttbl\{\\f0[^ ]* ([^;]+);")]
    private static partial Regex RtfFont();

    [GeneratedRegex(@"\\([a-z]+)(-?\d+)? ?")]
    private static partial Regex RtfWord();

    [Fact]
    [Trait(TC, "TC-TOOL-10-02")]
    public void Rtf_dump_uses_a_monospace_font_and_equal_rows()
    {
        string rtf = Dump(FormatIds.Rtf, Bytes256, Colored());
        (string font, string[] rows) = ParseRtf(rtf);
        Assert.Equal("Cascadia Mono", font);
        Assert.Equal(16, rows.Length);
        Assert.Single(rows.Select(r => r.Length).Distinct());
        Assert.Equal(ExpectedText(Bytes256), rows.Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]));
    }

    [RequiresToolFact("soffice")]
    [Trait("Category", "Integration")]
    [Trait(TC, "TC-TOOL-10-02")]
    public void LibreOffice_converts_the_rtf_dump()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "rtf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "out.rtf"), Dump(FormatIds.Rtf, Bytes256, Colored()));
        Run("soffice", "--headless --convert-to txt:Text out.rtf", dir);
        string[] lines = File.ReadAllLines(Path.Combine(dir, "out.txt")).Where(l => l.Length > 0).ToArray();
        Assert.Equal(16, lines.Length);
        Assert.Single(lines.Select(l => l.Length).Distinct());
    }

    [Fact]
    [Trait(TC, "TC-TOOL-10-05")]
    public void Special_characters_do_not_break_any_format()
    {
        byte[] special = TestDataCatalog.SpecialText();
        foreach (byte[] data in new[] { special, Bytes256 })
        {
            string[] expected = [.. ExpectedText(data)];

            // テキスト・Markdown のコードブロック・TeX の verbatim: そのまま。
            foreach ((string format, DumpOptions options) in new[]
            {
                (FormatIds.DumpText, new DumpOptions()),
                (FormatIds.Markdown, new DumpOptions()),
                (FormatIds.Tex, new DumpOptions()),
            })
            {
                string[] rows = Dump(format, data, options).Split("\r\n").Where(l => l.Length > 0 && char.IsAsciiHexDigit(l[0])).ToArray();
                Assert.Equal(expected, rows.Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]));
            }

            // HTML: 文字参照を戻すと元の文字。
            Assert.Equal(expected, HtmlRows(Dump(FormatIds.Html, data)).Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]));

            // RTF: \ { } と ASCII 以外はエスケープ。
            Assert.Equal(expected, ParseRtf(Dump(FormatIds.Rtf, data)).Rows.Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]));

            // TeX の alltt: \ { } だけが特別。
            string alltt = Dump(FormatIds.Tex, data, new DumpOptions { Tex = TexEnvironment.AlltColor });
            string[] alltRows = alltt.Split("\r\n").Where(l => l.Length > 0 && char.IsAsciiHexDigit(l[0])).ToArray();
            Assert.Equal(expected, alltRows.Select(r => r[(r.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..]
                .Replace("\\textbackslash{}", "\\").Replace("\\{", "{").Replace("\\}", "}")));

            // Markdown の表: 縦線は列の区切りにならず、記号はエスケープを戻すと元の文字 (CommonMark のバックスラッシュのエスケープ)。
            string table = Dump(FormatIds.Markdown, data, new DumpOptions { MarkdownTable = true });
            string[] cells = table.Split("\r\n").Skip(2).Where(l => l.Length > 0).Select(l => MarkdownCells(l)[^1]).ToArray();
            Assert.Equal(expected, cells);
        }
    }

    /// <summary>GFM の表の行をセルに分ける (エスケープしていない縦線で区切り、バックスラッシュのエスケープと文字参照を戻す)。</summary>
    private static string[] MarkdownCells(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        string body = line.Trim().Trim('|');
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length && char.IsAsciiLetterOrDigit(body[i + 1]) == false)
            {
                sb.Append(body[++i]);
            }
            else if (body[i] == '|')
            {
                cells.Add(WebUtility.HtmlDecode(sb.ToString().Trim()));
                sb.Clear();
            }
            else
            {
                sb.Append(body[i]);
            }
        }

        cells.Add(WebUtility.HtmlDecode(sb.ToString().Trim()));
        return [.. cells];
    }

    [Fact]
    public void Columns_radix_and_grouping_follow_the_options()
    {
        var options = new DumpOptions { ShowText = false, OffsetRadix = Core.View.OffsetRadix.Decimal, GroupSize = 4, UpperCase = false, BaseAddress = 0x100 };
        string text = Dump(FormatIds.DumpText, [0xAB, 0xCD, 0xEF, 0x01, 0x23], options);
        Assert.Equal("00000256 | ab cd ef 01  23\r\n", text);
    }

    [RequiresToolFact("pdflatex")]
    [Trait("Category", "Integration")]
    [Trait(TC, "TC-TOOL-10-04")]
    public void Pdflatex_processes_both_tex_variants()
    {
        foreach (TexEnvironment env in Enum.GetValues<TexEnvironment>())
        {
            string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "tex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            byte[] data = (byte[])Bytes256.Clone();
            data[0x20] = 0xAA;
            File.WriteAllText(Path.Combine(dir, "dump.tex"), Dump(FormatIds.Tex, data, Colored() with { Tex = env }));
            File.WriteAllText(Path.Combine(dir, "doc.tex"),
                "\\documentclass{article}\n\\usepackage{alltt}\n\\usepackage{xcolor}\n\\begin{document}\n\\input{dump}\n\\end{document}\n");
            string log = Run("pdflatex", "-interaction=nonstopmode -halt-on-error doc.tex", dir);
            Assert.DoesNotContain("Missing character", log);
            Assert.True(File.Exists(Path.Combine(dir, "doc.pdf")));
        }
    }

    [RequiresToolFact("java")]
    [Trait("Category", "Integration")]
    [Trait(TC, "TC-TOOL-10-01")]
    public void Html_dump_passes_the_nu_html_checker()
    {
        // vnu.jar の場所は環境変数 HEXEDITOR_VNU_JAR で渡す (CI ランナーで用意する)。
        if (Environment.GetEnvironmentVariable("HEXEDITOR_VNU_JAR") is not { Length: > 0 } jar)
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "vnu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (byte[] data in new[] { Bytes256, TestDataCatalog.SpecialText() })
        {
            File.WriteAllText(Path.Combine(dir, "out.html"), Dump(FormatIds.Html, data, Colored()));
            Run("java", $"-jar \"{jar}\" --errors-only out.html", dir);
        }
    }

    private static string Run(string tool, string arguments, string directory)
    {
        var info = new ProcessStartInfo(CopyFormatCompileTests.FindTool(tool)!, arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), $"{tool} が終わりません。");
        Assert.True(process.ExitCode == 0, $"{tool} {arguments}: {stderr.Result}{stdout.Result}");
        return stdout.Result;
    }
}
