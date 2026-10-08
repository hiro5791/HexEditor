using HexEditor.Core.Files;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;

namespace HexEditor.Core.Tests.Files;

/// <summary>ENG-19 外部変更の検知 (Core の部分: 種類の区別、示し方、通知のまとめ、自分の保存の除外)。</summary>
public sealed class ExternalChangeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-external").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeWatcher : IFileChangeWatcher
    {
        public HashSet<string> Watched { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Supported { get; set; } = true;

        public event Action<string>? Changed;

        public bool Watch(string path)
        {
            Watched.Add(path);
            return Supported;
        }

        public void Unwatch(string path) => Watched.Remove(path);

        public void Raise(string path) => Changed?.Invoke(path);

        public void Dispose()
        {
        }
    }

    private static readonly FileStamp Base = new(100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "0001-0000000000000001");

    [Fact]
    public void ClassifiesContentChangesReplacementsAndDeletions()
    {
        Assert.Equal(ExternalChangeKind.None, ExternalChangeRules.Classify(Base, Base, Base));
        Assert.Equal(ExternalChangeKind.ContentChanged, ExternalChangeRules.Classify(Base, Base with { Length = 101 }, Base with { Length = 101 }));
        Assert.Equal(ExternalChangeKind.ContentChanged,
            ExternalChangeRules.Classify(Base, Base with { LastWriteTimeUtc = Base.LastWriteTimeUtc.AddSeconds(1) }, Base));
        Assert.Equal(ExternalChangeKind.Replaced, ExternalChangeRules.Classify(Base, Base with { FileId = "0001-0000000000000002" }, Base));
        Assert.Equal(ExternalChangeKind.Deleted, ExternalChangeRules.Classify(Base, null, Base));
    }

    [Theory]
    [InlineData(ExternalChangeKind.ContentChanged, false, true, true, ExternalChangePrompt.AutoReload)]
    [InlineData(ExternalChangeKind.Replaced, false, true, false, ExternalChangePrompt.AutoReload)]
    [InlineData(ExternalChangeKind.ContentChanged, false, false, false, ExternalChangePrompt.Changed)]
    [InlineData(ExternalChangeKind.Replaced, true, true, true, ExternalChangePrompt.Changed)]
    [InlineData(ExternalChangeKind.Replaced, true, true, false, ExternalChangePrompt.Changed)]
    [InlineData(ExternalChangeKind.ContentChanged, true, true, false, ExternalChangePrompt.Mixed)]
    [InlineData(ExternalChangeKind.ContentChanged, true, true, true, ExternalChangePrompt.Changed)]
    [InlineData(ExternalChangeKind.Deleted, false, true, false, ExternalChangePrompt.Deleted)]
    [InlineData(ExternalChangeKind.Deleted, true, true, true, ExternalChangePrompt.Deleted)]
    public void DecidesHowToTellTheUser(ExternalChangeKind kind, bool modified, bool autoReload, bool writesDenied, ExternalChangePrompt expected) =>
        Assert.Equal(expected, ExternalChangeRules.Decide(kind, modified, autoReload, writesDenied));

    [Fact]
    public void ButtonsFollowTheSpecification()
    {
        Assert.Equal(
            ExternalChangeActions.Reload | ExternalChangeActions.Merge | ExternalChangeActions.Compare | ExternalChangeActions.Ignore,
            ExternalChangeRules.ActionsFor(ExternalChangePrompt.Changed, modified: true));
        Assert.Equal(ExternalChangeActions.Reload | ExternalChangeActions.SaveAs | ExternalChangeActions.Compare,
            ExternalChangeRules.ActionsFor(ExternalChangePrompt.Mixed, modified: true));
        Assert.Equal(ExternalChangeActions.SaveAs | ExternalChangeActions.Close, ExternalChangeRules.ActionsFor(ExternalChangePrompt.Deleted, modified: false));
    }

    [Fact]
    public void BurstOfNotificationsIsReportedOnce()
    {
        var watcher = new FakeWatcher();
        var time = new FakeTimerProvider();
        FileStamp current = Base;
        using var monitor = new ExternalChangeMonitor(watcher, time, _ => current);
        var reports = new List<ExternalChangeKind>();
        monitor.Detected += (_, kind) => reports.Add(kind);
        WatchedFile file = monitor.Track(this, Path.Combine(_dir, "a.bin"), Base, () => current);
        Assert.False(file.IsPolled);

        // 10 ms ごとに 100 回の書き込み (仕様 9 と TC-ENG-19-05)。
        for (int i = 0; i < 100; i++)
        {
            current = current with { LastWriteTimeUtc = current.LastWriteTimeUtc.AddMilliseconds(10) };
            watcher.Raise(file.Path);
            time.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Empty(reports);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([ExternalChangeKind.ContentChanged], reports);

        // 利用者が選ぶまでは、続く変更も知らせない。
        watcher.Raise(file.Path);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(reports);
    }

    [Fact]
    public void QuietBurstIsReportedFiveHundredMillisecondsAfterTheLastNotification()
    {
        var watcher = new FakeWatcher();
        var time = new FakeTimerProvider();
        FileStamp current = Base with { Length = 200 };
        using var monitor = new ExternalChangeMonitor(watcher, time, _ => current);
        int reports = 0;
        monitor.Detected += (_, _) => reports++;
        WatchedFile file = monitor.Track(this, Path.Combine(_dir, "a.bin"), Base, () => current);

        watcher.Raise(file.Path);
        time.Advance(TimeSpan.FromMilliseconds(400));
        watcher.Raise(file.Path);
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.Equal(0, reports);
        time.Advance(TimeSpan.FromMilliseconds(150));
        Assert.Equal(1, reports);
    }

    [Fact]
    public void OwnSavesAreNotReported()
    {
        var watcher = new FakeWatcher();
        var time = new FakeTimerProvider();
        FileStamp current = Base;
        using var monitor = new ExternalChangeMonitor(watcher, time, _ => current);
        int reports = 0;
        monitor.Detected += (_, _) => reports++;
        WatchedFile file = monitor.Track(this, Path.Combine(_dir, "a.bin"), Base, () => current);

        // 保存の間は検知を止め、保存の後に基準の値を新しいファイルにする (仕様 3)。
        monitor.Suspend(file);
        current = Base with { FileId = "0001-0000000000000009", Length = 101 };
        watcher.Raise(file.Path);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalChangeKind.None, monitor.CheckNow(file));
        monitor.Rebase(file, current);
        watcher.Raise(file.Path);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalChangeKind.None, monitor.CheckNow(file));
        Assert.Equal(0, reports);

        // 保存の後の外部の変更は知らせる。
        current = current with { Length = 102 };
        Assert.Equal(ExternalChangeKind.ContentChanged, monitor.CheckNow(file));
        Assert.Equal(1, reports);
    }

    [Fact]
    public void UnwatchableLocationsArePolled()
    {
        var watcher = new FakeWatcher { Supported = false };
        using var monitor = new ExternalChangeMonitor(watcher, new FakeTimerProvider(), _ => null);
        WatchedFile file = monitor.Track(this, @"\\server\share\a.bin", Base, () => Base);
        Assert.True(file.IsPolled);
        Assert.Equal(ExternalChangeKind.Deleted, monitor.CheckNow(file));
        monitor.Untrack(file);
        Assert.Empty(watcher.Watched);
    }

    [Fact]
    public void ReloadReportsAnAlreadyReportedChangeAgain()
    {
        // ENG-18 の仕様 1: 知らせた変更の InfoBar を閉じた後の再読み込み (Ctrl+R) で、もう一度知らせる。
        var watcher = new FakeWatcher();
        FileStamp current = Base with { Length = 200 };
        using var monitor = new ExternalChangeMonitor(watcher, new FakeTimerProvider(), _ => current);
        int reports = 0;
        monitor.Detected += (_, _) => reports++;
        WatchedFile file = monitor.Track(this, Path.Combine(_dir, "a.bin"), Base, () => current);

        Assert.Equal(ExternalChangeKind.ContentChanged, monitor.CheckNow(file));
        Assert.Equal(ExternalChangeKind.ContentChanged, monitor.CheckNow(file));
        Assert.Equal(1, reports);

        Assert.Equal(ExternalChangeKind.ContentChanged, monitor.CheckNow(file, reportAgain: true));
        Assert.Equal(2, reports);
    }

    [Fact]
    public void ForgottenChangeIsReportedOnTheNextCheck()
    {
        // 処理中で扱えなかった変更は、忘れさせると次の確認で知らせる (ENG-19 の仕様 11)。
        var watcher = new FakeWatcher();
        FileStamp current = Base with { Length = 200 };
        using var monitor = new ExternalChangeMonitor(watcher, new FakeTimerProvider(), _ => current);
        int reports = 0;
        monitor.Detected += (_, _) => reports++;
        WatchedFile file = monitor.Track(this, Path.Combine(_dir, "a.bin"), Base, () => current);
        monitor.CheckNow(file);
        monitor.Forget(file);
        Assert.Equal(ExternalChangeKind.None, file.Reported);
        Assert.Equal(Base, file.Baseline);
        monitor.CheckNow(file);
        Assert.Equal(2, reports);
    }

    [Fact]
    public void NetworkDrivesArePolled()
    {
        // ENG-19 の仕様 1: 割り当てたネットワークドライブ (UNC の書き方でないもの) も、変更通知を使わず定期的に確認する。
        string path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, [1]);
        using var network = new FileSystemChangeWatcher { IsNetworkPath = _ => true };
        Assert.False(network.Watch(path));
        using var local = new FileSystemChangeWatcher { IsNetworkPath = _ => false };
        Assert.True(local.Watch(path));
        using var unc = new FileSystemChangeWatcher();
        Assert.False(unc.Watch(@"\\server\share\a.bin"));
    }

    [Fact]
    public async Task RealWatcherSeesWritesReplacementsAndDeletions()
    {
        string path = Path.Combine(_dir, "a.bin");
        await File.WriteAllBytesAsync(path, new byte[1024]);
        using FileByteSource source = FileByteSource.Open(path);
        using var watcher = new FileSystemChangeWatcher();
        using var monitor = new ExternalChangeMonitor(watcher);
        var detected = new System.Collections.Concurrent.ConcurrentQueue<ExternalChangeKind>();
        monitor.Detected += (_, kind) => detected.Enqueue(kind);
        WatchedFile file = monitor.Track(this, path, source.Stamp, source.ReadCurrentStamp);
        Assert.False(file.IsPolled);

        // 内容の変更。
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Position = 0x100;
            stream.WriteByte(0xCC);
            stream.SetLength(2048);
        }

        Assert.Equal(ExternalChangeKind.ContentChanged, await WaitAsync(detected));

        // 置き換え (他のエディタの安全な保存)。
        monitor.Rebase(file, FileStamp.FromPath(path)!);
        string temp = Path.Combine(_dir, "a.tmp");
        await File.WriteAllBytesAsync(temp, new byte[10]);
        File.Replace(temp, path, null);
        Assert.Equal(ExternalChangeKind.Replaced, await WaitAsync(detected));

        // 削除。
        monitor.Rebase(file, FileStamp.FromPath(path)!);
        File.Delete(path);
        Assert.Equal(ExternalChangeKind.Deleted, await WaitAsync(detected));
    }

    private static async Task<ExternalChangeKind> WaitAsync(System.Collections.Concurrent.ConcurrentQueue<ExternalChangeKind> queue)
    {
        for (int i = 0; i < 100; i++)
        {
            if (queue.TryDequeue(out ExternalChangeKind kind))
            {
                return kind;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("外部変更が検知されません。");
    }
}
