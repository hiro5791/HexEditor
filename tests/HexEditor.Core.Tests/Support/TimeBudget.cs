namespace HexEditor.Core.Tests.Support;

/// <summary>
/// 時間の目標 (テスト方針 6.6。性能テストの PerfSupport.TimeLimit と同じ扱い)。性能テスト用の固定の環境 (HEXEDITOR_PERF_MACHINE=1) では
/// 失敗にし、それ以外 (共有の CI ランナー・開発者の PC) では警告にする (標準出力と、CI ではジョブの概要)。共有のランナーの時間は
/// 他の負荷で大きく変わり、壁時計での合否を判定できないため。処理が止まらない・桁違いに遅いといった誤りは、呼び出し側が
/// 余裕のある上限で Assert する。
/// </summary>
public static class TimeBudget
{
    public static bool IsPerfMachine => Environment.GetEnvironmentVariable("HEXEDITOR_PERF_MACHINE") == "1";

    public static void Limit(bool ok, string message, [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        if (ok)
        {
            return;
        }

        Assert.False(IsPerfMachine, message);
        string line = $"⚠ {test}: {message} (time limit, a failure only on the performance machine: HEXEDITOR_PERF_MACHINE=1)";
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
        {
            try
            {
                File.AppendAllText(summary, "- " + line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 概要に書けなくても警告は標準出力に残っている。
            }
        }
    }
}
