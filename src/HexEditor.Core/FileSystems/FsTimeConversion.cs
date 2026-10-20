using System.Globalization;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ファイルシステムの日時 (<see cref="FsTimestamp"/>) を、フォレンジックの日時の表示 (FOR-05 の <see cref="ForensicTimestamp"/>) にする。
/// タイムゾーンを持たない日時 (FAT など) は、記録された値を <c>zone</c> (ケースのタイムゾーン。ケースがなければ Windows のタイムゾーン) の
/// ローカル時刻とみなして UTC にし、「タイムゾーン推定」の印を付ける。
/// </summary>
public static class FsTimeConversion
{
    /// <summary>変換する。日時がない (0) ときは null。</summary>
    public static ForensicTimestamp? ToForensic(FsTimestamp t, TimeZoneInfo zone)
    {
        string raw = t.Raw.ToString("X", CultureInfo.InvariantCulture);
        if (t.Invalid)
        {
            return ForensicTimestamp.Invalid(raw);
        }

        if (t.ZoneUnknown && t.Recorded is DateTime wall)
        {
            return ForensicTimestamp.FromWallClock(wall, Precision(wall.Ticks), raw, zone);
        }

        return t.Utc is DateTime utc ? ForensicTimestamp.FromTicks(utc.Ticks, Precision(utc.Ticks), raw, t.ZoneUnknown) : null;
    }

    /// <summary>表示の精度 (秒より細かい値がなければ秒)。</summary>
    private static long Precision(long ticks) =>
        ticks % TimeSpan.TicksPerSecond == 0 ? TimeSpan.TicksPerSecond : ticks % TimeSpan.TicksPerMillisecond == 0 ? TimeSpan.TicksPerMillisecond : 1;
}
