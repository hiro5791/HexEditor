namespace HexEditor.Core.Remote;

/// <summary>接続先の状態 (ENG-40 の仕様 8、ENG-41 の仕様 7)。</summary>
public enum RemoteConnectionState
{
    Connected,
    Connecting,
    Disconnected,
}
