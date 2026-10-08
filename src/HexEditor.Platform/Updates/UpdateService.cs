using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using HexEditor.Platform.Network;

namespace HexEditor.Platform.Updates;

/// <summary>確認できなかった理由 (10 の PKG-17 の「エラー」: 接続できない、GitHub の API の利用制限など)。</summary>
public enum UpdateFailure
{
    None,

    /// <summary>オフラインモード (09 の UI-58)。</summary>
    Offline,

    /// <summary>接続できない・応答がない (15 秒)。</summary>
    NoConnection,

    /// <summary>GitHub の API の利用制限。</summary>
    RateLimited,

    /// <summary>サーバーがエラーを返した。</summary>
    ServerError,

    /// <summary>応答の形式が違う。</summary>
    BadResponse,

    /// <summary>この配布形態・ビルドでは確認できない (開発中の実行)。</summary>
    NotSupported,
}

public enum UpdateCheckOutcome
{
    /// <summary>新しい版はない。</summary>
    UpToDate,

    /// <summary>新しい版がある。</summary>
    Available,

    /// <summary>新しい版はあるが「この版をスキップ」した版なので、自動の確認では知らせない (PKG-22 の仕様 5)。</summary>
    Skipped,

    /// <summary>確認しなかった (自動の確認が無効、オフライン)。</summary>
    NotChecked,

    Failed,

    /// <summary>見つけた版をダウンロード中 (インストーラ版。確認し直さない)。</summary>
    Downloading,

    /// <summary>見つけた版のダウンロードが済んでいる (インストーラ版。「版 X の準備ができました」。PKG-18 の仕様 2)。</summary>
    Ready,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, bool Manual, UpdateOffer? Offer = null, UpdateFailure Failure = UpdateFailure.None);

/// <summary>更新の進み具合 (InfoBar の表示を決める。PKG-22 の仕様 1)。</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    Available,
    Downloading,
    Ready,
}

/// <summary>
/// 更新の確認・ダウンロード・適用の流れ (10 の PKG-17〜PKG-22)。配布形態の違いは <see cref="IUpdateBackend"/> に任せる。
/// 自動の確認の時刻は <see cref="UpdateSchedule"/>、前回の確認の日時などは <c>state.json</c> の <c>update</c> の区分に記録する。
/// 自分から再起動することはない (PKG-22 の仕様 4)。適用は利用者の操作 (<see cref="ApplyAndRestart"/>、<see cref="ApplyOnExit"/>) だけ。
/// </summary>
public sealed class UpdateService
{
    public const string StateSection = "update";

    private readonly IUpdateBackend _backend;
    private readonly UpdatePreferences _preferences;
    private readonly NetworkPolicy _policy;
    private readonly IStateSections? _state;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _downloadLock = new();
    private DateTimeOffset? _lastChecked;

    public UpdateService(IUpdateBackend backend, UpdatePreferences preferences, NetworkPolicy policy, SemanticVersion current,
        IStateSections? state, Func<DateTimeOffset> now)
    {
        _backend = backend;
        _preferences = preferences;
        _policy = policy;
        Current = current;
        _state = state;
        _now = now;
        StartedAt = now();
        JsonObject saved = state?.ReadSection(StateSection) ?? [];
        _lastChecked = ReadTime(saved, "lastChecked");
        LastRunVersion = ReadVersion(saved, "lastRunVersion");
        PendingVersion = ReadVersion(saved, "pendingVersion");
    }

    public SemanticVersion Current { get; }

    public UpdateKind Kind => _backend.Kind;

    public DateTimeOffset StartedAt { get; }

    /// <summary>最後に確認した日時 (設定画面「更新」に出す。PKG-22 の仕様 3)。</summary>
    public DateTimeOffset? LastChecked => _lastChecked;

    /// <summary>前回この設定フォルダで動いた版 (更新後の初回起動の判定。PKG-18 の仕様 6)。</summary>
    public SemanticVersion? LastRunVersion { get; }

