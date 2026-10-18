using System.Globalization;
using System.Numerics;
using HexEditor.Core.Inspector;

namespace HexEditor.Core.Tools;

/// <summary>タイムスタンプ形式の生の値。<see cref="IsFloat"/> の形式 (OLE、Cocoa) は <see cref="Double"/>、それ以外は <see cref="Integer"/>。</summary>
public readonly record struct TimestampRaw(BigInteger Integer, double Double);

/// <summary>
/// 日時の変換の対象の形式 (TOOL-28 の仕様 1)。<see cref="InspectorTypeId"/> は、同じ定義のデータインスペクタの型 (INSP-13・INSP-14)。
/// <see cref="WallClock"/> の形式 (DOS、OLE、.NET) はタイムゾーンを持たない。
/// </summary>
public sealed class TimestampFormat
{
    private readonly Func<DateTime, TimestampRaw?> _encode;
    private readonly Func<TimestampRaw, DateTime?> _decode;

    internal TimestampFormat(string id, string name, int bytes, string? inspectorTypeId, bool wallClock, bool isFloat, int fractionDigits,
        Func<DateTime, TimestampRaw?> encode, Func<TimestampRaw, DateTime?> decode)
    {
        Id = id;
        Name = name;
        Bytes = bytes;
        InspectorTypeId = inspectorTypeId;
        WallClock = wallClock;
        IsFloat = isFloat;
        FractionDigits = fractionDigits;
        _encode = encode;
        _decode = decode;
    }

    public string Id { get; }

    /// <summary>英語の名前 (UI は <c>Timestamp_Format_&lt;Id&gt;</c> のリソースで表示する)。</summary>
    public string Name { get; }

    public int Bytes { get; }

    public string? InspectorTypeId { get; }

    public bool WallClock { get; }

    public bool IsFloat { get; }

    /// <summary>表示する小数部の桁数(0〜7)。</summary>
    public int FractionDigits { get; }

    /// <summary>日時 (UTC。<see cref="WallClock"/> の形式では現地の日時) を生の値にする。表せなければ null。</summary>
    public TimestampRaw? Encode(DateTime value) => _encode(value);

    /// <summary>生の値を日時にする。範囲外なら null。</summary>
    public DateTime? Decode(TimestampRaw raw) => _decode(raw);
}

/// <summary>タイムスタンプ形式の一覧 (TOOL-28 の仕様 1)。</summary>
public static class TimestampFormats
{
    private static readonly DateTime UnixEpoch = InspectorDateTime.UnixEpoch;
    private static readonly DateTime FileTimeEpoch = InspectorDateTime.FileTimeEpoch;
    private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CocoaEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GpsEpoch = new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>GPS 時刻の開始(1980-01-06)より後に挿入された閏秒の日 (UTC の翌日 0 時から適用)。</summary>
    private static readonly DateTime[] LeapSeconds =
    [
        new(1981, 7, 1), new(1982, 7, 1), new(1983, 7, 1), new(1985, 7, 1), new(1988, 1, 1), new(1990, 1, 1), new(1991, 1, 1), new(1992, 7, 1),
        new(1993, 7, 1), new(1994, 7, 1), new(1996, 1, 1), new(1997, 7, 1), new(1999, 1, 1), new(2006, 1, 1), new(2009, 1, 1), new(2012, 7, 1),
        new(2015, 7, 1), new(2017, 1, 1),
    ];

    public static IReadOnlyList<TimestampFormat> All { get; } = Create();

    public static TimestampFormat? Find(string id) => All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    private static BigInteger FloorDiv(BigInteger a, BigInteger b)
    {
        BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
        return r.Sign < 0 ? q - 1 : q;
    }

    private static BigInteger TicksSince(DateTime epoch, DateTime value) => value.Ticks - epoch.Ticks;

    private static DateTime? Add(DateTime epoch, BigInteger ticks, DateTimeKind kind = DateTimeKind.Utc)
    {
        BigInteger result = epoch.Ticks + ticks;
        return result < DateTime.MinValue.Ticks || result > DateTime.MaxValue.Ticks ? null : new DateTime((long)result, kind);
    }

    private static TimestampFormat Seconds(string id, string name, int bytes, string typeId, DateTime epoch, BigInteger min, BigInteger max) =>
        new(id, name, bytes, typeId, wallClock: false, isFloat: false, 0,
            v =>
            {
                BigInteger s = FloorDiv(TicksSince(epoch, v), TimeSpan.TicksPerSecond);
                return s < min || s > max ? null : new TimestampRaw(s, 0);
            },
            r => r.Integer < min || r.Integer > max ? null : Add(epoch, r.Integer * TimeSpan.TicksPerSecond));

