using System.Globalization;
using System.Net;
using System.Text;

namespace HexEditor.Core.Bookmarks;

/// <summary>
/// 位置マネージャの内容の書き出し (INSP-31 の仕様 6): ブックマークを開始オフセット順に、見出し = 名前と範囲、本文 = コメントで並べる。
/// Markdown と HTML (コメントの Markdown を HTML にしたもの) に書ける。
/// </summary>
public static class PositionNotes
{
    /// <summary>範囲の表記 (<c>0x100–0x10F (16 bytes)</c>)。<paramref name="format"/> は「{0}–{1} ({2} バイト)」の形の文。</summary>
    public static string RangeText(Bookmark b, string format)
    {
        long last = b.Start + Math.Max(1, b.Length) - 1;
        return string.Format(CultureInfo.InvariantCulture, format, Hex(b.Start), Hex(last), b.Length);
    }

    private static string Hex(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Markdown (見出しは <c>## 名前 — 範囲</c>、その後に空行とコメント)。</summary>
    public static string ToMarkdown(IEnumerable<Bookmark> bookmarks, string title, string rangeFormat)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(title).Append("\n\n");
        foreach (Bookmark b in bookmarks.OrderBy(b => b.Start))
        {
            text.Append("## ").Append(EscapeHeading(b.Name)).Append(" — ").Append(RangeText(b, rangeFormat)).Append("\n\n");
            string comment = b.Comment.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
            if (comment.Length > 0)
            {
                text.Append(comment).Append("\n\n");
            }
        }

        return text.ToString();
    }

    private static string EscapeHeading(string name) => name.Replace("\n", " ", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal);

    /// <summary>HTML (UTF-8。コメントは Markdown を HTML にする。画像と HTML は文字列のまま)。</summary>
    public static string ToHtml(IEnumerable<Bookmark> bookmarks, string title, string rangeFormat)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n")
            .Append("<style>body{font-family:sans-serif;max-width:60em;margin:2em auto;}code,pre{font-family:Consolas,monospace;}table{border-collapse:collapse;}td,th{border:1px solid #888;padding:2px 6px;}</style>\n")
            .Append("</head>\n<body>\n<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1>\n");
        foreach (Bookmark b in bookmarks.OrderBy(b => b.Start))
        {
            html.Append("<h2>").Append(WebUtility.HtmlEncode(b.Name)).Append(" — ").Append(WebUtility.HtmlEncode(RangeText(b, rangeFormat))).Append("</h2>\n");
            AppendMarkdown(html, b.Comment);
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    /// <summary>Markdown の本文を HTML にする (<see cref="MarkdownLite"/> の解釈を使う)。</summary>
    public static void AppendMarkdown(StringBuilder html, string markdown)
    {
        bool bullets = false;
        bool numbers = false;
        void CloseLists()
        {
            if (bullets)
            {
                html.Append("</ul>\n");
                bullets = false;
            }

            if (numbers)
            {
                html.Append("</ol>\n");
                numbers = false;
            }
        }

        foreach (MarkdownBlock block in MarkdownLite.Parse(markdown))
        {
            if (block.Kind != MarkdownBlockKind.BulletItem && bullets || block.Kind != MarkdownBlockKind.NumberedItem && numbers)
            {
                CloseLists();
            }

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                {
                    int level = Math.Clamp(block.Level + 2, 3, 6);
                    html.Append("<h").Append(level).Append('>');
                    AppendInlines(html, block.Inlines);
                    html.Append("</h").Append(level).Append(">\n");
                    break;
                }

                case MarkdownBlockKind.BulletItem:
                    if (!bullets)
                    {
                        html.Append("<ul>\n");
                        bullets = true;
                    }

                    html.Append("<li>");
                    AppendInlines(html, block.Inlines);
                    html.Append("</li>\n");
                    break;
                case MarkdownBlockKind.NumberedItem:
                    if (!numbers)
                    {
                        html.Append("<ol>\n");
                        numbers = true;
                    }

                    html.Append("<li>");
                    AppendInlines(html, block.Inlines);
                    html.Append("</li>\n");
                    break;
                case MarkdownBlockKind.CodeBlock:
                    html.Append("<pre><code>").Append(WebUtility.HtmlEncode(block.Code ?? string.Empty)).Append("</code></pre>\n");
                    break;
                case MarkdownBlockKind.Table when block.Rows is { } rows:
                    html.Append("<table>\n");
                    for (int r = 0; r < rows.Count; r++)
                    {
                        html.Append("<tr>");
                        foreach (IReadOnlyList<MarkdownInline> cell in rows[r])
                        {
                            html.Append(r == 0 ? "<th>" : "<td>");
                            AppendInlines(html, cell);
                            html.Append(r == 0 ? "</th>" : "</td>");
                        }

                        html.Append("</tr>\n");
                    }

                    html.Append("</table>\n");
                    break;
                default:
                    html.Append("<p>");
                    AppendInlines(html, block.Inlines);
                    html.Append("</p>\n");
                    break;
            }
        }

        CloseLists();
    }

    private static void AppendInlines(StringBuilder html, IReadOnlyList<MarkdownInline> inlines)
    {
        foreach (MarkdownInline inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Strong:
                    html.Append("<strong>");
                    AppendInlines(html, inline.Children);
                    html.Append("</strong>");
                    break;
                case MarkdownInlineKind.Emphasis:
                    html.Append("<em>");
                    AppendInlines(html, inline.Children);
                    html.Append("</em>");
                    break;
                case MarkdownInlineKind.Code:
                    html.Append("<code>").Append(WebUtility.HtmlEncode(inline.Text)).Append("</code>");
                    break;
                case MarkdownInlineKind.LineBreak:
                    html.Append("<br>");
                    break;
                case MarkdownInlineKind.Link when MarkdownLite.Classify(inline.Url) == MarkdownLite.LinkKind.Web:
                    html.Append("<a href=\"").Append(WebUtility.HtmlEncode(inline.Url)).Append("\">");
                    AppendInlines(html, inline.Children);
                    html.Append("</a>");
                    break;
                case MarkdownInlineKind.Link:
                    AppendInlines(html, inline.Children);
                    break;
                default:
                    html.Append(WebUtility.HtmlEncode(inline.Text));
                    break;
            }
        }
    }
}
