using System.Text.Json.Nodes;
using HexEditor.Core.Selection;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>
/// 選択 (EDIT-05〜EDIT-09、EDIT-17、EDIT-18)・履歴パネル (EDIT-20)・クリップボードパネル (EDIT-28) のテスト用の命令。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>この領域の命令。知らない命令なら null。</summary>
    private JsonObject? HandleSelectionTestCommand(string cmd, JsonObject request) => cmd switch
    {
        // 履歴パネルの行 (番号・操作名・範囲・長さの変化・保存の印・現在の行)。
        "historyPanel" => HistoryVm.TestModel(),
        "historyGoTo" => Run(() => HistoryVm.GoTo((int)request["index"]!.GetValue<long>())),

        // クリップボードパネルの項目 (ユーザークリップボード 1〜9 と履歴)。
        "clipboardPanel" => ClipboardVm.TestModel(),
        _ => null,
    };

    /// <summary>状態の表示 (state) の document に、選択の形・要素・カーソルを足す。</summary>
    private void AddSelectionState(JsonObject document, EditorState e)
    {
        document["selectionKind"] = e.SelectionKind.ToString();
        document["selectionCount"] = e.SelectedRangeCount;
        document["selectedBytes"] = e.SelectedByteCount;
        document["caretCount"] = e.CaretCount;
        if (e.PrimaryRange is { } primary)
        {
            document["primaryStart"] = primary.Start;
            document["primaryLength"] = primary.Length;
        }

        // 要素は先頭から 1,000 個まで (それ以上はテストで使わない)。
        document["ranges"] = new JsonArray([.. e.SelectedRanges.Take(1000).Select(r => (JsonNode?)new JsonArray(r.Start, r.Length))]);
        document["carets"] = new JsonArray([.. e.Carets.Take(1000).Select(c => (JsonNode?)c.Offset)]);
        if (e.Rectangle is { } rect)
        {
            document["rectangle"] = new JsonObject
            {
                ["firstRow"] = rect.FirstRow,
                ["lastRow"] = rect.LastRow,
                ["firstColumn"] = rect.FirstColumn,
                ["lastColumn"] = rect.LastColumn,
            };
        }

        document["dropEffect"] = CurrentView()?.DropEffect;
        document["selectionSets"] = Vm.Selected is { } doc
            ? new JsonArray([.. SetsOf(doc).Sets.Select(s => (JsonNode?)new JsonObject
            {
                ["name"] = s.Name,
                ["count"] = s.Count,
                ["total"] = s.TotalLength,
            })])
            : null;
    }
}
