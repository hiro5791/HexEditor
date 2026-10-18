#if HEX_TEST_HOOKS
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.Core.Tools;
using HexEditor.Core.Tools.Checksums;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、電卓と変換・参照ツール (TOOL-22〜TOOL-28)、チェックサムファイル (TOOL-18・TOOL-19)、フォルダの比較 (TOOL-20) の命令。
/// 入力欄に入れる・ボタンを押すのと同じ処理を呼び、マウスは使わない。答えは見えている内容。
/// <list type="bullet">
/// <item><c>tcCalc</c>: {action: state / set / commit / history / write / goTo / selectLength / programmer / width / toggleBit / selectBits / readCursor / operation / operand / copy}。</item>
/// <item><c>tcConv</c>: {action: state / type / endian / codePage / read / write / search}。</item>
/// <item><c>tcFloat</c>: {action: state / format / decimal / hex / toggleBit / read / write}。</item>
/// <item><c>tcChars</c>: {action: state / encoding / select / activate / cell / utf16 / search / list / base}。</item>
/// <item><c>tcColor</c>: {action: state / hex / format / endian / read / write / search}。</item>
/// <item><c>tcTime</c>: {action: state / zone / endian / date / field / format / read / write / now}。</item>
/// <item><c>tcSums</c>: {action: create / verify / state / sort / filter / activate / export}、<c>tcFolder</c>: {action: compare / state / filter / activate}。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleToolsCalcTestCommandAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "tcCalc":
                return TestCalc(request);
            case "tcConv":
                return TestConv(request);
            case "tcFloat":
                return TestFloat(request);
            case "tcChars":
                return TestChars(request);
            case "tcColor":
                return TestColor(request);
            case "tcTime":
                return TestTime(request);
            case "tcSums":
                return await TestSumsAsync(request);
            case "tcFolder":
                return await TestFolderAsync(request);
            default:
                return await HandleToolsFileTestCommandAsync(cmd, request);
        }
    }

    private static string TStr(JsonObject request, string key, string fallback = "") => request[key]?.GetValue<string>() ?? fallback;

    private static int TInt(JsonObject request, string key, int fallback = 0) => (int)TestHookSettings.ReadLong(request[key], fallback);

    private JsonObject TestCalc(JsonObject request)
    {
        CalculatorPanel panel = OpenToolPanel<CalculatorPanel>(CalculatorPanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "set":
                panel.TestSetInput(TStr(request, "text"));
                break;
            case "commit":
                panel.Commit();
                break;
            case "history":
                panel.UseHistory(TInt(request, "index"));
                break;
            case "write":
                panel.TestConfigureWrite(TInt(request, "size", 4), request["big"]?.GetValue<bool>() ?? false);
                panel.WriteResult();
                break;
            case "goTo":
                panel.TestGoTo();
                break;
            case "selectLength":
                panel.TestSelectLength();
                break;
            case "programmer":
                panel.TestSetProgrammer(request["on"]?.GetValue<bool>() ?? true);
                break;
            case "width":
                panel.SetWidthAndSign(TInt(request, "bits", 32), request["signed"]?.GetValue<bool>() ?? false);
                break;
            case "toggleBit":
                panel.ToggleBit(TInt(request, "index"));
                break;
            case "selectBits":
                panel.SelectBits(TInt(request, "from"), TInt(request, "to"));
                break;
            case "readCursor":
                panel.TestReadCursor(request["big"]?.GetValue<bool>() ?? false);
                break;
            case "operand":
                panel.TestOperand(TStr(request, "text"));
                break;
            case "operation":
                panel.TestOperation(TStr(request, "name"));
                break;
            case "copy":
                panel.TestInvokeCopy(TStr(request, "id"));
                break;
            case "bitKey":
                panel.TestBitKey(TStr(request, "key"), request["shift"]?.GetValue<bool>() ?? false);
                break;
            case "focusBit":
                panel.TestFocusBit(TInt(request, "index"));
                break;
        }

        JsonObject state = panel.TestState();
        state["visible"] = IsPanelShown(CalculatorPanelId);
        return state;
    }

    private JsonObject TestConv(JsonObject request)
    {
        ConverterPanel panel = OpenToolPanel<ConverterPanel>(ConverterPanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "type":
                panel.TypeInto(TStr(request, "field"), TStr(request, "text"));
                break;
            case "endian":
                panel.SelectEndian(request["big"]?.GetValue<bool>() ?? false);
                break;
            case "codePage":
                panel.SelectCodePage(TStr(request, "id"));
                break;
            case "read":
                panel.ReadFromCursor();
                break;
            case "write":
                panel.WriteAtCursor();
                break;
            case "search":
                panel.SendToSearch();
                break;
        }

        return panel.TestState();
    }

    private JsonObject TestFloat(JsonObject request)
    {
        FloatPanel panel = OpenToolPanel<FloatPanel>(FloatPanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "format":
                panel.SelectFormat(TStr(request, "id"));
                break;
            case "decimal":
                panel.SetDecimal(TStr(request, "text"));
                break;
            case "hex":
                panel.SetHex(TStr(request, "text"));
                break;
            case "toggleBit":
                panel.ToggleBit(TInt(request, "index"));
                break;
            case "read":
                panel.ReadFromCursor();
                break;
            case "write":
                panel.WriteAtCursor();
                break;
        }

        return panel.TestState();
    }

    private JsonObject TestChars(JsonObject request)
    {
        CharTablePanel panel = OpenToolPanel<CharTablePanel>(CharTablePanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "encoding":
                panel.SelectEncoding(TStr(request, "id"));
                break;
            case "select":
                panel.SelectCell(TInt(request, "value"), request["lead"] is null ? null : TInt(request, "lead"));
                break;
            case "activate":
                panel.InsertSelected();
                break;
            case "utf16":
                panel.SetUtf16BigEndian(request["big"]?.GetValue<bool>() ?? false);
                break;
            case "focus":
                panel.FocusFirstCell(TInt(request, "value"));
                break;
            case "move":
                panel.TestMoveFocus(TInt(request, "delta"));
                break;
            case "cell":
                {
                    JsonObject cell = panel.TestCell(TInt(request, "value"), request["trail"]?.GetValue<bool>() ?? false);
                    cell["focusedName"] = panel.FocusedName();
                    return cell;
                }
        }

        return panel.TestState();
    }

    private JsonObject TestColor(JsonObject request)
    {
        ColorPanel panel = OpenToolPanel<ColorPanel>(ColorPanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "hex":
                panel.SetHexText(TStr(request, "text"));
                break;
            case "format":
                panel.SelectFormat(TStr(request, "id"));
                break;
            case "endian":
                panel.SetEndian(TStr(request, "id"), request["big"]?.GetValue<bool>() ?? false);
                break;
            case "read":
                panel.ReadFromCursor();
                break;
            case "write":
                panel.WriteAtCursor();
                break;
            case "search":
                panel.SearchColor();
                break;
        }

        return panel.TestState();
    }

    private JsonObject TestTime(JsonObject request)
    {
        TimePanel panel = OpenToolPanel<TimePanel>(TimePanelId)!;
        switch (TStr(request, "action", "state"))
        {
            case "zone":
                panel.SelectZone(TStr(request, "id"));
                break;
            case "endian":
                panel.SelectEndian(request["big"]?.GetValue<bool>() ?? false);
                break;
            case "date":
                panel.SetDateText(TStr(request, "text"));
                break;
            case "field":
                panel.SetField(TStr(request, "format"), TStr(request, "field"), TStr(request, "text"));
                break;
            case "format":
                panel.SelectFormat(TStr(request, "id"));
                break;
            case "read":
                panel.ReadFromCursor();
                break;
            case "write":
                panel.WriteAtCursor();
                break;
        }

        return panel.TestState();
    }

    private async Task<JsonObject> TestSumsAsync(JsonObject request)
    {
        switch (TStr(request, "action", "state"))
        {
            case "create":
                {
                    var options = new ChecksumCreateOptions
                    {
                        AlgorithmId = TStr(request, "algorithm", "sha256"),
                        Format = Enum.Parse<ChecksumFormat>(TStr(request, "format", "Gnu"), ignoreCase: true),
                        IncludeSubfolders = request["subfolders"]?.GetValue<bool>() ?? true,
                        BinaryMark = request["binary"]?.GetValue<bool>() ?? false,
                        Separator = TStr(request, "separator", "slash") == "backslash" ? ChecksumSeparator.Backslash : ChecksumSeparator.Slash,
                        LineEnding = TStr(request, "lineEnding", "lf") == "crlf" ? ChecksumLineEnding.CrLf : ChecksumLineEnding.Lf,
                    };
                    string[] targets = [.. request["targets"]!.AsArray().Select(t => t!.GetValue<string>())];
                    ChecksumCreateResult? result = await CreateChecksumsAsync(targets, options, confirmOverwrite: false);
                    return new JsonObject
                    {
                        ["written"] = new JsonArray([.. (result?.Written ?? []).Select(w => (JsonNode?)w)]),
                        ["skipped"] = new JsonArray([.. (result?.Skipped ?? []).Select(s => (JsonNode?)s.Path)]),
                        ["files"] = result?.FileCount ?? 0,
                    };
                }

            case "verify":
                await VerifyChecksumPathAsync(TStr(request, "path"), confirmSidecar: false);
                break;
            case "filter":
                _checksumTable?.FilterIndex = TInt(request, "index");
                break;
            case "sort":
                _checksumTable?.SortColumn(TInt(request, "column"), request["descending"]?.GetValue<bool>() ?? false);
                break;
            case "activate":
                if (_checksumTable is { } table && table.VisibleRows.Count > TInt(request, "index"))
                {
                    table.Activate(table.VisibleRows[TInt(request, "index")]);
                }

                break;
            case "csv":
                return new JsonObject { ["text"] = _checksumTable?.ToCsv() };
            case "json":
                return new JsonObject { ["text"] = _checksumTable?.ToJson() };
        }

        return TestTableState(_checksumTable, IsPanelShown(ChecksumResultsPanelId), _checksumRunning);
    }

    private async Task<JsonObject> TestFolderAsync(JsonObject request)
    {
        switch (TStr(request, "action", "state"))
        {
            case "compare":
                await CompareFoldersAsync(TStr(request, "left"), TStr(request, "right"), new FolderCompareOptions
                {
                    IncludeSubfolders = request["subfolders"]?.GetValue<bool>() ?? true,
                    Pattern = TStr(request, "pattern"),
                    UseHash = request["hash"]?.GetValue<bool>() ?? false,
                });
                break;
            case "filter":
                _folderTable?.FilterIndex = TInt(request, "index");
                break;
            case "activate":
                {
                    ToolResultTable? table = _folderTable;
                    FolderCompareRow? row = table?.VisibleRows.Cast<FolderCompareRow>().FirstOrDefault(r => r.RelativePath == TStr(request, "path"));
                    if (table is not null && row is not null)
                    {
                        table.Activate(row);
                    }

                    break;
                }
        }

        JsonObject state = TestTableState(_folderTable, IsPanelShown(FolderComparePanelId), _folderRunning);
        state["left"] = _folderLeft;
        state["right"] = _folderRight;
        var rows = new JsonArray();
        foreach (FolderCompareRow row in _folderRows.Cast<FolderCompareRow>())
        {
            rows.Add(new JsonObject
            {
                ["status"] = row.Status.ToString(),
                ["path"] = row.RelativePath,
                ["leftSize"] = row.LeftSize,
                ["rightSize"] = row.RightSize,
                ["firstDifference"] = row.FirstDifference,
                ["leftHash"] = row.LeftHash,
            });
        }

        state["allRows"] = rows;
        return state;
    }

    private JsonObject TestTableState(ToolResultTable? table, bool open, bool running)
    {
        var rows = new JsonArray();
        if (table is not null)
        {
            foreach (IReadOnlyList<(string Text, string? Glyph)> row in table.CellTexts())
            {
                rows.Add(new JsonArray([.. row.Select(c => (JsonNode?)new JsonObject { ["text"] = c.Text, ["glyph"] = c.Glyph })]));
            }
        }

        var state = new JsonObject
        {
            ["open"] = open,
            ["running"] = running,
            ["summary"] = table?.Summary ?? (ReferenceEquals(table, _checksumTable) ? _checksumSummaryText : _folderSummaryText),
            ["rows"] = rows,
            ["filter"] = table?.FilterIndex,
        };
        if (ReferenceEquals(table, _checksumTable))
        {
            state["checksumRows"] = new JsonArray([.. _checksumRows.Cast<ChecksumResultRow>().Select(r => (JsonNode?)new JsonObject
            {
                ["path"] = r.Path,
                ["status"] = r.Status.ToString(),
                ["expected"] = r.Expected,
                ["actual"] = r.Actual,
                ["size"] = r.Size,
                ["algorithm"] = r.AlgorithmId,
            })]);
            state["file"] = _checksumFile;
            state["format"] = _checksumDocument?.Format.ToString();
        }

        return state;
    }
}
#endif
