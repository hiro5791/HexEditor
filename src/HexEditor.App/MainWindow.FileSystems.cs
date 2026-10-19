using System.Collections.Specialized;
using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ntfs;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Selection;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// パーティションとファイルシステム (FOR-06 パーティション解析、FOR-08 失われたパーティションのスキャン、FOR-09 ファイルシステムブラウザ、FOR-10 NTFS、
/// FOR-11 FAT / exFAT、FOR-15 クラスタの対応付け、FOR-16 強調と抽出、FOR-17 削除項目)。解析は Core (HexEditor.Core.FileSystems) が行い、
/// ここはドキュメントへの結び付け・タブ・コマンドを受け持つ。
/// </summary>
public sealed partial class MainWindow
{
    internal const string PartitionsPanelId = "partitions";
    internal const string FileSystemPanelId = "fileSystem";
    internal const string FragmentsPanelId = "fragments";
    internal const string ForensicResultsPanelId = "forensicResults";

    /// <summary>設定のキー (FOR-06 の仕様 4・6、FOR-10 の仕様 5、FOR-16 の仕様 2)。</summary>
    internal const string ShowSmallGapsKey = "forensics.partitions.showSmallGaps";
    internal const string PartitionAbsoluteOffsetsKey = "forensics.partitions.absoluteOffsets";
    internal const string ShowStreamsKey = "forensics.fileSystem.showStreams";
    internal const string HighlightLegendKey = "forensics.highlight.legend";
    internal const string HighlightColorsKey = "forensics.highlight.colors";

    /// <summary>パネルを登録する (起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterFileSystemPanels()
    {
        Register(PartitionsPanelId, "Panel_Partitions_Title", PanelDock.Left, ctx => new PartitionPanel(ctx), "PartitionPanel");
        Register(FileSystemPanelId, "Panel_FileSystem_Title", PanelDock.Left, ctx => new FileSystemPanel(ctx), "FileSystemPanel");
        Register(FragmentsPanelId, "Panel_Fragments_Title", PanelDock.Bottom, ctx => new FragmentListPanel(ctx), "FragmentListPanel");
        Register(ForensicResultsPanelId, "Panel_ForensicResults_Title", PanelDock.Bottom, ctx => new ForensicResultsPanel(ctx), "ForensicResultsPanel");

        static void Register(string id, string title, PanelDock dock, Func<PanelContext, Microsoft.UI.Xaml.FrameworkElement> create, string automationId)
        {
            if (PanelRegistry.Find(id) is null)
            {
                PanelRegistry.Register(new PanelRegistration(id, title, dock, ctx =>
                {
                    Microsoft.UI.Xaml.FrameworkElement panel = create(ctx);
                    AutomationProperties.SetAutomationId(panel, automationId);
                    return panel;
                }));
            }
        }
    }

