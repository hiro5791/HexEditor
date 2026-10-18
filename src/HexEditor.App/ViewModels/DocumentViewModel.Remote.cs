using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Remote;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Remote.Udp;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>リモートのデータソース (GDB サーバー・SFTP・UDP の受信。ENG-40〜ENG-42) のドキュメントの判別と状態。</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>GDB サーバーの対象のメモリ (ENG-40)。</summary>
    public GdbByteSource? Gdb => Document.Source as GdbByteSource;

    /// <summary>SFTP のリモートのファイル (ENG-41)。</summary>
    public SftpByteSource? Sftp => Document.Source as SftpByteSource;

    /// <summary>UDP の受信 (ENG-42)。</summary>
    public UdpByteSource? Udp => Document.Source as UdpByteSource;

    /// <summary>接続を持つリモートのドキュメント (GDB・SFTP)。</summary>
    public bool IsRemote => Gdb is not null || Sftp is not null;

    /// <summary>GDB の書き込みの確認を 1 回出したか (ENG-34 と同じ。2 回目以降は確認しない)。</summary>
    public bool RemoteWriteConfirmed { get; set; }

    /// <summary>リモートの接続の状態 (切断なら「再接続」を出す)。リモートでなければ null。</summary>
    public RemoteConnectionState? RemoteState => Gdb?.State ?? Sftp?.Connection.State;

    /// <summary>タブのツールチップ・セッションに出すリモートの場所 (<c>sftp://…</c>、<c>GDB localhost:1234</c>)。</summary>
    public string? RemoteLocation => Sftp?.Uri.ToString() ?? (Gdb is { } gdb ? $"GDB {gdb.Endpoint.Display}" : null)
        ?? (Udp is { } udp ? $"UDP {udp.Capture.LocalEndPoint}" : null);

    /// <summary>
    /// ステータスバーの接続先と接続状態 (ENG-40・ENG-41 の「画面」)、UDP の受信数と受信量 (ENG-42 の「画面」)。該当しなければ null。
    /// </summary>
    public string? RemoteStatusText
    {
        get
        {
            if (Gdb is { } gdb)
            {
                return Loc.Format("Remote_StatusGdb", gdb.Endpoint.Display, StateText(gdb.State));
            }

            if (Sftp is { } sftp)
            {
                return Loc.Format("Remote_StatusSftp", sftp.Connection.Endpoint.Display, StateText(sftp.Connection.State));
            }

            if (Udp is { } udp)
            {
                CultureInfo culture = CultureInfo.CurrentCulture;
                string size = StatusFormat.ShortSize(udp.Capture.Store.End, culture) ?? StatusFormat.Number(udp.Capture.Store.End, culture);
                string state = udp.Capture.State switch
                {
                    UdpCaptureState.Paused => Loc.Get("Udp_StatePaused"),
                    UdpCaptureState.LimitReached => Loc.Get("Udp_StateLimit"),
                    UdpCaptureState.Stopped => Loc.Get("Udp_StateStopped"),
                    _ => Loc.Get("Udp_StateReceiving"),
                };
                return Loc.Format("Remote_StatusUdp", udp.Capture.LocalEndPoint.ToString(), udp.Capture.ReceivedCount, size, state);
            }

            return null;
        }
    }

    private static string StateText(RemoteConnectionState state) => state switch
    {
        RemoteConnectionState.Connected => Loc.Get("Remote_StateConnected"),
        RemoteConnectionState.Connecting => Loc.Get("Remote_StateConnecting"),
        _ => Loc.Get("Remote_StateDisconnected"),
    };
}