    /// <summary>前回、適用を指示した版 (適用の失敗の判定。PKG-18 の「エラー」)。</summary>
    public SemanticVersion? PendingVersion { get; }

    public UpdatePhase Phase { get; private set; }

    /// <summary>見つかった版 (Available / Downloading / Ready のとき)。</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>ダウンロードの進み具合 (0〜100)。</summary>
    public int DownloadProgress { get; private set; }

    /// <summary>状態が変わった。どのスレッドからも呼ばれる。</summary>
    public event EventHandler? Changed;

    /// <summary>次の自動の確認の時刻。</summary>
    public DateTimeOffset NextAutomaticCheck => UpdateSchedule.NextCheck(StartedAt, _lastChecked);

    /// <summary>
    /// 自動の確認をする時刻になったか (定期的に呼ぶ)。自動の確認が無効・オフラインなら false (通信しない)。
    /// 新しい版が見つかった後 (Available。自動のダウンロードに失敗した場合を含む) も 24 時間ごとに確認し直す (PKG-17 の仕様 1、
    /// PKG-18 の「エラー」: 次の周期で再試行)。確認中・ダウンロード中・ダウンロード済みのときは確認しない。
    /// </summary>
    public bool IsAutomaticCheckDue() =>
        _backend.Kind != UpdateKind.None && (Phase is UpdatePhase.Idle or UpdatePhase.Available) && _policy.UpdateCheckAllowed(manual: false)
        && _now() >= NextAutomaticCheck;

    /// <summary>
    /// 更新の確認 (PKG-17)。オフラインなら通信せずに Failed (Offline)。自動の確認が無効なら自動では通信しない。
    /// 自動の確認では「この版をスキップ」した版を知らせない (Skipped)。手動の確認では知らせる (PKG-22 の仕様 5)。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!_policy.UpdateCheckAllowed(manual))
        {
            return _policy.Offline && manual
                ? new UpdateCheckResult(UpdateCheckOutcome.Failed, manual, Failure: UpdateFailure.Offline)
                : new UpdateCheckResult(UpdateCheckOutcome.NotChecked, manual, Failure: _policy.Offline ? UpdateFailure.Offline : UpdateFailure.None);
        }

