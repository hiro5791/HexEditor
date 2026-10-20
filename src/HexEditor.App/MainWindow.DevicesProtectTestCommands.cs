#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices.Info;
using HexEditor.Core.Forensics.Encryption;
using HexEditor.Core.Sources;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、書き込み保護・デバイス情報・暗号化・メモリダンプの命令 (12 の FOR-01、FOR-02、FOR-27〜29、01 の ENG-36)。
/// ダイアログは UI オートメーションで操作し、ここでは状態の読み出しと、ファイルの選択ダイアログを介さない実行だけを持つ。
/// <list type="bullet">
/// <item><c>tcWriteBlock</c>: {action: state / enable / commandState (id)}。</item>
/// <item><c>tcUsbProtect</c>: {action: state}。</item>
/// <item><c>tcDeviceInfo</c>: {action: run (hpa, smart) / state}。</item>
/// <item><c>tcEncryption</c>: {action: scan}。</item>
/// <item><c>tcMemDump</c>: {action: open (path) / read (offset, length) / state}。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleDevicesProtectTestCommandsAsync(string cmd, JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        switch (cmd)
        {
            case "tcWriteBlock":
                return TestWriteBlock(action, request);
            case "tcUsbProtect":
                return new JsonObject
                {
                    ["on"] = UsbWriteProtectIsOn(),
                    ["value"] = App.Devices.UsbWriteProtectReader.Read() is { } v ? v : null,
                    ["checked"] = Commands.StateOf(UsbWriteProtectCommandId).Checked,
                    ["original"] = AppState.GetString(UsbOriginalStateKey, string.Empty),
                };
            case "tcDeviceInfo":
                if (action == "run")
                {
                    await RunDeviceInfoAsync(request["hpa"]?.GetValue<bool>() ?? true, request["smart"]?.GetValue<bool>() ?? false, show: true);
                }

                return TestDeviceInfoState();
            case "tcEncryption":
            {
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                IReadOnlyList<EncryptionScanEntry> scan = await ScanEncryptionAsync(doc);
                return new JsonObject
                {
                    ["entries"] = new JsonArray([.. scan.Select(e => (JsonNode?)new JsonObject
                    {
                        ["index"] = e.Index,
                        ["offset"] = e.Offset,
                        ["length"] = e.Length,
                        ["kind"] = e.Encryption?.Kind.ToString(),
                        ["blocksBrowser"] = e.Encryption?.BlocksFileSystem,
                        ["method"] = e.Encryption?.BitLocker?.MethodName ?? e.Encryption?.Luks?.Cipher,
                        ["protectors"] = new JsonArray([.. (e.Encryption?.BitLocker?.Protectors ?? []).Select(p => (JsonNode?)p.TypeName)]),
                        ["uuid"] = e.Encryption?.Luks?.Uuid,
                        ["keySlots"] = e.Encryption?.Luks?.KeySlots,
                        ["lines"] = e.Encryption is null ? new JsonArray() : new JsonArray([.. EncryptionLines(e).Select(l => (JsonNode?)l)]),
                    })]),
                };
            }

            case "tcMemDump":
                return await TestMemoryDumpAsync(action, request);
            case "tcHelperProbe":
                return await TestHelperWriteBlockProbeAsync(request);
            default:
                return null;
        }
    }

    /// <summary>
    /// TC-FOR-01-05 (CI の管理者のランナーだけ): 本物の補助プロセスを管理者のアプリから直接起動し (UAC は出ない)、<c>SetWriteBlock</c> の後に
    /// 読み取り・書き込みの要求を直接送って、それぞれの状態を返す。CI の管理者のランナー (環境変数) でなければ何もせずに例外にする
    /// (開発者の PC の実機のディスクに触れない)。
    /// </summary>
    private async Task<JsonObject> TestHelperWriteBlockProbeAsync(JsonObject request)
    {
        if (!TestHooks.IsCiPrivilegedRunner || !App.Devices.IsElevated)
        {
            throw new InvalidOperationException("tcHelperProbe runs only on the elevated CI runner.");
        }

        string disk = request["disk"]!.GetValue<string>();
        string volume = request["volume"]!.GetValue<string>();
        await using var session = new Core.Elevation.HelperSession(new Core.Elevation.HelperSessionOptions
        {
            HelperPath = Path.Combine(AppContext.BaseDirectory, "HexEditor.Elevated.exe"),
            Launcher = new Core.Elevation.DirectHelperLauncher(),
            AppVersion = Hosting.Program.Environment.AppVersion,
            ExtraArguments = ["--app-version", Hosting.Program.Environment.AppVersion],
            IdleMinutes = 0,
            RequestTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(60),
        });
        Core.Elevation.HelperClient client = await session.ConnectAsync();
        uint Status((uint Status, byte[] Body) r) => r.Status;
        var result = new JsonObject
        {
            ["setWriteBlock"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.SetWriteBlock, 0, new byte[] { 1 })),
        };

        // 1. 読み取りのみの OpenDevice と ReadSectors (LBA 0)。
        (uint openStatus, byte[] openBody) = await client.SendAsync(Core.Elevation.HelperCommand.OpenDevice, 0, new Core.Elevation.BodyWriter().Text(disk).ToArray());
        result["openRead"] = openStatus;
        uint handle = openStatus == 0 ? new Core.Elevation.BodyReader(openBody).U32() : 0;
        result["readSectors"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.ReadSectors, 0, new Core.Elevation.BodyWriter().U32(handle).I64(0).U32(512).ToArray()));

        // 2. 読み書きの OpenDevice。3. WriteSectors (LBA 100、FF × 512)。
        result["openWrite"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.OpenDevice, Core.Elevation.HelperProtocol.FlagWritable,
            new Core.Elevation.BodyWriter().Text(disk).ToArray()));
        result["writeSectors"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.WriteSectors, 0,
            new Core.Elevation.BodyWriter().U32(handle).I64(100 * 512).Bytes(Enumerable.Repeat((byte)0xFF, 512).ToArray()).ToArray()));

        // 4. ボリュームの LockVolume・DismountVolume。
        (uint volumeStatus, byte[] volumeBody) = await client.SendAsync(Core.Elevation.HelperCommand.OpenDevice, 0, new Core.Elevation.BodyWriter().Text(volume).ToArray());
        result["openVolume"] = volumeStatus;
        uint volumeHandle = volumeStatus == 0 ? new Core.Elevation.BodyReader(volumeBody).U32() : 0;
        result["lock"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.LockVolume, 0, new Core.Elevation.BodyWriter().U32(volumeHandle).ToArray()));
        result["dismount"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.DismountVolume, 0, new Core.Elevation.BodyWriter().U32(volumeHandle).ToArray()));

        // 5. GetUsbWriteProtect (読み取り。値は変えない)。
        result["getUsb"] = Status(await client.SendAsync(Core.Elevation.HelperCommand.GetUsbWriteProtect, 0, ReadOnlyMemory<byte>.Empty));
        return result;
    }

    private JsonObject TestWriteBlock(string action, JsonObject request)
    {
        switch (action)
        {
            case "enable":
                EnableWriteBlockFromMenu();
                break;
            case "commandState":
            {
                string id = request["id"]!.GetValue<string>();
                Commands.CommandState state = Commands.StateOf(id);
                return new JsonObject { ["enabled"] = state.Enabled, ["reason"] = state.Reason, ["checked"] = state.Checked };
            }
        }

        return new JsonObject
        {
            ["enabled"] = WriteBlockMode.IsEnabled,
            ["title"] = Title,
            ["statusVisible"] = StatusWriteBlock.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
            ["statusText"] = StatusWriteBlockText.Text,
            ["statusIndex"] = StatusItems.Children.IndexOf(StatusWriteBlock),
            ["firstVisibleStatus"] = StatusItems.Children.OfType<Microsoft.UI.Xaml.Controls.Button>()
                .FirstOrDefault(b => b.Visibility == Microsoft.UI.Xaml.Visibility.Visible)?.Tag as string,
            ["checked"] = Commands.StateOf(WriteBlockCommandId).Checked,
            ["exceptions"] = new JsonArray([.. WriteBlockMode.Policy.Exceptions.Select(e => (JsonNode?)e)]),
            ["notices"] = new JsonArray([.. Vm.Notifications.History.Select(n => (JsonNode?)n.Message)]),
            ["tabs"] = new JsonArray([.. Vm.Documents.Select(d => (JsonNode?)new JsonObject
            {
                ["name"] = d.DisplayName,
                ["header"] = d.Header,
                ["visibleHeader"] = VisibleTabHeader(d.Header, d.Document.IsModified),
                ["readOnly"] = d.Document.IsReadOnly,
                ["reason"] = d.Document.ReadOnlyReason.ToString(),
                ["release"] = d.Document.ReadOnlyRelease.ToString(),
                ["exception"] = d.WriteBlockException,
                ["modified"] = d.Document.IsModified,
                ["allowEnabled"] = AllowWritingState(d).Enabled,
                ["evidence"] = IsEvidenceDocument(d),

                // データソースのハンドルのアクセス権 (TC-FOR-01-03 の手順 2): ファイルは常に読み取りのアクセス権で開く (ENG-15)。
                ["access"] = d.Device is { } device ? (device.Handle.Writable ? "read-write" : "read")
                    : d.ProcessMemory is { } process ? (process.Memory.Writable ? "read-write" : "read")
                    : d.Document.Source is FileByteSource ? "read" : (d.Document.Source.Capabilities.HasFlag(SourceCapabilities.CanWrite) ? "read-write" : "read"),
            })]),
        };
    }

    private JsonObject TestDeviceInfoState()
    {
        var state = new JsonObject
        {
            ["hasReport"] = LastDeviceInfo is not null,
            ["json"] = LastDeviceInfo?.ToJson(),
            ["hiddenArea"] = LastDeviceInfo?.HpaDco?.HasHiddenArea,
            ["hpaAvailability"] = LastDeviceInfo?.HpaDco?.Availability.ToString(),
            ["smartAvailability"] = LastDeviceInfo?.Smart?.Availability.ToString(),
            ["notices"] = new JsonArray([.. Vm.Notifications.History.Select(n => (JsonNode?)n.Message)]),
        };
        if (_deviceInfoPanel is { } panel)
        {
            state["panelTab"] = panel.SelectedTab;
            state["header"] = panel.HeaderText;
            state["hpaLines"] = new JsonArray([.. panel.HpaLines.Select(l => (JsonNode?)l)]);
            state["identityLines"] = new JsonArray([.. panel.IdentityLines.Select(l => (JsonNode?)l)]);
            state["smartLines"] = new JsonArray([.. panel.SmartLines.Select(l => (JsonNode?)l)]);
            state["smartColumns"] = new JsonArray([.. panel.SmartColumns.Select(l => (JsonNode?)l)]);
            state["smartRows"] = new JsonArray([.. panel.SmartRows.Select(r => (JsonNode?)new JsonArray([.. r.Select(c => (JsonNode?)c)]))]);
        }

        return state;
    }

    private async Task<JsonObject> TestMemoryDumpAsync(string action, JsonObject request)
    {
        if (action == "open")
        {
            await OpenMemoryDumpAsync(request["path"]!.GetValue<string>());
        }

        DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
        var state = new JsonObject
        {
            ["name"] = doc.DisplayName,
            ["kind"] = doc.MemoryDump?.Kind.ToString(),
            ["captured"] = doc.MemoryDump?.CapturedUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["toolTip"] = doc.ToolTip,
            ["readOnly"] = doc.Document.IsReadOnly,
            ["reason"] = doc.Document.ReadOnlyReason.ToString(),
            ["release"] = doc.Document.ReadOnlyRelease.ToString(),
            ["length"] = doc.Document.Length,
            ["modules"] = new JsonArray([.. (doc.Snapshot?.Modules ?? []).Select(m => (JsonNode?)m.Name)]),
        };
        if (action == "read")
        {
            long offset = TestHookSettings.ReadLong(request["offset"], 0);
            int length = (int)TestHookSettings.ReadLong(request["length"], 16);
            byte[] buffer = new byte[length];
            ReadResult read = doc.Document.DataSnapshot.Read(offset, buffer);
            state["hex"] = Convert.ToHexString(buffer, 0, read.BytesReturned);
            state["unreadable"] = new JsonArray([.. read.Unreadable.Select(u => (JsonNode?)new JsonObject
            {
                ["offset"] = u.Offset,
                ["length"] = u.Length,
                ["reason"] = u.Reason.ToString(),
            })]);
        }

        return state;
    }
}
#endif
