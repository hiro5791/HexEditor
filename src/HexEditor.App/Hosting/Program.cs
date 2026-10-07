using HexEditor.App.Services;
using HexEditor.Core.Recovery;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Hosting;

/// <summary>
/// 起動処理 (PKG-11)。XAML の自動生成の Main は使わず (DISABLE_XAML_GENERATED_MAIN)、ここで順番を決める。
/// 1〜5 では XAML の型を使わない (2 つ目のプロセスが転送して終わるまでを短くするため)。
/// 1〜5 で例外が起きたら logs\startup-error.log に書き、MessageBox で知らせて終わる (PKG-11 の「エラー」)。
/// </summary>
public static class Program
{
    /// <summary>起動時に判定した実行環境。</summary>
    public static IAppEnvironment Environment => AppEnvironment;

    /// <summary>コマンドラインの引数 (AUTO-37)。</summary>
    public static CommandLine CommandLine { get; private set; } = null!;

    private static AppEnvironment AppEnvironment { get; set; } = null!;

    [STAThread]
    private static int Main(string[] args)
    {
        string step = "velopack";
        try
        {
            // 1. Velopack のフック (インストーラ版だけ。PKG-08)。フック用の引数ならフックを処理して終わる。
            VelopackBootstrap.Run(args);

            // 2. 実行環境の判定 (保存先・ログの場所が決まる)。
            step = "environment";
            CommandLine = CommandLine.Parse(args);
            AppEnvironment = AppEnvironment.DetectCurrent(typeof(Program).Assembly, CommandLine.TestProfile);

            // 3. 未処理例外の記録 (PKG-30)。
            step = "crash-reporter";
            CrashReporter.Initialize(Environment);

            // 4. COM の準備。
            step = "com";
            WinRT.ComWrappersSupport.InitializeComWrappers();

            // 5. 単一インスタンス化。既存のインスタンスがあれば起動を転送して終わる (UI-15)。
            step = "single-instance";
            if (CommandLine.NewInstance is false && SingleInstance.TryRedirect(Environment.InstanceKey))
            {
                return 0;
            }
        }
        catch (Exception ex)
        {
            ReportStartupError(ex, step);
            return 1;
        }

        PrepareSession();

        // 6. 表示言語 (最初のウィンドウを作る前。UI-43)。
        Localization.ApplyLanguageOverride(CommandLine.UiLanguage);

        // 7. XAML の起動。終了するまで戻らない。
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        // 終了時の後始末: ポータブル版の一時フォルダを消す (PKG-06 の仕様 4)。
        if (AppEnvironment.DeletesTempAtExit)
        {
            DataDirectory.DeleteTempAtExit(Environment.Locations.Temp);
        }

        return 0;
    }

    /// <summary>
    /// このプロセスが最初のインスタンスとして動くと決まった後の準備 (XAML の前)。失敗しても起動は続ける。
    /// </summary>
    private static void PrepareSession()
    {
        IAppEnvironment env = Environment;
        foreach (string warning in env.Warnings)
        {
            AppLog.Warning(warning);
        }

        // タスクバーのまとまりとジャンプリストのための AppUserModelID (PKG-12 の仕様 4。MSIX 版はパッケージが決める)。
        if (!env.IsPackaged && !ProcessIdentity.TrySetAppUserModelId(env.AppUserModelId))
        {
            AppLog.Warning("SetCurrentProcessExplicitAppUserModelID failed.");
        }

        if (SingleInstance.RedirectTimedOut)
        {
            AppLog.Warning("The existing instance did not respond within 5 seconds; started as a new instance.");
            StartupNotices.Add(new StartupNotice("Startup_RedirectTimedOut", StartupNoticeSeverity.Warning, []));
        }

        if (!env.IsDataDirectoryWritable)
        {
            StartupNotices.Add(new StartupNotice("Startup_DataFolderReadOnly", StartupNoticeSeverity.Warning, [], "Startup_ExportSettings"));
        }

        try
        {
            // 一時フォルダのうち、実行中の他のインスタンスが使っていないものを消す (PKG-13 の仕様 4)。
            int cleaned = DataDirectory.CleanTemp(env.Locations.Temp, keep: env.Locations.Recovery);
            if (cleaned > 0)
            {
                AppLog.Info($"Cleaned {cleaned} temporary item(s).");
            }

            // 30 日以上前の復旧用データを消す (PKG-13 の仕様 5)。
            int expired = RecoveryStore.DeleteExpired(env.Locations.Recovery, RecoveryStore.MaxAge, DateTime.UtcNow);
            if (expired > 0)
            {
                AppLog.Info($"Deleted {expired} expired recovery item(s).");
                StartupNotices.Add(new StartupNotice("Startup_OldRecoveryDeleted", StartupNoticeSeverity.Informational, [expired]));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Startup cleanup failed: {ex.GetType().Name}");
        }
    }

    private static void ReportStartupError(Exception ex, string step)
    {
        string? logsFolder = null;
        try
        {
            logsFolder = AppEnvironment?.Locations.Logs;
        }
        catch (Exception)
        {
            // 判定できていなければ %TEMP% に書く。
        }

        string? log = StartupError.WriteLog(ex, step, logsFolder, Path.GetTempPath());
        string title = "HexEditor";
        string message = $"HexEditor couldn't start.\n\n{ex.Message}\n\nDetails were written to:\n{log ?? "-"}";
        try
        {
            if (Loc.Get("StartupError_Message") != "StartupError_Message")
            {
                title = Loc.Get("StartupError_Title");
                message = Loc.Format("StartupError_Message", ex.Message, log ?? "-");
            }
        }
        catch (Exception)
        {
            // リソースを読めない段階なら英語の既定の文言を使う。
        }

        StartupError.Show(title, message);
    }
}
