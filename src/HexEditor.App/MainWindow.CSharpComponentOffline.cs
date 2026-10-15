using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting.CSharp;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// オフラインモード (または <c>network.downloads.enabled</c> がオフ) での C# スクリプト コンポーネントの追加 (09 の UI-58 の受け入れ基準 3、
/// 08 の AUTO-03): ダウンロードの確認を出さず、接続もせずに、手動で入れる手順 (zip の名前・入手先・展開先) を案内する。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ダウンロードできないなら手動で入れる手順を出して true (追加の処理はここで終わる)。</summary>
    private async Task<bool> ShowOfflineComponentInstructionsAsync()
    {
        CSharpComponentManager components = ScriptingService.Components;
        if (components.DownloadsAllowed())
        {
            return false;
        }

        string package = components.PackageName(Program.Environment.AppVersion, Program.Environment.ProcessArchitecture);
        ContentDialog dialog = NewDialog(Loc.Get("CSharpComponent_Title"),
            Loc.Format("CSharpComponent_OfflineInstructions", package, components.VersionFolder ?? components.Folder));
        dialog.CloseButtonText = Loc.Get("Common_Close");
        dialog.DefaultButton = ContentDialogButton.Close;
        AutomationProperties.SetAutomationId(dialog, "CSharpComponentOfflineDialog");
        AppLog.Info($"C# component: downloads are off; showing the manual steps for {package}");
        await dialog.ShowQueuedAsync();
        return true;
    }
}
