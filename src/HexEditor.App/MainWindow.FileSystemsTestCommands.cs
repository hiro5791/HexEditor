#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Partitions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、パーティションとファイルシステム (FOR-06〜FOR-17) の命令。ダイアログ (ディスクイメージの指定・スキャンの条件・書き出し先) は
/// 引数で置き換え、パネルの操作は UI と同じ処理を呼ぶ。イメージはテスト用の一時フォルダのファイルだけを使う。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleFileSystemTestCommandsAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "fsOpenImage":
            {
                // ディスクイメージとして開く (ENG-31 のダイアログの代わり)。パーティションの解析は開いたときに自動で始まる。
                string path = request["path"]!.GetValue<string>();
                int sector = (int)TestHookSettings.ReadLong(request["sectorSize"], 512);
                DiskImageByteSource source = DiskImage.Open(path, new DiskImageOptions { SectorSize = sector });
                DocumentViewModel vm = Vm.OpenDiskImageSource(source);
                if (request["readOnly"]?.GetValue<bool>() == false)
                {
                    vm.Document.SetReadOnly(Core.Engine.ReadOnlyReason.None);
                }

                UpdateTitle();
                return DocumentResult(vm);
            }

            case "fsPartitions":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                FileSystemDocumentState state = StateOf(d)!;
                if (request["analyze"]?.GetValue<bool>() == true && state.Partitions is null && !state.PartitionsRunning)
                {
                    await AnalyzePartitionsAsync(d);
                }

                PartitionPanel? panel = PartitionView;
                return new JsonObject
                {
                    ["running"] = state.PartitionsRunning,
                    ["done"] = state.Partitions is not null,
                    ["noTable"] = state.Partitions?.NoTable,
                    ["suggested"] = state.Partitions?.SuggestedSectorSize,
                    ["sectorSize"] = state.Partitions?.SectorSize,
                    ["panelVisible"] = IsPanelShown(PartitionsPanelId),
                    ["status"] = panel?.StatusText,
                    ["details"] = panel?.DetailsText,
                    ["scanLink"] = panel?.ScanLinkVisible,
                    ["rows"] = new JsonArray([.. (state.Partitions?.Rows ?? []).Select(r => (JsonNode?)new JsonObject
                    {
                        ["number"] = r.Number,
                        ["scheme"] = r.Scheme.ToString(),
                        ["schemeText"] = Loc.Get("Part_Scheme_" + r.Scheme),
                        ["type"] = PartitionPanel.TypeText(r),
                        ["typeCode"] = r.TypeCode,
                        ["name"] = r.Name,
                        ["startLba"] = r.StartLba,
                        ["endLba"] = r.EndLba,
                        ["sectors"] = r.SectorCount,
                        ["blockStart"] = r.BlockStart,
                        ["blockCount"] = r.BlockCount,
                        ["warning"] = r.Warning,
                        ["fileSystem"] = r.FileSystem.ToString(),
                        ["text"] = PartitionPanel.Format(r),
                    })]),
                    ["issues"] = new JsonArray([.. (state.Partitions?.Issues ?? []).Select(i => (JsonNode?)new JsonObject
                    {
                        ["kind"] = i.Kind.ToString(), ["row"] = i.Row, ["text"] = PartitionPanel.IssueText(i),
                    })]),
                };
            }

            case "fsPartitionAction":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                ShowPanel(PartitionsPanelId, focus: false);
                PartitionPanel panel = await WaitPanelAsync<PartitionPanel>(PartitionsPanelId);
                int number = (int)TestHookSettings.ReadLong(request["number"], 1);
                long? startLba = request["startLba"] is { } s ? TestHookSettings.ReadLong(s, 0) : null;
                if (!panel.SelectRow(number, startLba))
                {
                    return new JsonObject { ["error"] = "row not found" };
                }

                switch (request["action"]?.GetValue<string>())
                {
                    case "openInNewTab":
                        return TabResult(panel.OpenInNewTab());
                    case "goToStart":
                        panel.GoToStart();
                        break;
                    case "select":
                        panel.SelectPartition();
                        break;
                    case "showStructure":
                        panel.ShowStructure();
                        break;
                    case "showFileSystem":
                        await panel.ShowFileSystemAsync();
                        break;
                }

                return new JsonObject { ["cursor"] = d.Editor.Cursor, ["details"] = panel.DetailsText };
            }

            case "fsReopenSector":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                return TabResult(ReopenWithSectorSize(d, (int)TestHookSettings.ReadLong(request["sectorSize"], 4096)));
            }

            case "fsTab":
                return TabResult(Vm.Selected);

            case "fsShow":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                TestHooks.FileSystemKindChoice = request["kind"]?.GetValue<string>();
                long offset = TestHookSettings.ReadLong(request["offset"], 0);
                long length = TestHookSettings.ReadLong(request["length"], d.Document.Length - offset);
                FileSystemSession? session = await ShowFileSystemAsync(d, offset, length);
                TestHooks.FileSystemKindChoice = null;
                return new JsonObject
                {
                    ["opened"] = session is not null,
                    ["kind"] = session?.FileSystem.Kind.ToString(),
                    ["message"] = StateOf(d)?.FileSystemMessage,
                };
            }

            case "fsBrowser":
                return await TestFileSystemBrowserAsync(request);

            case "fsScan":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                ScanIntervals intervals = request["intervals"]?.GetValue<string>() is { } text ? Enum.Parse<ScanIntervals>(text) : ScanIntervals.Default;
                var options = new LostPartitionScanOptions { Intervals = intervals };
                Task task = RunLostPartitionScanAsync(d, options);
                if (request["wait"]?.GetValue<bool>() != false)
                {
                    await task;
                }

                return TestForensicResults(d);
            }

            case "fsResults":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                if (request["select"] is JsonArray select && FindPanelContent<ForensicResultsPanel>(ForensicResultsPanelId) is { } panel)
                {
                    panel.SelectRows(select.Select(n => (int)n!.GetValue<long>()));
                    switch (request["action"]?.GetValue<string>())
                    {
                        case "open":
                            return TabResult(OpenCandidate(d, (LostPartitionCandidate)panel.SelectedRows[0].Item!));
                        case "showFs":
                            await ShowCandidateFileSystemAsync(d, (LostPartitionCandidate)panel.SelectedRows[0].Item!);
                            return TabResult(Vm.Selected);
                        case "createMbr":
                        case "createGpt":
                            bool created = CreatePartitionEntries(d, [.. panel.SelectedRows.Select(r => r.Item).OfType<LostPartitionCandidate>()],
                                request["action"]!.GetValue<string>() == "createGpt");
                            return new JsonObject { ["created"] = created, ["modified"] = d.Document.IsModified };
                    }
                }

                return TestForensicResults(d);
            }

            case "fsHighlight":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                FileSystemDocumentState state = StateOf(d)!;
                if (request["on"]?.GetValue<bool>() is bool on && on != state.HighlightOn)
                {
                    ToggleForensicHighlight();
                }

                return new JsonObject
                {
                    ["on"] = state.HighlightOn,
                    ["tailsReady"] = state.Session?.Classifier.Tails is not null,
                    ["legend"] = CurrentView()?.Overlay("forensicLegend") is not null,
                };
            }

            case "fsRegions":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                FileSystemSession session = StateOf(d)?.Session ?? throw new InvalidOperationException("No file system.");
                long offset = TestHookSettings.ReadLong(request["offset"], 0);
                long length = TestHookSettings.ReadLong(request["length"], 4096);
                return new JsonObject
                {
                    ["regions"] = new JsonArray([.. session.Classifier.Classify(offset, offset + length).Select(r => (JsonNode?)new JsonObject
                    {
                        ["offset"] = r.Offset, ["length"] = r.Length, ["kind"] = r.Kind.ToString(), ["mark"] = MarkOf(r.Kind).ToString(),
                    })]),
                };
            }

            case "fsGoToCluster":
            {
                // 「クラスタへ移動」のダイアログを開き、入力欄に文字を入れる (確定は dialogButton の PrimaryButton)。
                if (_goToClusterBox is null)
                {
                    _ = ShowGoToClusterAsync(request["input"]?.GetValue<string>());
                    await WaitUntilTestAsync(() => _goToClusterBox is not null);
                }
                else if (request["input"]?.GetValue<string>() is { } input)
                {
                    _goToClusterBox.Text = input;
                }

                return new JsonObject { ["invalid"] = _goToClusterInvalid, ["open"] = _goToClusterBox is not null };
            }

            case "fsStatus":
                UpdateForensicStatus();
                return new JsonObject
                {
                    ["text"] = ForensicStatusText,
                    ["ownerLink"] = _forensicOwnerLink,
                    ["visible"] = StatusForensic.Visibility == Visibility.Visible,
                };

            case "fsFindOwner":
            {
                await BuildOwnerMapAsync(Vm.Selected);
                UpdateForensicStatus();
                return new JsonObject { ["text"] = ForensicStatusText, ["builds"] = ClusterOwnerMap.BuildCount };
            }

            case "fsFragments":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                FileSystemDocumentState state = StateOf(d)!;
                return new JsonObject
                {
                    ["fragmentCount"] = state.HighlightedFragments.Count,
                    ["fragments"] = new JsonArray([.. state.HighlightedFragments.Select(f => (JsonNode?)new JsonObject { ["offset"] = f.Offset, ["length"] = f.Length })]),
                    ["panel"] = new JsonArray([.. (FindPanelContent<FragmentListPanel>(FragmentsPanelId)?.Texts ?? []).Select(t => (JsonNode?)t)]),
                    ["goMenu"] = GoNextFragmentItem.Visibility == Visibility.Visible,
                    ["cursor"] = d.Editor.Cursor,
                    ["selections"] = new JsonArray([.. d.Editor.SelectedRanges.Select(r => (JsonNode?)new JsonObject { ["start"] = r.Start, ["length"] = r.Length })]),
                };
            }

            case "fsExport":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                string folder = request["folder"]!.GetValue<string>();
                _pendingExport = (folder, new FileExportOptions
                {
                    IncludeDeleted = request["deleted"]?.GetValue<bool>() ?? true,
                    IncludeSlack = request["slack"]?.GetValue<bool>() ?? false,
                });
                IReadOnlyList<FsEntry> entries = FileSystemView?.SelectedEntries ?? [];
                Task<IReadOnlyList<FileExportRow>?> task = ExportFsEntriesAsync(d, entries);
                if (request["wait"]?.GetValue<bool>() == false)
                {
                    return new JsonObject { ["started"] = true };
                }

                IReadOnlyList<FileExportRow>? rows = await task;
                return new JsonObject { ["count"] = rows?.Count, ["exported"] = rows is not null };
            }

            case "fsExtract":
            {
                _pendingExtract = (request["path"]!.GetValue<string>(), request["map"]?.GetValue<bool>() ?? true);
                await (request["slack"]?.GetValue<bool>() == true ? ExportSlackAsync() : ExportUnallocatedAsync());
                return new JsonObject();
            }

            case "fsDialog":
            {
                ContentDialog? dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault();
                return dialog is null ? new JsonObject { ["open"] = false } : new JsonObject
                {
                    ["open"] = true,
                    ["title"] = dialog.Title?.ToString(),
                    ["body"] = (dialog.Content as TextBlock)?.Text ?? (dialog.Content as FrameworkElement)?.Name,
                    ["primary"] = dialog.PrimaryButtonText,
                    ["secondary"] = dialog.SecondaryButtonText,
                    ["close"] = dialog.CloseButtonText,
                };
            }

            case "fsCancel":
            {
                DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
                foreach (Core.Operations.LongRunningOperation op in Vm.Operations.ActiveFor(d.Document))
                {
                    op.Cancel();
                }

                return new JsonObject();
            }

            case "fsCommandNames":
                return new JsonObject
                {
                    ["names"] = new JsonArray([.. Core.Commands.BuiltInCommands.All.Where(c => c.Category == "forensics")
                        .Select(c => (JsonNode?)(Loc.Get(c.CategoryKey) + ": " + Loc.Get(c.NameKey)))]),
                };

            default:
                return null;
        }
    }

    private JsonObject TabResult(DocumentViewModel? vm) => vm is null ? new JsonObject { ["error"] = "no tab" } : new JsonObject
    {
        ["index"] = Vm.Documents.IndexOf(vm),
        ["name"] = vm.DisplayName,
        ["title"] = vm.TabTitle,
        ["length"] = vm.Document.Length,
        ["readOnly"] = vm.Document.IsReadOnly,
        ["baseAddress"] = (long)vm.Editor.View.BaseAddress,
        ["linked"] = vm.LinkParent is not null,
        ["sectorSize"] = vm.Document.Source.LogicalSectorSize,
    };

    private JsonObject TestForensicResults(DocumentViewModel d)
    {
        FileSystemDocumentState state = StateOf(d)!;
        ForensicList? list = state.ActiveList is { } k ? state.Lists.GetValueOrDefault(k) : null;
        return new JsonObject
        {
            ["kind"] = list?.Kind.ToString(),
            ["status"] = list?.Status,
            ["header"] = list?.Header,
            ["rows"] = new JsonArray([.. (list?.Rows ?? []).Select(r => (JsonNode?)new JsonObject
            {
                ["text"] = r.Text,
                ["offset"] = r.Offset,
                ["startLba"] = (r.Item as LostPartitionCandidate)?.StartLba,
                ["sectors"] = (r.Item as LostPartitionCandidate)?.SectorCount,
                ["kind"] = (r.Item as LostPartitionCandidate)?.Kind.ToString(),
                ["confidence"] = (r.Item as LostPartitionCandidate)?.Confidence.ToString(),
                ["redo"] = (r.Item as Core.FileSystems.Ntfs.NtfsLogRecord)?.RedoName,
                ["undo"] = (r.Item as Core.FileSystems.Ntfs.NtfsLogRecord)?.UndoName,
                ["lsn"] = (r.Item as Core.FileSystems.Ntfs.NtfsLogRecord)?.Lsn,
                ["path"] = (r.Item as ClusterOwner)?.Path,
            })]),
        };
    }

    /// <summary>UI スレッドに積んだ処理 (結果一覧の行の追加など) が終わるまで待つ。</summary>
    private Task DispatcherIdleAsync()
    {
        var done = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => done.TrySetResult());
        return done.Task;
    }

    private async Task<T> WaitPanelAsync<T>(string id) where T : FrameworkElement
    {
        await WaitUntilTestAsync(() => FindPanelContent<T>(id) is { IsLoaded: true });
        return FindPanelContent<T>(id)!;
    }

    private static async Task WaitUntilTestAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        long end = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > end)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(20);
        }
    }

    private async Task<JsonObject> TestFileSystemBrowserAsync(JsonObject request)
    {
        DocumentViewModel d = Vm.Selected ?? throw new InvalidOperationException("No document.");
        ShowPanel(FileSystemPanelId, focus: false);
        FileSystemPanel panel = await WaitPanelAsync<FileSystemPanel>(FileSystemPanelId);
        if (request["folder"]?.GetValue<string>() is { } folder)
        {
            if (Enum.TryParse(folder, out FsVirtualFolder kind))
            {
                panel.SelectVirtualFolder(kind);
            }
            else if (!await panel.OpenPathAsync(folder))
            {
                return new JsonObject { ["error"] = "folder not found" };
            }
        }

        if (request["waitLoaded"]?.GetValue<bool>() != false)
        {
            await WaitUntilTestAsync(() => panel.FolderLoaded, (int)TestHookSettings.ReadLong(request["timeoutMs"], 60_000));
        }

        if (request["fat"] is { } fat)
        {
            panel.FatChoice = (int)fat.GetValue<long>();
            await DispatcherIdleAsync();
            await WaitUntilTestAsync(() => panel.FolderLoaded);
        }

        if (request["select"] is JsonArray many)
        {
            panel.SelectEntries(many.Select(n => n!.GetValue<string>()));
        }
        else if (request["select"]?.GetValue<string>() is { } name && !panel.SelectEntry(name))
        {
            return new JsonObject { ["error"] = "entry not found: " + name };
        }

        FsEntry? selected = panel.SelectedEntries.FirstOrDefault();
        DocumentViewModel? opened = null;
        switch (request["action"]?.GetValue<string>())
        {
            case "activate":
                panel.ActivateSelected();
                break;
            case "openFile" when selected is not null:
                opened = OpenFsEntryTab(d, selected);
                break;
            case "showClusters" when selected is not null:
                ShowFileClusters(d, selected);
                break;
            case "mftRecord" when selected is not null:
                ShowMftRecord(d, selected);
                break;
            case "logFile":
                await ShowLogFileAsync(d);
                await DispatcherIdleAsync();
                return TestForensicResults(d);
            case "clusterChain" when selected is not null:
                ShowClusterChain(d, selected);
                await DispatcherIdleAsync();
                return TestForensicResults(d);
        }

        var result = new JsonObject
        {
            ["title"] = panel.TitleText,
            ["warnings"] = panel.WarningsText,
            ["details"] = panel.DetailsText,
            ["status"] = panel.ListStatusText,
            ["count"] = panel.LoadedCount,
            ["fatDifferences"] = panel.FatDifferences,
            ["fragments"] = new JsonArray([.. panel.FragmentTexts.Select(t => (JsonNode?)t)]),
            ["cursor"] = d.Editor.Cursor,
            ["menu"] = new JsonArray([.. panel.MenuItems.Select(m => (JsonNode?)new JsonObject { ["id"] = m.Id, ["text"] = m.Text, ["visible"] = m.Visible })]),
            ["opened"] = opened is null ? null : TabResult(opened),
        };
        if (request["entries"]?.GetValue<bool>() != false && panel.LoadedCount <= 5000)
        {
            result["entries"] = new JsonArray([.. panel.Entries.Select(e => (JsonNode?)new JsonObject
            {
                ["name"] = e.Name,
                ["path"] = e.Path,
                ["size"] = e.Size,
                ["modified"] = e.Modified.Utc?.ToString("o"),
                ["state"] = e.State.ToString(),
                ["recovery"] = e.Recovery.ToString(),
                ["deletedSource"] = e.DeletedSource.ToString(),
                ["originalPath"] = e.OriginalPath,
                ["corrupt"] = e.Flags.HasFlag(FsEntryFlags.Corrupt),
                ["corruption"] = e.Corruption.ToString(),
                ["timeMismatch"] = e.Flags.HasFlag(FsEntryFlags.TimeMismatch),
                ["directory"] = e.IsDirectory,
                ["text"] = panel.Format(e),
            })]);
        }

        return result;
    }
}
#endif
