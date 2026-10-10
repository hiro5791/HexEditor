using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 編集メニューのコマンド: 読み取り専用 (EDIT-16)、バイトの挿入 (EDIT-14)、ファイルサイズの変更と切り詰め (EDIT-15)、塗りつぶし (EDIT-29)、
/// ファイルの内容の挿入 (EDIT-30)。ダイアログは MainWindow.EditDialogs.cs。ロジックは Core の <see cref="EditCommands"/> と <see cref="ContentBuilder"/>。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 読み取り専用の解除で、書き込みで開き直す処理 (ENG-14 の仕様 3。ファイル・セッションの担当が設定する)。確認ダイアログで承認された後に呼ばれ、
    /// 開き直せたら true。null なら開き直さずに解除する。
    /// </summary>
    public Func<DocumentViewModel, Task<bool>>? ReopenForWriting { get; set; }

    /// <summary>編集メニューのコマンド (UI-16。キーは 00-overview.md 8.3)。<see cref="RegisterCommandHandlers"/> から呼ぶ。</summary>
    private void RegisterEditCommands()
    {
        var e = new RoutedEventArgs();
        Commands.Register("edit.copyAs", () => CopyAs_Click(this, e), NeedsDocument);
        Commands.Register("edit.copyAsLast", () => CopyAsLast_Click(this, e),
            () => NeedsDocument(_ => AppState.GetString(CopyAsLastKey, string.Empty).Length > 0 ? null : Loc.Get("Command_NoPreviousFormat")));
        RegisterCopyAsFormatCommands();
        Commands.Register("edit.pasteSpecial", () => PasteSpecial_Click(this, e), () => NeedsEditable());
        Commands.Register("edit.selectRange", () => SelectRange_Click(this, e),
            () => NeedsDocument(d => d.Document.Length > 0 ? null : Loc.Get("Command_EmptyDocument")));
        Commands.Register("edit.insertBytes", () => InsertBytes_Click(this, e),
            () => NeedsEditable(d => d.Document.CanResize ? null : Loc.Get("Notice_FixedLength")));
        Commands.Register("edit.insertFile", () => InsertFile_Click(this, e), () => NeedsEditable());
        Commands.Register("edit.fill", () => Fill_Click(this, e),
            () => NeedsEditable(d => d.Editor.HasSelection || d.Document.Length > 0 ? null : Loc.Get("Command_EmptyDocument")));
        Commands.Register("edit.resize", () => Resize_Click(this, e),
            () => NeedsEditable(d => d.Document.CanResize ? null : Loc.Get("Notice_FixedLength")));
        Commands.Register("edit.truncate", () => Truncate_Click(this, e),
            () => NeedsEditable(d => !d.Document.CanResize ? Loc.Get("Notice_FixedLength")
                : EditCommands.CanTruncateAtCursor(d.Editor) ? null : Loc.Get("Command_NothingToTruncate")));
        Commands.Register("edit.readOnly", ToggleReadOnlyAsync,
            () => Vm.Selected is { } d ? Toggle(d.Document.IsReadOnly) : NeedsDocument());
    }

    /// <summary>
    /// Hex ビューの右クリックメニューに編集のコマンドを足し、状態とショートカットの表示を更新する (メニューを開くたびに呼ぶ):
    /// 形式を選択してコピー (EDIT-25)、形式を選択して貼り付け (EDIT-26)、バイトを挿入 (EDIT-14)、塗りつぶし (EDIT-29)。
    /// </summary>
    private void ExtendHexViewEditMenu(MenuFlyout menu)
    {
        // 形式を選択してコピーは、よく使う形式のサブメニュー付き (EDIT-25 の「呼び出し」)。
        ExtendHexViewCopyAsMenu(menu);
        foreach ((string after, string id, string textKey) in new[]
        {
            ("HexViewMenu_PasteOverwrite", "edit.pasteSpecial", "Cmd_edit_pasteSpecial"),
            ("HexViewMenu_Delete", "edit.insertBytes", "Cmd_edit_insertBytes"),
            ("HexViewMenu_Delete", "edit.fill", "Cmd_edit_fill"),
        })
        {
            string automationId = "HexViewMenu_" + id;
            if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == automationId))
            {
                var item = new MenuFlyoutItem { Text = Loc.Get(textKey), Tag = "Command" };
                AutomationProperties.SetAutomationId(item, automationId);
                item.Click += (_, _) => _ = Commands.ExecuteAsync(id);
                int index = menu.Items.ToList().FindIndex(i => AutomationProperties.GetAutomationId(i) == after);
                // 同じ項目の後ろに続けて足すものは、先に足したものの後ろに置く。
                while (index >= 0 && index + 1 < menu.Items.Count && menu.Items[index + 1] is MenuFlyoutItem { Tag: "Command" })
                {
                    index++;
                }

                menu.Items.Insert(index < 0 ? menu.Items.Count : index + 1, item);
            }

            if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == automationId) is MenuFlyoutItem existing)
            {
                existing.IsEnabled = Commands.StateOf(id).Enabled;
                existing.KeyboardAcceleratorTextOverride = CommandService.ShortcutText(id);
            }
        }
    }

    // ---- 読み取り専用 (EDIT-16) ----

    /// <summary>編集 > 読み取り専用: 読み取り専用にする、または解除する (解除は「編集を許可する」と同じ手順)。</summary>
    private async Task ToggleReadOnlyAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        if (doc.Document.IsReadOnly)
        {
            await AllowEditAsync(doc);
        }
        else
        {
            doc.Editor.ReadOnly = true;
        }

        UpdateCommandStates();
    }

    /// <summary>
    /// 「編集を許可する」・読み取り専用の解除 (EDIT-16 の仕様 4)。利用者が読み取り専用にした場合はそのまま解除し、属性・権限・デバイスが
    /// 原因の場合は確認ダイアログを出して承認されたら解除する。解除できない場合は理由を示す。解除したら true。
    /// </summary>
    private async Task<bool> AllowEditAsync(DocumentViewModel doc)
    {
        Document document = doc.Document;
        switch (document.ReadOnlyRelease)
        {
            case ReadOnlyRelease.Immediate:
                document.SetReadOnly(ReadOnlyReason.None);
                return true;
            case ReadOnlyRelease.NotAllowed:
                ShowNotice(Loc.Get("ReadOnly_CannotRelease_" + document.ReadOnlyReason), InfoBarSeverity.Warning, doc);
                return false;
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "ReadOnlyConfirmDialog", Loc.Get("ReadOnly_Confirm_Title"),
            new TextBlock { Text = Loc.Get("ReadOnly_Confirm_" + document.ReadOnlyReason), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            Loc.Get("ReadOnly_AllowEdit"));
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return false;
        }

        if (ReopenForWriting is { } reopen && !await reopen(doc))
        {
            return false;
        }

        document.SetReadOnly(ReadOnlyReason.None);
        FocusEditor();
        return true;
    }

    /// <summary>
    /// 読み取り専用のドキュメントでデータを変える操作をした (EDIT-16 の仕様 3)。InfoBar に「編集を許可する」を付ける (解除できない場合は付けない)。
    /// 同じ通知が出ている間は重ねない (通知の数を増やすだけ)。
    /// </summary>
    private void ShowReadOnlyNotice(DocumentViewModel doc)
    {
        IReadOnlyList<NotificationAction>? actions = doc.Document.ReadOnlyRelease == ReadOnlyRelease.NotAllowed
            ? null
            : [new NotificationAction(Loc.Get("ReadOnly_AllowEdit"), () => _ = AllowEditAsync(doc))];
        ShowNotice(Loc.Get("Notice_ReadOnly"), InfoBarSeverity.Warning, doc, actions: actions);
    }

    /// <summary>コマンドを実行できるか確かめ、読み取り専用・処理中なら知らせる。</summary>
    private bool EnsureEditable(DocumentViewModel doc)
    {
        if (doc.Document.IsReadOnly)
        {
            ShowReadOnlyNotice(doc);
            return false;
        }

        if (doc.Document.IsEditLocked)
        {
            IReadOnlyList<LongRunningOperation> busy = Vm.Operations.ActiveFor(doc.Document);
            ShowNotice(busy.Count > 0 ? Loc.Format("Notice_BusyWith", busy[0].Name) : Loc.Get("Notice_Busy"), InfoBarSeverity.Error, doc);
            return false;
        }

        return true;
    }

    /// <summary>長さを変える操作ができるか (固定長ドキュメントでは InfoBar で理由を示す。共通の約束 4)。</summary>
    private bool EnsureResizable(DocumentViewModel doc)
    {
        if (!EnsureEditable(doc))
        {
            return false;
        }

        if (!doc.Document.CanResize)
        {
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, doc);
            return false;
        }

        return true;
    }

    // ---- 内容を作る (長時間処理) ----

    private ContentBuilder BuilderFor(DocumentViewModel doc) =>
        ContentBuilder.For(doc.Document, TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance, TestHooks.OpenContentSource);

    /// <summary>
    /// 内容を作る。一定時間で作れるものはそのまま作り、実データを作るものは長時間処理 (進捗・キャンセル) として作る。作っている間は
    /// ドキュメントの編集を止める (ENG-09 の仕様 7)。キャンセル・失敗したら null (ドキュメントは変わらない。共通の約束 2)。
    /// </summary>
    private async Task<EditContent?> BuildContentAsync(DocumentViewModel doc, string operationName, FillSpec spec, long rangeStart, long length)
    {
        ContentBuilder builder = BuilderFor(doc);
        try
        {
            if (!ContentBuilder.NeedsGeneration(spec, length))
            {
                return builder.Build(spec, rangeStart, length, doc.Document);
            }

            return await Vm.Operations.RunAsync(operationName, OperationKind.ModifiesDocument, doc.Document, length,
                op => Task.FromResult(builder.Build(spec, rangeStart, length, doc.Document, op)),
                locked => doc.Document.SetEditLock(locked));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TempSpaceException ex)
        {
            ShowNotice(Loc.Format("Fill_Error_TempSpace", GigaBytes(ex.Required), GigaBytes(ex.Available)), InfoBarSeverity.Error, doc);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }
    }

    private static string GigaBytes(long bytes) =>
        (bytes / 1e9).ToString(bytes >= 10_000_000_000 ? "N0" : "N1", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>作った内容で編集する。入力の誤り・読み取り専用などは InfoBar で示す。</summary>
    private bool ApplyEdit(DocumentViewModel doc, Action edit)
    {
        try
        {
            edit();
            return true;
        }
        catch (RangeEditException ex)
        {
            ShowNotice(Loc.Get("RangeEdit_Error_" + ex.Error), InfoBarSeverity.Error, doc);
        }
        catch (DocumentReadOnlyException)
        {
            ShowReadOnlyNotice(doc);
        }
        catch (FixedLengthException)
        {
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, doc);
        }
        catch (DocumentLockedException)
        {
            ShowNotice(Loc.Get("Notice_Busy"), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or TempFileWriteException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
        }

        return false;
    }

    // ---- バイトの挿入 (EDIT-14) ----

    private async void InsertBytes_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc || !EnsureResizable(doc))
        {
            return;
        }

        if (await ShowInsertBytesDialogAsync(doc) is not { } request)
        {
            return;
        }

        // マルチカーソルは各カーソル位置に同じ内容を挿入し、矩形選択は矩形挿入にする (EDIT-14 の仕様 4)。
        if (doc.Editor.HasMultipleCarets)
        {
            InsertAtCarets(doc, request.Spec, request.Count);
            FocusEditor();
            return;
        }

        if (doc.Editor.SelectionKind == SelectionKind.Rectangle)
        {
            if (doc.Editor.CheckRectangleRows() is not null)
            {
                ShowRectangleRowLimit(doc);
                return;
            }

            await InsertIntoRectangleAsync(doc, request.Spec, request.Count);
            FocusEditor();
            return;
        }

        if (await BuildContentAsync(doc, Loc.Get("Operation_InsertBytes"), request.Spec, request.Position, request.Count) is { } content)
        {
            ApplyEdit(doc, () => EditCommands.Insert(doc.Editor, request.Position, content, request.SelectInserted, "バイトの挿入"));
        }

        FocusEditor();
    }

    // ---- 塗りつぶし (EDIT-29) ----

    private async void Fill_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        if (await ShowFillDialogAsync(doc) is not { } request)
        {
            return;
        }

        FillSpec spec = request.Spec;
        if (spec.Kind == FillKind.Clipboard)
        {
            (byte[]? bytes, Core.Sources.IByteSource? source) = await _clipboard.ReadForFillAsync(doc.Editor);
            if (bytes is not { Length: > 0 } && source is not { Length: > 0 })
            {
                ShowNotice(Loc.Get("Fill_Error_EmptyClipboard"), InfoBarSeverity.Error, doc);
                return;
            }

            spec = spec with { Pattern = bytes, ClipboardSource = source };
        }

        if (request.UseSelection && doc.Editor.HasMultipleRanges)
        {
            // マルチ選択・矩形は要素ごとに塗る (EDIT-29 の仕様 4)。1 つの編集グループ。
            await FillSelectedRangesAsync(doc, spec, request.ContinueAcross);
            FocusEditor();
            return;
        }

        if (await BuildContentAsync(doc, Loc.Get("Operation_Fill"), spec, request.Start, request.Length) is { } content)
        {
            ApplyEdit(doc, () => EditCommands.Overwrite(doc.Editor, request.Start, content, "塗りつぶし"));
        }

        FocusEditor();
    }

    // ---- ファイルサイズの変更と切り詰め (EDIT-15) ----

    private async void Resize_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc || !EnsureResizable(doc))
        {
            return;
        }

        if (await ShowResizeDialogAsync(doc) is not { } request)
        {
            return;
        }

        long current = doc.Document.Length;
        EditContent? extension = null;
        if (request.NewLength > current)
        {
            extension = await BuildContentAsync(doc, Loc.Get("Operation_Resize"), request.Spec, current, request.NewLength - current);
            if (extension is null)
            {
                return;
            }
        }

        long removed = 0;
        if (ApplyEdit(doc, () => removed = EditCommands.Resize(doc.Editor, request.NewLength, extension)) && removed > 0)
        {
            ShowTruncatedNotice(doc, removed);
        }

        FocusEditor();
    }

    private void Truncate_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc || !EnsureResizable(doc) || !EditCommands.CanTruncateAtCursor(doc.Editor))
        {
            return;
        }

        long removed = 0;
        if (ApplyEdit(doc, () => removed = EditCommands.TruncateAtCursor(doc.Editor)))
        {
            ShowTruncatedNotice(doc, removed);
        }
    }

    /// <summary>「N バイトを切り捨てました」と「元に戻す」(EDIT-15 の仕様 2。確認ダイアログは出さない)。</summary>
    private void ShowTruncatedNotice(DocumentViewModel doc, long removed) =>
        ShowNotice(Loc.Format("Resize_Truncated", removed.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)),
            InfoBarSeverity.Informational, doc, undo: new NotificationAction(Loc.Get("Common_Undo"), () => doc.Editor.Undo()));

    // ---- ファイルの内容の挿入 (EDIT-30) ----

    private async void InsertFile_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        if (await PickContentFileAsync() is { } path)
        {
            await InsertFileAsync(doc, path);
        }
    }

    /// <summary>ファイルピッカー (テストでは --test-hooks の openPicker の先頭)。</summary>
    private async Task<string?> PickContentFileAsync()
    {
        const string settingsIdentifier = "HexEditor.InsertFile";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>
    /// ファイルの内容を挿入・上書きする (EDIT-30)。挿入する範囲は挿入時に一時ファイルへコピーする (同じファイルなら元データを参照する)。
    /// Ctrl を押しながらのドロップ、エクスプローラーでコピーしたファイルの貼り付け (EDIT-23 の仕様 1 の 4) からも呼ぶ。
    /// </summary>
    private async Task InsertFileAsync(DocumentViewModel doc, string path)
    {
        if (!EnsureEditable(doc))
        {
            return;
        }

        if (await ShowInsertFileDialogAsync(doc, path) is not { } request)
        {
            return;
        }

        var spec = new FillSpec { Kind = FillKind.File, FilePath = path, FileOffset = request.Offset, FileLength = request.Length, Repeat = false };
        long at = doc.Editor.HasSelection ? doc.Editor.SelectionStart : doc.Editor.Cursor;
        if (await BuildContentAsync(doc, Loc.Format("Operation_InsertFile", Path.GetFileName(path)), spec, at, request.Length) is not { } content)
        {
            return;
        }

        ApplyEdit(doc, () =>
        {
            if (request.Insert)
            {
                EditCommands.Insert(doc.Editor, at, content, selectInserted: true, "ファイルの内容の挿入");
            }
            else
            {
                EditCommands.Overwrite(doc.Editor, at, content, "ファイルの内容で上書き");
            }
        });
        FocusEditor();
    }

    // ---- 範囲を選択 (EDIT-04) ----

    private async void SelectRange_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected is { } doc)
        {
            await ShowSelectRangeDialogAsync(doc);
        }
    }

    /// <summary>ステータスバーの選択範囲の表示をクリック: 範囲を選択を開く (EDIT-04 の「呼び出し」)。</summary>
    private void StatusSelection_Click(object sender, RoutedEventArgs e) => SelectRange_Click(sender, e);
}
