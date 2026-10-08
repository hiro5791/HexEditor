using System.Globalization;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Inspector;

/// <summary>日時の入力の解釈の結果。</summary>
public readonly record struct DateTimeInput(DateTime Value, bool HadOffset, int FractionDigits);

/// <summary>
/// 日時の表示と入力 (INSP-13、INSP-15)。タイムゾーンを持つ形式 (Unix 時刻・FILETIME など。値は UTC) は、選んだタイムゾーンに
/// 変換して末尾にオフセットを付ける。タイムゾーンのない形式 (DOS など) は値のとおりに表示する。
/// </summary>
public static partial class InspectorDateTime
{
    public static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>UUID のタイムスタンプの起点 (1582-10-15 00:00 UTC)。</summary>
    public static readonly DateTime GregorianEpoch = new(1582, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>表示の要素: 日付と時刻、日付だけ、時刻だけ。</summary>
    public enum Parts
    {
        DateAndTime,
        Date,
        Time,
    }

    /// <summary>
    /// UTC の日時を、選んだタイムゾーンで表示する (INSP-15 の仕様 1・3)。<paramref name="fractionDigits"/> は形式の精度
    /// (FILETIME は 7、ミリ秒の形式は 3、秒の形式は 0)。末尾の 0 は省く。変換できなければ null。
    /// </summary>
    public static string? FormatInstant(DateTime utc, int fractionDigits, InspectorOptions o)
    {
        DateTime shown;
        TimeSpan offset;
        try
        {
            if (o.TimeZoneMode == DateTimeZoneMode.Utc)
            {
                shown = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
                offset = TimeSpan.Zero;
            }
            else
            {
                offset = o.LocalTimeZone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
                long ticks = utc.Ticks + offset.Ticks;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                {
                    return null;
                }

                shown = new DateTime(ticks, DateTimeKind.Unspecified);
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (o.DateTimeStyle == DateTimeStyle.Iso8601)
        {
            string zone = o.TimeZoneMode == DateTimeZoneMode.Utc ? "Z" : OffsetText(offset);
            return shown.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(shown, fractionDigits, ".") + zone;
        }

        string suffix = o.TimeZoneMode == DateTimeZoneMode.Utc
            ? o.Text.Utc
            : string.Format(CultureInfo.InvariantCulture, o.Text.UtcOffset, OffsetText(offset));
        return Regional(shown, fractionDigits, o, Parts.DateAndTime) + " " + suffix;
    }

    /// <summary>タイムゾーンのない日時の表示 (INSP-15 の仕様 2)。末尾に「(タイムゾーンなし)」を付ける。</summary>
    public static string FormatNoZone(DateTime value, int fractionDigits, InspectorOptions o, Parts parts = Parts.DateAndTime)
    {
        string text = o.DateTimeStyle == DateTimeStyle.Iso8601
            ? parts switch
            {
                Parts.Date => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Parts.Time => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(value, fractionDigits, "."),
                _ => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(value, fractionDigits, "."),
            }
            : Regional(value, fractionDigits, o, parts);
        return text + " " + o.Text.NoTimeZone;
    }

    private static string Regional(DateTime value, int fractionDigits, InspectorOptions o, Parts parts)
    {
        // 日付の順序と区切りは地域設定、時刻は 24 時間制 (INSP-15 の仕様 3)。
        string date = value.ToString(o.Culture.DateTimeFormat.ShortDatePattern, o.Culture);
        string time = value.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture)
            + Fraction(value, fractionDigits, o.Culture.NumberFormat.NumberDecimalSeparator);
        return parts switch
        {
            Parts.Date => date,
            Parts.Time => time,
            _ => date + " " + time,
        };
    }

    private static string Fraction(DateTime value, int digits, string separator)
    {
        if (digits <= 0)
        {
            return string.Empty;
        }

        long ticks = value.Ticks % TimeSpan.TicksPerSecond;
        string seven = ticks.ToString("D7", CultureInfo.InvariantCulture)[..Math.Min(7, digits)].TrimEnd('0');
        return seven.Length == 0 ? string.Empty : separator + seven;
    }

    /// <summary>オフセットの表示 (+09:00)。</summary>
    public static string OffsetText(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // ---- 入力 (INSP-15 の仕様 4) ----

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})(?:[Tt ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,9}))?)?)?\s*(Z|z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,9}))?)?$")]
    private static partial Regex TimeOnlyPattern();

    /// <summary>
    /// 地域設定に依存しない書式 (ISO 8601 と <c>now</c>) を解釈する。<paramref name="hasZone"/> が true の形式では UTC の日時を返す
    /// (タイムゾーンを書かなければ、選んでいるタイムゾーンとみなす)。false の形式では入力した値をそのまま返す。
    /// <paramref name="allowTimeOnly"/> なら <c>12:34:56</c> だけの入力も受け付ける (DOS 時刻)。解釈できなければ null。
    /// </summary>
    public static DateTimeInput? Parse(string input, bool hasZone, InspectorOptions o, bool allowTimeOnly = false)
    {
        string text = input.Trim();
        if (text.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            DateTime utcNow = o.Time.GetUtcNow().UtcDateTime;
            if (hasZone)
            {
                return new DateTimeInput(utcNow, true, 7);
            }

            DateTime wall = o.TimeZoneMode == DateTimeZoneMode.Utc ? utcNow : TimeZoneInfo.ConvertTimeFromUtc(utcNow, o.LocalTimeZone);
            return new DateTimeInput(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), false, 7);
        }

        int year = 2000, month = 1, day = 1, hour, minute, second = 0;
        string? fraction;
        string? zone = null;
        Match m = IsoPattern().Match(text);
        if (m.Success)
        {
            year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            hour = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            minute = m.Groups[5].Success ? int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) : 0;
            second = m.Groups[6].Success ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;
            fraction = m.Groups[7].Success ? m.Groups[7].Value : null;
            zone = m.Groups[8].Success ? m.Groups[8].Value : null;
        }
        else if (allowTimeOnly && TimeOnlyPattern().Match(text) is { Success: true } t)
        {
            hour = int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
            minute = int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture);
            second = t.Groups[3].Success ? int.Parse(t.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            fraction = t.Groups[4].Success ? t.Groups[4].Value : null;
        }
        else
        {
            return null;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month) || year < 1
            || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        long ticks = new DateTime(year, month, day, hour, minute, second).Ticks;
        int digits = 0;
        if (fraction is not null)
        {
            digits = fraction.Length;
            string seven = (fraction + "0000000")[..7];
            ticks += long.Parse(seven, CultureInfo.InvariantCulture);
        }

        // 7 桁より細かい入力 (ナノ秒) は 100 ナノ秒単位に切り捨てる。精度の判定のため桁数は残す。
        var value = new DateTime(ticks, DateTimeKind.Unspecified);
        if (!hasZone)
        {
            return new DateTimeInput(value, zone is not null, digits);
        }

        try
        {
            DateTime utc;
            if (zone is not null)
            {
                TimeSpan offset = zone is "Z" or "z" ? TimeSpan.Zero : ParseOffset(zone);
                long u = value.Ticks - offset.Ticks;
                if (u < DateTime.MinValue.Ticks || u > DateTime.MaxValue.Ticks)
                {
                    return null;
                }

                utc = new DateTime(u, DateTimeKind.Utc);
            }
            else if (o.TimeZoneMode == DateTimeZoneMode.Utc)
            {
                utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
            else
            {
                utc = TimeZoneInfo.ConvertTimeToUtc(value, o.LocalTimeZone);
            }

            return new DateTimeInput(utc, zone is not null, digits);
        }
        catch (ArgumentException)
        {
            // 夏時間の切り替えで存在しない時刻など。
            return null;
        }
    }

    private static TimeSpan ParseOffset(string zone)
    {
        int sign = zone[0] == '-' ? -1 : 1;
        string digits = zone[1..].Replace(":", string.Empty, StringComparison.Ordinal);
        int hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        int minutes = int.Parse(digits[2..], CultureInfo.InvariantCulture);
        return TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
    }
}
