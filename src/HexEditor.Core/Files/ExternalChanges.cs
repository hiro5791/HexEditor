using HexEditor.Core.Sources;

namespace HexEditor.Core.Files;

/// <summary>外部変更の種類 (ENG-19 の仕様 2)。</summary>
public enum ExternalChangeKind
{
    None,

    /// <summary>内容の変更: 同じファイル (ファイル ID が同じ) のサイズ・更新日時が変わった。</summary>
    ContentChanged,

    /// <summary>置き換え: 同じパスに別のファイル (ファイル ID が違う) がある。開いているハンドルは置き換え前のファイルを指す。</summary>
    Replaced,

    /// <summary>削除・移動: パスにファイルがない。</summary>
    Deleted,
}

/// <summary>外部変更を知らせる InfoBar の種類とボタン (ENG-19 の仕様 4〜8)。</summary>
public enum ExternalChangePrompt
{
    /// <summary>未編集で、自動で再読み込みする設定: 再読み込みして「外部で変更されたため再読み込みしました」(5 秒で消える)。</summary>
    AutoReload,

    /// <summary>「data.bin は外部で変更されました」: 再読み込み・マージ・比較・無視 (仕様 5)。未編集で自動再読み込みがオフのときもこれ。</summary>
    Changed,

    /// <summary>「保存していない変更と外部の変更が混ざっています」: 再読み込み・名前を付けて保存・比較 (仕様 6。マージは選べない)。</summary>
    Mixed,

    /// <summary>「data.bin は削除または移動されました」: 名前を付けて保存・閉じる (仕様 8)。編集は続けられる。</summary>
    Deleted,
}

/// <summary>InfoBar のボタン。</summary>
[Flags]
public enum ExternalChangeActions
{
    None = 0,
    Reload = 1,
    Merge = 2,
    Compare = 4,
    Ignore = 8,
    SaveAs = 16,
    Close = 32,
}

/// <summary>外部変更の判定と、利用者への示し方の決定 (ENG-19)。</summary>
public static class ExternalChangeRules
{
    /// <summary>設定「未編集のファイルは自動で再読み込みする」(仕様 4。既定オン)。</summary>
    public const string AutoReloadKey = "files.autoReloadUnmodified";

    /// <summary>同じファイルの変更通知をまとめる時間 (仕様 9)。</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>通知が続いても、最初の通知からこの時間がたったら 1 回扱う (書き込みが止まらない場合)。</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(3);

    /// <summary>変更通知を使えない場所 (一部のネットワークドライブ) での確認の間隔 (仕様 1)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 変更の種類を区別する (仕様 2)。<paramref name="baseline"/> は開いた (保存した・再読み込みした) 時点の値、
    /// <paramref name="atPath"/> はパスで開き直して読んだ値 (なければ null)、<paramref name="atHandle"/> は開いているハンドルで読んだ値。
    /// </summary>
    public static ExternalChangeKind Classify(FileStamp baseline, FileStamp? atPath, FileStamp? atHandle)
    {
        if (atPath is null)
        {
            return ExternalChangeKind.Deleted;
        }

        if (baseline.FileId.Length > 0 && atPath.FileId.Length > 0 && atPath.FileId != baseline.FileId)
        {
            return ExternalChangeKind.Replaced;
        }

        FileStamp current = atHandle ?? atPath;
        return current.Length != baseline.Length || current.LastWriteTimeUtc != baseline.LastWriteTimeUtc
            || atPath.Length != baseline.Length || atPath.LastWriteTimeUtc != baseline.LastWriteTimeUtc
            ? ExternalChangeKind.ContentChanged
            : ExternalChangeKind.None;
    }

    /// <summary>
    /// 示し方を決める (仕様 4〜8)。<paramref name="writesDenied"/> は書き込み禁止のハンドル (ENG-15) を持てていたか。
    /// 持てていなかった状態で内容の変更があると、変更していない部分の表示がすでに外部の内容に変わっている (仕様 6)。
    /// </summary>
    public static ExternalChangePrompt Decide(ExternalChangeKind kind, bool modified, bool autoReloadUnmodified, bool writesDenied) =>
        kind switch
        {
            ExternalChangeKind.Deleted => ExternalChangePrompt.Deleted,
            _ when !modified && autoReloadUnmodified => ExternalChangePrompt.AutoReload,
            ExternalChangeKind.ContentChanged when modified && !writesDenied => ExternalChangePrompt.Mixed,
            _ => ExternalChangePrompt.Changed,
        };

