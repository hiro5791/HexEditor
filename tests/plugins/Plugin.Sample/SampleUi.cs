using HexEditor.Sdk.Plugins;
using HexEditor.Sdk.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Plugin.Sample;

/// <summary>パネルと設定画面 (WinUI。ウィンドウのあるアプリでだけ登録する)。</summary>
internal static class SampleUi
{
    public static void Register(IPluginContext context)
    {
        context.Register(new Panel(context.Id));
        context.Register(new Settings(context.Id));
    }

    private sealed class Panel(string p) : IPanelProvider
    {
        public string Id => p + ".panel";

        public string DisplayName => "Sample Panel";

        public UIElement CreatePanel()
        {
            var text = new TextBlock { Text = "Sample Panel content", Margin = new Thickness(12) };
            AutomationProperties.SetAutomationId(text, "SamplePanelText");
            return text;
        }
    }

    private sealed class Settings(string p) : IPluginSettingsProvider
    {
        public string Id => p + ".settings";

        public string DisplayName => "Sample settings";

        public UIElement CreateSettings()
        {
            var box = new CheckBox { Content = "Sample option" };
            AutomationProperties.SetAutomationId(box, "SampleSettingsOption");
            return box;
        }
    }
}
