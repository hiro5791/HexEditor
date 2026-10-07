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
        // 表示言語は、リソースを読み込む前に決める (UI-43)。--ui-lang で指定された言語を、そのプロセスの間だけ使う。
        ApplyUiLanguage(Environment.GetCommandLineArgs());
        InitializeComponent();

        // Shift_JIS・EBCDIC など、.NET が標準で持たないコードページを使えるようにする (VIEW-21)。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 未処理の例外は記録する (復旧用データの書き出しは ENG-27 で行う)。
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write(e.Exception);
    }

    /// <summary>対応する 23 言語 (00-overview 5.1)。</summary>
    public static readonly string[] SupportedLanguages =
    [
        "en", "zh-Hans", "zh-Hant", "ja", "ko", "id", "vi", "th", "de", "fr", "es", "pt",
        "it", "ru", "uk", "pl", "cs", "hu", "ro", "el", "ar", "tr", "fa",
    ];

    /// <summary>表示言語が右から左に書く言語か (アラビア語・ペルシア語。00-overview 5.4)。</summary>
    public static bool IsRightToLeft => System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    private static void ApplyUiLanguage(string[] args)
    {
        int index = Array.IndexOf(args, "--ui-lang");
        if (index < 0 || index + 1 >= args.Length)
        {
            return;
        }

        string language = args[index + 1];
        if (!SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(language);
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
        string[] all = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var files = new List<string>();
        for (int i = 0; i < all.Length; i++)
        {
            // 値を取るオプション (AUTO-37) は、値をファイル名として扱わない。
            if (all[i] is "--ui-lang" or "--test-profile" or "--test-hooks" or "--offset" or "-g" or "--select" or "--encoding" or "--template")
            {
                i++;
            }
            else if (!all[i].StartsWith('-'))
            {
                files.Add(all[i]);
            }
        }

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
