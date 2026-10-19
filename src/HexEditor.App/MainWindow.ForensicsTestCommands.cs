#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Reports;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、フォレンジックの命令 (12 の FOR-03、FOR-04、FOR-05、FOR-18、FOR-19、FOR-33)。ダイアログは UI オートメーションで操作し、
/// ここでは状態の読み出しと、ダイアログを介さない実行 (ファイルの選択ダイアログの代わり) だけを持つ。
/// <list type="bullet">
/// <item><c>tcCase</c>: {action: state / create / open / close / addEvidence / select / removeSelected / setZone / setTimeMode / auditFailing / recover / verify / report / carve / panels}。</item>
/// <item><c>tcCarve</c>: {action: state / cells / select / signatures / reload}。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>テスト用: 監査ログの保存先 (書き込みエラー・空き容量不足を再現する)。</summary>
    private sealed class FailingAuditStorage : IAuditStorage
    {
        public bool Failing { get; set; }

        public void Append(string path, ReadOnlySpan<byte> line)
        {
            Check();
            FileAuditStorage.Instance.Append(path, line);
        }

        public string? ReadLastLine(string path, out bool endsWithNewline) => FileAuditStorage.Instance.ReadLastLine(path, out endsWithNewline);

        public void Probe(string path)
        {
            Check();
            FileAuditStorage.Instance.Probe(path);
        }

        private void Check()
        {
            if (Failing)
            {
                throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
            }
        }
    }

    private static readonly FailingAuditStorage s_failingStorage = new();

    private async Task<JsonObject?> HandleForensicsCaseTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "tcCase":
                return await TestCaseAsync(request);
            case "tcCarve":
                return TestCarve(request);
            default:
                return null;
        }
    }

    private static JsonNode? EvidenceJson(EvidenceItem e) => new JsonObject
    {
        ["id"] = e.Id,
        ["description"] = e.Description,
        ["kind"] = e.Kind.ToString(),
        ["path"] = e.Path,
        ["model"] = e.Model,
        ["serial"] = e.Serial,
        ["capacity"] = e.CapacityBytes,
        ["sectors"] = e.SectorCount,
        ["sectorSize"] = e.SectorSize,
        ["hashState"] = e.HashState.ToString(),
        ["hashes"] = new JsonArray([.. e.Hashes.Select(h => (JsonNode?)new JsonObject { ["algorithm"] = h.Algorithm, ["value"] = h.Value })]),
        ["addedUtc"] = e.AddedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["excluded"] = e.Excluded,
    };

    private JsonObject CaseState()
    {
        ForensicCase? c = ForensicsService.Cases.CurrentCase;
        var state = new JsonObject
        {
            ["caseOpen"] = c is not null,
            ["title"] = Title,
            ["statusCase"] = StatusCase.Content as string ?? string.Empty,
            ["statusCaseVisible"] = StatusCase.Visibility == Microsoft.UI.Xaml.Visibility.Visible,
            ["writesAllowed"] = ForensicsService.Audit.WritesAllowed,
            ["auditFaultOpen"] = AuditFaultBar.IsOpen,
            ["auditFaultClosable"] = AuditFaultBar.IsClosable,
            ["auditFaultTitle"] = AuditFaultBar.Title,
            ["timeMode"] = ForensicTimeContext.ModeToSetting(ForensicsService.TimeContext.Mode),
            ["writeBlock"] = ForensicsService.IsWriteBlockEnabled(),
        };
        if (c is not null)
        {
            state["number"] = c.Info.Number;
            state["name"] = c.Info.Name;
            state["investigator"] = c.Info.Investigator;
            state["timeZoneId"] = c.Info.TimeZoneId;
            state["folder"] = c.Folder;
            state["evidence"] = new JsonArray([.. c.Evidence.Select(EvidenceJson)]);
            state["allEvidence"] = new JsonArray([.. c.AllEvidence.Select(EvidenceJson)]);
        }

        if (_evidencePanel is { } panel)
        {
            state["evidenceColumns"] = new JsonArray([.. panel.ColumnHeaders.Select(h => (JsonNode?)h)]);
            state["evidenceRows"] = new JsonArray([.. Enumerable.Range(0, panel.Items.Count).Select(i => (JsonNode?)new JsonArray([.. panel.Cells(i).Select(x => (JsonNode?)x)]))]);
        }

        if (_auditPanel is { } audit)
        {
            state["auditColumns"] = new JsonArray([.. audit.ColumnHeaders.Select(h => (JsonNode?)h)]);
            state["auditCount"] = audit.RowCount;
            long from = Math.Max(0, audit.RowCount - 8);
            state["auditRows"] = new JsonArray([.. Enumerable.Range((int)from, (int)(audit.RowCount - from)).Select(i => (JsonNode?)new JsonArray([.. audit.Cells(i).Select(x => (JsonNode?)x)]))]);
            state["verifyText"] = audit.VerifyResultText;
            state["verifyError"] = audit.VerifyResultIsError;
        }

        return state;
    }

    private async Task<JsonObject> TestCaseAsync(JsonObject request)
    {
        switch (TStr(request, "action", "state"))
        {
            case "create":
                ForensicsService.Cases.Create(TStr(request, "folder"), new CaseInfo
                {
                    Number = TStr(request, "number", "C-0001"),
                    Name = TStr(request, "name", "テストケース"),
                    Investigator = TStr(request, "investigator", "tester"),
                    TimeZoneId = TStr(request, "timeZone", "Tokyo Standard Time"),
                });
                OnCaseOpened(created: true);
                break;
            case "open":
                OpenCaseFolder(TStr(request, "folder"));
                break;
            case "close":
                CloseCase();
                break;
            case "addEvidence":
                if (Vm.Selected is { } doc && EvidenceRegistrationOf(doc) is { } registration)
                {
                    string[] algorithms = request["hashes"] is JsonArray list ? [.. list.Select(n => n!.GetValue<string>())] : [];
                    await AddEvidenceAsync(doc, registration with { Description = TStr(request, "description", registration.Description) }, algorithms);
                }

                break;
            case "removeSelected":
                _evidencePanel?.ExcludeSelected();
                break;
            case "select":
                _evidencePanel?.SelectRow(TInt(request, "index"));
                break;
            case "setZone":
                if (ForensicsService.Cases.CurrentCase is { } current)
                {
                    SetCaseInfo(current.Info with { TimeZoneId = TStr(request, "timeZone") });
                }

                break;
            case "setTimeMode":
                App.Settings.SetString(ForensicTimeContext.SettingKey, TStr(request, "mode", "both"), "both");
                ForensicsService.ApplySettings();
                break;
            case "auditFailing":
                // 次の書き込みから失敗させる (空き容量不足)。ケースを開く前に呼べば、そのケースの監査ログがこの保存先を使う。
                s_failingStorage.Failing = request["on"]?.GetValue<bool>() ?? true;
                ForensicsService.Cases.Storage = s_failingStorage;
                break;
            case "recover":
                s_failingStorage.Failing = false;
                RetryAuditLog();
                break;
            case "verify":
                await Commands.ExecuteAsync("forensics.audit.verify");
                break;
            case "report":
                ReportOptions options = ReportOptionsFor(
                    new HashSet<string>(request["sections"] is JsonArray sections ? sections.Select(n => n!.GetValue<string>()) : ReportSections.All),
                    TStr(request, "format", "html") == "pdf", TStr(request, "paper", "a4") == "letter",
                    TStr(request, "folder", ForensicsService.Cases.CurrentCase?.ReportsFolder ?? string.Empty), null, null, TInt(request, "dump", 64));
                ReportResult? report = await CreateReportAsync(options);
                var info = CaseState();
                info["report"] = report is null ? null : new JsonObject
                {
                    ["path"] = report.Path,
                    ["format"] = report.Format.ToString(),
                    ["sha256"] = report.Sha256,
                    ["pdfError"] = report.PdfError,
                    ["attachments"] = new JsonArray([.. report.Attachments.Select(a => (JsonNode?)a)]),
                };
                return info;
            case "carve" or "carveStart":
                if (Vm.Selected is { } target)
                {
                    IReadOnlyList<CarvingSignature> chosen = request["signatures"] is JsonArray names
                        ? [.. names.Select(n => ForensicsService.Signatures.Find(n!.GetValue<string>())!)]
                        : ForensicsService.Signatures.Enabled;
                    CarveScopeKind scope = Enum.Parse<CarveScopeKind>(TStr(request, "scope", "WholeDocument"));
                    var carve = new CarveRequest(scope, chosen, request["skipInside"]?.GetValue<bool>() ?? false, request["export"]?.GetValue<bool>() ?? true, request["output"]?.GetValue<string>());
                    string? problem = PreflightCarving(target, carve);
                    if (problem is not null)
                    {
                        return new JsonObject { ["problem"] = problem };
                    }

                    if (TStr(request, "action") == "carveStart")
                    {
                        // 走査の完了を待たずに戻る (途中でキャンセルするテスト)。
                        _ = RunCarvingAsync(target, carve);
                        return new JsonObject { ["started"] = true };
                    }

                    CarvingResult? result = await RunCarvingAsync(target, carve);
                    return new JsonObject
                    {
                        ["count"] = result?.Files.Count,
                        ["cancelled"] = result?.Cancelled,
                        ["manifest"] = result?.ManifestPath,
                        ["manifestSha256"] = result?.ManifestSha256,
                        ["files"] = new JsonArray([.. (result?.Files.Enumerate() ?? []).Select(f => (JsonNode?)new JsonObject
                        {
                            ["number"] = f.Number,
                            ["type"] = f.Type,
                            ["offset"] = f.Offset,
                            ["length"] = f.Length,
                            ["path"] = f.RelativePath,
                            ["method"] = f.Method.ToString(),
                        })]),
                    };
                }

                break;
            case "panels":
                ShowPanel(EvidencePanelId, focus: false);
                ShowPanel(AuditLogPanelId, focus: false);
                break;
        }

        await Task.Yield();
        return CaseState();
    }

    private JsonObject TestCarve(JsonObject request)
    {
        switch (TStr(request, "action", "state"))
        {
            case "select":
                _carvingPanel?.SelectRow(TInt(request, "index"));
                break;
            case "signatures":
                return new JsonObject
                {
                    ["signatures"] = new JsonArray([.. ForensicsService.Signatures.All.Select(s => (JsonNode?)new JsonObject
                    {
                        ["name"] = s.Name,
                        ["builtIn"] = s.BuiltIn,
                        ["enabled"] = s.Enabled,
                        ["maxSize"] = s.MaxSize,
                        ["headers"] = new JsonArray([.. s.Headers.Select(h => (JsonNode?)h)]),
                    })]),
                };
            case "reload":
                {
                    var fresh = new SignatureStore(Program.Environment.Locations.Settings);
                    fresh.Load();
                    return new JsonObject
                    {
                        ["signatures"] = new JsonArray([.. fresh.All.Where(s => !s.BuiltIn).Select(s => (JsonNode?)new JsonObject
                        {
                            ["name"] = s.Name,
                            ["maxSize"] = s.MaxSize,
                            ["headers"] = new JsonArray([.. s.Headers.Select(h => (JsonNode?)h)]),
                        })]),
                        ["file"] = File.Exists(Path.Combine(Program.Environment.Locations.Settings, SignatureStore.FileName)),
                    };
                }
        }

        var state = new JsonObject { ["rows"] = _carvingPanel?.RowCount ?? ForensicsService.LiveCarving?.Count ?? 0 };
        if (_carvingPanel is { } panel)
        {
            state["detail"] = panel.DetailText;
            state["thumbnail"] = panel.ThumbnailVisible;
            long count = Math.Min(panel.RowCount, TInt(request, "max", 20));
            state["cells"] = new JsonArray([.. Enumerable.Range(0, (int)count).Select(i => (JsonNode?)new JsonArray([.. panel.Cells(i).Select(x => (JsonNode?)x)]))]);
        }

        state["running"] = _carvingRunning;
        state["selection"] = Vm.Selected?.Editor.HasSelection == true ? new JsonObject
        {
            ["start"] = Vm.Selected.Editor.SelectionStart,
            ["length"] = Vm.Selected.Editor.SelectionLength,
        } : null;
        return state;
    }
}
#endif
