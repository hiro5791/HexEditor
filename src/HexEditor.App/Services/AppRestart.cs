using HexEditor.App.Hosting;

namespace HexEditor.App.Services;

/// <summary>
/// アプリの再起動 (09 の UI-43 の仕様 5「今すぐ再起動」)。<c>AppInstance.Restart</c> で再起動する。
/// 起動時のオプションのうち、そのプロセスの間だけ有効な表示言語 (<c>--ui-lang</c>、<c>--pseudo-locale</c>) と開いていたファイルは外す。
/// 開いていたウィンドウとタブはセッション (UI-31) で戻す: 再起動の前に全ウィンドウのセッションを保存し、<see cref="RestoreSessionFlag"/>
/// を付けて起動する (設定 session.restoreOnStartup に関係なく復元する)。
/// </summary>
public static class AppRestart
{
    /// <summary>再起動の印: 起動時の動作の設定に関係なく、保存したセッションを復元する (UI-43 の仕様 5)。</summary>
    public const string RestoreSessionFlag = "--restore-session";

    private static readonly HashSet<string> DropWithValue = ["--ui-lang", "--pseudo-locale", "--offset", "-g", "--select"];

    /// <summary>このプロセスは「今すぐ再起動」で起動した (セッションを必ず復元する)。</summary>
    public static bool IsSessionRestart { get; } = Environment.GetCommandLineArgs().Skip(1).Contains(RestoreSessionFlag);

    /// <summary>セッションを復元する再起動の引数 (開いていたファイルは渡さない。セッションが戻す)。</summary>
    public static IReadOnlyList<string> SessionArguments(IReadOnlyList<string> original) =>
        [.. Arguments(original, []).Where(a => a != RestoreSessionFlag), RestoreSessionFlag];

    /// <summary>再起動の引数を作る (テスト用の --test-hooks / --test-profile などはそのまま渡す)。</summary>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> original, IReadOnlyList<string> files)
    {
        CommandLine parsed = CommandLine.Parse(original);
        var args = new List<string>();
        for (int i = 0; i < original.Count; i++)
        {
            string a = original[i];
            if (DropWithValue.Contains(a))
            {
                i++;
                continue;
            }

            // 元の起動で開いたファイルは外す (今開いているファイルを渡す)。
            if (!a.StartsWith('-') && parsed.Files.Contains(a) && (i == 0 || !IsValueOption(original[i - 1])))
            {
                continue;
            }

            args.Add(a);
        }

        args.AddRange(files);
        return args;
    }

    /// <summary>
    /// 保存したセッションを復元するように再起動する (呼ぶ前にセッションを保存しておく)。戻ってきたら失敗 (理由をログに書き、false)。
    /// </summary>
    public static bool Restart()
    {
        IReadOnlyList<string> args = SessionArguments(Environment.GetCommandLineArgs().Skip(1).ToList());
        string line = string.Join(' ', args.Select(Quote));
        AppLog.Info($"Restarting: {args.Count} argument(s)");
        try
        {
            App.Settings.Flush();

            // 新しいプロセスがこのプロセスに転送しないよう、単一インスタンスのキーを先に外す (UI-15)。
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart(line);
            AppLog.Warning($"AppInstance.Restart failed: {reason}");
        }
        catch (Exception ex)
        {
            AppLog.Warning($"AppInstance.Restart failed: {ex.GetType().Name}");
        }

        return false;
    }

    /// <summary>
    /// 管理者として起動し直す (ENG-28 の仕様 12 の 7)。<c>HexEditor.exe</c> を <c>runas</c> で起動する。UAC で拒否されると
    /// <see cref="System.ComponentModel.Win32Exception"/> になる (呼び出し側は終了せず続ける)。起動できたら true。
    /// </summary>
    public static bool RestartAsAdministrator()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        IReadOnlyList<string> args = SessionArguments(Environment.GetCommandLineArgs().Skip(1).ToList());
        var info = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        App.Settings.Flush();
        Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
        try
        {
            return System.Diagnostics.Process.Start(info) is not null;
        }
        catch
        {
            // 失敗したら単一インスタンスのキーを登録し直さないが、このプロセスは続く。
            throw;
        }
    }

    private static bool IsValueOption(string a) => a is "--ui-lang" or "--test-profile" or "--test-hooks" or "--offset" or "-g" or "--select" or "--encoding"
        or "--template" or "--disk" or "--volume" or "--process" or "--run" or "--plugin-dev" or "--pseudo-locale";

    private static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
