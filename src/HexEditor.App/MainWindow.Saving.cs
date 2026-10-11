using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 保存の前の確認 (ENG-20 の仕様 3): ファイルサイズの上限 (ENG-25 の仕様 5)、空き容量不足 (ENG-25 の仕様 4)、
/// ジャーナルを使えないその場保存 (ENG-23 の仕様 3)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>空き容量不足のダイアログで「別の場所に保存」が選ばれた。</summary>
    private bool _saveElsewhereRequested;

    /// <summary>
    /// 保存の計画に問題があれば利用者に確かめる。続ける場合は (変えた) 計画を、やめる場合は null を返す。
    /// </summary>
    private async Task<SavePlan?> ConfirmSavePlanAsync(SavePlan plan, DocumentViewModel doc)
    {
        switch (plan.Issue)
        {
            case SaveIssue.FileTooLarge when plan.SizeLimit is { } limit:
                // 上限を超える場合は空き容量が足りなくても、このエラーだけを出す (ENG-25 の仕様 5)。
                ShowNotice(Loc.Format("Error_FileTooLarge", limit.Drive, limit.FileSystem, Size(limit.MaxFileSize)), InfoBarSeverity.Error, doc);
                return null;

            case SaveIssue.InsufficientSpace when plan.Space is { } space:
            {
                ContentDialog dialog = SaveDialog(
                    Loc.Get("SaveSpace_Title"),
                    Loc.Format("SaveSpace_Body", space.Drive, Size(space.Required), Size(space.Available)));
                dialog.PrimaryButtonText = Loc.Get("SaveSpace_SaveElsewhere");

                // 一時ファイルを作らずに元のファイルの中でずらしながら書ける (ENG-24)。
                if (plan.CanShift)
                {
                    dialog.SecondaryButtonText = Loc.Get("SaveSpace_Shift");
                }

                dialog.CloseButtonText = Loc.Get("Common_Cancel");
                dialog.DefaultButton = ContentDialogButton.Primary;
                switch (await dialog.ShowQueuedAsync())
                {
                    case ContentDialogResult.Primary:
                        _saveElsewhereRequested = true;
                        return null;
                    case ContentDialogResult.Secondary:
                        return SavePlanner.UseShiftInPlace(plan);
                    default:
                        return null;
                }
            }

            case SaveIssue.ConfirmShift when plan.Shift is { } shift:
            {
                // 実行のたびに確かめる (ENG-24 の仕様 3。確認を省略する設定は設けない)。
                string body = Loc.Format("Shift_Body", Path.GetFileName(plan.TargetPath ?? doc.DisplayName), Size(shift.WriteBytes), Size(shift.TemporaryBytes));
                if (shift.DiscardHistory)
                {
                    body += Environment.NewLine + Environment.NewLine + Loc.Get("Shift_DiscardHistory");
                }

                ContentDialog dialog = SaveDialog(Loc.Get("Shift_Title"), body);
                AutomationProperties.SetAutomationId(dialog, "ShiftSaveDialog");
                dialog.PrimaryButtonText = Loc.Get("Shift_Save");
                dialog.CloseButtonText = Loc.Get("Common_Cancel");
                dialog.DefaultButton = ContentDialogButton.Close;
                return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? SavePlanner.ConfirmShift(plan) : null;
            }

            case SaveIssue.JournalTooLarge when plan.Journal is { } journal:
            {
                ContentDialog dialog = SaveDialog(
                    Loc.Get("SaveJournal_Title"),
                    Loc.Format("SaveJournal_Body", Size(journal.Required), Size(journal.Limit)));
                dialog.PrimaryButtonText = Loc.Get("SaveJournal_UseSafe");
                dialog.SecondaryButtonText = Loc.Get("SaveJournal_Unprotected");
                dialog.CloseButtonText = Loc.Get("Common_Cancel");
                dialog.DefaultButton = ContentDialogButton.Primary;
                return await dialog.ShowQueuedAsync() switch
                {
                    ContentDialogResult.Primary => SavePlanner.UseSafeSave(plan),
                    ContentDialogResult.Secondary => SavePlanner.WriteWithoutJournal(plan),
                    _ => null,
                };
            }

            case SaveIssue.HardLinks when plan.LinkCount is { } links:
            {
                // 安全な保存ではハードリンクが切れる (ENG-22 の仕様 4)。長さが変わる場合はずらしながらのその場保存 (ENG-24) になる。
                // 「その場で保存」はその場で書ける (書き込めるファイル) ときに出す。
                ContentDialog dialog = SaveDialog(Loc.Get("SaveLinks_Title"), Loc.Format("SaveLinks_Body", links));
                dialog.PrimaryButtonText = Loc.Get("SaveLinks_Safe");
                if (plan.CanKeepLinks)
                {
                    dialog.SecondaryButtonText = Loc.Get("SaveLinks_InPlace");
                }

                dialog.CloseButtonText = Loc.Get("Common_Cancel");
                dialog.DefaultButton = ContentDialogButton.Primary;
                return await dialog.ShowQueuedAsync() switch
                {
                    ContentDialogResult.Primary => SavePlanner.BreakLinks(plan),
                    ContentDialogResult.Secondary => SavePlanner.KeepLinks(plan),
                    _ => null,
                };
            }

            case SaveIssue.BackupCopy when plan.BackupCopyBytes is { } bytes:
            {
                // その場保存のバックアップのためにファイル全体をコピーする (ENG-26 の仕様 5)。
                ContentDialog dialog = SaveDialog(Loc.Get("BackupCopy_Title"), Loc.Format("BackupCopy_Body", Size(bytes)));
                dialog.PrimaryButtonText = Loc.Get("BackupCopy_Copy");
                dialog.SecondaryButtonText = Loc.Get("Backup_SaveWithout");
                dialog.CloseButtonText = Loc.Get("Common_Cancel");
                dialog.DefaultButton = ContentDialogButton.Primary;
                return await dialog.ShowQueuedAsync() switch
                {
                    ContentDialogResult.Primary => SavePlanner.CopyBackup(plan),
                    ContentDialogResult.Secondary => SavePlanner.WithoutBackup(plan),
                    _ => null,
                };
            }

            case SaveIssue.ReadOnly:
                ShowNotice(Loc.Get("Error_SaveReadOnly"), InfoBarSeverity.Error, doc);
                return null;

            default:
                return null;
        }
    }

    private ContentDialog SaveDialog(string title, string body)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
        };
        AutomationProperties.SetAutomationId(dialog, "SaveDialog");
        return dialog;
    }

    /// <summary>サイズの表示: 「1.50 GB (1,610,612,736 バイト)」(地域設定で書式化。ENG-25 の画面)。</summary>
    private static string Size(long bytes) =>
        StatusFormat.ShortSize(bytes, CultureInfo.CurrentCulture) is { } shortSize
            ? Loc.Format("Size_WithBytes", shortSize, StatusFormat.Number(bytes, CultureInfo.CurrentCulture))
            : Loc.Format("Size_Bytes", StatusFormat.Number(bytes, CultureInfo.CurrentCulture));

    /// <summary>「他のアプリがこのファイルに書き込める状態です…」(ENG-15 の仕様 2)。</summary>
    public void ShowLockFailed(DocumentViewModel doc) =>
        ShowNotice(Loc.Get("Notice_LockFailed"), InfoBarSeverity.Warning, doc);

    /// <summary>「編集の数が多く、メモリの上限 (1 GiB) を超えています。保存すると使用量が減ります」(ENG-08 の仕様 2 の 3)。</summary>
    public void ShowMemoryOverLimit(long limit) =>
        ShowNotice(Loc.Format("Notice_MemoryOverLimit", Size(limit)), InfoBarSeverity.Warning);
}
