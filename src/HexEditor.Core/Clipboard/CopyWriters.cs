using System.Globalization;
using System.Text;

namespace HexEditor.Core.Clipboard;

/// <summary>1 つの形式の出力。<see cref="Write"/> には先頭から順にデータを渡す (<paramref name="at"/> はドキュメント上の位置)。</summary>
internal abstract class FormatWriter(TextWriter writer, CopyOptions options)
{
    protected TextWriter W { get; } = writer;

    protected CopyOptions O { get; } = options;

    protected string NL => O.NewLine;

    public List<CopyNote> Notes { get; } = [];

    public virtual void Begin()
    {
    }

    public abstract void Write(ReadOnlySpan<byte> data, long at);

    public virtual void End()
    {
    }
}

/// <summary>
/// 要素を並べる形式 (Hex 文字列・数値列・配列)。1 行に <c>perLine</c> 個の要素を並べ、全体が 1 行に収まる場合は 1 行の書式、
/// 収まらない場合は複数行の書式にする。要素の大きさが 2 バイト以上で端数がある場合は、残りを 0 で埋めた要素にする (EDIT-25 の仕様 5)。
/// </summary>
internal sealed class ListWriter(TextWriter writer, CopyOptions options, long length, int elementSize, int perLine, Func<ulong, string> element)
    : FormatWriter(writer, options)
{
    private readonly byte[] _pending = new byte[8];
    private int _pendingCount;
    private long _index;
    private bool _multi;

    public string Separator { get; init; } = " ";

    /// <summary>行の終わり (最後の行を除く) に付ける文字 (配列では <c>,</c>)。</summary>
    public string LineEnd { get; init; } = string.Empty;

    /// <summary>複数行のとき、最後の行の終わりに付ける文字 (Go の配列では <c>,</c>)。</summary>
    public string LastLineEnd { get; init; } = string.Empty;

    public string SingleOpen { get; init; } = string.Empty;

    public string SingleClose { get; init; } = string.Empty;

    /// <summary>複数行のときの書き出し (改行を含めて書く)。</summary>
    public string MultiOpen { get; init; } = string.Empty;

    /// <summary>複数行のときの閉じ。空でなければ改行の後に書く。</summary>
    public string MultiClose { get; init; } = string.Empty;

    public string LineIndent { get; init; } = string.Empty;

    public string LinePrefix { get; init; } = string.Empty;

    /// <summary>複数行のときの最初の行の頭 (MASM のラベル付きの行)。null なら他の行と同じ。</summary>
    public string? FirstLinePrefix { get; init; }

    public bool AlwaysMulti { get; init; }

    private long Count => (length + elementSize - 1) / elementSize;

    public override void Begin()
    {
        _multi = AlwaysMulti || Count > perLine;
        W.Write(_multi ? MultiOpen : SingleOpen);
    }

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        if (elementSize == 1 && _pendingCount == 0)
        {
            foreach (byte b in data)
            {
                Emit(b);
            }

            return;
        }

        foreach (byte b in data)
        {
            _pending[_pendingCount++] = b;
            if (_pendingCount == elementSize)
            {
                EmitPending();
            }
        }
    }

    public override void End()
    {
        if (_pendingCount > 0)
        {
            Array.Clear(_pending, _pendingCount, elementSize - _pendingCount);
            EmitPending();
            Notes.Add(CopyNote.PaddedLastElement);
        }

        if (_multi)
        {
            W.Write(LastLineEnd);
            if (MultiClose.Length > 0)
            {
                W.Write(NL);
                W.Write(MultiClose);
            }
        }
        else
        {
            W.Write(SingleClose);
        }
    }

    private void EmitPending()
    {
        ulong value = 0;
        for (int i = 0; i < elementSize; i++)
        {
            int shift = O.BigEndian ? (elementSize - 1 - i) * 8 : i * 8;
            value |= (ulong)_pending[i] << shift;
        }

        _pendingCount = 0;
        Emit(value);
    }

    private void Emit(ulong value)
    {
        if (_index % perLine == 0)
        {
            if (_index > 0)
            {
                W.Write(LineEnd);
                W.Write(NL);
            }

            if (_multi)
            {
                W.Write(_index == 0 && FirstLinePrefix is not null ? FirstLinePrefix : LineIndent + LinePrefix);
            }
        }
        else
        {
            W.Write(Separator);
        }

        W.Write(element(value));
        _index++;
    }
}

