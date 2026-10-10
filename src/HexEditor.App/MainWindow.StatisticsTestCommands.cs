#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls.Charts;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Statistics;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>統計パネルとファイル形式パネルのテスト用の命令 ("stats"、"fileType")。</summary>
public sealed partial class MainWindow
{
    /// <summary>「この長さでレコード表示」の要求の記録 (レコード表示 (VIEW-18) がつながっていないテスト用のビルドで使う)。</summary>
    private readonly List<long> _recordViewRequests = [];

    /// <summary>
    /// "stats": {action: show / hide / state / compute / set / graphClick / graphDrag / zoomIn / flushDetail / digramHover / digramInvoke /
    /// positionClick / patterns / periodMenu / classify / classMenu / recordProbe / table}。
    /// </summary>
    private async Task<JsonObject> TestStatisticsAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        var result = new JsonObject();
        switch (action)
        {
            case "show":
                ShowStatistics(Enum.TryParse(request["tab"]?.GetValue<string>(), out StatsTab tab) ? tab : StatsTab.Histogram);
                _statsPanel?.UpdateLayout();
                break;
            case "hide":
                HidePanel(StatisticsPanelId);
                break;
            case "compute":
                await StatsVm.ComputeAsync();
                break;
            case "set":
                ApplyStatisticsSettings(request);
                break;
            case "graphClick" when _statsPanel?.EntropyChartControl is { } chart:
                chart.Release(chart.XOf(request["fraction"]!.GetValue<double>()), chart.XOf(request["fraction"]!.GetValue<double>()));
                break;
            case "graphDrag" when _statsPanel?.EntropyChartControl is { } chart:
                chart.Release(chart.XOf(request["from"]!.GetValue<double>()), chart.XOf(request["to"]!.GetValue<double>()));
                break;
            case "zoomIn" when _statsPanel?.EntropyChartControl is { } chart:
                for (long i = 0; i < TestHookSettings.ReadLong(request["times"], 1); i++)
                {
                    chart.ZoomIn();
                }

                result["zoom"] = chart.Zoom;
                break;
            case "export":
                await StatsVm.ExportAsync(request["json"]?.GetValue<bool>() ?? true);
                break;
            case "graphKey" when _statsPanel?.EntropyChartControl is { } chart:
                // フォーカスのあるグラフにキーを送る (テスト用のキーの命令は Hex ビューに送るため)。
                chart.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                for (long i = 0; i < TestHookSettings.ReadLong(request["count"], 1); i++)
                {
                    chart.HandleKey(Enum.Parse<Windows.System.VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true));
                }

                break;
            case "flushDetail":
                await StatsVm.FlushDetailAsync();
                break;
            case "digramHover" when _statsPanel?.DigramHeatmap is { } map:
                result["text"] = map.HoverText((int)TestHookSettings.ReadLong(request["x"], 0), (int)TestHookSettings.ReadLong(request["y"], 0));
                break;
            case "digramInvoke" when _statsPanel?.DigramHeatmap is { } map:
                map.InvokeCell((int)TestHookSettings.ReadLong(request["x"], 0), (int)TestHookSettings.ReadLong(request["y"], 0));
                break;
            case "positionClick" when _statsPanel?.PositionHeatmap is { } map:
                map.ClickCell((int)TestHookSettings.ReadLong(request["x"], 0), (int)TestHookSettings.ReadLong(request["y"], 0));
                break;
            case "patterns":
                await StatsVm.ComputePatternsAsync();
                break;
            case "periodMenu" when _statsPanel is { } panel:
            {
                // 周期の行の右クリックメニューを作り、「この長さでレコード表示」を選ぶ。
                PeriodRowViewModel row = StatsVm.PeriodRows[(int)TestHookSettings.ReadLong(request["row"], 0)];
                MenuFlyout menu = panel.PeriodMenu(row);
                var item = (MenuFlyoutItem)menu.Items[0];
                result["enabled"] = item.IsEnabled;
                result["text"] = item.Text;
                if (item.IsEnabled)
                {
                    StatsVm.ShowRecords(row);
                }

                break;
            }

