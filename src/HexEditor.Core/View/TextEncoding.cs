using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.Core.View;

/// <summary>
/// テキスト列の文字コード (VIEW-21)。選べる文字コードの一覧は <see cref="EncodingCatalog"/>。表示 (1 バイト 1 文字)、テキスト列への入力 (EDIT-12)、
/// テキスト列からのコピー (EDIT-22) と貼り付け (EDIT-23) で同じものを使う。ドキュメントごとに持つ (VIEW-21 の仕様 8)。
/// </summary>
public sealed class TextEncoding
{
    /// <summary>表示しない文字の記号 (VIEW-21 の仕様 6・7 の既定)。</summary>
    public const char NonPrintable = '.';

    private const char Replacement = '�';

    private readonly Encoding _strict;
    private readonly Encoding _decoder;
    private readonly char[] _display = new char[256];
    private readonly bool[] _hidden = new bool[256];

    // 表示しない 1 バイトの文字の制御文字 (U+0000〜U+001F、U+007F。図記号の表示に使う)。制御文字でなければ -1。
    private readonly short[] _control = new short[256];

    // 静的な初期化は書いた順に行われるため、ASCII・ANSI を作る前にコードページを使えるようにしておく。
    private static readonly bool ProviderRegistered = RegisterProvider();

    private TextEncoding(string name, int codePage, bool isAscii, string? id = null)
    {
        Name = name;
        CodePage = codePage;
        IsAscii = isAscii;
        Id = id ?? (isAscii ? "ascii" : "cp" + codePage.ToString(CultureInfo.InvariantCulture));
        _strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback(Replacement.ToString()));
        _decoder = _strict;
        Kind = codePage switch
        {
            65001 => TextEncodingKind.Utf8,
            1200 or 1201 => TextEncodingKind.Utf16,
            12000 or 12001 => TextEncodingKind.Utf32,
            54936 => TextEncodingKind.Gb18030,
            _ when isAscii || _strict.IsSingleByte => TextEncodingKind.SingleByte,
            _ => TextEncodingKind.DoubleByte,
        };
        BigEndian = codePage is 1201 or 12001;

