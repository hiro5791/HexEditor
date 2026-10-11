using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using HexEditor.Core.Formats;
using HexEditor.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// デコードして開いたドキュメント (ENG-38) の元のテキストファイルの外部変更 (ENG-19)。元データはデコードした結果 (一時ファイル) のため、
/// 再読み込みは元のファイルをデコードし直す。保存していない変更をデコードし直した内容に重ねる「マージ」は、アドレスの対応が保てないため出さない。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>デコードしたドキュメントの元のファイルの外部変更を扱う (ENG-19 の仕様 4・5・8 に合わせる)。</summary>
    private void OnDecodedExternalChange(DocumentViewModel doc, ExternalChangeKind kind)
    {
        doc.ExternalChange = kind;
        if (kind == ExternalChangeKind.Deleted)
        {
            // 削除・移動: デコードした内容は一時ファイルにあるため編集を続けられる。保存すると元の場所に作り直す (確認する)。
            doc.SourceDeleted = true;
            ShowDecodedPrompt(doc, Loc.Format("External_Deleted", doc.DisplayName), reload: false);
            return;
        }

        // 未編集で自動の再読み込みが有効なら、デコードし直す (仕様 4)。
        if (!doc.Document.IsModified && FileSettings.AutoReload(App.Settings))
        {
            _ = ReloadDecodedAfterChangeAsync(doc);
            return;
        }

        ShowDecodedPrompt(doc, Loc.Format("External_Changed", doc.DisplayName), reload: true);
    }

    private async Task ReloadDecodedAfterChangeAsync(DocumentViewModel doc)
    {
        if (await ReloadDecodedAsync(doc))
        {
            ShowNotice(Loc.Get("External_Reloaded"), InfoBarSeverity.Informational, doc);
        }
    }

    private void ShowDecodedPrompt(DocumentViewModel doc, string message, bool reload)
    {
        var buttons = new List<NotificationAction>();
        if (reload)
        {
            buttons.Add(new NotificationAction(Loc.Get("External_Reload"), () => _ = ReloadDecodedWithConfirmAsync(doc, message)));
        }

        buttons.Add(new NotificationAction(Loc.Get("External_Ignore"), () =>
        {
            // このまま編集を続ける。保存すると外部の変更を上書きする (保存時に確かめる。ENG-19 の仕様 5)。
            if (doc.Watch is { } watch)
            {
                Vm.ExternalChanges?.Acknowledge(watch);
            }

            doc.OverwritesExternalChange |= doc.ExternalChange != ExternalChangeKind.Deleted;
            doc.ExternalChange = ExternalChangeKind.None;
        }));
        buttons.Add(new NotificationAction(Loc.Get("External_SaveAs"), () => _ = SaveAsync(doc, saveAs: true)));
        Vm.Notifications.Show(NotificationScope.Document, NotificationSeverity.Warning, message, doc, actions: buttons);
        AppLog.Info($"Notice (Warning): {message}");
    }

    /// <summary>保存していない変更を破棄してデコードし直す (確かめてから)。取り消したら InfoBar をもう一度出す。</summary>
    private async Task ReloadDecodedWithConfirmAsync(DocumentViewModel doc, string message)
    {
        if (doc.Document.IsModified)
        {
            ContentDialog dialog = DialogParts.Dialog(Root, "DecodedReloadDialog", doc.DisplayName,
                new TextBlock { Text = Loc.Format("External_DecodedReloadBody", doc.DisplayName), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                Loc.Get("External_Reload"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                ShowDecodedPrompt(doc, message, reload: true);
                return;
            }
        }

        if (!await ReloadDecodedAsync(doc))
        {
            ShowDecodedPrompt(doc, message, reload: true);
        }
    }

    /// <summary>
    /// 元のファイルをデコードし直して、ドキュメントの元データを置き換える (変更と Undo 履歴は捨てる)。隙間の塗りつぶしの値はデコードしたときと同じ。
    /// 形式として読めなくなっていれば理由を示して false。
    /// </summary>
    internal async Task<bool> ReloadDecodedAsync(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path || doc.Encoded is not { } encoded || !EnsureNotBusy(doc))
        {
            return false;
        }

        ImportResult result;
        try
        {
            long size = new FileInfo(path).Length;
            ImportOptions options = EncodedFile.OpenOptions(encoded.Format, encoded.GapFill);
            string temp = Vm.DocumentOptions.TempDirectory;
            result = size <= SyncDecodeLimit
                ? Importer.DecodeFile(path, options, temp)
                : await Vm.Operations.RunAsync(Loc.Format("Encoded_Decoding", Path.GetFileName(path), FormatName(encoded.Format)),
                    Core.Operations.OperationKind.ReadOnly, null, size,
                    op => Task.FromResult(Importer.DecodeFile(path, options, temp, new ImportProgress(op.CancellationToken, op.Report))));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(ex is FileNotFoundException or DirectoryNotFoundException ? Loc.Format("Error_NotFound", path) : Loc.Format("Error_Open", doc.DisplayName, ex.Message),
                InfoBarSeverity.Error, doc);
            return false;
        }

        using (result)
        {
            if (doc.Document.IsDisposed || !Vm.Documents.Contains(doc))
            {
                return false;
            }

            if (result.DataBytes == 0 && result.HasErrors)
            {
                int line = result.Issues.Items.FirstOrDefault(i => !i.IsWarning)?.Line ?? 1;
                ShowNotice(Loc.Format("Encoded_Unreadable", FormatName(encoded.Format), line), InfoBarSeverity.Error, doc);
                return false;
            }

            SparseImage image = result.TakeImage();
            image.Resizable = encoded.Format == FormatIds.Base64;
            long cursor = doc.Editor.Cursor;
            doc.Document.ReplaceSource(image);
            doc.Encoded = (result.Settings ?? new EncodedFileSettings { Format = encoded.Format }) with { GapFill = encoded.GapFill };
            doc.EncodedBaseAddress = result.BaseAddress;
            doc.FormatIssues = result.Issues.Items;
            doc.Editor.Click(Math.Min(cursor, doc.Document.Length), doc.Editor.ActiveColumn, lowNibble: false, extend: false);
        }

        Vm.RebaseWatch(doc);
        UpdateTitle();
        UpdateCommandStates();
        return true;
    }
}
