#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;

namespace HexEditor.App;

/// <summary>スクリプトの画面 (AUTO-19〜28、46) のテスト用の命令 ("scriptUi")。パネルとエディタの状態を読み、操作する。</summary>
public sealed partial class MainWindow
{
    /// <summary>"scriptUi": {action: panels / outputRows / outputLink / outputClear / list / activeEditor / editorText / consoleSubmit / consoleEntries / consoleInput / consoleUp / openEditor / waitIdle}。</summary>
    private async Task<JsonObject?> HandleScriptUiTestCommandAsync(string cmd, JsonObject request)
    {
        if (cmd != "scriptUi")
        {
            return null;
        }

        string action = request["action"]!.GetValue<string>();
        switch (action)
        {
            case "panels":
                return new JsonObject
                {
                    ["scripts"] = IsPanelShown(ScriptsPanelId),
                    ["output"] = IsPanelShown(OutputPanelId),
                    ["console"] = IsPanelShown(ConsolePanelId),
                };
            case "outputRows":
            {
                OutputVm.Flush();
                return new JsonObject
                {
                    ["total"] = OutputVm.TotalCount,
                    ["rows"] = new JsonArray([.. OutputVm.Rows.Select(r => (JsonNode?)new JsonObject
                    {
                        ["kind"] = r.KindName,
                        ["time"] = r.TimeText,
                        ["channel"] = r.ChannelText,
                        ["text"] = r.Text,
                        ["link"] = r.LinkText,
                        ["accessible"] = r.AccessibleName,
                    })]),
                };
            }

            case "outputLink":
            {
                OutputVm.Flush();
                OutputRow row = OutputVm.Rows[request["index"]!.GetValue<int>()];
                OpenScriptEditor(row.Line.SourcePath!, row.Line.Line, Math.Max(1, row.Line.Column));
                return new JsonObject();
            }

            case "outputClear":
                OutputVm.Clear();
                return new JsonObject();
            case "list":
                return new JsonObject
                {
                    ["items"] = new JsonArray([.. ScriptListVm.AllItems.Select(i => (JsonNode?)new JsonObject
                    {
                        ["name"] = i.DisplayName,
                        ["path"] = i.Path,
                        ["script"] = i.IsScript,
                        ["shortcut"] = i.ShortcutText,
                        ["description"] = i.Description,
                    })]),
                };
            case "activeEditor":
                return ActiveScriptEditor is { } editor
                    ? new JsonObject { ["open"] = true, ["path"] = editor.Path, ["line"] = editor.CaretLine, ["dirty"] = editor.IsDirty, ["problems"] = editor.ProblemCount }
                    : new JsonObject { ["open"] = false };
            case "editorText":
                return new JsonObject { ["text"] = ActiveScriptEditor?.Text };
            case "setEditorText":
                ActiveScriptEditor!.Text = request["text"]!.GetValue<string>();
                return new JsonObject();
            case "openEditor":
                OpenScriptEditor(request["path"]!.GetValue<string>(), request["line"]?.GetValue<int>() ?? 0);
                return new JsonObject();
            case "consoleSubmit":
                await ConsoleVm.SubmitAsync(request["code"]!.GetValue<string>());
                return new JsonObject();
            case "consoleEntries":
                return new JsonObject
                {
                    ["entries"] = new JsonArray([.. ConsoleVm.Entries.Select(e => (JsonNode?)new JsonObject { ["kind"] = e.Kind.ToString(), ["text"] = e.Text })]),
                };
            case "consoleInput":
                return new JsonObject { ["text"] = FindPanelContent<ConsolePanel>(ConsolePanelId)?.InputText };
            case "consoleUp":
                FindPanelContent<ConsolePanel>(ConsolePanelId)!.HistoryUp();
                return new JsonObject();
            case "start":
                // ダイアログが出るコマンドを、閉じるのを待たずに始める (命令の通り道をふさがない)。
                _ = Commands.ExecuteAsync(request["id"]!.GetValue<string>()).ContinueWith(
                    t => AppLog.Warning($"Started command failed: {t.Exception?.GetBaseException()}"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                return new JsonObject();
            case "macroState":
                return new JsonObject
                {
                    ["recording"] = ScriptWorkspace.Macros.IsRecording,
                    ["count"] = ScriptWorkspace.Macros.Recorder?.Count ?? 0,
                    ["last"] = ScriptWorkspace.Macros.LastScript,
                };
            case "status":
                return new JsonObject { ["text"] = StatusMessageText };
            case "listActivate":
            {
                // Enter キーと同じ: スクリプトは実行する。
                string path = request["path"]!.GetValue<string>();
                ScriptWorkspace.Run(ScriptListVm.Find(path)!.Node);
                return new JsonObject();
            }

            case "samples":
                return new JsonObject { ["folder"] = ScriptWorkspace.SamplesFolder, ["scripts"] = ScriptingService.Folders.Scripts };
            default:
                throw new ArgumentException($"Unknown scriptUi action: {action}");
        }
    }
}
#endif
