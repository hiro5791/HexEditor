using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Clipboard;

/// <summary>データの読み込み (<paramref name="offset"/> はドキュメント上の位置)。読めない範囲は 00 として扱う。</summary>
public delegate void ByteReader(long offset, Span<byte> destination);

/// <summary>
/// 「形式を選択してコピー」の変換部品 (EDIT-25)。選択範囲を 1 MiB 単位で読みながら出力を書く (仕様の「巨大ファイル」)。
/// クリップボードに入れる場合は出力全体 (上限 64 MiB) を文字列で持ち、ファイルに保存する場合は <see cref="TextWriter"/> に流す。
/// </summary>
public static partial class CopyFormatter
{
    /// <summary>読み込みの単位。</summary>
    public const int ChunkSize = 1024 * 1024;

    /// <summary>プレビューに出す出力の長さ (先頭 4 KiB。仕様 1)。</summary>
    public const int PreviewChars = 4096;

    public static CopyFormatCategory CategoryOf(CopyFormat format) => format switch
    {
        <= CopyFormat.HexCustom => CopyFormatCategory.HexString,
        <= CopyFormat.Binary => CopyFormatCategory.Numbers,
        <= CopyFormat.ArrayAssembly => CopyFormatCategory.Array,
        <= CopyFormat.QuotedPrintable => CopyFormatCategory.Encoding,
        <= CopyFormat.Tex => CopyFormatCategory.Document,
        <= CopyFormat.Text => CopyFormatCategory.Screen,
        <= CopyFormat.Json => CopyFormatCategory.TextFormat,
        _ => CopyFormatCategory.Position,
    };

    /// <summary>配列形式か (要素の大きさ・エンディアン・変数名を使う)。</summary>
    public static bool IsArray(CopyFormat format) => CategoryOf(format) == CopyFormatCategory.Array;

    /// <summary>設定の誤り。正しければ null。</summary>
    public static CopyOptionError? Validate(CopyFormat format, CopyOptions options, long offset, long length)
    {
        if (options.BytesPerLine is < 1 or > 1024 || options.RecordBytes is < 1 or > 255)
        {
            return CopyOptionError.InvalidLineLength;
        }

        if (IsArray(format) && !IsValidIdentifier(format, options.VariableName))
        {
            return CopyOptionError.InvalidVariableName;
        }

        if (format == CopyFormat.Ascii85 && options.Ascii85 == Ascii85Variant.Z85 && length % 4 != 0)
        {
            return CopyOptionError.Z85Length;
        }

        if (format is CopyFormat.IntelHex or CopyFormat.SRecord)
        {
            long first = offset + options.BaseAddress;
            if (first < 0 || length > 0 && first + length - 1 > uint.MaxValue)
            {
                return CopyOptionError.AddressTooLarge;
            }
        }

        return null;
    }

    /// <summary>変数名がその言語の識別子として正しいか (EDIT-25 の仕様 4)。</summary>
    public static bool IsValidIdentifier(CopyFormat format, string name)
    {
        if (!IdentifierPattern().IsMatch(name))
        {
            return false;
        }

        // 主な予約語は変数名にできない。
        return !Keywords.Contains(name) && !(format == CopyFormat.ArrayVisualBasic && VbKeywords.Contains(name.ToUpperInvariant()));
    }

    /// <summary>
    /// 出力を書く。<paramref name="offset"/>・<paramref name="length"/> は選択範囲 (位置の形式では長さ 0 がカーソル位置)。
    /// </summary>
    /// <returns>プレビューに示す注記。</returns>
    public static IReadOnlyList<CopyNote> Write(CopyFormat format, CopyOptions options, ByteReader read, long offset, long length,
        TextWriter writer, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        FormatWriter fw = Create(format, options, offset, length, writer);
        fw.Begin();
        byte[] buffer = new byte[(int)Math.Clamp(length, 1, ChunkSize)];
        for (long done = 0; done < length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, length - done);
            Span<byte> chunk = buffer.AsSpan(0, n);
            read(offset + done, chunk);
            fw.Write(chunk, offset + done);
            done += n;
            progress?.Invoke(done);
        }