    /// <summary>
    /// 1 秒より細かい単位の整数の形式。<paramref name="ticksPerUnit"/> は 1 単位の 100 ナノ秒の数 (ミリ秒は 10000、マイクロ秒は 10)、
    /// <paramref name="unitsPerTick"/> は 100 ナノ秒あたりの単位の数 (ナノ秒は 100)。どちらかが 1。
    /// </summary>
    private static TimestampFormat Units(string id, string name, string? typeId, long ticksPerUnit, long unitsPerTick, int digits, BigInteger min,
        BigInteger max, DateTime epoch) =>
        new(id, name, 8, typeId, wallClock: false, isFloat: false, digits,
            v =>
            {
                BigInteger ticks = TicksSince(epoch, v);
                BigInteger u = unitsPerTick > 1 ? ticks * unitsPerTick : FloorDiv(ticks, ticksPerUnit);
                return u < min || u > max ? null : new TimestampRaw(u, 0);
            },
            r =>
            {
                if (r.Integer < min || r.Integer > max)
                {
                    return null;
                }

                return Add(epoch, unitsPerTick > 1 ? FloorDiv(r.Integer, unitsPerTick) : r.Integer * ticksPerUnit);
            });

    private static int LeapCount(DateTime utc)
    {
        int count = 0;
        foreach (DateTime leap in LeapSeconds)
        {
            if (utc >= leap)
            {
                count++;
            }
        }

        return count;
    }

    private static IReadOnlyList<TimestampFormat> Create()
    {
        BigInteger i32min = int.MinValue, i32max = int.MaxValue, u32max = uint.MaxValue, i64min = long.MinValue, i64max = long.MaxValue, u64max = ulong.MaxValue;
        return
        [
            Seconds("unix32", "Unix time (32 bit, seconds)", 4, InspectorTypes.Unix32, UnixEpoch, i32min, i32max),
            Seconds("unix32u", "Unix time (unsigned 32 bit, seconds)", 4, InspectorTypes.Unix32U, UnixEpoch, 0, u32max),
            Seconds("unix64", "Unix time (64 bit, seconds)", 8, InspectorTypes.Unix64, UnixEpoch, i64min, i64max),
            Units("unixms", "Unix time (64 bit, milliseconds)", InspectorTypes.UnixMs, TimeSpan.TicksPerMillisecond, 1, 3, i64min, i64max, UnixEpoch),
            Units("unixus", "Unix time (64 bit, microseconds)", null, 10, 1, 6, i64min, i64max, UnixEpoch),
            Units("unixns", "Unix time (64 bit, nanoseconds)", null, 1, 100, 7, i64min, i64max, UnixEpoch),
            new("filetime", "FILETIME", 8, InspectorTypes.FileTime, wallClock: false, isFloat: false, 7,
                v => TicksSince(FileTimeEpoch, v) is var t && t.Sign >= 0 && t <= ulong.MaxValue ? new TimestampRaw(t, 0) : null,
                r => r.Integer.Sign >= 0 ? Add(FileTimeEpoch, r.Integer) : null),
            new("dos", "DOS date and time", 4, InspectorTypes.DosDateTime, wallClock: true, isFloat: false, 0, EncodeDos, DecodeDos),
            new("ole", "OLE Automation date", 8, InspectorTypes.OleDate, wallClock: true, isFloat: true, 3,
                v =>
                {
                    // DateTime.ToOADate の受け付ける範囲 (0100-01-01〜9999-12-31)。
                    return v.Year is < 100 or > 9999 ? null : new TimestampRaw(0, v.ToOADate());
                },
                r => double.IsFinite(r.Double) && r.Double > -657435.0 && r.Double < 2958466.0 ? DateTime.SpecifyKind(DateTime.FromOADate(r.Double), DateTimeKind.Unspecified) : null),
            Seconds("hfsplus", "HFS+", 4, InspectorTypes.HfsPlus, HfsEpoch, 0, u32max),
            Units("apfs", "APFS (nanoseconds)", InspectorTypes.Apfs, 1, 100, 7, 0, u64max, UnixEpoch),
            Units("java", "Java (milliseconds)", InspectorTypes.UnixMs, TimeSpan.TicksPerMillisecond, 1, 3, i64min, i64max, UnixEpoch),
            new("dotnet", ".NET DateTime.Ticks", 8, InspectorTypes.DotNetDateTime, wallClock: true, isFloat: false, 7,
                v => new TimestampRaw(v.Ticks, 0),
                r => r.Integer.Sign >= 0 && r.Integer <= DateTime.MaxValue.Ticks ? new DateTime((long)r.Integer, DateTimeKind.Unspecified) : null),
            new("gps", "GPS time (seconds)", 4, null, wallClock: false, isFloat: false, 0,
                v =>
                {
                    BigInteger s = FloorDiv(TicksSince(GpsEpoch, v), TimeSpan.TicksPerSecond) + LeapCount(v);
                    return s < 0 || s > uint.MaxValue ? null : new TimestampRaw(s, 0);
                },
                r =>
                {
                    if (r.Integer.Sign < 0 || r.Integer > uint.MaxValue)
                    {
                        return null;
                    }

                    // 閏秒の数は UTC の日時で決まるので、閏秒を引いて調べ直す。
                    DateTime? approx = Add(GpsEpoch, r.Integer * TimeSpan.TicksPerSecond);
                    if (approx is null)
                    {
                        return null;
                    }

                    int count = LeapCount(approx.Value.AddSeconds(-LeapCount(approx.Value)));
                    return Add(GpsEpoch, (r.Integer - count) * TimeSpan.TicksPerSecond);
                }),
            new("cocoa", "Mac absolute time (Cocoa)", 8, InspectorTypes.Cocoa, wallClock: false, isFloat: true, 3,
                v => new TimestampRaw(0, (double)TicksSince(CocoaEpoch, v) / TimeSpan.TicksPerSecond),
                r => double.IsFinite(r.Double) && Math.Abs(r.Double) < 4e11 ? Add(CocoaEpoch, new BigInteger(Math.Round(r.Double * TimeSpan.TicksPerSecond))) : null),
        ];
    }

