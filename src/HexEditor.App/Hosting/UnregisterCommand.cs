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

        // hexed --unregister (AUTO-36 の 9) は、結果のファイルを渡してくる。メッセージボックスは出さず、hexed が標準出力に出す。
        if (commandLine.UnregisterResult is { } resultFile)
        {
            WriteResult(resultFile, env.Distribution, result);
            return result.ExitCode;
        }

        // テスト用の起動ではメッセージボックスを出さない (前面に出て作業の邪魔になるため。終了コードとログで確かめる)。
        if (!TestHooks.Active && commandLine.TestProfile is null)
        {
            StartupError.ShowInformation("HexEditor", message);
        }

        return result.ExitCode;
    }

    /// <summary>解除する項目 (ポータブル版とインストーラ版)。</summary>
    private static readonly string[] PortableItems = ["contextMenu", "fileAssociations", "commandLine", "jumpList", "toast", "temp"];

    private static readonly string[] InstallerItems = ["contextMenu", "fileAssociations", "appPaths", "commandLine", "jumpList", "toast", "temp"];

    /// <summary>
    /// 結果を 1 行 1 項目で書く (<c>ok</c>/<c>fail</c>/<c>info</c>、タブ、項目の ID、タブ、詳細。<c>hexed</c> が読んで表示言語の文言にする)。
    /// 失敗した項目 (Platform が返す「項目の ID: 例外の型名」) 以外はすべて消せたことにする。
    /// </summary>
    internal static void WriteResult(string path, Distribution distribution, UnregisterResult result)
    {
        var lines = new List<string>();
        if (!result.Supported)
        {
            lines.Add(distribution == Distribution.Msix ? "info\tmanaged" : "info\tnothing");
        }
        else
        {
            string[] items = distribution == Distribution.Installer ? InstallerItems : PortableItems;
            var failed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string failure in result.Failures)
            {
                int colon = failure.IndexOf(':', StringComparison.Ordinal);
                string id = (colon < 0 ? failure : failure[..colon]).Trim();
                failed[id] = colon < 0 ? string.Empty : failure[(colon + 1)..].Trim();
            }

            foreach (string item in items)
            {
                lines.Add(failed.Remove(item, out string? reason) ? $"fail\t{item}\t{reason}" : $"ok\t{item}");
            }

            // 項目の一覧にない失敗 (レジストリの読み書きなど) もそのまま知らせる。
            foreach ((string id, string reason) in failed)
            {
                lines.Add($"fail\t{id}\t{reason}");
            }
        }

        try
        {
            File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"The unregister result could not be written: {ex.GetType().Name}");
        }
    }
}
