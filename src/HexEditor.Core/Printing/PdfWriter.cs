using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace HexEditor.Core.Printing;

/// <summary>PDF の文書情報 (TOOL-03 の仕様 3)。</summary>
public sealed record PdfInfo(string Title, string Creator, DateTimeOffset Created);

/// <summary>PDF に書く 1 つの書体 (使った文字だけをサブセットで埋め込む)。</summary>
public sealed class PdfFont
{
    private readonly SortedDictionary<int, int> _glyphToCodePoint = [];

    internal PdfFont(int index, FontFace face, int objectNumber)
    {
        Index = index;
        Face = face;
        ObjectNumber = objectNumber;
    }

    public int Index { get; }

    public FontFace Face { get; }

    internal int ObjectNumber { get; }

    internal IReadOnlyDictionary<int, int> UsedGlyphs => _glyphToCodePoint;

    /// <summary>この書体で表せるか (グリフがあるか)。</summary>
    public bool HasGlyph(int codePoint) => Face.Font.GlyphIndex(codePoint) != 0;

    /// <summary>符号位置のグリフ番号を使ったものとして記録して返す。なければ 0 (.notdef)。</summary>
    internal int Use(int codePoint)
    {
        int gid = Face.Font.GlyphIndex(codePoint);
        if (gid != 0 && !_glyphToCodePoint.ContainsKey(gid))
        {
            _glyphToCodePoint[gid] = codePoint;
        }

        return gid;
    }
}

/// <summary>
/// PDF をページごとにストリームへ書き出す (TOOL-03)。ページの内容は 1 ページ書くたびに圧縮して出力するため、メモリ使用量はページ数に比例しない
/// (ページの目録の番号とフォントの使用状況だけを覚える)。文字は画像ではなくテキストとして書き (Identity-H の CID フォント、ToUnicode 付き)、
/// フォントは使った文字だけを埋め込む。出力のストリームは移動できなくてよい。
/// </summary>
public sealed class PdfWriter : IDisposable
{
    private const int PagesPerNode = 100;

    private readonly Stream _output;
    private readonly PdfInfo _info;
    private readonly List<long> _offsets = [0];
    private readonly List<PdfFont> _fonts = [];
    private readonly List<int> _pageObjects = [];
    private readonly List<int> _nodeObjects = [];
    private readonly List<(string Title, int Page)> _outline = [];
    private readonly int _catalog;
    private readonly int _pagesRoot;
    private readonly int _resources;
    private long _position;
    private bool _finished;

    public PdfWriter(Stream output, PdfInfo info)
    {
        _output = output;
        _info = info;
        Write("%PDF-1.7\n%âãÏÓ\n");
        _catalog = Reserve();
        _pagesRoot = Reserve();
        _resources = Reserve();
    }

    /// <summary>書いたページの数。</summary>
    public int PageCount => _pageObjects.Count;

    /// <summary>書体を加える (サブセットの埋め込みは <see cref="Finish"/> で行う)。</summary>
    public PdfFont AddFont(FontFace face)
    {
        var font = new PdfFont(_fonts.Count, face, Reserve());
        _fonts.Add(font);
        return font;
    }

    /// <summary>しおりを加える (<paramref name="page"/> は 0 から始まるページの番号)。</summary>
    public void AddBookmark(string title, int page) => _outline.Add((title, page));

    /// <summary>ページを書き始める。ページの内容を作って <see cref="PdfPage.End"/> を呼ぶ。</summary>
    public PdfPage BeginPage(double width, double height) => new(this, width, height);

    // ---- 低レベルの出力 ----

    private int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    private void Write(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        _output.Write(bytes);
        _position += bytes.Length;
    }

    private void Write(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        _position += bytes.Length;
    }

    private void BeginObject(int number)
    {
        _offsets[number] = _position;
        Write($"{number} 0 obj\n");
    }

    private void EndObject() => Write("\nendobj\n");

    private void WriteStreamObject(int number, string dictionary, byte[] data, bool compress = true)
    {
        byte[] payload = data;
        string filter = string.Empty;
        if (compress)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                z.Write(data);
            }

