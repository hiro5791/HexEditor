using System.Globalization;
using System.Text;

namespace HexEditor.Core.View;

/// <summary>文字コードの一覧の分類 (VIEW-21 の仕様 2 の表の行)。</summary>
public enum EncodingGroup
{
    Basic,
    Unicode,
    Windows,
    Iso,
    Dos,
    Mac,
    Cjk,
    Other,
    Ebcdic,
}

/// <summary>
/// 文字コードの一覧の 1 項目 (VIEW-21 の仕様 2〜4)。<see cref="Label"/> は一覧とステータスバーに出す番号 (EBCDIC は IBM の番号。
/// 例: <c>037</c>)、<see cref="EnglishName"/> は翻訳がないときの名前 (UI ではリソース <c>Encoding_Name_&lt;Id&gt;</c> の翻訳を使う)。
/// <see cref="Stateful"/> は状態を持つ文字コード (ISO-2022 系、UTF-7、HZ、SO/SI の EBCDIC) で、テキスト列では選べない (仕様 4)。
/// </summary>
public sealed record EncodingEntry(string Id, int CodePage, string Label, EncodingGroup Group, string EnglishName, bool Stateful = false)
{
    /// <summary>テキスト列で選べるか (仕様 4)。</summary>
    public bool Selectable => !Stateful;

    /// <summary>名前に実際のコードページ番号を入れる項目 (ANSI と OEM。名前の <c>{0}</c> に入れる)。</summary>
    public bool NameHasCodePage => Id is "ansi" or "oem";
}

/// <summary>
/// テキスト列で選べる文字コードの一覧 (VIEW-21 の仕様 2〜4・8)。OS (.NET の <c>CodePagesEncodingProvider</c>) が提供しない
/// コードページは一覧に出さない (VIEW-21 の「エラー」)。状態を持つ文字コードは一覧に出すが選べない。
/// </summary>
public static class EncodingCatalog
{
    /// <summary>メニューの上部に出す最近使った文字コードの数 (仕様 8)。</summary>
    public const int RecentLimit = 5;

