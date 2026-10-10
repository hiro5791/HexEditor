using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using HexEditor.Core.Recovery;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// 起動時の動作 (UI-30)、セッションの保存と復元 (UI-31)、スタートページと初回起動 (UI-38)。
/// </summary>
public sealed partial class MainWindow
{
    private SessionState _startupSession = new();
    private bool _restoreAfterRecovery;
    /// <summary>
    /// 起動時に「前回のセッションを復元」を提案中 (ask)。アプリ全体で 1 つ (最初のウィンドウを閉じても、終了するウィンドウが別でも同じ)。
    /// </summary>
    private static bool s_sessionOffered;

    /// <summary>
    /// 起動の準備 (コマンドラインのファイルを開く前に呼ぶ)。セッションを読み、閉じたタブの記録を戻し、ウィンドウの位置を戻す。
    /// 復旧用データがあれば、その提案の後にセッションを復元する (UI-30 の仕様 3)。<c>always</c> なら復元してからコマンドラインの
    /// ファイルを開き、そのファイルをアクティブにする (仕様 2)。
    /// </summary>
    public void PrepareStartup()
    {
        InitializeStartPage();
        if (Vm.Files is not { } files)
        {
            return;
        }

        (SessionState session, SessionLoadStatus status) = files.Session.Load(DateTime.Now);
        _startupSession = session;
        Vm.ClosedTabs.Load(session.ClosedTabs);
        if (status == SessionLoadStatus.Broken)
        {
            AppLog.Warning("session.json was broken; started with an empty session.");
        }

        if (session.Windows.FirstOrDefault() is { } window)
        {
            RestoreBounds(window);
        }

        // 2 つ目以降のウィンドウのタブも、起動時の動作に従って戻す (UI-14 の仕様 5、UI-31)。

        // 「今すぐ再起動」(UI-43 の仕様 5) で起動したときは、設定に関係なく復元する。
        switch (StartupPlanner.Decide(AppRestart.IsSessionRestart ? RestoreOnStartup.Always : FileSettings.RestoreOnStartup(App.Settings), session))
        {
            case StartupSessionAction.Restore when HasRecoveryData():
                _restoreAfterRecovery = true;
                break;
            case StartupSessionAction.Restore:
                RestoreSession(session, []);
                break;
            case StartupSessionAction.Offer:
                s_sessionOffered = true;
                StartPage.ViewModel.CanRestoreSession = true;
                break;
        }

        // 正常終了時と、30 秒ごと (変化があったときだけ) に session.json に書く (UI-31 の仕様 2)。全ウィンドウの分をまとめて書く。
        WindowManager.StartSessionTimer();
    }

    /// <summary>復旧の提案を出すか (起動時の復旧の画面の対象があるか)。</summary>
    private bool HasRecoveryData()
    {
        string defaultRoot = Program.Environment.Locations.Recovery;
        return new[] { Vm.RecoveryRoot, defaultRoot }.Distinct(StringComparer.OrdinalIgnoreCase).Any(r => RecoveryStore.Scan(r).Count > 0)
            || Core.Saving.InPlaceSaver.FindJournals(defaultRoot).Count > 0;
    }

    /// <summary>復旧の提案が終わった (UI-30 の仕様 3): 復旧した文書と重ならないように、セッションを復元する。</summary>
    private void ContinueStartupAfterRecovery()
    {
        if (!_restoreAfterRecovery)
        {
            return;
        }

        _restoreAfterRecovery = false;
        DocumentViewModel? active = Vm.Selected;
        RestoreSession(_startupSession, [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()]);

        // コマンドラインで指定したファイル・復旧した文書をアクティブなタブのままにする。
        if (active is not null && Vm.Documents.Contains(active))
        {
            Vm.Selected = active;
        }
    }

