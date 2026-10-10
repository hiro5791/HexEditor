using System.Globalization;
using System.Text;
using HexEditor.Core.Clipboard;
using HexEditor.Core.View;

namespace HexEditor.Core.Formats;

/// <summary>
/// ダンプのエクスポート (TOOL-10): 画面と同じ形の Hex ダンプ (オフセット | Hex | テキスト) を、テキスト・HTML・RTF・TeX・Markdown で書く。
/// データは先頭から順に渡し、1 行ずつ書き出す (ストリーム)。テキストの列の文字は形式に合わせてエスケープし、表示できない文字は <c>.</c>
/// にする (仕様 3)。色は HTML では CSS のクラスで付け、色に加えて下線・枠などの書式も付ける (仕様 4)。
/// </summary>
public sealed class DumpExporter
{
    private readonly TextWriter _w;
    private readonly DumpOptions _o;
    private readonly string _format;
    private readonly string _nl;
    private readonly byte[] _row;
    private readonly long _start;
    private readonly long _length;
    private int _count;
    private long _rowOffset;
    private bool _firstRow = true;
    private int _highlight;

    /// <summary>RTF の色の表の番号 (RRGGBB → 番号。1・2 は変更バイト・ブックマークの既定の色)。</summary>
    private readonly Dictionary<string, int> _rtfColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>行ごとの強調 (色付けルール。<see cref="DumpOptions.RowHighlights"/>)。</summary>
    private IReadOnlyList<DumpHighlight> _rowHighlights = [];

    public DumpExporter(TextWriter writer, string format, DumpOptions options, string newLine, long offset, long length)
    {
        _w = writer;
        _o = options;
        _format = format;
        _nl = newLine;
        _row = new byte[Math.Clamp(options.BytesPerRow, 1, 1024)];
        _start = offset;
        _length = length;
        _rowOffset = offset;
    }

    private bool Colors => _o.Colors && _format is FormatIds.Html or FormatIds.Rtf || _format == FormatIds.Tex && _o.Tex == TexEnvironment.AlltColor && _o.Colors;

    private int OffsetDigits
    {
        get
        {
            long last = _o.BaseAddress + Math.Max(_start, _start + _length - 1);
            return _o.OffsetRadix switch
            {
                OffsetRadix.Decimal => Math.Max(8, last.ToString(CultureInfo.InvariantCulture).Length),
                OffsetRadix.Octal => Math.Max(8, Convert.ToString(last, 8).Length),
                _ => Math.Max(8, last.ToString("X", CultureInfo.InvariantCulture).Length),
            };
        }
    }

    /// <summary>まとめて書く (テスト・プレビュー用)。</summary>
    public static string Format(string format, DumpOptions options, ReadOnlySpan<byte> data, long offset = 0, string newLine = "\r\n")
    {
        var sw = new StringWriter(CultureInfo.InvariantCulture);
        var exporter = new DumpExporter(sw, format, options, newLine, offset, data.Length);
        exporter.Begin();
        exporter.Write(data);
        exporter.End();
        return sw.ToString();
    }

    public void Begin()
    {
        switch (_format)
        {
            case FormatIds.Html:
                WriteHtmlHead();
                break;
            case FormatIds.Rtf:
                WriteRtfHead();
                break;
            case FormatIds.Tex when _o.TexDocument:
                // 単独で処理できる文書 (pdflatex で直接処理できる。TOOL-10 の受け入れ基準 3)。
                _w.Write($"\\documentclass{{article}}{_nl}\\usepackage{{alltt}}{_nl}\\usepackage{{xcolor}}{_nl}");
                _w.Write($"\\usepackage[margin=1cm,landscape]{{geometry}}{_nl}\\begin{{document}}{_nl}\\footnotesize{_nl}");
                _w.Write(_o.Tex == TexEnvironment.AlltColor ? $"\\begin{{alltt}}{_nl}" : $"\\begin{{verbatim}}{_nl}");
                break;
            case FormatIds.Tex when _o.Tex == TexEnvironment.AlltColor:
                _w.Write($"% \\usepackage{{alltt}} \\usepackage{{xcolor}}{_nl}\\begin{{alltt}}{_nl}");
                break;
            case FormatIds.Tex:
                _w.Write($"\\begin{{verbatim}}{_nl}");
                break;
            case FormatIds.Markdown when _o.MarkdownTable:
                var head = new List<string>();
                var rule = new List<string>();
                if (_o.ShowOffset)
                {
                    head.Add("Offset");
                    rule.Add("---");
                }

                if (_o.ShowHex)
                {
                    head.Add("Hex");
                    rule.Add("---");
                }

                if (_o.ShowText)
                {
                    head.Add("Text");
                    rule.Add("---");
                }

                _w.Write($"| {string.Join(" | ", head)} |{_nl}| {string.Join(" | ", rule)} |{_nl}");
                break;
            case FormatIds.Markdown:
                _w.Write($"```text{_nl}");
                break;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            _row[_count++] = b;
            if (_count == _row.Length)
            {
                Row();
            }
        }
    }

