using System.Text.Json;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Files;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// ワークスペース (UI-33): 現在のウィンドウのタブとパネルの配置に名前を付けて保存し (<c>.hexworkspace</c>)、後で開き直す。ファイルのパスは
/// ワークスペースファイルからの相対パスで保存する。ブックマークなどの付随データは含めない。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterWorkspaceCommands()
    {
        Commands.Register("workspace.save", SaveWorkspaceAsync, NeedsDocument);
        Commands.Register("workspace.open", async () =>
        {
            if (await PickOneFileAsync("HexEditor.Workspace") is { } path)
            {
                await OpenWorkspaceAsync(path);
            }
        });
    }

    private async Task SaveWorkspaceAsync()
    {
        const string Extension = WorkspaceFile.Extension;
        string suggested = "workspace" + Extension;
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.Workspace" };
            picker.FileTypeChoices.Add(Loc.Get("Workspace_FileType"), [Extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            WorkspaceFile.FromWindow(CaptureSessionWindow(), path).Save(path);
            ShowStatusMessage(Loc.Format("Workspace_Saved", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Workspace_SaveFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// ワークスペースを開く (仕様 2): 現在のウィンドウにタブを追加するか、新しいウィンドウで開くかを選ぶ (既定は新しいウィンドウ)。タブは UI-31 と同じく
    /// 遅延して開き、見つからないファイルは UI-31 と同じ表示にする。
    /// </summary>
    internal async Task OpenWorkspaceAsync(string path)
    {
        WorkspaceFile workspace;
        try
        {
            workspace = WorkspaceFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowNotice(Loc.Format("Workspace_OpenFailed", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
            return;
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WorkspaceOpenDialog", Loc.Get("Workspace_OpenTitle"),
            new TextBlock { Text = Loc.Format("Workspace_OpenBody", Path.GetFileName(path), workspace.Tabs.Count), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            Loc.Get("Workspace_NewWindow"), Loc.Get("Workspace_ThisWindow"));
        ContentDialogResult answer = await dialog.ShowQueuedAsync();
        if (answer == ContentDialogResult.None)
        {
            return;
        }

        SessionWindow window = workspace.ToWindow(path);
        MainWindow target = answer == ContentDialogResult.Primary ? WindowManager.CreateWindow(this) : this;
        target.RestoreWindowTabs(window, window);
        AppLog.Info($"Workspace opened: {Path.GetFileName(path)} ({window.Tabs.Count} tab(s))");
    }
}
