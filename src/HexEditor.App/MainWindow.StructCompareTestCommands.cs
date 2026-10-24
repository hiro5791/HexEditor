#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace HexEditor.App;

/// <summary>テスト用の命令の通り道の、構造を理解した比較 (ANA-31・ANA-32) の命令。構造の差分パネルの状態を読み、操作する。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleStructCompareTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "structState":
                return TestStructState();
            case "structWait":
                return await TestStructWaitAsync(request);
            case "structTemplate":
                if (CurrentCompare is { } chosen)
                {
                    string path = request["path"]!.GetValue<string>();
                    string full = TemplateService.Catalog.Find(path)?.Path ?? Path.GetFullPath(path);
                    await chosen.SetStructTemplateAsync(new StructTemplateChoice(full, TestHookSettings.ReadLong(request["leftStart"], 0),
                        TestHookSettings.ReadLong(request["rightStart"], 0), StructTemplateReason.UserChoice));
                }

                return await TestStructWaitAsync(request);
            case "structSelect":
                if (CurrentCompare is { } s && FindStructRow(request) is { Node: { } node })
                {
                    StructPanel.SelectRow(node);
                    StructPanel.Activate(node);
                }

                return TestStructState();
            case "structRow":
                // 行を一覧で選ぶだけ (Hex ビューは動かさない)
                if (FindStructRow(request) is { Node: { } only })
                {
                    StructPanel.SelectRow(only);
                }

                return TestStructState();
            case "structFocus":
                ShowPanel(StructDiffsPanelId);
                StructPanel.FocusTree();
                await Task.Delay(1);
                return TestStructState();
            case "structKey":
                bool handled = StructPanel.HandleKey(Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true),
                    request["ctrl"]?.GetValue<bool>() ?? false, request["shift"]?.GetValue<bool>() ?? false);
                JsonObject keyState = TestStructState();
                keyState["handled"] = handled;
                return keyState;
            case "structFilter":
                StructDiffFilterKinds kinds = StructDiffFilterKinds.None;
                foreach (JsonNode? k in request["kinds"]?.AsArray() ?? [])
                {
                    kinds |= Enum.Parse<StructDiffFilterKinds>(k!.GetValue<string>(), ignoreCase: true);
                }

                StructPanel.SetFilter(request["kinds"] is null ? StructDiffFilterKinds.All : kinds, request["text"]?.GetValue<string>() ?? string.Empty,
                    request["padding"]?.GetValue<bool>() ?? false, request["same"]?.GetValue<bool>() ?? false);
                await WaitUntilForTestAsync(() => !StructPanel.IsBusy, TimeSpan.FromSeconds(30));
                return TestStructState();
            case "structMenu":
                // 右クリックメニューを開き、{item} があれば押す
                bool opened = StructPanel.ShowMenu(null);
                JsonObject menuState = TestStructState();
                menuState["menuOpened"] = opened;
                if (StructPanel.LastMenu is { } menu)
                {
                    menuState["menuItems"] = new JsonArray([.. menu.Items.OfType<MenuFlyoutItem>().Select(i => (JsonNode?)AutomationProperties.GetAutomationId(i))]);
                    if (request["item"]?.GetValue<string>() is { } id
                        && menu.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == "StructDiffMenu_" + id) is { } item)
                    {
                        menu.Hide();
                        InvokeMenuItem(item);
                    }
                    else if (request["close"]?.GetValue<bool>() == true)
                    {
                        menu.Hide();
                    }
                }

                return menuState;
            case "structRuleDialog":
                // 「対応付けの方法...」の画面: {method: 0〜3, key, ok}
                await WaitUntilForTestAsync(() => _structRuleDialog is not null, TimeSpan.FromSeconds(10));
                if (_structRuleDialog is not { } rule)
                {
                    return new JsonObject { ["open"] = false };
                }

                var ruleState = new JsonObject
                {
                    ["open"] = true,
                    ["keys"] = new JsonArray([.. rule.Keys.Items.OfType<string>().Select(k => (JsonNode?)k)]),
                };
                if (request["method"] is { } method)
                {
                    rule.Methods.SelectedIndex = (int)method.GetValue<long>();
                }

                if (request["key"]?.GetValue<string>() is { } key)
                {
                    rule.Keys.SelectedItem = key;
                }

                if (request["ok"]?.GetValue<bool>() == true)
                {
                    await WaitUntilForTestAsync(() => rule.Dialog.IsLoaded, TimeSpan.FromSeconds(10));
                    await Task.Delay(1);
                    TestDialogButton("PrimaryButton");
                }

                return ruleState;
            case "structExport":
                // 書き出しの本体を直接呼ぶ: {path, format: json / csv / text, all, hex}
                if (CurrentCompare is { StructResult: { } er } es)
                {
                    IReadOnlyList<StructDiffNode> rows = request["all"]?.GetValue<bool>() == true ? er.Diffs() : [.. StructPanel.View.FilteredDiffs()];
                    await ExportStructDiffsToAsync(es, er, rows, request["format"]?.GetValue<string>() ?? "json", request["hex"]?.GetValue<bool>() ?? false,
                        request["path"]!.GetValue<string>());
                }

                return TestStructState();
            case "structExportDialog":
                // 「エクスポート...」を押して画面が開いたか (閉じる)
                StructPanel.ClickExport();
                await WaitUntilForTestAsync(() => _structExportDialog is not null, TimeSpan.FromSeconds(10));
                bool exportOpen = _structExportDialog is not null;
                if (_structExportDialog is { } exportDialog)
                {
                    exportDialog.Hide();
                }

                return new JsonObject { ["open"] = exportOpen };
            case "structNavigate":
                if (CurrentCompare is { } nav)
                {
                    nav.NavigateStructure = request["structure"]?.GetValue<bool>() ?? true;
                }

                return TestStructState();
        }

        return null;
    }

    private StructDiffRowViewModel? FindStructRow(JsonObject request)
    {
        IReadOnlyList<StructDiffRowViewModel> rows = StructPanel.RowsForTest();
        if (request["row"] is { } row)
        {
            return rows.ElementAtOrDefault((int)row.GetValue<long>());
        }

        string path = request["path"]!.GetValue<string>();
        return rows.FirstOrDefault(r => r.Node is { } n && (n.LeftPath == path || n.RightPath == path || n.DisplayPath == path));
    }

    private async Task<JsonObject> TestStructWaitAsync(JsonObject request)
    {
        TimeSpan limit = TimeSpan.FromSeconds(TestHookSettings.ReadLong(request["timeoutSeconds"], 60));
        await WaitUntilForTestAsync(() => CurrentCompare is { } c && !c.IsRunning && c.StructResult is { } r && !r.IsRunning
            && (r.Choice.IsNone || r.Outside is not null || c.Result?.State != Core.Compare.CompareState.Completed)
            && StructPanel.View.Result is var shown && (r.Choice.IsNone || ReferenceEquals(shown, r)) && !StructPanel.IsBusy, limit);
        StructPanel.Update();
        return TestStructState();
    }

    private JsonObject TestStructState()
    {
        CompareSessionViewModel? c = CurrentCompare;
        StructCompareResult? r = c?.StructResult;
        JsonObject Editor(CompareSideViewModel? side) => side is null ? [] : new JsonObject
        {
            ["cursor"] = side.Editor.Cursor,
            ["selectionStart"] = side.Editor.SelectionStart,
            ["selectionLength"] = side.Editor.SelectionLength,
        };

        var rows = new JsonArray();
        int index = 0;
        foreach (StructDiffRowViewModel row in StructPanel.RowsForTest())
        {
            StructDiffNode? n = row.Node;
            rows.Add(new JsonObject
            {
                ["index"] = index++,
                ["name"] = row.Name,
                ["path"] = n?.DisplayPath,
                ["leftPath"] = n?.LeftPath,
                ["rightPath"] = n?.RightPath,
                ["kind"] = n is null ? "outside" : StructDiffExport.KindName(n.Kind),
                ["kindText"] = row.KindText,
                ["left"] = row.LeftValue,
                ["right"] = row.RightValue,
                ["leftOffset"] = n?.LeftOffset,
                ["rightOffset"] = n?.RightOffset,
                ["leftSize"] = n?.LeftSize,
                ["rightSize"] = n?.RightSize,
                ["depth"] = row.Row.Depth,
                ["hasChildren"] = row.Row.HasChildren,
                ["expanded"] = row.Row.IsExpanded,
                ["match"] = row.MatchText,
                ["accessibleName"] = row.AccessibleName,
                ["automationId"] = row.AutomationId,
            });
        }

        return new JsonObject
        {
            ["hasCompare"] = c is not null,
            ["enabled"] = c?.StructureEnabled,
            ["navigateStructure"] = c?.NavigateStructure,
            ["state"] = r is null ? null : StructDiffExport.StateName(r.State),
            ["template"] = r?.Choice.TemplatePath,
            ["templateName"] = r?.TemplateName,
            ["choiceReason"] = r?.Choice.Reason.ToString(),
            ["leftStart"] = r?.Choice.LeftStart,
            ["rightStart"] = r?.Choice.RightStart,
            ["diffCount"] = r?.DiffCount,
            ["leftFields"] = r?.LeftFields,
            ["outsideCount"] = r?.Outside?.Count,
            ["outsideBytes"] = r?.Outside?.Bytes,
            ["heading"] = StructPanel.HeadingForTest,
            ["summary"] = StructPanel.SummaryForTest,
            ["reason"] = StructPanel.ReasonForTest,
            ["chooseVisible"] = StructPanel.ChooseVisible,
            ["raiseVisible"] = StructPanel.RaiseVisible,
            ["panelShown"] = IsPanelShown(StructDiffsPanelId),
            ["treeFocused"] = StructPanel.HasTreeFocus,
            ["selectedRow"] = StructPanel.SelectedRowIndex,
            ["current"] = c?.StructCurrent?.DisplayPath,
            ["left"] = Editor(c?.Left),
            ["right"] = Editor(c?.Right),
            ["rows"] = rows,
            ["status"] = StatusMessageText,
        };
    }
}
#endif
