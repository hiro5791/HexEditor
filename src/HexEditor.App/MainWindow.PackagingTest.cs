#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Platform.Migration;
using HexEditor.Platform.Network;
using HexEditor.Platform.Shell;
using HexEditor.Platform.Updates;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 配布・更新・国際化の機能のテスト用の命令 (テスト方針 7.2): 更新の確認と InfoBar、通信の記録、ジャンプリストの内容、
/// Explorer 連携の登録、メニューの項目の状態、表示言語。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandlePackagingTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        // 更新の確認 (手動・自動)。結果と InfoBar の内容を返す。
        "updateCheck" => await TestUpdateCheckAsync(request["manual"]?.GetValue<bool>() ?? true),
        "updateState" => TestUpdateState(),
        "updateButton" => TestUpdateButton(request["button"]!.GetValue<string>()),

        // 「アプリの時刻を進める」: 更新の自動の確認の時計を進める (PKG-17 のテスト)。
        "advanceUpdateClock" => Run(() => PackagingTestHooks.UpdateClockOffset += TimeSpan.FromHours(request["hours"]!.GetValue<double>())),

        // アプリが行った通信の記録 (UI-58 のテスト。ループバック以外への接続がないこと)。
        "networkLog" => new JsonObject
        {
            ["requests"] = new JsonArray([.. AppUpdates.Network.Requests.Select(r => (JsonNode?)new JsonObject
            {
                ["feature"] = r.Feature.ToString(),
                ["method"] = r.Method,
                ["host"] = r.Host,
                ["path"] = r.Path,
            })]),
        },

        // ジャンプリストの内容 (UI-35)。
        "jumpList" => new JsonObject
        {
            ["items"] = new JsonArray([.. (_jumpList?.LastPlan ?? []).Select(i => (JsonNode?)new JsonObject
            {
                ["category"] = i.Category.ToString(),
                ["title"] = i.Title,
                ["arguments"] = i.Arguments,
                ["path"] = i.FilePath,
            })]),
        },
        "jumpListNow" => Run(() => _jumpList?.Update()),

        // メニューの項目の状態 (有効・無効、理由、ツールチップ)。
        "menuItem" => TestMenuItem(request["id"]!.GetValue<string>()),

        // Explorer 連携 (配布のテスト。インストーラ版・ポータブル版の本物のレジストリに書く)。
        "shell" => TestShell(request["action"]?.GetValue<string>() ?? "state"),

        // 他の配布形態の設定の取り込み (PKG-31)。
        "importFromOtherDistribution" => TestImportSettings(),

        "displayLanguage" => new JsonObject
        {
            ["language"] = Localization.CurrentLanguage,
            ["windows"] = new JsonArray([.. Localization.WindowsLanguages().Select(l => (JsonNode?)l)]),
            ["items"] = new JsonArray([.. DisplayLanguageItems().Select(i => (JsonNode?)new JsonObject { ["tag"] = i.Tag, ["text"] = i.Text })]),
        },
        "setDisplayLanguage" => Run(() => SetDisplayLanguage(request["language"]!.GetValue<string>())),

        // 通知 (UI-36) の操作ボタンを押す (InfoBar の「今すぐ再起動」など)。
        "noticeAction" => TestNoticeAction(request["label"]!.GetValue<string>()),
        _ => null,
    };

    private async Task<JsonObject> TestUpdateCheckAsync(bool manual)
    {
        await CheckForUpdatesAsync(manual);
        return TestUpdateState();
    }

    private JsonObject TestUpdateState()
    {
        UpdateService service = AppUpdates.Service;
        var buttons = new JsonArray();
        foreach ((UpdateButton kind, Button button) in UpdateBar.Buttons)
        {
            buttons.Add(new JsonObject
            {
                ["kind"] = kind.ToString(),
                ["text"] = button.Content?.ToString(),
                ["enabled"] = button.IsEnabled,
                ["id"] = AutomationProperties.GetAutomationId(button),
            });
        }

        return new JsonObject
        {
            ["kind"] = service.Kind.ToString(),
            ["phase"] = service.Phase.ToString(),
            ["current"] = service.Current.SemVer,
            ["offer"] = service.Offer?.Version.SemVer,
            ["lastChecked"] = service.LastChecked?.ToString("O"),
            ["nextCheck"] = service.NextAutomaticCheck.ToString("O"),
            ["now"] = AppUpdates.Now().ToString("O"),
            ["barVisible"] = UpdateBar.Current is not null,
            ["messageKey"] = UpdateBar.Current?.MessageKey,
            ["message"] = UpdateBar.Current is null ? null : UpdateBar.MessageText,
            ["reason"] = UpdateBar.ReasonText,
            ["buttons"] = buttons,
        };
    }

    private JsonObject TestUpdateButton(string button)
    {
        UpdateButton kind = Enum.Parse<UpdateButton>(button, ignoreCase: true);
        if (!UpdateBar.Buttons.TryGetValue(kind, out Button? b))
        {
            throw new InvalidOperationException($"The update bar has no {kind} button.");
        }

        if (!b.IsEnabled)
        {
            throw new InvalidOperationException($"The {kind} button is disabled.");
        }

        OnUpdateButton(kind);
        return new JsonObject();
    }

    private JsonObject TestNoticeAction(string label)
    {
        foreach (Core.Notifications.Notification notification in Vm.Notifications.Open)
        {
            if (notification.Actions.FirstOrDefault(a => a.Label == label) is { } action)
            {
                // 押した処理 (再起動など) は、この命令の答えを返した後に行う (答えの前にプロセスが終わらないように)。
                Vm.Notifications.Dismiss(notification);
                DispatcherQueue.TryEnqueue(() => action.Execute());
                return new JsonObject { ["message"] = notification.Message };
            }
        }

        throw new InvalidOperationException($"No notification has the button '{label}'.");
    }

    private JsonObject TestMenuItem(string id)
    {
        MenuFlyoutItem item = FindMenuItem(id) ?? throw new ArgumentException($"No menu item {id}.");
        return new JsonObject
        {
            ["text"] = item.Text,
            ["enabled"] = item.IsEnabled,
            ["checked"] = item is ToggleMenuFlyoutItem toggle ? toggle.IsChecked : null,
            ["reason"] = AutomationProperties.GetHelpText(item),
            ["acceleratorText"] = item.KeyboardAcceleratorTextOverride,
            ["toolTip"] = ToolTipService.GetToolTip(item)?.ToString(),
        };
    }

    private static JsonObject TestShell(string action)
    {
        ShellIntegration shell = ExplorerIntegration.Shell;
        IReadOnlyList<string> failures = action switch
        {
            "register" => shell.Register(),
            "unregister" => shell.Unregister(),
            _ => [],
        };
        ShellIntegrationState state = shell.State();
        return new JsonObject
        {
            ["supported"] = state.Supported,
            ["contextMenu"] = state.ContextMenuRegistered,
            ["fileAssociations"] = state.FileAssociationsRegistered,
            ["stale"] = state.Stale,
            ["other"] = state.OtherDistributionRegistered,
            ["registeredExe"] = state.RegisteredExe,
            ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode?)f)]),
        };
    }

    private JsonObject TestImportSettings()
    {
        IAppEnvironment env = Program.Environment;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IReadOnlyList<OtherDistributionData> others = SettingsMigration.Find(env.Distribution, env.Locations.Settings, localAppData);
        var buttons = new JsonArray();
        if (StartPage.Content is StackPanel page)
        {
            foreach (Button b in page.Children.OfType<Button>().Where(b => AutomationProperties.GetAutomationId(b).StartsWith("Start_ImportFrom_", StringComparison.Ordinal)))
            {
                buttons.Add(new JsonObject { ["id"] = AutomationProperties.GetAutomationId(b), ["text"] = b.Content?.ToString() });
            }
        }

        return new JsonObject
        {
            ["firstRun"] = _firstRun,
            ["others"] = new JsonArray([.. others.Select(o => (JsonNode?)new JsonObject { ["distribution"] = o.Distribution.ToString(), ["folder"] = o.Folder })]),
            ["buttons"] = buttons,
        };
    }
}
#endif