    /// <summary>InfoBar のボタン。</summary>
    public static ExternalChangeActions ActionsFor(ExternalChangePrompt prompt, bool modified) => prompt switch
    {
        ExternalChangePrompt.AutoReload => ExternalChangeActions.None,
        ExternalChangePrompt.Mixed => ExternalChangeActions.Reload | ExternalChangeActions.SaveAs | ExternalChangeActions.Compare,
        ExternalChangePrompt.Deleted => ExternalChangeActions.SaveAs | ExternalChangeActions.Close,

        // 未編集なら自分の変更がないため、マージは選べない (再読み込みと同じになる)。
        _ => modified
            ? ExternalChangeActions.Reload | ExternalChangeActions.Merge | ExternalChangeActions.Compare | ExternalChangeActions.Ignore
            : ExternalChangeActions.Reload | ExternalChangeActions.Compare | ExternalChangeActions.Ignore,
    };
}

/// <summary>ファイルの変更通知 (ENG-19 の仕様 1)。実物は <see cref="FileSystemChangeWatcher"/>、テストでは偽物を使う。</summary>
public interface IFileChangeWatcher : IDisposable
{
    /// <summary>監視しているパス (またはそのフォルダの同じ名前) に変化があった。引数は監視を始めたときのパス。どのスレッドからも呼ばれる。</summary>
    event Action<string>? Changed;

    /// <summary>監視を始める。変更通知を使えない場所なら false (呼び出し側は定期的に確認する)。</summary>
    bool Watch(string path);

    void Unwatch(string path);
}

