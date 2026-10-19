#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Forensics.Images;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、ディスクイメージ (12 の FOR-20〜26) の命令。ダイアログは UI オートメーションで操作し、ここでは
/// 処理を待たずに始める (detach) 命令と、結果の読み出しを用意する。ディスクは偽のデバイス (fakeDevices) だけを使う。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleImageTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "imgRun":
            {
                // コマンドを、完了を待たずに実行する (ダイアログを UI オートメーションで操作するため)。
                string id = request["id"]!.GetValue<string>();
                _ = Commands.ExecuteAsync(id).ContinueWith(t => AppLog.Warning($"imgRun {id} failed: {t.Exception?.GetBaseException().Message}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
                return new JsonObject { ["started"] = true };
            }

            case "imgCreate":
            {
                // ダイアログで選んだのと同じ取得 (RunAcquisitionAsync)。キャンセル後の「残す・削除する」は本物のダイアログで尋ねる。
                if (Vm.Selected is not { } doc)
                {
                    return new JsonObject { ["error"] = "no document" };
                }

                var hashes = request["hashes"] is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToList() : [.. ImageHashes.Default];
                var create = new CreateImageRequest
                {
                    Source = doc,
                    Scope = Enum.Parse<AcquisitionScope>(request["scope"]?.GetValue<string>() ?? "Full", true),
                    SelectionOnly = request["selectionOnly"]?.GetValue<bool>() ?? false,
                    Options = new AcquisitionOptions
                    {
                        Output = request["output"]!.GetValue<string>(),
                        Format = Enum.Parse<ImageFormat>(request["format"]?.GetValue<string>() ?? "Raw", true),
                        SplitSize = ImgParseSize(request["splitSize"]?.GetValue<string>() ?? "2G") ?? 2L << 30,
                        Hashes = hashes,
                        Verify = request["verify"]?.GetValue<bool>() ?? true,
                        Compression = Enum.Parse<EwfCompression>(request["compression"]?.GetValue<string>() ?? "Fast", true),
                        Case = new EwfCaseInfo
                        {
                            CaseNumber = request["caseNumber"]?.GetValue<string>() ?? string.Empty,
                            EvidenceNumber = request["evidenceNumber"]?.GetValue<string>() ?? string.Empty,
                        },
                    },
                };
                LastAcquisition = null;
                Task<AcquisitionResult?> run = RunAcquisitionAsync(create);
                if (request["wait"]?.GetValue<bool>() == true)
                {
                    await run;
                }

                return ImageTestState();
            }

            case "imgCancel":
            {
                // 名前に text を含む実行中の処理を、利用者の操作と同じ経路 (確認があれば確認する) で取り消す。
                string text = request["text"]?.GetValue<string>() ?? string.Empty;
                int count = 0;
                foreach (Core.Operations.LongRunningOperation op in Vm.Operations.Active.Where(o => o.Name.Contains(text, StringComparison.Ordinal)))
                {
                    _ = op.RequestCancelAsync();
                    count++;
                }

                return new JsonObject { ["cancelled"] = count };
            }

            case "imgVerifyStart":
            {
                // 「イメージを検証」をファイルを指定して始める (ダイアログは UI オートメーションで操作する)。
                string path = request["path"]!.GetValue<string>();
                LastVerification = null;
                _ = Task.Run(() => ImageOpener.Open(path)).ContinueWith(async t =>
                {
                    using IImageSource image = t.Result;
                    await VerifyImageAsync(image, path);
                }, TaskScheduler.FromCurrentSynchronizationContext());
                return new JsonObject { ["started"] = true };
            }

            case "imgOpen":
            {
                string path = request["path"]!.GetValue<string>();
                if (request["detach"]?.GetValue<bool>() == true)
                {
                    _ = OpenForensicImageAsync(path);
                    return new JsonObject { ["started"] = true };
                }

                DocumentViewModel? doc = await OpenForensicImageAsync(path);
                return new JsonObject { ["opened"] = doc is not null, ["length"] = doc?.Document.Length, ["readOnly"] = doc?.Document.IsReadOnly };
            }

            case "imgInfo":
                return Vm.Selected?.Document.Source is IImageSource source
                    ? new JsonObject { ["rows"] = new JsonArray([.. ImageInfoRows(source).Select(r => (JsonNode?)new JsonObject { ["label"] = r.Label, ["value"] = r.Value })]) }
                    : new JsonObject { ["error"] = "not an image" };

            case "imgCloneStart":
            {
                // クローンのダイアログで選んだのと同じ実行 (強い確認は本物のダイアログ)。出力先は偽のディスクの番号。
                if (Vm.Selected is not { } doc)
                {
                    return new JsonObject { ["error"] = "no document" };
                }

                DeviceCatalog catalog = DeviceService.EnumerateDevices();
                int number = request["disk"]!.GetValue<int>();
                DiskInfo target = catalog.Disks.Single(d => d.Number == number);
                var options = new CloneOptions { Verify = request["verify"]?.GetValue<bool>() ?? true, LogText = ImgLogText(), AppVersion = ImageAppVersion };
                LastClone = null;
                _ = RunCloneAsync(new CloneRequest(doc, false, target, request["startLba"]?.GetValue<long>() ?? 0, options), catalog);
                return new JsonObject { ["started"] = true, ["name"] = ImgDiskName(number) };
            }

            case "imgSectorsSave":
            {
                if (Vm.Selected is not { } doc)
                {
                    return new JsonObject { ["error"] = "no document" };
                }

                SectorRangeSidecar? saved = await SaveSectorRangeAsync(doc, request["startLba"]!.GetValue<long>(), request["count"]!.GetValue<long>(),
                    request["path"]!.GetValue<string>());
                return new JsonObject { ["saved"] = saved is not null, ["sha256"] = saved?.Sha256 };
            }

            case "imgSectorsRestoreStart":
            {
                if (Vm.Selected is not { } doc)
                {
                    return new JsonObject { ["error"] = "no document" };
                }

                LastSectorRestore = null;
                LastSectorRestorePlanText = null;
                _ = RestoreSectorRangeAsync(doc, request["path"]!.GetValue<string>());
                return new JsonObject { ["started"] = true };
            }

            case "imgRaid":
            {
                // 表示中の RAID のダイアログの種類・ストライプサイズ・順番を変え、判定結果の表示を返す。
                if (ImgRaidControls is not { } c)
                {
                    return new JsonObject { ["error"] = "no RAID dialog" };
                }

                if (request["level"] is { } level)
                {
                    c.Level.SelectedIndex = level.GetValue<int>();
                }

                if (request["stripe"] is { } stripe)
                {
                    c.Stripe.SelectedIndex = Array.IndexOf(ImgStripeSizes, stripe.GetValue<long>());
                }

                if (request["keep"] is JsonArray keep)
                {
                    var names = keep.Select(n => n!.GetValue<string>()).ToList();
                    foreach (ImgRaidMemberRow row in c.Members.Where(m => !names.Contains(m.Label)).ToList())
                    {
                        c.Members.Remove(row);
                    }

                    c.Refresh();
                }

                if (request["move"] is JsonArray move)
                {
                    c.Move(move[0]!.GetValue<int>(), move[1]!.GetValue<int>());
                }

                return new JsonObject
                {
                    ["detection"] = c.Detection.Text,
                    ["members"] = new JsonArray([.. c.Members.Select(m => (JsonNode?)m.Label)]),
                    ["canOpen"] = c.Dialog.IsPrimaryButtonEnabled,
                };
            }

            case "imgConfirm":
            {
                // 強い確認のダイアログの状態: 既定のボタンと、開いたときにフォーカスを置いた先、実行ボタンが押せるか。
                if (LastImgStrongConfirmDialog is not { } confirm)
                {
                    return new JsonObject { ["open"] = false };
                }

                return new JsonObject
                {
                    ["open"] = true,
                    ["defaultButton"] = confirm.DefaultButton.ToString(),
                    ["initialFocus"] = LastImgStrongConfirmInitialFocus,
                    ["primaryEnabled"] = confirm.IsPrimaryButtonEnabled,
                };
            }

            case "imgState":
                return ImageTestState();
        }

        return null;
    }

    private JsonObject ImageTestState()
    {
        var state = new JsonObject
        {
            ["acquisitionNotice"] = LastAcquisitionNotice,
            ["verificationText"] = LastVerificationText,
            ["raidDetection"] = LastRaidDetection,
            ["missingSegments"] = LastMissingSegmentsText,
            ["restorePlan"] = LastSectorRestorePlanText,
            ["writeBlocked"] = ImageForensics.Hooks.IsWriteBlocked,
        };
        if (LastAcquisition is { } a)
        {
            state["acquisition"] = new JsonObject
            {
                ["status"] = a.Status.ToString(),
                ["files"] = new JsonArray([.. a.Files.Select(f => (JsonNode?)f)]),
                ["log"] = a.LogPath,
                ["badSectors"] = a.BadSectorCount,
                ["kept"] = a.KeptPartialFiles,
                ["verification"] = a.Verification?.Overall.ToString(),
                ["hashes"] = new JsonObject([.. a.Hashes.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)Convert.ToHexStringLower(h.Value)))]),
            };
        }

        if (LastVerification is { } v)
        {
            state["verification"] = new JsonObject([.. v.Algorithms.Select(r => KeyValuePair.Create(r.Algorithm, (JsonNode?)r.Verdict.ToString()))]);
        }

        if (LastClone is { } c)
        {
            state["clone"] = new JsonObject { ["status"] = c.Status.ToString(), ["verification"] = c.Verification?.ToString(), ["bytes"] = c.BytesWritten };
        }

        if (LastSectorRestore is { } r)
        {
            state["restore"] = new JsonObject { ["success"] = r.Success, ["backup"] = r.BackupPath, ["failedLba"] = r.FailedLba };
        }

        var commands = new JsonObject();
        foreach (string id in new[] { "forensics.image.create", "forensics.disk.clone", "forensics.sectors.restore", "forensics.sectors.save", "forensics.raid.reconstruct" })
        {
            var s = Commands.StateOf(id);
            commands[id] = new JsonObject { ["enabled"] = s.Enabled, ["reason"] = s.Reason, ["menu"] = _menus.Find(id) is { } item ? Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(item)?.ToString() : null };
        }

        state["commands"] = commands;
        return state;
    }
}
#endif
