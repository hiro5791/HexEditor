using System.Globalization;
using System.Text;

namespace HexEditor.Core.Inspector;

/// <summary>インスペクタの行のグループ (INSP-01 の仕様 5)。並びは既定の表示順。</summary>
public enum InspectorGroup
{
    Integer,
    Float,
    VarInt,
    Text,
    DateTime,
    Other,
    User,
}

/// <summary>バイト順。</summary>
public enum Endianness
{
    Little,
    Big,
}

/// <summary>インスペクタのエンディアンの選択 (INSP-02 の仕様 1)。</summary>
public enum InspectorEndianMode
{
    /// <summary>ドキュメントのエンディアン (VIEW-11) に従う。</summary>
    Document,
    Little,
    Big,
}

/// <summary>整数の表示形式 (INSP-02 の仕様 4)。</summary>
public enum IntegerBase
{
    Decimal,
    Hexadecimal,
    Octal,
}

/// <summary>浮動小数点の表示形式 (INSP-02 の仕様 6)。</summary>
public enum FloatFormat
{
    /// <summary>値を元に戻せる最短の 10 進表記。</summary>
    Shortest,
    Exponent,
    HexFloat,
}

/// <summary>日時の表示のタイムゾーン (INSP-15 の仕様 1)。</summary>
public enum DateTimeZoneMode
{
    Local,
    Utc,
}

/// <summary>日時の書式 (INSP-15 の仕様 3)。</summary>
public enum DateTimeStyle
{
    /// <summary>日付の順序と区切りは地域設定、時刻は 24 時間制。</summary>
    Regional,
    Iso8601,
}

/// <summary>
/// インスペクタの型 1 つ。<see cref="Size"/> は固定長の型のバイト数、可変長の型 (文字) は最小のバイト数。
/// <see cref="FixedEndian"/> はエンディアンの指定に従わない型 (INSP-02 の仕様 3。名前の横に「(固定)」と表示する)。
/// <see cref="HasEndian"/> はエンディアンで解釈が変わる型 (反対のエンディアンの行を加えられる。INSP-02 の仕様 7)。
/// </summary>
public sealed record InspectorType(string Id, InspectorGroup Group, int Size, bool FixedEndian = false, bool HasEndian = true,
    bool Variable = false)
{
    /// <summary>固定小数点の形式 (INSP-06 の仕様 3)。固定小数点の型でなければ null。</summary>
    public FixedPointFormat? Fixed { get; init; }
}

/// <summary>
/// 固定小数点の形式 (Q 形式。INSP-06 の仕様 1・3): 符号の有無、全体のビット数 (8 / 16 / 32 / 64)、小数部のビット数 (0〜全体)。
/// 型の ID は <c>fixed_s32_16</c> (符号あり、32 bit、小数部 16 bit) の形。
/// </summary>
public readonly record struct FixedPointFormat(bool Signed, int TotalBits, int FractionBits)
{
    public const string Prefix = "fixed_";

    public string Id => $"{Prefix}{(Signed ? 's' : 'u')}{TotalBits}_{FractionBits}";

    public int IntegerBits => TotalBits - FractionBits;

    public bool IsValid => TotalBits is 8 or 16 or 32 or 64 && FractionBits >= 0 && FractionBits <= TotalBits;

    /// <summary>型の ID を読む。固定小数点の ID でなければ null。</summary>
    public static FixedPointFormat? Parse(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal) || id.Length < Prefix.Length + 4)
        {
            return null;
        }

        string body = id[Prefix.Length..];
        if (body[0] is not ('s' or 'u'))
        {
            return null;
        }

        string[] parts = body[1..].Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int total)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int fraction))
        {
            return null;
        }

        var format = new FixedPointFormat(body[0] == 's', total, fraction);
        return format.IsValid && format.Id == id ? format : null;
    }
}

