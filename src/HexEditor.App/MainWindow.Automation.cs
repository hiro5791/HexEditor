using HexEditor.App.Services;
using HexEditor.App.Services.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 外部からの自動操作 (08 の AUTO-45) のウィンドウの部分: ステータスバーの「外部からの自動操作: 有効」のアイコン (仕様 3。オンの間は隠せない)。
/// クリックで設定の「外部からの自動操作」を開く。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterAutomationFeatures()
    {
        EventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(UpdateAutomationStatus);
        AutomationService.StateChanged += changed;
        Closed += (_, _) => AutomationService.StateChanged -= changed;
        StatusAutomation.Click += (_, _) => OpenSettingsPage(Core.Settings.SettingCategories.Automation, AutomationService.EnabledKey);
        UpdateAutomationStatus();
    }

    private void UpdateAutomationStatus()
    {
        bool running = AutomationService.IsRunning;
        string text = running ? Loc.Get("Status_AutomationOn") : string.Empty;
        int connections = AutomationService.Connections;
        if (running && connections > 0)
        {
            text += " " + Loc.Format("Status_AutomationConnections", connections);
        }

        ToolTipService.SetToolTip(StatusAutomation, running ? text : null);
        AutomationProperties.SetName(StatusAutomation, text);
        UpdateStatusBarLayout();
    }
}
