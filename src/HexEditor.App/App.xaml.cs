using System.Text;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Settings;
using Microsoft.UI.Xaml;

namespace HexEditor.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Shift_JIS・EBCDIC など、.NET が標準で持たないコードページを使えるようにする (VIEW-21)。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 未処理の例外は記録する (PKG-30。復旧用データの書き出しは ENG-27 で行う)。
        UnhandledException += (_, e) => CrashReporter.Handle(e.Exception, exit: true);
    }

    public static Window Window { get; private set; } = null!;

    public const string TempDirectoryKey = "storage.tempDirectory";

    /// <summary>一時ファイル (パスを持たない項目のコピーなど) の置き場所 (PKG-13)。</summary>
    public static string TempRoot { get; private set; } = Path.GetTempPath();

    /// <summary>設定 (UI-23)。</summary>
    public static SettingsStore Settings { get; private set; } = null!;

    /// <summary>復旧用データの保存間隔 (ENG-27 の仕様 1。設定画面ができるまでは既定の 1 分)。</summary>
    public static TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recoveryTimer;

    /// <summary>メモリの監視 (ENG-08)。アプリの終了まで保持する。</summary>
    private MemoryMonitor? _memoryMonitor;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        IAppEnvironment env = Program.Environment;
        TestHooks.BeforeLaunch();

        // 設定 (UI-23)。アクセントカラーはリソースが参照される前に上書きする (UI-27)。
        Settings = new SettingsStore(env.Locations.Settings);
        SettingsLoadStatus settingsStatus = Settings.Load();
        AppLog.DebugEnabled = Settings.GetString("log.level", "info") == "debug";
        CrashReporter.WriteMiniDump = Settings.GetBool(CrashReporter.MiniDumpKey, false);
        AppLog.Initialize(env.Locations.Logs);
        Appearance.ApplyAccent(Settings);
        // 追加バッファの一時ファイルは復旧用データと同じフォルダに置き、異常終了後もそのまま参照できるようにする (ENG-27 の仕様 2)。
        // 設定 storage.tempDirectory があれば、ドキュメントごとのフォルダ (追加データの退避と復旧用データ) と一時ファイルを
        // その下に置く (PKG-13 の仕様 3。容量の大きいドライブを使うため)。
        string custom = Settings.GetString(TempDirectoryKey, string.Empty);
        TempRoot = custom.Length > 0 ? Path.Combine(custom, "HexEditor", "temp") : env.Locations.Temp;
        var options = new DocumentOptions
        {
            TempDirectory = custom.Length > 0 ? Path.Combine(custom, "HexEditor", "recovery") : env.Locations.Recovery,
        };
        var vm = new MainViewModel(new OperationCenter(TestHooks.Time), new EngineMemory(), options, env.Locations.Recovery);
        var window = new MainWindow(vm);
        Window = window;
        window.ApplyAppearance();
        window.ShowSettingsStatus(settingsStatus);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppLog.Info($"Started {env.AppVersion} ({env.Distribution}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        // タスクバーのまとまり (PKG-12 の仕様 4): プロセスに設定した AppUserModelID。MSIX 版はパッケージが決めるので設定しない。
        // 配布のテスト (TC-PKG-12-03) が、スタートメニューのショートカットの値と比べる。
        AppLog.Info($"AppUserModelID: {ProcessIdentity.TryGetAppUserModelId() ?? "(not set)"}");

        // 外部で編集された設定・コントラストテーマの切り替えを反映する (UI-23 の仕様 7、UI-26 の仕様 3)。
        Settings.Changed += _ => DispatcherQueue.TryEnqueue(() =>
        {
            AppLog.DebugEnabled = Settings.GetString("log.level", "info") == "debug";
            CrashReporter.WriteMiniDump = Settings.GetBool(CrashReporter.MiniDumpKey, false);
            window.ApplyAppearance();
            window.ApplyEditorSettings();
        });
        Settings.ExternalEditFailed += reason => DispatcherQueue.TryEnqueue(() => window.ShowSettingsEditError(reason));
        Appearance.SystemColorsChanged += () => DispatcherQueue.TryEnqueue(window.ApplyAppearance);
        try
        {
            Settings.StartWatching();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warning($"Settings watcher unavailable: {ex.GetType().Name}");
        }

        // 他のアプリの書き込みを禁止できなかったら、その文書の中で知らせる (ENG-15 の仕様 2)。
        vm.LockFailed += (_, doc) => DispatcherQueue.TryEnqueue(() => window.ShowLockFailed(doc));

        // メモリの上限の監視 (ENG-08)。1 秒ごとに集計し、上限を超えたら減らす。減らしきれなければ知らせる。
        _memoryMonitor = new MemoryMonitor(vm.Memory);
        _memoryMonitor.OverLimit += (_, _) => DispatcherQueue.TryEnqueue(() => window.ShowMemoryOverLimit(vm.Memory.Limit));
        _memoryMonitor.LowMemory += (_, _) => AppLog.Warning("Low memory notification: cache trimmed.");

        // トースト通知を押したら、ウィンドウを前に出す (UI-36 の仕様 8)。
        ToastNotifier.Invoked += () => DispatcherQueue.TryEnqueue(window.Activate);

        // 異常終了の直前に未保存の編集内容を書き出す (PKG-30 の仕様 1 の 1)。
        CrashReporter.WriteRecovery = timeout => vm.WriteRecoveryNow(timeout);

        // 復旧用データの定期の書き出し (ENG-27 の仕様 1。既定 1 分ごと)。
        // タイマーはフィールドに持つ (ローカル変数だけだとガベージコレクションで回収され、書き出しが止まる)。
        _recoveryTimer = DispatcherQueue.CreateTimer();
        _recoveryTimer.Interval = RecoveryInterval;
        _recoveryTimer.Tick += async (_, _) => await vm.WriteRecoveryAsync(window.ShowRecoveryWriteError);
        _recoveryTimer.Start();

        // 既存のインスタンスに転送された起動 (2 つ目の起動で指定したファイル) を、このウィンドウのタブとして開く (UI-15)。
        SingleInstance.Redirected += commandLine =>
            DispatcherQueue.TryEnqueue(() => window.OpenFromCommandLine(commandLine, activate: !DevOptions.NoActivate && !TestHooks.SuppressActivation));

        // コマンドラインで指定したファイルを開く (AUTO-37)。指定がなければスタートページを出す (UI-01 の仕様 2)。
        // テスト用のビルドでは、異常を再現するデータソースもここで開く (テスト方針 7.2)。
        window.OpenFromCommandLine(TestHooks.OpenStartupSources(vm, Program.CommandLine), activate: false);

        // 開発中の確認用: 作業の邪魔にならないよう、起動したら前のウィンドウに戻し、自分は後ろに回る。
        // 自動テスト (--test-hooks) では一度もアクティブにせずに表示する。
        if (!TestHooks.ShowWithoutActivation(window))
        {
            nint previous = DevOptions.NoActivate ? DevOptions.ForegroundWindow() : 0;
            window.Activate();
            if (previous != 0)
            {
                DevOptions.SendToBack(window, previous);
            }
        }

        // テスト用のメニューと命令の通り道 (テスト用のビルドだけ。テスト方針 8.4)。
        TestHooks.OnLaunched(window, vm);

        // 前回の異常終了の後始末: 復旧の提案と、クラッシュ情報の通知 (ENG-27 の仕様 6、PKG-30 の仕様 2)。
        window.ShowStartupNoticesWhenLoaded();
    }
}
