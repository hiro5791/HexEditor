#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;

namespace HexEditor.App;

/// <summary>スクリプトのテスト用の命令 ("script")。出力パネル (AUTO-24) ができるまで、出力の記録をここで読む。</summary>
public sealed partial class MainWindow
{
    /// <summary>"script": {action: run (path) / output / running / stopAll / results}。</summary>
    private JsonObject? HandleScriptTestCommand(string cmd, JsonObject request)
    {
        if (cmd != "script")
        {
            return null;
        }

        string action = request["action"]?.GetValue<string>() ?? "output";
        switch (action)
        {
            case "run":
            {
                ScriptRun run = ScriptingService.Start(new ScriptRunRequest { Path = request["path"]!.GetValue<string>() });
                return new JsonObject { ["id"] = run.Id.ToString("N") };
            }

            case "stopAll":
                ScriptingService.StopAll();
                return new JsonObject();
            case "running":
                return new JsonObject { ["count"] = ScriptingService.Host.Running.Count };
            default:
                return new JsonObject
                {
                    ["lines"] = new JsonArray([.. ScriptingService.Output.Lines.Select(l => (JsonNode?)new JsonObject
                    {
                        ["channel"] = l.Channel,
                        ["kind"] = l.Kind.ToString(),
                        ["text"] = l.Text,
                        ["line"] = l.Line,
                    })]),
                };
        }
    }
}
#endif
