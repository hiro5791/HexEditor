using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>ファイルの記録の置き場所と設定 (App が作る)。</summary>
public sealed record FilesContext
{
    public required RecentFileStore RecentStore { get; init; }

    public required DocumentDataStore Documents { get; init; }

    public required SessionStore Session { get; init; }

    public required AppStateStore State { get; init; }

    public required IFileChangeWatcher Watcher { get; init; }

    /// <summary>前回の位置を復元するか (<c>recent.restorePosition</c>)。</summary>
    public Func<bool> RestorePosition { get; init; } = () => true;

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// 最近使ったファイル (ENG-16、UI-32)、閉じたタブ (UI-12)、セッション (UI-31)、読み取り専用で開く (ENG-14)、外部変更の監視 (ENG-19)。
/// </summary>
public sealed partial class MainViewModel
{
    private FilesContext? _files;

    /// <summary>最近使ったファイル。ジャンプリスト (UI-35) もこの一覧と <see cref="RecentFileList.Changed"/> を使う。</summary>
    public RecentFileList Recent { get; } = new();

    /// <summary>閉じたタブ (アプリ全体。UI-12)。</summary>
    public ClosedTabHistory ClosedTabs { get; } = new();

    public FilesContext? Files => _files;

    /// <summary>外部変更の監視 (ENG-19)。</summary>
    public ExternalChangeMonitor? ExternalChanges { get; private set; }

    /// <summary>外部変更を検知した。監視のスレッドから呼ばれる。</summary>
    public event Action<DocumentViewModel, ExternalChangeKind>? ExternalChangeDetected;

    /// <summary>記録の置き場所を設定し、最近使ったファイルを読む (起動時に 1 回)。</summary>
    public void InitializeFiles(FilesContext files, int recentMaxItems)
    {
        _files = files;
        Recent.MaxItems = recentMaxItems;
        files.RecentStore.Load(Recent);
        Recent.Changed += (_, _) =>
        {
            if (files.RecentStore.Save(Recent) is { } error)
            {
                // 設定フォルダに書けない場合は一覧を更新せず、ログに記録する (利用者には出さない。ENG-16 の「エラー」)。
                AppLog.Warning($"recent.json was not written: {error}");
            }
        };
        ExternalChanges = new ExternalChangeMonitor(files.Watcher);
        ExternalChanges.Detected += (file, kind) =>
        {
            if (file.Owner is DocumentViewModel vm)
            {
                ExternalChangeDetected?.Invoke(vm, kind);
            }
        };
    }

    /// <summary>開いた直後: 最近使ったファイルに記録し、前回の位置を戻し、外部変更の監視を始める。</summary>
    private void AfterOpened(DocumentViewModel vm, bool restorePosition)
    {
        if (vm.FilePath is not { } path || _files is null)
        {
            return;
        }

        Recent.Record(path, vm.DisplayName, _files.UtcNow());
        if (restorePosition && _files.RestorePosition() && _files.Documents.GetPosition(path) is { } position)
        {
            DocumentPosition fitted = position.ClampTo(vm.Document.Length);
            vm.RestorePosition(fitted.Cursor, fitted.SelectionStart, fitted.SelectionLength, fitted.TopRow);
        }

        StartWatching(vm);
    }

    /// <summary>外部変更の監視を始める (ファイルのドキュメントだけ)。</summary>
    public void StartWatching(DocumentViewModel vm)
    {
        if (ExternalChanges is null || vm.Watch is not null || vm.FilePath is not { } path || vm.Document.Source is not FileByteSource file)
        {
            return;
        }

        vm.Watch = ExternalChanges.Track(vm, path, file.Stamp, file.ReadCurrentStamp);
    }

    /// <summary>監視をやめる (閉じたとき、別のパスに保存したとき)。</summary>
    public void StopWatching(DocumentViewModel vm)
    {
        if (vm.Watch is { } watch)
        {
            ExternalChanges?.Untrack(watch);
            vm.Watch = null;
        }
    }

    /// <summary>
    /// 自分の保存・再読み込みの後: 監視の基準を今の元データにする (自分の保存による変化を外部変更として扱わない。ENG-19 の仕様 3)。
    /// </summary>
    public void RebaseWatch(DocumentViewModel vm)
    {
        if (vm.Watch is { } watch && vm.Document.Source is FileByteSource file)
        {
            if (!string.Equals(watch.Path, file.Path, StringComparison.OrdinalIgnoreCase))
            {
                StopWatching(vm);
                StartWatching(vm);
                return;
            }

            ExternalChanges?.Rebase(watch, file.ReadCurrentStamp() ?? file.Stamp, file.ReadCurrentStamp);
        }
        else
        {
            StartWatching(vm);
        }

        vm.ExternalChange = ExternalChangeKind.None;
        vm.OverwritesExternalChange = false;
        vm.SourceDeleted = false;
    }

