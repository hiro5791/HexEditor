using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace HexEditor.App.Hosting;

/// <summary>
/// 単一インスタンス (UI-15、PKG-11)。2 つ目の起動は既存のインスタンスに転送し、既存のウィンドウのタブとして開く。
/// </summary>
public static class SingleInstance
{
    /// <summary>転送の完了を待つ時間 (PKG-11 の仕様 3、UI-15 の仕様 6)。</summary>
    public static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(5);

    private static readonly object Lock = new();
    private static readonly List<CommandLine> Pending = [];
    private static Action<CommandLine>? _redirected;

    /// <summary>
    /// 既存のインスタンスに転送された起動 (ファイルを開く要求など)。スレッドプールから呼ばれる。
    /// 購読される前に届いた転送は取っておき、購読したときに渡す (ウィンドウを作る前の転送を落とさない)。
    /// </summary>
    public static event Action<CommandLine>? Redirected
    {
        add
        {
            List<CommandLine> pending;
            lock (Lock)
            {
                _redirected += value;
                pending = [.. Pending];
                Pending.Clear();
            }

            foreach (CommandLine commandLine in pending)
            {
                value?.Invoke(commandLine);
            }
        }

        remove
        {
            lock (Lock)
            {
                _redirected -= value;
            }
        }
    }

    /// <summary>
    /// 転送が <see cref="RedirectTimeout"/> 以内に終わらず、独立したインスタンスとして起動した (UI-15 の仕様 6)。
    /// メインウィンドウはこれを見て InfoBar (Startup_RedirectTimedOut) を出す。
    /// </summary>
    public static bool RedirectTimedOut { get; private set; }

    /// <summary>
    /// このプロセスが既存のインスタンスに転送したら true (呼び出し側はそのまま終わる)。
    /// 自分が最初のインスタンスなら false。転送が 5 秒以内に終わらなければ false を返し、<see cref="RedirectTimedOut"/> を立てる。
    /// </summary>
    public static bool TryRedirect(string key)
    {
        AppInstance main = AppInstance.FindOrRegisterForKey(key);
        if (main.IsCurrent)
        {
            main.Activated += (_, args) => Raise(ToCommandLine(args));
            return false;
        }

        // 既存のプロセスに前面化を許可してから転送する。STA を止めないよう別スレッドで行い、最大 5 秒待つ (PKG-11 の仕様 3)。
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow((int)main.ProcessId);
        bool completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                completed = main.RedirectActivationToAsync(activation).AsTask().Wait(RedirectTimeout);
            }
            catch (Exception)
            {
                completed = false;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        if (thread.Join(RedirectTimeout + TimeSpan.FromMilliseconds(500)) && completed)
        {
            return true;
        }

        // 既存のプロセスが応答しない: 独立したインスタンスとして起動する (UI-15 の仕様 6)。
        RedirectTimedOut = true;
        return false;
    }

    private static void Raise(CommandLine commandLine)
    {
        Action<CommandLine>? handler;
        lock (Lock)
        {
            handler = _redirected;
            if (handler is null)
            {
                Pending.Add(commandLine);
                return;
            }
        }

        handler(commandLine);
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