    // ---- DOS 日付・時刻 (INSP-13) ----

    private static TimestampRaw? EncodeDos(DateTime v)
    {
        if (v.Year is < 1980 or > 2107)
        {
            return null;
        }

        int date = ((v.Year - 1980) << 9) | (v.Month << 5) | v.Day;
        int time = (v.Hour << 11) | (v.Minute << 5) | (v.Second / 2);
        return new TimestampRaw(((long)date << 16) | (uint)time, 0);
    }

    private static DateTime? DecodeDos(TimestampRaw raw)
    {
        if (raw.Integer.Sign < 0 || raw.Integer > uint.MaxValue)
        {
            return null;
        }

        uint value = (uint)raw.Integer;
        int date = (int)(value >> 16), time = (int)(value & 0xFFFF);
        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour >= 24 || minute >= 60 || second >= 60)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    }
}

/// <summary>1 つの形式の行: 数値 (10 進・16 進) とバイト列。表せない値は <see cref="Representable"/> が false。</summary>
public sealed record TimestampRow(TimestampFormat Format, bool Representable, string Decimal, string Hex, byte[] Bytes);

/// <summary>入力の誤り。<see cref="Code"/> は説明文のリソース (<c>Timestamp_Error_&lt;Code&gt;</c>) の名前。</summary>
public sealed record TimestampError(string Code);

/// <summary>
/// 日時の変換 (TOOL-28)。基準の日時 (UTC の瞬間、またはタイムゾーンを持たない現地の日時) から、各形式の数値とバイト列を作る。
/// どの欄に入力しても他の欄を更新する。表せない値 (例: DOS の日付で 1980 年より前) は <see cref="TimestampRow.Representable"/> が false。
/// </summary>
public sealed class TimestampConverter
{
    public TimestampConverter(InspectorOptions options)
    {
        Options = options;
    }

    public InspectorOptions Options { get; set; }

    /// <summary>エンディアン (バイト列の欄と「カーソル位置から読む」)。</summary>
    public bool BigEndian { get; set; }

    /// <summary>基準の日時。<see cref="HasZone"/> なら UTC の瞬間、そうでなければタイムゾーンを持たない日時。</summary>
    public DateTime Value { get; private set; } = InspectorDateTime.UnixEpoch;

    public bool HasZone { get; private set; } = true;

    /// <summary>タイムゾーンなしの値が .NET の DateTime (種類の指定なし) から読んだものか (データインスペクタは「(種類の指定なし)」と示す)。</summary>
    private bool _unspecifiedKind;

    /// <summary>入力のタイムゾーン (UTC / ローカル / 任意)。</summary>
    private TimeZoneInfo Zone => Options.TimeZoneMode == DateTimeZoneMode.Utc ? TimeZoneInfo.Utc : Options.LocalTimeZone;

