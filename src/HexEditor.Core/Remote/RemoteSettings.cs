namespace HexEditor.Core.Remote;

/// <summary>リモートのデータソースの設定のキー (ENG-40〜ENG-42)。</summary>
public static class RemoteSettings
{
    /// <summary>GDB サーバーの 1 つの要求の待ち時間の上限 (秒。既定 5。ENG-40 の仕様 8)。</summary>
    public const string GdbTimeoutKey = "remote.gdb.timeoutSeconds";

    /// <summary>SFTP の変更範囲だけの保存の前に、途中で切れたときの注意を確認する (既定オン。ENG-41 の仕様 5)。</summary>
    public const string SftpConfirmPartialWriteKey = "remote.sftp.confirmPartialWrite";

    /// <summary>UDP の受信で、カーソルが末尾にあれば末尾に追従してスクロールする (既定オン。ENG-42 の仕様 3)。</summary>
    public const string UdpFollowEndKey = "remote.udp.followEnd";

    /// <summary>外部変更の確認の間隔 (ENG-41 の仕様 6)。</summary>
    public static readonly TimeSpan SftpExternalCheckInterval = TimeSpan.FromSeconds(30);
}
