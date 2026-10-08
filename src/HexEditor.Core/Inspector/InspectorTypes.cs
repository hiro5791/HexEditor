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
    bool Variable = false);

/// <summary>インスペクタで使える型の一覧 (フェーズ 1 の範囲: F1-04)。ID は設定・リソースのキーに使う。</summary>
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
        new(Float32, InspectorGroup.Float, 4),
        new(Float64, InspectorGroup.Float, 8),
        new(Ansi, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(Utf8, InspectorGroup.Text, 1, HasEndian: false, Variable: true),
        new(Utf16, InspectorGroup.Text, 2, Variable: true),
        new(Unix32, InspectorGroup.DateTime, 4),
        new(Unix32U, InspectorGroup.DateTime, 4),
        new(Unix64, InspectorGroup.DateTime, 8),
        new(FileTime, InspectorGroup.DateTime, 8),
        new(DosDate, InspectorGroup.DateTime, 2),
        new(DosTime, InspectorGroup.DateTime, 2),
        new(DosDateTime, InspectorGroup.DateTime, 4),
        new(Binary8, InspectorGroup.Other, 1, HasEndian: false),
        new(Binary16, InspectorGroup.Other, 2),
        new(Binary32, InspectorGroup.Other, 4),
        new(Binary64, InspectorGroup.Other, 8),
        new(Guid, InspectorGroup.Other, 16, FixedEndian: true, HasEndian: false),
        new(Uuid, InspectorGroup.Other, 16, FixedEndian: true, HasEndian: false),
    ];

    private static readonly Dictionary<string, InspectorType> ById = All.ToDictionary(t => t.Id);

    public static InspectorType? Find(string id) => ById.GetValueOrDefault(id);

    public static InspectorType Get(string id) => ById.TryGetValue(id, out InspectorType? t) ? t : throw new ArgumentException($"未知の型です: {id}", nameof(id));

    public static bool IsInteger(string id) => Find(id)?.Group == InspectorGroup.Integer;

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

    public TimeZoneInfo LocalTimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>ANSI の行の文字コード。null ならシステムの ANSI コードページ。</summary>
    public Encoding? AnsiEncoding { get; init; }

    /// <summary>表示に使う文 (言語ごとのリソースを渡す)。</summary>
    public InspectorText Text { get; init; } = InspectorText.English;

    /// <summary>現在時刻 (入力の <c>now</c>)。</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
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
