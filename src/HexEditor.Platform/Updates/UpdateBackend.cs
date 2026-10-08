using HexEditor.Platform.Network;

namespace HexEditor.Platform.Updates;

/// <summary>配布形態ごとの更新の方法 (10 の PKG-17 の仕様 4)。InfoBar のボタンが変わる (PKG-22)。</summary>
public enum UpdateKind
{
    /// <summary>インストーラ版: Velopack でダウンロードして適用する (PKG-18)。</summary>
    Installer,

    /// <summary>MSIX 版: Microsoft Store で更新する (PKG-19)。</summary>
    Store,

    /// <summary>ポータブル版: 新しい版を知らせ、ダウンロードページを開く (PKG-20)。</summary>
    Portable,

    /// <summary>更新しない (開発中の実行。PKG-12 の仕様 1 の 4)。</summary>
    None,
}

/// <summary>見つかった新しい版。<see cref="Token"/> は配布形態の実装が使う値 (Velopack の UpdateInfo など)。</summary>
public sealed record UpdateOffer(SemanticVersion Version, Uri ReleaseNotes, Uri? DownloadPage = null, object? Token = null);

/// <summary>配布形態ごとの更新の実装。</summary>
public interface IUpdateBackend
{
    UpdateKind Kind { get; }

    /// <summary>
    /// <paramref name="channel"/> で受け取れる、現在の版より新しい最新の版を探す。なければ null。
    /// 通信できない・応答が不正なら例外 (<see cref="UpdateService"/> が理由に直す)。
    /// </summary>
    Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken);

    /// <summary>ダウンロードする (インストーラ版だけ。差分パッケージがあれば差分。PKG-18 の仕様 1)。</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>ダウンロードした版を適用してアプリを再起動する (インストーラ版。PKG-18 の仕様 3)。戻らない。</summary>
    void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArguments) => throw new NotSupportedException();

    /// <summary>次にアプリを終了したときに適用する (インストーラ版の「後で」。PKG-18 の仕様 4)。</summary>
    void ApplyOnExit(UpdateOffer offer) => throw new NotSupportedException();

    /// <summary>最新の安定版を探す (「今すぐ最新の安定版に戻す」。PKG-21 の仕様 4。古い版でも返す)。</summary>
    Task<UpdateOffer?> FindLatestStableAsync(CancellationToken cancellationToken) => Task.FromResult<UpdateOffer?>(null);
}

/// <summary>更新しない配布形態 (開発中の実行)。確認すると「この版では更新を確認できません」になる。</summary>
public sealed class NoUpdateBackend : IUpdateBackend
{
    public UpdateKind Kind => UpdateKind.None;

    public Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Updates are not available in this build.");
}

/// <summary>
/// GitHub Releases の API で最新版の版番号だけを取る (ポータブル版。PKG-17 の仕様 4、PKG-20)。
/// チャネルは Pre-release を含めるかどうかで切り替える (PKG-21 の仕様 3)。
/// </summary>
public sealed class GitHubUpdateBackend(NetworkClient network, UpdateSource source, SemanticVersion current, UpdateKind kind = UpdateKind.Portable)
    : IUpdateBackend
{
    public UpdateKind Kind => kind;

    public UpdateSource Source => source;

    public async Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReleaseInfo> releases = await GitHubReleases.FetchAsync(network, source, manual, cancellationToken).ConfigureAwait(false);
        return ReleaseSelector.Newest(releases, current, channel) is { } r ? Offer(r) : null;
    }

    public async Task<UpdateOffer?> FindLatestStableAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ReleaseInfo> releases = await GitHubReleases.FetchAsync(network, source, manual: true, cancellationToken).ConfigureAwait(false);
        return ReleaseSelector.LatestStable(releases) is { } r ? Offer(r) : null;
    }

    private UpdateOffer Offer(ReleaseInfo r)
    {
        Uri page = source.ReleasePage(r.Version);
        return new UpdateOffer(r.Version, page, page, r);
    }
}