        // EBCDIC は C1 が A になる文字コードとして見分ける (00〜3F の制御文字の位置が ASCII と違い、7F は `"`)。
        IsEbcdic = Kind == TextEncodingKind.SingleByte && !isAscii && _decoder.GetString([0xC1]) == "A";
        byte[] question = isAscii ? [(byte)'?'] : _strict.GetBytes("?");
        for (int b = 0; b < 256; b++)
        {
            _display[b] = ToDisplay((byte)b, question.Length == 1 ? question[0] : -1);
            _hidden[b] = _display[b] == NonPrintable && _decoder.GetString([(byte)b]) != ".";
            _control[b] = -1;
            if (_hidden[b])
            {
                string s = isAscii && b > 0x7F ? string.Empty : _decoder.GetString([(byte)b]);
                if (s.Length == 1 && (s[0] < 0x20 || s[0] == 0x7F))
                {
                    _control[b] = (short)s[0];
                }
            }
        }
    }

    /// <summary>
    /// 独自の文字表 (VIEW-23) の文字コード。表示は文字表の最長一致で解読する (<see cref="TextEncodingKind.Table"/>)。1 バイトの表示の
    /// 配列 (<see cref="DisplayChar"/> など) は ASCII と同じにする (文字表を使わない処理の安全のため)。
    /// </summary>
    private TextEncoding(TableFile table)
        : this(table.Name, 20127, isAscii: true, id: TableEncodings.IdOf(table.Name))
    {
        Table = table;
        Kind = TextEncodingKind.Table;
    }

    /// <summary>独自の文字表 (VIEW-23)。文字表の文字コードでなければ null。</summary>
    public TableFile? Table { get; }

    /// <summary>文字表を読み直したとき、前の文字コードを忘れる (次の <see cref="FromId"/> で作り直す)。</summary>
    internal static void ForgetTable(string id)
    {
        lock (ById)
        {
            ById.Remove(id);
        }
    }

    /// <summary>
    /// テキスト列で選べる文字コードの名前 (VIEW-21 のうち、VIEW-22 の表示規則を使うもの)。<see cref="FromId"/> に渡す。
    /// </summary>
    public static IReadOnlyList<string> SelectableIds { get; } =
        ["ascii", "ansi", "utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be", "cp932"];

    private static readonly Dictionary<string, TextEncoding> ById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 名前から文字コードを得る (<c>ascii</c>、<c>ansi</c>、<c>utf-8</c>、<c>utf-16le</c>、<c>utf-16be</c>、<c>utf-32le</c>、
    /// <c>utf-32be</c>、<c>cp&lt;番号&gt;</c>)。知らない名前・OS が提供しないコードページは ASCII。
    /// </summary>
    public static TextEncoding FromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("ascii", StringComparison.OrdinalIgnoreCase))
        {
            return Ascii;
        }

        if (id.Equals("ansi", StringComparison.OrdinalIgnoreCase))
        {
            return Ansi;
        }

        if (id.Equals("oem", StringComparison.OrdinalIgnoreCase))
        {
            return Oem;
        }

        lock (ById)
        {
            if (ById.TryGetValue(id, out TextEncoding? cached))
            {
                return cached;
            }

            TextEncoding? created = null;
            try
            {
                created = id.ToLowerInvariant() switch
                {
                    _ when id.StartsWith(TableEncodings.IdPrefix, StringComparison.OrdinalIgnoreCase)
                        => TableEncodings.Find(id[TableEncodings.IdPrefix.Length..]) is { } table ? new TextEncoding(table) : null,
                    "utf-8" => new TextEncoding("UTF-8", 65001, false, "utf-8"),
                    "utf-16le" => new TextEncoding("UTF-16 LE", 1200, false, "utf-16le"),
                    "utf-16be" => new TextEncoding("UTF-16 BE", 1201, false, "utf-16be"),
                    "utf-32le" => new TextEncoding("UTF-32 LE", 12000, false, "utf-32le"),
                    "utf-32be" => new TextEncoding("UTF-32 BE", 12001, false, "utf-32be"),
                    _ when id.StartsWith("cp", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(id.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int cp) && ProviderRegistered
                        && EncodingCatalog.Find(id) is not { Stateful: true }
                        => new TextEncoding(EncodingCatalog.Find(id)?.Label ?? cp.ToString(CultureInfo.InvariantCulture), cp, false,
                            "cp" + cp.ToString(CultureInfo.InvariantCulture)),
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
            }

            if (created is null)
            {
                return Ascii;
            }

            ById[id] = created;
            return created;
        }
    }

    /// <summary>設定に保存する名前。</summary>
    public string Id { get; }

    /// <summary>テキスト列での解読の方法 (VIEW-22)。</summary>
    public TextEncodingKind Kind { get; }

    /// <summary>UTF-16 / UTF-32 のビッグエンディアン。</summary>
    public bool BigEndian { get; }

    /// <summary>厳密な (不正なバイト列で例外にする) 解読器。テキスト列の解読 (VIEW-22) で使う。</summary>
    internal Encoding StrictDecoder => _strictDecoder ??= Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    private Encoding? _strictDecoder;

    /// <summary>ASCII (7 bit)。`20`〜`7E` を文字、それ以外を `.` で表示する (VIEW-21 の仕様 1 の既定)。</summary>
    public static TextEncoding Ascii { get; } = new("ASCII", 20127, isAscii: true);

    /// <summary>ANSI (システムの既定のコードページ)。</summary>
    public static TextEncoding Ansi { get; private set; } = CreateAnsi();

    /// <summary>OEM (システムの OEM コードページ。VIEW-21 の仕様 2 の「基本」)。</summary>
    public static TextEncoding Oem { get; } = CreateOem();

    /// <summary>
    /// ANSI として使うコードページを変える (テスト用。システムの既定と違うコードページ (932 の Shift_JIS など) の動作を、どの PC でも
    /// 同じように確かめるため)。ドキュメントを開く前に呼ぶ。
    /// </summary>
    public static void UseAnsiCodePage(int codePage)
    {
        // 静的なフィールドに触れて、コードページを使えるようにしてから作る (型の初期化はフィールドに触れるまで遅れることがある)。
        if (ProviderRegistered)
        {
            Ansi = new TextEncoding("ANSI", codePage, isAscii: false, id: "ansi");
        }
    }

    /// <summary>ステータスバーなどに出す名前 (`ASCII`、`932` など)。</summary>
    public string Name { get; }

    public int CodePage { get; }

    public bool IsAscii { get; }

    /// <summary>1 バイトの EBCDIC。</summary>
    public bool IsEbcdic { get; }

    /// <summary>
    /// テキスト列の 1 バイトの表示 (VIEW-21 の仕様 5・6)。制御文字・C1 制御文字・未定義のバイト、1 バイトだけでは文字に
    /// ならないバイト (多バイト文字の一部。表示規則は VIEW-22 で定める) は `.` にする。
    /// </summary>
    public char DisplayChar(byte value) => _display[value];

    /// <summary>表示しない文字か (<see cref="DisplayChar"/> が `.` でも、その文字コードで `.` のバイトは表示する文字)。</summary>
    public bool IsHidden(byte value) => _hidden[value];

    /// <summary>
    /// 表示しない 1 バイトの文字の記号 (VIEW-21 の仕様 7)。制御文字の図記号では、その文字コードで制御文字 (U+0000〜U+001F、U+007F)
    /// になるバイトを図記号 (U+2400〜U+2421) にする。図記号のない文字 (C1 制御文字、未定義のバイトなど) は `.`。
    /// </summary>
    public string NonPrintableSymbol(byte value, NonPrintableStyle style) => Symbol(_control[value], style);

    /// <summary>表示しない文字の記号 (<paramref name="codePoint"/> は解読した符号位置。分からなければ -1)。</summary>
    public static string Symbol(int codePoint, NonPrintableStyle style) => style switch
    {
        NonPrintableStyle.Space => " ",
        NonPrintableStyle.ControlPictures when codePoint is >= 0 and < 0x20 => ((char)(0x2400 + codePoint)).ToString(),
        NonPrintableStyle.ControlPictures when codePoint == 0x7F => "␡",
        _ => ".",
    };

    /// <summary>
    /// 入力・貼り付けの文字列をバイト列にする (EDIT-12 の仕様 3・4)。表せない文字が含まれる場合は false (近似文字には置き換えない)。
    /// </summary>
    public bool TryEncode(string text, out byte[] bytes)
    {
        if (Table is { } table)
        {
            return table.TryEncode(text, out bytes);
        }

        try
        {
            bytes = _strict.GetBytes(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>表せない最初の文字 (InfoBar の「『…』は ASCII で表せません」に使う)。すべて表せれば null。</summary>
    public string? FirstUnencodable(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int n = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            if (!TryEncode(text.Substring(i, n), out _))
            {
                return text.Substring(i, n);
            }

            i += n - 1;
        }

        return null;
    }

    /// <summary>
    /// コピーするテキスト (EDIT-22 の仕様 2): この文字コードで解釈した文字列。解釈できないバイトと NUL は U+FFFD にする。
    /// </summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        if (Table is { } table)
        {
            return table.Decode(bytes);
        }

        string text = _decoder.GetString(bytes);
        return text.Contains('\0') ? text.Replace('\0', Replacement) : text;
    }

    public override string ToString() => Name;

    private char ToDisplay(byte value, int questionByte)
    {
        // EBCDIC では 7F は文字 (`"`)。制御文字は解読した文字で判定する。
        if ((!IsEbcdic && (value < 0x20 || value == 0x7F)) || (IsAscii && value > 0x7F))
        {
            return NonPrintable;
        }

        string s = _decoder.GetString([value]);
        if (s.Length != 1)
        {
            return NonPrintable;
        }

        char c = s[0];
        // 未定義のバイトは置き換えの文字か `?` (最適な対応) になる。
        return c == Replacement || char.IsControl(c) || (c is '?' && value != questionByte) ? NonPrintable : c;
    }

    private static TextEncoding CreateAnsi()
    {
        int codePage = 1252;
        try
        {
            codePage = OperatingSystem.IsWindows() ? (int)GetACP() : CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        try
        {
            return new TextEncoding("ANSI", codePage, isAscii: false, id: "ansi");
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new TextEncoding("ANSI", 1252, isAscii: false, id: "ansi");
        }
    }

    private static TextEncoding CreateOem()
    {
        int codePage = 437;
        try
        {
            codePage = OperatingSystem.IsWindows() ? (int)GetOEMCP() : CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        try
        {
            return new TextEncoding("OEM", codePage, isAscii: false, id: "oem");
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new TextEncoding("OEM", 437, isAscii: false, id: "oem");
        }
    }

    private static bool RegisterProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}

/// <summary>表示しない文字の記号 (VIEW-21 の仕様 7)。</summary>
public enum NonPrintableStyle
{
    /// <summary>`.` (既定)。</summary>
    Dot,

    /// <summary>空白。</summary>
    Space,

    /// <summary>制御文字の図記号 (U+2400〜U+2421。例: 00 は ␀)。</summary>
    ControlPictures,
}

/// <summary>テキスト列での解読の方法 (VIEW-22 の仕様 4)。</summary>
public enum TextEncodingKind
{
    /// <summary>1 バイト = 1 文字 (VIEW-21 の仕様 5)。</summary>
    SingleByte,
    Utf8,
    Utf16,
    Utf32,

    /// <summary>2 バイトの CJK 文字コード (Shift_JIS、GBK、Big5 など)。</summary>
    DoubleByte,

    /// <summary>GB18030 (2 バイトと 4 バイトの文字)。</summary>
    Gb18030,

    /// <summary>独自の文字表 (VIEW-23。最長一致)。</summary>
    Table,
}
