using System.Globalization;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Printing;

/// <summary>印刷するデータ (ある時点のスナップショット。TOOL-01 の仕様 4・6)。</summary>
public interface IPrintData
{
    string Name { get; }

    string? FullPath { get; }

    long Length { get; }

    /// <summary>未保存の変更がある (<c>{modified}</c>)。</summary>
    bool IsModified { get; }

    ReadResult Read(long offset, Span<byte> buffer);

    /// <summary>変更したバイトの範囲 (白黒では下線、カラーでは背景色)。</summary>
    IEnumerable<(long Offset, long Length)> ModifiedRanges(long offset, long length);

    /// <summary>ブックマーク・色付けルールの背景色の範囲。</summary>
    IEnumerable<(long Offset, long Length, PrintColor Color)> Highlights(long offset, long length) => [];
}

/// <summary>ドキュメントのスナップショットを印刷のデータにする。印刷を始めた時点の内容で、その後の編集は反映しない。</summary>
public sealed class SnapshotPrintData(DocumentSnapshot snapshot, string name, string? fullPath, bool isModified,
    Func<long, long, IEnumerable<(long, long, PrintColor)>>? highlights = null) : IPrintData
{
    public string Name { get; } = name;

    public string? FullPath { get; } = fullPath;

    public long Length => snapshot.Length;

    public bool IsModified { get; } = isModified;

    public ReadResult Read(long offset, Span<byte> buffer) => snapshot.Read(offset, buffer);

    public IEnumerable<(long Offset, long Length)> ModifiedRanges(long offset, long length) => snapshot.EnumerateModifiedRanges(offset, length);

    public IEnumerable<(long Offset, long Length, PrintColor Color)> Highlights(long offset, long length) =>
        highlights?.Invoke(offset, length) ?? [];
}

/// <summary>文字の幅と、主フォントにある文字かの問い合わせ (レイアウト用)。</summary>
public interface IPrintFontMetrics
{
    /// <summary>主フォント (等幅) の 1 文字分の幅 (em を 1 とした比)。</summary>
    double CellAdvanceEm { get; }

    bool PrimaryHas(int codePoint);

    /// <summary>文字の幅 (em を 1 とした比)。主フォントになければ代替フォントの幅。</summary>
    double AdvanceEm(int codePoint);
}

/// <summary>固定幅だけを返す代わりの寸法 (テスト用)。すべての文字を主フォントにあるものとして扱う。</summary>
public sealed class FixedFontMetrics(double advanceEm = 0.6) : IPrintFontMetrics
{
    public double CellAdvanceEm { get; } = advanceEm;

    public bool PrimaryHas(int codePoint) => true;

    public double AdvanceEm(int codePoint) => CellAdvanceEm;
}

/// <summary>システムのフォントから求める寸法。</summary>
public sealed class SystemFontMetrics(FontFace primary, SystemFontCatalog catalog) : IPrintFontMetrics
{
    public FontFace Primary { get; } = primary;

    public double CellAdvanceEm { get; } = primary.Font.AdvanceEm(Math.Max(1, primary.Font.GlyphIndex('0')));

    public bool PrimaryHas(int codePoint) => Primary.Font.GlyphIndex(codePoint) != 0;

    public double AdvanceEm(int codePoint)
    {
        int gid = Primary.Font.GlyphIndex(codePoint);
        if (gid != 0)
        {
            return Primary.Font.AdvanceEm(gid);
        }

        FontFace? substitute = catalog.Substitute(codePoint);
        return substitute is null ? CellAdvanceEm : substitute.Font.AdvanceEm(substitute.Font.GlyphIndex(codePoint));
    }
}

public enum PrintFontKind
{
    /// <summary>設定した等幅フォント。</summary>
    Primary,

    /// <summary>主フォントにない文字の代替フォント。</summary>
    Substitute,
}

/// <summary>ページの文字列 1 つ。座標は点で、左上が原点・下向きが正。<see cref="Baseline"/> は基準線。</summary>
public sealed record PrintRun(double X, double Baseline, string Text, PrintFontKind Font, double Size, PrintColor Color);

public sealed record PrintRect(double X, double Y, double Width, double Height, PrintColor Color);

public sealed record PrintLine(double X1, double Y1, double X2, double Y2, double Thickness, PrintColor Color);

