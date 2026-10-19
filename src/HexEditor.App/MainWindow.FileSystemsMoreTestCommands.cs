#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems.Volumes;

namespace HexEditor.App;

/// <summary>テスト用の命令の通り道のうち、論理ボリューム (FOR-07) の命令。パネルの操作は UI と同じ処理を呼ぶ。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleFileSystemMoreTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "fsLogicalVolumes":
            {
                // action: show (論理ボリュームを表示)、open (名前のボリュームを開く)、なし (今の一覧)。
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                string? action = request["action"]?.GetValue<string>();
                if (action == "show" || request["via"]?.GetValue<string>() == "command")
                {
                    await Commands.ExecuteAsync("forensics.partitions.logicalVolumes");
                    await WaitUntilTestAsync(() => StateOf(d)?.LogicalVolumesRunning == false && StateOf(d)?.LogicalVolumes is not null, 30_000);
                }

                PartitionPanel panel = await WaitPanelAsync<PartitionPanel>(PartitionsPanelId);
                await DispatcherIdleAsync();

                // パネルが今のドキュメントの一覧を出すまで待つ (ドキュメントの切り替えと状態の変化は UI スレッドに積まれる)。
                await WaitUntilTestAsync(() => StateOf(d)?.LogicalVolumes is not { } s
                    || panel.LogicalVolumes.Select(v => v.Name).SequenceEqual(s.Volumes.Select(v => v.Name)) && panel.LogicalVolumes.All(v => s.Volumes.Contains(v)));
                DocumentViewModel? opened = null;
                if (action == "open")
                {
                    if (!panel.SelectLogicalVolume(request["name"]!.GetValue<string>()))
                    {
                        return new JsonObject { ["error"] = "volume not found" };
                    }

                    opened = await panel.OpenSelectedLogicalVolumeAsync();
                    await DispatcherIdleAsync();
                }

                LogicalVolumeScan? scan = StateOf(d)?.LogicalVolumes;
                return new JsonObject
                {
                    ["volumes"] = new JsonArray([.. (scan?.Volumes ?? []).Select(v => (JsonNode?)new JsonObject
                    {
                        ["name"] = v.Name,
                        ["group"] = v.Group,
                        ["kind"] = v.Kind.ToString(),
                        ["kindText"] = LvKindText(v),
                        ["size"] = v.Size,
                        ["layout"] = v.Layout,
                        ["state"] = LvStateText(v, scan!),
                        ["members"] = v.Members.Distinct().Count(),
                        ["missing"] = new JsonArray([.. scan!.MissingMembers(v).Select(m => (JsonNode?)m)]),
                    })]),
                    ["rows"] = new JsonArray([.. panel.LogicalVolumeTexts.Select(t => (JsonNode?)t)]),
                    ["status"] = panel.LogicalVolumeStatus,
                    ["message"] = StateOf(d)?.LogicalVolumesMessage,
                    ["documents"] = Vm.Documents.Count,
                    ["opened"] = opened is null ? null : TabResult(opened),
                };
            }

            default:
                return null;
        }
    }
}
#endif