/// <summary>
/// インスペクタで使える型の一覧 (F1-04 の型と、フェーズ 2 で加えた INSP-04・INSP-06・INSP-07・INSP-10・INSP-12・INSP-14 の型)。
/// ID は設定・リソースのキーに使う (固定小数点の ID は形式から作る。<see cref="FixedPointFormat"/>)。
/// </summary>
public static class InspectorTypes
{
    public const string Int8 = "int8";
    public const string UInt8 = "uint8";
    public const string Int16 = "int16";
    public const string UInt16 = "uint16";
    public const string Int32 = "int32";
    public const string UInt32 = "uint32";
    public const string Int64 = "int64";
    public const string UInt64 = "uint64";
    public const string Float32 = "float";
    public const string Float64 = "double";
    public const string Ansi = "ansi";
    public const string Utf8 = "utf8";
    public const string Utf16 = "utf16";
    public const string Unix32 = "unix32";
    public const string Unix32U = "unix32u";
    public const string Unix64 = "unix64";
    public const string FileTime = "filetime";
    public const string DosDate = "dosdate";
    public const string DosTime = "dostime";
    public const string DosDateTime = "dosdatetime";
    public const string Binary8 = "binary8";
    public const string Binary16 = "binary16";
    public const string Binary32 = "binary32";
    public const string Binary64 = "binary64";
    public const string Guid = "guid";
    public const string Uuid = "uuid";

    // ---- INSP-04 整数 (24 / 48 / 128 bit) ----
    public const string Int24 = "int24";
    public const string UInt24 = "uint24";
    public const string Int48 = "int48";
    public const string UInt48 = "uint48";
    public const string Int128 = "int128";
    public const string UInt128 = "uint128";

    // ---- INSP-06 拡張の浮動小数点と固定小数点 ----
    public const string Half = "half";
    public const string BFloat16 = "bfloat16";
    public const string Float80 = "float80";
    public const string Real48 = "real48";

    /// <summary>固定小数点の既定の行「16.16 (符号あり, 32 bit)」(INSP-06 の仕様 3)。</summary>
    public const string Fixed16_16 = "fixed_s32_16";

    /// <summary>固定小数点の既定の行「8.8 (符号あり, 16 bit)」。</summary>
    public const string Fixed8_8 = "fixed_s16_8";

    // ---- INSP-07 可変長整数 ----
    public const string ULeb128 = "uleb128";
    public const string SLeb128 = "sleb128";
    public const string SqliteVarint = "sqlitevarint";

    // ---- INSP-10 文字列 ----
    public const string CStringAnsi = "cstrAnsi";
    public const string CStringUtf8 = "cstrUtf8";
    public const string CStringUtf16 = "cstrUtf16";
    public const string PString8Ansi = "pstr8Ansi";
    public const string PString8Utf8 = "pstr8Utf8";
    public const string PString16Ansi = "pstr16Ansi";
    public const string PString16Utf8 = "pstr16Utf8";
    public const string PString32Ansi = "pstr32Ansi";
    public const string PString32Utf8 = "pstr32Utf8";

    // ---- INSP-12 色 ----
    public const string Rgba8 = "rgba8";
    public const string Bgra8 = "bgra8";
    public const string Rgb8 = "rgb8";
    public const string Rgb565 = "rgb565";

    // ---- INSP-14 日時 (拡張の形式) ----
    public const string UnixMs = "unixms";
    public const string OleDate = "oledate";
    public const string HfsPlus = "hfsplus";
    public const string Apfs = "apfs";
    public const string Cocoa = "cocoa";
    public const string SqlDateTime = "sqldatetime";
    public const string SqlSmallDateTime = "sqlsmalldatetime";
    public const string DotNetDateTime = "dotnetdatetime";
    public const string DigitsDateTime = "digitsdatetime";

