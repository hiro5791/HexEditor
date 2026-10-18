#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.Core.Engine;

namespace HexEditor.App;

/// <summary>
/// 分岐する履歴 (EDIT-21)・パッチ (EDIT-42〜EDIT-44)・変換ビュー (EDIT-45) のテスト用の命令。ダイアログを開く命令は開いたらすぐに答える
/// (ダイアログの欄と ボタンは UI オートメーションと dialogButton で操作する)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>この領域の命令。知らない命令なら null。</summary>
    private Task<JsonObject?> HandleEditingPhase4TestCommandsAsync(string cmd, JsonObject request) => Task.FromResult(cmd switch
    {
        // 履歴パネルの「木で表示」の切り替え (on) と、木の節へ移動 (ダブルクリック・Enter と同じ処理)。
        "historyTreeView" => Run(() => HistoryVm.IsTreeView = request["on"]?.GetValue<bool>() ?? true),
        "historyGoToNode" => Run(() =>
        {
            if (HistoryVm.History?.FindNode(request["id"]!.GetValue<int>()) is { } node)
            {
                HistoryVm.GoToNode(node);
            }
        }),

        // 木の節の右クリックメニュー「名前を付ける」「固定する」「枝を削除」(ダイアログを開いて答える)。
        "historyNameNode" => Run(() =>
        {
            if (HistoryVm.History?.FindNode(request["id"]!.GetValue<int>()) is { } node)
            {
                _ = ((IHistoryPanelHost)this).NameNodeAsync(node);
            }
        }),
        "historyPinNode" => Run(() =>
        {
            if (HistoryVm.History is { } history && history.FindNode(request["id"]!.GetValue<int>()) is { } node)
            {
                history.SetPinned(node, request["pinned"]?.GetValue<bool>() ?? true);
            }
        }),
        "historyDeleteBranch" => Run(() =>
        {
            if (HistoryVm.History?.FindNode(request["id"]!.GetValue<int>()) is { } node)
            {
                _ = ((IHistoryPanelHost)this).DeleteBranchAsync(node);
            }
        }),

        // 開いている「履歴の節へ移動」・読み込みの警告、パッチの作成・適用のダイアログの状態。
        "historyDialog" => HistoryDialogTestModel(),
        "patchDialog" => PatchDialogTestModel(),

        // 変換ビューの状態 (定義・バッジ・ステータスバー・検索バーの表示)。
        "transformView" => TransformViewState(),
        _ => null,
    });

    private JsonObject TransformViewState()
    {
        JsonObject state = TransformViewTestModel();
        state["findBar"] = FindBar.TransformLabelText;
        state["inspectorTransformActive"] = _inspectorVm.TransformActive;
        return state;
    }
}
#endif
