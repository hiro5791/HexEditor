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
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
    }

    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        IAppEnvironment env = Program.Environment;
        var options = new DocumentOptions
        {
            TempDirectory = Path.Combine(env.Locations.Temp, "documents"),
        };
        var vm = new MainViewModel(new OperationCenter(), new EngineMemory(), options, env.Locations.Recovery);
        var window = new MainWindow(vm);
        Window = window;
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

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
    }
}