/// <summary>
/// フォルダの変更通知 (<c>ReadDirectoryChangesW</c>。<see cref="FileSystemWatcher"/>) で、ファイルの変更・置き換え・削除・名前の変更を
/// 知る。同じフォルダの複数のファイルは 1 つの監視をまとめて使う。監視を始められない場所 (ネットワークドライブの一部、権限) は false。
/// </summary>
public sealed class FileSystemChangeWatcher : IFileChangeWatcher
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (FileSystemWatcher Watcher, HashSet<string> Paths)> _folders = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Changed;

    public bool Watch(string path)
    {
        string full = Path.GetFullPath(path);
        string? folder = Path.GetDirectoryName(full);
        if (folder is null || RecentFileList.IsNetworkPathSyntax(full))
        {
            // UNC のパスでは通知が届かないことがあるため、定期の確認に任せる。
            return false;
        }

        lock (_lock)
        {
            if (_folders.TryGetValue(folder, out var entry))
            {
                entry.Paths.Add(full);
                return true;
            }

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += (_, e) => Raise(folder, e.FullPath);
                watcher.Created += (_, e) => Raise(folder, e.FullPath);
                watcher.Deleted += (_, e) => Raise(folder, e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Raise(folder, e.OldFullPath);
                    Raise(folder, e.FullPath);
                };

                // 通知のバッファがあふれたら、そのフォルダの全部のファイルを確かめる。
                watcher.Error += (_, _) => RaiseAll(folder);
                watcher.EnableRaisingEvents = true;
                _folders[folder] = (watcher, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    public void Unwatch(string path)
    {
        string full = Path.GetFullPath(path);
        string? folder = Path.GetDirectoryName(full);
        if (folder is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_folders.TryGetValue(folder, out var entry) && entry.Paths.Remove(full) && entry.Paths.Count == 0)
            {
                entry.Watcher.Dispose();
                _folders.Remove(folder);
            }
        }
    }

    private void Raise(string folder, string changedPath)
    {
        string? match;
        lock (_lock)
        {
            match = _folders.TryGetValue(folder, out var entry) && entry.Paths.TryGetValue(changedPath, out string? p) ? p : null;
        }

        if (match is not null)
        {
            Changed?.Invoke(match);
        }
    }

    private void RaiseAll(string folder)
    {
        List<string> paths;
        lock (_lock)
        {
            paths = _folders.TryGetValue(folder, out var entry) ? [.. entry.Paths] : [];
        }

        foreach (string path in paths)
        {
            Changed?.Invoke(path);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach ((FileSystemWatcher watcher, _) in _folders.Values)
            {
                watcher.Dispose();
            }

            _folders.Clear();
        }
    }
}

/// <summary>監視している 1 つのファイル。</summary>
public sealed class WatchedFile
{
    internal WatchedFile(object owner, string path, FileStamp baseline, Func<FileStamp?> handleStamp, bool polled)
    {
        Owner = owner;
        Path = path;
        Baseline = baseline;
        HandleStamp = handleStamp;
        IsPolled = polled;
    }

    /// <summary>持ち主 (ドキュメントの ViewModel など)。</summary>
    public object Owner { get; }

    public string Path { get; }

    /// <summary>自分が開いた・保存した・再読み込みした時点の値。これと違えば外部変更。</summary>
    public FileStamp Baseline { get; internal set; }

    /// <summary>開いているハンドルで読んだ今の値。</summary>
    internal Func<FileStamp?> HandleStamp { get; set; }

    /// <summary>変更通知が使えず、定期的に確認する (アクティブなドキュメントだけ。ENG-19 の仕様 1)。</summary>
    public bool IsPolled { get; }

    /// <summary>保存中など、検知を止めている。</summary>
    public bool IsSuspended => SuspendCount > 0;

    internal int SuspendCount { get; set; }

    /// <summary>知らせた変更 (利用者が選ぶまで同じ変更を何度も知らせない)。</summary>
    public ExternalChangeKind Reported { get; internal set; }

    internal ITimer? Timer { get; set; }

    internal long FirstEventTimestamp { get; set; } = -1;
}

/// <summary>
/// 外部変更の監視 (ENG-19)。変更通知を 500 ms まとめてから確かめ (仕様 9)、自分の保存による変化は除き (仕様 3。保存中は
/// <see cref="Suspend"/>、保存の後は <see cref="Rebase"/>)、利用者が選ぶまで同じ変更を何度も知らせない。ウィンドウのアクティブ化と
/// タブの切り替えでは <see cref="CheckNow"/> ですぐに確かめる。
/// </summary>
public sealed class ExternalChangeMonitor : IDisposable
{
    private readonly object _lock = new();
    private readonly IFileChangeWatcher _watcher;
    private readonly TimeProvider _time;
    private readonly Func<string, FileStamp?> _readPath;
    private readonly List<WatchedFile> _files = [];

    /// <param name="readPath">パスで開き直して値を読む (テストで差し替える)。既定は <see cref="FileStamp.FromPath"/>。</param>
    public ExternalChangeMonitor(IFileChangeWatcher watcher, TimeProvider? time = null, Func<string, FileStamp?>? readPath = null)
    {
        _watcher = watcher;
        _time = time ?? TimeProvider.System;
        _readPath = readPath ?? FileStamp.FromPath;
        _watcher.Changed += OnChanged;
    }

    /// <summary>外部変更を検知した。どのスレッドからも呼ばれる (UI は自分のスレッドに移して扱う)。</summary>
    public event Action<WatchedFile, ExternalChangeKind>? Detected;

    /// <summary>監視を始める。</summary>
    public WatchedFile Track(object owner, string path, FileStamp baseline, Func<FileStamp?> handleStamp)
    {
        string full = System.IO.Path.GetFullPath(path);
        bool watched = _watcher.Watch(full);
        var file = new WatchedFile(owner, full, baseline, handleStamp, polled: !watched);
        lock (_lock)
        {
            _files.Add(file);
        }

        return file;
    }

    /// <summary>監視をやめる (閉じたとき)。</summary>
    public void Untrack(WatchedFile file)
    {
        lock (_lock)
        {
            if (!_files.Remove(file))
            {
                return;
            }

            file.Timer?.Dispose();
            file.Timer = null;
        }

        _watcher.Unwatch(file.Path);
    }

    public IReadOnlyList<WatchedFile> Files
    {
        get
        {
            lock (_lock)
            {
                return [.. _files];
            }
        }
    }

    /// <summary>検知を止める (自分の保存の間。仕様 3)。<see cref="Rebase"/> で再開する。</summary>
    public void Suspend(WatchedFile file)
    {
        lock (_lock)
        {
            file.SuspendCount++;
        }
    }

    /// <summary>
    /// 自分の保存・再読み込みの後: 基準の値を新しいファイルにし、知らせた変更を忘れて検知を再開する。<paramref name="handleStamp"/> は
    /// 新しく開いたハンドルの値の読み方 (変わらなければ null)。
    /// </summary>
    public void Rebase(WatchedFile file, FileStamp baseline, Func<FileStamp?>? handleStamp = null)
    {
        lock (_lock)
        {
            file.Baseline = baseline;
            if (handleStamp is not null)
            {
                file.HandleStamp = handleStamp;
            }

            file.Reported = ExternalChangeKind.None;
            file.SuspendCount = Math.Max(0, file.SuspendCount - 1);
            file.Timer?.Dispose();
            file.Timer = null;
            file.FirstEventTimestamp = -1;
        }
    }

    /// <summary>
    /// 「無視」: 今の状態を受け入れ、次の変更から知らせる (仕様 5)。基準の値は変えない (保存時に外部変更を上書きすることを確かめるため、
    /// 呼び出し側が覚えておく)。
    /// </summary>
    public void Acknowledge(WatchedFile file)
    {
        FileStamp? now = _readPath(file.Path);
        lock (_lock)
        {
            file.Reported = ExternalChangeKind.None;
            if (now is not null)
            {
                file.Baseline = now;
            }
        }
    }

    /// <summary>すぐに確かめる (ウィンドウのアクティブ化・タブの切り替え・定期の確認。仕様 1)。変化があれば <see cref="Detected"/>。</summary>
    public ExternalChangeKind CheckNow(WatchedFile file)
    {
        lock (_lock)
        {
            if (!_files.Contains(file) || file.IsSuspended)
            {
                return ExternalChangeKind.None;
            }

            file.Timer?.Dispose();
            file.Timer = null;
            file.FirstEventTimestamp = -1;
        }

        FileStamp? atPath = _readPath(file.Path);
        FileStamp? atHandle = file.HandleStamp();
        ExternalChangeKind kind = ExternalChangeRules.Classify(file.Baseline, atPath, atHandle);
        bool report;
        lock (_lock)
        {
            // 保存が始まった・閉じた・もう知らせた変更は知らせない (仕様 3・9)。
            report = kind != ExternalChangeKind.None && !file.IsSuspended && _files.Contains(file) && file.Reported == ExternalChangeKind.None;
            if (report)
            {
                file.Reported = kind;
            }
        }

        if (report)
        {
            Detected?.Invoke(file, kind);
        }

        return kind;
    }

    /// <summary>変更通知: 最後の通知から 500 ms (続く場合は最初の通知から最大 3 秒) たってから 1 回だけ確かめる (仕様 9)。</summary>
    private void OnChanged(string path)
    {
        lock (_lock)
        {
            foreach (WatchedFile file in _files.Where(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                if (file.IsSuspended || file.Reported != ExternalChangeKind.None)
                {
                    continue;
                }

                long now = _time.GetTimestamp();
                if (file.FirstEventTimestamp < 0)
                {
                    file.FirstEventTimestamp = now;
                }

                TimeSpan sinceFirst = _time.GetElapsedTime(file.FirstEventTimestamp, now);
                TimeSpan due = sinceFirst + ExternalChangeRules.Debounce > ExternalChangeRules.MaxDelay
                    ? ExternalChangeRules.MaxDelay - sinceFirst
                    : ExternalChangeRules.Debounce;
                if (due < TimeSpan.Zero)
                {
                    due = TimeSpan.Zero;
                }

                WatchedFile target = file;
                if (file.Timer is null)
                {
                    file.Timer = _time.CreateTimer(_ => CheckNow(target), null, due, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    file.Timer.Change(due, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    public void Dispose()
    {
        _watcher.Changed -= OnChanged;
        lock (_lock)
        {
            foreach (WatchedFile file in _files)
            {
                file.Timer?.Dispose();
            }

            _files.Clear();
        }
    }
}
