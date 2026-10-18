using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Sftp;

namespace HexEditor.App.Services;

/// <summary>
/// リモートのデータソース (ENG-40〜ENG-42) のアプリ全体の部品: 信頼したホスト鍵の表 (設定フォルダの <c>known_hosts.json</c>)、パスワードの保存先
/// (Windows の資格情報マネージャー)、SFTP の接続 (SSH.NET)。テスト用のビルドで偽の部品が有効な間は、偽の SFTP サーバーとメモリの中の保存先を使い、
/// この PC の資格情報マネージャー・SSH エージェントには触れない。
/// </summary>
public static class RemoteServices
{
    private static KnownHosts? s_knownHosts;
    private static ICredentialStore? s_credentials;
    private static ISftpConnector? s_connector;

    /// <summary>信頼したホスト鍵の表。</summary>
    public static KnownHosts KnownHosts => s_knownHosts ??= new KnownHosts(Path.Combine(App.Settings.Folder, KnownHosts.FileName));

    /// <summary>パスワード・パスフレーズの保存先 (利用者が「保存する」を選んだ場合だけ書く。ENG-41 の仕様 3)。</summary>
    public static ICredentialStore Credentials => s_credentials ??= TestHooks.Active ? new MemoryCredentialStore() : new WindowsCredentialStore();

    /// <summary>SFTP の接続を作る部品。</summary>
    public static ISftpConnector Connector => s_connector ??= new SshNetSftpConnector(TestHooks.Active ? null : new WindowsSshAgent());

    /// <summary>GDB サーバーの 1 つの要求の待ち時間の上限 (設定。ENG-40 の仕様 8)。</summary>
    public static TimeSpan GdbTimeout => TimeSpan.FromSeconds(Math.Clamp(App.Settings.GetInt(RemoteSettings.GdbTimeoutKey, 5), 1, 120));

#if HEX_TEST_HOOKS
    /// <summary>テスト用: SFTP の接続を偽のサーバーにする (null で戻す)。</summary>
    internal static void UseConnector(ISftpConnector? connector) => s_connector = connector;

    /// <summary>テスト用: 信頼したホスト鍵の表を読み直す。</summary>
    internal static void ReloadKnownHosts() => s_knownHosts = null;

    /// <summary>テスト用: メモリの中の保存先 (保存されたキーを確かめる)。</summary>
    internal static MemoryCredentialStore? MemoryCredentials => s_credentials as MemoryCredentialStore;
#endif
}