            payload = ms.ToArray();
            filter = " /Filter /FlateDecode";
        }

        BeginObject(number);
        Write($"<< {dictionary}{filter} /Length {payload.Length} >>\nstream\n");
        Write(payload);
        Write("\nendstream");
        EndObject();
    }

    internal int AddPage(double width, double height, byte[] content)
    {
        int contentObject = Reserve();
        int pageObject = Reserve();
        int index = _pageObjects.Count;
        int node = index / PagesPerNode;
        if (node >= _nodeObjects.Count)
        {
            _nodeObjects.Add(Reserve());
        }

        WriteStreamObject(contentObject, string.Empty, content);
        BeginObject(pageObject);
        Write(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /Page /Parent {_nodeObjects[node]} 0 R /MediaBox [0 0 {Num(width)} {Num(height)}] /Resources {_resources} 0 R /Contents {contentObject} 0 R >>"));
        EndObject();
        _pageObjects.Add(pageObject);
        return index;
    }

    internal static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>フォント・しおり・文書情報・目録を書いて終える。</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        // フォント。
        var fontNames = new StringBuilder();
        foreach (PdfFont font in _fonts)
        {
            WriteFont(font);
            fontNames.Append($"/F{font.Index} {font.ObjectNumber} 0 R ");
        }

        BeginObject(_resources);
        Write($"<< /Font << {fontNames}>> /ProcSet [/PDF /Text] >>");
        EndObject();

        // ページの目録。
        for (int node = 0; node < _nodeObjects.Count; node++)
        {
            var kids = new StringBuilder();
            int first = node * PagesPerNode;
            int count = Math.Min(PagesPerNode, _pageObjects.Count - first);
            for (int i = 0; i < count; i++)
            {
                kids.Append(_pageObjects[first + i]).Append(" 0 R ");
            }

            BeginObject(_nodeObjects[node]);
            Write($"<< /Type /Pages /Parent {_pagesRoot} 0 R /Kids [{kids}] /Count {count} >>");
            EndObject();
        }

        BeginObject(_pagesRoot);
        Write($"<< /Type /Pages /Kids [{string.Join(' ', _nodeObjects.Select(n => n + " 0 R"))}] /Count {_pageObjects.Count} >>");
        EndObject();

        // しおり。
        int outlines = 0;
        if (_outline.Count > 0)
        {
            outlines = Reserve();
            int[] items = [.. _outline.Select(_ => Reserve())];
            BeginObject(outlines);
            Write($"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {items.Length} >>");
            EndObject();
            for (int i = 0; i < items.Length; i++)
            {
                (string title, int page) = _outline[i];
                BeginObject(items[i]);
                string prev = i > 0 ? $" /Prev {items[i - 1]} 0 R" : string.Empty;
                string next = i + 1 < items.Length ? $" /Next {items[i + 1]} 0 R" : string.Empty;
                Write($"<< /Title {TextString(title)} /Parent {outlines} 0 R{prev}{next} /Dest [{_pageObjects[Math.Min(page, _pageObjects.Count - 1)]} 0 R /Fit] >>");
                EndObject();
            }
        }

        BeginObject(_catalog);
        Write($"<< /Type /Catalog /Pages {_pagesRoot} 0 R{(outlines > 0 ? $" /Outlines {outlines} 0 R /PageMode /UseOutlines" : string.Empty)} >>");
        EndObject();

        int info = Reserve();
        BeginObject(info);
        Write($"<< /Title {TextString(_info.Title)} /Creator {TextString(_info.Creator)} /Producer {TextString(_info.Creator)} /CreationDate (D:{DateString(_info.Created)}) >>");
        EndObject();

        long xref = _position;
        Write($"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
        for (int i = 1; i < _offsets.Count; i++)
        {
            Write($"{_offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {_offsets.Count} /Root {_catalog} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        _output.Flush();
    }

    private static string DateString(DateTimeOffset date)
    {
        TimeSpan offset = date.Offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return string.Create(CultureInfo.InvariantCulture, $"{date:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
    }

    /// <summary>PDF のテキスト文字列 (UTF-16BE の 16 進)。</summary>
    internal static string TextString(string text)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (char c in text)
        {
            sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }

    private void WriteFont(PdfFont font)
    {
        TrueTypeFont ttf = font.Face.Font;
        var used = font.UsedGlyphs;
        byte[] subset = ttf.Subset(used.Keys);
        string tag = SubsetTag(used.Keys);
        string baseName = tag + "+" + new string([.. (ttf.FamilyName.Length > 0 ? ttf.FamilyName : "Font").Where(c => c is > ' ' and < '\u007f' && c is not ('(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%'))]);
        int type0 = font.ObjectNumber;
        int cid = Reserve();
        int descriptor = Reserve();
        int file = Reserve();
        int toUnicode = Reserve();

        BeginObject(type0);
        Write($"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
        EndObject();

        double scale = 1000.0 / ttf.UnitsPerEm;
        var widths = new StringBuilder();
        foreach (int gid in used.Keys)
        {
            widths.Append(gid).Append(" [").Append(Math.Round(ttf.Advance(gid) * scale).ToString(CultureInfo.InvariantCulture)).Append("] ");
        }

        BeginObject(cid);
        Write($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseName} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptor} 0 R /DW {Math.Round(ttf.Advance(0) * scale).ToString(CultureInfo.InvariantCulture)} /W [{widths}] /CIDToGIDMap /Identity >>");
        EndObject();

        int flags = 32 | (ttf.IsFixedPitch ? 1 : 0);
        BeginObject(descriptor);
        Write(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /FontDescriptor /FontName /{baseName} /Flags {flags} /FontBBox [{Math.Round(ttf.XMin * scale)} {Math.Round(ttf.YMin * scale)} {Math.Round(ttf.XMax * scale)} {Math.Round(ttf.YMax * scale)}] " +
            $"/ItalicAngle 0 /Ascent {Math.Round(ttf.Ascent * scale)} /Descent {Math.Round(ttf.Descent * scale)} /CapHeight {Math.Round(ttf.CapHeight * scale)} /StemV 80 /FontFile2 {file} 0 R >>"));
        EndObject();

        WriteStreamObject(file, $"/Length1 {subset.Length}", subset);
        WriteStreamObject(toUnicode, string.Empty, Encoding.ASCII.GetBytes(ToUnicodeCMap(used)));
    }

    private static string SubsetTag(IEnumerable<int> glyphs)
    {
        uint hash = 2166136261;
        foreach (int g in glyphs)
        {
            hash = (hash ^ (uint)g) * 16777619;
        }

        char[] tag = new char[6];
        for (int i = 0; i < 6; i++)
        {
            tag[i] = (char)('A' + hash % 26);
            hash = hash / 26 + 0x9E3779B1u * (uint)(i + 1);
        }

        return new string(tag);
    }

    private static string ToUnicodeCMap(IReadOnlyDictionary<int, int> glyphs)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = glyphs.ToList();
        for (int i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach ((int gid, int cp) in chunk)
            {
                sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (char c in char.ConvertFromUtf32(cp))
                {
                    sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                }

                sb.Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (!_finished)
        {
            Finish();
        }
    }
}

/// <summary>PDF の 1 ページの内容。座標は点 (pt) で、左上が原点・下向きが正 (PDF の下向きでない座標系へは自分で変換する)。</summary>
public sealed class PdfPage
{
    private readonly PdfWriter _writer;
    private readonly StringBuilder _content = new();

    internal PdfPage(PdfWriter writer, double width, double height)
    {
        _writer = writer;
        Width = width;
        Height = height;
    }

    public double Width { get; }

    public double Height { get; }

    private static string N(double v) => PdfWriter.Num(v);

    /// <summary>塗りつぶしの色 (0〜1)。</summary>
    public void FillColor(double r, double g, double b) => _content.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n");

    /// <summary>線の色 (0〜1)。</summary>
    public void StrokeColor(double r, double g, double b) => _content.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" RG\n");

    /// <summary>塗りつぶした四角 (左上 x, y・幅・高さ)。</summary>
    public void FillRectangle(double x, double y, double width, double height) =>
        _content.Append(N(x)).Append(' ').Append(N(Height - y - height)).Append(' ').Append(N(width)).Append(' ').Append(N(height)).Append(" re f\n");

    /// <summary>線 (太さ <paramref name="thickness"/>)。</summary>
    public void Line(double x1, double y1, double x2, double y2, double thickness)
    {
        _content.Append(N(thickness)).Append(" w ").Append(N(x1)).Append(' ').Append(N(Height - y1)).Append(" m ")
            .Append(N(x2)).Append(' ').Append(N(Height - y2)).Append(" l S\n");
    }

    /// <summary>
    /// 文字列を書く (<paramref name="baseline"/> は基準線の y)。符号位置ごとにグリフ番号へ変換し、書体にない文字は .notdef になる。
    /// </summary>
    public void Text(PdfFont font, double size, double x, double baseline, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var hex = new StringBuilder(text.Length * 4);
        foreach (Rune rune in text.EnumerateRunes())
        {
            hex.Append(font.Use(rune.Value).ToString("X4", CultureInfo.InvariantCulture));
        }

        _content.Append("BT /F").Append(font.Index).Append(' ').Append(N(size)).Append(" Tf ").Append(N(x)).Append(' ').Append(N(Height - baseline))
            .Append(" Td <").Append(hex).Append("> Tj ET\n");
    }

    /// <summary>ページを書き出して、ページの番号 (0 から) を返す。</summary>
    public int End() => _writer.AddPage(Width, Height, Encoding.ASCII.GetBytes(_content.ToString()));
}
