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
    private bool _sessionOffered;

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
                _sessionOffered = true;
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
        var restored = new List<MainWindow>();
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
            restored.Add(target);
        }

        if (session.LastActiveWindow > 0 && session.LastActiveWindow < session.Windows.Count && restored.Count > 1)
        {
            WindowManager.MarkActive(restored[Math.Min(session.LastActiveWindow, restored.Count - 1)]);
        }
        else if (restored.Count > 0)
        {
            WindowManager.MarkActive(restored[0]);
        }
    }

    /// <summary>ウィンドウ 1 つ分のパネルの配置とタブを戻す (UI-31)。</summary>
    private void RestoreWindowTabs(SessionWindow window, SessionWindow tabs)
    {
        // パネルの配置を戻す (UI-31 の仕様 1。記録がなければ最後に閉じたウィンドウの配置のまま。UI-05 の仕様 7)。
        if (window.Panels is { ValueKind: System.Text.Json.JsonValueKind.Object } panels)
        {
            RestorePanelLayout(System.Text.Json.Nodes.JsonNode.Parse(panels.GetRawText()));
        }

        Vm.RestoreTabs(
            tabs,
            (path, readOnly) => TryOpen(path, readOnly: readOnly, restorePosition: false),
            vm => ShowNotice(Loc.Get("Session_FileChanged"), InfoBarSeverity.Informational, vm));
        AppLog.Info($"Session restored: {Vm.Documents.Count} tab(s)");
        UpdateTitle();
    }

    /// <summary>「前回のセッションを復元」(スタートページ、UI-30 の ask。コマンドパレット「ウィンドウ: 前回のセッションを復元」)。</summary>
    public void RestorePreviousSession()
    {
        StartPage.ViewModel.CanRestoreSession = false;
        _sessionOffered = false;
        RestoreSession(_startupSession, [.. Vm.Documents.Select(d => d.FilePath).OfType<string>()]);
    }

    /// <summary>今のウィンドウの位置と大きさ (物理ピクセル)。</summary>
    private SessionWindow CurrentBounds()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return new SessionWindow
        {
            X = position.X,
            Y = position.Y,
            Width = size.Width,
            Height = size.Height,
            Maximized = maximized,
            FullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen,
            Monitor = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)?.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),

            // パネルの配置と大きさ (UI-05、UI-31 の仕様 1)。
            Panels = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(PanelLayoutJson.ToJsonString()),
        };
    }

    /// <summary>セッションに書くこのウィンドウの記録 (位置・大きさ・パネル・タブ)。</summary>
    public SessionWindow CaptureSessionWindow() => Vm.CaptureWindow(CurrentBounds());

    /// <summary>
    /// 前回の位置と大きさに戻す。画面の外になる場合 (外したモニターにあったなど) は、主モニターの中央に既定の大きさで出す
    /// (UI-01 の「エラー」)。
    /// </summary>
    private void RestoreBounds(SessionWindow window)
    {
        if (window.Width < 200 || window.Height < 150)
        {
            return;
        }

        static Core.View.PixelRect Pixels(RectInt32 r) => new(r.X, r.Y, r.Width, r.Height);
        var areas = new List<Core.View.PixelRect>();
        // DisplayArea.FindAll() の一覧は foreach で列挙すると例外になる (CsWinRT の既知の問題) ので、番号で読む。
        IReadOnlyList<DisplayArea> displays = DisplayArea.FindAll();
        for (int i = 0; i < displays.Count; i++)
        {
            areas.Add(Pixels(displays[i].WorkArea));
        }

        DisplayArea primary = DisplayArea.Primary;
        double scale = GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        Core.View.PixelRect placed = Core.View.WindowPlacement.Restore(
            new Core.View.PixelRect(window.X, window.Y, window.Width, window.Height), areas, Pixels(primary.WorkArea), scale);
        AppWindow.MoveAndResize(new RectInt32(placed.X, placed.Y, placed.Width, placed.Height));
        if (placed.X != window.X || placed.Y != window.Y)
        {
            AppLog.Info("The saved window position is off screen; centered on the primary monitor.");
            return;
        }
        if (window.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>session.json に書く (変化があったときだけ)。書けなければログに残す。</summary>
    public void SaveSession(SessionState? state = null)
    {
        if (Vm.Files is not { } files)
        {
            return;
        }

        // 「前回のセッションを復元」を押さずに使っている間は、前回のセッションを上書きしない (タブを開くまで)。
        if (_sessionOffered && WindowManager.Windows.All(w => w.Vm.Documents.Count == 0))
        {
            return;
        }

        try
        {
            files.Session.SaveIfChanged(state ?? WindowManager.CaptureSession());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"session.json was not written: {ex.Message}");
        }
    }

    // ---- スタートページ (UI-38) ----

    private void InitializeStartPage()
    {
        StartPage.OpenRequested += (_, _) => Open_Click(this, new RoutedEventArgs());
        StartPage.NewRequested += (_, _) => New_Click(this, new RoutedEventArgs());
        StartPage.RestoreSessionRequested += (_, _) => RestorePreviousSession();
        StartPage.ShowAllRequested += (_, _) => ShowAllRecent_Click(this, new RoutedEventArgs());
        StartPage.RecentRequested += (_, entry) => OpenRecent(entry.Item);
        StartPage.WelcomeClosed += (_, _) => DismissWelcome();
        StartPage.ChoiceChanged += (_, choice) => OnWelcomeChoice(choice.Kind, choice.Value);

        bool welcome = Vm.Files?.State is { WelcomeDismissed: false };
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
        // 最近使ったファイルはアプリ全体で 1 つ。閉じたウィンドウには知らせない (UI-14)。
        EventHandler recentChanged = (_, _) => DispatcherQueue.TryEnqueue(RefreshRecentViews);
        Vm.Recent.Changed += recentChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Recent.Changed -= recentChanged;
            }
        };
        RefreshRecentViews();
    }

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
