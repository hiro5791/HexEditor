#if HEX_TEST_HOOKS
using System.Net;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Processes;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Sftp;
using HexEditor.Core.Remote.Udp;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令のうち、リモート (ENG-40〜ENG-42) とスナップショットの保存 (ENG-35) の命令。接続ダイアログの入力の代わりに、ダイアログが呼ぶのと
/// 同じ処理を直接呼ぶ。SFTP は偽のサーバー (<see cref="FakeSftpServer"/>) だけを使い、GDB・UDP はテストのプロセスが 127.0.0.1 に用意した相手だけに
/// 接続する (外部のホストには接続しない)。
/// </summary>
public sealed partial class MainWindow
{
    private FakeSftpServer? _fakeSftp;
    private Task<string?>? _sftpTask;

    private async Task<JsonObject?> HandleRemoteTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "gdbConnect":
            {
                var endpoint = new GdbEndpoint
                {
                    Host = request["host"]?.GetValue<string>() ?? "127.0.0.1",
                    Port = request["port"]!.GetValue<int>(),
                    AddressBits = request["bits"]?.GetValue<int>() ?? 64,
                    Timeout = TimeSpan.FromSeconds(request["timeoutSeconds"]?.GetValue<double>() ?? RemoteServices.GdbTimeout.TotalSeconds),
                };
                DocumentViewModel? doc = await ConnectGdbCoreAsync(endpoint, request["readOnly"]?.GetValue<bool>() ?? true);
                return new JsonObject { ["opened"] = doc is not null, ["error"] = LastGdbConnectError, ["name"] = doc?.DisplayName };
            }

            case "remoteState":
            {
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                return new JsonObject
                {
                    ["state"] = doc.RemoteState?.ToString(),
                    ["status"] = doc.RemoteStatusText,
                    ["location"] = doc.RemoteLocation,
                    ["readOnly"] = doc.Document.IsReadOnly,
                    ["modified"] = doc.Document.IsModified,
                    ["length"] = doc.Document.Length,
                    ["disconnectNotice"] = LastDisconnectNotice,
                    ["missingRemote"] = doc.IsMissingRemote,
                    ["toolTip"] = doc.ToolTip,
                };
            }

            case "remoteReconnect":
                await ReconnectRemoteAsync(Vm.Selected ?? throw new InvalidOperationException("No document."));
                return new JsonObject { ["state"] = Vm.Selected?.RemoteState?.ToString() };

            case "remoteConnectRestored":
                await ConnectRestoredRemoteAsync(Vm.Selected ?? throw new InvalidOperationException("No document."));
                return new JsonObject();

            case "fakeSftp":
            {
                // 偽の SFTP サーバーを使う (spec: FakeSftpSpec の JSON)。信頼したホスト鍵の表は読み直す。
                _fakeSftp = FakeSftpServer.FromJson(request["spec"]!.ToJsonString());
                RemoteServices.UseConnector(_fakeSftp);
                RemoteServices.ReloadKnownHosts();
                return new JsonObject();
            }

            case "fakeSftpHostKey":
                NeedFake(_fakeSftp).HostKey = new HostKeyInfo(request["algorithm"]!.GetValue<string>(), request["fingerprint"]!.GetValue<string>());
                return new JsonObject();

            case "fakeSftpState":
            {
                FakeSftpServer server = NeedFake(_fakeSftp);
                return new JsonObject
                {
                    ["sessions"] = server.SessionsCreated,
                    ["handshakes"] = server.HandshakeCount,
                    ["bytesRead"] = server.BytesRead,
                    ["log"] = new JsonArray([.. server.Log.Select(l => (JsonNode?)l)]),
                    ["savedCredentials"] = new JsonArray([.. (RemoteServices.MemoryCredentials?.Keys ?? []).Select(k => (JsonNode?)k)]),
                };
            }

            case "sftpConnect":
            {
                // 接続ダイアログで入力して「接続」を押したのと同じ処理。ホスト鍵の確認ダイアログを待つため、終わりを待たずに返す (sftpResult で確かめる)。
                var endpoint = new SftpEndpoint
                {
                    Host = request["host"]?.GetValue<string>() ?? "localhost",
                    Port = request["port"]?.GetValue<int>() ?? 22,
                    User = request["user"]!.GetValue<string>(),
                };
                var credentials = new SftpCredentials { Method = SftpAuthMethod.Password, Password = request["password"]?.GetValue<string>() };
                string path = request["path"]!.GetValue<string>();
                bool save = request["save"]?.GetValue<bool>() ?? false;
                _sftpTask = ConnectAndOpenSftpForTestAsync(endpoint, credentials, path, save);
                return new JsonObject { ["started"] = true };
            }