        fw.End();
        return fw.Notes;
    }

    /// <summary>メモリ上のバイト列の出力 (プレビュー・テスト用)。</summary>
    public static string Format(CopyFormat format, ReadOnlySpan<byte> data, long offset, CopyOptions? options = null) =>
        Format(format, data.ToArray(), offset, options ?? new CopyOptions(), out _);

    public static string Format(CopyFormat format, byte[] data, long offset, CopyOptions options, out IReadOnlyList<CopyNote> notes)
    {
        var sw = new StringWriter(CultureInfo.InvariantCulture);
        notes = Write(format, options, (o, dest) => data.AsSpan((int)(o - offset), dest.Length).CopyTo(dest), offset, data.Length, sw);
        return sw.ToString();
    }

    /// <summary>
    /// 出力全体の推定サイズ (文字数。仕様 1)。先頭の最大 4 KiB を実際に変換し、残りは同じ割合として見積もる。
    /// </summary>
    public static long EstimateChars(CopyFormat format, CopyOptions options, ByteReader read, long offset, long length)
    {
        if (length <= 0 || format == CopyFormat.Position)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture);
            Write(format, options, read, offset, length, sw);
            return sw.GetStringBuilder().Length;
        }

        int sample = (int)Math.Min(length, 4096);
        byte[] bytes = new byte[sample];
        read(offset, bytes);
        var opts = options with { };
        var probe = new StringWriter(CultureInfo.InvariantCulture);
        Write(format, opts, (o, dest) => bytes.AsSpan((int)(o - offset), dest.Length).CopyTo(dest), offset, sample, probe);
        long chars = probe.GetStringBuilder().Length;
        if (sample == length)
        {
            return chars;
        }

        // 配列の宣言など、長さによって変わる部分の誤差は小さいので比で見積もる。
        return (long)Math.Ceiling(chars * ((double)length / sample));
    }

    // ---- 出力の作り分け ----

    private static FormatWriter Create(CopyFormat format, CopyOptions o, long offset, long length, TextWriter w) => format switch
    {
        CopyFormat.HexSpaced => HexList(o, w, length, e => Hex(e, 2, o), " ", ""),
        CopyFormat.HexPlain => HexList(o, w, length, e => Hex(e, 2, o), "", ""),
        CopyFormat.HexCommaPrefixed => HexList(o, w, length, e => "0x" + Hex(e, 2, o), ", ", ","),
        CopyFormat.HexEscaped => HexList(o, w, length, e => "\\x" + Hex(e, 2, o), "", ""),
        CopyFormat.HexUrl => new ListWriter(w, o, length, 1, int.MaxValue,
            e => o.UrlKeepUnreserved && IsUrlUnreserved((byte)e) ? ((char)e).ToString() : "%" + Hex(e, 2, o)) { Separator = "" },
        CopyFormat.HexCustom => HexList(o, w, length, e => o.CustomPrefix + Hex(e, 2, o) + o.CustomSuffix, o.CustomSeparator, ""),
        CopyFormat.Decimal => HexList(o, w, length, e => e.ToString(CultureInfo.InvariantCulture), " ", ""),
        CopyFormat.Octal => HexList(o, w, length, e => Convert.ToString((long)e, 8), " ", ""),
        CopyFormat.Binary => HexList(o, w, length, e => Convert.ToString((long)e, 2).PadLeft(8, '0'), " ", ""),
        CopyFormat.ArrayPython when o.PythonBytesLiteral => new PythonBytesWriter(w, o, length),
        >= CopyFormat.ArrayC and <= CopyFormat.ArrayAssembly => ArrayWriter(format, o, w, length),
        CopyFormat.Base64 => new Base64Writer(w, o),
        CopyFormat.Base32 => new Base32Writer(w, o),
        CopyFormat.Ascii85 => new Ascii85Writer(w, o),
        CopyFormat.UUEncode => new UuWriter(w, o, xx: false),
        CopyFormat.XXEncode => new UuWriter(w, o, xx: true),
        CopyFormat.QuotedPrintable => new QuotedPrintableWriter(w, o),
        CopyFormat.Html or CopyFormat.Rtf or CopyFormat.Markdown or CopyFormat.Tex or CopyFormat.ScreenDump =>
            new DumpWriter(w, o, format, offset, length),
        CopyFormat.Text => new TextFormatWriter(w, o),
        CopyFormat.IntelHex => new IntelHexWriter(w, o, offset, length),
        CopyFormat.SRecord => new SRecordWriter(w, o, offset, length),
        CopyFormat.Json => new JsonWriter(w, o, offset, length),
        _ => new PositionWriter(w, o, offset, length),
    };

    /// <summary>RFC 3986 の非予約文字 (英数字と <c>-._~</c>)。</summary>
    private static bool IsUrlUnreserved(byte b) => b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
        or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static ListWriter HexList(CopyOptions o, TextWriter w, long length, Func<ulong, string> element, string separator, string lineEnd) =>
        new(w, o, length, 1, o.BytesPerLine, element) { Separator = separator, LineEnd = lineEnd };

    /// <summary>16 進 (<paramref name="digits"/> 桁、大文字・小文字は設定に従う)。</summary>
    internal static string Hex(ulong value, int digits, CopyOptions o) =>
        value.ToString(o.UpperCase ? "X" + digits : "x" + digits, CultureInfo.InvariantCulture);

    private static ListWriter ArrayWriter(CopyFormat format, CopyOptions o, TextWriter w, long length)
    {
        int e = o.ElementSize is 1 or 2 or 4 or 8 ? o.ElementSize : 1;
        int digits = e * 2;
        long count = (length + e - 1) / e;
        int perLine = Math.Max(1, o.BytesPerLine / e);
        string name = o.VariableName;
        string nl = o.NewLine;
        string N = count.ToString(CultureInfo.InvariantCulture);
        int idx = e switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };

        // 10 進 (TOOL-09 の仕様 2 の「16 進 / 10 進」)。C / C++ の 4・8 バイトは符号なしの接尾辞を付ける。
        string dec(ulong v) => v.ToString(CultureInfo.InvariantCulture)
            + (format is CopyFormat.ArrayC or CopyFormat.ArrayCpp ? (e == 8 ? "ULL" : e == 4 ? "U" : string.Empty) : string.Empty);
        string hx(ulong v) => o.ArrayDecimal ? dec(v) : "0x" + Hex(v, digits, o);
        string pick(string a, string b, string c, string d) => idx switch { 0 => a, 1 => b, 2 => c, _ => d };

        ListWriter list(Func<ulong, string> element, string singleOpen, string singleClose, string multiOpen, string multiClose,
            string separator = ", ", string lastLineEnd = "") =>
            new(w, o, length, e, perLine, element)
            {
                Separator = separator,
                LineEnd = ",",
                LastLineEnd = lastLineEnd,
                SingleOpen = singleOpen,
                SingleClose = singleClose,
                MultiOpen = multiOpen + nl,
                MultiClose = multiClose,
                LineIndent = o.Indent,
            };

        switch (format)
        {
            case CopyFormat.ArrayC:
            {
                string type = pick("unsigned char", "uint16_t", "uint32_t", "uint64_t");
                return list(hx, $"{type} {name}[{N}] = {{ ", " };", $"{type} {name}[{N}] = {{", "};");
            }

            case CopyFormat.ArrayCpp:
            {
                string type = pick("std::uint8_t", "std::uint16_t", "std::uint32_t", "std::uint64_t");
                string head = $"constexpr std::array<{type}, {N}> {name} = {{";
                return list(hx, head + " ", " };", head, "};");
            }

            case CopyFormat.ArrayCSharp when o.CSharpSpan:
            {
                string type = pick("byte", "ushort", "uint", "ulong");
                string head = $"ReadOnlySpan<{type}> {name} => [";
                return list(hx, head, "];", head, "];");
            }

            case CopyFormat.ArrayCSharp:
            {
                string type = pick("byte", "ushort", "uint", "ulong");
                return list(hx, $"{type}[] {name} = {{ ", " };", $"{type}[] {name} = {{", "};");
            }

            case CopyFormat.ArrayJava:
            {
                string type = pick("byte", "short", "int", "long");
                // Java の 10 進のリテラルは符号付きの範囲だけ (int・long は 2 の補数の値で書く)。
                string jv(ulong v) => !o.ArrayDecimal ? hx(v)
                    : idx == 2 ? unchecked((int)(uint)v).ToString(CultureInfo.InvariantCulture)
                    : idx == 3 ? unchecked((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
                string elem(ulong v) => idx switch
                {
                    0 => "(byte) " + jv(v),
                    1 => "(short) " + jv(v),
                    2 => jv(v),
                    _ => jv(v) + "L",
                };
                return list(elem, $"{type}[] {name} = {{ ", " };", $"{type}[] {name} = {{", "};");
            }

            case CopyFormat.ArrayJavaScript:
            {
                string type = pick("Uint8Array", "Uint16Array", "Uint32Array", "BigUint64Array");
                string elem(ulong v) => idx == 3 ? hx(v) + "n" : hx(v);
                string head = $"const {name} = new {type}([";
                return list(elem, head, "]);", head, "]);");
            }

            case CopyFormat.ArrayPython:
                return e == 1
                    ? list(hx, $"{name} = bytes([", "])", $"{name} = bytes([", "])")
                    : list(hx, $"{name} = [", "]", $"{name} = [", "]");

            case CopyFormat.ArrayRust:
            {
                string type = pick("u8", "u16", "u32", "u64");
                string head = $"let {name}: [{type}; {N}] = [";
                return list(hx, head, "];", head, "];");
            }

            case CopyFormat.ArrayGo:
            {
                string type = pick("byte", "uint16", "uint32", "uint64");
                string head = $"{name} := []{type}{{";
                return list(hx, head, "}", head, "}", lastLineEnd: ",");
            }

            case CopyFormat.ArrayPascal:
            {
                string type = pick("Byte", "Word", "LongWord", "UInt64");
                string head = $"const {name}: array[0..{count - 1}] of {type} = (";
                return list(v => o.ArrayDecimal ? dec(v) : "$" + Hex(v, digits, o), head, ");", head, ");");
            }

            case CopyFormat.ArrayVisualBasic:
            {
                string type = pick("Byte", "UShort", "UInteger", "ULong");
                string suffix = pick(string.Empty, "US", "UI", "UL");
                string head = $"Dim {name} As {type}() = {{";
                return list(v => (o.ArrayDecimal ? dec(v) : "&H" + Hex(v, digits, o)) + suffix, head, "}", head, "}");
            }

            case CopyFormat.ArrayPureBasic:
            {
                string type = pick("a", "u", "l", "q");
                return new ListWriter(w, o, length, e, perLine, v => "$" + Hex(v, digits, o))
                {
                    Separator = ", ",
                    AlwaysMulti = true,
                    MultiOpen = "DataSection" + nl + name + ":" + nl,
                    MultiClose = "EndDataSection",
                    LinePrefix = $"Data.{type} ",
                };
            }

            default:
            {
                (string directive, Func<ulong, string> elem, string label) = o.Assembly switch
                {
                    AssemblySyntax.Masm => (pick("db", "dw", "dd", "dq"), (Func<ulong, string>)(v => "0" + Hex(v, digits, o) + "h"), name),
                    AssemblySyntax.Gas => (pick(".byte", ".short", ".long", ".quad"), hx, name + ":"),
                    _ => (pick("db", "dw", "dd", "dq"), hx, name + ":"),
                };
                return new ListWriter(w, o, length, e, perLine, elem)
                {
                    Separator = ", ",
                    SingleOpen = $"{label} {directive} ",
                    MultiOpen = o.Assembly == AssemblySyntax.Masm ? string.Empty : label + nl,
                    FirstLinePrefix = o.Assembly == AssemblySyntax.Masm ? $"{label} {directive} " : null,
                    LinePrefix = directive + " ",
                    LineIndent = o.Indent,
                };
            }
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    private static readonly HashSet<string> Keywords =
    [
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for", "goto",
        "if", "int", "long", "register", "return", "short", "signed", "sizeof", "static", "struct", "switch", "typedef", "union",
        "unsigned", "void", "volatile", "while", "class", "new", "delete", "public", "private", "protected", "namespace", "using",
        "byte", "let", "var", "fn", "func", "def", "import", "package", "true", "false", "null", "None", "True", "False", "in", "is",
        "and", "or", "not", "begin", "end", "array", "of", "type", "interface", "this", "base", "object", "string", "bool", "pass",
        "lambda", "yield", "async", "await", "match", "mut", "impl", "pub", "mod", "use", "crate", "self", "super", "go", "chan",
        "map", "range", "select", "defer", "fallthrough",
    ];

    private static readonly HashSet<string> VbKeywords = ["DIM", "AS", "BYTE", "END", "SUB", "FUNCTION", "IF", "THEN", "NEXT", "FOR", "TO", "REM"];
}
