#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Processes;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、ディスク・プロセスの命令。ダイアログの一覧の選択を UI で動かす代わりに、
/// ダイアログが開く経路と同じ処理 (DeviceService の経路の決定 → 開く) を直接呼ぶ。偽のデバイス・プロセス (fakeDevices /
/// fakeProcesses) を使うため、実機のディスク・プロセスには触れない。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleDeviceTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "enumDevices":
            {
                DeviceCatalog catalog = App.Devices.EnumerateDevices();
                var disks = new JsonArray();
                foreach (DiskInfo d in catalog.Disks)
                {
                    disks.Add(new JsonObject
                    {
                        ["path"] = d.Path,
                        ["number"] = d.Number,
                        ["needsAdmin"] = !App.Devices.IsElevated,
                        ["sectorSize"] = d.LogicalSectorSize,
                    });
                }

                var volumes = new JsonArray();
                foreach (VolumeDeviceInfo v in catalog.Volumes)
                {
                    volumes.Add(new JsonObject
                    {
                        ["path"] = v.Path,
                        ["letter"] = v.DriveLetter,
                        ["removableUsb"] = v.IsRemovableUsb,
                        ["needsAdmin"] = !v.IsRemovableUsb && !App.Devices.IsElevated,
                    });
                }

                return new JsonObject { ["disks"] = disks, ["volumes"] = volumes, ["elevated"] = App.Devices.IsElevated };
            }

            case "openDisk":
            {
                // ダイアログで選んだのと同じ経路 (OpenDeviceItemAsync)。readOnly (既定 true)、range ({start, length, sectors}) を指定できる。
                string path = request["path"]!.GetValue<string>();
                DeviceCatalog catalog = App.Devices.EnumerateDevices();
                VolumeDeviceInfo? volume = catalog.FindVolume(path);
                bool usb = volume?.IsRemovableUsb ?? false;
                OpenRoute route = App.Devices.RouteForDisk(usb);
                if (route == OpenRoute.GuidanceNeeded && request["showGuidance"]?.GetValue<bool>() != true)
                {
                    return new JsonObject { ["route"] = "guidance" };
                }

                DeviceListItem? item = DeviceItems(catalog).FirstOrDefault(i => string.Equals(i.CreateInfo().Path, path, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    return new JsonObject { ["error"] = "not found" };
                }

                bool readOnly = request["readOnly"]?.GetValue<bool>() ?? true;
                DeviceRangeInput? range = request["range"] is JsonObject r
                    ? new DeviceRangeInput(r["start"]?.ToString() ?? "0", r["length"]?.ToString() ?? string.Empty, r["sectors"]?.GetValue<bool>() ?? false)
                    : null;
                DocumentViewModel? vm = await OpenDeviceItemAsync(item, readOnly, range);
                UpdateHelperShield();
                return vm?.Device is not { } source
                    ? new JsonObject { ["route"] = route.ToString(), ["error"] = "not opened", ["guidance"] = LastAdminGuidance }
                    : new JsonObject
                    {
                        ["route"] = source.Route.ToString(), ["length"] = source.Length, ["helper"] = App.Devices.IsHelperRunning,
                        ["readOnly"] = vm.Document.IsReadOnly, ["writable"] = source.Handle.Writable, ["rangeStart"] = source.Info.RangeStart,
                    };
            }

            case "openProcess":
            {
                int pid = (int)TestHookSettings.ReadLong(request["pid"], 0);
                ProcessEntry? entry = App.Devices.EnumerateProcesses().FirstOrDefault(p => p.Pid == pid);
                if (entry is null)
                {
                    return new JsonObject { ["error"] = "not found" };
                }

                OpenRoute route = App.Devices.RouteForProcess(entry.Access);
                if (route is OpenRoute.GuidanceNeeded or OpenRoute.Impossible)
                {
                    return new JsonObject { ["route"] = route.ToString() };
                }

                bool readOnly = request["readOnly"]?.GetValue<bool>() ?? true;
                string? moduleName = request["module"]?.GetValue<string>();
                ProcessOpenScope scope = moduleName is not null ? ProcessOpenScope.Module : ProcessOpenScope.Whole;
                DocumentViewModel? vm = await OpenProcessEntryAsync(entry, readOnly, scope, null,
                    modules => Task.FromResult(modules.FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase))));
                UpdateHelperShield();
                if (vm?.ProcessMemory is not { } source)
                {
                    return new JsonObject { ["route"] = route.ToString(), ["error"] = "not opened" };
                }

                return new JsonObject
                {
                    ["route"] = route.ToString(), ["modules"] = source.Modules.Count, ["readOnly"] = vm.Document.IsReadOnly,
                    ["baseAddress"] = source.BaseAddress, ["length"] = source.Length, ["name"] = vm.DisplayName,
                };
            }

            case "immediateWrite":
            {
                // 即時書き込みモード (ENG-34 の仕様 2)。確認ダイアログを経ずに設定する (確認はダイアログのテストで見る)。
                if (Vm.Selected is { IsProcessMemory: true } doc)
                {
                    doc.ProcessWriteConfirmed = true;
                    SetImmediateWrite(doc, request["on"]?.GetValue<bool>() ?? true);
                    return new JsonObject { ["on"] = doc.ImmediateWrite };
                }

                return new JsonObject { ["error"] = "no process" };
            }

            case "memoryMapPanel":
            {
                // メモリマップのパネルの一覧 (表示している行の文字列) と、右クリックメニューの操作。
                if (FindPanelContent<Panels.MemoryMapPanel>("memoryMap") is not { } panel)
                {
                    return new JsonObject { ["error"] = "no panel" };
                }

                if (request["select"] is { } select && !panel.SelectRowAt(TestHookSettings.ReadLong(select, 0)))
                {
                    return new JsonObject { ["error"] = "no row" };
                }

                switch (request["action"]?.GetValue<string>())
                {
                    case "selectRegion":
                        panel.SelectRegion();
                        break;
                    case "openInNewTab":
                        panel.OpenRegionInNewTab();
                        break;
                }

                return new JsonObject
                {
                    ["rows"] = new JsonArray([.. panel.RegionTexts.Select(t => (JsonNode?)t)]),
                    ["selection"] = Vm.Selected is { } d ? new JsonArray(d.Editor.SelectionStart, d.Editor.SelectionLength) : null,
                    ["tab"] = Vm.Selected?.DisplayName,
                    ["position"] = Vm.Selected?.PositionText,
                };
            }

            case "processWrite":
            {
                // 偽のプロセスのメモリを書き換える (TestTarget に値を書き換えさせる代わり)。アプリの時計の時刻を返す。
                int pid = (int)TestHookSettings.ReadLong(request["pid"], 0);
                byte[] data = Convert.FromHexString(request["hex"]!.GetValue<string>());
                if (TestHooks.FakeProcesses?.Direct is FakeProcessAccess direct)
                {
                    direct.Process(pid).WriteRaw(TestHookSettings.ReadLong(request["address"], 0), data);
                }

                return new JsonObject { ["at"] = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency };
            }

            case "processRead":
            {
                // 偽のプロセスのメモリの今の内容 (書き込みが届いたかを確かめる)。
                int pid = (int)TestHookSettings.ReadLong(request["pid"], 0);
                int length = (int)TestHookSettings.ReadLong(request["length"], 1);
                if (TestHooks.FakeProcesses?.Direct is FakeProcessAccess direct)
                {
                    return new JsonObject { ["hex"] = Convert.ToHexString(direct.Process(pid).ReadRaw(TestHookSettings.ReadLong(request["address"], 0), length)) };
                }

                return new JsonObject { ["error"] = "no fake processes" };
            }

            case "autoRefresh":
            {
                // タブの右クリックメニューの「自動更新」と同じ設定。ms を省くと状態 (再読み込みした時刻) だけを返す。
                if (Vm.Selected is { } doc && request["ms"] is { } ms)
                {
                    long value = TestHookSettings.ReadLong(ms, 0);
                    SetAutoRefresh(doc, value > 0 ? TimeSpan.FromMilliseconds(value) : null);
                }

                return new JsonObject
                {
                    ["interval"] = Vm.Selected?.AutoRefreshInterval?.TotalMilliseconds,
                    ["refreshes"] = new JsonArray([.. AutoRefreshTimes.Select(t => (JsonNode?)t)]),
                };
            }

            case "memoryMap":
            {
                if (Vm.Selected?.ProcessMemory is not { } mem)
                {
                    return new JsonObject { ["error"] = "no process" };
                }

                var regions = new JsonArray();
                foreach (Core.Sources.SourceRegion r in mem.Regions)
                {
                    regions.Add(new JsonObject { ["offset"] = r.Offset, ["length"] = r.Length, ["access"] = r.Access.ToString(), ["label"] = r.Label });
                }

                return new JsonObject
                {
                    ["regions"] = regions,
                    ["modules"] = new JsonArray([.. mem.Modules.Select(m => (JsonNode?)m.Name)]),
                    ["panelVisible"] = IsPanelShown("memoryMap"),
                };
            }

            default:
                return null;
        }
    }
}
#endif