/// <summary>Python のバイト列リテラル <c>b"\xde\xad"</c> (EDIT-25 の Python の別表記)。</summary>
internal sealed class PythonBytesWriter(TextWriter writer, CopyOptions options, long length) : FormatWriter(writer, options)
{
    private long _index;
    private bool _multi;

    public override void Begin()
    {
        _multi = length > O.BytesPerLine;
        W.Write(_multi ? $"{O.VariableName} = ({NL}" : $"{O.VariableName} = b\"");
    }

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            if (_multi && _index % O.BytesPerLine == 0)
            {
                if (_index > 0)
                {
                    W.Write('"');
                    W.Write(NL);
                }

                W.Write(O.Indent);
                W.Write("b\"");
            }

            W.Write("\\x");
            W.Write(b.ToString("x2", CultureInfo.InvariantCulture));
            _index++;
        }
    }

    public override void End() => W.Write(_multi ? $"\"{NL})" : "\"");
}

/// <summary>Base64 (標準 / URL 安全、76 文字での改行)。</summary>
internal sealed class Base64Writer(TextWriter writer, CopyOptions options, bool wrap = true) : FormatWriter(writer, options)
{
    private readonly byte[] _carry = new byte[3];
    private int _carryCount;
    private int _column;

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        while (_carryCount > 0 && _carryCount < 3 && !data.IsEmpty)
        {
            _carry[_carryCount++] = data[0];
            data = data[1..];
        }

        if (_carryCount == 3)
        {
            Put(Convert.ToBase64String(_carry));
            _carryCount = 0;
        }

        int full = data.Length / 3 * 3;
        for (int i = 0; i < full; i += 3 * 4096)
        {
            Put(Convert.ToBase64String(data.Slice(i, Math.Min(3 * 4096, full - i))));
        }

        data[full..].CopyTo(_carry);
        _carryCount += data.Length - full;
    }

    public override void End()
    {
        if (_carryCount > 0)
        {
            string last = Convert.ToBase64String(_carry.AsSpan(0, _carryCount));
            Put(O.Base64Padding ? last : last.TrimEnd('='));
        }
    }

    /// <summary>1 行の文字数 (0 は改行なし)。</summary>
    private int LineLength => !wrap ? 0 : O.Base64LineLength ?? (O.Base64Wrap ? 76 : 0);

    private void Put(string text)
    {
        if (O.Base64UrlSafe)
        {
            text = text.Replace('+', '-').Replace('/', '_');
        }

        int lineLength = LineLength;
        if (lineLength <= 0)
        {
            W.Write(text);
            return;
        }

        foreach (char c in text)
        {
            if (_column == lineLength)
            {
                W.Write(NL);
                _column = 0;
            }

            W.Write(c);
            _column++;
        }
    }
}

/// <summary>決まった文字数で改行しながら書く (Base32・Ascii85 の 1 行の文字数。TOOL-07)。</summary>
internal sealed class WrappingWriter(TextWriter inner, int lineLength, string newLine)
{
    private int _column;

    public void Write(char c)
    {
        if (lineLength > 0 && _column == lineLength)
        {
            inner.Write(newLine);
            _column = 0;
        }

        inner.Write(c);
        _column++;
    }

    public void Write(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            Write(c);
        }
    }
}

/// <summary>Base32 (RFC 4648)、Base32hex。</summary>
internal sealed class Base32Writer(TextWriter writer, CopyOptions options) : FormatWriter(writer, options)
{
    public const string Standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public const string HexAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUV";

    private readonly byte[] _carry = new byte[5];
    private readonly WrappingWriter _out = new(writer, options.Base32LineLength, options.NewLine);
    private int _carryCount;

