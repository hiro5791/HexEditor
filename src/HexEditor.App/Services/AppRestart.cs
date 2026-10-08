using HexEditor.App.Hosting;

namespace HexEditor.App.Services;

/// <summary>
/// アプリの再起動 (09 の UI-43 の仕様 5「今すぐ再起動」)。<c>AppInstance.Restart</c> で再起動する。
/// 起動時のオプションのうち、そのプロセスの間だけ有効な表示言語 (<c>--ui-lang</c>、<c>--pseudo-locale</c>) と開いていたファイルは外し、
/// 代わりに開いているファイルを渡してタブを戻す。セッションの復元 (UI-31) ができたら、そちらに任せる。
/// </summary>
public static class AppRestart
{
    private static readonly HashSet<string> DropWithValue = ["--ui-lang", "--pseudo-locale", "--offset", "-g", "--select"];

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

    /// <summary>再起動する。戻ってきたら失敗 (理由をログに書き、false)。</summary>
    public static bool Restart(IReadOnlyList<string> files)
    {
        IReadOnlyList<string> args = Arguments(Environment.GetCommandLineArgs().Skip(1).ToList(), files);
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

    private static bool IsValueOption(string a) => a is "--ui-lang" or "--test-profile" or "--test-hooks" or "--offset" or "-g" or "--select" or "--encoding"
        or "--template" or "--disk" or "--volume" or "--process" or "--run" or "--plugin-dev" or "--pseudo-locale";

    private static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
