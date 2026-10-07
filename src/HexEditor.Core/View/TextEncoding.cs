using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace HexEditor.Core.View;

/// <summary>
/// テキスト列の文字コード (VIEW-21。フェーズ 0 は ASCII と ANSI だけ)。表示 (1 バイト 1 文字)、テキスト列への入力 (EDIT-12)、
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

    // 静的な初期化は書いた順に行われるため、ASCII・ANSI を作る前にコードページを使えるようにしておく。
    private static readonly bool ProviderRegistered = RegisterProvider();

    private TextEncoding(string name, int codePage, bool isAscii)
    {
        Name = name;
        CodePage = codePage;
        IsAscii = isAscii;
        _strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback(Replacement.ToString()));
        _decoder = _strict;
        for (int b = 0; b < 256; b++)
        {
            _display[b] = ToDisplay((byte)b);
        }
    }

    /// <summary>ASCII (7 bit)。`20`〜`7E` を文字、それ以外を `.` で表示する (VIEW-21 の仕様 1 の既定)。</summary>
    public static TextEncoding Ascii { get; } = new("ASCII", 20127, isAscii: true);

    /// <summary>ANSI (システムの既定のコードページ)。</summary>
    public static TextEncoding Ansi { get; private set; } = CreateAnsi();

    /// <summary>
    /// ANSI として使うコードページを変える (テスト用。システムの既定と違うコードページ (932 の Shift_JIS など) の動作を、どの PC でも
    /// 同じように確かめるため)。ドキュメントを開く前に呼ぶ。
    /// </summary>
    public static void UseAnsiCodePage(int codePage)
    {
        // 静的なフィールドに触れて、コードページを使えるようにしてから作る (型の初期化はフィールドに触れるまで遅れることがある)。
        if (ProviderRegistered)
        {
            Ansi = new TextEncoding("ANSI", codePage, isAscii: false);
        }
    }

    /// <summary>ステータスバーなどに出す名前 (`ASCII`、`932` など)。</summary>
    public string Name { get; }

    public int CodePage { get; }

    public bool IsAscii { get; }

    /// <summary>
    /// テキスト列の 1 バイトの表示 (VIEW-21 の仕様 5・6)。制御文字・C1 制御文字・未定義のバイト、1 バイトだけでは文字に
    /// ならないバイト (多バイト文字の一部。表示規則は VIEW-22 で定める) は `.` にする。
    /// </summary>
    public char DisplayChar(byte value) => _display[value];

    /// <summary>
    /// 入力・貼り付けの文字列をバイト列にする (EDIT-12 の仕様 3・4)。表せない文字が含まれる場合は false (近似文字には置き換えない)。
    /// </summary>
    public bool TryEncode(string text, out byte[] bytes)
    {
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
        string text = _decoder.GetString(bytes);
        return text.Contains('\0') ? text.Replace('\0', Replacement) : text;
    }

    public override string ToString() => Name;

    private char ToDisplay(byte value)
    {
        if (value < 0x20 || value == 0x7F || (IsAscii && value > 0x7F))
        {
            return NonPrintable;
        }

        string s = _decoder.GetString([value]);
        if (s.Length != 1)
        {
            return NonPrintable;
        }

        char c = s[0];
        return c == Replacement || char.IsControl(c) || (c is '?' && value != (byte)'?') ? NonPrintable : c;
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
            return new TextEncoding("ANSI", codePage, isAscii: false);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new TextEncoding("ANSI", 1252, isAscii: false);
        }
    }

    private static bool RegisterProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