/// <summary>描画する 1 ページ分の内容 (プレビュー・PDF・プリンタで共通)。</summary>
public sealed class PrintPage(int number, int count, double width, double height)
{
    /// <summary>印刷するページの中での番号 (1 から)。</summary>
    public int Number { get; } = number;

    /// <summary>印刷するページの数。</summary>
    public int Count { get; } = count;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public List<PrintRect> Rects { get; } = [];

    public List<PrintLine> Lines { get; } = [];

    public List<PrintRun> Runs { get; } = [];

    /// <summary>このページが読んだデータの範囲 (オフセットと長さ)。</summary>
    public (long Offset, long Length) DataRange { get; set; }

    /// <summary>このページで読めなかったバイトがあった (<c>??</c> で印刷した)。</summary>
    public bool HadUnreadable { get; set; }
}

/// <summary>印刷範囲の 1 区間。</summary>
public sealed record PrintSection(long Start, long Length)
{
    public long End => Start + Length;
}

/// <summary>ページの範囲 (<c>1-3, 8</c>) の解釈 (TOOL-01 の仕様 2)。</summary>
public sealed class PageRangeSet
{
    private readonly List<(long First, long Last)> _ranges;
    private readonly List<long> _starts = [];

    private PageRangeSet(List<(long, long)> ranges)
    {
        _ranges = ranges;
        long total = 0;
        foreach ((long first, long last) in ranges)
        {
            _starts.Add(total);
            total += last - first + 1;
        }

        Count = total;
    }

    /// <summary>範囲に含まれるページの数。</summary>
    public long Count { get; }

    /// <summary>
    /// 文字列を読む。<c>1-3, 8</c>、<c>5-</c> (最後まで)、<c>-3</c> (先頭から)。ページは 1 から <paramref name="totalPages"/> までに収める。
    /// 読めない、または 1 ページも含まなければ null。
    /// </summary>
    public static PageRangeSet? Parse(string text, long totalPages)
    {
        var ranges = new List<(long, long)>();
        foreach (string part in text.Split([',', ';', '、', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            long first;
            long last;
            int dash = part.IndexOfAny(['-', '–', '〜', '~']);
            if (dash < 0)
            {
                if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out first))
                {
                    return null;
                }

                last = first;
            }
            else
            {
                string a = part[..dash].Trim();
                string b = part[(dash + 1)..].Trim();
                first = 1;
                last = totalPages;
                if ((a.Length > 0 && !long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out first))
                    || (b.Length > 0 && !long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out last)))
                {
                    return null;
                }

                if (a.Length == 0 && b.Length == 0)
                {
                    return null;
                }
            }

            bool bothGiven = dash < 0 || (part[..dash].Trim().Length > 0 && part[(dash + 1)..].Trim().Length > 0);
            if (first < 1 || (bothGiven && last < first))
            {
                return null;
            }

            first = Math.Max(1, first);
            last = Math.Min(last, totalPages);
            if (first <= last)
            {
                ranges.Add((first, last));
            }
        }

        // 重なりをまとめ、昇順にする。
        ranges.Sort();
        var merged = new List<(long, long)>();
        foreach ((long f, long l) in ranges)
        {
            if (merged.Count > 0 && f <= merged[^1].Item2 + 1)
            {
                merged[^1] = (merged[^1].Item1, Math.Max(merged[^1].Item2, l));
            }
            else
            {
                merged.Add((f, l));
            }
        }

        return merged.Count == 0 ? null : new PageRangeSet(merged);
    }

    /// <summary><paramref name="index"/> 番目 (0 から) のページ番号 (1 から)。</summary>
    public long Nth(long index)
    {
        int i = _starts.BinarySearch(index);
        if (i < 0)
        {
            i = ~i - 1;
        }

        return _ranges[i].First + (index - _starts[i]);
    }
}

/// <summary>ヘッダ・フッタの置換記号 (TOOL-01 の仕様 3) に使う値。</summary>
public sealed record PrintFields(string Name, string? Path, long Size, bool Modified, string ModifiedText, DateTimeOffset Now, CultureInfo Culture);

