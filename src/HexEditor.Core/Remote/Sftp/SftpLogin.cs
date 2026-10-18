namespace HexEditor.Core.Remote.Sftp;

/// <summary>パスワード・パスフレーズの保存の規則 (ENG-41 の仕様 3)。</summary>
public static class SftpLogin
{
    /// <summary>
    /// 接続できた後に呼ぶ。<paramref name="save"/> (「保存する」) なら秘密 (パスワード、または鍵ファイルのパスフレーズ) を保存先に書き、
    /// そうでなければ以前に保存したものを消す。エージェントは秘密を持たないので何もしない。
    /// </summary>
    public static void RememberSecret(ICredentialStore store, SftpEndpoint endpoint, SftpCredentials credentials, bool save)
    {
        string? secret = credentials.Method switch
        {
            SftpAuthMethod.Password => credentials.Password,
            SftpAuthMethod.KeyFile => credentials.Passphrase,
            _ => null,
        };
        if (credentials.Method == SftpAuthMethod.Agent)
        {
            return;
        }

        if (save && !string.IsNullOrEmpty(secret))
        {
            store.Write(endpoint.Key, secret);
        }
        else
        {
            store.Delete(endpoint.Key);
        }
    }

    /// <summary>保存した秘密 (接続ダイアログの初期値。なければ null)。</summary>
    public static string? SavedSecret(ICredentialStore store, SftpEndpoint endpoint) => store.Read(endpoint.Key);
}
