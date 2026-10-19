using System.Globalization;
using System.Net;
using System.Text;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>表の添付 CSV の書き出し。先頭の <see cref="ReportTable.MaxRows"/> 行を覚えておき、超えた時点で CSV を作って続ける。</summary>
internal sealed class TableAttachment : IDisposable
{
    private readonly ReportTable _table;
    private readonly string _folder;
    private readonly string _prefix;
    private readonly List<IReadOnlyList<string>> _head = [];
    private StreamWriter? _writer;

    public TableAttachment(ReportTable table, string folder, string prefix)
    {
        _table = table;
        _folder = folder;
        _prefix = prefix;
    }

    public string? Path { get; private set; }

    public long Rows { get; private set; }

    /// <summary>行を渡す (本文に入れる行は 1 行目から <see cref="ReportTable.MaxRows"/> 行目まで)。</summary>
    public void Add(IReadOnlyList<string> row)
    {
        Rows++;
        if (_table.AttachmentName is null || _table.AttachmentSource is not null)
        {
            return;
        }

        if (_writer is null)
        {
            if (Rows <= _table.MaxRows)
            {
                _head.Add(row);
                return;
            }

            Path = System.IO.Path.Combine(_folder, _prefix + _table.AttachmentName);
            _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            WriteRow(_table.Columns);
            foreach (IReadOnlyList<string> r in _head)
            {
                WriteRow(r);
            }

            _head.Clear();
        }

        WriteRow(row);
    }

    private void WriteRow(IReadOnlyList<string> row) =>
        _writer!.Write(string.Join(',', row.Select(Audit.AuditExporter.CsvEscape)) + "\r\n");

