using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Operations;
using HexEditor.Core.Recovery;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>メインウィンドウの状態: 開いているドキュメントと、ファイル操作 (ENG-10、ENG-11、ENG-20〜ENG-22)。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppWideState _app;

    private readonly DocumentOptions _options;
    private readonly string _journalDirectory;

    public MainViewModel(OperationCenter operations, EngineMemory memory, DocumentOptions options, string journalDirectory)
    {
        Operations = operations;
        Memory = memory;
        _options = options;
        _journalDirectory = journalDirectory;
        _app = new AppWideState();
        Recent = new RecentFileList();
        ClosedTabs = new ClosedTabHistory();
    }

    /// <summary>
    /// 同じプロセスの別のウィンドウの状態 (UI-14)。長時間処理・メモリ・最近使ったファイル・閉じたタブ・外部変更の監視・無題の番号は
    /// アプリ全体で共有し、開いているドキュメントと通知はウィンドウごとに持つ。
    /// </summary>
    public MainViewModel(MainViewModel shared)
    {
        Operations = shared.Operations;
        Memory = shared.Memory;
        _options = shared._options;
        _journalDirectory = shared._journalDirectory;
        _app = shared._app;
        Recent = shared.Recent;
        ClosedTabs = shared.ClosedTabs;
        _files = shared._files;
        ExternalChanges = shared.ExternalChanges;
    }

    /// <summary>アプリ全体で 1 つの値 (ウィンドウの間で共有する)。</summary>
    private sealed class AppWideState
    {
        public int UntitledCount;

        public BackupSettings? Backup;
    }

    public OperationCenter Operations { get; }

    public EngineMemory Memory { get; }

    /// <summary>通知 (UI-36)。</summary>
    public Core.Notifications.NotificationCenter Notifications { get; } = new();

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    public partial DocumentViewModel? Selected { get; set; }

    /// <summary>新規作成 (ENG-10)。長さ 0 の「無題 N」を開く。</summary>
    public DocumentViewModel NewDocument() => NewDocument(0, [0]);

    /// <summary>
    /// サイズを指定して新規作成 (ENG-10 の仕様 2)。指定サイズの生成ピース 1 つで作るため、サイズに関係なく即座に開く。
    /// </summary>
    public DocumentViewModel NewDocument(long length, byte[] fill)
    {
        string name = Loc.Format("Untitled_Name", ++_app.UntitledCount);
        var doc = new Document(MemoryByteSource.CreateEmpty(name), _options);
        if (length > 0)
        {
            doc.InsertPattern(0, length, fill, Loc.Get("NewSize_Title"));
        }

        return Add(doc, null, name);
    }

    /// <summary>ファイルを開く (ENG-11)。同じファイルが開いていればそのタブを選ぶ。</summary>
    /// <param name="insertAt">タブの挿入位置 (タブ列へのドロップ。UI-34)。null なら末尾。</param>
    /// <param name="readOnly">「読み取り専用で開く」(ENG-14 の仕様 1)。データソースは読み取りのアクセス権だけで開き、編集を受け付けない。</param>
    /// <param name="restorePosition">前回の位置を戻す (ENG-16 の仕様 5。セッション・閉じたタブは自分の記録で戻すため false)。</param>
    public DocumentViewModel Open(string path, int? insertAt = null, bool readOnly = false, bool restorePosition = true)
    {
        string full = Path.GetFullPath(path);
        DocumentViewModel? existing = FindSameFile(full);
        if (existing is not null)
        {
            // まだ開いていない復元したタブは、選ぶとウィンドウが開いた文書に置き換える (UI-31 の仕様 6)。
            Selected = existing;
            return existing.IsPending && Selected is { } opened ? opened : existing;
        }

        FileByteSource source = FileByteSource.Open(full);

        // 読み取り専用で開く理由 (ENG-14 の仕様 1): 指定された、または書き込めない (読み取り専用のメディア、読み取り専用属性、書き込み権限が
        // ない、他のアプリが書き込みのために開いている)。書き込み禁止のハンドル (ENG-15) を開く前に確かめる。
        ReadOnlyReason reason;
        try
        {
            reason = readOnly ? ReadOnlyReason.OpenedReadOnly
                : FileWriteProbe.Probe(full, source.HasReadOnlyAttribute, TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance, TestHooks.OpenForWrite);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        var doc = new Document(source, _options);
        DocumentViewModel vm = Add(doc, full, Path.GetFileName(full), insertAt);
        doc.SetReadOnly(reason);
        AfterOpened(vm, restorePosition);
        return vm;
    }

    /// <summary>
    /// パスを持たない項目 (ZIP の中のファイルなど) を、一時ファイルにコピーしたものから無題のドキュメントとして開く
    /// (ENG-12 の仕様 2)。タブには元の名前を出し、保存は「名前を付けて保存」になる。一時ファイルは閉じるときに消す。
    /// </summary>
    public DocumentViewModel OpenTemporaryCopy(string tempPath, string displayName, int? insertAt = null)
    {
        var doc = new Document(FileByteSource.Open(tempPath), _options);
        DocumentViewModel vm = Add(doc, null, displayName, insertAt);
        vm.TemporaryFile = tempPath;
        return vm;
    }

    /// <summary>
    /// 保存 (ENG-20〜ENG-23)。方式の選択と事前の確認は Core の <see cref="SavePlanner"/> が行う。長時間処理として実行し、保存中は
    /// 編集を受け付けない。完了後は UI スレッドで保存したファイルを新しい元データにする。
    /// <paramref name="confirm"/> は確認の要る計画 (ジャーナルの上限超え・空き容量不足など。<see cref="SavePlan.Issue"/>) を UI で
    /// 確かめ、続ける計画 (<see cref="SavePlanner.UseSafeSave"/> など) を返す。null を返すとキャンセル。<paramref name="confirm"/> を
    /// 渡さない場合は、問題を例外 (<see cref="JournalLimitException"/>・<see cref="InsufficientSpaceException"/>・
    /// <see cref="FileSizeLimitException"/>) で知らせる (方式を勝手に切り替えない)。
    /// </summary>
    /// <returns>保存した (変更がなく何もしなかった場合を含む) か。キャンセルなら false。</returns>
    public async Task<bool> SaveAsync(DocumentViewModel vm, string path, Func<SavePlan, Task<SavePlan?>>? confirm = null)
    {
        Document doc = vm.Document;
        SavePlan? plan = SavePlanner.Plan(doc, path, new SaveSettings
        {
            JournalDirectory = JournalDirectory,
            Volumes = TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance,

            // 外部で変更されたファイルの上書き・削除されたファイルの作り直しは、全体を書く (ENG-19 の仕様 5・8)。
            AlwaysSafeSave = vm.OverwritesExternalChange || vm.SourceDeleted,
            Backup = SkipBackupOnce ? null : BackupSettings,
        });
        SkipBackupOnce = false;
        if (plan.Method == SaveMethod.NoChanges)
        {
            return true; // 変更がない: 書き込まない (ENG-20 の仕様 1)。
        }

        while (plan is not null && !plan.CanExecute)
        {
            if (confirm is not null)
            {
                plan = await confirm(plan);
                continue;
            }

            throw plan.Issue switch
            {
                SaveIssue.JournalTooLarge when plan.Journal!.Space is { } space => new InsufficientSpaceException(space.Drive, space.Required, space.Available),
                SaveIssue.JournalTooLarge => new JournalLimitException(plan.Journal!.Required, plan.Journal.Limit),
                SaveIssue.InsufficientSpace => new InsufficientSpaceException(plan.Space!.Drive, plan.Space.Required, plan.Space.Available),
                SaveIssue.FileTooLarge => new FileSizeLimitException(plan.SizeLimit!.Drive, plan.SizeLimit.FileSystem, plan.SizeLimit.MaxFileSize, plan.SizeLimit.Length),
                SaveIssue.HardLinks => new InvalidOperationException($"The file has {plan.LinkCount} hard links; confirm before saving."),
                _ => new UnauthorizedAccessException(),
            };
        }

        if (plan is null)
        {
            return false;
        }

        string name = Loc.Format("Operation_Save", Path.GetFileName(path));
        SaveResult result;

        // 読み取り専用属性のあるファイルへの書き込みを承認していた: 属性を外して保存する (ENG-22 の仕様 5、ENG-14 の仕様 3)。
        // 置き換え (ReplaceFile) は元のファイルの属性を引き継ぐため、書く前に外す。保存に失敗したら戻す。
        FileAttributes? restoreAttributes = null;
        if (doc.Source is FileByteSource { WriteApproved: true } approved
            && string.Equals(approved.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            && File.Exists(path) && File.GetAttributes(path) is var attributes && attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            restoreAttributes = attributes;
        }

        // 自分の保存による変化は外部変更として扱わない (ENG-19 の仕様 3)。
        if (vm.Watch is { } watch)
        {
            ExternalChanges?.Suspend(watch);
        }

        try
        {
            result = await Operations.RunAsync(
                name, OperationKind.WritesExternal, doc, plan.TotalBytes,
                op => Task.FromResult(SavePlanner.Execute(plan, op)),
                locked => doc.SetEditLock(locked));
        }
        catch
        {
            SavePlanner.Abort(plan);
            if (restoreAttributes is { } original)
            {
                try
                {
                    File.SetAttributes(path, original);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warning($"Read-only attribute not restored: {ex.Message}");
                }
            }

            if (vm.Watch is { } failed)
            {
                ExternalChanges?.Rebase(failed, failed.Baseline);
            }

            throw;
        }

        SavePlanner.Complete(plan, result);
        if (result.BackupTime is { } backupTime)
        {
            AppLog.Info($"Backup created in {backupTime.TotalMilliseconds:F1} ms");
        }

        string? previousPath = vm.FilePath;
        vm.SetSavedPath(plan.TargetPath!);
        vm.OnSaved();
        RebaseWatch(vm);
        if (_files is not null && !string.Equals(previousPath, vm.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            Recent.Record(vm.FilePath!, vm.DisplayName, _files.UtcNow());
        }

        return true;
    }

    /// <summary>「バックアップを作る」の設定 (ENG-26)。null なら作らない。</summary>
    public BackupSettings? BackupSettings
    {
        get => _app.Backup;
        set => _app.Backup = value;
    }

    /// <summary>次の保存 1 回だけバックアップを作らない (「バックアップなしで保存」。ENG-26 の「エラー」)。</summary>
    public bool SkipBackupOnce { get; set; }

    /// <summary>その場保存のジャーナルの置き場所 (ENG-23。復旧用フォルダ。PKG-13)。</summary>
    private string JournalDirectory => _journalDirectory;

    public void Close(DocumentViewModel vm)
    {
        // 取り除くと TabView の双方向の結び付けで Selected が null になるため、先に選択中かを調べておく。
        bool wasSelected = Selected == vm;

        // 親のタブを閉じると連動ビューのタブも閉じる (ENG-39 の仕様 1)。
        foreach (DocumentViewModel child in LinkedTabsOf(vm))
        {
            (child.Owner ?? this).Close(child);
        }

        BeforeClose(vm, Documents.IndexOf(vm));
        Documents.Remove(vm);
        Notifications.DismissOwnedBy(vm);
        Memory.Unregister(vm.Document);
        _ = MaterializeReferencesAsync(vm.Document);
        vm.Dispose();
        if (wasSelected || Selected == vm || Selected is null)
        {
            Selected = Documents.LastOrDefault();
        }
    }

    /// <summary>
    /// 閉じるドキュメントの範囲を他のタブ・アプリ内クリップボードが参照していれば、一時ファイルに書き出す (EDIT-24 の仕様 3・5)。
    /// 閉じる操作は待たない。書き出せなかった場合も参照はそのまま読める (参照元のデータの解放が遅れるだけ)。
    /// </summary>
    /// <summary>閉じた文書のデータの実体化 (EDIT-24 の仕様 3) が失敗・キャンセルされた。</summary>
    public event EventHandler<Exception>? MaterializeFailed;

    private async Task MaterializeReferencesAsync(Document doc)
    {
        long bytes = doc.PendingReferenceBytes;
        if (bytes == 0)
        {
            return;
        }

        try
        {
            await Operations.RunAsync(Loc.Format("Operation_Materialize", doc.Source.DisplayName), OperationKind.WritesExternal, null, bytes,
                op =>
                {
                    doc.MaterializeReferences(op);
                    return Task.CompletedTask;
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // キャンセル・一時領域の不足: アプリ内クリップボードを破棄して知らせる (EDIT-24 の仕様 5)。
            AppLog.Warning($"Materializing clipboard references failed: {ex.GetType().Name}");
            MaterializeFailed?.Invoke(this, ex);
        }
    }

    /// <summary>ドキュメントを作るときの設定。</summary>
    public DocumentOptions DocumentOptions => _options;

    /// <summary>復旧用データのフォルダ (ドキュメントの一時ファイルと同じ。ENG-27 の仕様 2)。</summary>
    public string RecoveryRoot => _options.TempDirectory;

    /// <summary>復旧用データから開く (ENG-27 の仕様 6)。元のファイルが変わっていた場合は読み取り専用にする。</summary>
    public DocumentViewModel AddRestored(RestoredDocument restored)
    {
        RecoveryRecord record = restored.Record;
        string name = record.Path is null ? record.DisplayName : Path.GetFileName(record.Path);
        var vm = new DocumentViewModel(restored.Document, record.Path, name) { Recovery = restored.Recovery, Notifications = Notifications };
        vm.Editor.ReadOnly = restored.SourceChanged;
        long length = restored.Document.Length;
        if (record.SelectionLength > 0 && record.SelectionStart + record.SelectionLength <= length)
        {
            vm.Editor.Select(record.SelectionStart, record.SelectionLength);
        }
        else
        {
            vm.Editor.GoTo(Math.Clamp(record.Cursor, 0, length));
        }

        DocumentViewModel added = AddViewModel(vm);
        StartWatching(added);
        return added;
    }

    /// <summary>
    /// 復旧用データの定期の書き出し (ENG-27 の仕様 1、3)。内容を UI スレッドで取り、書き込みはバックグラウンドで行う。
    /// 失敗したドキュメントがあれば <paramref name="onError"/> を呼ぶ。
    /// </summary>
    public async Task WriteRecoveryAsync(Action<Exception> onError)
    {
        var work = Documents.Select(vm => (vm, capture: vm.CaptureRecoveryIfChanged()))
            .Where(w => w.capture is not null)
            .ToList();
        foreach ((DocumentViewModel vm, RecoveryCapture? capture) in work)
        {
            try
            {
                await Task.Run(() => vm.Recovery!.Write(capture!));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                vm.ForgetRecorded();
                onError(ex);
            }
        }
    }

    /// <summary>
    /// 異常終了の直前に、全ドキュメントの復旧用データを書き出す (PKG-30 の仕様 1 の 1)。<paramref name="timeout"/> で打ち切る。
    /// </summary>
    public void WriteRecoveryNow(TimeSpan timeout)
    {
        var captures = new List<(DocumentViewModel Vm, RecoveryCapture Capture)>();
        foreach (DocumentViewModel vm in Documents.ToList())
        {
            try
            {
                if (vm.CaptureRecoveryIfChanged() is { } capture)
                {
                    captures.Add((vm, capture));
                }
            }
            catch (Exception)
            {
                // 異常終了の途中なので、書けるものだけ書く。
            }
        }

        Task all = Task.Run(() =>
        {
            foreach ((DocumentViewModel vm, RecoveryCapture capture) in captures)
            {
                try
                {
                    vm.Recovery!.Write(capture);
                }
                catch (Exception)
                {
                }
            }
        });
        all.Wait(timeout);
    }

    /// <param name="recovery">
    /// 復旧用データを作るか。範囲を指定して開いた (ENG-13)・デコードして開いた (ENG-38) ドキュメントは、元データをファイルとして開き直せないため作らない。
    /// </param>
    private DocumentViewModel Add(Document doc, string? path, string name, int? insertAt = null, bool recovery = true)
    {
        DocumentRecovery? recoveryData = null;
        try
        {
            recoveryData = recovery ? new DocumentRecovery(RecoveryRoot, doc.Id) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 復旧用データを作れなくても編集はできる。書き出しのときに通知する。
            AppLog.Warning($"Recovery folder unavailable: {ex.Message}");
        }

        return AddViewModel(new DocumentViewModel(doc, path, name) { Recovery = recoveryData, Notifications = Notifications }, insertAt);
    }

    /// <summary>
    /// 編集を始めたのに、他のアプリの書き込みを禁止できなかった (他のアプリが書き込み用に開いている。ENG-15 の仕様 2)。
    /// </summary>
    public event EventHandler<DocumentViewModel>? LockFailed;

    private DocumentViewModel AddViewModel(DocumentViewModel vm, int? insertAt = null)
    {
        Document doc = vm.Document;
        doc.LockStateChanged += (_, _) =>
        {
            if (doc.LockState == FileLockState.Failed)
            {
                MainViewModel owner = vm.Owner ?? this;
                owner.LockFailed?.Invoke(owner, vm);
            }
        };
        if (doc.LinkedParent is null)
        {
            // 連動ビューは親とデータを共有するため、メモリの使用量を二重に数えない。
            Memory.Register(doc);
        }

        vm.Owner = this;
        InsertDocument(vm, insertAt);
        Selected = vm;
        return vm;
    }
}
