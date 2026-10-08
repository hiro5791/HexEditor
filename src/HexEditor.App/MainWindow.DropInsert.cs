using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Storage;

namespace HexEditor.App;

/// <summary>
/// Ctrl を押しながら Hex ビューにファイルをドロップしたときは、ファイルの内容をドロップ位置に挿入する (UI-34 の仕様 1、EDIT-18 の仕様 6、
/// EDIT-30)。読み取り専用・固定長の文書には受け付けない (UI-34 の仕様 2)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ドロップ位置の Hex ビューのバイト (Ctrl を押していて、選択中のタブの Hex ビューの上のとき)。そうでなければ null。</summary>
    private long? InsertDropOffset(DragEventArgs e)
    {
        if ((e.Modifiers & DragDropModifiers.Control) == 0 || SelectedView() is not { } view)
        {
            return null;
        }

        return view.OffsetAt(view, e.GetPosition(view));
    }

    /// <summary>挿入のドロップを受け付けるか (読み取り専用・固定長では受け付けない)。</summary>
    private bool CanInsertDrop() => Vm.Selected is { } doc && !doc.Editor.ReadOnly && doc.Document.CanResize;

    /// <summary>
    /// ドラッグ中の表示 (操作と、カーソルの横の文字)。挿入なら「挿入」、開くなら「開く」。挿入できない文書の上では受け付けない。
    /// </summary>
    internal (DataPackageOperation Operation, string? Caption) DropFeedback(bool insert) =>
        !insert ? (DataPackageOperation.Copy, Loc.Get("Drop_Open"))
        : CanInsertDrop() ? (DataPackageOperation.Copy, Loc.Get("Drop_Insert"))
        : (DataPackageOperation.None, null);

    /// <summary>ドロップしたファイル (先頭の 1 つ) の内容を <paramref name="offset"/> に挿入する (EDIT-30 のダイアログで範囲を選ぶ)。</summary>
    internal async Task InsertDroppedFileAsync(IReadOnlyList<IStorageItem> items, long offset)
    {
        if (Vm.Selected is not DocumentViewModel doc || items.OfType<StorageFile>().FirstOrDefault() is not { } file)
        {
            return;
        }

        if (!CanInsertDrop())
        {
            return;
        }

        doc.Editor.GoTo(Math.Clamp(offset, 0, doc.Document.Length));
        await InsertFileAsync(doc, file.Path);
    }
}