    public void SetInstant(DateTime utc)
    {
        _unspecifiedKind = false;
        Value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        HasZone = true;
    }

    public void SetWallClock(DateTime wall)
    {
        _unspecifiedKind = false;
        Value = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        HasZone = false;
    }

    /// <summary>現在の日時にする (<c>now</c>)。</summary>
    public void SetNow() => SetInstant(Options.Time.GetUtcNow().UtcDateTime);

    /// <summary>基準の日時の UTC の瞬間 (タイムゾーンなしの値は、選んだタイムゾーンの日時とみなす)。</summary>
    public DateTime? Utc
    {
        get
        {
            if (HasZone)
            {
                return Value;
            }

            try
            {
                return TimeZoneInfo.ConvertTimeToUtc(Value, Zone);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>基準の日時の現地の日時(タイムゾーンなしの形式の値)。</summary>
    public DateTime? Wall
    {
        get
        {
            if (!HasZone)
            {
                return Value;
            }

            try
            {
                return TimeZoneInfo.ConvertTimeFromUtc(Value, Zone);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    // ---- 日時の表示 ----

    /// <summary>ISO 8601 の表示 (UTC は <c>Z</c>、それ以外は <c>+09:00</c>。タイムゾーンなしは付けない)。</summary>
    public string Iso()
    {
        InspectorOptions iso = Options with { DateTimeStyle = DateTimeStyle.Iso8601 };
        return HasZone ? InspectorDateTime.FormatInstant(Value, 7, iso) ?? string.Empty : Strip(InspectorDateTime.FormatNoZone(Value, 7, iso));
    }

    /// <summary>地域設定の書式の表示 (データインスペクタと同じ書式)。</summary>
    public string Regional()
    {
        InspectorOptions regional = Options with { DateTimeStyle = DateTimeStyle.Regional };
        if (!HasZone && _unspecifiedKind)
        {
            regional = regional with { Text = Options.Text with { NoTimeZone = Options.Text.KindUnspecified } };
        }

        return HasZone ? InspectorDateTime.FormatInstant(Value, 7, regional) ?? string.Empty : InspectorDateTime.FormatNoZone(Value, 7, regional);
    }

    private string Strip(string text) => text.EndsWith(" " + Options.Text.NoTimeZone, StringComparison.Ordinal) ? text[..^(Options.Text.NoTimeZone.Length + 1)] : text;

    // ---- 入力 ----

    /// <summary>日時の欄の入力 (ISO 8601 または <c>now</c>)。</summary>
    public TimestampError? SetFromText(string text)
    {
        if (InspectorDateTime.Parse(text, hasZone: true, Options, allowTimeOnly: false) is not { } parsed)
        {
            return new TimestampError("InvalidDate");
        }

        SetInstant(parsed.Value);
        return null;
    }

    /// <summary>形式の行の数値 (10 進、または <c>0x</c> 付きの 16 進) の入力。</summary>
    public TimestampError? SetFromNumber(TimestampFormat format, string text)
    {
        string t = text.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        TimestampRaw raw;
        if (format.IsFloat)
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bits))
                {
                    return new TimestampError("InvalidNumber");
                }

                raw = new TimestampRaw(0, BitConverter.UInt64BitsToDouble(bits));
            }
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                raw = new TimestampRaw(0, d);
            }
            else
            {
                return new TimestampError("InvalidNumber");
            }
        }
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!BigInteger.TryParse("0" + t[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out BigInteger hex))
            {
                return new TimestampError("InvalidNumber");
            }

            // 16 進は形式の大きさのビット列。符号付きの形式では上位ビットが符号。
            raw = new TimestampRaw(Signed(format) && hex >= (BigInteger.One << (format.Bytes * 8 - 1)) ? hex - (BigInteger.One << (format.Bytes * 8)) : hex, 0);
        }
        else if (BigInteger.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger integer))
        {
            raw = new TimestampRaw(integer, 0);
        }
        else
        {
            return new TimestampError("InvalidNumber");
        }

