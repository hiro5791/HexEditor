using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace HexEditor.App.Hosting;

/// <summary>
/// 単一インスタンス (UI-15、PKG-11)。2 つ目の起動は既存のインスタンスに転送し、既存のウィンドウのタブとして開く。
/// </summary>
public static class SingleInstance
{
    /// <summary>既存のインスタンスに転送された起動 (ファイルを開く要求など)。スレッドプールから呼ばれる。</summary>
    public static event Action<CommandLine>? Redirected;

    /// <summary>このプロセスが既存のインスタンスに転送したら true (呼び出し側はそのまま終わる)。</summary>
    public static bool TryRedirect(string key)
    {
        AppInstance main = AppInstance.FindOrRegisterForKey(key);
        if (main.IsCurrent)
        {
            main.Activated += (_, args) => Redirected?.Invoke(ToCommandLine(args));
            return false;
        }

        // 既存のプロセスに前面化を許可してから転送する。STA を止めないよう別スレッドで行い、最大 5 秒待つ (PKG-11 の仕様 3)。
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow((int)main.ProcessId);
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                main.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(5));
            }
            finally
            {
                done.Set();
            }
        });
        thread.Start();
        done.Wait(TimeSpan.FromSeconds(5));
        return true;
    }

    private static CommandLine ToCommandLine(AppActivationArguments args) => args.Kind switch
    {
        ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs file =>
            CommandLine.Parse(file.Files.Select(f => f.Path).ToList()),
        ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs launch =>
            CommandLine.ParseString(launch.Arguments ?? string.Empty),
        _ => CommandLine.Parse([]),
    };

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
