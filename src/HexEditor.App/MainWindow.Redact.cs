using System.Runtime.CompilerServices;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ファイルシステムを理解した墨塗り (12 の FOR-32)。ファイルシステムブラウザで選んだファイルのデータの断片とスラック (と、オプションで名前) を、
/// データエンジンの編集として上書きする (保存するまで書き込まない、Undo できる)。<c>redaction-log.csv</c> を書き、監査ログに記録する。
/// 墨塗りしたドキュメントを元のイメージ・ディスクに保存するときは確認ダイアログを出す。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>墨塗りしたドキュメント (元の場所への保存を確かめる)。</summary>
    private static readonly ConditionalWeakTable<Document, object> s_redactedDocuments = [];

    /// <summary>ファイルシステムブラウザの右クリックメニューの「ファイルの内容を墨塗り」を使えるか。</summary>
    internal bool CanRedact(DocumentViewModel? d, IReadOnlyList<FsEntry> selected) => RedactDisabledReason(d, selected) is null;

    /// <summary>使えない理由 (使えるなら null)。</summary>
    internal string? RedactDisabledReason(DocumentViewModel? d, IReadOnlyList<FsEntry> selected)
    {
        if (d is null || StateOf(d)?.Session is null)
        {
            return Loc.Get("Fs_NoFileSystem");
        }

        if (selected.Count == 0 || selected.All(e => e.IsDirectory && !e.Flags.HasFlag(FsEntryFlags.Stream)))
        {
            return Loc.Get("Fs_NoEntry");
        }

        return d.Document.IsReadOnly
            ? WipeForensics.Hooks.IsWriteBlockEnabled ? Loc.Get("WriteBlock_Enabled") : Loc.Get("Redact_ReadOnly")
            : null;
    }

    private Task ShowRedactDialogAsync() =>
        Vm.Selected is { } d && FileSystemView?.SelectedEntries is { Count: > 0 } entries ? ShowRedactDialogAsync(d, entries) : Task.CompletedTask;

    /// <summary>墨塗りのダイアログ (対象ファイルの一覧、パターン、名前も置き換えるか、記録の保存先)。</summary>
    internal async Task<RedactionPlan?> ShowRedactDialogAsync(DocumentViewModel d, IReadOnlyList<FsEntry> entries)
    {
        if (RedactDisabledReason(d, entries) is { } reason)
        {
            ShowNotice(reason, InfoBarSeverity.Warning, d);
            return null;
        }

        var body = new StackPanel { Spacing = 8, MinWidth = 440, MaxWidth = 520 };
        body.Children.Add(Strong(Loc.Format("Redact_Files", entries.Count)));
        var list = new TextBlock
        {
            Text = string.Join(Environment.NewLine, entries.Take(20).Select(e => e.Path.Length > 0 ? e.Path : e.Name)) + (entries.Count > 20 ? Environment.NewLine + "…" : string.Empty),
            FontFamily = ToolParts.Mono,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AutomationProperties.SetAutomationId(list, "Redact_List");
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 160 });
        body.Children.Add(Strong(Loc.Get("Redact_Pattern")));
        RadioButton zero = DialogParts.Radio("Redact_Zero", Loc.Get("Redact_Pattern_Zero"), "RedactPattern", true);
        RadioButton random = DialogParts.Radio("Redact_Random", Loc.Get("Redact_Pattern_Random"), "RedactPattern", false);
        RadioButton custom = DialogParts.Radio("Redact_Custom", Loc.Get("Redact_Pattern_Custom"), "RedactPattern", false);
        TextBox hex = DialogParts.Field("Redact_PatternField", Loc.Get("Redact_PatternField"), "00");
        CheckBox names = DialogParts.Check("Redact_Names", Loc.Get("Redact_Names"), false);
        TextBox log = DialogParts.Field("Redact_Log", Loc.Get("Redact_Log"), DefaultRedactionLogPath(d), monospace: false);
        TextBlock error = ToolParts.ErrorText("Redact_Error");
        foreach (UIElement element in new UIElement[] { zero, random, custom, hex, names, log, error })
        {
            body.Children.Add(element);
        }

        Line(body, "Redact_NoteText", Loc.Get("Redact_Note"));
        ContentDialog dialog = DialogParts.Dialog(Root, "RedactDialog", Loc.Get("Redact_Title"), new ScrollViewer { Content = body, MaxHeight = 600 }, Loc.Get("Redact_Run"));
        ShredPass? Pattern() => zero.IsChecked == true ? ShredPass.Fill(0x00)
            : random.IsChecked == true ? ShredPass.Random
            : ShredMethod.ParsePass(hex.Text) is { IsRandom: false } pass ? pass : null;

        void Update()
        {
            hex.IsEnabled = custom.IsChecked == true;
            ShredPass? pattern = Pattern();
            ToolParts.ShowError(error, null, pattern is null ? Loc.Get("Redact_Error_Pattern") : null);
            dialog.IsPrimaryButtonEnabled = pattern is not null && log.Text.Trim().Length > 0;
        }

        foreach (RadioButton radio in new[] { zero, random, custom })
        {
            radio.Checked += (_, _) => Update();
        }

        hex.TextChanged += (_, _) => Update();
        log.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || Pattern() is not { } chosen)
        {
            return null;
        }

        return await RedactAsync(d, entries, chosen, names.IsChecked == true, log.Text.Trim());
    }

    /// <summary>記録の既定の保存先: イメージと同じフォルダ (書けなければドキュメントフォルダ) の redaction-log.csv。</summary>
    private static string DefaultRedactionLogPath(DocumentViewModel d)
    {
        string? folder = (d.LinkParent ?? d).FilePath is { } path ? Path.GetDirectoryName(path) : null;
        if (folder is null || !IsWritableFolder(folder))
        {
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(folder, FileSystemRedaction.LogFileName);
    }

    /// <summary>
    /// 墨塗りする (仕様 1〜6): 範囲を求め (長時間処理)、編集として適用し、記録を書いて監査ログに記録する。対象外にしたファイルと名前を置き換えられなかった
    /// ファイルは理由を一覧に示す。<paramref name="logPath"/> が null なら記録を書かない。
    /// </summary>
    internal async Task<RedactionPlan?> RedactAsync(DocumentViewModel d, IReadOnlyList<FsEntry> entries, ShredPass pattern, bool replaceNames, string? logPath)
    {
        if (RedactDisabledReason(d, entries) is { } reason || StateOf(d)?.Session is not { } session)
        {
            ShowNotice(RedactDisabledReason(d, entries) ?? Loc.Get("Fs_NoFileSystem"), InfoBarSeverity.Warning, d);
            return null;
        }

        if (logPath is not null && WipeForensics.Hooks.CheckOutput(logPath) is { } refused)
        {
            await ShowMessageAsync(Loc.Get("Redact_Title"), Loc.Format("Wipe_Refused_Policy", refused), "WipeRefusedDialog");
            return null;
        }

        RedactionPlan plan;
        try
        {
            DocumentSnapshot snapshot = d.Document.DataSnapshot;
            plan = await Vm.Operations.RunAsync(Loc.Get("Redact_Planning"), OperationKind.ReadOnly, d.Document, null,
                op => Task.FromResult(FileSystemRedaction.Plan(session.FileSystem, session.VolumeOffset, new SnapshotVolumeReader(snapshot), entries, pattern, replaceNames,
                    op.CancellationToken)));
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (plan.Items.Count == 0)
        {
            LastWipeResult = plan;
            await ShowRedactionIssuesAsync(plan, Loc.Get("Redact_Nothing"));
            return plan;
        }

        try
        {
            FileSystemRedaction.Run(d.Document, plan, d.DisplayName, logPath, DateTimeOffset.UtcNow, description: Loc.Get("EditOp_Redact"));
        }
        catch (WipeRefusedException ex)
        {
            await ShowMessageAsync(Loc.Get("Redact_Title"), RefusalMessage(ex.Reason, ex.Detail), "WipeRefusedDialog");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 編集は済んでいる。記録を書けなかったことを知らせる。
            ShowNotice(Loc.Format("Redact_LogFailed", ex.Message), InfoBarSeverity.Error, d);
        }

        s_redactedDocuments.AddOrUpdate((d.LinkParent ?? d).Document, true);
        LastWipeResult = plan;
        string done = Loc.Format("Redact_Done", plan.Items.Count, WipeNumber(plan.TotalBytes));
        if (plan.Excluded.Count > 0 || plan.Items.Any(i => i.Warnings.Count > 0))
        {
            await ShowRedactionIssuesAsync(plan, done);
        }
        else
        {
            ShowNotice(done, InfoBarSeverity.Success, d);
        }

        return plan;
    }

    private static string ExclusionText(RedactionExclusion x) => Loc.Get("Redact_Reason_" + x.Reason);

    private async Task ShowRedactionIssuesAsync(RedactionPlan plan, string summary)
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 520 };
        Line(body, "RedactResult_Summary", summary);
        if (plan.Excluded.Count > 0)
        {
            Line(body, "RedactResult_Excluded", Loc.Format("Redact_Excluded_Body", plan.Excluded.Count) + Environment.NewLine
                + string.Join(Environment.NewLine, plan.Excluded.Take(100).Select(x => $"{x.Path}  ({ExclusionText(x)})")));
        }

        RedactionItem[] warned = [.. plan.Items.Where(i => i.Warnings.Count > 0)];
        if (warned.Length > 0)
        {
            Line(body, "RedactResult_Warnings", Loc.Format("Redact_Warnings", string.Join(", ", warned.Take(50).Select(i => i.Path))));
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "RedactResultDialog", Loc.Get("Redact_Excluded_Title"), new ScrollViewer { Content = body, MaxHeight = 480 },
            Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }

    /// <summary>
    /// 墨塗りしたドキュメントを元のイメージ・ディスクに保存するときの確認 (仕様 2。共通の前提「破壊的な操作の確認」)。墨塗りしていない・編集が
    /// 残っていないドキュメントでは何も出さない。
    /// </summary>
    private async Task<bool> ConfirmRedactedInPlaceSaveAsync(DocumentViewModel doc)
    {
        Document document = (doc.LinkParent ?? doc).Document;
        if (!s_redactedDocuments.TryGetValue(document, out _) || !document.IsModified)
        {
            return true;
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        Line(body, "RedactSave_Body", Loc.Format("Redact_SaveConfirm_Body", doc.DisplayName));
        Line(body, "RedactSave_Irreversible", Loc.Get("Wipe_Confirm_Irreversible"), strong: true);
        bool ok = await ConfirmDestructiveAsync("RedactSaveDialog", Loc.Get("Redact_SaveConfirm_Title"), body,
            Loc.Format("Redact_SaveConfirm_Primary", doc.DisplayName), null);
        if (ok)
        {
            WipeForensics.Hooks.Audit(WipeAuditOperations.Redact, doc.DisplayName, WipeAuditOutcome.Succeeded, new Dictionary<string, string>
            {
                ["action"] = "saveInPlace",
            });
        }

        return ok;
    }
}
