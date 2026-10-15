#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services.Plugins;
using HexEditor.App.ViewModels;
using HexEditor.Core.Plugins;

namespace HexEditor.App;

/// <summary>プラグイン (08 の AUTO-29〜AUTO-35) のテスト用の命令 ("plugins")。プラグインマネージャの状態を読み、操作する。</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// "plugins": {action: list / waitLoaded / openManager / install (path。一覧へのドロップと同じ処理) / select (index) / manager / menus}。
    /// </summary>
    private async Task<JsonObject?> HandlePluginTestCommandsAsync(string cmd, JsonObject request)
    {
        if (cmd != "plugins")
        {
            return null;
        }

        string action = request["action"]!.GetValue<string>();
        PluginManager? m = PluginService.Manager;
        switch (action)
        {
            case "waitLoaded":
                if (m is not null)
                {
                    await m.Loaded;
                }

                return new JsonObject { ["loaded"] = m is not null };
            case "list":
                return new JsonObject
                {
                    ["loaded"] = m?.Loaded.IsCompleted ?? false,
                    ["safeMode"] = PluginService.SafeMode,
                    ["restartRequired"] = PluginService.RestartRequired,
                    ["folder"] = PluginService.PluginsFolder,
                    ["lastOpenedFolder"] = PluginService.LastOpenedFolder,
                    ["plugins"] = new JsonArray([.. (m?.Plugins ?? []).Select(p => (JsonNode?)new JsonObject
                    {
                        ["id"] = p.Id,
                        ["name"] = p.NameFor(PluginService.UiLanguage),
                        ["version"] = p.Version,
                        ["state"] = p.State.ToString(),
                        ["reason"] = p.Reason,
                        ["signature"] = p.Signature.State.ToString(),
                        ["publisher"] = p.Signature.Publisher,
                        ["development"] = p.IsDevelopment,
                        ["assemblyLoaded"] = p.AssemblyLoaded,
                        ["stateText"] = new PluginItemViewModel(p).StateText,
                        ["extensions"] = new JsonArray([.. p.Extensions.Select(e => (JsonNode?)new JsonObject
                        {
                            ["id"] = e.Id,
                            ["kind"] = e.Kind,
                            ["status"] = e.Status.ToString(),
                        })]),
                    })]),
                    ["commands"] = new JsonArray([.. PluginService.Commands.Keys.Select(k => (JsonNode?)k)]),
                };
            case "openManager":
                OpenPluginManager();
                return new JsonObject();
            case "install":
            {
                // 一覧へのドロップ (OLE のドロップ) と同じ処理を呼ぶ (ドロップの操作は UI オートメーションで行えないため)。
                OpenPluginManager();
                await _pluginManagerVm!.InstallAsync(request["path"]!.GetValue<string>());
                return new JsonObject { ["message"] = _pluginManagerVm.Message, ["error"] = _pluginManagerVm.MessageIsError };
            }

            case "select":
                OpenPluginManager();
                _pluginManagerVm!.Selected = _pluginManagerVm.Items[request["index"]!.GetValue<int>()];
                return new JsonObject();
            case "manager":
            {
                PluginManagerViewModel? vm = _pluginManagerVm;
                return new JsonObject
                {
                    ["open"] = vm is not null && ToolTabIds.Contains(PluginManagerPageId),
                    ["selected"] = vm?.Selected?.Id,
                    ["selectedEnabled"] = vm?.SelectedEnabled,
                    ["restartVisible"] = vm?.RestartRequired,
                    ["message"] = vm?.Message,
                    ["items"] = new JsonArray([.. (vm?.Items ?? []).Select(i => (JsonNode?)new JsonObject { ["id"] = i.Id, ["state"] = i.StateText, ["signature"] = i.SignatureText })]),
                };
            }

            case "menus":
                RebuildPluginMenus();
                return new JsonObject
                {
                    ["tools"] = new JsonArray([.. PluginsMenu.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().Select(i => (JsonNode?)new JsonObject
                    {
                        ["text"] = i.Text,
                        ["toolTip"] = Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(i) as string,
                    })]),
                    ["sources"] = new JsonArray([.. PluginSourcesMenu.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().Select(i => (JsonNode?)i.Text)]),
                    ["data"] = new JsonArray([.. PluginDataMenu.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().Select(i => (JsonNode?)i.Text)]),
                };
            default:
                return null;
        }
    }
}
#endif