    /// <summary>すべての型 (既定の並び)。</summary>
    public static IReadOnlyList<InspectorType> All { get; } =
    [
        new(Int8, InspectorGroup.Integer, 1, HasEndian: false),
        new(UInt8, InspectorGroup.Integer, 1, HasEndian: false),
        new(Int16, InspectorGroup.Integer, 2),
        new(UInt16, InspectorGroup.Integer, 2),
        new(Int32, InspectorGroup.Integer, 4),
        new(UInt32, InspectorGroup.Integer, 4),
        new(Int64, InspectorGroup.Integer, 8),
        new(UInt64, InspectorGroup.Integer, 8),
        new(Int24, InspectorGroup.Integer, 3),
        new(UInt24, InspectorGroup.Integer, 3),
        new(Int48, InspectorGroup.Integer, 6),
        new(UInt48, InspectorGroup.Integer, 6),
        new(Int128, InspectorGroup.Integer, 16),
        new(UInt128, InspectorGroup.Integer, 16),
        new(Float32, InspectorGroup.Float, 4),
        new(Float64, InspectorGroup.Float, 8),
        new(Half, InspectorGroup.Float, 2),
        new(BFloat16, InspectorGroup.Float, 2),
        new(Float80, InspectorGroup.Float, 10),

        // Real48 は x86 の Turbo Pascal の形式で、バイト順は常にリトルエンディアン (INSP-02 の仕様 3 の「固定」)。
        new(Real48, InspectorGroup.Float, 6, FixedEndian: true, HasEndian: false),
        FixedType(new FixedPointFormat(true, 32, 16)),
        FixedType(new FixedPointFormat(true, 16, 8)),

        // LEB128 はバイト順の概念がなく、SQLite varint は常にビッグエンディアン (INSP-02 の仕様 3)。
        new(ULeb128, InspectorGroup.VarInt, 1, FixedEndian: true, HasEndian: false, Variable: true),
        new(SLeb128, InspectorGroup.VarInt, 1, FixedEndian: true, HasEndian: false, Variable: true),
        new(SqliteVarint, InspectorGroup.VarInt, 1, FixedEndian: true, HasEndian: false, Variable: true),
        new(Ansi, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(Utf8, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(Utf16, InspectorGroup.Text, 2, Variable: true),
        new(CStringAnsi, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(CStringUtf8, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(CStringUtf16, InspectorGroup.Text, 2, Variable: true),
        new(PString8Ansi, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(PString8Utf8, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(PString16Ansi, InspectorGroup.Text, 2, Variable: true),
        new(PString16Utf8, InspectorGroup.Text, 2, Variable: true),
        new(PString32Ansi, InspectorGroup.Text, 4, Variable: true),
        new(PString32Utf8, InspectorGroup.Text, 4, Variable: true),
        new(Unix32, InspectorGroup.DateTime, 4),
        new(Unix32U, InspectorGroup.DateTime, 4),
        new(Unix64, InspectorGroup.DateTime, 8),
        new(FileTime, InspectorGroup.DateTime, 8),
        new(DosDate, InspectorGroup.DateTime, 2),
        new(DosTime, InspectorGroup.DateTime, 2),
        new(DosDateTime, InspectorGroup.DateTime, 4),
        new(UnixMs, InspectorGroup.DateTime, 8),
        new(OleDate, InspectorGroup.DateTime, 8),
        new(HfsPlus, InspectorGroup.DateTime, 4),
        new(Apfs, InspectorGroup.DateTime, 8),
        new(Cocoa, InspectorGroup.DateTime, 8),
        new(SqlDateTime, InspectorGroup.DateTime, 8),
        new(SqlSmallDateTime, InspectorGroup.DateTime, 4),
        new(DotNetDateTime, InspectorGroup.DateTime, 8),

        // 10 進の数字の日時は ASCII の数字の並びなので、エンディアンの概念がない。
        new(DigitsDateTime, InspectorGroup.DateTime, 8, HasEndian: false, Variable: true),
        new(Binary8, InspectorGroup.Other, 1, HasEndian: false),
        new(Binary16, InspectorGroup.Other, 2),
        new(Binary32, InspectorGroup.Other, 4),
        new(Binary64, InspectorGroup.Other, 8),
        new(Guid, InspectorGroup.Other, 16, FixedEndian: true, HasEndian: false),
        new(Uuid, InspectorGroup.Other, 16, FixedEndian: true, HasEndian: false),
        new(Rgba8, InspectorGroup.Other, 4, HasEndian: false),
        new(Bgra8, InspectorGroup.Other, 4, HasEndian: false),
        new(Rgb8, InspectorGroup.Other, 3, HasEndian: false),
        new(Rgb565, InspectorGroup.Other, 2),
    ];

    private static readonly Dictionary<string, InspectorType> ById = All.ToDictionary(t => t.Id);

    /// <summary>固定小数点の行の上限 (INSP-06 の仕様 3)。</summary>
    public const int MaxFixedRows = 16;

    /// <summary>固定小数点の形式の型。</summary>
    public static InspectorType FixedType(FixedPointFormat format) =>
        new(format.Id, InspectorGroup.Float, format.TotalBits / 8, HasEndian: format.TotalBits > 8) { Fixed = format };

    /// <summary>型を探す。固定小数点は一覧にない形式も ID から作る (行の設定で加えた行)。</summary>
    public static InspectorType? Find(string id) =>
        ById.TryGetValue(id, out InspectorType? t) ? t : FixedPointFormat.Parse(id) is { } format ? FixedType(format) : null;

    public static InspectorType Get(string id) => Find(id) ?? throw new ArgumentException($"未知の型です: {id}", nameof(id));

    /// <summary>整数の行 (INSP-03・INSP-04。入力式で書き換えられる)。</summary>
    public static bool IsInteger(string id) => Find(id)?.Group == InspectorGroup.Integer;

    public static bool IsFixedPoint(string id) => id.StartsWith(FixedPointFormat.Prefix, StringComparison.Ordinal);

    /// <summary>文字列の型 (INSP-10)。読む長さが 4 KB になる (INSP-01 の仕様 4)。</summary>
    public static bool IsString(string id) => id.StartsWith("cstr", StringComparison.Ordinal) || id.StartsWith("pstr", StringComparison.Ordinal);

    /// <summary>色の型 (INSP-12)。値の列に色見本を出す。</summary>
    public static bool IsColor(string id) => id is Rgba8 or Bgra8 or Rgb8 or Rgb565;

    public static bool IsBinary(string id) => id.StartsWith("binary", StringComparison.Ordinal);

    /// <summary>既定で表示する行 (INSP-01 の仕様 6)。</summary>
    public static IReadOnlySet<string> Basic { get; } = new HashSet<string>
    {
        Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, Binary8, Ansi, Utf8, Utf16, Guid, Unix32, FileTime, DosDateTime,
    };
}

/// <summary>
/// 解釈と表示の条件 (INSP-02、INSP-15)。<see cref="LocalTimeZone"/> は「ローカル時刻」の表示に使うタイムゾーン
/// (テストで差し替える)。<see cref="AnsiEncoding"/> は ANSI の行の文字コード (INSP-09 の仕様 1)。
/// </summary>
public sealed record InspectorOptions
{
    public IntegerBase IntegerBase { get; init; } = IntegerBase.Decimal;

    /// <summary>10 進の表示で地域設定の桁区切りを使う (INSP-02 の仕様 5)。</summary>
    public bool DigitGrouping { get; init; } = true;

    public FloatFormat FloatFormat { get; init; } = FloatFormat.Shortest;

    public DateTimeZoneMode TimeZoneMode { get; init; } = DateTimeZoneMode.Local;

    public DateTimeStyle DateTimeStyle { get; init; } = DateTimeStyle.Regional;

    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    /// <summary>表示言語 (文の複数形の規則に使う。UI-42 の仕様 4)。</summary>
    public string Language { get; init; } = CultureInfo.CurrentUICulture.Name;

    public TimeZoneInfo LocalTimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>ANSI の行の文字コード。null ならシステムの ANSI コードページ。</summary>
    public Encoding? AnsiEncoding { get; init; }

    /// <summary>表示に使う文 (言語ごとのリソースを渡す)。</summary>
    public InspectorText Text { get; init; } = InspectorText.English;

    /// <summary>現在時刻 (入力の <c>now</c>)。</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// 文字を表示できるか (代替フォントを含めて、どれかのフォントにその文字があるか。INSP-09 の仕様 3)。表示できない文字は
    /// コードポイントだけを表示する。null ならすべて表示できるとみなす (UI がフォントを調べる関数を渡す)。
    /// </summary>
    public Func<int, bool>? CanDisplay { get; init; }
}

/// <summary>
/// 表示・誤りの文 (UI の言語のリソースから作る。Core はリソースを持たないので、既定は英語)。書式の {0} などは
/// <see cref="string.Format(IFormatProvider, string, object[])"/> で埋める。
/// </summary>
public sealed record InspectorText
{
    public static InspectorText English { get; } = new();

    public string NotEnoughValue { get; init; } = "—";
    public string NotEnoughTip { get; init; } = "Not enough data ({0} bytes needed, {1} left)";
    public string Loading { get; init; } = "…";
    public string Unreadable { get; init; } = "Can't read";
    public string Invalid { get; init; } = "Invalid ({0})";
    public string ReasonContinuation { get; init; } = "continuation byte";
    public string ReasonIncomplete { get; init; } = "incomplete sequence";
    public string ReasonOverlong { get; init; } = "overlong encoding";
    public string ReasonSurrogate { get; init; } = "surrogate code point";
    public string ReasonTooLarge { get; init; } = "beyond U+10FFFF";
    public string ReasonBadLead { get; init; } = "invalid lead byte";
    public string ReasonMissingContinuation { get; init; } = "missing continuation byte";
    public string ReasonUnpairedHigh { get; init; } = "unpaired high surrogate";
    public string ReasonUnpairedLow { get; init; } = "unpaired low surrogate";
    public string ReasonUndefined { get; init; } = "not defined in the code page";
    public string OneByte { get; init; } = "(1 byte)";
    public string Bytes { get; init; } = "({0} bytes)";
    public string Denormal { get; init; } = "{0} (denormal)";
    public string OutOfRange { get; init; } = "Out of range ({0})";
    public string FieldMonth { get; init; } = "month = {0}";
    public string FieldDay { get; init; } = "day = {0}";
    public string FieldHour { get; init; } = "hour = {0}";
    public string FieldMinute { get; init; } = "minute = {0}";
    public string FieldSecond { get; init; } = "second = {0}";
    public string NoTimeZone { get; init; } = "(no time zone)";
    public string Utc { get; init; } = "(UTC)";
    public string UtcOffset { get; init; } = "(UTC{0})";
    public string ComponentVersion { get; init; } = "Version";
    public string ComponentVariant { get; init; } = "Variant";
    public string ComponentTimestamp { get; init; } = "Timestamp";
    public string ComponentClockSequence { get; init; } = "Clock sequence";
    public string ComponentNode { get; init; } = "Node";
    public string ComponentUnixTime { get; init; } = "Unix time (ms)";
    public string RandomNode { get; init; } = "{0} (random node)";
    public string VersionUnknown { get; init; } = "Unknown";
    public string VariantNcs { get; init; } = "NCS";
    public string VariantRfc { get; init; } = "RFC";
    public string VariantMicrosoft { get; init; } = "Microsoft";
    public string VariantReserved { get; init; } = "Reserved";

    // ---- フェーズ 2 の型 (INSP-06、INSP-07、INSP-10、INSP-12、INSP-14) ----
    public string Unnormal { get; init; } = "Unnormal representation";
    public string TooLarge64 { get; init; } = "Exceeds 64 bit";

    /// <summary>文字列の文字数とバイト数 (ICU MessageFormat の複数形を使える)。</summary>
    public string StringCounts { get; init; } = "({0, plural, one {# char} other {# chars}}, {1, plural, one {# byte} other {# bytes}})";
    public string NotTerminated { get; init; } = "No terminator (4 KB or more)";
    public string NotTerminatedAtEnd { get; init; } = "No terminator before the end of the data";
    public string ColorComponents { get; init; } = "R {0}, G {1}, B {2}, A {3}";
    public string KindLocal { get; init; } = "(local time)";
    public string KindUnspecified { get; init; } = "(unspecified kind)";
    public string DigitsNeeded { get; init; } = "At least 8 digits are needed ({0} found)";
    public string DigitsUnixSeconds { get; init; } = "Unix seconds";
    public string DigitsUnixMilliseconds { get; init; } = "Unix milliseconds";
    public string InvalidDigits { get; init; } = "Invalid";
    public string ErrorVarIntLength { get; init; } = "The byte count would change, so the value can't be written (was {0} bytes, now {1} bytes)";
    public string ErrorStringTooLong { get; init; } = "Longer than the original length ({0} bytes)";
    public string ErrorNotTerminated { get; init; } = "The original string has no terminator, so its length is unknown";
    public string ErrorColor { get; init; } = "Enter a color such as #FF8000 or #FF8000FF";
    public string ErrorEscape { get; init; } = @"Invalid escape sequence (use \n, \t, \0, \xNN, \"" or \\)";
    public string ErrorDigits { get; init; } = "The value needs {1} digits, but the original has {0}";

    // ---- 書き換えの誤り (INSP-17) ----
    public string ErrorIntegerRange { get; init; } = "Out of the {0} range ({1} to {2})";
    public string ErrorInteger { get; init; } = "Enter an integer or an expression";
    public string ErrorFloat { get; init; } = "Enter a number such as 1.5, 1e-3, inf, nan or 0x1.8p+1";
    public string ErrorFloatOverflow { get; init; } = "The value is too large for {0} and would become infinity";
    public string ErrorDate { get; init; } = "Enter the date and time in the form 2026-10-07 12:34:56";
    public string ErrorDateRange { get; init; } = "Out of the {0} range ({1} to {2})";
    public string ErrorGuid { get; init; } = "Enter 32 hexadecimal digits";
    public string ErrorBinary { get; init; } = "Enter up to {0} binary digits (0 or 1)";
    public string ErrorChar { get; init; } = "Enter one character";
    public string ErrorNotEncodable { get; init; } = "This character can't be written in {0}";
    public string ErrorPastEnd { get; init; } = "The value goes past the end of the document ({0} bytes needed, {1} left)";
    public string ErrorReadOnly { get; init; } = "The document is read-only";
}
