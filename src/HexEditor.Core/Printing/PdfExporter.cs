using HexEditor.Core.Operations;
using HexEditor.Core.Saving;

namespace HexEditor.Core.Printing;

/// <summary>PDF の書き出しの結果。</summary>
public sealed record PdfExportResult(int Pages, bool HadUnreadable);

/// <summary>印刷の計画 (<see cref="PrintPlan"/>) を PDF に書き出す (TOOL-03)。</summary>
public static class PdfExporter
{
    /// <summary>しおりを付けるページの間隔 (TOOL-03 の仕様 3)。</summary>
    public const int BookmarkEvery = 1000;

    /// <summary>
    /// ストリームに PDF を書く。ページ単位で書き出すため、メモリ使用量はページ数に比例しない。100,000 ページを超える範囲は書かない。
    /// </summary>
    public static PdfExportResult Write(PrintPlan plan, IPrintData data, PrintFields fields, Stream output, SystemFontMetrics fonts,
        SystemFontCatalog? catalog = null, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        if (plan.ExceedsLimit)
        {
            throw new InvalidOperationException("100,000 ページを超える範囲は書き出せません。");
        }

        catalog ??= SystemFontCatalog.Instance;
        using var writer = new PdfWriter(output, new PdfInfo(data.Name, "HexEditor", fields.Now));
        PdfFont primary = writer.AddFont(fonts.Primary);
        var substitutes = new Dictionary<string, PdfFont>(StringComparer.OrdinalIgnoreCase);
        operation?.SetTotal(plan.PrintedPages);
        bool unreadable = false;
        for (long i = 0; i < plan.PrintedPages; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrintPage page = plan.Render(i, data, fields);
            unreadable |= page.HadUnreadable;
            PdfPage pdf = writer.BeginPage(page.Width, page.Height);
            foreach (PrintRect rect in page.Rects)
            {
                pdf.FillColor(rect.Color.R / 255.0, rect.Color.G / 255.0, rect.Color.B / 255.0);
                pdf.FillRectangle(rect.X, rect.Y, rect.Width, rect.Height);
            }

            foreach (PrintLine line in page.Lines)
            {
                pdf.StrokeColor(line.Color.R / 255.0, line.Color.G / 255.0, line.Color.B / 255.0);
                pdf.Line(line.X1, line.Y1, line.X2, line.Y2, line.Thickness);
            }

            pdf.FillColor(0, 0, 0);
            foreach (PrintRun run in page.Runs)
            {
                PdfFont font = primary;
                if (run.Font == PrintFontKind.Substitute)
                {
                    int cp = char.ConvertToUtf32(run.Text, 0);
                    FontFace? face = catalog.Substitute(cp);
                    if (face is not null)
                    {
                        if (!substitutes.TryGetValue(face.Path + "#" + face.FaceIndex, out font!))
                        {
                            font = writer.AddFont(face);
                            substitutes[face.Path + "#" + face.FaceIndex] = font;
                        }
                    }
                }

                pdf.Text(font, run.Size, run.X, run.Baseline, run.Text);
            }

            int index = pdf.End();
            if (i % BookmarkEvery == 0)
            {
                writer.AddBookmark(plan.BookmarkTitle(i, BookmarkEvery), index);
            }

            operation?.Report(i + 1);
        }

        writer.Finish();
        return new PdfExportResult((int)plan.PrintedPages, unreadable);
    }

    /// <summary>
    /// ファイルに PDF を書く。一時ファイルに書いてから置き換え (TOOL-03 の仕様 5)、失敗・キャンセルなら書きかけのファイルを残さない。
    /// </summary>
    public static PdfExportResult WriteFile(string path, PrintPlan plan, IPrintData data, PrintFields fields, SystemFontMetrics fonts,
        SystemFontCatalog? catalog = null, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        PdfExportResult? result = null;
        SafeFileWriter.Write(path, stream => result = Write(plan, data, fields, stream, fonts, catalog, operation, cancellationToken));
        return result!;
    }
}
