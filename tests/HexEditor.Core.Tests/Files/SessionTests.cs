using HexEditor.Core.Files;

namespace HexEditor.Core.Tests.Files;

/// <summary>UI-12 閉じたタブの記録、UI-30 起動時の動作、UI-31 セッション、UI-38 の state.json (Core の部分)。</summary>
public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-session").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SessionTab Tab(string name, long cursor = 0, SessionTabKind kind = SessionTabKind.File) =>
        new() { Kind = kind, Path = kind == SessionTabKind.Untitled ? null : $@"C:\data\{name}", DisplayName = name, Cursor = cursor };

    [Fact]
    public void ClosedTabsReopenInReverseOrderAndSkipMissingFiles()
    {
        var history = new ClosedTabHistory();
        history.Push(new ClosedTab { Tab = Tab("file02.bin", 0x100), Index = 1 });
        history.Push(new ClosedTab { Tab = Tab("file04.bin", 0x300), Index = 2 });
        history.Push(new ClosedTab { Tab = Tab("file03.bin", 0x200), Index = 1 });

        // プロセスメモリ・無題は記録しない (UI-12 の仕様 6、UI-31 の仕様 4)。
        Assert.False(history.Push(new ClosedTab { Tab = Tab("notepad.exe", kind: SessionTabKind.Process) }));
        Assert.False(history.Push(new ClosedTab { Tab = Tab("Untitled 1", kind: SessionTabKind.Untitled) }));

        Assert.Equal("file03.bin", history.Pop(_ => true)!.Tab.DisplayName);

        // 見つからない記録は飛ばして次を開く (UI-12 の仕様 4)。
        var skipped = new List<string>();
        ClosedTab? next = history.Pop(c => c.Tab.DisplayName != "file04.bin", c => skipped.Add(c.Tab.DisplayName));
        Assert.Equal("file02.bin", next!.Tab.DisplayName);
        Assert.Equal(0x100, next.Tab.Cursor);
        Assert.Equal(["file04.bin"], skipped);
        Assert.Null(history.Pop(_ => true));
    }

    [Fact]
    public void ClosedTabHistoryKeepsTwenty()
    {
        var history = new ClosedTabHistory();
        for (int i = 1; i <= 25; i++)
        {
            history.Push(new ClosedTab { Tab = Tab($"f{i}.bin") });
        }

        Assert.Equal(ClosedTabHistory.Capacity, history.Count);
        Assert.Equal("f25.bin", history.Items[0].Tab.DisplayName);
        Assert.Equal("f6.bin", history.Items[^1].Tab.DisplayName);
    }

    [Fact]
    public void SessionRoundTripsAndWritesOnlyWhenChanged()
    {
        var store = new SessionStore(_dir);
        var state = new SessionState
        {
            Windows =
            [
                new SessionWindow { X = 10, Y = 20, Width = 1280, Height = 800, ActiveTab = 1, Tabs = [Tab("a.bin", 0x10), Tab("b.bin", 0x20)] },
            ],
            ClosedTabs = [new ClosedTab { Tab = Tab("c.bin", 0x30), Index = 2 }],
        };
        Assert.True(store.SaveIfChanged(state));
        Assert.False(store.SaveIfChanged(state));

        (SessionState loaded, SessionLoadStatus status) = new SessionStore(_dir).Load(DateTime.Now);
        Assert.Equal(SessionLoadStatus.Ok, status);
        SessionWindow window = Assert.Single(loaded.Windows);
        Assert.Equal((10, 20, 1280, 800, 1), (window.X, window.Y, window.Width, window.Height, window.ActiveTab));
        Assert.Equal([0x10L, 0x20L], window.Tabs.Select(t => t.Cursor));
        Assert.Equal("c.bin", loaded.ClosedTabs.Single().Tab.DisplayName);
    }

    [Fact]
    public void BrokenSessionStartsEmptyAndKeepsTheFile()
    {
        File.WriteAllText(Path.Combine(_dir, SessionStore.FileName), "{ broken");
        (SessionState state, SessionLoadStatus status) = new SessionStore(_dir).Load(new DateTime(2026, 10, 8, 12, 0, 0));
        Assert.Equal(SessionLoadStatus.Broken, status);
        Assert.Empty(state.Windows);
        Assert.True(File.Exists(Path.Combine(_dir, "session.json.broken-20261008-120000")));
        Assert.False(File.Exists(Path.Combine(_dir, SessionStore.FileName)));
    }

    [Theory]
    [InlineData("always", StartupSessionAction.Restore)]
    [InlineData("ask", StartupSessionAction.Offer)]
    [InlineData("never", StartupSessionAction.None)]
    [InlineData("unknown", StartupSessionAction.Restore)]
    public void StartupActionFollowsTheSetting(string setting, StartupSessionAction expected)
    {
        var session = new SessionState { Windows = [new SessionWindow { Tabs = [Tab("a.bin")], ActiveTab = 0 }] };
        Assert.Equal(expected, StartupPlanner.Decide(StartupPlanner.Parse(setting), session));

        // 復元するタブがなければ何もしない (無題だけのセッションなど)。
        var empty = new SessionState { Windows = [new SessionWindow { Tabs = [Tab("Untitled 1", kind: SessionTabKind.Untitled)] }] };
        Assert.Equal(StartupSessionAction.None, StartupPlanner.Decide(StartupPlanner.Parse(setting), empty));
    }

    [Fact]
    public void RecoveredDocumentsAreNotDuplicated()
    {
        var window = new SessionWindow { Tabs = [Tab("a.bin"), Tab("b.bin"), Tab("c.bin")], ActiveTab = 2 };
        SessionWindow result = StartupPlanner.WithoutRecovered(window, [@"c:\DATA\b.bin"]);
        Assert.Equal(["a.bin", "c.bin"], result.Tabs.Select(t => t.DisplayName));
        Assert.Equal(1, result.ActiveTab);
    }

    [Fact]
    public void WelcomeIsShownUntilDismissed()
    {
        var state = new AppStateStore(_dir);
        state.Load();
        Assert.True(state.IsFirstRun);
        Assert.False(state.WelcomeDismissed);
        Assert.Null(state.DismissWelcome());

        var again = new AppStateStore(_dir);
        again.Load();
        Assert.False(again.IsFirstRun);
        Assert.True(again.WelcomeDismissed);
    }
}