    public void End()
    {
        if (_count > 0)
        {
            Row();
        }

        switch (_format)
        {
            case FormatIds.Html:
                _w.Write($"</pre>{_nl}</body>{_nl}</html>{_nl}");
                break;
            case FormatIds.Rtf:
                _w.Write($"}}{_nl}");
                break;
            case FormatIds.Tex:
                _w.Write(_o.Tex == TexEnvironment.AlltColor ? $"\\end{{alltt}}{_nl}" : $"\\end{{verbatim}}{_nl}");
                if (_o.TexDocument)
                {
                    _w.Write($"\\end{{document}}{_nl}");
                }

                break;
            case FormatIds.Markdown when !_o.MarkdownTable:
                _w.Write($"```{_nl}");
                break;
        }
    }

    // ---- 先頭部分 ----

    private void WriteHtmlHead()
    {
        // 色は CSS のクラスで付ける。色だけで伝えないよう、下線・点線の枠も付ける (00 の 11.5)。
        (string bg, string fg, string mod, string modBg, string mark, string markBg) = _o.DarkScheme
            ? ("#1E1E1E", "#D4D4D4", "#FF99A4", "#442726", "#FFD866", "#3D3519")
            : ("#FFFFFF", "#1B1B1B", "#C42B1C", "#FDE7E9", "#7A4F00", "#FFF4CE");
        _w.Write($"<!DOCTYPE html>{_nl}<html lang=\"en\">{_nl}<head>{_nl}<meta charset=\"utf-8\">{_nl}");
        _w.Write($"<title>{HtmlEscape(_o.Title)}</title>{_nl}<style>{_nl}");
        _w.Write($"body {{ background: {bg}; color: {fg}; }}{_nl}");
        _w.Write($"pre.hexdump {{ font-family: 'Cascadia Mono', Consolas, 'Courier New', monospace; font-size: 10pt; line-height: 1.3; }}{_nl}");
        _w.Write($".mod {{ color: {mod}; background-color: {modBg}; font-weight: bold; text-decoration: underline; }}{_nl}");
        _w.Write($".mark {{ color: {mark}; background-color: {markBg}; outline: 1px dotted {mark}; }}{_nl}");
        _w.Write($".rule {{ text-decoration: underline dotted; }}{_nl}");
        _w.Write($"</style>{_nl}</head>{_nl}<body>{_nl}<pre class=\"hexdump\">");
    }

    private void WriteRtfHead()
    {
        int size = (int)Math.Round(Math.Clamp(_o.RtfFontSize, 4, 72) * 2);
        _w.Write("{\\rtf1\\ansi\\ansicpg1252\\deff0");
        _w.Write($"{{\\fonttbl{{\\f0\\fmodern\\fprq1 {RtfEscape(_o.RtfFont)};}}}}");
        // 色の表: 1 は変更バイト、2 はブックマークの既定の色、3 以降は強調の色 (ブックマーク・色付けルールの色。最大 200 色)。
        // 色付けルールの色は行ごとに求めるため、表に入れられるのは先に分かっている強調の色だけ (それ以外はブックマークの既定の色)。
        var table = new StringBuilder("{\\colortbl ;\\red196\\green43\\blue28;\\red122\\green79\\blue0;");
        foreach (string? color in _o.Highlights.Select(h => h.Color).Concat(_o.RuleColors))
        {
            if (_rtfColors.Count >= 200)
            {
                break;
            }

            if (ParseColor(color) is { } rgb && _rtfColors.TryAdd(RgbText(rgb), _rtfColors.Count + 3))
            {
                table.Append(CultureInfo.InvariantCulture, $"\\red{rgb >> 16 & 0xFF}\\green{rgb >> 8 & 0xFF}\\blue{rgb & 0xFF};");
            }
        }

        _w.Write(table.Append('}').ToString());
        _w.Write($"{_nl}\\f0\\fs{size} ");
    }

    // ---- 行 ----

