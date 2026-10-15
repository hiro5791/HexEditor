using System.Runtime.InteropServices;
using HexEditor.App.Services;
using HexEditor.Platform;

namespace HexEditor.App.Hosting;

/// <summary>
/// Shift キーを押しながらの起動でセーフモードにする (09 の UI-30 の仕様 4、08 の AUTO-30)。最初のインスタンスとして動くと決まった後に、
/// <c>GetAsyncKeyState(VK_SHIFT)</c> でキーの状態を読むだけ (キーの入力は送らない)。テスト用のビルドでは <c>safeModeShift</c> で差し替える。
/// </summary>
internal static class SafeModeStartup
{
    private const int VkShift = 0x10;

    /// <summary>Shift キーが押されていれば、セーフモードにしたコマンドラインを返す。</summary>
    public static CommandLine Apply(CommandLine commandLine)
    {
        if (commandLine.SafeMode || !ShiftHeld())
        {
            return commandLine;
        }

        AppLog.Info("Safe mode: the Shift key was held at start-up");
        return commandLine with { SafeMode = true };
    }

    private static bool ShiftHeld()
    {
#if HEX_TEST_HOOKS
        if (TestHooks.Active)
        {
            // テストでは実際のキーボードの状態に左右されないようにする。
            return TestHooks.Settings.SafeModeShift ?? false;
        }
#endif
        return (GetAsyncKeyState(VkShift) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