            case "sftpResult":
                return new JsonObject
                {
                    ["done"] = _sftpTask?.IsCompleted ?? false,
                    ["result"] = _sftpTask is { IsCompletedSuccessfully: true } t ? t.Result : null,
                    ["error"] = LastSftpError,
                };

            case "hostKeyDialog":
                return LastHostKeyDialog is { } d
                    ? new JsonObject { ["title"] = d.Title, ["message"] = d.Message, ["defaultButton"] = d.DefaultButton }
                    : new JsonObject();

            case "udpStart":
            {
                DocumentViewModel? doc = StartUdpCore(IPAddress.Parse(request["address"]?.GetValue<string>() ?? "127.0.0.1"),
                    request["port"]!.GetValue<int>(), request["limit"]?.GetValue<long>() ?? UdpCaptureSettings.DefaultLimit);
                return new JsonObject { ["opened"] = doc is not null, ["error"] = LastUdpError, ["endpoint"] = doc?.Udp?.Capture.LocalEndPoint.ToString() };
            }

            case "udpState":
            {
                DocumentViewModel doc = Vm.Selected is { Udp: not null } selected ? selected : throw new InvalidOperationException("No UDP document.");
                if (request["flush"]?.GetValue<bool>() == true)
                {
                    UpdateUdpDocument(doc);
                }

                UdpCapture capture = doc.Udp!.Capture;
                return new JsonObject
                {
                    ["length"] = doc.Document.Length,
                    ["baseAddress"] = doc.Udp.BaseAddress,
                    ["received"] = capture.ReceivedCount,
                    ["total"] = capture.Store.End,
                    ["state"] = capture.State.ToString(),
                    ["readOnly"] = doc.Document.IsReadOnly,
                    ["status"] = doc.RemoteStatusText,
                    ["limitNotice"] = LastUdpLimitNotice,
                    ["packetCount"] = capture.PacketCount,
                    ["packets"] = new JsonArray([.. capture.Packets.Take(request["maxPackets"]?.GetValue<int>() ?? 20).Select(p => (JsonNode?)new JsonObject
                    {
                        ["position"] = p.Position,
                        ["length"] = p.Length,
                        ["source"] = p.Source.ToString(),
                    })]),
                    ["panelRows"] = FindPanelContent<Panels.PacketsPanel>(PacketsPanelId)?.RowCount,
                    ["panelVisible"] = IsPanelShown(PacketsPanelId),
                };
            }

            case "processCapture":
            {
                // 保存のダイアログで選んで「保存」を押したのと同じ処理 (スナップショット・ミニダンプ)。
                DocumentViewModel doc = Vm.Selected is { ProcessMemory: not null } selected ? selected : throw new InvalidOperationException("No process.");
                var options = new ProcessCaptureOptions
                {
                    Scope = Enum.Parse<SnapshotScope>(request["scope"]?.GetValue<string>() ?? nameof(SnapshotScope.AllReadable)),
                    PauseTarget = request["pause"]?.GetValue<bool>() ?? false,
                };
                bool ok = await SaveProcessCaptureCoreAsync(doc, doc.ProcessMemory!, request["path"]!.GetValue<string>(), options,
                    request["minidump"]?.GetValue<bool>() ?? false);
                return new JsonObject
                {
                    ["ok"] = ok,
                    ["unreadableRegions"] = LastCaptureSummary?.UnreadableRegions,
                    ["pauseFailed"] = LastCaptureSummary?.PauseFailed,
                };
            }

            default:
                return null;
        }
    }

    private static T NeedFake<T>(T? value) where T : class => value ?? throw new InvalidOperationException($"{typeof(T).Name} is not set.");

    private async Task<string?> ConnectAndOpenSftpForTestAsync(SftpEndpoint endpoint, SftpCredentials credentials, string path, bool save)
    {
        (SftpConnection? connection, string? error) = await ConnectSftpAsync(endpoint, credentials);
        if (connection is null)
        {
            return error is null ? "cancelled" : "error";
        }

        SftpLogin.RememberSecret(RemoteServices.Credentials, endpoint, credentials, save);
        return await OpenSftpFileAsync(connection, path) is { } doc ? doc.DisplayName : "error";
    }
}
#endif
