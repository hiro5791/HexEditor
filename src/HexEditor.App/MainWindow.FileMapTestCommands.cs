#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Charts;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.FileMap;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>ファイルマップ (ANA-33・ANA-34) のテスト用の命令 ("fileMap")。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleFileMapTestCommandsAsync(string cmd, JsonObject request)
    {
        if (cmd != "fileMap")
        {
            return null;
        }

        string action = request["action"]?.GetValue<string>() ?? "state";
        int region = (int)TestHookSettings.ReadLong(request["region"], FileMapSelected);
        var result = new JsonObject();
        switch (action)
        {
            case "show":
                ShowPanel(FileMapPanelId, focus: false);
                break;
            case "build":
                // 作り終えるまで待つ (rebuild なら作り直す)。
                await BuildFileMapAsync(request["rebuild"]?.GetValue<bool>() ?? false);
                break;
            case "start":
                _ = BuildFileMapAsync(request["rebuild"]?.GetValue<bool>() ?? false);
                break;
            case "cancel":
                CancelFileMap();
                break;
            case "select":
                FileMapSelect(region, fromBand: false);
                break;
            case "selectKind":
                if (CurrentFileMap is { } km && Enum.TryParse(request["kind"]?.GetValue<string>(), true, out FileMapKind kind))
                {
                    int found = Enumerable.Range(0, km.Regions.Count).FirstOrDefault(i => km.Regions[i].Kind == kind, -1);
                    FileMapSelect(found, fromBand: false);
                    result["region"] = found;
                }

                break;
            case "bandClick" or "bandDoubleClick" when _fileMapPanel?.Band is { } band && CurrentFileMap is { } map && region >= 0:
            {
                double x = band.CenterXOf(region);
                double y = FileMapBand.LaneCenterY(map.Regions[region].Lane);
                if (action == "bandClick")
                {
                    band.ClickAt(x, y);
                }
                else
                {
                    band.DoubleClickAt(x, y);
                }

                break;
            }

            case "focusBand" when _fileMapPanel?.Band is { } band:
                band.Focus(FocusState.Keyboard);
                band.SelectAtCursor();
                break;
            case "bandKey" when _fileMapPanel?.Band is { } band:
            {
                string key = request["key"]!.GetValue<string>();
                for (long i = 0; i < TestHookSettings.ReadLong(request["count"], 1); i++)
                {
                    if (key == "ContextMenu")
                    {
                        band.OpenContextMenu();
                    }
                    else
                    {
                        band.HandleKey(Enum.Parse<Windows.System.VirtualKey>(key, ignoreCase: true));
                    }
                }

                break;
            }

            case "zoom" when _fileMapPanel?.Band is { } band:
                band.ZoomBy(request["factor"]?.GetValue<double>() ?? 2);
                break;
            case "scroll" when _fileMapPanel?.Band is { } band:
                band.ScrollTo(TestHookSettings.ReadLong(request["offset"], 0));
                break;
            case "menu":
                result["items"] = new JsonArray([.. FileMapActions(region).Select(a => (JsonNode?)new JsonObject
                {
                    ["id"] = a.Id, ["label"] = a.Label, ["enabled"] = a.Enabled, ["reason"] = a.Reason,
                })]);
                break;
            case "run":
            {
                string id = request["id"]!.GetValue<string>();
                if (FileMapActions(region).FirstOrDefault(a => a.Id == id) is { Enabled: true } a)
                {
                    _ = a.Run();
                    result["ran"] = true;
                }

                break;
            }

            case "filter":
                FilterFileMapRows(Enum.TryParse(request["kind"]?.GetValue<string>(), true, out FileMapKind f) ? f : null);
                break;
            case "sort":
                SortFileMapRows((int)TestHookSettings.ReadLong(request["column"], 0));
                break;
            case "suggestion":
                if (LastFileMapSuggestion is { } notice && request["invoke"] is { } invoke)
                {
                    notice.Actions[(int)TestHookSettings.ReadLong(invoke, 0)].Execute();
                }

                break;
            case "applyTemplates":
                ApplyFileMapTemplates();
                break;
            case "export":
                await ExportFileMapAsync(Enum.Parse<FileMapExportFormat>(request["format"]?.GetValue<string>() ?? "Json", ignoreCase: true));
                break;
            case "highContrast":
                ChartSupport.ForcedHighContrast = request["on"]?.GetValue<bool>() ?? true;
                _fileMapPanel?.Refresh();
                _fileMapPanel?.Band.Redraw();
                break;
            case "installInferProbe":
                // テンプレートの推定 (TPL-38) のコマンドがないビルドで、コマンドの代わりに要求を記録する (FileMapLastInferRequest)。
                FileMapInferProbeEnabled = true;
                break;
        }

        await Task.Yield();
        FillFileMapState(result, request["regions"]?.GetValue<bool>() ?? true);
        return result;
    }


    private void FillFileMapState(JsonObject result, bool regions)
    {
        FileMapState? state = CurrentFileMapState;
        FileMapResult? map = state?.Result;
        result["panelOpen"] = IsPanelShown(FileMapPanelId);
        result["hasMap"] = map is not null;
        result["building"] = state?.IsBuilding ?? false;
        result["overviewMs"] = state?.OverviewElapsed?.TotalMilliseconds;
        result["complete"] = map?.Complete ?? false;
        result["stages"] = map is null ? null : new JsonObject
        {
            ["sample"] = FileMapWords.StageState(map.Stages.Sample),
            ["scan"] = FileMapWords.StageState(map.Stages.Scan),
            ["structure"] = FileMapWords.StageState(map.Stages.Structure),
            ["scanFraction"] = map.Stages.ScanFraction,
        };
        result["regionCount"] = map?.Regions.Count;
        result["blockSize"] = map?.BlockSize;
        result["selected"] = FileMapSelected;
        result["rowCount"] = FileMapRowCount;
        result["lastInfer"] = FileMapLastInferRequest is { } infer ? new JsonObject { ["start"] = infer.Start, ["length"] = infer.Length, ["recordLength"] = infer.RecordLength } : null;
        result["inferAvailable"] = Commands.HasHandler(TemplateInferCommand);
        result["lastFileMapNotice"] = LastFileMapNotice;
        if (_fileMapPanel is { } panel)
        {
            result["status"] = panel.StatusText;
            result["staleShown"] = panel.StaleShown;
            result["rebuildShown"] = panel.RebuildShown;
            result["heading"] = panel.HeadingText;
            result["narrow"] = panel.IsNarrow;
            result["detail"] = panel.DetailText;
            result["detailActions"] = new JsonArray([.. panel.DetailActionIds.Select(i => (JsonNode?)i)]);
            result["bandSelected"] = panel.Band.SelectedRegion;
            result["bandFocused"] = panel.Band.FocusState != FocusState.Unfocused;
            result["zoom"] = panel.Band.Zoom;
            result["viewStart"] = panel.Band.ViewStart;
            result["viewLength"] = panel.Band.ViewLength;
            result["drawCount"] = panel.Band.DrawStats.Count;
            result["lastDrawMs"] = panel.Band.DrawStats.LastMs;
            result["drawn"] = new JsonObject([.. panel.Band.DrawnRects.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))]);
            result["menuOpen"] = panel.LastMenu?.IsOpen ?? false;
            result["menuItems"] = panel.LastMenu is { } menu
                ? new JsonArray([.. menu.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)new JsonObject { ["id"] = AutomationProperties.GetAutomationId(i), ["text"] = i.Text, ["enabled"] = i.IsEnabled })])
                : null;
            result["legend"] = new JsonArray([.. panel.Legend.Children.OfType<FrameworkElement>().Select(e => (JsonNode?)new JsonObject
            {
                ["id"] = AutomationProperties.GetAutomationId(e),
                ["name"] = AutomationProperties.GetName(e),
                ["status"] = AutomationProperties.GetItemStatus(e),
            })]);
        }

        if (LastFileMapSuggestion is { } notice)
        {
            result["suggestion"] = new JsonObject
            {
                ["message"] = notice.Message,
                ["open"] = Vm.Notifications.Open.Contains(notice),
                ["actions"] = new JsonArray([.. notice.Actions.Select(a => (JsonNode?)a.Label)]),
            };
        }

        if (Vm.Selected is { } doc)
        {
            result["templates"] = new JsonArray([.. DocumentTemplates.For(doc).Entries.Select(e => (JsonNode?)new JsonObject
            {
                ["path"] = e.Info.TemplatePath,
                ["start"] = e.Info.StartOffset,
                ["chunk0Type"] = ChunkType(e),
            })]);
            if (CurrentNotice() is { } last)
            {
                result["lastNotice"] = last;
            }
        }

        if (map is not null && regions)
        {
            result["regions"] = new JsonArray([.. Enumerable.Range(0, Math.Min(map.Regions.Count, 5000)).Select(i =>
            {
                FileMapRegion r = map.Regions[i];
                return (JsonNode?)new JsonObject
                {
                    ["index"] = i,
                    ["offset"] = r.Offset,
                    ["length"] = r.Length,
                    ["kind"] = FileMapWords.Kind(r.Kind),
                    ["lane"] = FileMapWords.Lane(r.Lane),
                    ["label"] = FileMapVisuals.Label(map, i),
                    ["confidence"] = FileMapWords.Confidence(r.Confidence),
                    ["stage"] = FileMapWords.Stage(r.Stage),
                    ["parent"] = r.Parent,
                    ["extra"] = r.Extra,
                    ["template"] = r.Detail >= 0 ? map.Details[r.Detail].Template?.Path : null,
                    ["name"] = FileMapVisuals.RegionName(map, i),
                };
            })]);
        }
    }

    /// <summary>最後の通知の文 (テスト用)。</summary>
    private string? CurrentNotice() => Vm.Notifications.Open.LastOrDefault()?.Message;

    /// <summary>PNG のテンプレートの適用の chunks[0].type (評価を待つ)。</summary>
    private static string? ChunkType(TemplateApplicationEntry entry)
    {
        if (entry.Result is not { } result)
        {
            return null;
        }

        try
        {
            result.Completion.Wait(TimeSpan.FromSeconds(10));
            return result.FindByPath("chunks[0].type")?.Value?.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
        {
            return null;
        }
    }
}
#endif
