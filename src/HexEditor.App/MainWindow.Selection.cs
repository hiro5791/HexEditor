using System.Globalization;
using System.Runtime.CompilerServices;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 選択のコマンド: 選択範囲をずらす・広げる (EDIT-05)、矩形選択 (EDIT-06)、マルチ選択 (EDIT-07)、マルチカーソル (EDIT-08)、
/// 選択範囲の保存と読み込み (EDIT-09)、矩形挿入 (EDIT-17)、要素ごとの塗りつぶし・挿入 (EDIT-29 の仕様 4、EDIT-14 の仕様 4)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>今の編集位置を表示している Hex ビュー (分割表示ではアクティブなペイン)。</summary>
    private HexView? CurrentView() => _views.FirstOrDefault(v => v.Editor == Editor);

    /// <summary>「N バイトずらす」で最後に指定した量 (EDIT-05 の仕様 2。アプリの状態)。</summary>
    private const string ShiftAmountKey = "edit.selectionShift.last";

    /// <summary>要素数がこれを超える塗りつぶし・削除は長時間処理として行う (EDIT-07 の「巨大ファイル・長時間処理」)。</summary>
    private const int ManyElements = EditorState.LongRunningElements;

    /// <summary>ドキュメントごとの選択セット (EDIT-09)。初めて使うときに付随データから読む。</summary>
    private static readonly ConditionalWeakTable<Document, SelectionSetCollection> s_selectionSets = [];

    /// <summary>選択セットごとの保存 (UI スレッドの外で、間引いて書く。EDIT-09)。</summary>
    private static readonly ConditionalWeakTable<SelectionSetCollection, Core.Files.DebouncedWriter> s_selectionSetWriters = [];

    /// <summary>保存を間引く時間。</summary>
    private static readonly TimeSpan BackgroundSaveDelay = TimeSpan.FromMilliseconds(300);

    private void RegisterSelectionCommands()
    {
        string noSelection = Loc.Get("Command_NoSelection");
        InitializeSelectionDrop();
        _clipboard.LongRectangleInsert = PasteRectangleRowsAsync;
        CommandState NeedsSelection() => NeedsDocument(d => d.Editor.HasSelection ? null : noSelection);

        // ---- ずらす・広げる (EDIT-05) ----
        Commands.Register("edit.selection.shiftNext", () => ShiftSelectionStep(forward: true), NeedsSelection);
        Commands.Register("edit.selection.shiftPrevious", () => ShiftSelectionStep(forward: false), NeedsSelection);
        Commands.Register("edit.selection.shiftBy", ShowShiftSelectionDialogAsync, NeedsSelection);
        Commands.Register("edit.selection.resize", ShowResizeSelectionDialogAsync, NeedsSelection);
        Commands.Register("edit.selection.swapEnds", () => ReportSelection(Editor?.SwapAnchorAndCursor()), NeedsSelection);

        // ---- マルチ選択・矩形・マルチカーソル (EDIT-06〜EDIT-08) ----
        Commands.Register("edit.selection.invert", InvertSelectionAsync,
            () => NeedsDocument(d => d.Document.Length > 0 ? null : Loc.Get("Command_EmptyDocument")));
        Commands.Register("edit.selection.nextElement", () => ReportSelection(Editor?.NextElement()), NeedsSelection);
        Commands.Register("edit.selection.previousElement", () => ReportSelection(Editor?.PreviousElement()), NeedsSelection);
        Commands.Register("edit.selection.startRectangle", () =>
        {
            // 「矩形選択を開始」: カーソル位置を対角の一方にして、Shift+矢印で矩形を広げられるようにする (EDIT-06 の「呼び出し」)。
            if (Editor is { } editor && CurrentView() is { } view)
            {
                editor.BeginRectangle(editor.Cursor, editor.ActiveColumn);
                view.BeginRectangleKeys();
                FocusEditor();
            }
        }, NeedsDocument);
        Commands.Register("edit.selection.toMulti", ConvertRectangleToMulti,
            () => NeedsDocument(d => d.Editor.SelectionKind == SelectionKind.Rectangle ? null : Loc.Get("Command_NoRectangle")));
        Commands.Register("edit.caret.addAbove", () => ReportSelection(Editor?.AddCaretAbove()), NeedsDocument);
        Commands.Register("edit.caret.addBelow", () => ReportSelection(Editor?.AddCaretBelow()), NeedsDocument);
        Commands.Register("edit.caret.atElements", () => ReportSelection(Editor?.CaretsAtSelectionElements()), NeedsSelection);
        Commands.Register("edit.insertRectangle", InsertRectangleAsync,
            () => NeedsEditable(d => !d.Document.CanResize ? Loc.Get("Notice_FixedLength")
                : d.Editor.SelectionKind != SelectionKind.Rectangle ? Loc.Get("Command_NoRectangle") : null));

        // ---- 選択範囲の保存と読み込み (EDIT-09) ----
        Commands.Register("edit.selection.save", SaveSelectionSetAsync, NeedsSelection);
        Commands.Register("edit.selection.load", LoadSelectionSetAsync,
            () => NeedsDocument(d => SetsOf(d).Sets.Count > 0 ? null : Loc.Get("SelectionSets_None")));
        Commands.Register("edit.selection.export", ExportSelectionAsync, NeedsSelection);
        Commands.Register("edit.selection.import", ImportSelectionAsync, NeedsDocument);

        // 検索結果を選択範囲に変換 (FIND-21 の仕様 2。F2-12)。100 万範囲を超える分は先頭から上限までにして知らせる。
        Commands.Register("search.results.toSelection", () => SearchResults.ToSelection(),
            () => SearchResults.HasResults ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoSearchResults")));
        SearchResults.SelectionRequested += (_, e) =>
        {
            ActivateEditor(e.Editor);
            SelectionResult result = e.Editor.SetSelections(e.Ranges);

            // 変換で上限を超えた結果を数え上げなかった場合も、上限まで変換した旨を知らせる。
            ReportSelection(e.Truncated && result == SelectionResult.Done ? SelectionResult.Truncated : result);
            FocusEditor();
        };
    }

    /// <summary>
    /// 「選択を反転」(EDIT-07 の仕様 4)。上限を超えることが先に分かる場合は要素を数え上げずに知らせ、要素数が 10,000 を超える場合は
    /// 長時間処理で反転した集合を作ってから選択にする (「巨大ファイル・長時間処理」)。
    /// </summary>
    private async Task InvertSelectionAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        if (editor.CheckInvertLimit() is { } tooMany)
        {
            ReportSelection(tooMany);
            return;
        }

        if (!editor.InvertIsLongRunning)
        {
            ReportSelection(editor.InvertSelection());
            return;
        }

        SelectionSnapshot selection = editor.CaptureSelection();
        int max = editor.MaxSelectionElements;
        try
        {
            RangeSet? inverted = await Vm.Operations.RunAsync(Loc.Get("Operation_InvertSelection"), OperationKind.ReadOnly, doc.Document, null,
                op => Task.Run(() => EditorState.ComputeInversion(selection, max, op.CancellationToken)));
            if (doc.Document.Length != selection.DocumentLength)
            {
                // 処理の間に長さが変わった (反転した範囲は古い)。
                return;
            }

            ReportSelection(inverted is null ? SelectionResult.TooManyElements : editor.SetSelectionSet(inverted));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 「マルチ選択に変換」(EDIT-06 の仕様 7)。上限を超える場合は「矩形の行数 (N 行) がマルチ選択の上限 (M) を超えるため変換できません」
    /// (EDIT-06 の「エラー」)。
    /// </summary>
    private void ConvertRectangleToMulti()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        long rows = editor.SelectedRangeCount;
        SelectionResult result = editor.ConvertRectangleToMulti();
        if (result == SelectionResult.TooManyElements)
        {
            ShowNotice(Loc.Format("Notice_RectangleToMultiLimit", rows.ToString("N0", CultureInfo.CurrentCulture),
                editor.MaxSelectionElements.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, doc);
            return;
        }

        ReportSelection(result);
    }

    /// <summary>選択の操作の結果を InfoBar で知らせる (上限超過・範囲外)。</summary>
    private void ReportSelection(SelectionResult? result, string outOfRangeKey = "Notice_SelectionOutOfRange")
    {
        if (result is not { } r || Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        string? message = r switch
        {
            SelectionResult.OutOfRange => Loc.Get(outOfRangeKey),
            SelectionResult.TooManyElements => Loc.Format("Notice_TooManyElements", editor.MaxSelectionElements.ToString("N0", CultureInfo.CurrentCulture)),
            SelectionResult.Truncated => Loc.Format("Notice_SelectionTruncated", editor.MaxSelectionElements.ToString("N0", CultureInfo.CurrentCulture)),
            SelectionResult.TooManyCarets => Loc.Format("Notice_TooManyCarets", EditorState.MaxCarets.ToString("N0", CultureInfo.CurrentCulture)),
            _ => null,
        };
        if (message is not null)
        {
            ShowNotice(message, r == SelectionResult.Truncated ? InfoBarSeverity.Informational : InfoBarSeverity.Warning, doc);
        }
    }

    /// <summary>Hex ビューの選択の操作の拒否 (マウス・キー) と、マルチカーソルの入力の失敗。</summary>
    private void AttachSelectionEvents(HexView view)
    {
        view.SelectionRejected += (_, result) => ReportSelection(result);
        view.SelectionDragStarting += (_, e) => FillSelectionDragData(e);
        view.CaretInputFailed += (_, count) =>
            ShowNotice(Loc.Format("Notice_CaretInputFailed", count), InfoBarSeverity.Warning, Vm.Selected);
        view.LongRangeDeleteRequested += (_, action) => _ = DeleteManyRangesAsync(action);
    }

    /// <summary>
    /// 要素数の多いマルチ選択・矩形への Delete / Backspace を長時間処理として行う (EDIT-07・EDIT-17 の「巨大ファイル・長時間処理」)。
    /// </summary>
    private async Task DeleteManyRangesAsync(RangeDeleteAction action)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        if (action == RangeDeleteAction.ZeroFill)
        {
            await FillSelectedRangesAsync(doc, FillSpec.Zero, continueAcross: false, description: "削除");
        }
        else
        {
            await DeleteSelectedRangesAsync(doc, "削除");
        }
    }

    /// <summary>ドキュメントごとの選択の設定と、表示設定の変更での矩形の変換の通知 (EDIT-06 の仕様 5)。</summary>
    private static readonly ConditionalWeakTable<EditorState, object> s_selectionNoticeHooked = [];

    private void ApplySelectionSettings(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        editor.MaxSelectionElements = Math.Clamp(App.Settings.GetInt(EditingSettings.MaxSelectionElementsKey, EditorState.DefaultMaxSelectionElements),
            1_000, 10_000_000);
        editor.MaxRectangleRows = Math.Clamp(App.Settings.GetInt(EditingSettings.MaxRectangleRowsKey, EditorState.DefaultMaxRectangleRows), 1, 10_000_000);
        if (!s_selectionNoticeHooked.TryGetValue(editor, out _))
        {
            s_selectionNoticeHooked.Add(editor, new object());
            editor.SelectionNotice += (_, e) =>
            {
                if (WindowManager.OwnerOf(doc) is { } window)
                {
                    window.ShowNotice(Loc.Format("Notice_RectangleTooManyRows", e.Count.ToString("N0", CultureInfo.CurrentCulture),
                        e.Limit.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, doc);
                }
            };
        }
    }

    // ---- ずらす・広げる (EDIT-05) ----

    private void ShiftSelectionStep(bool forward)
    {
        if (Editor is not { } editor)
        {
            return;
        }

        bool useLast = App.Settings.GetString(EditingSettings.SelectionShiftAmountKey, "selectionLength") == "lastAmount";
        if (useLast && editor.LastShiftAmount is null && long.TryParse(AppState.GetString(ShiftAmountKey, string.Empty), out long saved))
        {
            editor.LastShiftAmount = saved;
        }

        ReportSelection(editor.ShiftSelectionStep(forward, useLast));
    }

    /// <summary>「選択範囲を N バイトずらす…」(EDIT-05 の仕様 2)。入力した量は記憶する。</summary>
    private async Task ShowShiftSelectionDialogAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        var context = new EditorExpressionContext(editor);
        TextBox amount = DialogParts.Field("ShiftSelection_Amount", Loc.Get("ShiftSelection_Amount"),
            AppState.GetString(ShiftAmountKey, RangeSelectionModel.Format(editor.PrimaryRange?.Length ?? 1)));
        TextBlock result = DialogParts.Caption("ShiftSelection_Result");
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(amount);
        body.Children.Add(result);
        ContentDialog dialog = DialogParts.Dialog(Root, "ShiftSelectionDialog", Loc.Get("ShiftSelection_Title"), body, Loc.Get("ShiftSelection_Shift"));
        long value = 0;
        void Validate()
        {
            bool ok = DialogParts.TryEvaluate(amount.Text, context, out value, out ExpressionException? error);
            DialogParts.MarkInvalid(amount, !ok);
            result.Text = ok ? DialogParts.Interpretation(value) : DialogParts.ExpressionError(error!);
            dialog.IsPrimaryButtonEnabled = ok && value != 0;
        }

        amount.TextChanged += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        AppState.SetString(ShiftAmountKey, value.ToString(CultureInfo.InvariantCulture));
        editor.LastShiftAmount = value;
        ReportSelection(editor.ShiftSelection(value));
        FocusEditor();
    }

    /// <summary>「選択範囲を広げる / 狭める…」(EDIT-05 の仕様 4)。</summary>
    private async Task ShowResizeSelectionDialogAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        var context = new EditorExpressionContext(editor);
        TextBox startBox = DialogParts.Field("ResizeSelection_Start", Loc.Get("ResizeSelection_Start"), "0");
        TextBox endBox = DialogParts.Field("ResizeSelection_End", Loc.Get("ResizeSelection_End"), "0");
        TextBlock result = DialogParts.Caption("ResizeSelection_Result");
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(startBox);
        body.Children.Add(endBox);
        body.Children.Add(result);
        ContentDialog dialog = DialogParts.Dialog(Root, "ResizeSelectionDialog", Loc.Get("ResizeSelection_Title"), body, Loc.Get("ResizeSelection_Apply"));
        long start = 0, end = 0;
        void Validate()
        {
            bool s = DialogParts.TryEvaluate(startBox.Text, context, out start, out ExpressionException? se);
            bool e = DialogParts.TryEvaluate(endBox.Text, context, out end, out ExpressionException? ee);
            DialogParts.MarkInvalid(startBox, !s);
            DialogParts.MarkInvalid(endBox, !e);
            result.Text = !s ? DialogParts.ExpressionError(se!) : !e ? DialogParts.ExpressionError(ee!) : string.Empty;
            dialog.IsPrimaryButtonEnabled = s && e && (start != 0 || end != 0);
        }

        startBox.TextChanged += (_, _) => Validate();
        endBox.TextChanged += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            // 広げる・狭めるの範囲外は「ずらせません」とは別の文で知らせる (EDIT-05 の仕様 4)。
            ReportSelection(editor.ResizeSelection(start, end), "Notice_ResizeOutOfRange");
            FocusEditor();
        }
    }

    // ---- 要素ごとの削除・塗りつぶし・挿入 (EDIT-07 の仕様 7、EDIT-17、EDIT-29 の仕様 4、EDIT-14 の仕様 4) ----

    /// <summary>
    /// マルチ選択・矩形のすべての要素を削除する。要素数が多い場合は長時間処理として新しい木を作ってから 1 回で入れる (共通の約束 2)。
    /// </summary>
    private async Task<bool> DeleteSelectedRangesAsync(DocumentViewModel doc, string description)
    {
        EditorState editor = doc.Editor;
        if (editor.CheckRectangleRows() is not null)
        {
            ShowRectangleRowLimit(doc);
            return false;
        }

        SelectionSnapshot selection = editor.CaptureSelection();
        if (selection.Count <= ManyElements)
        {
            ReportEdit(doc, editor.DeleteSelectedRanges(description));
            return true;
        }

        try
        {
            PreparedReplacement prepared = await Vm.Operations.RunAsync(Loc.Get("Operation_DeleteRanges"), OperationKind.ModifiesDocument, doc.Document,
                selection.TotalLength, op => Task.Run(() => EditorState.PrepareRangeDeletion(doc.Document, selection, op, ignoreEditLock: true, op.CancellationToken)),
                locked => doc.Document.SetEditLock(locked));
            editor.CommitRangeDeletion(prepared, selection, description);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 行数の多い矩形の挿入の貼り付け (EDIT-17 の仕様 3、「巨大ファイル・長時間処理」)。行ごとの挿入を長時間処理で 1 つの木にしてから 1 回で入れる。
    /// </summary>
    private async Task<EditResult> PasteRectangleRowsAsync(EditorState editor, IReadOnlyList<byte[]> rows)
    {
        if (Vm.Selected is not { } doc || doc.Document != editor.Document)
        {
            return editor.PasteRectangle(rows, overwrite: false);
        }

        long origin = editor.RectanglePasteOrigin;
        int bytesPerRow = editor.BytesPerRow;
        long total = rows.Sum(r => (long)r.Length);
        try
        {
            PreparedReplacement prepared = await Vm.Operations.RunAsync(Loc.Get("Operation_PasteRectangle"), OperationKind.ModifiesDocument, doc.Document,
                total, op => Task.Run(() => EditorState.PrepareRectangleRowsInsert(doc.Document, rows, origin, bytesPerRow, op, ignoreEditLock: true,
                    op.CancellationToken)), locked => doc.Document.SetEditLock(locked));
            editor.CommitRectangleRowsInsert(prepared, origin);
            return EditResult.Done;
        }
        catch (OperationCanceledException)
        {
            return EditResult.Ignored;
        }
    }

    /// <summary>
    /// 矩形を挿入モードで貼り付けた後の注記: 影響した行数と、後ろの行の配置がずれたこと (EDIT-17 の仕様 3・「画面」)。
    /// </summary>
    private void ShowRectangleInsertNote(DocumentViewModel doc, long rows) =>
        ShowNotice(Loc.Format("Notice_RectanglePasteInserted", rows.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Informational, doc);

    /// <summary>「矩形が N 行あり、上限 M 行を超えています。上書きの操作は使えます」(EDIT-17 の「エラー」)。</summary>
    private void ShowRectangleRowLimit(DocumentViewModel doc) =>
        ShowNotice(Loc.Format("Notice_RectangleRowLimit", doc.Editor.SelectedRangeCount.ToString("N0", CultureInfo.CurrentCulture),
            doc.Editor.MaxRectangleRows.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Error, doc);

    /// <summary>編集の結果のうち、知らせるもの (行数・カーソル数の上限、固定長)。</summary>
    private void ReportEdit(DocumentViewModel doc, EditResult result)
    {
        switch (result)
        {
            case EditResult.TooManyRows:
                ShowRectangleRowLimit(doc);
                break;
            case EditResult.TooManyCarets:
                ShowNotice(Loc.Format("Notice_TooManyCarets", EditorState.MaxCarets.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, doc);
                break;
            case EditResult.FixedLength:
                ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, doc);
                break;
            case EditResult.NotEditable:
                ShowNotice(Loc.Get("Notice_Busy"), InfoBarSeverity.Error, doc);
                break;
        }
    }

    /// <summary>
    /// マルチ選択・矩形の各要素を塗りつぶす (EDIT-29 の仕様 4)。<paramref name="continueAcross"/> なら内容を要素をまたいで続け、
    /// そうでなければ要素ごとに先頭から始める。要素数が 10,000 を超える場合と実データを作る場合は長時間処理にする。
    /// </summary>
    private async Task FillSelectedRangesAsync(DocumentViewModel doc, FillSpec spec, bool continueAcross, string description = "塗りつぶし")
    {
        EditorState editor = doc.Editor;
        SelectionSnapshot selection = editor.CaptureSelection();
        ContentBuilder builder = BuilderFor(doc);
        long total = selection.TotalLength;
        PreparedReplacement Prepare(LongRunningOperation? op)
        {
            if (continueAcross)
            {
                // 全体の長さの内容を 1 つ作り、要素ごとにその続きを切り出す。
                EditContent whole = builder.Build(spec, selection.Bounds.Start, total, doc.Document, op);
                long position = 0;
                return EditorState.PrepareRangeOverwrite(doc.Document, selection.Ranges, r =>
                {
                    EditContent part = whole.Slice(position, r.Length);
                    position += r.Length;
                    return part;
                }, op, ignoreEditLock: op is not null, op?.CancellationToken ?? default);
            }

            return EditorState.PrepareRangeOverwrite(doc.Document, selection.Ranges, r => builder.Build(spec, r.Start, r.Length, doc.Document),
                op, ignoreEditLock: op is not null, op?.CancellationToken ?? default);
        }

        try
        {
            PreparedReplacement prepared = selection.Count <= ManyElements && !ContentBuilder.NeedsGeneration(spec, total)
                ? Prepare(null)
                : await Vm.Operations.RunAsync(Loc.Get("Operation_Fill"), OperationKind.ModifiesDocument, doc.Document, total,
                    op => Task.Run(() => Prepare(op)), locked => doc.Document.SetEditLock(locked));
            doc.Document.CommitReplacements(prepared, description);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TempSpaceException ex)
        {
            ShowNotice(Loc.Format("Fill_Error_TempSpace", GigaBytes(ex.Required), GigaBytes(ex.Available)), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>
    /// マルチカーソルの各位置に同じ内容を挿入する (EDIT-14 の仕様 4)。1 つの編集グループ。
    /// </summary>
    private void InsertAtCarets(DocumentViewModel doc, FillSpec spec, long count)
    {
        EditorState editor = doc.Editor;
        ContentBuilder builder = BuilderFor(doc);
        long[] positions = [.. editor.Carets.Select(c => c.Offset).Distinct().Order()];
        ApplyEdit(doc, () =>
        {
            PreparedReplacement prepared = doc.Document.PrepareContentEdits(
                positions.Select(p => new ContentEdit(p, 0, builder.Build(spec, p, count, doc.Document))));
            doc.Document.CommitReplacements(prepared, "バイトの挿入");
        });
    }

    /// <summary>「矩形挿入」(EDIT-17 の仕様 4): 矩形の各行の左端の列に、バイトの挿入のダイアログで指定したバイト数を挿入する。</summary>
    private async Task InsertRectangleAsync()
    {
        if (Vm.Selected is not { } doc || doc.Editor.SelectionKind != SelectionKind.Rectangle || !EnsureResizable(doc))
        {
            return;
        }

        if (doc.Editor.CheckRectangleRows() is not null)
        {
            ShowRectangleRowLimit(doc);
            return;
        }

        if (await ShowInsertBytesDialogAsync(doc, doc.Editor.SelectedRangeCount) is not { } request)
        {
            return;
        }

        await InsertIntoRectangleAsync(doc, request.Spec, request.Count);
        FocusEditor();
    }

    private async Task InsertIntoRectangleAsync(DocumentViewModel doc, FillSpec spec, long count)
    {
        EditorState editor = doc.Editor;
        ContentBuilder builder = BuilderFor(doc);
        long rows = editor.SelectedRangeCount;
        try
        {
            PreparedReplacement prepared = rows <= ManyElements
                ? editor.PrepareRectangleInsert(row => builder.Build(spec, row, count, doc.Document))
                : await Vm.Operations.RunAsync(Loc.Get("Operation_InsertBytes"), OperationKind.ModifiesDocument, doc.Document, rows * count,
                    op => Task.Run(() => editor.PrepareRectangleInsert(row => builder.Build(spec, row, count, doc.Document), op, ignoreEditLock: true,
                        op.CancellationToken)), locked => doc.Document.SetEditLock(locked));
            doc.Document.CommitReplacements(prepared, "矩形挿入");
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- 選択範囲の保存と読み込み (EDIT-09) ----

    private SelectionSetCollection SetsOf(DocumentViewModel doc)
    {
        if (!s_selectionSets.TryGetValue(doc.Document, out SelectionSetCollection? sets))
        {
            sets = doc.FilePath is { } path ? SelectionSetCollection.Load(_documentData, path) : new SelectionSetCollection();
            s_selectionSets.AddOrUpdate(doc.Document, sets);
            var writer = new Core.Files.DebouncedWriter(BackgroundSaveDelay, ex => AppLog.Warning($"選択セットを保存できません: {ex.Message}"));
            s_selectionSetWriters.AddOrUpdate(sets, writer);
            sets.Changed += (_, _) =>
            {
                // 100 万要素の選択セットも UI スレッドで書かない (写しだけを作り、書くのは間引いてスレッドプールで)。
                if (doc.FilePath is { } p)
                {
                    writer.Request(sets.CaptureSave(_documentData, p, (doc.Document.Source as Core.Sources.FileByteSource)?.Stamp));
                }
            };
        }

        return sets;
    }

    /// <summary>「選択範囲を保存…」(EDIT-09 の仕様 1): 名前を入れて保存する。同じ名前があれば確認の上で上書きする。</summary>
    private async Task SaveSelectionSetAsync()
    {
        if (Vm.Selected is not { } doc || !doc.Editor.HasSelection)
        {
            return;
        }

        SelectionSetCollection sets = SetsOf(doc);
        TextBox name = DialogParts.Field("SaveSelection_Name", Loc.Get("SaveSelection_Name"), string.Empty, monospace: false);
        name.MaxLength = SelectionSetCollection.MaxNameLength;
        TextBlock note = DialogParts.Caption("SaveSelection_Note");
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(name);
        body.Children.Add(note);
        ContentDialog dialog = DialogParts.Dialog(Root, "SaveSelectionDialog", Loc.Get("SaveSelection_Title"), body, Loc.Get("SaveSelection_Save"));
        void Validate()
        {
            bool ok = SelectionSetCollection.IsValidName(name.Text);
            note.Text = ok && sets.Find(name.Text.Trim()) is not null ? Loc.Get("SaveSelection_Exists") : string.Empty;
            dialog.IsPrimaryButtonEnabled = ok;
        }

        name.TextChanged += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        SelectionSnapshot selection = doc.Editor.CaptureSelection();
        SelectionSetSaveResult result = sets.Save(name.Text, selection);
        if (result == SelectionSetSaveResult.Exists)
        {
            ConfirmChoice choice = await ConfirmAsync(new ConfirmRequest("SaveSelectionOverwrite", Loc.Get("SaveSelection_Title"),
                Loc.Format("SaveSelection_OverwriteBody", name.Text.Trim()), Loc.Get("SaveSelection_Overwrite"), null, Loc.Get("Common_Cancel")));
            if (choice != ConfirmChoice.Primary)
            {
                return;
            }

            result = sets.Save(name.Text, selection, overwrite: true);
        }

        if (result == SelectionSetSaveResult.TooMany)
        {
            ShowNotice(Loc.Format("SelectionSets_TooMany", SelectionSetCollection.MaxSets), InfoBarSeverity.Error, doc);
        }
        else if (result == SelectionSetSaveResult.Saved)
        {
            ShowNotice(Loc.Format("SelectionSets_Saved", name.Text.Trim()), InfoBarSeverity.Success, doc);
        }

        FocusEditor();
    }

    /// <summary>
    /// 「保存した選択範囲を読み込む…」(EDIT-09 の仕様 3〜5): 一覧 (名前、要素数、合計バイト数、保存日時) から選び、置き換えるか現在の選択に
    /// 追加する。削除・名前変更もできる。
    /// </summary>
    private async Task LoadSelectionSetAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        SelectionSetCollection sets = SetsOf(doc);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 280, MinWidth = 420 };
        AutomationProperties.SetAutomationId(list, "LoadSelection_List");
        AutomationProperties.SetName(list, Loc.Get("LoadSelection_ListName"));
        void Fill()
        {
            list.Items.Clear();
            foreach (SelectionSet set in sets.Sets)
            {
                string text = Loc.Format("LoadSelection_Item", set.Name, set.Count.ToString("N0", CultureInfo.CurrentCulture),
                    set.TotalLength.ToString("N0", CultureInfo.CurrentCulture), set.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
                var item = new ListViewItem { Content = text, Tag = set.Name };
                AutomationProperties.SetName(item, text);
                AutomationProperties.SetAutomationId(item, "LoadSelection_Item_" + set.Name);
                list.Items.Add(item);
            }

            if (list.Items.Count > 0)
            {
                list.SelectedIndex = 0;
            }
        }

        RadioButton replace = DialogParts.Radio("LoadSelection_Replace", Loc.Get("LoadSelection_Replace"), "LoadSelectionMode", true);
        RadioButton add = DialogParts.Radio("LoadSelection_Add", Loc.Get("LoadSelection_Add"), "LoadSelectionMode", false);
        var delete = new Button { Content = Loc.Get("LoadSelection_Delete") };
        AutomationProperties.SetAutomationId(delete, "LoadSelection_Delete");
        var rename = new Button { Content = Loc.Get("LoadSelection_Rename") };
        AutomationProperties.SetAutomationId(rename, "LoadSelection_Rename");
        TextBox newName = DialogParts.Field("LoadSelection_NewName", Loc.Get("LoadSelection_NewName"), string.Empty, monospace: false);
        newName.MaxLength = SelectionSetCollection.MaxNameLength;
        // 名前の変更に失敗した (名前が正しくない・同じ名前がある) ときは、名前の欄の下に理由を示す。
        TextBlock renameError = DialogParts.Caption("LoadSelection_RenameError");
        renameError.TextWrapping = TextWrapping.Wrap;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(delete);
        buttons.Children.Add(rename);

        // 編集でデータがずれても保存した選択セットのオフセットは調整しない (仕様 5。ブックマークと異なる) ことを注記する。
        TextBlock note = DialogParts.Caption("LoadSelection_Note");
        note.Text = Loc.Get("LoadSelection_Note");
        note.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        foreach (UIElement e in new UIElement[] { list, newName, renameError, buttons, replace, add, note })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "LoadSelectionDialog", Loc.Get("LoadSelection_Title"), new ScrollViewer { Content = body },
            Loc.Get("LoadSelection_Load"));
        string? Selected() => (list.SelectedItem as ListViewItem)?.Tag as string;
        void Update() => dialog.IsPrimaryButtonEnabled = delete.IsEnabled = rename.IsEnabled = Selected() is not null;
        list.SelectionChanged += (_, _) => Update();
        delete.Click += (_, _) =>
        {
            if (Selected() is { } n)
            {
                sets.Delete(n);
                Fill();
                Update();
            }
        };
        newName.TextChanged += (_, _) =>
        {
            renameError.Text = string.Empty;
            DialogParts.MarkInvalid(newName, false);
        };
        rename.Click += (_, _) =>
        {
            if (Selected() is not { } n)
            {
                return;
            }

            SelectionSetRenameResult renamed = sets.Rename(n, newName.Text);
            renameError.Text = renamed switch
            {
                SelectionSetRenameResult.InvalidName => Loc.Format("LoadSelection_RenameInvalid", SelectionSetCollection.MaxNameLength),
                SelectionSetRenameResult.Duplicate => Loc.Format("LoadSelection_RenameDuplicate", newName.Text.Trim()),
                _ => string.Empty,
            };
            DialogParts.MarkInvalid(newName, renameError.Text.Length > 0);
            if (renamed == SelectionSetRenameResult.Renamed)
            {
                Fill();
                Update();
            }
        };
        Fill();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || Selected() is not { } chosen || sets.Find(chosen) is not { } setToLoad)
        {
            return;
        }

        await ApplySelectionSetAsync(doc, setToLoad, add.IsChecked == true);
        FocusEditor();
    }

    /// <summary>
    /// 選択セットを選択にする。長さより後ろの要素は切り詰める・除外し、件数を InfoBar で知らせる (仕様 4)。要素が 10,000 個を超える置き換えは、
    /// 選択の集合を長時間処理で作ってから入れる (「巨大ファイル・長時間処理」: 100 万要素の読み込みは進捗とキャンセル)。
    /// </summary>
    private async Task ApplySelectionSetAsync(DocumentViewModel doc, SelectionSet set, bool add)
    {
        EditorState editor = doc.Editor;
        (IReadOnlyList<ByteRange> ranges, RectSelection? rect, int truncated, int removed) =
            SelectionSetCollection.Resolve(set, doc.Document.Length, editor.BytesPerRow, editor.Layout.RowShift);
        if (rect is { } r && !add)
        {
            HexLayout layout = editor.Layout;
            editor.SelectRectangle(layout.RowStart(r.FirstRow) + r.FirstColumn, layout.RowStart(r.LastRow) + r.LastColumn);
        }
        else if (!add && rect is null && ranges.Count > ManyElements)
        {
            long length = doc.Document.Length;
            int max = editor.MaxSelectionElements;
            try
            {
                (RangeSet built, bool cut) = await Vm.Operations.RunAsync(Loc.Get("Operation_LoadSelection"), OperationKind.ReadOnly, doc.Document, null,
                    op => Task.Run(() => EditorState.BuildSelectionSet(ranges, length, max, op.CancellationToken)));
                if (doc.Document.Length != length)
                {
                    return;
                }

                SelectionResult result = editor.SetSelectionSet(built);
                ReportSelection(result == SelectionResult.Done && cut ? SelectionResult.Truncated : result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        else
        {
            // 矩形を現在の選択に追加する場合も、要素数の上限で打ち切る (行の一覧は上限までしか作らない)。
            IEnumerable<ByteRange> items = rect is { } r2 ? r2.Ranges(doc.Document.Length) : ranges;
            ReportSelection(editor.SetSelections(items, add));
        }

        editor.RecordJump();
        if (truncated + removed > 0)
        {
            ShowNotice(Loc.Format("SelectionSets_Clipped", truncated + removed), InfoBarSeverity.Informational, doc);
        }
    }

    /// <summary>「選択範囲をエクスポート…」(EDIT-09 の仕様 6): CSV または JSON に書く。100 万要素でも書き流す。</summary>
    private async Task ExportSelectionAsync()
    {
        if (Vm.Selected is not { } doc || !doc.Editor.HasSelection)
        {
            return;
        }

        string? path = await PickSaveFileAsync("HexEditor.SelectionExport", "selection.csv",
            [(Loc.Get("SelectionFile_Csv"), ".csv"), (Loc.Get("SelectionFile_Json"), ".json")]);
        if (path is null)
        {
            return;
        }

        SelectionSnapshot selection = doc.Editor.CaptureSelection();
        bool json = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("Operation_ExportSelection"), OperationKind.WritesExternal, doc.Document, null, _ => Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                if (json)
                {
                    SelectionFile.WriteJson(stream, doc.DisplayName, selection.Ranges);
                }
                else
                {
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                    SelectionFile.WriteCsv(writer, selection.Ranges);
                }

                return true;
            }));
            ShowNotice(Loc.Format("SelectionFile_Exported", Path.GetFileName(path)), InfoBarSeverity.Success, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("SelectionFile_WriteError", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>「選択範囲をインポート…」(EDIT-09 の仕様 6、「エラー」): 書式の誤りは行番号を示して読み込まない。</summary>
    private async Task ImportSelectionAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        string? path = await PickContentFileAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            // 読み込みと書式の解釈は長時間処理で行う (100 万要素のファイルでも UI を止めない。EDIT-09 の「巨大ファイル・長時間処理」)。
            List<ByteRange> ranges = await Vm.Operations.RunAsync(Loc.Get("Operation_ImportSelection"), OperationKind.ReadOnly, doc.Document, null,
                op => Task.Run(async () =>
                {
                    string content = await File.ReadAllTextAsync(path, op.CancellationToken);
                    return SelectionFile.Parse(content, out _);
                }));
            var set = new SelectionSet(Path.GetFileNameWithoutExtension(path), ranges, null, DateTimeOffset.Now);
            await ApplySelectionSetAsync(doc, set, add: false);
            FocusEditor();
        }
        catch (OperationCanceledException)
        {
        }
        catch (SelectionImportException ex)
        {
            ShowNotice(Loc.Format("SelectionFile_ImportError", ex.Line, Loc.Get("SelectionFile_Error_" + ex.Error)), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("SelectionFile_ReadError", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    /// <summary>保存のファイルピッカー (テストでは --test-hooks の savePicker)。</summary>
    private async Task<string?> PickSaveFileAsync(string settingsIdentifier, string suggestedName, IReadOnlyList<(string Name, string Extension)> types)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId)
        {
            SettingsIdentifier = settingsIdentifier,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        foreach ((string name, string extension) in types)
        {
            picker.FileTypeChoices.Add(name, [extension]);
        }

        return (await picker.PickSaveFileAsync())?.Path;
    }
}
