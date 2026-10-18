using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// 分岐する Undo 履歴 (EDIT-21): 履歴の保存 (<c>.hexhist</c>) と読み込み、名前の付いた節への移動、節の名前付けと枝の削除、閉じるときの確認。
/// 木の構造と保存の形式は Core の <see cref="EditHistory"/>・<see cref="HistoryArchive"/>。
/// </summary>
public sealed partial class MainWindow : IHistoryPanelHost
{
    /// <summary>テスト用: 開いている「履歴の節へ移動」の候補。</summary>
    private Func<JsonObject>? _historyDialogState;

    internal JsonObject HistoryDialogTestModel() => _historyDialogState?.Invoke() ?? new JsonObject { ["open"] = false };

    private void RegisterBranchingHistoryCommands()
    {
        string? Linked(DocumentViewModel d) => d.Document.LinkedParent is not null ? Loc.Get("History_LinkedView") : null;
        Commands.Register("edit.history.save", ((IHistoryPanelHost)this).SaveHistoryAsync, () => NeedsDocument(Linked));
        Commands.Register("edit.history.load", ((IHistoryPanelHost)this).LoadHistoryAsync, () => NeedsEditable(Linked));
        Commands.Register("edit.history.goToNode", GoToNamedNodeAsync,
            () => NeedsEditable(d => !d.Document.History.HasNamedNodes ? Loc.Get("History_NoNamedNodes") : null));
    }

    // ---- 履歴の保存 (仕様 8) ----

