using HexEditor.Core.Bookmarks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// コメントの Markdown (INSP-24 の仕様 5) を RichTextBlock に描く。見出し・強調・リスト・コード・表・リンク。画像と HTML は
/// 解釈せず文字列のまま。リンクを押したら <paramref name="linkClicked"/> にリンク先を渡す (開くかどうかは呼び出し側が決める)。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    public static void Render(RichTextBlock target, string markdown, Action<string> linkClicked)
    {
        target.Blocks.Clear();
        foreach (MarkdownBlock block in MarkdownLite.Parse(markdown))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = block.Level switch { 1 => 20, 2 => 17, _ => 15 };
                    AddInlines(paragraph.Inlines, block.Inlines, linkClicked);
                    break;
                case MarkdownBlockKind.BulletItem:
                    paragraph.Margin = new Thickness(12, 0, 0, 2);
                    paragraph.Inlines.Add(new Run { Text = "• " });
                    AddInlines(paragraph.Inlines, block.Inlines, linkClicked);
                    break;
                case MarkdownBlockKind.NumberedItem:
                    paragraph.Margin = new Thickness(12, 0, 0, 2);
                    paragraph.Inlines.Add(new Run { Text = block.Level + ". " });
                    AddInlines(paragraph.Inlines, block.Inlines, linkClicked);
                    break;
                case MarkdownBlockKind.CodeBlock:
                    paragraph.FontFamily = Mono;
                    paragraph.Inlines.Add(new Run { Text = block.Code ?? string.Empty });
                    break;
                case MarkdownBlockKind.Table:
                    // 表は列を「|」で区切った行として描く (見出しの行は太字)。
                    paragraph.FontFamily = Mono;
                    for (int r = 0; r < block.Rows!.Count; r++)
                    {
                        if (r > 0)
                        {
                            paragraph.Inlines.Add(new LineBreak());
                        }

                        var row = new Span();
                        if (r == 0)
                        {
                            row.FontWeight = FontWeights.SemiBold;
                        }

                        for (int c = 0; c < block.Rows[r].Count; c++)
                        {
                            if (c > 0)
                            {
                                row.Inlines.Add(new Run { Text = " | " });
                            }

                            AddInlines(row.Inlines, block.Rows[r][c], linkClicked);
                        }

                        paragraph.Inlines.Add(row);
                    }

                    break;
                default:
                    AddInlines(paragraph.Inlines, block.Inlines, linkClicked);
                    break;
            }

            target.Blocks.Add(paragraph);
        }
    }

    private static void AddInlines(InlineCollection target, IReadOnlyList<MarkdownInline> inlines, Action<string> linkClicked)
    {
        foreach (MarkdownInline inline in inlines)
        {
            switch (inline.Kind)
            {
                case MarkdownInlineKind.Text:
                    target.Add(new Run { Text = inline.Text });
                    break;
                case MarkdownInlineKind.LineBreak:
                    target.Add(new LineBreak());
                    break;
                case MarkdownInlineKind.Code:
                    target.Add(new Run { Text = inline.Text, FontFamily = Mono });
                    break;
                case MarkdownInlineKind.Strong:
                {
                    var bold = new Bold();
                    AddInlines(bold.Inlines, inline.Children, linkClicked);
                    target.Add(bold);
                    break;
                }

                case MarkdownInlineKind.Emphasis:
                {
                    var italic = new Italic();
                    AddInlines(italic.Inlines, inline.Children, linkClicked);
                    target.Add(italic);
                    break;
                }

                case MarkdownInlineKind.Link:
                {
                    var link = new Hyperlink();
                    string url = inline.Url ?? string.Empty;
                    AddInlines(link.Inlines, inline.Children, linkClicked);
                    link.Click += (_, _) => linkClicked(url);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, inline.PlainText);
                    target.Add(link);
                    break;
                }
            }
        }
    }

    /// <summary>描いた内容の文字列と太字の範囲 (テスト用の読み出し)。</summary>
    public static IEnumerable<(string Text, bool Bold, bool Link)> Runs(RichTextBlock block)
    {
        foreach (Block b in block.Blocks)
        {
            if (b is Paragraph p)
            {
                foreach ((string, bool, bool) run in Runs(p.Inlines, p.FontWeight.Weight >= 600, false))
                {
                    yield return run;
                }

                yield return ("\n", false, false);
            }
        }
    }

    private static IEnumerable<(string, bool, bool)> Runs(InlineCollection inlines, bool bold, bool link)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return (run.Text, bold || run.FontWeight.Weight >= 600, link);
                    break;
                case LineBreak:
                    yield return ("\n", false, link);
                    break;
                case Span span:
                    foreach ((string, bool, bool) r in Runs(span.Inlines, bold || span is Bold || span.FontWeight.Weight >= 600, link || span is Hyperlink))
                    {
                        yield return r;
                    }

                    break;
            }
        }
    }
}
