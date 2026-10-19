using System.Globalization;
using System.Text;
using HexEditor.Core.Printing;

namespace HexEditor.Core.Forensics.Reports;

/// <summary>PDF を作れない (使える書体がないなど)。</summary>
public sealed class ReportPdfException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// PDF のレポート (FOR-33 の仕様 2)。同じ内容の部品 (<see cref="ReportBlock"/>) を、アプリ自身の PDF 書き出し (<see cref="PdfWriter"/>。TOOL-03 と同じ) で
/// A4 またはレターの縦向きに組む。各ページにケース番号とページ番号を入れる。文字はテキストとして書く (検索・コピーできる)。
/// 表は <see cref="ReportOptions.PdfMaxRows"/> 行までにして、全件は HTML か CSV で出す。
/// </summary>
internal sealed class ReportPdf
{
    private const double Margin = 42;
    private const double TopMargin = 54;
    private const double BottomMargin = 54;

    private abstract record Op;

    private sealed record TextOp(FontFace Face, double Size, double X, double Baseline, string Text, bool Gray = false) : Op;

    private sealed record RectOp(double X, double Y, double Width, double Height, double R, double G, double B) : Op;

    private sealed record LineOp(double X1, double Y1, double X2, double Y2, double Thickness) : Op;

    private readonly ReportContext _context;
    private readonly FontFace _primary;
    private readonly SystemFontCatalog _catalog = SystemFontCatalog.Instance;
    private readonly double _width, _height;
    private readonly List<List<Op>> _pages = [];
    private readonly Dictionary<string, double> _advance = new(StringComparer.Ordinal);
    private List<Op> _page = [];
    private double _y;

    public bool Truncated { get; private set; }

    private ReportPdf(ReportContext context, FontFace primary)
    {
        _context = context;
        _primary = primary;
        (_width, _height) = context.Options.Paper == ReportPaper.Letter ? (612.0, 792.0) : (595.28, 841.89);
        NewPage();
    }

    private double ContentWidth => _width - 2 * Margin;

    private string T(string key) => _context.T(key);

    // ---- 書体と文字幅 ----

    private FontFace FaceFor(int codePoint)
    {
        if (_primary.Font.GlyphIndex(codePoint) != 0)
        {
            return _primary;
        }

        return _catalog.Substitute(codePoint) ?? _primary;
    }

    private double Advance(int codePoint, double size)
    {
        FontFace face = FaceFor(codePoint);
        int gid = face.Font.GlyphIndex(codePoint);
        return face.Font.AdvanceEm(gid == 0 ? Math.Max(1, face.Font.GlyphIndex('0')) : gid) * size;
    }

    private double Measure(string text, double size)
    {
        double w = 0;
        foreach (Rune r in text.EnumerateRunes())
        {
            w += Advance(r.Value, size);
        }

        return w;
    }

    /// <summary>幅に収まるよう折り返す (空白で折り返せなければ文字の途中で折る)。</summary>
    private List<string> Wrap(string text, double width, double size)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var current = new StringBuilder();
            double used = 0;
            int lastSpace = -1;
            foreach (Rune r in paragraph.EnumerateRunes())
            {
                double a = Advance(r.Value, size);
                if (used + a > width && current.Length > 0)
                {
                    if (lastSpace > 0 && r.Value != ' ')
                    {
                        string head = current.ToString(0, lastSpace);
                        string tail = current.ToString(lastSpace + 1, current.Length - lastSpace - 1);
                        lines.Add(head);
                        current.Clear().Append(tail);
                        used = Measure(tail, size);
                    }
                    else
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        used = 0;
                    }

                    lastSpace = -1;
                }

                if (r.Value == ' ')
                {
                    lastSpace = current.Length;
                }