    private static readonly EncodingEntry[] Table =
    [
        // <生成: 表>
        new("ascii", 20127, "ASCII", EncodingGroup.Basic, "ASCII (7 bit)"),
        new("ansi", 0, "ANSI", EncodingGroup.Basic, "ANSI (code page {0})"),
        new("oem", 1, "OEM", EncodingGroup.Basic, "OEM (code page {0})"),
        new("utf-8", 65001, "UTF-8", EncodingGroup.Unicode, "UTF-8"),
        new("utf-16le", 1200, "UTF-16 LE", EncodingGroup.Unicode, "UTF-16 LE"),
        new("utf-16be", 1201, "UTF-16 BE", EncodingGroup.Unicode, "UTF-16 BE"),
        new("utf-32le", 12000, "UTF-32 LE", EncodingGroup.Unicode, "UTF-32 LE"),
        new("utf-32be", 12001, "UTF-32 BE", EncodingGroup.Unicode, "UTF-32 BE"),
        new("cp65000", 65000, "65000", EncodingGroup.Unicode, "UTF-7", Stateful: true),
        new("cp874", 874, "874", EncodingGroup.Windows, "Thai (Windows)"),
        new("cp1250", 1250, "1250", EncodingGroup.Windows, "Central European (Windows)"),
        new("cp1251", 1251, "1251", EncodingGroup.Windows, "Cyrillic (Windows)"),
        new("cp1252", 1252, "1252", EncodingGroup.Windows, "Western European (Windows)"),
        new("cp1253", 1253, "1253", EncodingGroup.Windows, "Greek (Windows)"),
        new("cp1254", 1254, "1254", EncodingGroup.Windows, "Turkish (Windows)"),
        new("cp1255", 1255, "1255", EncodingGroup.Windows, "Hebrew (Windows)"),
        new("cp1256", 1256, "1256", EncodingGroup.Windows, "Arabic (Windows)"),
        new("cp1257", 1257, "1257", EncodingGroup.Windows, "Baltic (Windows)"),
        new("cp1258", 1258, "1258", EncodingGroup.Windows, "Vietnamese (Windows)"),
        new("cp28591", 28591, "28591", EncodingGroup.Iso, "Western European (ISO-8859-1)"),
        new("cp28592", 28592, "28592", EncodingGroup.Iso, "Central European (ISO-8859-2)"),
        new("cp28593", 28593, "28593", EncodingGroup.Iso, "Latin 3 (ISO-8859-3)"),
        new("cp28594", 28594, "28594", EncodingGroup.Iso, "Baltic (ISO-8859-4)"),
        new("cp28595", 28595, "28595", EncodingGroup.Iso, "Cyrillic (ISO-8859-5)"),
        new("cp28596", 28596, "28596", EncodingGroup.Iso, "Arabic (ISO-8859-6)"),
        new("cp28597", 28597, "28597", EncodingGroup.Iso, "Greek (ISO-8859-7)"),
        new("cp28598", 28598, "28598", EncodingGroup.Iso, "Hebrew, visual order (ISO-8859-8)"),
        new("cp38598", 38598, "38598", EncodingGroup.Iso, "Hebrew, logical order (ISO-8859-8-I)"),
        new("cp28599", 28599, "28599", EncodingGroup.Iso, "Turkish (ISO-8859-9)"),
        new("cp28600", 28600, "28600", EncodingGroup.Iso, "Nordic (ISO-8859-10)"),
        new("cp28601", 28601, "28601", EncodingGroup.Iso, "Thai (ISO-8859-11)"),
        new("cp28603", 28603, "28603", EncodingGroup.Iso, "Estonian (ISO-8859-13)"),
        new("cp28604", 28604, "28604", EncodingGroup.Iso, "Celtic (ISO-8859-14)"),
        new("cp28605", 28605, "28605", EncodingGroup.Iso, "Latin 9 (ISO-8859-15)"),
        new("cp28606", 28606, "28606", EncodingGroup.Iso, "South-Eastern European (ISO-8859-16)"),
        new("cp437", 437, "437", EncodingGroup.Dos, "United States (DOS)"),
        new("cp720", 720, "720", EncodingGroup.Dos, "Arabic (DOS)"),
        new("cp737", 737, "737", EncodingGroup.Dos, "Greek (DOS)"),
        new("cp775", 775, "775", EncodingGroup.Dos, "Baltic (DOS)"),
        new("cp850", 850, "850", EncodingGroup.Dos, "Western European (DOS)"),
        new("cp852", 852, "852", EncodingGroup.Dos, "Central European (DOS)"),
        new("cp855", 855, "855", EncodingGroup.Dos, "Cyrillic (DOS 855)"),
        new("cp857", 857, "857", EncodingGroup.Dos, "Turkish (DOS)"),
        new("cp858", 858, "858", EncodingGroup.Dos, "Western European with euro (DOS)"),
        new("cp860", 860, "860", EncodingGroup.Dos, "Portuguese (DOS)"),
        new("cp861", 861, "861", EncodingGroup.Dos, "Icelandic (DOS)"),
        new("cp862", 862, "862", EncodingGroup.Dos, "Hebrew (DOS)"),
        new("cp863", 863, "863", EncodingGroup.Dos, "French Canadian (DOS)"),
        new("cp864", 864, "864", EncodingGroup.Dos, "Arabic (DOS 864)"),
        new("cp865", 865, "865", EncodingGroup.Dos, "Nordic (DOS)"),
        new("cp866", 866, "866", EncodingGroup.Dos, "Cyrillic (DOS 866)"),
        new("cp869", 869, "869", EncodingGroup.Dos, "Modern Greek (DOS)"),
        new("cp10000", 10000, "10000", EncodingGroup.Mac, "Mac Roman"),
        new("cp10001", 10001, "10001", EncodingGroup.Mac, "Japanese (Mac)"),
        new("cp10002", 10002, "10002", EncodingGroup.Mac, "Traditional Chinese (Mac)"),
        new("cp10003", 10003, "10003", EncodingGroup.Mac, "Korean (Mac)"),
        new("cp10004", 10004, "10004", EncodingGroup.Mac, "Arabic (Mac)"),
        new("cp10005", 10005, "10005", EncodingGroup.Mac, "Hebrew (Mac)"),
        new("cp10006", 10006, "10006", EncodingGroup.Mac, "Greek (Mac)"),
        new("cp10007", 10007, "10007", EncodingGroup.Mac, "Cyrillic (Mac)"),
        new("cp10008", 10008, "10008", EncodingGroup.Mac, "Simplified Chinese (Mac)"),
        new("cp10010", 10010, "10010", EncodingGroup.Mac, "Romanian (Mac)"),
        new("cp10017", 10017, "10017", EncodingGroup.Mac, "Ukrainian (Mac)"),
        new("cp10021", 10021, "10021", EncodingGroup.Mac, "Thai (Mac)"),
        new("cp10029", 10029, "10029", EncodingGroup.Mac, "Central European (Mac)"),
        new("cp10079", 10079, "10079", EncodingGroup.Mac, "Icelandic (Mac)"),
        new("cp10081", 10081, "10081", EncodingGroup.Mac, "Turkish (Mac)"),
        new("cp10082", 10082, "10082", EncodingGroup.Mac, "Croatian (Mac)"),
        new("cp932", 932, "932", EncodingGroup.Cjk, "Japanese (Shift-JIS)"),
        new("cp51932", 51932, "51932", EncodingGroup.Cjk, "Japanese (EUC-JP)"),
        new("cp20932", 20932, "20932", EncodingGroup.Cjk, "Japanese (EUC-JP, JIS X 0208 and 0212)"),
        new("cp50220", 50220, "50220", EncodingGroup.Cjk, "Japanese (ISO-2022-JP)", Stateful: true),
        new("cp50221", 50221, "50221", EncodingGroup.Cjk, "Japanese (ISO-2022-JP, 1-byte kana)", Stateful: true),
        new("cp50222", 50222, "50222", EncodingGroup.Cjk, "Japanese (ISO-2022-JP, SO/SI)", Stateful: true),
        new("cp936", 936, "936", EncodingGroup.Cjk, "Simplified Chinese (GBK)"),
        new("cp54936", 54936, "54936", EncodingGroup.Cjk, "Simplified Chinese (GB18030)"),
        new("cp51936", 51936, "51936", EncodingGroup.Cjk, "Simplified Chinese (EUC-CN)"),
        new("cp50227", 50227, "50227", EncodingGroup.Cjk, "Simplified Chinese (ISO-2022-CN)", Stateful: true),
        new("cp52936", 52936, "52936", EncodingGroup.Cjk, "Simplified Chinese (HZ)", Stateful: true),
        new("cp949", 949, "949", EncodingGroup.Cjk, "Korean (Unified Hangul Code)"),
        new("cp51949", 51949, "51949", EncodingGroup.Cjk, "Korean (EUC-KR)"),
        new("cp50225", 50225, "50225", EncodingGroup.Cjk, "Korean (ISO-2022-KR)", Stateful: true),
        new("cp950", 950, "950", EncodingGroup.Cjk, "Traditional Chinese (Big5)"),
        new("cp20866", 20866, "20866", EncodingGroup.Other, "Cyrillic (KOI8-R)"),
        new("cp21866", 21866, "21866", EncodingGroup.Other, "Cyrillic (KOI8-U)"),
        new("cp57002", 57002, "57002", EncodingGroup.Other, "ISCII Devanagari"),
        new("cp57003", 57003, "57003", EncodingGroup.Other, "ISCII Bengali"),
        new("cp57004", 57004, "57004", EncodingGroup.Other, "ISCII Tamil"),
        new("cp57005", 57005, "57005", EncodingGroup.Other, "ISCII Telugu"),
        new("cp57006", 57006, "57006", EncodingGroup.Other, "ISCII Assamese"),
        new("cp57007", 57007, "57007", EncodingGroup.Other, "ISCII Odia"),
        new("cp57008", 57008, "57008", EncodingGroup.Other, "ISCII Kannada"),
        new("cp57009", 57009, "57009", EncodingGroup.Other, "ISCII Malayalam"),
        new("cp57010", 57010, "57010", EncodingGroup.Other, "ISCII Gujarati"),
        new("cp57011", 57011, "57011", EncodingGroup.Other, "ISCII Punjabi"),
        new("cp708", 708, "708", EncodingGroup.Other, "Arabic (ASMO 708)"),
        new("cp1361", 1361, "1361", EncodingGroup.Other, "Korean (Johab)"),
        new("cp20000", 20000, "20000", EncodingGroup.Other, "Traditional Chinese (CNS)"),
        new("cp20001", 20001, "20001", EncodingGroup.Other, "Traditional Chinese (TCA)"),
        new("cp20002", 20002, "20002", EncodingGroup.Other, "Traditional Chinese (Eten)"),
        new("cp20003", 20003, "20003", EncodingGroup.Other, "Traditional Chinese (IBM 5550)"),
        new("cp20004", 20004, "20004", EncodingGroup.Other, "Traditional Chinese (TeleText)"),
        new("cp20005", 20005, "20005", EncodingGroup.Other, "Traditional Chinese (Wang)"),
        new("cp20105", 20105, "20105", EncodingGroup.Other, "Western European (IA5)"),
        new("cp20106", 20106, "20106", EncodingGroup.Other, "German (IA5)"),
        new("cp20107", 20107, "20107", EncodingGroup.Other, "Swedish (IA5)"),
        new("cp20108", 20108, "20108", EncodingGroup.Other, "Norwegian (IA5)"),
        new("cp20261", 20261, "20261", EncodingGroup.Other, "T.61"),
        new("cp20269", 20269, "20269", EncodingGroup.Other, "ISO 6937"),
        new("cp20936", 20936, "20936", EncodingGroup.Other, "Simplified Chinese (GB 2312-80)"),
        new("cp20949", 20949, "20949", EncodingGroup.Other, "Korean (Wansung)"),
        new("cp29001", 29001, "29001", EncodingGroup.Other, "Europa"),
        new("cp37", 37, "037", EncodingGroup.Ebcdic, "EBCDIC US-Canada"),
        new("cp20273", 20273, "273", EncodingGroup.Ebcdic, "EBCDIC Germany"),
        new("cp20277", 20277, "277", EncodingGroup.Ebcdic, "EBCDIC Denmark-Norway"),
        new("cp20278", 20278, "278", EncodingGroup.Ebcdic, "EBCDIC Finland-Sweden"),
        new("cp20280", 20280, "280", EncodingGroup.Ebcdic, "EBCDIC Italy"),
        new("cp20284", 20284, "284", EncodingGroup.Ebcdic, "EBCDIC Spain"),
        new("cp20285", 20285, "285", EncodingGroup.Ebcdic, "EBCDIC United Kingdom"),
        new("cp20290", 20290, "290", EncodingGroup.Ebcdic, "EBCDIC Japanese katakana"),
        new("cp20297", 20297, "297", EncodingGroup.Ebcdic, "EBCDIC France"),
        new("cp20420", 20420, "420", EncodingGroup.Ebcdic, "EBCDIC Arabic"),
        new("cp20423", 20423, "423", EncodingGroup.Ebcdic, "EBCDIC Greek"),
        new("cp20424", 20424, "424", EncodingGroup.Ebcdic, "EBCDIC Hebrew"),
        new("cp500", 500, "500", EncodingGroup.Ebcdic, "EBCDIC International"),
        new("cp20833", 20833, "833", EncodingGroup.Ebcdic, "EBCDIC Korean extended"),
        new("cp20838", 20838, "838", EncodingGroup.Ebcdic, "EBCDIC Thai"),
        new("cp870", 870, "870", EncodingGroup.Ebcdic, "EBCDIC Multilingual Latin 2"),
        new("cp20871", 20871, "871", EncodingGroup.Ebcdic, "EBCDIC Icelandic"),
        new("cp875", 875, "875", EncodingGroup.Ebcdic, "EBCDIC Modern Greek"),
        new("cp20880", 20880, "880", EncodingGroup.Ebcdic, "EBCDIC Cyrillic Russian"),
        new("cp20905", 20905, "905", EncodingGroup.Ebcdic, "EBCDIC Turkish"),
        new("cp20924", 20924, "924", EncodingGroup.Ebcdic, "EBCDIC Latin 1 with euro"),
        new("cp21025", 21025, "1025", EncodingGroup.Ebcdic, "EBCDIC Cyrillic Serbian-Bulgarian"),
        new("cp1026", 1026, "1026", EncodingGroup.Ebcdic, "EBCDIC Turkish Latin 5"),
        new("cp1047", 1047, "1047", EncodingGroup.Ebcdic, "EBCDIC Latin 1 (open systems)"),
        new("cp1140", 1140, "1140", EncodingGroup.Ebcdic, "EBCDIC US-Canada with euro"),
        new("cp1141", 1141, "1141", EncodingGroup.Ebcdic, "EBCDIC Germany with euro"),
        new("cp1142", 1142, "1142", EncodingGroup.Ebcdic, "EBCDIC Denmark-Norway with euro"),
        new("cp1143", 1143, "1143", EncodingGroup.Ebcdic, "EBCDIC Finland-Sweden with euro"),
        new("cp1144", 1144, "1144", EncodingGroup.Ebcdic, "EBCDIC Italy with euro"),
        new("cp1145", 1145, "1145", EncodingGroup.Ebcdic, "EBCDIC Spain with euro"),
        new("cp1146", 1146, "1146", EncodingGroup.Ebcdic, "EBCDIC United Kingdom with euro"),
        new("cp1147", 1147, "1147", EncodingGroup.Ebcdic, "EBCDIC France with euro"),
        new("cp1148", 1148, "1148", EncodingGroup.Ebcdic, "EBCDIC International with euro"),
        new("cp1149", 1149, "1149", EncodingGroup.Ebcdic, "EBCDIC Icelandic with euro"),
        new("cp930", 930, "930", EncodingGroup.Ebcdic, "EBCDIC Japanese katakana mixed (SO/SI)", Stateful: true),
        new("cp939", 939, "939", EncodingGroup.Ebcdic, "EBCDIC Japanese Latin mixed (SO/SI)", Stateful: true),
        // </生成>
    ];

