using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace HexEditor.App;

/// <summary>
/// ファイルのドロップで開く (UI-34、ENG-12)。エディタ領域・空のウィンドウへのドロップは末尾に、タブ列へのドロップは
/// ドロップした位置にタブを開く。フォルダは開かない。21 個以上は確認する。
/// </summary>
public sealed partial class MainWindow
{
    public const int ConfirmDropCount = 21;

    /// <summary>管理者として実行中のドロップの案内を出した (起動ごとに 1 回。ウィンドウを増やしても出し直さない)。</summary>
    private static bool _adminDropNoticeShown;

    private void InitializeDragDrop()
    {
        Root.AllowDrop = true;
        Root.DragOver += Root_DragOver;
        Root.Drop += async (_, e) => await DropAsync(e, insertAt: null);
        Tabs.AllowDropTabs = true;
        Tabs.TabStripDragOver += Root_DragOver;
        Tabs.TabStripDrop += async (_, e) =>
        {
            e.Handled = true;
            await DropAsync(e, TabInsertIndex(e));
        };
    }

    /// <summary>
    /// 管理者として実行中は、エクスプローラーからのドロップを Windows が拒否する (UIPI)。拒否を検出できないため、
    /// 起動したときに一度だけ知らせる (UI-34 の仕様 7 の 2)。
    /// </summary>
    private void ShowAdminDropNoticeOnce()
    {
        // テスト用の仕組みが有効なとき (CI のランナーは管理者として動く) は、設定 adminDropNotice のときだけ出す。ほかの通知を確かめるテストの
        // 邪魔になるため。
        if (Program.Environment.IsElevated && !_adminDropNoticeShown && TestHooks.AllowsAdminDropNotice)
        {
            _adminDropNoticeShown = true;
            ShowNotice(Loc.Get("Drop_AdminLimited"), InfoBarSeverity.Informational);
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // Ctrl を押しながら Hex ビューの上では「挿入」(MainWindow.DropInsert.cs)。
            (DataPackageOperation operation, string? caption) = DropFeedback(sender != Tabs && InsertDropOffset(e) is not null);
            e.AcceptedOperation = operation;
            e.DragUIOverride.Caption = caption ?? string.Empty;
            e.DragUIOverride.IsCaptionVisible = caption is not null;
            e.Handled = true;
        }
    }

    /// <summary>タブ列のドロップ位置から、タブを挿入する位置を求める (タブの中央より左なら前に入れる)。</summary>
    private int TabInsertIndex(DragEventArgs e)
    {
        for (int i = 0; i < Vm.Documents.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is TabViewItem item)
            {
                double x = e.GetPosition(item).X;
                if (x < item.ActualWidth / 2)
                {
                    return i;
                }
            }
        }

        return Vm.Documents.Count;
    }

    private async Task DropAsync(DragEventArgs e, int? insertAt)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        long? insertOffset = insertAt is null ? InsertDropOffset(e) : null;
        DragOperationDeferral deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }

        // Shift を押しながら 2 つのファイルをドロップ: その 2 つで比較ダイアログを開く (ANA-01 の仕様 10。MainWindow.Compare.cs)。
        if ((e.Modifiers & Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) != 0 && await TryDropCompareAsync(items))
        {
            return;
        }

        if (insertOffset is { } offset)
        {
            await InsertDroppedFileAsync(items, offset);
            return;
        }

        await DropItemsAsync(items, insertAt);
    }

    /// <summary>
    /// ドロップされた項目を開く。<paramref name="insertAt"/> はタブを挿入する位置 (null なら末尾)。テスト用の命令の通り道からも
    /// 同じ処理を呼ぶ (Explorer からの実際のドラッグはマウスを使うため)。
    /// </summary>
    internal async Task DropItemsAsync(IReadOnlyList<IStorageItem> items, int? insertAt)
    {
        var files = items.OfType<StorageFile>().ToList();
        int folders = items.Count - files.Count;
        if (folders > 0)
        {
            // フォルダは開かない (UI-34 の仕様 4)。複数ファイル検索 (フェーズ 2) ができたらボタンを付ける。
            ShowNotice(Loc.Format("Drop_FoldersSkipped", folders), InfoBarSeverity.Warning);
        }

        if (files.Count >= ConfirmDropCount && !await ConfirmOpenManyAsync(files.Count))
        {
            return;
        }

        int? position = insertAt;
        foreach (StorageFile file in files)
        {
            if (await OpenDroppedAsync(file, position) && position is int p)
            {
                position = p + 1;
            }
        }

        UpdateTitle();
    }

    private async Task<bool> ConfirmOpenManyAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Format("Drop_ConfirmMany", count),
            PrimaryButtonText = Loc.Get("Drop_OpenButton"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "DropConfirmDialog");
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>1 つのファイルを開く。開けなければそのファイルだけエラーを出し、他のファイルは続ける。</summary>
    private async Task<bool> OpenDroppedAsync(StorageFile file, int? insertAt)
    {
        try
        {
            if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
            {
                return TryOpen(file.Path, insertAt) is not null;
            }

            // パスを持たない項目は一時ファイルにコピーしてから無題として開く (ENG-12 の仕様 2)。コピーは長時間処理。
            string folder = Path.Combine(App.TempRoot, "dropped", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, string.Concat(file.Name.Split(Path.GetInvalidFileNameChars())));
            ulong size = (await file.GetBasicPropertiesAsync()).Size;
            await Vm.Operations.RunAsync(
                Loc.Format("Operation_Copy", file.Name),
                OperationKind.ReadOnly,
                null,
                (long)size,
                async op =>
                {
                    using IRandomAccessStreamWithContentType input = await file.OpenReadAsync();
                    await using Stream source = input.AsStreamForRead();
                    await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write);
                    byte[] buffer = new byte[1024 * 1024];
                    long done = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, op.CancellationToken)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, n), op.CancellationToken);
                        done += n;
                        op.Report(done);
                    }

                    return true;
                });
            Vm.OpenTemporaryCopy(temp, file.Name, insertAt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowNotice(Loc.Format("Error_Open", file.Name, ex.Message), InfoBarSeverity.Error);
            return false;
        }
    }
}
