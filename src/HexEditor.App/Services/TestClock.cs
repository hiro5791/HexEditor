#if HEX_TEST_HOOKS
using System.Diagnostics;

namespace HexEditor.App.Services;

/// <summary>
/// テスト用の時計 (ミリ秒)。アプリの中で時間を測り、テストからの命令の往復の時間を含めないために使う
/// (テスト方針 6.6: 時間の目標はアプリの中で測る)。
/// </summary>
internal static class TestClock
{
    public static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
#endif
