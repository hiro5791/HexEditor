using HexEditor.Sdk.Plugins;
using Microsoft.UI.Xaml;

namespace HexEditor.Sdk.UI;

/// <summary>
/// A panel added by a plugin (AUTO-31, 09 UI-05). The panel is docked like the application's own panels; the title and the close
/// button are provided by the dock. <see cref="CreatePanel"/> may be called again when the panel moves between docked and floating.
/// <ja>プラグインが追加するパネル (AUTO-31、09 の UI-05)。アプリのパネルと同じくドックに置く。見出しと閉じるボタンはドックが付ける。ドックと浮動の切り替えで <see cref="CreatePanel"/> がもう一度呼ばれることがある。</ja>
/// </summary>
public interface IPanelProvider : IExtension
{
    /// <summary>Creates the panel content (on the UI thread). <ja>パネルの中身を作る (UI スレッドで呼ばれる)。</ja></summary>
    UIElement CreatePanel();
}

/// <summary>The settings page of a plugin, opened from the plugin manager (AUTO-32 spec 4). <ja>プラグインの設定画面。プラグインマネージャから開く (AUTO-32 の仕様 4)。</ja></summary>
public interface IPluginSettingsProvider : IExtension
{
    /// <summary>Creates the settings content (on the UI thread). <ja>設定画面の中身を作る (UI スレッドで呼ばれる)。</ja></summary>
    UIElement CreateSettings();
}
