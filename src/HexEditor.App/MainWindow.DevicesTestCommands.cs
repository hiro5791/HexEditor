#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
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
                string path = request["path"]!.GetValue<string>();
                DeviceCatalog catalog = App.Devices.EnumerateDevices();
                DiskInfo? disk = catalog.FindDisk(path);
                VolumeDeviceInfo? volume = catalog.FindVolume(path);
                bool usb = volume?.IsRemovableUsb ?? false;
                OpenRoute route = App.Devices.RouteForDisk(usb);
                if (route == OpenRoute.GuidanceNeeded)
                {
                    return new JsonObject { ["route"] = "guidance" };
                }

                var info = new DeviceOpenInfo
                {
                    Path = path,
                    DisplayName = disk is not null ? DiskDisplayName(disk) : volume is not null ? VolumeDisplayName(volume) : path,
                    SerialNumber = disk?.SerialNumber,
                    Disk = disk,
                    Volume = volume,
                };
                DeviceByteSource source = await App.Devices.OpenDeviceAsync(info, writable: false, route);
                Vm.OpenDevice(source);
                UpdateHelperShield();
                return new JsonObject { ["route"] = route.ToString(), ["length"] = source.Length, ["helper"] = App.Devices.IsHelperRunning };
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

                var info = new ProcessOpenInfo { DisplayName = ProcessDisplayName(entry) };
                ProcessMemoryByteSource source = await App.Devices.OpenProcessAsync(pid, info, writable: false, route);
                Vm.OpenProcess(source, readOnly: true);
                ShowMemoryMapPanel();
                UpdateHelperShield();
                return new JsonObject { ["route"] = route.ToString(), ["modules"] = source.Modules.Count };
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
