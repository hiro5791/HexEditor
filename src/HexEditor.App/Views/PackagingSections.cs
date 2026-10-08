using HexEditor.App.Services;
using HexEditor.Core.Settings;
using HexEditor.Platform.Network;
using HexEditor.Platform.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>
/// 配布形態に関わる設定画面の専用の区画: Explorer 連携の登録の状態と登録・解除 (09 の UI-54 の仕様 5・7、UI-56)、
/// ネットワークを使う機能の一覧 (UI-58 の仕様 1・2)。各機能のスイッチは汎用の項目 (BuiltInSettings) で出す。
/// </summary>
public static class PackagingSections
{
    public static void Register()
    {
        SettingsSections.Register(new SettingsSection(SettingCategories.Explorer, "explorer", "SetSection_Explorer", Explorer)
        {
            SearchKeys = ["SetSearch_Explorer"],
        });
        SettingsSections.Register(new SettingsSection(SettingCategories.Privacy, "network", "SetSection_Network", Network)
        {
            SearchKeys = ["SetSearch_Network"],
        });

        // ポータブル版だけ: 「この PC から登録を解除」(10 の PKG-09 の仕様 4)。
        if (Hosting.Program.Environment.Distribution == Platform.Distribution.Portable)
        {
            SettingsSections.Register(new SettingsSection(SettingCategories.Advanced, "unregisterPc", "SetSection_UnregisterPc", UnregisterFromThisPc)
            {
                SearchKeys = ["SetSearch_UnregisterPc"],
            });
        }
    }

    /// <summary>
    /// 「この PC から登録を解除」: レジストリの登録、ジャンプリスト、トースト通知の登録、一時フォルダを消し、
    /// 「フォルダを削除すればアンインストールは完了です」を出す。失敗した項目は一覧で出す (PKG-09 の「エラー」)。
    /// </summary>
    private static FrameworkElement UnregisterFromThisPc(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Shell_UnregisterPcDescription"), TextWrapping = TextWrapping.Wrap });
        var button = new Button { Content = Loc.Get("Shell_UnregisterPcButton") };
        AutomationProperties.SetAutomationId(button, "Settings_UnregisterPc");
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(result, "Settings_UnregisterPcResult");
        AutomationProperties.SetLiveSetting(result, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        button.Click += (_, _) =>
        {
            (_, string message) = ExplorerIntegration.UnregisterFromThisPc();
            result.Text = message;
            result.Visibility = Visibility.Visible;
        };
        panel.Children.Add(button);
        panel.Children.Add(result);
        return panel;
    }

    /// <summary>Explorer 連携: 状態と「登録する」「登録を解除する」。MSIX 版・開発中の実行では「Windows の設定で管理されます」など。</summary>
    private static FrameworkElement Explorer(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "Settings_ExplorerStatus");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var other = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(other, "Settings_ExplorerOther");
        var register = new Button { Content = Loc.Get("Shell_Register") };
        AutomationProperties.SetAutomationId(register, "Settings_ExplorerRegister");
        var unregister = new Button { Content = Loc.Get("Shell_Unregister") };
        AutomationProperties.SetAutomationId(unregister, "Settings_ExplorerUnregister");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(register);
        buttons.Children.Add(unregister);
        panel.Children.Add(status);
        panel.Children.Add(other);
        panel.Children.Add(buttons);

        void Refresh()
        {
            ShellIntegrationState state;
            try
            {
                state = ExplorerIntegration.Shell.State();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Warning($"Explorer registration state unavailable: {ex.GetType().Name}");
                status.Text = Loc.Get("Shell_StateUnavailable");
                buttons.Visibility = Visibility.Collapsed;
                return;
            }

            if (!state.Supported)
            {
                status.Text = Loc.Get(Hosting.Program.Environment.Distribution == Platform.Distribution.Msix ? "Shell_ManagedByWindows" : "Shell_NotAvailable");
                buttons.Visibility = Visibility.Collapsed;
            }
            else
            {
                bool registered = state.ContextMenuRegistered || state.FileAssociationsRegistered;
                status.Text = Loc.Get(state.Stale ? "Shell_StaleRegistration" : registered ? "Shell_Registered" : "Shell_NotRegistered");
                buttons.Visibility = Visibility.Visible;
                unregister.IsEnabled = registered;
            }

            other.Text = Loc.Get("Shell_OtherDistribution");
            other.Visibility = state.OtherDistributionRegistered ? Visibility.Visible : Visibility.Collapsed;
        }

        register.Click += (_, _) =>
        {
            window.ReportShellFailures(ExplorerIntegration.Shell.Register());
            Refresh();
        };
        unregister.Click += (_, _) =>
        {
            window.ReportShellFailures(ExplorerIntegration.Shell.Unregister());
            Refresh();
        };
        Refresh();
        return panel;
    }

    /// <summary>ネットワークを使う機能の一覧 (UI-58 の仕様 1) と、対象外の機能の説明 (仕様 2)。</summary>
    private static FrameworkElement Network(MainWindow window)
    {
        var panel = new StackPanel { Spacing = 8 };
        var list = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(list, "Settings_NetworkFeatures");
        AutomationProperties.SetName(list, Loc.Get("SetSection_Network"));
        foreach ((NetworkFeature feature, string key, bool _) in NetworkPolicy.Features)
        {
            var row = new StackPanel { Spacing = 2 };
            AutomationProperties.SetAutomationId(row, "Settings_NetworkFeature_" + feature);
            row.Children.Add(new TextBlock
            {
                Text = Loc.Get("Network_Feature_" + feature),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            });
            row.Children.Add(new TextBlock
            {
                Text = Loc.Format("Network_FeatureDetail", Loc.Get("Network_Destination_" + feature), Loc.Get("Set_" + Core.Commands.CommandDefinition.KeyPart(key))),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            AutomationProperties.SetName(row, Loc.Get("Network_Feature_" + feature));
            list.Children.Add(row);
        }

        panel.Children.Add(list);
        var note = new TextBlock
        {
            Text = Loc.Get("Network_UserDestinations"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(note, "Settings_NetworkUserDestinations");
        panel.Children.Add(note);
        return panel;
    }
}