    /// <summary>閉じる直前: 前回の位置・最近使ったファイル・閉じたタブを記録し、監視をやめる。</summary>
    private void BeforeClose(DocumentViewModel vm, int index)
    {
        StopWatching(vm);
        if (_files is null)
        {
            return;
        }

        if (vm.FilePath is { } path && !vm.IsMissing)
        {
            FileStamp? stamp = vm.OpenedStamp;
            string? error = _files.Documents.SetPosition(new DocumentPosition
            {
                Path = path,
                Cursor = vm.Editor.Cursor,
                SelectionStart = vm.Editor.SelectionStart,
                SelectionLength = vm.Editor.SelectionLength,
                TopRow = vm.Editor.TopRow,
                Length = vm.Document.Length,
                LastWriteTimeUtc = stamp?.LastWriteTimeUtc ?? default,
            });
            if (error is not null)
            {
                AppLog.Warning($"The position was not saved: {error}");
            }

            Recent.Record(path, vm.DisplayName, _files.UtcNow());
        }

        ClosedTabs.Push(new ClosedTab { Tab = vm.ToSessionTab(), Index = index, ClosedAtUtc = _files.UtcNow() });
    }

    /// <summary>
    /// 閉じたタブを開き直す (UI-12 の仕様 2・4)。削除・移動されたファイルの記録は飛ばし、<paramref name="missing"/> に表示名を渡す。
    /// 記録が空なら null。閉じたときに破棄した未保存の変更は戻らない (仕様 3)。
    /// </summary>
    public DocumentViewModel? ReopenClosedTab(Action<string> missing, Func<string, int?, bool, DocumentViewModel?> open)
    {
        while (ClosedTabs.Pop(c => c.Tab.Path is { } p && File.Exists(p), c => missing(c.Tab.DisplayName)) is { } closed)
        {
            SessionTab tab = closed.Tab;
            DocumentViewModel? vm = open(tab.Path!, Math.Min(closed.Index, Documents.Count), tab.ReadOnly);
            if (vm is null)
            {
                continue;
            }

            vm.RestorePosition(tab.Cursor, tab.SelectionStart, tab.SelectionLength, tab.TopRow);
            return vm;
        }

        return null;
    }

    // ---- セッション (UI-31) ----

    /// <summary>今のウィンドウのタブの記録。無題・プロセスメモリのタブは含めない (仕様 4・5)。</summary>
    public SessionWindow CaptureWindow(SessionWindow bounds)
    {
        var tabs = new List<SessionTab>();
        int active = -1;
        foreach (DocumentViewModel vm in Documents)
        {
            SessionTab tab = vm.ToSessionTab();
            if (!SessionRules.IsRestorable(tab))
            {
                continue;
            }

            if (vm == Selected)
            {
                active = tabs.Count;
            }

            tabs.Add(tab);
        }

        return bounds with { Tabs = tabs, ActiveTab = active >= 0 ? active : tabs.Count - 1 };
    }

    /// <summary>セッション全体 (ウィンドウ 1 つ。複数ウィンドウは UI-14 で加える)。</summary>
    public SessionState CaptureSession(SessionWindow bounds) => new()
    {
        Windows = [CaptureWindow(bounds)],
        ClosedTabs = [.. ClosedTabs.Items],
        LastActiveWindow = 0,
    };

    /// <summary>
    /// セッションのタブを開く (UI-31 の仕様 5〜7)。見つからないファイルはタブを残して「ファイルが見つかりません」を表示する。前回の終了後に
    /// サイズや更新日時が変わっていたら、カーソル位置を長さの範囲に収め、<paramref name="changed"/> で知らせる。
    /// </summary>
    public void RestoreTabs(SessionWindow window, Func<string, bool, DocumentViewModel?> open, Action<DocumentViewModel> changed)
    {
        DocumentViewModel? active = null;
        for (int i = 0; i < window.Tabs.Count; i++)
        {
            SessionTab tab = window.Tabs[i];
            if (!SessionRules.IsRestorable(tab))
            {
                continue;
            }

            DocumentViewModel? vm = File.Exists(tab.Path) ? open(tab.Path!, tab.ReadOnly) : AddMissing(tab);
            if (vm is null)
            {
                continue;
            }

            if (!vm.IsMissing)
            {
                vm.RestorePosition(tab.Cursor, tab.SelectionStart, tab.SelectionLength, tab.TopRow);
                vm.HexZoom = tab.Zoom;
                FileStamp? stamp = vm.OpenedStamp;
                if (stamp is not null && tab.LastWriteTimeUtc != default
                    && (stamp.Length != tab.Length || stamp.LastWriteTimeUtc != tab.LastWriteTimeUtc))
                {
                    changed(vm);
                }
            }

            if (i == window.ActiveTab)
            {
                active = vm;
            }
        }

        if (active is not null)
        {
            Selected = active;
        }
    }

    /// <summary>見つからないファイルのタブ (UI-31 の「エラー」)。内容は空で、編集できない。</summary>
    public DocumentViewModel AddMissing(SessionTab tab, int? insertAt = null)
    {
        var doc = new Document(MemoryByteSource.CreateEmpty(tab.DisplayName), _options);
        var vm = new DocumentViewModel(doc, null, tab.DisplayName) { MissingPath = tab.Path, MissingRecord = tab, Notifications = Notifications };
        vm.Editor.ReadOnly = true;
        return AddViewModel(vm, insertAt);
    }

    /// <summary>開いているタブのうち、パスが同じもの。</summary>
    public DocumentViewModel? FindOpen(string path) =>
        Documents.FirstOrDefault(d => string.Equals(d.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
}
