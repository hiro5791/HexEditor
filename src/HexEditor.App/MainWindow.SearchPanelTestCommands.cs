#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、フェーズ 2 の検索の命令: 検索バーの複数の文字コード・複数語・位置の条件 (FIND-08、FIND-17、FIND-26)、
/// 文字列の抽出 (FIND-32)、複数ファイル検索・置換 (FIND-30、FIND-31)。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleSearchPanelTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "findBarPhase2":
                if (request["multiEncodings"] is JsonArray encodings)
                {
                    FindBar.SetMultiEncodings([.. encodings.Select(e => e!.GetValue<string>())]);
                }

                if (request["terms"] is JsonArray terms)
                {
                    FindBar.SetTerms([.. terms.OfType<JsonObject>().Select(t => (
                        Enum.Parse<SearchKind>(t["kind"]!.GetValue<string>(), ignoreCase: true),
                        t["text"]!.GetValue<string>(),
                        t["encoding"]?.GetValue<string>()))]);
                }

                if (request["importTerms"]?.GetValue<string>() is { } path)
                {
                    FindBar.ImportTerms(path, json: false);
                }

                return new JsonObject
                {
                    ["queryLabel"] = FindBar.QueryLabel,
                    ["positionText"] = FindBar.PositionText,
                    ["positionX"] = FindBar.PositionInputs.X,
                    ["positionY"] = FindBar.PositionInputs.Y,
                    ["positionValid"] = FindBar.PositionIsValid,
                    ["multiEncodings"] = new JsonArray([.. FindBar.MultiEncodings.Select(e => (JsonNode?)e)]),
                    ["multiTerm"] = FindBar.IsMultiTerm,
                    ["termCount"] = FindBar.Terms.Count,
                    ["hasPattern"] = FindBar.HasPattern,
                    ["regexSingleline"] = FindBar.RegexSinglelineChecked,
                    ["encodingWarning"] = FindBar.EncodingWarningMessage,
                    ["statusMessage"] = StatusMessageText,
                    ["variantCounts"] = new JsonArray([.. SearchResults.VariantCounts.Select(v => (JsonNode?)new JsonObject { ["name"] = v.Name, ["count"] = v.Count })]),
                };

            case "stringsPanel":
                if (request["show"]?.GetValue<bool>() == true)
                {
                    ShowPanel(StringsPanelId);
                }

                if (request["encodings"] is JsonArray ids)
                {
                    StringsExtraction.SetEncodings([.. ids.Select(i => i!.GetValue<string>())]);
                }

                StringsExtraction.SetOptions((int?)request["minLength"]?.GetValue<long>(), request["nulOnly"]?.GetValue<bool>(), request["newlines"]?.GetValue<bool>());
                if (request["extract"]?.GetValue<bool>() == true)
                {
                    _ = StringsExtraction.ExtractAsync();
                    await Task.Yield();
                }

                if (request["cancel"]?.GetValue<bool>() == true)
                {
                    StringsExtraction.Cancel();
                }

                if (request["click"] is { } click)
                {
                    StringsExtraction.Results.Click(click.GetValue<long>(), false);
                }

                return TestResultsOf(StringsExtraction.Results, request);

            case "multiFile":
                if (request["show"]?.GetValue<bool>() == true)
                {
                    OpenMultiFile(request["mode"]?.GetValue<string>() == "replace");
                }

                MultiFileSearch.Configure(request);
                if (request["check"] is JsonObject check)
                {
                    MultiFileSearch.SetChecked(check["file"]!.GetValue<string>(), (int?)check["match"]?.GetValue<long>(), check["value"]!.GetValue<bool>());
                }

                if (request["search"]?.GetValue<bool>() == true)
                {
                    _ = MultiFileSearch.SearchAsync();
                    await Task.Yield();
                }

                if (request["runReplace"]?.GetValue<bool>() == true)
                {
                    // 確認ダイアログを出すので、答えを待たずに返す (ダイアログはテストが dialogButton で押す)。
                    _ = MultiFileSearch.ReplaceAsync();
                    await Task.Yield();
                }

                if (request["open"] is JsonObject open)
                {
                    MultiFileSearch.OpenRow(open["file"]!.GetValue<string>(), (int)open["match"]!.GetValue<long>());
                }

                return TestMultiFileState();

            default:
                return null;
        }
    }

    private JsonObject TestMultiFileState()
    {
        MultiFileSearchResults? r = MultiFileSearch.Results;
        var rows = new JsonArray();
        foreach (MultiFileRow row in MultiFileSearch.Rows)
        {
            rows.Add(new JsonObject
            {
                ["file"] = row.IsFile,
                ["path"] = row.File.Path,
                ["offset"] = row.Match?.Offset,
                ["length"] = row.Match?.Length,
                ["title"] = row.Title,
                ["checked"] = row.Checked,
            });
        }

        return new JsonObject
        {
            ["visible"] = IsPanelShown(MultiFilePanelId),
            ["running"] = MultiFileSearch.IsRunning,
            ["state"] = r?.State.ToString(),
            ["files"] = r?.Files.Count ?? 0,
            ["matches"] = r?.MatchCount ?? 0,
            ["processed"] = r?.ProcessedFiles ?? 0,
            ["found"] = r?.FoundFiles ?? 0,
            ["withoutMatches"] = r?.FilesWithoutMatches ?? 0,
            ["skipped"] = new JsonArray([.. (r?.Skipped ?? []).Select(s => (JsonNode?)new JsonObject { ["path"] = s.Path, ["reason"] = s.Reason.ToString() })]),
            ["summary"] = MultiFileSearch.SummaryText,
            ["error"] = MultiFileSearch.ErrorText,
            ["outcomes"] = new JsonArray([.. MultiFileSearch.Outcomes.Select(o => (JsonNode?)new JsonObject
            {
                ["path"] = o.Path,
                ["status"] = o.Status.ToString(),
                ["count"] = o.Count,
                ["reason"] = o.Reason?.ToString(),
                ["backup"] = o.BackupPath,
            })]),
            ["operationDetails"] = new JsonArray([.. Vm.Operations.History.Concat(Vm.Operations.Active)
                .Select(o => (JsonNode?)(o.Name + "|" + (o.Detail ?? string.Empty)))]),
            ["rows"] = rows,
        };
    }

    /// <summary>結果一覧の部品の状態 (文字列の抽出の一覧など、検索結果の一覧と同じ形)。</summary>
    private static JsonObject TestResultsOf(SearchResultsPanel panel, JsonObject request)
    {
        long from = request["from"]?.GetValue<long>() ?? 0;
        long count = Math.Min(request["count"]?.GetValue<long>() ?? 0, Math.Max(0, panel.ViewCount - from));
        var rows = new JsonArray();
        for (long i = from; i < from + count; i++)
        {
            if (panel.RowAt(i) is ({ } row, string _))
            {
                rows.Add(new JsonObject
                {
                    ["offset"] = row.Offset,
                    ["length"] = row.Length,
                    ["text"] = row.Text,
                    ["variant"] = row.Variant,
                    ["chars"] = row.Chars,
                });
            }
        }

        return new JsonObject
        {
            ["visible"] = panel.Visibility == Visibility.Visible,
            ["summary"] = panel.SummaryText,
            ["state"] = panel.StateText,
            ["running"] = panel.IsRunning,
            ["count"] = panel.Count,
            ["rows"] = rows,
        };
    }
}
#endif
