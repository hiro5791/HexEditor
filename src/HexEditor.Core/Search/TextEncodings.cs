using System.Globalization;
using System.Text;

namespace HexEditor.Core.Search;

/// <summary>テキストの検索で選べる文字コード (FIND-07 の仕様 1 の「少なくとも」の一覧)。</summary>
public enum TextEncodingId
{
    Ascii,

    /// <summary>システムの ANSI コードページ。</summary>
    Ansi,

    /// <summary>システムの OEM コードページ。</summary>
    Oem,

    /// <summary>EBCDIC (米国・カナダ、コードページ 37)。</summary>
    Ebcdic,
    Utf8,
    Utf16LE,
    Utf16BE,
    Utf32LE,
    Utf32BE,
    ShiftJis,
    EucJp,
    Gb18030,
    Big5,
    EucKr,
}

/// <summary>
/// 検索で使う文字コードを作る。どれも BOM を付けない。コードページの文字コード (ANSI、Shift_JIS など) は
/// <see cref="CodePagesEncodingProvider"/> を登録して使う (初回に自動で登録する)。
/// </summary>
public static class TextEncodings
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    /// <summary>一覧に出す順。</summary>
    public static IReadOnlyList<TextEncodingId> All { get; } = Enum.GetValues<TextEncodingId>();

    /// <summary>コードページの番号。</summary>
    public static int CodePage(TextEncodingId id) => id switch
    {
        TextEncodingId.Ascii => 20127,
        TextEncodingId.Ansi => AnsiCodePage(),
        TextEncodingId.Oem => OemCodePage(),
        TextEncodingId.Ebcdic => 37,
        TextEncodingId.Utf8 => 65001,
        TextEncodingId.Utf16LE => 1200,
        TextEncodingId.Utf16BE => 1201,
        TextEncodingId.Utf32LE => 12000,
        TextEncodingId.Utf32BE => 12001,
        TextEncodingId.ShiftJis => 932,
        TextEncodingId.EucJp => 51932,
        TextEncodingId.Gb18030 => 54936,
        TextEncodingId.Big5 => 950,
        TextEncodingId.EucKr => 51949,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>文字コードを作る。</summary>
    public static Encoding Get(TextEncodingId id)
    {
        _ = Registered.Value;
        return id switch
        {
            TextEncodingId.Ascii => Encoding.ASCII,
            TextEncodingId.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TextEncodingId.Utf16LE => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
            TextEncodingId.Utf16BE => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
            TextEncodingId.Utf32LE => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
            TextEncodingId.Utf32BE => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
            _ => Encoding.GetEncoding(CodePage(id)),
        };
    }

    /// <summary>「文字の境界に揃える」が使えるか (UTF-16 / UTF-32。FIND-07 の仕様 5)。</summary>
    public static bool SupportsAlignment(TextEncodingId id) =>
        id is TextEncodingId.Utf16LE or TextEncodingId.Utf16BE or TextEncodingId.Utf32LE or TextEncodingId.Utf32BE;

    private static int AnsiCodePage()
    {
        int cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return cp is 0 or 1 ? 1252 : cp; // 0 / 1 は「ANSI のコードページがない」(Unicode だけの地域)。
    }

    private static int OemCodePage()
    {
        int cp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        return cp is 0 or 1 ? 437 : cp;
    }
}
