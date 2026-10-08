using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Notifications;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>再読み込みと変更の破棄 (ENG-18)、外部変更の検知と再読み込み・マージ (ENG-19)。</summary>
public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _pollTimer;

    /// <summary>
    /// 「比較」(ENG-19 の仕様 5): 現在のドキュメントとディスク上の新しい内容の比較 (ANA-08 の保存済みの内容との比較)。比較の担当が
    /// 設定する。設定されていなければ「比較」のボタンを出さない。
    /// </summary>
    public static Func<MainWindow, DocumentViewModel, Task>? CompareWithDisk { get; set; }

    private void InitializeExternalChanges()
    {
        Vm.ExternalChangeDetected += (doc, kind) => DispatcherQueue.TryEnqueue(() => OnExternalChange(doc, kind));

        // ウィンドウがアクティブになったとき・タブを切り替えたときに確かめる (仕様 1)。
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                CheckSelectedForExternalChange();
            }
        };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                CheckSelectedForExternalChange();
            }
        };

        // 変更通知を使えない場所では、アクティブなドキュメントについて 5 秒ごとに確かめる (仕様 1)。
        _pollTimer = DispatcherQueue.CreateTimer();
        _pollTimer.Interval = ExternalChangeRules.PollInterval;
        _pollTimer.Tick += (_, _) =>
        {
            if (Vm.Selected?.Watch is { IsPolled: true })
            {
                CheckSelectedForExternalChange();
            }
        };
        _pollTimer.Start();
    }

    private void CheckSelectedForExternalChange()
    {
        if (Vm.Selected?.Watch is { } watch && Vm.ExternalChanges is { } monitor)
        {
            WatchedFile target = watch;
            _ = Task.Run(() => monitor.CheckNow(target));
        }
    }

    /// <summary>外部変更を検知した (仕様 4〜8)。</summary>
    private void OnExternalChange(DocumentViewModel doc, ExternalChangeKind kind)
    {
        if (!Vm.Documents.Contains(doc) || doc.Watch is null)
        {
            return;
        }

        AppLog.Info($"External change: {kind} ({(doc.Document.IsModified ? "modified" : "unmodified")})");

        // ドキュメントを変える長時間処理の実行中 (ENG-09 の仕様 7) は、再読み込みもマージもできない。処理が終わってから扱う。
        if (doc.Document.IsEditLocked)
        {
            DeferExternalChange(doc);
            return;
        }

        // そのファイルのブロックキャッシュを捨てる (仕様 10)。
        doc.Document.Cache.Invalidate();
        ExternalChangePrompt prompt = ExternalChangeRules.Decide(kind, doc.Document.IsModified, FileSettings.AutoReload(App.Settings),
            doc.Document.LockState == FileLockState.Locked);
        if (prompt == ExternalChangePrompt.AutoReload)
        {
            if (ReloadFromDisk(doc))
            {
                ShowNotice(Loc.Get("External_Reloaded"), InfoBarSeverity.Informational, doc);
            }

            return;
        }

        doc.ExternalChange = kind;
        if (prompt == ExternalChangePrompt.Deleted)
        {
            doc.SourceDeleted = true;
        }
        else if (prompt == ExternalChangePrompt.Mixed)
        {
            // 変更していない部分の表示がすでに外部の内容に変わっている (仕様 6)。保存するときは全体を書く。
            doc.OverwritesExternalChange = true;
            doc.Document.RefreshFromSource();
        }

        ShowExternalPrompt(doc, kind, prompt);
    }

    /// <summary>警告の InfoBar を出す。ボタンの処理を取り消したら、もう一度出す。</summary>
    private void ShowExternalPrompt(DocumentViewModel doc, ExternalChangeKind kind, ExternalChangePrompt prompt)
    {
        string message = prompt switch
        {
            ExternalChangePrompt.Mixed => Loc.Format("External_Mixed", doc.DisplayName),
            ExternalChangePrompt.Deleted => Loc.Format("External_Deleted", doc.DisplayName),
            _ => Loc.Format("External_Changed", doc.DisplayName),
        };
        ExternalChangeActions actions = ExternalChangeRules.ActionsFor(prompt, doc.Document.IsModified, doc.Document.IsReadOnly);
        var buttons = new List<NotificationAction>();
        void Add(ExternalChangeActions action, string key, Func<Task> run)
        {
            if (actions.HasFlag(action))
            {
                buttons.Add(new NotificationAction(Loc.Get(key), () => _ = run()));
            }
        }

        Add(ExternalChangeActions.Reload, "External_Reload", async () =>
        {
            if (!await ReloadWithConfirmAsync(doc))
            {
                ShowExternalPrompt(doc, kind, prompt);
            }
        });
        Add(ExternalChangeActions.Merge, "External_Merge", async () =>
        {
            if (!await MergeAsync(doc))
            {
                ShowExternalPrompt(doc, kind, prompt);
            }
        });
        // 比較 (ANA-08) はフェーズ 2。比較の担当が CompareWithDisk を設定するまではボタンを出さない。
        if (CompareWithDisk is { } compare)
        {
            Add(ExternalChangeActions.Compare, "External_Compare", async () =>
            {
                await compare(this, doc);
                ShowExternalPrompt(doc, kind, prompt);
            });
        }
        Add(ExternalChangeActions.Ignore, "External_Ignore", () =>
        {
            // このまま編集を続ける。保存すると外部の変更を上書きする (保存時に確かめる。仕様 5)。
            if (doc.Watch is { } watch)
            {
                Vm.ExternalChanges?.Acknowledge(watch);
            }

            doc.OverwritesExternalChange = true;
            doc.ExternalChange = ExternalChangeKind.None;
            return Task.CompletedTask;
        });
        Add(ExternalChangeActions.SaveAs, "External_SaveAs", async () =>
        {
            if (!await SaveAsync(doc, saveAs: true))
            {
                ShowExternalPrompt(doc, kind, prompt);
            }
        });
        Add(ExternalChangeActions.Close, "External_Close", async () =>
        {
            if (!await CloseAsync([doc]))
            {
                ShowExternalPrompt(doc, kind, prompt);
            }
        });
        Vm.Notifications.Show(NotificationScope.Document, NotificationSeverity.Warning, message, doc, actions: buttons);
        AppLog.Info($"Notice (Warning): {message}");
    }

    /// <summary>
    /// ディスク上の今の内容で開き直す (変更を捨て、Undo 履歴を消す)。カーソルは新しい長さに収める。開けなければ理由を示して false。
    /// </summary>
    private bool ReloadFromDisk(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path || !EnsureNotBusy(doc))
        {
            return false;
        }

        FileByteSource source;
        try
        {
            source = FileByteSource.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(ex is FileNotFoundException or DirectoryNotFoundException ? Loc.Format("Error_NotFound", path) : Loc.Format("Error_Open", doc.DisplayName, ex.Message),
                InfoBarSeverity.Error, doc);
            return false;
        }

        long cursor = doc.Editor.Cursor;
        doc.Document.ReplaceSource(source);
        doc.Editor.Click(Math.Min(cursor, doc.Document.Length), doc.Editor.ActiveColumn, lowNibble: false, extend: false);
        Vm.RebaseWatch(doc);
        UpdateTitle();
        return true;
    }

    /// <summary>外部で変わったかを確かめる (変更を破棄するときの、元に戻せるかの判断。ENG-18 の仕様 3)。</summary>
    private static bool SourceChangedOnDisk(DocumentViewModel doc) =>
        doc.Watch is { } watch && doc.Document.Source is FileByteSource file
        && ExternalChangeRules.Classify(watch.Baseline, FileStamp.FromPath(watch.Path), file.ReadCurrentStamp()) != ExternalChangeKind.None;

    /// <summary>
    /// 変更を破棄して再読み込み (ENG-18 の仕様 3): 「N か所、M バイトの変更を破棄します」と、元に戻せるかを示して確かめる。
    /// 外部で変更されていなければ破棄を 1 つの Undo 単位にし、変更されていれば Undo 履歴を消去する。
    /// </summary>
    private async Task<bool> ReloadWithConfirmAsync(DocumentViewModel doc)
    {
        if (!EnsureNotBusy(doc))
        {
            return false;
        }

        bool deleted = doc.SourceDeleted || (doc.FilePath is { } current && doc.Watch is not null && !File.Exists(current));
        bool changedOnDisk = !deleted && (SourceChangedOnDisk(doc) || doc.ExternalChange != ExternalChangeKind.None || doc.OverwritesExternalChange);
        if (doc.Document.IsModified)
        {
            ChangeSummary changes = ChangeSummary.Of(doc.Document.Current);
            string body = Loc.Format("Discard_Body", StatusFormat.Number(changes.Places, CultureInfo.CurrentCulture),
                StatusFormat.Number(changes.Bytes, CultureInfo.CurrentCulture))
                + "\n" + Loc.Get(changedOnDisk ? "Discard_NotUndoable" : "Discard_Undoable");
            ContentDialog dialog = NewDialog(Loc.Format("Discard_Title", doc.DisplayName), body);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "DiscardDialog");
            dialog.PrimaryButtonText = Loc.Get("Discard_Confirm");
            dialog.CloseButtonText = Loc.Get("Common_Cancel");
            dialog.DefaultButton = ContentDialogButton.Close;
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || !EnsureNotBusy(doc))
            {
                return false;
            }
        }

        // 削除されたファイルは開いているハンドルから読めるため、その内容に戻す (元に戻せる)。
        if (changedOnDisk)
        {
            if (!ReloadFromDisk(doc))
            {
                return false;
            }
        }
        else if (!doc.Document.IsModified)
        {
            // 変更がなく外部でも変わっていない: 破棄するものがないため、Undo 単位を積まずに表示を読み直すだけにする。
            doc.Document.RefreshFromSource();
        }
        else
        {
            doc.Document.DiscardChanges(Loc.Get("Discard_UndoName"));
        }

        Vm.Notifications.DismissOwnedBy(doc);
        UpdateTitle();
        return true;
    }

    /// <summary>「マージ」(ENG-19 の仕様 5)。長さが変わっている場合は、オフセットがずれる可能性を確かめる。</summary>
    private async Task<bool> MergeAsync(DocumentViewModel doc)
    {
        if (doc.FilePath is not { } path || !EnsureNotBusy(doc))
        {
            return false;
        }

        // マージは自分の変更を適用し直す編集のため、読み取り専用では行わない (EDIT-16 の仕様 2)。
        if (doc.Document.IsReadOnly)
        {
            ShowReadOnlyNotice(doc);
            return false;
        }

        FileByteSource source;
        try
        {
            source = FileByteSource.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Error_Open", doc.DisplayName, ex.Message), InfoBarSeverity.Error, doc);
            return false;
        }

        if (source.Length != doc.Document.CurrentSourceLength)
        {
            ContentDialog dialog = NewDialog(Loc.Get("Merge_Title"), Loc.Format("Merge_LengthChanged",
                StatusFormat.Number(doc.Document.CurrentSourceLength, CultureInfo.CurrentCulture),
                StatusFormat.Number(source.Length, CultureInfo.CurrentCulture)));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "MergeDialog");
            dialog.PrimaryButtonText = Loc.Get("External_Merge");
            dialog.CloseButtonText = Loc.Get("Common_Cancel");
            dialog.DefaultButton = ContentDialogButton.Close;
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || !EnsureNotBusy(doc) || doc.Document.IsReadOnly)
            {
                source.Dispose();
                return false;
            }
        }

        doc.Document.MergeOnto(source, Loc.Get("Merge_UndoName"));
        Vm.RebaseWatch(doc);
        UpdateTitle();
        return true;
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        // ファイルが外部で変更されていれば、その扱い (ENG-19) になる。変わっていなければ表示を読み直すだけ (変更は残す)。
        // すでに知らせた変更 (InfoBar を閉じた後など) も、もう一度知らせて扱いを選べるようにする (ENG-18 の仕様 1)。
        if (doc.Watch is { } watch && Vm.ExternalChanges is { } monitor)
        {
            ExternalChangeKind kind = await Task.Run(() => monitor.CheckNow(watch, reportAgain: true));
            if (kind != ExternalChangeKind.None)
            {
                return;
            }
        }

        doc.Document.RefreshFromSource();
    }

    private async void DiscardReload_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await ReloadWithConfirmAsync(doc);
        }
    }

    /// <summary>
    /// 「変更を破棄して再読み込み」の使えない理由: ファイルでない、処理中 (ENG-09 の仕様 7)。読み取り専用でも使える (EDIT-16 の仕様 2)。
    /// </summary>
    private static string? NeedsDiscardableFile(DocumentViewModel doc) =>
        NeedsFile(doc) ?? (doc.Document.IsEditLocked ? Loc.Get("Notice_Busy") : null);

    /// <summary>処理中 (ENG-09 の仕様 7) なら「処理中のため…」を示して false。読み取り専用かどうかは問わない (再読み込みはできる)。</summary>
    private bool EnsureNotBusy(DocumentViewModel doc)
    {
        if (!doc.Document.IsEditLocked)
        {
            return true;
        }

        IReadOnlyList<Core.Operations.LongRunningOperation> busy = Vm.Operations.ActiveFor(doc.Document);
        ShowNotice(busy.Count > 0 ? Loc.Format("Notice_BusyWith", busy[0].Name) : Loc.Get("Notice_Busy"), InfoBarSeverity.Error, doc);
        return false;
    }

    /// <summary>処理中に検知した外部変更を待たせているドキュメント。</summary>
    private readonly HashSet<DocumentViewModel> _deferredExternalChanges = [];

    /// <summary>
    /// 処理中に検知した外部変更を、処理が終わってから扱う。監視には知らせた変更を忘れさせ、処理が終わったら確かめ直す (ファイルがその後も
    /// 変わっていれば、そのときの種類で知らせる)。タブを閉じた・別のウィンドウに移した場合はやめる (移した先のウィンドウの確認で知らせる)。
    /// </summary>
    private void DeferExternalChange(DocumentViewModel doc)
    {
        if (doc.Watch is { } watch)
        {
            Vm.ExternalChanges?.Forget(watch);
        }

        if (!_deferredExternalChanges.Add(doc))
        {
            return;
        }

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += (_, _) =>
        {
            if (Vm.Documents.Contains(doc) && doc.Watch is not null && doc.Document.IsEditLocked && !doc.Document.IsDisposed)
            {
                return;
            }

            timer.Stop();
            _deferredExternalChanges.Remove(doc);
            if (Vm.Documents.Contains(doc) && doc.Watch is { } target && Vm.ExternalChanges is { } monitor)
            {
                _ = Task.Run(() => monitor.CheckNow(target));
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 元の場所に保存する前の確認 (ENG-19 の仕様 5・8): 外部の変更を上書きする、削除されたファイルを作り直す。続けるなら true。
    /// </summary>
    private async Task<bool> ConfirmExternalSaveAsync(DocumentViewModel doc)
    {
        string? key = doc.SourceDeleted || (doc.FilePath is { } path && doc.Watch is not null && !File.Exists(path)) ? "External_RecreateBody"
            : doc.OverwritesExternalChange ? "External_OverwriteBody"
            : null;
        if (key is null)
        {
            return true;
        }

        doc.SourceDeleted |= key == "External_RecreateBody";
        doc.OverwritesExternalChange |= key == "External_OverwriteBody";
        ContentDialog dialog = SaveDialog(doc.DisplayName, Loc.Format(key, doc.FilePath ?? doc.DisplayName));
        dialog.PrimaryButtonText = Loc.Get("Close_Save");
        dialog.CloseButtonText = Loc.Get("Common_Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }
}
