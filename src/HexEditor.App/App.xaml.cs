using System.Text;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
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
        UnhandledException += (_, e) => CrashReporter.Handle(e.Exception, exit: false);
    }

    public static Window Window { get; private set; } = null!;

    /// <summary>復旧用データの保存間隔 (ENG-27 の仕様 1。設定画面ができるまでは既定の 1 分)。</summary>
    public static TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromMinutes(1);

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        IAppEnvironment env = Program.Environment;
        // 追加バッファの一時ファイルは復旧用データと同じフォルダに置き、異常終了後もそのまま参照できるようにする (ENG-27 の仕様 2)。
        var options = new DocumentOptions
        {
            TempDirectory = env.Locations.Recovery,
        };
        var vm = new MainViewModel(new OperationCenter(), new EngineMemory(), options, env.Locations.Recovery);
        var window = new MainWindow(vm);
        Window = window;
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        AppLog.Info($"Started {env.AppVersion} ({env.Distribution}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");

        // 異常終了の直前に未保存の編集内容を書き出す (PKG-30 の仕様 1 の 1)。
        CrashReporter.WriteRecovery = timeout => vm.WriteRecoveryNow(timeout);

        // 復旧用データの定期の書き出し (ENG-27 の仕様 1。既定 1 分ごと)。
        var recoveryTimer = DispatcherQueue.CreateTimer();
        recoveryTimer.Interval = RecoveryInterval;
        recoveryTimer.Tick += async (_, _) => await vm.WriteRecoveryAsync(window.ShowRecoveryWriteError);
        recoveryTimer.Start();

        // 既存のインスタンスに転送された起動 (2 つ目の起動で指定したファイル) を、このウィンドウのタブとして開く (UI-15)。
        SingleInstance.Redirected += commandLine =>
            DispatcherQueue.TryEnqueue(() => window.OpenFromCommandLine(commandLine, activate: !DevOptions.NoActivate));

        // コマンドラインで指定したファイルを開く (AUTO-37)。指定がなければ無題を 1 つ開く。
        window.OpenFromCommandLine(Program.CommandLine, activate: false);
        if (vm.Documents.Count == 0)
        {
            vm.NewDocument();
        }

        // 開発中の確認用: 作業の邪魔にならないよう、起動したら前のウィンドウに戻し、自分は後ろに回る。
        nint previous = DevOptions.NoActivate ? DevOptions.ForegroundWindow() : 0;
        window.Activate();
        if (previous != 0)
        {
            DevOptions.SendToBack(window, previous);
        }

        // 前回の異常終了の後始末: 復旧の提案と、クラッシュ情報の通知 (ENG-27 の仕様 6、PKG-30 の仕様 2)。
        window.ShowStartupNoticesWhenLoaded();
    }
}
