using System.Diagnostics;
using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Printing;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>TOOL-01 印刷とページ設定、TOOL-02 印刷プレビュー、TOOL-03 PDF への出力。物理的なプリンタには何も送らず、ページの内容と PDF だけを確かめる。</summary>
public sealed class PrintingTests : IDisposable
{
    private static readonly DateTimeOffset Fixed = new(2026, 10, 7, 14, 3, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-print").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private static SystemFontMetrics? Fonts(string? family = null) =>
        SystemFontCatalog.Instance.Primary(family) is { } face ? new SystemFontMetrics(face, SystemFontCatalog.Instance) : null;

    private static PrintFields Fields(string name, string? path = null, long size = 0, bool modified = false) =>
        new(name, path, size, modified, "(modified)", Fixed, CultureInfo.GetCultureInfo("en-US"));

    private static IPrintData DataOf(Document doc, string name, string? path = null, bool? modified = null) =>
        new SnapshotPrintData(doc.Current, name, path, modified ?? doc.IsModified);

    private PdfExportResult ExportPdf(string pdf, PrintSettings settings, IPrintData data, SystemFontMetrics fonts, PrintFields? fields = null)
    {
        PrintPlan plan = PrintPlan.Create(settings, data.Length, fonts);
        return PdfExporter.WriteFile(pdf, plan, data, fields ?? Fields(data.Name, data.FullPath, data.Length, data.IsModified), fonts);
    }

    private static string AllText(PdfDocument pdf) => string.Join('\n', pdf.GetPages().Select(p => p.Text));

    // ---- TOOL-03 ----

    [Fact]
    [Trait(TC, "TC-TOOL-03-01")]
    public void HexValuesInTheExportedPdfCanBeSearchedAndTheDocumentInformationIsSet()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        string file = TestDataCatalog.Generate("TD-BYTES-256", _dir);
        using var doc = new Document(FileByteSource.Open(file));
        string pdfPath = Path("out.pdf");
        PdfExportResult result = ExportPdf(pdfPath, new PrintSettings { Paper = PaperKind.A4 }, DataOf(doc, "TD-BYTES-256", file), fonts);
        Assert.Equal((1, false), (result.Pages, result.HadUnreadable));

        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        Assert.Equal(1, pdf.NumberOfPages);
        string text = pdf.GetPage(1).Text;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "7C 7D 7E 7F"));
        for (int row = 0; row < 16; row++)
        {
            Assert.Contains($"{row * 16:X8}", text, StringComparison.Ordinal);
        }

        Assert.Equal("TD-BYTES-256", pdf.Information.Title);
        Assert.Equal("HexEditor", pdf.Information.Creator);
        Assert.NotNull(pdf.Information.CreationDate);

        // フォントはすべて埋め込み (サブセット: 名前に ABCDEF+ の印がある) で、しおりが 1 つ (0x0-0xFF)。
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        int fontsInFile = System.Text.RegularExpressions.Regex.Matches(raw, "/Subtype /Type0").Count;
        Assert.True(fontsInFile >= 1);
        Assert.Equal(fontsInFile, System.Text.RegularExpressions.Regex.Matches(raw, "/FontFile2").Count);
        Assert.Equal(fontsInFile * 2, System.Text.RegularExpressions.Regex.Matches(raw, @"/BaseFont /[A-Z]{6}\+").Count);
        Assert.True(pdf.TryGetBookmarks(out UglyToad.PdfPig.Outline.Bookmarks? bookmarks));
        UglyToad.PdfPig.Outline.BookmarkNode node = Assert.Single(bookmarks!.GetNodes());
        Assert.Equal("0x0-0xFF", node.Title);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-03-03")]
    public void JapaneseTextInTheTextColumnIsWrittenAsTextWithASubstituteFont()
    {
        if (Fonts() is not { } fonts || SystemFontCatalog.Instance.Substitute('日') is null)
        {
            return;
        }

        const string Sample = "日本語のテキスト。半角ｶﾅも含む";
        string file = Path("ja.bin");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(Sample));
        using var doc = new Document(FileByteSource.Open(file));
        PrintSettings settings = new() { View = ViewSettings.Default with { Encoding = "utf-8" } };
        string pdfPath = Path("ja.pdf");
        ExportPdf(pdfPath, settings, DataOf(doc, "ja.bin", file), fonts);

        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        string text = pdf.GetPage(1).Text;
        // テキスト列の文字は 1 文字ずつ位置を決めて書くため、文字の並びを位置順に集めて比べる。
        string[] words = [.. pdf.GetPage(1).Letters.Where(l => l.Value.Length > 0 && !char.IsAscii(l.Value[0])).OrderByDescending(l => Math.Round(l.StartBaseLine.Y)).ThenBy(l => l.StartBaseLine.X).Select(l => l.Value)];
        Assert.Equal(Sample.Where(c => !char.IsAscii(c)).Select(c => c.ToString()), words);
        Assert.DoesNotContain("\uFFFD", text, StringComparison.Ordinal);

        // 日本語の文字を含むフォント (代替フォント) が主フォントとは別に埋め込まれている。
        string raw = Encoding.Latin1.GetString(File.ReadAllBytes(pdfPath));
        Assert.True(System.Text.RegularExpressions.Regex.Matches(raw, "/Subtype /Type0").Count >= 2);
        Assert.Contains(pdf.GetPage(1).Letters, l => l.Value == "日" && l.BoundingBox.Width > 0);
    }

    [Fact]
    public void PdfWriterWritesPagesBookmarksAndAValidFontSubset()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        var output = new MemoryStream();
        using (var writer = new PdfWriter(output, new PdfInfo("Title あ", "HexEditor", Fixed)))
        {
            PdfFont font = writer.AddFont(fonts.Primary);
            for (int i = 0; i < 250; i++)
            {
                PdfPage page = writer.BeginPage(200, 100);
                page.Text(font, 10, 10, 20, $"page {i}");
                page.FillColor(1, 0, 0);
                page.FillRectangle(5, 30, 20, 10);
                page.StrokeColor(0, 0, 0);
                page.Line(5, 50, 100, 50, 0.5);
                int index = page.End();
                if (i % 100 == 0)
                {
                    writer.AddBookmark($"b{i}", index);
                }
            }
        }

        using PdfDocument pdf = PdfDocument.Open(output.ToArray());
        Assert.Equal(250, pdf.NumberOfPages);
        Assert.Equal("page 0", pdf.GetPage(1).Text);
        Assert.Equal("page 249", pdf.GetPage(250).Text);
        Assert.Equal("Title あ", pdf.Information.Title);
        Assert.True(pdf.TryGetBookmarks(out UglyToad.PdfPig.Outline.Bookmarks? bookmarks));
        Assert.Equal(["b0", "b100", "b200"], bookmarks!.GetNodes().Select(n => n.Title));
        Assert.Equal(200, pdf.GetPage(1).Width, 1);

        // サブセットの TrueType は、元のグリフ番号のまま読み直せる。
        TrueTypeFont original = fonts.Primary.Font;
        TrueTypeFont? subset = TrueTypeFont.Load(original.Subset([original.GlyphIndex('A'), original.GlyphIndex('7')]));
        Assert.NotNull(subset);
        Assert.Equal(original.NumGlyphs, subset.NumGlyphs);
        Assert.Equal(original.Advance(original.GlyphIndex('A')), subset.Advance(original.GlyphIndex('A')));
        Assert.True(original.Subset([original.GlyphIndex('A')]).Length < fonts.Primary.Data.Length);
    }

    [Fact]
    public void SubsetKeepsTheComponentsOfCompositeGlyphs()
    {
        if (SystemFontCatalog.Instance.Find("Segoe UI") is not { } face)
        {
            return;
        }

        TrueTypeFont font = face.Font;
        int accented = font.GlyphIndex('é');
        TrueTypeFont? subset = TrueTypeFont.Load(font.Subset([accented]));
        Assert.NotNull(subset);
        Assert.True(accented > 0);
    }

    // ---- TOOL-01 ----

    [Fact]
    [Trait(TC, "TC-TOOL-01-02")]
    public void HeaderAndFooterPlaceholdersAreReplaced()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        string copy = Path("print.bin");
        File.Copy(TestDataCatalog.Generate("TD-SEQ-1M", _dir), copy);
        using var doc = new Document(FileByteSource.Open(copy));
        doc.Overwrite(0, [0xFF]);
        var settings = new PrintSettings
        {
            Range = PrintRangeKind.Offsets,
            Offsets = (0, 0x1000),
            Header = new HeaderFooterText("{name}", "{path}", "{range}"),
            Footer = new HeaderFooterText("{size} {modified}", "{page} / {pages}", "{date} {time}"),
        };
        IPrintData data = DataOf(doc, "print.bin", copy);
        string pdfPath = Path("out.pdf");
        ExportPdf(pdfPath, settings, data, fonts, Fields("print.bin", copy, 1_048_576, modified: true));

        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        Assert.True(pdf.NumberOfPages >= 2);
        for (int p = 1; p <= pdf.NumberOfPages; p++)
        {
            string text = pdf.GetPage(p).Text;
            Assert.Contains("print.bin", text, StringComparison.Ordinal);
            Assert.Contains(copy, text, StringComparison.Ordinal);
            Assert.Contains("0x0-0xFFF", text, StringComparison.Ordinal);
            Assert.Contains("1,048,576 B (modified)", text, StringComparison.Ordinal);
            Assert.Contains($"{p} / {pdf.NumberOfPages}", text, StringComparison.Ordinal);
            Assert.Contains("10/7/2026 2:03 PM", text, StringComparison.Ordinal);

            // 置換されずに残った { がない (本文のテキスト列には { のバイトがありうるため、ヘッダとフッタの行だけを調べる)。
            double height = pdf.GetPage(p).Height;
            Assert.DoesNotContain(pdf.GetPage(p).Letters, l => l.Value == "{" && (l.StartBaseLine.Y > height - 60 || l.StartBaseLine.Y < 60));
        }
    }

    [Fact]
    public void PlaceholderExpansionHandlesUnknownSymbolsAndUnmodifiedDocuments()
    {
        PrintFields fields = Fields("a.bin", @"C:\x\a.bin", 10);
        Assert.Equal("a.bin  r", HeaderFooterFormatter.Expand("{name} {modified} {range}", fields, 1, 2, "r"));
        Assert.Equal("{unknown} 1/2 r", HeaderFooterFormatter.Expand("{unknown} {page}/{pages} {range}", fields, 1, 2, "r"));
        Assert.Equal(@"C:\x\a.bin 10 B", HeaderFooterFormatter.Expand("{path} {size}", fields, 1, 1, string.Empty));
        Assert.Equal("1,234 / 12,345", HeaderFooterFormatter.Expand("{page} / {pages}", fields, 1234, 12345, string.Empty));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-01-03")]
    public void ModifiedBytesAreUnderlinedInBlackAndWhite()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        string copy = Path("b.bin");
        File.Copy(TestDataCatalog.Generate("TD-BYTES-256", _dir), copy);
        using var doc = new Document(FileByteSource.Open(copy));
        doc.Overwrite(0x11, [0xAA]);
        string pdfPath = Path("out.pdf");
        ExportPdf(pdfPath, new PrintSettings(), DataOf(doc, "b.bin", copy), fonts);

        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        Page page = pdf.GetPage(1);
        Word rowLabel = page.GetWords().Single(w => w.Text.EndsWith("00000010", StringComparison.Ordinal) || w.Text == "0010");
        Word changed = page.GetWords().Single(w => w.Text == "AA" && Math.Abs(w.BoundingBox.Bottom - rowLabel.BoundingBox.Bottom) < 1);
        Word next = page.GetWords().First(w => w.Text == "12" && Math.Abs(w.BoundingBox.Bottom - changed.BoundingBox.Bottom) < 1);

        bool HasUnderline(Word w)
        {
            foreach (var path in page.Paths)
            {
                var box = path.GetBoundingRectangle();
                if (box is null)
                {
                    continue;
                }

                var r = box.Value;
                bool below = r.Top <= w.BoundingBox.Bottom + 0.5 && r.Top >= w.BoundingBox.Bottom - w.BoundingBox.Height * 0.2 - 0.5;
                bool wide = r.Width >= w.BoundingBox.Width * 0.8 && r.Height < 2;
                bool overlapped = r.Left <= w.BoundingBox.Left + 0.5 && r.Right >= w.BoundingBox.Right - 0.5;
                if (below && wide && overlapped)
                {
                    return true;
                }
            }

            return false;
        }

        Assert.True(HasUnderline(changed));
        Assert.False(HasUnderline(next));

        // 白黒: 彩度のある色がない。
        Assert.All(page.Paths, p => Assert.True(p.StrokeColor is null || p.StrokeColor.ToRGBValues() is var c && c.r == c.g && c.g == c.b));
        Assert.All(page.Letters, l => Assert.True(l.Color.ToRGBValues() is var c && c.r == c.g && c.g == c.b));
    }

    [Fact]
    public void ColorModePrintsABackgroundForModifiedBytesAndTheHighlights()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        using var doc = new Document(new MemoryByteSource(new byte[64]));
        doc.Overwrite(3, [1]);
        var data = new SnapshotPrintData(doc.Current, "m", null, true, (o, l) => [(10L, 2L, new PrintColor(10, 200, 30))]);
        PrintPlan colorPlan = PrintPlan.Create(new PrintSettings { Color = PrintColorMode.Color }, 64, fonts);
        PrintPage color = colorPlan.Render(0, data, Fields("m"));
        Assert.Contains(color.Rects, r => r.Color == PrintColor.ModifiedColor);
        Assert.Contains(color.Rects, r => r.Color == new PrintColor(10, 200, 30));
        Assert.Empty(color.Lines);

        PrintPlan bwPlan = PrintPlan.Create(new PrintSettings(), 64, fonts);
        PrintPage bw = bwPlan.Render(0, data, Fields("m"));
        Assert.All(bw.Rects, r => Assert.Equal(PrintColor.Shade, r.Color));
        Assert.Equal(2, bw.Lines.Count);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-01-05")]
    public void PrintsOverAThousandPagesNeedAConfirmationButARangeDoesNot()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        PrintPlan all = PrintPlan.Create(new PrintSettings(), 16 * 1024 * 1024, fonts);
        Assert.True(all.PrintedPages > 1000);
        Assert.True(all.NeedsConfirmation);
        Assert.False(all.ExceedsLimit);

        PrintPlan range = PrintPlan.Create(new PrintSettings { Range = PrintRangeKind.Pages, Pages = "1-3" }, 16 * 1024 * 1024, fonts);
        Assert.Equal(3, range.PrintedPages);
        Assert.False(range.NeedsConfirmation);

        // 3 ページだけを出力する。
        using var doc = new Document(FileByteSource.Open(TestDataCatalog.Generate("TD-RANDOM-16M", _dir)));
        string pdfPath = Path("out.pdf");
        PdfExportResult result = ExportPdf(pdfPath, new PrintSettings { Range = PrintRangeKind.Pages, Pages = "1-3" }, DataOf(doc, "r.bin"), fonts);
        Assert.Equal(3, result.Pages);
        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        Assert.Equal(3, pdf.NumberOfPages);
    }

    [Fact]
    public void RangesAboveTheLimitCannotBePrintedAndPageNumbersAreDerivedByCalculation()
    {
        FixedFontMetrics fonts = new();
        PrintPlan huge = PrintPlan.Create(new PrintSettings(), 100L << 30, fonts);
        Assert.True(huge.ExceedsLimit);
        long rows = ((100L << 30) + huge.BytesPerRow - 1) / huge.BytesPerRow;
        Assert.Equal((rows + huge.RowsPerPage - 1) / huge.RowsPerPage, huge.TotalPages);

        // ページの範囲で絞れば印刷できる。
        PrintPlan narrowed = PrintPlan.Create(new PrintSettings { Range = PrintRangeKind.Pages, Pages = "5-6" }, 100L << 30, fonts);
        Assert.False(narrowed.ExceedsLimit);
        Assert.Equal(2, narrowed.PrintedPages);
        Assert.Equal(4L * huge.RowsPerPage * huge.BytesPerRow, narrowed.DataRangeOfPage(0).Offset);

        using var big = new Document(new VirtualByteSource(100L << 30, VirtualContent.OffsetLowByte));
        Assert.Throws<InvalidOperationException>(() => PdfExporter.Write(huge, DataOf(big, "big"), Fields("big"), new MemoryStream(), Fonts() ?? throw new InvalidOperationException()));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-01-04")]
    public void ThePageCountOfA100GiBFileIsCalculatedWithoutReadingTheData()
    {
        FixedFontMetrics fonts = new();
        using var faulty = new FaultyByteSource(new VirtualByteSource(100L << 30, VirtualContent.OffsetLowByte));
        using var doc = new Document(faulty);
        var watch = Stopwatch.StartNew();
        PrintPlan plan = PrintPlan.Create(new PrintSettings(), doc.Length, fonts);
        long pages = plan.PrintedPages;
        watch.Stop();
        long rows = ((100L << 30) + plan.BytesPerRow - 1) / plan.BytesPerRow;
        Assert.Equal((rows + plan.RowsPerPage - 1) / plan.RowsPerPage, pages);
        Assert.Equal(0, faulty.ReadCount);
        Support.TimeBudget.Limit(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    [Trait(TC, "TC-TOOL-01-06")]
    public void EditsAfterThePrintStartedDoNotChangeThePrintedContent()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        string copy = Path("p.bin");
        File.Copy(TestDataCatalog.Generate("TD-SEQ-1M", _dir), copy);
        using var doc = new Document(FileByteSource.Open(copy));
        IPrintData data = DataOf(doc, "p.bin", copy);
        PrintPlan plan = PrintPlan.Create(new PrintSettings { Range = PrintRangeKind.Offsets, Offsets = (0, 0x10000), Header = HeaderFooterText.Empty, Footer = HeaderFooterText.Empty }, doc.Length, fonts);

        // 印刷を始めた後 (スナップショットを取った後) に 0xFF00 を書き換える。
        doc.Overwrite(0xFF00, [0xEE]);
        PrintPage last = plan.Render(plan.PrintedPages - 1, data, Fields("p.bin"));
        int row = last.Runs.FindIndex(r => r.Text.EndsWith("FF00", StringComparison.Ordinal));
        Assert.True(row >= 0);
        Assert.StartsWith("00 01 02", last.Runs[row + 1].Text, StringComparison.Ordinal);

        // 新しいスナップショットには反映されている。
        PrintPage fresh = plan.Render(plan.PrintedPages - 1, DataOf(doc, "p.bin", copy), Fields("p.bin"));
        int freshRow = fresh.Runs.FindIndex(r => r.Text.EndsWith("FF00", StringComparison.Ordinal));
        Assert.StartsWith("EE 01 02", fresh.Runs[freshRow + 1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableBytesArePrintedAsQuestionMarksAndReported()
    {
        FixedFontMetrics fonts = new();
        using var faulty = new FaultyByteSource(new VirtualByteSource(4096, VirtualContent.OffsetLowByte));
        faulty.AddReadError(0x20, 4);
        using var doc = new Document(faulty);
        PrintPlan plan = PrintPlan.Create(new PrintSettings { Header = HeaderFooterText.Empty, Footer = HeaderFooterText.Empty }, doc.Length, fonts);
        PrintPage page = plan.Render(0, DataOf(doc, "f"), Fields("f"));
        Assert.True(page.HadUnreadable);
        string text = string.Join('\n', page.Runs.Select(r => r.Text));
        Assert.Contains("?? ?? ?? ??", text, StringComparison.Ordinal);
    }

    // ---- 設定とレイアウト ----

    [Fact]
    public void FitToPaperChoosesTheLargestMultipleOfEightThatFitsAndFourOtherwise()
    {
        FixedFontMetrics fonts = new();
        PrintPlan fit = PrintPlan.Create(new PrintSettings { BytesPerRow = 0, FontSize = 9 }, 1024, fonts);
        Assert.Equal(0, fit.BytesPerRow % 8);
        Assert.True(fit.LineChars * fit.CharWidth <= fit.PageWidth - fit.MarginLeft - fit.MarginRight + 0.01);

        PrintPlan explicitOk = PrintPlan.Create(new PrintSettings { BytesPerRow = 16 }, 1024, fonts);
        Assert.Equal(16, explicitOk.BytesPerRow);

        // 用紙に入らない 1 行のバイト数は「用紙に合わせる」になる。
        PrintPlan tooWide = PrintPlan.Create(new PrintSettings { BytesPerRow = 64, FontSize = 24 }, 1024, fonts);
        Assert.NotEqual(64, tooWide.BytesPerRow);
        Assert.Equal(0, tooWide.BytesPerRow % 4);
        PrintPlan landscape = PrintPlan.Create(new PrintSettings { BytesPerRow = 0, Landscape = true }, 1024, fonts);
        Assert.True(landscape.BytesPerRow >= fit.BytesPerRow);
        Assert.True(landscape.PageWidth > landscape.PageHeight);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-02-01")]
    public void ChangingTheFontSizeChangesThePageCountAndThePageContentQuickly()
    {
        if (Fonts() is not { } fonts)
        {
            return;
        }

        string file = TestDataCatalog.Generate("TD-RANDOM-16M", _dir);
        using var doc = new Document(FileByteSource.Open(file));
        IPrintData data = DataOf(doc, "r.bin");
        PrintPlan nine = PrintPlan.Create(new PrintSettings { FontSize = 9 }, doc.Length, fonts);
        PrintPage before = nine.Render(0, data, Fields("r.bin"));
        var watch = Stopwatch.StartNew();
        PrintPlan twelve = PrintPlan.Create(new PrintSettings { FontSize = 12 }, doc.Length, fonts);
        PrintPage after = twelve.Render(0, data, Fields("r.bin"));
        watch.Stop();
        Assert.True(twelve.PrintedPages > nine.PrintedPages);
        Assert.NotEqual(before.Runs.Count, after.Runs.Count);
        Assert.Equal(12, after.Runs[0].Size);
        Support.TimeBudget.Limit(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"{watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void PageRangesAreParsedAndClipped()
    {
        PageRangeSet set = PageRangeSet.Parse("1-3, 8, 20-", 10)!;
        Assert.Equal(4, set.Count);
        Assert.Equal([1L, 2, 3, 8], Enumerable.Range(0, 4).Select(i => set.Nth(i)));
        Assert.Equal(8, PageRangeSet.Parse("-3, 5-7, 7-9", 10)!.Count);
        Assert.Equal(10, PageRangeSet.Parse("1-", 10)!.Count);
        Assert.Null(PageRangeSet.Parse("abc", 10));
        Assert.Null(PageRangeSet.Parse("3-1", 10));
        Assert.Null(PageRangeSet.Parse("11-12", 10));
        Assert.Null(PageRangeSet.Parse(string.Empty, 10));
    }

    [Fact]
    public void SelectionsArePrintedPerRangeAndTheRangeLabelFollowsTheSection()
    {
        FixedFontMetrics fonts = new();
        var settings = new PrintSettings { Range = PrintRangeKind.Selection, Selection = [(0x100, 0x20), (0x400, 0x10)] };
        PrintPlan plan = PrintPlan.Create(settings, 0x1000, fonts);
        Assert.Equal(2, plan.PrintedPages);
        Assert.Equal("0x100-0x11F", plan.RangeLabel(0));
        Assert.Equal("0x400-0x40F", plan.RangeLabel(1));
        Assert.Equal((0x400L, 0x10L), plan.DataRangeOfPage(1));
        Assert.Equal(PrintRangeKind.All, new PrintSettings().Range);

        // 選択がなければすべて。
        Assert.Equal(PrintPlan.Create(new PrintSettings(), 0x1000, fonts).PrintedPages, PrintPlan.Create(new PrintSettings { Range = PrintRangeKind.Selection }, 0x1000, fonts).PrintedPages);
    }

    [Fact]
    public void SettingsRoundTripThroughJsonAndAreClamped()
    {
        var settings = new PrintSettings
        {
            Paper = PaperKind.Legal,
            Landscape = true,
            MarginTopMm = 10,
            FontSize = 11,
            BytesPerRow = 32,
            Color = PrintColorMode.Color,
            Header = new HeaderFooterText("a", "b", "c"),
            Copies = 3,
        };
        PrintSettings back = PrintSettings.FromJson(settings.ToJson());
        Assert.Equal((settings.Paper, settings.Landscape, settings.MarginTopMm, settings.FontSize, settings.BytesPerRow, settings.Color, settings.Header, settings.Copies),
            (back.Paper, back.Landscape, back.MarginTopMm, back.FontSize, back.BytesPerRow, back.Color, back.Header, back.Copies));

        PrintSettings clamped = new PrintSettings { FontSize = 100, MarginLeftMm = -5, BytesPerRow = 2, Copies = 5000 }.Normalize();
        Assert.Equal((24.0, 0.0, 4, 999), (clamped.FontSize, clamped.MarginLeftMm, clamped.BytesPerRow, clamped.Copies));
        Assert.Equal(PaperKind.Letter, PaperSizes.DefaultFor(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(PaperKind.A4, PaperSizes.DefaultFor(CultureInfo.GetCultureInfo("ja-JP")));
    }

    [Fact]
    public void HidingColumnsChangesTheLayout()
    {
        FixedFontMetrics fonts = new();
        var noOffset = new PrintSettings { View = ViewSettings.Default with { ShowOffsetColumn = false }, Header = HeaderFooterText.Empty, Footer = HeaderFooterText.Empty };
        using var doc = new Document(new MemoryByteSource(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        PrintPage page = PrintPlan.Create(noOffset, 64, fonts).Render(0, DataOf(doc, "x"), Fields("x"));
        Assert.DoesNotContain(page.Runs, r => r.Text.StartsWith("00000000", StringComparison.Ordinal));

        var hexOnly = new PrintSettings { View = ViewSettings.Default with { ShowTextColumn = false }, Header = HeaderFooterText.Empty, Footer = HeaderFooterText.Empty };
        PrintPage hexPage = PrintPlan.Create(hexOnly, 64, fonts).Render(0, DataOf(doc, "x"), Fields("x"));
        Assert.Equal(2 * 4, hexPage.Runs.Count);
    }
}
