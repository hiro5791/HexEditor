using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Ext;
using HexEditor.Core.FileSystems.Partitions;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// LDM・LVM2 の論理ボリューム (FOR-07) と ext の操作 (FOR-12 の「inode を表示」「inode へ移動」、ジャーナルの一覧)。ISO 9660 / UDF・HFS+ / APFS は
/// ファイルシステムブラウザの共通の仕組み (IFileSystem) だけで表示する。
/// </summary>
public sealed partial class MainWindow
{
    private const string LvMemberPickerId = "HexEditor.LogicalVolumeMember";

    /// <summary>Core が <see cref="FileSystemOpenException.MessageKey"/> で返す文のキー (表示は <see cref="ShowFileSystemAsync"/>)。</summary>
    internal static readonly string[] FileSystemOpenMessageKeys = ["Fs_ApfsNoCheckpoint"];

    /// <summary>
    /// LDM のミラー・RAID-5 を FOR-26 (RAID の再構成) で開く処理 (統合で RAID の再構成の画面につなぐ)。設定 (構成要素・ストライプ・配置) と、見つかった構成要素の
    /// 読み込みを渡す。null のときは設定を通知に出すだけにする。
    /// </summary>
    internal static Func<MainWindow, LogicalVolumeRaidRequest, IReadOnlyDictionary<string, Core.FileSystems.IVolumeReader>, Task<bool>>? RaidReconstructionHook { get; set; }

    private void RegisterFileSystemMoreCommands()
    {
        Commands.Register("forensics.fs.goToInode", new CommandHandler(GoToInodeAsync,
            () => NeedsSession(s => s.FileSystem is ExtFileSystem ? null : Loc.Get("Fs_ExtOnly"))));
        Commands.Register("forensics.partitions.logicalVolumes", () => ShowLogicalVolumesAsync(), NeedsDocument);
    }

    // ---- ext (FOR-12) ----

    /// <summary>「フォレンジック: inode へ移動」(inode 番号を入力)。</summary>
    internal async Task GoToInodeAsync(string? argument)
    {
        if (Vm.Selected is not { } d || StateOf(d)?.Session is not { FileSystem: ExtFileSystem ext } session)
        {
            return;
        }

        string? text = argument is { Length: > 0 } ? argument : await PromptAsync(Loc.Get("Cmd_forensics_fs_goToInode"), Loc.Get("CmdArg_forensics_fs_goToInode"));
        if (text is null)
        {
            return;
        }

        if (!Controls.DialogParts.TryEvaluate(text, new EditorExpressionContext(d.Editor), out long number, out _, Core.Expressions.DefaultRadix.Decimal)
            || ext.InodeOffset(number) is not long offset)
        {
            ShowNotice(Loc.Format("Fs_InodeOutOfRange", text), InfoBarSeverity.Warning, d);
            return;
        }

        d.Editor.GoTo(session.VolumeOffset + offset);
        FocusEditor();
    }

    /// <summary>「inode を表示」: inode を構造体テンプレート (07) で色分けし、その位置へ移動する。</summary>
    internal void ShowInode(DocumentViewModel d, FsEntry entry)
    {
        if (StateOf(d)?.Session is { FileSystem: ExtFileSystem ext } session && entry.MetadataOffset is long offset)
        {
            ApplyStructureTemplate(d, $"ext-inode-{ext.InodeSize}.hxt", FileSystemTemplates.ExtInode(ext.InodeSize), session.VolumeOffset + offset);
            d.Editor.GoTo(session.VolumeOffset + offset);
        }
    }

