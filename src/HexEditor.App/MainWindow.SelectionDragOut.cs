using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 選択範囲のドラッグ &amp; ドロップのうち、Hex ビューの外とのやり取り (EDIT-18 の仕様 4〜6): 他のタブへのドロップ (コピーとして挿入)、
/// 他のアプリへのドラッグ (コピーと同じ形式)、他のアプリからのテキストのドロップ (形式を選択して貼り付けをドロップ位置で開く)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>このアプリの選択範囲のドラッグの印の形式 (中身はドラッグの通し番号)。</summary>
    private const string SelectionDragFormat = "HexEditor.SelectionDrag";

    /// <summary>ドラッグ中の選択範囲 (範囲の参照)。アプリ全体で 1 つ (別のウィンドウのタブにもドロップできる)。</summary>
    private static ClipboardEntry? s_draggedSelection;
    private static long s_dragSerial;

    private void InitializeSelectionDrop()
    {
        Root.DragOver += SelectionDrop_DragOver;
        Root.Drop += async (_, e) => await SelectionDropAsync(e);
        Tabs.TabStripDragOver += SelectionDrop_DragOver;
        Tabs.TabStripDrop += async (_, e) => await SelectionDropOnTabAsync(e);
    }

    /// <summary>
    /// 他のアプリ・タブへのドラッグを始めた: コピー (EDIT-22) と同じ形式 (Meta、バイナリ、他のエディタ互換の形式、テキスト) を、コピーと同じ
    /// 大きさの上限でデータとして渡す (EDIT-18 の仕様 5)。データはコピーと同じ部品 (<see cref="ClipboardService.FillCopyFormatsAsync"/>) で作る。
    /// システムのクリップボードは変えない。
    /// </summary>
    private async void FillSelectionDragData(SelectionDragStartingEventArgs e)
    {
        EditorState editor = e.Editor;
        if (!editor.HasSelection || editor.HasMultipleRanges)
        {
            return;
        }

        long start = editor.SelectionStart, length = editor.SelectionLength;
        s_draggedSelection = ClipboardEntry.Capture(editor.Document, start, length);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetData(SelectionDragFormat, (++s_dragSerial).ToString(System.Globalization.CultureInfo.InvariantCulture));
        DragOperationDeferral? deferral = e.GetDeferral();
        try
        {
            _clipboard.CompatFormatsEnabled = App.Settings.GetBool(CompatClipboardFormats.SettingKey, true);
            await _clipboard.FillCopyFormatsAsync(e.Data, editor, start, length, serial: 0);
        }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning($"ドラッグのデータを作れません: {ex.Message}");
        }
        finally
        {
            deferral?.Complete();
        }
    }

    private void SelectionDrop_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        bool ours = e.DataView.Contains(SelectionDragFormat) && s_draggedSelection is not null;
        bool text = e.DataView.Contains(StandardDataFormats.Text);
        DocumentViewModel? target = sender == Tabs ? DocumentUnder(e) : Vm.Selected;
        bool overView = sender == Tabs || (SelectedView() is { } view && view.OffsetAt(view, e.GetPosition(view)) is not null);
        if ((ours || text) && overView && target is { Editor.ReadOnly: false })
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Get(ours ? "Drop_InsertCopy" : "Drop_PasteSpecial");
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
        else if (ours || text)
        {
            // 読み取り専用のドキュメントへのドロップは受け付けない (EDIT-18 の「エラー」)。
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }
    }

    /// <summary>タブ列の上の位置のタブの文書。</summary>
    private DocumentViewModel? DocumentUnder(DragEventArgs e)
    {
        for (int i = 0; i < Vm.Documents.Count; i++)
        {
            if (Tabs.ContainerFromIndex(i) is TabViewItem item)
            {
                Windows.Foundation.Point p = e.GetPosition(item);
                if (p.X >= 0 && p.Y >= 0 && p.X < item.ActualWidth && p.Y < item.ActualHeight)
                {
                    return Vm.Documents[i];
                }
            }
        }

        return null;
    }

    /// <summary>Hex ビューへのドロップ: 他のタブの選択範囲はドロップ位置に挿入し、他のアプリのテキストは形式を選択して貼り付けを開く。</summary>
    private async Task SelectionDropAsync(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || Vm.Selected is not { } doc || SelectedView() is not { } view
            || view.OffsetAt(view, e.GetPosition(view)) is not long offset || doc.Editor.ReadOnly)
        {
            return;
        }

        if (e.DataView.Contains(SelectionDragFormat) && s_draggedSelection is { Range: { } range })
        {
            // 同じドキュメントの中の移動・コピーは Hex ビューが処理する。
            if (ReferenceEquals(range.Owner, doc.Document))
            {
                return;
            }

            InsertDraggedSelection(doc, offset, range);
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            DragOperationDeferral deferral = e.GetDeferral();
            string text;
            try
            {
                text = await e.DataView.GetTextAsync();
            }
            finally
            {
                deferral.Complete();
            }

            // 他のアプリからのテキスト: ドロップ位置で形式を選択して貼り付けを開く (EDIT-18 の仕様 6)。
            doc.Editor.GoTo(Math.Clamp(offset, 0, doc.Document.Length));
            await PasteSpecialAsync(doc, new SpecialClipboard(text, null, []));
        }
    }

    /// <summary>他のタブへのドロップ: そのタブのドキュメントのカーソル位置に挿入 (コピー) する (EDIT-18 の仕様 4)。</summary>
    private Task SelectionDropOnTabAsync(DragEventArgs e)
    {
        if (e.DataView.Contains(SelectionDragFormat) && s_draggedSelection is { Range: { } range } && DocumentUnder(e) is { } target
            && !target.Editor.ReadOnly && !ReferenceEquals(range.Owner, target.Document))
        {
            InsertDraggedSelection(target, target.Editor.Cursor, range);
        }

        return Task.CompletedTask;
    }

    private void InsertDraggedSelection(DocumentViewModel doc, long offset, Core.Engine.SnapshotRange range)
    {
        EditorState editor = doc.Editor;
        if (!doc.Document.CanResize)
        {
            ShowNotice(Loc.Get("Notice_FixedLength"), InfoBarSeverity.Error, doc);
            return;
        }

        bool insert = editor.InsertMode;
        if (!insert)
        {
            editor.ToggleInsertMode();
        }

        editor.GoTo(Math.Clamp(offset, 0, doc.Document.Length));
        ReportEdit(doc, editor.Paste(range, overwrite: false));
        if (!insert)
        {
            editor.ToggleInsertMode();
        }
    }
}
