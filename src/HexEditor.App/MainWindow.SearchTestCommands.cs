#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、検索・置換・結果一覧・検索履歴の命令 (FIND-20〜FIND-28 の UI のテスト)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>検索の命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleSearchTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "findBar":
                return TestFindBarState();
            case "replaceKey":
                FindBar.RecallReplacementHistory(request["key"]!.GetValue<string>() == "Up");
                return new JsonObject();
            case "findHistory":
                int index = (int)(request["index"]?.GetValue<long>() ?? 0);
                if (request["action"]?.GetValue<string>() == "delete")
                {
                    FindBar.DeleteHistory(index);
                }
                else
                {
                    FindBar.ChooseHistory(index);
                }

                return TestFindBarState();
            case "searchResults":
                return TestSearchResults(request);
            case "searchResultsKey":
                bool handled = SearchResults.HandleKey(Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true),
                    request["shift"]?.GetValue<bool>() ?? false, request["ctrl"]?.GetValue<bool>() ?? false);
                return new JsonObject { ["handled"] = handled };
            case "searchResultsClick":
                SearchResults.Click(request["index"]!.GetValue<long>(), request["shift"]?.GetValue<bool>() ?? false);
                return new JsonObject();
            case "searchResultsContinue":
                _ = SearchResults.ContinueAsync();
                return new JsonObject();
            case "searchResultsExport":
                await SearchResults.ExportToAsync(request["format"]?.GetValue<string>() == "json" ? ExportFormat.Json : ExportFormat.Csv,
                    request["path"]!.GetValue<string>());
                return new JsonObject();
            case "searchResultsCopy":
                SearchResults.CopyRows(request["hex"]?.GetValue<bool>() ?? true);
                return new JsonObject();
            default:
                return null;
        }
    }

    private JsonObject TestFindBarState() => new()
    {
        ["open"] = FindBar.IsOpen,
        ["replaceMode"] = FindBar.IsReplaceMode,
        ["kind"] = FindBar.Kind.ToString(),
        ["policyHighlighted"] = FindBar.PolicyHighlighted,
        ["incrementalTruncated"] = FindBar.IncrementalTruncated,
        ["history"] = new JsonArray([.. FindBar.HistoryTexts(HistoryList.Find).Select(t => (JsonNode?)t)]),
        ["replaceHistory"] = new JsonArray([.. FindBar.HistoryTexts(HistoryList.Replace).Select(t => (JsonNode?)t)]),
        ["conditions"] = FindBar.CurrentConditions() is { } c ? new JsonObject
        {
            ["kind"] = c.Kind.ToString(),
            ["encoding"] = c.Encoding.ToString(),
            ["caseSensitive"] = c.CaseSensitive,
            ["bits"] = c.IntegerBits,
            ["endian"] = c.Endian.ToString(),
        } : null,
    };

    /// <summary>結果一覧の状態と行 (from から count 行。既定は見えている行)。</summary>
    private JsonObject TestSearchResults(JsonObject request)
    {
        SearchResults? results = SearchResults.Results;
        long total = results?.LongCount ?? 0;
        long from = request["from"]?.GetValue<long>() ?? SearchResults.TopIndex;
        long count = Math.Min(request["count"]?.GetValue<long>() ?? SearchResults.VisibleRowCount, Math.Max(0, total - from));
        var rows = new JsonArray();
        if (request["offsetsOnly"]?.GetValue<bool>() == true && results is not null)
        {
            // 件数が多い場合はオフセットだけを返す (行の内容は作らない)。
            foreach (SearchMatch m in results.GetRange(from, (int)count))
            {
                rows.Add(m.Offset);
            }
        }
        else
        {
            for (long i = from; i < from + count; i++)
            {
                if (SearchResults.RowAt(i) is { } row)
                {
                    rows.Add(new JsonObject
                    {
                        ["number"] = row.Number,
                        ["offset"] = row.Offset,
                        ["offsetText"] = StatusFormat.Hex(row.Offset),
                        ["length"] = row.Length,
                        ["hex"] = row.Hex,
                        ["text"] = row.Text,
                        ["status"] = SearchResultsPanel.StatusText(row.Status),
                        ["statusKind"] = row.Status.ToString(),
                        ["endian"] = row.Variant,
                        ["value"] = row.Value,
                    });
                }
            }
        }

        return new JsonObject
        {
            ["visible"] = SearchResults.Visibility == Visibility.Visible,
            ["summary"] = SearchResults.SummaryText,
            ["state"] = results?.State.ToString(),
            ["running"] = SearchResults.IsRunning,
            ["count"] = total,
            ["canContinue"] = SearchResults.CanContinue,
            ["selected"] = SearchResults.SelectedIndex,
            ["top"] = SearchResults.TopIndex,
            ["visibleRows"] = SearchResults.VisibleRowCount,
            ["rows"] = rows,
        };
    }
}
#endif
