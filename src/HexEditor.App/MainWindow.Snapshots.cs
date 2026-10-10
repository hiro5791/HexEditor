using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// プロセスメモリのスナップショットと比較 (ANA-09)。「スナップショットを作成」は一時フォルダに <c>.hexsnap</c> を作って読み取り専用のタブで開き
/// (アプリの終了時に削除。仕様 1・9)、「スナップショットを比較」は比較のダイアログを開く (比較は領域ごと。<see cref="Core.Compare.RegionComparer"/>)。
/// スナップショットのタブの「名前を付けて保存」は <c>.hexsnap</c> を書き出し、一時ファイルの管理から外す。
/// </summary>
public sealed partial class MainWindow
{
    private static SnapshotManager? s_snapshots;

    /// <summary>この入口から作った一時スナップショット (アプリ全体で 1 つ)。</summary>
    internal static SnapshotManager Snapshots => s_snapshots ??= new SnapshotManager(Path.Combine(App.TempRoot, "snapshots"));

    /// <summary>アプリの終了時に、一時フォルダのスナップショットを削除する (ANA-09 の仕様 9)。</summary>
    internal static void DeleteTemporarySnapshots()
    {
        s_snapshots?.Dispose();
        s_snapshots = null;
    }

    private void RegisterSnapshotCommands()
    {
        Commands.Register("compare.createSnapshot", CreateSnapshotAsync,
            () => NeedsDocument(d => d.IsProcessMemory ? null : Loc.Get("Command_ProcessOnly")));
        Commands.Register("compare.snapshots", CompareSnapshotsAsync,
            () => Vm.Documents.Count(d => d.Snapshot is not null || d.IsProcessMemory) >= 2 || Vm.Documents.Any(d => d.Snapshot is not null)
                ? CommandState.Available : CommandState.Unavailable(Loc.Get("Snapshot_NoneToCompare")));
    }

    /// <summary>
    /// 「スナップショットを作成」(ANA-09 の仕様 1・4): 名前 (既定は「プロセス名 時刻」) を決め、一時フォルダに作って読み取り専用のタブで開く。
    /// </summary>
    private async Task CreateSnapshotAsync()
    {
        if (Vm.Selected is not { ProcessMemory: { } process } doc)
        {
            return;
        }

        string defaultName = $"{process.Memory.Name} {DateTime.Now:HH:mm:ss}";
        var name = new TextBox { Text = defaultName, Header = Loc.Get("Snapshot_Name") };
        AutomationProperties.SetAutomationId(name, "Snapshot_Name");
        string? chosen = await ConfirmAsync(Loc.Get("Snapshot_CreateTitle"), name, Loc.Get("Snapshot_Create"), "SnapshotCreateDialog")
            ? name.Text.Trim() : null;
        if (string.IsNullOrEmpty(chosen))
        {
            return;
        }

        try
        {
            SnapshotByteSource snapshot = await Vm.Operations.RunAsync(Loc.Format("Snapshot_Creating", chosen), Core.Operations.OperationKind.ReadOnly,
                doc.Document, null, op => Task.FromResult(Snapshots.CreateTemporary(process, chosen, operation: op)));
            DocumentViewModel opened = Vm.OpenSnapshotSource(snapshot);
            LastSnapshot = opened;
            AppLog.Info($"Snapshot created: {snapshot.Path}");
            if (snapshot.Metadata.UnreadablePages is { Count: > 0 } gaps)
            {
                ShowNotice(Loc.Format("Snapshot_Unreadable", gaps.Count, Size(gaps.Sum(g => g.Size))), InfoBarSeverity.Informational, opened);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("Snapshot_Error", ex.Message), InfoBarSeverity.Error, doc);
        }

        UpdateTitle();
    }

    /// <summary>最後に作ったスナップショットのタブ (テスト用)。</summary>
    internal DocumentViewModel? LastSnapshot { get; private set; }

    /// <summary>
    /// 「スナップショットを比較」(ANA-09 の仕様 3・6): 比較のダイアログを、左に最も古いスナップショット、右に最も新しいスナップショット (なければ
    /// プロセスメモリのタブ) を選んで開く。
    /// </summary>
    private Task CompareSnapshotsAsync()
    {
        List<DocumentViewModel> snapshots = [.. Vm.Documents.Where(d => d.Snapshot is not null)];
        DocumentViewModel? left = snapshots.FirstOrDefault();
        DocumentViewModel? right = snapshots.Count > 1 ? snapshots[^1] : Vm.Documents.FirstOrDefault(d => d.IsProcessMemory);
        return ShowCompareDialogAsync(left, right);
    }

    /// <summary>
    /// スナップショットのタブの「名前を付けて保存」(ANA-09 の仕様 1・9、ENG-35 の仕様 3): <c>.hexsnap</c> をそのまま書き出す
    /// (アドレス空間全体をバイト列として保存しない)。一時スナップショットは管理から外し、終了時に消さない。
    /// </summary>
    private async Task<bool> SaveSnapshotAsAsync(DocumentViewModel doc, SnapshotByteSource snapshot)
    {
        string suggested = string.Concat(doc.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + HexSnapshot.Extension;
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.SaveSnapshot" };
            picker.FileTypeChoices.Add(Loc.Get("FileType_Snapshot"), [HexSnapshot.Extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return false;
        }

        try
        {
            string target = Path.GetFullPath(path);
            string temp = target + ".tmp";
            await Task.Run(() =>
            {
                File.Copy(snapshot.Path, temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            });
            // 書き出したファイルは残る。タブが読んでいる一時ファイルは、終了時に削除する (ANA-09 の仕様 9)。
            ShowStatusMessage(Loc.Format("Snapshot_Saved", Path.GetFileName(target)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Snapshot_Error", ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }
    }
}