    private string OffsetText(long offset)
    {
        long value = offset + _o.BaseAddress;
        string digits = _o.OffsetRadix switch
        {
            OffsetRadix.Decimal => value.ToString(CultureInfo.InvariantCulture),
            OffsetRadix.Octal => Convert.ToString(value, 8),
            _ => value.ToString(_o.UpperCase ? "X" : "x", CultureInfo.InvariantCulture),
        };
        return digits.PadLeft(OffsetDigits, '0');
    }

    /// <summary>テキストの列の文字。表示できない文字は '.'。TeX では ASCII 以外も '.' (pdflatex で処理できるように)。</summary>
    private char TextChar(byte b)
    {
        char c = _o.Encoding.DisplayChar(b);
        if (char.IsControl(c) || char.IsSurrogate(c) || c == '�')
        {
            return '.';
        }

        if (_format is FormatIds.Tex && c > 0x7E)
        {
            return '.';
        }

        return c;
    }

    /// <summary><c>#RRGGBB</c> (または <c>#AARRGGBB</c>) を 0xRRGGBB にする。読めなければ null。</summary>
    internal static uint? ParseColor(string? color)
    {
        if (color is not { Length: 7 or 9 } || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        return value & 0xFFFFFF;
    }

    private static string RgbText(uint rgb) => rgb.ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>その位置の強調: 先に分かっている強調 (変更バイト・ブックマーク) を優先し、なければ行ごとの強調 (色付けルール)。</summary>
    private DumpHighlight? HighlightAt(long position)
    {
        if (FixedHighlightAt(position) is { } mark)
        {
            return mark;
        }

        foreach (DumpHighlight h in _rowHighlights)
        {
            if (h.Offset <= position && position < h.Offset + h.Length)
            {
                return h;
            }
        }

        return null;
    }

    private DumpHighlight? FixedHighlightAt(long position)
    {
        IReadOnlyList<DumpHighlight> list = _o.Highlights;
        while (_highlight < list.Count && list[_highlight].Offset + list[_highlight].Length <= position)
        {
            _highlight++;
        }

        // 重なる強調は先のものを使う (一覧はオフセットの昇順)。
        for (int i = _highlight; i < list.Count && list[i].Offset <= position; i++)
        {
            if (list[i].Offset + list[i].Length > position)
            {
                return list[i];
            }
        }

        return null;
    }

    private void Row()
    {
        int perRow = _row.Length;
        string offsetText = OffsetText(_rowOffset);
        _rowHighlights = Colors && _o.RowHighlights is { } rows ? rows(_rowOffset, _count) : [];
        var hexCells = new List<(string Text, DumpHighlight? Mark)>(_count);
        for (int i = 0; i < _count; i++)
        {
            string cell = _row[i].ToString(_o.UpperCase ? "X2" : "x2", CultureInfo.InvariantCulture);
            hexCells.Add((cell, Colors ? HighlightAt(_rowOffset + i) : null));
        }

        var text = new StringBuilder(_count);
        for (int i = 0; i < _count; i++)
        {
            text.Append(TextChar(_row[i]));
        }

        if (_format == FormatIds.Markdown && _o.MarkdownTable)
        {
            WriteMarkdownTableRow(offsetText, hexCells, text.ToString());
        }
        else
        {
            // 行の終わり: RTF は行の間に \par、それ以外は各行の後に改行。
            if (!_firstRow && _format == FormatIds.Rtf)
            {
                _w.Write("\\par" + _nl);
            }

            WriteDumpRow(offsetText, hexCells, text.ToString(), perRow);
            if (_format != FormatIds.Rtf)
            {
                _w.Write(_nl);
            }
        }

        _firstRow = false;
        _rowOffset += _count;
        _count = 0;
    }

    /// <summary>グループの区切りを含めた Hex の列の幅 (文字数)。</summary>
    private int HexWidth(int cells)
    {
        int width = cells * 3 - (cells > 0 ? 1 : 0);
        if (_o.GroupSize > 0 && cells > 0)
        {
            width += (cells - 1) / _o.GroupSize;
        }

        return width;
    }

    private void WriteDumpRow(string offsetText, List<(string Text, DumpHighlight? Mark)> cells, string text, int perRow)
    {
        string sep = _o.Separator == DumpColumnSeparator.Bar ? " | " : "  ";
        bool first = true;
        void Separator()
        {
            if (!first)
            {
                _w.Write(sep);
            }

            first = false;
        }

        if (_o.ShowOffset)
        {
            Separator();
            _w.Write(Escape(offsetText));
        }

        if (_o.ShowHex)
        {
            Separator();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0)
                {
                    _w.Write(_o.GroupSize > 0 && i % _o.GroupSize == 0 ? "  " : " ");
                }

                WriteCell(cells[i].Text, cells[i].Mark);
            }

            // 最後の行は Hex の列を空白で埋めて、テキストの列の位置をそろえる。
            if (_o.ShowText)
            {
                _w.Write(new string(' ', HexWidth(perRow) - HexWidth(cells.Count)));
            }
        }