        if (_backend.Kind == UpdateKind.None)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, manual, Failure: UpdateFailure.NotSupported);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // ダウンロード中・準備済みの版があれば確認し直さない (準備済みなら「版 X の準備ができました」を出し直す。PKG-18 の仕様 2)。
            if (Phase is UpdatePhase.Downloading or UpdatePhase.Ready && Offer is { } current)
            {
                return new UpdateCheckResult(Phase == UpdatePhase.Ready ? UpdateCheckOutcome.Ready : UpdateCheckOutcome.Downloading, manual, current);
            }

            // 前に見つけた版 (Available) は、確認に失敗しても残す (次の周期で再試行する)。
            UpdatePhase before = Phase;
            SetPhase(UpdatePhase.Checking);
            UpdateOffer? offer;
            try
            {
                offer = await _backend.FindAsync(_preferences.Channel, manual, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                RecordChecked();
                SetPhase(before == UpdatePhase.Available && Offer is not null ? UpdatePhase.Available : UpdatePhase.Idle);
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, manual, Failure: Classify(ex));
            }

            RecordChecked();
            if (offer is null)
            {
                Offer = null;
                SetPhase(UpdatePhase.Idle);
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, manual);
            }

            if (!manual && _preferences.SkippedVersion is { } skipped && skipped.SameVersion(offer.Version))
            {
                Offer = null;
                SetPhase(UpdatePhase.Idle);
                return new UpdateCheckResult(UpdateCheckOutcome.Skipped, manual, offer);
            }

            Offer = offer;
            SetPhase(UpdatePhase.Available);
            return new UpdateCheckResult(UpdateCheckOutcome.Available, manual, offer);
        }
        catch (OperationCanceledException)
        {
            SetPhase(Offer is null ? UpdatePhase.Idle : UpdatePhase.Available);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>見つかった版を裏でダウンロードするか (インストーラ版で <c>update.downloadAutomatically</c> が true。PKG-18 の仕様 1)。</summary>
    public bool ShouldDownloadAutomatically => _backend.Kind == UpdateKind.Installer && _preferences.DownloadAutomatically && _policy.Allows(NetworkFeature.Updates);

    /// <summary>
    /// ダウンロードする (インストーラ版)。成功したら Ready。失敗・取り消しは Available に戻して例外を投げる。
    /// 既にダウンロード中・ダウンロード済みなら何もしない (false。自動のダウンロード中に「ダウンロード」を押しても 2 つ目を始めない)。
    /// </summary>
    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        UpdateOffer offer;
        lock (_downloadLock)
        {
            if (Phase is UpdatePhase.Downloading or UpdatePhase.Ready)
            {
                return false;
            }

            if (Offer is not { } found || _backend.Kind != UpdateKind.Installer)
            {
                throw new InvalidOperationException("There is no update to download.");
            }

            offer = found;
            SetPhase(UpdatePhase.Downloading);
        }

        try
        {
            await _backend.DownloadAsync(offer, p =>
            {
                DownloadProgress = Math.Clamp(p, 0, 100);
                Changed?.Invoke(this, EventArgs.Empty);
            }, cancellationToken).ConfigureAwait(false);
            SetPhase(UpdatePhase.Ready);
            return true;
        }
        catch
        {
            SetPhase(UpdatePhase.Available);
            throw;
        }
    }

    /// <summary>
    /// 「今すぐ最新の安定版に戻す」を出すか (PKG-21 の仕様 4): 今の版がプレビュー版で、チャネルが stable。MSIX 版 (Store は安定版だけ) と
    /// 更新しない配布形態では出さない。
    /// </summary>
    public bool CanReturnToStable =>
        _backend.Kind is UpdateKind.Installer or UpdateKind.Portable && Current.Preview is not null && _preferences.Channel == ReleaseChannel.Stable;

    /// <summary>
    /// 「今すぐ最新の安定版に戻す」(PKG-21 の仕様 4。確認は呼び出し側が先に行う): 最新の安定版 (今の版より古くてもよい) を探し、
    /// 見つかれば新しい版と同じ流れ (Available。インストーラ版はダウンロードして「再起動して更新」、ポータブル版はダウンロードページ) に乗せる。
    /// オフラインなら通信せずに Failed (Offline)。今の版と同じ版しかなければ UpToDate。
    /// </summary>
    public async Task<UpdateCheckResult> FindLatestStableAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.UpdateCheckAllowed(manual: true))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, true, Failure: UpdateFailure.Offline);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Phase is UpdatePhase.Downloading)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Downloading, true, Offer);
            }

            UpdateOffer? offer;
            try
            {
                offer = await _backend.FindLatestStableAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Failed, true, Failure: Classify(ex));
            }

            if (offer is null || offer.Version.SameVersion(Current))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, true);
            }

            Offer = offer;
            SetPhase(UpdatePhase.Available);
            return new UpdateCheckResult(UpdateCheckOutcome.Available, true, offer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>「この版をスキップ」(PKG-20、PKG-22 の仕様 5)。</summary>
    public void Skip()
    {
        if (Offer is { } offer)
        {
            _preferences.SkippedVersion = offer.Version;
        }

        Offer = null;
        SetPhase(UpdatePhase.Idle);
    }

    /// <summary>「後で」(PKG-18 の仕様 4): 次にアプリを終了したときに適用する。</summary>
    public void ApplyOnExit()
    {
        if (Phase == UpdatePhase.Ready && Offer is { } offer)
        {
            RememberPending(offer.Version);
            _backend.ApplyOnExit(offer);
        }
    }

    /// <summary>「再起動して更新」(PKG-18 の仕様 3)。未保存の変更の確認は呼び出し側が先に行う。</summary>
    public void ApplyAndRestart(IReadOnlyList<string> restartArguments)
    {
        if (Phase == UpdatePhase.Ready && Offer is { } offer)
        {
            RememberPending(offer.Version);
            _backend.ApplyAndRestart(offer, restartArguments);
        }
    }

    /// <summary>
    /// 「再起動して更新」を押せるか (PKG-18 の仕様 5)。実行中の長時間処理 (保存を含む) があるときは押せない。
    /// </summary>
    public static bool CanRestartNow(int runningOperations) => runningOperations == 0;

    /// <summary>
    /// 起動時の知らせ (PKG-18 の仕様 6・「エラー」): 前回より新しい版で起動したら「版 X に更新しました」、
    /// 適用を指示した版より古い版のままなら「更新に失敗しました」。どちらでもなければ null。
    /// </summary>
    public (bool Updated, SemanticVersion Version)? StartupNotice()
    {
        if (PendingVersion is { } pending && Current < pending)
        {
            return (false, pending);
        }

        if (LastRunVersion is { } last && Current > last)
        {
            return (true, Current);
        }

        return null;
    }

    /// <summary>今の版を「前回動いた版」として記録する (終了時に呼ぶ。初回起動の判定を他の機能と取り合わないよう、起動時には書かない)。</summary>
    public void RecordRun()
    {
        JsonObject section = _state?.ReadSection(StateSection) ?? [];
        section["lastRunVersion"] = Current.SemVer;
        if (PendingVersion is { } pending && Current >= pending)
        {
            section.Remove("pendingVersion");
        }

        _state?.WriteSection(StateSection, section);
    }

    /// <summary>確認できなかった理由に直す。</summary>
    public static UpdateFailure Classify(Exception ex) => ex switch
    {
        NetworkDisabledException => UpdateFailure.Offline,
        NotSupportedException => UpdateFailure.NotSupported,
        TimeoutException or TaskCanceledException => UpdateFailure.NoConnection,
        HttpRequestException { Data: var d } when d.Contains("RateLimited") => UpdateFailure.RateLimited,
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } => UpdateFailure.RateLimited,
        HttpRequestException { StatusCode: not null } => UpdateFailure.ServerError,
        HttpRequestException => UpdateFailure.NoConnection,
        System.Text.Json.JsonException or FormatException => UpdateFailure.BadResponse,
        _ when ex.InnerException is { } inner => Classify(inner),
        _ => UpdateFailure.NoConnection,
    };

    private void RecordChecked()
    {
        _lastChecked = _now();
        JsonObject section = _state?.ReadSection(StateSection) ?? [];
        section["lastChecked"] = _lastChecked.Value.ToString("O", CultureInfo.InvariantCulture);
        _state?.WriteSection(StateSection, section);
    }

    private void RememberPending(SemanticVersion version)
    {
        JsonObject section = _state?.ReadSection(StateSection) ?? [];
        section["pendingVersion"] = version.SemVer;
        section["lastRunVersion"] = Current.SemVer;
        _state?.WriteSection(StateSection, section);
    }

    private void SetPhase(UpdatePhase phase)
    {
        Phase = phase;
        if (phase is UpdatePhase.Idle or UpdatePhase.Checking)
        {
            DownloadProgress = 0;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static DateTimeOffset? ReadTime(JsonObject section, string name) =>
        section[name] is JsonValue v && v.TryGetValue(out string? s)
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;

    private static SemanticVersion? ReadVersion(JsonObject section, string name) =>
        section[name] is JsonValue v && v.TryGetValue(out string? s) && SemanticVersion.TryParse(s, out SemanticVersion version, out _) ? version : null;
}
