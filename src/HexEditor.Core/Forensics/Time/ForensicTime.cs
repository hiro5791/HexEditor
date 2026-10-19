using System.Buffers.Binary;
using System.Globalization;

namespace HexEditor.Core.Forensics.Time;

/// <summary>
/// フォレンジックの日時 (12 の FOR-05)。内部では UTC の 100 ナノ秒単位 (<see cref="UtcTicks"/>) で持ち、元のデータの精度
/// (<see cref="PrecisionTicks"/>。2 秒・10 ミリ秒・1 秒・100 ナノ秒など) と、タイムゾーンを推定したか (<see cref="ZoneEstimated"/>。FAT など
/// タイムゾーンを持たない形式) を一緒に持つ。範囲外・存在しない日付は <see cref="IsValid"/> が false で、生の値の 16 進 (<see cref="RawHex"/>) を持つ。
/// 作るときに例外は出さない (仕様 5)。
/// </summary>
public readonly record struct ForensicTimestamp
{
    /// <summary>1970-01-01 の <see cref="DateTime.Ticks"/>。</summary>
    internal const long UnixEpochTicks = 621_355_968_000_000_000L;

    /// <summary>1601-01-01 (FILETIME) の <see cref="DateTime.Ticks"/>。</summary>
    internal const long FileTimeEpochTicks = 504_911_232_000_000_000L;

    private ForensicTimestamp(long utcTicks, long precisionTicks, bool estimated, string rawHex)
    {
        UtcTicks = utcTicks;
        PrecisionTicks = precisionTicks;
        ZoneEstimated = estimated;
        RawHex = rawHex;
        IsValid = true;
    }

    private ForensicTimestamp(string rawHex)
    {
        UtcTicks = 0;
        PrecisionTicks = 1;
        ZoneEstimated = false;
        RawHex = rawHex;
        IsValid = false;
    }

    /// <summary>UTC の <see cref="DateTime.Ticks"/> (0001-01-01 起点、100 ナノ秒)。無効な値では 0。</summary>
    public long UtcTicks { get; }

    /// <summary>元のデータの精度 (100 ナノ秒単位)。表示ではこの桁までを示す。</summary>
    public long PrecisionTicks { get; }

    /// <summary>タイムゾーンを持たない形式から、ケースのタイムゾーンを仮定して UTC にした (「タイムゾーン推定」の印)。</summary>
    public bool ZoneEstimated { get; }

    /// <summary>元の値の 16 進 (無効な値の表示に使う)。</summary>
    public string RawHex { get; }

    public bool IsValid { get; }

    /// <summary>UTC の日時。無効なら null。</summary>
    public DateTime? Utc => IsValid ? new DateTime(UtcTicks, DateTimeKind.Utc) : null;

    /// <summary>無効な日時 (範囲外・存在しない日付)。<paramref name="rawHex"/> は元の値の 16 進。</summary>
    public static ForensicTimestamp Invalid(string rawHex) => new(rawHex);

    /// <summary>UTC の <see cref="DateTime.Ticks"/> から作る。範囲外なら無効。</summary>
    public static ForensicTimestamp FromTicks(long utcTicks, long precisionTicks, string rawHex, bool estimated = false) =>
        utcTicks is < 0 or > 3_155_378_975_999_999_999L ? Invalid(rawHex) : new(utcTicks, Math.Max(1, precisionTicks), estimated, rawHex);

    /// <summary>UTC の瞬間から作る。</summary>
    public static ForensicTimestamp FromUtc(DateTime utc, long precisionTicks = TimeSpan.TicksPerSecond) =>
        FromTicks(utc.ToUniversalTime().Ticks, precisionTicks, utc.Ticks.ToString("X16", CultureInfo.InvariantCulture));

    // ---- NTFS (FILETIME) ----

    /// <summary>NTFS の FILETIME (1601-01-01 起点の 100 ナノ秒、UTC)。<c>0xFFFFFFFFFFFFFFFF</c> などの範囲外は無効。</summary>
    public static ForensicTimestamp FromFileTime(ulong fileTime)
    {
        string hex = fileTime.ToString("X16", CultureInfo.InvariantCulture);
        if (fileTime > (ulong)(3_155_378_975_999_999_999L - FileTimeEpochTicks))
        {
            return Invalid(hex);
        }

        return new((long)fileTime + FileTimeEpochTicks, 1, false, hex);
    }

    // ---- UNIX 時刻 (ext2/3/4 など) ----

    /// <summary>UNIX 時刻 (秒、UTC)。</summary>
    public static ForensicTimestamp FromUnix(long seconds, long nanoseconds = 0, long precisionTicks = TimeSpan.TicksPerSecond)
    {
        string hex = seconds.ToString("X16", CultureInfo.InvariantCulture);
        if (seconds is < -62_135_596_800L or > 253_402_300_799L || nanoseconds is < 0 or > 999_999_999)
        {
            return Invalid(hex);
        }

        return new(UnixEpochTicks + seconds * TimeSpan.TicksPerSecond + nanoseconds / 100, precisionTicks, false, hex);
    }

    /// <summary>
    /// ext2/3/4 の日時。<paramref name="seconds"/> は 32 bit の符号付き秒。拡張領域 <paramref name="extra"/> があれば、下位 2 bit を
    /// 2038 年以降の値 (秒に 2^32 の倍数を足す)、上位 30 bit をナノ秒として使う。
    /// </summary>
    public static ForensicTimestamp FromExt(int seconds, uint? extra)
    {
        if (extra is not { } e)
        {
            return FromUnix(seconds);
        }

        long total = seconds + ((long)(e & 3) << 32);
        long nanos = e >> 2;
        return nanos > 999_999_999 ? Invalid(((ulong)(uint)seconds << 32 | e).ToString("X16", CultureInfo.InvariantCulture)) : FromUnix(total, nanos, 1);
    }

    // ---- HFS+ / APFS ----

    /// <summary>HFS+ の日時 (1904-01-01 起点の秒、UTC)。</summary>
    public static ForensicTimestamp FromHfsPlus(uint seconds)
    {
        long ticks = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + seconds * TimeSpan.TicksPerSecond;
        return FromTicks(ticks, TimeSpan.TicksPerSecond, seconds.ToString("X8", CultureInfo.InvariantCulture));
    }

    /// <summary>HFS+ のボリュームヘッダーの作成日時。ローカル時刻で書かれているので FAT と同じ扱い (タイムゾーン推定)。</summary>
    public static ForensicTimestamp FromHfsPlusLocal(uint seconds, TimeZoneInfo zone)
    {
        DateTime wall = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddSeconds(seconds);
        return FromWallClock(wall, TimeSpan.TicksPerSecond, seconds.ToString("X8", CultureInfo.InvariantCulture), zone);
    }

    /// <summary>APFS の日時 (1970-01-01 起点のナノ秒、UTC)。</summary>
    public static ForensicTimestamp FromApfs(ulong nanoseconds)
    {
        string hex = nanoseconds.ToString("X16", CultureInfo.InvariantCulture);
        ulong ticks = nanoseconds / 100;
        if (ticks > (ulong)(3_155_378_975_999_999_999L - UnixEpochTicks))
        {
            return Invalid(hex);
        }

        return new(UnixEpochTicks + (long)ticks, 1, false, hex);
    }

    // ---- FAT / exFAT ----

    /// <summary>
    /// FAT の日時。タイムゾーンを持たないので、<paramref name="zone"/> (ケースのタイムゾーン) のローカル時刻とみなして UTC にし、「タイムゾーン推定」の印を付ける。
    /// <paramref name="date"/> が 0 (未設定) や存在しない日付なら無効。<paramref name="tenMs"/> は作成時刻の 10 ミリ秒単位の補正 (0〜199。使わなければ null)。
    /// </summary>
    public static ForensicTimestamp FromFat(ushort date, ushort time, byte? tenMs, TimeZoneInfo zone)
    {
        string hex = ((uint)date << 16 | time).ToString("X8", CultureInfo.InvariantCulture);
        if (!TryFatWallClock(date, time, tenMs, out DateTime wall))
        {
            return Invalid(hex);
        }

        long precision = tenMs is null ? 2 * TimeSpan.TicksPerSecond : TimeSpan.TicksPerMillisecond * 10;
        return FromWallClock(wall, precision, hex, zone);
    }

    /// <summary>FAT のアクセス日 (日付だけ。精度は 1 日)。</summary>
    public static ForensicTimestamp FromFatDate(ushort date, TimeZoneInfo zone)
    {
        string hex = date.ToString("X4", CultureInfo.InvariantCulture);
        if (!TryFatWallClock(date, 0, null, out DateTime wall))
        {
            return Invalid(hex);
        }

        return FromWallClock(wall, TimeSpan.TicksPerDay, hex, zone);
    }

    /// <summary>
    /// exFAT の日時。<paramref name="utcOffset"/> は UTC オフセットの項目 (bit 7 が有効、下位 7 bit が 15 分単位の符号付きの値)。
    /// 有効ならそれを使い (タイムゾーン推定にならない)、無効なら FAT と同じ扱いにする。
    /// </summary>
    public static ForensicTimestamp FromExFat(ushort date, ushort time, byte tenMs, byte utcOffset, TimeZoneInfo zone)
    {
        string hex = ((uint)date << 16 | time).ToString("X8", CultureInfo.InvariantCulture);
        if (!TryFatWallClock(date, time, tenMs, out DateTime wall))
        {
            return Invalid(hex);
        }

        if ((utcOffset & 0x80) == 0)
        {
            return FromWallClock(wall, TimeSpan.TicksPerMillisecond * 10, hex, zone);
        }

        int quarter = utcOffset & 0x7F;
        if (quarter >= 0x40)
        {
            quarter -= 0x80;
        }

        return FromTicks(wall.Ticks - quarter * 15 * TimeSpan.TicksPerMinute, TimeSpan.TicksPerMillisecond * 10, hex);
    }

    private static bool TryFatWallClock(ushort date, ushort time, byte? tenMs, out DateTime wall)
    {
        wall = default;
        int year = 1980 + (date >> 9), month = (date >> 5) & 0xF, day = date & 0x1F;
        int hour = time >> 11, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
        if (date == 0 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour >= 24 || minute >= 60 || second >= 60)
        {
            return false;
        }

        if (tenMs is > 199)
        {
            return false;
        }

        wall = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        if (tenMs is { } t)
        {
            wall = wall.AddMilliseconds(t * 10);
        }

        return true;
    }

    // ---- ISO 9660 / UDF ----

    /// <summary>ISO 9660 の 7 バイトの日時 (年 - 1900、月、日、時、分、秒、15 分単位の UTC オフセット)。全部 0 は未設定で無効。</summary>
    public static ForensicTimestamp FromIso9660Short(ReadOnlySpan<byte> bytes)
    {
        string hex = Convert.ToHexString(bytes[..Math.Min(7, bytes.Length)]);
        if (bytes.Length < 7 || bytes[..7].IndexOfAnyExcept((byte)0) < 0)
        {
            return Invalid(hex);
        }

        return FromComponents(1900 + bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], 0, (sbyte)bytes[6] * 15, TimeSpan.TicksPerSecond, hex);
    }

    /// <summary>ISO 9660 の 17 バイトの日時 (ASCII の <c>YYYYMMDDHHMMSSCC</c> と 15 分単位の UTC オフセット)。数字が 0 だけなら未設定で無効。</summary>
    public static ForensicTimestamp FromIso9660Long(ReadOnlySpan<byte> bytes)
    {
        string hex = Convert.ToHexString(bytes[..Math.Min(17, bytes.Length)]);
        if (bytes.Length < 17)
        {
            return Invalid(hex);
        }

        int[] parts = new int[7];
        int[] widths = [4, 2, 2, 2, 2, 2, 2];
        int at = 0;
        bool allZero = true;
        for (int i = 0; i < parts.Length; i++)
        {
            int value = 0;
            for (int k = 0; k < widths[i]; k++)
            {
                byte c = bytes[at++];
                if (c is < (byte)'0' or > (byte)'9')
                {
                    return Invalid(hex);
                }

                value = value * 10 + (c - '0');
                allZero &= c == '0';
            }

            parts[i] = value;
        }

        if (allZero)
        {
            return Invalid(hex);
        }

        return FromComponents(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6] * 100_000L, (sbyte)bytes[16] * 15, TimeSpan.TicksPerMillisecond * 10, hex);
    }

    /// <summary>
    /// UDF の 12 バイトの日時 (ECMA-167 の 1/7.3)。種類 0 は UTC、種類 1 は現地時刻 + 分単位のオフセット。オフセットが「未指定」(-2047) なら
    /// FAT と同じ扱い (タイムゾーン推定)。
    /// </summary>
    public static ForensicTimestamp FromUdf(ReadOnlySpan<byte> bytes, TimeZoneInfo zone)
    {
        string hex = Convert.ToHexString(bytes[..Math.Min(12, bytes.Length)]);
        if (bytes.Length < 12)
        {
            return Invalid(hex);
        }

        ushort typeAndZone = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int type = typeAndZone >> 12;
        int minutes = typeAndZone & 0xFFF;
        if (minutes >= 0x800)
        {
            minutes -= 0x1000;
        }

        int year = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        if (year == 0 && bytes[4] == 0)
        {
            return Invalid(hex);
        }

        // 秒未満: 1/100 秒、100 マイクロ秒、マイクロ秒 (100 ナノ秒 = 1 tick なので、1 μs = 10 ticks)。
        long subTicks = bytes[9] * TimeSpan.TicksPerMillisecond * 10 + bytes[10] * 1000L + bytes[11] * 10L;
        if (type == 0)
        {
            return FromComponents(year, bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], subTicks, 0, 10, hex);
        }

        if (minutes == -2047 || minutes is < -1440 or > 1440)
        {
            if (!TryWall(year, bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], subTicks, out DateTime wall))
            {
                return Invalid(hex);
            }

            return FromWallClock(wall, 10, hex, zone);
        }

        return FromComponents(year, bytes[4], bytes[5], bytes[6], bytes[7], bytes[8], subTicks, minutes, 10, hex);
    }

    // ---- 共通 ----

    private static ForensicTimestamp FromComponents(int year, int month, int day, int hour, int minute, int second, long subTicks, int offsetMinutes,
        long precision, string hex)
    {
        if (!TryWall(year, month, day, hour, minute, second, subTicks, out DateTime wall))
        {
            return Invalid(hex);
        }

        return FromTicks(wall.Ticks - offsetMinutes * TimeSpan.TicksPerMinute, precision, hex);
    }

    private static bool TryWall(int year, int month, int day, int hour, int minute, int second, long subTicks, out DateTime wall)
    {
        wall = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        wall = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(subTicks);
        return true;
    }

    /// <summary>
    /// ローカル時刻 (タイムゾーンなし) を <paramref name="zone"/> のものとみなして UTC にする。夏時間の切り替えで存在しない時刻は標準時の
    /// オフセットを使い、2 回ある時刻は標準時の側 (後の側) を使う。結果には「タイムゾーン推定」の印が付く。
    /// </summary>
    public static ForensicTimestamp FromWallClock(DateTime wall, long precisionTicks, string rawHex, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(wall))
        {
            offset = zone.BaseUtcOffset;
        }
        else if (zone.IsAmbiguousTime(wall))
        {
            offset = zone.GetAmbiguousTimeOffsets(wall).Min();
        }
        else
        {
            offset = zone.GetUtcOffset(wall);
        }

        return FromTicks(wall.Ticks - offset.Ticks, precisionTicks, rawHex, estimated: true);
    }
}

