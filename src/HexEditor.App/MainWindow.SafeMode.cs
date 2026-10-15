using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Core.Notifications;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// セーフモード (09 の UI-30 の仕様 4、08 の AUTO-30): 最初のウィンドウに、プラグインと自動実行のスクリプトを読み込んでいないことと
/// 「通常どおり再起動」のボタンを InfoBar で出す。タイトルの「(セーフモード)」は UI-02 (MainWindow.xaml.cs)。
/// </summary>
public sealed partial class MainWindow
{
    private static bool s_safeModeNoticeShown;

    /// <summary>起動時の通知の後に呼ぶ (<see cref="ContinueStartupAfterRecovery"/> の後)。セーフモードでなければ何もしない。</summary>
    private void ShowSafeModeNotice()
    {
        if (!Program.CommandLine.SafeMode || s_safeModeNoticeShown)
        {
            return;
        }

        s_safeModeNoticeShown = true;
        ShowNotice(Loc.Get("SafeMode_Notice"), InfoBarSeverity.Warning, actions:
        [
            new NotificationAction(Loc.Get("SafeMode_RestartNormally"), () => _ = RestartNormallyAsync()),
        ]);
    }

    /// <summary>「通常どおり再起動」: セッションを保存して、セーフモードを外して再起動する。</summary>
    private async Task RestartNormallyAsync()
    {
        AppRestart.LeaveSafeMode = true;
        if (!await WindowManager.RestartAsync(this))
        {
            AppRestart.LeaveSafeMode = false;
        }
    }
}
