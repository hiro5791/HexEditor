#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Windows.System;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、比較 (ANA-01〜ANA-08) の命令。比較タブ・差分の一覧・「ファイルを比較」ダイアログの状態を読み、操作する
/// (実際のマウス・キーボードは使わない。テスト方針 7.2)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>比較の命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleCompareTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "compareOpen":
                return await TestCompareOpenAsync(request);
            case "compareState":
                return TestCompareState(request);
            case "compareWait":
                // 比較の完了を待つ (結果の状態を返す)。
                var opened = System.Diagnostics.Stopwatch.StartNew();
                while ((ActiveCompare ?? CurrentCompare) is null && opened.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                }

                if ((ActiveCompare ?? CurrentCompare) is { } waiting)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    TimeSpan limit = TimeSpan.FromSeconds(TestHookSettings.ReadLong(request["timeoutSeconds"], 60));
                    while ((waiting.IsRunning || waiting.Result is null || (waiting.Result.State == CompareState.Completed && waiting.Distribution is null))
                        && watch.Elapsed < limit)
                    {
                        await Task.Delay(20);
                    }
                }

                return TestCompareState(request);
            case "compareDialog":
                return await TestCompareDialogAsync(request);
            case "compareFocus":
                if (ActiveCompare is { } focusSession && _compareViews.TryGetValue(focusSession, out CompareView? focusView))
                {
                    bool right = request["right"]?.GetValue<bool>() ?? false;
                    focusView.ViewOf(right).Focus(FocusState.Programmatic);
                    OnCompareSideFocused(focusSession, right ? focusSession.Right : focusSession.Left);
                }

                return TestCompareState(request);
            case "compareMethodBox":
                if (ActiveCompare is { } methodSession && _compareViews.TryGetValue(methodSession, out CompareView? methodView))
                {
                    methodView.SelectMethod((int)request["index"]!.GetValue<long>());
                }

                return TestCompareState(request);
            case "compareMapClick":
                if (ActiveCompare is { } mapSession && _compareViews.TryGetValue(mapSession, out CompareView? mapView))
                {
                    mapView.MapOf(request["right"]?.GetValue<bool>() ?? false).Click(request["fraction"]!.GetValue<double>());
                    RenderCompareViews(mapSession);
                }

                return TestCompareState(request);
            case "compareListClick":
                DiffPanel.ClickRow(request["row"]!.GetValue<long>(), request["ctrl"]?.GetValue<bool>() ?? false, request["shift"]?.GetValue<bool>() ?? false);
                return TestCompareState(request);
            case "compareListKey":
                DiffPanel.HandleKey(Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true),
                    request["shift"]?.GetValue<bool>() ?? false, request["ctrl"]?.GetValue<bool>() ?? false);
                return TestCompareState(request);
            case "compareListFilter":
                DiffKindFilter kinds = DiffKindFilter.None;
                foreach (JsonNode? k in request["kinds"]?.AsArray() ?? [])
                {
                    kinds |= Enum.Parse<DiffKindFilter>(k!.GetValue<string>(), ignoreCase: true);
                }

                await DiffPanel.SetFilterAsync(request["kinds"] is null ? DiffKindFilter.All : kinds, TestHookSettings.ReadLong(request["minLength"], 0));
                return TestCompareState(request);
            case "compareGraph":
                if (request["table"] is { } table)
                {
                    DiffPanel.ShowTable(table.GetValue<bool>());
                }

                bool moved = request["bucket"] is { } bucket && DiffPanel.ClickBar((int)bucket.GetValue<long>());
                JsonObject graph = TestCompareState(request);
                graph["moved"] = moved;
                graph["tableRows"] = new JsonArray([.. DiffPanel.TableRows.Select(r => (JsonNode?)r)]);
                graph["bars"] = DiffPanel.BarCount;
                return graph;
            case "compareConvert":
                if (CurrentCompare is { } convertSession)
                {
                    IReadOnlyList<long> indices = DiffPanel.SelectedIndices;
                    bool toRight = request["right"]?.GetValue<bool>() ?? false;
                    if (request["to"]?.GetValue<string>() == "bookmarks")
                    {
                        DiffsToBookmarks(convertSession, toRight, indices);
                    }
                    else
                    {
                        DiffsToMultiSelection(convertSession, toRight, indices);
                    }
                }

                JsonObject converted = TestCompareState(request);
                if (_lastMultiSelection is { } last)
                {
                    converted["multiSelection"] = new JsonObject
                    {
                        ["document"] = last.Document.DisplayName,
                        ["ranges"] = new JsonArray([.. last.Ranges.Select(r => (JsonNode?)new JsonObject { ["offset"] = r.Offset, ["length"] = r.Length })]),
                    };
                }

                return converted;
            case "compareExport":
                if (CurrentCompare is { } exportSession)
                {
                    IReadOnlyList<long>? rows = request["selected"]?.GetValue<bool>() == true ? DiffPanel.SelectedIndices : null;
                    Task export = ExportDiffsToAsync(exportSession, request["format"]?.GetValue<string>() ?? "csv", rows, request["path"]!.GetValue<string>());
                    if (request["noWait"]?.GetValue<bool>() != true)
                    {
                        await export;
                    }
                }

                return TestCompareState(request);
            case "compareHighlights":
                if (ActiveCompare is { } hlSession && _compareViews.TryGetValue(hlSession, out CompareView? hlView))
                {
                    HexView view = hlView.ViewOf(request["right"]?.GetValue<bool>() ?? false);
                    view.RenderNow();
                    JsonObject highlights = view.ReadHighlights();
                    highlights["markers"] = new JsonArray([.. view.PlacedMarkers.Select(m => (JsonNode?)new JsonObject { ["kind"] = m.Kind, ["top"] = m.Top })]);
                    highlights["mapLines"] = new JsonArray([.. hlView.MapOf(request["right"]?.GetValue<bool>() ?? false).Placed
                        .Select(m => (JsonNode?)new JsonObject { ["kind"] = m.Kind.ToString(), ["top"] = m.Top, ["height"] = m.Height })]);
                    highlights["summary"] = view.CursorSummary();
                    return highlights;
                }

                return new JsonObject();
            default:
                return null;
        }
    }

    /// <summary>
    /// 2 つのファイル (開いていなければタブで開く) を比較する (コマンドラインの --compare (AUTO-37、F3-08) の代わり)。
    /// {left, right, leftStart, leftLength, rightStart, rightLength, method, window, minMatch, mergeGap, unit}。
    /// </summary>
    private async Task<JsonObject> TestCompareOpenAsync(JsonObject request)
    {
        DocumentViewModel Doc(string path) => Vm.Documents.FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            ?? TryOpen(path) ?? throw new InvalidOperationException("Cannot open " + path);
        DocumentViewModel left = Doc(request["left"]!.GetValue<string>());
        DocumentViewModel right = Doc(request["right"]!.GetValue<string>());
        var options = new CompareOptions
        {
            Method = request["method"]?.GetValue<string>() == "insertDelete" ? CompareMethod.InsertDelete : CompareMethod.Simple,
            Window = (int)TestHookSettings.ReadLong(request["resyncWindow"], CompareOptions.DefaultWindow),
            MinMatch = (int)TestHookSettings.ReadLong(request["minMatch"], CompareOptions.DefaultMinMatch),
            MergeGap = (int)TestHookSettings.ReadLong(request["mergeGap"], 0),
            Unit = (int)TestHookSettings.ReadLong(request["unit"], 1),
        };
        long? Length(string key) => request[key] is null ? null : TestHookSettings.ReadLong(request[key], 0);
        Task<CompareSessionViewModel?> open = OpenCompareAsync(
            new CompareTargetSpec(CompareSourceKind.Document, left, null, TestHookSettings.ReadLong(request["leftStart"], 0), Length("leftLength")),
            new CompareTargetSpec(CompareSourceKind.Document, right, null, TestHookSettings.ReadLong(request["rightStart"], 0), Length("rightLength")),
            options);
        if (request["noWait"]?.GetValue<bool>() != true)
        {
            await open;
        }

        return TestCompareState(request);
    }

    /// <summary>「ファイルを比較」ダイアログ: {action: open / set / swap / state / compare / cancel, …}。</summary>
    private async Task<JsonObject> TestCompareDialogAsync(JsonObject request)
    {
        switch (request["action"]?.GetValue<string>() ?? "state")
        {
            case "open":
                _ = Commands.ExecuteAsync("analysis.compare");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while ((_compareDialog is null || !_compareDialog.IsLoaded) && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(20);
                }

                break;
            case "set" when _compareDialog is { } dialog:
                foreach (string side in new[] { "left", "right" })
                {
                    if (request[side] is JsonObject s)
                    {
                        bool right = side == "right";
                        if (s["target"] is { } t)
                        {
                            dialog.SetTarget(right, (int)t.GetValue<long>());
                        }

                        if (s["path"] is { } p)
                        {
                            dialog.SetPath(right, p.GetValue<string>());
                        }

                        dialog.SetRange(right, s["start"]?.GetValue<string>(), s["length"]?.GetValue<string>());
                    }
                }

                if (request["method"] is { } m)
                {
                    dialog.SetMethod(m.GetValue<string>() == "insertDelete" ? CompareMethod.InsertDelete : CompareMethod.Simple);
                }

                dialog.SetOptions(request["resyncWindow"]?.GetValue<string>(), request["minMatch"]?.GetValue<string>(), request["mergeGap"]?.GetValue<string>(),
                    request["unit"] is { } u ? (int)u.GetValue<long>() : null);
                break;
            case "swap" when _compareDialog is { } dialog:
                dialog.Swap();
                break;
            case "compare":
                await PressCompareDialogButtonAsync("PrimaryButton");
                break;
            case "cancel":
                await PressCompareDialogButtonAsync("CloseButton");
                break;
        }

        return _compareDialog?.State() ?? new JsonObject { ["open"] = false };
    }

    /// <summary>ダイアログのボタンを押す (ダイアログが表示されるまで待つ)。</summary>
    private async Task PressCompareDialogButtonAsync(string name)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                TestDialogButton(name);
                return;
            }
            catch (ArgumentException) when (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50);
            }
        }
    }

    private void RenderCompareViews(CompareSessionViewModel session)
    {
        if (_compareViews.TryGetValue(session, out CompareView? view))
        {
            view.LeftView.RenderNow();
            view.RightView.RenderNow();
        }
    }

    /// <summary>比較の状態: 表示中の比較タブ、結果 (差分の先頭の {limit} 件)、左右のカーソル・選択・一番上の行、要約、一覧の行。</summary>
    private JsonObject TestCompareState(JsonObject request)
    {
        CompareSessionViewModel? s = ActiveCompare ?? CurrentCompare;
        var state = new JsonObject
        {
            ["active"] = ActiveCompare?.Id,
            ["count"] = _compares.Count,
            ["toolTabs"] = new JsonArray([.. ToolTabTitles().Select(t => (JsonNode?)t)]),
            ["statusMessage"] = StatusMessageText,
            ["statusCompare"] = StatusCompare.Visibility == Visibility.Visible ? StatusCompare.Content as string : null,
            ["panelShown"] = IsPanelShown(DiffsPanelId),
            ["tabs"] = new JsonArray([.. Vm.Documents.Select(d => (JsonNode?)new JsonObject
            {
                ["name"] = d.DisplayName,
                ["selectionStart"] = d.Editor.SelectionStart,
                ["selectionLength"] = d.Editor.SelectionLength,
            })]),
        };
        if (s is null)
        {
            return state;
        }

        RenderCompareViews(s);
        int limit = (int)TestHookSettings.ReadLong(request["limit"], 100);
        CompareResult? r = s.Result;
        JsonObject Side(CompareSideViewModel side)
        {
            EditorState e = side.Editor;
            var o = new JsonObject
            {
                ["name"] = side.Name,
                ["cursor"] = e.Cursor,
                ["top"] = e.TopRow,
                ["topOffset"] = e.Layout.RowStart(e.TopRow),
                ["visibleRows"] = e.VisibleRows,
                ["selectionStart"] = e.SelectionStart,
                ["selectionLength"] = e.SelectionLength,
                ["closed"] = side.IsClosed,
                ["modified"] = side.Document.IsModified,
                ["length"] = side.Document.Length,
            };
            if (_compareViews.TryGetValue(s, out CompareView? view))
            {
                o["summary"] = view.ViewOf(side.IsRight).CursorSummary();
            }

            return o;
        }

        state["id"] = s.Id;
        state["title"] = s.Title;
        state["running"] = s.IsRunning;
        state["stale"] = s.IsStale;
        state["message"] = s.StatusMessage;
        state["sync"] = s.SyncScroll;
        state["stacked"] = s.Stacked;
        state["focusedRight"] = s.FocusedRight;
        state["currentIndex"] = s.CurrentIndex;
        state["statusText"] = s.StatusText;
        state["left"] = Side(s.Left);
        state["right"] = Side(s.Right);
        state["summary"] = new JsonObject(s.Summary.Select(x => KeyValuePair.Create(x.Id, (JsonNode?)x.Value)));
        state["listCount"] = s.ListCount;
        state["selectedRows"] = new JsonArray([.. DiffPanel.SelectedIndices.Select(i => (JsonNode?)i)]);
        state["distribution"] = s.Distribution?.Count;
        if (r is not null)
        {
            state["state"] = r.State.ToString();
            state["method"] = r.Method.ToString();
            state["diffCount"] = r.Diffs.Count;
            state["differentBytes"] = r.DifferentBytes;
            state["matchPercent"] = r.MatchPercent;
            state["stoppedAt"] = r.StoppedAt;
            state["diffs"] = new JsonArray([.. r.Diffs.Enumerate().Take(limit).Select(d => (JsonNode?)Diff(d))]);
            state["list"] = new JsonArray([.. Enumerable.Range(0, (int)Math.Min(limit, s.ListCount)).Select(row => (JsonNode?)Diff(r.Diffs[s.ListIndex(row)]))]);
        }

        return state;

        static JsonObject Diff(DiffRange d) => new()
        {
            ["kind"] = DiffExport.KindName(d.Kind),
            ["leftOffset"] = d.LeftOffset,
            ["leftLength"] = d.LeftLength,
            ["rightOffset"] = d.RightOffset,
            ["rightLength"] = d.RightLength,
        };
    }

    private IEnumerable<string> ToolTabTitles() => _tabItems.OfType<ToolPageTab>().Select(t => t.Title);
}
#endif