/// <summary>日時の表示の設定 (FOR-05 の仕様 3)。</summary>
public enum TimeDisplayMode
{
    /// <summary>UTC とローカルの両方 (既定)。</summary>
    Both,

    /// <summary>UTC だけ。</summary>
    Utc,

    /// <summary>ローカルだけ。</summary>
    Local,
}

/// <summary>日時の列の種類。</summary>
public enum TimeColumn
{
    Utc,
    Local,
}

/// <summary>日時の表示の文脈: 表示の設定と、ローカルのタイムゾーン (ケースのタイムゾーン。ケースがなければ Windows のタイムゾーン)。</summary>
public sealed record ForensicTimeContext(TimeDisplayMode Mode, TimeZoneInfo Zone)
{
    /// <summary>設定の値 (<c>both</c> / <c>utc</c> / <c>local</c>)。</summary>
    public const string SettingKey = "forensics.time.display";

    public static ForensicTimeContext Default { get; } = new(TimeDisplayMode.Both, TimeZoneInfo.Local);

    public static TimeDisplayMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "utc" => TimeDisplayMode.Utc,
        "local" => TimeDisplayMode.Local,
        _ => TimeDisplayMode.Both,
    };

    public static string ModeToSetting(TimeDisplayMode mode) => mode switch
    {
        TimeDisplayMode.Utc => "utc",
        TimeDisplayMode.Local => "local",
        _ => "both",
    };

    /// <summary>ケースのタイムゾーン ID からタイムゾーンを引く。空や不明な ID は Windows のタイムゾーン。</summary>
    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>表示する列 (一覧では UTC の列とローカルの列を並べる。設定が「UTC」ならローカルの列は出さない)。</summary>
    public IReadOnlyList<TimeColumn> Columns => Mode switch
    {
        TimeDisplayMode.Utc => [TimeColumn.Utc],
        TimeDisplayMode.Local => [TimeColumn.Local],
        _ => [TimeColumn.Utc, TimeColumn.Local],
    };

    /// <summary>一覧の 1 つの列のセル。</summary>
    public string Cell(ForensicTimestamp value, TimeColumn column, string invalidText) =>
        !value.IsValid ? InvalidCell(value, invalidText) : column == TimeColumn.Utc ? ForensicTimeFormat.Utc(value) : ForensicTimeFormat.Local(value, Zone);

    /// <summary>詳細表示: 設定が「両方」なら UTC の後に括弧でローカルを付ける。</summary>
    public string Detail(ForensicTimestamp value, string invalidText)
    {
        if (!value.IsValid)
        {
            return InvalidCell(value, invalidText);
        }

        return Mode switch
        {
            TimeDisplayMode.Utc => ForensicTimeFormat.Utc(value),
            TimeDisplayMode.Local => ForensicTimeFormat.Local(value, Zone),
            _ => $"{ForensicTimeFormat.Utc(value)} ({ForensicTimeFormat.Local(value, Zone)})",
        };
    }

    private static string InvalidCell(ForensicTimestamp value, string invalidText) => $"{value.RawHex} ({invalidText})";
}

