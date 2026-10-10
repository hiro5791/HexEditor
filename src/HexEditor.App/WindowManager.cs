using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using Microsoft.UI.Xaml;

namespace HexEditor.App;

/// <summary>
/// 複数ウィンドウ (UI-14)。1 つのプロセスで開いているメインウィンドウの一覧、最後にアクティブだったウィンドウ、新しいウィンドウの作成、
/// 外部から開いたファイルの振り分け (UI-15)、アプリ全体のセッション (UI-31) と終了 (UI-13)。
/// </summary>
/// <remarks>
/// 設定・ショートカット・テーマ・最近使ったファイル・閉じたタブの記録・長時間処理はアプリ全体で 1 つ (仕様 2)。開いている文書・パネル・
/// 検索バー・通知・コマンドの処理はウィンドウごと (<see cref="MainWindow"/>、<see cref="MainViewModel"/>)。
/// </remarks>
public static partial class WindowManager
{
    private static readonly List<MainWindow> s_windows = [];

    /// <summary>開いているメインウィンドウ (作った順)。</summary>
    public static IReadOnlyList<MainWindow> Windows => s_windows;

    /// <summary>最後にアクティブだったウィンドウ (UI-14 の仕様 3)。</summary>
    public static MainWindow? LastActive { get; private set; }

    /// <summary>操作の対象のウィンドウ: 最後にアクティブだったウィンドウ (なければ最初のウィンドウ)。</summary>
    public static MainWindow Current => LastActive ?? s_windows[0];

    /// <summary>アプリの終了の途中 (ウィンドウを 1 つずつ閉じる処理と区別する)。</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>ウィンドウが増えた・減った・アクティブなウィンドウが変わった (「別のウィンドウに移動」の一覧などを作り直す)。</summary>
    public static event Action? Changed;

    /// <summary>ウィンドウを一覧に加える (作ったウィンドウは最後にアクティブだったウィンドウになる)。</summary>
    public static void Register(MainWindow window)
    {
        s_windows.Add(window);
        LastActive = window;
        window.Activated += (_, e) =>
        {
            // テスト (ウィンドウをアクティブにしない) では、ウィンドウの作成と操作の対象の指定だけで決める (結果を安定させるため)。
            if (e.WindowActivationState != WindowActivationState.Deactivated && !TestHooks.SuppressActivation)
            {
                MarkActive(window);
            }
        };
        Changed?.Invoke();
    }

    /// <summary>ウィンドウを一覧から外す (閉じた)。</summary>
    public static void Unregister(MainWindow window)
    {
        if (!s_windows.Remove(window))
        {
            return;
        }

        if (LastActive == window)
        {
            LastActive = s_windows.LastOrDefault();
        }

        Changed?.Invoke();
    }

    /// <summary>最後にアクティブだったウィンドウにする。</summary>
    public static void MarkActive(MainWindow window)
    {
        if (LastActive != window && s_windows.Contains(window))
        {
            LastActive = window;
            Changed?.Invoke();
        }
    }

    /// <summary>ウィンドウの番号 (1 から。「ウィンドウ 2」などの表示)。</summary>
    public static int NumberOf(MainWindow window) => s_windows.IndexOf(window) + 1;

    /// <summary>その文書のタブを持っているウィンドウ。</summary>
    public static MainWindow? OwnerOf(DocumentViewModel doc) => s_windows.FirstOrDefault(w => w.Vm.Documents.Contains(doc));

    /// <summary>
    /// 新しいウィンドウ (UI-14 の仕様 1)。空 (スタートページ) で開き、パネルの配置は <paramref name="layoutFrom"/> (最後にアクティブ
    /// だったウィンドウ) を引き継ぐ。<paramref name="bounds"/> を指定すると、その位置と大きさで開く (セッションの復元、タブの切り離し)。
    /// </summary>
    public static MainWindow CreateWindow(MainWindow? layoutFrom = null, SessionWindow? bounds = null, bool show = true)
    {
        MainViewModel shared = s_windows[0].Vm;
        var window = new MainWindow(new MainViewModel(shared));
        Register(window);
        window.PrepareSecondaryWindow(layoutFrom ?? (s_windows.Count > 1 ? s_windows[^2] : null), bounds);
        if (show)
        {
            Show(window);
        }

        AppLog.Info($"Window opened ({s_windows.Count} window(s))");
        return window;
    }

