using HexEditor.App.Hosting;
using HexEditor.Platform.Network;
using HexEditor.Platform.Updates;

namespace HexEditor.App.Services;

/// <summary>
/// アプリが自分から行う通信の入口と更新の確認を、配布形態に合わせて組み立てる (00-overview 11.4、09 の UI-58、10 の PKG-17〜PKG-22)。
/// 配布形態ごとの実装 (PKG-17 の仕様 4): インストーラ版は Velopack、MSIX 版は Microsoft Store、ポータブル版は GitHub Releases の API。
/// 開発中の実行は確認しない。テスト用のビルドで設定 <c>test.update.source</c> があれば、開発中の実行もポータブル版と同じ方法で
/// その配布元を確認する (UI テストで偽の配布元を使うため)。
/// </summary>
public static class AppUpdates
{
    private static NetworkClient? _network;
    private static UpdateService? _service;

    public static NetworkPolicy Policy => new(new SettingsAccess(App.Settings));

    public static UpdatePreferences Preferences => new(new SettingsAccess(App.Settings), PackagingTestHooks.TestBuild);

    public static NetworkClient Network => _network ??= new NetworkClient(Policy, Program.Environment.AppVersion);

    /// <summary>今の時刻 (テスト用の命令で進められる)。</summary>
    public static DateTimeOffset Now() => DateTimeOffset.UtcNow + PackagingTestHooks.UpdateClockOffset;

    public static UpdateService Service => _service ??= Create();

    private static UpdateService Create()
    {
        IAppEnvironment env = Program.Environment;
        UpdatePreferences preferences = Preferences;
        UpdateSource source = UpdateSource.Resolve(preferences);
        SemanticVersion current = SemanticVersion.FromInformationalVersion(env.InformationalVersion);
        IUpdateBackend backend = env.Distribution switch
        {
            Distribution.Installer => VelopackBootstrap.CreateUpdateBackend(Network, source, env.ProcessArchitecture) ?? new NoUpdateBackend(),
            Distribution.Msix => new StoreUpdateBackend(Network, env.AppUserModelId.Split('!')[0]),
            Distribution.Portable => new GitHubUpdateBackend(Network, source, current),
            _ when preferences.TestSource is not null => new GitHubUpdateBackend(Network, source, current),
            _ => new NoUpdateBackend(),
        };
        AppLog.Info($"Updates: {backend.Kind}{(preferences.TestSource is null ? string.Empty : " (test source)")}");
        return new UpdateService(backend, preferences, Policy, current, new Platform.StateFile(env.Locations.Settings), Now);
    }
}