    async Task IHistoryPanelHost.SaveHistoryAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        await SaveHistoryAsync(doc);
    }

    /// <summary>履歴を保存する。保存前に推定サイズを示し、全体の SHA-256 を入れるかを選ばせる。保存したら true。</summary>
    private async Task<bool> SaveHistoryAsync(DocumentViewModel doc)
    {
        long estimate = HistoryArchive.EstimateSize(doc.Document);
        var fullHash = DialogParts.Check("HistorySave_FullHash", Loc.Get("HistorySave_FullHash"), false);
        var pathBox = DialogParts.Field("HistorySave_Path", Loc.Get("HistorySave_Path"),
            doc.FilePath is { } own ? own + HistoryArchive.Extension : string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("PatchCreate_Browse"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(browse, "HistorySave_Browse");
        TextBlock size = DialogParts.Caption("HistorySave_Estimate");
        size.Text = Loc.Format("HistorySave_EstimateText", Size(estimate), doc.Document.History.NodeCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
        var content = new StackPanel { Spacing = 8, MinWidth = 440 };
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pathBox, browse } });
        content.Children.Add(fullHash);
        content.Children.Add(size);
        ContentDialog dialog = DialogParts.Dialog(Root, "HistorySaveDialog", Loc.Get("HistorySave_Title"), content, Loc.Get("HistorySave_Save"));
        void Validate() => dialog.IsPrimaryButtonEnabled = pathBox.Text.Trim().Length > 0;
        pathBox.TextChanged += (_, _) => Validate();
        browse.Click += async (_, _) =>
        {
            string suggested = doc.DisplayName + HistoryArchive.Extension;
            if (TestHooks.TrySavePicker(suggested, out string? chosen))
            {
                pathBox.Text = chosen ?? pathBox.Text;
                return;
            }

            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.HistorySave" };
            picker.FileTypeChoices.Add(Loc.Get("HistorySave_FileType"), [HistoryArchive.Extension]);
            if ((await picker.PickSaveFileAsync())?.Path is { } p)
            {
                pathBox.Text = p;
            }
        };
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        string path = pathBox.Text.Trim();

        // 書き出し先の容量不足は保存前に確かめる (EDIT-21 の「エラー」)。
        if (FreeSpace(path) is long free && free < estimate)
        {
            ShowNotice(Loc.Format("HistorySave_NoSpace", Size(estimate), Size(free)), InfoBarSeverity.Error, doc);
            return false;
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        bool full = fullHash.IsChecked == true;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("HistorySave_Operation"), OperationKind.WritesExternal, null, estimate, op => Task.Run(() =>
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                {
                    HistoryArchive.Save(doc.Document, output, full, op.Report, op.CancellationToken);
                }

                File.Move(temp, path, overwrite: true);
                return true;
            }));
            ShowStatusMessage(Loc.Format("HistorySave_Done", Path.GetFileName(path)));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(temp);
            ShowNotice(Loc.Format("HistorySave_Failed", ex.Message), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    private static long? FreeSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- 履歴の読み込み (仕様 9) ----

    async Task IHistoryPanelHost.LoadHistoryAsync()
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        string? path;
        if (TestHooks.OpenPickerResult("HexEditor.HistoryLoad") is { } chosen)
        {
            path = chosen.FirstOrDefault();
        }
        else
        {
            var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.HistoryLoad" };
            picker.FileTypeFilter.Add(HistoryArchive.Extension);
            picker.FileTypeFilter.Add("*");
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            // 識別情報の確認 (先頭・中央・末尾の 1 MiB。全体の SHA-256 があれば全体を読むため長時間処理)。
            (HistoryArchiveInfo info, bool matches) = await Vm.Operations.RunAsync(Loc.Get("HistoryLoad_Checking"), OperationKind.ReadOnly, doc.Document,
                doc.Document.Length, op => Task.Run(() =>
                {
                    using var input = File.OpenRead(path);
                    HistoryArchiveInfo i = HistoryArchive.ReadInfo(input);
                    return (i, HistoryArchive.Matches(doc.Document, i));
                }));
            if (!matches)
            {
                // 「元ファイルが変わっている可能性があります」(読み込む / キャンセル。既定はキャンセル)。
                ContentDialog warn = DialogParts.Dialog(Root, "HistoryLoadMismatchDialog", Loc.Get("HistoryLoad_Title"),
                    new TextBlock { Text = Loc.Get("HistoryLoad_Mismatch"), TextWrapping = TextWrapping.Wrap }, Loc.Get("HistoryLoad_LoadAnyway"));
                warn.DefaultButton = ContentDialogButton.Close;
                _historyDialogState = () => new JsonObject
                {
                    ["open"] = true,
                    ["kind"] = "mismatch",
                    ["text"] = Loc.Get("HistoryLoad_Mismatch"),
                    ["primary"] = warn.PrimaryButtonText,
                    ["close"] = warn.CloseButtonText,
                };
                ContentDialogResult answer;
                try
                {
                    answer = await ShowEditDialogAsync(warn);
                }
                finally
                {
                    _historyDialogState = null;
                }

                if (answer != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            HistoryArchive.PreparedHistory prepared = await Vm.Operations.RunAsync(Loc.Get("HistoryLoad_Operation"), OperationKind.ReadOnly, doc.Document,
                info.DataLength, op => Task.Run(() =>
                {
                    using var input = File.OpenRead(path);
                    return HistoryArchive.Prepare(doc.Document, input, op.Report, op.CancellationToken);
                }));
            using (prepared)
            {
                prepared.ApplyTo(doc.Document);
            }

            ShowStatusMessage(Loc.Format("HistoryLoad_Done", Path.GetFileName(path)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (HistoryArchiveException ex)
        {
            ShowNotice(Loc.Format("HistoryLoad_Failed", ex.Reason), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowNotice(Loc.Format("HistoryLoad_Failed", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    // ---- 名前の付いた節へ移動 (仕様 4) ----

    private async Task GoToNamedNodeAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        IReadOnlyList<HistoryNode> named = doc.Document.History.NamedNodes();
        var filter = DialogParts.Field("HistoryGoTo_Filter", Loc.Get("HistoryGoTo_Filter"), string.Empty, monospace: false);
        var list = new ListView { Height = 240, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, "HistoryGoTo_List");
        AutomationProperties.SetName(list, Loc.Get("HistoryGoTo_Title"));
        void Fill()
        {
            list.Items.Clear();
            foreach (HistoryNode node in named.Where(n => n.Name!.Contains(filter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            {
                var item = new ListViewItem { Content = $"{node.Name}  (#{node.Id} {EditOperationName(node.Entry.Description)})", Tag = node };
                AutomationProperties.SetName(item, node.Name);
                list.Items.Add(item);
            }

            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        }

        filter.TextChanged += (_, _) => Fill();
        Fill();
        var content = new StackPanel { Spacing = 8, MinWidth = 380, Children = { filter, list } };
        ContentDialog dialog = DialogParts.Dialog(Root, "HistoryGoToDialog", Loc.Get("HistoryGoTo_Title"), content, Loc.Get("HistoryGoTo_Go"));
        list.DoubleTapped += (_, _) => dialog.Hide();
        HistoryNode? Selected() => (list.SelectedItem as ListViewItem)?.Tag as HistoryNode;
        bool doubleTapped = false;
        list.DoubleTapped += (_, _) => doubleTapped = true;
        _historyDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = "goToNode",
            ["candidates"] = new JsonArray([.. list.Items.OfType<ListViewItem>().Select(i => (JsonNode)(((HistoryNode)i.Tag).Name ?? string.Empty))]),
        };
        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _historyDialogState = null;
        }

        if ((result == ContentDialogResult.Primary || doubleTapped) && Selected() is { } target)
        {
            GoToHistoryNode(doc, target);
        }

        FocusEditor();
    }

    /// <summary>履歴の節へ移る (読み取り専用・処理中なら知らせる)。</summary>
    internal void GoToHistoryNode(DocumentViewModel doc, HistoryNode node)
    {
        if (EnsureEditable(doc))
        {
            doc.Document.MoveToHistoryNode(node);
        }
    }

    // ---- 名前付け・枝の削除 (仕様 4・6) ----

    async Task IHistoryPanelHost.NameNodeAsync(HistoryNode node)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        var name = DialogParts.Field("HistoryName_Name", Loc.Get("HistoryName_Name"), node.Name ?? string.Empty, monospace: false);
        name.MaxLength = EditHistory.MaxNameLength;
        TextBlock note = DialogParts.Caption("HistoryName_Note");
        note.Text = Loc.Get("HistoryName_Note");
        ContentDialog dialog = DialogParts.Dialog(Root, "HistoryNameDialog", Loc.Get("HistoryName_Title"),
            new StackPanel { Spacing = 8, MinWidth = 360, Children = { name, note } }, Loc.Get("HistoryName_Ok"));
        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            doc.Document.History.SetName(node, name.Text);
        }
    }

    async Task IHistoryPanelHost.DeleteBranchAsync(HistoryNode node)
    {
        if (Vm.Selected is not { } doc || !doc.Document.History.CanDelete(node))
        {
            return;
        }

        // 削除は元に戻せないため確かめる (「枝を削除」「キャンセル」)。
        ContentDialog dialog = DialogParts.Dialog(Root, "HistoryDeleteDialog", Loc.Get("HistoryDelete_Title"),
            new TextBlock { Text = Loc.Get("HistoryDelete_Body"), TextWrapping = TextWrapping.Wrap }, Loc.Get("HistoryDelete_Delete"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            doc.Document.History.DeleteBranch(node);
        }
    }

    /// <summary>「この 2 時点からパッチを作成」(EDIT-42 の「呼び出し」)。</summary>
    Task IHistoryPanelHost.CreatePatchAsync(HistoryNode from, HistoryNode to)
    {
        if (Vm.Selected is not { } doc)
        {
            return Task.CompletedTask;
        }

        EditHistory history = doc.Document.History;
        return history.IsOnPath(from) && history.IsOnPath(to)
            ? CreatePatchAsync(PatchDiffSource.History, from.Depth, to.Depth)
            : CreatePatchAsync(PatchDiffSource.History, null, null,
                (from.Entry.Snapshot, to.Entry.Snapshot, Loc.Format("PatchCreate_HistoryPairText", from.Id, to.Id)));
    }

    // ---- 閉じるときの確認 (仕様 10) ----

    /// <summary>「履歴を保存」「保存しないで閉じる」「キャンセル」。閉じてよければ true。</summary>
    private async Task<bool> ConfirmHistoryOnCloseAsync(DocumentViewModel doc)
    {
        ContentDialog dialog = NewDialog(Loc.Format("HistoryClose_Title", doc.DisplayName), Loc.Get("HistoryClose_Body"));
        AutomationProperties.SetAutomationId(dialog, "HistoryCloseDialog");
        dialog.PrimaryButtonText = Loc.Get("HistoryClose_Save");
        dialog.SecondaryButtonText = Loc.Get("HistoryClose_DontSave");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowQueuedAsync() switch
        {
            ContentDialogResult.Primary => await SaveHistoryAsync(doc),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }
}