    /// <summary>
    /// セッションのウィンドウとタブを開く (UI-31、UI-14 の仕様 5)。1 つ目のウィンドウの記録はこのウィンドウに、2 つ目以降は新しいウィンドウに
    /// 戻す (タブのないウィンドウは作らない)。最後にアクティブだったウィンドウを戻す。
    /// </summary>
    private void RestoreSession(SessionState session, IReadOnlyList<string> exclude)
    {
        // セッションのウィンドウの番号 → 戻したウィンドウ (飛ばしたウィンドウがあっても番号がずれないように)。
        var restored = new Dictionary<int, MainWindow>();
        for (int i = 0; i < session.Windows.Count; i++)
        {
            SessionWindow record = session.Windows[i];
            SessionWindow tabs = StartupPlanner.WithoutRecovered(record, exclude);
            if (i > 0 && !tabs.Tabs.Any(SessionRules.IsRestorable))
            {
                continue;
            }

            MainWindow target = i == 0 ? this : WindowManager.CreateWindow(this, record);
            target.RestoreWindowTabs(record, tabs);
            restored[i] = target;
        }

        // 最後にアクティブだったウィンドウ (飛ばした場合は最初のウィンドウ) を操作の対象にし、自分のウィンドウの中で前に出す。
        // 最初のウィンドウは復元の後に表示される (表示でアクティブになる) ので、表示の後に行う。前に出すのは起動した直後で、
        // 他のアプリの作業を奪うことはない (テスト・開発中の確認では前に出さない。WindowManager.BringToFront)。
        if ((restored.GetValueOrDefault(session.LastActiveWindow) ?? restored.GetValueOrDefault(0)) is not { } active)
        {
            return;
        }

        WindowManager.MarkActive(active);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            WindowManager.MarkActive(active);
            if (active != this)
            {
                WindowManager.BringToFront(active);
            }
        });
    }

    /// <summary>ウィンドウ 1 つ分のパネルの配置とタブを戻す (UI-31)。</summary>
    private void RestoreWindowTabs(SessionWindow window, SessionWindow tabs)
    {
        // パネルの配置を戻す (UI-31 の仕様 1。記録がなければ最後に閉じたウィンドウの配置のまま。UI-05 の仕様 7)。
        if (window.Panels is { ValueKind: System.Text.Json.JsonValueKind.Object } panels)
        {
            RestorePanelLayout(System.Text.Json.Nodes.JsonNode.Parse(panels.GetRawText()));
        }

        int first = Vm.Documents.Count;
        Vm.RestoreTabs(
            tabs,
            tab => OpenSessionTab(tab, null),
            vm => ShowNotice(Loc.Get("Session_FileChanged"), InfoBarSeverity.Informational, vm));
        RestoreSideBySide(tabs, first);
        AppLog.Info($"Session restored: {Vm.Documents.Count} tab(s)");
        UpdateTitle();
    }

    /// <summary>デコードに時間がかかるため、セッションのタブをあとで開く (見つからないタブにしない)。</summary>
    private bool _sessionTabDecoding;

    /// <summary>
    /// セッションのタブ 1 つを開く (UI-31): 範囲を開いたタブ (ENG-13) は同じ範囲、デコードしたタブ (ENG-38) は同じ形式で開き直す。
    /// 大きいファイルのデコードは長時間処理として続け、終わったら開く (それまでは null)。
    /// </summary>
    private DocumentViewModel? OpenSessionTab(SessionTab tab, int? index)
    {
        _sessionTabDecoding = false;
        string path = tab.Path!;
        if (tab.RangeStart is { } start && tab.RangeLength is { } length)
        {
            try
            {
                DocumentViewModel vm = Vm.OpenRange(path, start, length, tab.RangeResizable, tab.ReadOnly);
                ApplyLinkedOrRangeView(vm);
                if (index is int at)
                {
                    Vm.MoveDocument(vm, Math.Min(at, Vm.Documents.Count - 1));
                }

                return vm;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                ShowNotice(Loc.Format("Error_Open", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error);
                return null;
            }
        }

        if (tab.EncodedFormat is { } format)
        {
            Task<DocumentViewModel?> decoding = OpenEncodedAsync(path, format, index);
            if (decoding.IsCompleted)
            {
                return decoding.Result;
            }

            _sessionTabDecoding = true;
            return null;
        }

        return TryOpen(path, index, tab.ReadOnly, restorePosition: false);
    }

    /// <summary>「前回のセッションを復元」(スタートページ、UI-30 の ask。コマンドパレット「ウィンドウ: 前回のセッションを復元」)。</summary>
    public void RestorePreviousSession()
    {
        StartPage.ViewModel.CanRestoreSession = false;
        s_sessionOffered = false;
        RestoreSession(_startupSession, [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()]);
    }

    /// <summary>通常の表示 (最大化・全画面・最小化でない) のときの位置と大きさ。最大化などの間もセッションに書くために覚えておく。</summary>
    private RectInt32 _normalBounds;

    /// <summary>最小化する前に最大化していたか (最小化したままセッションを書くときに使う)。</summary>
    private bool _wasMaximized;

    /// <summary>テストでは最大化しない (アクティブになるため) が、セッションの記録では最大化として扱う。</summary>
    private bool _maximizeSkipped;

    /// <summary>通常の表示の位置と大きさを追いかける (UI-31 の仕様 1)。ウィンドウごとに 1 回呼ぶ。</summary>
    private void TrackNormalBounds()
    {
        _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        AppWindow.Changed += (_, e) =>
        {
            if (!(e.DidPositionChange || e.DidSizeChange || e.DidPresenterChange) || _fullScreen
                || AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (presenter.State == OverlappedPresenterState.Restored)
            {
                _normalBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
                _wasMaximized = false;
            }
            else if (presenter.State == OverlappedPresenterState.Maximized)
            {
                _wasMaximized = true;
            }
        };
    }

    /// <summary>
    /// 今のウィンドウの位置と大きさ (物理ピクセル)。最大化・全画面・最小化の間は、その前の通常の表示の位置と大きさを書き、最大化・全画面かを
    /// 別に書く (戻したときに通常の大きさが失われないように。UI-31 の仕様 1)。モニターは名前で書く。
    /// </summary>
    private SessionWindow CurrentBounds()
    {
        OverlappedPresenterState? state = AppWindow.Presenter is OverlappedPresenter p ? p.State : null;
        bool maximized = _fullScreen ? _restoreMaximized
            : state == OverlappedPresenterState.Maximized || (state == OverlappedPresenterState.Minimized && _wasMaximized) || _maximizeSkipped;
        var current = new Core.View.PixelRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        Core.View.PixelRect? normal = _fullScreen && !_restoreMaximized && _restoreBounds.Width > 0
            ? new Core.View.PixelRect(_restoreBounds.X, _restoreBounds.Y, _restoreBounds.Width, _restoreBounds.Height)
            : _normalBounds.Width > 0 ? new Core.View.PixelRect(_normalBounds.X, _normalBounds.Y, _normalBounds.Width, _normalBounds.Height) : null;
        Core.View.PixelRect bounds = Core.View.WindowPlacement.NormalBounds(
            current, normal, isNormalState: !_fullScreen && state == OverlappedPresenterState.Restored && !_maximizeSkipped);
        return new SessionWindow
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = maximized,
            FullScreen = _fullScreen,
            Monitor = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest) is { } area ? MonitorName(area) : null,

            // パネルの配置と大きさ (UI-05、UI-31 の仕様 1)。
            Panels = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(PanelLayoutJson.ToJsonString()),
        };
    }

    /// <summary>モニターの名前 (<c>\\.\DISPLAY1</c> など)。取れなければ null。</summary>
    private static string? MonitorName(DisplayArea area)
    {
        nint monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        var info = new MonitorInfoEx { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfoEx>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info) && info.Device is { Length: > 0 } name ? name : null;
    }

    /// <summary>モニターの名前として書いた記録か (以前の版はモニターの番号を書いていた。番号は起動ごとに変わるので使わない)。</summary>
    private static bool IsMonitorName(string? value) => value?.StartsWith(@"\\.\", StringComparison.Ordinal) == true;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        // RECT と同じ大きさ (int 4 つ)。値は使わない。
        public RectInt32 Monitor;
        public RectInt32 Work;
        public uint Flags;

        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    /// <summary>セッションに書くこのウィンドウの記録 (位置・大きさ・パネル・タブ)。</summary>
    public SessionWindow CaptureSessionWindow() => Vm.CaptureWindow(CurrentBounds()) with { SideBySide = CaptureSideBySide() };

    /// <summary>
    /// 前回の位置と大きさに戻す。画面の外になる場合と、記録したモニターがない場合 (外したモニターにあったなど) は、主モニターの中央に既定の
    /// 大きさで出す (UI-01 の「エラー」)。最大化・全画面だったウィンドウは、元の大きさに戻してから最大化・全画面にする (UI-31 の仕様 1)。
    /// </summary>
    private void RestoreBounds(SessionWindow window)
    {
        if (window.Width < 200 || window.Height < 150)
        {
            return;
        }

        static Core.View.PixelRect Pixels(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);
        var monitors = new List<(string? Name, Core.View.PixelRect WorkArea)>();
        // DisplayArea.FindAll() の一覧は foreach で列挙すると例外になる (CsWinRT の既知の問題) ので、番号で読む。
        IReadOnlyList<DisplayArea> displays = DisplayArea.FindAll();
        for (int i = 0; i < displays.Count; i++)
        {
            monitors.Add((MonitorName(displays[i]), Pixels(displays[i].WorkArea)));
        }

        DisplayArea primary = DisplayArea.Primary;
        double scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        Core.View.PixelRect placed = Core.View.WindowPlacement.Restore(
            new Core.View.PixelRect(window.X, window.Y, window.Width, window.Height), monitors,
            IsMonitorName(window.Monitor) ? window.Monitor : null, Pixels(primary.WorkArea), scale);
        var bounds = new RectInt32(placed.X, placed.Y, placed.Width, placed.Height);
        AppWindow.MoveAndResize(bounds);
        _normalBounds = bounds;
        bool moved = placed.X != window.X || placed.Y != window.Y;
        if (moved)
        {
            AppLog.Info("The saved window position is off screen; centered on the primary monitor.");
        }
        else if (window.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // テストではウィンドウをアクティブにしない (最大化はアクティブにする) ので、最大化したことにだけする。
            if (TestHooks.SuppressActivation)
            {
                _maximizeSkipped = true;
            }
            else
            {
                presenter.Maximize();
            }
        }

        // 全画面はウィンドウを表示してから切り替える (表示の前に切り替えると、表示の処理と重なるため)。
        if (window.FullScreen)
        {
            DispatcherQueue.TryEnqueue(EnterFullScreen);
        }
    }

    /// <summary>session.json に書く (変化があったときだけ)。書けなければログに残す。</summary>
    public void SaveSession(SessionState? state = null)
    {
        if (Vm.Files is not { } files)
        {
            return;
        }

        // 「前回のセッションを復元」を押さずに使っている間は、前回のセッションを上書きしない (タブを開くまで)。書く内容で判断する
        // (終了時は閉じる確認の前に写したタブを書く。確認の後はウィンドウに文書が残っていないため)。
        SessionState session = state ?? WindowManager.CaptureSession();
        bool hasTabs = session.Windows.Any(w => w.Tabs.Count > 0);
        if (s_sessionOffered && !hasTabs)
        {
            return;
        }

        try
        {
            files.Session.SaveIfChanged(session);

            // 新しいセッションを書いたら、前回のセッションはもう残っていない (この後はタブを閉じても普通に書く)。
            if (hasTabs)
            {
                s_sessionOffered = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"session.json was not written: {ex.Message}");
        }
    }

    // ---- スタートページ (UI-38) ----

    private void InitializeStartPage()
    {
        // セッションに書く通常の表示の位置と大きさ (どのウィンドウもここを 1 回通る)。
        TrackNormalBounds();
        StartPage.OpenRequested += (_, _) => Open_Click(this, new RoutedEventArgs());
        StartPage.NewRequested += (_, _) => New_Click(this, new RoutedEventArgs());
        StartPage.CommandRequested += (_, id) => _ = Commands.ExecuteAsync(id);
        StartPage.RestoreSessionRequested += (_, _) => RestorePreviousSession();
        StartPage.ShowAllRequested += (_, _) => ShowAllRecent_Click(this, new RoutedEventArgs());
        StartPage.RecentRequested += (_, entry) => OpenRecent(entry.Item);

        // 行の右クリックは「すべて表示…」と同じメニュー (UI-32 の仕様 4)。一覧は最近使ったファイルの変更で作り直される。
        StartPage.RecentContextRequested += (_, entry) =>
        {
            _startRecentMenu = RecentContextMenu(entry, () => { });
            StartPage.ShowRecentMenu(_startRecentMenu);
        };
        StartPage.WelcomeClosed += (_, _) => DismissWelcome();
        StartPage.ChoiceChanged += (_, choice) => OnWelcomeChoice(choice.Kind, choice.Value);

        // 初回起動 (state.json がなかった) で、まだ閉じていないときだけ (UI-38 の仕様 2・3)。
        bool welcome = Vm.Files?.State is { ShowWelcome: true };
        StartPage.ViewModel.ShowWelcome = welcome;
        StartPage.SetChoices(
            App.Settings.GetString(LanguageKey, "system"),
            App.Settings.GetString(Appearance.ThemeKey, Appearance.ThemeDefault),
            CommandService.Keys.Preset);

        // 何も選ばずにファイルを開いても「はじめに」は消える (仕様 3)。
        Vm.Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && StartPage.ViewModel.ShowWelcome)
            {
                DismissWelcome();
            }
        };
        // 最近使ったファイルとキー割り当てはアプリ全体で 1 つ。閉じたウィンドウには知らせない (UI-14)。
        EventHandler recentChanged = (_, _) => DispatcherQueue.TryEnqueue(RefreshRecentViews);
        Vm.Recent.Changed += recentChanged;
        Action bindingsChanged = () => DispatcherQueue.TryEnqueue(RefreshStartShortcuts);
        CommandService.BindingsChanged += bindingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Recent.Changed -= recentChanged;
                CommandService.BindingsChanged -= bindingsChanged;
            }
        };
        RefreshRecentViews();
        RefreshStartShortcuts();
    }

    /// <summary>「開く」「新規作成」に今のキー割り当て (Ctrl+O、Ctrl+N。UI-38 の仕様 1) を添える。</summary>
    private void RefreshStartShortcuts() =>
        StartPage.SetShortcuts(CommandService.ShortcutText("file.open"), CommandService.ShortcutText("file.new"));

    /// <summary>スタートページで最後に開いた行のメニュー (テスト用の命令が項目を押す)。</summary>
    private MenuFlyout? _startRecentMenu;

    /// <summary>表示言語の設定 (UI-43)。</summary>
    public const string LanguageKey = "ui.language";

    private void DismissWelcome()
    {
        StartPage.ViewModel.ShowWelcome = false;
        Vm.Files?.State.DismissWelcome();
    }

    /// <summary>「はじめに」の選択を設定に書く (表示言語は再起動で反映。テーマはすぐに反映)。</summary>
    private void OnWelcomeChoice(string kind, string value)
    {
        switch (kind)
        {
            case "language":
                App.Settings.SetString(LanguageKey, value, "system");
                StartPage.ViewModel.LanguageRestartNeeded = true;
                break;
            case "theme":
                App.Settings.SetString(Appearance.ThemeKey, value, Appearance.ThemeDefault);
                ApplyAppearance();
                break;
            case "preset":
                // キー割り当て (UI-18、UI-19) の仕組みで切り替える。利用者が個別に変えた割り当ては保つ。keybindings.json は自動で書かれる。
                if (value != CommandService.Keys.Preset)
                {
                    CommandService.Keys.SwitchPreset(value, preferUser: true);
                }

                break;
        }
    }

    /// <summary>スタートページとメニューの最近使ったファイルを作り直す。</summary>
    private void RefreshRecentViews()
    {
        StartPage.ViewModel.SetRecent(Vm.Recent.MenuEntries(), action => DispatcherQueue.TryEnqueue(() => action()));
        RebuildRecentMenu();
        RefreshCommandUi();
    }

    // ---- 見つからないファイルのタブ (UI-31 の「エラー」) ----

    private async void MissingClose_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } doc)
        {
            await CloseAsync([doc]);
        }
    }

    /// <summary>「場所を探す」: ファイルを選び、同じ位置のタブで開き直す。</summary>
    private async void MissingLocate_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { MissingRecord: { } record } doc)
        {
            return;
        }

        IReadOnlyList<string>? paths = TestHooks.OpenPickerResult("HexEditor.Locate");
        if (paths is null)
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.Open" };
            picker.FileTypeFilter.Add("*");
            paths = await picker.PickSingleFileAsync() is { } file ? [file.Path] : [];
        }

        if (paths.Count == 0)
        {
            return;
        }

        int index = Vm.Documents.IndexOf(doc);
        Vm.Close(doc);
        if (TryOpen(paths[0], index, record.ReadOnly, restorePosition: false) is { } opened)
        {
            opened.RestorePosition(record.Cursor, record.SelectionStart, record.SelectionLength, record.TopRow);
        }
    }
}