    /// <summary>「ジャーナルを表示」(ext3/4): jbd2 のスーパーブロックとブロックの種類の一覧 (再生はしない。FOR-12 の仕様 6)。</summary>
    internal async Task ShowExtJournalAsync(DocumentViewModel d)
    {
        if (StateOf(d)?.Session is not { FileSystem: ExtFileSystem ext } session || ExtJournal.Read(ext) is not { } journal)
        {
            ShowNotice(Loc.Get("ExtJournal_None"), InfoBarSeverity.Informational, d);
            return;
        }

        ForensicList list = NewForensicList(d, ForensicListKind.Journal, Loc.Get("ExtJournal_Title"), Loc.Get("ExtJournal_Header"));
        string status = Loc.Format("ExtJournal_Status", journal.BlockSize, journal.MaxLength, journal.Sequence, journal.Start);
        list.Status = status;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("ExtJournal_OperationName"), OperationKind.ReadOnly, d.Document, journal.MaxLength, op =>
            {
                var batch = new List<ForensicRow>();
                foreach (ExtJournalBlock b in journal.Blocks(op.CancellationToken))
                {
                    long? disk = b.VolumeOffset is long v ? session.VolumeOffset + v : null;
                    batch.Add(new ForensicRow(string.Format(CultureInfo.InvariantCulture, "{0,10}  {1,16}  {2,-14}  {3,10}", b.Index,
                        disk is long o ? $"0x{o:X}" : "-", Loc.Get("ExtJournal_Type_" + b.Type), b.Sequence), disk, b));
                    op.Report(b.Index);
                    if (batch.Count >= 500)
                    {
                        AppendRows(d, list, batch);
                        batch = [];
                    }
                }

                AppendRows(d, list, batch, status);
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- 論理ボリューム (FOR-07) ----

    /// <summary>このウィンドウで開いているディスク・イメージ・パーティションから論理ボリュームを組み立て、パーティションパネルの下に一覧を出す。</summary>
    internal async Task ShowLogicalVolumesAsync()
    {
        if (Vm.Selected is not { } d)
        {
            return;
        }

        ShowPanel(PartitionsPanelId, focus: false);
        FileSystemDocumentState state = StateOf(d)!;
        state.LogicalVolumesRunning = true;
        state.RaiseChanged();
        var sources = Vm.Documents.OrderBy(doc => IsDiskDocument(doc) ? 0 : 1)
            .Select(doc => (Doc: doc, Reader: ReaderOf(doc), Partitions: StateOf(doc)?.Partitions, Sector: SectorSizeOf(doc))).ToList();
        try
        {
            state.LogicalVolumes = await Task.Run(() => LogicalVolumeAnalyzer.Analyze(sources.SelectMany(s =>
                LogicalVolumeAnalyzer.Discover(s.Reader, s.Doc.DisplayName, s.Partitions ?? PartitionAnalyzer.Analyze(s.Reader, s.Sector)))));
            state.LogicalVolumesMessage = state.LogicalVolumes.Volumes.Count == 0 ? Loc.Get("Lv_NoneFound") : null;
        }
        finally
        {
            state.LogicalVolumesRunning = false;
            state.RaiseChanged();
            PartitionView?.EnsureAttached();
        }
    }

    /// <summary>「論理ボリュームを開く」: 構成要素がそろっていれば読み取り専用のドキュメントとして開く。足りなければその識別子を知らせて開かない。</summary>
    internal async Task<DocumentViewModel?> OpenLogicalVolumeAsync(DocumentViewModel d, LogicalVolumeInfo volume, IReadOnlyDictionary<string, Core.FileSystems.IVolumeReader>? assigned = null)
    {
        FileSystemDocumentState state = StateOf(d)!;
        if (state.LogicalVolumes is not { } scan)
        {
            return null;
        }

        if (volume.Kind == LogicalVolumeKind.Unsupported)
        {
            SetLvMessage(d, Loc.Format("Lv_Unsupported", volume.TypeText), InfoBarSeverity.Warning);
            return null;
        }

        IReadOnlyList<string> missing = [.. scan.MissingMembers(volume).Where(id => assigned is null || !assigned.ContainsKey(Normalize(volume, id)))];
        if (missing.Count > 0)
        {
            SetLvMessage(d, Loc.Format("Lv_MissingMembers", volume.Name, string.Join(", ", missing)), InfoBarSeverity.Warning,
                [new Core.Notifications.NotificationAction(Loc.Get("Lv_Menu_Assign"), () => _ = AssignMembersAsync(d, volume))]);
            return null;
        }

        if (!volume.CanOpenDirectly)
        {
            if (volume.Raid is { } raid && RaidReconstructionHook is { } hook)
            {
                var readers = scan.Members.ToDictionary(m => m.Id, m => m.Reader, StringComparer.OrdinalIgnoreCase);
                await hook(this, raid, readers);
            }
            else
            {
                SetLvMessage(d, Loc.Format("Lv_NeedsRaid", volume.Name, volume.Layout), InfoBarSeverity.Informational);
            }

            return null;
        }

        LogicalVolumeByteSource source = LogicalVolumeAnalyzer.Open(volume, scan, assigned);
        DocumentViewModel vm = Vm.AddFileSystemContent(source, Loc.Format("Lv_TabName", volume.Name, volume.Group));
        state.LogicalVolumesMessage = null;
        state.RaiseChanged();
        UpdateTitle();
        return vm;
    }

    private static string Normalize(LogicalVolumeInfo volume, string id) =>
        volume.Scheme == LogicalVolumeScheme.Lvm2 ? Lvm2PhysicalVolumeLabel.Normalize(id) : id;

    private void SetLvMessage(DocumentViewModel d, string message, InfoBarSeverity severity, IReadOnlyList<Core.Notifications.NotificationAction>? actions = null)
    {
        FileSystemDocumentState state = StateOf(d)!;
        state.LogicalVolumesMessage = message;
        state.RaiseChanged();
        PartitionView?.EnsureAttached();
        ShowNotice(message, severity, d, actions: actions);
    }

    /// <summary>構成要素の割り当てダイアログ: 見つからない構成要素ごとに、開いているドキュメントかファイル (ディスクイメージとして開く) を選ぶ。</summary>
    internal async Task AssignMembersAsync(DocumentViewModel d, LogicalVolumeInfo volume)
    {
        if (StateOf(d)?.LogicalVolumes is not { } scan)
        {
            return;
        }

        IReadOnlyList<string> missing = scan.MissingMembers(volume);
        if (missing.Count == 0)
        {
            await OpenLogicalVolumeAsync(d, volume);
            return;
        }

        var panel = new StackPanel { Spacing = 8, MinWidth = 360, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = Loc.Format("Lv_AssignBody", volume.Name), TextWrapping = TextWrapping.Wrap });
        var choices = new List<(string Id, ComboBox Combo, List<DocumentViewModel?> Docs)>();
        foreach (string id in missing)
        {
            var docs = new List<DocumentViewModel?> { null };
            docs.AddRange(Vm.Documents);
            ComboBox combo = Controls.DialogParts.Combo("LvAssign_" + choices.Count, id, docs.Select(x => x?.DisplayName ?? Loc.Get("Lv_AssignNone")), 0);
            var pick = new Button { Content = Loc.Get("Lv_AssignFile") };
            AutomationProperties.SetAutomationId(pick, "LvAssignFile_" + choices.Count);
            pick.Click += async (_, _) =>
            {
                if (await PickLvMemberAsync() is { } path)
                {
                    DocumentViewModel opened = Vm.OpenDiskImageSource(DiskImage.Open(path, new DiskImageOptions { SectorSize = 512 }));
                    docs.Add(opened);
                    combo.Items.Add(opened.DisplayName);
                    combo.SelectedIndex = docs.Count - 1;
                }
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(combo);
            row.Children.Add(pick);
            panel.Children.Add(row);
            choices.Add((id, combo, docs));
        }

        ContentDialog dialog = Controls.DialogParts.Dialog(Root, "LvAssignDialog", Loc.Get("Lv_AssignTitle"), panel, Loc.Get("Lv_Menu_Open"));
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var assigned = new Dictionary<string, Core.FileSystems.IVolumeReader>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, ComboBox combo, List<DocumentViewModel?> docs) in choices)
        {
            if (combo.SelectedIndex > 0 && docs[combo.SelectedIndex] is { } doc)
            {
                assigned[Normalize(volume, id)] = MemberReader(doc, volume);
            }
        }

        await OpenLogicalVolumeAsync(d, volume, assigned);
    }