    /// <summary>
    /// ウィンドウを表示する。テスト (--test-hooks) と開発中の確認 (dev-no-activate の印) では、作業中のウィンドウからフォーカスを奪わない。
    /// </summary>
    public static void Show(Window window)
    {
        if (TestHooks.ShowWithoutActivation(window))
        {
            return;
        }

        nint previous = DevOptions.NoActivate ? DevOptions.ForegroundWindow() : 0;
        window.Activate();
        if (previous != 0)
        {
            DevOptions.SendToBack(window, previous);
        }
    }

    /// <summary>ウィンドウを前に出す (外部から開いた・トースト通知を押した)。テスト・開発中の確認では前に出さない。</summary>
    public static void BringToFront(MainWindow window)
    {
        if (!DevOptions.NoActivate && !TestHooks.SuppressActivation)
        {
            window.Activate();
        }
    }

    /// <summary>
    /// 既存のインスタンスに転送された起動 (UI-15 の仕様 2・4・7)。設定 <c>window.openExternalIn</c> (<c>--new-window</c> が優先) に
    /// 従い、最後にアクティブだったウィンドウか新しいウィンドウに、すべてのファイルを同じウィンドウのタブとして開く。
    /// </summary>
    public static void OpenRedirected(CommandLine commandLine)
    {
        if (s_windows.Count == 0)
        {
            return;
        }

        bool hasFiles = commandLine.Files.Count > 0 || commandLine.NewDocument;
        bool newWindow = commandLine.NewWindow
            || (hasFiles && WindowSettings.OpenExternalInNewWindow(App.Settings))
            || (!hasFiles && WindowSettings.LaunchWithoutFileOpensNewWindow(App.Settings));
        MainWindow target = newWindow ? CreateWindow(Current) : Current;
        target.OpenFromCommandLine(commandLine, activate: false);
        BringToFront(target);
    }

    /// <summary>「ウィンドウ: 次のウィンドウ」(UI-14 の仕様 6): 作った順に次のウィンドウへ。</summary>
    public static MainWindow? Next(MainWindow from)
    {
        if (s_windows.Count < 2)
        {
            return null;
        }

        MainWindow next = s_windows[(s_windows.IndexOf(from) + 1) % s_windows.Count];
        MarkActive(next);
        BringToFront(next);
        return next;
    }

    /// <summary>
    /// 開いているファイルを他のウィンドウから探す (UI-09 の仕様 1: 同じファイルは新しいタブを作らず既存のタブをアクティブにする)。
    /// </summary>
    public static (MainWindow Window, DocumentViewModel Document)? FindOpenElsewhere(MainWindow except, string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (MainWindow w in s_windows.Where(w => w != except))
        {
            if (w.Vm.FindSameFile(full) is { } doc)
            {
                return (w, doc);
            }
        }

        return null;
    }

    // ---- セッション (UI-31) ----