    private static readonly Lazy<IReadOnlyList<EncodingEntry>> Available = new(() => [.. Table.Where(IsAvailable)]);

    /// <summary>この環境で使える文字コード (状態を持つものを含む)。分類の順、分類の中は表の順。</summary>
    public static IReadOnlyList<EncodingEntry> All => Available.Value;

    /// <summary>名前 (<c>ascii</c>、<c>cp932</c> など) の項目。一覧にない、またはこの環境で使えなければ null。</summary>
    public static EncodingEntry? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 絞り込みに合うか (仕様 3): 番号 (一覧の番号とコードページ番号)・名前 (表示名と英語名)・Web での名前 (<c>shift_jis</c> など) の
    /// どれかが、<paramref name="query"/> を大文字・小文字を区別せずに含む。空の絞り込みはすべてに合う。
    /// </summary>
    public static bool Matches(EncodingEntry entry, string displayName, string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
        {
            return true;
        }

        return Contains(entry.Label, q) || Contains(entry.CodePage.ToString(CultureInfo.InvariantCulture), q) || Contains(displayName, q)
            || Contains(entry.EnglishName, q) || Contains(WebName(entry), q) || Contains(entry.Id, q);

        static bool Contains(string text, string q) => text.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 最近使った文字コードの一覧に <paramref name="id"/> を加える (先頭に置き、重複を除き、<see cref="RecentLimit"/> 件まで。仕様 8)。
    /// </summary>
    public static IReadOnlyList<string> PushRecent(IEnumerable<string> recent, string id) =>
        [.. new[] { id }.Concat(recent.Where(r => !string.Equals(r, id, StringComparison.OrdinalIgnoreCase))).Take(RecentLimit)];

    private static string WebName(EncodingEntry entry)
    {
        if (entry.CodePage <= 1)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.GetEncoding(entry.CodePage).WebName;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static bool IsAvailable(EncodingEntry entry)
    {
        if (entry.Group is EncodingGroup.Basic || entry.Id.StartsWith("utf-", StringComparison.Ordinal) && !entry.Stateful)
        {
            return true;
        }

        // TextEncoding の静的な初期化で CodePagesEncodingProvider を登録してから調べる。
        _ = TextEncoding.Ascii;
        try
        {
            _ = Encoding.GetEncoding(entry.CodePage);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