    private void RegisterFileSystemCommands()
    {
        Vm.Documents.CollectionChanged += OnDocumentsChangedForPartitions;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Documents.CollectionChanged -= OnDocumentsChangedForPartitions;
            }
        };

        // ---- パーティション (FOR-06・FOR-08) ----
        Commands.Register("forensics.partitions.show", ShowPartitionsPanel, NeedsDocument);
        Commands.Register("forensics.partitions.openInNewTab", () => OpenPartitionAtCursor(), () => NeedsPartitionAtCursor());
        Commands.Register("forensics.partitions.goToStart", GoToPartitionStartAtCursor, () => NeedsPartitionAtCursor());
        Commands.Register("forensics.partitions.openHere", OpenPartitionHere, NeedsDocument);
        Commands.Register("forensics.partitions.scan", () => ShowLostPartitionScanAsync(null), NeedsDocument);

        // ---- ファイルシステム (FOR-09・FOR-10・FOR-11・FOR-17) ----
        Commands.Register("forensics.fs.show", () => ShowFileSystemForActiveAsync(), NeedsDocument);
        Commands.Register("forensics.fs.openFile", () => OpenSelectedFsEntryAsync(), () => NeedsFsEntry());
        Commands.Register("forensics.fs.exportFiles", () => ExportSelectedFsEntriesAsync(), () => NeedsFsEntry());
        Commands.Register("forensics.fs.findDeleted", ShowDeletedFiles, () => NeedsSession());
        Commands.Register("forensics.fs.goToMftRecord", new CommandHandler(GoToMftRecordAsync,
            () => NeedsSession(s => s.FileSystem is NtfsFileSystem ? null : Loc.Get("Fs_NtfsOnly"))));

        // ---- クラスタの対応付け (FOR-15) ----
        Commands.Register("forensics.fs.goToCluster", () => ShowGoToClusterAsync(), () => NeedsSession());
        Commands.Register("forensics.fs.whoUsesThis", () => FindOwnersAtCursorAsync(), () => NeedsSession());
        Commands.Register("forensics.fs.showFileClusters", ShowSelectedFileClusters, () => NeedsFsEntry());
        Commands.Register("forensics.fs.nextFragment", () => MoveToFragment(next: true), () => NeedsHighlightedFragments());
        Commands.Register("forensics.fs.previousFragment", () => MoveToFragment(next: false), () => NeedsHighlightedFragments());

        // ---- 強調と抽出 (FOR-16) ----
        Commands.Register("forensics.fs.toggleHighlight", ToggleForensicHighlight,
            () => NeedsSession(s => s.Classifier.CanClassify ? null : Loc.Get("Fs_HighlightUnavailable")) is { Enabled: true }
                ? Toggle(Vm.Selected is { } d && FileSystemDocumentState.Find(d.Document)?.HighlightOn == true)
                : NeedsSession(s => s.Classifier.CanClassify ? null : Loc.Get("Fs_HighlightUnavailable")));
        Commands.Register("forensics.fs.exportUnallocated", () => ExportUnallocatedAsync(), () => NeedsSession(s => s.FileSystem.Allocation is null ? Loc.Get("Fs_HighlightUnavailable") : null));
        Commands.Register("forensics.fs.exportSlack", () => ExportSlackAsync(), () => NeedsSession());
        Commands.Register("forensics.fs.selectUnallocated", () => SelectRegionsAsync(slack: false), () => NeedsSession(s => s.FileSystem.Allocation is null ? Loc.Get("Fs_HighlightUnavailable") : null));
        Commands.Register("forensics.fs.selectSlack", () => SelectRegionsAsync(slack: true), () => NeedsSession());
        Commands.Register("forensics.fs.openUnallocated", OpenUnallocatedTab, () => NeedsSession(s => s.FileSystem.Allocation is null ? Loc.Get("Fs_HighlightUnavailable") : null));

        // ---- LDM・LVM2 (FOR-07)、ext (FOR-12) ----
        RegisterFileSystemMoreCommands();
    }

    // ---- ドキュメントの判別 ----

    /// <summary>ディスクまたはディスクイメージのドキュメントか (パーティション解析を自動で行う。FOR-06 の「呼び出し」)。</summary>
    internal static bool IsDiskDocument(DocumentViewModel d) =>
        d.Document.LinkedParent is null && (d.Document.Source is DiskImageByteSource || d.Device?.Info.Disk is not null);

    /// <summary>ドキュメントのセクタサイズ (ディスクはデバイス、イメージは開くときの指定。ファイルは 512)。</summary>
    internal static int SectorSizeOf(DocumentViewModel d) => d.Document.Source.LogicalSectorSize is >= 128 and var s ? s : 512;

    /// <summary>解析が読むドキュメントの内容 (変換ビューを通さない元のデータ)。</summary>
    internal static IVolumeReader ReaderOf(DocumentViewModel d) => new CachedVolumeReader(new SnapshotVolumeReader(d.Document.DataSnapshot), 64 * 1024, 64);

    internal FileSystemDocumentState? StateOf(DocumentViewModel? d) => d is null ? null : FileSystemDocumentState.For(d.Document);

    private CommandState NeedsSession(Func<FileSystemSession, string?>? more = null) =>
        Vm.Selected is not { } d ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
        : StateOf(d)?.Session is not { } s ? CommandState.Unavailable(Loc.Get("Fs_NoFileSystem"))
        : more?.Invoke(s) is { } reason ? CommandState.Unavailable(reason) : CommandState.Available;

    private CommandState NeedsFsEntry() =>
        NeedsSession() is { Enabled: false } state ? state
        : FileSystemView?.SelectedEntries.Count > 0 ? CommandState.Available : CommandState.Unavailable(Loc.Get("Fs_NoEntry"));

    private CommandState NeedsHighlightedFragments() =>
        Vm.Selected is { } d && StateOf(d)?.HighlightedFragments.Count > 0 ? CommandState.Available : CommandState.Unavailable(Loc.Get("Fs_NoFragments"));

    private CommandState NeedsPartitionAtCursor() =>
        Vm.Selected is not { } d ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
        : StateOf(d)?.Partitions?.PartitionAt(d.Editor.Cursor) is null ? CommandState.Unavailable(Loc.Get("Part_NoPartitionAtCursor")) : CommandState.Available;

    internal PartitionPanel? PartitionView => FindPanelContent<PartitionPanel>(PartitionsPanelId);

    internal FileSystemPanel? FileSystemView => FindPanelContent<FileSystemPanel>(FileSystemPanelId);

    // ---- パーティションの自動解析 (FOR-06) ----

    private void OnDocumentsChangedForPartitions(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel d in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            if (IsDiskDocument(d))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!Vm.Documents.Contains(d))
                    {
                        return;
                    }

                    ShowPanel(PartitionsPanelId, focus: false);
                    _ = AnalyzePartitionsAsync(d);
                });
            }
        }
    }

    private void ShowPartitionsPanel()
    {
        ShowPanel(PartitionsPanelId, focus: true);
        if (Vm.Selected is { } d && StateOf(d) is { Partitions: null, PartitionsRunning: false })
        {
            _ = AnalyzePartitionsAsync(d);
        }
    }

    /// <summary>
    /// パーティションを解析する (非同期。終わるまで一覧に「解析中」を出す。FOR-06 の「巨大ファイル」)。GPT が見つからず別のセクタサイズで見つかれば、
    /// セクタサイズの変更を InfoBar で提案する (仕様 2)。
    /// </summary>
    internal async Task AnalyzePartitionsAsync(DocumentViewModel d)
    {
        FileSystemDocumentState state = StateOf(d)!;
        if (state.PartitionsRunning)
        {
            return;
        }

        state.PartitionsRunning = true;
        state.RaiseChanged();
        int sector = SectorSizeOf(d);
        var options = new PartitionAnalyzerOptions { ShowSmallGaps = App.Settings.GetBool(ShowSmallGapsKey, false) };
        IVolumeReader reader = ReaderOf(d);
        try
        {
            // 暗号化の検出 (FOR-29) もパーティションごとに行う (元のデータを読む)。
            var data = new Core.Forensics.Carving.SnapshotCarveData(d.Document.DataSnapshot);
            state.Partitions = await Task.Run(() => PartitionEncryption.Annotate(PartitionAnalyzer.Analyze(reader, sector, options), data));
            state.PartitionsSectorSize = sector;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            AppLog.Info($"partition analysis failed: {ex.Message}");
        }
        finally
        {
            state.PartitionsRunning = false;
            state.RaiseChanged();
        }

        if (state.Partitions?.SuggestedSectorSize is int suggested && !state.SuggestionShown && Vm.Documents.Contains(d))
        {
            state.SuggestionShown = true;
            var actions = new List<Core.Notifications.NotificationAction>();
            if (d.Document.Source is DiskImageByteSource)
            {
                actions.Add(new Core.Notifications.NotificationAction(Loc.Format("Part_ReopenWithSector", suggested.ToString(CultureInfo.InvariantCulture)), () => ReopenWithSectorSize(d, suggested)));
            }

            ShowNotice(Loc.Format("Part_SuggestSector", suggested.ToString(CultureInfo.InvariantCulture), sector.ToString(CultureInfo.InvariantCulture)), InfoBarSeverity.Informational, d, actions: actions);
        }

        UpdateForensicStatus();
    }

    /// <summary>ディスクイメージを別のセクタサイズで開き直す (FOR-06 の仕様 2 の提案に従う)。</summary>
    internal DocumentViewModel? ReopenWithSectorSize(DocumentViewModel d, int sectorSize)
    {
        if (d.Document.Source is not DiskImageByteSource image || d.Document.IsModified || !Vm.Documents.Contains(d))
        {
            return null;
        }

        string path = image.Path;
        Vm.Close(d);
        DiskImageByteSource source = DiskImage.Open(path, new DiskImageOptions { SectorSize = sectorSize });
        DocumentViewModel opened = Vm.OpenDiskImageSource(source);
        UpdateTitle();
        return opened;
    }

    // ---- パーティションのタブ (FOR-06 の仕様 6・7) ----

    /// <summary>パーティションの番号を付けたタブの名前 (<c>ディスク 2 - パーティション 3</c>)。</summary>
    internal static string PartitionTabName(DocumentViewModel disk, PartitionRow row) =>
        Loc.Format("Part_TabName", disk.DisplayName, row.Number);

    /// <summary>
    /// パーティションの範囲を、先頭をオフセット 0 とする別タブで開く (仕様 6)。元のディスクのドキュメントとデータソースを共有する連動ビュー
    /// (ENG-39) で、セクタサイズ・読み取り専用・書き込み保護の状態を引き継ぐ。表示のベースアドレスは 0 (設定で絶対オフセット)。
    /// </summary>
    internal DocumentViewModel? OpenPartitionTab(DocumentViewModel disk, long offset, long length, string name, PartitionRow? row)
    {
        DocumentViewModel root = disk.LinkParent ?? disk;
        long start = offset + (disk.LinkParent is not null ? disk.Document.LinkStart : 0);
        length = Math.Min(length, root.Document.Length - start);
        if (length <= 0)
        {
            return null;
        }

        DocumentViewModel? vm = OpenRangeInNewTab(root, start, length, name, copy: false);
        if (vm is null)
        {
            return null;
        }

        vm.RangeLabel = string.Empty;
        if (!App.Settings.GetBool(PartitionAbsoluteOffsetsKey, false))
        {
            vm.Editor.ApplyView(vm.Editor.View with { BaseAddress = 0 });
        }

        FileSystemDocumentState state = StateOf(vm)!;
        state.PartitionParent = root;
        state.PartitionRow = row;
        UpdateForensicStatus();
        return vm;
    }

    internal DocumentViewModel? OpenPartitionTab(DocumentViewModel disk, PartitionRow row) =>
        OpenPartitionTab(disk, row.StartOffset, row.Size, PartitionTabName(disk, row), row);

    private DocumentViewModel? OpenPartitionAtCursor()
    {
        if (Vm.Selected is { } d && StateOf(d)?.Partitions?.PartitionAt(d.Editor.Cursor) is { } row)
        {
            return OpenPartitionTab(d, row);
        }

        return null;
    }

    private void GoToPartitionStartAtCursor()
    {
        if (Vm.Selected is { } d && StateOf(d)?.Partitions?.PartitionAt(d.Editor.Cursor) is { } row)
        {
            d.Editor.GoTo(Math.Clamp(row.StartOffset, 0, Math.Max(0, d.Document.Length - 1)));
        }
    }

    /// <summary>「ここをパーティションの先頭として開く」(仕様 7): カーソル位置 (セクタ境界に切り下げ) から末尾まで。</summary>
    private void OpenPartitionHere()
    {
        if (Vm.Selected is not { } d)
        {
            return;
        }

        int sector = SectorSizeOf(d);
        long start = d.Editor.Cursor / sector * sector;
        OpenPartitionTab(d, start, d.Document.Length - start, Loc.Format("Part_TabNameAt", d.DisplayName, start / sector), null);
    }

    // ---- ファイルシステム (FOR-09) ----

    /// <summary>
    /// 作業中のドキュメントのファイルシステムを表示する。ディスクのドキュメントでカーソルがパーティションの中にあれば、そのパーティションのファイルシステム、
    /// そうでなければドキュメントの先頭から。
    /// </summary>
    private Task ShowFileSystemForActiveAsync()
    {
        if (Vm.Selected is not { } d)
        {
            return Task.CompletedTask;
        }

        FileSystemDocumentState state = StateOf(d)!;
        if (state.Session is not null)
        {
            ShowPanel(FileSystemPanelId, focus: true);
            return Task.CompletedTask;
        }

        if (state.Partitions?.PartitionAt(d.Editor.Cursor) is { } row && !row.IsContainer)
        {
            return ShowFileSystemAsync(d, row.StartOffset, row.Size);
        }

        return ShowFileSystemAsync(d, 0, d.Document.Length);
    }

    /// <summary>
    /// ファイルシステムを解析してブラウザに出す (仕様 1)。署名で種類を判定し、判定できなければ種類を選ぶダイアログを出す。暗号化されたボリュームは開かない。
    /// </summary>
    internal async Task<FileSystemSession?> ShowFileSystemAsync(DocumentViewModel d, long offset, long length, FileSystemKind? kind = null,
        FileSystemOpenOptions? options = null)
    {
        FileSystemDocumentState state = StateOf(d)!;
        IVolumeReader volume = ReaderOf(d).Slice(offset, length);
        FileSystemSignature? signature = await Task.Run(() => FileSystemDetector.Detect(volume, 0, checkBackup: false));
        bool chosen = kind is not null;
        kind ??= signature?.Kind;

        // 暗号化されたボリュームは開かない (FOR-29 の仕様 4)。APFS のコンテナはボリュームごとに FOR-13 の解析が暗号化のフラグを見て
        // ファイル一覧を出さないので、ここでは止めない。種類を選んで開くときは、署名で判定できる暗号化だけで止める。
        Guid? partitionType = state.Partitions?.Rows.FirstOrDefault(r => !r.IsUnallocated && r.StartOffset == offset && r.Size == length)?.TypeGuid;
        var data = new Core.Forensics.Carving.SnapshotCarveData(d.Document.DataSnapshot);
        Core.Forensics.Encryption.EncryptionInfo? encrypted = await Task.Run(() => Core.Forensics.Encryption.EncryptionDetector.FileSystemBlockReason(data, offset, length, partitionType));
        if (encrypted is { Kind: not Core.Forensics.Encryption.EncryptionKind.ApfsEncrypted }
            && !(chosen && encrypted.Kind == Core.Forensics.Encryption.EncryptionKind.PossiblyEncrypted))
        {
            kind = FileSystemKind.BitLocker;
        }

        if (kind == FileSystemKind.BitLocker)
        {
            state.FileSystemMessage = encrypted is { } info && info.Kind is not (Core.Forensics.Encryption.EncryptionKind.BitLocker or Core.Forensics.Encryption.EncryptionKind.BitLockerToGo)
                ? Loc.Format("Fs_EncryptedKind", Loc.Get("Encryption_Kind_" + info.Kind))
                : Loc.Get("Fs_Encrypted");
            state.RaiseChanged();
            ShowPanel(FileSystemPanelId, focus: false);
            return null;
        }

        if (kind is null or FileSystemKind.Unknown || !FileSystemRegistry.CanOpen(kind.Value))
        {
            kind = await ChooseFileSystemKindAsync();
            if (kind is null)
            {
                return null;
            }
        }

        options = (options ?? new FileSystemOpenOptions()) with
        {
            Kind = kind,
            SectorSize = SectorSizeOf(d),
            ShowStreams = App.Settings.GetBool(ShowStreamsKey, true),
            LocalOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow),
        };
        try
        {
            IFileSystem fs = await Task.Run(() => FileSystemRegistry.Open(volume, options));
            var session = new FileSystemSession(d.Document, fs, offset, length, IsDiskDocument(d) ? state.Partitions : null);
            FileSystemSession.Attach(d.Document, session);
            state.Session = session;
            state.FileSystemMessage = null;
            state.HighlightOn = false;
            state.HighlightedEntry = null;
            state.HighlightedFragments = [];
            state.RaiseChanged();
            ShowPanel(FileSystemPanelId, focus: false);
            UpdateForensicStatus();
            RefreshForensicHighlights(d);
            return session;
        }
        catch (FileSystemOpenException ex)
        {
            state.FileSystemMessage = Loc.Format("Fs_OpenFailed", FileSystemDetector.DisplayName(kind.Value), ex.MessageKey is { } key ? Loc.Get(key) : ex.Message);
            state.RaiseChanged();
            ShowPanel(FileSystemPanelId, focus: false);
            ShowNotice(state.FileSystemMessage, InfoBarSeverity.Warning, d);
            return null;
        }
    }

    /// <summary>判定できないときに種類を選ぶ (FOR-09 の仕様 1)。</summary>
    private async Task<FileSystemKind?> ChooseFileSystemKindAsync()
    {
        IReadOnlyList<FileSystemKind> kinds = FileSystemRegistry.OpenableKinds;
        if (TestHooks.FileSystemKindChoice is { } forced)
        {
            return Enum.TryParse(forced, out FileSystemKind k) && kinds.Contains(k) ? k : null;
        }

        ComboBox combo = Controls.DialogParts.Combo("FsKindChoice", Loc.Get("Fs_ChooseKind_Label"), kinds.Select(FileSystemDetector.DisplayName), 0);
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "FsKindDialog", Loc.Get("Fs_ChooseKind_Title"),
            new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = Loc.Get("Fs_ChooseKind_Body"), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 420 }, combo },
            }, Loc.Get("Fs_ChooseKind_Open"));
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary && combo.SelectedIndex >= 0 ? kinds[combo.SelectedIndex] : null;
    }

    /// <summary>ファイルシステムを閉じる (ブラウザ・強調を外す)。</summary>
    internal void CloseFileSystem(DocumentViewModel d)
    {
        FileSystemDocumentState state = StateOf(d)!;
        FileSystemSession.Attach(d.Document, null);
        state.Session = null;
        state.HighlightOn = false;
        state.HighlightedEntry = null;
        state.HighlightedFragments = [];
        state.RaiseChanged();
        RefreshForensicHighlights(d);
        UpdateForensicStatus();
    }

    /// <summary>ファイルシステムの項目の断片を選択し、先頭へジャンプする (仕様 5 のダブルクリック・Enter。FOR-15)。</summary>
    internal void JumpToFsEntry(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is not { } session)
        {
            return;
        }

        IReadOnlyList<(long Offset, long Length, FsExtent Extent)> fragments = session.DocumentFragments(entry);
        if (fragments.Count == 0)
        {
            if (entry.MetadataOffset is long meta)
            {
                d.Editor.GoTo(Math.Clamp(session.VolumeOffset + meta, 0, Math.Max(0, d.Document.Length - 1)));
            }

            return;
        }

        // 常駐データ (NTFS) はその格納位置へ。
        d.Editor.SelectFragments(fragments.Select(f => new ByteRange(f.Offset, Math.Min(f.Length, d.Document.Length - f.Offset))));
        FocusEditor();
    }

    /// <summary>メタデータの位置 (MFT レコード・ディレクトリエントリ) へ移動する。</summary>
    internal void GoToFsMetadata(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is { } session && entry.MetadataOffset is long meta)
        {
            d.Editor.GoTo(Math.Clamp(session.VolumeOffset + meta, 0, Math.Max(0, d.Document.Length - 1)));
            FocusEditor();
        }
    }

    private Task OpenSelectedFsEntryAsync()
    {
        if (Vm.Selected is { } d && FileSystemView?.SelectedEntries is { Count: > 0 } entries)
        {
            foreach (FsEntry e in entries.Where(e => !e.IsDirectory).Take(16))
            {
                OpenFsEntryTab(d, e);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 「ファイルを別タブで開く」(仕様 6): ファイルの論理的な内容 (断片をつないだもの。スパースは 0、圧縮は展開したもの) を読み取り専用のタブで開く。
    /// タブ名は <c>ファイル名 (ディスク 2 - パーティション 3)</c>。タブのオフセットとディスク上のオフセットの対応はステータスバーに出す。
    /// </summary>
    internal DocumentViewModel? OpenFsEntryTab(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is not { } session)
        {
            return null;
        }

        IVolumeReader content = session.FileSystem.OpenContent(entry);
        string name = Loc.Format("Fs_FileTabName", entry.Name, d.DisplayName);
        var source = new FileSystemContentSource(content, name, $"fs:{d.Document.Id}:{session.VolumeOffset}:{entry.Id}");
        DocumentViewModel vm = Vm.AddFileSystemContent(source, name);
        long diskBase = session.VolumeOffset + (d.LinkParent is not null ? d.Document.LinkStart : 0);
        IFileSystem fs = session.FileSystem;
        StateOf(vm)!.DiskOffsetOf = logical => (fs is NtfsFileSystem ntfs ? ntfs.VolumeOffsetOf(entry, logical)
            : content is ExtentContentReader ext ? ext.VolumeOffsetOf(logical) : null) is long v ? diskBase + v : null;
        UpdateTitle();
        UpdateForensicStatus();
        return vm;
    }

    private void ShowDeletedFiles()
    {
        ShowPanel(FileSystemPanelId, focus: true);
        FileSystemView?.SelectVirtualFolder(FsVirtualFolder.Deleted);
    }

    /// <summary>「フォレンジック: MFT レコードへ移動」(MFT 番号を入力)。</summary>
    internal async Task GoToMftRecordAsync(string? argument)
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { FileSystem: NtfsFileSystem ntfs } session)
        {
            return;
        }

        string? text = argument is { Length: > 0 } ? argument : await PromptAsync(Loc.Get("Cmd_forensics_fs_goToMftRecord"), Loc.Get("CmdArg_forensics_fs_goToMftRecord"));
        if (text is null)
        {
            return;
        }

        if (!Controls.DialogParts.TryEvaluate(text, new Core.View.EditorExpressionContext(d.Editor), out long number, out _, Core.Expressions.DefaultRadix.Decimal)
            || ntfs.RecordOffset(number) is not long offset)
        {
            ShowNotice(Loc.Format("Fs_MftRecordOutOfRange", text), InfoBarSeverity.Warning, d);
            return;
        }

        d.Editor.GoTo(session.VolumeOffset + offset);
        FocusEditor();
    }

    // ---- 未割り当て領域のタブ (FOR-16 の仕様 5) ----

    private void OpenUnallocatedTab()
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { } session)
        {
            return;
        }

        IVolumeReader document = ReaderOf(d);
        var source = new ConcatenatedByteSource(document, FileSystemExtraction.UnallocatedRuns(session.FileSystem, session.VolumeOffset),
            Loc.Format("Fs_UnallocatedTabName", d.DisplayName));
        DocumentViewModel vm = Vm.AddFileSystemContent(source, source.DisplayName);
        long diskBase = d.LinkParent is not null ? d.Document.LinkStart : 0;
        StateOf(vm)!.DiskOffsetOf = offset => source.DiskOffsetOf(offset) is long v ? diskBase + v : null;
        UpdateTitle();
        UpdateForensicStatus();
    }

    /// <summary>
    /// 「未割り当て領域を選択」「スラックを選択」(FOR-16 の仕様 7): 区間をマルチ選択 (EDIT-07) にする。検索 (FIND-06 の「選択範囲」)・文字列の抽出・カービングの
    /// 対象範囲として使う。区間の数がマルチ選択の上限を超える分は選ばない (知らせる)。
    /// </summary>
    internal async Task SelectRegionsAsync(bool slack)
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { } session)
        {
            return;
        }

        List<ByteRange> ranges = await Vm.Operations.RunAsync(Loc.Get(slack ? "Fs_SelectingSlack" : "Fs_SelectingUnallocated"), OperationKind.ReadOnly,
            d.Document, null, op =>
            {
                IEnumerable<ExtractMapRow> rows = slack
                    ? FileSystemExtraction.SlackRuns(session.FileSystem, session.VolumeOffset, session.Classifier.Tails ?? FileTailTable.Build(session.FileSystem, false, null, op.CancellationToken))
                    : FileSystemExtraction.UnallocatedRuns(session.FileSystem, session.VolumeOffset, op.CancellationToken);
                return Task.FromResult(rows.Select(r => new ByteRange(r.DiskOffset, r.Length)).Take(Core.View.EditorState.DefaultMaxSelectionElements + 1).ToList());
            });
        if (ranges.Count == 0)
        {
            ShowNotice(Loc.Get("Fs_NothingToSelect"), InfoBarSeverity.Informational, d);
            return;
        }

        if (d.Editor.SetSelections(ranges) == Core.View.SelectionResult.Truncated)
        {
            ShowNotice(Loc.Get("Fs_SelectionTruncated"), InfoBarSeverity.Warning, d);
        }
    }

    /// <summary>ドキュメントが閉じられた・変わったときにパネルを更新する。</summary>
    internal void NotifyForensicStateChanged(DocumentViewModel d)
    {
        StateOf(d)?.RaiseChanged();
        UpdateForensicStatus();
    }

    /// <summary>入力欄 1 つのダイアログ (コマンドパレットの引数がないとき)。</summary>
    private async Task<string?> PromptAsync(string title, string label)
    {
        TextBox box = Controls.DialogParts.Field("ForensicPrompt", label);
        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "ForensicPromptDialog", title, box, Loc.Get("Common_Ok"));
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>
    /// フォルダの中身を読む (バックグラウンドで 500 件ずつ <paramref name="batch"/> に渡す)。仮想フォルダ (削除されたファイル・孤立したファイル) は
    /// ボリューム全体の走査なので、長時間処理として処理センターに出す (FOR-09 の「巨大ファイル」、FOR-17)。
    /// </summary>
    internal void TrackFileSystemListing(FileSystemSession session, FsEntry folder, CancellationToken token, Action<IReadOnlyList<FsEntry>> batch,
        Action<string?> done)
    {
        FsVirtualFolder? kind = FileSystemSession.VirtualKind(folder);
        bool scan = kind is FsVirtualFolder.Deleted or FsVirtualFolder.Orphans;

        void Work(IProgress<double>? progress, CancellationToken ct)
        {
            var buffer = new List<FsEntry>();
            foreach (FsEntry e in session.Children(folder, progress, ct))
            {
                buffer.Add(e);
                if (buffer.Count >= 500)
                {
                    batch(buffer);
                    buffer = [];
                }
            }

            batch(buffer);
        }

        if (!scan)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Work(null, token);
                    done(null);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                {
                    done(ex.Message);
                }
            }, token);
            return;
        }

        _ = Vm.Operations.RunAsync(Loc.Get(kind == FsVirtualFolder.Deleted ? "Fs_ScanDeleted" : "Fs_ScanOrphans"), OperationKind.ReadOnly, session.Document,
            1_000_000, op =>
            {
                using CancellationTokenRegistration reg = token.Register(op.Cancel);
                Work(OperationProgress(op), op.CancellationToken);
                done(null);
                return Task.CompletedTask;
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    done(t.Exception?.InnerException?.Message);
                }
                else if (t.IsCanceled)
                {
                    done(Loc.Get("LostScan_Cancelled"));
                }
            }, TaskScheduler.Default);
    }

    /// <summary>長時間処理の進捗 (0〜1) を処理センターの値にする。</summary>
    private static IProgress<double> OperationProgress(LongRunningOperation op, long scale = 1_000_000) =>
        new Progress<double>(p => op.Report((long)(Math.Clamp(p, 0, 1) * scale)));

    private static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
