using System.Text;

namespace HexEditor.Core.Bookmarks;

/// <summary>Markdown の行内の要素の種類。</summary>
public enum MarkdownInlineKind
{
    Text,
    Strong,
    Emphasis,
    Code,
    Link,
    LineBreak,
}

/// <summary>行内の要素。<see cref="Children"/> は強調・リンクの中身、<see cref="Url"/> はリンク先。</summary>
public sealed record MarkdownInline(MarkdownInlineKind Kind, string Text, IReadOnlyList<MarkdownInline> Children, string? Url = null)
{
    public static MarkdownInline Plain(string text) => new(MarkdownInlineKind.Text, text, []);

    /// <summary>表示される文字列 (記号を除く)。</summary>
    public string PlainText => Kind switch
    {
        MarkdownInlineKind.Text or MarkdownInlineKind.Code => Text,
        MarkdownInlineKind.LineBreak => "\n",
        _ => string.Concat(Children.Select(c => c.PlainText)),
    };
}

/// <summary>ブロックの種類。</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    BulletItem,
    NumberedItem,
    CodeBlock,
    Table,
}

/// <summary>
/// ブロック 1 つ。見出しは <see cref="Level"/> (1〜6)、番号付きの項目は <see cref="Level"/> が番号。表は <see cref="Rows"/>
/// (先頭が見出しの行)。コードのブロックは <see cref="Code"/>。
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownInline> Inlines, int Level = 0, string? Code = null,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownInline>>>? Rows = null);

/// <summary>
/// ブックマークのコメントの Markdown (CommonMark の一部。INSP-24 の仕様 5)。見出し、強調、リスト、コード (行内・ブロック)、表、
/// リンクを解釈する。画像と HTML は解釈せず、文字列のまま表示する。
/// </summary>
public static class MarkdownLite
{
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                var inlines = new List<MarkdownInline>();
                for (int i = 0; i < paragraph.Count; i++)
                {
                    if (i > 0)
                    {
                        inlines.Add(new MarkdownInline(MarkdownInlineKind.LineBreak, "\n", []));
                    }

                    inlines.AddRange(ParseInlines(paragraph[i].Trim()));
                }

                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, inlines));
                paragraph.Clear();
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                string fence = trimmed[..3];
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Append(code.Length > 0 ? "\n" : string.Empty).Append(lines[i]);
                    i++;
                }

                blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, [], Code: code.ToString()));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            int hashes = trimmed.TakeWhile(c => c == '#').Count();
            if (hashes is >= 1 and <= 6 && (trimmed.Length == hashes || trimmed[hashes] == ' '))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(trimmed[hashes..].Trim().TrimEnd('#').Trim()), hashes));
                continue;
            }

            if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.BulletItem, ParseInlines(trimmed[2..].Trim())));
                continue;
            }

            int digits = trimmed.TakeWhile(char.IsAsciiDigit).Count();
            if (digits is > 0 and < 10 && trimmed.Length > digits + 1 && trimmed[digits] is '.' or ')' && trimmed[digits + 1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.NumberedItem, ParseInlines(trimmed[(digits + 2)..].Trim()),
                    int.Parse(trimmed[..digits], System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            if (trimmed.Contains('|') && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownInline>>> { Cells(trimmed) };
                i += 2;
                while (i < lines.Length && lines[i].Contains('|') && lines[i].Trim().Length > 0)
                {
                    rows.Add(Cells(lines[i].Trim()));
                    i++;
                }

                i--;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, [], Rows: rows));
                continue;
            }

            paragraph.Add(line);
        }

        FlushParagraph();
        return blocks;
    }

    private static bool IsTableSeparator(string line)
    {
        string t = line.Trim().Trim('|');
        return t.Length > 0 && t.Contains('-') && t.All(c => c is '-' or ':' or '|' or ' ');
    }

    private static IReadOnlyList<IReadOnlyList<MarkdownInline>> Cells(string line)
    {
        string t = line.Trim();
        if (t.StartsWith('|'))
        {
            t = t[1..];
        }

        if (t.EndsWith('|'))
        {
            t = t[..^1];
        }

        return [.. t.Split('|').Select(c => (IReadOnlyList<MarkdownInline>)ParseInlines(c.Trim()))];
    }

    /// <summary>行内の要素: <c>`code`</c>、<c>**強調**</c> / <c>__強調__</c>、<c>*斜体*</c> / <c>_斜体_</c>、<c>[文字](URL)</c>、<c>\</c> の退避。</summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length > 0)
            {
                result.Add(MarkdownInline.Plain(plain.ToString()));
                plain.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && char.IsAsciiLetterOrDigit(text[i + 1]) is false && !char.IsWhiteSpace(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    result.Add(new MarkdownInline(MarkdownInlineKind.Code, text[(i + 1)..close], []));
                    i = close + 1;
                    continue;
                }
            }

            // 語の中の _ (bm_name など) は強調にしない (CommonMark と同じ)。
            bool canOpen = c == '*' || c == '_' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]));
            if (canOpen && i + 1 < text.Length && text[i + 1] == c)
            {
                string marker = new(c, 2);
                int close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush();
                    result.Add(new MarkdownInline(MarkdownInlineKind.Strong, string.Empty, ParseInlines(text[(i + 2)..close])));
                    i = close + 2;
                    continue;
                }
            }

            if (canOpen && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                int close = text.IndexOf(c, i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]))
                {
                    Flush();
                    result.Add(new MarkdownInline(MarkdownInlineKind.Emphasis, string.Empty, ParseInlines(text[(i + 1)..close])));
                    i = close + 1;
                    continue;
                }
            }

            // 画像 (![...](...)) は解釈しない: 「!」を文字として残し、続くリンクだけを解釈しないよう、そのまま文字列にする。
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int end = LinkEnd(text, i + 1, out _, out _);
                if (end > 0)
                {
                    plain.Append(text[i..end]);
                    i = end;
                    continue;
                }
            }

            if (c == '[')
            {
                int end = LinkEnd(text, i, out string label, out string url);
                if (end > 0)
                {
                    Flush();
                    result.Add(new MarkdownInline(MarkdownInlineKind.Link, string.Empty, ParseInlines(label), url));
                    i = end;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    /// <summary><c>[label](url)</c> の終わりの位置。リンクでなければ -1。</summary>
    private static int LinkEnd(string text, int open, out string label, out string url)
    {
        label = url = string.Empty;
        int close = text.IndexOf(']', open + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
        {
            return -1;
        }

        int paren = text.IndexOf(')', close + 2);
        if (paren < 0)
        {
            return -1;
        }

        label = text[(open + 1)..close];
        url = text[(close + 2)..paren].Trim();
        return paren + 1;
    }

    /// <summary>リンク先の種類 (INSP-24 の仕様 5)。</summary>
    public enum LinkKind
    {
        /// <summary>開かない (http / https 以外のスキームなど)。</summary>
        None,

        /// <summary>ドキュメントの中の位置 (<c>#0x1F00</c>、<c>#bm.header</c>)。<c>#</c> の後ろを入力式として評価する。</summary>
        Document,

        /// <summary>既定のブラウザで開く (http / https)。</summary>
        Web,
    }

    public static LinkKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return LinkKind.None;
        }

        if (url.StartsWith('#') && url.Length > 1)
        {
            return LinkKind.Document;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? LinkKind.Web
            : LinkKind.None;
    }
}