            case "recordProbe":
                // レコード表示 (VIEW-18) がまだつながっていなければ、要求を記録するだけの行き先をつなぐ。
                StatsVm.ShowRecordView ??= length => _recordViewRequests.Add(length);
                break;
            case "classify":
                await StatsVm.ClassifyAsync();
                break;
            case "classMenu" when _statsPanel is { } panel:
            {
                ClassRowViewModel row = StatsVm.ClassRows.First(r => r.Signature?.Format == request["format"]?.GetValue<string>());
                MenuFlyout menu = panel.ClassMenu(row);
                result["items"] = new JsonArray([.. menu.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)new JsonObject
                {
                    ["text"] = i.Text,
                    ["id"] = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(i),
                    ["enabled"] = i.IsEnabled,
                })]);
                break;
            }
        }

        result["open"] = IsStatisticsPanelOpen;
        result["location"] = PanelLocation(StatisticsPanelId);
        if (_statsVm is not { } vm)
        {
            return result;
        }

        result["computing"] = vm.IsComputing;
        result["range"] = vm.RangeText;
        result["note"] = vm.ResultNote;
        result["status"] = vm.StatusText;
        result["stale"] = vm.IsStale;
        result["highlighted"] = vm.ComputeHighlighted;
        result["tab"] = vm.Tab.ToString();
        result["completed"] = vm.Result?.Completed;
        result["verdict"] = vm.Verdict;
        result["digramMessage"] = vm.DigramMessage;
        result["selectedBlock"] = vm.SelectedBlock;
        result["recordViewRequests"] = new JsonArray([.. _recordViewRequests.Select(l => (JsonNode?)l)]);
        if (vm.Histogram is { } h)
        {
            result["bins"] = h.BinCount;
            result["counts"] = h.BinCount <= 4096 ? new JsonArray([.. h.Counts.Select(c => (JsonNode?)c)]) : null;
        }

        result["histogramRows"] = new JsonArray([.. vm.HistogramRows.Select(r => (JsonNode?)new JsonObject
        {
            ["value"] = r.Value,
            ["char"] = r.Character,
            ["count"] = r.Count,
            ["percent"] = r.Percent,
        })]);
        result["descriptive"] = Rows(vm.DescriptiveRows);
        result["entropy"] = Rows(vm.EntropyRows);
        if (vm.Blocks is { } b)
        {
            result["blockSize"] = b.BlockSize;
            result["blocks"] = b.Count;
        }

        result["detailLog"] = new JsonArray([.. vm.DetailLog.Select(d => (JsonNode?)new JsonObject { ["blockSize"] = d.BlockSize, ["from"] = d.From, ["to"] = d.To })]);
        result["blockRows"] = new JsonArray([.. vm.BlockRows.Select(r => (JsonNode?)new JsonObject { ["offset"] = r.Offset, ["blockSize"] = r.BlockSize, ["entropy"] = r.Entropy })]);
        result["ngrams"] = new JsonArray([.. vm.NGramRows.Select(r => (JsonNode?)new JsonObject { ["hex"] = r.Hex, ["count"] = r.Row.Count, ["first"] = r.Row.FirstOffset })]);
        result["periods"] = new JsonArray([.. vm.PeriodRows.Select(r => (JsonNode?)r.Candidate.Period)]);
        result["patternWorkingBytes"] = vm.PatternWorkingBytes;
        result["classRows"] = new JsonArray([.. vm.ClassRows.Select(r => (JsonNode?)new JsonObject
        {
            ["offset"] = r.OffsetValue,
            ["length"] = r.LengthValue,
            ["kind"] = r.Kind,
            ["class"] = r.Class?.ToString(),
            ["signature"] = r.Signature?.Format,
        })]);
        result["legend"] = new JsonArray([.. vm.Legend.Select(l => (JsonNode?)new JsonObject { ["name"] = l.Name, ["pattern"] = l.Pattern })]);
        result["classifyStatus"] = vm.ClassifyStatus;
        return result;
    }

    private static JsonArray Rows(IEnumerable<NameValueRowViewModel> rows) =>
        [.. rows.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["value"] = r.Value, ["toolTip"] = r.ToolTip })];

    private void ApplyStatisticsSettings(JsonObject request)
    {
        StatisticsViewModel vm = StatsVm;
        if (request["type"]?.GetValue<string>() is { } type && ElementTypes.TryParse(type, out ElementType t))
        {
            vm.TypeIndex = (int)t;
        }

        if (request["bigEndian"] is { } big)
        {
            vm.BigEndian = big.GetValue<bool>();
        }

        if (request["stride"] is { } stride)
        {
            vm.Stride = stride.GetValue<string>();
        }

        if (request["blockSize"] is { } size)
        {
            vm.BlockSizeIndex = StatisticsViewModel.BlockSizes.ToList().IndexOf(size.GetValue<long>());
        }

        if (request["histogramAsTable"] is { } table)
        {
            vm.HistogramAsTable = table.GetValue<bool>();
        }

        if (request["graphAsTable"] is { } graph)
        {
            vm.GraphAsTable = graph.GetValue<bool>();
        }

        if (request["autoRecompute"] is { } auto)
        {
            vm.AutoRecompute = auto.GetValue<bool>();
        }

        if (request["target"]?.GetValue<string>() is { } target && Enum.TryParse(target, ignoreCase: true, out StatsTargetKind kind))
        {
            vm.ChooseTarget(kind);
        }

        if (request["tab"]?.GetValue<string>() is { } tabName && Enum.TryParse(tabName, out StatsTab tab))
        {
            vm.Tab = tab;
            _statsPanel?.ShowTab(tab);
        }

        if (request["patternView"] is { } view)
        {
            vm.PatternView = (int)view.GetValue<long>();
        }
    }

    /// <summary>"fileType": {action: show / state / detect / here / embedded}。</summary>
    private async Task<JsonObject> TestFileTypeAsync(JsonObject request)
    {
        switch (request["action"]?.GetValue<string>() ?? "state")
        {
            case "show":
                ShowFileTypePanel();
                break;
            case "detect":
                ShowFileTypePanel();
                await FileTypeVm.DetectAsync();
                break;
            case "here":
                ShowFileTypePanel();
                await FileTypeVm.DetectHereAsync();
                break;
            case "embedded":
                ShowFileTypePanel();
                await FileTypeVm.FindEmbeddedAsync();
                break;
            case "embeddedTarget":
                // 「埋め込まれた形式を探す」の対象範囲 (06 の 0.1): {target: 0〜3, start, length, usesEnd}。
                ShowFileTypePanel();
                if (request["target"] is { } target)
                {
                    FileTypeVm.ChooseEmbeddedTarget((Core.Statistics.AnalysisTargetKind)target.GetValue<long>());
                }

                if (request["start"] is { } start)
                {
                    FileTypeVm.EmbeddedStart = start.GetValue<string>();
                }

                if (request["length"] is { } length)
                {
                    FileTypeVm.EmbeddedLength = length.GetValue<string>();
                }

                if (request["usesEnd"] is { } usesEnd)
                {
                    FileTypeVm.EmbeddedUsesEnd = usesEnd.GetValue<bool>();
                }

                break;
        }

        var result = new JsonObject
        {
            ["open"] = IsFileTypePanelOpen,
            ["location"] = PanelLocation(FileTypePanelId),
            ["status"] = StatusFileType.Content as string ?? string.Empty,
            ["autoBytesRead"] = FileTypeViewModel.LastAutoBytesRead,
        };
        if (_fileTypeVm is { } vm)
        {
            result["summary"] = vm.Summary;
            result["mismatch"] = vm.MismatchText;
            result["candidates"] = new JsonArray([.. vm.Candidates.Select(c => (JsonNode?)new JsonObject { ["name"] = c.Name, ["mime"] = c.Mime, ["confidence"] = c.Candidate.Confidence })]);
            result["embedded"] = new JsonArray([.. vm.Embedded.Select(e => (JsonNode?)new JsonObject { ["offset"] = e.Format.Offset, ["name"] = e.Name, ["mime"] = e.Mime })]);
            result["embeddedTarget"] = vm.EmbeddedTargetIndex;
            result["embeddedRange"] = vm.EmbeddedRangeText;
            result["embeddedStatus"] = vm.EmbeddedStatus;
        }

        return result;
    }
}
#endif
