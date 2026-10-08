using HexEditor.App.Services;
using HexEditor.Platform.Shell;

namespace HexEditor.App.Hosting;

/// <summary>
/// <c>HexEditor.exe --unregister</c> (08 の AUTO-36 の 9、10 の PKG-09)。GUI を起動せず、既存のインスタンスにも転送しない。
/// ポータブル版は設定画面の「この PC から登録を解除」、インストーラ版はアンインストール前のフックと同じ解除を行い、結果を完了の
/// メッセージボックスで示す (テスト用の起動ではログだけ)。MSIX 版・開発中の実行は何もしない (その旨を示して 0 で終わる)。
/// 終了コードは、すべて消せたら 0、消せない項目があれば 3。
/// </summary>
internal static class UnregisterCommand
{
    public static int Run(IAppEnvironment env, CommandLine commandLine)
    {
        AppLog.Initialize(env.Locations.Logs);

        // ジャンプリストとトースト通知の登録は AppUserModelID ごとなので、先に設定する (MSIX 版はパッケージが決める)。
        if (!env.IsPackaged && !ProcessIdentity.TrySetAppUserModelId(env.AppUserModelId))
        {
            AppLog.Warning("SetCurrentProcessExplicitAppUserModelID failed.");
        }

        Localization.ApplyLanguage(commandLine.UiLanguage, env.Locations.Settings);
        AppLog.Info($"--unregister ({env.Distribution})");
        UnregisterResult result;
        try
        {
            result = Unregistration.Run(env.Distribution,
                portable: () => ExplorerIntegration.UnregisterFromThisPc().Failures,
                installer: () =>
                {
                    IReadOnlyList<string> failures = VelopackBootstrap.CreateHooks().Unregister();
                    DataDirectory.DeleteTempAtExit(env.Locations.Temp);
                    return failures;
                });
        }
        catch (Exception ex)
        {
            result = new UnregisterResult(true, [ex.GetType().Name]);
        }

        string message = !result.Supported
            ? Loc.Get(env.Distribution == Distribution.Msix ? "Unregister_ManagedByWindows" : "Unregister_NothingRegistered")
            : result.Failures.Count > 0
                ? Loc.Format("Shell_UnregisterPcFailed", ExplorerIntegration.DescribeFailures(result.Failures))
                : Loc.Get(env.Distribution == Distribution.Portable ? "Shell_UnregisterPcDone" : "Unregister_Done");
        if (result.Failures.Count > 0)
        {
            AppLog.Warning("--unregister failed: " + string.Join(", ", result.Failures));
        }
        else
        {
            AppLog.Info($"--unregister finished ({(result.Supported ? "unregistered" : "nothing to do")}).");
        }

        // テスト用の起動ではメッセージボックスを出さない (前面に出て作業の邪魔になるため。終了コードとログで確かめる)。
        if (!TestHooks.Active && commandLine.TestProfile is null)
        {
            StartupError.ShowInformation("HexEditor", message);
        }

        return result.ExitCode;
    }
}
