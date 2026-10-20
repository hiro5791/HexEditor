using HexEditor.Core.Forensics.Time;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>FOR-05 日時の扱い。</summary>
public sealed class ForensicTimeTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static ulong FileTime(DateTime utc) => (ulong)(utc.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks);

    private static ForensicTimeContext Context(TimeDisplayMode mode, TimeZoneInfo zone) => new(mode, zone);

    [Fact]
    [Trait(TC, "TC-FOR-05-01")]
    public void NtfsFileTimeShowsUtcAndTheLocalTimeThatDirWouldShow()
    {
        // 2026-01-15T01:30:00Z の FILETIME は UTC のまま。東京のローカルは 10:30 (+09:00)。dir の表示 (2026/01/15 10:30) と同じ時刻。
        ForensicTimestamp t = ForensicTimestamp.FromFileTime(FileTime(new DateTime(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc)));
        Assert.True(t.IsValid);
        Assert.False(t.ZoneEstimated);
        Assert.Equal("2026-01-15T01:30:00.0000000Z", ForensicTimeFormat.Utc(t));
        Assert.Equal("2026-01-15T10:30:00.0000000+09:00", ForensicTimeFormat.Local(t, Tokyo));

        // 精度が 1 秒の値 (ISO 8601 の秒まで) は小数部を出さない。
        ForensicTimestamp seconds = ForensicTimestamp.FromUtc(new DateTime(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc));
        Assert.Equal("2026-01-15T01:30:00Z", ForensicTimeFormat.Utc(seconds));
        Assert.Equal("2026-01-15T10:30:00+09:00", ForensicTimeFormat.Local(seconds, Tokyo));
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0), TimeZoneInfo.ConvertTimeFromUtc(t.Utc!.Value, Tokyo));

        // 両方の表示は UTC の後ろに括弧でローカル。
        Assert.Equal("2026-01-15T01:30:00Z (2026-01-15T10:30:00+09:00)", Context(TimeDisplayMode.Both, Tokyo).Detail(seconds, "invalid"));
    }

    [Fact]
    [Trait(TC, "TC-FOR-05-02")]
    public void FatTimesAreMarkedAsZoneEstimatedAndFollowTheCaseTimeZone()
    {
        // readme.txt の更新日時 2026-01-15 10:30:00 (タイムゾーンなし)。
        ushort date = (ushort)(((2026 - 1980) << 9) | (1 << 5) | 15);
        ushort time = (ushort)((10 << 11) | (30 << 5) | 0);

        ForensicTimestamp tokyo = ForensicTimestamp.FromFat(date, time, null, Tokyo);
        Assert.True(tokyo.ZoneEstimated);
        Assert.Equal("2026-01-15T01:30:00Z", ForensicTimeFormat.Utc(tokyo));
        Assert.Equal("2026-01-15T10:30:00+09:00", ForensicTimeFormat.Local(tokyo, Tokyo));

        // ケースのタイムゾーンを UTC に変えると、UTC もローカルも変わる。
        ForensicTimestamp utc = ForensicTimestamp.FromFat(date, time, null, Utc);
        Assert.True(utc.ZoneEstimated);
        Assert.Equal("2026-01-15T10:30:00Z", ForensicTimeFormat.Utc(utc));
        Assert.Equal("2026-01-15T10:30:00+00:00", ForensicTimeFormat.Local(utc, Utc));
    }

    [Fact]
    [Trait(TC, "TC-FOR-05-03")]
    public void UtcDisplayModeHasNoLocalColumnAndEveryValueEndsWithZ()
    {
        Assert.Equal([TimeColumn.Utc], Context(TimeDisplayMode.Utc, Tokyo).Columns);
        Assert.Equal([TimeColumn.Utc, TimeColumn.Local], Context(TimeDisplayMode.Both, Tokyo).Columns);
        Assert.Equal([TimeColumn.Local], Context(TimeDisplayMode.Local, Tokyo).Columns);

        ForensicTimeContext utcOnly = Context(TimeDisplayMode.Utc, Tokyo);
        ForensicTimestamp t = ForensicTimestamp.FromFileTime(FileTime(new DateTime(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc)));
        foreach (TimeColumn column in utcOnly.Columns)
        {
            Assert.EndsWith("Z", utcOnly.Cell(t, column, "invalid"), StringComparison.Ordinal);
        }

        Assert.EndsWith("Z", utcOnly.Detail(t, "invalid"), StringComparison.Ordinal);
        Assert.DoesNotContain("+", utcOnly.Detail(t, "invalid"), StringComparison.Ordinal);

        // 設定の値の読み取り。
        Assert.Equal(TimeDisplayMode.Utc, ForensicTimeContext.ParseMode("utc"));
        Assert.Equal(TimeDisplayMode.Both, ForensicTimeContext.ParseMode("something else"));
        Assert.Equal("local", ForensicTimeContext.ModeToSetting(TimeDisplayMode.Local));
    }

    [Fact]
    [Trait(TC, "TC-FOR-05-04")]
    public void OutOfRangeFileTimesAreShownAsInvalidWithoutThrowing()
    {
        ForensicTimeContext context = Context(TimeDisplayMode.Both, Tokyo);
        foreach (ulong raw in new[] { 0xFFFFFFFFFFFFFFFFUL, 0x8000000000000000UL })
        {
            ForensicTimestamp t = ForensicTimestamp.FromFileTime(raw);
            Assert.False(t.IsValid);
            string text = context.Detail(t, "invalid time");
            Assert.Contains(raw.ToString("X16"), text, StringComparison.Ordinal);
            Assert.Contains("invalid time", text, StringComparison.Ordinal);
            Assert.Equal(string.Empty, ForensicTimeFormat.Utc(t));
        }

        // 0 は 1601-01-01 (有効な値)。
        ForensicTimestamp zero = ForensicTimestamp.FromFileTime(0);
        Assert.True(zero.IsValid);
        Assert.StartsWith("1601-01-01T00:00:00", ForensicTimeFormat.Utc(zero), StringComparison.Ordinal);
    }

    [Fact]
    public void ExFatUsesTheRecordedOffsetWhenValidAndOtherwiseBehavesLikeFat()
    {
        ushort date = (ushort)(((2026 - 1980) << 9) | (1 << 5) | 15);
        ushort time = (ushort)((10 << 11) | (30 << 5));
        // オフセット +9:00 = 36 × 15 分 = 0x24、有効 (bit 7)。
        ForensicTimestamp withOffset = ForensicTimestamp.FromExFat(date, time, 0, 0x80 | 36, Utc);
        Assert.False(withOffset.ZoneEstimated);
        Assert.Equal("2026-01-15T01:30:00.00Z", ForensicTimeFormat.Utc(withOffset));

        // 負のオフセット (-5:00 = -20 × 15 分 → 7 bit の 2 の補数 0x6C)。
        ForensicTimestamp negative = ForensicTimestamp.FromExFat(date, time, 0, 0x80 | 0x6C, Utc);
        Assert.Equal("2026-01-15T15:30:00.00Z", ForensicTimeFormat.Utc(negative));

        ForensicTimestamp invalidOffset = ForensicTimestamp.FromExFat(date, time, 0, 0x24, Tokyo);
        Assert.True(invalidOffset.ZoneEstimated);
        Assert.Equal("2026-01-15T01:30:00.00Z", ForensicTimeFormat.Utc(invalidOffset));
    }

    [Fact]
    public void FatEdgeCasesDoNotThrow()
    {
        Assert.False(ForensicTimestamp.FromFat(0, 0, null, Tokyo).IsValid);
        Assert.False(ForensicTimestamp.FromFat(0xFFFF, 0xFFFF, null, Tokyo).IsValid);
        // 2026-02-30 のような存在しない日付。
        Assert.False(ForensicTimestamp.FromFat((ushort)(((2026 - 1980) << 9) | (2 << 5) | 30), 0, null, Tokyo).IsValid);

        // 夏時間で存在しない時刻 (ニューヨーク 2026-03-08 02:30) と、2 回ある時刻 (2026-11-01 01:30) でも例外にならない。
        TimeZoneInfo newYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        ushort gap = (ushort)(((2026 - 1980) << 9) | (3 << 5) | 8);
        Assert.True(ForensicTimestamp.FromFat(gap, (ushort)((2 << 11) | (30 << 5)), null, newYork).IsValid);
        ushort ambiguous = (ushort)(((2026 - 1980) << 9) | (11 << 5) | 1);
        ForensicTimestamp a = ForensicTimestamp.FromFat(ambiguous, (ushort)((1 << 11) | (30 << 5)), null, newYork);
        Assert.True(a.IsValid);
        // 2 回ある時刻は標準時の側 (UTC-5): 06:30Z。
        Assert.Equal("2026-11-01T06:30:00Z", ForensicTimeFormat.Utc(a));
    }

    [Fact]
    public void OtherFormatsFollowTheTable()
    {
        // ext4: 拡張領域のナノ秒と 2038 年以降。
        ForensicTimestamp ext = ForensicTimestamp.FromExt(0x7FFFFFFF, (123_456_700u << 2) | 1u);
        Assert.True(ext.IsValid);
        string expectedSecond = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(0x7FFFFFFFL + (1L << 32)).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith(expectedSecond + ".1234567", ForensicTimeFormat.Utc(ext), StringComparison.Ordinal);
        Assert.Equal("1970-01-01T00:00:00Z", ForensicTimeFormat.Utc(ForensicTimestamp.FromExt(0, null)));

        // HFS+: 1904 年起点。
        Assert.Equal("1970-01-01T00:00:00Z", ForensicTimeFormat.Utc(ForensicTimestamp.FromHfsPlus(2_082_844_800u)));
        // HFS+ のボリュームヘッダーの作成日時はローカル時刻 (FAT と同じ扱い)。
        ForensicTimestamp local = ForensicTimestamp.FromHfsPlusLocal(2_082_844_800u + 3600 * 9, Tokyo);
        Assert.True(local.ZoneEstimated);
        Assert.Equal("1970-01-01T00:00:00Z", ForensicTimeFormat.Utc(local));

        // APFS: 1970 年起点のナノ秒。
        Assert.Equal("2001-09-09T01:46:40.0000000Z", ForensicTimeFormat.Utc(ForensicTimestamp.FromApfs(1_000_000_000_000_000_000UL)));
        Assert.True(ForensicTimestamp.FromApfs(ulong.MaxValue).IsValid);

        // ISO 9660 (7 バイト): 2026-01-15 10:30:00、オフセット +36 × 15 分 = +9:00。
        byte[] iso = [126, 1, 15, 10, 30, 0, 36];
        ForensicTimestamp isoTime = ForensicTimestamp.FromIso9660Short(iso);
        Assert.Equal("2026-01-15T01:30:00Z", ForensicTimeFormat.Utc(isoTime));
        Assert.False(isoTime.ZoneEstimated);
        Assert.False(ForensicTimestamp.FromIso9660Short(new byte[7]).IsValid);

        // ISO 9660 (17 バイト)。
        byte[] iso17 = [.. "2026011510300000"u8, 36];
        Assert.Equal("2026-01-15T01:30:00.00Z", ForensicTimeFormat.Utc(ForensicTimestamp.FromIso9660Long(iso17)));

        // UDF: 種類 1 (ローカル) + 540 分、2026-01-15 10:30:00。
        byte[] udf = [0x1C, 0x12, 0xEA, 0x07, 1, 15, 10, 30, 0, 0, 0, 0];
        ForensicTimestamp udfTime = ForensicTimestamp.FromUdf(udf, Utc);
        Assert.StartsWith("2026-01-15T01:30:00", ForensicTimeFormat.Utc(udfTime), StringComparison.Ordinal);
        Assert.False(udfTime.ZoneEstimated);

        // UDF のタイムゾーン未指定 (-2047) は FAT と同じ扱い。
        byte[] unspecified = [0x01, 0x18, 0xEA, 0x07, 1, 15, 10, 30, 0, 0, 0, 0];
        Assert.True(ForensicTimestamp.FromUdf(unspecified, Tokyo).ZoneEstimated);
    }

    [Fact]
    public void ZoneResolutionFallsBackToTheWindowsZone()
    {
        Assert.Equal(Tokyo.Id, ForensicTimeContext.ResolveZone("Tokyo Standard Time").Id);
        Assert.Equal(TimeZoneInfo.Local.Id, ForensicTimeContext.ResolveZone(null).Id);
        Assert.Equal(TimeZoneInfo.Local.Id, ForensicTimeContext.ResolveZone("No Such Zone").Id);
        Assert.Contains("Tokyo", ForensicTimeFormat.ZoneName(Tokyo), StringComparison.Ordinal);
    }
}
