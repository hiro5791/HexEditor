using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Sdk.Plugins;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Services.Plugins;

/// <summary>プラグインの確認の UI (08 の AUTO-34 の仕様 2・8)。UI スレッドで、作業中のウィンドウにダイアログを出す。</summary>
internal sealed class AppPluginPrompts(DispatcherQueue queue) : IPluginPrompts
{
    public Task<PluginTrustAnswer> ConfirmTrustAsync(PluginTrustPrompt prompt) => OnUi(w => w.ConfirmPluginTrustAsync(prompt), PluginTrustAnswer.DontEnable);

    public Task<bool> ConfirmElevatedLoadAsync(IReadOnlyList<PluginTrustPrompt> plugins) => OnUi(w => w.ConfirmElevatedPluginsAsync(plugins), false);

    private Task<T> OnUi<T>(Func<MainWindow, Task<T>> show, T fallback)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = queue.TryEnqueue(async () =>
        {
            try
            {
                done.TrySetResult(WindowManager.Windows.Count == 0 ? fallback : await show(WindowManager.Current));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 確認を出せなければ「有効にしない」(安全側)。
                AppLog.Warning($"Plugin confirmation failed: {ex.GetType().Name}");
                done.TrySetResult(fallback);
            }
        });
        if (!queued)
        {
            done.TrySetResult(fallback);
        }

        return done.Task;
    }
}

/// <summary>プラグインのパネルの中身 (AUTO-31 の IPanelProvider)。例外・止めたプラグインは理由の文字列にする。</summary>
internal static class PluginPanels
{
    public static FrameworkElement Create(RegisteredExtension registration)
    {
        if (registration.Extension is HexEditor.Sdk.UI.IPanelProvider provider)
        {
            UIElement? content = registration.Guard.Invoke("CreatePanel", provider.CreatePanel, null, uiThread: true);
            if (content is FrameworkElement element)
            {
                return element;
            }

            if (content is not null)
            {
                return new Microsoft.UI.Xaml.Controls.Border { Child = content };
            }
        }

        return new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = registration.Guard.IsStopped ? registration.Guard.Stopped() : Loc.Get("Plugin_PanelUnavailable"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12),
        };
    }

    /// <summary>プラグインの設定画面 (AUTO-32 の仕様 4)。なければ null。</summary>
    public static UIElement? Settings(PluginRecord plugin)
    {
        RegisteredExtension? registration = plugin.Extensions.FirstOrDefault(e => e.IsUsable && e.Kind == PluginExtensionKinds.Settings);
        return registration?.Extension is HexEditor.Sdk.UI.IPluginSettingsProvider provider
            ? registration.Guard.Invoke("CreateSettings", provider.CreateSettings, null, uiThread: true)
            : null;
    }
}