                current.Append(r.ToString());
                used += a;
            }

            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>1 行を書く (書体が変わるところで分けて、続けて並べる)。</summary>
    private void DrawText(double x, double baseline, string text, double size, bool gray = false)
    {
        var run = new StringBuilder();
        FontFace? face = null;
        double cx = x;
        void Flush()
        {
            if (run.Length > 0 && face is not null)
            {
                string s = run.ToString();
                _page.Add(new TextOp(face, size, cx, baseline, s, gray));
                cx += Measure(s, size);
                run.Clear();
            }
        }

        foreach (Rune r in text.EnumerateRunes())
        {
            FontFace f = FaceFor(r.Value);
            if (face is not null && !ReferenceEquals(f, face))
            {
                Flush();
            }

            face = f;
            run.Append(r.ToString());
        }

        Flush();
    }

    // ---- ページ ----

    private void NewPage()
    {
        _page = [];
        _pages.Add(_page);
        _y = TopMargin;
    }

    private void Ensure(double needed)
    {
        if (_y + needed > _height - BottomMargin)
        {
            NewPage();
        }
    }

    private void Heading(string text, double size)
    {
        Ensure(size * 2.6);
        _y += size * 0.8;
        DrawText(Margin, _y + size, text, size);
        _y += size * 1.3;
        _page.Add(new LineOp(Margin, _y, _width - Margin, _y, 0.6));
        _y += 6;
    }

    private void Paragraph(string text, double size = 9, bool gray = false)
    {
        foreach (string line in Wrap(text, ContentWidth, size))
        {
            Ensure(size * 1.5);
            DrawText(Margin, _y + size, line, size, gray);
            _y += size * 1.4;
        }

        _y += 4;
    }

    private void KeyValues(ReportKeyValues kv)
    {
        const double Size = 9;
        double keyWidth = ContentWidth * 0.26;
        foreach (KeyValuePair<string, string> item in kv.Items)
        {
            List<string> key = Wrap(item.Key, keyWidth - 6, Size), value = Wrap(item.Value, ContentWidth - keyWidth - 6, Size);
            int lines = Math.Max(key.Count, value.Count);
            double h = lines * Size * 1.35 + 6;
            Ensure(h);
            _page.Add(new RectOp(Margin, _y, keyWidth, h, 0.91, 0.91, 0.91));
            _page.Add(new LineOp(Margin, _y + h, _width - Margin, _y + h, 0.3));
            for (int i = 0; i < key.Count; i++)
            {
                DrawText(Margin + 3, _y + 3 + Size + i * Size * 1.35, key[i], Size);
            }

            for (int i = 0; i < value.Count; i++)
            {
                DrawText(Margin + keyWidth + 3, _y + 3 + Size + i * Size * 1.35, value[i], Size);
            }

            _y += h;
        }

        _y += 6;
    }

    private void Table(ReportTable table, string attachmentFolder, string attachmentPrefix, List<string> attachments, CancellationToken token)
    {
        const double Size = 7.5;
        int columns = table.Columns.Count;
        int cap = Math.Min(table.MaxRows, _context.Options.PdfMaxRows);
        var rows = new List<IReadOnlyList<string>>();
        using var attachment = new TableAttachment(table, attachmentFolder, attachmentPrefix);
        long total = 0;
        foreach (IReadOnlyList<string> row in table.Rows)
        {
            if ((++total & 0xFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            attachment.Add(row);
            if (rows.Count < cap)
            {
                rows.Add(row);
            }
            else if (table.AttachmentName is null)
            {
                // 表示しない行がある。全件を添付に書く必要がなければ、ここで読むのをやめる。
                break;
            }
        }

        attachment.Complete();
        if (attachment.Path is { } path)
        {
            attachments.Add(path);
        }

        bool truncatedHere = total > rows.Count || (table.TotalRows is { } t && t > rows.Count);
        // 最初の 200 行の長さから列の幅の配分を決める。
        double[] weights = new double[columns];
        for (int c = 0; c < columns; c++)
        {
            int longest = table.Columns[c].Length;
            foreach (IReadOnlyList<string> row in rows.Take(200))
            {
                if (c < row.Count)
                {
                    longest = Math.Max(longest, row[c].Split('\n').Max(l => l.Length));
                }
            }

            weights[c] = Math.Clamp(longest, 4, 48);
        }

        double sum = weights.Sum();
        double[] widths = [.. weights.Select(w => w / sum * ContentWidth)];

        void Header()
        {
            List<string>[] cells = [.. table.Columns.Select((h, i) => Wrap(h, widths[i] - 4, Size))];
            double h2 = cells.Max(c => c.Count) * Size * 1.3 + 5;
            Ensure(h2 + Size * 3);
            _page.Add(new RectOp(Margin, _y, ContentWidth, h2, 0.86, 0.86, 0.86));
            double x = Margin;
            for (int c = 0; c < columns; c++)
            {
                for (int i = 0; i < cells[c].Count; i++)
                {
                    DrawText(x + 2, _y + 2.5 + Size + i * Size * 1.3, cells[c][i], Size);
                }

                x += widths[c];
            }

            _y += h2;
        }

        Header();
        foreach (IReadOnlyList<string> row in rows)
        {
            token.ThrowIfCancellationRequested();
            List<string>[] cells = new List<string>[columns];
            double lines = 1;
            for (int c = 0; c < columns; c++)
            {
                cells[c] = Wrap(c < row.Count ? row[c] : string.Empty, widths[c] - 4, Size);
                lines = Math.Max(lines, cells[c].Count);
            }

            double h = lines * Size * 1.3 + 4;
            if (_y + h > _height - BottomMargin)
            {
                NewPage();
                Header();
            }

            double x = Margin;
            for (int c = 0; c < columns; c++)
            {
                for (int i = 0; i < cells[c].Count; i++)
                {
                    DrawText(x + 2, _y + 2 + Size + i * Size * 1.3, cells[c][i], Size);
                }

                x += widths[c];
            }

            _y += h;
            _page.Add(new LineOp(Margin, _y, _width - Margin, _y, 0.25));
        }

        _y += 4;
        if (rows.Count == 0)
        {
            Paragraph(T("NoData"), 8, gray: true);
        }

        if (truncatedHere)
        {
            Truncated = true;
            Paragraph(string.Format(CultureInfo.InvariantCulture, T("TruncatedPdf"), rows.Count), 8, gray: true);
        }
    }

    private void Dump(ReportDump dump)
    {
        const double Size = 7.5;
        Heading(dump.Title, 10);
        string text = HexDump.Format(dump.Offset, dump.Data);
        foreach (string line in text.Split('\n'))
        {
            Ensure(Size * 1.4);
            DrawText(Margin, _y + Size, line, Size);
            _y += Size * 1.3;
        }

        _y += 6;
    }

    private static readonly (double R, double G, double B)[] Palette =
    [
        (0.31, 0.47, 0.65), (0.95, 0.56, 0.17), (0.35, 0.63, 0.31), (0.69, 0.48, 0.63), (0.88, 0.34, 0.35), (0.46, 0.72, 0.70), (0.93, 0.79, 0.28), (0.61, 0.46, 0.37),
    ];

    private void Diagram(ReportDiagram diagram)
    {
        Heading(diagram.Title, 10);
        const double H = 36;
        Ensure(H + 10 + diagram.Segments.Count * 11 + 8);
        _page.Add(new RectOp(Margin, _y, ContentWidth, H, 0.93, 0.93, 0.93));
        int i = 0;
        foreach (DiagramSegment s in diagram.Segments)
        {
            double x = Margin + (diagram.TotalLength <= 0 ? 0 : (double)s.Start / diagram.TotalLength * ContentWidth);
            double w = diagram.TotalLength <= 0 ? 0 : Math.Max(1.5, (double)s.Length / diagram.TotalLength * ContentWidth);
            (double r, double g, double b) = Palette[Math.Abs(s.Color == 0 ? i : s.Color) % Palette.Length];
            _page.Add(new RectOp(x, _y + 2, w, H - 4, r, g, b));
            i++;
        }

        _y += H + 8;
        foreach (DiagramSegment s in diagram.Segments)
        {
            DrawText(Margin, _y + 8, $"{s.Label}  {ReportSectionsBuilder.OffsetText(s.Start)}  {s.Length.ToString(CultureInfo.InvariantCulture)}", 8);
            _y += 11;
        }

        _y += 6;
    }

    // ---- 全体 ----

    /// <summary>PDF を書く。書体が見つからなければ <see cref="ReportPdfException"/>。ページ数を返す。</summary>
    public static int Write(Stream output, ReportContext context, IEnumerable<ReportSection> sections, string attachmentFolder, string attachmentPrefix,
        List<string> attachments, out bool truncated, CancellationToken token)
    {
        FontFace primary = SystemFontCatalog.Instance.Primary(null) ?? throw new ReportPdfException("No usable font was found.");
        var pdf = new ReportPdf(context, primary);
        pdf.Layout(sections, attachmentFolder, attachmentPrefix, attachments, token);
        truncated = pdf.Truncated;
        return pdf.Emit(output);
    }

    private void Layout(IEnumerable<ReportSection> sections, string attachmentFolder, string attachmentPrefix, List<string> attachments, CancellationToken token)
    {
        ReportOptions o = _context.Options;
        ReportInput input = _context.Input;
        DrawText(Margin, _y + 20, T("Title"), 20);
        _y += 28;
        if (!string.IsNullOrWhiteSpace(o.HeaderText))
        {
            Paragraph(o.HeaderText, 10);
        }

        Paragraph($"{T("Case")} {input.Case.Number} - {input.Case.Name}", 10, gray: true);
        foreach (ReportSection section in sections)
        {
            token.ThrowIfCancellationRequested();
            Heading(section.Title, 14);
            foreach (ReportBlock block in section.Blocks)
            {
                token.ThrowIfCancellationRequested();
                switch (block)
                {
                    case ReportParagraph p:
                        Paragraph(p.Text);
                        break;
                    case ReportKeyValues kv:
                        KeyValues(kv);
                        break;
                    case ReportTable t:
                        Table(t, attachmentFolder, attachmentPrefix, attachments, token);
                        break;
                    case ReportDump d:
                        Dump(d);
                        break;
                    case ReportDiagram g:
                        Diagram(g);
                        break;
                }
            }
        }
    }

    private int Emit(Stream output)
    {
        ReportInput input = _context.Input;
        using var writer = new PdfWriter(output, new PdfInfo($"{T("Title")} - {input.Case.Number}", input.AppName, new DateTimeOffset(DateTime.SpecifyKind(input.NowUtc, DateTimeKind.Utc))));
        var fonts = new Dictionary<string, PdfFont>(StringComparer.OrdinalIgnoreCase);
        PdfFont FontOf(FontFace face)
        {
            string key = face.Path + "#" + face.FaceIndex.ToString(CultureInfo.InvariantCulture);
            if (!fonts.TryGetValue(key, out PdfFont? f))
            {
                f = writer.AddFont(face);
                fonts[key] = f;
            }

            return f;
        }

        int count = _pages.Count;
        for (int p = 0; p < count; p++)
        {
            PdfPage page = writer.BeginPage(_width, _height);
            foreach (Op op in _pages[p])
            {
                switch (op)
                {
                    case RectOp r:
                        page.FillColor(r.R, r.G, r.B);
                        page.FillRectangle(r.X, r.Y, r.Width, r.Height);
                        break;
                    case LineOp l:
                        page.StrokeColor(0.3, 0.3, 0.3);
                        page.Line(l.X1, l.Y1, l.X2, l.Y2, l.Thickness);
                        break;
                    case TextOp t:
                        if (t.Gray)
                        {
                            page.FillColor(0.3, 0.3, 0.3);
                        }
                        else
                        {
                            page.FillColor(0, 0, 0);
                        }

                        page.Text(FontOf(t.Face), t.Size, t.X, t.Baseline, t.Text);
                        break;
                }
            }

            // ヘッダーとフッター: 各ページにケース番号とページ番号を入れる (仕様 2)。
            page.FillColor(0.25, 0.25, 0.25);
            page.StrokeColor(0.5, 0.5, 0.5);
            page.Line(Margin, _height - BottomMargin + 8, _width - Margin, _height - BottomMargin + 8, 0.4);
            string left = $"{T("Case")} {input.Case.Number}";
            string right = $"{T("Page")} {(p + 1).ToString(CultureInfo.InvariantCulture)} / {count.ToString(CultureInfo.InvariantCulture)}";
            page.Text(FontOf(_primary), 8, Margin, _height - BottomMargin + 22, left);
            page.Text(FontOf(_primary), 8, _width - Margin - Measure(right, 8), _height - BottomMargin + 22, right);
            page.Text(FontOf(_primary), 8, Margin, TopMargin - 22, $"{T("Title")} - {input.Case.Name}");
            page.End();
        }

        writer.Finish();
        return count;
    }
}
