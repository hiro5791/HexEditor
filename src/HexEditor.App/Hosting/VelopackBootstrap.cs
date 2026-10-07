namespace HexEditor.App.Hosting;

/// <summary>
/// Velopack の初期化とフック (PKG-08、PKG-11 の手順 1)。インストーラ版のビルド (HEX_DISTRO_INSTALLER) 以外では何もしない。
/// フック用の引数で起動された場合、Velopack がフックを呼んでからプロセスを終える (ウィンドウは作らない)。
/// </summary>
public static class VelopackBootstrap
{
    public static void Run(string[] args)
    {
#if HEX_DISTRO_INSTALLER
        Velopack.VelopackApp.Build()
            .SetArgs(args)
            .OnAfterInstallFastCallback(v => CreateHooks().AfterInstall(v.ToString()))
            .OnAfterUpdateFastCallback(v => CreateHooks().AfterUpdate(v.ToString()))
            .OnBeforeUninstallFastCallback(v => CreateHooks().BeforeUninstall(v.ToString()))
            .Run();
#else
        _ = args;
#endif
    }

#if HEX_DISTRO_INSTALLER
    private static Platform.Shell.InstallHooks CreateHooks()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HexEditor.exe");
        return new Platform.Shell.InstallHooks(new Platform.Shell.WindowsUserRegistry(), InstallerEnvironment.DataRoot(localAppData), exe);
    }
#endif
}