public static class HeaderFooterFormatter
{
    /// <summary>
    /// <c>{name}</c> <c>{path}</c> <c>{page}</c> <c>{pages}</c> <c>{date}</c> <c>{time}</c> <c>{range}</c> <c>{size}</c> <c>{modified}</c> を置き換える。
    /// 知らない記号はそのまま残す。
    /// </summary>
    public static string Expand(string template, PrintFields fields, int page, int pages, string range)
    {
        if (template.Length == 0 || !template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 16);
        for (int i = 0; i < template.Length; i++)
        {
            int end;
            if (template[i] == '{' && (end = template.IndexOf('}', i)) > i)
            {
                string key = template[(i + 1)..end].ToLowerInvariant();
                string? value = key switch
                {
                    "name" => fields.Name,
                    "path" => fields.Path ?? fields.Name,
                    "page" => page.ToString("N0", fields.Culture),
                    "pages" => pages.ToString("N0", fields.Culture),
                    "date" => fields.Now.ToString("d", fields.Culture),
                    "time" => fields.Now.ToString("t", fields.Culture),
                    "range" => range,
                    "size" => fields.Size.ToString("N0", fields.Culture) + " B",
                    "modified" => fields.Modified ? fields.ModifiedText : string.Empty,
                    _ => null,
                };
                if (value is not null)
                {
                    sb.Append(value);
                    i = end;
                    continue;
                }
            }

            sb.Append(template[i]);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 印刷の計画 (TOOL-01・TOOL-02): 設定とデータの長さから、1 行のバイト数・1 ページの行数・ページ数を求める。ページ数は計算だけで求め、
/// データは読まない (巨大ファイルでも即座に出る)。<see cref="Render"/> は印刷するページを 1 つずつ作る (データはそのページの分だけ読む)。
/// </summary>
public sealed class PrintPlan
{
    private readonly PrintSettings _settings;
    private readonly IPrintFontMetrics _fonts;
    private readonly long[] _sectionFirstPage;
    private readonly PageRangeSet? _pageSet;
    private readonly ViewSettings _view;
    private readonly OffsetFormat _offsets;
    private readonly RowFormat _row;
    private readonly TextEncoding _encoding;

    private PrintPlan(PrintSettings settings, IPrintFontMetrics fonts, long dataLength)
    {
        _settings = settings.Normalize();
        _fonts = fonts;
        DataLength = dataLength;
        (double paperWidth, double paperHeight) = PaperSizes.Size(_settings.Paper);
        PageWidth = _settings.Landscape ? paperHeight : paperWidth;
        PageHeight = _settings.Landscape ? paperWidth : paperHeight;
        MarginLeft = PaperSizes.MmToPoints(_settings.MarginLeftMm);
        MarginRight = PaperSizes.MmToPoints(_settings.MarginRightMm);
        MarginTop = PaperSizes.MmToPoints(_settings.MarginTopMm);
        MarginBottom = PaperSizes.MmToPoints(_settings.MarginBottomMm);
        FontSize = _settings.FontSize;
        CharWidth = FontSize * fonts.CellAdvanceEm;
        LineHeight = FontSize * 1.2;

        _view = _settings.View with
        {
            CellFormat = CellFormat.Hex,
            ReverseGroups = false,
            BigEndian = false,
            ExtraTextColumns = string.Empty,
            RecordView = false,
        };
        if (!_view.ShowOffsetColumn && !_view.ShowHexColumn && !_view.ShowTextColumn)
        {
            _view = _view with { ShowHexColumn = true };
        }

        _encoding = TextEncoding.FromId(_view.Encoding);
        long maxCursor = Math.Max(0, dataLength - 1);
        _offsets = new OffsetFormat(_view, maxCursor, _view.SectorSize);

        double contentWidth = Math.Max(CharWidth, PageWidth - MarginLeft - MarginRight);
        int capacity = (int)Math.Floor(contentWidth / CharWidth + 1e-9);
        BytesPerRow = ChooseBytesPerRow(capacity);
        _row = RowFormat.For(_view, BytesPerRow) with { TextColumns = 1 };
        OffsetWidth = _view.ShowOffsetColumn ? _offsets.ColumnWidth : 0;
        HexStartChar = OffsetWidth == 0 ? 0 : OffsetWidth + RowFormat.ColumnGap;
        LineChars = HexStartChar + (_row.ShowHex || _row.ShowText ? _row.LineLength : 0);

        bool header = !_settings.Header.IsEmpty;
        bool footer = !_settings.Footer.IsEmpty;
        HeaderBand = header ? LineHeight + FontSize * 0.5 : 0;
        FooterBand = footer ? LineHeight + FontSize * 0.5 : 0;
        BodyTop = MarginTop + HeaderBand;
        double bodyHeight = PageHeight - MarginTop - MarginBottom - HeaderBand - FooterBand;
        RowsPerPage = (int)Math.Max(1, Math.Floor(bodyHeight / LineHeight + 1e-9));

        Sections = BuildSections(_settings, dataLength);
        _sectionFirstPage = new long[Sections.Count + 1];
        long pages = 0;
        for (int i = 0; i < Sections.Count; i++)
        {
            _sectionFirstPage[i] = pages;
            long rows = (Sections[i].Length + BytesPerRow - 1) / BytesPerRow;
            pages += (rows + RowsPerPage - 1) / RowsPerPage;
        }

        _sectionFirstPage[^1] = pages;
        TotalPages = pages;
        if (_settings.Range == PrintRangeKind.Pages)
        {
            _pageSet = PageRangeSet.Parse(_settings.Pages, TotalPages);
            PageRangeValid = _pageSet is not null;
            PrintedPages = _pageSet?.Count ?? 0;
        }
        else
        {
            PageRangeValid = true;
            PrintedPages = TotalPages;
        }
    }

    public static PrintPlan Create(PrintSettings settings, long dataLength, IPrintFontMetrics fonts) => new(settings, fonts, dataLength);

    public long DataLength { get; }

    public double PageWidth { get; }

    public double PageHeight { get; }

    public double MarginLeft { get; }

    public double MarginRight { get; }

    public double MarginTop { get; }

    public double MarginBottom { get; }

    public double FontSize { get; }

    /// <summary>1 文字分の幅 (点)。</summary>
    public double CharWidth { get; }

    public double LineHeight { get; }

    public int BytesPerRow { get; }

    public int RowsPerPage { get; }

    public int LineChars { get; }

    public int OffsetWidth { get; }

    public int HexStartChar { get; }

    public double HeaderBand { get; }

    public double FooterBand { get; }

    public double BodyTop { get; }

    public IReadOnlyList<PrintSection> Sections { get; }

    /// <summary>範囲全体のページ数 (ページの範囲で絞る前)。</summary>
    public long TotalPages { get; }

    /// <summary>印刷するページの数。</summary>
    public long PrintedPages { get; }

    /// <summary>ページの範囲の文字列を読めた。</summary>
    public bool PageRangeValid { get; }

    /// <summary>1,000 ページを超える印刷は、印刷の前に確認を出す。</summary>
    public bool NeedsConfirmation => PrintedPages > PrintSettings.ConfirmPages;

    /// <summary>100,000 ページを超える範囲は印刷できない。</summary>
    public bool ExceedsLimit => PrintedPages > PrintSettings.MaxPages;

    public PrintSettings Settings => _settings;

    private static List<PrintSection> BuildSections(PrintSettings settings, long length)
    {
        var sections = new List<PrintSection>();
        if (length <= 0)
        {
            return sections;
        }

        switch (settings.Range)
        {
            case PrintRangeKind.Selection when settings.Selection.Count > 0:
                foreach ((long start, long len) in settings.Selection)
                {
                    if (start >= 0 && start < length && len > 0)
                    {
                        sections.Add(new PrintSection(start, Math.Min(len, length - start)));
                    }
                }

                break;
            case PrintRangeKind.Offsets:
                (long s, long l) = settings.Offsets;
                if (s >= 0 && s < length && l > 0)
                {
                    sections.Add(new PrintSection(s, Math.Min(l, length - s)));
                }

                break;
            default:
                sections.Add(new PrintSection(0, length));
                break;
        }

        return sections;
    }

    private int ChooseBytesPerRow(int capacity)
    {
        int Fits(int bpr)
        {
            RowFormat row = RowFormat.For(_view, bpr) with { TextColumns = 1 };
            int offset = _view.ShowOffsetColumn ? _offsets.ColumnWidth + RowFormat.ColumnGap : 0;
            return offset + row.LineLength;
        }

        int requested = _settings.BytesPerRow;
        if (requested > 0 && Fits(requested) <= capacity)
        {
            return requested;
        }

        // 「用紙に合わせる」: 余白とフォントから入る最大の 8 の倍数。入らなければ 4。
        for (int bpr = 64; bpr >= 8; bpr -= 8)
        {
            if (Fits(bpr) <= capacity)
            {
                return bpr;
            }
        }

        return 4;
    }

    // ---- ページと範囲の対応 ----

    private long GlobalPage(long printedIndex) => _pageSet is null ? printedIndex : _pageSet.Nth(printedIndex) - 1;

    private (int Section, long PageInSection) Locate(long globalPage)
    {
        int lo = 0;
        int hi = Sections.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_sectionFirstPage[mid] <= globalPage)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (lo, globalPage - _sectionFirstPage[lo]);
    }

    /// <summary>印刷するページ <paramref name="printedIndex"/> (0 から) が読むデータの範囲。</summary>
    public (long Offset, long Length) DataRangeOfPage(long printedIndex)
    {
        (int section, long inSection) = Locate(GlobalPage(printedIndex));
        PrintSection s = Sections[section];
        long offset = s.Start + inSection * RowsPerPage * BytesPerRow;
        return (offset, Math.Min(RowsPerPage * (long)BytesPerRow, s.End - offset));
    }

    /// <summary>ヘッダの <c>{range}</c> に出す、印刷範囲のオフセット (<c>0x0-0xFFF</c>)。</summary>
    public string RangeLabel(long printedIndex)
    {
        PrintSection s = Sections.Count == 0 ? new PrintSection(0, 0) : Sections[Locate(GlobalPage(printedIndex)).Section];
        return $"0x{s.Start:X}-0x{Math.Max(s.Start, s.End - 1):X}";
    }

    /// <summary>しおり (PDF) の題: そのページから <paramref name="count"/> ページ分のオフセットの範囲。</summary>
    public string BookmarkTitle(long firstIndex, long count)
    {
        (long start, _) = DataRangeOfPage(firstIndex);
        (long lastStart, long lastLength) = DataRangeOfPage(Math.Min(PrintedPages - 1, firstIndex + count - 1));
        return $"0x{start:X}-0x{Math.Max(start, lastStart + lastLength - 1):X}";
    }

    // ---- ページの作成 ----

    /// <summary>
    /// 印刷するページ <paramref name="printedIndex"/> (0 から) の内容を作る。データはそのページの分だけ読む。読めなかったバイトは <c>??</c>
    /// で印刷する (<see cref="PrintPage.HadUnreadable"/>)。
    /// </summary>
    public PrintPage Render(long printedIndex, IPrintData data, PrintFields fields)
    {
        if (printedIndex < 0 || printedIndex >= PrintedPages)
        {
            throw new ArgumentOutOfRangeException(nameof(printedIndex));
        }

        var page = new PrintPage((int)(printedIndex + 1), (int)Math.Min(int.MaxValue, PrintedPages), PageWidth, PageHeight);
        (long offset0, long bytes) = DataRangeOfPage(printedIndex);
        page.DataRange = (offset0, bytes);
        double left = MarginLeft;

        // データを読む (文字コードによっては前後の数バイトも)。
        int lookback = (int)Math.Min(offset0, TextCellDecoder.LookbackFor(_encoding));
        int lookahead = (int)Math.Min(data.Length - (offset0 + bytes), TextCellDecoder.LookaheadFor(_encoding));
        long dataStart = offset0 - lookback;
        byte[] buffer = new byte[lookback + bytes + lookahead];
        ReadResult read = data.Read(dataStart, buffer);
        var states = new ByteState[buffer.Length];
        foreach (UnreadableRange range in read.Unreadable)
        {
            long from = Math.Max(range.Offset, dataStart) - dataStart;
            long to = Math.Min(range.End, dataStart + buffer.Length) - dataStart;
            for (long i = from; i < to; i++)
            {
                states[i] = ByteState.Unreadable;
            }
        }

        if (read.BytesReturned < buffer.Length)
        {
            for (int i = read.BytesReturned; i < buffer.Length; i++)
            {
                states[i] = ByteState.Unreadable;
            }
        }

        var cells = new TextCell[bytes];
        if (_row.ShowText)
        {
            TextCellDecoder.Decode(_encoding, buffer, dataStart, offset0, cells, states, _view.Utf16Phase, _view.Utf32Phase);
        }

        // 変更したバイト、ブックマークなどの強調。
        var modified = new bool[bytes];
        foreach ((long o, long l) in data.ModifiedRanges(offset0, bytes))
        {
            for (long i = Math.Max(o, offset0); i < Math.Min(o + l, offset0 + bytes); i++)
            {
                modified[i - offset0] = true;
            }
        }

        var highlight = new PrintColor?[bytes];
        foreach ((long o, long l, PrintColor hc) in data.Highlights(offset0, bytes))
        {
            for (long i = Math.Max(o, offset0); i < Math.Min(o + l, offset0 + bytes); i++)
            {
                highlight[i - offset0] = hc;
            }
        }

        bool color = _settings.Color == PrintColorMode.Color;
        int rows = (int)((bytes + BytesPerRow - 1) / BytesPerRow);
        bool hadUnreadable = false;
        double hexX = left + HexStartChar * CharWidth;
        double textX = left + (HexStartChar + (_row.ShowHex ? _row.TextStart : 0)) * CharWidth;
        char[] hex = new char[Math.Max(1, _row.ShowHex ? _row.HexWidth : 0)];
        string digits = _view.LowercaseHex ? "0123456789abcdef" : "0123456789ABCDEF";
        for (int r = 0; r < rows; r++)
        {
            int first = r * BytesPerRow;
            int n = (int)Math.Min(BytesPerRow, bytes - first);
            double baseline = BodyTop + r * LineHeight + FontSize * 0.9;
            double top = BodyTop + r * LineHeight;
            if (_view.ShowOffsetColumn)
            {
                page.Runs.Add(new PrintRun(left, baseline, _offsets.Column(offset0 + first), PrintFontKind.Primary, FontSize, PrintColor.Black));
            }

            if (_row.ShowHex)
            {
                Array.Fill(hex, ' ');
                for (int c = 0; c < n; c++)
                {
                    (int start, _) = _row.ByteSpan(c, n);
                    long at = first + c;
                    bool bad = states[lookback + at] != ByteState.Valid;
                    hadUnreadable |= bad;
                    byte value = buffer[lookback + at];
                    hex[start] = bad ? '?' : digits[value >> 4];
                    hex[start + 1] = bad ? '?' : digits[value & 15];
                }

                page.Runs.Add(new PrintRun(hexX, baseline, new string(hex).TrimEnd(), PrintFontKind.Primary, FontSize, PrintColor.Black));
            }

            // 強調 (背景・網掛け・下線)。
            for (int c = 0; c < n; c++)
            {
                int at = first + c;
                PrintColor? fill = null;
                if (highlight[at] is { } h)
                {
                    fill = color ? h : PrintColor.Shade;
                }

                if (modified[at] && color)
                {
                    fill = PrintColor.ModifiedColor;
                }

                if (fill is { } f)
                {
                    if (_row.ShowHex)
                    {
                        (int start, _) = _row.ByteSpan(c, n);
                        page.Rects.Add(new PrintRect(hexX + start * CharWidth, top, 2 * CharWidth, LineHeight, f));
                    }

                    if (_row.ShowText)
                    {
                        page.Rects.Add(new PrintRect(textX + c * CharWidth, top, CharWidth, LineHeight, f));
                    }
                }

                if (modified[at] && !color)
                {
                    double y = baseline + FontSize * 0.12;
                    if (_row.ShowHex)
                    {
                        (int start, _) = _row.ByteSpan(c, n);
                        page.Lines.Add(new PrintLine(hexX + start * CharWidth, y, hexX + (start + 2) * CharWidth, y, 0.6, PrintColor.Black));
                    }

                    if (_row.ShowText)
                    {
                        page.Lines.Add(new PrintLine(textX + c * CharWidth, y, textX + (c + 1) * CharWidth, y, 0.6, PrintColor.Black));
                    }
                }
            }

            if (_row.ShowText)
            {
                AddTextColumn(page, cells, states, lookback, first, n, textX, baseline);
            }
        }

        page.HadUnreadable = hadUnreadable;
        AddHeaderFooter(page, data, fields, printedIndex);
        return page;
    }

    private void AddTextColumn(PrintPage page, TextCell[] cells, ByteState[] states, int lookback, int first, int n, double textX, double baseline)
    {
        var run = new StringBuilder();
        int runStart = 0;

        void Flush()
        {
            if (run.Length > 0)
            {
                page.Runs.Add(new PrintRun(textX + runStart * CharWidth, baseline, run.ToString().TrimEnd(), PrintFontKind.Primary, FontSize, PrintColor.Black));
                run.Clear();
            }
        }

        for (int c = 0; c < n; c++)
        {
            TextCell cell = cells[first + c];
            string text = cell.Kind switch
            {
                TextCellKind.Char => cell.Text,
                TextCellKind.Continuation => " ",
                TextCellKind.NonPrintable or TextCellKind.Invalid => cell.Text.Length > 0 ? cell.Text : ".",
                _ => states[lookback + first + c] == ByteState.Valid ? " " : "?",
            };
            if (text.Length == 0)
            {
                text = " ";
            }

            int cp = char.ConvertToUtf32(text, 0);
            bool single = text.Length == char.ConvertFromUtf32(cp).Length && text.Length <= 2;
            if (single && _fonts.PrimaryHas(cp) && !cell.Wide)
            {
                if (run.Length == 0)
                {
                    runStart = c;
                }

                run.Append(text);
                continue;
            }

            Flush();
            if (text != " ")
            {
                PrintFontKind kind = _fonts.PrimaryHas(cp) ? PrintFontKind.Primary : PrintFontKind.Substitute;
                page.Runs.Add(new PrintRun(textX + c * CharWidth, baseline, text, kind, FontSize, PrintColor.Black));
            }
        }

        Flush();
    }

    private void AddHeaderFooter(PrintPage page, IPrintData data, PrintFields fields, long printedIndex)
    {
        string range = RangeLabel(printedIndex);
        int number = page.Number;
        int count = page.Count;
        string Expand(string t) => HeaderFooterFormatter.Expand(t, fields, number, count, range);
        double left = MarginLeft;
        double right = PageWidth - MarginRight;
        if (!_settings.Header.IsEmpty)
        {
            double baseline = MarginTop + FontSize * 0.9;
            AddSlots(page, Expand(_settings.Header.Left), Expand(_settings.Header.Center), Expand(_settings.Header.Right), left, right, baseline);
        }

        if (!_settings.Footer.IsEmpty)
        {
            double baseline = PageHeight - MarginBottom - LineHeight + FontSize * 0.9;
            AddSlots(page, Expand(_settings.Footer.Left), Expand(_settings.Footer.Center), Expand(_settings.Footer.Right), left, right, baseline);
        }
    }

    private void AddSlots(PrintPage page, string leftText, string centerText, string rightText, double left, double right, double baseline)
    {
        if (leftText.Length > 0)
        {
            AddString(page, leftText, left, baseline);
        }

        if (centerText.Length > 0)
        {
            double width = MeasureString(centerText);
            AddString(page, centerText, left + ((right - left) - width) / 2, baseline);
        }

        if (rightText.Length > 0)
        {
            AddString(page, rightText, right - MeasureString(rightText), baseline);
        }
    }

    private double MeasureString(string text)
    {
        double width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            width += _fonts.AdvanceEm(rune.Value) * FontSize;
        }

        return width;
    }

    /// <summary>文字列を置く。主フォントにない文字は代替フォントの別の文字列にして、それぞれの幅で進める。</summary>
    private void AddString(PrintPage page, string text, double x, double baseline)
    {
        var run = new StringBuilder();
        double runX = x;
        double cursor = x;
        void Flush()
        {
            if (run.Length > 0)
            {
                page.Runs.Add(new PrintRun(runX, baseline, run.ToString(), PrintFontKind.Primary, FontSize, PrintColor.Black));
                run.Clear();
            }
        }

        foreach (Rune rune in text.EnumerateRunes())
        {
            double advance = _fonts.AdvanceEm(rune.Value) * FontSize;
            if (_fonts.PrimaryHas(rune.Value))
            {
                if (run.Length == 0)
                {
                    runX = cursor;
                }

                run.Append(rune.ToString());
            }
            else
            {
                Flush();
                page.Runs.Add(new PrintRun(cursor, baseline, rune.ToString(), PrintFontKind.Substitute, FontSize, PrintColor.Black));
            }

            cursor += advance;
        }

        Flush();
    }
}