        return SetFromRaw(format, raw);
    }

    private static bool Signed(TimestampFormat format) => format.Id is "unix32" or "unix64" or "unixms" or "unixus" or "unixns" or "java" or "dotnet";

    public TimestampError? SetFromRaw(TimestampFormat format, TimestampRaw raw)
    {
        if (format.Decode(raw) is not { } value)
        {
            return new TimestampError("OutOfRange");
        }

        if (format.WallClock)
        {
            SetWallClock(value);
            _unspecifiedKind = format.Id == "dotnet";
        }
        else
        {
            SetInstant(value);
        }

        return null;
    }

    /// <summary>バイト列から読む (「カーソル位置から読む」)。足りなければ誤り。</summary>
    public TimestampError? SetFromBytes(TimestampFormat format, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < format.Bytes)
        {
            return new TimestampError("NotEnoughData");
        }

        return SetFromRaw(format, RawFromBytes(format, bytes[..format.Bytes], BigEndian));
    }

    /// <summary>バイト列の欄の入力 (Hex)。</summary>
    public TimestampError? SetFromHexBytes(TimestampFormat format, string text)
    {
        byte[]? bytes = Clipboard.HexText.TryParse(text);
        return bytes is null || bytes.Length != format.Bytes ? new TimestampError("InvalidHex") : SetFromBytes(format, bytes);
    }

    // ---- 各形式の行 ----

    public TimestampRow Row(TimestampFormat format)
    {
        DateTime? source = format.WallClock ? Wall : Utc;
        TimestampRaw? raw = source is { } v ? format.Encode(v) : null;
        if (raw is not { } r)
        {
            return new TimestampRow(format, false, string.Empty, string.Empty, []);
        }

        byte[] bytes = BytesOf(format, r, BigEndian);
        string decimalText = format.IsFloat ? r.Double.ToString("R", CultureInfo.InvariantCulture) : r.Integer.ToString(CultureInfo.InvariantCulture);
        string hex = "0x" + HexOf(format, r);
        return new TimestampRow(format, true, decimalText, hex, bytes);
    }

    public IReadOnlyList<TimestampRow> Rows() => [.. TimestampFormats.All.Select(Row)];

    private static string HexOf(TimestampFormat format, TimestampRaw raw)
    {
        ulong bits = format.IsFloat ? BitConverter.DoubleToUInt64Bits(raw.Double) : (ulong)(raw.Integer & ulong.MaxValue);
        string hex = bits.ToString("X", CultureInfo.InvariantCulture).PadLeft(format.Bytes * 2, '0');
        return hex[^(format.Bytes * 2)..];
    }

    /// <summary>生の値をバイト列にする (DOS はバイト順が 16 bit ごと: 時刻、日付の順)。</summary>
    public static byte[] BytesOf(TimestampFormat format, TimestampRaw raw, bool bigEndian)
    {
        ulong bits = format.IsFloat ? BitConverter.DoubleToUInt64Bits(raw.Double) : (ulong)(raw.Integer & ulong.MaxValue);
        byte[] bytes = new byte[format.Bytes];
        if (format.Id == "dos")
        {
            WriteWord(bytes, 0, (ushort)(bits & 0xFFFF), bigEndian);
            WriteWord(bytes, 2, (ushort)(bits >> 16), bigEndian);
            return bytes;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[bigEndian ? bytes.Length - 1 - i : i] = (byte)(bits >> (8 * i));
        }

        return bytes;
    }

    private static void WriteWord(byte[] bytes, int index, ushort value, bool bigEndian)
    {
        bytes[index + (bigEndian ? 1 : 0)] = (byte)value;
        bytes[index + (bigEndian ? 0 : 1)] = (byte)(value >> 8);
    }

    /// <summary>バイト列から生の値にする(<see cref="BytesOf"/> の逆)。</summary>
    public static TimestampRaw RawFromBytes(TimestampFormat format, ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        ulong bits = 0;
        if (format.Id == "dos")
        {
            ushort time = ReadWord(bytes, 0, bigEndian), date = ReadWord(bytes, 2, bigEndian);
            return new TimestampRaw(((long)date << 16) | time, 0);
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bigEndian ? bytes[i] : bytes[bytes.Length - 1 - i];
            bits = (bits << 8) | b;
        }

        if (format.IsFloat)
        {
            return new TimestampRaw(0, BitConverter.UInt64BitsToDouble(bits));
        }

        BigInteger value = bits;
        if (Signed(format) && format.Bytes * 8 <= 64 && (bits & (1UL << (format.Bytes * 8 - 1))) != 0)
        {
            value -= BigInteger.One << (format.Bytes * 8);
        }

        return new TimestampRaw(value, 0);
    }

    private static ushort ReadWord(ReadOnlySpan<byte> bytes, int index, bool bigEndian) =>
        bigEndian ? (ushort)((bytes[index] << 8) | bytes[index + 1]) : (ushort)((bytes[index + 1] << 8) | bytes[index]);
}