    private string Alphabet => O.Base32Hex ? HexAlphabet : Standard;

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            _carry[_carryCount++] = b;
            if (_carryCount == 5)
            {
                Group(5);
                _carryCount = 0;
            }
        }
    }

    public override void End()
    {
        if (_carryCount > 0)
        {
            Array.Clear(_carry, _carryCount, 5 - _carryCount);
            Group(_carryCount);
        }
    }

    private void Group(int bytes)
    {
        ulong v = 0;
        for (int i = 0; i < 5; i++)
        {
            v = (v << 8) | _carry[i];
        }

        int chars = bytes switch { 1 => 2, 2 => 4, 3 => 5, 4 => 7, _ => 8 };
        string alphabet = Alphabet;
        for (int i = 0; i < 8; i++)
        {
            if (i < chars)
            {
                _out.Write(alphabet[(int)((v >> (35 - i * 5)) & 31)]);
            }
            else if (O.Base32Padding)
            {
                _out.Write('=');
            }
        }
    }
}

/// <summary>Ascii85 (Adobe 形式。<c>&lt;~</c> <c>~&gt;</c> の有無を選べる)、Z85。</summary>
internal sealed class Ascii85Writer(TextWriter writer, CopyOptions options) : FormatWriter(writer, options)
{
    public const string Z85Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-:+=^!/*?&<>()[]{}@%$#";

    private readonly byte[] _carry = new byte[4];
    private readonly WrappingWriter _out = new(writer, options.Ascii85LineLength, options.NewLine);
    private int _carryCount;

    private bool Z85 => O.Ascii85 == Ascii85Variant.Z85;

    public override void Begin()
    {
        if (!Z85 && O.Ascii85Delimiters)
        {
            _out.Write("<~");
        }
    }

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            _carry[_carryCount++] = b;
            if (_carryCount == 4)
            {
                Group(4);
                _carryCount = 0;
            }
        }
    }

    public override void End()
    {
        if (_carryCount > 0)
        {
            Array.Clear(_carry, _carryCount, 4 - _carryCount);
            Group(_carryCount);
        }

        if (!Z85 && O.Ascii85Delimiters)
        {
            _out.Write("~>");
        }
    }

    private void Group(int bytes)
    {
        uint v = (uint)(_carry[0] << 24 | _carry[1] << 16 | _carry[2] << 8 | _carry[3]);
        if (!Z85 && bytes == 4 && v == 0)
        {
            _out.Write('z');
            return;
        }

        Span<char> chars = stackalloc char[5];
        for (int i = 4; i >= 0; i--)
        {
            uint digit = v % 85;
            v /= 85;
            chars[i] = Z85 ? Z85Alphabet[(int)digit] : (char)(digit + 33);
        }

        _out.Write(chars[..(bytes + 1)]);
    }
}

/// <summary>UUEncode / XXEncode (begin 行から end までの完全な形式)。</summary>
internal sealed class UuWriter(TextWriter writer, CopyOptions options, bool xx) : FormatWriter(writer, options)
{
    public const string XxAlphabet = "+-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int LineBytes = 45;

    private readonly byte[] _line = new byte[LineBytes];
    private int _count;

    public override void Begin() => W.Write($"begin {O.EncodedFileMode} {O.EncodedFileName}{NL}");

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            _line[_count++] = b;
            if (_count == LineBytes)
            {
                Line();
            }
        }
    }

    public override void End()
    {
        if (_count > 0)
        {
            Line();
        }

        W.Write(Char(0));
        W.Write(NL);
        W.Write("end");
        W.Write(NL);
    }

    private char Char(int value) => xx ? XxAlphabet[value] : value == 0 ? '`' : (char)(value + 32);

    private void Line()
    {
        W.Write(Char(_count));
        for (int i = 0; i < _count; i += 3)
        {
            int b0 = _line[i], b1 = i + 1 < _count ? _line[i + 1] : 0, b2 = i + 2 < _count ? _line[i + 2] : 0;
            W.Write(Char(b0 >> 2));
            W.Write(Char(((b0 & 3) << 4) | (b1 >> 4)));
            W.Write(Char(((b1 & 15) << 2) | (b2 >> 6)));
            W.Write(Char(b2 & 63));
        }

        W.Write(NL);
        _count = 0;
    }
}

/// <summary>
/// Quoted-Printable (RFC 2045。1 行 76 文字)。バイナリを元に戻せるよう、空白・タブ・CR・LF もすべて <c>=XX</c> にする。
/// </summary>
internal sealed class QuotedPrintableWriter(TextWriter writer, CopyOptions options) : FormatWriter(writer, options)
{
    private int _column;

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            bool literal = b is >= 33 and <= 126 && b != '=';
            int width = literal ? 1 : 3;
            if (_column + width > Math.Max(4, O.QuotedPrintableLineLength) - 1)
            {
                W.Write('=');
                W.Write(NL);
                _column = 0;
            }

            if (literal)
            {
                W.Write((char)b);
            }
            else
            {
                W.Write('=');
                W.Write(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            _column += width;
        }
    }
}

/// <summary>
/// 画面表示どおりのダンプ (オフセット・Hex・テキストの 3 列) と、それを使う文書形式 (HTML・RTF・Markdown・TeX)。
/// 行は選択範囲の先頭から <see cref="CopyOptions.ScreenBytesPerRow"/> バイトずつ。最後の行の Hex の列は空白で埋めてテキストの列をそろえる。
/// </summary>
internal sealed class DumpWriter(TextWriter writer, CopyOptions options, CopyFormat format, long offset, long length) : FormatWriter(writer, options)
{
    private readonly byte[] _row = new byte[Math.Max(1, options.ScreenBytesPerRow)];
    private int _count;
    private readonly long _start = offset;
    private readonly long _length = length;
    private long _rowOffset = offset;
    private int _rowIndex;
    private int _modifiedIndex;

    // RTF の色の表の番号 (色付けルールの色。0xRRGGBB → 番号)。
    private readonly Dictionary<uint, int> _rtfColors = [];

    private int RowBytes => _row.Length;

    private bool Colors => O.IncludeColors && format is CopyFormat.Html or CopyFormat.Rtf;

    private bool Table => O.Layout == DocumentLayout.Table && format is CopyFormat.Html or CopyFormat.Markdown or CopyFormat.Tex;

    private int OffsetDigits
    {
        get
        {
            long last = Math.Max(_start, _start + _length - 1);
            return O.DecimalOffsets
                ? Math.Max(8, last.ToString(CultureInfo.InvariantCulture).Length)
                : Math.Max(8, last.ToString("X", CultureInfo.InvariantCulture).Length);
        }
    }

    public override void Begin()
    {
        string font = "font-family: 'Cascadia Mono', Consolas, monospace;";
        switch (format)
        {
            case CopyFormat.Html when Table:
                W.Write($"<table style=\"{font} border-collapse: collapse;\">{NL}<tr><th>Offset</th><th>Hex</th><th>Text</th></tr>{NL}");
                break;
            case CopyFormat.Html:
                W.Write($"<pre style=\"{font}\">");
                break;
            case CopyFormat.Rtf:
                (int r, int g, int b) = ParseColor(O.ModifiedColor);
                // 色の表: 1 は変更されたバイト、2 から色付けルールの色 (INSP-33 の仕様 8)。
                var table = new StringBuilder($"\\red{r}\\green{g}\\blue{b};");
                if (Colors && O.Coloring is { } coloring)
                {
                    foreach (uint rgb in coloring.Palette)
                    {
                        if (!_rtfColors.ContainsKey(rgb))
                        {
                            _rtfColors[rgb] = _rtfColors.Count + 2;
                            table.Append(CultureInfo.InvariantCulture, $"\\red{(rgb >> 16) & 0xFF}\\green{(rgb >> 8) & 0xFF}\\blue{rgb & 0xFF};");
                        }
                    }
                }

                W.Write($"{{\\rtf1\\ansi\\deff0{{\\fonttbl{{\\f0\\fmodern Consolas;}}}}{{\\colortbl ;{table}}}\\f0\\fs20 ");
                break;
            case CopyFormat.Markdown when Table:
                W.Write($"| Offset | Hex | Text |{NL}| --- | --- | --- |{NL}");
                break;
            case CopyFormat.Markdown:
                W.Write($"```text{NL}");
                break;
            case CopyFormat.Tex when Table:
                W.Write($"\\begin{{tabular}}{{lll}}{NL}Offset & Hex & Text \\\\{NL}\\hline{NL}");
                break;
            case CopyFormat.Tex:
                W.Write($"\\begin{{verbatim}}{NL}");
                break;
        }
    }

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        foreach (byte b in data)
        {
            _row[_count++] = b;
            if (_count == RowBytes)
            {
                Row();
            }
        }
    }

    public override void End()
    {
        if (_count > 0)
        {
            Row();
        }

        switch (format)
        {
            case CopyFormat.Html when Table:
                W.Write("</table>");
                break;
            case CopyFormat.Html:
                W.Write("</pre>");
                break;
            case CopyFormat.Rtf:
                W.Write('}');
                break;
            case CopyFormat.Markdown when !Table:
                W.Write($"{NL}```");
                break;
            case CopyFormat.Tex:
                W.Write(Table ? $"\\end{{tabular}}" : $"{NL}\\end{{verbatim}}");
                break;
        }
    }

    private void Row()
    {
        string offsetText = O.DecimalOffsets
            ? _rowOffset.ToString(CultureInfo.InvariantCulture).PadLeft(OffsetDigits, '0')
            : _rowOffset.ToString(O.UpperCase ? "X" : "x", CultureInfo.InvariantCulture).PadLeft(OffsetDigits, '0');
        var hex = new StringBuilder();
        var plain = new StringBuilder();

        // 色付けルールの色 (「色を含める」のとき。INSP-33 の仕様 8)。
        (CopyCellColor[] Hex, CopyCellColor[] Text)? ruleColors = Colors && O.Coloring is { } coloring ? coloring.Cells(_rowOffset, _count) : null;
        for (int i = 0; i < _count; i++)
        {
            string cell = CopyFormatter.Hex(_row[i], 2, O);
            if (i > 0)
            {
                hex.Append(' ');
                plain.Append(' ');
            }

            plain.Append(cell);
            bool modified = Colors && IsModified(_rowOffset + i);
            CopyCellColor color = ruleColors is { } rc && i < rc.Hex.Length ? rc.Hex[i] : default;
            hex.Append(modified || !color.IsEmpty ? Highlight(cell, modified, color) : cell);
        }

        // 最後の行は Hex の列を空白で埋めて、テキストの列の位置をそろえる。
        int pad = (RowBytes - _count) * 3;
        string text = Text();
        string? coloredText = ruleColors is { } colors ? ColoredText(colors.Text) : null;

        bool first = _rowIndex == 0;
        switch (format)
        {
            case CopyFormat.Html when Table:
                W.Write($"<tr><td>{offsetText}</td><td>{hex}</td><td>{coloredText ?? HtmlEscape(text)}</td></tr>{NL}");
                break;
            case CopyFormat.Markdown when Table:
                W.Write($"| {offsetText} | {plain} | `{text.Replace("`", "'")}` |{NL}");
                break;
            case CopyFormat.Tex when Table:
                W.Write($"\\texttt{{{offsetText}}} & \\texttt{{{plain}}} & \\texttt{{{TexEscape(text)}}} \\\\{NL}");
                break;
            default:
                if (!first)
                {
                    W.Write(format == CopyFormat.Rtf ? "\\par" + NL : NL);
                }

                string textColumn = coloredText ?? format switch
                {
                    CopyFormat.Html => HtmlEscape(text),
                    CopyFormat.Rtf => RtfEscape(text),
                    _ => text,
                };
                W.Write(Columns(offsetText, hex.ToString() + new string(' ', pad), textColumn));
                break;
        }

        _rowOffset += _count;
        _count = 0;
        _rowIndex++;
    }

    private string Columns(string offsetText, string hex, string text)
    {
        var parts = new List<string>(3);
        if (O.ShowOffset)
        {
            parts.Add(offsetText);
        }

        if (O.ShowHex)
        {
            parts.Add(hex);
        }

        if (O.ShowText)
        {
            parts.Add(text);
        }

        return string.Join("  ", parts).TrimEnd();
    }

    private string Text()
    {
        var sb = new StringBuilder(_count);
        for (int i = 0; i < _count; i++)
        {
            sb.Append(O.Encoding.DisplayChar(_row[i]));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 色付けしたセル。変更されたバイトは変更の色の太字 (文字色はルールより優先)。ルールの文字色・背景色はそのまま書く
    /// (HTML は style 属性、RTF は cf と chcbpat)。
    /// </summary>
    private string Highlight(string cell, bool modified, CopyCellColor color)
    {
        if (format == CopyFormat.Rtf)
        {
            var codes = new StringBuilder();
            if (modified)
            {
                codes.Append("\\cf1\\b");
            }
            else if (color.Foreground is { } fg && _rtfColors.TryGetValue(fg, out int f))
            {
                codes.Append(CultureInfo.InvariantCulture, $"\\cf{f}");
            }

            if (color.Background is { } rtfBack && _rtfColors.TryGetValue(rtfBack, out int b))
            {
                codes.Append(CultureInfo.InvariantCulture, $"\\chcbpat{b}");
            }

            return codes.Length == 0 ? cell : $"{{{codes} {cell}}}";
        }

        var style = new StringBuilder();
        if (modified)
        {
            style.Append(CultureInfo.InvariantCulture, $"color: {O.ModifiedColor}; font-weight: bold;");
        }
        else if (color.Foreground is { } fg)
        {
            style.Append(CultureInfo.InvariantCulture, $"color: #{fg:X6};");
        }

        if (color.Background is { } bg)
        {
            style.Append(style.Length > 0 ? " " : string.Empty).Append(CultureInfo.InvariantCulture, $"background-color: #{bg:X6};");
        }

        return $"<span style=\"{style}\">{cell}</span>";
    }

    /// <summary>テキストの列を、色付けルールの色を付けて書く (色のない文字はそのまま。HTML・RTF)。</summary>
    private string ColoredText(CopyCellColor[] colors)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _count; i++)
        {
            string ch = O.Encoding.DisplayChar(_row[i]).ToString();
            string escaped = format == CopyFormat.Rtf ? RtfEscape(ch) : HtmlEscape(ch);
            CopyCellColor color = i < colors.Length ? colors[i] : default;
            sb.Append(color.IsEmpty ? escaped : Highlight(escaped, modified: false, color));
        }

        return sb.ToString();
    }

    private bool IsModified(long position)
    {
        IReadOnlyList<(long Offset, long Length)> ranges = O.ModifiedRanges;
        while (_modifiedIndex < ranges.Count && ranges[_modifiedIndex].Offset + ranges[_modifiedIndex].Length <= position)
        {
            _modifiedIndex++;
        }

        return _modifiedIndex < ranges.Count && ranges[_modifiedIndex].Offset <= position;
    }

    private static string HtmlEscape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string RtfEscape(string text)
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

    private static string TexEscape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\\' => "\\textbackslash{}",
                '{' or '}' or '#' or '$' or '%' or '&' or '_' => "\\" + c,
                '~' => "\\textasciitilde{}",
                '^' => "\\textasciicircum{}",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static (int R, int G, int B) ParseColor(string hex)
    {
        string h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            return (0, 0, 0);
        }

        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }
}

/// <summary>テキスト (現在の文字コードで解釈した文字列)。</summary>
internal sealed class TextFormatWriter(TextWriter writer, CopyOptions options) : FormatWriter(writer, options)
{
    public override void Write(ReadOnlySpan<byte> data, long at) => W.Write(O.Encoding.Decode(data));
}

/// <summary>
/// Intel HEX。アドレス拡張は最大のアドレスで自動に選ぶ (0xFFFF 以下はなし、0xFFFFF 以下はレコード型 02、それ以上は 04)。レコードの書き方は
/// エクスポート (TOOL-05) と共通 (<see cref="Formats.RecordExporter"/>)。
/// </summary>
internal sealed class IntelHexWriter : FormatWriter
{
    private readonly Formats.IntelHexSink _sink;
    private readonly long _base;

    public IntelHexWriter(TextWriter writer, CopyOptions options, long offset, long length) : base(writer, options)
    {
        _base = options.BaseAddress;
        long last = offset + options.BaseAddress + Math.Max(0, length - 1);
        Formats.IntelHexAddressMode mode = last <= 0xFFFF ? Formats.IntelHexAddressMode.I8Hex
            : last <= 0xFFFFF ? Formats.IntelHexAddressMode.I16Hex : Formats.IntelHexAddressMode.I32Hex;
        _sink = new Formats.IntelHexSink(writer, new Formats.RecordExportOptions { RecordLength = options.RecordBytes }, mode, options.NewLine);
    }

    public override void Write(ReadOnlySpan<byte> data, long at) => _sink.Put(at + _base, data);

    public override void End() => _sink.End(null, false, finalNewLine: true);
}

/// <summary>
/// Motorola S-record。アドレスの大きさで S19 / S28 / S37 を自動で選ぶ (指定も可)。レコード数 (S5) は出さない。レコードの書き方は
/// エクスポート (TOOL-06) と共通。
/// </summary>
internal sealed class SRecordWriter : FormatWriter
{
    private readonly Formats.SRecordSink _sink;
    private readonly long _first;
    private readonly long _base;

    public SRecordWriter(TextWriter writer, CopyOptions options, long offset, long length) : base(writer, options)
    {
        _base = options.BaseAddress;
        _first = offset + options.BaseAddress;
        long last = _first + Math.Max(0, length - 1);
        int addressBytes = options.SRecordKind switch
        {
            SRecordKind.S19 => 2,
            SRecordKind.S28 => 3,
            SRecordKind.S37 => 4,
            _ => last <= 0xFFFF ? 2 : last <= 0xFFFFFF ? 3 : 4,
        };
        _sink = new Formats.SRecordSink(writer, new Formats.RecordExportOptions { RecordLength = Math.Min(options.RecordBytes, 250) },
            addressBytes, options.NewLine);
    }

    public override void Begin() => _sink.WriteHeader(O.SRecordHeader);

    public override void Write(ReadOnlySpan<byte> data, long at) => _sink.Put(at + _base, data);

    // 終わりのレコード (S9 / S8 / S7)。開始アドレスは先頭のアドレス。
    public override void End() => _sink.End(writeCount: false, _first, finalNewLine: true);
}

/// <summary>JSON (<c>{"offset": 256, "length": 4, "data": "3q2+7w=="}</c>)。data は Base64 / Hex 文字列 / 数値の配列。</summary>
internal sealed class JsonWriter : FormatWriter
{
    private readonly long _offset;
    private readonly long _length;
    private readonly Base64Writer _base64;
    private bool _first = true;

    public JsonWriter(TextWriter writer, CopyOptions options, long offset, long length) : base(writer, options)
    {
        _offset = offset;
        _length = length;
        _base64 = new Base64Writer(writer, options with { Base64UrlSafe = false }, wrap: false);
    }

    public override void Begin()
    {
        W.Write($"{{\"offset\": {_offset.ToString(CultureInfo.InvariantCulture)}, \"length\": {_length.ToString(CultureInfo.InvariantCulture)}, \"data\": ");
        W.Write(O.JsonData == JsonDataEncoding.Numbers ? "[" : "\"");
    }

    public override void Write(ReadOnlySpan<byte> data, long at)
    {
        switch (O.JsonData)
        {
            case JsonDataEncoding.Base64:
                _base64.Write(data, at);
                break;
            case JsonDataEncoding.Hex:
                W.Write(O.UpperCase ? Convert.ToHexString(data) : Convert.ToHexStringLower(data));
                break;
            default:
                foreach (byte b in data)
                {
                    if (!_first)
                    {
                        W.Write(", ");
                    }

                    W.Write(b.ToString(CultureInfo.InvariantCulture));
                    _first = false;
                }

                break;
        }
    }

    public override void End()
    {
        if (O.JsonData == JsonDataEncoding.Base64)
        {
            _base64.End();
        }

        W.Write(O.JsonData == JsonDataEncoding.Numbers ? "]}" : "\"}");
    }
}

/// <summary>位置 (カーソル位置、または <c>0x100-0x1FF (256 バイト)</c>)。</summary>
internal sealed class PositionWriter(TextWriter writer, CopyOptions options, long offset, long length) : FormatWriter(writer, options)
{
    public override void Write(ReadOnlySpan<byte> data, long at)
    {
    }

    public override void End()
    {
        string f(long v) => O.DecimalPosition ? v.ToString(CultureInfo.InvariantCulture) : "0x" + v.ToString(O.UpperCase ? "X" : "x", CultureInfo.InvariantCulture);
        if (length <= 0)
        {
            W.Write(f(offset));
            return;
        }

        W.Write($"{f(offset)}-{f(offset + length - 1)} ");
        W.Write(string.Format(CultureInfo.InvariantCulture, O.PositionLengthFormat, length.ToString("N0", CultureInfo.InvariantCulture)));
    }
}
