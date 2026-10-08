using HexEditor.Platform.Network;
using HexEditor.Platform.Updates;

namespace HexEditor.App.Hosting;

/// <summary>
/// Velopack の初期化とフック (PKG-08、PKG-11 の手順 1) と、インストーラ版の更新 (PKG-18)。インストーラ版のビルド (HEX_DISTRO_INSTALLER)
/// 以外では何もしない。フック用の引数で起動された場合、Velopack がフックを呼んでからプロセスを終える (ウィンドウは作らない)。
/// </summary>
public static class VelopackBootstrap
{
    public static void Run(string[] args)
    {
#if HEX_DISTRO_INSTALLER
        Velopack.VelopackApp.Build()
            .SetArgs(args)
            .OnAfterInstallFastCallback(v => CreateHooks().AfterInstall(v.ToString()))
            .OnAfterUpdateFastCallback(v => CreateHooks().AfterUpdate(v.ToString()))
            .OnBeforeUninstallFastCallback(v => CreateHooks().BeforeUninstall(v.ToString()))
            .Run();
#else
        _ = args;
#endif
    }

    /// <summary>
    /// インストーラ版の更新の実装 (Velopack の UpdateManager と GithubSource。PKG-17 の仕様 4、PKG-18、PKG-21 の仕様 3)。
    /// インストーラ版のビルドでなければ null。
    /// </summary>
    public static IUpdateBackend? CreateUpdateBackend(NetworkClient network, UpdateSource source, System.Runtime.InteropServices.Architecture architecture)
    {
#if HEX_DISTRO_INSTALLER
        return new VelopackUpdateBackend(network, source, architecture);
#else
        _ = (network, source, architecture);
        return null;
#endif
    }

#if HEX_DISTRO_INSTALLER
    private static Platform.Shell.InstallHooks CreateHooks()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HexEditor.exe");

        // Explorer に出す文字列は、インストールした利用者の表示言語のリソースから取る (UI-54 の仕様 1)。読めなければ英語。
        return new Platform.Shell.InstallHooks(new Platform.Shell.WindowsUserRegistry(), InstallerEnvironment.DataRoot(localAppData), exe,
            labels: Services.ExplorerIntegration.Labels());
    }

    /// <summary>Velopack での更新。チャネルは <c>win-&lt;アーキテクチャ&gt;-stable / -preview</c> を ExplicitChannel で選ぶ。</summary>
    private sealed class VelopackUpdateBackend(NetworkClient network, UpdateSource source, System.Runtime.InteropServices.Architecture architecture)
        : IUpdateBackend
    {
        public UpdateKind Kind => UpdateKind.Installer;

        public async Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken)
        {
            network.EnsureAllowed(NetworkFeature.Updates, manual);
            Velopack.UpdateManager manager = Manager(channel, allowDowngrade: false);
            if (!manager.IsInstalled)
            {
                throw new NotSupportedException("Velopack is not installed.");
            }

            Velopack.UpdateInfo? info = await manager.CheckForUpdatesAsync().WaitAsync(NetworkClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);
            return info is null ? null : Offer(manager, info);
        }

        public async Task<UpdateOffer?> FindLatestStableAsync(CancellationToken cancellationToken)
        {
            network.EnsureAllowed(NetworkFeature.Updates);
            Velopack.UpdateManager manager = Manager(ReleaseChannel.Stable, allowDowngrade: true);
            Velopack.UpdateInfo? info = await manager.CheckForUpdatesAsync().WaitAsync(NetworkClient.DefaultTimeout, cancellationToken).ConfigureAwait(false);
            return info is null ? null : Offer(manager, info);
        }

        public async Task DownloadAsync(UpdateOffer offer, Action<int>? progress, CancellationToken cancellationToken)
        {
            (Velopack.UpdateManager manager, Velopack.UpdateInfo info) = ((Velopack.UpdateManager, Velopack.UpdateInfo))offer.Token!;

            // Velopack が差分パッケージを選び、SHA-256 で検証する (PKG-18 の仕様 1・7)。
            await manager.DownloadUpdatesAsync(info, p => progress?.Invoke(p), cancellationToken).ConfigureAwait(false);
        }

        public void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArguments)
        {
            (Velopack.UpdateManager manager, Velopack.UpdateInfo info) = ((Velopack.UpdateManager, Velopack.UpdateInfo))offer.Token!;
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease, [.. restartArguments]);
        }

        public void ApplyOnExit(UpdateOffer offer)
        {
            (Velopack.UpdateManager manager, Velopack.UpdateInfo info) = ((Velopack.UpdateManager, Velopack.UpdateInfo))offer.Token!;
            manager.WaitExitThenApplyUpdates(info.TargetFullRelease, true, false, []);
        }

        private Velopack.UpdateManager Manager(ReleaseChannel channel, bool allowDowngrade) => new(
            new Velopack.Sources.GithubSource(source.Repository.ToString(), null, channel == ReleaseChannel.Preview, new GatedDownloader(network)),
            new Velopack.UpdateOptions { ExplicitChannel = VelopackChannels.For(architecture, channel), AllowVersionDowngrade = allowDowngrade });

        private UpdateOffer Offer(Velopack.UpdateManager manager, Velopack.UpdateInfo info)
        {
            SemanticVersion version = SemanticVersion.Parse(info.TargetFullRelease.Version.ToString());
            Uri page = source.ReleasePage(version);
            return new UpdateOffer(version, page, page, (manager, info));
        }
    }

    /// <summary>Velopack のダウンロードも、アプリの通信の入口 (オフラインの確認と記録。UI-58) を通す。</summary>
    private sealed class GatedDownloader(NetworkClient network) : Velopack.Sources.IFileDownloader
    {
        private readonly Velopack.Sources.HttpClientFileDownloader _inner = new();

        public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30,
            CancellationToken cancelToken = default)
        {
            Check(url);
            return _inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Check(url);
            return _inner.DownloadBytes(url, headers, timeout);
        }

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Check(url);
            return _inner.DownloadString(url, headers, timeout);
        }

        private void Check(string url)
        {
            network.EnsureAllowed(NetworkFeature.Updates);
            network.Record(NetworkFeature.Updates, "GET", new Uri(url));
        }
    }
#endif
}