    /// <summary>アプリ全体のセッション: 各ウィンドウの位置・タブ・パネル、閉じたタブ、最後にアクティブだったウィンドウ。</summary>
    public static SessionState CaptureSession()
    {
        MainViewModel shared = s_windows[0].Vm;
        return new SessionState
        {
            Windows = [.. s_windows.Select(w => w.CaptureSessionWindow())],
            ClosedTabs = [.. shared.ClosedTabs.Items],
            LastActiveWindow = LastActive is { } active ? Math.Max(0, s_windows.IndexOf(active)) : 0,
        };
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? s_sessionTimer;

    /// <summary>30 秒ごと (変化があったときだけ) に session.json に書く (UI-31 の仕様 2)。</summary>
    public static void StartSessionTimer()
    {
        s_sessionTimer = App.DispatcherQueue.CreateTimer();
        s_sessionTimer.Interval = SessionStore.SaveInterval;
        s_sessionTimer.Tick += (_, _) =>
        {
            if (s_windows.Count > 0 && !IsExiting)
            {
                s_windows[0].SaveSession();
            }
        };
        s_sessionTimer.Start();
    }

    // ---- 終了 (UI-13、UI-14 の仕様 4) ----

    /// <summary>終了の確認のダイアログを出している (2 つ目の終了の要求を受け付けない)。</summary>
    private static bool s_confirmingExit;

    /// <summary>
    /// アプリを終了する (ファイル > 終了、最後のウィンドウを閉じる)。全ウィンドウの文書をまとめて確かめ (UI-13 の仕様 2)、セッションを
    /// 保存してからすべてのウィンドウを閉じる。キャンセルされたら false。
    /// </summary>
    public static async Task<bool> ExitAsync(MainWindow from)
    {
        if (IsExiting || s_confirmingExit)
        {
            return false;
        }

        // 閉じる前のタブをセッションに残す (閉じた後は文書がなくなるため。UI-31 の仕様 2)。
        SessionState session = CaptureSession();
        s_confirmingExit = true;
        try
        {
            if (!await from.ConfirmExitAsync())
            {
                return false;
            }
        }
        finally
        {
            s_confirmingExit = false;
        }

        IsExiting = true;
        s_sessionTimer?.Stop();
        from.SaveSession(session);
        App.Settings.Flush();
        Commands.CommandService.Flush();
        AppLog.Info("Exited");
        foreach (MainWindow w in s_windows.ToList())
        {
            w.CloseForExit();
        }

        // この入口から一時フォルダに作ったプロセスのスナップショットを削除する (ANA-09 の仕様 9)。
        MainWindow.DeleteTemporarySnapshots();
        return true;
    }

    /// <summary>
    /// 「今すぐ再起動」(UI-43 の仕様 5、UI-13 の仕様 2): 全ウィンドウの文書をまとめて確かめ、全ウィンドウのセッションを保存してから
    /// 再起動する。新しいプロセスはそのセッションを復元する (すべてのウィンドウとタブが戻る)。キャンセルされた・再起動できなかった
    /// 場合は false (再起動できなかったときは、確認で閉じた文書は戻らない。呼び出し側が知らせる)。
    /// </summary>
    public static async Task<bool> RestartAsync(MainWindow from)
    {
        LastRestartFailed = false;
        if (IsExiting || s_confirmingExit)
        {
            return false;
        }

        // 閉じる前のタブをセッションに残す (閉じた後は文書がなくなるため)。
        SessionState session = CaptureSession();
        s_confirmingExit = true;
        try
        {
            if (!await from.ConfirmExitAsync())
            {
                return false;
            }
        }
        finally
        {
            s_confirmingExit = false;
        }

        s_sessionTimer?.Stop();
        SaveSessionForRestart(session);
        App.Settings.Flush();
        Commands.CommandService.Flush();
        AppLog.Info("Restarting with the session");
        LastRestartFailed = false;
        if (AppRestart.Restart())
        {
            return true;
        }

        LastRestartFailed = true;
        s_sessionTimer?.Start();
        return false;
    }

    /// <summary>直前の <see cref="RestartAsync"/> が再起動に失敗した (キャンセルではない)。「手動で再起動してください」を出す。</summary>
    public static bool LastRestartFailed { get; private set; }

    /// <summary>
    /// 再起動の前のセッションを書く。起動時に「前回のセッションを復元」を提案中でも上書きする (再起動では必ずこのセッションを戻すため)。
    /// </summary>
    private static void SaveSessionForRestart(SessionState session)
    {
        if (s_windows.Count == 0 || s_windows[0].Vm.Files is not { } files)
        {
            return;
        }

        try
        {
            files.Session.SaveIfChanged(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"session.json was not written before restarting: {ex.Message}");
        }
    }

    /// <summary>異常終了の直前に、全ウィンドウの文書の復旧用データを書き出す (PKG-30 の仕様 1 の 1)。</summary>
    public static void WriteRecoveryNow(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        foreach (MainWindow w in s_windows.ToList())
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            w.Vm.WriteRecoveryNow(left);
        }
    }

    /// <summary>復旧用データの定期の書き出し (ENG-27 の仕様 1)。全ウィンドウの文書。</summary>
    public static async Task WriteRecoveryAsync()
    {
        foreach (MainWindow w in s_windows.ToList())
        {
            await w.Vm.WriteRecoveryAsync(w.ShowRecoveryWriteError);
        }
    }
}
