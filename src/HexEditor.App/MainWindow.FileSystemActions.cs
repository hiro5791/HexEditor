using System.Globalization;
using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Fat;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Operations;
using HexEditor.Core.Templates;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ファイルシステムの長い操作とダイアログ: 失われたパーティションのスキャン (FOR-08)、パーティション表のエントリの作成、クラスタへ移動 (FOR-15 の仕様 2)、
/// 逆引き (仕様 3)、書き出し (FOR-09 の仕様 6、FOR-16 の仕様 5・6、FOR-17 の仕様 5)、$LogFile の一覧 (FOR-10 の仕様 9)、構造のテンプレート表示。
/// </summary>
public sealed partial class MainWindow
{
    // ---- 結果一覧 ----

    /// <summary>結果一覧を作り直して出す (同じ種類の前の一覧を置き換える)。</summary>
    internal ForensicList NewForensicList(DocumentViewModel d, ForensicListKind kind, string title, string header)
    {
        FileSystemDocumentState state = StateOf(d)!;
        var list = new ForensicList(kind, title, header);
        state.Lists[kind] = list;
        state.ActiveList = kind;
        ShowPanel(ForensicResultsPanelId, focus: false);
        state.RaiseChanged();
        return list;
    }

    /// <summary>結果一覧に行を加える (UI スレッドで)。</summary>
    private void AppendRows(DocumentViewModel d, ForensicList list, IReadOnlyList<ForensicRow> rows, string? status = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            list.Rows.AddRange(rows);
            if (status is not null)
            {
                list.Status = status;
            }

            list.Version++;
            StateOf(d)?.RaiseChanged();
        });
    }

    // ---- 失われたパーティションのスキャン (FOR-08) ----

    /// <summary>スキャンの条件のダイアログ (仕様 1・2) を出して、長時間処理でスキャンする。<paramref name="range"/> は未割り当て行から呼んだときの範囲。</summary>
    internal async Task ShowLostPartitionScanAsync(PartitionRow? range)
    {
        if (Vm.Selected is not { } d)
        {
            return;
        }

        int sector = SectorSizeOf(d);
        long total = d.Document.Length / sector;
        LostPartitionScanOptions options;
        if (TestHooks.Active && _pendingScanOptions is { } pending)
        {
            options = pending;
            _pendingScanOptions = null;
        }
        else
        {
            TextBox startBox = DialogParts.Field("LostScan_Start", Loc.Get("LostScan_StartLba"), (range?.StartLba ?? 0).ToString(CultureInfo.InvariantCulture));
            TextBox endBox = DialogParts.Field("LostScan_End", Loc.Get("LostScan_EndLba"),
                (range is { } r ? r.StartLba + r.SectorCount : total).ToString(CultureInfo.InvariantCulture));
            CheckBox mib = DialogParts.Check("LostScan_MiB", Loc.Get("LostScan_IntervalMiB"), true);
            CheckBox cylinder = DialogParts.Check("LostScan_Cylinder", Loc.Get("LostScan_IntervalCylinder"), true);
            CheckBox every = DialogParts.Check("LostScan_Every", Loc.Get("LostScan_IntervalEvery"), false);
            TextBlock estimate = DialogParts.Caption("LostScan_Estimate");
            estimate.Text = Loc.Format("LostScan_EveryEstimate", FormatDuration(LostPartitionScanner.EstimateFullScan(d.Document.Length)));
            var kinds = new Dictionary<LostPartitionKind, CheckBox>();
            var kindPanel = new WrapPanel { HorizontalSpacing = 12, VerticalSpacing = 4 };
            foreach (LostPartitionKind k in Enum.GetValues<LostPartitionKind>())
            {
                CheckBox box = DialogParts.Check("LostScan_Kind_" + k, Loc.Get("LostScan_Kind_" + k), true);
                kinds[k] = box;
                kindPanel.Children.Add(box);
            }

            var content = new StackPanel { Spacing = 8, MaxWidth = 520 };
            content.Children.Add(startBox);
            content.Children.Add(endBox);
            content.Children.Add(new TextBlock { Text = Loc.Get("LostScan_Intervals"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            content.Children.Add(mib);
            content.Children.Add(cylinder);
            content.Children.Add(every);
            content.Children.Add(estimate);
            content.Children.Add(new TextBlock { Text = Loc.Get("LostScan_Kinds"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            content.Children.Add(kindPanel);
            ContentDialog dialog = DialogParts.Dialog(Root, "LostScanDialog", Loc.Get("LostScan_Title"), new ScrollViewer { Content = content },
                Loc.Get("LostScan_Run"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var context = new EditorExpressionContext(d.Editor);
            long start = DialogParts.TryEvaluate(startBox.Text, context, out long s, out _, DefaultRadix.Decimal) ? s : 0;
            long end = DialogParts.TryEvaluate(endBox.Text, context, out long e, out _, DefaultRadix.Decimal) ? e : total;
            ScanIntervals intervals = (mib.IsChecked == true ? ScanIntervals.MiB : 0) | (cylinder.IsChecked == true ? ScanIntervals.Cylinder : 0)
                | (every.IsChecked == true ? ScanIntervals.EverySector : 0);
            options = new LostPartitionScanOptions
            {
                StartLba = start,
                EndLba = end,
                Intervals = intervals == ScanIntervals.None ? ScanIntervals.Default : intervals,
                Kinds = kinds.Where(k => k.Value.IsChecked == true).Select(k => k.Key).ToHashSet(),
            };
        }

        await RunLostPartitionScanAsync(d, options);
    }

    /// <summary>テスト用: 次のスキャンで使う条件 (ダイアログの代わり)。</summary>
    private LostPartitionScanOptions? _pendingScanOptions;

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? Loc.Format("LostScan_Hours", t.TotalHours.ToString("0.0", CultureInfo.CurrentCulture))
        : Loc.Format("LostScan_Minutes", Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)));

    /// <summary>
    /// スキャン (仕様の「巨大ファイル」): 処理センターに進捗 (LBA)・速度・残り時間・見つかった件数を出し、結果は見つかるたびに一覧に加える。キャンセルしても
    /// それまでの結果は残す。読めなかった範囲は末尾にまとめて示す。
    /// </summary>
    internal async Task RunLostPartitionScanAsync(DocumentViewModel d, LostPartitionScanOptions options)
    {
        FileSystemDocumentState state = StateOf(d)!;
        int sector = SectorSizeOf(d);
        ForensicList list = NewForensicList(d, ForensicListKind.LostPartitions, Loc.Get("LostScan_ListTitle"), Loc.Get("LostScan_Header"));
        IVolumeReader reader = ReaderOf(d);
        PartitionAnalysis? existing = state.Partitions;
        long startByte = options.StartLba * sector;
        long endByte = (options.EndLba ?? d.Document.Length / sector) * sector;
        LostPartitionScanResult? result = null;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("LostScan_OperationName"), OperationKind.ReadOnly, d.Document, Math.Max(1, endByte - startByte), op =>
            {
                long found = 0;
                result = LostPartitionScanner.Scan(reader, sector, options, existing,
                    lba => op.Report(Math.Max(0, lba * sector - startByte)),
                    c =>
                    {
                        op.ReportMatches(++found);
                        AppendRows(d, list, [new ForensicRow(CandidateText(c), c.StartOffset, c)]);
                    },
                    op.CancellationToken);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }

        state.LastScan = result;
        string status = result is null ? string.Empty : result.Cancelled ? Loc.Get("LostScan_Cancelled") : Loc.Format("LostScan_Done", result.Candidates.Count);
        var tail = new List<ForensicRow>();
        foreach ((long lba, long count) in result?.Unreadable ?? [])
        {
            tail.Add(new ForensicRow(Loc.Format("LostScan_Unreadable", Number(lba), Number(count)), lba * sector));
        }

        AppendRows(d, list, tail, status);
        if (result is { Cancelled: false, Candidates.Count: 0 })
        {
            // 候補が 0 件: 「すべてのセクタ」での再スキャンを提案する (仕様の「エラー」)。
            var actions = new List<Core.Notifications.NotificationAction>();
            if (!options.Intervals.HasFlag(ScanIntervals.EverySector))
            {
                actions.Add(new Core.Notifications.NotificationAction(Loc.Get("LostScan_RescanEvery"),
                    () => _ = RunLostPartitionScanAsync(d, options with { Intervals = ScanIntervals.EverySector })));
            }

            ShowNotice(Loc.Get("LostScan_NoneFound"), InfoBarSeverity.Informational, d, actions: actions);
        }
    }

    private static string CandidateText(LostPartitionCandidate c) =>
        string.Format(CultureInfo.CurrentCulture, "{0,14}  {1,14}  {2,12}  {3,-10}  {4,-6}  {5}", Number(c.StartLba),
            c.SectorCount is long n ? Number(n) : "-", c.Size is long size ? StatusFormat.ShortSize(size, CultureInfo.CurrentCulture) ?? Number(size) : "-",
            c.KindText, Loc.Get("LostScan_Confidence_" + c.Confidence), c.Overlaps.Count > 0 ? Loc.Format("LostScan_Overlaps", string.Join(", ", c.Overlaps)) : string.Empty);

    /// <summary>候補をパーティションとして開く (FOR-06 と同じ。仕様 4)。</summary>
    internal DocumentViewModel? OpenCandidate(DocumentViewModel d, LostPartitionCandidate c)
    {
        long length = c.Size ?? d.Document.Length - c.StartOffset;
        return OpenPartitionTab(d, c.StartOffset, length, Loc.Format("Part_TabNameAt", d.DisplayName, c.StartLba), null);
    }

    /// <summary>「ファイルシステムを表示」: 候補を開いてすぐにブラウザを出す。</summary>
    internal async Task ShowCandidateFileSystemAsync(DocumentViewModel d, LostPartitionCandidate c)
    {
        if (OpenCandidate(d, c) is { } tab)
        {
            await ShowFileSystemAsync(tab, 0, tab.Document.Length, c.Kind == FileSystemKind.Unknown ? null : c.Kind);
        }
    }

    /// <summary>
    /// 「パーティション表のエントリを作成」(仕様 4): 候補から MBR または GPT のエントリを作り、データエンジン上の編集としてドキュメントに加える。保存するまで
    /// ディスクには書かない。書き込み保護モードでは無効。
    /// </summary>
    internal bool CreatePartitionEntries(DocumentViewModel d, IReadOnlyList<LostPartitionCandidate> candidates, bool gpt)
    {
        if (WriteBlockMode.IsEnabled)
        {
            ShowNotice(Loc.Get("WriteBlock_Enabled"), InfoBarSeverity.Warning, d);
            return false;
        }

        if (d.Editor.ReadOnly)
        {
            ShowNotice(Loc.Get("Command_ReadOnly"), InfoBarSeverity.Warning, d);
            return false;
        }

        var entries = candidates.Where(c => c.SectorCount is > 0)
            .Select(c => new PartitionTableEntry(c.StartLba, c.SectorCount!.Value, c.Kind, c.KindText)).ToList();
        IVolumeReader reader = new SnapshotVolumeReader(d.Document.Current);
        (IReadOnlyList<PartitionTableWrite> writes, PartitionTableError error) = gpt
            ? PartitionTableWriter.CreateGpt(reader, SectorSizeOf(d), entries)
            : PartitionTableWriter.CreateMbr(reader, SectorSizeOf(d), entries);
        if (error != PartitionTableError.None)
        {
            ShowNotice(Loc.Get("LostScan_EntryError_" + error), InfoBarSeverity.Warning, d);
            return false;
        }

        using (d.Document.BeginGroup(Loc.Get("LostScan_CreateEntries")))
        {
            foreach (PartitionTableWrite w in writes)
            {
                d.Document.Overwrite(w.Offset, w.Data, Loc.Get("LostScan_CreateEntries"));
            }
        }

        FileSystemAudit.Record(FileSystemAuditAction.CreatePartitionEntries, d.DisplayName, new Dictionary<string, string>
        {
            ["format"] = gpt ? "GPT" : "MBR",
            ["entries"] = string.Join("; ", entries.Select(e => $"{e.StartLba}+{e.SectorCount} {e.Kind}")),
        });
        StateOf(d)!.Partitions = null;
        _ = AnalyzePartitionsAsync(d);
        return true;
    }

    // ---- クラスタへ移動 (FOR-15 の仕様 2) ----

    /// <summary>
    /// 「クラスタへ移動」: クラスタ番号を入力式で入力する (この入力欄に限り <c>cluster</c> は現在のクラスタ番号)。範囲外は赤枠。FAT のクラスタは 2 始まり。
    /// ext・HFS+・APFS では「ブロックへ移動」。
    /// </summary>
    internal async Task ShowGoToClusterAsync(string? input = null)
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { } session)
        {
            return;
        }

        IFileSystem fs = session.FileSystem;
        var context = new ClusterExpressionContext(d.Editor, session);
        string title = Loc.Get(fs.UsesBlocks ? "Fs_GoToBlock" : "Cmd_forensics_fs_goToCluster");
        TextBox box = DialogParts.Field("GoToCluster_Input", Loc.Format("GoToCluster_Label", Number(fs.FirstClusterNumber), Number(fs.FirstClusterNumber + fs.ClusterCount - 1)),
            input ?? string.Empty);
        TextBlock caption = DialogParts.Caption("GoToCluster_Result", monospace: true);
        _goToClusterBox = box;
        ContentDialog dialog = DialogParts.Dialog(Root, "GoToClusterDialog", title, new StackPanel { Spacing = 6, Children = { box, caption } }, Loc.Get("GoToCluster_Go"));
        long? target = null;

        void Validate()
        {
            target = null;
            if (!DialogParts.TryEvaluate(box.Text, context, out long cluster, out ExpressionException? error, DefaultRadix.Decimal))
            {
                caption.Text = error is null || error.Error == ExpressionError.Empty ? string.Empty : DialogParts.ExpressionError(error);
                DialogParts.MarkInvalid(box, error is { Error: not ExpressionError.Empty });
            }
            else if (session.ClusterDocumentOffset(cluster) is not long offset || offset >= d.Document.Length)
            {
                caption.Text = Loc.Format("GoToCluster_OutOfRange", Number(cluster));
                DialogParts.MarkInvalid(box, true);
            }
            else
            {
                target = offset;
                caption.Text = DialogParts.Interpretation(cluster) + "  →  " + StatusFormat.Hex(offset);
                DialogParts.MarkInvalid(box, false);
            }

            dialog.IsPrimaryButtonEnabled = target is not null;
            _goToClusterInvalid = target is null && box.Text.Length > 0;
        }

        box.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && target is long t)
        {
            d.Editor.GoTo(t);
            FocusEditor();
        }

        _goToClusterBox = null;
    }

    private TextBox? _goToClusterBox;
    private bool _goToClusterInvalid;

    /// <summary>「クラスタへ移動」の入力式の名前: <c>cluster</c> は現在のクラスタ番号 (00 の 6 章のこの入力欄だけの決まり)。</summary>
    private sealed class ClusterExpressionContext(EditorState editor, FileSystemSession session) : IExpressionContext
    {
        private readonly EditorExpressionContext _inner = new(editor);

        public long Cursor => _inner.Cursor;
        public long Length => _inner.Length;
        public long SelectionStart => _inner.SelectionStart;
        public long SelectionLength => _inner.SelectionLength;
        public int SectorSize => _inner.SectorSize;
        public long? ClusterSize => session.ClusterAtDocument(editor.Cursor)?.Cluster ?? session.FileSystem.FirstClusterNumber;
        public long? RecordLength => _inner.RecordLength;
        public long? Bookmark(string name) => _inner.Bookmark(name);
        public bool TryRead(long offset, Span<byte> destination) => _inner.TryRead(offset, destination);
    }

    // ---- 逆引き (FOR-15 の仕様 3) ----

    /// <summary>逆引き表を作る (初回だけ。付随データの場所に保存し、データソースの大きさとシリアル番号が同じなら再利用する)。</summary>
    internal async Task<ClusterOwnerMap?> BuildOwnerMapAsync(DocumentViewModel? d)
    {
        if (d is null || StateOf(d) is not { Session: { } session } state)
        {
            return null;
        }

        if (session.Owners is { } existing)
        {
            return existing;
        }

        if (state.OwnersRunning)
        {
            return null;
        }

        state.OwnersRunning = true;
        state.RaiseChanged();
        string folder = Program.Environment.Locations.Documents;
        string key = OwnerMapKey(d);
        string name = session.OwnerMapName(key);
        var identity = ClusterOwnerMap.Identity((d.LinkParent ?? d).Document.Source.Length, session.VolumeOffset + (d.LinkParent is not null ? d.Document.LinkStart : 0),
            session.FileSystem);
        try
        {
            ClusterOwnerMap map = await Vm.Operations.RunAsync(Loc.Get("Fs_BuildingOwners"), OperationKind.ReadOnly, d.Document, 1_000_000, op =>
            {
                ClusterOwnerMap m = ClusterOwnerMap.LoadOrBuild(folder, name, identity, session.FileSystem, out bool built, OperationProgress(op),
                    op.CancellationToken);
                AppLog.Info(built ? $"cluster owner map built: {name} ({m.RunCount} runs)" : $"cluster owner map reused: {name}");
                return Task.FromResult(m);
            });
            session.Owners = map;
            if (map.SkippedCorrupt > 0)
            {
                ShowNotice(Loc.Format("Fs_OwnersSkipped", map.SkippedCorrupt), InfoBarSeverity.Informational, d);
            }

            return map;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            state.OwnersRunning = false;
            state.RaiseChanged();
            UpdateForensicStatus();
        }
    }

    /// <summary>逆引き表の保存名に使うドキュメントの識別 (ファイルのパスまたはデバイスのパス)。</summary>
    private static string OwnerMapKey(DocumentViewModel d)
    {
        DocumentViewModel root = d.LinkParent ?? d;
        string identity = root.FilePath ?? (root.Document.Source as Core.Devices.DiskImageByteSource)?.Path ?? root.Document.Source.Identity;
        return Core.Files.DocumentDataStore.Key(identity);
    }

    /// <summary>「この位置を使っているファイル」: カーソル位置または選択範囲のクラスタの持ち主を結果一覧に出す。</summary>
    internal async Task FindOwnersAtCursorAsync()
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { } session || await BuildOwnerMapAsync(d) is not { } map)
        {
            return;
        }

        long start = d.Editor.HasSelection ? d.Editor.SelectionStart : d.Editor.Cursor;
        long end = d.Editor.HasSelection ? d.Editor.SelectionStart + d.Editor.SelectionLength : start + 1;
        if (session.ClusterAtDocument(start) is not (long first, _))
        {
            ShowNotice(Loc.Get("Fs_NotInClusters"), InfoBarSeverity.Informational, d);
            return;
        }

        long last = session.ClusterAtDocument(Math.Max(start, end - 1))?.Cluster ?? first;
        ForensicList list = NewForensicList(d, ForensicListKind.Owners, Loc.Format("Fs_OwnersTitle", Number(first), Number(last)), Loc.Get("Fs_OwnersHeader"));
        var rows = map.OwnersOf(first, last - first + 1).Select(o => new ForensicRow(
            $"{Loc.Get("Fs_OwnerKind_" + o.Kind),-24}  {o.Path}", null, o)).ToList();
        if (rows.Count == 0)
        {
            rows.Add(new ForensicRow(Loc.Get("FsStatus_Unallocated"), null));
        }

        AppendRows(d, list, rows, string.Empty);
        UpdateForensicStatus();
    }

    // ---- 書き出し (FOR-09 の仕様 6、FOR-17 の仕様 5) ----

    private Task ExportSelectedFsEntriesAsync() =>
        Vm.Selected is { } d && FileSystemView?.SelectedEntries is { Count: > 0 } entries ? ExportFsEntriesAsync(d, entries) : Task.CompletedTask;

    /// <summary>テスト用: 次の書き出しで使うフォルダと設定 (ダイアログの代わり)。</summary>
    private (string Folder, FileExportOptions Options)? _pendingExport;

    /// <summary>
    /// 「ファイルを書き出す」: オプション (ディレクトリ構造、代替データストリーム、スラック、削除されたファイル) と書き出し先を選び、長時間処理で書き出す。
    /// 「上書き済み」の削除ファイルは、内容が他のファイルのものである可能性を確認ダイアログで示す。マニフェストと監査ログに記録する。
    /// </summary>
    internal async Task<IReadOnlyList<FileExportRow>?> ExportFsEntriesAsync(DocumentViewModel d, IReadOnlyList<FsEntry> entries)
    {
        if (StateOf(d)?.Session is not { } session)
        {
            return null;
        }

        if (entries.Any(e => e.State == FsEntryState.Deleted && e.Recovery == FsRecoveryStatus.Overwritten) && !await ConfirmOverwrittenExportAsync())
        {
            return null;
        }

        string folder;
        FileExportOptions options;
        if (_pendingExport is { } pending)
        {
            (folder, options) = pending;
            _pendingExport = null;
        }
        else
        {
            CheckBox keep = DialogParts.Check("FsExport_KeepStructure", Loc.Get("FsExport_KeepStructure"), true);
            CheckBox streams = DialogParts.Check("FsExport_Streams", Loc.Get("FsExport_Streams"), true);
            CheckBox slack = DialogParts.Check("FsExport_Slack", Loc.Get("FsExport_Slack"), false);
            CheckBox deleted = DialogParts.Check("FsExport_Deleted", Loc.Get("FsExport_Deleted"), entries.Any(e => e.State == FsEntryState.Deleted));
            ContentDialog dialog = DialogParts.Dialog(Root, "FsExportDialog", Loc.Get("Cmd_forensics_fs_exportFiles"),
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = Loc.Format("FsExport_Count", entries.Count) }, keep, streams, slack, deleted } },
                Loc.Get("FsExport_ChooseFolder"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || await PickFolderToolAsync("HexEditor.FsExport") is not { } picked)
            {
                return null;
            }

            folder = picked;
            options = new FileExportOptions
            {
                KeepStructure = keep.IsChecked == true,
                IncludeStreams = streams.IsChecked == true,
                IncludeSlack = slack.IsChecked == true,
                IncludeDeleted = deleted.IsChecked == true,
            };
        }

        try
        {
            long total = Math.Max(1, entries.Sum(e => e.Size));
            IReadOnlyList<FileExportRow> rows = await Vm.Operations.RunAsync(Loc.Get("FsExport_OperationName"), OperationKind.ReadOnly, d.Document, total,
                op => Task.FromResult(FileExporter.Export(session.FileSystem, entries, folder, options, n => op.Report(n), null, op.CancellationToken)));
            FileSystemAudit.Record(FileSystemAuditAction.ExportFiles, d.DisplayName, new Dictionary<string, string>
            {
                ["destination"] = folder,
                ["manifest"] = Path.Combine(folder, FileExporter.ManifestName),
                ["files"] = rows.Count.ToString(CultureInfo.InvariantCulture),
            });
            ShowNotice(Loc.Format("FsExport_Done", rows.Count, folder), InfoBarSeverity.Success, d);
            return rows;
        }
        catch (ExportSpaceException ex)
        {
            ShowNotice(Loc.Format("FsExport_NoSpace", StatusFormat.ShortSize(ex.Required, CultureInfo.CurrentCulture) ?? Number(ex.Required),
                StatusFormat.ShortSize(ex.Available, CultureInfo.CurrentCulture) ?? Number(ex.Available)), InfoBarSeverity.Error, d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("FsExport_Failed", ex.Message), InfoBarSeverity.Error, d);
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    /// <summary>上書き済みの削除ファイルを書き出す前の確認 (FOR-17 の仕様 5: ボタン「書き出す」「キャンセル」)。</summary>
    private async Task<bool> ConfirmOverwrittenExportAsync()
    {
        var body = new TextBlock { Text = Loc.Get("FsExport_OverwrittenBody"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(body, "FsExportOverwritten_Body");
        ContentDialog dialog = DialogParts.Dialog(Root, "FsExportOverwrittenDialog", Loc.Get("FsExport_OverwrittenTitle"), body, Loc.Get("FsExport_Export"));
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary;
    }

    /// <summary>テスト用: 次の未割り当て・スラックの書き出し先 (保存のダイアログの代わり)。</summary>
    private (string Path, bool Map)? _pendingExtract;

    /// <summary>「未割り当て領域を書き出す」(FOR-16 の仕様 5): 未割り当てのクラスタをつないで 1 つのファイルにし、対応表 (.map.csv) を作る。</summary>
    internal Task ExportUnallocatedAsync() => ExtractAsync(slack: false);

    /// <summary>「スラックを書き出す」(仕様 6): 全ファイルのスラックを同じ形式で書き出す。対応表に元のファイルのパスを入れる。</summary>
    internal Task ExportSlackAsync() => ExtractAsync(slack: true);

    private async Task ExtractAsync(bool slack)
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { } session)
        {
            return;
        }

        string path;
        bool map;
        if (_pendingExtract is { } pending)
        {
            (path, map) = pending;
            _pendingExtract = null;
        }
        else
        {
            string suggested = slack ? "slack.bin" : "unalloc.bin";
            string? chosen;
            if (!TestHooks.TrySavePicker(suggested, out chosen))
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.FsExtract" };
                picker.FileTypeChoices.Add(Loc.Get("FsExtract_FileType"), [".bin"]);
                chosen = (await picker.PickSaveFileAsync())?.Path;
            }

            if (chosen is null)
            {
                return;
            }

            CheckBox mapBox = DialogParts.Check("FsExtract_Map", Loc.Get("FsExtract_Map"), true);
            ContentDialog dialog = DialogParts.Dialog(Root, "FsExtractDialog", Loc.Get(slack ? "Cmd_forensics_fs_exportSlack" : "Cmd_forensics_fs_exportUnallocated"),
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = chosen, TextWrapping = TextWrapping.Wrap }, mapBox } }, Loc.Get("FsExport_Export"));
            if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            path = chosen;
            map = mapBox.IsChecked == true;
        }

        IVolumeReader document = ReaderOf(d);
        try
        {
            long written = await Vm.Operations.RunAsync(Loc.Get(slack ? "FsExtract_SlackOperation" : "FsExtract_UnallocatedOperation"), OperationKind.ReadOnly,
                d.Document, null, op =>
                {
                    IEnumerable<ExtractMapRow> rows;
                    if (slack)
                    {
                        FileTailTable tails = FileTailTable.Build(session.FileSystem, keepPaths: true, null, op.CancellationToken);
                        rows = FileSystemExtraction.SlackRuns(session.FileSystem, session.VolumeOffset, tails);
                    }
                    else
                    {
                        if (session.FileSystem.Allocation is { } allocation)
                        {
                            op.SetTotal(allocation.CountFree(op.CancellationToken) * session.FileSystem.ClusterSize);
                        }

                        rows = FileSystemExtraction.UnallocatedRuns(session.FileSystem, session.VolumeOffset, op.CancellationToken);
                    }

                    return Task.FromResult(FileSystemExtraction.Write(document, rows, path, map, n => op.Report(n), op.CancellationToken));
                });
            FileSystemAudit.Record(slack ? FileSystemAuditAction.ExportSlack : FileSystemAuditAction.ExportUnallocated, d.DisplayName,
                new Dictionary<string, string> { ["destination"] = path, ["bytes"] = written.ToString(CultureInfo.InvariantCulture) });
            ShowNotice(Loc.Format("FsExtract_Done", StatusFormat.ShortSize(written, CultureInfo.CurrentCulture) ?? Number(written), path), InfoBarSeverity.Success, d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("FsExport_Failed", ex.Message), InfoBarSeverity.Error, d);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- 構造の表示 (FOR-06 の仕様 6、FOR-10 の仕様 3・6・9、FOR-11) ----

    private static string TemplateFolder => Path.Combine(Program.Environment.Locations.Settings, "templates-forensics");

    /// <summary>テンプレートを適用する (テンプレートのパネルにツリー、Hex ビューに色分け)。</summary>
    private void ApplyStructureTemplate(DocumentViewModel d, string fileName, string text, long offset)
    {
        string path = FileSystemTemplates.Write(TemplateFolder, fileName, text);
        AddTemplateApplication(d, new TemplateApplicationInfo { TemplatePath = path, StartOffset = offset });
    }

    /// <summary>「エントリの構造を表示」: パーティション表に標準テンプレート (MBR / GPT) を当てる。</summary>
    internal void ShowPartitionEntryStructure(DocumentViewModel d, PartitionRow row)
    {
        if (row.Template is not { } name || HexEditor.App.Templates.TemplateService.Catalog.Find(name) is not { } entry)
        {
            return;
        }

        long offset = name == "mbr" && row.EntryOffset is long e ? e / SectorSizeOf(d) * SectorSizeOf(d) : 0;
        AddTemplateApplication(d, new TemplateApplicationInfo { TemplatePath = entry.Path, StartOffset = offset });
    }

    /// <summary>「MFT レコードを表示」(NTFS)。</summary>
    internal void ShowMftRecord(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is { FileSystem: NtfsFileSystem ntfs } session && entry.MetadataOffset is long offset)
        {
            ApplyStructureTemplate(d, $"ntfs-record-{ntfs.RecordSize}.hxt", FileSystemTemplates.NtfsRecord(ntfs.RecordSize), session.VolumeOffset + offset);
            d.Editor.GoTo(session.VolumeOffset + offset);
        }
    }

    /// <summary>「INDX を表示」(NTFS のディレクトリ): 最初の INDX レコード。</summary>
    internal void ShowIndexRecord(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is { FileSystem: NtfsFileSystem ntfs } session && ntfs.GetExtents(entry).FirstOrDefault()?.VolumeOffset is long offset)
        {
            ApplyStructureTemplate(d, $"ntfs-indx-{ntfs.IndexRecordSize}.hxt", FileSystemTemplates.NtfsIndex(ntfs.IndexRecordSize), session.VolumeOffset + offset);
            d.Editor.GoTo(session.VolumeOffset + offset);
        }
    }

    /// <summary>「ディレクトリエントリを表示」(FAT・exFAT)。</summary>
    internal void ShowDirectoryEntry(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is not { } session || entry.MetadataOffset is not long offset)
        {
            return;
        }

        int count = Math.Max(1, entry.MetadataLength / 32);
        (string name, string text) = session.FileSystem is ExFatFileSystem
            ? ($"exfat-entries-{count}.hxt", FileSystemTemplates.ExFatEntries(count))
            : ($"fat-entries-{count}.hxt", FileSystemTemplates.FatDirectoryEntries(count));
        ApplyStructureTemplate(d, name, text, session.VolumeOffset + offset);
        d.Editor.GoTo(session.VolumeOffset + offset);
    }

    /// <summary>「クラスタチェーンを表示」(FAT・exFAT): クラスタ番号の並びを結果一覧に出す。</summary>
    internal void ShowClusterChain(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is not { } session)
        {
            return;
        }

        IReadOnlyList<long> clusters = session.FileSystem switch
        {
            FatFileSystem fat => fat.ChainOf(entry).Clusters,
            ExFatFileSystem exfat => exfat.ChainOf(entry),
            _ => [],
        };
        ForensicList list = NewForensicList(d, ForensicListKind.FileSearch, Loc.Format("Fs_ChainTitle", entry.Name, clusters.Count), Loc.Get("Fs_ChainHeader"));
        var rows = new List<ForensicRow>();
        int index = 0;
        foreach (long c in clusters.Take(100_000))
        {
            rows.Add(new ForensicRow($"{index++,8}  {Number(c),14}", session.ClusterDocumentOffset(c)));
        }

        if (entry.Corruption != FsCorruptionKind.None)
        {
            rows.Add(new ForensicRow(Loc.Get("FsCorrupt_" + entry.Corruption) + " " + entry.CorruptionDetail, null));
        }

        AppendRows(d, list, rows, string.Empty);
    }

    // ---- $LogFile (FOR-10 の仕様 9) ----

    /// <summary>「$LogFile を表示」: リスタート領域と、ログレコードの一覧を結果一覧に出す (長時間処理。ログの再生はしない)。</summary>
    internal async Task ShowLogFileAsync(DocumentViewModel d)
    {
        if (StateOf(d)?.Session is not { FileSystem: NtfsFileSystem ntfs } || NtfsLogFile.Open(ntfs) is not { } log)
        {
            return;
        }

        IReadOnlyList<NtfsRestartArea> restart = NtfsLogFile.ReadRestartAreas(log, ntfs.BytesPerSector);
        string restartText = string.Join("  ", restart.Select(r => Loc.Format("LogFile_Restart", r.PageOffset / 4096, r.CurrentLsn.ToString(CultureInfo.InvariantCulture),
            string.Join(", ", r.Clients.Select(c => c.Name)))));
        ForensicList list = NewForensicList(d, ForensicListKind.LogFile, Loc.Get("LogFile_Title"), Loc.Get("LogFile_Header"));
        list.Status = restartText;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("LogFile_OperationName"), OperationKind.ReadOnly, d.Document, log.Length, op =>
            {
                var batch = new List<ForensicRow>();
                foreach (NtfsLogRecord r in NtfsLogFile.ReadRecords(log, ntfs.MftRuns, ntfs.ClusterSize, ntfs.RecordSize, ntfs.BytesPerSector,
                    cancellationToken: op.CancellationToken))
                {
                    batch.Add(new ForensicRow(string.Format(CultureInfo.InvariantCulture, "{0,12}  {1,12}  {2,12}  {3,-7}  {4,8:X}  {5,-30}  {6,-30}  {7,8}  {8,6}",
                        r.Lsn, r.PreviousLsn, r.UndoNextLsn, r.RecordType == 1 ? "Client" : "Restart", r.TransactionId, r.RedoName, r.UndoName,
                        r.TargetRecord?.ToString(CultureInfo.InvariantCulture) ?? "-", r.Length), null, r));
                    op.Report(r.PageOffset);
                    if (batch.Count >= 500)
                    {
                        AppendRows(d, list, batch);
                        batch = [];
                    }
                }

                AppendRows(d, list, batch, restartText);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- ファイルの検索 (FOR-09 の仕様 7) ----

    /// <summary>「ファイルを検索」: ボリューム全体を名前で検索し、結果一覧に出す (長時間処理。見つかった順に加える)。</summary>
    internal async Task FindFilesAsync(DocumentViewModel d, string pattern)
    {
        if (StateOf(d)?.Session is not { } session)
        {
            return;
        }

        var filter = new FsNameFilter(pattern);
        ForensicList list = NewForensicList(d, ForensicListKind.FileSearch, Loc.Format("Fs_SearchTitle", pattern), Loc.Get("Fs_SearchHeader"));
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Fs_SearchOperation", pattern), OperationKind.ReadOnly, d.Document, 1_000_000, op =>
            {
                var batch = new List<ForensicRow>();
                long found = 0;
                foreach (FsEntry e in session.FileSystem.EnumerateAll(OperationProgress(op), op.CancellationToken))
                {
                    if (filter.Matches(e.Name))
                    {
                        batch.Add(new ForensicRow($"{Number(e.Size),14}  {e.Path}",
                            session.DocumentFragments(e).FirstOrDefault() is { Length: > 0 } f ? f.Offset : null, e));
                        op.ReportMatches(++found);
                        if (batch.Count >= 200)
                        {
                            AppendRows(d, list, batch);
                            batch = [];
                        }
                    }
                }

                AppendRows(d, list, batch, Loc.Format("Fs_SearchDone", found));
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>一覧を CSV / JSON に書き出す (FOR-09 の仕様 6、FOR-08 の仕様 5)。</summary>
    internal async Task ExportListAsync(string suggestedName, Func<bool, string> content)
    {
        string? path;
        if (!TestHooks.TrySavePicker(suggestedName, out path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.FsList" };
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            picker.FileTypeChoices.Add("JSON", [".json"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        try
        {
            bool json = string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(path, content(json), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("FsExport_Failed", ex.Message), InfoBarSeverity.Error);
        }
    }
}
