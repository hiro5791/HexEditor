#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates.Inference;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App;

/// <summary>テンプレートの推定 (TPL-38〜TPL-40) のテスト用の命令 ("tplInfer")。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleTemplateInferenceTestCommandsAsync(string cmd, JsonObject request) =>
        cmd == "tplInfer" ? await TestTemplateInferenceAsync(request) : null;

    private async Task<JsonObject> TestTemplateInferenceAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        TemplateInferencePanel panel = InferencePanel;
        TemplateInferenceSession? s = _inferenceSession;
        int RowIndex() => request["offset"] is { } o ? s!.Table.RowAt(o.GetValue<int>()) : (int)TestHookSettings.ReadLong(request["index"], panel.Table.SelectedIndex);
        switch (action)
        {
            case "state":
                return InferenceTestState();
            case "wait":
            {
                int runs = (int)TestHookSettings.ReadLong(request["runs"], 1);
                DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while ((s is null || s.Running || s.Runs < runs) && DateTime.UtcNow < until)
                {
                    await Task.Delay(20);
                    s = _inferenceSession;
                }

                return InferenceTestState();
            }

            case "set":
                if (s is null)
                {
                    throw new InvalidOperationException("No document.");
                }

                s.StartText = request["start"]?.GetValue<string>() ?? s.StartText;
                s.LengthText = request["length"]?.GetValue<string>() ?? s.LengthText;
                s.RecordLengthText = request["recordLength"]?.GetValue<string>() ?? s.RecordLengthText;
                s.RecordCountText = request["count"]?.GetValue<string>() ?? s.RecordCountText;
                if (request["endian"]?.GetValue<string>() is { } endian)
                {
                    s.Endian = Enum.Parse<InferenceEndian>(endian, ignoreCase: true);
                }

                panel.Refresh();
                return InferenceTestState();
            case "run":
                await RunInferenceAsync(keepManual: request["keepManual"]?.GetValue<bool>() ?? false);
                return InferenceTestState();
            case "selectRow":
                panel.Table.SelectedIndex = RowIndex();
                return InferenceTestState();
            case "rename":
            {
                panel.Table.SelectedIndex = RowIndex();
                await panel.RenameAsync();
                bool ok = panel.CommitRenameForTest(request["name"]!.GetValue<string>());
                JsonObject state = InferenceTestState();
                state["committed"] = ok;
                state["renameError"] = panel.RenameError?.Text;
                panel.RenameFlyout?.Hide();
                return state;
            }

            case "setType":
            {
                int index = RowIndex();
                InferenceTypeChoice choice = s!.Table.TypeChoices(index).First(c => c.Type.Id == request["type"]!.GetValue<string>());
                panel.SetType(index, choice);
                return InferenceTestState();
            }

            case "merge":
                panel.Table.SelectedIndex = RowIndex();
                InvokeRowMenu(panel, "TplInfer_Menu_Merge");
                return InferenceTestState();
            case "split":
            {
                panel.Table.SelectedIndex = RowIndex();
                InvokeRowMenu(panel, "TplInfer_Menu_Split");
                bool ok = panel.CommitSplitForTest((int)TestHookSettings.ReadLong(request["at"], 1));
                JsonObject state = InferenceTestState();
                state["committed"] = ok;
                return state;
            }

            case "focus":
                if (request["target"]?.GetValue<string>() == "recordLength")
                {
                    panel.FocusRecordLength();
                }
                else
                {
                    panel.FocusTable();
                }

                await Task.Delay(50);
                return InferenceTestState();
            case "key":
                return await InferenceKeyForTestAsync(panel, request);
            case "menuInvoke":
                InvokeRowMenu(panel, request["id"]!.GetValue<string>());
                return InferenceTestState();
            case "renameType":
            {
                bool ok = panel.CommitRenameForTest(request["text"]!.GetValue<string>());
                JsonObject state = InferenceTestState();
                state["committed"] = ok;
                return state;
            }

            case "apply":
                ApplyInference();
                return InferenceTestState();
            case "openEditor":
                OpenInferenceInEditor();
                return InferenceTestState();
            case "catalogFind":
                return new JsonObject { ["path"] = TemplateService.Catalog.Find(request["name"]!.GetValue<string>())?.Path };
            case "close":
                HidePanel(TemplateInferencePanelId);
                await Task.Delay(50);
                return InferenceTestState();
            default:
                throw new ArgumentException("Unknown tplInfer action: " + action);
        }
    }

    private static void InvokeRowMenu(TemplateInferencePanel panel, string id)
    {
        // メニューを開いたときと同じく項目を作り直してから押す
        panel.UpdateRowMenu();
        MenuFlyoutItem item = panel.RowMenu.Items.OfType<MenuFlyoutItem>().First(i => AutomationProperties.GetAutomationId(i) == id);
        if (!item.IsEnabled)
        {
            throw new InvalidOperationException(id + " is disabled.");
        }

        if (panel.RowMenu.IsOpen)
        {
            panel.RowMenu.Hide();
        }

        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item)).Invoke();
    }

    /// <summary>パネルの中のキー (Tab・↑↓・Shift+F10・Enter・Ctrl+Z / Ctrl+Y)。実際のキーボードは使わない (テスト方針 7.2)。</summary>
    private async Task<JsonObject> InferenceKeyForTestAsync(TemplateInferencePanel panel, JsonObject request)
    {
        var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        bool shift = request["shift"]?.GetValue<bool>() ?? false;
        bool ctrl = request["ctrl"]?.GetValue<bool>() ?? false;
        object? focused = panel.XamlRoot is null ? null : FocusManager.GetFocusedElement(panel.XamlRoot);
        bool inTable = focused is DependencyObject d && IsWithin(d, panel.Table);
        string handled = "none";
        if (ctrl && panel.HandleEditorKey(key, ctrl, shift, false))
        {
            handled = "panel";
        }
        else if (key == VirtualKey.Tab)
        {
            FocusManager.TryMoveFocus(shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next,
                new FindNextElementOptions { SearchRoot = panel.XamlRoot!.Content });
            handled = "focus";
        }
        else if (inTable && key is VirtualKey.Down or VirtualKey.Up)
        {
            int next = Math.Clamp(panel.Table.SelectedIndex + (key == VirtualKey.Down ? 1 : -1), 0, panel.Table.Items.Count - 1);
            panel.Table.SelectedIndex = next;
            (panel.Table.ContainerFromIndex(next) as Control)?.Focus(FocusState.Keyboard);
            handled = "table";
        }
        else if (inTable && key == VirtualKey.F10 && shift)
        {
            panel.RowMenu.ShowAt(panel.Table.ContainerFromIndex(panel.Table.SelectedIndex) as FrameworkElement ?? panel.Table);
            handled = "menu";
        }
        else if (key == VirtualKey.Enter && focused is Button button)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
            handled = "button";
        }

        await Task.Delay(30);
        JsonObject state = InferenceTestState();
        state["handledBy"] = handled;
        return state;
    }

    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? n = node; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private JsonObject InferenceTestState()
    {
        TemplateInferenceSession? s = _inferenceSession;
        TemplateInferencePanel? panel = _inferencePanel;
        object? focused = panel?.XamlRoot is null ? null : FocusManager.GetFocusedElement(panel.XamlRoot);
        string? focusedId = focused is DependencyObject fd ? AutomationProperties.GetAutomationId(fd) : null;
        if (focused is DependencyObject node && panel is not null && IsWithin(node, panel.Table))
        {
            focusedId = "TplInfer_Table";
        }

        TemplateInferenceResult? r = s?.Result;
        return new JsonObject
        {
            ["shown"] = IsPanelShown(TemplateInferencePanelId),
            ["loaded"] = panel?.IsLoaded ?? false,
            ["start"] = s?.StartText,
            ["length"] = s?.LengthText,
            ["recordLengthText"] = s?.RecordLengthText,
            ["running"] = s?.Running ?? false,
            ["runs"] = s?.Runs ?? 0,
            ["recordLength"] = r?.RecordLength,
            ["recordCount"] = r?.RecordCount,
            ["remainder"] = r?.Remainder,
            ["rangeStart"] = r?.Start,
            ["rangeLength"] = r?.RangeLength,
            ["notFound"] = r?.NotFound ?? false,
            ["blocked"] = r?.Blocked?.Key,
            ["bigEndian"] = r?.BigEndian,
            ["status"] = TemplateInferencePanel.StatusText(s),
            ["candidates"] = new JsonArray([.. (s?.Candidates ?? []).Select(c => (JsonNode?)c.Length)]),
            ["rows"] = new JsonArray([.. (s?.Table.Rows ?? []).Select(row => (JsonNode?)new JsonObject
            {
                ["offset"] = row.Offset,
                ["size"] = row.Size,
                ["name"] = row.Name,
                ["type"] = row.TypeDisplay,
                ["typeId"] = row.Type.Id,
                ["kind"] = row.Kind.ToString(),
                ["confidence"] = row.Confidence.ToString(),
                ["manual"] = row.IsManual,
                ["low"] = row.IsLowConfidence,
                ["automationName"] = TemplateInferencePanel.RowName(row),
            })]),
            ["selectedRow"] = panel?.Table.SelectedIndex ?? -1,
            ["draft"] = s?.Draft,
            ["internalError"] = s?.InternalError,
            ["focused"] = focusedId,
            ["rowMenuOpen"] = panel?.RowMenu.IsOpen ?? false,
            ["renameOpen"] = panel?.RenameBox is not null,
            ["applied"] = s?.Applied is { } a && DocumentTemplates.Find(s.Document) is { } t && t.Entries.Contains(a),
            ["canUndo"] = s?.Table.CanUndo ?? false,
            ["canRedo"] = s?.Table.CanRedo ?? false,
            ["documentUndo"] = Vm.Selected?.Document.History.Count,
        };
    }
}
#endif