    /// <summary>終える。既存の CSV の添付 (マニフェスト) は超えたときだけコピーする。</summary>
    public void Complete()
    {
        _writer?.Dispose();
        _writer = null;
        if (_table.AttachmentSource is { } source && _table.AttachmentName is { } name && Rows > _table.MaxRows)
        {
            Path = System.IO.Path.Combine(_folder, _prefix + name);
            File.Copy(source, Path, overwrite: true);
        }
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>
/// HTML のレポート (FOR-33 の仕様 2)。1 つのファイル: CSS を埋め込み、外部の参照を持たない。配置図は SVG で埋め込む。逐次書き出して、
/// 監査ログのような大きな表もメモリに溜めない。
/// </summary>
internal static class ReportHtml
{
    private static readonly string[] Palette = ["#4e79a7", "#f28e2b", "#59a14f", "#b07aa1", "#e15759", "#76b7b2", "#edc948", "#9c755f"];

    private const string Css = """
        :root{color-scheme:light dark}
        body{margin:0;font:14px/1.5 "Segoe UI",system-ui,sans-serif;color:#1b1b1b;background:#fff}
        main{max-width:1200px;margin:0 auto;padding:24px}
        header.report{display:flex;align-items:center;gap:16px;border-bottom:2px solid #333;margin-bottom:16px;padding-bottom:8px}
        header.report img{max-height:56px}
        h1{font-size:26px;margin:0}
        h2{font-size:20px;margin:32px 0 8px;border-bottom:1px solid #888;padding-bottom:4px}
        h3{font-size:15px;margin:16px 0 4px}
        nav ul{list-style:none;padding:0;margin:0 0 16px;display:flex;flex-wrap:wrap;gap:4px 16px}
        table{border-collapse:collapse;width:100%;font-size:12px;margin:8px 0}
        th,td{border:1px solid #888;padding:4px 6px;text-align:left;vertical-align:top;overflow-wrap:anywhere}
        th{background:#e8e8e8;color:#111}
        table.kv th{width:22%;font-weight:600}
        pre{font:12px Consolas,"Cascadia Mono",monospace;background:#f3f3f3;color:#111;padding:8px;overflow:auto;border:1px solid #bbb}
        .note{color:#444;font-style:italic}
        svg{max-width:100%;height:auto}
        @media (prefers-color-scheme:dark){body{color:#eaeaea;background:#1b1b1b}th{background:#333;color:#eee}pre{background:#252525;color:#eaeaea;border-color:#555}.note{color:#bbb}a{color:#8ab4f8}}
        @media print{body{background:#fff;color:#000}main{max-width:none;padding:0}h2{break-after:avoid}tr{break-inside:avoid}}
        """;

    public static string Escape(string text) => WebUtility.HtmlEncode(text);

    private static string Multiline(string text) => Escape(text).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    public static void Write(TextWriter w, ReportContext context, IEnumerable<ReportSection> sections, string attachmentFolder, string attachmentPrefix,
        List<string> attachments, CancellationToken token)
    {
        ReportInput input = context.Input;
        ReportOptions o = context.Options;
        w.Write("<!DOCTYPE html>\n<html lang=\"" + Escape(o.Language) + "\">\n<head>\n<meta charset=\"utf-8\">\n");
        w.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        w.Write("<title>" + Escape($"{context.T("Title")} - {input.Case.Number}") + "</title>\n<style>\n" + Css + "\n</style>\n</head>\n<body>\n<main>\n");

        w.Write("<header class=\"report\">");
        if (LogoDataUri(o.LogoPath) is { } logo)
        {
            w.Write("<img alt=\"\" src=\"" + logo + "\">");
        }

        w.Write("<div><h1>" + Escape(context.T("Title")) + "</h1>");
        if (!string.IsNullOrWhiteSpace(o.HeaderText))
        {
            w.Write("<div>" + Escape(o.HeaderText) + "</div>");
        }

        w.Write($"<div class=\"note\">{Escape(context.T("Case"))} {Escape(input.Case.Number)} - {Escape(input.Case.Name)}</div></div></header>\n");

        // 節は逐次生成されるので、目次は先に見出しだけ集められない。節の ID は固定なので、含める節の ID から目次を作る。
        List<ReportSection> materialized = [];
        foreach (ReportSection section in sections)
        {
            token.ThrowIfCancellationRequested();
            materialized.Add(section);
        }

        w.Write("<nav><ul>");
        foreach (ReportSection s in materialized)
        {
            w.Write($"<li><a href=\"#section-{Escape(s.Id)}\">{Escape(s.Title)}</a></li>");
        }

        w.Write("</ul></nav>\n");
        foreach (ReportSection s in materialized)
        {
            token.ThrowIfCancellationRequested();
            w.Write($"<section id=\"section-{Escape(s.Id)}\">\n<h2>{Escape(s.Title)}</h2>\n");
            foreach (ReportBlock block in s.Blocks)
            {
                WriteBlock(w, context, block, attachmentFolder, attachmentPrefix, attachments, token);
            }

            w.Write("</section>\n");
        }

        w.Write("</main>\n</body>\n</html>\n");
    }

    private static string? LogoDataUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            FileInfo info = new(path);
            if (info.Length > 2 * 1024 * 1024)
            {
                return null;
            }

            string mime = info.Extension.ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".gif" => "image/gif",
                _ => string.Empty,
            };
            return mime.Length == 0 ? null : $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteBlock(TextWriter w, ReportContext context, ReportBlock block, string attachmentFolder, string attachmentPrefix, List<string> attachments,
        CancellationToken token)
    {
        switch (block)
        {
            case ReportParagraph p:
                w.Write("<p>" + Multiline(p.Text) + "</p>\n");
                break;
            case ReportKeyValues kv:
                w.Write("<table class=\"kv\">\n");
                foreach (KeyValuePair<string, string> item in kv.Items)
                {
                    w.Write($"<tr><th scope=\"row\">{Escape(item.Key)}</th><td>{Multiline(item.Value)}</td></tr>\n");
                }

                w.Write("</table>\n");
                break;
            case ReportTable table:
                WriteTable(w, context, table, attachmentFolder, attachmentPrefix, attachments, token);
                break;
            case ReportDump dump:
                w.Write($"<h3>{Escape(dump.Title)}</h3>\n<pre>{Escape(HexDump.Format(dump.Offset, dump.Data))}</pre>\n");
                break;
            case ReportDiagram diagram:
                WriteDiagram(w, context, diagram);
                break;
        }
    }

    private static void WriteTable(TextWriter w, ReportContext context, ReportTable table, string attachmentFolder, string attachmentPrefix, List<string> attachments,
        CancellationToken token)
    {
        w.Write("<table>\n<thead><tr>");
        foreach (string column in table.Columns)
        {
            w.Write($"<th scope=\"col\">{Escape(column)}</th>");
        }

        w.Write("</tr></thead>\n<tbody>\n");
        using var attachment = new TableAttachment(table, attachmentFolder, attachmentPrefix);
        long rows = 0;
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            if ((++rows & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            attachment.Add(row);
            if (rows > table.MaxRows)
            {
                continue;
            }

            w.Write("<tr>");
            foreach (string cell in row)
            {
                w.Write("<td>" + Multiline(cell) + "</td>");
            }

            w.Write("</tr>\n");
        }

        w.Write("</tbody>\n</table>\n");
        attachment.Complete();
        if (attachment.Path is { } path)
        {
            attachments.Add(path);
            w.Write("<p class=\"note\">" + Escape(string.Format(CultureInfo.InvariantCulture, context.T("Truncated"), table.MaxRows, rows)) + " "
                + Escape(Path.GetFileName(path)) + "</p>\n");
        }
        else if (rows == 0)
        {
            w.Write("<p class=\"note\">" + Escape(context.T("NoData")) + "</p>\n");
        }
    }

    private static void WriteDiagram(TextWriter w, ReportContext context, ReportDiagram diagram)
    {
        const double Width = 900, Height = 70;
        w.Write($"<h3>{Escape(diagram.Title)}</h3>\n");
        w.Write(FormattableString.Invariant($"<svg viewBox=\"0 0 {Width} {Height + 24}\" role=\"img\" aria-label=\"{Escape(diagram.Title)}\">\n"));
        w.Write(FormattableString.Invariant($"<rect x=\"0\" y=\"0\" width=\"{Width}\" height=\"{Height}\" fill=\"#f3f3f3\" stroke=\"#777\"/>\n"));
        int i = 0;
        foreach (DiagramSegment s in diagram.Segments)
        {
            double x = diagram.TotalLength <= 0 ? 0 : (double)s.Start / diagram.TotalLength * Width;
            double len = diagram.TotalLength <= 0 ? 0 : Math.Max(2, (double)s.Length / diagram.TotalLength * Width);
            string color = Palette[Math.Abs(s.Color == 0 ? i : s.Color) % Palette.Length];
            w.Write(FormattableString.Invariant($"<rect x=\"{x:0.##}\" y=\"4\" width=\"{len:0.##}\" height=\"{Height - 8}\" fill=\"{color}\" stroke=\"#222\"><title>{Escape(s.Label)}</title></rect>\n"));
            if (len > 40)
            {
                w.Write(FormattableString.Invariant($"<text x=\"{x + 4:0.##}\" y=\"{Height / 2 + 4}\" font-size=\"12\" fill=\"#fff\" stroke=\"#000\" stroke-width=\"0.3\">{Escape(s.Label)}</text>\n"));
            }

            i++;
        }

        w.Write("</svg>\n");
        w.Write("<table>\n<tbody>\n");
        foreach (DiagramSegment s in diagram.Segments)
        {
            w.Write($"<tr><td>{Escape(s.Label)}</td><td>{Escape(ReportSectionsBuilder.OffsetText(s.Start))}</td><td>{s.Length.ToString(CultureInfo.InvariantCulture)}</td></tr>\n");
        }

        w.Write("</tbody>\n</table>\n");
        _ = context;
    }
}

/// <summary>Hex ダンプの書式 (16 バイトずつ: オフセット、Hex、ASCII)。</summary>
public static class HexDump
{
    public static string Format(long offset, ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append((offset + i).ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            int n = Math.Min(16, data.Length - i);
            for (int k = 0; k < 16; k++)
            {
                sb.Append(k < n ? data[i + k].ToString("X2", CultureInfo.InvariantCulture) : "  ").Append(k == 7 ? "  " : " ");
            }

            sb.Append(' ');
            for (int k = 0; k < n; k++)
            {
                byte b = data[i + k];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }
}
