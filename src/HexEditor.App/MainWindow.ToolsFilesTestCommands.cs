#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.Core.Printing;
using HexEditor.Core.Tools.Files;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、ファイルツール (TOOL-13〜17、TOOL-21)、印刷・PDF (TOOL-01〜03)、代替データストリーム (ENG-37) の命令。
/// ダイアログは UI オートメーションで操作し、ここでは読み出しと、入力欄を変えるのと同じ処理だけを持つ。
/// <list type="bullet">
/// <item><c>tcPrint</c>: {action: state / fontSize / range / color / text / paper / bytes / page / key / zoom / twoPages / focus / saveDefaults}。</item>
/// <item><c>tcAds</c>: {action: state / load / open / create / rename / delete / export / import / select}。</item>
/// <item><c>tcShred</c>: {action: state / run}。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleToolsFileTestCommandAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "tcPrint":
                return TestPrint(request);
            case "tcAds":
                return await TestAdsAsync(request);
            case "tcShred":
                return await TestShredAsync(request);
            default:
                return null;
        }
    }

    private JsonObject TestPrintState()
    {
        var state = new JsonObject { ["open"] = _printContent is not null };
        if (_printContent is not { } content || _printPreview is not { } preview)
        {
            return state;
        }

        state["selectionEnabled"] = content.TestSelectionEnabled;
        state["range"] = content.TestRange.ToString();
        state["hasPreview"] = true;
        state["page"] = preview.Page;
        state["pageCount"] = preview.PageCount;
        state["info"] = preview.InfoText;
        state["text"] = preview.PageText;
        state["hadUnreadable"] = preview.HadUnreadable;
        state["twoPages"] = preview.TwoPages;
        state["problem"] = _printProblem;
        state["refreshMs"] = _printRefreshMs;
        if (_printPlan is { } plan)
        {
            state["printedPages"] = plan.PrintedPages;
            state["bytesPerRow"] = plan.BytesPerRow;
            state["rowsPerPage"] = plan.RowsPerPage;
            state["needsConfirmation"] = plan.NeedsConfirmation;
            state["fontSize"] = plan.FontSize;
        }

        return state;
    }

    private JsonObject TestPrint(JsonObject request)
    {
        JsonObject? result = null;
        if (_printContent is { } content && _printPreview is { } preview)
        {
            switch (TStr(request, "action", "state"))
            {
                case "fontSize":
                    content.TestSetFontSize(request["size"]!.GetValue<double>());
                    break;
                case "range":
                    content.TestSetRange(Enum.Parse<PrintRangeKind>(TStr(request, "kind", "All")), request["text"]?.GetValue<string>());
                    break;
                case "color":
                    content.TestSetColor(request["color"]?.GetValue<bool>() ?? false);
                    break;
                case "text":
                    content.TestSetText(TStr(request, "slot"), TStr(request, "text"));
                    break;
                case "paper":
                    content.TestSetPaper(Enum.Parse<PaperKind>(TStr(request, "kind", "A4")));
                    break;
                case "bytes":
                    content.TestSetBytesPerRow(TInt(request, "value"));
                    break;
                case "page":
                    preview.GoTo(TestHookSettings.ReadLong(request["index"], 0));
                    break;
                case "zoom":
                    preview.SetZoom(TInt(request, "percent"));
                    break;
                case "twoPages":
                    preview.TwoPages = request["on"]?.GetValue<bool>() ?? true;
                    break;
                case "focus":
                    preview.FocusPreview();
                    break;
                case "key":
                    result = new JsonObject { ["handled"] = preview.HandleKey(Enum.Parse<Windows.System.VirtualKey>(TStr(request, "key"), ignoreCase: true)) };
                    break;
                case "pageNumber":
                    preview.EnterPageNumber(TStr(request, "text"));
                    break;
            }
        }

        JsonObject state = TestPrintState();
        if (result is not null)
        {
            foreach ((string key, JsonNode? value) in result.ToList())
            {
                result.Remove(key);
                state[key] = value;
            }
        }

        return state;
    }

    private async Task<JsonObject> TestAdsAsync(JsonObject request)
    {
        string action = TStr(request, "action", "state");
        var result = new JsonObject();
        switch (action)
        {
            case "load":
                ShowPanel(AdsPanelId);
                LoadAds(TStr(request, "path"));
                break;
            case "show":
                await ShowAdsAsync();
                break;
            case "open":
                await OpenStreamAsync(_adsFile!, TStr(request, "name"));
                break;
            case "create":
                result["created"] = await CreateStreamAsync(_adsFile!, TStr(request, "name"), open: request["open"]?.GetValue<bool>() ?? false);
                break;
            case "rename":
                result["renamed"] = await RenameStreamAsync(_adsFile!, TStr(request, "name"), TStr(request, "to"));
                break;
            case "delete":
                result["deleted"] = await DeleteStreamAsync(_adsFile!, TStr(request, "name"));
                break;
            case "export":
                result["exported"] = await ExportStreamAsync(_adsFile!, TStr(request, "name"), TStr(request, "path"));
                break;
            case "import":
                result["imported"] = await ImportStreamAsync(_adsFile!, TStr(request, "name"), TStr(request, "path"));
                break;
            case "select":
                _adsTable?.SelectRow(_adsRows.OfType<AdsRow>().FirstOrDefault(r => r.Info.Name == TStr(request, "name")));
                break;
        }

        result["file"] = _adsFile;
        result["supported"] = _adsSupported;
        result["summary"] = _adsSummary;
        result["rows"] = new JsonArray([.. _adsRows.OfType<AdsRow>().Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Info.Name,
            ["display"] = r.Info.DisplayName,
            ["size"] = r.Info.Size,
            ["head"] = HeadHex(r.Head),
            ["description"] = r.Description,
        })]);
        result["buttons"] = new JsonObject(_adsButtons.Select(b => new KeyValuePair<string, JsonNode?>(b.Key, b.Value.IsEnabled)));
        return result;
    }

    private Task<JsonObject> TestShredAsync(JsonObject request) =>
        Task.FromResult(new JsonObject { ["writeBlock"] = WriteBlockMode.IsEnabled });
}
#endif
