#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Platform.Network;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令のつづき。
/// <list type="bullet">
/// <item><c>tplonline</c>: オンラインのテンプレート (07 の TPL-36)。<c>action</c> が <c>state</c> (既定。管理画面の「オンライン」の行・状態の表示・
/// 無効の表示)・<c>open</c> (「テンプレート: オンラインで探す」)・<c>wait</c> (読み込みの終わりを待つ)・<c>search</c> (<c>text</c>)・
/// <c>install</c> / <c>update</c> (<c>id</c>)・<c>requests</c> (アプリが行ったダウンロードの通信の数)。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleTplAutoTestCommands2Async(string cmd, JsonObject request) => cmd switch
    {
        "tplonline" => await TestOnlineTemplatesAsync(request),
        _ => await HandleTplAutoTestCommands3Async(cmd, request),
    };

    private async Task<JsonObject> TestOnlineTemplatesAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        Views.OnlineTemplateSection.Root? view = Views.OnlineTemplateSection.Current;
        switch (action)
        {
            case "open":
                OpenOnlineTemplates();
                return new JsonObject();
            case "wait":
                if (view?.LoadingTask is { } task)
                {
                    await task;
                }

                break;
            case "search":
                if (view is not null && Views.SettingsPage.FindByAutomationId(view, "TplOnline_Search") is Microsoft.UI.Xaml.Controls.TextBox box)
                {
                    box.Text = request["text"]!.GetValue<string>();
                }

                break;
            case "install":
            case "update":
            {
                string id = request["id"]!.GetValue<string>();
                if (view?.Listings.SelectMany(l => l.Templates.Select(t => (l, t))).Where(x => x.t.Id == id)
                    .OrderByDescending(x => x.t.Version, Comparer<string>.Create(Core.Templates.Online.TemplateVersion.Compare)).FirstOrDefault() is { l: { } listing, t: { } entry })
                {
                    return new JsonObject { ["ok"] = await view.InstallAsync(listing, entry) };
                }

                return new JsonObject { ["ok"] = false };
            }

            case "requests":
                return new JsonObject
                {
                    ["count"] = AppUpdates.Network.Requests.Count(r => r.Feature == NetworkFeature.Downloads),
                };
        }

        return new JsonObject
        {
            ["open"] = view is not null,
            ["disabled"] = view is not null && Views.SettingsPage.FindByAutomationId(view, "TplOnline_Disabled") is not null,
            ["status"] = view?.StatusText,
            ["folder"] = TemplateService.OnlineFolder,
            ["installed"] = new JsonArray([.. OnlineTemplateService.Store.Installed.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id,
                ["version"] = t.Version,
                ["file"] = t.File,
                ["unsigned"] = t.Unsigned,
            })]),
            ["rows"] = new JsonArray([.. (view?.RowsForTest ?? []).Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["description"] = r.Description,
                ["version"] = r.Version,
                ["license"] = r.License,
                ["unsigned"] = r.Unsigned,
                ["installed"] = r.Installed,
                ["update"] = r.Update,
            })]),
        };
    }

    /// <summary>
    /// <c>rpc</c>: 外部からの自動操作 (08 の AUTO-45)。<c>action</c> が <c>state</c> (既定。待ち受け・パイプの有無・ステータスバーのアイコン・接続の数)・
    /// <c>trust</c> (<c>path</c> の実行ファイルを「常に許可する」で記録する。テストの前提の「許可した記録を置いている」)・<c>allowed</c> (許可した一覧)。
    /// </summary>
    private Task<JsonObject?> HandleTplAutoTestCommands3Async(string cmd, JsonObject request)
    {
        if (cmd != "rpc")
        {
            return Task.FromResult<JsonObject?>(null);
        }

        string action = request["action"]?.GetValue<string>() ?? "state";
        switch (action)
        {
            case "trust":
                Core.Scripting.Rpc.JsonRpcTrust.Remember(Services.Scripting.ScriptingService.Trust, request["path"]!.GetValue<string>());
                break;
            case "allowed":
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["paths"] = new JsonArray([.. Core.Scripting.Rpc.JsonRpcTrust.Allowed(Services.Scripting.ScriptingService.Trust)
                        .Select(r => (JsonNode?)(r.DisplayPath ?? r.Key))]),
                });
        }

        string pipe = Services.Automation.AutomationService.PipeName;
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["running"] = Services.Automation.AutomationService.IsRunning,
            ["pipe"] = pipe,
            ["pipeExists"] = Platform.Automation.NamedPipeRpcListener.Exists(pipe),
            ["connections"] = Services.Automation.AutomationService.Connections,
            ["error"] = Services.Automation.AutomationService.StartError,
            ["statusVisible"] = StatusAutomation.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
            ["statusName"] = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(StatusAutomation),
        });
    }
}
#endif
