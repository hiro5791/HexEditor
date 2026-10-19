#if HEX_TEST_HOOKS
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.Wiping;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、消去・墨塗り (FOR-30〜32) の命令。消去の対象は偽のディスク (fakeDevices) とテスト用の一時フォルダのイメージだけ。
/// OS 経由の空き領域の消去の命令は設けない (この PC のボリュームに書かないため)。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleWipeTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "wipeDelay":
                // 書き込みごとの待ち (遅いデータソースの再現。TC-FOR-30-02)。
                WipeTestWriteDelay = TimeSpan.FromMilliseconds(TestHookSettings.ReadLong(request["ms"], 0));
                return new JsonObject { ["ms"] = WipeTestWriteDelay.TotalMilliseconds };

            case "wipeState":
                return TestWipeState();

            case "wipeConfirm":
                return TestWipeConfirm();

            case "wipeConfirmEnter":
            {
                // 確認ダイアログで Enter キーを押したときと同じ: 既定のボタン (DefaultButton) を押す。既定は「キャンセル」なので実行されない。
                if (LastWipeConfirmDialog is not { } dialog)
                {
                    return new JsonObject { ["error"] = "no dialog" };
                }

                string pressed = dialog.DefaultButton.ToString();
                if (dialog.DefaultButton == ContentDialogButton.Primary && dialog.IsPrimaryButtonEnabled)
                {
                    return new JsonObject { ["pressed"] = pressed, ["error"] = "the default button would run the wipe" };
                }

                dialog.Hide();
                await DispatcherIdleAsync();
                return new JsonObject { ["pressed"] = pressed };
            }

            case "wipeHexMenu":
            {
                // Hex ビューの右クリックメニューの「選択範囲を安全に消去」(開いたときと同じ処理で項目を作る)。
                var menu = new MenuFlyout();
                ExtendHexViewWipeMenu(menu);
                MenuFlyoutItem item = menu.Items.OfType<MenuFlyoutItem>().Single();
                return new JsonObject
                {
                    ["text"] = item.Text,
                    ["visible"] = item.Visibility == Visibility.Visible,
                    ["enabled"] = item.IsEnabled,
                    ["toolTip"] = ToolTipService.GetToolTip(item)?.ToString(),
                };
            }

            case "fsRedact":
            {
                // ファイルシステムブラウザで選んだファイルの墨塗り。dialog が true ならダイアログを開く (UI のテスト)。それ以外は引数で行う。
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                IReadOnlyList<FsEntry> entries = FileSystemView?.SelectedEntries ?? [];
                if (request["dialog"]?.GetValue<bool>() == true)
                {
                    _ = ShowRedactDialogAsync(d, entries);
                    return new JsonObject { ["opened"] = true };
                }

                ShredPass pattern = request["pattern"]?.GetValue<string>() is { } text ? ShredMethod.ParsePass(text) ?? ShredPass.Fill(0) : ShredPass.Fill(0);
                RedactionPlan? plan = await RedactAsync(d, entries, pattern, request["names"]?.GetValue<bool>() == true, request["log"]?.GetValue<string>());
                return RedactionJson(plan);
            }

            case "wipeDirect":
            {
                // 直接書き込みの空き領域・スラックの消去 (確認ダイアログは UI で押す。結果は wipeState)。
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                if (StateOf(d)?.Session is not { } session || ResolveWipeTarget(d) is not { } target)
                {
                    return new JsonObject { ["error"] = "no file system" };
                }

                LastWipeResult = null;
                ShredMethod method = TestMethod(request);
                _ = WipeFileSystemDirectAsync(d, target, session, method, request["slack"]?.GetValue<bool>() == true);
                return new JsonObject { ["started"] = true };
            }

            case "wipeFreeSpaceOsCi":
            {
                // OS 経由の空き領域の消去 (CI の管理者のテストだけ: build/tests/New-WipeTestVhd.ps1 が作ったラベル HEXWIPE のボリュームに限る。
                // この PC のボリュームには書かない)。確認ダイアログは UI で押す。
                string root = request["root"]!.GetValue<string>();
                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.VolumeLabel != "HEXWIPE" || drive.DriveType != DriveType.Fixed
                    || string.Equals(Path.GetPathRoot(Environment.SystemDirectory), drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return new JsonObject { ["error"] = "only the HEXWIPE test volume can be used" };
                }

                LastWipeResult = null;
                _ = WipeFreeSpaceOsAsync(new WindowsFreeSpaceVolume(drive.RootDirectory.FullName), drive, TestMethod(request));
                return new JsonObject { ["started"] = true };
            }

            case "wipeCancel":
                LastWipeOperation?.Cancel();
                return new JsonObject { ["cancelled"] = LastWipeOperation is not null };

            case "wipeDocSha":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                Core.Engine.DocumentSnapshot snapshot = d.Document.Current;
                string sha = await Task.Run(() =>
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[1 << 20];
                    for (long at = 0; at < snapshot.Length; at += buffer.Length)
                    {
                        int n = (int)Math.Min(buffer.Length, snapshot.Length - at);
                        snapshot.Read(at, buffer.AsSpan(0, n));
                        hash.AppendData(buffer, 0, n);
                    }

                    return Convert.ToHexStringLower(hash.GetHashAndReset());
                });
                return new JsonObject
                {
                    ["sha256"] = sha,
                    ["modified"] = d.Document.IsModified,
                    ["readOnly"] = d.Document.IsReadOnly,
                    ["canUndo"] = d.Document.History.CanUndo,
                    ["undoName"] = d.Document.History.CanUndo ? EditOperationName(d.Document.History.UndoDescription ?? string.Empty) : null,
                };
            }

            default:
                return null;
        }
    }

    private static ShredMethod TestMethod(JsonObject request) => request["method"]?.GetValue<string>() switch
    {
        "random" => ShredMethod.RandomOnce,
        "dod" => ShredMethod.Dod,
        _ => ShredMethod.ZeroOnce,
    };

    private static JsonObject RedactionJson(RedactionPlan? plan) => plan is null
        ? new JsonObject { ["done"] = false }
        : new JsonObject
        {
            ["done"] = true,
            ["items"] = new JsonArray([.. plan.Items.Select(i => (JsonNode?)new JsonObject
            {
                ["path"] = i.Path,
                ["fileId"] = i.FileId,
                ["bytes"] = i.Bytes,
                ["ranges"] = new JsonArray([.. i.Ranges.Select(r => (JsonNode?)new JsonObject { ["offset"] = r.Offset, ["length"] = r.Length, ["kind"] = r.Kind.ToString() })]),
                ["warnings"] = new JsonArray([.. i.Warnings.Select(w => (JsonNode?)w)]),
            })]),
            ["excluded"] = new JsonArray([.. plan.Excluded.Select(x => (JsonNode?)new JsonObject { ["path"] = x.Path, ["reason"] = x.Reason.ToString() })]),
        };

    private JsonObject TestWipeState()
    {
        var result = new JsonObject();
        if (LastWipeOperation is { } op)
        {
            result["operation"] = new JsonObject
            {
                ["name"] = op.Name,
                ["state"] = op.State.ToString(),
                ["detail"] = op.Detail,
                ["processed"] = op.ProcessedBytes,
                ["total"] = op.TotalBytes,
                ["remainingMs"] = op.EstimatedRemaining?.TotalMilliseconds,
                ["shouldShow"] = op.ShouldShow,
            };
        }

        if (LastSlackSkipped is { } skipped)
        {
            result["slackSkipped"] = new JsonObject
            {
                ["total"] = skipped.Total, ["resident"] = skipped.Resident, ["compressed"] = skipped.Compressed, ["sparse"] = skipped.Sparse,
                ["inconsistent"] = skipped.Inconsistent,
            };
        }

        switch (LastWipeResult)
        {
            case WipeResult r:
                result["result"] = new JsonObject
                {
                    ["kind"] = "wipe",
                    ["succeeded"] = r.Succeeded,
                    ["verified"] = r.Verified,
                    ["mismatches"] = r.Mismatches.Count,
                    ["unwritten"] = r.Unwritten.Count,
                    ["cancelled"] = r.Cancelled,
                    ["written"] = r.BytesWritten,
                    ["total"] = r.TotalBytes,
                    ["passes"] = r.Passes,
                };
                break;
            case OsFreeSpaceWipeResult os:
                result["result"] = new JsonObject
                {
                    ["kind"] = "os",
                    ["cancelled"] = os.Cancelled,
                    ["overwritten"] = os.BytesOverwritten,
                    ["leftovers"] = os.Leftovers.Count,
                    ["folder"] = os.TemporaryFolder,
                    ["error"] = os.Error,
                };
                break;
            case RedactionPlan plan:
                result["result"] = RedactionJson(plan);
                break;
        }

        return result;
    }

    private JsonObject TestWipeConfirm()
    {
        if (LastWipeConfirmDialog is not { } dialog || !LastWipeConfirmOpened)
        {
            return new JsonObject { ["open"] = false };
        }

        object? focused = Root.XamlRoot is { } xr ? FocusManager.GetFocusedElement(xr) : null;
        var texts = new List<string>();
        if (dialog.Content is ScrollViewer { Content: Panel panel })
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is TextBlock block)
                {
                    texts.Add(block.Text);
                }
            }
        }

        TextBox? input = FindDescendant<TextBox>(dialog, t => AutomationProperties.GetAutomationId(t).EndsWith("_Input", StringComparison.Ordinal));
        return new JsonObject
        {
            ["open"] = true,
            ["id"] = AutomationProperties.GetAutomationId(dialog),
            ["primary"] = dialog.PrimaryButtonText,
            ["primaryEnabled"] = dialog.IsPrimaryButtonEnabled,
            ["defaultButton"] = dialog.DefaultButton.ToString(),
            ["initialFocus"] = LastWipeConfirmInitialFocus,
            ["focused"] = focused is Button { Name: var name } ? "Button:" + name : focused is FrameworkElement fe ? $"{fe.GetType().Name}:{AutomationProperties.GetAutomationId(fe)}" : focused?.GetType().Name,
            ["text"] = string.Join("\n", texts),
            ["input"] = input is null ? null : AutomationProperties.GetAutomationId(input),
        };
    }
}
#endif
