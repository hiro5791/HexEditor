using System.Runtime.InteropServices;

namespace HexEditor.Platform.Updates;

/// <summary>
/// 受け取る版を選ぶ (10 の PKG-21 の仕様 2・4)。安定版のチャネルは安定版だけ、プレビュー版のチャネルはプレビュー版と安定版の
/// 新しいほう。どちらも現在の版より新しいものだけを選ぶ (古い版には自動で戻らない)。下書きは選ばない。
/// </summary>
public static class ReleaseSelector
{
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, SemanticVersion current, ReleaseChannel channel) =>
        releases.Where(r => !r.IsDraft && (channel == ReleaseChannel.Preview || r.IsStable) && r.Version > current)
            .MaxBy(r => r.Version);

    /// <summary>最新の安定版 (「今すぐ最新の安定版に戻す」。PKG-21 の仕様 4)。</summary>
    public static ReleaseInfo? LatestStable(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => !r.IsDraft && r.IsStable).MaxBy(r => r.Version);
}

/// <summary>Velopack のチャネルの名前 (PKG-21 の仕様 3: <c>win-x64-stable</c> など)。</summary>
public static class VelopackChannels
{
    public static string For(Architecture architecture, ReleaseChannel channel) =>
        $"win-{(architecture == Architecture.Arm64 ? "arm64" : "x64")}-{(channel == ReleaseChannel.Preview ? "preview" : "stable")}";
}

/// <summary>
/// 自動の確認の時刻 (10 の PKG-17 の仕様 1): 起動から 30 秒後と、その後 24 時間ごと。
/// 前回の確認から 24 時間たっていなければ、起動時の確認は行わず、前回から 24 時間後に行う。
/// </summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static DateTimeOffset NextCheck(DateTimeOffset startedAt, DateTimeOffset? lastChecked)
    {
        DateTimeOffset afterStart = startedAt + StartupDelay;
        return lastChecked is { } last && last + Interval > afterStart ? last + Interval : afterStart;
    }
}
