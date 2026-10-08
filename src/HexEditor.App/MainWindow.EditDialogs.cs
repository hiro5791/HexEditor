using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>範囲を選択・バイトの挿入・塗りつぶし・ファイルサイズの変更・ファイルの内容の挿入のダイアログ (ContentDialog)。</summary>
public sealed partial class MainWindow
{
    private sealed record InsertBytesRequest(long Position, long Count, FillSpec Spec, bool SelectInserted);

    private sealed record FillRequest(long Start, long Length, FillSpec Spec);

    private sealed record ResizeRequest(long NewLength, FillSpec Spec);

    private sealed record InsertFileRequest(long Offset, long Length, bool Insert);

    private const string InsertCountKey = "edit.insert.count";

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>編集のダイアログを閉じた回数 (テストで、閉じた後の処理を待つため)。</summary>
    private int _editDialogsClosed;

    private async Task<ContentDialogResult> ShowEditDialogAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _editDialogsClosed++;
        }
    }

    // ---- 範囲を選択 (EDIT-04) ----

    private async Task ShowSelectRangeDialogAsync(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        var model = new RangeSelectionModel(new EditorExpressionContext(editor), doc.Document.Length,
            editor.HasSelection ? (editor.SelectionStart, editor.SelectionLength) : null, editor.Cursor);
        var fields = new Dictionary<RangeField, (TextBox Box, TextBlock Result)>
        {
            [RangeField.Start] = (DialogParts.Field("SelectRange_Start", Loc.Get("SelectRange_Start")), DialogParts.Caption("SelectRange_StartResult")),
            [RangeField.End] = (DialogParts.Field("SelectRange_End", Loc.Get("SelectRange_End")), DialogParts.Caption("SelectRange_EndResult")),
            [RangeField.Length] = (DialogParts.Field("SelectRange_Length", Loc.Get("SelectRange_Length")), DialogParts.Caption("SelectRange_LengthResult")),
        };
        var clamp = new Button { Content = Loc.Get("SelectRange_ClampEnd"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(clamp, "SelectRange_ClampEnd");
        RadioButton modeNew = DialogParts.Radio("SelectRange_ModeNew", Loc.Get("SelectRange_ModeNew"), "SelectRangeMode", true);
        RadioButton modeExtend = DialogParts.Radio("SelectRange_ModeExtend", Loc.Get("SelectRange_ModeExtend"), "SelectRangeMode", false);
        modeExtend.IsEnabled = editor.HasSelection;
        CheckBox scroll = DialogParts.Check("SelectRange_ScrollToStart", Loc.Get("SelectRange_ScrollToStart"), true);

        var body = new StackPanel { Spacing = 8, MinWidth = 380 };
        foreach ((TextBox box, TextBlock result) in fields.Values)
        {
            body.Children.Add(box);
            body.Children.Add(result);
        }

        body.Children.Add(clamp);
        body.Children.Add(modeNew);
        body.Children.Add(modeExtend);
        body.Children.Add(scroll);
        ContentDialog dialog = DialogParts.Dialog(Root, "SelectRangeDialog", Loc.Get("SelectRange_Title"), new ScrollViewer { Content = body },
            Loc.Get("SelectRange_Select"));

        bool updating = false;
        void Refresh(RangeField? edited)
        {
            updating = true;
            foreach ((RangeField field, (TextBox box, TextBlock result)) in fields)
            {
                if (field != edited && box.Text != model.TextOf(field))
                {
                    box.Text = model.TextOf(field);
                }

                // 計算で求めた欄は斜体 (仕様 3)。読み上げでも分かるよう項目の状態にも入れる。
                bool computed = field == model.Computed;
                box.FontStyle = computed ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
                AutomationProperties.SetItemStatus(box, computed ? Loc.Get("SelectRange_Computed") : string.Empty);
                RangeFieldIssue issue = model.IssueOf(field);
                DialogParts.MarkInvalid(box, issue != RangeFieldIssue.None);
                result.Text = issue switch
                {
                    RangeFieldIssue.Expression => DialogParts.ExpressionError(model.ErrorOf(field)!),
                    RangeFieldIssue.StartBeyondEnd => Loc.Format("SelectRange_Error_StartBeyondEnd", StatusFormat.Hex(model.DocumentLength)),
                    RangeFieldIssue.EndBeyondEnd => Loc.Format("SelectRange_Error_EndBeyondEnd", StatusFormat.Hex(model.DocumentLength)),
                    RangeFieldIssue.EndBeforeStart => Loc.Get("SelectRange_Error_EndBeforeStart"),
                    _ => model.ValueOf(field) is long v ? DialogParts.Interpretation(v) : string.Empty,
                };
            }

            clamp.Visibility = model.IssueOf(RangeField.End) == RangeFieldIssue.EndBeyondEnd ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = model.CanConfirm;
            updating = false;
        }

        foreach ((RangeField field, (TextBox box, _)) in fields)
        {
            box.TextChanged += (_, _) =>
            {
                // 計算した値を入れたときの TextChanged は後から届くため、値が変わっていなければ編集として扱わない。
                if (!updating && box.Text != model.TextOf(field))
                {
                    model.SetText(field, box.Text);
                    Refresh(field);
                }
            };
        }

        clamp.Click += (_, _) =>
        {
            model.ClampEndToDocument();
            Refresh(null);
        };
        Refresh(null);
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || !model.CanConfirm)
        {
            return;
        }

        model.Mode = modeExtend.IsChecked == true ? RangeSelectionMode.Extend : RangeSelectionMode.New;
        (long start, long length) = model.Result(editor.HasSelection ? (editor.SelectionStart, editor.SelectionLength) : null);

        // 確定: アンカーを開始、カーソルを終了の次に置く (仕様 7)。選択後に開始位置へ移動 (仕様 6)。
        editor.Select(start, length);
        if (scroll.IsChecked == true)
        {
            editor.ScrollToRow(start / editor.BytesPerRow);
        }

        FocusEditor();
    }

    // ---- バイトの挿入 (EDIT-14) ----

    private async Task<InsertBytesRequest?> ShowInsertBytesDialogAsync(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        var context = new EditorExpressionContext(editor);
        long at = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        TextBox position = DialogParts.Field("InsertBytes_Position", Loc.Get("InsertBytes_Position"), RangeSelectionModel.Format(at));
        TextBlock positionResult = DialogParts.Caption("InsertBytes_PositionResult");
        TextBox count = DialogParts.Field("InsertBytes_Count", Loc.Get("InsertBytes_Count"), App.Settings.GetString(InsertCountKey, "1"));
        TextBlock countResult = DialogParts.Caption("InsertBytes_CountResult");
        var content = new FillContentPanel(editor, "edit.insert.content", allowRepeatOptions: false);
        CheckBox select = DialogParts.Check("InsertBytes_SelectInserted", Loc.Get("InsertBytes_SelectInserted"), true);
        TextBlock preview = DialogParts.Caption("InsertBytes_Preview", monospace: true);
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        foreach (UIElement e in new UIElement[] { position, positionResult, count, countResult, content, select, preview })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "InsertBytesDialog", Loc.Get("InsertBytes_Title"), new ScrollViewer { Content = body },
            Loc.Get("InsertBytes_Insert"));
        InsertBytesRequest? request = null;
        void Validate()
        {
            request = null;
            bool posOk = DialogParts.TryEvaluate(position.Text, context, out long pos, out ExpressionException? posError);
            bool countOk = DialogParts.TryEvaluate(count.Text, context, out long n, out ExpressionException? countError);
            RangeEditError? error = posOk && countOk ? EditCommands.ValidateInsert(doc.Document, pos, n) : null;
            positionResult.Text = !posOk ? DialogParts.ExpressionError(posError!)
                : error == RangeEditError.PositionOutOfRange ? Loc.Get("RangeEdit_Error_PositionOutOfRange") : DialogParts.Interpretation(pos);
            countResult.Text = !countOk ? DialogParts.ExpressionError(countError!)
                : error is RangeEditError.CountTooLarge or RangeEditError.CountTooSmall
                    ? Loc.Format("RangeEdit_Error_" + error, StatusFormat.Number(EditCommands.MaxInsertCount(doc.Document), Culture))
                    : DialogParts.Interpretation(n);
            DialogParts.MarkInvalid(position, !posOk || error == RangeEditError.PositionOutOfRange);
            DialogParts.MarkInvalid(count, !countOk || error is RangeEditError.CountTooLarge or RangeEditError.CountTooSmall);
            FillSpec? spec = content.TryGetSpec();
            bool ok = posOk && countOk && error is null && spec is not null;
            preview.Text = ok ? Loc.Format("Dialog_Preview", PreviewOf(doc, spec!, pos, n)) : string.Empty;
            if (ok)
            {
                request = new InsertBytesRequest(pos, n, spec!, select.IsChecked == true);
            }

            dialog.IsPrimaryButtonEnabled = ok;
        }

        position.TextChanged += (_, _) => Validate();
        count.TextChanged += (_, _) => Validate();
        content.Changed += (_, _) => Validate();
        select.Checked += (_, _) => Validate();
        select.Unchecked += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
        {
            return null;
        }

        // 最後に使った内容とバイト数を記憶する (仕様 5)。
        content.Save();
        App.Settings.SetString(InsertCountKey, count.Text, "1");
        return request;
    }

    /// <summary>内容の先頭 64 バイトのプレビュー。ファイル・クリップボードの内容は読まずに種類だけを示す。</summary>
    private string PreviewOf(DocumentViewModel doc, FillSpec spec, long rangeStart, long length)
    {
        if (spec.Kind is FillKind.File or FillKind.Clipboard || length <= 0)
        {
            return Loc.Get("Fill_Kind_" + spec.Kind);
        }

        try
        {
            using EditContent content = BuilderFor(doc).Build(spec, rangeStart, Math.Min(64, length));
            return DialogParts.Hex(content.Preview(64));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return string.Empty;
        }
    }

    // ---- 塗りつぶし (EDIT-29) ----

    private async Task<FillRequest?> ShowFillDialogAsync(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        var context = new EditorExpressionContext(editor);
        bool hasSelection = editor.HasSelection;
        RadioButton targetSelection = DialogParts.Radio("Fill_TargetSelection", Loc.Get("Fill_TargetSelection"), "FillTarget", hasSelection);
        RadioButton targetRange = DialogParts.Radio("Fill_TargetRange", Loc.Get("Fill_TargetRange"), "FillTarget", !hasSelection);
        targetSelection.IsEnabled = hasSelection;

        // 選択範囲がなければ、対象範囲の欄にカーソル位置から 1 バイトを入れて開く (共通の約束 5)。
        long start0 = hasSelection ? editor.SelectionStart : editor.Cursor;
        long length0 = hasSelection ? editor.SelectionLength : 1;
        TextBox start = DialogParts.Field("Fill_RangeStart", Loc.Get("Fill_RangeStart"), RangeSelectionModel.Format(start0));
        TextBox length = DialogParts.Field("Fill_RangeLength", Loc.Get("Fill_RangeLength"), RangeSelectionModel.Format(length0));
        TextBlock rangeResult = DialogParts.Caption("Fill_RangeResult");
        var content = new FillContentPanel(editor, "edit.fill.content", allowRepeatOptions: true);
        TextBlock before = DialogParts.Caption("Fill_PreviewBefore", monospace: true);
        TextBlock after = DialogParts.Caption("Fill_PreviewAfter", monospace: true);
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement e in new UIElement[] { targetSelection, targetRange, start, length, rangeResult, content, before, after })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "FillDialog", Loc.Get("Fill_Title"), new ScrollViewer { Content = body, MaxHeight = 560 },
            Loc.Get("Fill_Fill"));
        FillRequest? request = null;
        void Validate()
        {
            request = null;
            bool useSelection = targetSelection.IsChecked == true;
            start.IsEnabled = length.IsEnabled = !useSelection;
            long s = editor.SelectionStart, n = editor.SelectionLength;
            string? rangeError = null;
            if (!useSelection)
            {
                bool sOk = DialogParts.TryEvaluate(start.Text, context, out s, out ExpressionException? sErr);
                bool nOk = DialogParts.TryEvaluate(length.Text, context, out n, out ExpressionException? nErr);
                rangeError = !sOk ? DialogParts.ExpressionError(sErr!)
                    : !nOk ? DialogParts.ExpressionError(nErr!)
                    : s < 0 || s > doc.Document.Length ? Loc.Get("RangeEdit_Error_PositionOutOfRange")
                    : n <= 0 ? Loc.Get("Fill_Error_EmptyRange")
                    : !doc.Document.CanResize && n > doc.Document.Length - s ? Loc.Get("Fill_Error_BeyondEndFixed")
                    : null;
                DialogParts.MarkInvalid(start, !sOk || s < 0 || s > doc.Document.Length);
                DialogParts.MarkInvalid(length, sOk && rangeError is not null && !(s < 0 || s > doc.Document.Length) || !nOk);
            }
            else
            {
                DialogParts.MarkInvalid(start, false);
                DialogParts.MarkInvalid(length, false);
            }

            rangeResult.Text = rangeError ?? Loc.Format("Fill_RangeInfo", StatusFormat.Hex(s), StatusFormat.Number(n, Culture));
            FillSpec? spec = content.TryGetSpec();
            bool ok = rangeError is null && spec is not null;
            if (ok)
            {
                // プレビュー: 対象範囲の先頭 64 バイトの変更前と変更後 (仕様 6)。
                int shown = (int)Math.Min(64, Math.Max(0, Math.Min(n, doc.Document.Length - s)));
                byte[] current = new byte[shown];
                doc.Document.Current.Read(s, current);
                before.Text = Loc.Format("Fill_PreviewBefore", DialogParts.Hex(current));
                after.Text = Loc.Format("Fill_PreviewAfter", PreviewOf(doc, spec!, s, n));
                request = new FillRequest(s, n, spec!);
            }
            else
            {
                before.Text = after.Text = string.Empty;
            }

            dialog.IsPrimaryButtonEnabled = ok;
        }

        targetSelection.Checked += (_, _) => Validate();
        targetRange.Checked += (_, _) => Validate();
        start.TextChanged += (_, _) => Validate();
        length.TextChanged += (_, _) => Validate();
        content.Changed += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
        {
            return null;
        }

        content.Save();
        return request;
    }

    // ---- ファイルサイズの変更 (EDIT-15) ----

    private async Task<ResizeRequest?> ShowResizeDialogAsync(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        var context = new EditorExpressionContext(editor);
        long current = doc.Document.Length;
        TextBox newLength = DialogParts.Field("Resize_Length", Loc.Get("Resize_Length"), RangeSelectionModel.Format(current));
        TextBlock info = DialogParts.Caption("Resize_Info");
        var content = new FillContentPanel(editor, "edit.resize.content", allowRepeatOptions: false);
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Resize_Current", StatusFormat.Number(current, Culture)) });
        body.Children.Add(newLength);
        body.Children.Add(info);
        body.Children.Add(new TextBlock { Text = Loc.Get("Resize_ExtensionHeader") });
        body.Children.Add(content);
        ContentDialog dialog = DialogParts.Dialog(Root, "ResizeDialog", Loc.Get("Resize_Title"), new ScrollViewer { Content = body },
            Loc.Get("Resize_Change"));
        ResizeRequest? request = null;
        void Validate()
        {
            request = null;
            bool ok = DialogParts.TryEvaluate(newLength.Text, context, out long n, out ExpressionException? error);
            RangeEditError? rangeError = ok ? EditCommands.ValidateResize(doc.Document, n) : null;
            ok &= rangeError is null;
            DialogParts.MarkInvalid(newLength, !ok);
            long diff = n - current;
            info.Text = error is not null ? DialogParts.ExpressionError(error)
                : rangeError is not null ? Loc.Get("RangeEdit_Error_" + rangeError)
                : diff > 0 ? Loc.Format("Resize_Grow", "+" + StatusFormat.Number(diff, Culture), StatusFormat.ShortSize(diff, Culture) ?? string.Empty)
                : diff < 0 ? Loc.Format("Resize_Shrink", StatusFormat.Number(-diff, Culture))
                : Loc.Get("Resize_Same");
            FillSpec? spec = content.TryGetSpec();
            content.Visibility = ok && diff <= 0 ? Visibility.Collapsed : Visibility.Visible;
            ok &= spec is not null;
            if (ok)
            {
                request = new ResizeRequest(n, spec!);
            }

            dialog.IsPrimaryButtonEnabled = ok;
        }

        newLength.TextChanged += (_, _) => Validate();
        content.Changed += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
        {
            return null;
        }

        content.Save();
        return request;
    }

    // ---- ファイルの内容の挿入 (EDIT-30) ----

    private async Task<InsertFileRequest?> ShowInsertFileDialogAsync(DocumentViewModel doc, string path)
    {
        long size;
        byte[] head;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            size = stream.Length;
            head = new byte[(int)Math.Min(64, size)];
            stream.ReadExactly(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }

        var context = new EditorExpressionContext(doc.Editor);
        TextBlock sizeText = DialogParts.Caption("InsertFile_Size");
        sizeText.Text = Loc.Format("InsertFile_Size", Path.GetFileName(path), StatusFormat.Number(size, Culture));
        TextBlock preview = DialogParts.Caption("InsertFile_Preview", monospace: true);
        preview.Text = Loc.Format("Dialog_Preview", DialogParts.Hex(head));
        TextBox offset = DialogParts.Field("InsertFile_Offset", Loc.Get("InsertFile_Offset"), "0");
        TextBox length = DialogParts.Field("InsertFile_Length", Loc.Get("InsertFile_Length"));
        length.PlaceholderText = Loc.Get("Fill_FileLengthPlaceholder");
        TextBlock result = DialogParts.Caption("InsertFile_Result");
        bool canInsert = doc.Document.CanResize;
        RadioButton insert = DialogParts.Radio("InsertFile_Insert", Loc.Get("InsertFile_Insert"), "InsertFileMode", canInsert && doc.Editor.InsertMode);
        RadioButton overwrite = DialogParts.Radio("InsertFile_Overwrite", Loc.Get("InsertFile_Overwrite"), "InsertFileMode", !canInsert || !doc.Editor.InsertMode);
        insert.IsEnabled = canInsert;
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        foreach (UIElement e in new UIElement[] { sizeText, preview, offset, length, result, insert, overwrite })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "InsertFileDialog", Loc.Get("InsertFile_Title"), body, Loc.Get("InsertFile_Run"));
        InsertFileRequest? request = null;
        void Validate()
        {
            request = null;
            bool offOk = DialogParts.TryEvaluate(offset.Text, context, out long off, out ExpressionException? offErr) && off >= 0 && off <= size;
            long n = size - Math.Max(0, off);
            bool lenOk = true;
            ExpressionException? lenErr = null;
            if (!string.IsNullOrWhiteSpace(length.Text))
            {
                lenOk = DialogParts.TryEvaluate(length.Text, context, out long requested, out lenErr) && requested > 0;
                n = Math.Min(n, requested);
            }

            DialogParts.MarkInvalid(offset, !offOk);
            DialogParts.MarkInvalid(length, !lenOk);
            bool ok = offOk && lenOk && n > 0;
            result.Text = offErr is not null ? DialogParts.ExpressionError(offErr)
                : lenErr is not null ? DialogParts.ExpressionError(lenErr)
                : !offOk ? Loc.Get("InsertFile_Error_Offset")
                : Loc.Format("InsertFile_Range", StatusFormat.Hex(off), StatusFormat.Number(Math.Max(0, n), Culture));
            if (ok)
            {
                request = new InsertFileRequest(off, n, insert.IsChecked == true);
            }

            dialog.IsPrimaryButtonEnabled = ok;
        }

        offset.TextChanged += (_, _) => Validate();
        length.TextChanged += (_, _) => Validate();
        insert.Checked += (_, _) => Validate();
        overwrite.Checked += (_, _) => Validate();
        Validate();
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary ? request : null;
    }
}