/// <summary>ISO 8601 の表示 (FOR-05 の仕様 2。地域設定の書式は使わない)。</summary>
public static class ForensicTimeFormat
{
    /// <summary>精度に応じた小数部の桁数 (1 秒以上の精度は 0、10 ミリ秒は 2、100 ナノ秒は 7)。</summary>
    public static int FractionDigits(long precisionTicks)
    {
        if (precisionTicks >= TimeSpan.TicksPerSecond)
        {
            return 0;
        }

        int digits = 7;
        long p = Math.Max(1, precisionTicks);
        while (p >= 10)
        {
            p /= 10;
            digits--;
        }

        return Math.Clamp(digits, 0, 7);
    }

    private static string Fraction(long ticks, int digits)
    {
        if (digits == 0)
        {
            return string.Empty;
        }

        long sub = ticks % TimeSpan.TicksPerSecond;
        return "." + sub.ToString("D7", CultureInfo.InvariantCulture)[..digits];
    }

    /// <summary>UTC の表示 (<c>2026-10-07T03:15:42Z</c>)。精度が秒より細かければ小数部も示す。</summary>
    public static string Utc(ForensicTimestamp value)
    {
        if (!value.IsValid)
        {
            return string.Empty;
        }

        var d = new DateTime(value.UtcTicks, DateTimeKind.Utc);
        return string.Create(CultureInfo.InvariantCulture, $"{d:yyyy-MM-ddTHH:mm:ss}{Fraction(value.UtcTicks, FractionDigits(value.PrecisionTicks))}Z");
    }

    /// <summary>ローカルの表示 (<c>2026-10-07T12:15:42+09:00</c>)。</summary>
    public static string Local(ForensicTimestamp value, TimeZoneInfo zone)
    {
        if (!value.IsValid)
        {
            return string.Empty;
        }

        var utc = new DateTime(value.UtcTicks, DateTimeKind.Utc);
        TimeSpan offset = zone.GetUtcOffset(utc);
        DateTime local = value.UtcTicks + offset.Ticks is > 3_155_378_975_999_999_999L or < 0 ? utc : utc + offset;
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        TimeSpan abs = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture,
            $"{local:yyyy-MM-ddTHH:mm:ss}{Fraction(value.UtcTicks + offset.Ticks, FractionDigits(value.PrecisionTicks))}{sign}{abs.Hours:00}:{abs.Minutes:00}");
    }

    /// <summary>タイムゾーンの表示名 (レポートに出す名前)。</summary>
    public static string ZoneName(TimeZoneInfo zone) => zone.Id == zone.DisplayName ? zone.Id : $"{zone.Id} ({zone.DisplayName})";
}
