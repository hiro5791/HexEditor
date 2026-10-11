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
            case "searchResultsScroll":
                SearchResults.ScrollToFraction(request["fraction"]?.GetValue<double>() ?? 0);
                return new JsonObject();
            case "searchResultsContinue":
                _ = SearchResults.ContinueAsync();
                return new JsonObject();
            case "searchResultsExport":
                await SearchResults.ExportToAsync(request["format"]?.GetValue<string>() == "json" ? ExportFormat.Json : ExportFormat.Csv,
                    request["path"]!.GetValue<string>());
                return new JsonObject();
            case "searchResultsToBookmarks":
                if (request["target"]?.GetValue<string>() is { } target)
                {
                    SearchResults.SetTarget(target == "selected");
                }

                // 確認ダイアログを出す場合は、答えを待たずに返す (ダイアログはテストが dialogButton で押す)。
                Task converting = SearchResults.ToBookmarksAsync();
                if (request["noWait"]?.GetValue<bool>() != true)
                {
                    await converting;
                }

                SearchResults.SetTarget(null);
                return new JsonObject();
            case "searchResultsTab":
                if (request["pin"]?.GetValue<bool>() == true)
                {
                    SearchResults.TogglePin();
                }

                if (request["select"] is { } select)
                {
                    SearchResults.SelectTab((int)select.GetValue<long>());
                }

                if (request["close"] is { } close)
                {
                    SearchResults.CloseTab((int)close.GetValue<long>());
                }

                return TestSearchResults(new JsonObject());
            case "searchResultsResearch":
                _ = SearchResults.ResearchAsync();
                return new JsonObject();
            case "searchResultsOrder":
                if (request["sort"]?.GetValue<string>() is { } key)
                {
                    SearchResults.SortBy(Enum.Parse<SearchResultSortKey>(key, ignoreCase: true));
                }

                if (request["filter"]?.GetValue<string>() is { } filter)
                {
                    SearchResults.SetFilter(filter, immediately: true);
                }

                await SearchResults.WhenViewReadyAsync();
                return TestSearchResults(new JsonObject { ["from"] = 0L, ["count"] = request["count"]?.GetValue<long>() ?? 0L });
            case "searchResultsCopy":
                SearchResults.CopyRows(request["hex"]?.GetValue<bool>() ?? true);
                return new JsonObject();
            default:
                return await HandleSearchPanelTestCommandsAsync(cmd, request);
        }
    }

    private JsonObject TestFindBarState() => new()
    {
        ["open"] = FindBar.IsOpen,
        ["replaceMode"] = FindBar.IsReplaceMode,
        ["kind"] = FindBar.Kind.ToString(),
        ["policyHighlighted"] = FindBar.PolicyHighlighted,
        ["policyIndex"] = FindBar.PolicyIndex,
        ["policyChangeEnabled"] = FindBar.PolicyChangeEnabled,
        ["incrementalTruncated"] = FindBar.IncrementalTruncated,
        ["statusMessage"] = StatusMessageText,
        ["encodings"] = new JsonArray([.. FindBar.EncodingIds.Select(id => (JsonNode?)id)]),
        ["scopeOutline"] = new JsonArray([.. FindBar.OutlinedScope.Select(r => (JsonNode?)new JsonObject { ["offset"] = r.Offset, ["length"] = r.Length })]),
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
        long total = SearchResults.Count;
        long from = request["from"]?.GetValue<long>() ?? SearchResults.TopIndex;
        // 行の内容はドキュメントを読むため、指定したときだけ作る (状態だけを何度も読むテストで UI のスレッドを止めない)。
        long count = Math.Min(request["count"]?.GetValue<long>() ?? 0, Math.Max(0, SearchResults.ViewCount - from));
        var rows = new JsonArray();
        if (request["offsetsOnly"]?.GetValue<bool>() == true)
        {
            // 件数が多い場合はオフセットだけを返す (行の内容は作らない)。
            foreach (long offset in SearchResults.OffsetsAt(from, (int)count))
            {
                rows.Add(offset);
            }
        }
        else
        {
            for (long i = from; i < from + count; i++)
            {
                if (SearchResults.RowAt(i) is ({ } row, string document))
                {
                    rows.Add(new JsonObject
                    {
                        ["number"] = i + 1,
                        ["resultNumber"] = SearchResults.ResultNumberAt(i),
                        ["document"] = document,
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
            ["state"] = SearchResults.StateText,
            ["running"] = SearchResults.IsRunning,
            ["count"] = total,
            ["viewCount"] = SearchResults.ViewCount,
            ["canContinue"] = SearchResults.CanContinue,
            ["selected"] = SearchResults.SelectedIndex,
            ["top"] = SearchResults.TopIndex,
            ["visibleRows"] = SearchResults.VisibleRowCount,
            ["tabs"] = new JsonArray([.. SearchResults.TabTitles.Select(t => (JsonNode?)t)]),
            ["activeTab"] = SearchResults.ActiveTabIndex,
            ["pinned"] = SearchResults.IsPinned,
            ["stale"] = SearchResults.ResearchVisible,
            ["skipped"] = new JsonArray([.. SearchResults.SkippedRanges.Select(r => (JsonNode?)new JsonObject { ["offset"] = r.Offset, ["length"] = r.Length })]),
            ["statusMessage"] = StatusMessageText,
            ["rows"] = rows,
        };
    }
}
#endif