        if (_o.ShowText)
        {
            Separator();
            _w.Write(Escape(text));
        }
    }

    private void WriteCell(string cell, DumpHighlight? mark)
    {
        if (mark is not { } m)
        {
            _w.Write(cell);
            return;
        }

        switch (_format)
        {
            case FormatIds.Html:
            {
                string cls = m.Kind switch { DumpHighlightKind.Modified => "mod", DumpHighlightKind.Bookmark => "mark", _ => "rule" };
                string title = m.Kind != DumpHighlightKind.Modified && _o.BookmarkTooltips && m.Name is { Length: > 0 } name
                    ? $" title=\"{HtmlEscape(name)}\"" : string.Empty;

                // ブックマーク・色付けルールの色は半透明の背景色にする (クラスの下線・枠も付くため、色だけで伝えない。仕様 4)。
                string style = m.Kind != DumpHighlightKind.Modified && ParseColor(m.Color) is { } rgb
                    ? $" style=\"background-color: #{RgbText(rgb)}55\"" : string.Empty;
                _w.Write($"<span class=\"{cls}\"{title}{style}>{cell}</span>");
                break;
            }

            case FormatIds.Rtf:
            {
                int color = m.Kind != DumpHighlightKind.Modified && ParseColor(m.Color) is { } c && _rtfColors.TryGetValue(RgbText(c), out int index)
                    ? index : 2;
                _w.Write(m.Kind == DumpHighlightKind.Modified ? $"{{\\cf1\\b\\ul {cell}}}" : $"{{\\cf{color}\\uld {cell}}}");
                break;
            }

            case FormatIds.Tex:
            {
                string tex = m.Kind == DumpHighlightKind.Modified ? "{red}" : ParseColor(m.Color) is { } t ? $"[HTML]{{{RgbText(t)}}}" : "{blue}";
                _w.Write($"\\textcolor{tex}{{\\underline{{{cell}}}}}");
                break;
            }
            default:
                _w.Write(cell);
                break;
        }
    }

    private void WriteMarkdownTableRow(string offsetText, List<(string Text, DumpHighlight? Mark)> cells, string text)
    {
        var parts = new List<string>();
        if (_o.ShowOffset)
        {
            parts.Add(offsetText);
        }

        if (_o.ShowHex)
        {
            parts.Add(string.Join(' ', cells.Select(c => c.Text)));
        }

        if (_o.ShowText)
        {
            parts.Add(MarkdownCellEscape(text));
        }

        _w.Write($"| {string.Join(" | ", parts)} |{_nl}");
    }

    // ---- エスケープ ----

    /// <summary>テキストの列の文字列を形式に合わせてエスケープする (仕様 3)。</summary>
    private string Escape(string text) => _format switch
    {
        FormatIds.Html => HtmlEscape(text),
        FormatIds.Rtf => RtfEscape(text),
        FormatIds.Tex when _o.Tex == TexEnvironment.AlltColor => AlltEscape(text),
        _ => text,
    };

    internal static string HtmlEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    internal static string RtfEscape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '{' or '}')
            {
                sb.Append('\\').Append(c);
            }
            else if (c > 127)
            {
                sb.Append("\\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary><c>alltt</c> 環境では \ { } だけが特別 (他の文字はそのまま出る)。</summary>
    private static string AlltEscape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\\' => "\\textbackslash{}",
                '{' => "\\{",
                '}' => "\\}",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    /// <summary>
    /// Markdown の表のセル: ASCII の記号をバックスラッシュでエスケープし (縦線を列の区切りにしない、書式として解釈させない)、空白は
    /// 文字参照にする (セルの前後の空白は取り除かれるため)。
    /// </summary>
    private static string MarkdownCellEscape(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            if (c == ' ')
            {
                sb.Append("&#32;");
            }
            else if (c < 128 && char.IsPunctuation(c) || c is '`' or '|' or '<' or '>' or '^' or '$' or '+' or '=' or '~')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
