using HexEditor.App.Services;
using HexEditor.App.Services.Automation;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Rpc;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面「外部からの自動操作」(08 の AUTO-45 の「画面」): 待ち受けの状態とパイプ名、同梱の PowerShell モジュールとメソッド一覧の場所、
/// 許可した実行ファイルの一覧 (取り消せる。仕様 4)。オン・オフは設定の項目 <c>automation.rpc.enabled</c>。
/// </summary>
public static class AutomationSections
{
    public static void Register()
    {
        if (SettingsSections.All.All(s => s.Id != "rpcClients"))
        {
            SettingsSections.Register(new SettingsSection(SettingCategories.Automation, "rpcClients", "SetSection_RpcClients", Create, 60)
            {
                SearchKeys = ["SetSearch_RpcClients"],
            });
        }
    }

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static FrameworkElement Create(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        AutomationProperties.SetAutomationId(panel, "RpcClients");
        TextBlock state = Secondary(string.Empty);
        AutomationProperties.SetAutomationId(state, "RpcClients_State");
        panel.Children.Add(state);
        TextBlock pipe = Secondary(@"\\.\pipe\" + AutomationService.PipeName);
        pipe.FontFamily = Controls.DialogParts.Mono;
        pipe.FlowDirection = FlowDirection.LeftToRight;
        pipe.IsTextSelectionEnabled = true;
        AutomationProperties.SetAutomationId(pipe, "RpcClients_Pipe");
        panel.Children.Add(pipe);
        string module = Path.Combine(AppContext.BaseDirectory, "Automation", "HexEditor.Automation");
        TextBlock help = Secondary(Loc.Format("RpcClients_Module", module, Path.Combine(AppContext.BaseDirectory, "Automation", "jsonrpc-methods.json")));
        help.IsTextSelectionEnabled = true;
        panel.Children.Add(help);

        var rows = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(rows, "RpcClients_List");
        panel.Children.Add(rows);
        void Render()
        {
            state.Text = AutomationService.IsRunning ? Loc.Get("RpcClients_Running")
                : AutomationService.StartError is { } error ? Loc.Format("RpcClients_StartError", error)
                : Loc.Get("RpcClients_Stopped");
            rows.Children.Clear();
            IReadOnlyList<TrustRecord> records = JsonRpcTrust.Allowed(ScriptingService.Trust);
            if (records.Count == 0)
            {
                TextBlock none = Secondary(Loc.Get("RpcClients_None"));
                AutomationProperties.SetAutomationId(none, "RpcClients_None");
                rows.Children.Add(none);
            }

            for (int i = 0; i < records.Count; i++)
            {
                TrustRecord record = records[i];
                string path = record.DisplayPath ?? record.Key;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                AutomationProperties.SetAutomationId(row, "RpcClients_Row_" + i);
                var text = new StackPanel { Spacing = 2 };
                text.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
                TextBlock location = Secondary(path);
                location.FontFamily = Controls.DialogParts.Mono;
                location.FlowDirection = FlowDirection.LeftToRight;
                text.Children.Add(location);
                text.Children.Add(Secondary(Loc.Format("RpcClients_Allowed", record.Time.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture))));
                row.Children.Add(text);
                var revoke = new Button { Content = Loc.Get("TrustList_Revoke"), VerticalAlignment = VerticalAlignment.Center };
                AutomationProperties.SetAutomationId(revoke, "RpcClients_Revoke_" + i);
                AutomationProperties.SetName(revoke, Loc.Format("TrustList_RevokeName", Path.GetFileName(path)));
                revoke.Click += (_, _) =>
                {
                    ScriptingService.Trust.Revoke(record.Kind, record.Key);
                    Render();
                };
                Grid.SetColumn(revoke, 1);
                row.Children.Add(revoke);
                rows.Children.Add(row);
            }
        }

        Render();
        EventHandler changed = (_, _) => panel.DispatcherQueue.TryEnqueue(Render);
        AutomationService.StateChanged += changed;
        ScriptingService.Trust.Changed += changed;
        panel.Unloaded += (_, _) =>
        {
            AutomationService.StateChanged -= changed;
            ScriptingService.Trust.Changed -= changed;
        };
        return panel;
    }
}
