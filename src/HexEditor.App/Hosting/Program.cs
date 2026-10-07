using HexEditor.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Hosting;

/// <summary>
/// 起動処理 (PKG-11)。XAML の自動生成の Main は使わず (DISABLE_XAML_GENERATED_MAIN)、ここで順番を決める。
/// 1〜5 では XAML の型を使わない (2 つ目のプロセスが転送して終わるまでを短くするため)。
/// </summary>
public static class Program
{
    /// <summary>起動時に判定した実行環境。</summary>
    public static IAppEnvironment Environment { get; private set; } = null!;

    /// <summary>コマンドラインの引数 (AUTO-37)。</summary>
    public static CommandLine CommandLine { get; private set; } = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        // 1. Velopack のフック (インストーラ版だけ。PKG-08)。
#if HEX_DISTRO_INSTALLER
        Velopack.VelopackApp.Build().Run();
#endif

        // 2. 実行環境の判定 (保存先・ログの場所が決まる)。
        CommandLine = CommandLine.Parse(args);
        Environment = AppEnvironment.Detect(CommandLine.TestProfile);

        // 3. 未処理例外の記録 (PKG-30)。
        CrashLog.Initialize(Environment.Locations.Crash);

        // 4. COM の準備。
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // 5. 単一インスタンス化。既存のインスタンスがあれば起動を転送して終わる (UI-15)。
        if (CommandLine.NewInstance is false && SingleInstance.TryRedirect(Environment.InstanceKey))
        {
            return 0;
        }

        // 6. 表示言語 (最初のウィンドウを作る前。UI-43)。
        Localization.ApplyLanguageOverride(CommandLine.UiLanguage);

        // 7. XAML の起動。
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
