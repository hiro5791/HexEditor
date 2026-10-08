namespace HexEditor.Platform.Updates;

/// <summary>更新の InfoBar のボタン (10 の PKG-22 の仕様 1)。</summary>
public enum UpdateButton
{
    /// <summary>ダウンロード (インストーラ版で自動のダウンロードがオフのとき)。</summary>
    Download,

    /// <summary>Microsoft Store で更新 (MSIX 版)。</summary>
    OpenStore,

    /// <summary>ダウンロードページを開く (ポータブル版)。</summary>
    OpenDownloadPage,

    ReleaseNotes,

    /// <summary>この版をスキップ。</summary>
    Skip,

    /// <summary>再起動して更新 (インストーラ版のダウンロード完了後)。</summary>
    RestartToUpdate,

    /// <summary>後で (次の終了時に適用)。</summary>
    Later,
}

public enum UpdateMessageSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 更新の InfoBar の内容。<see cref="MessageKey"/> はリソースのキー、<see cref="Arguments"/> はその書式の値。
/// <see cref="AutoClose"/> が true なら 8 秒で閉じる (手動の確認で最新のとき)。
/// </summary>
public sealed record UpdateMessage(string MessageKey, IReadOnlyList<object> Arguments, UpdateMessageSeverity Severity, IReadOnlyList<UpdateButton> Buttons, bool AutoClose = false);

/// <summary>
/// 更新に関する表示を、配布形態をまたいで同じ形にする (10 の PKG-22)。文言は配布形態に関係なく同じで、ボタンだけが違う。
/// </summary>
public static class UpdatePresentation
{
    /// <summary>手動の確認で最新のときの InfoBar を閉じるまでの時間 (PKG-22 の仕様 1)。</summary>
    public static readonly TimeSpan UpToDateCloseAfter = TimeSpan.FromSeconds(8);

    /// <summary>新しい版がある (ダウンロード前)。</summary>
    public static UpdateMessage Available(SemanticVersion version, UpdateKind kind) => new(
        "Update_Available",
        [version.SemVer],
        UpdateMessageSeverity.Informational,
        [
            kind switch
            {
                UpdateKind.Installer => UpdateButton.Download,
                UpdateKind.Store => UpdateButton.OpenStore,
                _ => UpdateButton.OpenDownloadPage,
            },
            UpdateButton.ReleaseNotes,
            UpdateButton.Skip,
        ]);

    /// <summary>ダウンロード完了 (インストーラ版)。</summary>
    public static UpdateMessage Ready(SemanticVersion version) => new(
        "Update_Ready", [version.SemVer], UpdateMessageSeverity.Informational,
        [UpdateButton.RestartToUpdate, UpdateButton.ReleaseNotes, UpdateButton.Later]);

    /// <summary>更新後の初回起動。</summary>
    public static UpdateMessage Updated(SemanticVersion version) => new(
        "Update_Updated", [version.SemVer], UpdateMessageSeverity.Success, [UpdateButton.ReleaseNotes]);

    /// <summary>適用に失敗して元の版に戻った (PKG-18 の「エラー」)。引数はログの場所。</summary>
    public static UpdateMessage ApplyFailed(SemanticVersion version, string logFolder) => new(
        "Update_ApplyFailed", [version.SemVer, logFolder], UpdateMessageSeverity.Error, []);

    /// <summary>手動の確認で最新。</summary>
    public static UpdateMessage UpToDate(SemanticVersion current) => new(
        "Update_UpToDate", [current.SemVer], UpdateMessageSeverity.Success, [], AutoClose: true);

    /// <summary>手動の確認の失敗 (PKG-17 の「エラー」: 理由を表示)。</summary>
    public static UpdateMessage Failed(UpdateFailure failure) => new(
        "Update_Failed", [ReasonKey(failure)], UpdateMessageSeverity.Error, []);

    /// <summary>ダウンロードの失敗 (手動で始めたとき)。</summary>
    public static UpdateMessage DownloadFailed(UpdateFailure failure) => new(
        "Update_DownloadFailed", [ReasonKey(failure)], UpdateMessageSeverity.Error, []);

    /// <summary>理由の文言のリソースキー (<see cref="Failed"/> の引数。App がキーを文言に直してから書式に入れる)。</summary>
    public static string ReasonKey(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Offline => "Update_Reason_Offline",
        UpdateFailure.RateLimited => "Update_Reason_RateLimited",
        UpdateFailure.ServerError => "Update_Reason_Server",
        UpdateFailure.BadResponse => "Update_Reason_BadResponse",
        UpdateFailure.NotSupported => "Update_Reason_NotSupported",
        _ => "Update_Reason_NoConnection",
    };

    /// <summary>
    /// 新しい版を裏でダウンロードしている (インストーラ版で <c>update.downloadAutomatically</c> が true。PKG-18 の仕様 1)。
    /// 手動の確認の結果として「版 X があります」を出すが、「ダウンロード」「この版をスキップ」は出さない (ダウンロードは処理センターに出る)。
    /// </summary>
    public static UpdateMessage Downloading(SemanticVersion version) => new(
        "Update_Available", [version.SemVer], UpdateMessageSeverity.Informational, [UpdateButton.ReleaseNotes]);

    /// <summary>
    /// 確認の結果を InfoBar の内容に直す。表示しないなら null (自動の確認で最新・失敗・スキップした版。PKG-17 の仕様 6)。
    /// <paramref name="downloadsAutomatically"/> が true (見つけた版を続けて裏でダウンロードする) なら、「ダウンロード」付きの
    /// 「版 X があります」は出さない (自動の確認では何も出さず、ダウンロードが終わってから「準備ができました」を出す。PKG-18 の仕様 1・2)。
    /// </summary>
    public static UpdateMessage? ForCheck(UpdateCheckResult result, SemanticVersion current, UpdateKind kind, bool downloadsAutomatically = false) =>
        result.Outcome switch
        {
            UpdateCheckOutcome.Available when result.Offer is { } offer && downloadsAutomatically && kind == UpdateKind.Installer =>
                result.Manual ? Downloading(offer.Version) : null,
            UpdateCheckOutcome.Available when result.Offer is { } offer => Available(offer.Version, kind),
            UpdateCheckOutcome.Downloading when result.Offer is { } offer => result.Manual ? Downloading(offer.Version) : null,
            UpdateCheckOutcome.Ready when result.Offer is { } offer => Ready(offer.Version),
            UpdateCheckOutcome.UpToDate when result.Manual => UpToDate(current),
            UpdateCheckOutcome.Failed when result.Manual => Failed(result.Failure),
            _ => null,
        };
}
