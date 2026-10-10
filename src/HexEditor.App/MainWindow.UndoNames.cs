using HexEditor.App.Services;
using HexEditor.Core.Engine;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 「元に戻す」「やり直し」のメニューの項目名とツールバーのツールチップに、対象の操作名を入れる (EDIT-19 の仕様 11、「画面」)。
/// 例: 「元に戻す: 塗りつぶし」「やり直し: 入力」。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 履歴の操作名 (Core が記録する日本語の名前) と、表示言語の操作名のリソースキー。ここにない名前 (表示言語で記録したもの。
    /// インスペクタの書き換えなど) はそのまま出す。
    /// </summary>
    private static readonly Dictionary<string, string> EditOperationKeys = new(StringComparer.Ordinal)
    {
        ["入力"] = "EditOp_Typing",
        ["削除"] = "EditOp_Delete",
        ["挿入"] = "EditOp_Insert",
        ["上書き"] = "EditOp_Overwrite",
        ["貼り付け"] = "EditOp_Paste",
        ["上書き貼り付け"] = "EditOp_PasteOverwrite",
        ["塗りつぶし"] = "EditOp_Fill",
        ["パターンの挿入"] = "EditOp_InsertPattern",
        ["乱数の挿入"] = "EditOp_InsertRandom",
        ["乱数で塗りつぶし"] = "EditOp_FillRandom",
        ["バイトの挿入"] = "EditOp_InsertBytes",
        ["ファイルの内容の挿入"] = "EditOp_InsertFile",
        ["ファイルの内容で上書き"] = "EditOp_OverwriteFile",
        ["ファイルサイズの変更"] = "EditOp_Resize",
        ["カーソル位置で切り詰める"] = "EditOp_Truncate",
        ["レコードのアドレスに書く"] = "EditOp_WriteRecordAddress",
        ["移動"] = "EditOp_Move",
        ["コピー"] = "EditOp_Copy",
        ["切り取り"] = "EditOp_Cut",
        ["矩形挿入"] = "EditOp_InsertRectangle",
        ["00 で塗りつぶし"] = "EditOp_FillZero",
        [Document.DiscardDescription] = "EditOp_Discard",
        [Document.MergeDescription] = "EditOp_Merge",
        [Core.Compare.DiffMerger.CopyRightDescription] = "EditOp_CopyDiffRight",
        [Core.Compare.DiffMerger.CopyLeftDescription] = "EditOp_CopyDiffLeft",
    };

    private Document? _undoNamesSource;
    private string? _undoText;
    private string? _redoText;

    /// <summary>履歴の操作名を表示言語にする。</summary>
    internal static string EditOperationName(string description) =>
        EditOperationKeys.TryGetValue(description, out string? key) ? Loc.Get(key) : description;

    /// <summary>選んでいる文書の編集・元に戻す・やり直しで、項目名を更新する (選んでいる文書が変わったときに呼ぶ)。</summary>
    private void WatchUndoNames()
    {
        Document? document = Vm.Selected?.Document;
        if (!ReferenceEquals(document, _undoNamesSource))
        {
            if (_undoNamesSource is not null)
            {
                _undoNamesSource.Changed -= UndoNamesSource_Changed;
            }

            _undoNamesSource = document;
            if (document is not null)
            {
                document.Changed += UndoNamesSource_Changed;
            }
        }

        UpdateUndoNames();
    }

    private void UndoNamesSource_Changed(object? sender, DocumentChangedEventArgs e) => DispatcherQueue.TryEnqueue(UpdateUndoNames);

    /// <summary>「元に戻す: 操作名」「やり直し: 操作名」。対象がなければ元の名前。</summary>
    private void UpdateUndoNames()
    {
        EditHistory? history = Vm.Selected?.Document.History;
        if (_menus?.Find("edit.undo") is MenuFlyoutItem undo)
        {
            _undoText ??= undo.Text;
            undo.Text = history?.UndoDescription is { } u ? Loc.Format("Menu_Edit_UndoNamed", EditOperationName(u)) : _undoText;
        }

        if (_menus?.Find("edit.redo") is MenuFlyoutItem redo)
        {
            _redoText ??= redo.Text;
            redo.Text = history?.RedoDescription is { } r ? Loc.Format("Menu_Edit_RedoNamed", EditOperationName(r)) : _redoText;
        }

        foreach (AppBarButton button in Toolbar.PrimaryCommands.OfType<AppBarButton>())
        {
            string? name = (string)button.Tag switch
            {
                "edit.undo" when history?.UndoDescription is { } u => Loc.Format("Command_UndoNamed", EditOperationName(u)),
                "edit.redo" when history?.RedoDescription is { } r => Loc.Format("Command_RedoNamed", EditOperationName(r)),
                "edit.undo" or "edit.redo" => button.Label,
                _ => null,
            };
            if (name is not null)
            {
                string keys = HexEditor.App.Commands.CommandService.ShortcutText((string)button.Tag);
                ToolTipService.SetToolTip(button, keys.Length > 0 ? Loc.Format("Toolbar_ToolTip", name, keys) : name);
                AutomationProperties.SetHelpText(button, name);
            }
        }
    }
}