    /// <summary>割り当てたドキュメントの構成要素の読み込み (LVM2 なら PV のパーティションの範囲、なければドキュメント全体)。</summary>
    private Core.FileSystems.IVolumeReader MemberReader(DocumentViewModel doc, LogicalVolumeInfo volume)
    {
        Core.FileSystems.IVolumeReader reader = ReaderOf(doc);
        LogicalVolumeMember? member = LogicalVolumeAnalyzer.Discover(reader, doc.DisplayName, StateOf(doc)?.Partitions).FirstOrDefault(m => m.Scheme == volume.Scheme);
        return member?.Reader ?? reader;
    }

    private async Task<string?> PickLvMemberAsync()
    {
        if (TestHooks.OpenPickerResult(LvMemberPickerId) is { Count: > 0 } paths)
        {
            return paths[0];
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = LvMemberPickerId };
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>種類の表示 (一覧の「種類」)。</summary>
    internal static string LvKindText(LogicalVolumeInfo v) =>
        v.Kind == LogicalVolumeKind.Unsupported ? Loc.Format("LvKind_Unsupported", v.TypeText) : Loc.Get("LvKind_" + v.Kind);

    /// <summary>状態の表示 (一覧の「状態」)。</summary>
    internal static string LvStateText(LogicalVolumeInfo v, LogicalVolumeScan scan)
    {
        int missing = scan.MissingMembers(v).Count;
        return v.Kind == LogicalVolumeKind.Unsupported ? Loc.Get("Lv_State_Unsupported")
            : missing > 0 ? Loc.Format("Lv_State_Missing", missing)
            : v.CanOpenDirectly ? Loc.Get("Lv_State_Ok") : Loc.Get("Lv_State_Raid");
    }

    /// <summary>選んだ論理ボリュームの詳細 (グループ、構成要素とそれを見つけたドキュメント、セグメント)。</summary>
    internal static IEnumerable<string> LvDetailLines(LogicalVolumeInfo v, LogicalVolumeScan scan)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        yield return Loc.Format("Lv_Detail_Group", v.Scheme == LogicalVolumeScheme.Ldm ? "LDM" : "LVM2", v.Group, v.Name);
        foreach (string id in v.Members.Distinct())
        {
            LogicalVolumeMember? m = scan.Members.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            string shown = v.Scheme == LogicalVolumeScheme.Lvm2 ? Lvm2PhysicalVolumeLabel.Format(id) : id;
            yield return Loc.Format("Lv_Detail_Member", shown, m?.DisplayName ?? Loc.Get("Lv_Detail_Missing"));
        }

        foreach (LvPiece p in v.Pieces.Take(64))
        {
            string member = v.Scheme == LogicalVolumeScheme.Lvm2 ? Lvm2PhysicalVolumeLabel.Format(p.MemberId) : p.MemberId;
            yield return Loc.Format("Lv_Detail_Piece", $"0x{p.LogicalOffset:X}", StatusFormat.ShortSize(p.Length, c) ?? p.Length.ToString(c), member, $"0x{p.MemberOffset:X}");
        }

        foreach (LvStripeSet s in v.Stripes.Take(64))
        {
            yield return Loc.Format("Lv_Detail_Stripes", $"0x{s.LogicalOffset:X}", StatusFormat.ShortSize(s.Length, c) ?? s.Length.ToString(c),
                StatusFormat.ShortSize(s.StripeSize, c) ?? s.StripeSize.ToString(c), s.Columns.Count);
        }
    }

    /// <summary>一覧の 1 行 (名前、種類、サイズ、構成、状態)。</summary>
    internal static string LvRowText(LogicalVolumeInfo v, LogicalVolumeScan scan) =>
        string.Format(CultureInfo.CurrentCulture, "{0,-20}  {1,-12}  {2,10}  {3,-28}  {4}", $"{v.Group}/{v.Name}", LvKindText(v),
            StatusFormat.ShortSize(v.Size, CultureInfo.CurrentCulture) ?? v.Size.ToString(CultureInfo.CurrentCulture), v.Layout, LvStateText(v, scan));
}
