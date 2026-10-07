namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// 性能テスト用の固定の環境 (テスト方針 6.6) でだけ実行するテスト。環境変数 HEXEDITOR_PERF_MACHINE が 1 のときだけ実行し、
/// それ以外 (開発者の PC、共有の CI ランナー) では理由を付けてスキップする。フレームの間隔は他の処理の負荷で大きく変わり、
/// 固定の環境以外では合否を判定できないため。
/// </summary>
public sealed class PerfEnvironmentFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_PERF_MACHINE";

    public PerfEnvironmentFactAttribute()
    {
        if (!IsPerfMachine)
        {
            Skip = $"性能テスト用の固定の環境 (テスト方針 6.6) でだけ実行する ({Variable}=1 で実行する)。";
        }
    }

    public static bool IsPerfMachine => Environment.GetEnvironmentVariable(Variable) == "1";
}
