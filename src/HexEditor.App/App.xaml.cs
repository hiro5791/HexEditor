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

    /// <summary>
    /// 復旧用データの保存間隔のテスト用の上書き (ENG-27 の仕様 1。--test-hooks の recoveryIntervalSeconds)。null なら設定
    /// <c>save.recoveryIntervalMinutes</c> (既定 1 分、0 は無効) に従う。
    /// </summary>
    public static TimeSpan? RecoveryInterval { get; set; }

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _recoveryTimer;

    /// <summary>復旧用データの保存間隔を反映する (0 = 無効なら止める。ENG-27 の仕様 1)。</summary>
    private void ApplyRecoveryInterval()
    {
        if (_recoveryTimer is null)
        {
            return;
        }

        TimeSpan? interval = RecoveryInterval ?? FileSettings.RecoveryInterval(Settings);
        if (interval is { } value)
        {
            if (_recoveryTimer.Interval != value || !_recoveryTimer.IsRunning)
            {
                _recoveryTimer.Interval = value;
                _recoveryTimer.Start();
            }
        }
        else
        {
            _recoveryTimer.Stop();
        }
    }

    /// <summary>メモリの監視 (ENG-08)。アプリの終了まで保持する。</summary>
    private MemoryMonitor? _memoryMonitor;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        IAppEnvironment env = Program.Environment;
        TestHooks.BeforeLaunch();

        // 設定 (UI-23)。アクセントカラーはリソースが参照される前に上書きする (UI-27)。
        // $schema は同梱の settings.schema.json を指す (UI-23 の仕様 3、8。アプリの場所が変わっても書くたびに今の場所にする)。
        Settings = new SettingsStore(env.Locations.Settings, new Uri(Path.Combine(AppContext.BaseDirectory, SettingsSchema.FileName)).AbsoluteUri);
        SettingsLoadStatus settingsStatus = Settings.Load();

        // 配布形態に固有の設定項目 (更新の自動ダウンロードはインストーラ版だけ。10 の PKG-17)。開発用のビルドはすべて出す。
        Commands.CommandService.Settings.Distribution = env.Distribution == Distribution.Development ? null : env.Distribution.ToString().ToLowerInvariant();

        // 初回起動では $schema と $schemaVersion だけの settings.json を作る (UI-23 の受け入れ基準 1)。
        if (settingsStatus == SettingsLoadStatus.Ok && !File.Exists(Settings.PathName))
        {
            // 書けなくても例外にはならない (SettingsStore が知らせる。UI-22 の「エラー」)。
            if (!Settings.SaveNow())
            {
                AppLog.Warning("settings.json not created.");
            }
        }
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
        // コマンド・キー割り当て・状態 (UI-16〜UI-21、UI-23 の state.json)。
        Commands.CommandService.Initialize(env.Locations.Settings);
        var vm = new MainViewModel(new OperationCenter(TestHooks.Time), new EngineMemory(), options, env.Locations.Recovery);

        // 最近使ったファイル・前回の位置・セッション・初回起動の状態 (ENG-16、UI-31、UI-38)。ポータブル版では exe と同じドライブの
        // ファイルを exe からの相対パスで記録する (UI-32 の仕様 8)。
        string exeFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var files = new FilesContext
        {
            RecentStore = new Core.Files.RecentFileStore(env.Locations.Settings,
                env.Distribution == Distribution.Portable ? new Core.Files.RecentPathMapper(exeFolder) : null),
            Documents = new Core.Files.DocumentDataStore(env.Locations.Documents),
            Session = new Core.Files.SessionStore(env.Locations.Settings),
            State = new Core.Files.AppStateStore(Commands.CommandService.State),
            Watcher = new Core.Files.FileSystemChangeWatcher(),
            RestorePosition = () => FileSettings.RestorePosition(Settings),
        };
        vm.InitializeFiles(files, FileSettings.RecentMaxItems(Settings));
        ViewOptions.Documents = files.Documents;
        vm.BackupSettings = FileSettings.Backup(Settings);
        vm.ShiftWhenLengthChanges = Settings.GetBool(Core.Saving.SavePlanner.ShiftInPlaceKey, false);

        // コマンドパレットの「ファイル」モードの候補 (UI-17 の仕様 2、UI-32)。
        MainWindow.PaletteRecentFiles = () => vm.Recent.Items.Select(i => new PaletteRecentFile(i.DisplayName, i.Path));

        // パネル (UI-05) の登録。ウィンドウを作る前に行う。
        MainWindow.RegisterHashPanel();
        MainWindow.RegisterAnnotationPanels();
        MainWindow.RegisterSearchResultsPanel();
        MainWindow.RegisterSelectionPanels();
        MainWindow.RegisterFormatIssuesPanel();
        MainWindow.RegisterComparePanel();
        var window = new MainWindow(vm);
        Window = window;

        // 複数ウィンドウ (UI-14): 最初のウィンドウ。設定・テーマなどの変更は全ウィンドウに反映する (仕様 2)。
        WindowManager.Register(window);
        window.ApplyAppearance();
        window.ShowSettingsStatus(settingsStatus);
        window.ShowKeybindingsStatus();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppLog.Info($"Started {env.AppVersion} ({env.Distribution}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        // タスクバーのまとまり (PKG-12 の仕様 4): プロセスに設定した AppUserModelID。MSIX 版はパッケージが決めるので設定しない。
        // 配布のテスト (TC-PKG-12-03) が、スタートメニューのショートカットの値と比べる。
        AppLog.Info($"AppUserModelID: {ProcessIdentity.TryGetAppUserModelId() ?? "(not set)"}");

        // 外部で編集された設定・コントラストテーマの切り替えを反映する (UI-23 の仕様 7、UI-26 の仕様 3)。
        Settings.Changed += _ => DispatcherQueue.TryEnqueue(() =>
        {
            vm.Recent.MaxItems = FileSettings.RecentMaxItems(Settings);
            vm.BackupSettings = FileSettings.Backup(Settings);
            vm.ShiftWhenLengthChanges = Settings.GetBool(Core.Saving.SavePlanner.ShiftInPlaceKey, false);
            ApplyRecoveryInterval();
            AppLog.DebugEnabled = Settings.GetString("log.level", "info") == "debug";
            CrashReporter.WriteMiniDump = Settings.GetBool(CrashReporter.MiniDumpKey, false);
            foreach (MainWindow w in WindowManager.Windows)
            {
                w.ApplyAppearance();
                w.ApplyEditorSettings();
            }
        });
        Settings.ExternalEditFailed += error => DispatcherQueue.TryEnqueue(() => WindowManager.Current.ShowSettingsEditError(error));

        // 設定・キー割り当てを書けなかった (UI-22 の「エラー」)。値はセッション中だけ有効にする。
        Settings.WriteFailed += reason => DispatcherQueue.TryEnqueue(() => WindowManager.Current.ShowSettingsSaveFailed(reason));
        Commands.CommandService.SaveFailed += reason => DispatcherQueue.TryEnqueue(() => WindowManager.Current.ShowSettingsSaveFailed(reason));
        Appearance.SystemColorsChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (MainWindow w in WindowManager.Windows)
            {
                w.ApplyAppearance();
            }
        });
        try
        {
            Settings.StartWatching();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warning($"Settings watcher unavailable: {ex.GetType().Name}");
        }

        // メモリの上限の監視 (ENG-08)。1 秒ごとに集計し、上限を超えたら減らす。減らしきれなければ知らせる。
        _memoryMonitor = new MemoryMonitor(vm.Memory);
        _memoryMonitor.OverLimit += (_, _) => DispatcherQueue.TryEnqueue(() => WindowManager.Current.ShowMemoryOverLimit(vm.Memory.Limit));
        _memoryMonitor.LowMemory += (_, _) => AppLog.Warning("Low memory notification: cache trimmed.");

        // トースト通知を押したら、ウィンドウを前に出す (UI-36 の仕様 8)。
        ToastNotifier.Invoked += () => DispatcherQueue.TryEnqueue(() => WindowManager.Current.Activate());

        // 異常終了の直前に未保存の編集内容を書き出す (PKG-30 の仕様 1 の 1)。
        CrashReporter.WriteRecovery = WindowManager.WriteRecoveryNow;

        // 復旧用データの定期の書き出し (ENG-27 の仕様 1。既定 1 分ごと)。
        // タイマーはフィールドに持つ (ローカル変数だけだとガベージコレクションで回収され、書き出しが止まる)。
        _recoveryTimer = DispatcherQueue.CreateTimer();
        _recoveryTimer.Tick += async (_, _) => await WindowManager.WriteRecoveryAsync();
        ApplyRecoveryInterval();

        // セッション・閉じたタブの記録を読み、起動時の動作 (UI-30) に従ってタブを戻す。コマンドラインのファイルより先に行い、
        // 指定されたファイルをアクティブなタブにする (UI-30 の仕様 2)。
        window.PrepareStartup();

        // 既存のインスタンスに転送された起動 (2 つ目の起動で指定したファイル) を、最後にアクティブだったウィンドウ (設定
        // window.openExternalIn と --new-window によっては新しいウィンドウ) のタブとして開く (UI-15)。
        SingleInstance.Redirected += commandLine => DispatcherQueue.TryEnqueue(() => WindowManager.OpenRedirected(commandLine));

        // コマンドラインで指定したファイルを開く (AUTO-37)。指定がなければスタートページを出す (UI-01 の仕様 2)。
        // テスト用のビルドでは、異常を再現するデータソースもここで開く (テスト方針 7.2)。
        window.OpenFromCommandLine(TestHooks.OpenStartupSources(vm, Program.CommandLine), activate: false);

        // 開発中の確認用: 作業の邪魔にならないよう、起動したら前のウィンドウに戻し、自分は後ろに回る。
        // 自動テスト (--test-hooks) では一度もアクティブにせずに表示する。
        WindowManager.Show(window);

        // テスト用のメニューと命令の通り道 (テスト用のビルドだけ。テスト方針 8.4)。
        TestHooks.OnLaunched(window, vm);

        // 更新の確認、ジャンプリスト、Explorer 連携の案内、他の配布形態の設定の取り込み (F1-22、F1-24、PKG-31)。
        window.RecentFiles = new RecentJumpListSource(vm.Recent);
        window.StartPackagingFeatures();

        // 前回の異常終了の後始末: 復旧の提案と、クラッシュ情報の通知 (ENG-27 の仕様 6、PKG-30 の仕様 2)。
        window.ShowStartupNoticesWhenLoaded();
    }
}
