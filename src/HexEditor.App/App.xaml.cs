using System.Text;
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

        // 未処理の例外は記録する (復旧用データの書き出しは ENG-27 で行う)。
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write(e.Exception);
    }

    public static Window Window { get; private set; } = null!;

    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var vm = new MainViewModel(new OperationCenter(), new EngineMemory());
        var window = new MainWindow(vm);
        Window = window;
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // コマンドラインで指定したファイルを開く (AUTO-37 の最小限)。指定がなければ無題を 1 つ開く。
        string[] files = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith('-')).ToArray();
        foreach (string file in files)
        {
            try
            {
                vm.Open(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 開けなかったファイルは無視して起動を続ける (エラー表示は ENG-11 の実装で整える)。
            }
        }

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
